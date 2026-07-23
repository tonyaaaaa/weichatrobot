[CmdletBinding(SupportsShouldProcess, ConfirmImpact = "Medium")]
param(
    [Parameter(Mandatory = $true)][uri]$TunnelUrl,
    [Parameter(Mandatory = $true)][string]$CallbackToken,
    [Parameter(Mandatory = $true)][string]$WorkToolRobotId,
    [Parameter(Mandatory = $true)][uri]$WorkToolUpdateUri,
    [switch]$Apply,
    [ValidateRange(1, 60)][int]$TimeoutSeconds = 10,
    [string]$Confirmation
)

$ErrorActionPreference = "Stop"

if (-not $TunnelUrl.IsAbsoluteUri -or
    $TunnelUrl.Scheme -cne "https" -or
    -not [string]::IsNullOrEmpty($TunnelUrl.UserInfo) -or
    $TunnelUrl.AbsolutePath -ne "/" -or
    -not [string]::IsNullOrEmpty($TunnelUrl.Query) -or
    -not [string]::IsNullOrEmpty($TunnelUrl.Fragment)) {
    throw "Cloudflare tunnel URL must be an HTTPS origin without user info, path, query, or fragment."
}
$updateIsLoopback = $WorkToolUpdateUri.IsLoopback -and $WorkToolUpdateUri.Scheme -eq "http"
if ($WorkToolUpdateUri.Scheme -ne "https" -and -not $updateIsLoopback) {
    throw "WorkTool update URI must use HTTPS; HTTP is allowed only for loopback fake tests."
}
if ([string]::IsNullOrWhiteSpace($CallbackToken) -or [string]::IsNullOrWhiteSpace($WorkToolRobotId)) {
    throw "Callback token and the authoritative WorkTool robot ID are required."
}

$callbackBuilder = [UriBuilder]::new($TunnelUrl)
$callbackBuilder.Path = "/api/worktool/callback/$([uri]::EscapeDataString($WorkToolRobotId))"
$callbackBuilder.Query = "token=$([uri]::EscapeDataString($CallbackToken))"
$callbackUrl = $callbackBuilder.Uri.AbsoluteUri
$safeUrl = "$($TunnelUrl.Scheme)://$($TunnelUrl.Authority)/api/worktool/callback/{robot-code}?token=[REDACTED]"
Write-Host "Callback route preview: $safeUrl"
$robotHash = [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData([Text.Encoding]::UTF8.GetBytes($WorkToolRobotId))).Substring(0, 12)
Write-Host "Target route fingerprint (SHA-256 prefix): $robotHash"

if (-not $Apply) {
    Write-Host "Preview only. Re-run with -Apply to permit an update."
    return
}

$answer = if ($PSBoundParameters.ContainsKey("Confirmation")) {
    if (-not $updateIsLoopback) { throw "-Confirmation is permitted only for loopback fake tests." }
    $Confirmation
} else {
    Read-Host "Type UPDATE to confirm this external callback change"
}
if ($answer -cne "UPDATE") { throw "Confirmation did not match; no update was sent." }

$safeTarget = "$($WorkToolUpdateUri.Scheme)://$($WorkToolUpdateUri.Authority)/"
if ($PSCmdlet.ShouldProcess($safeTarget, "Update the fingerprinted WorkTool robot callback")) {
    $payload = @{ robotId = $WorkToolRobotId; callbackUrl = $callbackUrl } | ConvertTo-Json -Compress
    $handler = [Net.Http.HttpClientHandler]::new()
    $client = [Net.Http.HttpClient]::new($handler)
    $client.Timeout = [TimeSpan]::FromSeconds($TimeoutSeconds)
    try {
        $content = [Net.Http.StringContent]::new($payload, [Text.Encoding]::UTF8, "application/json")
        $response = $client.PostAsync($WorkToolUpdateUri, $content).GetAwaiter().GetResult()
        $body = $response.Content.ReadAsStringAsync().GetAwaiter().GetResult()
        if (-not $response.IsSuccessStatusCode) {
            throw "Callback update endpoint returned HTTP $([int]$response.StatusCode)."
        }
        try { $business = $body | ConvertFrom-Json -ErrorAction Stop }
        catch { throw "Callback update endpoint returned invalid JSON." }
        if ($business.success -isnot [bool] -or -not $business.success) {
            throw "Callback update endpoint did not report business success."
        }
        Write-Host "WorkTool callback update request accepted."
    } finally {
        if ($content) { $content.Dispose() }
        if ($response) { $response.Dispose() }
        $client.Dispose()
        $handler.Dispose()
    }
}
