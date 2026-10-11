<#
    The scenarios that need the real Deluno in the VM: workflows set up from it, and the hand-offs, resends and outcomes Deluno's
    own suite really made, read through Deluno's API with a key that never leaves the VM.

    Deluno's side of a hand-off cannot be made by a script (a hand-off starts when a download finishes in one of its clients), so
    these scenarios follow the real ones from both ends and drive what Deluno does offer: its own send-again for an outcome.
    A precondition that is not met (no Deluno answering, no key, no hand-off of the kind) is recorded not-applicable with the
    reason, and a record with any not-applicable scenario sets no success status.
#>

# --- the key (it never leaves this machine) ---------------------------------------------------------------------------------

# The first way to have a key: the Deluno session of a combined round mints a read,imports key and leaves it in this machine, in a
# file holding that one line (the key, nothing else). It is read here, never copied out, and the file is deleted at once so it is
# not left lying around. Returns the key, or $null when there is no file. A file that is there but unusable is a failure.
function Read-DelunoKeyFile {
    $path = $script:Ctx.DelunoKeyFile
    if (-not $path -or -not (Test-Path -LiteralPath $path -PathType Leaf)) { return $null }
    $lines = @(Get-Content -LiteralPath $path | Where-Object { $_.Trim() })
    $key = if ($lines.Count -eq 1) { $lines[0].Trim() } else { $null }
    Remove-Item -LiteralPath $path -Force
    if (Test-Path -LiteralPath $path) { throw "The key file $path was read but could not be deleted, so it was left lying around." }
    if (-not $key -or $key -notmatch '^deluno_\S{8,}$') { throw "The key file $path did not hold one line with a Deluno API key (deluno_...), and it has been deleted. The file is the key alone, on one line." }
    Register-Secret $key
    Add-Evidence "read the API key from $path inside this machine and deleted the file: ****"
    $key
}

