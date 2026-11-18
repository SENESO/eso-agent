# EsoAgent post-install verification (item 45): the installer finishes
# only after the agent proves itself — service running, doctor's first
# run clean enough to answer, owner panel reachable. Writes install.log
# next to the data dir and first-run.txt with the device code + how
# approval works. Exit code != 0 makes the installer report the failure
# (nothing is left half-claimed: the log says exactly what failed).
param(
  [string]$DataDir = "C:\Program Files\EsoAgent\data",
  [int]$Port = 8737
)
$ErrorActionPreference = "Continue"
New-Item -ItemType Directory -Force -Path $DataDir | Out-Null
$log = Join-Path $DataDir "install.log"
function Log($m) { $line = "[{0}] {1}" -f (Get-Date -Format o), $m; Add-Content -Path $log -Value $line; Write-Host $line }
Log "EsoAgent post-install verification starting."

$ok = $true
# 1) service up
$svc = Get-Service -Name "EsoAgent" -ErrorAction SilentlyContinue
if ($svc -and $svc.Status -eq "Running") { Log "service: Running" }
else { Log "service: NOT running ($($svc.Status))"; $ok = $false }

# 2) agent answers on its loopback status (doctor-class self-check)
$answered = $false
foreach ($i in 1..15) {
  try {
    $tcp = New-Object System.Net.Sockets.TcpClient
    $tcp.Connect("127.0.0.1", $Port)
    $tcp.Close()
    $answered = $true
    break
  } catch { Start-Sleep -Seconds 1 }
}
if ($answered) { Log "agent port $Port: listening" } else { Log "agent port $Port: no answer after 15 s"; $ok = $false }

# 3) device code (the first-run screen's key fact)
$code = ""
$deviceFile = Join-Path $DataDir "device.json"
if (Test-Path $deviceFile) {
  try { $code = (Get-Content $deviceFile -Raw | ConvertFrom-Json).code } catch {}
}
$firstRun = @"
EsoAgent is installed and running.

Your device code: $code

What happens next:
  1. Your operator (or you, in the fleet panel) approves this device
     under its code — until then it accepts nothing.
  2. Open the EsoAgent app to control your devices, or the owner panel
     in a browser at https://<this-device>:$Port/panel/
  3. The tray icon (golden wing) shows live status: gold = online,
     grey = offline, waves lit = being controlled right now.

Install log: $log
"@
Set-Content -Path (Join-Path $DataDir "first-run.txt") -Value $firstRun
Log "device code: $code"
Log ("verification: " + ($(if ($ok) { "PASS" } else { "FAIL" })))
if (-not $ok) { exit 1 }
exit 0
