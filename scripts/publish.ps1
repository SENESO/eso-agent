# Builds the distributable packages into dist\ (same layout as publish.sh).
$ErrorActionPreference = "Stop"
Set-Location (Join-Path $PSScriptRoot "..")
$dist = Join-Path $PWD "dist"
if (Test-Path $dist) { Remove-Item $dist -Recurse -Force }
New-Item -ItemType Directory -Force -Path $dist | Out-Null

function Pub($project, $rid, $out) {
    dotnet publish $project -c Release -r $rid --self-contained true `
        -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true `
        -p:DebugType=none -o $out --nologo -v q
    if ($LASTEXITCODE -ne 0) { throw "publish failed: $project ($rid)" }
}

Write-Host "== Windows service + tray (win-x64) =="
Pub "src/EsoAgent.Windows" "win-x64" "$dist\pkg-windows"
Pub "src/EsoAgent.Tray" "win-x64" "$dist\pkg-windows"
Copy-Item install\install.ps1, install\uninstall.ps1, install\update-apply.ps1 "$dist\pkg-windows\"
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
