<#
.SYNOPSIS
    Runs Weir's scenarios on the machine it is on: use the build under test the way people do, and say what happened.

.DESCRIPTION
    This is the part of the scenario suite that runs INSIDE the golden VM, in the signed-in desktop session, so that the tray has a
    desktop (Invoke-WeirScenarios.ps1 starts it there). Nothing here touches a machine other than the one it runs on.

    Phase WithDeluno runs first, with no restore, on the VM exactly as Deluno's suite leaves it: Deluno set up, Weir installed by
    Deluno's picker, and a file with a Deluno API key in the VM. It installs the build under test over that Weir, then follows what
    Deluno really did through Weir. Phase Fresh runs on the clean checkpoint, installs the build under test alone, and runs the
    scenarios that need no manager.
    Each scenario says what it does and what passes (Weir.Scenarios.Catalog.ps1), writes its own evidence file, and gets one
    verdict. The run writes results-<phase>.json, the evidence folder, the logs of Weir's server and tray (and Deluno's when it is
    there), Weir's request log, and scenario-<version>-<sha>.<phase>.md.

    -Plan lists the scenarios and what each one does and passes on, and exits. It touches nothing: no folder, no process, no file.

    -Rehearsal runs the Fresh scenarios that do not need an installer against a Weir already running somewhere (-ServerUrl, whose
    data folder is -RuntimeHome, and FFmpeg's folder is -ToolsFolder), playing the tray where the real one is missing. It is how a
    change to a scenario is tried before a VM round. A rehearsal's record says so and can never set the golden-path status.

.PARAMETER Phase
    WithDeluno or Fresh.

.PARAMETER SetupPath
    The Weir-win-Setup.exe of the build under test (the manual CI run's artifact weir-windows-<short sha>).

.PARAMETER Version
    The version under test, such as 1.0.0-rc.14. The installed build must report it.

.PARAMETER ShortSha
    The commit of the build under test (at least 7 characters). The build must report it.

.PARAMETER RunFolder
    Where the record, the evidence and the logs are kept.

.PARAMETER DelunoUrl
    The Deluno in this machine (default http://127.0.0.1:7879). Its API key is never given to the run; see DelunoKeyFileInVm.

.PARAMETER DelunoKeyFileInVm
    A file inside this machine holding one line, a `read,imports` Deluno API key and nothing else, left by the Deluno session. It is
    read here and deleted. Without it, a Deluno with no account gets a throwaway one and a minted key; otherwise the Deluno
    scenarios are recorded not-applicable with the reason.

.PARAMETER WeirLoginFileInVm
    A file inside this machine holding two lines, a Weir user name then its password, for when Weir already has an account (Deluno's
    Connect Weir makes one) and the run was not the one that made it. Read here and deleted. Weir with no account gets the run's own.

.PARAMETER SourceFilm
    A real film to use instead of making one, such as Big Buck Bunny (Creative Commons). Its tracks are not known, so the
    scenarios judge the cleaned copy on what must hold for any film.

.EXAMPLE
    powershell -File Run-WeirScenarios.ps1 -Plan
#>
[CmdletBinding()]
param(
    [ValidateSet('WithDeluno', 'Fresh')] [string] $Phase = 'Fresh',
    [string] $SetupPath,
    [string] $Version,
    [string] $ShortSha,
    [string] $RunFolder = 'C:\golden\weir\run',
    [string] $MediaRoot = 'C:\golden\weir\media',
    [int] $Port = 9347,
    [string] $InstallRoot = (Join-Path $env:LOCALAPPDATA 'Weir'),
    [string] $RuntimeHome = (Join-Path $env:ProgramData 'Weir'),
    [string] $DelunoUrl = 'http://127.0.0.1:7879',
    [string] $DelunoKeyFileInVm = 'C:\golden\deluno-weir-scenario-key.txt',
    [string] $WeirLoginFileInVm = 'C:\golden\weir-scenario-login.txt',
    [string] $SourceFilm,
    [string[]] $Only,
    [switch] $Plan,
    [switch] $Json,
    [switch] $Rehearsal,
    [string] $ServerUrl,
    [string] $ToolsFolder
)

$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'Weir.Scenarios.Lib.ps1')
. (Join-Path $PSScriptRoot 'Weir.Scenarios.Catalog.ps1')
. (Join-Path $PSScriptRoot 'Weir.Scenarios.Context.ps1')
. (Join-Path $PSScriptRoot 'Weir.Scenarios.Install.ps1')
. (Join-Path $PSScriptRoot 'Weir.Scenarios.WeirOnly.ps1')
. (Join-Path $PSScriptRoot 'Weir.Scenarios.Update.ps1')
. (Join-Path $PSScriptRoot 'Weir.Scenarios.Deluno.ps1')
. (Join-Path $PSScriptRoot 'Weir.Scenarios.Tray.ps1')

# The warnings and errors a run is allowed to cause on purpose, each with why. An unexplained one fails logs-clean. Empty
# unless a scenario provokes one deliberately; the scenarios here provoke none.
$KnownLogRows = @()

function Get-ScenarioFunction {
    param([Parameter(Mandatory)] [string] $Id)
    Get-Command -Name ('Scenario_' + $Id.Replace('-', '_')) -CommandType Function -ErrorAction SilentlyContinue
}

$phaseScenarios = @($ScenarioCatalog | Where-Object { $_.Phase -eq $Phase })
if ($Only) { $phaseScenarios = @($phaseScenarios | Where-Object { $Only -contains $_.Id }) }

if ($Plan) {
    $planned = @($ScenarioCatalog | ForEach-Object {
            [pscustomobject]@{
                Id = $_.Id; Phase = $_.Phase; Required = $_.Required; Title = $_.Title; Does = $_.Does; Passes = $_.Passes
                Implemented = [bool](Get-ScenarioFunction -Id $_.Id)
            }
        })
    if ($Json) { ConvertTo-Json -InputObject $planned -Depth 4 }
    else {
        $number = 0
        foreach ($entry in $planned) {
            $number++
            "{0,2}. [{1}] {2}{3}" -f $number, $entry.Phase, $entry.Title, $(if ($entry.Required) { '' } else { ' (optional)' })
            "      does:   $($entry.Does)"
            "      passes: $($entry.Passes)"
        }
    }
    return
}

if (-not $Rehearsal) {
    if (-not $SetupPath -or -not $Version -or -not $ShortSha) { throw '-SetupPath, -Version and -ShortSha are required (or -Plan, or -Rehearsal).' }
}
else {
    if (-not $ServerUrl) { throw '-Rehearsal needs -ServerUrl, the Weir to rehearse against.' }
    if (-not $Version) { $Version = 'rehearsal' }
    if (-not $ShortSha) { $ShortSha = '0000000' }
}

Initialize-ScenarioRun -RunFolder $RunFolder
$script:Ctx.Port = $Port
$script:Ctx.Base = if ($Rehearsal) { $ServerUrl.TrimEnd('/') } else { "http://127.0.0.1:$Port" }
$script:Ctx.Version = $Version
$script:Ctx.ShortSha = $ShortSha
$script:Ctx.SetupPath = $SetupPath
$script:Ctx.InstallRoot = $InstallRoot
$script:Ctx.RuntimeHome = $RuntimeHome
$script:Ctx.MediaRoot = $MediaRoot
$script:Ctx.RunFolder = $RunFolder
$script:Ctx.DelunoKeyFile = $DelunoKeyFileInVm
$script:Ctx.WeirLoginFile = $WeirLoginFileInVm
$script:Ctx.PhaseStartUtc = (Get-Date).ToUniversalTime()
$script:Ctx.DelunoUrl = if ($Rehearsal -and -not $PSBoundParameters.ContainsKey('DelunoUrl')) { '' } else { $DelunoUrl }
$script:Ctx.SourceFilm = $SourceFilm
$script:Ctx.ToolsFolder = $ToolsFolder
$script:Ctx.Rehearsal = [bool]$Rehearsal
$script:Ctx.KnownLogRows = $KnownLogRows
$script:Ctx.Session = $null

New-Item -ItemType Directory -Force -Path $MediaRoot | Out-Null
$runClock = [Diagnostics.Stopwatch]::StartNew()
# A rehearsal runs no installer and has no Deluno, so these cannot be rehearsed; the scenarios that need only a running Weir can.
$skippedInRehearsal = 'fresh-install', 'update-over-installed-weir', 'workflows-from-deluno', 'deluno-film-handoff', 'deluno-release-with-extra', 'deluno-resend', 'deluno-outcomes', 'logs-clean-deluno'

foreach ($entry in $phaseScenarios) {
    $script:EvidenceFile = Join-Path $RunFolder "evidence\$($entry.Id).log"
    Set-Content -LiteralPath $script:EvidenceFile -Value @("Scenario: $($entry.Title) ($($entry.Id))", "Does:   $($entry.Does)", "Passes: $($entry.Passes)", '') -Encoding UTF8
    $clock = [Diagnostics.Stopwatch]::StartNew()
    Write-Host ("[{0}] {1}" -f $entry.Id, $entry.Title) -ForegroundColor Cyan
    try {
        if ($Rehearsal -and $entry.Id -in $skippedInRehearsal) {
            Add-ScenarioResult -Id $entry.Id -Status 'not-applicable' -Detail 'Rehearsal: no installer is run, so this scenario was not.' -Seconds 0
            continue
        }
        $function = Get-ScenarioFunction -Id $entry.Id
        if (-not $function) { throw "The scenario has no function Scenario_$($entry.Id.Replace('-', '_'))." }
        # The Fresh phase's scenarios after the account is made share its session. The Deluno phase's scenarios get theirs themselves
        # (Initialize-WeirSession), because the Weir they find may already have an account.
        if ($Phase -eq 'Fresh' -and $entry.Id -ne 'fresh-install' -and $entry.Id -ne 'first-visit-account' -and $null -eq $script:Ctx.Session) {
            if (-not $script:Ctx.ContainsKey('Username')) { throw 'Blocked: the account was never created, so there is nothing to sign in to.' }
            $script:Ctx.Session = Sign-InScenarioAccount
        }
        $detail = & $function
        if ($detail -is [array]) { $detail = $detail[-1] }
        if ($null -ne $detail -and $detail.PSObject.Properties['NotApplicable']) {
            Add-ScenarioResult -Id $entry.Id -Status 'not-applicable' -Detail $detail.Detail -Seconds $clock.Elapsed.TotalSeconds
            Write-Host (Protect-Secrets "    not applicable: $($detail.Detail)") -ForegroundColor Yellow
            continue
        }
        Add-ScenarioResult -Id $entry.Id -Status 'passed' -Detail ([string]$detail) -Seconds $clock.Elapsed.TotalSeconds
        Write-Host (Protect-Secrets "    passed in $([int]$clock.Elapsed.TotalSeconds) s: $detail") -ForegroundColor Green
        Add-Evidence "PASSED: $detail"
    }
    catch {
        $message = $_.Exception.Message
        Add-ScenarioResult -Id $entry.Id -Status 'failed' -Detail $message -Seconds $clock.Elapsed.TotalSeconds
        Write-Host (Protect-Secrets "    FAILED: $message") -ForegroundColor Red
        Add-Evidence "FAILED: $message"
        Add-Evidence $_.ScriptStackTrace
    }
}

# --- what to keep ---------------------------------------------------------------------------------------------------


if ($Rehearsal) { Add-Content -LiteralPath (Join-Path $RuntimeHome 'tray-host.log') -Value "[$(Get-Date -Format 'yyyy-MM-dd HH:mm:ss')] Rehearsal: the tray is played by the suite." }
$kept = New-Object System.Collections.Generic.List[string]
try {
$logsOut = Join-Path $RunFolder "logs-$($Phase.ToLowerInvariant())"
New-Item -ItemType Directory -Force -Path $logsOut | Out-Null
$sources = @(
    @{ From = Join-Path $RuntimeHome 'logs\*'; To = 'weir-server-logs' },
    @{ From = Join-Path $RuntimeHome 'tray-host.log'; To = '.' },
    @{ From = Join-Path $RuntimeHome 'install-hook.log'; To = '.' },
    @{ From = Join-Path $RuntimeHome 'update-state.json'; To = '.' },
    @{ From = Join-Path $RuntimeHome 'update-backup-result.json'; To = '.' },
    # Deluno's logs, when it is on this machine (the golden VM's Deluno installer puts them under its data folder).
    @{ From = Join-Path $env:LOCALAPPDATA 'DelunoData\logs\*'; To = 'deluno-logs' },
    @{ From = Join-Path $env:LOCALAPPDATA 'Deluno\logs\*'; To = 'deluno-logs' }
)
foreach ($source in $sources) {
    $target = Join-Path $logsOut $source.To
    New-Item -ItemType Directory -Force -Path $target | Out-Null
    try { Copy-Item -Path $source.From -Destination $target -Recurse -Force -ErrorAction Stop; $kept.Add("logs-$($Phase.ToLowerInvariant())/$($source.To)/$(Split-Path -Leaf $source.From)") }
    catch [System.Management.Automation.ItemNotFoundException] { }
    catch { Write-Host "Could not keep $($source.From): $($_.Exception.Message)" -ForegroundColor Yellow }
}
if (Test-Path -LiteralPath (Join-Path $RunFolder 'api-requests.jsonl')) { $kept.Add('api-requests.jsonl') }
$kept.Add('evidence/<scenario>.log (one per scenario)')

$facts = @{
    Version = $Version; ShortSha = $ShortSha; Phase = $Phase
    Date = (Get-Date -Format 'yyyy-MM-dd HH:mm:ss')
    Machine = "$((Get-CimInstance Win32_OperatingSystem).Caption) $((Get-CimInstance Win32_OperatingSystem).Version), Windows PowerShell $($PSVersionTable.PSVersion)"
    'Run time' = "$([int]$runClock.Elapsed.TotalSeconds) s"
    Rehearsal = $(if ($Rehearsal) { 'YES: no installer was run and the tray was played; this record can never set the golden-path status' } else { 'no' })
}
if ($SetupPath -and (Test-Path -LiteralPath $SetupPath)) { $facts['Installer sha256'] = (Get-FileHash -LiteralPath $SetupPath -Algorithm SHA256).Hash }
if ($script:Ctx.ContainsKey('FilmMaster')) { $facts['Film'] = "$([IO.Path]::GetFileName($script:Ctx.FilmMaster.Path)), $($script:Ctx.FilmMaster.Summary.Bytes) bytes, sha256 $($script:Ctx.FilmMaster.Sha256)$(if ($script:Ctx.FilmMaster.Generated) { ' (made with Weir''s FFmpeg)' } else { ' (given to the run)' })" }

$allResults = $script:Results.ToArray()
ConvertTo-Json -InputObject @{ Facts = $facts; Results = $allResults } -Depth 6 | Set-Content -LiteralPath (Join-Path $RunFolder "results-$($Phase.ToLowerInvariant()).json") -Encoding UTF8
Write-ScenarioRecord -Path (Join-Path $RunFolder "scenario-$Version-$ShortSha.$($Phase.ToLowerInvariant()).md") -Catalog $phaseScenarios -Results $allResults -Facts $facts -Files $kept.ToArray()
}
catch {
    # A record that cannot be written must say where, not just that: this runs at the end of a long run.
    Write-Host "The run could not write its record: $($_.Exception.Message)`n$($_.ScriptStackTrace)" -ForegroundColor Red
    exit 2
}

$failed = @($allResults | Where-Object { $_.Status -eq 'failed' })
$missing = @($phaseScenarios | Where-Object { $_.Required -and $_.Id -notin ($allResults | ForEach-Object { $_.Id }) })
Write-Host ("{0} passed, {1} failed, {2} not applicable, in {3} s." -f @($allResults | Where-Object Status -eq 'passed').Count, $failed.Count, @($allResults | Where-Object Status -eq 'not-applicable').Count, [int]$runClock.Elapsed.TotalSeconds)
if ($failed.Count -gt 0 -or $missing.Count -gt 0) { exit 1 }
