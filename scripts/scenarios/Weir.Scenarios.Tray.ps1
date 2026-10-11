<#
    The scenarios about what the tray and the server leave each other, a Deluno that is really there, and the logs:
    the update buttons, the copy saved before an update, workflows set up from Deluno, and Logs.
#>

# Standing in for the tray, for a rehearsal on a machine with no tray: it keeps the heartbeat fresh and takes the update
# flags the way the real one does. A real run (the golden VM) never calls this; the real tray is there.
function Invoke-RehearsalTray {
    $dataFolder = $script:Ctx.RuntimeHome
    Write-FileAtomically -Folder $dataFolder -Name 'tray-heartbeat.json' -Text (@{ at = (Get-Date).ToUniversalTime().ToString('o') } | ConvertTo-Json -Compress)
    foreach ($flag in 'update-check-now', 'update-download-now') {
        $path = Join-Path $dataFolder $flag
        if (-not (Test-Path -LiteralPath $path)) { continue }
        Remove-Item -LiteralPath $path -Force
        $line = if ($flag -eq 'update-check-now') { 'Check-now flag detected - checking for an update.' } else { 'Download-now flag detected - downloading the update.' }
        Add-Content -LiteralPath (Get-WeirPaths).TrayLog -Value ("[{0}] {1}" -f (Get-Date -Format 'yyyy-MM-dd HH:mm:ss'), $line) -Encoding UTF8
        $state = if ($flag -eq 'update-check-now') { @{ state = 'idle'; downloaded = $false; version = $null; failure = $null } } else { @{ state = 'failed'; downloaded = $false; version = $null; failure = 'There is no newer version of Weir to download.' } }
        Write-FileAtomically -Folder $dataFolder -Name 'update-state.json' -Text ($state | ConvertTo-Json -Compress)
    }
}

function Get-UpdateState {
    Get-WeirJson $script:Ctx.Session '/api/v1/suite/update-state'
}

# Waits for the tray to say a line in its log after $FromLength characters of it, and returns the line.
function Wait-TrayLogLine {
    param([Parameter(Mandatory)] [string] $Contains, [Parameter(Mandatory)] [int] $FromLength, [int] $TimeoutSeconds = 30)
    Wait-Until -What "the tray to log '$Contains'" -TimeoutSeconds $TimeoutSeconds -IntervalMilliseconds 500 -Probe {
        if ($script:Ctx.Rehearsal) { Invoke-RehearsalTray }
        $text = Get-TrayLogText
        if ($text.Length -gt $FromLength) { $text.Substring($FromLength) -split "`r?`n" | Where-Object { $_.Contains($Contains) } | Select-Object -First 1 }
    }
}

# --- update-requests ----------------------------------------------------------------------------------------------------

function Scenario_update_requests {
    $session = $script:Ctx.Session
    if ($script:Ctx.Rehearsal) { Invoke-RehearsalTray }
    $state = Wait-Until -What 'the tray to say it is alive' -TimeoutSeconds 90 -Probe {
        if ($script:Ctx.Rehearsal) { Invoke-RehearsalTray }
        $current = Get-UpdateState
        if ($current.tray_running -eq $true) { $current }
    }
    Assert-That ($state.tray_running -eq $true) "Weir counts the tray alive, so the buttons are offered (update state: $($state.state))"

    # A tray set to install by itself must not restart Weir under the run: only the person's own buttons are pressed.
    $original = Get-WeirJson $session '/api/v1/suite/update-settings'
    Get-WeirJson $session '/api/v1/suite/update-settings' -Method PUT -Body @{ mode = 'NotifyOnly'; check_on_startup = [bool]$original.check_on_startup; check_interval_minutes = [int]$original.check_interval_minutes } | Out-Null
    try {
        $mark = (Get-TrayLogText).Length
        $check = Invoke-Weir -Session $session -Method POST -Path '/api/v1/suite/check-update' -Body @{}
        Assert-That ($check.Status -eq 200 -and $check.Json.state -eq 'checking') "Check for updates was answered 200 with the step under way ($($check.Status), state $($check.Json.state))"
        Wait-TrayLogLine -Contains 'Check-now flag detected' -FromLength $mark | Out-Null
        $checked = Wait-Until -What 'the check to come to an answer' -TimeoutSeconds 180 -IntervalMilliseconds 1000 -Probe {
            if ($script:Ctx.Rehearsal) { Invoke-RehearsalTray }
            $current = Get-UpdateState
            if ($current.state -in 'idle', 'failed', 'downloaded') { $current }
        }
        $checkedText = if ($checked.state -eq 'failed') { "failed with ""$($checked.failure)""" } elseif ($checked.state -eq 'downloaded') { "found and downloaded $($checked.pending_version)" } else { 'found nothing newer' }
        Assert-That (-not ($checked.state -eq 'failed' -and -not $checked.failure)) "a failed check says why ($checkedText)"

        if ($checked.state -ne 'downloaded') {
            $mark = (Get-TrayLogText).Length
            $download = Invoke-Weir -Session $session -Method POST -Path '/api/v1/suite/download-update' -Body @{}
            Assert-That ($download.Status -eq 200 -and $download.Json.state -eq 'downloading') "Download update was answered 200 with the step under way ($($download.Status), state $($download.Json.state))"
            Wait-TrayLogLine -Contains 'Download-now flag detected' -FromLength $mark | Out-Null
            $downloaded = Wait-Until -What 'the download request to come to an answer' -TimeoutSeconds 600 -IntervalMilliseconds 1000 -Probe {
                if ($script:Ctx.Rehearsal) { Invoke-RehearsalTray }
                $current = Get-UpdateState
                if ($current.state -in 'idle', 'failed', 'downloaded') { $current }
            }
            Assert-That (-not ($downloaded.state -eq 'failed' -and -not $downloaded.failure)) 'a failed download says why'
            $downloadText = if ($downloaded.state -eq 'downloaded') { "downloaded $($downloaded.pending_version)" } else { "answered ""$($downloaded.failure)""" }
            $apply = Invoke-Weir -Session $session -Method POST -Path '/api/v1/suite/apply-update' -Body @{}
            if ($downloaded.state -ne 'downloaded') { Assert-That ($apply.Status -eq 409) "Restart and apply with nothing downloaded is refused with 409 ($($apply.Status): $($apply.Json.detail))" }
        }
        else { $downloadText = 'not pressed: the check had already downloaded the update' }
    }
    finally {
        Get-WeirJson $session '/api/v1/suite/update-settings' -Method PUT -Body @{ mode = $original.mode; check_on_startup = [bool]$original.check_on_startup; check_interval_minutes = [int]$original.check_interval_minutes } | Out-Null
    }
    "The tray was alive; Check for updates -> the tray logged ""Check-now flag detected"" and the check $checkedText; Download update -> the tray logged ""Download-now flag detected"" and $downloadText."
}

