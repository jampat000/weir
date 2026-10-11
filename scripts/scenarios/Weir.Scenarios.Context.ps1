<#
    What the scenarios share: the run's context, Weir's installed paths, getting a signed-in session, and the calls Activity, the
    file list and the job list need. Dot-sourced by Run-WeirScenarios.ps1 after Weir.Scenarios.Lib.ps1.
#>

# The context the runner fills in before the first scenario. Scenarios read it and add what later scenarios need.
$script:Ctx = @{}

function Get-Prop {
    param($Object, [Parameter(Mandatory)] [string] $Name)
    if ($null -ne $Object -and $Object.PSObject.Properties[$Name]) { $Object.$Name } else { $null }
}

function New-NotApplicable {
    param([Parameter(Mandatory)] [string] $Reason)
    [pscustomobject]@{ NotApplicable = $true; Detail = $Reason }
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

# --- a signed-in session ----------------------------------------------------------------------------------------------

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

# Reads a login the Deluno session left in the VM (two lines: the user name, then the password), then deletes the file. Used
# only when Weir already has an account whose password this run was not given any other way. The same rule as the Deluno key file:
# it is read inside the VM and never copied out.
function Read-WeirLoginFile {
    $path = $script:Ctx.WeirLoginFile
    if (-not $path -or -not (Test-Path -LiteralPath $path -PathType Leaf)) { return $null }
    $lines = @(Get-Content -LiteralPath $path | Where-Object { $_.Trim() })
    Remove-Item -LiteralPath $path -Force
    if (Test-Path -LiteralPath $path) { throw "The login file $path was read but could not be deleted, so it was left lying around." }
    if ($lines.Count -ne 2) { throw "The login file $path did not hold two lines (the user name, then the password), and it has been deleted." }
    Register-Secret $lines[1].Trim()
    Add-Evidence "read Weir's login from $path inside this machine and deleted the file: user $($lines[0].Trim()), password ****"
    [pscustomobject]@{ Username = $lines[0].Trim(); Password = $lines[1].Trim() }
}

# Makes sure the run has a signed-in session on the Weir that is running. Weir with no account yet gets the run's own; one with an
# account needs its login from the login file in the VM. Returns $true, or $false with the reason in $script:Ctx.NoSessionReason.
function Initialize-WeirSession {
    if ($script:Ctx.Session) { return $true }
    if ($script:Ctx.ContainsKey('Username') -and $script:Ctx.Username) {
        $script:Ctx.Session = Sign-InScenarioAccount
        return $true
    }
    $anonymous = New-WeirSession -BaseUrl $script:Ctx.Base
    $status = Get-WeirJson $anonymous '/api/v1/auth/bootstrap/status'
    if ($status.bootstrap_allowed -eq $true) {
        New-ScenarioAccount
        $created = Invoke-Weir -Session $anonymous -Method POST -Path '/api/v1/auth/bootstrap' -Body @{ username = $script:Ctx.Username; password = $script:Ctx.Password }
        if ($created.Status -ne 200) { throw "Creating the run's account answered $($created.Status): $($created.Text)" }
        $script:Ctx.Session = Sign-InScenarioAccount
        return $true
    }
    $login = Read-WeirLoginFile
    if ($login) {
        $script:Ctx.Username = $login.Username
        $script:Ctx.Password = $login.Password
        $script:Ctx.Session = Sign-InScenarioAccount
        return $true
    }
    $script:Ctx.NoSessionReason = "Weir already has an account (it is not offering to create one), and no login was left for this run in the VM ($($script:Ctx.WeirLoginFile)), so there is no way to sign in without a secret from outside."
    $false
}

# --- Activity, files and jobs --------------------------------------------------------------------------------------------

function Get-ActivityItems {
    param([string] $EventType, [int] $Limit = 100)
    $path = "/api/v1/activity/recent?limit=$Limit"
    if ($EventType) { $path += "&event_type=$EventType" }
    @((Get-WeirJson $script:Ctx.Session $path).items)
}

function Get-ScenarioFiles {
    param([int] $LibraryId)
    $path = '/api/v1/processing/files?limit=1000'
    if ($LibraryId) { $path += "&library_id=$LibraryId" }
    @((Get-WeirJson $script:Ctx.Session $path).files)
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
