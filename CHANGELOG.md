# Changelog

Each release is named **"ORBIT Connectors v<X.Y.Z>"** and contains
per-connector, per-OS installer artifacts following the naming
convention `OrbitConnector-<Host>-Setup-v<Version>.<exe|dmg>`. YAK
files for Rhino keep their McNeel-imposed naming
(`orbit-connector-<v>-rh8-<platform>.yak`) and ship alongside the
canonical artifacts.

The release CI (`.github/workflows/release.yml`) extracts the section
matching the pushed tag (e.g. `## v0.1.1`) and uses it as the GitHub
Release body, so the format of each entry below matters.

## v0.1.9 — Plugin branding: ORBIT logo and publisher metadata

Populates the fields shown in Rhino's **Options → Plug-ins** list and
wires the ORBIT logo as the plug-in icon.

### Publisher metadata

Three new `[assembly: PlugInDescription(...)]` attributes in
`Properties/AssemblyInfo.cs` fill in the Rhino Plugin Manager's
contact fields:

| Field | Value |
|---|---|
| Organization / Publisher | REBUS Industries |
| Email | IT@rebus.industries |
| Website | https://rebus.industries |

`OrbitConnectorPlugin` also overrides the `Email` and `Website`
virtual properties so the same values are returned when Rhino queries
the plug-in programmatically.

The csproj `<Authors>` / `<Company>` fields are updated from
`REBUS-ORBIT` to `REBUS Industries` so the assembly-level company
attribute aligns with the above.

### ORBIT logo

The embedded `Resources/orbit-logo.png` (shipped since v0.1.2) is now
wired to two Rhino icon surfaces:

1. **Plugin Manager icon** — `OrbitConnectorPlugin.Icon` is overridden
   to load the PNG from the manifest resource, scale it to 32 × 32,
   and return a `System.Drawing.Icon`. The icon is loaded once and
   cached; failure falls back to Rhino's default (generic icon), so
   a missing or corrupt resource cannot break plug-in load.

2. **Panel rail icon** — `Panels.RegisterPanel` is updated to pass the
   same icon so the ORBIT panel tab in Rhino's side dock shows the
   logo instead of a blank square.

The `[assembly: PlugInDescription(DescriptionType.Icon, "OrbitConnector.Rhino.Resources.orbit-logo.png")]`
attribute is also added so Rhino's scanner can surface the icon before
the plug-in is fully loaded.

### Safety note on `System.Drawing`

The `System.Drawing.Icon` type is already referenced in our assembly's
metadata via the existing `Panels.RegisterPanel(…, Icon)` call (added
in v0.1.7). Adding the `Icon` property override does not introduce a
new assembly dependency. The csproj continues to use
`ExcludeAssets="runtime" PrivateAssets="all"` on
`System.Drawing.Common` so the DLL is never bundled alongside the
`.rhp` — Rhino's shared `Microsoft.WindowsDesktop.App` framework
provides it at runtime, the same as before.

## v0.1.8 — Restore full Rhino panel functionality (regression fix)

**v0.1.7 made the plug-in load again, but the panel content itself was a
two-button "+ Send / + Receive" stub with a `MessageBox.Show("project
picker coming soon")` placeholder behind both buttons.** The full
WebView-based UI (project / model picker, layer tree, send pipeline,
receive scaffold, real Speckle/ORBIT mesh + material + UV upload) had
been silently dropped between the May 20 working `.rhp` and the
v0.1.0-v0.1.7 installer-packaging release branch. v0.1.8 restores the
working panel and re-applies the v0.1.2 / v0.1.6 / v0.1.7 polish on
top.

### Root cause

When the `orbit-connectors` repo was scaffolded for the multi-connector
release pipeline, the `src/OrbitConnector.Rhino/UI/OrbitEtoPanel.cs`
file was committed as a 79-line Eto stub rather than ported from the
working source-of-truth in the parent ORBIT meta-repo
(`ORBIT/Connectors/src/OrbitConnector.Rhino/UI/OrbitEtoPanel.cs`,
527 lines, last touched 2026-05-20 commit `245fb65`). Every later
release branch built on top of that stub. The v0.1.2 work added a
version label + GitHub-API "Check for updates" link **on top of the
stub**, the v0.1.3 - v0.1.7 work fixed installer + assembly-loading
issues — but nobody noticed the panel itself was a placeholder
because the plug-in didn't successfully load until v0.1.7.

