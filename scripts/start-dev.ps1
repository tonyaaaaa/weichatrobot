[CmdletBinding(SupportsShouldProcess)]
param(
    [string]$EnvFile = ".env",
    [string]$ApiUrl = "http://127.0.0.1:5268",
    [int]$WebPort = 5173,
    [switch]$SkipDependencies,
    [switch]$FakeRuntime,
    [ValidateSet("", "api", "worker", "web")][string]$FakeFailComponent = "",
    [ValidateRange(5, 180)][int]$StartupTimeoutSeconds = 60
)

$ErrorActionPreference = "Stop"
$repoRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot ".."))
$runtimeRoot = Join-Path $repoRoot ".dev"
$logRoot = Join-Path $runtimeRoot "logs"
$statePath = Join-Path $runtimeRoot "processes.json"
$heartbeatPath = Join-Path $runtimeRoot "worker.ready"
$resolvedEnv = if ([IO.Path]::IsPathRooted($EnvFile)) { $EnvFile } else { Join-Path $repoRoot $EnvFile }
$apiUri = [Uri]$ApiUrl
if (-not $apiUri.IsLoopback -or $apiUri.Scheme -ne "http" -or $apiUri.AbsolutePath -ne "/") {
    throw "ApiUrl must be a loopback HTTP origin."
}
$apiPort = $apiUri.Port
$apiOrigin = $apiUri.AbsoluteUri.TrimEnd("/")
$repositoryHash = [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData([Text.Encoding]::UTF8.GetBytes($repoRoot))).Substring(0, 16)
$repositoryMarker = "wechatrobot:$repositoryHash"
$runId = [Guid]::NewGuid().ToString("N")

