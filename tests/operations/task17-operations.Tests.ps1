param([switch]$CallbackOnly)

$ErrorActionPreference = "Stop"
$repoRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot "../.."))
$callbackScript = Join-Path $repoRoot "scripts/update-worktool-callback.ps1"

function Assert-True([bool]$Condition, [string]$Message) {
    if (-not $Condition) { throw "ASSERTION FAILED: $Message" }
}

function Invoke-Captured([scriptblock]$Action) {
    $oldPreference = $InformationPreference
    $InformationPreference = "Continue"
    try { return (& $Action 6>&1 | Out-String) }
    finally { $InformationPreference = $oldPreference }
}

$robotConfigId = [Guid]::NewGuid()
$bearerToken = "fake-operations-bearer-token"
$preview = Invoke-Captured {
    & $callbackScript `
        -ApiBaseUrl "https://admin.example.test/" `
        -RobotConfigId $robotConfigId `
        -PublicBaseUrl "https://callbacks.example.test/" `
        -BearerToken $bearerToken
}
Assert-True ($preview -match [regex]::Escape($robotConfigId.ToString("D"))) "preview must identify only the internal robot configuration"
Assert-True ($preview -match "configure message callback and command-result callback") "preview must name both callback actions"
Assert-True ($preview -notmatch [regex]::Escape($bearerToken)) "preview must never print the bearer token"

$badBaseFailed = $false
try {
    & $callbackScript -ApiBaseUrl "https://admin.example.test/base?x=1" -RobotConfigId $robotConfigId `
        -PublicBaseUrl "https://callbacks.example.test/" -BearerToken $bearerToken | Out-Null
} catch { $badBaseFailed = $true }
Assert-True $badBaseFailed "API base URL with path/query must be rejected"

$port = Get-Random -Minimum 22000 -Maximum 42000
$listenerJob = Start-Job -ScriptBlock {
    param($Port)
    $listener = [Net.HttpListener]::new()
    $listener.Prefixes.Add("http://127.0.0.1:$Port/")
    $listener.Start()
    try {
        1..2 | ForEach-Object {
            $context = $listener.GetContext()
            $reader = [IO.StreamReader]::new($context.Request.InputStream, $context.Request.ContentEncoding)
            $body = $reader.ReadToEnd()
            $reader.Dispose()
            $path = $context.Request.Url.AbsolutePath
            $authorization = $context.Request.Headers["Authorization"]
            $responseBytes = [Text.Encoding]::UTF8.GetBytes('{"succeeded":true}')
            $context.Response.StatusCode = 200
            $context.Response.ContentType = "application/json"
            $context.Response.OutputStream.Write($responseBytes, 0, $responseBytes.Length)
            $context.Response.Close()
            [pscustomobject]@{
                Path = $path
                Authorization = $authorization
                Body = $body
            }
        }
    } finally { $listener.Stop() }
} -ArgumentList $port
try {
    Start-Sleep -Milliseconds 1000
    $applyOutput = Invoke-Captured {
        & $callbackScript -ApiBaseUrl "http://127.0.0.1:$port/" -RobotConfigId $robotConfigId `
            -PublicBaseUrl "https://callbacks.example.test/" -BearerToken $bearerToken `
            -Apply -Confirmation "APPLY"
    }
    $received = @(Receive-Job -Job $listenerJob -Wait)
    Assert-True ($received.Count -eq 2) "apply must call exactly two admin endpoints"
    Assert-True ($received[0].Path -eq "/api/admin/worktool/robots/$($robotConfigId.ToString('D'))/message-callback/configure") "first call must configure the message callback"
    Assert-True ($received[1].Path -eq "/api/admin/worktool/robots/$($robotConfigId.ToString('D'))/command-result-callback/configure") "second call must configure the command-result callback"
    Assert-True (@($received | Where-Object { $_.Authorization -ne "Bearer $bearerToken" }).Count -eq 0) "bearer token must be sent only in the Authorization header"
    Assert-True (@($received | Where-Object { $_.Body -match [regex]::Escape($bearerToken) }).Count -eq 0) "request bodies must not contain the bearer token"
    Assert-True ($applyOutput -notmatch [regex]::Escape($bearerToken)) "apply output must not contain the bearer token"
    Assert-True ($applyOutput -match "Command-result callback configuration accepted") "both callback configurations must be accepted"
} finally {
    Remove-Job -Job $listenerJob -Force -ErrorAction SilentlyContinue
}

$port = Get-Random -Minimum 22000 -Maximum 42000
$failureJob = Start-Job -ScriptBlock {
    param($Port)
    $listener = [Net.HttpListener]::new()
    $listener.Prefixes.Add("http://127.0.0.1:$Port/")
    $listener.Start()
    try {
        $context = $listener.GetContext()
        $bytes = [Text.Encoding]::UTF8.GetBytes('{"succeeded":false}')
        $context.Response.StatusCode = 200
        $context.Response.ContentType = "application/json"
        $context.Response.OutputStream.Write($bytes, 0, $bytes.Length)
        $context.Response.Close()
    } finally { $listener.Stop() }
} -ArgumentList $port
try {
    Start-Sleep -Milliseconds 1000
    $rejected = $false
    try {
        & $callbackScript -ApiBaseUrl "http://127.0.0.1:$port/" -RobotConfigId $robotConfigId `
            -PublicBaseUrl "https://callbacks.example.test/" -BearerToken $bearerToken `
            -Apply -Confirmation "APPLY" | Out-Null
    } catch { $rejected = $_.Exception.Message -match "did not report success" }
    Assert-True $rejected "HTTP 2xx without business success must be rejected"
    Receive-Job -Job $failureJob -Wait | Out-Null
} finally {
    Remove-Job -Job $failureJob -Force -ErrorAction SilentlyContinue
}

Write-Host "PASS callback fake-runtime acceptance"
if ($CallbackOnly) { return }

$startScript = Join-Path $repoRoot "scripts/start-dev.ps1"
$stopScript = Join-Path $repoRoot "scripts/stop-dev.ps1"
$statePath = Join-Path $repoRoot ".dev/processes.json"
function Test-PortOpen([int]$Port) {
    $client = [Net.Sockets.TcpClient]::new()
    try { return $client.ConnectAsync("127.0.0.1", $Port).Wait(500) -and $client.Connected }
    catch { return $false }
    finally { $client.Dispose() }
}
function New-PortPair {
    while ($true) {
        $api = Get-Random -Minimum 22000 -Maximum 40000
        $web = $api + 1
        if (-not (Test-PortOpen $api) -and -not (Test-PortOpen $web)) { return @($api, $web) }
    }
}
function Invoke-FakeStart([int]$ApiPort, [int]$WebPort, [string]$Fail = "") {
    & $startScript -FakeRuntime -ApiUrl "http://127.0.0.1:$ApiPort" -WebPort $WebPort `
        -StartupTimeoutSeconds 10 -FakeFailComponent $Fail
}

