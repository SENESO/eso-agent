# Check: install.ps1 config preservation (LAUNCH-REPORT.md s.21 root fix).
#
# Proves BOTH paths of the config block in install/install.ps1 by
# extracting that exact block from the shipped script and executing it
# against sandbox data dirs - the logic under test is the logic that
# ships, not a copy of it:
#
#   A) Fresh install (no esoagent.json): defaults ARE seeded, and the
#      fleet section reflects the parameters passed.
#   B) Upgrade (esoagent.json already present, enrolled): the file is
#      left BYTE-FOR-BYTE identical even when the installer is invoked
#      with empty/default parameters - the v1.2.4 defect scenario.
#
# Run:  pwsh -File scripts/check-config-preservation.ps1
#   or: powershell -ExecutionPolicy Bypass -File scripts\check-config-preservation.ps1
# Exit code 0 = both paths pass; 1 = a path failed.
# Pure ASCII. No admin rights, no service, no network - file ops only
# inside a temp sandbox that is removed afterwards.

$ErrorActionPreference = "Stop"
$repoRoot = Split-Path -Parent $PSScriptRoot
$installPs1 = Join-Path (Join-Path $repoRoot "install") "install.ps1"
if (-not (Test-Path $installPs1)) { throw "install.ps1 not found at $installPs1" }

# --- extract the config block from install.ps1 ---
$lines = Get-Content $installPs1
$start = -1; $end = -1
for ($i = 0; $i -lt $lines.Count; $i++) {
    if ($lines[$i] -match '^# --- config \(esoagent\.json\) ---') { $start = $i; continue }
    if ($start -ge 0 -and $lines[$i] -match '^# --- ') { $end = $i; break }
}
if ($start -lt 0 -or $end -lt 0) { throw "Could not locate the config block markers in install.ps1." }
$block = ($lines[$start..($end - 1)] -join "`n")

# Structural guard: the shipped block must actually contain the
# preservation check, so this script can never silently pass against
# a regressed installer.
if ($block -notmatch 'Test-Path \$configFile') {
    throw "FAIL: the config block in install.ps1 has no existence check (Test-Path `$configFile)."
}

function Invoke-ConfigBlock([string]$DataDir, [string]$FleetUrl, [string]$FleetFingerprint, [string]$EnrollmentKey, [bool]$Mesh) {
    $dataDir = $DataDir
    $Port = 8737
    $FleetUrl = $FleetUrl
    $FleetFingerprint = $FleetFingerprint
    $EnrollmentKey = $EnrollmentKey
    $EnableMesh = $Mesh
    Invoke-Expression $block
}

$sandbox = Join-Path ([IO.Path]::GetTempPath()) ("esoagent-cfgcheck-" + [Guid]::NewGuid().ToString("N"))
New-Item -ItemType Directory -Force -Path $sandbox | Out-Null
$failures = 0
try {
    # --- Case A: fresh install seeds defaults ---
    $dirA = Join-Path $sandbox "fresh\data"
    New-Item -ItemType Directory -Force -Path $dirA | Out-Null
    Invoke-ConfigBlock $dirA "https://fleet.example:8740" "AA:BB:CC" "enroll-key-1" $true
    $cfgA = Join-Path $dirA "esoagent.json"
    if (-not (Test-Path $cfgA)) {
        Write-Host "FAIL A: fresh install did not seed esoagent.json"; $failures++
    } else {
        $json = Get-Content $cfgA -Raw | ConvertFrom-Json
        if ($json.port -ne 8737) { Write-Host "FAIL A: seeded port wrong"; $failures++ }
        if ($json.fleet.enabled -ne $true) { Write-Host "FAIL A: seeded fleet.enabled should be true (FleetUrl given)"; $failures++ }
        if ($json.fleet.serverUrl -ne "https://fleet.example:8740") { Write-Host "FAIL A: seeded serverUrl wrong"; $failures++ }
        if ($json.fleet.serverFingerprint -ne "AA:BB:CC") { Write-Host "FAIL A: seeded fingerprint wrong"; $failures++ }
        if ($json.mesh.enabled -ne $true) { Write-Host "FAIL A: seeded mesh.enabled wrong"; $failures++ }
        Write-Host "PASS A: fresh install seeds defaults from the parameters."
    }

    # --- Case B: upgrade preserves an enrolled config byte-for-byte ---
    $dirB = Join-Path $sandbox "upgrade\data"
    New-Item -ItemType Directory -Force -Path $dirB | Out-Null
    $enrolled = @'
{
    "port": 9999,
    "fleet": {
        "enabled": true,
        "serverUrl": "https://fleet.owner-box:8740",
        "serverFingerprint": "11:22:33:44",
        "enrollmentKey": "KEEP-ME-SECRET-KEY"
    },
    "mesh": { "enabled": true },
    "deviceName": "owner-laptop"
}
'@
    $cfgB = Join-Path $dirB "esoagent.json"
    [IO.File]::WriteAllText($cfgB, $enrolled)
    $before = (Get-FileHash $cfgB -Algorithm SHA256).Hash
    # The defect scenario: upgrade invoked with NO fleet parameters.
    Invoke-ConfigBlock $dirB "" "" "" $false
    $after = (Get-FileHash $cfgB -Algorithm SHA256).Hash
    if ($before -ne $after) {
        Write-Host "FAIL B: upgrade REWROTE the existing esoagent.json (enrollment would be lost)"; $failures++
    } else {
        $jsonB = Get-Content $cfgB -Raw | ConvertFrom-Json
        if ($jsonB.fleet.enabled -ne $true -or $jsonB.fleet.enrollmentKey -ne "KEEP-ME-SECRET-KEY") {
            Write-Host "FAIL B: enrolled values changed"; $failures++
        } else {
            Write-Host "PASS B: upgrade preserves the enrolled config byte-for-byte."
        }
    }
}
finally {
    Remove-Item $sandbox -Recurse -Force -ErrorAction SilentlyContinue
}

if ($failures -gt 0) { Write-Host "RESULT: FAIL ($failures)"; exit 1 }
Write-Host "RESULT: PASS - fresh seeds defaults; upgrade preserves config."
exit 0