In addition to the panel, the following files were either missing
from `orbit-connectors` entirely or out-of-date relative to the
working source:

- **Missing converters** (12 files): `RhinoMaterialHelper.cs`,
  `RhinoNativeEncoder.cs`, `RhinoBrepDisplayMeshes.cs`,
  `RhinoCurveConverter.cs`, `RhinoExtrusionConverter.cs`,
  `RhinoInstanceConverter.cs`, `RhinoObjectMeshes.cs`,
  `RhinoPointCloudConverter.cs`, `RhinoPointConverter.cs`,
  `RhinoSubDConverter.cs`, `RhinoSurfaceConverter.cs`,
  `RhinoTextConverter.cs`. Without these the connector could only
  send `Mesh` / `Brep` / fallback geometry; curves, points, instances,
  text, point clouds, sub-Ds, surfaces, extrusions all fell off.
- **Missing WebView UI**: `UI/wwwroot/index.html`. Without the
  embedded HTML the panel renders nothing but the static fallback
  "ORBIT Connector could not load UI resources" error.
- **Stale baseline scaffolds** for `Auth/*`, `Models/*`, `Commands/*`,
  `Converters/ConversionContext.cs`, `Converters/ToOrbit/{IRhinoToOrbitConverter,RhinoBrepConverter,RhinoFallbackConverter,RhinoMeshConverter}.cs`,
  `Pipeline/RhinoSendPipeline.cs`, `Properties/AssemblyInfo.cs`. Every
  one of these files differed from the working ORBIT/Connectors copy
  (verified by MD5 hash compare).

### Fix

`UI/OrbitEtoPanel.cs` and the 24 supporting files listed above are now
**byte-for-byte the May 20 working baseline**, with a thin merge layer
that preserves every additive v0.1.2 / v0.1.6 / v0.1.7 change:

- **Version label + "Check for updates" link** are now rendered inside
  the WebView header (top-right of the panel) by `index.html`, with
  matching styling. The C# side exposes `OrbitConnectorPlugin.Version`
  via a new `getVersion` JS dispatch action and answers the "check for
  updates" click via a new `checkUpdates` action that hits
  `https://api.github.com/repos/REBUS-ORBIT/orbit-connectors/releases/latest`,
  parses the tag through the same `System.Version`-based comparator
  v0.1.2 used, and replies with `{ kind: 'uptodate' | 'newer' | 'failed' }`
  for the JS to render. The 10-second timeout, GitHub User-Agent,
  pre-release suffix stripping, and graceful HTTP-error handling
  carried over verbatim.
- **Diagnostic load log** (`%LOCALAPPDATA%\OrbitConnector\load.log`)
  in `OrbitConnectorPlugin.cs` is **untouched** — that file kept the
  v0.1.7 version with `Log()` instrumentation around cctor / ctor /
  OnLoad / panel registration / doc-event wiring.
- **`OrbitConnector.Rhino.csproj`** keeps the v0.1.6
  `<CopyLocalLockFileAssemblies>true</CopyLocalLockFileAssemblies>`
  fix and the v0.1.7 `<PackageReference Include="System.Drawing.Common"
  ExcludeAssets="runtime" PrivateAssets="all" />` fix. The only edit
  is a new `<EmbeddedResource Include="UI\wwwroot\**\*" />` entry so
  the WebView can resolve the embedded `index.html` at runtime.
- **`Properties/Resources.cs`** is unchanged from v0.1.7 — the WebView
  panel doesn't consume the `OrbitIcon16Bytes` byte[] placeholder, so
  there's no metadata regression risk in keeping it. The orbit logo
  ships through `Resources/orbit-logo.png` (also unchanged).
