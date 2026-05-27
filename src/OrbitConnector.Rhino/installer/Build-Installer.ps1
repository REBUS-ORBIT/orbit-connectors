# Build-Installer.ps1
# Builds the ORBIT Rhino Connector in Release configuration and packages it with Inno Setup.
# Usage: .\Build-Installer.ps1 [-Version "1.0.0"] [-Config "Release"]

param(
    [string]$Version = "1.0.0",
    [string]$Config  = "Release"
)

$ErrorActionPreference = "Stop"
$Root     = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
$SlnPath  = Join-Path $Root "ORBIT-Connectors.sln"
$IssPath  = Join-Path $PSScriptRoot "OrbitConnector.iss"
$OutDir   = Join-Path $Root "dist"

Write-Host "=== ORBIT Connector Build ===" -ForegroundColor Cyan
Write-Host "Version : $Version"
Write-Host "Config  : $Config"

# 1. Build
Write-Host "`nBuilding solution..." -ForegroundColor Yellow
dotnet build $SlnPath --configuration $Config -p:Version=$Version
if ($LASTEXITCODE -ne 0) { throw "Build failed" }

# 2. Package with Inno Setup
$inno = "C:\Program Files (x86)\Inno Setup 6\ISCC.exe"
if (-not (Test-Path $inno)) {
    Write-Warning "Inno Setup not found at $inno — skipping installer packaging"
    exit 0
}

Write-Host "`nPackaging installer..." -ForegroundColor Yellow
New-Item -ItemType Directory -Force -Path $OutDir | Out-Null
& $inno $IssPath /DMyAppVersion=$Version /DMyOutputDir=$OutDir
if ($LASTEXITCODE -ne 0) { throw "Inno Setup failed" }

Write-Host "`nDone. Installer written to: $OutDir" -ForegroundColor Green
