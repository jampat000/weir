<#
    The update scenario: the build under test installed over the Weir that Deluno's picker installed, with that Weir running and
    holding what it was used for. Needs Weir.Scenarios.Install.ps1 (Invoke-WeirSetup) and Weir.Scenarios.WeirOnly.ps1.
#>

function Scenario_update_over_installed_weir {
    $paths = Get-WeirPaths
    Assert-That (Test-Path -LiteralPath $script:Ctx.SetupPath) "the build under test's installer is in the machine ($($script:Ctx.SetupPath))"

    # 1. The Weir Deluno's picker installed, as it was left.
    Wait-WeirReady -TimeoutSeconds 60
    $oldVersion = Get-ServerVersion
    Assert-That (-not (Test-VersionUnderTest -Reported $oldVersion -Expected $script:Ctx.Version)) "the machine runs the Weir Deluno installed ($oldVersion), not the build under test"
    if (-not (Initialize-WeirSession)) { return New-NotApplicable $script:Ctx.NoSessionReason }
    $workflow = New-ScenarioWorkflow -Name 'Scenario survivors' -Root (Join-Path $script:Ctx.MediaRoot 'update')
    $before = Invoke-WeirOnlyFilm -Name 'Survivor.Film.2010' -Workflow $workflow
    Assert-CleanedFilm -OutputPath $before.Release.Output | Out-Null
    $workflowsBefore = @(Get-WeirJson $script:Ctx.Session '/api/v1/processing/libraries')
    $activityBefore = @(Get-ActivityItems -Limit 100)
    Assert-That (@($activityBefore | Where-Object event_type -eq 'processing.file_remux_pass_completed').Count -ge 1) "Weir $oldVersion recorded the film it cleaned in Activity"

    # 2. The build under test, installed over the running Weir.
    $update = Invoke-WeirSetup -Path $script:Ctx.SetupPath
    Assert-That ($update.ExitCode -eq 0) "the build under test's Setup --silent over the running Weir exited 0 (it exited $($update.ExitCode))"
    Wait-Until -What 'Weir to be running the build under test again without being started by hand' -TimeoutSeconds 240 -IntervalMilliseconds 1000 -Probe {
        Test-VersionUnderTest -Reported (Get-ServerVersion) -Expected $script:Ctx.Version -ShortSha $script:Ctx.ShortSha
    } | Out-Null
    $newVersion = Get-ServerVersion
    $tray = @(Get-Process -Name 'Weir' -ErrorAction SilentlyContinue | Where-Object { $_.Path -eq $paths.TrayExe })
    Assert-That ($tray.Count -ge 1) 'a tray from the new install is running'
    Assert-That ((Get-TrayLogText) -match 'Weir was running before this install') 'the tray log says Weir was running before the install and started again'

    # 3. What survived.
    $script:Ctx.Session = Sign-InScenarioAccount
    $session = $script:Ctx.Session
    $workflowsAfter = @(Get-WeirJson $session '/api/v1/processing/libraries')
    foreach ($kept in $workflowsBefore | Where-Object { $_.watched_folder }) {
        $same = @($workflowsAfter | Where-Object { $_.id -eq $kept.id -and $_.watched_folder -eq $kept.watched_folder -and $_.output_folder -eq $kept.output_folder })
        Assert-That ($same.Count -eq 1) "the workflow '$($kept.name)' and its folders are as they were"
    }
    $activityAfter = @(Get-ActivityItems -Limit 100)
    $stillThere = @($activityBefore | Where-Object { $row = $_; @($activityAfter | Where-Object { $_.id -eq $row.id }).Count -eq 1 })
    Assert-That ($stillThere.Count -eq $activityBefore.Count) "all $($activityBefore.Count) Activity entries from before the update are still there"

    # 4. The copy saved before the update, when the update changed the database; and the tray's own request for one.
    $log = Get-FileTextOrNull -Path $paths.ServerLog
    $opened = @([regex]::Matches([string]$log, '(Upgraded database to schema revision|Opened database at schema revision)=\S+') | Select-Object -Last 1)
    $backup = Get-Prop (Get-WeirJson $session '/api/v1/system/overview') 'last_update_backup'
    $backupText = 'the database was not changed by the update, so no copy was due'
    if ($opened.Count -eq 1 -and $opened[0].Value -like 'Upgraded*') {
        Assert-That ($null -ne $backup -and (Test-Path -LiteralPath $backup.path) -and (Get-Item -LiteralPath $backup.path).Length -gt 0) 'the update changed the database, so a copy of the data was saved first and System overview names it'
        $backupText = "the update upgraded the database, and the copy $([IO.Path]::GetFileName($backup.path)) was saved first"
    }
    $requested = Test-PreUpdateCopyRequest

    # 5. Working on the new version.
    $after = Invoke-WeirOnlyFilm -Name 'Survivor.Film.2011' -Workflow $workflow
    Assert-CleanedFilm -OutputPath $after.Release.Output | Out-Null
    "The Weir Deluno installed ($oldVersion) was used (account, workflow, one film cleaned), then Setup --silent over it took $($update.Seconds) s and Weir came back as $newVersion by itself; account, workflow and $($activityBefore.Count) Activity entries survived; $backupText; $requested; a film dropped in afterwards was cleaned in $($after.Seconds) s."
}
