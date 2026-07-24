param(
    [Parameter(Mandatory = $true)][ValidateSet("api", "web", "worker")][string]$Kind,
    [int]$Port,
    [string]$HeartbeatPath,
    [string]$RepositoryMarker,
    [switch]$FailImmediately
)

$ErrorActionPreference = "Stop"
if ($FailImmediately) { exit 23 }
if ($Kind -eq "worker") {
    while ($true) {
        [IO.File]::WriteAllText($HeartbeatPath, [DateTimeOffset]::UtcNow.ToString("O"))
        Start-Sleep -Milliseconds 250
    }
}

$listener = [Net.HttpListener]::new()
$listener.Prefixes.Add("http://127.0.0.1:$Port/")
$listener.Start()
try {
    while ($true) {
        $context = $listener.GetContext()
        $valid = ($Kind -eq "api" -and $context.Request.Url.AbsolutePath -eq "/health/live") -or
                 ($Kind -eq "web" -and $context.Request.Url.AbsolutePath -eq "/")
        $bytes = [Text.Encoding]::UTF8.GetBytes($(if ($valid) { '{"status":"healthy"}' } else { '{"status":"missing"}' }))
        $context.Response.StatusCode = if ($valid) { 200 } else { 404 }
        $context.Response.ContentType = "application/json"
        $context.Response.OutputStream.Write($bytes, 0, $bytes.Length)
        $context.Response.Close()
    }
} finally {
    $listener.Stop()
}