- **`installers/rhino/inno/OrbitConnector.Rhino.iss`** is unchanged
  from v0.1.7 — the registry write-out, `%LOCALAPPDATA%\Programs\
  OrbitConnector\Rhino\<v>\` install path, `UsePreviousAppDir=no`,
  `LoadProtection` clear, and orphan-YAK-dir cleanup are all correct.

### Build / SDK alignment

The May 20 send pipeline references SDK types
(`Orbit.Objects.BuiltElements.View3D`, `Orbit.Objects.Geometry.Vector`,
plus `OrbitObject.{CollectionType,LayerPath,LayerColor,Views}`) that
exist in the parent ORBIT meta-repo's local SDK at commit `245fb65`
but were never pushed to the standalone `orbit-sdk` repository on
GitHub. Cloning `orbit-sdk` from GitHub in CI therefore yielded an
SDK that was missing the surface the connector needed, and the
build failed before any artefacts were produced.

To keep `orbit-connectors` self-contained without forcing an
out-of-band push to `orbit-sdk`, this release vendors the May 20
SDK source under `vendor/SDK/src/` (36 files copied verbatim from
`245fb65:SDK/src/`). `Directory.Build.props` defaults
`OrbitSdkLocal=true` and `OrbitSdkPath=$(MSBuildThisFileDirectory)
vendor\SDK\src` so a clean clone builds without needing
`ORBIT_SDK_LOCAL=1` or a sibling SDK clone. The CI workflow's
`Clone ORBIT SDK` step is now a no-op for `OrbitSdkPath` resolution
but stays in place as documentation for the future migration back
to a separate SDK repo (just delete `vendor/SDK/`, repoint
`OrbitSdkPath`, and remove this paragraph from the changelog).

### What works again in v0.1.8

After installing v0.1.8 the ORBIT panel renders the full functional UI
that the May 20 `.rhp` shipped:

- ORBIT brand header with "v0.1.8" and "Check for updates" in the
  top-right corner.
- OAuth + Personal Access Token login against
  `https://orbit.rebus.industries` (or any custom URL).
- Per-document "send" and "receive" cards persisted into the Rhino
  document strings (so they survive document save/reopen).
- Project picker dropdown populated from the user's ORBIT account,
  including inline "+" to create a new project on the fly.
- Model picker dropdown populated from the selected project, plus
  inline "+" to create a new model.
- Layer selection mode picker: All / By layer / Selection. The
  layer tree pulls from the active Rhino doc with the same
  hierarchy display the May 20 build used.
- Send button drives the full `RhinoSendPipeline`: meshing, PBR
  material extraction, texture blob upload, layer collection
  hierarchy, named views, and a real Speckle/ORBIT version commit
  posted to the chosen model.
- Live progress + status reporting back to the panel; result URL
  exposes an "Open in ORBIT" link that uses the host's default
  browser via `Process.Start(url, UseShellExecute=true)`.
- Footer-driven update check works the same as v0.1.2: surfaces
  the latest GitHub release, asks whether to open the releases
  page, degrades gracefully if the host is offline / GitHub is
  rate-limited / response can't be parsed.

### Migration

Users on v0.1.6 / v0.1.7 should:

1. Open **Add/Remove Programs**, find `ORBIT Connector for Rhino`,
   click **Uninstall**.
