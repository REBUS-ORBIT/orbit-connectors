# OrbitConnector.UE5

ORBIT connector for **Unreal Engine 5** — placeholder.

## Status

**Scaffold only.** No plugin code yet. This folder exists so the v0.1.1
release pipeline can ship a per-OS installer artifact named
`OrbitConnector-UE5-Setup-v<X.Y.Z>.{exe,dmg}` — the installer currently
deploys a "coming soon" README and nothing else.

When real UE5 plugin development starts, the canonical layout for an
Unreal plug-in is:

```
src/OrbitConnector.UE5/
├── README.md                            # this file
├── src/
│   └── OrbitConnector/                  # Unreal plug-in folder
│       ├── OrbitConnector.uplugin       # plug-in manifest
│       ├── Source/
│       │   └── OrbitConnector/
│       │       ├── OrbitConnector.Build.cs
│       │       ├── Public/              # headers
│       │       └── Private/             # implementation
│       │           ├── Pipeline/
│       │           ├── Converters/
│       │           ├── Auth/
│       │           ├── Models/
│       │           └── UI/              # Slate / UMG
│       └── Resources/                   # icons + textures
└── installer/                           # platform installer scripts (also live under /installers/ue5)
```

The connector targets the Unreal Editor (not packaged shipping games):
the user installs the plug-in into either `<UnrealEngine>/Engine/Plugins/`
(engine-wide) or `<Project>/Plugins/` (per-project). The Windows installer
copies the plug-in folder to `%USERPROFILE%\Documents\Unreal Projects\Plugins\OrbitConnector\`
by default; the user copies it from there into the right `Plugins/` folder.

See the top-level [README](../../README.md) for the canonical
"Quick start for AI agents" and the conversion model that every
connector implementation must respect.

## TODO

- [ ] Decide whether the connector targets editor-time scene capture
      (read the level / `UWorld`'s `AActor`s) or runtime mesh streaming
      (a plug-in that pulls ORBIT data live during PIE / cooked builds).
      Editor-time is the simpler v1 — mirror the Rhino reference impl.
- [ ] Implement the converter set: `UStaticMesh`, `USkeletalMesh`,
      `ULandscapeComponent`, `USplineComponent`, `UInstancedStaticMeshComponent`
      (block instance), plus a fallback that bakes anything else via
      `MeshDescription`.
- [ ] Hook the OAuth flow into Unreal's `IHttpRequest` / `FHttpModule`.
      The Rhino reference impl uses `System.Net.HttpListener` for the
      OAuth callback; Unreal will need a small embedded local HTTP
      listener (look at `FHttpServerModule`).
- [ ] Add a Slate / UMG dockable tab for the ORBIT panel; the WebView
      pattern used by the Rhino connector translates poorly to Unreal —
      prefer native Slate widgets for the cards UI.
- [ ] Coordinate-system swap: Unreal is **left-handed, Z-up** (X forward,
      Y right, Z up); ORBIT is **right-handed, Z-up**. Apply
      `(x, y, z) → (x, -y, z)` once at the converter boundary. Apply it
      to vertices, vertex normals, transforms, and the view target / up
      vector. Do **not** apply per-converter (see the README pitfalls).
- [ ] Units: Unreal's default unit is the centimetre. Tag every emitted
      `Mesh` with `units: "cm"` (or read the project's
      `WorldToMeters` and map accordingly).
- [ ] Smoke-test against an ORBIT dev server using the checklist in the
      top-level README ("How to test a new connector").