& $stopScript -ErrorAction SilentlyContinue | Out-Null
$ports = New-PortPair
try {
    Invoke-FakeStart $ports[0] $ports[1] | Out-Null
    $firstState = Get-Content -Raw $statePath | ConvertFrom-Json
    $first = @($firstState.Processes)
    Assert-True ($firstState.Version -eq 2) "manifest must use the exact-identity schema"
    Assert-True ($firstState.ApiUrl -eq "http://127.0.0.1:$($ports[0])") "manifest must persist the API origin"
    Assert-True ($firstState.WebPort -eq $ports[1]) "manifest must persist the Web port"
    Assert-True ($first.Count -eq 3) "first start must record exactly API, Worker, and Web"
    Invoke-FakeStart $ports[0] $ports[1] | Out-Null
    $second = @((Get-Content -Raw $statePath | ConvertFrom-Json).Processes)
    Assert-True (($first.Pid -join ",") -eq ($second.Pid -join ",")) "second start must reuse the same processes"
    $mismatchFailed = $false
    try { Invoke-FakeStart $ports[0] ($ports[1] + 10) | Out-Null }
    catch { $mismatchFailed = $_.Exception.Message -match "configuration does not match" }
    Assert-True $mismatchFailed "running manifest with different requested endpoints must fail clearly"
    Assert-True (Test-Path $statePath) "configuration mismatch must preserve the running manifest"
    Assert-True ((Invoke-WebRequest -UseBasicParsing "http://127.0.0.1:$($ports[0])/health/live").StatusCode -eq 200) "configuration mismatch must leave the original API running"
    & $stopScript | Out-Null
    & $stopScript | Out-Null
    Assert-True (-not (Test-Path $statePath)) "stop x2 must leave no manifest"
} finally { & $stopScript -ErrorAction SilentlyContinue | Out-Null }
Write-Host "PASS start x2 / stop x2"

