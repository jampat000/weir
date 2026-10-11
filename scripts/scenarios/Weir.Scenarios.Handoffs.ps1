<#
    The scenarios that hand Weir real files: a film, a release with a small extra, a resend, pause and resume,
    Process again, and a file deleted while it waits.

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

# The film every hand-off in the run is a copy of. Made once; a real film is used as it is when the run is given one.
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

# The connection, the workflow and the folders the hand-off scenarios share, made once.
function Initialize-HandOffs {
    if ($script:Ctx.ContainsKey('Films')) { return }
    Connect-StandInManager
    $script:Ctx.Films = New-ScenarioWorkflow -Name 'Scenario films' -Root (Join-Path $script:Ctx.MediaRoot 'films') -LinkedToStandIn -MinimumMegabytes 1
    Get-FilmMaster | Out-Null
    Add-Evidence "workflow 'Scenario films' (id $($script:Ctx.Films.Id)): watched $($script:Ctx.Films.Watched), output $($script:Ctx.Films.Output)"
}

# A release folder in the workflow's watched folder holding the film, an .nfo, and optionally a small extra.
function New-Release {
    param([Parameter(Mandatory)] [string] $Name, [switch] $WithExtra, $Workflow = $script:Ctx.Films)
    $folder = (New-Item -ItemType Directory -Force -Path (Join-Path $Workflow.Watched $Name)).FullName
    $film = Join-Path $folder "$Name.mkv"
    Copy-Item -LiteralPath (Get-FilmMaster).Path -Destination $film -Force
    $nfo = Join-Path $folder "$Name.nfo"
    Set-Content -LiteralPath $nfo -Value "Scenario release $Name" -Encoding ASCII
    $release = [ordered]@{ Folder = $folder; Film = $film; Nfo = $nfo; Extra = $null; Relative = "$Name/$Name.mkv"; Output = Join-Path $Workflow.Output "$Name\$Name.mkv" }
    if ($WithExtra) {
        $extra = Join-Path $folder 'Gallery.mkv'
        New-ScenarioFilm -Ffmpeg (Get-ScenarioTool ffmpeg) -Path $extra -Seconds 2 -AudioLanguages @('eng') -SubtitleLanguages @() | Out-Null
        $release.Extra = $extra
    }
    [pscustomobject]$release
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
    $hash = (Get-FileHash -LiteralPath $Release.Film -Algorithm SHA256).Hash
    Assert-That ($hash -eq (Get-FilmMaster).Sha256) 'the original film is byte-for-byte what was handed over'
    if ($Release.Extra) { Assert-That (Test-Path -LiteralPath $Release.Extra) 'the extra is still where it was' }
}

# Makes a workflow of its own, hands one film over and waits for it to be cleaned. For the update scenario, which uses the
# previous release's Weir and then the new one.
function Complete-OneHandOff {
    param([Parameter(Mandatory)] [string] $Id, [Parameter(Mandatory)] [string] $Name, [Parameter(Mandatory)] [string] $Root, $Tools)
    Connect-StandInManager
    $workflow = New-ScenarioWorkflow -Name $Name -Root $Root -LinkedToStandIn -MinimumMegabytes 1
    $release = New-Release -Name ($Name -replace '[^A-Za-z0-9]+', '.').Trim('.') -Workflow $workflow
    $clock = [Diagnostics.Stopwatch]::StartNew()
    Send-HandOff -Id $Id -SourcePath $release.Folder | Out-Null
    $status = Wait-HandOff -Id $Id -State completed
    Assert-CleanedFilm -OutputPath $status.outputPath | Out-Null
    [pscustomobject]@{ Id = $Id; Seconds = [int]$clock.Elapsed.TotalSeconds; OutputPath = $status.outputPath; Workflow = $workflow; Release = $release }
}

# --- handoff-film -------------------------------------------------------------------------------------------------------

