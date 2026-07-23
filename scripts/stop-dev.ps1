[CmdletBinding(SupportsShouldProcess)]
param([ValidateRange(5, 180)][int]$OperationTimeoutSeconds = 60)

$ErrorActionPreference = "Stop"
$repoRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot ".."))
$runtimeRoot = Join-Path $repoRoot ".dev"
$statePath = Join-Path $runtimeRoot "processes.json"
$heartbeatPath = Join-Path $runtimeRoot "worker.ready"
$hash = [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData([Text.Encoding]::UTF8.GetBytes($repoRoot))).Substring(0, 16)

if ($WhatIfPreference) {
    Write-Host "WHATIF: acquire the repository operation lock and stop only exact process identities recorded in $statePath."
    return
}

$mutex = [Threading.Mutex]::new($false, "Local\WechatRobot-dev-$hash")
$ownsMutex = $false
try {
    try { $ownsMutex = $mutex.WaitOne([TimeSpan]::FromSeconds($OperationTimeoutSeconds)) }
    catch [Threading.AbandonedMutexException] { $ownsMutex = $true }
    if (-not $ownsMutex) { throw "Another start/stop operation for this checkout is still running." }
    if (-not (Test-Path -LiteralPath $statePath)) {
        Write-Host "No repository-recorded development processes are running."
        return
    }
    $state = Get-Content -LiteralPath $statePath -Raw | ConvertFrom-Json
    if ($state.Version -ne 2 -or $state.RepositoryMarker -cne "wechatrobot:$hash") {
        throw "Development manifest has an unsupported or foreign ownership schema; no process was stopped."
    }
    $entries = @($state.Processes)
    function Test-ExactOwnership($Entry) {
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
            ([string]$Entry.CommandMarker).StartsWith("wechatrobot:$hash`:", [StringComparison]::Ordinal) -and
            $cim.CommandLine.Contains([string]$Entry.CommandMarker, [StringComparison]::Ordinal)
    }
    foreach ($entry in $entries) {
        $process = Get-Process -Id ([int]$entry.Pid) -ErrorAction SilentlyContinue
        if (-not $process) { continue }
        if (-not (Test-ExactOwnership $entry)) {
            Write-Warning "PID $($entry.Pid) identity does not exactly match this repository manifest; it was not stopped."
            continue
        }
        if ($PSCmdlet.ShouldProcess("$($entry.Pid) ($($entry.Name))", "Stop exact repository-recorded process")) {
            Stop-Process -Id ([int]$entry.Pid) -Force -ErrorAction Stop
        }
    }
    Remove-Item -LiteralPath $statePath -Force
    Remove-Item -LiteralPath $heartbeatPath -Force -ErrorAction SilentlyContinue
    Write-Host "Stopped repository-recorded development processes. Docker dependencies were left running."
} finally {
    if ($ownsMutex) { $mutex.ReleaseMutex() }
    $mutex.Dispose()
}