# --- pre-update-copy ----------------------------------------------------------------------------------------------------

function Scenario_pre_update_copy {
    $dataFolder = $script:Ctx.RuntimeHome
    $session = $script:Ctx.Session
    $ready = Wait-Until -What 'the server to say it can take a request for a copy of its data' -TimeoutSeconds 60 -Probe {
        $text = Get-FileTextOrNull -Path (Join-Path $dataFolder 'update-backup-ready')
        if ($text) { $text | ConvertFrom-Json }
    }
    Assert-That ([int]$ready.pid -gt 0) "the server's ready marker names process $($ready.pid)"

    $id = 'scenario-' + [guid]::NewGuid().ToString('N')
    $target = '9.9.9'
    Write-FileAtomically -Folder $dataFolder -Name 'update-backup-request.json' -Text (@{ id = $id; requested_at = (Get-Date).ToUniversalTime().ToString('o'); target_version = $target } | ConvertTo-Json -Compress)
    $answer = Wait-Until -What 'the server to save the copy' -TimeoutSeconds 120 -IntervalMilliseconds 250 -Probe {
        $text = Get-FileTextOrNull -Path (Join-Path $dataFolder 'update-backup-result.json')
        if ($text) { $result = $text | ConvertFrom-Json; if ($result.id -eq $id -and $result.state -in 'saved', 'failed') { $result } }
    }
    Assert-That ($answer.state -eq 'saved') "the server saved the copy (state $($answer.state)$(if ($answer.PSObject.Properties['reason']) { ': ' + $answer.reason }))"
    $expectedFolder = Join-Path $dataFolder 'backups\pre-update'
    Assert-That ((Split-Path -Parent $answer.path) -eq $expectedFolder) "the copy is under $expectedFolder"
    Assert-That ((Split-Path -Leaf $answer.path) -match '^weir-\d{4}-to-9\.9\.9-\d{8}T\d{6}Z\.db$') "the copy is named for the version it was saved before ($(Split-Path -Leaf $answer.path))"
    $size = (Get-Item -LiteralPath $answer.path).Length
    Assert-That ($size -gt 0) "the copy is not empty ($size bytes)"
    Wait-Until -What 'the server to take the answered request away' -TimeoutSeconds 30 -Probe { -not (Test-Path -LiteralPath (Join-Path $dataFolder 'update-backup-request.json')) } | Out-Null
    $overview = Get-WeirJson $session '/api/v1/system/overview'
    Assert-That ($overview.last_update_backup.path -eq $answer.path -and $overview.last_update_backup.to_version -eq $target) 'System overview names that copy as the last update backup'
    "The server (pid $($ready.pid)) answered the tray's request with a saved copy, $(Split-Path -Leaf $answer.path) ($size bytes), and System overview names it."
}

# --- workflows-from-deluno ----------------------------------------------------------------------------------------------

