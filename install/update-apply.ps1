# EsoAgent update applier (Windows). Invoked DETACHED by the agent's UpdateService
# (the copy that ships inside the package - UpdateService prefers it over the
# installed one, because the script must match the package's layout):
#   update-apply.ps1 <extractedPackageDir> <appDir>
# Stops the service, swaps the payload keeping a timestamped backup, restarts,
# health-checks /api/status on loopback (any HTTP response = the new build is
# up), rolls back on failure.
#
# The payload is a FOLDER (installer diet, BUILD-REPORT §19): one shared
# runtime laid out once, with an apphost exe per program. The swap unit is
# therefore the whole install dir EXCEPT the device data (data\), previous
# backups (backup-*\) and this log. The tray and the desktop app hold
# payload files open while they run, so both are closed first - the tray
# returns at next logon, the app when the user reopens it (the same
# behaviour the Inno upgrade path has always had).
param([string]$ExtractedDir, [string]$AppDir)
$ErrorActionPreference = "Continue"
$logFile = Join-Path $AppDir "update-apply.log"
function Log($msg) { Add-Content $logFile "$(Get-Date -Format o) $msg" }
Log "apply started: $ExtractedDir -> $AppDir"

function Test-IsPayloadFile([string]$FullPath, [string]$Root) {
    $rel = $FullPath.Substring($Root.Length).TrimStart('\', '/')
    if ($rel -like "data\*" -or $rel -like "backup-*\*") { return $false }
    if ($rel -eq "update-apply.log") { return $false }
    return $true
}

# Fallback case: when the package carried no script, THIS file is the
# installed copy and it is executing from inside the tree being cleared.
# Deleting it would leave the install with no applier at all - keep it;
# a package that does carry a script overwrites it during the copy.
$runningScript = [IO.Path]::GetFullPath($PSCommandPath)

function Clear-Payload([string]$Root) {
    foreach ($file in Get-ChildItem $Root -Recurse -File) {
        if ([IO.Path]::GetFullPath($file.FullName) -eq $runningScript) { continue }
        if (Test-IsPayloadFile $file.FullName $Root) { Remove-Item $file.FullName -Force -ErrorAction SilentlyContinue }
    }
    # Prune now-empty payload subdirs (licenses\ and friends), deepest first.
    Get-ChildItem $Root -Recurse -Directory |
        Sort-Object { $_.FullName.Length } -Descending |
        Where-Object { $_.Name -ne "data" -and $_.Name -notlike "backup-*" } |
        ForEach-Object {
            if (-not (Get-ChildItem $_.FullName -Force -ErrorAction SilentlyContinue)) {
                Remove-Item $_.FullName -Force -ErrorAction SilentlyContinue
            }
        }
}

function Copy-PayloadTree([string]$SourceDir, [string]$DestDir) {
    $srcRoot = [IO.Path]::GetFullPath($SourceDir)
    foreach ($file in Get-ChildItem $SourceDir -Recurse -File) {
        $rel = $file.FullName.Substring($srcRoot.Length).TrimStart('\', '/')
        $destFile = Join-Path $DestDir $rel
        New-Item -ItemType Directory -Force -Path (Split-Path $destFile -Parent) | Out-Null
        Copy-Item $file.FullName $destFile -Force
    }
}

$appRoot = [IO.Path]::GetFullPath($AppDir)
$backupDir = Join-Path $AppDir ("backup-" + (Get-Date -Format "yyyyMMddHHmmss"))
New-Item -ItemType Directory -Force -Path $backupDir | Out-Null

Stop-Service -Name "EsoAgent" -Force -ErrorAction SilentlyContinue
# The tray and app keep payload files locked; close them before the swap.
taskkill /IM EsoAgent.Tray.exe /F 2>$null | Out-Null
taskkill /IM EsoAgent.App.exe /F 2>$null | Out-Null
Start-Sleep -Seconds 2

# Back up the current payload, clear it, then copy the new payload in.
foreach ($file in Get-ChildItem $AppDir -Recurse -File) {
    if (-not (Test-IsPayloadFile $file.FullName $appRoot)) { continue }
    $rel = $file.FullName.Substring($appRoot.Length).TrimStart('\', '/')
    $destFile = Join-Path $backupDir $rel
    New-Item -ItemType Directory -Force -Path (Split-Path $destFile -Parent) | Out-Null
    Copy-Item $file.FullName $destFile -Force
}
Log "backup kept at $backupDir"
Clear-Payload $AppDir
Copy-PayloadTree $ExtractedDir $AppDir

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
    Clear-Payload $AppDir
    Copy-PayloadTree $backupDir $AppDir
    Start-Service -Name "EsoAgent" -ErrorAction SilentlyContinue
    Log "rollback complete"
}
