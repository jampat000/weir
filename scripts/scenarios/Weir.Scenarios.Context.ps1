<#
    What the scenarios share: the run's context, Weir's installed paths, the stand-in for Deluno, and the calls a
    hand-off, Activity and the file list need. Dot-sourced by Run-WeirScenarios.ps1 after Weir.Scenarios.Lib.ps1.
#>

# The context the runner fills in before the first scenario. Scenarios read it and add what later scenarios need.
$script:Ctx = @{}

function Get-Prop {
    param($Object, [Parameter(Mandatory)] [string] $Name)
    if ($null -ne $Object -and $Object.PSObject.Properties[$Name]) { $Object.$Name } else { $null }
}

function Get-FreePort {
    $probe = New-Object System.Net.Sockets.TcpListener([System.Net.IPAddress]::Loopback, 0)
    $probe.Start()
    $port = $probe.LocalEndpoint.Port
    $probe.Stop()
    $port
}

# --- the installed Weir -----------------------------------------------------------------------------------------------

function Get-WeirPaths {
    $install = $script:Ctx.InstallRoot
    [pscustomobject]@{
        TrayExe    = Join-Path $install 'current\Weir.exe'
        ServerExe  = Join-Path $install 'current\server\WeirServer.exe'
        TrayLog    = Join-Path $script:Ctx.RuntimeHome 'tray-host.log'
        ServerLog  = Join-Path $script:Ctx.RuntimeHome 'logs\weir.log'
        LogsFolder = Join-Path $script:Ctx.RuntimeHome 'logs'
    }
}

function Wait-WeirReady {
    param([int] $TimeoutSeconds = 120)
    Wait-Until -What "Weir to answer /ready at $($script:Ctx.Base)" -TimeoutSeconds $TimeoutSeconds -Probe {
        $response = Invoke-WebRequest -Uri "$($script:Ctx.Base)/ready" -UseBasicParsing -TimeoutSec 3
        if ((ConvertFrom-Json $response.Content).ready -eq $true) { $true } else { $false }
    } | Out-Null
}

# The version the running server reports (the OpenAPI document carries it), the way a client would read it.
function Get-ServerVersion {
    (Invoke-RestMethod -Uri "$($script:Ctx.Base)/openapi.json" -TimeoutSec 10).info.version
}

# True when $Reported is the build under test: the release's own version, and the commit when the build carries one.
function Test-VersionUnderTest {
    param([Parameter(Mandatory)] [string] $Reported, [Parameter(Mandatory)] [string] $Expected, [string] $ShortSha)
    $expectedBase = $Expected.Split('+')[0]
    $reportedBase = $Reported.Split('+')[0]
    if ($reportedBase -ne $expectedBase) { return $false }
    # A build made for the golden path carries its commit (`<version>+<short sha>`); one that does not cannot be tied to the commit.
    if ($ShortSha) { return $Reported.Contains('+') -and $Reported.Split('+')[1].StartsWith($ShortSha.Substring(0, [Math]::Min(7, $ShortSha.Length))) }
    $true
}

function Get-TrayLogText {
    $text = Get-FileTextOrNull -Path (Get-WeirPaths).TrayLog
    if ($null -eq $text) { '' } else { $text }
}

# The account the run uses. A throwaway password made for this run, never shown in the record.
function New-ScenarioAccount {
    $script:Ctx.Username = 'scenario-admin'
    $script:Ctx.Password = 'Sc-' + [guid]::NewGuid().ToString('N') + '-Aa1'
    Register-Secret $script:Ctx.Password
}

function Sign-InScenarioAccount {
    $session = New-WeirSession -BaseUrl $script:Ctx.Base
    Connect-Weir -Session $session -Username $script:Ctx.Username -Password $script:Ctx.Password
    $session
}

# --- the stand-in for Deluno ------------------------------------------------------------------------------------------

