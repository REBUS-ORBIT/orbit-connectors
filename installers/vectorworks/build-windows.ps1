<#
.SYNOPSIS
  Build the placeholder ORBIT Connector for Vectorworks installer (.exe).

.DESCRIPTION
  v0.1.1 SCAFFOLD. No real Vectorworks plug-in exists yet (see
  src/OrbitConnector.Vectorworks/README.md). This script just stages a
  README.txt that the Inno Setup .iss bundles into a per-user .exe.

  Steps:
    1. Stage a "coming soon" README.txt into a temp dir.
    2. Invoke ISCC.exe on installers/vectorworks/inno/OrbitConnector.Vectorworks.iss
       with /DConnectorVersion + /DPayloadDir + /DLicenseFile.
    3. Move the produced OrbitConnector-Vectorworks-Setup-v<Version>.exe
       into installers/vectorworks/dist/.

  ISCC is pre-installed on `windows-latest`. Locally, install via:
      choco install innosetup -y

.PARAMETER Version
  The connector version, without leading "v". Required.
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory=$true)]
    [string] $Version
)

$ErrorActionPreference = 'Stop'
$PSNativeCommandUseErrorActionPreference = $true

$ScriptDir   = Split-Path -Parent $MyInvocation.MyCommand.Path        # installers/vectorworks
$RepoRoot    = (Resolve-Path (Join-Path $ScriptDir '..\..')).Path
$DistDir     = Join-Path $ScriptDir 'dist'
$StageDir    = Join-Path $ScriptDir 'build\stage'
$Iss         = Join-Path $ScriptDir 'inno\OrbitConnector.Vectorworks.iss'
$License     = Join-Path $RepoRoot 'LICENCE.txt'

if (-not (Test-Path $Iss))     { throw "Inno Setup script missing: $Iss" }
if (-not (Test-Path $License)) { throw "Licence file missing: $License" }

# Locate ISCC.
$iscc = "C:\Program Files (x86)\Inno Setup 6\ISCC.exe"
if (-not (Test-Path $iscc)) {
    choco install innosetup -y --no-progress | Out-Null
}
if (-not (Test-Path $iscc)) {
    throw "ISCC.exe still missing after choco install"
}

# Stage placeholder payload.
if (Test-Path $StageDir) { Remove-Item -Recurse -Force $StageDir }
New-Item -ItemType Directory -Force -Path $StageDir | Out-Null

$readme = @"
ORBIT Connector for Vectorworks
================================

This installer is a placeholder shipped with ORBIT Connectors v$Version.

The actual Vectorworks plug-in is under development. There is nothing
to load yet -- this README is the entire payload.

Watch https://github.com/REBUS-ORBIT/orbit-connectors for updates.
"@
Set-Content -Path (Join-Path $StageDir 'README.txt') -Value $readme -Encoding UTF8

# Compile.
New-Item -ItemType Directory -Force -Path $DistDir | Out-Null
$payload = (Resolve-Path $StageDir).Path

Write-Host ">>> ISCC: building Vectorworks placeholder .exe for v$Version" -ForegroundColor Cyan
& $iscc `
    "/DPayloadDir=$payload" `
    "/DConnectorVersion=$Version" `
    "/DLicenseFile=$License" `
    "/Q" `
    $Iss
if ($LASTEXITCODE -ne 0) { throw "ISCC failed with exit $LASTEXITCODE" }

$exe = Get-ChildItem (Split-Path $Iss -Parent) -Filter "OrbitConnector-Vectorworks-Setup-v*.exe" | Select-Object -First 1
if (-not $exe) { throw "Inno Setup did not produce an .exe in $(Split-Path $Iss -Parent)" }
Move-Item -Force $exe.FullName $DistDir
Write-Host "Produced: $(Join-Path $DistDir $exe.Name)" -ForegroundColor Green