2. Close Rhino.
3. Run `OrbitConnector-Rhino-Setup-v0.1.8.exe`. The installer places
   files at `%LOCALAPPDATA%\Programs\OrbitConnector\Rhino\0.1.8\`,
   refreshes the HKCU plug-in registry entry, and clears any stale
   `LoadProtection` marker carried over from a prior broken install.
4. Start Rhino. The footer reads `v0.1.8`, `+ Send` and `+ Receive`
   open the real card config (project / model / layer pickers — not
   a "coming soon" message box), and a successful send produces a
   real ORBIT version commit.
5. If the panel ever again degrades to a blank surface, paste the
   tail of `%LOCALAPPDATA%\OrbitConnector\load.log` into a GitHub
   issue.

The plug-in registry GUID is unchanged from v0.1.3
(`4F3A2B1C-8E5D-4A9F-B6C2-1D7E3F4A5B6C`), so an in-place upgrade also
works — but a clean uninstall + reinstall avoids any leftover state
from the v0.1.0-v0.1.7 broken-load loop.

## v0.1.7 — Rhino plug-in actually loads now (installer registry + System.Drawing metadata)

**v0.1.6 still threw "initialization failed" on Rhino startup despite the
NuGet transitive DLL bundling. This release fixes the two distinct
root causes that survived that hotfix and adds a permanent diagnostic
log so the next regression isn't a guessing game.**

### Root cause #1 — installer wrote an incomplete plug-in registry entry

`installers/rhino/inno/OrbitConnector.Rhino.iss` v0.1.3 .. v0.1.6 wrote
only `Name` and `FileName` under
`HKCU\Software\McNeel\Rhinoceros\8.0\Plug-ins\<guid>\`. That's enough
for Rhino's PluginManager to *list* the plug-in but **not** enough for
Rhino to actually load it at startup. Rhino's plug-in scanner needs
`LoadMode`, `Type`, `IsDotNETPlugIn`, `Description`, and `EnglishName`
to decide a particular entry is a load candidate.

Reproduced by bisecting against the official v0.1.6 `.rhp` on a clean
machine: with only `Name`+`FileName` the static cctor on
`OrbitConnectorPlugin` never fires, no `OnLoad` runs, no error dialog —
but the user sees nothing happen either, which Rhino sometimes reports
back as the generic "initialization failed" dialog if the same GUID
had previously failed to load for any reason (Rhino caches the failure
in `HKCU\...\Global Options\Plug-ins\<guid>\LoadProtection`).

**Fix:** the installer now writes the full set of values Rhino would
normally back-populate after a successful first load. Specifically:

```
LoadMode        = 1   (PlugInLoadTime.LoadAtStartup)
Type            = 16  (RhinoPlugInType.General)
IsDotNETPlugIn  = 1
AddToHelpMenu   = 0
EnglishName     = OrbitConnector.Rhino
Description     = ORBIT Connector for Rhino
```

Plus the existing `Name` and `FileName`. Rhino layers
plug-in-discovered values (CommandList, Panels, exact display Name) on
top of these on first successful load.

The installer also defensively clears
`Global Options\Plug-ins\<guid>\LoadProtection` on every install so
any stale "previously failed, never load again" marker from a prior
broken install is wiped, and re-points `Plug-ins\<guid>\PlugIn\FileName`
at the freshly-installed payload so Rhino doesn't keep hitting a stale
path cached from a previous broken install.

### Root cause #2 — bundled `System.Drawing.Common.dll` collided with the shared framework copy

`OrbitConnector.Rhino.csproj` in v0.1.6 referenced
`System.Drawing.Common` as a plain `<PackageReference>`, and the v0.1.6
`<CopyLocalLockFileAssemblies>true</CopyLocalLockFileAssemblies>`
hotfix happily copied `System.Drawing.Common.dll 8.0.0.0` into the
installer payload. On disk, that DLL sat right next to the `.rhp`.

Rhino's plug-in custom `AssemblyLoadContext` resolves siblings first.
That gave the .rhp's load a `System.Drawing.Common` identity distinct
from the copy `Microsoft.WindowsDesktop.App` had already loaded into
the default ALC during Rhino startup (the one RhinoCommon itself is
linked against). The legacy `System.Drawing` facade's
`[TypeForwardedFrom]` attributes are stamped `Version=0.0.0.0`, and
the custom ALC's binder couldn't unify that synthetic version across
the now-distinct sibling and default-ALC identities. The result was a
`FileNotFoundException` for
`'System.Drawing.Common, Version=0.0.0.0, PublicKeyToken=cc7b13ffcd2ddd51'`
thrown **during type-metadata scan**, before any of our code (cctor,
ModuleInitializer, OnLoad) had a chance to run. Rhino caught the
TypeLoadException, swallowed the inner detail, and shipped the
generic "initialization failed" dialog.

(Verified by reflection-loading the v0.1.6 `.rhp` in an isolated
`net8.0-windows` `AssemblyLoadContext` mirroring Rhino's: the inner
`FileNotFoundException` for the 0.0.0.0 `System.Drawing.Common`
request surfaces immediately.)

**Fix:**
- `OrbitConnector.Rhino.csproj` keeps `System.Drawing.Common` as a
  `PackageReference` but with `ExcludeAssets="runtime"` and
  `PrivateAssets="all"`. The package is only needed at **compile
  time** because `Rhino.UI.Panels.RegisterPanel`'s signature takes a
  `System.Drawing.Icon`. At runtime, Rhino's load context delegates
  to the default ALC, which already has
  `System.Drawing.Common 8.0.0.0` loaded from
  `Microsoft.WindowsDesktop.App` — that's the only copy Rhino sees,
  and the 0.0.0.0 forwarder is satisfied from it.
- `Properties/Resources.cs` no longer references `System.Drawing.*`
  in its public surface. The future-icon placeholder is now
  `byte[]? OrbitIcon16Bytes`; consumers can turn it into the
  platform-appropriate type via `Eto.Drawing.Bitmap` (or whatever
  the call site needs). Eto.Forms doesn't need `System.Drawing`
  anywhere, and removing the metadata reference is a second-belt
  defence even if Rhino's ALC ever changes how it resolves siblings.

### Diagnostic load log (permanent)

So the next regression doesn't take three releases to find: every
plug-in load now appends to
`%LOCALAPPDATA%\OrbitConnector\load.log`. Lines tag the cctor, the
plug-in ctor, `OnLoad` entry/exit, panel registration, document
event wiring, and any caught exception (including full inner-chain
and stack). All I/O is wrapped in `try/catch` — a logging failure
will never break plug-in load. If a user reports another failure,
the file is one path to copy + paste.

A healthy load looks like:

```
[hh:mm:ss.fff] cctor v0.1.7 runtime=.NET 8.0.14
[hh:mm:ss.fff] ctor done
[hh:mm:ss.fff] OnLoad enter
[hh:mm:ss.fff] registering OrbitEtoPanel
[hh:mm:ss.fff] panel registered
[hh:mm:ss.fff] doc events wired
[hh:mm:ss.fff] OnLoad ok
```

(A pre-cctor `TypeLoadException` like the v0.1.6 one will NOT show
up here -- the cctor never gets to run -- but any failure inside
`OnLoad` itself or any exception that surfaces post-cctor will be
logged with full inner-chain + stack.)

### Local verification

Built the v0.1.7 payload on Windows 11 / Rhino 8.30 with Inno Setup 6,
uninstalled v0.1.6, wiped every HKCU plug-in registry entry, installed
the v0.1.7 `.exe`, and started Rhino. Both first-run and second-run
startups produced the healthy `load.log` above, no error dialog, panel
opens via `_Orbit` / Panels menu, footer shows `v0.1.7`.

### Recovery for v0.1.x users

1. Open **Add/Remove Programs**, find `ORBIT Connector for Rhino`
   v0.1.6 (or older), click **Uninstall**.
2. Close Rhino.
3. Run `OrbitConnector-Rhino-Setup-v0.1.7.exe`. It installs to
   `%LOCALAPPDATA%\Programs\OrbitConnector\Rhino\0.1.7\` and writes
   the full plug-in registry entry.
4. Start Rhino. The plug-in error dialog must not appear; the ORBIT
   panel auto-registers and the footer reads `v0.1.7`.
5. If anything looks wrong, paste
   `%LOCALAPPDATA%\OrbitConnector\load.log` in a new issue.

## v0.1.6 — Rhino plug-in init hotfix (bundle NuGet transitive DLLs)

**v0.1.5 installed cleanly but the plug-in itself wouldn't load:**

> Rhino Plug-in Error
> C:\Users\…\OrbitConnector.Rhino.rhp
> **Unable to load OrbitConnector.Rhino.rhp plug-in: initialization failed.**

Root cause: `src/OrbitConnector.Rhino/OrbitConnector.Rhino.csproj` was set
to `<CopyLocalLockFileAssemblies>false</CopyLocalLockFileAssemblies>`. As a
result every NuGet transitive runtime DLL was excluded from the build
output, and the installer payload landed on the user's machine with only
`OrbitConnector.Rhino.{dll,rhp,pdb,deps.json}` + `Orbit.Sdk.dll` +
`Orbit.Objects.dll`. Missing from the installed `0.1.6/` folder:

- `Newtonsoft.Json.dll`
- `Microsoft.Extensions.Logging.Abstractions.dll`
- `Microsoft.Extensions.DependencyInjection.Abstractions.dll`
- `System.Drawing.Common.dll`
- `Microsoft.Win32.SystemEvents.dll`

Rhino 8's `System/` directory does happen to ship `Newtonsoft.Json.dll`,
but it does **not** ship any of the `Microsoft.Extensions.*` assemblies
that `Orbit.Sdk` references for `ILogger` injection. So when Rhino reflected
over the `.rhp` (via `Assembly.GetTypes()`) to find the `PlugIn` subclass,
the CLR tried to load every assembly the panel + SDK types reference,
failed to resolve `Microsoft.Extensions.Logging.Abstractions`, raised
`ReflectionTypeLoadException`, and Rhino surfaced the generic
"initialization failed" dialog before `OrbitConnectorPlugin.OnLoad` ever
got a chance to run. The `try/catch` in `OnLoad` could not have caught
this — the type that owns `OnLoad` itself never finished loading.

### Fix

`src/OrbitConnector.Rhino/OrbitConnector.Rhino.csproj`:

```xml
<CopyLocalLockFileAssemblies>true</CopyLocalLockFileAssemblies>
```

`dotnet build -c Release` now copies every NuGet runtime DLL into
`bin/Release/net8.0-windows/`. Rhino-provided assemblies (`RhinoCommon`,
`Eto`, `Rhino.UI`) stay out via the existing `Private=false` /
`ExcludeAssets="runtime"` plumbing, so we don't double-bundle and risk
type-identity drift against whatever Rhino loaded at startup. Inno Setup
continues to copy everything from the build output directory verbatim, so
no installer changes were needed.

### Recovery for v0.1.5 users

1. Open **Add/Remove Programs**, find `ORBIT Connector for Rhino` v0.1.5,
   click **Uninstall**.
2. Close Rhino.
3. Run `OrbitConnector-Rhino-Setup-v0.1.6.exe`. It installs to
   `%LOCALAPPDATA%\Programs\OrbitConnector\Rhino\0.1.6\` (same per-user
   layout introduced in v0.1.5) and registers the HKCU plug-in entry.
4. Start Rhino. The plug-in error dialog should not appear; the ORBIT
   panel should register and display `v0.1.6` in its footer.

## v0.1.5 — Installer hotfix follow-up (force new install path on upgrade)

**Do not use v0.1.4.** It shipped with one missing piece that caused an
in-place upgrade from v0.1.3 to silently re-write all the new payload back
into the OLD YAK-managed directory before the cleanup hook then DelTree'd
the parent and wiped the install. v0.1.5 fixes this; use it directly.

The bug: Inno Setup, by default, when it detects a previous install with
the same `AppId`, uses that install's `InstallLocation` as `{app}` and
ignores the script's `DefaultDirName`. So a v0.1.4 install on a machine
that previously had v0.1.3 ended up with `{app}` set to
`%APPDATA%\McNeel\Rhinoceros\packages\8.0\OrbitConnector\0.1.3\` (the
v0.1.3 install dir), wrote the payload there, and the new
`CleanupOrphanYakDir` hook then deleted the parent — taking the just-
installed v0.1.4 payload with it.

Three fixes in `installers/rhino/inno/OrbitConnector.Rhino.iss`:

- **`UsePreviousAppDir=no`** in `[Setup]` — always honour `DefaultDirName`,
  regardless of any `InstallLocation` value carried over from a previous
  install. With this, the payload is always written to
  `%LOCALAPPDATA%\Programs\OrbitConnector\Rhino\<v>\`.
- **Removed `PrivilegesRequiredOverridesAllowed=dialog`** — prevents the
  installer from auto-elevating to admin (and writing HKLM uninstall keys
  + ProgramData Start Menu shortcuts) when a previous admin install of
  the same `AppId` is on the machine. Every install is now per-user, full
  stop.
- **Safety belt in `CleanupOrphanYakDir`** — refuses to delete the YAK-
  managed dir if `{app}` is somehow inside it. Should never trigger now
  that `UsePreviousAppDir=no` forces `{app}` into the new path; defensive
  against future regressions re-introducing the v0.1.4 self-destruct.

### Recovery for v0.1.3 / v0.1.4 users

Same path as before, but use the v0.1.5 `.exe`:

1. Open **Add/Remove Programs**, find any `ORBIT Connector for Rhino`
   entry, click **Uninstall**. (If both a v0.1.3 admin install and a
   broken v0.1.4 user install are present, uninstall both.)
2. Make sure Rhino is **closed** before the next step.
3. Run `OrbitConnector-Rhino-Setup-v0.1.5.exe`. The installer places
   files at `%LOCALAPPDATA%\Programs\OrbitConnector\Rhino\0.1.5\`,
   sweeps any leftover `%APPDATA%\McNeel\Rhinoceros\packages\8.0\
   OrbitConnector\` folder out of the way, and rewrites the HKCU
   plug-in registry entry to point at the new install path.
4. Start Rhino. The connector auto-loads on startup; verify the ORBIT
   panel shows up and reports `v0.1.5` in its footer.

## v0.1.4 — Installer hotfix (move install path out of Rhino's YAK-managed dir)

- **Critical fix:** Installer no longer drops files into Rhino's YAK-managed
  package directory (`%APPDATA%\McNeel\Rhinoceros\packages\8.0\`). Rhino's
  Package Manager scans that path on every startup and treats any subfolder
  it didn't install itself as an "uninstalled package" and wipes it — Rhino's
  startup log on a v0.1.3 install showed the smoking gun:
  ```
  [PackageManager] Cleaning up uninstalled packages...
  [PackageManager] Removing OrbitConnector
  ```
  Net effect on v0.1.3: the `.rhp` got deleted before Rhino's plug-in loader
  could find it on the next launch, the auto-register registry key pointed at
  a now-nonexistent file, and the Start Menu shortcut for "Install in Rhino 8"
  became a broken link. Files now install to
  `%LOCALAPPDATA%\Programs\OrbitConnector\Rhino\<version>\` instead, which is
  outside every Rhino-managed directory tree. The HKCU plug-in registry entry
  continues to point Rhino at the correct `.rhp`.
- **Post-install cleanup:** any orphan v0.1.0 - v0.1.3 folder still sitting in
  the YAK-managed dir is removed during install. Rhino must be closed for the
  cleanup to run; if the installer detects a running Rhino it shows a popup
  asking the user to close Rhino and re-run, or delete the folder manually.
- **Start Menu:** added an `ORBIT Connector Updates` URL shortcut pointing at
  `https://github.com/REBUS-ORBIT/orbit-connectors/releases/latest`. Its
  target is a URL rather than a file, so it survives even if the `.rhp` is
  ever deleted — useful as an always-present marker that the install
  completed and as a one-click path to the next release.

