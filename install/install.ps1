# EsoAgent installer (Windows). Run ONCE as Administrator from the published package folder.
# Installs the EsoAgent service (LocalSystem) + tray companion, generates the API token
# and owner password, scopes the firewall to the tailnet subnet, and prints the summary
# the operator needs (the token itself is shown ONCE - hand it over via secure entry).
[CmdletBinding()]
param(
    [string]$InstallDir = "C:\Program Files\EsoAgent",
    [int]$Port = 8737,
    [string]$FleetUrl = "",
    [string]$FleetFingerprint = "",
    [string]$EnrollmentKey = "",
    [string]$OwnerPassword = "",
    [switch]$EnableMesh
)
$ErrorActionPreference = "Stop"

# --- must be admin ---
$principal = New-Object Security.Principal.WindowsPrincipal([Security.Principal.WindowsIdentity]::GetCurrent())
if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    throw "Run this installer as Administrator."
}

$src = Split-Path -Parent $MyInvocation.MyCommand.Path
# The publish layout puts binaries next to the installer; also support running from the repo's install/ folder.
$binSrc = $src
if (-not (Test-Path (Join-Path $binSrc "EsoAgent.Windows.exe"))) {
    $binSrc = Join-Path $src "windows"
    if (-not (Test-Path (Join-Path $binSrc "EsoAgent.Windows.exe"))) { throw "EsoAgent.Windows.exe not found next to the installer." }
}

$dataDir = Join-Path $InstallDir "data"
New-Item -ItemType Directory -Force -Path $InstallDir, $dataDir | Out-Null

# --- stop any previous install ---
$existing = Get-Service -Name "EsoAgent" -ErrorAction SilentlyContinue
if ($existing) { Stop-Service -Name "EsoAgent" -Force -ErrorAction SilentlyContinue }

# --- copy binaries ---
# When the Inno installer runs this script, the script already sits
# inside $InstallDir with the binaries beside it - copying a file onto
# itself throws, so skip any copy whose source IS the destination.
function Copy-IfNewLocation([string]$SourceFile, [string]$DestDir) {
    $destFile = Join-Path $DestDir (Split-Path $SourceFile -Leaf)
    if ([IO.Path]::GetFullPath($SourceFile) -ne [IO.Path]::GetFullPath($destFile)) {
        Copy-Item $SourceFile $DestDir -Force
    }
}
Copy-IfNewLocation (Join-Path $binSrc "EsoAgent.Windows.exe") $InstallDir
Copy-IfNewLocation (Join-Path $binSrc "EsoAgent.Tray.exe") $InstallDir
Copy-IfNewLocation (Join-Path $src "update-apply.ps1") $InstallDir

# --- config (esoagent.json) ---
# PRESERVE an existing config. An upgrade must never wipe a fleet
# enrollment (fleet.enabled, server URL, fingerprint, enrollment key)
# by reseeding defaults over it - that silently dropped an enrolled
# device off its fleet in the v1.2.4 upgrade (LAUNCH-REPORT.md s.21).
# Defaults are written ONLY on a fresh install, when no esoagent.json
# exists in the data dir yet. Explicit re-configuration is done from
# the panel / by editing the file, never by reinstalling.
$configFile = Join-Path $dataDir "esoagent.json"
if (Test-Path $configFile) {
    Write-Host "Existing config preserved (upgrade): $configFile"
    Write-Host "  Enrollment and settings left untouched; defaults NOT reseeded."
} else {
    # No -FleetUrl given: seed the default hub so a fresh install shows its
    # device code with zero setup. An explicit -FleetUrl always wins.
    $seedUrl = $FleetUrl
    $seedFingerprint = $FleetFingerprint
    if (-not $seedUrl) {
        $seedUrl = "wss://100.98.165.106:8740/fleet/connect"
        if (-not $seedFingerprint) { $seedFingerprint = "9b5c0bcc778ebd660d3997f1d9b6dfcc0eae678b1340b3f6d8dfcbe1d0e849c9" }
    }
    $config = [ordered]@{
        port = $Port
        fleet = [ordered]@{
            enabled = $true
            serverUrl = $seedUrl
            serverFingerprint = $seedFingerprint
            enrollmentKey = $EnrollmentKey
        }
        mesh = [ordered]@{ enabled = [bool]$EnableMesh }
    }
    $config | ConvertTo-Json -Depth 5 | Set-Content $configFile -Encoding UTF8
}