function Scenario_handoff_film {
    Initialize-HandOffs
    $release = New-Release -Name 'Big.Buck.Bunny.2008'
    $id = 'scn-film'
    $clock = [Diagnostics.Stopwatch]::StartNew()
    $accepted = Send-HandOff -Id $id -SourcePath $release.Folder
    Assert-That ($accepted.status -eq 'ok' -and $accepted.event -eq 'handoff') "Weir accepted the hand-off ($($accepted.status), $($accepted.event), enqueued $($accepted.enqueued))"
    $done = Wait-HandOff -Id $id -State completed
    $seconds = [int]$clock.Elapsed.TotalSeconds
    Assert-That ($done.outputPath -eq $release.Output) "Weir reports the cleaned file at $($release.Output)"
    $cleaned = Assert-CleanedFilm -OutputPath $done.outputPath
    Assert-OriginalsKept -Release $release
    Assert-That (Test-Path -LiteralPath (Join-Path (Split-Path -Parent $done.outputPath) 'Big.Buck.Bunny.2008.nfo')) 'the .nfo travelled with the cleaned film'

    $report = @(Wait-Until -What 'the stand-in to be told the hand-off finished' -TimeoutSeconds 60 -Probe {
            $reports = @(Get-StandInReports -HandoffId $id)
            if ($reports.Count -ge 1) { $reports }
        })
    Assert-That ($report.Count -eq 1) "the stand-in received exactly one report for the hand-off (it received $($report.Count))"
    Assert-That ($report[0].Json.status -eq 'completed' -and $report[0].Json.outputPath -eq $done.outputPath) "the report says completed, naming the cleaned file ($($report[0].Json.message))"
    Assert-That ($report[0].headers.'X-Api-Key' -eq 'scenario-stand-in-key') "the report carried the connection's API key"

    $titles = @(Get-ActivityItems | ForEach-Object { "$($_.event_type)|$($_.title)" })
    Assert-That (@($titles | Where-Object { $_ -like 'processing.file_remux_pass_completed|*Big.Buck.Bunny.2008.mkv*' }).Count -ge 1) 'Activity says the film was processed'
    Assert-That (@($titles | Where-Object { $_ -like 'processing.handoff_reported|*' }).Count -ge 1) 'Activity says the stand-in was told'

    # The manager imports the copy (the stand-in plays that by copying it into its library) and says so.
    $library = Join-Path $script:Ctx.MediaRoot 'library\Big Buck Bunny (2008)'
    New-Item -ItemType Directory -Force -Path $library | Out-Null
    $imported = Join-Path $library 'Big Buck Bunny (2008).mkv'
    Copy-Item -LiteralPath $done.outputPath -Destination $imported -Force
    $outcome = Invoke-Weir -Session $script:Ctx.Session -Method POST -Path "/api/v1/intake/handoffs/deluno/$id/outcome" -NotABrowser -Headers (& $script:SecretHeader) -Body @{
        outcome = 'imported'; occurredUtc = (Get-Date).ToUniversalTime().ToString('yyyy-MM-dd''T''HH:mm:ss''Z'''); importedPath = $imported }
    Assert-That ($outcome.Status -eq 200 -and $outcome.Json.outcome -eq 'imported') "Weir took the imported answer ($($outcome.Status): $($outcome.Json.message))"
    Assert-That ($outcome.Json.released -eq $true) 'Weir released its own copy because the manager has the film now'
    Assert-That (-not (Test-Path -LiteralPath $done.outputPath)) 'Weir''s copy of the cleaned film is gone and the manager''s copy is the one left'
    $row = Wait-Until -What 'the file to show the manager imported it' -TimeoutSeconds 30 -Probe {
        Get-ScenarioFiles -LibraryId $script:Ctx.Films.Id | Where-Object { $_.relative_path -eq $release.Relative -and (Get-Prop (Get-Prop $_ 'handback') 'outcome') -eq 'imported' } | Select-Object -First 1
    }
    "A $([int]$cleaned.Seconds) s film ($($script:Ctx.FilmMaster.Summary.Bytes) bytes, audio $(@($script:Ctx.FilmMaster.Summary.Audio) -join '/'), $(@($script:Ctx.FilmMaster.Summary.Subtitles).Count) subtitle tracks) was handed over and finished in $seconds s as $($cleaned.Bytes) bytes with audio $(@($cleaned.Audio) -join '/') and no subtitles; original and .nfo untouched; one completed report reached the stand-in; the imported answer released Weir's copy and the file shows $($row.handback.outcome)."
}

# --- handoff-release-with-extra ------------------------------------------------------------------------------------------