function Get-OperationMutex {
    return [Threading.Mutex]::new($false, "Local\WechatRobot-dev-$repositoryHash")
}
function Import-EnvironmentFile([string]$Path) {
    if (-not (Test-Path -LiteralPath $Path)) { throw "Environment file not found: $Path. Copy .env.example to .env and set local-only values." }
    foreach ($line in [IO.File]::ReadAllLines($Path)) {
        $trimmed = $line.Trim()
        if (-not $trimmed -or $trimmed.StartsWith("#")) { continue }
        $parts = $trimmed.Split("=", 2)
        if ($parts.Count -ne 2) { throw "Invalid environment entry in $Path." }
        [Environment]::SetEnvironmentVariable($parts[0].Trim(), $parts[1].Trim(), "Process")
    }
}
function Require-Value([string]$Name) {
    if ([string]::IsNullOrWhiteSpace([Environment]::GetEnvironmentVariable($Name, "Process"))) { throw "Required local setting '$Name' is missing." }
}
function Save-State($Entries) {
    [IO.Directory]::CreateDirectory($runtimeRoot) | Out-Null
    $state = [pscustomobject]@{
        Version = 2
        RepositoryMarker = $repositoryMarker
        ApiUrl = $apiOrigin
        WebPort = $WebPort
        WebUrl = "http://127.0.0.1:$WebPort"
        FakeRuntime = [bool]$FakeRuntime
        HeartbeatPath = $heartbeatPath
        Processes = @($Entries)
    }
    [IO.File]::WriteAllText($statePath, ($state | ConvertTo-Json -Depth 6))
}
function Test-RecordedProcess($Entry) {
    if (-not $Entry.Pid -or -not $Entry.CreationTimeUtc -or -not $Entry.ExecutablePath -or -not $Entry.CommandMarker) { return $false }
    $cim = Get-CimInstance Win32_Process -Filter "ProcessId = $([int]$Entry.Pid)" -ErrorAction SilentlyContinue
    if (-not $cim -or [string]::IsNullOrWhiteSpace($cim.ExecutablePath) -or [string]::IsNullOrWhiteSpace($cim.CommandLine)) { return $false }
    $creation = $cim.CreationDate.ToUniversalTime().ToString("O")
    $recordedCreation = if ($Entry.CreationTimeUtc -is [datetime]) {
        $Entry.CreationTimeUtc.ToUniversalTime().ToString("O")
    }
    else {
        [datetimeoffset]::Parse(
            [string]$Entry.CreationTimeUtc,
            [Globalization.CultureInfo]::InvariantCulture,
            [Globalization.DateTimeStyles]::RoundtripKind
        ).UtcDateTime.ToString("O")
    }
    $expectedPath = [IO.Path]::GetFullPath([string]$Entry.ExecutablePath)
    $actualPath = [IO.Path]::GetFullPath([string]$cim.ExecutablePath)
    return $creation -ceq $recordedCreation -and
        $actualPath.Equals($expectedPath, [StringComparison]::OrdinalIgnoreCase) -and
        $cim.CommandLine.Contains([string]$Entry.CommandMarker, [StringComparison]::Ordinal)
}
function Get-StartedIdentity([Diagnostics.Process]$Process, [string]$Name, [string]$CommandMarker) {
    $deadline = [DateTime]::UtcNow.AddSeconds(3)
    do {
        $cim = Get-CimInstance Win32_Process -Filter "ProcessId = $($Process.Id)" -ErrorAction SilentlyContinue
        if ($cim -and -not [string]::IsNullOrWhiteSpace($cim.ExecutablePath) -and
            $cim.CommandLine.Contains($CommandMarker, [StringComparison]::Ordinal)) {
            return [pscustomobject]@{
                Name = $Name
                Pid = $Process.Id
                CreationTimeUtc = $cim.CreationDate.ToUniversalTime().ToString("O")
                ExecutablePath = [IO.Path]::GetFullPath([string]$cim.ExecutablePath)
                CommandMarker = $CommandMarker
            }
        }
        Start-Sleep -Milliseconds 50
    } while ([DateTime]::UtcNow -lt $deadline)
    throw "$Name process identity could not be verified after launch."
}
function Test-PortOpen([int]$Port) {
    $client = [Net.Sockets.TcpClient]::new()
    try { return $client.ConnectAsync("127.0.0.1", $Port).Wait(250) -and $client.Connected }
    catch { return $false }
    finally { $client.Dispose() }
}
function Stop-StartedProcesses($Entries) {
    foreach ($entry in @($Entries | Sort-Object { $_.Pid } -Descending)) {
        if (Test-RecordedProcess $entry) { Stop-Process -Id ([int]$entry.Pid) -Force -ErrorAction SilentlyContinue }
    }
    Remove-Item -LiteralPath $statePath -Force -ErrorAction SilentlyContinue
    Remove-Item -LiteralPath $heartbeatPath -Force -ErrorAction SilentlyContinue
}
function Wait-Http200([string]$Uri, [DateTime]$Deadline, $Entries) {
    while ([DateTime]::UtcNow -lt $Deadline) {
        foreach ($entry in $Entries) {
            if (-not (Test-RecordedProcess $entry)) { throw "$($entry.Name) exited during startup. Inspect .dev/logs." }
        }
        try {
            $response = Invoke-WebRequest -UseBasicParsing -Uri $Uri -TimeoutSec 2
            if ($response.StatusCode -eq 200) { return }
        } catch { }
        Start-Sleep -Milliseconds 200
    }
    throw "Timed out waiting for HTTP 200 from $Uri."
}
function Wait-Heartbeat([DateTime]$Deadline, $Entries) {
    while ([DateTime]::UtcNow -lt $Deadline) {
        foreach ($entry in $Entries) {
            if (-not (Test-RecordedProcess $entry)) { throw "$($entry.Name) exited during startup. Inspect .dev/logs." }
        }
        if (Test-Path -LiteralPath $heartbeatPath) {
            $stamp = [DateTimeOffset]::Parse([IO.File]::ReadAllText($heartbeatPath))
            if ([DateTimeOffset]::UtcNow - $stamp -lt [TimeSpan]::FromSeconds(5)) { return }
        }
        Start-Sleep -Milliseconds 200
    }
    throw "Timed out waiting for a fresh Worker heartbeat."
}

if ($WhatIfPreference) {
    Write-Host "WHATIF: acquire the repository operation lock, validate ports/tools/configuration, start dependencies, build/migrate, launch API/Worker/Vite, and poll readiness."
    Write-Host "Liveness: $ApiUrl/health/live"
    Write-Host "Authenticated readiness: $ApiUrl/api/admin/health/ready"
    return
}