### Recovery instructions for v0.1.3 users

If you installed v0.1.3 and Rhino's `[PackageManager] Removing OrbitConnector`
log line ate your install:

1. Open **Add/Remove Programs**, find **ORBIT Connector for Rhino**, click
   **Uninstall**. (This removes the broken HKCU registry entry left over from
   v0.1.3.)
2. Make sure Rhino is **closed** before the next step — the v0.1.4 installer's
   YAK-dir cleanup needs exclusive access to delete the orphan folder.
3. Run the v0.1.4 `OrbitConnector-Rhino-Setup-v0.1.4.exe`. The installer will
   place files at `%LOCALAPPDATA%\Programs\OrbitConnector\Rhino\0.1.4\`, sweep
   any leftover folder under `%APPDATA%\McNeel\Rhinoceros\packages\8.0\
   OrbitConnector\` out of the way, and rewrite the HKCU plug-in registry
   entry to point at the new install path.
4. Start Rhino. The connector auto-loads on startup; verify the ORBIT panel
   shows up and reports `v0.1.4` in its footer.

## v0.1.3 — Auto-register Rhino plug-in on install

- **Inno Setup installer now writes Rhino's plug-in discovery keys** under
  `HKCU\Software\McNeel\Rhinoceros\8.0\Plug-ins\{<plugin-guid>}` (with the
  `Name` and `FileName` values Rhino reads on startup), so the connector
  loads automatically on the next Rhino launch. No more manual drag-drop
  of the `.rhp` file or running the "Install in Rhino 8" Start Menu
  shortcut after every install.
- **"Install in Rhino 8" Start Menu shortcut retained as a manual
  fallback** — useful when Rhino is running during install or the user
  wants to re-register against a different Rhino version. Comment text
  updated to reflect that it's no longer the primary registration path.
- **Stable plug-in identity GUID added to the Rhino assembly**
  (`Properties/AssemblyInfo.cs` — `[assembly: Guid("4F3A2B1C-...")]`).
  Rhino reads this attribute from the compiled `.rhp` and uses it as the
  plug-in's persistent ID. The Inno Setup script's `PluginGuid` define
  matches it; both must move together if ever changed (which they
  shouldn't be).
- **Uninstall cleanly removes the registry entry** via Inno Setup's
  `uninsdeletekey` flag.

### Upgrading from v0.1.2

The cleanest path is **uninstall v0.1.2 first** (Add/Remove Programs ->
"ORBIT Connector for Rhino" -> Uninstall) and then run the v0.1.3 `.exe`.
An in-place upgrade also works — v0.1.3's installer overwrites the
registry key on top of v0.1.2's missing one — but the uninstall+install
path leaves the cleanest set of files on disk.

## v0.1.2 — Lockstep versioning + Rhino UI polish

- **Lockstep versioning policy** — every connector now ships with the same
  version stamp as the release tag, regardless of which connector's code
  actually changed. Bumping the version is a release-pipeline action: push
  `vX.Y.Z`, and CI builds + tags every connector with that version. See
  [`RELEASE_POLICY.md`](RELEASE_POLICY.md) for the full rationale.
- **Single source of truth** for the version: `OrbitConnectorVersion` in
  the repo-root [`Directory.Build.props`](Directory.Build.props). The
  Rhino csproj no longer hardcodes `<Version>` — it inherits from the
  property. `installers/rhino/build-yak.ps1` and `build-mac.sh` now pass
  `-p:OrbitConnectorVersion=$VERSION` to MSBuild, which flows into
  `AssemblyVersion`, `FileVersion`, and `InformationalVersion` on the
  produced `.rhp`. The YAK manifest and Inno Setup script keep getting
  the same version through their existing `-Version` / `/DConnectorVersion`
  parameters.
- **Version label in the Rhino plugin UI** — the Orbit Eto panel footer
  now reads `v<X.Y.Z>` in muted grey text, sourced at runtime from
  `OrbitConnectorPlugin.Version`. Users can finally tell which build of
  the connector they're running without digging into the YAK package
  manager.
- **ORBIT logo in the Rhino plugin UI** — the ORBIT brand image is now
  embedded into the connector assembly (`Resources/orbit-logo.png`) and
  rendered in the top-left of the panel next to the panel title. Adds
  visual identity to the plug-in panel that previously had only a plain
  text header.
- **Rhino panel: "Check for updates" link** surfaces the latest GitHub
  release (`https://api.github.com/repos/REBUS-ORBIT/orbit-connectors/releases/latest`)
  and prompts the user to download. The link sits next to the version
  label in the header; on click it disables itself, fetches the latest
  release tag on a background thread with a 10s timeout, normalises both
  tag and current version through `System.Version`, and shows one of
  three message boxes: "up to date", "update available — open releases
  page?", or "couldn't check for updates" with the underlying error.
  Network failures (offline, GitHub rate-limited, timeout) degrade
  gracefully and re-enable the link.

