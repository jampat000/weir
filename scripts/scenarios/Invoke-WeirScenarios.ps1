<#
.SYNOPSIS
    Proves a Weir build on the clean Hyper-V golden VM before it is tagged, and records the result as the `golden-path` commit status.

.DESCRIPTION
    The same shape as Deluno's scripts/golden-path/Invoke-GoldenPath.ps1, so one session can run both suites in one VM round:
    PowerShell remoting to the Hyper-V host (-RigHost), then PowerShell Direct into the VM, with the two DPAPI credential files
    Deluno's script uses. The run touches nothing on the host except this one VM (restore its checkpoint, start it, copy files in
    and results out through a temporary folder it deletes).

    It runs two phases, each from the clean checkpoint:
      Fresh   installs the build under test (-InstallerPath) and runs every scenario against it.
      Update  installs the previous release, uses it, installs the build under test over it, and checks what survived.
    Inside the VM the scenarios run in the signed-in desktop session (a one-shot scheduled task), so the tray has a desktop. They
    are in Run-WeirScenarios.ps1; docs/release.md says what each proves.

    Everything the run produced is copied to <OutDirectory>\weir-scenarios-<version>-<sha>\: scenario-<version>-<sha>.md (one line
    per scenario, then what each does, what passes, and what was seen), each scenario's evidence, Weir's server and tray logs,
    Deluno's logs when it is installed, the stand-in manager's request log (Weir's /api/integrations/processors/events reports) and
    Weir's own request log.

    Then the pass record, as docs/golden-path.md step 12 describes: on a full pass it sets the `golden-path` commit status to
    success on -CommitSha, and on a failure it sets failure, so an earlier success can never be used by mistake. It needs `gh`
    signed in with the right to write statuses. -NoStatus keeps the record and sets nothing.

    -WhatIf lists what the run would do, and the scenarios, and touches no machine.

.PARAMETER RigHost
    The Hyper-V host that holds the VM. Never committed; pass it on the command line.

.PARAMETER InstallerPath
    The build under test: Weir-win-Setup.exe from the manual CI run given the release it will become
    (`gh workflow run ci.yml --repo jampat000/Weir --ref main -f version=<version>`, artifact weir-windows-<short sha>).

.PARAMETER Version
    The version under test, for example 1.0.0-rc.14. The installed build must report it.

.PARAMETER CommitSha
    The full 40-character SHA of the commit that build was made from, and that the tag will point at. The status is set on it. The
    installed build must report its first 7 characters.

.PARAMETER PreviousInstallerPath
    The previous release's Weir-win-Setup.exe. When omitted it is downloaded with `gh release download` from the newest published
    release older than -Version (found as release.yml finds it), and its sha256 is recorded.

.PARAMETER DelunoUrl
    Optional. A Deluno that is really there and reachable from the VM, for the "workflows set up from Deluno" scenario.

.PARAMETER DelunoApiKey
    That Deluno's API key.

.PARAMETER SourceFilm
    Optional. A real film (Big Buck Bunny, Creative Commons) to hand over instead of the film the run makes with Weir's FFmpeg.

.PARAMETER EvidenceUrl
    Where the status points. Defaults to the commit's page; pass the issue comment holding the record once it is posted.

.EXAMPLE
    ./scripts/scenarios/Invoke-WeirScenarios.ps1 -RigHost <host> -InstallerPath .\weir-build\Weir-win-Setup.exe -Version 1.0.0-rc.14 -CommitSha <40 hex>
#>
[CmdletBinding(SupportsShouldProcess)]
param(
    [Parameter(Mandatory)] [string] $RigHost,
    [Parameter(Mandatory)] [string] $InstallerPath,
    [Parameter(Mandatory)] [string] $Version,
    [Parameter(Mandatory)] [ValidatePattern('^[0-9a-f]{40}$')] [string] $CommitSha,
    [string] $PreviousInstallerPath,
    [string] $Repository = 'jampat000/Weir',
    [string] $VmName = 'Deluno-GoldenPath',
    [string] $Checkpoint = 'clean',
    [string] $RigCredentialFile = (Join-Path $env:LOCALAPPDATA 'Deluno-rig-admin.xml'),
    [string] $VmCredentialFile = (Join-Path $env:LOCALAPPDATA 'Deluno-goldenvm-admin.xml'),
    [string] $OutDirectory,
    [string] $DelunoUrl,
    [string] $DelunoApiKey,
    [string] $SourceFilm,
    [string] $EvidenceUrl,
    [int] $PhaseTimeoutMinutes = 60,
    [switch] $NoStatus
)

$ErrorActionPreference = 'Stop'
$repoRoot = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path
$shortSha = $CommitSha.Substring(0, 7)
if (-not $OutDirectory) { $OutDirectory = Join-Path $repoRoot 'artifacts' }
$runFolder = Join-Path $OutDirectory "weir-scenarios-$Version-$shortSha"

. (Join-Path $PSScriptRoot 'Weir.Scenarios.Lib.ps1')
. (Join-Path $PSScriptRoot 'Weir.Scenarios.Catalog.ps1')

$phases = @('Fresh', 'Update')

if ($WhatIfPreference) {
    Write-Host "WhatIf: nothing below is done. No machine is touched, no file is written, no status is set."
    Write-Host "Build under test: $Version, commit $CommitSha (the installed build must report +$shortSha), installer $InstallerPath"
    Write-Host "Previous release for the Update phase: $(if ($PreviousInstallerPath) { $PreviousInstallerPath } else { 'downloaded with gh from the newest release older than ' + $Version })"
    foreach ($phase in $phases) {
        Write-Host ""
        Write-Host "Phase ${phase}:"
        Write-Host "  1. On the Hyper-V host ${RigHost}: restore checkpoint '$Checkpoint' of VM '$VmName' and start it; wait for PowerShell Direct."
        Write-Host "  2. Copy the scenario scripts, the installer$(if ($phase -eq 'Update') { ', the previous release''s installer' })$(if ($SourceFilm) { ', the film' }) into C:\golden\weir (checking their SHA256)."
        Write-Host "  3. Start Run-WeirScenarios.ps1 -Phase $phase as a one-shot task in the signed-in desktop session; wait up to $PhaseTimeoutMinutes minutes."
        Write-Host "  4. Copy its results, evidence and logs back to $runFolder\$($phase.ToLowerInvariant())."
        foreach ($entry in $ScenarioCatalog | Where-Object { $_.Phase -eq $phase }) {
            Write-Host ("     - {0}{1}" -f $entry.Title, $(if ($entry.Required) { '' } else { ' (optional)' }))
            Write-Host "         does:   $($entry.Does)"
            Write-Host "         passes: $($entry.Passes)"
        }
    }
    Write-Host ""
    Write-Host "Then: write $runFolder\scenario-$Version-$shortSha.md; on a full pass set the golden-path status to success on $CommitSha (failure otherwise)$(if ($NoStatus) { ' - not with -NoStatus' })."
    return
}

if (-not (Test-Path -LiteralPath $InstallerPath)) { throw "Installer not found: $InstallerPath" }
$InstallerPath = (Resolve-Path -LiteralPath $InstallerPath).Path
if ($SourceFilm -and -not (Test-Path -LiteralPath $SourceFilm)) { throw "Film not found: $SourceFilm" }
if ([bool]$DelunoUrl -ne [bool]$DelunoApiKey) { throw '-DelunoUrl and -DelunoApiKey go together.' }
New-Item -ItemType Directory -Force -Path $runFolder | Out-Null

# --- the previous release ----------------------------------------------------------------------------------------------

$previousTag = $null
if (-not $PreviousInstallerPath) {
    $tags = & gh api --paginate "repos/$Repository/releases" --jq '.[] | select(.draft | not) | .tag_name'
    if ($LASTEXITCODE -ne 0) { throw 'gh could not list the published releases.' }
    $previousVersion = ($tags | & node (Join-Path $repoRoot 'scripts\find-previous-release.mjs') $Version.Split('+')[0])
    if (-not $previousVersion) { throw "No published release is older than $Version, so there is nothing to update from. (The first release has no Update phase; say so and run the Fresh phase alone by hand.)" }
    $previousTag = "v$previousVersion"
    $download = Join-Path $runFolder "previous-$previousTag"
    New-Item -ItemType Directory -Force -Path $download | Out-Null
    & gh release download $previousTag --repo $Repository --pattern 'Weir-win-Setup.exe' --dir $download --clobber
    if ($LASTEXITCODE -ne 0) { throw "gh could not download Weir-win-Setup.exe from $previousTag." }
    $PreviousInstallerPath = Join-Path $download 'Weir-win-Setup.exe'
}
$PreviousInstallerPath = (Resolve-Path -LiteralPath $PreviousInstallerPath).Path

# --- talking to the VM (the same two hops as Deluno's Invoke-GoldenPath.ps1) --------------------------------------------

$rigCred = Import-Clixml $RigCredentialFile
$vmCred = Import-Clixml $VmCredentialFile
$rig = $null

function Invoke-InVm([scriptblock] $Block, [object[]] $ArgumentList = @()) {
    Invoke-Command -Session $rig -ArgumentList $Block.ToString(), $ArgumentList, $VmName, $vmCred -ScriptBlock {
        param($text, $vmArgs, $name, $cred)
        $sb = [scriptblock]::Create($text)
        Invoke-Command -VMName $name -Credential $cred -ArgumentList $vmArgs -ScriptBlock $sb
    }
}

# local file -> host temp -> VM, and the hash checked at the far end.
function Copy-ToVm([string] $Source, [string] $Destination) {
    $staged = Invoke-Command -Session $rig -ScriptBlock { $d = Join-Path $env:TEMP ('weir-scn-' + [guid]::NewGuid().ToString('N')); New-Item -ItemType Directory -Path $d | Out-Null; $d }
    try {
        Copy-Item -LiteralPath $Source -Destination $staged -ToSession $rig
        $hash = (Get-FileHash -LiteralPath $Source -Algorithm SHA256).Hash
        $inVm = Invoke-Command -Session $rig -ArgumentList $staged, (Split-Path $Source -Leaf), $Destination, $VmName, $vmCred -ScriptBlock {
            param($dir, $file, $target, $name, $cred)
            $s = New-PSSession -VMName $name -Credential $cred
            try {
                Invoke-Command -Session $s -ArgumentList $target -ScriptBlock { param($t) New-Item -ItemType Directory -Force -Path $t | Out-Null }
                Copy-Item -LiteralPath (Join-Path $dir $file) -Destination $target -ToSession $s -Force
                Invoke-Command -Session $s -ArgumentList (Join-Path $target $file) -ScriptBlock { param($p) (Get-FileHash -LiteralPath $p -Algorithm SHA256).Hash }
            } finally { Remove-PSSession $s }
        }
        if ($inVm -ne $hash) { throw "Hash mismatch after copying $Source (local $hash, VM $inVm)." }
    }
    finally { Invoke-Command -Session $rig -ArgumentList $staged -ScriptBlock { param($d) Remove-Item -LiteralPath $d -Recurse -Force -ErrorAction SilentlyContinue } }
}

# VM folder -> zip -> host temp -> here.
function Copy-FromVm([string] $VmFolder, [string] $LocalFolder) {
    Invoke-InVm { param($f) Remove-Item 'C:\golden\weir-results.zip' -Force -ErrorAction SilentlyContinue; Compress-Archive -Path (Join-Path $f '*') -DestinationPath 'C:\golden\weir-results.zip' -Force } @($VmFolder)
    $staged = Invoke-Command -Session $rig -ScriptBlock { $d = Join-Path $env:TEMP ('weir-scn-' + [guid]::NewGuid().ToString('N')); New-Item -ItemType Directory -Path $d | Out-Null; $d }
    try {
        Invoke-Command -Session $rig -ArgumentList $staged, $VmName, $vmCred -ScriptBlock {
            param($dir, $name, $cred)
            $s = New-PSSession -VMName $name -Credential $cred
            try { Copy-Item -LiteralPath 'C:\golden\weir-results.zip' -Destination $dir -FromSession $s } finally { Remove-PSSession $s }
        }
        New-Item -ItemType Directory -Force -Path $LocalFolder | Out-Null
        $zip = Join-Path $LocalFolder 'results.zip'
        Copy-Item -LiteralPath (Join-Path $staged 'weir-results.zip') -Destination $zip -FromSession $rig -Force
        Expand-Archive -LiteralPath $zip -DestinationPath $LocalFolder -Force
        Remove-Item -LiteralPath $zip -Force
    }
    finally { Invoke-Command -Session $rig -ArgumentList $staged -ScriptBlock { param($d) Remove-Item -LiteralPath $d -Recurse -Force -ErrorAction SilentlyContinue } }
}

function Start-CleanVm {
    Invoke-Command -Session $rig -ArgumentList $VmName, $Checkpoint -ScriptBlock {
        param($name, $cp)
        if ((Get-VM -Name $name).State -ne 'Off') { Stop-VM -Name $name -TurnOff -Force }
        Restore-VMSnapshot -VMName $name -Name $cp -Confirm:$false
        Start-VM -Name $name
    }
    $deadline = (Get-Date).AddMinutes(15)
    $lastError = ''
    while ((Get-Date) -lt $deadline) {
        try { $null = Invoke-InVm { $env:COMPUTERNAME }; return } catch { $lastError = $_.Exception.Message; Start-Sleep -Seconds 5 }
    }
    throw "The VM did not answer over PowerShell Direct within 15 minutes (last error: $lastError)."
}

# --- one phase ------------------------------------------------------------------------------------------------------------

function Invoke-Phase([string] $Phase) {
    $local = Join-Path $runFolder $Phase.ToLowerInvariant()
    New-Item -ItemType Directory -Force -Path $local | Out-Null
    Write-Host "== Phase ${Phase}: restoring '$Checkpoint' on $VmName" -ForegroundColor Cyan
    Start-CleanVm

    $vmScripts = 'C:\golden\weir\scripts'
    foreach ($script in Get-ChildItem -LiteralPath $PSScriptRoot -Filter '*.ps1') { if ($script.Name -ne 'Invoke-WeirScenarios.ps1') { Copy-ToVm $script.FullName $vmScripts } }
    Copy-ToVm $InstallerPath 'C:\golden\weir\candidate'
    if ($Phase -eq 'Update') { Copy-ToVm $PreviousInstallerPath 'C:\golden\weir\previous' }
    if ($SourceFilm) { Copy-ToVm $SourceFilm 'C:\golden\weir\film' }

    $arguments = @{ Phase = $Phase; SetupPath = 'C:\golden\weir\candidate\Weir-win-Setup.exe'; Version = $Version; ShortSha = $shortSha }
    if ($Phase -eq 'Update') { $arguments.PreviousSetupPath = 'C:\golden\weir\previous\Weir-win-Setup.exe' }
    if ($DelunoUrl) { $arguments.DelunoUrl = $DelunoUrl; $arguments.DelunoApiKey = $DelunoApiKey }
    if ($SourceFilm) { $arguments.SourceFilm = Join-Path 'C:\golden\weir\film' (Split-Path $SourceFilm -Leaf) }

    $started = Invoke-InVm {
        param($arguments)
        $run = 'C:\golden\weir\run'
        Remove-Item $run -Recurse -Force -ErrorAction SilentlyContinue
        New-Item -ItemType Directory -Force -Path $run | Out-Null
        $pairs = foreach ($key in $arguments.Keys) { "-$key '" + ([string]$arguments[$key]).Replace("'", "''") + "'" }
        $wrapper = @(
            "`$ErrorActionPreference = 'Continue'",
            "try { & 'C:\golden\weir\scripts\Run-WeirScenarios.ps1' $($pairs -join ' ') *> '$run\runner.out'; Set-Content '$run\exitcode.txt' `$LASTEXITCODE }",
            "catch { `$_ | Out-String | Add-Content '$run\runner.out'; Set-Content '$run\exitcode.txt' 3 }") -join "`r`n"
        Set-Content -Path 'C:\golden\weir\start.ps1' -Value $wrapper -Encoding UTF8
        $user = "$env:USERDOMAIN\$env:USERNAME"
        $principal = New-ScheduledTaskPrincipal -UserId $user -LogonType Interactive -RunLevel Limited
        $settings = New-ScheduledTaskSettingsSet -ExecutionTimeLimit ([TimeSpan]::Zero) -AllowStartIfOnBatteries -DontStopIfGoingOnBatteries
        $action = New-ScheduledTaskAction -Execute 'powershell.exe' -Argument '-NoProfile -ExecutionPolicy Bypass -WindowStyle Hidden -File C:\golden\weir\start.ps1'
        Register-ScheduledTask -TaskName 'WeirScenarios' -Action $action -Principal $principal -Settings $settings -Force | Out-Null
        Start-ScheduledTask -TaskName 'WeirScenarios'
        'started'
    } @(, $arguments)
    if ($started -ne 'started') { throw 'The scenario task did not start.' }

    $deadline = (Get-Date).AddMinutes($PhaseTimeoutMinutes)
    $shown = 0
    $exitCode = $null
    while ((Get-Date) -lt $deadline -and $null -eq $exitCode) {
        Start-Sleep -Seconds 20
        $state = Invoke-InVm {
            $out = if (Test-Path 'C:\golden\weir\run\runner.out') { @(Get-Content 'C:\golden\weir\run\runner.out') } else { @() }
            $code = if (Test-Path 'C:\golden\weir\run\exitcode.txt') { (Get-Content 'C:\golden\weir\run\exitcode.txt' -Raw).Trim() } else { $null }
            [pscustomobject]@{ Out = $out; Code = $code }
        }
        $lines = @($state.Out)
        for (; $shown -lt $lines.Count; $shown++) { Write-Host "   [$Phase] $($lines[$shown])" }
        if ($state.Code) { $exitCode = [int]$state.Code }
    }
    Invoke-InVm { Unregister-ScheduledTask -TaskName 'WeirScenarios' -Confirm:$false -ErrorAction SilentlyContinue } | Out-Null
    Copy-FromVm 'C:\golden\weir\run' $local
    if ($null -eq $exitCode) { throw "Phase $Phase did not finish within $PhaseTimeoutMinutes minutes. What it had done is in $local." }
    Write-Host "== Phase $Phase finished with exit code $exitCode" -ForegroundColor Cyan
}

# --- the run ------------------------------------------------------------------------------------------------------------------

$clock = [Diagnostics.Stopwatch]::StartNew()
$aborted = $null
$phaseRan = $false
try {
    $rig = New-PSSession -ComputerName $RigHost -Credential $rigCred
    foreach ($phase in $phases) {
        $phaseRan = $true
        Invoke-Phase $phase
    }
}
catch {
    $aborted = $_.Exception.Message
    Write-Host "The run was cut short: $aborted" -ForegroundColor Red
}
finally {
    if ($rig) {
        try { Write-Host "VM $VmName is left $(Invoke-Command -Session $rig -ArgumentList $VmName -ScriptBlock { param($n) (Get-VM -Name $n).State.ToString() })." } catch { }
        Remove-PSSession $rig -ErrorAction SilentlyContinue
    }
}

# --- the record -----------------------------------------------------------------------------------------------------------

$results = New-Object System.Collections.Generic.List[object]
$facts = @{ Version = $Version; ShortSha = $shortSha; Commit = $CommitSha; Rehearsal = 'no'; 'Total run time' = "$([int]$clock.Elapsed.TotalMinutes) min" }
foreach ($phase in $phases) {
    $file = Join-Path $runFolder "$($phase.ToLowerInvariant())\results-$($phase.ToLowerInvariant()).json"
    if (-not (Test-Path -LiteralPath $file)) { continue }
    $phaseResults = Get-Content -LiteralPath $file -Raw | ConvertFrom-Json
    foreach ($result in @($phaseResults.Results)) { $results.Add($result) }
    foreach ($property in $phaseResults.Facts.PSObject.Properties) {
        if ($property.Name -in 'Version', 'ShortSha', 'Rehearsal') { continue }
        $facts["$($property.Name) ($phase)"] = $property.Value
    }
}
$facts['Installer'] = $InstallerPath
$facts['Previous release'] = "$(if ($previousTag) { $previousTag } else { 'given' }), sha256 $((Get-FileHash -LiteralPath $PreviousInstallerPath -Algorithm SHA256).Hash)"
if ($aborted) { $facts['Cut short'] = $aborted }
$resultArray = $results.ToArray()
$recordFile = Join-Path $runFolder "scenario-$Version-$shortSha.md"
$kept = @(Get-ChildItem -LiteralPath $runFolder -Recurse -File | Where-Object { $_.FullName -ne $recordFile } | ForEach-Object { $_.FullName.Substring($runFolder.Length + 1) })
Write-ScenarioRecord -Path $recordFile -Catalog $ScenarioCatalog -Results $resultArray -Facts $facts -Files $kept
Write-Host "Record: $recordFile"

$failed = @($ScenarioCatalog | Where-Object { $entry = $_; $r = @($resultArray | Where-Object { $_.Id -eq $entry.Id }) | Select-Object -First 1; $entry.Required -and (-not $r -or $r.Status -ne 'passed') })
$passedCount = @($resultArray | Where-Object Status -eq 'passed').Count
$optionalLeft = @($resultArray | Where-Object Status -eq 'not-applicable').Count
$allPassed = ($failed.Count -eq 0) -and -not $aborted

# --- the pass record --------------------------------------------------------------------------------------------------------

if ($NoStatus) {
    Write-Host "-NoStatus: the golden-path status is not set. Result: $(if ($allPassed) { 'every required scenario passed' } else { "$($failed.Count) required scenario(s) did not pass" })."
}
elseif (-not $phaseRan -or ($aborted -and $resultArray.Count -eq 0)) {
    Write-Host 'No scenario ran, so there is nothing to record and the golden-path status is left as it was.' -ForegroundColor Yellow
}
else {
    $state = if ($allPassed) { 'success' } else { 'failure' }
    $description = if ($allPassed) { "Weir scenarios passed: $passedCount of $(@($ScenarioCatalog).Count) ($optionalLeft optional not run), $Version+$shortSha" }
    else { "Weir scenarios failed: $($failed.Count) of $(@($ScenarioCatalog | Where-Object Required).Count) required did not pass$(if ($aborted) { '; run cut short' }), $Version+$shortSha" }
    $target = if ($EvidenceUrl) { $EvidenceUrl } else { "https://github.com/$Repository/commit/$CommitSha" }
    & gh api "repos/$Repository/statuses/$CommitSha" -f state=$state -f context=golden-path -f "description=$($description.Substring(0, [Math]::Min(140, $description.Length)))" -f "target_url=$target" | Out-Null
    if ($LASTEXITCODE -ne 0) { throw "gh could not set the golden-path status on $CommitSha. The record is at $recordFile." }
    Write-Host "golden-path status on ${shortSha}: $state ($description)" -ForegroundColor $(if ($allPassed) { 'Green' } else { 'Red' })
}
if (-not $allPassed) { exit 1 }
