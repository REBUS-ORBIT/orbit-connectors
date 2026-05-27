<#
.SYNOPSIS
  Build the placeholder ORBIT Connector for Unreal Engine 5 installer (.exe).

.DESCRIPTION
  v0.1.1 SCAFFOLD. No real UE5 plug-in exists yet (see
  src/OrbitConnector.UE5/README.md). This script just stages a README.txt
  that the Inno Setup .iss bundles into a per-user .exe.

  Steps:
    1. Stage a "coming soon" README.txt into a temp dir.
    2. Invoke ISCC.exe on installers/ue5/inno/OrbitConnector.UE5.iss
       with /DConnectorVersion + /DPayloadDir + /DLicenseFile.
    3. Move the produced OrbitConnector-UE5-Setup-v<Version>.exe
       into installers/ue5/dist/.

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

$ScriptDir   = Split-Path -Parent $MyInvocation.MyCommand.Path        # installers/ue5
$RepoRoot    = (Resolve-Path (Join-Path $ScriptDir '..\..')).Path
$DistDir     = Join-Path $ScriptDir 'dist'
$StageDir    = Join-Path $ScriptDir 'build\stage'
$Iss         = Join-Path $ScriptDir 'inno\OrbitConnector.UE5.iss'
$License     = Join-Path $RepoRoot 'LICENCE.txt'

if (-not (Test-Path $Iss))     { throw "Inno Setup script missing: $Iss" }
if (-not (Test-Path $License)) { throw "Licence file missing: $License" }

$iscc = "C:\Program Files (x86)\Inno Setup 6\ISCC.exe"
if (-not (Test-Path $iscc)) {
    choco install innosetup -y --no-progress | Out-Null
}
if (-not (Test-Path $iscc)) {
    throw "ISCC.exe still missing after choco install"
}

if (Test-Path $StageDir) { Remove-Item -Recurse -Force $StageDir }
New-Item -ItemType Directory -Force -Path $StageDir | Out-Null

$readme = @"
ORBIT Connector for Unreal Engine 5
====================================

This installer is a placeholder shipped with ORBIT Connectors v$Version.

The actual UE5 plug-in is under development. There is nothing to load
yet -- this README is the entire payload.

When real source lands, this installer will deposit a complete
.uplugin folder at:

    %USERPROFILE%\Documents\Unreal Projects\Plugins\OrbitConnector\

which you can then copy into either:
    <UnrealEngine>\Engine\Plugins\   (engine-wide install)
    <YourProject>\Plugins\           (per-project install)

Watch https://github.com/REBUS-ORBIT/orbit-connectors for updates.
"@
Set-Content -Path (Join-Path $StageDir 'README.txt') -Value $readme -Encoding UTF8

New-Item -ItemType Directory -Force -Path $DistDir | Out-Null
$payload = (Resolve-Path $StageDir).Path

Write-Host ">>> ISCC: building UE5 placeholder .exe for v$Version" -ForegroundColor Cyan
& $iscc `
    "/DPayloadDir=$payload" `
    "/DConnectorVersion=$Version" `
    "/DLicenseFile=$License" `
    "/Q" `
    $Iss
if ($LASTEXITCODE -ne 0) { throw "ISCC failed with exit $LASTEXITCODE" }

$exe = Get-ChildItem (Split-Path $Iss -Parent) -Filter "OrbitConnector-UE5-Setup-v*.exe" | Select-Object -First 1
if (-not $exe) { throw "Inno Setup did not produce an .exe in $(Split-Path $Iss -Parent)" }
Move-Item -Force $exe.FullName $DistDir
Write-Host "Produced: $(Join-Path $DistDir $exe.Name)" -ForegroundColor Green