## v0.1.1 — Multi-connector release pipeline

- Release naming standardised: each release is now "ORBIT Connectors v<X.Y.Z>"
  containing per-connector, per-OS installers.
- Installer artifact naming: `OrbitConnector-<Host>-Setup-v<Version>.<exe|dmg>`.
- Scaffolded Vectorworks connector — installer build skeleton ships placeholder; plugin source TODO.
- Scaffolded Unreal Engine 5 connector — installer build skeleton ships placeholder; plugin source TODO.
- macOS `.dmg` build path via `hdiutil` for every connector.
- CI: 6 parallel build jobs (Rhino / Vectorworks / UE5 × Windows / macOS)
  feeding a single release job that auto-populates the body from `CHANGELOG.md`.
- Switched `orbit-sdk` clone in CI from PAT-authenticated to tokenless
  (the SDK is now a public repo).

### Caveats

- Vectorworks and UE5 plug-in source is empty — the scaffolded
  installers ship a "coming soon" `README.txt` and nothing else.
- The Rhino macOS YAK build is still gated on splitting the csproj
  into `Core` / `Windows` / `Mac` projects — see
  `installers/rhino/MACOS.md`. The macOS `.dmg` produced by this
  release wraps a "use the YAK package" README rather than a real
  Mac `.pkg`.
