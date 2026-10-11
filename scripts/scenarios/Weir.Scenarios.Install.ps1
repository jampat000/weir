<#
    The install scenarios: a fresh install, the first visit and account, and an update over the previous release.
    Each Scenario_<id> function returns what it saw, as one sentence for the record, or throws what went wrong.
#>

# Runs Weir-win-Setup.exe --silent the way another program installing Weir does, and waits for Setup's own exit.
function Invoke-WeirSetup {
    param([Parameter(Mandatory)] [string] $Path)
    if (-not (Test-Path -LiteralPath $Path)) { throw "The installer is not in the machine: $Path" }
    $clock = [Diagnostics.Stopwatch]::StartNew()
    $setup = Start-Process -FilePath $Path -ArgumentList '--silent' -PassThru
    if (-not $setup.WaitForExit(600000)) { throw "Setup ($Path) was still running after 10 minutes." }
    Add-Evidence "Setup --silent of $([IO.Path]::GetFileName($Path)) exited $($setup.ExitCode) after $([int]$clock.Elapsed.TotalSeconds) s"
    [pscustomobject]@{ ExitCode = $setup.ExitCode; Seconds = [int]$clock.Elapsed.TotalSeconds }
}

# Starts the installed tray the way an unattended caller does, in this (the signed-in) desktop session.
function Start-WeirTray {
    $paths = Get-WeirPaths
    if (-not (Test-Path -LiteralPath $paths.TrayExe)) { throw "Weir.exe is not at $($paths.TrayExe) after Setup." }
    $tray = Start-Process -FilePath $paths.TrayExe -ArgumentList @('--port', $script:Ctx.Port, '--silent') -PassThru
    $script:Ctx.TrayProcess = $tray
    Add-Evidence "started $($paths.TrayExe) --port $($script:Ctx.Port) --silent, pid $($tray.Id)"
    $tray
}

function Get-EdgePath {
    foreach ($root in ${env:ProgramFiles(x86)}, $env:ProgramFiles) {
        if (-not $root) { continue }
        $candidate = Join-Path $root 'Microsoft\Edge\Application\msedge.exe'
        if (Test-Path -LiteralPath $candidate) { return $candidate }
    }
    throw 'Microsoft Edge is not installed on this machine, and the first-visit scenario needs a real browser.'
}

# What a brand-new browser profile shows at $Path after the page has loaded and run: the page's DOM, as text.
function Get-PageDom {
    param([Parameter(Mandatory)] [string] $Path)
    $profile = Join-Path ([IO.Path]::GetTempPath()) ('weir-scenario-edge-' + [guid]::NewGuid().ToString('N'))
    try {
        Invoke-Tool -Program (Get-EdgePath) -Arguments @(
            '--headless=new', '--disable-gpu', '--no-first-run', '--no-default-browser-check',
            "--user-data-dir=$profile", '--virtual-time-budget=20000', '--dump-dom', "$($script:Ctx.Base)$Path")
    }
    finally { Remove-Item -LiteralPath $profile -Recurse -Force -ErrorAction SilentlyContinue }
}

function Get-TestIds {
    param([Parameter(Mandatory)] [string] $Dom)
    @([regex]::Matches($Dom, 'data-testid="([^"]+)"') | ForEach-Object { $_.Groups[1].Value } | Select-Object -Unique)
}

# --- fresh-install ------------------------------------------------------------------------------------------------------

