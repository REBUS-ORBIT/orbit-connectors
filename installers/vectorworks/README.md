# Vectorworks installer

Build scripts for the ORBIT Connector for Vectorworks installers.

**Status (v0.1.1):** scaffolding only. The Vectorworks plug-in source
does not exist yet (see [`../../src/OrbitConnector.Vectorworks/README.md`](../../src/OrbitConnector.Vectorworks/README.md)).
The installers built here ship a single placeholder `README.txt` and
nothing else; the value of v0.1.1 is the **release pipeline shape**,
not the payload.

## Layout

```
installers/vectorworks/
├── build-windows.ps1                    # ISCC + placeholder payload
├── build-macos.sh                       # hdiutil + placeholder payload
├── inno/
│   └── OrbitConnector.Vectorworks.iss   # Inno Setup script
├── pkg/
│   └── build-pkg.sh                     # SKELETON; .pkg flow TODO
└── dist/                                # build output (gitignored)
```

## Outputs

- Windows: `dist/OrbitConnector-Vectorworks-Setup-v<Version>.exe`
- macOS:   `dist/OrbitConnector-Vectorworks-Setup-v<Version>.dmg`

Both filenames follow the v0.1.1 cross-connector naming convention:
`OrbitConnector-<Host>-Setup-v<Version>.<ext>`.

## Local build

Windows:
```powershell
installers\vectorworks\build-windows.ps1 -Version 0.1.1
```

macOS:
```bash
installers/vectorworks/build-macos.sh 0.1.1
```

## CI

`.github/workflows/release.yml` runs `build-vectorworks-windows` and
`build-vectorworks-macos` on every `v*` tag push. Each job uploads
its artifact for the `release` job to attach to the GitHub Release.
