# EsoAgent update applier (Windows). Invoked DETACHED by the agent's UpdateService:
#   update-apply.ps1 <extractedPackageDir> <appDir>
# Stops the service, swaps binaries keeping a timestamped backup, restarts, health-checks
# /api/status on loopback (any HTTP response = the new build is up), rolls back on failure.
param([string]$ExtractedDir, [string]$AppDir)
$ErrorActionPreference = "Continue"
$logFile = Join-Path $AppDir "update-apply.log"
function Log($msg) { Add-Content $logFile "$(Get-Date -Format o) $msg" }
Log "apply started: $ExtractedDir -> $AppDir"

$backupDir = Join-Path $AppDir ("backup-" + (Get-Date -Format "yyyyMMddHHmmss"))
New-Item -ItemType Directory -Force -Path $backupDir | Out-Null

Stop-Service -Name "EsoAgent" -Force -ErrorAction SilentlyContinue
Start-Sleep -Seconds 2

# Back up current binaries, then swap in the new ones.
$binaries = @("EsoAgent.Windows.exe", "EsoAgent.Tray.exe")
foreach ($bin in $binaries) {
    $current = Join-Path $AppDir $bin
    if (Test-Path $current) { Copy-Item $current $backupDir -Force }
    $new = Join-Path $ExtractedDir $bin
    if (Test-Path $new) { Copy-Item $new $AppDir -Force }
}

Start-Service -Name "EsoAgent" -ErrorAction SilentlyContinue

# Health check: any HTTP response from /api/status proves the new build serves.
$port = 8737
try {
    $cfgPath = Join-Path $AppDir "data\esoagent.json"
    if (Test-Path $cfgPath) {
        $cfg = Get-Content $cfgPath -Raw | ConvertFrom-Json
        if ($cfg.port) { $port = [int]$cfg.port }
    }
} catch { }
$healthy = $false
for ($i = 0; $i -lt 30; $i++) {
    Start-Sleep -Seconds 1
    try {
        [System.Net.ServicePointManager]::ServerCertificateValidationCallback = { $true }
        $req = [System.Net.WebRequest]::Create("https://127.0.0.1:$port/api/status")
        $req.Timeout = 2000
        $res = $req.GetResponse(); $res.Close(); $healthy = $true; break
    } catch [System.Net.WebException] {
        if ($_.Exception.Response) { $healthy = $true; break } # any HTTP response (401/403) = up
    } catch { }
}

if ($healthy) {
    Log "health check OK - update applied; backup kept at $backupDir"
} else {
    Log "health check FAILED - rolling back from $backupDir"
    Stop-Service -Name "EsoAgent" -Force -ErrorAction SilentlyContinue
    Start-Sleep -Seconds 2
    foreach ($bin in $binaries) {
        $backup = Join-Path $backupDir $bin
        if (Test-Path $backup) { Copy-Item $backup $AppDir -Force }
    }
    Start-Service -Name "EsoAgent" -ErrorAction SilentlyContinue
    Log "rollback complete"
}
