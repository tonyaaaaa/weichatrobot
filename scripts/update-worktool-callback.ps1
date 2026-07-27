[CmdletBinding(SupportsShouldProcess, ConfirmImpact = "Low")]
param(
    [uri]$ApiBaseUrl = "https://wxrobot.aavisa.com/",
    [uri]$PublicBaseUrl = "https://wxrobot.aavisa.com/",
    [switch]$Apply,
    [ValidateRange(1, 60)][int]$TimeoutSeconds = 15,
    [string]$Email,
    [securestring]$Password,
    [string]$Confirmation,
    [ValidateRange(0, 2147483647)][int]$RobotSelection = 0,
    [string]$TestPassword
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

$apiOrigin = $ApiBaseUrl.GetLeftPart([UriPartial]::Authority)
$publicOrigin = $PublicBaseUrl.GetLeftPart([UriPartial]::Authority)
Write-Host "API origin: $apiOrigin"
Write-Host "Public callback origin: $publicOrigin"
Write-Host "Actions: configure message callback and command-result callback"

if (-not $Apply -or $WhatIfPreference) {
    Write-Host "Preview only. Re-run with -Apply."
    return
}

$isLoopbackApi = $ApiBaseUrl.IsLoopback
if (-not [string]::IsNullOrEmpty($TestPassword) -and -not $isLoopbackApi) {
    throw "-TestPassword is permitted only for loopback fake API tests."
}
if ($PSBoundParameters.ContainsKey("Confirmation") -and -not $isLoopbackApi) {
    throw "-Confirmation is permitted only for loopback fake API tests."
}
if ([string]::IsNullOrWhiteSpace($Email)) {
    $Email = Read-Host "Administrator email"
}
if ([string]::IsNullOrWhiteSpace($Email)) {
    throw "Administrator email is required."
}
if ($null -eq $Password -and [string]::IsNullOrEmpty($TestPassword)) {
    $Password = Read-Host "Administrator password" -AsSecureString
}

$handler = [Net.Http.HttpClientHandler]::new()
$client = [Net.Http.HttpClient]::new($handler)
$client.Timeout = [TimeSpan]::FromSeconds($TimeoutSeconds)

function Invoke-JsonRequest {
    param(
        [Parameter(Mandatory = $true)][Net.Http.HttpMethod]$Method,
        [Parameter(Mandatory = $true)][string]$RelativePath,
        [object]$Payload,
        [Parameter(Mandatory = $true)][string]$ActionName
    )

    $endpoint = [Uri]::new($ApiBaseUrl, $RelativePath.TrimStart("/"))
    $request = [Net.Http.HttpRequestMessage]::new($Method, $endpoint)
    $response = $null
    try {
        if ($null -ne $Payload) {
            $json = $Payload | ConvertTo-Json -Compress -Depth 8
            $request.Content = [Net.Http.StringContent]::new(
                $json,
                [Text.Encoding]::UTF8,
                "application/json")
        }
        $response = $client.SendAsync($request).GetAwaiter().GetResult()
        $body = $response.Content.ReadAsStringAsync().GetAwaiter().GetResult()
        if (-not $response.IsSuccessStatusCode) {
            throw "$ActionName returned HTTP $([int]$response.StatusCode)."
        }
        try { return $body | ConvertFrom-Json -ErrorAction Stop }
        catch { throw "$ActionName returned invalid JSON." }
    } finally {
        if ($response) { $response.Dispose() }
        $request.Dispose()
    }
}

$passwordBstr = [IntPtr]::Zero
$plainPassword = $null
$loginPayload = $null
try {
    try {
        if (-not [string]::IsNullOrEmpty($TestPassword)) {
            $plainPassword = $TestPassword
        } else {
            $passwordBstr = [Runtime.InteropServices.Marshal]::SecureStringToBSTR($Password)
            $plainPassword = [Runtime.InteropServices.Marshal]::PtrToStringBSTR($passwordBstr)
        }
        $loginPayload = @{ email = $Email.Trim(); password = $plainPassword }
        $login = Invoke-JsonRequest `
            -Method ([Net.Http.HttpMethod]::Post) `
            -RelativePath "/api/auth/login" `
            -Payload $loginPayload `
            -ActionName "Administrator login"
    } finally {
        $loginPayload = $null
        $plainPassword = $null
        $Password = $null
        $TestPassword = $null
        if ($passwordBstr -ne [IntPtr]::Zero) {
            [Runtime.InteropServices.Marshal]::ZeroFreeBSTR($passwordBstr)
        }
    }

    if ([string]::IsNullOrWhiteSpace($login.accessToken)) {
        throw "Administrator login did not return an access token."
    }
    if (@($login.user.roles) -notcontains "Admin") {
        throw "The authenticated account does not have the administrator role."
    }
    $client.DefaultRequestHeaders.Authorization =
        [Net.Http.Headers.AuthenticationHeaderValue]::new("Bearer", [string]$login.accessToken)
    $login = $null

    $robotsResponse = Invoke-JsonRequest `
        -Method ([Net.Http.HttpMethod]::Get) `
        -RelativePath "/api/admin/worktool/robots" `
        -ActionName "Robot list"
    $enabledRobots = @($robotsResponse | Where-Object { $_.isEnabled -eq $true })
    if ($enabledRobots.Count -eq 0) {
        throw "No enabled WorkTool robot configuration is available."
    }

    if ($enabledRobots.Count -eq 1) {
        $selectedRobot = $enabledRobots[0]
    } else {
        Write-Host "Enabled robots:"
        for ($index = 0; $index -lt $enabledRobots.Count; $index++) {
            Write-Host "  $($index + 1). $($enabledRobots[$index].name)"
        }
        if ($RobotSelection -eq 0) {
            $selectionText = Read-Host "Select a robot by number"
            if (-not [int]::TryParse($selectionText, [ref]$RobotSelection)) {
                throw "Robot selection must be a number."
            }
        }
        if ($RobotSelection -lt 1 -or $RobotSelection -gt $enabledRobots.Count) {
            throw "Robot selection is outside the available range."
        }
        $selectedRobot = $enabledRobots[$RobotSelection - 1]
    }

    $robotConfigId = [Guid]::Parse([string]$selectedRobot.id)
    Write-Host "Selected robot: $($selectedRobot.name)"

    $answer = if ($PSBoundParameters.ContainsKey("Confirmation")) {
        $Confirmation
    } else {
        Read-Host "Type UPDATE to confirm both callback configuration changes"
    }
    if ($answer -cne "UPDATE") {
        throw "Confirmation did not match; no callback configuration was changed."
    }

    if ($PSCmdlet.ShouldProcess(
        [string]$selectedRobot.name,
        "Configure WorkTool message and command-result callbacks")) {
        $robotPath = [uri]::EscapeDataString($robotConfigId.ToString("D"))
        $messageResult = Invoke-JsonRequest `
            -Method ([Net.Http.HttpMethod]::Post) `
            -RelativePath "/api/admin/worktool/robots/$robotPath/message-callback/configure" `
            -Payload @{ publicBaseUrl = $publicOrigin; replyAll = $true } `
            -ActionName "Message callback configuration"
        if ($messageResult.succeeded -isnot [bool] -or -not $messageResult.succeeded) {
            throw "Message callback configuration did not report success."
        }
        Write-Host "Message callback configuration accepted."

        $commandResult = Invoke-JsonRequest `
            -Method ([Net.Http.HttpMethod]::Post) `
            -RelativePath "/api/admin/worktool/robots/$robotPath/command-result-callback/configure" `
            -Payload @{ publicBaseUrl = $publicOrigin } `
            -ActionName "Command-result callback configuration"
        if ($commandResult.succeeded -isnot [bool] -or -not $commandResult.succeeded) {
            throw "Command-result callback configuration did not report success."
        }
        Write-Host "Command-result callback configuration accepted."
    }
} finally {
    $client.Dispose()
    $handler.Dispose()
}
