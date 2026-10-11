<#
.SYNOPSIS
    Proves a Weir build on the golden Hyper-V VM before it is tagged, and records the result as the `golden-path` commit status.

.DESCRIPTION
    The same shape as Deluno's scripts/golden-path/Invoke-GoldenPath.ps1, so one session can run both suites in one VM round:
    PowerShell remoting to the Hyper-V host (-RigHost), then PowerShell Direct into the VM, with the two DPAPI credential files
    Deluno's script uses. The run touches nothing on the host except this one VM (restore its checkpoint for the second phase,
    copy files in and results out through a temporary folder it deletes).

    It runs two phases, in this order:
      WithDeluno  FIRST, with NO restore, on the VM exactly as Deluno's suite leaves it: Deluno set up, Weir installed by Deluno's
                  picker (the previous release), and C:\golden\deluno-weir-scenario-key.txt in the VM. It installs the build under
                  test (-InstallerPath) over that Weir, then follows what Deluno really did through Weir: workflows set up from
                  Deluno, its film hand-offs, a release with a small extra, a resend, the outcomes, and the Logs.
      Fresh       SECOND: restores the clean checkpoint (which has no Deluno, so Deluno and the key file are gone by then), installs the
                  build under test alone and runs the scenarios that need no manager: first visit and account, a film in a Weir-only
                  workflow, Process again, pause and resume, a deleted queued file, the update buttons, Logs.
    Inside the VM the scenarios run in the signed-in desktop session (a one-shot scheduled task), so the tray has a desktop. They
    are in Run-WeirScenarios.ps1; docs/release.md says what each proves.

    Everything the run produced is copied to <OutDirectory>\weir-scenarios-<version>-<sha>\: scenario-<version>-<sha>.md (one line
    per scenario, then what each does, what passes, and what was seen), each scenario's evidence, Weir's server and tray logs,
    Deluno's logs when it is installed, and Weir's own request log.

    Then the pass record, as docs/golden-path.md step 12 describes. Success needs BOTH phases and every scenario passed:
      - every scenario passed: the `golden-path` commit status on -CommitSha is set to success;
      - any scenario failed, or the run was cut short after tests began: failure, so an earlier success can never be used;
      - nothing failed but some scenario was not applicable (Deluno not answering, no key file, no hand-off of a kind to follow):
        pending, naming them. A release cannot pass without the real-Deluno scenarios.
    It needs `gh` signed in with the right to write statuses. -NoStatus keeps the record and sets nothing.

    -WhatIf lists what the run would do, in order, and the scenarios, and touches no machine.

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

