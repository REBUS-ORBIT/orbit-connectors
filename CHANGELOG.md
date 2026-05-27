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
