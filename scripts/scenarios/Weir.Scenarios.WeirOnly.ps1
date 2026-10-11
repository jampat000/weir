<#
    The scenarios that need no manager: a film dropped into a Weir-only workflow, Process again, pause and resume from the
    tray, and a file deleted while it waits. Also the helpers the update scenario uses to give the old Weir something to keep.

    The film is made with the FFmpeg the installed Weir ships (or `-SourceFilm`, a real film given to the run), and Weir cleans it
    with that same FFmpeg, so these scenarios prove the installed build's own tools work, not a copy of them.
#>

function Get-ScenarioTool {
    param([Parameter(Mandatory)] [ValidateSet('ffmpeg', 'ffprobe')] [string] $Name)
    if ($script:Ctx.ToolsFolder) {
        $found = Get-ChildItem -LiteralPath $script:Ctx.ToolsFolder -Filter "$Name.exe" -ErrorAction SilentlyContinue | Select-Object -First 1
        if (-not $found) { throw "$Name.exe is not in -ToolsFolder $($script:Ctx.ToolsFolder)." }
        return $found.FullName
    }
    Find-WeirTool -InstallRoot (Join-Path $script:Ctx.InstallRoot 'current') -Name $Name
}

# The film every release in the run is a copy of. Made once; a real film is used as it is when the run is given one.
function Get-FilmMaster {
    if ($script:Ctx.ContainsKey('FilmMaster')) { return $script:Ctx.FilmMaster }
    $folder = Join-Path $script:Ctx.MediaRoot 'source'
    New-Item -ItemType Directory -Force -Path $folder | Out-Null
    if ($script:Ctx.SourceFilm) {
        $master = Join-Path $folder ([IO.Path]::GetFileName($script:Ctx.SourceFilm))
        Copy-Item -LiteralPath $script:Ctx.SourceFilm -Destination $master -Force
        $generated = $false
    }
    else {
        $master = (New-ScenarioFilm -Ffmpeg (Get-ScenarioTool ffmpeg) -Path (Join-Path $folder 'Big.Buck.Bunny.2008.mkv') -Seconds 30).FullName
        $generated = $true
    }
    $summary = Get-MediaSummary -Ffprobe (Get-ScenarioTool ffprobe) -Path $master
    $script:Ctx.FilmMaster = [pscustomobject]@{ Path = $master; Generated = $generated; Summary = $summary; Sha256 = (Get-FileHash -LiteralPath $master -Algorithm SHA256).Hash }
    Add-Evidence "film: $master, $($summary.Bytes) bytes, $([int]$summary.Seconds) s, video $($summary.Video), audio [$($summary.Audio -join ',')], subtitles [$($summary.Subtitles -join ',')], sha256 $($script:Ctx.FilmMaster.Sha256)"
    $script:Ctx.FilmMaster
}

# A movie workflow with no manager, over folders of its own. Originals are kept so the scenarios can see them.
function New-ScenarioWorkflow {
    param(
        [Parameter(Mandatory)] [string] $Name,
        [Parameter(Mandatory)] [string] $Root,
        [int] $MinimumMegabytes = 1,
        [int] $ReadyAfterSeconds = 0
    )
    $folders = @{}
    foreach ($kind in 'watched', 'work', 'output') {
        $folders[$kind] = (New-Item -ItemType Directory -Force -Path (Join-Path $Root $kind)).FullName
    }
    $created = Get-WeirJson $script:Ctx.Session '/api/v1/processing/libraries' -Method POST -Body @{
        name = $Name; media_type = 'movie'
        watched_folder = $folders.watched; work_folder = $folders.work; output_folder = $folders.output
        ready_after_seconds = $ReadyAfterSeconds; min_file_size_mb = $MinimumMegabytes; skip_access_tests = $true
        remove_original_after_success = $false; retry_backoff_seconds = 1; minimum_free_disk_space_mb = 0 }
    Add-Evidence "workflow '$Name' (id $($created.id)): watched $($folders.watched), output $($folders.output)"
    [pscustomobject]@{ Id = [int]$created.id; Name = $Name; Watched = $folders.watched; Work = $folders.work; Output = $folders.output }
}

