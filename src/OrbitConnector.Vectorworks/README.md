# OrbitConnector.Vectorworks

ORBIT connector for **Vectorworks** — placeholder.

## Status

**Scaffold only.** No plugin code yet. This folder exists so the v0.1.1
release pipeline can ship a per-OS installer artifact named
`OrbitConnector-Vectorworks-Setup-v<X.Y.Z>.{exe,dmg}` — the installer
currently deploys a "coming soon" README and nothing else.

When real Vectorworks plugin development starts, drop the source under
`src/` (Vectorworks plug-ins are typically Vectorscript / Python or
C++ via the Vectorworks SDK). Mirror the directory layout used by
`src/OrbitConnector.Rhino/`:

```
src/OrbitConnector.Vectorworks/
├── README.md                       # this file
├── src/                            # plugin source goes here
│   ├── Pipeline/                   # VectorworksSendPipeline.* / VectorworksReceivePipeline.*
│   ├── Converters/                 # ConversionContext + per-primitive converters
│   │   ├── ToOrbit/                # native -> ORBIT
│   │   └── FromOrbit/              # ORBIT -> native (round-trip; optional)
│   ├── Auth/                       # OAuth + token store
│   ├── Models/                     # ConnectorCard, ServerConfig, CardStore
│   ├── UI/                         # dockable panel + embedded HTML/JS
│   └── Commands/                   # host-native command bindings
└── installer/                      # platform installer scripts (also live under /installers/vectorworks)
```

See the top-level [README](../../README.md) for the canonical
"Quick start for AI agents" and the conversion model that every
connector implementation must respect.

## TODO

- [ ] Pick the SDK language (Vectorscript / Python / C++) and target
      Vectorworks version (2024+ recommended).
- [ ] Implement the converter set: Mesh, NURBS Surface, Extrude, Symbol
      (instance), Polyline, Point, plus a fallback that meshes anything
      else via the Vectorworks tessellator.
- [ ] Wire up the OAuth flow against `<server>/authn/verify/<appId>/<challenge>`
      following `src/OrbitConnector.Rhino/Auth/OrbitAuthManager.cs`.
- [ ] Stand up a dockable UI panel that hosts a WebView with the same
      `index.html` shell as the Rhino connector.
- [ ] Implement the texture / `RenderMaterial` mapping. Vectorworks texture
      slots map roughly: Color → `diffuseTexture`, Reflectivity → metallic /
      roughness, Glow → `emissiveTexture`, Bump → `normalTexture`,
      Transparency → `opacityTexture`.
- [ ] Smoke-test against an ORBIT dev server using the checklist in the
      top-level README ("How to test a new connector").
