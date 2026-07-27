param(
    [Parameter(Mandatory = $true)]
    [string]$LogPath,
    [int]$Port = 5588
)

$ErrorActionPreference = "Stop"
$listener = [System.Net.HttpListener]::new()
$listener.Prefixes.Add("http://127.0.0.1:$Port/")
$messageCallbackUrl = $null
$replyAll = 0
$eventCallbacks = @{}
$commandSequence = 0

function Write-JsonResponse {
    param(
        [Parameter(Mandatory = $true)]$Context,
        [Parameter(Mandatory = $true)]$Value,
        [int]$StatusCode = 200
    )
    $json = $Value | ConvertTo-Json -Depth 8 -Compress
    $bytes = [System.Text.Encoding]::UTF8.GetBytes($json)
    $Context.Response.StatusCode = $StatusCode
    $Context.Response.ContentType = "application/json"
    $Context.Response.ContentLength64 = $bytes.Length
    $Context.Response.OutputStream.Write($bytes, 0, $bytes.Length)
    $Context.Response.Close()
}

function Post-CommandResult {
    param(
        [Parameter(Mandatory = $true)][string]$CallbackUrl,
        [Parameter(Mandatory = $true)][string]$MessageId
    )
    $payload = @{
        messageId = $MessageId
        errorCode = 0
        errorReason = ""
        runTime = [DateTimeOffset]::UtcNow.ToUnixTimeMilliseconds()
        timeCost = 0.01
        type = 1
        successList = @()
        failList = @()
    } | ConvertTo-Json -Compress
    try {
        Invoke-WebRequest -UseBasicParsing -Method Post -Uri $CallbackUrl -ContentType "application/json" -Body $payload | Out-Null
    } catch {
        [System.IO.File]::AppendAllText($LogPath, "$([DateTime]::UtcNow.ToString('O')) RESULT_CALLBACK_FAILED`n")
    }
}

$listener.Start()
try {
    while ($listener.IsListening) {
        $context = $listener.GetContext()
        $request = $context.Request
        $reader = [System.IO.StreamReader]::new($request.InputStream, $request.ContentEncoding)
        $body = $reader.ReadToEnd()
        $reader.Dispose()
        $path = $request.Url.AbsolutePath
        $loggedBody = if ($path -in @(
            "/robot/robotInfo/update",
            "/robot/robotInfo/callBack/bind")) { "[callback configuration redacted]" } else { $body }
        [System.IO.File]::AppendAllText(
            $LogPath,
            ("{0} {1} {2}`n{3}`n" -f [DateTime]::UtcNow.ToString("O"), $request.HttpMethod, $request.RawUrl, $loggedBody))

        switch ($path) {
            "/robot/robotInfo/get" {
                Write-JsonResponse $context @{
                    code = 200
                    message = "fake ok"
                    data = @{
                        robotId = $request.QueryString["robotId"]
                        openCallback = if ($messageCallbackUrl) { 1 } else { 0 }
                        replyAll = $replyAll
                    }
                }
            }
            "/robot/robotInfo/online" {
                Write-JsonResponse $context @{ code = 200; message = "fake ok"; data = @{ online = $true } }
            }
            "/robot/robotInfo/update" {
                $payload = $body | ConvertFrom-Json
                $messageCallbackUrl = [string]$payload.callbackUrl
                $replyAll = [int]$payload.replyAll
                Write-JsonResponse $context @{ code = 0; message = "fake configured"; data = $null }
            }
            "/robot/robotInfo/callBack/get" {
                $callbacks = @($eventCallbacks.GetEnumerator() | Sort-Object Name | ForEach-Object {
                    @{ id = [long]$_.Name; type = [int]$_.Name; callBackUrl = [string]$_.Value; typeName = "fake event callback" }
                })
                Write-JsonResponse $context @{ code = 0; message = "fake ok"; data = $callbacks }
            }
            "/robot/robotInfo/callBack/bind" {
                $payload = $body | ConvertFrom-Json
                $eventCallbacks[[int]$payload.type] = [string]$payload.callBackUrl
                Write-JsonResponse $context @{ code = 0; message = "fake bound"; data = $null }
            }
            "/robot/robotInfo/callBack/deleteByType" {
                $payload = $body | ConvertFrom-Json
                $eventCallbacks.Remove([int]$payload.type)
                Write-JsonResponse $context @{ code = 0; message = "fake deleted"; data = $null }
            }
            "/wework/sendRawMessage" {
                $commandSequence++
                $messageId = "fake-command-{0:D6}" -f $commandSequence
                Write-JsonResponse $context @{ code = 0; message = "fake accepted"; data = $messageId }
                if ($eventCallbacks.ContainsKey(1)) {
                    Post-CommandResult -CallbackUrl $eventCallbacks[1] -MessageId $messageId
                }
            }
            "/fake/inbound" {
                if (-not $messageCallbackUrl) {
                    Write-JsonResponse $context @{ code = 409; message = "message callback is not configured"; data = $null } 409
                    continue
                }
                try {
                    $forward = Invoke-WebRequest -UseBasicParsing -Method Post -Uri $messageCallbackUrl -ContentType "application/json" -Body $body
                    Write-JsonResponse $context ($forward.Content | ConvertFrom-Json) ([int]$forward.StatusCode)
                } catch {
                    Write-JsonResponse $context @{ code = 502; message = "fake callback forwarding failed"; data = $null } 502
                }
            }
            default {
                Write-JsonResponse $context @{ code = 404; message = "fake route not found"; data = $null } 404
            }
        }
    }
}
finally {
    $listener.Close()
}