# A release folder in the workflow's watched folder holding the film and an .nfo.
function New-Release {
    param([Parameter(Mandatory)] [string] $Name, [Parameter(Mandatory)] $Workflow)
    $folder = (New-Item -ItemType Directory -Force -Path (Join-Path $Workflow.Watched $Name)).FullName
    $film = Join-Path $folder "$Name.mkv"
    Copy-Item -LiteralPath (Get-FilmMaster).Path -Destination $film -Force
    $nfo = Join-Path $folder "$Name.nfo"
    Set-Content -LiteralPath $nfo -Value "Scenario release $Name" -Encoding ASCII
    [pscustomobject]@{ Folder = $folder; Film = $film; Nfo = $nfo; Relative = "$Name/$Name.mkv"; Output = Join-Path $Workflow.Output "$Name\$Name.mkv" }
}

# What a cleaned film must look like. A film made here has known tracks, so the check is exact; a real film given to the
# run is judged on what must hold for any film: the video kept, no new tracks, nothing longer, smaller or the same size.
function Assert-CleanedFilm {
    param([Parameter(Mandatory)] [string] $OutputPath)
    $master = Get-FilmMaster
    Assert-That (Test-Path -LiteralPath $OutputPath -PathType Leaf) "the cleaned film is at $OutputPath"
    $cleaned = Get-MediaSummary -Ffprobe (Get-ScenarioTool ffprobe) -Path $OutputPath
    Assert-That ($cleaned.Video -eq $master.Summary.Video) "the video is kept ($($cleaned.Video) track)"
    Assert-That ([math]::Abs($cleaned.Seconds - $master.Summary.Seconds) -lt 1.5) "the film's length is kept ($([math]::Round($cleaned.Seconds, 1)) s of $([math]::Round($master.Summary.Seconds, 1)) s)"
    if ($master.Generated) {
        Assert-That ((@($cleaned.Audio) -join ',') -eq 'eng') "only the English audio is kept (kept: $(@($cleaned.Audio) -join ','); it had $(@($master.Summary.Audio) -join ','))"
        Assert-That (@($cleaned.Subtitles).Count -eq 0) "the subtitle tracks are removed (it had $(@($master.Summary.Subtitles) -join ','))"
        Assert-That ($cleaned.Bytes -lt $master.Summary.Bytes) "the cleaned film is smaller ($($cleaned.Bytes) bytes of $($master.Summary.Bytes))"
    }
    else {
        Assert-That (@($cleaned.Audio).Count -le @($master.Summary.Audio).Count -and @($cleaned.Subtitles).Count -le @($master.Summary.Subtitles).Count) 'no track was added'
        Assert-That ($cleaned.Bytes -le $master.Summary.Bytes + 1MB) 'the cleaned film is no larger than the original'
    }
    $cleaned
}

function Assert-OriginalsKept {
    param([Parameter(Mandatory)] $Release)
    Assert-That (Test-Path -LiteralPath $Release.Film) "the original film is still where it was ($($Release.Film))"
    Assert-That (Test-Path -LiteralPath $Release.Nfo) 'its .nfo is still beside it'
    Assert-That ((Get-FileHash -LiteralPath $Release.Film -Algorithm SHA256).Hash -eq (Get-FilmMaster).Sha256) 'the original film is byte-for-byte what was dropped in'
}

# Asks Weir to look at a workflow's watched folder, and to queue what it finds when -QueueFiles (as a periodic scan does).
function Start-WatchedFolderLook {
    param([Parameter(Mandatory)] [int] $LibraryId, [switch] $QueueFiles)
    $queued = Invoke-Weir -Session $script:Ctx.Session -Method POST -Path '/api/v1/processing/jobs/watched-folder-remux-scan-dispatch/enqueue' -Body @{
        media_scope = 'movie'; library_id = $LibraryId; enqueue_remux_jobs = [bool]$QueueFiles }
    if ($queued.Status -ne 200) { throw "Asking Weir to look at the watched folder was answered $($queued.Status): $($queued.Text)" }
}

function Get-ProcessedFileRow {
    param([Parameter(Mandatory)] $Workflow, [Parameter(Mandatory)] $Release)
    Get-ScenarioFiles -LibraryId $Workflow.Id | Where-Object { $_.relative_path -eq $Release.Relative -and $_.status -eq 'processed' } | Select-Object -First 1
}