function Scenario_handoff_release_with_extra {
    Initialize-HandOffs
    $release = New-Release -Name 'Nosferatu.1922' -WithExtra
    $extraBytes = (Get-Item -LiteralPath $release.Extra).Length
    Assert-That ($extraBytes -lt 1MB) "the extra is under the workflow's 1 MB minimum ($extraBytes bytes)"
    $id = 'scn-extra'
    Send-HandOff -Id $id -SourcePath $release.Folder -ReleaseName 'Nosferatu.1922.1080p.WEB-DL' | Out-Null
    $done = Wait-HandOff -Id $id -State completed
    Assert-CleanedFilm -OutputPath $release.Output | Out-Null
    Assert-That (@($done.outputFiles).Count -eq 1 -and @($done.outputFiles)[0] -eq $release.Output) "Weir reports the film alone as the finished file (output files: $(@($done.outputFiles) -join ', '))"
    Assert-That (-not (Test-Path -LiteralPath (Join-Path $script:Ctx.Films.Output 'Nosferatu.1922\Gallery.mkv'))) 'the extra is not in the output folder'
    Assert-OriginalsKept -Release $release

    $reports = @(Get-StandInReports -HandoffId $id)
    Assert-That ($reports.Count -eq 1) "the stand-in received exactly one report (it received $($reports.Count))"
    Assert-That (@($reports[0].Json.outputFiles).Count -eq 1 -and @($reports[0].Json.outputFiles)[0] -eq $release.Output) 'the report names the film alone'

    $extraRow = Wait-Until -What 'Weir to account for the extra' -TimeoutSeconds 60 -Probe {
        Get-ScenarioFiles -LibraryId $script:Ctx.Films.Id | Where-Object { $_.relative_path -eq 'Nosferatu.1922/Gallery.mkv' } | Select-Object -First 1
    }
    Assert-That ($extraRow.status -eq 'skipped') "the extra is skipped, not failed or waiting for the person (status $($extraRow.status): $($extraRow.status_reason))"
    $counts = (Get-WeirJson $script:Ctx.Session "/api/v1/processing/files?limit=1&library_id=$($script:Ctx.Films.Id)").status_counts
    Assert-That ($counts.processing_failed -eq 0 -and $counts.rejected -eq 0) 'no file in the workflow needs the person'
    $mentions = @(Get-ActivityItems | Where-Object { $_.title -like '*Gallery*' -and $_.result -in 'failed', 'warning', 'error' })
    Assert-That ($mentions.Count -eq 0) 'Activity has no failure or warning about the extra'
    "The film was cleaned and reported once; the $extraBytes-byte extra was skipped (""$($extraRow.status_reason)""), stayed where it was, and nothing needs the person."
}

# --- handoff-resend -----------------------------------------------------------------------------------------------------

# A hand-off that was cleaned and not yet imported: the file the repeat scenarios resend and process again.
function Get-CleanedRepeat {
    if ($script:Ctx.ContainsKey('Repeat')) { return $script:Ctx.Repeat }
    Initialize-HandOffs
    $release = New-Release -Name 'Retry.Film.2017'
    $id = 'scn-repeat'
    Send-HandOff -Id $id -SourcePath $release.Folder -ReleaseName 'Retry.Film.2017.1080p.WEB-DL' | Out-Null
    $done = Wait-HandOff -Id $id -State completed
    Assert-CleanedFilm -OutputPath $release.Output | Out-Null
    $first = Wait-Until -What 'the first completed report' -TimeoutSeconds 60 -Probe { $reports = @(Get-StandInReports -HandoffId $id); if ($reports.Count -ge 1) { $reports[0] } }
    $row = Wait-Until -What 'the film to be a processed file' -TimeoutSeconds 60 -Probe {
        Get-ScenarioFiles -LibraryId $script:Ctx.Films.Id | Where-Object { $_.relative_path -eq $release.Relative -and $_.status -eq 'processed' } | Select-Object -First 1
    }
    $script:Ctx.Repeat = [pscustomobject]@{
        Id = $id; Release = $release; OutputPath = $done.outputPath; FileId = [int]$row.id; FirstReport = $first
        WrittenAt = (Get-Item -LiteralPath $release.Output).LastWriteTimeUtc; JobsBefore = @(Get-RemuxJobs).Count
        SkipLinesBefore = @(Get-ActivityItems -EventType 'processing.file_skipped_repeat').Count
    }
    $script:Ctx.Repeat
}