function Scenario_fresh_install {
    $paths = Get-WeirPaths
    $setup = Invoke-WeirSetup -Path $script:Ctx.SetupPath
    Assert-That ($setup.ExitCode -eq 0) "Setup --silent exited 0 (it exited $($setup.ExitCode))"
    Assert-That (Test-Path -LiteralPath $paths.TrayExe) "Weir.exe is installed at $($paths.TrayExe)"
    Assert-That (Test-Path -LiteralPath $paths.ServerExe) "WeirServer.exe is installed at $($paths.ServerExe)"

    $tray = Start-WeirTray
    Wait-WeirReady -TimeoutSeconds 120
    $reported = Get-ServerVersion
    Assert-That (Test-VersionUnderTest -Reported $reported -Expected $script:Ctx.Version -ShortSha $script:Ctx.ShortSha) "the running server reports $reported, the build under test ($($script:Ctx.Version) $($script:Ctx.ShortSha))"
    $fileVersion = (Invoke-Tool -Program $paths.ServerExe -Arguments @('--version')).Trim()
    Assert-That (Test-VersionUnderTest -Reported $fileVersion -Expected $script:Ctx.Version -ShortSha $script:Ctx.ShortSha) "WeirServer.exe --version says $fileVersion"

    $tray.Refresh()
    Assert-That (-not $tray.HasExited) 'the tray is still running'
    Assert-That ($tray.MainWindowHandle -eq [IntPtr]::Zero) 'the tray opened no window'
    $server = @(Get-CimInstance Win32_Process -Filter "ParentProcessId = $($tray.Id)" | Where-Object Name -eq 'WeirServer.exe')
    Assert-That ($server.Count -ge 1) 'the tray started the server as its child'

    $addresses = @(Get-NetTCPConnection -LocalPort $script:Ctx.Port -State Listen -ErrorAction SilentlyContinue | ForEach-Object { $_.LocalAddress } | Sort-Object -Unique)
    $strangers = @($addresses | Where-Object { $_ -notin '127.0.0.1', '::1' })
    Assert-That ($addresses -contains '127.0.0.1' -and $strangers.Count -eq 0) "port $($script:Ctx.Port) listens on this PC only (listening on: $($addresses -join ', '))"

    $trayLog = Get-TrayLogText
    Assert-That ($trayLog -match [regex]::Escape("Using port $($script:Ctx.Port) from --port")) 'the tray log says it used the port it was given'
    Assert-That ($trayLog -match [regex]::Escape("Weir is healthy on http://127.0.0.1:$($script:Ctx.Port)/")) 'the tray log says Weir is healthy'
    "Setup --silent exited 0 in $($setup.Seconds) s; Weir $reported answered /ready; tray pid $($tray.Id) with the server as its child, no window; listening on $($addresses -join ', ') only."
}

# --- first-visit-account --------------------------------------------------------------------------------------------------

function Scenario_first_visit_account {
    $anonymous = New-WeirSession -BaseUrl $script:Ctx.Base
    $status = Get-WeirJson $anonymous '/api/v1/auth/bootstrap/status'
    Assert-That ($status.bootstrap_allowed -eq $true) "a fresh install has no account yet (bootstrap_allowed is true, reason $($status.reason))"

    $first = Get-PageDom -Path '/'
    $firstIds = Get-TestIds -Dom $first
    Assert-That ($firstIds -contains 'setup-username') "a first visit lands on account creation (the page has: $($firstIds -join ', '))"
    Assert-That ($first -notmatch '(?i)session expired') 'the first visit never says a session expired'

    New-ScenarioAccount
    $created = Invoke-Weir -Session $anonymous -Method POST -Path '/api/v1/auth/bootstrap' -Body @{ username = $script:Ctx.Username; password = $script:Ctx.Password }
    Assert-That ($created.Status -eq 200) "the account was created (answered $($created.Status))"

    $second = Get-PageDom -Path '/'
    $secondIds = Get-TestIds -Dom $second
    Assert-That ($secondIds -contains 'login-username') "a new browser, once an account exists, lands on sign-in (the page has: $($secondIds -join ', '))"
    Assert-That ($second -notmatch '(?i)session expired') 'the new browser is never told a session expired'

    $script:Ctx.Session = Sign-InScenarioAccount
    $me = Get-WeirJson $script:Ctx.Session '/api/v1/auth/me'
    Assert-That ($me.user.username -eq $script:Ctx.Username) "the new account signs in and Weir knows who it is ($($me.user.username), $($me.user.role))"
    Set-Content -LiteralPath (Join-Path $script:Ctx.RunFolder 'evidence\first-visit-first-page.html') -Value $first -Encoding UTF8
    Set-Content -LiteralPath (Join-Path $script:Ctx.RunFolder 'evidence\first-visit-second-page.html') -Value $second -Encoding UTF8
    'A new Edge profile saw account creation, then (after the account existed) sign-in; neither page said a session expired; the account signed in as an admin.'
}

# --- update-over-previous ---------------------------------------------------------------------------------------------