# Drops a release into the workflow's watched folder, asks Weir to look, and waits for the film to be cleaned.
function Invoke-WeirOnlyFilm {
    param([Parameter(Mandatory)] [string] $Name, [Parameter(Mandatory)] $Workflow)
    $release = New-Release -Name $Name -Workflow $Workflow
    $clock = [Diagnostics.Stopwatch]::StartNew()
    Start-WatchedFolderLook -LibraryId $Workflow.Id -QueueFiles
    $row = Wait-Until -What "Weir to clean $($release.Relative)" -TimeoutSeconds 180 -IntervalMilliseconds 1000 -Probe { Get-ProcessedFileRow -Workflow $Workflow -Release $release }
    [pscustomobject]@{ Release = $release; Workflow = $Workflow; Row = $row; Seconds = [int]$clock.Elapsed.TotalSeconds }
}

function Initialize-FilmsWorkflow {
    if (-not $script:Ctx.ContainsKey('Films')) { $script:Ctx.Films = New-ScenarioWorkflow -Name 'Scenario films' -Root (Join-Path $script:Ctx.MediaRoot 'films') }
    $script:Ctx.Films
}

# --- weir-only-film -----------------------------------------------------------------------------------------------------

function Scenario_weir_only_film {
    $workflow = Initialize-FilmsWorkflow
    $done = Invoke-WeirOnlyFilm -Name 'Big.Buck.Bunny.2008' -Workflow $workflow
    $script:Ctx.Cleaned = $done
    $cleaned = Assert-CleanedFilm -OutputPath $done.Release.Output
    Assert-OriginalsKept -Release $done.Release
    $titles = @(Get-ActivityItems | ForEach-Object { "$($_.event_type)|$($_.title)" })
    Assert-That (@($titles | Where-Object { $_ -like 'processing.file_remux_pass_completed|*Big.Buck.Bunny.2008.mkv*' }).Count -ge 1) 'Activity says the film was processed'
    $master = Get-FilmMaster
    "A $([int]$master.Summary.Seconds) s film ($($master.Summary.Bytes) bytes, audio $(@($master.Summary.Audio) -join '/'), $(@($master.Summary.Subtitles).Count) subtitle tracks) dropped into a Weir-only workflow was cleaned in $($done.Seconds) s to $($cleaned.Bytes) bytes with audio $(@($cleaned.Audio) -join '/') and no subtitles; the original and its .nfo are untouched; Activity says it was processed."
}

# --- process-again ------------------------------------------------------------------------------------------------------

function Scenario_process_again {
    if (-not $script:Ctx.ContainsKey('Cleaned')) { $script:Ctx.Cleaned = Invoke-WeirOnlyFilm -Name 'Retry.Film.2017' -Workflow (Initialize-FilmsWorkflow) }
    $cleaned = $script:Ctx.Cleaned
    $name = Split-Path -Leaf $cleaned.Release.Film
    $skipLinesBefore = @(Get-ActivityItems -EventType 'processing.file_skipped_repeat').Count
    $jobsBefore = @(Get-RemuxJobs).Count
    $written = (Get-Item -LiteralPath $cleaned.Release.Output).LastWriteTimeUtc
    $answer = Invoke-Weir -Session $script:Ctx.Session -Method POST -Path "/api/v1/processing/files/$([int]$cleaned.Row.id)/requeue" -Body @{}
    Assert-That ($answer.Status -eq 200) "Process again was answered 200 (it was $($answer.Status): $($answer.Text))"
    Assert-That ($answer.Json.requeued -eq 0 -and $answer.Json.skipped -eq 1) "nothing was queued and one file was skipped (requeued $($answer.Json.requeued), skipped $($answer.Json.skipped))"
    Assert-That ($answer.Json.detail -like 'Weir already cleaned this file, so it skipped it. Already done: cleaned on *') "the answer says Weir already cleaned this file and skipped it: $($answer.Json.detail)"
    $line = Wait-Until -What 'the Skipped line in Activity' -TimeoutSeconds 60 -Probe {
        $found = @(Get-ActivityItems -EventType 'processing.file_skipped_repeat')
        if ($found.Count -gt $skipLinesBefore) { $found | Where-Object { $_.trigger -eq 'manual' } | Select-Object -First 1 }
    }
    Assert-That ($line.title -eq "Skipped: already done ($name)" -and $line.result -eq 'skipped') "Activity says ""$($line.title)"""
    Assert-That ((Get-Item -LiteralPath $cleaned.Release.Output).LastWriteTimeUtc -eq $written) 'the cleaned file was not written a second time'
    Assert-That (@(Get-RemuxJobs).Count -eq $jobsBefore) 'no second job was made'
    $row = Get-ScenarioFiles -LibraryId $cleaned.Workflow.Id | Where-Object { $_.id -eq $cleaned.Row.id } | Select-Object -First 1
    Assert-That ($row.status -eq 'processed') "the file still reads processed (it reads $($row.status))"
    "Process again answered: ""$($answer.Json.detail)""; Activity gained ""$($line.title)""; the output and the job count were unchanged."
}

