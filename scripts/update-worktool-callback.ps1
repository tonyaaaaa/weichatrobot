[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][uri]$ApiBaseUrl,
    [Parameter(Mandatory = $true)][Guid]$RobotConfigId,
    [Parameter(Mandatory = $true)][uri]$PublicBaseUrl,
    [Parameter(Mandatory = $true)][string]$BearerToken,
    [switch]$Apply,
    [string]$Confirmation
)

$ErrorActionPreference = "Stop"
Add-Type -AssemblyName System.Net.Http

function Test-Origin {
    param(
        [Parameter(Mandatory = $true)][uri]$Value,
        [switch]$AllowLoopbackHttp
    )

    if (-not $Value.IsAbsoluteUri -or
        -not [string]::IsNullOrEmpty($Value.UserInfo) -or
        $Value.AbsolutePath -ne "/" -or
        -not [string]::IsNullOrEmpty($Value.Query) -or
        -not [string]::IsNullOrEmpty($Value.Fragment)) {
        return $false
    }
    if ($Value.Scheme -eq "https") { return $true }
    return $AllowLoopbackHttp -and $Value.Scheme -eq "http" -and $Value.IsLoopback
}

if (-not (Test-Origin -Value $ApiBaseUrl -AllowLoopbackHttp)) {
    throw "API base URL must be an HTTPS origin; HTTP is allowed only for loopback tests."
}
if (-not (Test-Origin -Value $PublicBaseUrl -AllowLoopbackHttp)) {
    throw "Public callback base URL must be an HTTPS origin; HTTP is allowed only for loopback tests."
}
if ([string]::IsNullOrWhiteSpace($BearerToken)) {
    throw "Bearer token is required."
}

$publicOrigin = $PublicBaseUrl.GetLeftPart([UriPartial]::Authority)
Write-Host "Robot configuration: $($RobotConfigId.ToString('D'))"
Write-Host "Public callback origin: $publicOrigin"
Write-Host "Actions: configure message callback and command-result callback"

if (-not $Apply) {
    Write-Host "Preview only. Re-run with -Apply."
    return
}

$answer = if ($PSBoundParameters.ContainsKey("Confirmation")) {
    $Confirmation
} else {
    Read-Host "Type APPLY to confirm both callback configuration changes"
}
if ($answer -cne "APPLY") {
    throw "Confirmation did not match; no callback configuration was changed."
}

$handler = [Net.Http.HttpClientHandler]::new()
$client = [Net.Http.HttpClient]::new($handler)
$client.Timeout = [TimeSpan]::FromSeconds(30)
$client.DefaultRequestHeaders.Authorization =
    [Net.Http.Headers.AuthenticationHeaderValue]::new("Bearer", $BearerToken)

function Invoke-CallbackConfiguration {
    param(
        [Parameter(Mandatory = $true)][string]$RelativePath,
        [Parameter(Mandatory = $true)][hashtable]$Payload,
        [Parameter(Mandatory = $true)][string]$ActionName
    )

    $endpoint = [Uri]::new($ApiBaseUrl, $RelativePath)
    $content = $null
    $response = $null
    try {
        $json = $Payload | ConvertTo-Json -Compress
        $content = [Net.Http.StringContent]::new($json, [Text.Encoding]::UTF8, "application/json")
        $response = $client.PostAsync($endpoint, $content).GetAwaiter().GetResult()
        $body = $response.Content.ReadAsStringAsync().GetAwaiter().GetResult()
        if (-not $response.IsSuccessStatusCode) {
            throw "$ActionName returned HTTP $([int]$response.StatusCode)."
        }
        try { $business = $body | ConvertFrom-Json -ErrorAction Stop }
        catch { throw "$ActionName returned invalid JSON." }
        if ($business.succeeded -isnot [bool] -or -not $business.succeeded) {
            throw "$ActionName did not report success."
        }
    } finally {
        if ($content) { $content.Dispose() }
        if ($response) { $response.Dispose() }
    }
}

try {
    $robotPath = [uri]::EscapeDataString($RobotConfigId.ToString("D"))
    Invoke-CallbackConfiguration `
        -RelativePath "api/admin/worktool/robots/$robotPath/message-callback/configure" `
        -Payload @{ publicBaseUrl = $publicOrigin; replyAll = $true } `
        -ActionName "Message callback configuration"
    Write-Host "Message callback configuration accepted."

    Invoke-CallbackConfiguration `
        -RelativePath "api/admin/worktool/robots/$robotPath/command-result-callback/configure" `
        -Payload @{ publicBaseUrl = $publicOrigin } `
        -ActionName "Command-result callback configuration"
    Write-Host "Command-result callback configuration accepted."
} finally {
    $client.Dispose()
    $handler.Dispose()
}