$mutex = Get-OperationMutex
$ownsMutex = $false
$entries = @()
try {
    try { $ownsMutex = $mutex.WaitOne([TimeSpan]::FromSeconds($StartupTimeoutSeconds)) }
    catch [Threading.AbandonedMutexException] { $ownsMutex = $true }
    if (-not $ownsMutex) { throw "Another start/stop operation for this checkout is still running." }

    if (Test-Path -LiteralPath $statePath) {
        $state = Get-Content -LiteralPath $statePath -Raw | ConvertFrom-Json
        if ($state.Version -ne 2 -or $state.RepositoryMarker -cne $repositoryMarker) {
            throw "Existing development manifest has an unsupported or foreign ownership schema; inspect it before starting."
        }
        $recorded = @($state.Processes)
        $alive = @($recorded | Where-Object { Test-RecordedProcess $_ })
        $configurationMatches =
            [string]$state.ApiUrl -ceq $apiOrigin -and
            [int]$state.WebPort -eq $WebPort -and
            [bool]$state.FakeRuntime -eq [bool]$FakeRuntime
        if ($alive.Count -gt 0 -and -not $configurationMatches) {
            throw "Existing development process configuration does not match the requested API URL, Web port, or runtime mode. Stop it before changing endpoints."
        }
        if ($alive.Count -eq $recorded.Count -and $recorded.Count -eq 3) {
            $deadline = [DateTime]::UtcNow.AddSeconds($StartupTimeoutSeconds)
            Wait-Http200 "$apiOrigin/health/live" $deadline $recorded
            Wait-Http200 "http://127.0.0.1:$WebPort/" $deadline $recorded
            Wait-Heartbeat $deadline $recorded
            Write-Host "WechatRobot development processes are already running."
            Write-Host "Liveness: $apiOrigin/health/live"
            Write-Host "Authenticated readiness: $apiOrigin/api/admin/health/ready"
            return
        }
        if ($alive.Count -gt 0) { throw "The recorded process set is only partially running. Run scripts/stop-dev.ps1 before starting again." }
        Remove-Item -LiteralPath $statePath -Force
    }
    foreach ($port in @($apiPort, $WebPort)) {
        if (Test-PortOpen $port) { throw "Required local port $port is already in use." }
    }

    if (-not $FakeRuntime) {
        Import-EnvironmentFile $resolvedEnv
        foreach ($name in @("MYSQL_DATABASE", "MYSQL_USER", "MYSQL_PASSWORD", "MYSQL_PORT", "QDRANT_HTTP_PORT", "QDRANT_API_KEY",
            "WECHATROBOT_MASTER_KEY_BASE64", "JWT_SIGNING_KEY", "OSS_ACCESS_KEY_ID", "OSS_ACCESS_KEY_SECRET", "OSS_BUCKET", "OSS_ENDPOINT", "OSS_PUBLIC_BASE_URL")) {
            Require-Value $name
        }
        $decodedMasterKey = [Convert]::FromBase64String($env:WECHATROBOT_MASTER_KEY_BASE64)
        if ($decodedMasterKey.Length -ne 32) { throw "WECHATROBOT_MASTER_KEY_BASE64 must decode to exactly 32 bytes." }
        if ($env:JWT_SIGNING_KEY.Length -lt 32) { throw "JWT_SIGNING_KEY must contain at least 32 characters." }
        foreach ($command in @("dotnet", "npm")) {
            if (-not (Get-Command $command -ErrorAction SilentlyContinue)) { throw "$command is required." }
        }
        if (-not $SkipDependencies) {
            if (-not (Get-Command docker -ErrorAction SilentlyContinue)) { throw "Docker is required." }
            docker info *> $null
            if ($LASTEXITCODE -ne 0) { throw "Docker Desktop is not available." }
            & docker compose --env-file $resolvedEnv -p wechatrobot-dev up -d --wait --wait-timeout 180 mysql qdrant
            if ($LASTEXITCODE -ne 0) { throw "Required Docker dependencies did not become healthy." }
            & docker compose --env-file $resolvedEnv -p wechatrobot-dev up -d ocr
            if ($LASTEXITCODE -ne 0) { throw "Optional OCR dependency could not be started." }
        }
        $env:ConnectionStrings__WechatRobot = "Server=127.0.0.1;Port=$($env:MYSQL_PORT);Database=$($env:MYSQL_DATABASE);User Id=$($env:MYSQL_USER);Password=$($env:MYSQL_PASSWORD)"
        $env:Cors__AllowedOrigins__0 = "http://127.0.0.1:$WebPort"
        $env:Jwt__Issuer = "wechatrobot-local"
        $env:Jwt__Audience = "wechatrobot-admin-local"
        $env:Jwt__SigningKey = $env:JWT_SIGNING_KEY
        $env:Qdrant__BaseUrl = "http://127.0.0.1:$($env:QDRANT_HTTP_PORT)/"
        $env:Qdrant__ApiKey = $env:QDRANT_API_KEY
        $env:Ocr__BaseAddress = "http://127.0.0.1:$($env:OCR_PORT ?? '18000')/"
        $env:Oss__AccessKeyId = $env:OSS_ACCESS_KEY_ID
        $env:Oss__AccessKeySecret = $env:OSS_ACCESS_KEY_SECRET
        $env:Oss__Bucket = $env:OSS_BUCKET
        $env:Oss__Endpoint = $env:OSS_ENDPOINT
        $env:Oss__PublicBaseUrl = $env:OSS_PUBLIC_BASE_URL
        $env:Health__HeartbeatReadyFile = $heartbeatPath
    }

    Push-Location $repoRoot
    try {
        if (-not $FakeRuntime) {
            dotnet build WechatRobot.slnx --nologo
            if ($LASTEXITCODE -ne 0) { throw "Server build failed." }
            dotnet ef database update --project src/server/WechatRobot.Infrastructure --startup-project src/server/WechatRobot.Api --no-build
            if ($LASTEXITCODE -ne 0) { throw "Database migration failed." }
        }
        [IO.Directory]::CreateDirectory($logRoot) | Out-Null
        Remove-Item -LiteralPath $heartbeatPath -Force -ErrorAction SilentlyContinue
        $webRoot = Join-Path $repoRoot "src/web/wechatrobot-admin"
        if ($FakeRuntime) {
            $shell = (Get-Command pwsh -ErrorAction Stop).Source
            $fake = Join-Path $repoRoot "tests/operations/fake-dev-service.ps1"
            $specs = @(
                @{ Name = "api"; File = $shell; Args = @("-NoProfile", "-File", $fake, "-Kind", "api", "-Port", "$apiPort") },
                @{ Name = "worker"; File = $shell; Args = @("-NoProfile", "-File", $fake, "-Kind", "worker", "-HeartbeatPath", $heartbeatPath) },
                @{ Name = "web"; File = $shell; Args = @("-NoProfile", "-File", $fake, "-Kind", "web", "-Port", "$WebPort") }
            )
        } else {
            $node = (Get-Command node -ErrorAction Stop).Source
            $specs = @(
                @{ Name = "api"; File = (Join-Path $repoRoot "src/server/WechatRobot.Api/bin/Debug/net10.0/WechatRobot.Api.exe"); Args = @("--urls", $ApiUrl) },
                @{ Name = "worker"; File = (Join-Path $repoRoot "src/server/WechatRobot.Worker/bin/Debug/net10.0/WechatRobot.Worker.exe"); Args = @() },
                @{ Name = "web"; File = $node; Args = @((Join-Path $webRoot "node_modules/vite/bin/vite.js"), "--host", "127.0.0.1", "--port", "$WebPort", "--strictPort") }
            )
        }
        foreach ($spec in $specs) {
            $commandMarker = "${repositoryMarker}:$($spec.Name):$runId"
            if ($FakeRuntime) {
                $spec.Args += @("-RepositoryMarker", $commandMarker)
            } elseif ($spec.Name -eq "web") {
                $spec.Args = @("--title=$commandMarker") + $spec.Args
            } else {
                $spec.Args += "--WechatRobotProcessMarker=$commandMarker"
            }
            if ($FakeRuntime -and $FakeFailComponent -eq $spec.Name) { $spec.Args += "-FailImmediately" }
            $process = Start-Process -FilePath $spec.File -ArgumentList $spec.Args -WorkingDirectory $(if ($spec.Name -eq "web") { $webRoot } else { $repoRoot }) `
                -PassThru -WindowStyle Hidden -RedirectStandardOutput (Join-Path $logRoot "$($spec.Name).stdout.log") `
                -RedirectStandardError (Join-Path $logRoot "$($spec.Name).stderr.log")
            $entries += Get-StartedIdentity $process $spec.Name $commandMarker
            Save-State $entries
        }
        $deadline = [DateTime]::UtcNow.AddSeconds($StartupTimeoutSeconds)
        Wait-Http200 "$apiOrigin/health/live" $deadline $entries
        Wait-Http200 "http://127.0.0.1:$WebPort/" $deadline $entries
        Wait-Heartbeat $deadline $entries
    } finally { Pop-Location }
} catch {
    if ($entries.Count -gt 0) { Stop-StartedProcesses $entries }
    throw
} finally {
    if ($ownsMutex) { $mutex.ReleaseMutex() }
    $mutex.Dispose()
}

Write-Host "WechatRobot development stack started and ready."
Write-Host "Liveness: $apiOrigin/health/live"
Write-Host "Authenticated readiness: $apiOrigin/api/admin/health/ready"
Write-Host "Admin UI: http://127.0.0.1:$WebPort/"
Write-Host "Logs: $logRoot"
