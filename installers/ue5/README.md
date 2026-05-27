# Unreal Engine 5 installer

Build scripts for the ORBIT Connector for Unreal Engine 5 installers.

**Status (v0.1.1):** scaffolding only. The UE5 plug-in source does not
exist yet (see [`../../src/OrbitConnector.UE5/README.md`](../../src/OrbitConnector.UE5/README.md)).
The installers built here ship a single placeholder `README.txt` and
nothing else; the value of v0.1.1 is the **release pipeline shape**,
not the payload.

## Layout

```
installers/ue5/
├── build-windows.ps1                    # ISCC + placeholder payload
├── build-macos.sh                       # hdiutil + placeholder payload
├── inno/
│   └── OrbitConnector.UE5.iss           # Inno Setup script
├── pkg/
│   └── build-pkg.sh                     # SKELETON; .pkg flow TODO
└── dist/                                # build output (gitignored)
```

## Outputs

- Windows: `dist/OrbitConnector-UE5-Setup-v<Version>.exe`
- macOS:   `dist/OrbitConnector-UE5-Setup-v<Version>.dmg`

Both filenames follow the v0.1.1 cross-connector naming convention:
`OrbitConnector-<Host>-Setup-v<Version>.<ext>`.

## Local build

Windows:
```powershell
installers\ue5\build-windows.ps1 -Version 0.1.1
```

macOS:
```bash
installers/ue5/build-macos.sh 0.1.1
```

## CI

`.github/workflows/release.yml` runs `build-ue5-windows` and `build-ue5-macos`
on every `v*` tag push. Each job uploads its artifact for the `release` job
to attach to the GitHub Release.

## Install path

The Windows installer drops the (currently placeholder) payload into
`%USERPROFILE%\Documents\Unreal Projects\Plugins\OrbitConnector\<Version>\`.
That matches a sensible "user plug-in" location an Unreal user can copy
from into either an engine-wide `<UnrealEngine>/Engine/Plugins/` folder
or a per-project `<YourProject>/Plugins/` folder. The real plug-in
payload (when it lands) will be a complete `.uplugin` folder ready to
move into either of those locations.