$ports = New-PortPair
$runRoot = Join-Path $repoRoot ".dev/test-runs"
[IO.Directory]::CreateDirectory($runRoot) | Out-Null
$shell = (Get-Command pwsh).Source
$common = @("-NoProfile", "-File", $startScript, "-FakeRuntime", "-ApiUrl", "http://127.0.0.1:$($ports[0])", "-WebPort", "$($ports[1])", "-StartupTimeoutSeconds", "15")
try {
    $p1 = Start-Process $shell -ArgumentList $common -PassThru -WindowStyle Hidden `
        -RedirectStandardOutput (Join-Path $runRoot "concurrent-1.out") -RedirectStandardError (Join-Path $runRoot "concurrent-1.err")
    $p2 = Start-Process $shell -ArgumentList $common -PassThru -WindowStyle Hidden `
        -RedirectStandardOutput (Join-Path $runRoot "concurrent-2.out") -RedirectStandardError (Join-Path $runRoot "concurrent-2.err")
    $p1.WaitForExit(30000) | Out-Null
    $p2.WaitForExit(30000) | Out-Null
    Assert-True ($p1.ExitCode -eq 0 -and $p2.ExitCode -eq 0) "both concurrent starts must converge successfully"
    $state = @((Get-Content -Raw $statePath | ConvertFrom-Json).Processes)
    Assert-True ($state.Count -eq 3) "concurrent starts must create one three-process manifest"
} finally { & $stopScript -ErrorAction SilentlyContinue | Out-Null }
Write-Host "PASS concurrent start serialization"

$ports = New-PortPair
$blocker = Start-Job -ScriptBlock {
    param($Port)
    $listener = [Net.Sockets.TcpListener]::new([Net.IPAddress]::Loopback, $Port)
    $listener.Start()
    try { Start-Sleep -Seconds 20 } finally { $listener.Stop() }
} -ArgumentList $ports[0]
try {
    Start-Sleep -Milliseconds 400
    $failed = $false
    try { Invoke-FakeStart $ports[0] $ports[1] | Out-Null } catch { $failed = $_.Exception.Message -match "already in use" }
    Assert-True $failed "occupied API port must fail before launching children"
    Assert-True (-not (Test-Path $statePath)) "port conflict must not leave a manifest"
} finally {
    Stop-Job $blocker -ErrorAction SilentlyContinue
    Remove-Job $blocker -Force -ErrorAction SilentlyContinue
}
Write-Host "PASS port conflict preflight"

$ports = New-PortPair
try {
    $failed = $false
    try { Invoke-FakeStart $ports[0] $ports[1] "web" | Out-Null } catch { $failed = $true }
    Assert-True $failed "component startup failure must fail the start operation"
    Start-Sleep -Milliseconds 300
    Assert-True (-not (Test-Path $statePath)) "startup failure must remove the manifest"
    Assert-True (-not (Test-PortOpen $ports[0]) -and -not (Test-PortOpen $ports[1])) "startup failure must stop this run's listeners"
} finally { & $stopScript -ErrorAction SilentlyContinue | Out-Null }
Write-Host "PASS startup failure cleanup"

$foreignMarker = "foreign-process-" + [Guid]::NewGuid().ToString("N")
$foreignHeartbeat = Join-Path $repoRoot ".dev/foreign.ready"
$foreign = Start-Process $shell -ArgumentList @("-NoProfile", "-File", (Join-Path $repoRoot "tests/operations/fake-dev-service.ps1"),
    "-Kind", "worker", "-HeartbeatPath", $foreignHeartbeat, "-RepositoryMarker", $foreignMarker) -PassThru -WindowStyle Hidden
try {
    Start-Sleep -Milliseconds 400
    $cim = Get-CimInstance Win32_Process -Filter "ProcessId = $($foreign.Id)"
    $repoHash = [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData([Text.Encoding]::UTF8.GetBytes($repoRoot))).Substring(0, 16)
    $forged = [pscustomobject]@{
        Version = 2
        RepositoryMarker = "wechatrobot:$repoHash"
        ApiUrl = "http://127.0.0.1:1"
        WebPort = 2
        FakeRuntime = $true
        Processes = @([pscustomobject]@{
            Name = "worker"
            Pid = $foreign.Id
            CreationTimeUtc = $cim.CreationDate.ToUniversalTime().ToString("O")
            ExecutablePath = $cim.ExecutablePath
            CommandMarker = "wechatrobot:$repoHash`:worker:forged"
        })
    }
    [IO.File]::WriteAllText($statePath, ($forged | ConvertTo-Json -Depth 6))
    & $stopScript | Out-Null
    Assert-True ($null -ne (Get-Process -Id $foreign.Id -ErrorAction SilentlyContinue)) "stale/reused PID identity mismatch must never be stopped"
    Assert-True (-not (Test-Path $statePath)) "stale manifest must be removed after safe refusal"
} finally {
    Stop-Process -Id $foreign.Id -Force -ErrorAction SilentlyContinue
    Remove-Item $foreignHeartbeat -Force -ErrorAction SilentlyContinue
    Remove-Item $statePath -Force -ErrorAction SilentlyContinue
}
Write-Host "PASS stale/reused PID ownership refusal"

Write-Host "PASS all Task17 local operation acceptance tests"
