# Builds the self-contained Windows desktop app: frontend static export -> Api wwwroot -> ECUStudio.exe
# Usage (Windows, PowerShell 7): ./build/publish-desktop.ps1 [-Configuration Release]
param([string]$Configuration = "Release")
$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot

Push-Location (Join-Path $root "frontend")
try {
    npm ci
    npm run export:desktop   # next build + copy to src/ECUStudio.Api/wwwroot
} finally { Pop-Location }

dotnet test (Join-Path $root "tests/ECUStudio.Tests") -c $Configuration
dotnet publish (Join-Path $root "src/ECUStudio.Desktop") -c $Configuration -r win-x64 -o (Join-Path $root "artifacts/desktop")
Write-Host "Done: artifacts/desktop/ECUStudio.exe (requires Microsoft Edge WebView2 Runtime)"