# Gets an API key for the Deluno on this machine without any secret from outside the VM, in this order: the key file the Deluno
# session left in the VM (Read-DelunoKeyFile), then a throwaway account on a Deluno that has none, otherwise no key and the reason.
# Deluno keeps only a hash of each key it hands out (api_keys.key_hash), so an existing key cannot be read back; one is minted
# through Deluno's own local API (POST /api/api-keys, signed in). Returns { Key } or { Reason }. The key and the token are
# registered as secrets, so nothing the run writes can show them.
function Get-DelunoApiKey {
    $fromFile = Read-DelunoKeyFile
    if ($fromFile) { return [pscustomobject]@{ Key = $fromFile; Reason = $null } }
    $url = $script:Ctx.DelunoUrl.TrimEnd('/')
    try { $status = Invoke-RestMethod -Uri "$url/api/auth/bootstrap-status" -TimeoutSec 10 }
    catch { return [pscustomobject]@{ Key = $null; Reason = "No Deluno answers at $url (GET /api/auth/bootstrap-status: $($_.Exception.Message)), and no key file was left in the VM ($($script:Ctx.DelunoKeyFile))." } }
    if ($null -eq $status.PSObject.Properties['requiresSetup']) { return [pscustomobject]@{ Key = $null; Reason = "Something answers at $url but it does not look like Deluno (no requiresSetup in its bootstrap status)." } }
    if (-not $status.requiresSetup) {
        return [pscustomobject]@{ Key = $null; Reason = "The Deluno at $url already has an account and no key file was left in the VM ($($script:Ctx.DelunoKeyFile)). Deluno keeps only a hash of each API key it hands out, so an existing key cannot be read back, and minting one needs a signed-in Deluno account whose password only the session that made it holds. Nothing in this run carries that password." }
    }
    $password = 'Sc-' + [guid]::NewGuid().ToString('N') + '-Aa1'
    Register-Secret $password
    $account = @{ username = 'weir-scenarios'; displayName = 'Weir scenarios'; password = $password } | ConvertTo-Json -Compress
    $signedIn = Invoke-RestMethod -Method POST -Uri "$url/api/auth/bootstrap" -ContentType 'application/json' -Body $account -TimeoutSec 30
    Register-Secret $signedIn.accessToken
    Add-Evidence "created a throwaway Deluno account 'weir-scenarios' on the Deluno at $url (it had none); its password was made here and is kept nowhere"
    $minted = Invoke-RestMethod -Method POST -Uri "$url/api/api-keys" -ContentType 'application/json' -Headers @{ Authorization = "Bearer $($signedIn.accessToken)" } `
        -Body (@{ name = 'Weir scenarios (throwaway)'; scopes = 'read,imports' } | ConvertTo-Json -Compress) -TimeoutSec 30
    Register-Secret $minted.apiKey
    Add-Evidence 'minted an API key (scopes read, imports) through Deluno''s own API: ****'
    [pscustomobject]@{ Key = $minted.apiKey; Reason = $null }
}

# Sets up the run's way into Deluno once (the key file can only be read once). $true, or $false with the reason in
# $script:Ctx.NoDelunoReason.
function Initialize-Deluno {
    if ($script:Ctx.ContainsKey('DelunoReady')) { return $script:Ctx.DelunoReady }
    $script:Ctx.DelunoReady = $false
    if (-not $script:Ctx.DelunoUrl) {
        $script:Ctx.NoDelunoReason = 'This run was not pointed at a Deluno (a rehearsal does not look for one).'
        return $false
    }
    $obtained = Get-DelunoApiKey
    if (-not $obtained.Key) { $script:Ctx.NoDelunoReason = $obtained.Reason; return $false }
    $script:Ctx.DelunoKey = $obtained.Key
    $script:Ctx.DelunoReady = $true
    $true
}

# What a scenario needs before it starts: Deluno reachable with a key, and a signed-in Weir. Returns $null when it can go ahead,
# else the not-applicable result to return.
function Test-DelunoPreconditions {
    if (-not (Initialize-Deluno)) { return New-NotApplicable $script:Ctx.NoDelunoReason }
    if (-not (Initialize-WeirSession)) { return New-NotApplicable $script:Ctx.NoSessionReason }
    $null
}

# One call to Deluno with the key. Never throws on an HTTP error; the key is never written anywhere.
function Invoke-Deluno {
    param([string] $Method = 'GET', [Parameter(Mandatory)] [string] $Path, $Body)
    $parameters = @{ Uri = "$($script:Ctx.DelunoUrl.TrimEnd('/'))$Path"; Method = $Method; Headers = @{ 'X-Api-Key' = $script:Ctx.DelunoKey; Accept = 'application/json' }; UseBasicParsing = $true; TimeoutSec = 60 }
    if ($null -ne $Body) { $parameters['Body'] = [Text.Encoding]::UTF8.GetBytes(($Body | ConvertTo-Json -Depth 8 -Compress)); $parameters['ContentType'] = 'application/json' }
    $status = 0
    $text = ''
    try { $response = Invoke-WebRequest @parameters; $status = [int]$response.StatusCode; $text = [string]$response.Content }
    catch {
        if ($_.Exception.Response) { $status = [int]$_.Exception.Response.StatusCode; $text = if ($_.ErrorDetails) { [string]$_.ErrorDetails.Message } else { '' } }
        else { $text = $_.Exception.Message }
    }
    $json = $null
    if ($text.TrimStart().StartsWith('{') -or $text.TrimStart().StartsWith('[')) { try { $json = $text | ConvertFrom-Json } catch { $json = $null } }
    Add-Evidence "Deluno $Method $Path -> $status"
    [pscustomobject]@{ Status = $status; Json = $json; Text = $text }
}

# Deluno's hand-offs (its own list, newest first), as Deluno reports them.
function Get-DelunoHandoffs {
    $answer = Invoke-Deluno -Path '/api/integrations/processors/handoffs?take=200'
    if ($answer.Status -ne 200) { throw "Deluno's hand-off list answered $($answer.Status): $($answer.Text)" }
    @($answer.Json)
}

function Get-OutcomeOf {
    param($Handoff)
    Get-Prop $Handoff 'outcome'
}

# The Weir files a Deluno hand-off ended up as: its output path's last part is the cleaned file's name, or the release folder's.
function Find-WeirFilesFor {
    param([Parameter(Mandatory)] $Handoff, [Parameter(Mandatory)] $WeirFiles)
    $output = [string](Get-Prop $Handoff 'outputPath')
    if (-not $output) { return @() }
    $leaf = Split-Path -Leaf ($output.TrimEnd('\', '/'))
    @($WeirFiles | Where-Object {
            $copy = [string](Get-Prop (Get-Prop $_ 'handback') 'output_path')
            $copy -and ((Split-Path -Leaf $copy) -eq $leaf -or (Split-Path -Leaf (Split-Path -Parent $copy)) -eq $leaf)
        })
}

# The connection Weir has to this Deluno (the one Deluno's Connect Weir made, or the one this run makes).
function Get-WeirDelunoConnection {
    $port = ([uri]$script:Ctx.DelunoUrl).Port
    @((Get-WeirJson $script:Ctx.Session '/api/v1/media-managers/connections') | Where-Object { $_.kind -eq 'deluno' -and ([uri]$_.base_url).Port -eq $port }) | Select-Object -First 1
}

# --- workflows-from-deluno ----------------------------------------------------------------------------------------------

function Scenario_workflows_from_deluno {
    $unmet = Test-DelunoPreconditions
    if ($unmet) { return $unmet }
    $session = $script:Ctx.Session
    $connection = Get-WeirDelunoConnection
    $how = 'found the connection Deluno had already made'
    if (-not $connection) {
        $created = Invoke-Weir -Session $session -Method POST -Path '/api/v1/media-managers/connections' -Body @{ kind = 'deluno'; base_url = $script:Ctx.DelunoUrl; api_key = $script:Ctx.DelunoKey; enabled = $true }
        Assert-That ($created.Status -in 200, 201) "Weir accepted the connection to Deluno at $($script:Ctx.DelunoUrl) (answered $($created.Status): $($created.Text))"
        $connection = $created.Json
        $how = 'connected Weir to Deluno with the key from the key file'
    }
    $connectionId = [int]$connection.id
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
    $script:Ctx.DelunoConnectionId = $connectionId
    "Weir $how; $($workflows.Count) workflow(s) are set up from Deluno ($(@($workflows | ForEach-Object { $_.name }) -join ', ')), linked, with their folders from Deluno and every step of the folder chain ready."
}

# --- deluno-film-handoff ------------------------------------------------------------------------------------------------

function Scenario_deluno_film_handoff {
    $unmet = Test-DelunoPreconditions
    if ($unmet) { return $unmet }
    $films = @(Get-DelunoHandoffs | Where-Object { $_.mediaType -eq 'movies' -and $_.status -eq 'completed' })
    if ($films.Count -eq 0) { return New-NotApplicable 'Deluno has made no completed film hand-off, so there is no film to follow through Weir. Deluno''s own suite makes them (its torrent and Usenet films) before this phase.' }
    $weirFiles = @(Get-ScenarioFiles)
    $followed = 0
    foreach ($film in $films) {
        $copies = @(Find-WeirFilesFor -Handoff $film -WeirFiles $weirFiles)
        Assert-That ($copies.Count -ge 1) "Weir shows the cleaned file for Deluno's hand-off of $($film.releaseName)"
        foreach ($copy in $copies) { Assert-That ($copy.status -eq 'processed') "Weir shows $($copy.relative_path) as processed (it reads $($copy.status))" }
        $outcome = Get-OutcomeOf $film
        if ($outcome -and $outcome.outcome -eq 'imported') {
            foreach ($copy in $copies) { Assert-That ((Get-Prop $copy.handback 'outcome') -eq 'imported') "Weir has Deluno's imported answer recorded against $($copy.relative_path)" }
        }
        $followed++
    }
    $imported = @($films | Where-Object { $o = Get-OutcomeOf $_; $o -and $o.outcome -eq 'imported' }).Count
    Assert-That ($imported -ge 1) 'at least one film Deluno handed over was imported by Deluno'
    "Followed $followed completed film hand-off(s) from Deluno's list through Weir ($imported imported): each shows as processed in Weir, with Deluno's imported answer recorded against it."
}

# --- deluno-release-with-extra ------------------------------------------------------------------------------------------

function Scenario_deluno_release_with_extra {
    $unmet = Test-DelunoPreconditions
    if ($unmet) { return $unmet }
    if (-not $script:Ctx.ContainsKey('DelunoConnectionId')) { $script:Ctx.DelunoConnectionId = [int](Get-WeirDelunoConnection).id }
    $fed = @((Get-WeirJson $script:Ctx.Session '/api/v1/processing/libraries') | Where-Object { @($_.manager_connection_ids) -contains $script:Ctx.DelunoConnectionId })
    if ($fed.Count -eq 0) { return New-NotApplicable 'No Weir workflow is fed by this Deluno, so there is no release to look at.' }
    foreach ($workflow in $fed) {
        $minimum = [long]$workflow.min_file_size_mb * 1MB
        $groups = Get-ScenarioFiles -LibraryId $workflow.id | Group-Object { ([string]$_.relative_path) -replace '/[^/]*$', '' } | Where-Object { $_.Name -and $_.Count -ge 2 }
        foreach ($group in $groups) {
            $film = @($group.Group | Where-Object { $_.status -eq 'processed' }) | Select-Object -First 1
            $extra = @($group.Group | Where-Object { $_.status -in 'skipped', 'unprocessed' -and $_.size_bytes -lt $minimum }) | Select-Object -First 1
            if (-not $film -or -not $extra) { continue }
            $counts = (Get-WeirJson $script:Ctx.Session "/api/v1/processing/files?limit=1&library_id=$($workflow.id)").status_counts
            Assert-That ($extra.status -ne 'processing_failed' -and $extra.status -ne 'rejected') "the extra $($extra.relative_path) is left alone, not failed (status $($extra.status))"
            Assert-That ($counts.processing_failed -eq 0 -and $counts.rejected -eq 0) "nothing in '$($workflow.name)' needs the person"
            return "In '$($workflow.name)', the release $($group.Name) holds the film $($film.relative_path) (processed) and the extra $($extra.relative_path) ($($extra.size_bytes) bytes, under the $($workflow.min_file_size_mb) MB minimum), which was left alone (status $($extra.status): ""$($extra.status_reason)""); nothing needs the person."
        }
    }
    New-NotApplicable 'None of the releases Deluno handed over holds a film and an extra under the workflow''s minimum size, so there is nothing to look at. Deluno''s suite has to include the film that has a small extra.'
}

# --- deluno-resend ------------------------------------------------------------------------------------------------------

function Scenario_deluno_resend {
    $unmet = Test-DelunoPreconditions
    if ($unmet) { return $unmet }
    $weirFiles = @(Get-ScenarioFiles)
    $chosen = $null
    foreach ($handoff in Get-DelunoHandoffs) {
        $outcome = Get-OutcomeOf $handoff
        if (-not $outcome -or $outcome.outcome -ne 'imported' -or $outcome.delivery -notin 'delivered', 'settled') { continue }
        $copies = @(Find-WeirFilesFor -Handoff $handoff -WeirFiles $weirFiles | Where-Object { (Get-Prop $_.handback 'outcome') -eq 'imported' })
        if ($copies.Count -ge 1) { $chosen = [pscustomobject]@{ Handoff = $handoff; Copies = $copies }; break }
    }
    if (-not $chosen) { return New-NotApplicable 'Deluno has no imported hand-off with its outcome delivered and recorded in Weir, so there is nothing to send again.' }
    $before = @($chosen.Copies | ForEach-Object { "$($_.id)|$($_.handback.outcome)|$($_.handback.settled_at)|$($_.handback.released_at)" })
    $failuresBefore = @(Get-ActivityItems | Where-Object { $_.result -in 'failed', 'error' }).Count

    $sent = Invoke-Deluno -Method POST -Path "/api/integrations/processors/handoffs/$($chosen.Handoff.id)/outcome/send-again"
    Assert-That ($sent.Status -eq 200) "Deluno sent the outcome again (answered $($sent.Status): $($sent.Json.message))"
    Assert-That (@('Answered', 'Settled', 0, 7) -contains $sent.Json.outcome) "Weir answered the repeat as heard or already settled, never refused (Deluno reports: $($sent.Json.outcome))"
    $after = Get-DelunoHandoffs | Where-Object { $_.id -eq $chosen.Handoff.id } | Select-Object -First 1
    Assert-That ($after.outcome.delivery -in 'delivered', 'settled') "Deluno records the repeat as delivered or settled (it reads $($after.outcome.delivery))"

    $afterCopies = @(Find-WeirFilesFor -Handoff $after -WeirFiles @(Get-ScenarioFiles) | Where-Object { $chosen.Copies.id -contains $_.id })
    $afterState = @($afterCopies | ForEach-Object { "$($_.id)|$($_.handback.outcome)|$($_.handback.settled_at)|$($_.handback.released_at)" })
    Assert-That ((($before | Sort-Object) -join ';') -eq (($afterState | Sort-Object) -join ';')) 'Weir kept the one imported answer and the file it had already settled, unchanged'
    Assert-That (@(Get-ActivityItems | Where-Object { $_.result -in 'failed', 'error' }).Count -eq $failuresBefore) 'the repeat raised no failure in Weir''s Activity'
    "Deluno sent the outcome for $($chosen.Handoff.releaseName) again; it answered $($sent.Json.outcome), Deluno reads the delivery as $($after.outcome.delivery), and Weir's file stayed imported and settled as it was, with no failure."
}

# --- deluno-outcomes ----------------------------------------------------------------------------------------------------

function Scenario_deluno_outcomes {
    $unmet = Test-DelunoPreconditions
    if ($unmet) { return $unmet }
    $withOutcome = @(Get-DelunoHandoffs | Where-Object { Get-OutcomeOf $_ })
    if ($withOutcome.Count -eq 0) { return New-NotApplicable 'Deluno has recorded no outcome for any hand-off yet, so there is nothing to compare.' }
    $weirFiles = @(Get-ScenarioFiles)
    $compared = 0
    foreach ($handoff in $withOutcome) {
        $outcome = Get-OutcomeOf $handoff
        Assert-That ($outcome.delivery -notin 'pending', 'refused', 'never-received', 'given-up') "Deluno's outcome for $($handoff.releaseName) is $($outcome.outcome), delivery $($outcome.delivery), not pending, refused, never received or given up"
        foreach ($copy in @(Find-WeirFilesFor -Handoff $handoff -WeirFiles $weirFiles)) {
            $weir = Get-Prop $copy.handback 'outcome'
            if ($weir) {
                Assert-That ($weir -eq $outcome.outcome) "$($copy.relative_path): Deluno says $($outcome.outcome) and Weir says $weir"
                $compared++
            }
        }
    }
    "Read the outcomes of $($withOutcome.Count) hand-off(s) in Deluno: none pending, refused, never received or given up; $compared of Weir's files carry the same answer as Deluno."
}

# --- logs-clean-deluno --------------------------------------------------------------------------------------------------

function Scenario_logs_clean_deluno {
    if (-not (Initialize-WeirSession)) { return New-NotApplicable $script:Ctx.NoSessionReason }
    Test-LogsClean -Since $script:Ctx.PhaseStartUtc
}
