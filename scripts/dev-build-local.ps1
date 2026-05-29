<#
.SYNOPSIS
    Local-only build-and-reload loop for the ORBIT Rhino connector.

.DESCRIPTION
    Bypasses CI: builds OrbitConnector.Rhino from source, copies the
    fresh artefacts into the running Rhino install's plugin folder
    (the Inno-installer-managed location under
    %LOCALAPPDATA%\Programs\OrbitConnector\Rhino\<version>\), and
    relaunches Rhino. Total round-trip is seconds rather than the
    ~10 minute CI -> installer -> uninstall -> install cycle.

    Nothing leaves your machine. Nothing is uploaded.

    Workflow:
        1. Edit .cs / .html files under
           orbit-connectors-repo/src/OrbitConnector.Rhino/
        2. Run this script.
        3. Test the change in Rhino.
        4. Repeat.
        5. When the behaviour is correct, commit + push and let CI
           build the next installer for distribution.

.PARAMETER CsprojPath
    Optional. Path to OrbitConnector.Rhino.csproj. Defaults to the
    csproj sibling to this script (orbit-connectors-repo layout).

.PARAMETER Configuration
    Debug or Release. Defaults to Release because the installer
    ships Release; matching it removes one variable from PBR-probe
    debugging.

.PARAMETER InstallRoot
    Optional. Override the plugin install root. Defaults to
    %LOCALAPPDATA%\Programs\OrbitConnector\Rhino which is what the
    Inno installer creates and what Rhino's registry entry points
    at on this machine.

.PARAMETER Version
    Optional. Sub-folder under InstallRoot to write into. Defaults
    to the version stamp resolved from Directory.Build.props of
    the resolved csproj. The script ALSO mirrors the artefacts
    into whichever version folder the Rhino registry currently
    points at, so a Rhino restart always picks up the new build
    without needing to re-register the plugin.

.PARAMETER NoRestart
    Skip closing/relaunching Rhino. Useful when you want to copy
    fresh bits to disk while a separate Rhino instance manages its
    own lifecycle.

.PARAMETER NoBuild
    Skip the dotnet build step. Useful if you've already built and
    just want to recopy the existing artefacts.

.EXAMPLE
    PS> .\scripts\dev-build-local.ps1
    Builds Release, copies into the active Rhino plugin folder,
    relaunches Rhino.

