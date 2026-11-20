# Builds the embedded mesh engine (eso-mesh.exe) from PINNED wireguard-go
# source (MIT) and stages it into a payload directory:
#   <Out>\eso-mesh.exe
#   <Out>\licenses\LICENSE-wireguard-go.txt
#   <Out>\licenses\eso-mesh-provenance.txt   (source commit, Go version, SHA-256)
#
# Usage: build-mesh-engine.ps1 -Out DIR [-Arch amd64]
# Env:   MESH_BUILD_CACHE (clone/build cache; default: %LOCALAPPDATA%\esoagent-mesh-build)
#
# The build FAILS if the checked-out source is not exactly the pinned commit.
# Provenance: MESH-EMBED-SPIKE.md + third-party/README.md.
param(
    [Parameter(Mandatory = $true)][string]$Out,
    [string]$Arch = "amd64"
)
$ErrorActionPreference = "Stop"

$PinCommit = "b3bc704ef0ee21a36298601a9e1e5ff90377ac52"
$PinTag = "v0.0.20250522"
$RepoUrls = @("https://git.zx2c4.com/wireguard-go", "https://github.com/WireGuard/wireguard-go")

if (-not (Get-Command go -ErrorAction SilentlyContinue)) { throw "go toolchain not found on PATH" }
if (-not (Get-Command git -ErrorAction SilentlyContinue)) { throw "git not found on PATH" }

$Cache = $env:MESH_BUILD_CACHE
if (-not $Cache) { $Cache = Join-Path $env:LOCALAPPDATA "esoagent-mesh-build" }
$Src = Join-Path $Cache "wireguard-go"
New-Item -ItemType Directory -Force -Path $Cache | Out-Null

if (-not (Test-Path (Join-Path $Src ".git"))) {
    $cloned = $false
    foreach ($url in $RepoUrls) {
        Write-Host "cloning $url"
        git clone --quiet $url $Src
        if ($LASTEXITCODE -eq 0) { $cloned = $true; break }
        if (Test-Path $Src) { Remove-Item $Src -Recurse -Force }
    }
    if (-not $cloned) { throw "could not clone wireguard-go from any known remote" }
}

git -C $Src fetch --quiet --tags origin
git -C $Src checkout --quiet $PinCommit
if ($LASTEXITCODE -ne 0) { throw "checkout of pinned commit failed" }
$Head = (git -C $Src rev-parse HEAD).Trim()
if ($Head -ne $PinCommit) { throw "REFUSING TO BUILD: source is at $Head, pinned is $PinCommit" }

$Licenses = Join-Path $Out "licenses"
New-Item -ItemType Directory -Force -Path $Licenses | Out-Null
Write-Host "building eso-mesh.exe (wireguard-go $PinTag @ $PinCommit, GOOS=windows GOARCH=$Arch)"
$ExePath = Join-Path $Out "eso-mesh.exe"
$env:CGO_ENABLED = "0"
$env:GOOS = "windows"
$env:GOARCH = $Arch
Push-Location $Src
try {
    go build -trimpath -buildvcs=false -o $ExePath .
    if ($LASTEXITCODE -ne 0) { throw "go build failed" }
} finally {
    Pop-Location
    Remove-Item Env:CGO_ENABLED, Env:GOOS, Env:GOARCH -ErrorAction SilentlyContinue
}

Copy-Item (Join-Path $Src "LICENSE") (Join-Path $Licenses "LICENSE-wireguard-go.txt") -Force
$Sha = (Get-FileHash $ExePath -Algorithm SHA256).Hash.ToLowerInvariant()
$GoVer = (go version)
$BuiltAt = (Get-Date).ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ssZ")
@(
    "eso-mesh.exe - embedded WireGuard mesh engine for EsoAgent"
    "component: wireguard-go (userspace WireGuard implementation)"
    "license: MIT (see LICENSE-wireguard-go.txt)"
    "source: https://git.zx2c4.com/wireguard-go"
    "source-tag: $PinTag"
    "source-commit: $PinCommit"
    "target: windows/$Arch"
    "go: $GoVer"
    "built-at-utc: $BuiltAt"
    "sha256: $Sha"
) | Set-Content (Join-Path $Licenses "eso-mesh-provenance.txt") -Encoding ascii

Write-Host "eso-mesh.exe sha256: $Sha"
Write-Host "staged into $Out"