function Start-StandIn {
    $folder = $script:Ctx.RunFolder
    $script:Ctx.StandInLog = Join-Path $folder 'stand-in-manager-requests.jsonl'
    $script:Ctx.StandInStop = Join-Path $folder 'stand-in-manager.stop'
    Remove-Item -LiteralPath $script:Ctx.StandInStop -Force -ErrorAction SilentlyContinue
    $script:Ctx.StandInPort = Get-FreePort
    $arguments = @(
        '-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', (Join-Path $PSScriptRoot 'Start-StandInManager.ps1'),
        '-Port', $script:Ctx.StandInPort, '-RequestLog', $script:Ctx.StandInLog, '-StopFile', $script:Ctx.StandInStop)
    $script:Ctx.StandInProcess = Start-Process -FilePath 'powershell.exe' -ArgumentList $arguments -WindowStyle Hidden -PassThru
    Wait-Until -What 'the stand-in manager to answer' -TimeoutSeconds 30 -Probe {
        (Invoke-WebRequest -Uri "http://127.0.0.1:$($script:Ctx.StandInPort)/api/integrations/external/health" -UseBasicParsing -TimeoutSec 3).StatusCode -eq 200
    } | Out-Null
    Add-Evidence "stand-in manager listening on 127.0.0.1:$($script:Ctx.StandInPort), pid $($script:Ctx.StandInProcess.Id)"
}

# Stops only the process this run started.
function Stop-StandIn {
    if (-not $script:Ctx.ContainsKey('StandInProcess')) { return }
    New-Item -ItemType File -Force -Path $script:Ctx.StandInStop | Out-Null
    $process = $script:Ctx.StandInProcess
    if (-not $process.WaitForExit(10000)) { Stop-Process -Id $process.Id -Force -ErrorAction SilentlyContinue }
}

# Every request the stand-in has received, oldest first, as { at; method; path; query; headers; body (text); Json (parsed body or $null) }.
function Get-StandInRequests {
    param([string] $Method, [string] $PathPrefix)
    $text = Get-FileTextOrNull -Path $script:Ctx.StandInLog
    if (-not $text) { return @() }
    $rows = foreach ($line in ($text -split "`r?`n")) {
        if (-not $line.Trim()) { continue }
        $row = $line | ConvertFrom-Json
        $json = $null
        if ($row.body) { try { $json = $row.body | ConvertFrom-Json } catch { $json = $null } }
        $row | Add-Member -NotePropertyName Json -NotePropertyValue $json -PassThru
    }
    @($rows | Where-Object { (-not $Method -or $_.method -eq $Method) -and (-not $PathPrefix -or $_.path.StartsWith($PathPrefix)) })
}

# What Weir told the stand-in about one hand-off, oldest first (Deluno's /api/integrations/processors/events).
function Get-StandInReports {
    param([Parameter(Mandatory)] [string] $HandoffId)
    @(Get-StandInRequests -Method POST -PathPrefix '/api/integrations/processors/events' |
        Where-Object { $_.Json -and (Get-Prop $_.Json 'handoffId') -eq $HandoffId })
}

# Connects Weir to the stand-in as a Deluno connection and takes its webhook secret, once.
function Connect-StandInManager {
    if ($script:Ctx.ContainsKey('WebhookSecret')) { return }
    Start-StandIn
    $session = $script:Ctx.Session
    $connection = Get-WeirJson $session '/api/v1/media-managers/connections' -Method POST -Body @{
        kind = 'deluno'; base_url = "http://127.0.0.1:$($script:Ctx.StandInPort)"; api_key = 'scenario-stand-in-key'; enabled = $true }
    $script:Ctx.ConnectionId = $connection.id
    $script:Ctx.WebhookSecret = (Get-WeirJson $session "/api/v1/media-managers/connections/$($connection.id)/webhook-secret" -Method POST -Body @{}).webhook_secret
    Register-Secret $script:Ctx.WebhookSecret
    Add-Evidence "connected Weir to the stand-in as connection $($connection.id) ($($connection.name))"
}

# A movie workflow over folders of its own, linked to the stand-in connection when -LinkedToStandIn.
function New-ScenarioWorkflow {
    param(
        [Parameter(Mandatory)] [string] $Name,
        [Parameter(Mandatory)] [string] $Root,
        [switch] $LinkedToStandIn,
        [int] $MinimumMegabytes = 1,
        [int] $ReadyAfterSeconds = 0
    )
    $folders = @{}
    foreach ($kind in 'watched', 'work', 'output') {
        $folders[$kind] = (New-Item -ItemType Directory -Force -Path (Join-Path $Root $kind)).FullName
    }
    $body = @{
        name = $Name; media_type = 'movie'
        watched_folder = $folders.watched; work_folder = $folders.work; output_folder = $folders.output
        ready_after_seconds = $ReadyAfterSeconds; min_file_size_mb = $MinimumMegabytes; skip_access_tests = $true
        retry_backoff_seconds = 1; minimum_free_disk_space_mb = 0
    }
    if ($LinkedToStandIn) { $body['manager_connection_ids'] = @([int]$script:Ctx.ConnectionId) }
    $created = Get-WeirJson $script:Ctx.Session '/api/v1/processing/libraries' -Method POST -Body $body
    [pscustomobject]@{ Id = [int]$created.id; Name = $Name; Watched = $folders.watched; Work = $folders.work; Output = $folders.output }
}

