<#
.SYNOPSIS
    A stand-in for Deluno's side of a hand-off, for Weir's scenario suite: it answers the calls Weir makes to a media manager and records every request.

.DESCRIPTION
    Weir's hand-off scenarios post hand-offs to Weir's intake API the way Deluno does, and Weir reports each finished
    hand-off back to the manager that sent it. This is that manager. It answers the paths a Deluno connection needs
    (the same ones the contract suite's FakeManager answers, apps/server/tests/Weir.Contract.Tests/Harness/Fakes) and
    appends one JSON line per request to -RequestLog, which is where the scenarios read the report Weir sent and where
    the run keeps the request log for /api/integrations/processors/events.

    It listens on 127.0.0.1 only and needs no administrator rights. Run it in its own PowerShell process: it serves
    until -StopFile exists, then exits.

.PARAMETER Port
    The port to listen on, on 127.0.0.1.

.PARAMETER RequestLog
    Where each request is recorded, one JSON object per line: time, method, path, query, headers and body.

.PARAMETER StopFile
    The file whose existence tells the stand-in to stop.

.NOTES
    The manifest lists no libraries on purpose. The scenarios build their own workflow over their own folders and link it
    to this connection, so Weir has no library of the manager's to set up from and the hand-off scenarios never touch the
    Movies and TV workflows a real Deluno sets up.
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)] [int] $Port,
    [Parameter(Mandatory)] [string] $RequestLog,
    [Parameter(Mandatory)] [string] $StopFile
)

$ErrorActionPreference = 'Stop'
$listener = New-Object System.Net.HttpListener
$listener.Prefixes.Add("http://127.0.0.1:$Port/")
$listener.Start()

function Send-Json($Context, [int] $Status, $Body) {
    $json = if ($null -eq $Body) { '' } else { $Body | ConvertTo-Json -Depth 8 -Compress }
    $bytes = [Text.Encoding]::UTF8.GetBytes($json)
    $Context.Response.StatusCode = $Status
    $Context.Response.ContentType = 'application/json'
    $Context.Response.ContentLength64 = $bytes.Length
    $Context.Response.OutputStream.Write($bytes, 0, $bytes.Length)
    $Context.Response.OutputStream.Close()
}

# The shape of Deluno's own /api/integrations/external/manifest (Deluno.Platform), with no libraries listed.
$manifest = @{
    product              = 'Deluno'
    version              = 'scenario-stand-in'
    instanceName         = 'Scenario stand-in'
    capabilities         = @('movies', 'tv', 'library-routing', 'metadata', 'pre-import-processing')
    recommendedCategories = @{ movies = 'deluno-movies'; tv = 'deluno-tv' }
    libraries            = @()
}

try {
    while (-not (Test-Path -LiteralPath $StopFile)) {
        $pending = $listener.GetContextAsync()
        while (-not $pending.Wait(500)) {
            if (Test-Path -LiteralPath $StopFile) { return }
        }
        $context = $pending.Result
        $request = $context.Request
        $body = ''
        if ($request.HasEntityBody) {
            $reader = New-Object System.IO.StreamReader($request.InputStream, [Text.Encoding]::UTF8)
            $body = $reader.ReadToEnd()
            $reader.Close()
        }
        $headers = @{}
        foreach ($name in $request.Headers.AllKeys) {
            if ($name -notmatch '^(Cookie|Authorization)$') { $headers[$name] = $request.Headers[$name] }
        }
        $record = [ordered]@{
            at      = (Get-Date).ToUniversalTime().ToString('o')
            method  = $request.HttpMethod
            path    = $request.Url.AbsolutePath
            query   = $request.Url.Query
            headers = $headers
            body    = $body
        }
        [IO.File]::AppendAllText($RequestLog, (($record | ConvertTo-Json -Depth 6 -Compress) + "`n"), (New-Object System.Text.UTF8Encoding($false)))

        $route = "$($request.HttpMethod) $($request.Url.AbsolutePath)"
        switch ($route) {
            'GET /api/integrations/external/health' { Send-Json $context 200 @{ status = 'ok' } }
            'GET /api/integrations/external/manifest' { Send-Json $context 200 $manifest }
            'GET /api/integrations/external/queue' { Send-Json $context 200 @{ jobs = @(); dispatches = @() } }
            'GET /api/integrations/processors/download-destinations' { Send-Json $context 200 @{ libraries = @() } }
            'POST /api/integrations/processors/events' { Send-Json $context 202 @{ accepted = $true } }
            default { Send-Json $context 404 @{ message = "the stand-in manager has no route for $route" } }
        }
    }
}
finally {
    $listener.Abort()
}