function Scenario_workflows_from_deluno {
    if (-not $script:Ctx.DelunoUrl) {
        return [pscustomobject]@{ NotApplicable = $true; Detail = 'No Deluno was given to this run (-DelunoUrl and -DelunoApiKey), so the step was not run. The real Deluno''s side of the set-up is covered by the golden path''s own checklist.' }
    }
    $session = $script:Ctx.Session
    $created = Invoke-Weir -Session $session -Method POST -Path '/api/v1/media-managers/connections' -Body @{ kind = 'deluno'; base_url = $script:Ctx.DelunoUrl; api_key = $script:Ctx.DelunoApiKey; enabled = $true }
    Assert-That ($created.Status -in 200, 201) "Weir accepted the connection to Deluno at $($script:Ctx.DelunoUrl) (answered $($created.Status): $($created.Text))"
    $connectionId = [int]$created.Json.id
    $workflows = Wait-Until -What 'Weir to set up its workflows from Deluno' -TimeoutSeconds 120 -IntervalMilliseconds 2000 -Probe {
        Invoke-Weir -Session $session -Method POST -Path "/api/v1/media-managers/connections/$connectionId/test" -Body @{} | Out-Null
        $linked = @((Get-WeirJson $session '/api/v1/processing/libraries') | Where-Object { $_.folders_synced_from_connection_id -eq $connectionId -and $_.watched_folder -and $_.output_folder })
        if ($linked.Count -ge 1) { $linked }
    }
    foreach ($workflow in $workflows) {
        Assert-That (@($workflow.manager_connection_ids) -contains $connectionId) "'$($workflow.name)' is linked to Deluno"
        Assert-That ($workflow.watched_folder -and $workflow.output_folder) "'$($workflow.name)' has its folders from Deluno (watched $($workflow.watched_folder), output $($workflow.output_folder))"
        $chain = Get-WeirJson $session "/api/v1/processing/libraries/$($workflow.id)/folder-chain"
        Assert-That ($chain.ready -eq $true) "the folder chain for '$($workflow.name)' is ready"
    }
    "Weir set up $($workflows.Count) workflow(s) from Deluno ($(@($workflows | ForEach-Object { $_.name }) -join ', ')), linked, with their folders from Deluno and every step of the folder chain ready."
}

# --- logs-clean ---------------------------------------------------------------------------------------------------------

# Lines of Weir's JSON server log at warning or worse, as { Line; Level; Message }.
function Get-ServerLogProblems {
    param([Parameter(Mandatory)] [string] $Path)
    $text = Get-FileTextOrNull -Path $Path
    if (-not $text) { return @() }
    foreach ($line in ($text -split "`r?`n")) {
        if (-not $line.Trim().StartsWith('{')) { continue }
        try { $entry = $line | ConvertFrom-Json } catch { continue }
        if ([string]$entry.level -in 'WARNING', 'ERROR', 'CRITICAL', 'FATAL') {
            [pscustomobject]@{ Level = $entry.level; Message = [string]$entry.message; Logger = [string]$entry.logger }
        }
    }
}

# Lines of the tray's log that report trouble.
function Get-TrayLogProblems {
    $pattern = '(?i)(could not|unhandled|fatal|exited unexpectedly|giving up|failed|did not stop|killing it|stopped after an error|not starting|error)'
    @((Get-TrayLogText) -split "`r?`n" | Where-Object { $_.Trim() -and $_ -match $pattern })
}

function Scenario_logs_clean {
    $paths = Get-WeirPaths
    $known = @($script:Ctx.KnownLogRows)
    $page = @(Get-LogPageProblems)
    $unexpectedPage = @(Get-UnexpectedLogRows -Rows $page -Known $known)
    Assert-That ($unexpectedPage.Count -eq 0) "System > Logs shows no unexpected warning or error (found $($unexpectedPage.Count): $((@($unexpectedPage | ForEach-Object { $_.title }) -join ' | ')))"

    $server = @(Get-ServerLogProblems -Path $paths.ServerLog)
    $serverText = Get-FileTextOrNull -Path $paths.ServerLog
    Assert-That ($serverText -and $serverText.Length -gt 0) "the server log exists and has lines ($($paths.ServerLog))"
    $unexpectedServer = @($server | Where-Object { $row = $_; -not (@($known | Where-Object { $row.Message -match $_.Pattern }).Count -gt 0) })
    Assert-That ($unexpectedServer.Count -eq 0) "the server log file has no unexpected warning or error (found $($unexpectedServer.Count): $((@($unexpectedServer | ForEach-Object { $_.Level + ' ' + $_.Message }) -join ' | ')))"

    $trayText = Get-TrayLogText
    Assert-That ($trayText.Length -gt 0) "the tray log exists and has lines ($($paths.TrayLog))"
    $tray = @(Get-TrayLogProblems | Where-Object { $line = $_; -not (@($known | Where-Object { $line -match $_.Pattern }).Count -gt 0) })
    Assert-That ($tray.Count -eq 0) "the tray log has no line reporting trouble (found $($tray.Count): $(($tray -join ' | ')))"
    $explained = @($page).Count + @($server).Count - $unexpectedPage.Count - $unexpectedServer.Count
    $onPurpose = if ($known.Count -gt 0) { "$explained caused on purpose and named in the suite ($((@($known | ForEach-Object { $_.Why }) -join '; ')))" } else { 'none were caused on purpose' }
    "Logs page: $($page.Count) warning/error row(s); server log file: $(@($server).Count); tray log: no line reporting trouble; $onPurpose."
}