function Scenario_handoff_resend {
    $repeat = Get-CleanedRepeat
    $filmJobs = $repeat.JobsBefore

    # The same hand-off again, as a resend or a replay would send it.
    Send-HandOff -Id $repeat.Id -SourcePath $repeat.Release.Folder -ReleaseName 'Retry.Film.2017.1080p.WEB-DL' | Out-Null
    $again = Wait-Until -What 'Weir to answer the resend with a second report' -TimeoutSeconds 60 -Probe {
        $reports = @(Get-StandInReports -HandoffId $repeat.Id)
        if ($reports.Count -ge 2) { $reports[1] }
    }
    Assert-That ($again.Json.status -eq 'completed') 'the resend is answered completed, never failed'
    Assert-That ($again.Json.outputPath -eq $repeat.FirstReport.Json.outputPath) 'the resend is answered with the first run''s output path'
    Assert-That ((Get-Item -LiteralPath $repeat.Release.Output).LastWriteTimeUtc -eq $repeat.WrittenAt) 'the cleaned file was not rewritten'
    Assert-That ((Get-HandOff -Id $repeat.Id).state -eq 'completed') 'the hand-off still reads completed'

    # Another hand-off for the same download.
    $secondId = 'scn-repeat-2'
    Send-HandOff -Id $secondId -SourcePath $repeat.Release.Folder -ReleaseName 'Retry.Film.2017.1080p.WEB-DL' | Out-Null
    $other = Wait-Until -What 'a report for the second hand-off' -TimeoutSeconds 60 -Probe {
        $reports = @(Get-StandInReports -HandoffId $secondId)
        if ($reports.Count -ge 1) { $reports[0] }
    }
    Assert-That ($other.Json.status -eq 'completed' -and $other.Json.outputPath -eq $repeat.FirstReport.Json.outputPath) 'a second hand-off for the same download is answered with the same completed output'
    Assert-That ((Get-Item -LiteralPath $repeat.Release.Output).LastWriteTimeUtc -eq $repeat.WrittenAt) 'the cleaned file was still not rewritten'

    $jobs = @(Get-RemuxJobs).Count
    Assert-That ($jobs -eq $filmJobs) "no job was made for either repeat (jobs: $filmJobs before, $jobs after)"
    $lines = Wait-Until -What 'Activity to say the repeats were left alone' -TimeoutSeconds 60 -Probe {
        $found = @(Get-ActivityItems -EventType 'processing.file_skipped_repeat')
        if ($found.Count -ge $repeat.SkipLinesBefore + 2) { $found }
    }
    Assert-That (@($lines | Where-Object { $_.title -eq 'Skipped: already done (Retry.Film.2017.mkv)' -and $_.result -eq 'skipped' }).Count -ge 2) 'Activity has "Skipped: already done (Retry.Film.2017.mkv)" for both repeats'
    'The hand-off sent again, and a second id for the same download, were each answered with the first completed report and output path; the file was not rewritten, no job was made, and Activity says "Skipped: already done" twice.'
}

# --- process-again ------------------------------------------------------------------------------------------------------

