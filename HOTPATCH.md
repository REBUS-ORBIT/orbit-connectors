# Local hot-patch / dev build loop

Rapid inner loop for the ORBIT Rhino connector — bypass CI for in-flight
debugging, then push to GitHub when the change is correct.

## TL;DR

```powershell
# from the repo root
.\scripts\dev-build-local.ps1
```

Default: builds `Release`, copies the freshly built `.rhp` + sibling
DLLs into `%LOCALAPPDATA%\Programs\OrbitConnector\Rhino\<version>\`,
also mirrors them into whichever folder Rhino's registry currently
points at, then relaunches Rhino. Round-trip ≈ a few seconds.

## What it does

1. Closes Rhino if it's running (`Stop-Process -Name Rhino`).
2. Runs `dotnet build src\OrbitConnector.Rhino\OrbitConnector.Rhino.csproj -c Release`.
3. Copies the build output (`OrbitConnector.Rhino.rhp`, the matching
   `.dll`/`.pdb`, `Orbit.Objects.*`, `Orbit.Sdk.*`, `Newtonsoft.Json.dll`,
   `Microsoft.Extensions.*Abstractions.dll`) into:
   - `%LOCALAPPDATA%\Programs\OrbitConnector\Rhino\<version>\` — the
     standard Inno install layout, where `<version>` is read from
     `Directory.Build.props` (`OrbitConnectorVersion`).
   - The folder Rhino's registry currently points at
     (`HKCU\Software\McNeel\Rhinoceros\8.0\Plug-Ins\<plugin-guid>\PlugIn\FileName`).
     If that's already the same folder, no second copy.
4. Re-launches `Rhino.exe` so the user can immediately retry the
   send/receive flow.
5. Echoes `dev build copied to <path>; Rhino restarting`.

UI assets (`UI\wwwroot\*.html / .js / .css`) and `Resources\*` are
embedded into the `.rhp` as `ManifestResource` entries by the csproj's
`<EmbeddedResource>` globs, so no separate file copy is needed.

## Why this exists (vs CI)

The full release path is:

> push to GitHub → CI build → Inno installer → download installer →
> uninstall old version → install new version → restart Rhino

That's ~10 minutes per iteration, far too slow for things like
texture-probe / UV-mapping debugging where you genuinely have to try a
few permutations against an actual Rhino model.

This script trades release-grade packaging (no signed installer, no
uninstaller registration, no version bump on disk) for sub-second
iterations on your own dev machine.

## Intended workflow

```
1. Edit src\OrbitConnector.Rhino\**\*.cs (or UI\wwwroot\*.html / .js / .css)
2. .\scripts\dev-build-local.ps1
3. Test in Rhino — send / receive against a dev ORBIT server
4. Read the [ORBIT] log lines in Rhino's command window
5. Iterate (back to 1) until the behaviour is right
6. git commit + git push
7. Bump $(OrbitConnectorVersion) in Directory.Build.props
8. git tag v<new-version> && git push --tags
   (CI builds the installer + YAK + DMG and publishes a GitHub Release)
```

## Where things live on disk

```
%LOCALAPPDATA%\Programs\OrbitConnector\Rhino\
├── 0.1.16\                 ← prior installer (rollback target)
├── 0.1.17\                 ← prior installer (rollback target)
└── 0.1.18\                 ← what dev-build-local.ps1 writes by default
    ├── OrbitConnector.Rhino.rhp    ← Rhino loads this
    ├── OrbitConnector.Rhino.dll
    ├── OrbitConnector.Rhino.pdb
    ├── Orbit.Objects.dll  (+ .pdb, .xml)
    ├── Orbit.Sdk.dll      (+ .pdb, .xml)
    ├── Newtonsoft.Json.dll
    ├── Microsoft.Extensions.Logging.Abstractions.dll
    └── Microsoft.Extensions.DependencyInjection.Abstractions.dll
```

The Rhino plug-in registry pointer:

```
HKCU\Software\McNeel\Rhinoceros\8.0\Plug-Ins\
    {4F3A2B1C-8E5D-4A9F-B6C2-1D7E3F4A5B6C}\PlugIn\FileName
```

is whatever the most-recently-installed Inno installer wrote. The
script honours that — your dev build is mirrored into both the
version folder it identifies as AND the folder Rhino actually loads
from, so a Rhino restart picks up the bits without touching the
registry. Roll back to a prior official build by re-running its
installer's `unins000.exe` then installing the version you want.

## Pre-flight checks

The script will refuse to run if:

- `OrbitConnector.Rhino.csproj` can't be found (override with
  `-CsprojPath <path>`).
- The Inno install root (`%LOCALAPPDATA%\Programs\OrbitConnector\Rhino\`)
  doesn't exist AND Rhino's registry has no entry for the plugin GUID.
  This means you've never installed an official build on this machine —
  do that once first (any v0.1.x installer from
  [REBUS-ORBIT/orbit-connectors releases](https://github.com/REBUS-ORBIT/orbit-connectors/releases)),
  then the script will know where to drop bits.
- `dotnet build` fails — exit code propagates and Rhino is NOT
  restarted, so you don't end up with stale-but-working bits in front
  of a broken source tree.

## Useful flags

```powershell
# Build Debug instead of Release
.\scripts\dev-build-local.ps1 -Configuration Debug

# Copy the last-built artefacts without rebuilding
.\scripts\dev-build-local.ps1 -NoBuild

# Build but don't restart Rhino (e.g. you closed it yourself)
.\scripts\dev-build-local.ps1 -NoRestart

# Override the destination version folder
.\scripts\dev-build-local.ps1 -Version 0.1.18-dev1

# Override the install root (if your machine has a non-standard layout)
.\scripts\dev-build-local.ps1 -InstallRoot 'C:\some\other\path'
```

## Privacy / safety

- Writes only to your local Rhino plugin folder + your local
  HKCU registry hive.
- Nothing leaves the machine. No GitHub, no GHCR, no upload.
- The script never modifies source files in `src\`. It only reads
  `Directory.Build.props` to resolve the version stamp.
- Stop-Process targets the `Rhino` image name. If you have an
  unrelated process literally called `Rhino.exe`, it gets caught
  too — close it before running, or use `-NoRestart` and shut Rhino
  down yourself.

## Companion stub

`Connectors/scripts/dev-build-local.ps1` (in the legacy in-tree
working copy under `D:\Documents\Claude\REBUS System\ORBIT\Connectors\`)
is a thin wrapper that re-invokes the canonical script in this
repo. Either entry point works; the canonical implementation lives
here.