.PARAMETER DelunoUrl
    The Deluno inside the VM (default http://127.0.0.1:7879). No secret is passed to the VM or carried between sessions.

.PARAMETER DelunoKeyFileInVm
    A path INSIDE the VM (default C:\golden\deluno-weir-scenario-key.txt): one line, a `read,imports` Deluno API key and nothing
    else, written there by the Deluno session. The run reads it in the VM session, never copying it out, and deletes it. The
    orchestrator only passes the path: it never reads, copies or logs the file.

.PARAMETER WeirLoginFileInVm
    A path INSIDE the VM (default C:\golden\weir-scenario-login.txt): two lines, a Weir user name then its password. Only for when
    Weir already has an account (Deluno's Connect Weir makes one) that this run did not make; Weir with no account gets the run's
    own. Read in the VM and deleted, like the key file.

.PARAMETER SourceFilm
    Optional. A real film (Big Buck Bunny, Creative Commons) to drop in instead of the film the run makes with Weir's own FFmpeg.

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
    [string] $Repository = 'jampat000/Weir',
    [string] $VmName = 'Deluno-GoldenPath',
    [string] $Checkpoint = 'clean',
    [string] $RigCredentialFile = (Join-Path $env:LOCALAPPDATA 'Deluno-rig-admin.xml'),
    [string] $VmCredentialFile = (Join-Path $env:LOCALAPPDATA 'Deluno-goldenvm-admin.xml'),
    [string] $OutDirectory,
    [string] $DelunoUrl = 'http://127.0.0.1:7879',
    [string] $DelunoKeyFileInVm = 'C:\golden\deluno-weir-scenario-key.txt',
    [string] $WeirLoginFileInVm = 'C:\golden\weir-scenario-login.txt',
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

# The order matters: Deluno's state is only there before the clean checkpoint is restored.
$phases = @('WithDeluno', 'Fresh')

if ($WhatIfPreference) {
    Write-Host "WhatIf: nothing below is done. No machine is touched, no file is written, no status is set."
    Write-Host "Build under test: $Version, commit $CommitSha (the installed build must report +$shortSha), installer $InstallerPath"
    foreach ($phase in $phases) {
        Write-Host ""
        if ($phase -eq 'WithDeluno') {
            Write-Host "Phase ${phase} (first, NO restore): the VM exactly as Deluno's suite left it - Deluno set up, Weir installed by Deluno's picker."
            Write-Host "  1. On the Hyper-V host ${RigHost}: check VM '$VmName' is running; restore nothing."
            Write-Host "  2. Copy the scenario scripts and the installer$(if ($SourceFilm) { ' and the film' }) into C:\golden\weir (checking their SHA256)."
            Write-Host "  3. Start Run-WeirScenarios.ps1 -Phase $phase as a one-shot task in the signed-in desktop session; wait up to $PhaseTimeoutMinutes minutes."
            Write-Host "     Inside the VM: Deluno is looked for at $DelunoUrl; its API key is read from $DelunoKeyFileInVm and the file deleted (else minted on a Deluno with no account, else not-applicable with the reason); Weir's login, if Weir already has an account, from $WeirLoginFileInVm (read, then deleted). No secret is passed or copied."
        }
        else {
            Write-Host "Phase ${phase} (second): the clean machine."
            Write-Host "  1. On the Hyper-V host ${RigHost}: restore checkpoint '$Checkpoint' of VM '$VmName' and start it; wait for PowerShell Direct. (Deluno and the key file are gone.)"
            Write-Host "  2. Copy the scenario scripts and the installer$(if ($SourceFilm) { ' and the film' }) into C:\golden\weir (checking their SHA256)."
            Write-Host "  3. Start Run-WeirScenarios.ps1 -Phase $phase as a one-shot task in the signed-in desktop session; wait up to $PhaseTimeoutMinutes minutes."
        }
        Write-Host "  4. Copy its results, evidence and logs back to $runFolder\$($phase.ToLowerInvariant())."
        foreach ($entry in $ScenarioCatalog | Where-Object { $_.Phase -eq $phase }) {
            Write-Host ("     - {0}" -f $entry.Title)
            Write-Host "         does:   $($entry.Does)"
            Write-Host "         passes: $($entry.Passes)"
        }
    }
    Write-Host ""
    Write-Host "Then: write $runFolder\scenario-$Version-$shortSha.md; set the golden-path status on $CommitSha$(if ($NoStatus) { ' - not with -NoStatus' }): success only if BOTH phases passed every scenario; failure if any failed; pending if none failed but some were not applicable (no success without the real-Deluno scenarios)."
    return
}

if (-not (Test-Path -LiteralPath $InstallerPath)) { throw "Installer not found: $InstallerPath" }
$InstallerPath = (Resolve-Path -LiteralPath $InstallerPath).Path
if ($SourceFilm -and -not (Test-Path -LiteralPath $SourceFilm)) { throw "Film not found: $SourceFilm" }
New-Item -ItemType Directory -Force -Path $runFolder | Out-Null

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

function Wait-VmAnswers {
    $deadline = (Get-Date).AddMinutes(15)
    $lastError = ''
    while ((Get-Date) -lt $deadline) {
        try { $null = Invoke-InVm { $env:COMPUTERNAME }; return } catch { $lastError = $_.Exception.Message; Start-Sleep -Seconds 5 }
    }
    throw "The VM did not answer over PowerShell Direct within 15 minutes (last error: $lastError)."
}

# The Fresh phase's start: the clean checkpoint, which has no Deluno.
function Restore-CleanVm {
    Invoke-Command -Session $rig -ArgumentList $VmName, $Checkpoint -ScriptBlock {
        param($name, $cp)
        if ((Get-VM -Name $name).State -ne 'Off') { Stop-VM -Name $name -TurnOff -Force }
        Restore-VMSnapshot -VMName $name -Name $cp -Confirm:$false
        Start-VM -Name $name
    }
    Wait-VmAnswers
}

# The WithDeluno phase's start: nothing is restored; the VM must already be running as Deluno's suite left it.
function Use-VmAsItIs {
    $state = Invoke-Command -Session $rig -ArgumentList $VmName -ScriptBlock { param($n) (Get-VM -Name $n).State.ToString() }
    if ($state -ne 'Running') { throw "VM '$VmName' is $state, not Running. The WithDeluno phase runs on the VM exactly as Deluno's suite left it, with nothing restored, so Deluno's suite has to have run and left it running." }
    Wait-VmAnswers
}

# --- one phase ------------------------------------------------------------------------------------------------------------

function Invoke-Phase([string] $Phase) {
    $local = Join-Path $runFolder $Phase.ToLowerInvariant()
    New-Item -ItemType Directory -Force -Path $local | Out-Null
    if ($Phase -eq 'WithDeluno') {
        Write-Host "== Phase ${Phase}: using $VmName exactly as Deluno's suite left it (no restore)" -ForegroundColor Cyan
        Use-VmAsItIs
    }
    else {
        Write-Host "== Phase ${Phase}: restoring '$Checkpoint' on $VmName" -ForegroundColor Cyan
        Restore-CleanVm
    }

    $vmScripts = 'C:\golden\weir\scripts'
    Invoke-InVm { Remove-Item 'C:\golden\weir\scripts' -Recurse -Force -ErrorAction SilentlyContinue }
    foreach ($script in Get-ChildItem -LiteralPath $PSScriptRoot -Filter '*.ps1') { if ($script.Name -ne 'Invoke-WeirScenarios.ps1') { Copy-ToVm $script.FullName $vmScripts } }
    Copy-ToVm $InstallerPath 'C:\golden\weir\candidate'
    if ($SourceFilm) { Copy-ToVm $SourceFilm 'C:\golden\weir\film' }

    $arguments = @{ Phase = $Phase; SetupPath = 'C:\golden\weir\candidate\Weir-win-Setup.exe'; Version = $Version; ShortSha = $shortSha
        DelunoUrl = $DelunoUrl; DelunoKeyFileInVm = $DelunoKeyFileInVm; WeirLoginFileInVm = $WeirLoginFileInVm }
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
if ($aborted) { $facts['Cut short'] = $aborted }
$resultArray = $results.ToArray()
$recordFile = Join-Path $runFolder "scenario-$Version-$shortSha.md"
$kept = @(Get-ChildItem -LiteralPath $runFolder -Recurse -File | Where-Object { $_.FullName -ne $recordFile } | ForEach-Object { $_.FullName.Substring($runFolder.Length + 1) })
Write-ScenarioRecord -Path $recordFile -Catalog $ScenarioCatalog -Results $resultArray -Facts $facts -Files $kept
Write-Host "Record: $recordFile"

function Get-ResultOf($Entry) { @($resultArray | Where-Object { $_.Id -eq $Entry.Id }) | Select-Object -First 1 }
$failed = @($ScenarioCatalog | Where-Object { $r = Get-ResultOf $_; -not $r -or $r.Status -eq 'failed' })
$notApplicable = @($ScenarioCatalog | Where-Object { $r = Get-ResultOf $_; $r -and $r.Status -eq 'not-applicable' })
$passedCount = @($resultArray | Where-Object Status -eq 'passed').Count
$total = @($ScenarioCatalog).Count
$allPassed = ($failed.Count -eq 0) -and ($notApplicable.Count -eq 0) -and -not $aborted

# --- the pass record --------------------------------------------------------------------------------------------------------

if ($NoStatus) {
    Write-Host "-NoStatus: the golden-path status is not set. Result: $passedCount of $total passed, $($failed.Count) failed or missing, $($notApplicable.Count) not applicable."
}
elseif (-not $phaseRan -or ($aborted -and $resultArray.Count -eq 0)) {
    Write-Host 'No scenario ran, so there is nothing to record and the golden-path status is left as it was.' -ForegroundColor Yellow
}
else {
    if ($allPassed) { $state = 'success'; $description = "Weir scenarios passed: $passedCount of $total, $Version+$shortSha" }
    elseif ($failed.Count -gt 0 -or $aborted) { $state = 'failure'; $description = "Weir scenarios failed: $($failed.Count) of $total did not pass$(if ($aborted) { '; run cut short' }), $Version+$shortSha" }
    else { $state = 'pending'; $description = "Weir scenarios incomplete: $($notApplicable.Count) not applicable ($((@($notApplicable | ForEach-Object { $_.Id }) -join ', '))), $Version+$shortSha" }
    $target = if ($EvidenceUrl) { $EvidenceUrl } else { "https://github.com/$Repository/commit/$CommitSha" }
    & gh api "repos/$Repository/statuses/$CommitSha" -f state=$state -f context=golden-path -f "description=$($description.Substring(0, [Math]::Min(140, $description.Length)))" -f "target_url=$target" | Out-Null
    if ($LASTEXITCODE -ne 0) { throw "gh could not set the golden-path status on $CommitSha. The record is at $recordFile." }
    Write-Host "golden-path status on ${shortSha}: $state ($description)" -ForegroundColor $(if ($allPassed) { 'Green' } else { 'Red' })
}
if (-not $allPassed) { exit 1 }