# --- hand-offs ----------------------------------------------------------------------------------------------------------

$script:SecretHeader = { @{ 'X-Webhook-Secret' = $script:Ctx.WebhookSecret } }

# What Deluno sends when it hands Weir a download (the payload the contract suite and Deluno share).
function Send-HandOff {
    param([Parameter(Mandatory)] [string] $Id, [Parameter(Mandatory)] [string] $SourcePath, [string] $ReleaseName = 'Big.Buck.Bunny.2008.WEB-DL')
    $response = Invoke-Weir -Session $script:Ctx.Session -Method POST -Path '/api/v1/intake/webhook/deluno' -NotABrowser -Headers (& $script:SecretHeader) -Body @{
        eventType = 'deluno.processor-handoff'; handoffId = $Id; libraryId = 'scenario-movies'; mediaType = 'movies'
        sourcePath = $SourcePath; releaseName = $ReleaseName; callbackPath = '/api/integrations/processors/events' }
    if ($response.Status -ne 200) { throw "The hand-off $Id was answered $($response.Status): $($response.Text)" }
    $response.Json
}

function Get-HandOff {
    param([Parameter(Mandatory)] [string] $Id)
    $response = Invoke-Weir -Session $script:Ctx.Session -Method GET -Path "/api/v1/intake/handoffs/deluno/$Id" -NotABrowser -Headers (& $script:SecretHeader)
    if ($response.Status -ne 200) { throw "The status of hand-off $Id was answered $($response.Status): $($response.Text)" }
    $response.Json
}

function Wait-HandOff {
    param([Parameter(Mandatory)] [string] $Id, [Parameter(Mandatory)] [string] $State, [int] $TimeoutSeconds = 180)
    Wait-Until -What "hand-off $Id to reach '$State'" -TimeoutSeconds $TimeoutSeconds -IntervalMilliseconds 500 -Probe {
        $status = Get-HandOff -Id $Id
        if ($status.state -eq $State) { $status }
        elseif ($status.state -in 'failed', 'cancelled') { throw "Hand-off $Id ended $($status.state): $($status.message)" }
        else { $false }
    }
}

# --- Activity, files and jobs --------------------------------------------------------------------------------------------

function Get-ActivityItems {
    param([string] $EventType, [int] $Limit = 100)
    $path = "/api/v1/activity/recent?limit=$Limit"
    if ($EventType) { $path += "&event_type=$EventType" }
    @((Get-WeirJson $script:Ctx.Session $path).items)
}

function Get-ScenarioFiles {
    param([Parameter(Mandatory)] [int] $LibraryId)
    @((Get-WeirJson $script:Ctx.Session "/api/v1/processing/files?limit=1000&library_id=$LibraryId").files)
}

function Get-RemuxJobs {
    @((Get-WeirJson $script:Ctx.Session '/api/v1/processing/jobs/inspection?limit=100&status=pending&status=leased&status=completed&status=failed&status=handler_ok_finalize_failed&status=cancelled').jobs |
        Where-Object { $_.job_kind -like 'processing.file.remux_pass*' })
}

# --- the Weir log ----------------------------------------------------------------------------------------------------------

# System > Logs rows at warning or error, as the Logs page lists them.
function Get-LogPageProblems {
    # The Logs page lists at most 100 rows at a time. More than that is itself a failure, not something to page through and excuse.
    $page = Get-WeirJson $script:Ctx.Session '/api/v1/system/log?level=warning,error&limit=100'
    if ($page.next_cursor) { throw "Logs lists more than 100 warnings and errors ($($page.total) in all), which is not a clean run." }
    @($page.items)
}

# The warnings and errors on Logs that nothing above explains. $Known is { Pattern; Why } entries, matched against the row's title.
function Get-UnexpectedLogRows {
    param($Rows, $Known = @())
    @($Rows | Where-Object {
            $title = [string]$_.title
            -not (@($Known | Where-Object { $title -match $_.Pattern }).Count -gt 0)
        })
}