- All installers ship **unsigned** — Windows code signing and Apple
  notarisation are parked across every connector for v0.1.x.

## v0.1.0 — Initial public release

- First public ORBIT connector for Rhino 8 (Windows) and Rhino 8 (macOS scaffold).
- Geometry support: Mesh, Brep, Curve, Surface, SubD, Extrusion, PointCloud, Point, Text, Block Instances.
- PBR materials with diffuse / metallic-roughness / emissive / normal textures uploaded as blobs.
- Layer hierarchy with per-object `layerColor` / `layerPath`.
- YAK and Inno Setup installers for Windows.
- macOS YAK scaffold — requires testing on a real Mac before first stable release.

### Installers

- **YAK (Windows, Rhino 8):** `orbit-connector-0.1.0-rh8-win.yak`
  - Recommended distribution. Drop into Rhino via `_PackageManager` or
    install with `yak install <file>.yak` from the command line.
- **Inno Setup (Windows):** `OrbitConnector-Rhino-Setup-v0.1.0.exe`
  - Traditional Windows installer. Per-user install — no admin needed.
- **YAK (macOS):** `orbit-connector-0.1.0-rh8-mac-arm64.yak`,
  `orbit-connector-0.1.0-rh8-mac-x64.yak`
  - Scaffold only — see `installers/rhino/MACOS.md`. Requires a project
    split before it can actually build on macOS CI.

### Release pipeline

- `.github/workflows/release.yml` triggers on tag push `v*`, runs
  `build-windows` + `build-macos`, then publishes a GitHub Release with
  whichever artefacts were produced.
- Installers ship **unsigned** in v0.1.x — code signing is parked, same
  posture as the PRISM agent.
