# Fetches the official prebuilt wintun.dll (WireGuard LLC Prebuilt Binaries
# License) and stages it, UNMODIFIED, into a payload directory:
#   <Out>\wintun.dll
#   <Out>\licenses\LICENSE-wintun.txt
#
# Usage: fetch-wintun.ps1 -Out DIR [-Arch amd64]
# Env:   MESH_BUILD_CACHE (download cache; default: %LOCALAPPDATA%\esoagent-mesh-build)
#
# Both the distribution zip and the extracted DLL are verified against the
# SHA-256 values pinned in third-party/README.md; any mismatch fails the
# build. The license forbids modifying the DLL - verification is how we
# prove we ship it byte-for-byte.
param(
    [Parameter(Mandatory = $true)][string]$Out,
    [string]$Arch = "amd64"
)
$ErrorActionPreference = "Stop"

$Version = "0.14.1"
$ZipUrl = "https://www.wintun.net/builds/wintun-$Version.zip"
$ZipSha256 = "07c256185d6ee3652e09fa55c0b673e2624b565e02c4b9091c79ca7d2f24ef51"
$DllSha256Amd64 = "e5da8447dc2c320edc0fc52fa01885c103de8c118481f683643cacc3220dafce"

if ($Arch -ne "amd64") { throw "only amd64 is pinned/verified today (got: $Arch)" }

$Cache = $env:MESH_BUILD_CACHE
if (-not $Cache) { $Cache = Join-Path $env:LOCALAPPDATA "esoagent-mesh-build" }
New-Item -ItemType Directory -Force -Path $Cache | Out-Null
$Licenses = Join-Path $Out "licenses"
New-Item -ItemType Directory -Force -Path $Licenses | Out-Null
$Zip = Join-Path $Cache "wintun-$Version.zip"

if (-not (Test-Path $Zip)) {
    Write-Host "downloading $ZipUrl"
    Invoke-WebRequest -Uri $ZipUrl -OutFile "$Zip.tmp"
    Move-Item "$Zip.tmp" $Zip -Force
}

$ActualZipSha = (Get-FileHash $Zip -Algorithm SHA256).Hash.ToLowerInvariant()
if ($ActualZipSha -ne $ZipSha256) {
    throw "REFUSING: wintun zip sha256 mismatch (expected $ZipSha256, actual $ActualZipSha)"
}

$Extract = Join-Path $Cache "wintun-$Version-extract"
if (Test-Path $Extract) { Remove-Item $Extract -Recurse -Force }
Expand-Archive -Path $Zip -DestinationPath $Extract -Force

$Dll = Join-Path $Extract "wintun\bin\$Arch\wintun.dll"
if (-not (Test-Path $Dll)) { throw "wintun.dll for $Arch not found inside the zip" }
$ActualDllSha = (Get-FileHash $Dll -Algorithm SHA256).Hash.ToLowerInvariant()
if ($ActualDllSha -ne $DllSha256Amd64) {
    throw "REFUSING: wintun.dll sha256 mismatch (expected $DllSha256Amd64, actual $ActualDllSha)"
}

Copy-Item $Dll (Join-Path $Out "wintun.dll") -Force
Copy-Item (Join-Path $Extract "wintun\LICENSE.txt") (Join-Path $Licenses "LICENSE-wintun.txt") -Force
Write-Host "wintun.dll $Version ($Arch) sha256 verified: $ActualDllSha"
Write-Host "staged into $Out"
