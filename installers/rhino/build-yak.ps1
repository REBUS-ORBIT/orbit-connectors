<#
.SYNOPSIS
  Build a YAK package for the ORBIT Rhino + Grasshopper connector.

.DESCRIPTION
  Steps:
    1. dotnet build src/OrbitConnector.Rhino/OrbitConnector.Rhino.csproj -c Release.
    2. Stage the produced .rhp (and .gha if present) plus any side-loaded
       dependency DLLs into a clean folder alongside a copy of manifest.yml.
    3. Run `yak build` from that folder.
    4. Move the resulting .yak into installers/rhino/dist/.

  YAK target distributions:
    -RhinoTarget rh8 (default) emits orbit-connector-<version>-rh8-win.yak.
    Adding rh7 in future is a one-line change (extend $RhinoTargets array).

  YAK must be on PATH. On GH Actions windows-latest we install it via choco
  (`choco install yak`); locally you can grab it from McNeel's package manager
  or from a Rhino 8 install (it ships in %ProgramFiles%\Rhino 8\System\Yak.exe).

.PARAMETER Version
  Override the manifest version. Defaults to the value in manifest.yml.

.PARAMETER RhinoTargets
  Array of YAK distribution targets. Default: @('rh8').
  Mac targets are built separately by installers/rhino/build-mac.sh on macOS
  runners — pyassimp/Rhino Mac SDKs aren't on Windows.

.PARAMETER YakExe
  Path to yak.exe. Default: searches PATH then %ProgramFiles%\Rhino 8\System.

.PARAMETER Configuration
  dotnet build config. Default: Release.
#>
[CmdletBinding()]
param(
    [string]   $Version,
    [string[]] $RhinoTargets = @('rh8'),
    [string]   $YakExe,
    [string]   $Configuration = 'Release'
)

$ErrorActionPreference = 'Stop'
$PSNativeCommandUseErrorActionPreference = $true

# ---- Resolve repo paths ----
$ScriptDir   = Split-Path -Parent $MyInvocation.MyCommand.Path   # installers/rhino
$InstallRoot = $ScriptDir
$RepoRoot    = (Resolve-Path (Join-Path $ScriptDir '..\..')).Path
$Csproj      = Join-Path $RepoRoot 'src\OrbitConnector.Rhino\OrbitConnector.Rhino.csproj'
$ManifestSrc = Join-Path $InstallRoot 'yak\manifest.yml'
$DistDir     = Join-Path $InstallRoot 'dist'
$StageRoot   = Join-Path $InstallRoot 'build\yak-stage'

if (-not (Test-Path $Csproj))      { throw "Project not found: $Csproj" }
if (-not (Test-Path $ManifestSrc)) { throw "Manifest not found: $ManifestSrc" }

# ---- Resolve YAK ----
if (-not $YakExe) {
    $cmd = Get-Command yak.exe -ErrorAction SilentlyContinue
    if ($cmd) { $YakExe = $cmd.Source }
}
if (-not $YakExe -or -not (Test-Path $YakExe)) {
    $fallback = Join-Path $env:ProgramFiles 'Rhino 8\System\Yak.exe'
    if (Test-Path $fallback) { $YakExe = $fallback }
}
if (-not $YakExe -or -not (Test-Path $YakExe)) {
    throw "yak.exe not found. Install via 'choco install yak' or pass -YakExe <path>."
}
Write-Host "Using YAK: $YakExe"

# ---- Resolve version (override manifest if -Version passed) ----
$manifestText = Get-Content $ManifestSrc -Raw
if (-not $Version) {
    $m = [regex]::Match($manifestText, '(?m)^version:\s*(\S+)\s*$')
    if (-not $m.Success) { throw "Could not parse version from $ManifestSrc" }
    $Version = $m.Groups[1].Value
}
Write-Host "Package version: $Version"

# ---- Build ----
Write-Host "`n>>> Building $Csproj ($Configuration)" -ForegroundColor Cyan
& dotnet build $Csproj -c $Configuration -p:Version=$Version
if ($LASTEXITCODE -ne 0) { throw "dotnet build failed (exit $LASTEXITCODE)" }

$BinDir = Join-Path (Split-Path $Csproj -Parent) "bin\$Configuration\net8.0-windows"
if (-not (Test-Path $BinDir)) {
    throw "Build output directory missing: $BinDir"
}

# ---- Locate artefacts ----
$Rhp = Get-ChildItem -Path $BinDir -Filter 'OrbitConnector.Rhino.rhp' -ErrorAction SilentlyContinue | Select-Object -First 1
if (-not $Rhp) {
    throw "OrbitConnector.Rhino.rhp not found in $BinDir (csproj's RenameToRhp target should have produced it)."
}

$Gha = Get-ChildItem -Path $BinDir -Filter '*.gha' -ErrorAction SilentlyContinue | Select-Object -First 1

# Dependency DLLs that need to ship alongside the .rhp. RhinoCommon / Eto /
# Rhino.UI are intentionally excluded — Rhino loads its own copies at runtime
# and bundling ours causes type-identity conflicts.
$rhinoProvided = @('RhinoCommon.dll', 'Rhino.UI.dll', 'Eto.dll', 'Eto.Wpf.dll',
                   'Eto.Mac64.dll', 'Eto.WinForms.dll', 'Eto.Gtk.dll')
$Deps = Get-ChildItem -Path $BinDir -Filter '*.dll' |
    Where-Object { $rhinoProvided -notcontains $_.Name }

# ---- Build each target ----
New-Item -ItemType Directory -Force -Path $DistDir | Out-Null
$produced = @()

foreach ($target in $RhinoTargets) {
    Write-Host "`n>>> Staging YAK build for distribution: $target" -ForegroundColor Cyan
    $stage = Join-Path $StageRoot "$target-win"
    if (Test-Path $stage) { Remove-Item -Recurse -Force $stage }
    New-Item -ItemType Directory -Force -Path $stage | Out-Null

    # Inject the version into a staged manifest so each build matches -Version.
    $stagedManifest = [regex]::Replace(
        $manifestText,
        '(?m)^version:\s*\S+\s*$',
        "version: $Version"
    )
    Set-Content -Path (Join-Path $stage 'manifest.yml') -Value $stagedManifest -Encoding UTF8

    Copy-Item $Rhp.FullName $stage -Force
    if ($Gha) { Copy-Item $Gha.FullName $stage -Force }
    foreach ($dep in $Deps) { Copy-Item $dep.FullName $stage -Force }

    Write-Host "Staged files:"
    Get-ChildItem $stage | ForEach-Object { Write-Host "  $($_.Name)" }

    Write-Host "`n>>> Running 'yak build' in $stage" -ForegroundColor Cyan
    Push-Location $stage
    try {
        & $YakExe build --platform win
        if ($LASTEXITCODE -ne 0) { throw "yak build failed (exit $LASTEXITCODE) in $stage" }
    } finally {
        Pop-Location
    }

    $built = Get-ChildItem -Path $stage -Filter '*.yak' | Select-Object -First 1
    if (-not $built) { throw "yak build produced no .yak file in $stage" }

    $destName = "orbit-connector-$Version-$target-win.yak"
    $destPath = Join-Path $DistDir $destName
    Move-Item -Force -LiteralPath $built.FullName -Destination $destPath
    Write-Host "Produced: $destPath" -ForegroundColor Green
    $produced += $destPath
}

Write-Host "`nDone. Artefacts:" -ForegroundColor Green
$produced | ForEach-Object { Write-Host "  $_" }
