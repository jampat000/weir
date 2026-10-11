<#
    The scenario suite's shared helpers (dot-sourced by Run-WeirScenarios.ps1 and Invoke-WeirScenarios.ps1).

    Windows PowerShell 5.1 compatible: it runs inside the golden VM, where nothing else is installed.
    Everything here talks to Weir the way its users and its tray do: HTTP on its port, and the small files the tray and
    the server leave each other in Weir's data folder. Nothing reads Weir's database, and nothing is faked
    except what has to be played from outside (Deluno's side of a hand-off, in Start-StandInManager.ps1).
#>

Set-StrictMode -Version 1.0

# --- the run's state ------------------------------------------------------------------------------------------------

$script:Results = New-Object System.Collections.Generic.List[object]
$script:EvidenceFile = $null
$script:ApiLog = $null

function Initialize-ScenarioRun {
    param([Parameter(Mandatory)] [string] $RunFolder)
    New-Item -ItemType Directory -Force -Path $RunFolder, (Join-Path $RunFolder 'evidence') | Out-Null
    $script:ApiLog = Join-Path $RunFolder 'api-requests.jsonl'
}

# Secrets this run holds in memory (the account password, a webhook secret, a Deluno API key and token). They are read inside the
# machine, used there, and never written anywhere: every line that reaches an evidence file, the request log, the console or the
# record goes through Protect-Secrets, which masks any of them (and anything shaped like a Deluno API key) as ****.
$script:Secrets = New-Object System.Collections.Generic.List[string]

function Register-Secret {
    param([string] $Value)
    if ($Value -and $Value.Length -ge 6 -and -not $script:Secrets.Contains($Value)) { $script:Secrets.Add($Value) }
}

function Protect-Secrets {
    param([string] $Text)
    if (-not $Text) { return $Text }
    foreach ($secret in $script:Secrets.ToArray()) { $Text = $Text.Replace($secret, '****') }
    # A secret in a response is masked by its field name too, because the response that first hands it over arrives before it can be registered.
    $Text = $Text -replace '(?i)("(?:webhook_secret|api_key|apiKey|accessToken|access_token|password|csrf_token)"\s*:\s*")[^"]+', '$1****'
    $Text -replace '(?i)deluno_[A-Za-z0-9_-]{8,}', '****'
}

function Add-Evidence {
    param([Parameter(Mandatory)] [string] $Line)
    if ($script:EvidenceFile) {
        Add-Content -LiteralPath $script:EvidenceFile -Value ("{0}  {1}" -f (Get-Date -Format 'HH:mm:ss.fff'), (Protect-Secrets $Line)) -Encoding UTF8
    }
}

# --- assertions and waiting -----------------------------------------------------------------------------------------

function Assert-That {
    param([Parameter(Mandatory)] $Condition, [Parameter(Mandatory)] [string] $Message)
    if (-not $Condition) { throw $Message }
    Add-Evidence "ok: $Message"
}

# Polls $Probe until it returns something other than $null or $false, and returns that. Fails with what was last seen.
function Wait-Until {
    param(
        [Parameter(Mandatory)] [scriptblock] $Probe,
        [Parameter(Mandatory)] [string] $What,
        [int] $TimeoutSeconds = 60,
        [int] $IntervalMilliseconds = 250
    )
    $deadline = (Get-Date).AddSeconds($TimeoutSeconds)
    $lastError = $null
    do {
        try {
            $value = & $Probe
            if ($null -ne $value -and $value -ne $false) { return $value }
        }
        catch { $lastError = $_.Exception.Message }
        Start-Sleep -Milliseconds $IntervalMilliseconds
    } while ((Get-Date) -lt $deadline)
    $because = if ($lastError) { " Last error: $lastError" } else { '' }
    throw "Timed out after $TimeoutSeconds s waiting for $What.$because"
}

# Watches for $Duration and fails the moment $Happened returns true. Watching can only show that something did not happen
# yet, so use it for claims no API can answer directly ("nothing was cleaned while paused").
function Assert-NeverWithin {
    param([Parameter(Mandatory)] [scriptblock] $Happened, [Parameter(Mandatory)] [int] $Seconds, [Parameter(Mandatory)] [string] $What)
    $deadline = (Get-Date).AddSeconds($Seconds)
    do {
        if (& $Happened) { throw "$What happened, and it must not." }
        Start-Sleep -Milliseconds 250
    } while ((Get-Date) -lt $deadline)
    Add-Evidence "ok: $What did not happen in $Seconds s"
}

# --- Weir's HTTP API ------------------------------------------------------------------------------------------------

function New-WeirSession {
    param([Parameter(Mandatory)] [string] $BaseUrl)
    [pscustomobject]@{
        BaseUrl = $BaseUrl.TrimEnd('/')
        Web     = New-Object Microsoft.PowerShell.Commands.WebRequestSession
    }
}

# One request. Never throws on an HTTP error: the caller gets { Status; Json; Text } and judges it, because "the server
# refused with 409" is often exactly what a scenario is checking. A request that cannot be made at all gives Status 0.
function Invoke-Weir {
    param(
        [Parameter(Mandatory)] $Session,
        [Parameter(Mandatory)] [string] $Method,
        [Parameter(Mandatory)] [string] $Path,
        $Body,
        [hashtable] $Headers = @{},
        # A hand-off or a hand-off report is not a browser: no session, no CSRF token, no Origin.
        [switch] $NotABrowser,
        # DELETE routes that carry the token in a header rather than a body.
        [switch] $CsrfInHeader,
        [int] $TimeoutSeconds = 45
    )
    $url = if ($Path -match '^https?://') { $Path } else { "$($Session.BaseUrl)$Path" }
    $requestHeaders = @{ 'X-Requested-With' = 'XMLHttpRequest'; Accept = '*/*' }
    $payload = $null
    if (-not $NotABrowser) {
        $requestHeaders['Origin'] = $Session.BaseUrl
        if ($Method -ne 'GET') {
            $csrf = (Invoke-RestMethod -Uri "$($Session.BaseUrl)/api/v1/auth/csrf" -WebSession $Session.Web -Headers @{ 'X-Requested-With' = 'XMLHttpRequest' } -TimeoutSec 15).csrf_token
            if ($CsrfInHeader) { $requestHeaders['X-CSRF-Token'] = $csrf }
            else {
                $withToken = @{}
                if ($null -ne $Body) { foreach ($name in $Body.Keys) { $withToken[$name] = $Body[$name] } }
                $withToken['csrf_token'] = $csrf
                $Body = $withToken
            }
        }
    }
    foreach ($key in $Headers.Keys) { $requestHeaders[$key] = $Headers[$key] }
    if ($null -ne $Body) {
        $payload = $Body | ConvertTo-Json -Depth 12 -Compress
        $requestHeaders['Content-Type'] = 'application/json'
    }

    $started = Get-Date
    $status = 0
    $text = ''
    try {
        $parameters = @{ Uri = $url; Method = $Method; Headers = $requestHeaders; UseBasicParsing = $true; TimeoutSec = $TimeoutSeconds }
        if (-not $NotABrowser) { $parameters['WebSession'] = $Session.Web }
        if ($null -ne $payload) { $parameters['Body'] = [Text.Encoding]::UTF8.GetBytes($payload) }
        $response = Invoke-WebRequest @parameters
        $status = [int]$response.StatusCode
        $text = [string]$response.Content
    }
    catch {
        $failed = $_.Exception.Response
        if ($failed) {
            $status = [int]$failed.StatusCode
            $text = if ($_.ErrorDetails -and $_.ErrorDetails.Message) { [string]$_.ErrorDetails.Message } else { '' }
        }
        else { $text = $_.Exception.Message }
    }
    $json = $null
    $trimmed = $text.TrimStart()
    if ($trimmed.StartsWith('{') -or $trimmed.StartsWith('[')) {
        try { $json = $text | ConvertFrom-Json } catch { $json = $null }
    }
    $elapsed = [int]((Get-Date) - $started).TotalMilliseconds
    $shown = Protect-Secrets $(if ($text.Length -gt 600) { $text.Substring(0, 600) + '...' } else { $text })
    Add-Evidence ("{0} {1} -> {2} ({3} ms) {4}" -f $Method, $Path, $status, $elapsed, $(if ($Method -ne 'GET' -or $status -ge 400 -or $status -eq 0) { $shown } else { '' }))
    if ($script:ApiLog) {
        $line = [ordered]@{ at = (Get-Date).ToUniversalTime().ToString('o'); method = $Method; path = $Path; status = $status; ms = $elapsed; body = $shown }
        Add-Content -LiteralPath $script:ApiLog -Value ($line | ConvertTo-Json -Compress) -Encoding UTF8
    }
    [pscustomobject]@{ Status = $status; Json = $json; Text = $text }
}

# A request that must come back 2xx; returns the parsed body.
function Get-WeirJson {
    param([Parameter(Mandatory)] $Session, [Parameter(Mandatory)] [string] $Path, [string] $Method = 'GET', $Body, [hashtable] $Headers = @{}, [switch] $NotABrowser)
    $response = Invoke-Weir -Session $Session -Method $Method -Path $Path -Body $Body -Headers $Headers -NotABrowser:$NotABrowser
    if ($response.Status -lt 200 -or $response.Status -ge 300) {
        throw "$Method $Path answered $($response.Status): $($response.Text)"
    }
    $response.Json
}

function Connect-Weir {
    param([Parameter(Mandatory)] $Session, [Parameter(Mandatory)] [string] $Username, [Parameter(Mandatory)] [string] $Password)
    $login = Invoke-Weir -Session $Session -Method POST -Path '/api/v1/auth/login' -Body @{ username = $Username; password = $Password; trusted_device = $false }
    if ($login.Status -ne 200) { throw "Sign-in as $Username answered $($login.Status): $($login.Text)" }
}

# --- Weir's folders and files ---------------------------------------------------------------------------------------

# Writes a file the way the tray does: whole, then renamed into place, so the server never reads half of it.
function Write-FileAtomically {
    param([Parameter(Mandatory)] [string] $Folder, [Parameter(Mandatory)] [string] $Name, [Parameter(Mandatory)] [string] $Text)
    $scratch = Join-Path $Folder (".{0}.{1}.tmp" -f $Name, [guid]::NewGuid().ToString('N'))
    [IO.File]::WriteAllText($scratch, $Text, (New-Object Text.UTF8Encoding($false)))
    Move-Item -LiteralPath $scratch -Destination (Join-Path $Folder $Name) -Force
}

function Get-FileTextOrNull {
    param([Parameter(Mandatory)] [string] $Path)
    try {
        $stream = New-Object IO.FileStream($Path, [IO.FileMode]::Open, [IO.FileAccess]::Read, ([IO.FileShare]::ReadWrite -bor [IO.FileShare]::Delete))
        try { (New-Object IO.StreamReader($stream)).ReadToEnd() } finally { $stream.Dispose() }
    }
    catch { $null }
}

# --- media ----------------------------------------------------------------------------------------------------------

function Find-WeirTool {
    param([Parameter(Mandatory)] [string] $InstallRoot, [Parameter(Mandatory)] [string] $Name)
    $found = Get-ChildItem -LiteralPath $InstallRoot -Recurse -Filter "$Name.exe" -ErrorAction SilentlyContinue | Select-Object -First 1
    if (-not $found) { throw "$Name.exe is not under ${InstallRoot}: the installed Weir does not carry the tool its own passes use." }
    $found.FullName
}

function Invoke-Tool {
    param([Parameter(Mandatory)] [string] $Program, [Parameter(Mandatory)] [string[]] $Arguments)
    $info = New-Object Diagnostics.ProcessStartInfo
    $info.FileName = $Program
    $info.UseShellExecute = $false
    $info.CreateNoWindow = $true
    $info.RedirectStandardOutput = $true
    $info.RedirectStandardError = $true
    # Windows PowerShell 5.1 has no ArgumentList: quote each argument by hand.
    $info.Arguments = ($Arguments | ForEach-Object { if ($_ -match '[\s"]') { '"' + ($_ -replace '"', '\"') + '"' } else { $_ } }) -join ' '
    $process = [Diagnostics.Process]::Start($info)
    $output = $process.StandardOutput.ReadToEndAsync()
    $errors = $process.StandardError.ReadToEndAsync()
    $process.WaitForExit()
    if ($process.ExitCode -ne 0) { throw "$([IO.Path]::GetFileName($Program)) exited $($process.ExitCode): $($errors.Result)" }
    $output.Result
}

# A small film for the scenarios. Real moving frames and tones, an audio track per language, and a subtitle track per
# language, so a clean has real work: Weir keeps what the workflow's rules keep and drops the rest.
# Uses only encoders every FFmpeg build has (mpeg4, aac, srt), so it works with the build Weir ships.
function New-ScenarioFilm {
    param(
        [Parameter(Mandatory)] [string] $Ffmpeg,
        [Parameter(Mandatory)] [string] $Path,
        [int] $Seconds = 30,
        [string[]] $AudioLanguages = @('eng', 'fre', 'jpn'),
        [string[]] $SubtitleLanguages = @('eng', 'spa'),
        [string] $Title = 'Big Buck Bunny'
    )
    New-Item -ItemType Directory -Force -Path (Split-Path -Parent $Path) | Out-Null
    $arguments = @('-nostdin', '-hide_banner', '-loglevel', 'error', '-y', '-f', 'lavfi', '-i', "testsrc2=duration=${Seconds}:size=640x360:rate=24")
    for ($i = 0; $i -lt $AudioLanguages.Count; $i++) { $arguments += @('-f', 'lavfi', '-i', "sine=frequency=$(220 * ($i + 1)):duration=$Seconds") }
    $subtitleFiles = @()
    foreach ($language in $SubtitleLanguages) {
        $srt = [IO.Path]::ChangeExtension($Path, ".$language.srt")
        [IO.File]::WriteAllText($srt, "1`n00:00:01,000 --> 00:00:04,000`n$Title ($language)`n`n2`n00:00:05,000 --> 00:00:08,000`nSubtitle two`n", (New-Object Text.UTF8Encoding($false)))
        $arguments += @('-i', $srt)
        $subtitleFiles += $srt
    }
    $arguments += @('-map', '0:v')
    for ($i = 0; $i -lt $AudioLanguages.Count; $i++) { $arguments += @('-map', "$($i + 1):a") }
    for ($i = 0; $i -lt $SubtitleLanguages.Count; $i++) { $arguments += @('-map', "$($AudioLanguages.Count + 1 + $i):s") }
    $arguments += @('-c:v', 'mpeg4', '-b:v', '3M', '-c:a', 'aac', '-c:s', 'srt', '-metadata', "title=$Title")
    for ($i = 0; $i -lt $AudioLanguages.Count; $i++) { $arguments += @("-metadata:s:a:$i", "language=$($AudioLanguages[$i])") }
    for ($i = 0; $i -lt $SubtitleLanguages.Count; $i++) { $arguments += @("-metadata:s:s:$i", "language=$($SubtitleLanguages[$i])") }
    $arguments += $Path
    Invoke-Tool -Program $Ffmpeg -Arguments $arguments | Out-Null
    foreach ($srt in $subtitleFiles) { Remove-Item -LiteralPath $srt -Force -ErrorAction SilentlyContinue }
    if (-not (Test-Path -LiteralPath $Path -PathType Leaf)) { throw "FFmpeg made no film at $Path." }
    Get-Item -LiteralPath $Path
}

# What is inside a media file, from FFprobe: { Video = n; Audio = @(lang...); Subtitles = @(lang...); Seconds; Bytes }.
function Get-MediaSummary {
    param([Parameter(Mandatory)] [string] $Ffprobe, [Parameter(Mandatory)] [string] $Path)
    $probe = Invoke-Tool -Program $Ffprobe -Arguments @('-v', 'error', '-print_format', 'json', '-show_streams', '-show_format', $Path) | ConvertFrom-Json
    $language = { param($stream) if ($stream.PSObject.Properties['tags'] -and $stream.tags.PSObject.Properties['language']) { [string]$stream.tags.language } else { '' } }
    [pscustomobject]@{
        Video     = @($probe.streams | Where-Object codec_type -eq 'video').Count
        Audio     = @($probe.streams | Where-Object codec_type -eq 'audio' | ForEach-Object { & $language $_ })
        Subtitles = @($probe.streams | Where-Object codec_type -eq 'subtitle' | ForEach-Object { & $language $_ })
        Seconds   = [double]$probe.format.duration
        Bytes     = (Get-Item -LiteralPath $Path).Length
    }
}

# --- logs -----------------------------------------------------------------------------------------------------------

# Lines in a log file at or above Warning, as { File; Line }. Weir's server log is one JSON-ish or plain line per entry;
# the match is on the level word either way, so a new log shape cannot hide a warning.
function Find-LogProblems {
    param([Parameter(Mandatory)] [string[]] $Files, [string[]] $ExpectedPatterns = @(), [int] $FromLine = 0)
    foreach ($file in $Files) {
        if (-not (Test-Path -LiteralPath $file)) { continue }
        $lines = @(Get-Content -LiteralPath $file -ErrorAction SilentlyContinue)
        for ($i = $FromLine; $i -lt $lines.Count; $i++) {
            $line = [string]$lines[$i]
            if ($line -notmatch '(?i)\b(warn|warning|error|fail|failed|fatal|exception|crit|critical)\b') { continue }
            $expected = $false
            foreach ($pattern in $ExpectedPatterns) { if ($line -match $pattern) { $expected = $true; break } }
            if (-not $expected) { [pscustomobject]@{ File = $file; Line = $line } }
        }
    }
}

# --- the record -----------------------------------------------------------------------------------------------------

function Add-ScenarioResult {
    param(
        [Parameter(Mandatory)] [string] $Id,
        [Parameter(Mandatory)] [ValidateSet('passed', 'failed', 'not-applicable')] [string] $Status,
        [Parameter(Mandatory)] [string] $Detail,
        [double] $Seconds = 0
    )
    $script:Results.Add([pscustomobject]@{ Id = $Id; Status = $Status; Detail = (Protect-Secrets $Detail); Seconds = [math]::Round($Seconds, 1) })
}

# The record: scenario-<version>-<sha>.md, one line per scenario, then what each scenario did, what had to be true, and
# what was seen. $Catalog is the list from Weir.Scenarios.Catalog.ps1; $Results hold the verdicts.
function Write-ScenarioRecord {
    param(
        [Parameter(Mandatory)] [string] $Path,
        [Parameter(Mandatory)] $Catalog,
        [Parameter(Mandatory)] $Results,
        [Parameter(Mandatory)] [hashtable] $Facts,
        [string[]] $Files = @()
    )
    $lines = New-Object System.Collections.Generic.List[string]
    $lines.Add("# Weir scenario suite $($Facts.Version), commit $($Facts.ShortSha)")
    $lines.Add('')
    foreach ($key in $Facts.Keys | Sort-Object) {
        if ($key -in 'Version', 'ShortSha') { continue }
        $lines.Add("- **${key}:** $($Facts[$key])")
    }
    $lines.Add('')
    $lines.Add('| # | Scenario | Result | Seconds | Saw |')
    $lines.Add('| --- | --- | --- | --- | --- |')
    $number = 0
    foreach ($entry in $Catalog) {
        $number++
        $result = @($Results | Where-Object { $_.Id -eq $entry.Id }) | Select-Object -First 1
        $status = if ($result) { $result.Status } else { 'failed' }
        $detail = if ($result) { $result.Detail } else { 'The scenario did not run.' }
        $seconds = if ($result) { $result.Seconds } else { 0 }
        $lines.Add(("| {0} | {1} | {2} | {3} | {4} |" -f $number, $entry.Title, $status, $seconds, (($detail -replace '\|', '/') -replace '\r?\n', ' ')))
    }
    $failed = @($Catalog | Where-Object { $e = $_; $r = @($Results | Where-Object { $_.Id -eq $e.Id }) | Select-Object -First 1; -not $r -or $r.Status -eq 'failed' })
    $passed = @($Results | Where-Object Status -eq 'passed').Count
    $lines.Add('')
    $lines.Add("**$passed of $(@($Catalog).Count) scenarios passed; $(@($failed).Count) failed.**")
    $lines.Add('')
    $lines.Add('## What each scenario does and what passes')
    foreach ($entry in $Catalog) {
        $lines.Add('')
        $lines.Add("### $($entry.Title) (``$($entry.Id)``)")
        $lines.Add('')
        $lines.Add("- **Does:** $($entry.Does)")
        $lines.Add("- **Passes when:** $($entry.Passes)")
        $lines.Add("- **Evidence:** ``evidence/$($entry.Id).log``")
    }
    if ($Files.Count -gt 0) {
        $lines.Add('')
        $lines.Add('## Kept with this record')
        $lines.Add('')
        foreach ($file in $Files) { $lines.Add("- ``$file``") }
    }
    [IO.File]::WriteAllLines($Path, $lines.ToArray(), (New-Object Text.UTF8Encoding($false)))
}