function Scenario_process_again {
    $repeat = Get-CleanedRepeat
    $skipLinesBefore = @(Get-ActivityItems -EventType 'processing.file_skipped_repeat').Count
    $jobsBefore = @(Get-RemuxJobs).Count
    $written = (Get-Item -LiteralPath $repeat.Release.Output).LastWriteTimeUtc
    $answer = Invoke-Weir -Session $script:Ctx.Session -Method POST -Path "/api/v1/processing/files/$($repeat.FileId)/requeue" -Body @{}
    Assert-That ($answer.Status -eq 200) "Process again was answered 200 (it was $($answer.Status): $($answer.Text))"
    Assert-That ($answer.Json.requeued -eq 0 -and $answer.Json.skipped -eq 1) "nothing was queued and one file was skipped (requeued $($answer.Json.requeued), skipped $($answer.Json.skipped))"
    Assert-That ($answer.Json.detail -like 'Weir already cleaned this file, so it skipped it. Already done: cleaned on *') "the answer says Weir already cleaned this file and skipped it: $($answer.Json.detail)"
    $line = Wait-Until -What 'the Skipped line in Activity' -TimeoutSeconds 60 -Probe {
        $found = @(Get-ActivityItems -EventType 'processing.file_skipped_repeat')
        if ($found.Count -gt $skipLinesBefore) { $found | Where-Object { $_.trigger -eq 'manual' } | Select-Object -First 1 }
    }
    Assert-That ($line.title -eq 'Skipped: already done (Retry.Film.2017.mkv)' -and $line.result -eq 'skipped') "Activity says ""$($line.title)"""
    Assert-That ((Get-Item -LiteralPath $repeat.Release.Output).LastWriteTimeUtc -eq $written) 'the cleaned file was not written a second time'
    Assert-That (@(Get-RemuxJobs).Count -eq $jobsBefore) 'no second job was made'
    $row = Get-ScenarioFiles -LibraryId $script:Ctx.Films.Id | Where-Object { $_.id -eq $repeat.FileId } | Select-Object -First 1
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

function Scenario_pause_resume {
    Initialize-HandOffs
    $release = New-Release -Name 'Paused.Film.2018'
    $id = 'scn-paused'
    $session = $script:Ctx.Session
    Assert-That ((Get-WeirJson $session '/api/v1/pause').paused -eq $false) 'Weir is not paused to begin with'

    Write-TrayPauseRequest -Paused $true
    $paused = Wait-Until -What 'Weir to pause on the tray''s request' -TimeoutSeconds 30 -Probe { $state = Get-WeirJson $session '/api/v1/pause'; if ($state.paused) { $state } }
    Assert-That ($paused.scan_while_paused -eq $true) 'the pause left "keep looking for new files" as it was'
    Wait-Until -What 'Weir to take the request file away' -TimeoutSeconds 30 -Probe { -not (Test-Path -LiteralPath (Join-Path $script:Ctx.RuntimeHome 'pause-request.json')) } | Out-Null

    $startedBefore = @(Get-RemuxJobs | Where-Object { $_.status -in 'leased', 'completed', 'failed' }).Count
    $jobsBefore = @(Get-RemuxJobs).Count
    $accepted = Send-HandOff -Id $id -SourcePath $release.Folder -ReleaseName 'Paused.Film.2018.1080p.WEB-DL'
    Assert-That ($accepted.status -eq 'ok') 'a hand-off during the pause is accepted'
    Assert-That ((Get-HandOff -Id $id).state -eq 'queued') 'it waits, queued'
    Assert-NeverWithin -Seconds 20 -What 'Weir cleaning, writing or removing anything while paused' -Happened {
        (Get-HandOff -Id $id).state -ne 'queued' -or (Test-Path -LiteralPath $release.Output) -or -not (Test-Path -LiteralPath $release.Film) -or -not (Test-Path -LiteralPath $release.Nfo)
    }
    Assert-That (@(Get-StandInReports -HandoffId $id).Count -eq 0) 'nothing was reported to the manager while paused'
    Assert-That (@(Get-RemuxJobs | Where-Object { $_.status -in 'leased', 'completed', 'failed' }).Count -eq $startedBefore) 'no pass started while paused'

    Start-Sleep -Seconds 1
    Write-TrayPauseRequest -Paused $false
    Wait-Until -What 'Weir to resume on the tray''s request' -TimeoutSeconds 30 -Probe { -not (Get-WeirJson $session '/api/v1/pause').paused } | Out-Null
    $done = Wait-HandOff -Id $id -State completed
    Assert-CleanedFilm -OutputPath $release.Output | Out-Null
    Assert-That ($done.outputPath -eq $release.Output) 'after the resume the film was cleaned'
    $reports = @(Get-StandInReports -HandoffId $id)
    Assert-That ($reports.Count -eq 1) "it was cleaned and reported once (reports: $($reports.Count))"
    Assert-That ((@(Get-RemuxJobs).Count) -eq $jobsBefore + 1) 'exactly one pass ran for it'
    Assert-OriginalsKept -Release $release

    $paused = @(Get-ActivityItems -EventType 'system.processing_paused' | Where-Object { ($_ | ConvertTo-Json -Depth 5 -Compress) -match 'The tray' })
    $resumed = @(Get-ActivityItems -EventType 'system.processing_resumed' | Where-Object { ($_ | ConvertTo-Json -Depth 5 -Compress) -match 'The tray' })
    Assert-That ($paused.Count -ge 1 -and $resumed.Count -ge 1) 'Activity shows Paused and Resumed, by the tray'
    'Pause and resume requests in the tray''s file were taken and deleted; a hand-off made during the pause stayed queued with nothing cleaned, written, reported or removed for 20 s, then ran once after the resume; Activity shows both by the tray.'
}

# --- deleted-queued-file ------------------------------------------------------------------------------------------------

function Start-WatchedFolderLook {
    param([Parameter(Mandatory)] [int] $LibraryId)
    $queued = Invoke-Weir -Session $script:Ctx.Session -Method POST -Path '/api/v1/processing/jobs/watched-folder-remux-scan-dispatch/enqueue' -Body @{
        media_scope = 'movie'; library_id = $LibraryId; enqueue_remux_jobs = $false }
    if ($queued.Status -ne 200) { throw "Asking Weir to look at the watched folder was answered $($queued.Status): $($queued.Text)" }
}

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
