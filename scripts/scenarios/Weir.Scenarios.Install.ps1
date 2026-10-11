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

