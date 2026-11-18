# EsoAgent uninstaller (Windows). Run as Administrator.
# Stops and removes the service, the firewall rule and the tray autostart entry.
# Asks before deleting the data directory (token, cert, device identity, audit logs).
[CmdletBinding()]
param([string]$InstallDir = "C:\Program Files\EsoAgent")
$ErrorActionPreference = "Continue"

$principal = New-Object Security.Principal.WindowsPrincipal([Security.Principal.WindowsIdentity]::GetCurrent())
if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    throw "Run this uninstaller as Administrator."
}

Stop-Service -Name "EsoAgent" -Force -ErrorAction SilentlyContinue
sc.exe delete EsoAgent | Out-Null
Get-NetFirewallRule -Name "EsoAgent-In" -ErrorAction SilentlyContinue | Remove-NetFirewallRule
Remove-ItemProperty -Path "HKCU:\Software\Microsoft\Windows\CurrentVersion\Run" -Name "EsoAgentTray" -ErrorAction SilentlyContinue
Get-Process -Name "EsoAgent.Tray" -ErrorAction SilentlyContinue | Stop-Process -Force -ErrorAction SilentlyContinue

$dataDir = Join-Path $InstallDir "data"
$answer = Read-Host "Delete ALL EsoAgent data (token, cert, device identity, audit logs) in $dataDir ? [y/N]"
if ($answer -eq "y") {
    Remove-Item $InstallDir -Recurse -Force -ErrorAction SilentlyContinue
    Write-Host "EsoAgent fully removed (binaries + data)."
} else {
    Get-ChildItem $InstallDir -Exclude "data" | Remove-Item -Recurse -Force -ErrorAction SilentlyContinue
    Write-Host "EsoAgent service removed; binaries deleted; data kept in $dataDir"
}