function Scenario_update_over_previous {
    $paths = Get-WeirPaths
    Assert-That (Test-Path -LiteralPath $script:Ctx.PreviousSetupPath) "the previous release's installer is in the machine ($($script:Ctx.PreviousSetupPath))"

    # 1. The previous release, installed and used.
    $previous = Invoke-WeirSetup -Path $script:Ctx.PreviousSetupPath
    Assert-That ($previous.ExitCode -eq 0) "the previous release's Setup --silent exited 0 (it exited $($previous.ExitCode))"
    Start-WeirTray | Out-Null
    Wait-WeirReady -TimeoutSeconds 120
    $oldVersion = Get-ServerVersion
    Assert-That (-not (Test-VersionUnderTest -Reported $oldVersion -Expected $script:Ctx.Version)) "the machine runs the previous release ($oldVersion), not the build under test"

    New-ScenarioAccount
    $anonymous = New-WeirSession -BaseUrl $script:Ctx.Base
    $created = Invoke-Weir -Session $anonymous -Method POST -Path '/api/v1/auth/bootstrap' -Body @{ username = $script:Ctx.Username; password = $script:Ctx.Password }
    Assert-That ($created.Status -eq 200) "the account was created on the previous release (answered $($created.Status))"
    $script:Ctx.Session = Sign-InScenarioAccount
    $before = Complete-OneHandOff -Id 'update-before' -Name 'Big Buck Bunny 2008' -Root (Join-Path $script:Ctx.MediaRoot 'update')
    $workflowsBefore = @(Get-WeirJson $script:Ctx.Session '/api/v1/processing/libraries')
    $activityBefore = @(Get-ActivityItems -Limit 200)
    Assert-That (@($activityBefore | Where-Object event_type -eq 'processing.file_remux_pass_completed').Count -ge 1) 'the previous release recorded the finished hand-off in Activity'

    # 2. The build under test, installed over the running Weir.
    $updateStarted = (Get-Date).ToUniversalTime()
    $update = Invoke-WeirSetup -Path $script:Ctx.SetupPath
    Assert-That ($update.ExitCode -eq 0) "the build under test's Setup --silent over the running Weir exited 0 (it exited $($update.ExitCode))"
    Wait-Until -What 'Weir to be running the build under test again without being started by hand' -TimeoutSeconds 240 -IntervalMilliseconds 1000 -Probe {
        Test-VersionUnderTest -Reported (Get-ServerVersion) -Expected $script:Ctx.Version -ShortSha $script:Ctx.ShortSha
    } | Out-Null
    $newVersion = Get-ServerVersion
    $tray = @(Get-Process -Name 'Weir' -ErrorAction SilentlyContinue | Where-Object { $_.Path -eq $paths.TrayExe })
    Assert-That ($tray.Count -ge 1) 'a tray from the new install is running'
    $trayLog = Get-TrayLogText
    Assert-That ($trayLog -match 'Weir was running before this install') 'the tray log says Weir was running before the install and started again'

    # 3. What survived.
    $session = Sign-InScenarioAccount
    $script:Ctx.Session = $session
    $workflowsAfter = @(Get-WeirJson $session '/api/v1/processing/libraries')
    foreach ($workflow in $workflowsBefore | Where-Object { $_.watched_folder }) {
        $same = @($workflowsAfter | Where-Object { $_.id -eq $workflow.id -and $_.watched_folder -eq $workflow.watched_folder -and $_.output_folder -eq $workflow.output_folder })
        Assert-That ($same.Count -eq 1) "the workflow '$($workflow.name)' and its folders are as they were"
    }
    $activityAfter = @(Get-ActivityItems -Limit 200)
    $kept = @($activityBefore | Where-Object { $row = $_; @($activityAfter | Where-Object { $_.id -eq $row.id }).Count -eq 1 })
    Assert-That ($kept.Count -eq $activityBefore.Count) "all $($activityBefore.Count) Activity entries from before the update are still there"

    # 4. The copy saved before the update, when the update changed the database.
    $log = Get-FileTextOrNull -Path $paths.ServerLog
    $opened = @([regex]::Matches([string]$log, '(Upgraded database to schema revision|Opened database at schema revision)=\S+') | Select-Object -Last 1)
    $overview = Get-WeirJson $session '/api/v1/system/overview'
    $backup = Get-Prop $overview 'last_update_backup'
    $backupText = 'the database was not changed by the update, so no copy was due'
    if ($opened.Count -eq 1 -and $opened[0].Value -like 'Upgraded*') {
        Assert-That ($null -ne $backup -and (Test-Path -LiteralPath $backup.path) -and (Get-Item -LiteralPath $backup.path).Length -gt 0) 'the update changed the database, so a copy of the data was saved first and System overview names it'
        $backupText = "the update upgraded the database, and the copy $([IO.Path]::GetFileName($backup.path)) was saved first"
    }

    # 5. Working on the new version.
    $after = Complete-OneHandOff -Id 'update-after' -Name 'Big Buck Bunny 2008 (after)' -Root (Join-Path $script:Ctx.MediaRoot 'update-after') -Tools $paths
    $problems = @(Get-LogPageProblems | Where-Object { [datetime]::Parse($_.at).ToUniversalTime() -ge $updateStarted.AddSeconds(-5) })
    $unexpected = @(Get-UnexpectedLogRows -Rows $problems -Known $script:Ctx.KnownLogRows)
    Assert-That ($unexpected.Count -eq 0) "the log has no unexpected warnings or errors since the update (found: $((@($unexpected | ForEach-Object { $_.title }) -join ' | ')))"
    "Previous release $oldVersion used (account, workflow, one film cleaned), then Setup --silent over it took $($update.Seconds) s and Weir came back as $newVersion by itself; account, workflow and $($activityBefore.Count) Activity entries survived; $backupText; a film handed over afterwards was cleaned in $($after.Seconds) s."
}