# --- pause-resume -------------------------------------------------------------------------------------------------------

function Write-TrayPauseRequest {
    param([Parameter(Mandatory)] [bool] $Paused)
    $text = @{ paused = $Paused; requested_at = (Get-Date).ToUniversalTime().ToString('o') } | ConvertTo-Json -Compress
    Write-FileAtomically -Folder $script:Ctx.RuntimeHome -Name 'pause-request.json' -Text $text
    Add-Evidence "wrote $($script:Ctx.RuntimeHome)\pause-request.json: $text"
}

function Get-StartedRemuxJobCount {
    @(Get-RemuxJobs | Where-Object { $_.status -in 'leased', 'completed', 'failed' }).Count
}

function Scenario_pause_resume {
    $workflow = Initialize-FilmsWorkflow
    $session = $script:Ctx.Session
    Assert-That ((Get-WeirJson $session '/api/v1/pause').paused -eq $false) 'Weir is not paused to begin with'

    Write-TrayPauseRequest -Paused $true
    $paused = Wait-Until -What 'Weir to pause on the tray''s request' -TimeoutSeconds 30 -Probe { $state = Get-WeirJson $session '/api/v1/pause'; if ($state.paused) { $state } }
    Assert-That ($paused.scan_while_paused -eq $true) 'the pause left "keep looking for new files" as it was'
    Wait-Until -What 'Weir to take the request file away' -TimeoutSeconds 30 -Probe { -not (Test-Path -LiteralPath (Join-Path $script:Ctx.RuntimeHome 'pause-request.json')) } | Out-Null

    $startedBefore = Get-StartedRemuxJobCount
    $release = New-Release -Name 'Paused.Film.2018' -Workflow $workflow
    Start-WatchedFolderLook -LibraryId $workflow.Id -QueueFiles
    $held = Wait-Until -What 'Weir to find the film and hold it' -TimeoutSeconds 90 -IntervalMilliseconds 1000 -Probe {
        Get-ScenarioFiles -LibraryId $workflow.Id | Where-Object { $_.relative_path -eq $release.Relative } | Select-Object -First 1
    }
    Assert-That ($held.status -ne 'processed' -and $held.status -ne 'processing') "Weir found the film and is holding it (status $($held.status): $($held.status_reason))"
    Assert-That ([string]$held.status_reason -like 'Processing is paused*') "the reason it waits is the pause (""$($held.status_reason)"")"
    Assert-NeverWithin -Seconds 20 -What 'Weir cleaning, writing or removing anything while paused' -Happened {
        (Get-StartedRemuxJobCount) -ne $startedBefore -or (Test-Path -LiteralPath $release.Output) -or -not (Test-Path -LiteralPath $release.Film) -or -not (Test-Path -LiteralPath $release.Nfo)
    }

    Start-Sleep -Seconds 1
    Write-TrayPauseRequest -Paused $false
    Wait-Until -What 'Weir to resume on the tray''s request' -TimeoutSeconds 30 -Probe { -not (Get-WeirJson $session '/api/v1/pause').paused } | Out-Null
    $look = @{ Asked = $false }
    $watch = [Diagnostics.Stopwatch]::StartNew()
    $row = Wait-Until -What 'the waiting film to be cleaned once Weir resumed' -TimeoutSeconds 180 -IntervalMilliseconds 1000 -Probe {
        if (-not $look.Asked -and $watch.Elapsed.TotalSeconds -gt 45) { $look.Asked = $true; Start-WatchedFolderLook -LibraryId $workflow.Id -QueueFiles }
        Get-ProcessedFileRow -Workflow $workflow -Release $release
    }
    Assert-CleanedFilm -OutputPath $release.Output | Out-Null
    Assert-That ((Get-StartedRemuxJobCount) -eq $startedBefore + 1) 'exactly one pass ran for it'
    Assert-OriginalsKept -Release $release

    $pausedLines = @(Get-ActivityItems -EventType 'system.processing_paused' | Where-Object { ($_ | ConvertTo-Json -Depth 5 -Compress) -match 'The tray' })
    $resumedLines = @(Get-ActivityItems -EventType 'system.processing_resumed' | Where-Object { ($_ | ConvertTo-Json -Depth 5 -Compress) -match 'The tray' })
    Assert-That ($pausedLines.Count -ge 1 -and $resumedLines.Count -ge 1) 'Activity shows Paused and Resumed, by the tray'
    $looked = if ($look.Asked) { ' (after a look at the folder was asked for, as a periodic scan would do)' } else { ' (without any further look)' }
    "Pause and resume requests in the tray's file were taken and deleted; a film dropped in during the pause was found and held (""$($held.status_reason)"") with nothing cleaned, written or removed for 20 s, then cleaned once after the resume$looked in $([int]$watch.Elapsed.TotalSeconds) s; Activity shows both by the tray."
}

