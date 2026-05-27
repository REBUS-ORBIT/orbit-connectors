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