# --- API token (256-bit, generated here; the service only reads it) ---
$tokenFile = Join-Path $dataDir "token"
if (-not (Test-Path $tokenFile)) {
    $bytes = New-Object byte[] 32
    [Security.Cryptography.RandomNumberGenerator]::Create().GetBytes($bytes)
    [IO.File]::WriteAllText($tokenFile, [Convert]::ToBase64String($bytes))
}

# --- owner password (dashboard) ---
# Supplied non-interactively by the Inno installer (wizard page) via
# -OwnerPassword; the interactive prompt remains for direct/zip runs.
$plain = $OwnerPassword
if (-not $plain) {
    Write-Host ""
    Write-Host "Set the OWNER PASSWORD for the control panel (you will log in with it at https://localhost:$Port/panel/):"
    $secure = Read-Host "Owner password" -AsSecureString
    $plain = [Runtime.InteropServices.Marshal]::PtrToStringAuto([Runtime.InteropServices.Marshal]::SecureStringToBSTR($secure))
}
if ($plain.Length -lt 8) { throw "Owner password must be at least 8 characters." }
$env:ESOAGENT_OWNER_PASSWORD = $plain
& (Join-Path $InstallDir "EsoAgent.Windows.exe") --set-owner-password
Remove-Item Env:ESOAGENT_OWNER_PASSWORD
$plain = $null

# --- service registration (LocalSystem, auto-start) ---
if ($existing) { sc.exe delete EsoAgent | Out-Null; Start-Sleep -Seconds 1 }
$exePath = Join-Path $InstallDir "EsoAgent.Windows.exe"
sc.exe create EsoAgent binPath= "`"$exePath`"" start= auto obj= LocalSystem DisplayName= "EsoAgent" | Out-Null
sc.exe description EsoAgent "EsoAgent remote administration agent (owner-controlled)" | Out-Null
sc.exe failure EsoAgent reset= 86400 actions= restart/60000/restart/60000/restart/60000 | Out-Null

# --- firewall: inbound ONLY from the tailnet subnet ---
Get-NetFirewallRule -Name "EsoAgent-In" -ErrorAction SilentlyContinue | Remove-NetFirewallRule
New-NetFirewallRule -Name "EsoAgent-In" -DisplayName "EsoAgent (tailnet only)" -Direction Inbound `
    -Program $exePath -Action Allow -RemoteAddress 100.64.0.0/10 -Profile Any | Out-Null

# --- tray autostart (per-user, at logon) ---
$runKey = "HKCU:\Software\Microsoft\Windows\CurrentVersion\Run"
Set-ItemProperty -Path $runKey -Name "EsoAgentTray" -Value "`"$(Join-Path $InstallDir 'EsoAgent.Tray.exe')`""

# --- start ---
Start-Service -Name "EsoAgent"
Start-Sleep -Seconds 3

# --- summary ---
$tailnet = (Get-NetIPAddress -AddressFamily IPv4 -ErrorAction SilentlyContinue |
    Where-Object { $_.IPAddress -like "100.*" } | Select-Object -First 1).IPAddress
$fingerprintFile = Join-Path $dataDir "cert.pfx.fingerprint"
$fingerprint = if (Test-Path $fingerprintFile) { (Get-Content $fingerprintFile -Raw).Trim() } else { "(service still starting - check $fingerprintFile)" }

Write-Host ""
Write-Host "================ EsoAgent installed ================"
Write-Host "Service        : EsoAgent (LocalSystem, auto-start) - running"
Write-Host "Data directory : $dataDir"
Write-Host "Port           : $Port"
Write-Host "Tailnet IP     : $tailnet"
Write-Host "Cert fingerprint (pin this in the client config):"
Write-Host "  $fingerprint"
Write-Host "Owner panel    : https://localhost:$Port/panel/  (also https://$tailnet`:$Port/panel/ from your phone on the tailnet)"
Write-Host ""
Write-Host "API TOKEN - shown ONCE. Hand it to the operator via secure entry, then clear this window:"
Write-Host "  $((Get-Content $tokenFile -Raw).Trim())"
Write-Host "Token file     : $tokenFile (rotate it any time from the panel's Access tab)"
Write-Host "====================================================="