.EXAMPLE
    PS> .\scripts\dev-build-local.ps1 -Configuration Debug -NoRestart
    Builds Debug and copies the artefacts but leaves Rhino alone
    (e.g. you've already restarted it manually).
#>
[CmdletBinding()]
param(
    [string]$CsprojPath,
    [ValidateSet('Debug', 'Release')]
    [string]$Configuration = 'Release',
    [string]$InstallRoot,
    [string]$Version,
    [switch]$NoRestart,
    [switch]$NoBuild
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

# ─── 0. Resolve paths ────────────────────────────────────────────────────────
$scriptDir = $PSScriptRoot
if (-not $scriptDir) { $scriptDir = Split-Path -Parent $MyInvocation.MyCommand.Path }
$repoRoot = Resolve-Path (Join-Path $scriptDir '..')

if (-not $CsprojPath) {
    $CsprojPath = Join-Path $repoRoot 'src\OrbitConnector.Rhino\OrbitConnector.Rhino.csproj'
}
if (-not (Test-Path $CsprojPath)) {
    throw "OrbitConnector.Rhino.csproj not found at: $CsprojPath`n" +
          "Pass -CsprojPath or run this script from inside an orbit-connectors-repo checkout."
}
$CsprojPath = (Resolve-Path $CsprojPath).Path
$projectDir = Split-Path -Parent $CsprojPath

# Version stamp from Directory.Build.props (single source of truth)
if (-not $Version) {
    $propsPath = Join-Path $repoRoot 'Directory.Build.props'
    if (Test-Path $propsPath) {
        $propsXml = [xml](Get-Content $propsPath -Raw)
        $stamped = $propsXml.SelectNodes('//OrbitConnectorVersion') |
                   Where-Object { $_.'#text' } |
                   Select-Object -First 1
        if ($stamped) { $Version = $stamped.'#text' }
    }
    if (-not $Version) { $Version = '0.0.0-dev' }
}

if (-not $InstallRoot) {
    $InstallRoot = Join-Path $env:LOCALAPPDATA 'Programs\OrbitConnector\Rhino'
}

# ─── 1. Locate the destination(s) ─────────────────────────────────────────────
# Two destinations:
#   a) $InstallRoot\$Version            -- the version this build identifies as
#   b) $registryTarget                  -- whatever folder Rhino currently loads
# Mirroring to (b) means the next Rhino launch always picks up the new bits
# without us having to re-register the plugin or bump $Version every time.
$pluginGuid = '4F3A2B1C-8E5D-4A9F-B6C2-1D7E3F4A5B6C'
$regKey = "HKCU:\Software\McNeel\Rhinoceros\8.0\Plug-Ins\$pluginGuid\PlugIn"
$registryRhpPath = $null
$registryTarget = $null
try {
    $regVal = Get-ItemProperty -Path $regKey -Name FileName -ErrorAction Stop
    if ($regVal.FileName) {
        $registryRhpPath = $regVal.FileName
        $registryTarget = Split-Path -Parent $registryRhpPath
    }
} catch {
    # Registry entry missing — Rhino has never loaded the plugin on this machine.
}

if (-not $registryTarget -and -not (Test-Path $InstallRoot)) {
    throw "ORBIT Rhino plugin install folder not found: $InstallRoot`n" +
          "Install at least one official build first (run an Inno installer once) " +
          "so the GUID-keyed plugin folder + registry entry exist, then re-run " +
          "this script. Alternatively pass -InstallRoot <path> to write somewhere else."
}

$primaryDest = Join-Path $InstallRoot $Version

# ─── 2. Stop Rhino ────────────────────────────────────────────────────────────
$rhinoExe = Join-Path $env:ProgramFiles 'Rhino 8\System\Rhino.exe'
if (-not (Test-Path $rhinoExe)) {
    Write-Warning "Rhino 8 not found at $rhinoExe. -NoRestart will be assumed."
    $NoRestart = $true
}

if (-not $NoRestart) {
    $rhinoProcs = @(Get-Process -Name Rhino -ErrorAction SilentlyContinue)
    if ($rhinoProcs.Count -gt 0) {
        Write-Host "Stopping Rhino ($($rhinoProcs.Count) process(es))..." -ForegroundColor DarkGray
        $rhinoProcs | Stop-Process -Force -ErrorAction SilentlyContinue
        # Give the OS a moment to release file locks on the .rhp.
        Start-Sleep -Milliseconds 800
    }
}

# ─── 3. Build ─────────────────────────────────────────────────────────────────
if (-not $NoBuild) {
    Write-Host "dotnet build  ($Configuration)  $CsprojPath" -ForegroundColor Cyan
    & dotnet build $CsprojPath -c $Configuration --nologo -v minimal
    if ($LASTEXITCODE -ne 0) {
        throw "dotnet build failed (exit $LASTEXITCODE). Plugin NOT updated; Rhino NOT restarted."
    }
}

# ─── 4. Locate build output ───────────────────────────────────────────────────
$tfm = 'net8.0-windows'
$outDir = Join-Path $projectDir "bin\$Configuration\$tfm"
if (-not (Test-Path $outDir)) {
    throw "Build output directory not found: $outDir"
}

# Files to copy. Same set the Inno installer ships (see installers/rhino).
# Filter to what's actually on disk so a Debug build (no Newtonsoft/Extensions
# DLLs in older configurations) doesn't blow up.
$fileGlobs = @(
    'OrbitConnector.Rhino.rhp',
    'OrbitConnector.Rhino.dll',
    'OrbitConnector.Rhino.pdb',
    'OrbitConnector.Rhino.deps.json',
    'Orbit.Objects.dll',
    'Orbit.Objects.pdb',
    'Orbit.Objects.xml',
    'Orbit.Sdk.dll',
    'Orbit.Sdk.pdb',
    'Orbit.Sdk.xml',
    'Newtonsoft.Json.dll',
    'Microsoft.Extensions.Logging.Abstractions.dll',
    'Microsoft.Extensions.DependencyInjection.Abstractions.dll'
)
$artefacts = $fileGlobs |
    ForEach-Object { Join-Path $outDir $_ } |
    Where-Object { Test-Path $_ }

if ($artefacts.Count -eq 0) {
    throw "No artefacts found under $outDir."
}
if (-not ($artefacts | Where-Object { $_ -like '*OrbitConnector.Rhino.rhp' })) {
    throw "Build succeeded but no OrbitConnector.Rhino.rhp under $outDir. " +
          "Check the RenameToRhp target in OrbitConnector.Rhino.csproj."
}

# ─── 5. Copy to the version folder we just built ─────────────────────────────
# The plugin always loads from whatever path Rhino's registry FileName points
# at. We deploy to $primaryDest ($InstallRoot\$Version, e.g. ...\0.1.19) and
# then (step 6) repoint the registry there. This guarantees that the folder
# Rhino loads == the version we built. Earlier this script left the registry
# pinned to a legacy folder (e.g. 0.1.17) while building 0.1.19, so fresh bits
# never reached the loaded folder if the mirror-copy silently failed.
if (-not (Test-Path $primaryDest)) {
    New-Item -ItemType Directory -Path $primaryDest -Force | Out-Null
}
foreach ($src in $artefacts) {
    Copy-Item -LiteralPath $src -Destination $primaryDest -Force
}

# Embedded assets (UI\wwwroot\*) are bundled into OrbitConnector.Rhino.dll
# as ManifestResource by the csproj's <EmbeddedResource> globs, so no
# wwwroot folder needs copying. The same goes for Resources\*.

# ─── 6. Point Rhino's registry at the folder we just deployed ────────────────
# ALWAYS repoint — never trust a stale registry value from an older version.
$newRhp = Join-Path $primaryDest 'OrbitConnector.Rhino.rhp'
try {
    if (-not (Test-Path $regKey)) { New-Item -Path $regKey -Force | Out-Null }
    Set-ItemProperty -Path $regKey -Name FileName -Value $newRhp -Type String -Force
} catch {
    Write-Warning "Could not update Rhino plugin registry entry: $_"
}

# ─── 6b. Verify the deployed artefacts match the freshly built ones ──────────
$srcRhp = Join-Path $outDir 'OrbitConnector.Rhino.rhp'
$dstRhp = Join-Path $primaryDest 'OrbitConnector.Rhino.rhp'
if ((Test-Path $srcRhp) -and (Test-Path $dstRhp)) {
    $s = Get-Item $srcRhp
    $d = Get-Item $dstRhp
    if ($s.Length -ne $d.Length -or $d.LastWriteTime -lt $s.LastWriteTime) {
        throw "Deploy verification FAILED: $dstRhp (len=$($d.Length), " +
              "$($d.LastWriteTime)) does not match freshly built $srcRhp " +
              "(len=$($s.Length), $($s.LastWriteTime)). The loaded plugin would be stale."
    }
    Write-Host "verified: $dstRhp matches build output (len=$($d.Length), $($d.LastWriteTime))" -ForegroundColor DarkGray
}

# ─── 7. Restart Rhino ────────────────────────────────────────────────────────
if (-not $NoRestart) {
    Start-Process -FilePath $rhinoExe | Out-Null
}

# ─── 8. One-line summary ─────────────────────────────────────────────────────
$rhinoTag = if ($NoRestart) { 'Rhino NOT restarted (-NoRestart)' } else { 'Rhino restarting' }
Write-Host "dev build v$Version copied to $primaryDest (registry repointed here); $rhinoTag" -ForegroundColor Green
