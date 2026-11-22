# Builds the distributable packages into dist\ (same layout as publish.sh).
$ErrorActionPreference = "Stop"
Set-Location (Join-Path $PSScriptRoot "..")
$dist = Join-Path $PWD "dist"
if (Test-Path $dist) { Remove-Item $dist -Recurse -Force }
New-Item -ItemType Directory -Force -Path $dist | Out-Null

function Pub($project, $rid, $out) {
    dotnet publish $project -c Release -r $rid --self-contained true `
        -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true `
        -p:DebugType=none -p:SatelliteResourceLanguages=en -o $out --nologo -v q
    if ($LASTEXITCODE -ne 0) { throw "publish failed: $project ($rid)" }
}

# Windows payload (installer diet, BUILD-REPORT §19; same layout as
# publish.sh): service + tray publish NON-single-file into ONE folder
# so the shared frameworks land once, satellites are English-only
# (the runtime packs ship no Arabic resources; the product's own
# AR/EN strings are in the app), and the WPF + design-time files no
# shipped code can reach are pruned (the tray is WinForms-only; no
# project references WPF; System.Windows.Forms.Design.dll is the
# Visual Studio designer assembly).
function PubWin($project, $out) {
    dotnet publish $project -c Release -r win-x64 --self-contained true `
        -p:DebugType=none -p:SatelliteResourceLanguages=en -o $out --nologo -v q
    if ($LASTEXITCODE -ne 0) { throw "publish failed: $project (win-x64)" }
}

$PruneWin = @(
    "PresentationFramework.dll", "PresentationCore.dll", "PresentationUI.dll",
    "ReachFramework.dll", "System.Windows.Controls.Ribbon.dll",
    "PresentationNative_cor3.dll", "wpfgfx_cor3.dll", "D3DCompiler_47_cor3.dll",
    "PenImc_cor3.dll", "UIAutomationClientSideProviders.dll", "System.Printing.dll",
    "PresentationFramework.Aero.dll", "PresentationFramework.Aero2.dll",
    "PresentationFramework.AeroLite.dll", "PresentationFramework.Classic.dll",
    "PresentationFramework.Luna.dll", "PresentationFramework.Royale.dll",
    "PresentationFramework-SystemData.dll", "PresentationFramework-SystemDrawing.dll",
    "PresentationFramework-SystemXml.dll", "PresentationFramework-SystemXmlLinq.dll",
    "System.Windows.Presentation.dll", "System.Windows.Forms.Design.dll"
)

Write-Host "== Windows service + tray (win-x64, shared-runtime folder) =="
PubWin "src/EsoAgent.Windows" "$dist\pkg-windows"
PubWin "src/EsoAgent.Tray" "$dist\pkg-windows"
foreach ($f in $PruneWin) { Remove-Item (Join-Path "$dist\pkg-windows" $f) -ErrorAction SilentlyContinue }
Copy-Item install\install.ps1, install\uninstall.ps1, install\update-apply.ps1 "$dist\pkg-windows\"
Write-Host "== Embedded mesh engine (eso-mesh.exe + wintun.dll) =="
# The Windows package must never ship without the embedded engine: a failed
# build or hash check throws and aborts the publish rather than silently
# reintroducing the external WireGuard dependency.
& "$PWD\scripts\build-mesh-engine.ps1" -Out "$dist\pkg-windows"
& "$PWD\scripts\fetch-wintun.ps1" -Out "$dist\pkg-windows"
Compress-Archive -Path "$dist\pkg-windows\*" -DestinationPath "$dist\esoagent-windows.zip"

Write-Host "== Linux daemon (linux-x64) =="
Pub "src/EsoAgent.Linux" "linux-x64" "$dist\pkg-linux"
Copy-Item install\install.sh, install\uninstall.sh, install\update-apply.sh "$dist\pkg-linux\"
Compress-Archive -Path "$dist\pkg-linux\*" -DestinationPath "$dist\esoagent-linux.zip"

Write-Host "== Fleet server (linux-x64 + win-x64) =="
Pub "src/EsoAgent.Server" "linux-x64" "$dist\pkg-server\linux"
Pub "src/EsoAgent.Server" "win-x64" "$dist\pkg-server\windows"
Compress-Archive -Path "$dist\pkg-server\*" -DestinationPath "$dist\esoagent-server.zip"

Write-Host "== Operator client (linux-x64 + win-x64) =="
Pub "src/EsoAgent.Client" "linux-x64" "$dist\pkg-client\linux"
Pub "src/EsoAgent.Client" "win-x64" "$dist\pkg-client\windows"
Compress-Archive -Path "$dist\pkg-client\*" -DestinationPath "$dist\esoagent-client.zip"

Write-Host ""
Get-ChildItem "$dist\*.zip" | Format-Table Name, Length