# --- deleted-queued-file ------------------------------------------------------------------------------------------------

function Scenario_deleted_queued_file {
    $root = Join-Path $script:Ctx.MediaRoot 'settling'
    $workflow = New-ScenarioWorkflow -Name 'Scenario settling' -Root $root -MinimumMegabytes 50 -ReadyAfterSeconds 60
    $file = Join-Path $workflow.Watched 'Harbour Lights 2024.mkv'
    [IO.File]::WriteAllBytes($file, [byte[]](1, 2, 3, 4, 5))
    $relative = 'Harbour Lights 2024.mkv'
    $problemsBefore = @(Get-LogPageProblems).Count

    Start-WatchedFolderLook -LibraryId $workflow.Id
    $waiting = Wait-Until -What 'Weir to find the file and wait for it to settle' -TimeoutSeconds 90 -IntervalMilliseconds 1000 -Probe {
        Get-ScenarioFiles -LibraryId $workflow.Id | Where-Object { $_.relative_path -eq $relative } | Select-Object -First 1
    }
    Assert-That ($waiting.status -eq 'on_hold' -and -not $waiting.source_gone) "Weir is waiting for the file to settle (status $($waiting.status): $($waiting.status_reason))"

    Remove-Item -LiteralPath $file -Force
    $gone = Wait-Until -What 'Weir to notice the file is no longer there' -TimeoutSeconds 90 -IntervalMilliseconds 2000 -Probe {
        Start-WatchedFolderLook -LibraryId $workflow.Id
        Start-Sleep -Seconds 2
        Get-ScenarioFiles -LibraryId $workflow.Id | Where-Object { $_.relative_path -eq $relative -and $_.source_gone } | Select-Object -First 1
    }
    Assert-That ($gone.status -ne 'processing_failed' -and $gone.status -ne 'rejected' -and $null -eq $gone.failure_class) "the vanished file is not a failure (status $($gone.status), failure class $($gone.failure_class))"
    $counts = (Get-WeirJson $script:Ctx.Session "/api/v1/processing/files?limit=1&library_id=$($workflow.Id)").status_counts
    Assert-That ($counts.processing_failed -eq 0 -and $counts.rejected -eq 0) 'nothing in the workflow needs the person'
    $failures = @(Get-ActivityItems | Where-Object { $_.title -like '*Harbour Lights*' -and $_.result -in 'failed', 'warning', 'error' })
    Assert-That ($failures.Count -eq 0) 'Activity has no failure or warning about the file'
    $problemsAfter = @(Get-LogPageProblems).Count
    Assert-That ($problemsAfter -eq $problemsBefore) "the log gained no warning or error ($problemsBefore before, $problemsAfter after)"
    "Weir held the 5-byte file while it settled (""$($waiting.status_reason)""), then, once it was deleted, marked it no longer there (status $($gone.status), source_gone true) with no failure, nothing needing the person and no new warning."
}
