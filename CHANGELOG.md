# Changelog

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
