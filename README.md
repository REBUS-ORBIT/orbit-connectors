# ORBIT Connectors

ORBIT is REBUS Industries' data exchange platform for AEC and design workflows: a
self-hosted server (`https://orbit.rebus.industries`) that stores object-graph
snapshots ("versions") of native CAD scenes and serves them to a web viewer, SDKs,
and other host applications. A **connector** is the per-host plugin that walks the
host application's scene graph, converts each native primitive to ORBIT's transport
schema, packages materials and textures, and pushes a version to the server.

This repository contains the canonical reference connector for **Rhino 8** (and,
by extension, Grasshopper). Connectors for other host applications — Revit,
AutoCAD, SketchUp, Blender, 3ds Max, ArchiCAD — should be built by pattern-matching
against this implementation. Licence: see [Licence](#licence) below; pending a
formal `LICENCE` file, treat as "all rights reserved, contributions welcome".

## Quick start for AI agents

If you have been instructed to build a connector for a host application that does
not yet have one (e.g. "build an ORBIT connector for SketchUp"), follow this
process strictly. Do not improvise; do not invent ORBIT APIs.

1. **Read this entire README first.** Every section. Do not skim. The conversion
   model, the texture pipeline, and the pitfalls list are non-negotiable contracts.
2. **Read the entire `src/OrbitConnector.Rhino/` tree end-to-end** before writing
   a single line of new code. Start with `OrbitConnectorPlugin.cs`, then
   `Pipeline/RhinoSendPipeline.cs`, then `Converters/ConversionContext.cs`, then
   each `Converters/ToOrbit/*.cs` in the order they are dispatched in
   `RhinoSendPipeline._converters`.
3. **Read the ORBIT SDK** at <https://github.com/REBUS-ORBIT/orbit-sdk> to
   understand the wire types you are emitting (`Orbit.Objects.Geometry.Mesh`,
   `Orbit.Objects.Other.RenderMaterial`, `Orbit.Objects.Base.OrbitObject` /
   `Collection`, the `OrbitSerializer`, `IOrbitTransport`, `OrbitClient`,
   `OrbitBlobUploader`). The SDK is the wire format. Do not write your own.
4. **Ask the user which host application to target and which SDK language**
   (C# / Python / TS / Ruby) is appropriate for that host. Do not guess.
5. **Mirror the directory layout** under `src/OrbitConnector.<Host>/`. Use the
   same sub-folder names: `Pipeline/`, `Converters/ToOrbit/`, `Converters/FromOrbit/`,
   `Auth/`, `Models/`, `UI/`, `Commands/`, `installer/`.
6. **Implement converters in this dispatch order**, most-specific to most-generic:
   `Mesh → Surface → Brep → Extrusion → SubD → Curve → Point → PointCloud → Text
   → Instance (block) → Fallback`. Stop at whichever subset the host actually
   exposes (e.g. Blender has no Brep — skip it).
7. **Run the smoke-test checklist** (see [How to test a new connector](#how-to-test-a-new-connector)) before opening a PR. Every box must be ticked.
8. **Open a draft PR early.** Reference this README in the PR description and
   confirm which checklist items pass.

The canonical project layout for a new connector:

```
src/OrbitConnector.<Host>/
├── OrbitConnector.<Host>.csproj      # or pyproject.toml / gemspec / etc.
├── OrbitConnector<Host>Plugin.cs     # host plugin entry point
├── Commands/                          # host-native commands that open the UI
├── UI/                                # dockable panel + embedded HTML/JS
│   └── wwwroot/index.html
├── Auth/                              # OAuth + token store
├── Models/                            # ConnectorCard, ServerConfig, CardStore
├── Pipeline/                          # HostSendPipeline, HostReceivePipeline
├── Converters/
│   ├── ConversionContext.cs           # shared per-send state
│   ├── HostMaterialHelper.cs          # texture extraction + blob staging
│   ├── HostNativeEncoder.cs           # optional: native byte round-trip
│   └── ToOrbit/
│       ├── IHostToOrbitConverter.cs
│       ├── Host<Type>Converter.cs     # one file per primitive type
│       └── HostFallbackConverter.cs
├── Properties/                        # AssemblyInfo, embedded resources
└── installer/                         # platform-specific installer scripts
```

## What ORBIT expects from a connector

A connector is a pure send/receive bridge. It must:

1. **Enumerate** the host document's geometry, respecting a user-chosen filter
   (all objects, by layer, by current selection).
2. **Convert** each native primitive to an ORBIT wire type from `Orbit.Objects.*`.
   The wire format is content-addressed JSON; the SDK handles hashing,
   chunking, and detachment.
3. **Resolve materials** from the host's render-material graph into the ORBIT
   `RenderMaterial` PBR shape, including texture maps.
4. **Stage texture files** for upload, hashing them with SHA-256 client-side as a
   stable cache key, then post the bytes to the ORBIT blob endpoint and rewrite
   any `@blob:<sha256hex>` placeholders to the server-assigned short blob id
   **before** serialising the commit.
5. **Assemble a `Collection` hierarchy** that mirrors the host's layer/group/tag
   structure. Per-object metadata (`layerPath`, `layerColor`, `colorSource`,
   `applicationId`) is written onto every leaf so the ORBIT viewer's model
   browser shows the correct tree.
6. **Serialise** the root via the SDK's `OrbitSerializer`. The serializer produces
   a batch of `(id, json)` pairs, where each `id` is the SHA-256 of the object's
   canonical JSON. The root id becomes the version's root reference.
7. **Deduplicate** against the server (`IOrbitTransport.HasObjectAsync`) so
   already-known objects skip the upload step.
8. **Push** the unknown objects via `IOrbitTransport.SaveObjectBatchAsync`, then
   call `OrbitClient.CreateVersionAsync(projectId, modelId, rootId, ...)`.

A connector that does any of these steps differently from the reference
implementation **will desync** with the viewer. Stick to the contract.

## Reference implementations

The repository ships one canonical, fully-implemented connector (Rhino) and
release-pipeline scaffolds for the next two hosts on the roadmap so that
every release shape stays consistent across hosts from day one.

| Host                  | Status         | Source                                          | Installer artifacts (per release)                                                |
|-----------------------|----------------|-------------------------------------------------|-----------------------------------------------------------------------------------|
| **Rhino 8 / Grasshopper** | **Shipped**    | `src/OrbitConnector.Rhino/`                  | `orbit-connector-<v>-rh8-win.yak`, `OrbitConnector-Rhino-Setup-v<v>.exe`, `OrbitConnector-Rhino-Setup-v<v>.dmg` *(YAK Mac scaffolds also attached)* |
| **Vectorworks**       | **Scaffold**   | `src/OrbitConnector.Vectorworks/` *(placeholder)* | `OrbitConnector-Vectorworks-Setup-v<v>.exe`, `OrbitConnector-Vectorworks-Setup-v<v>.dmg` |
| **Unreal Engine 5**   | **Scaffold**   | `src/OrbitConnector.UE5/` *(placeholder)*      | `OrbitConnector-UE5-Setup-v<v>.exe`, `OrbitConnector-UE5-Setup-v<v>.dmg` |

*Shipped* means the connector has been smoke-tested against an ORBIT
server and the installer payload contains a real, loadable plug-in.
*Scaffold* means the installer compiles cleanly and produces a valid
artifact with the canonical filename, but the payload is a placeholder
README — the actual plug-in source is the next deliverable for that host.

The naming convention for installer artifacts is fixed:

```
OrbitConnector-<Host>-Setup-v<Version>.<exe|dmg>
```

Windows installers are produced by Inno Setup; macOS installers are
produced by `hdiutil` wrapping a `.pkg` (or, for scaffolds, a plain
README) into a mountable `.dmg`. YAK files for Rhino keep their
McNeel-imposed naming (`orbit-connector-<v>-rh8-<platform>.yak`) and
ride along as additional artifacts on the Rhino releases.

### Adding a new connector

When you add ORBIT support for a new host (Revit, Blender, AutoCAD, etc.),
follow the Vectorworks/UE5 pattern as the canonical template:

1. Create `src/OrbitConnector.<Host>/` with a placeholder `README.md` and
   an empty `src/` folder (see `src/OrbitConnector.Vectorworks/README.md`
   for the template). Real plug-in source lands here when development
   starts.
2. Create `installers/<host>/` with:
   - `inno/OrbitConnector.<Host>.iss` — Inno Setup script. Use a
     **new constant `AppId` GUID** so Windows upgrades work cleanly.
     Default install dir should match the host's per-user plug-in path.
   - `build-windows.ps1` — wraps ISCC on the .iss with the connector
     version.
   - `build-macos.sh` — wraps `hdiutil` to produce the matching `.dmg`.
   - `pkg/build-pkg.sh` — skeleton for a future native `.pkg`
     (only relevant when the host has a real macOS plug-in payload).
   - `README.md` — describes the local-build commands and the CI
     wiring.
3. Add two new jobs to `.github/workflows/release.yml`:
   `build-<host>-windows` and `build-<host>-macos`. While the
   connector is a scaffold, mark both as `continue-on-error: true` so
   a glitchy placeholder build never blocks a release.
4. Extend the `release` job's `files:` glob list to include the new
   artifacts.

The contract for what a real connector plug-in must do is documented
below — read it end-to-end before writing any host-specific code.

## Reference implementation: Rhino + Grasshopper

The Rhino 8 connector lives under `src/OrbitConnector.Rhino/`. Target framework
is `net8.0-windows`; output is renamed from `.dll` to `.rhp` after build (see
the `RenameToRhp` target in the csproj). The plugin loads on Rhino startup and
registers a dockable Eto panel that hosts a `WebView` running the connector UI.

### Plugin entry point

- `OrbitConnectorPlugin.cs` — derives from `Rhino.PlugIns.PlugIn`. `OnLoad`
  registers the `OrbitEtoPanel` and wires `RhinoDoc.BeginOpenDocument /
  CloseDocument / EndSaveDocument` so card state stays in sync with the active
  document. Static `Instance` exposes the singleton for the rest of the plugin.
- `Commands/OrbitOpenPanelCommand.cs` — the user-facing `Orbit` command. Opens
  the registered panel; that is the only command surface needed.

### Panel and message bridge

- `UI/OrbitEtoPanel.cs` — the dockable panel. Hosts an Eto `WebView` that loads
  `UI/wwwroot/index.html` (embedded resource, extracted to `%TEMP%/orbit_connector/`
  on first show). All user interaction lives in the HTML/JS layer; C# and JS
  exchange messages via a `orbit://msg/<action>?d=<urlencoded-json>` scheme that
  the panel intercepts in `OnDocumentLoading`. Dispatched actions: `ready`,
  `login`, `logout`, `addCard`, `removeCard`, `updateCard`, `getProjects`,
  `getModels`, `createProject`, `send`, `receive`, `requestLayers`,
  `captureSelection`, `openUrl`. C# pushes events back to JS by calling
  `window.orbitReceive('<jsonstring>')`. `IPanel.PanelShown` tracks
  `_lastSyncedDocSerial` so re-showing the panel without a document change does
  **not** re-push cards (which would tear down in-flight UI state).

### Auth

- `Auth/OrbitAuthManager.cs` — OAuth flow against `<server>/authn/verify/<appId>/<challenge>`.
  Opens the system browser, listens on `http://localhost:29364/` for the access
  code, then POSTs `{appId, appSecret, accessCode, challenge}` to
  `<server>/auth/token` and reads back `{token: "..."}`. The reference
  implementation in this repo uses PKCE-style verifier-as-secret (suitable for
  desktop apps with no real shared secret). New connectors targeting public
  distribution should follow that pattern; do **not** ship hardcoded OAuth
  client secrets in distributable binaries.
- `Auth/OrbitTokenStore.cs` — persists tokens in Rhino plugin settings
  (`PlugIn.Settings`), keyed by `MD5(serverUrl)`. Also stores `LastProjectId`,
  `LastModelName`, `LastTarget`, `ThemeMode`. The store survives Rhino
  restarts; tokens are per-user, per-machine, never written to the document.

### Models (persistence)

- `Models/ServerConfig.cs` — server URLs and OAuth app ids. Two targets: `Prod`
  (`orbit.rebus.industries`) and `Dev` (`orbit-dev.rebus.industries`). Static
  `ServerConfig.Default` is the build-time configuration.
- `Models/ConnectorCard.cs` — a Send or Receive card. Persisted in
  `RhinoDoc.Strings` so cards travel with the `.3dm`. Fields: `Id`, `Type`
  (`Send`/`Receive`), `Target` (`Prod`/`Dev`), `ProjectId`, `ProjectName`,
  `ModelId`, `ModelName`, `LayerMode` (`All`/`ByLayer`/`Selection`),
  `IncludedLayers`, `SelectedObjectIds` (GUID snapshot for `Selection` mode),
  `LastVersionId`, `LastSentAt`, `PinnedVersionId`, `LastReceivedAt`,
  `LastReceivedVersionId`.
- `Models/CardStore.cs` — singleton; serialises `_cards` to
  `RhinoDoc.Strings["orbit_connector"]["cards"]` on every mutation. Fires
  `CardsChanged` for the panel to refresh the JS view. `LoadFromDocument(doc)`
  rehydrates on document open.

### The conversion pipeline

- `Pipeline/RhinoSendPipeline.cs` — orchestrator. `SendAsync(card, doc, transport, client, progress, ct)` runs in five stages:

  1. **Extract** — `ExtractObjects(card, doc)` enumerates the document with
     `ObjectEnumeratorSettings { NormalObjects = true, LockedObjects = false,
     HiddenObjects = false, DeletedObjects = false, IncludeLights = false,
     IncludeGrips = false, IncludePhantoms = false }`. Filter applied per
     `LayerMode`: `All` → every normal object, `ByLayer` → objects whose
     layer's `FullPath` is in `card.IncludedLayers`, `Selection` → objects
     whose `Id` is in the user-snapshotted `card.SelectedObjectIds` (or the
     live `GetSelectedObjects(false, false)` if the snapshot is empty).
  2. **Convert** — `BuildObjectTree(...)` groups objects by `LayerIndex` and
     builds one `OrbitObject { CollectionType = "layer", ... }` per layer.
     For each object, dispatches to the first converter whose `CanConvert`
     returns true; on exception or null result, falls through to
     `RhinoFallbackConverter`, then to whole-object render-mesh extraction,
     then to a bounding-box placeholder so nothing silently disappears.
     Block instances are flattened: the instance wrapper is dropped and its
     pre-transformed members are added directly under the layer collection.
  3. **Upload blobs** — `OrbitBlobUploader.UploadAsync(context.PendingBlobFiles, ct)`
     posts each on-disk texture file to the server, receives a short blob id
     keyed by the SHA-256 hash, then `TextureBlobPatcher.Patch(root, hashToServerId)`
     rewrites every `@blob:<sha256hex>` placeholder on every `RenderMaterial`
     to `@blob:<short-id>`. **This must happen before serialise**, because the
     content hash of the root depends on the patched values.
  4. **Serialise** — `OrbitSerializer.SerialiseAsync(root, ct)` produces
     `List<(string id, string json)>` where each id is `sha256(canonical-json)`.
  5. **Transport + version** — for each `(id, json)` pair, skip if
     `IOrbitTransport.HasObjectAsync(id, ct)` returns true, else add to the
     upload batch. `SaveObjectBatchAsync(toUpload, progress, ct)` pushes the
     batch. Finally `OrbitClient.CreateVersionAsync(projectId, modelId, rootId,
     message, sourceApplication, totalChildrenCount, ct)` materialises a new
     version pointing at the root id.

  Named views are extracted in `ExtractNamedViews(doc, units)` and attached to
  `root.Views` as inline `View3D` objects (no `@` prefix — the viewer expects
  them inline). Detached proxies (`MaterialProxies`, `ColorProxies`,
  `GroupProxies`, `DefinitionProxies`) are populated by the converters and
  attached to the root as detached properties — see `ConversionContext` below.

  Helper: `ArgbToUnsignedLong(int) => (long)(uint)argb` packs a signed
  `System.Drawing.Color.ToArgb()` into the unsigned representation the Speckle
  viewer expects. Without the `(uint)` cast, any colour with the alpha-bit set
  ships as a negative `int` and the viewer renders the wrong colour.
  `TagWithLayerInfo(converted, layerPath, layerColor)` writes `layerPath`,
  `layerColor`, and `colorSource: "layer"` onto each leaf — typed properties
  for `Mesh`/`Brep`, dynamic indexer for everything else.

### The conversion context

- `Converters/ConversionContext.cs` — per-send state. Holds:
  - `Doc` (active `RhinoDoc`) and `Units` (mapped from `ModelUnitSystem`:
    `Millimeters → "mm"`, `Centimeters → "cm"`, `Meters → "m"`, `Feet → "ft"`,
    `Inches → "in"`, else `"none"`).
  - `CurrentObject` (`RhinoObject?`) — the converter dispatcher sets this
    immediately before each `Convert()` call so per-type converters can reach
    `obj.Attributes` for colour, layer, material index, user strings.
  - Proxy collections (`RenderMaterialProxy`, `ColorProxy`, `GroupProxy`,
    `DefinitionProxy`) — populated when a converter needs to detach shared
    data; the current pipeline emits material/colour inline on each mesh,
    so these stay empty for typical sends but the schema reserves them.
  - `RegisteredMaterials: Dictionary<int, RenderMaterial>` — cache keyed by
    `(matIdx * 31 + resolvedColorArgb.GetHashCode())` so two objects on
    different layers sharing the same Rhino material legitimately get
    different cached entries (a "Metal" preset with black `DiffuseColor`
    legitimately needs the layer colour as the displayed diffuse).
  - `PendingBlobFiles: Dictionary<string,string>` — `sha256hex → on-disk
    texture file path`, populated by `RhinoMaterialHelper.AttachTextures`
    and drained by the pipeline at stage 3.

  Two key methods:
  - `ResolveCurrentColor()` returns `(source, argb)` for `CurrentObject`
    following Rhino's colour-source rules: `ColorFromObject` → per-object
    override; `ColorFromMaterial` → assigned material's `PreviewColor` (with
    fallback to layer when the material index is invalid);
    `ColorFromLayer` / `ColorFromParent` → layer colour. Output is
    `(string, long)` where the long is the unsigned-ARGB pack.
  - `BuildCurrentRenderMaterial()` builds or fetches the cached
    `RenderMaterial` for `CurrentObject`. PBR roughness/metalness come from
    `mat.PhysicallyBased` when available; the displayed diffuse is the
    *resolved* colour (not the raw material colour) for `ColorFromLayer`/
    `ColorFromObject` so layer-tinted metallic materials don't ship as
    black. For `ColorFromMaterial`, the PBR `BaseColor` is preferred over
    the legacy `DiffuseColor` (which is a tint derived from textures and
    misrepresents black-base materials with emission textures). Hands off
    to `RhinoMaterialHelper.AttachTextures` for the texture maps.

### The converters

Every converter implements `IRhinoToOrbitConverter` in
`Converters/ToOrbit/IRhinoToOrbitConverter.cs`:

```csharp
public interface IRhinoToOrbitConverter
{
    bool CanConvert(GeometryBase geometry);
    OrbitBase Convert(GeometryBase geometry, ConversionContext context);
}
```

The pipeline tries each registered converter in order; the first whose
`CanConvert` returns true is chosen.

**Currently published in this repository:**

- `Converters/ToOrbit/RhinoMeshConverter.cs` — converts a Rhino `Mesh` to
  `Orbit.Objects.Geometry.Mesh`. Emits `Vertices` as a flat `[x,y,z,...]`
  `List<double>`, `Faces` as ORBIT's variable-length encoding
  (`[3,a,b,c, 3,d,e,f, ...]` for triangles, `[4,a,b,c,d, ...]` for quads),
  optional `VertexNormals` flat `[nx,ny,nz,...]`, optional flat
  `TextureCoordinates` `[u,v,...]` (with backfill from the parent object's
  render mesh when the tessellated mesh has none), and optional per-vertex
  `Colors` as `List<int>` of `Color.ToArgb()` values. Computes normals via
  `Normals.ComputeNormals()` when missing. Public static helpers
  `CopyTextureCoordinates` and `AttachRenderMaterial` are reused by other
  converters that produce display meshes.

- `Converters/ToOrbit/RhinoBrepConverter.cs` — wraps a `Brep` in an
  `Orbit.Objects.Data.RhinoDataObject` (the round-trip wrapper):
  - A `RawEncoding { Format = "3dm", Contents = base64(File3dm bytes) }`
    carrying the native NURBS data, so a Rhino-aware receiver recovers the
    real Brep byte-for-byte.
  - A `DisplayValue: List<Mesh>` produced from per-face tessellation with
    `MeshingParameters { JaggedSeams = true }` so the viewer renders the
    geometry without understanding the native format.
  - Materials, vertex colours, UVs all live on the display meshes.

  The static `BuildWrapper(geometry, type, context, meshConverter, brepForDisplay)`
  is reused by Extrusion, SubD, and Surface converters so every native wrapper
  has identical wire shape; only the `type` string ("Brep" / "Extrusion" /
  "SubD" / "Surface") differs.

- `Converters/ToOrbit/RhinoFallbackConverter.cs` — last-resort converter
  (`CanConvert` always returns true). Tries Brep tessellation first, then
  forwards `Mesh` straight to `RhinoMeshConverter`, then asks the parent
  `RhinoObject` for its render meshes. Throws `NotSupportedException` only
  when even render-mesh extraction returns nothing. Pipeline catches and
  drops the object after logging.

**Extending the converter set.** Implement these per host primitive type. Use
the file-naming convention `Rhino<Type>Converter.cs` (substitute `<Host><Type>`
for new connectors), one converter per file, all under `Converters/ToOrbit/`.
Pattern for each:

```csharp
public class Rhino<Type>Converter : IRhinoToOrbitConverter
{
    public bool CanConvert(GeometryBase geometry) => geometry is <RhinoType>;

    public OrbitBase Convert(GeometryBase geometry, ConversionContext context)
    {
        var native = (<RhinoType>)geometry;
        // 1. Build ORBIT typed object (Orbit.Objects.Geometry.* or
        //    Orbit.Objects.Data.RhinoDataObject for native round-trip)
        // 2. Attach DisplayValue mesh fragments where the viewer needs them
        // 3. Attach RenderMaterial via context.BuildCurrentRenderMaterial()
        // 4. Return the wire object — pipeline tags layer metadata on top
    }
}
```

Recommended set, with the ORBIT wire type for each:

| Host type     | ORBIT wire                                  | Notes |
|---------------|---------------------------------------------|-------|
| `Surface`     | `Orbit.Objects.Data.RhinoDataObject` (`type:"Surface"`) | wrap untrimmed NURBS surface; display from `ToBrep` |
| `Extrusion`   | `Orbit.Objects.Data.RhinoDataObject` (`type:"Extrusion"`) | wrap native extrusion; display from `ext.ToBrep()` |
| `SubD`        | `Orbit.Objects.Data.RhinoDataObject` (`type:"SubD"`) | display from `Mesh.CreateFromSubD(subd, 4)`; do **not** go via `ToBrep` (loses creases) |
| `Curve` (any) | `Orbit.Objects.Geometry.Line` / `Arc` / `Circle` / `Polyline` / `PolyCurve` / `NurbsCurve` | dispatch on subclass; attach a polyline `displayValue` so non-NURBS viewers can render |
| `Point`       | `Orbit.Objects.Geometry.Point`              | direct |
| `PointCloud`  | `Orbit.Objects.Geometry.PointCloud`         | forward per-point normals and ARGB colours when present |
| `TextEntity`  | `OrbitObject { displayValue: Mesh[] }`      | prefer parent's render meshes; fall back to `te.Explode()` + `Brep.CreatePlanarBreps` |
| `InstanceReferenceGeometry` | `Orbit.Objects.Geometry.Instance { definitionId, transform, elements }` | requires `CurrentObject is InstanceObject`; duplicate-and-transform each definition member, dispatch per member, attach as `elements` |

Helpers worth implementing alongside the converters:

- **NativeEncoder** — round-trip the host's native byte format. For Rhino this
  is a single-object `.3dm` via `Rhino.FileIO.File3dm`:

  ```csharp
  internal static class RhinoNativeEncoder
  {
      public static string? Encode(GeometryBase geometry)
      {
          using var file3dm = new File3dm();
          file3dm.Objects.Add(geometry, attributes: null);
          var bytes = file3dm.ToByteArray(new File3dmWriteOptions { Version = 8 });
          return bytes is { Length: > 0 } ? Convert.ToBase64String(bytes) : null;
      }
  }
  ```

  Hosts that don't expose a single-object native serializer (e.g. SketchUp,
  Blender) should skip the `RawEncoding` field; receivers will fall back to
  the `displayValue` mesh, which means a one-way send rather than round-trip.

- **BrepDisplayMeshes** — extract per-face display meshes for Breps in a way
  that preserves sharp creases. Strategy: prefer the parent `RhinoObject`'s
  cached render meshes (`rhinoObj.GetMeshes(MeshType.Render)`); fall back to
  per-face `Mesh.CreateFromBrep(faceBrep, new MeshingParameters(default) { JaggedSeams = true })`;
  last resort, mesh the whole Brep. Always filter empty meshes.

- **ObjectMeshes** — aggressive whole-object mesh extraction used by the
  pipeline's fallback path. Tries `MeshType.Render`, `MeshType.Default`,
  `MeshType.Analysis` in order; on no meshes, dispatches by geometry type
  (Brep → BrepDisplayMeshes, Extrusion → ToBrep, Mesh → duplicate, Surface
  → ToBrep, Curve → `Brep.CreatePipe(curve, radius, ...)`); on total failure,
  builds a bounding-box `Box.ToBrep()` mesh so the object is never invisible.

- **MaterialHelper** — see [Material and texture handling](#material-and-texture-handling).

## Conversion model

The ORBIT wire is content-addressed JSON. The SDK's `OrbitSerializer` produces
`(id, json)` pairs where `id = sha256(canonical-json)`. Connectors do not
compute these ids; they only build the typed object tree and let the
serializer hash. The same object hashed twice — even from different connectors
— produces the same id, which is what makes server-side dedup work.

### Mesh

`Orbit.Objects.Geometry.Mesh`:

| Field                | Type           | Required | Notes |
|----------------------|----------------|----------|-------|
| `vertices`           | `List<double>` | yes      | Flat `[x,y,z,x,y,z,...]` |
| `faces`              | `List<int>`    | yes      | Variable-length: `[n,i0,...,in-1, n,i0,...]`. Triangles: `[3,a,b,c]`. Quads: `[4,a,b,c,d]`. **Never** omit the leading count. |
| `vertexNormals`      | `List<double>` | no       | Flat `[nx,ny,nz,...]`; same vertex count as `vertices`. |
| `textureCoordinates` | `List<double>` | no       | Flat `[u,v,u,v,...]`; same count as `vertices`. |
| `colors`             | `List<int>`    | no       | Per-vertex `Color.ToArgb()` ints. Same count as `vertices`. |
| `units`              | `string`       | yes      | "mm" / "cm" / "m" / "ft" / "in" / "none". |
| `renderMaterial`     | `RenderMaterial`| optional | Inline material; or use `colorSource: "layer"` and let the viewer resolve from `layerColor`. |
| `layerPath`          | `string`       | optional | Full layer path (e.g. `"Furniture::Chairs"`). |
| `layerColor`         | `long`         | optional | Unsigned-ARGB-as-long (see pitfalls). |
| `colorSource`        | `string`       | optional | `"object"` / `"layer"` / `"material"`. |

### RenderMaterial (PBR)

`Orbit.Objects.Other.RenderMaterial`:

| Field                  | Type     | Notes |
|------------------------|----------|-------|
| `name`                 | `string` | Material name; fallback `"default"` when missing. |
| `diffuse`              | `long`   | Unsigned-ARGB-as-long base colour. |
| `emissive`             | `long`   | Unsigned-ARGB-as-long emissive colour. **If you attach an emissive texture, this MUST be non-black** (see pitfalls). |
| `opacity`              | `double` | `1.0 - transparency`. |
| `roughness`            | `double` | 0..1. |
| `metalness`            | `double` | 0..1. |
| `ior`                  | `double` | Index of refraction; optional. |
| `diffuseTexture` / `baseColorTexture` | `string` | `@blob:<short-id>` reference. Set both for compatibility. |
| `emissiveTexture` / `pbrEmissionTexture` | `string` | `@blob:<short-id>`. |
| `roughnessTexture`     | `string` | `@blob:<short-id>`. |
| `metalnessTexture`     | `string` | `@blob:<short-id>`. |
| `normalTexture`        | `string` | `@blob:<short-id>`. |
| `opacityTexture`       | `string` | `@blob:<short-id>`. |
| `emissiveIntensity`    | `double` | `1.0` when emission is real glow; `0.0` to suppress. |

### Collection

`Orbit.Objects.Base.OrbitObject` doubles as the Speckle-compatible `Collection`
container:

| Field                  | Type            | Notes |
|------------------------|-----------------|-------|
| `name`                 | `string`        | Display name. Layer's `FullPath` for layer collections. |
| `collectionType`       | `string`        | `"model"` (root), `"layer"` (per-layer), `"rhino layer"`, `"level"`, `"type"`. Must be `"model"` at root for the viewer sidebar to render. |
| `elements`             | `List<OrbitBase>` | Children — meshes, instances, sub-collections. |
| `layerPath`            | `string`        | Same as `name` for layer collections. |
| `layerColor`           | `long`          | Unsigned-ARGB-as-long. |
| `units`                | `string`        | Inherits to children. |
| `sourceApplication`    | `string`        | `"OrbitRhino"`, `"OrbitBlender"`, etc. — used by the server for filtering. |
| `views`                | `List<View3D>`  | Inline named views (no `@` detach prefix). |
| `renderMaterialProxies`, `colorProxies`, `groupProxies`, `definitionProxies` | `List<...Proxy>` | Detached shared data; usually empty for typical sends. |

### Coordinate conventions

ORBIT is **Z-up, right-handed**. Hosts that share this convention (Rhino,
Revit, AutoCAD, SketchUp) send vertex coordinates unchanged. Hosts that use
Y-up (Blender, three.js, WebGL-native apps, some game engines) must apply the
swap `(x, y, z) → (x, -z, y)` **once**, at the connector boundary, not
per-converter. Apply it to vertices, vertex normals, transforms, camera
origins/targets, and the up vector for named views.

### Units

ORBIT emits a `units` field on every typed object that has spatial extent.
Connectors must map the host's document unit system to one of:

| Host unit    | ORBIT value |
|--------------|-------------|
| Millimeters  | `"mm"`      |
| Centimeters  | `"cm"`      |
| Meters       | `"m"`       |
| Feet         | `"ft"`      |
| Inches       | `"in"`      |
| Unknown      | `"none"`    |

Do not pre-multiply vertex coordinates to convert units — emit them in the
host's native units and tag with `units` so the receiver/viewer can scale.

### Per-object layer metadata

Every geometry leaf must carry `layerPath`, `layerColor`, and `colorSource`:

```csharp
mesh.LayerPath   = layer.FullPath;                     // e.g. "Furniture::Chairs"
mesh.LayerColor  = (long)(uint)layer.Color.ToArgb();   // unsigned-pack
mesh.ColorSource = "layer";                            // or "object" / "material"
```

The viewer's model browser uses `layerPath` to build the sidebar tree and
`layerColor` to colour the row. `colorSource: "layer"` with no
`renderMaterial` tells the viewer to tint the geometry with `layerColor`. For
the typed `Mesh` and `Brep` classes use the typed setter; for everything else
use the dynamic indexer (`converted["layerPath"] = ...`). See
`RhinoSendPipeline.TagWithLayerInfo` for the canonical implementation.

## Material and texture handling

### Four-strategy texture probe

Rhino exposes textures across two parallel APIs (RDK and PhysicallyBased) and
two legacy ones (`Material.GetTexture(index)` and `SimulatedMaterial`). The
connector probes in this exact order, taking the first non-null path per slot
(see `RhinoMaterialHelper.AttachTextures`):

1. **RDK FirstChild/NextSibling** — walk `renderMat.FirstChild` /
   `child.NextSibling`. For each `RenderTexture` child, read `ChildSlotName`,
   classify it (`ClassifySlot("base"/"diffuse"/"color"/"bitmap"` →
   `basecolor`; `"roughness"`, `"metallic"`, `"metalness"`, `"emission"`,
   `"emissive"`, `"bump"`, `"normal"`, `"alpha"`, `"opacity"` to the obvious
   slot; anything else to `other_<slot>`), and read the file path via
   `rt.SimulatedTexture(RenderTexture.TextureGeneration.Allow).Filename`.
2. **PhysicallyBased channel API** — when `mat.IsPhysicallyBased`, iterate
   `(TextureType.PBR_BaseColor → "basecolor", PBR_Emission → "emission",
   PBR_Roughness → "roughness", PBR_Metallic → "metallic")` and read each
   via `pbr.GetTexture(type).FileReference.FullPath`. Skips any slot already
   attached by strategy 1.
3. **Legacy `Material.GetTexture(TextureType.Bitmap)`** — only if `basecolor`
   wasn't already attached. Reads `FileReference.FullPath`.
4. **SimulatedMaterial fallback** — `renderMat.ToMaterial(TextureGeneration.Allow)`
   then read PBR or Bitmap texture. Last resort for materials that don't
   expose textures through any other API.

For each found texture: hash the file with SHA-256, stash
`pendingBlobFiles[sha256hex] = absolute path`, and write `@blob:<sha256hex>`
into the appropriate ORBIT `RenderMaterial` slot. The pipeline replaces these
placeholders with server-assigned short ids before serialise.

### Upload + patch

The pipeline at stage 3:

```csharp
using var uploader = new OrbitBlobUploader(client.ServerUrl, projectId, client.AuthToken);
var hashToServerId = await uploader.UploadAsync(context.PendingBlobFiles, ct);
TextureBlobPatcher.Patch(root, hashToServerId);
```

The uploader posts each on-disk file to `<server>/api/projects/<id>/blobs`
multipart, parses the response `{ id: "abc123def4" }`, and returns
`Dictionary<sha256hex, short-id>`. `TextureBlobPatcher.Patch` walks the
object tree and replaces every `@blob:<sha256hex>` token with
`@blob:<short-id>` on every `RenderMaterial` field. **Do not skip this step**:
the SHA-256 placeholder is meaningless to the viewer's blob resolver, which
expects the short id.

### Emissive-promotion (and emissive-suppression)

three.js (the viewer's renderer) computes `emission = emissiveColor *
emissiveMap`. If you attach an emissive texture but leave `emissive` as
`0xFF000000` (opaque black), the texture is multiplied by zero and renders
nothing. Two correct behaviours:

- **Real glow** — the host's material has a non-zero emission colour. Ship
  `emissive = <unsigned-ARGB long>` and `emissiveIntensity = 1.0`. The
  texture modulates the colour.
- **Texture-as-base-colour misclassification** — the host put a base-colour
  bitmap into the emission slot, but the material has no genuine emission
  colour. Promote: move the texture from `emissiveTexture` /
  `pbrEmissionTexture` to `baseColorTexture` / `diffuseTexture`, set
  `diffuse = 4278190080L` (black, so the texture provides the colour), and
  clear the emissive slot. This is exactly what `RhinoMaterialHelper`
  does at the end of `AttachTextures`.

Never synthesise white emission to "boost" a base-colour texture — it makes
everything render ~2× brighter than the source viewport (this was a real
shipped bug; see the comments in `RhinoMaterialHelper.AttachTextures`).

## Send pipeline

End-to-end flow when the user clicks "Send" on a card:

1. **JS `send` message** lands in `OrbitEtoPanel.HandleSendAsync`.
2. **Validate** card, client, doc, model id; create model if `newMdlName`
   was provided via `OrbitClient.CreateModelAsync`.
3. **Open transport** — `using var transport = new ServerTransport(serverUrl,
   projectId, token)`. The transport owns the HTTP/WebSocket connection
   and handles batched object upload + dedup queries.
4. **Run the pipeline**:

    ```csharp
    var progress = new Progress<(string s, int p)>(x =>
        SendToJs(new { type = "sendProgress", cardId, status = x.s, percent = x.p }));
    var versionId = await _pipeline.SendAsync(card, doc, transport, _client, progress);
    ```

5. **Update card** — `card.LastVersionId = versionId; card.LastSentAt =
   DateTime.UtcNow; _cardStore.UpdateCard(card)`.
6. **Notify JS** — `SendToJs({ type = "sendOk", cardId, versionId, url =
   "<server>/projects/<id>/models/<id>" })`.

**Idempotency.** Every Base object has a content hash; the dedup check
(`transport.HasObjectAsync(id, ct)`) ensures that re-sending an unchanged
mesh costs nothing on the wire. New connectors should preserve this — never
re-upload an object the server already has.

**Streaming vs batch.** `ServerTransport` is the HTTP batch transport. For
real-time / collaboration scenarios, the SDK also exposes a WebSocket
transport (`OrbitWsTransport`) that streams individual objects to the server
as they're produced. Use `IOrbitTransport` as the abstraction; the rest of
the pipeline does not care which is wired in.

## Authentication

Two paths are supported; connectors should offer both:

1. **OAuth (interactive)** — for end users on desktops. See
   `Auth/OrbitAuthManager.cs`. Flow:

   - Generate a PKCE-style verifier and challenge:

     ```csharp
     var verifier = Base64Url(RandomBytes(32));
     var challenge = Base64Url(SHA256(ASCII(verifier)));
     ```

   - Open the system browser to `<server>/authn/verify/<appId>/<challenge>`.
   - Spin up a one-shot `HttpListener` on `http://localhost:29364/` to
     receive the OAuth callback.
   - Read `access_code` from the callback query string.
   - POST to `<server>/auth/token`:

     ```json
     {
       "appId": "<your-app-id>",
       "appSecret": "<verifier>",
       "accessCode": "<access_code>",
       "challenge": "<verifier>"
     }
     ```

     The server matches `SHA256(appSecret)` against the stored challenge.
   - Read `{ "token": "..." }` from the response. Persist via
     `OrbitTokenStore.SaveToken(serverUrl, token)`.

   The app id is per-server (one per host application). Do not ship a real
   shared OAuth client secret in a distributable binary; the verifier-as-secret
   pattern is the correct approach for desktop apps.

2. **Personal Access Token (headless / CI)** — accept a long-lived token via
   a config file or env var. The Rhino panel exposes this as
   `{ method: "pat", token: "..." }` in the `login` message.

All authenticated API calls go through the SDK's `OrbitClient`, which sets
`Authorization: Bearer <token>` automatically.

## Project skeleton for a new connector

Recommended technology stack per host application:

| Host          | Language       | Build target              | UI shell        | SDK package       |
|---------------|----------------|---------------------------|-----------------|-------------------|
| Rhino 8       | C# (.NET 8)    | `.rhp` (renamed `.dll`)   | Eto + WebView   | `Orbit.Sdk`       |
| Grasshopper   | C# (.NET 8)    | `.gha`                    | WinForms / Eto  | `Orbit.Sdk`       |
| Revit         | C# (.NET 4.8 or 8) | `.dll` + `.addin` manifest | WPF + WebView2 | `Orbit.Sdk`       |
| AutoCAD       | C# (.NET 4.8)  | `.dll` loaded via NETLOAD | WPF + WebView2  | `Orbit.Sdk`       |
| SketchUp      | Ruby           | Extension `.rb` + `.rbz`  | UI::HtmlDialog  | TBD (Ruby gem)    |
| Blender       | Python 3.11+   | Addon `.py` + `__init__.py` | Blender Panel + HTMX | `orbit-sdk` (PyPI) |
| 3ds Max       | C# (.NET 4.8) or MAXScript | `.dlu` (CLR) | WinForms / WebView2 | `Orbit.Sdk` |
| ArchiCAD      | C++            | `.apx` add-on             | DG / WebView    | C bindings (TBD)  |
| Web / three.js| TypeScript     | npm package               | React / Vue     | `@orbit/sdk` (TBD)|

**Directory layout** — mirror the Rhino tree exactly. The folder names are
not arbitrary; the patterns documented above expect them:

```
src/OrbitConnector.<Host>/
├── OrbitConnector.<Host>.csproj      # or pyproject.toml / etc.
├── Pipeline/<Host>SendPipeline.cs
├── Pipeline/<Host>ReceivePipeline.cs # for round-trip connectors
├── Converters/ConversionContext.cs
├── Converters/<Host>MaterialHelper.cs
├── Converters/<Host>NativeEncoder.cs # if the host has a single-object byte format
├── Converters/ToOrbit/
│   ├── I<Host>ToOrbitConverter.cs
│   ├── <Host><Type>Converter.cs      # one per primitive
│   └── <Host>FallbackConverter.cs
├── Converters/FromOrbit/             # receive side; mirror layout
├── Auth/<Host>AuthManager.cs
├── Auth/<Host>TokenStore.cs
├── Models/ConnectorCard.cs
├── Models/CardStore.cs
├── Models/ServerConfig.cs
├── UI/<Host>Panel.cs
├── UI/wwwroot/index.html
├── Commands/                         # host-native command bindings
└── installer/                        # platform-specific installer (Inno / pkg / dmg)
```

**NuGet / pip / gem packages** — use the published ORBIT SDK for your
language; do not vendor the wire types:

- C# .NET: `Orbit.Sdk` and `Orbit.Objects` from
  `https://nuget.pkg.github.com/REBUS-ORBIT/index.json`.
- Python: `orbit-sdk` from PyPI (when published — track
  <https://github.com/REBUS-ORBIT/orbit-sdk>).
- TypeScript: `@orbit/sdk` from npm (when published).
- Ruby / C++: native bindings TBD; use the HTTP/JSON API directly until
  available (the wire format is documented above).

## How to test a new connector

A connector ships only after every box below is ticked. Use the user's ORBIT
prod or dev server, an empty test project, and a throwaway Rhino doc to
mirror the assertions against the published reference.

1. **Empty scene** — create the smallest valid host scene with no geometry.
   Send. Expect: a version is created, root is `Collection { collectionType:
   "model", elements: [] }`, no errors logged. The model browser shows the
   model with zero layers.

2. **Single primitive** — one box / sphere / cube. Send. Expect: geometry
   visible in the ORBIT viewer with correct dimensions (measure in the
   viewer; should match the source viewport) and orientation (up axis
   matches the host's up axis after any documented coordinate swap).

3. **Coloured material** — assign a non-textured solid-colour material to a
   primitive. Send. Expect: viewer renders the geometry with the correct
   diffuse colour (compare swatches side-by-side).

4. **Textured material** — assign a PBR material with a base-colour bitmap.
   Send. Expect: texture renders correctly oriented (test with a directional
   pattern — text or arrows — so flips show), unstretched, mip-mapped, not
   white-on-failure. Confirm in browser devtools that the texture URL
   resolves (200 OK, not 404).

5. **Emissive texture** — material with an emission bitmap. Send. Expect:
   the emission renders (texture visible, not black). If the host material
   has no real glow set, the connector should have promoted the texture to
   base-colour automatically — verify by checking that the surface is not
   over-bright.

6. **Layer hierarchy (3 levels deep)** — `A::B::C` with geometry on each
   level. Send. Expect: the viewer's model browser shows a 3-deep tree
   with the correct geometry under each leaf, layer colours match the
   source layer swatches.

7. **Block / linked instance** — one definition placed twice at different
   transforms. Send. Expect: both placements appear in the viewer with the
   correct transforms; the block contents render once per placement (the
   reference implementation flattens instances — verify the user-visible
   model browser shape matches expectations).

8. **Round-trip** (where the SDK supports receive) — send, then create a
   Receive card pointing at the same version, receive into a fresh host
   document. Expect: the rebuilt scene matches the source within unit
   tolerance, including layer hierarchy, materials, and named views.

9. **Named views** — a `.3dm` with 2-3 saved views. Send. Expect: views
   appear in the viewer's saved-views panel with correct camera position,
   target, up vector, and lens length.

10. **Large mesh** — at least one mesh with >100k vertices. Send. Expect:
    the UI thread remains responsive throughout conversion (offload to a
    background worker — never convert on the UI thread). Upload progress
    advances smoothly. Dedup works: a second send of the same scene takes
    near-zero wire time.

## Common pitfalls

These are real bugs that have shipped (and been fixed) in the Rhino reference
implementation. Do not reproduce them in new connectors.

- **Face encoding** — every face in `Orbit.Objects.Geometry.Mesh.faces` must
  be prefixed with its vertex count. Triangles are `[3, a, b, c]`, quads are
  `[4, a, b, c, d]`. Emitting bare `[a, b, c]` indices ships a malformed
  mesh that the viewer rejects with `cannot reshape array of size N into
  shape (3)`.

- **Chunked array attributes on Mesh** — the C# SDK marks
  `Objects.Geometry.Mesh.textureCoordinates` (and similar fields) as
  `[Chunkable] [DetachProperty]`. On the wire this becomes
  `{referencedId: "..."}` stubs that the **viewer** must dechunk before use.
  If your connector author is also touching the viewer or a downstream
  consumer, ensure the dechunk path runs for these fields — Python
  uploaders that send flat arrays don't hit this, but C# uploaders do.
  This is a viewer concern, not a connector one, but documented here so
  new connector authors don't omit the `[Chunkable]` attribute when
  defining new chunked fields.

- **Blob ids are server-assigned** — do not compute a SHA-256 of the texture
  file and send it as the blob reference. Upload the bytes via
  `OrbitBlobUploader`, read the short id from the response, and patch every
  `@blob:<sha256hex>` placeholder to `@blob:<short-id>` **before**
  serialising. The placeholder pattern is for client-side bookkeeping only.

- **Emissive black with an emissive texture** — three.js multiplies
  `emissiveMap * emissive`. Black emissive zeroes the texture and the
  surface renders unlit. Either promote the texture to base-colour (when
  the host material has no real glow) or set `emissive` to a non-black
  unsigned-ARGB-long and `emissiveIntensity` to `1.0`. See `RhinoMaterialHelper.AttachTextures`.

- **Raw NURBS without displayValue** — the viewer cannot tessellate NURBS
  client-side. Every `Brep` / `Surface` / `SubD` / `Extrusion` / `NurbsCurve`
  you ship MUST carry a `displayValue` (mesh array for surfaces, polyline
  for curves). The `RhinoDataObject` wrapper attaches this automatically
  via `RhinoBrepConverter.BuildWrapper`; mimic the pattern.

- **Heavy work on the UI thread** — Rhino's `Eto.Forms` and equivalent
  WinForms / WPF threads will freeze for the duration of geometry
  conversion if you don't offload. The reference pipeline runs `SendAsync`
  on a `Task.Run` (`OrbitEtoPanel.DispatchMessage` does `Task.Run(() =>
  DispatchMessage(action, data))`). Do the same in your connector.

- **Coordinate-system swap done per converter** — applying `(x,y,z) → (x,-z,y)`
  inside every per-type converter creates inconsistencies (normals get
  swapped, transforms get double-applied, named views forget). Apply the
  swap **once**, at the boundary — either at extract time (mutate the
  geometry buffer before dispatch) or at serialise time (the SDK lets you
  register an axis-swap callback). Document which side the swap lives on
  in the connector's README.

- **Signed-int colour packing** — `System.Drawing.Color.ToArgb()` returns
  `int` and can be negative (any colour with alpha-bit set). The Speckle
  viewer expects unsigned ARGB packed as a `long`. Always cast through
  `(long)(uint)color.ToArgb()`. The reference impl exposes this as
  `RhinoSendPipeline.ArgbToUnsignedLong(int)` and uses it everywhere a
  colour is written.

- **Forgetting `colorSource`** — a leaf with `layerColor` set but no
  `colorSource` falls back to the material colour (usually grey or black)
  in the viewer. Always set `colorSource: "layer"` (or `"object"` /
  `"material"`) when you set `layerColor`.

- **OAuth client secrets in source** — Rhino plugin builds are distributable
  binaries that anyone can disassemble. Do not hardcode real OAuth shared
  secrets in `ServerConfig.cs` or its equivalent. Use the PKCE
  verifier-as-secret pattern in `OrbitAuthManager.cs` (the public reference
  implementation in this repo).

- **CollectionType at the root** — the root collection must be
  `collectionType: "model"`. Any other value (including the older
  `"root"`) prevents the viewer's sidebar tree from rendering, even though
  the geometry itself loads and shows in the viewport.

- **Inline views need no `@` prefix** — `View3D` objects in `root.views`
  should be inline, not detached. Prefixing the field with `@` (the SDK's
  detach marker) causes the viewer's saved-views panel to stay empty.

## Repository layout

```
orbit-connectors/
├── ORBIT-Connectors.sln                             # solution; add new csproj here
├── Directory.Build.props                            # SDK reference switch (Local / NuGet)
├── nuget.config                                     # nuget.org + ghcr.io REBUS-ORBIT feed
├── .gitignore                                       # ignores bin/ obj/ .env etc.
├── CHANGELOG.md                                     # per-release notes (used by CI for release body)
├── LICENCE.txt                                      # MIT placeholder
├── .github/
│   └── workflows/
│       ├── build.yml                                # dotnet build + test (push / PR)
│       └── release.yml                              # tag-driven multi-connector release
├── src/                                             # one OrbitConnector.<Host>/ per supported host
│   ├── OrbitConnector.Rhino/                        # canonical reference connector (shipped)
│   │   ├── OrbitConnector.Rhino.csproj
│   │   ├── OrbitConnectorPlugin.cs                  # PlugIn entry point
│   │   ├── Auth/
│   │   │   ├── OrbitAuthManager.cs                  # OAuth flow (PKCE-style)
│   │   │   └── OrbitTokenStore.cs                   # per-server token persistence
│   │   ├── Commands/
│   │   │   └── OrbitOpenPanelCommand.cs             # "Orbit" Rhino command
│   │   ├── Converters/
│   │   │   ├── ConversionContext.cs                 # per-send state + material build
│   │   │   └── ToOrbit/
│   │   │       ├── IRhinoToOrbitConverter.cs        # converter interface
│   │   │       ├── RhinoMeshConverter.cs            # Mesh → Orbit.Objects.Geometry.Mesh
│   │   │       ├── RhinoBrepConverter.cs            # Brep → RhinoDataObject (native + display)
│   │   │       └── RhinoFallbackConverter.cs        # catch-all → display meshes
│   │   ├── Models/
│   │   │   ├── CardStore.cs                         # cards persisted in RhinoDoc.Strings
│   │   │   ├── ConnectorCard.cs                     # Send/Receive card model
│   │   │   └── ServerConfig.cs                      # prod/dev URLs + OAuth app ids
│   │   ├── Pipeline/
│   │   │   └── RhinoSendPipeline.cs                 # 5-stage orchestrator
│   │   ├── Properties/
│   │   │   └── Resources.cs                         # embedded icon placeholder
│   │   ├── UI/
│   │   │   └── OrbitEtoPanel.cs                     # WebView-hosted Eto panel
│   │   └── installer/                               # legacy in-source installer scripts (kept for local use)
│   │       ├── Build-Installer.ps1
│   │       └── OrbitConnector.iss
│   ├── OrbitConnector.Vectorworks/                  # scaffold (no plug-in source yet)
│   │   ├── README.md                                # describes the planned layout
│   │   └── src/                                     # empty; real source goes here
│   └── OrbitConnector.UE5/                          # scaffold (no plug-in source yet)
│       ├── README.md
│       └── src/
├── installers/                                      # one subfolder per host -- CI calls these
│   ├── rhino/
│   │   ├── build-yak.ps1                            # YAK build (Windows + Rhino-shipped YAK on Mac)
│   │   ├── build-mac.sh                             # Mac YAK build (scaffold; csproj split pending)
│   │   ├── build-dmg.sh                             # hdiutil wrapper -- ships a "use YAK on Mac" README until pkg builds
│   │   ├── MACOS.md                                 # Mac story / required follow-up
│   │   ├── inno/OrbitConnector.Rhino.iss            # Inno Setup script (real payload)
│   │   ├── yak/manifest.yml                         # YAK package manifest
│   │   └── pkg/build-pkg.sh                         # SKELETON; .pkg flow TODO
│   ├── vectorworks/
│   │   ├── README.md
│   │   ├── build-windows.ps1                        # ISCC + placeholder payload
│   │   ├── build-macos.sh                           # hdiutil + placeholder payload
│   │   ├── inno/OrbitConnector.Vectorworks.iss
│   │   └── pkg/build-pkg.sh                         # SKELETON
│   └── ue5/
│       ├── README.md
│       ├── build-windows.ps1
│       ├── build-macos.sh
│       ├── inno/OrbitConnector.UE5.iss
│       └── pkg/build-pkg.sh                         # SKELETON
└── README.md                                        # this file
```

For new hosts, follow [Adding a new connector](#adding-a-new-connector)
above. The Vectorworks and UE5 folders are the canonical templates —
copy the layout, swap the host name, and you get a release artifact
slot for free.

## Contributing

Branch model: `main` is always green and shippable. All work happens on
feature branches; merge to `main` via PR with green CI required. The CI
workflow (`.github/workflows/build.yml`) runs `dotnet restore`, `dotnet
build --configuration Release`, and `dotnet test` on `windows-latest`.
Release tags also upload the built `.rhp` as a GitHub artefact (and, when
the runner has Inno Setup installed, an installer `.exe`).

**AI-generated PRs are welcome.** The PR description must:

- Reference this README (link to the section(s) the change implements).
- Confirm which smoke-test checklist items pass. Paste a screenshot or
  log snippet for each one ticked.
- Identify the host application and ORBIT wire types touched.
- Note any deliberate deviations from the reference implementation and
  why they were necessary.

Open a draft PR early — preferably after the smallest viable change (one
new converter, one bug fix) compiles. Iterate in review rather than
landing a 5000-line monolith.

**Issues** — file under `https://github.com/REBUS-ORBIT/orbit-connectors/issues`.
Tag with the host application the issue applies to (`host:rhino`,
`host:revit`, etc.) and the wire type if relevant (`wire:mesh`,
`wire:material`, `wire:transport`).

## Licence

No licence file has been added yet. Pending a formal declaration, treat the
contents of this repository as **"all rights reserved"** — REBUS Industries
retains copyright. Contributions are welcome via PR and will be licensed
under whichever licence is eventually published (MIT is the recommended
default for connectors, since they are intended to be widely forkable and
modified per host).

If you are an AI agent forking this repo to build a connector for a new
host, do not assume an open licence. Get explicit permission from the
maintainer before redistributing modified builds.
