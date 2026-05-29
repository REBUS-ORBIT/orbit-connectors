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

## v0.1.20 — Send/receive textured round-trip (serializer, transport, GraphQL, viewer + Rhino texture mapping)

> Completes the textured-model round-trip that v0.1.17–v0.1.19 began. With
> v0.1.19 the connector *extracted* embedded/procedural bitmaps, but the
> send still failed with a generic `400 (Bad Request)`; once that was
> resolved the ORBIT viewer crashed, then rendered the model without the
> bitmap, and finally a received model dropped the texture (and, once
> recovered, mapped it wrong). v0.1.20 fixes every link in that chain so a
> textured model sends cleanly, renders in the viewer, and receives back
> into Rhino with the bitmap mapped exactly as authored.

### Send — `400 (Bad Request)` resolved (three independent bugs)

- **Serializer (`vendor/SDK/.../Serialisation/OrbitSerializer.cs`,
  `Orbit.Objects/Base/OrbitObject.cs`).** Restored Speckle-compatible
  wire format: 32-char **MD5** object ids (was 64-char SHA-256), `id`
  injected on every detached object, detach stubs emit
  `speckle_type: "reference"`, and `@elements` / `OrbitType` /
  `source_application` are carried on the root collection. The server
  rejected the old shape outright.
- **Transport (`vendor/SDK/.../Transport/ServerTransport.cs`).** Object
  batches are now uploaded as `multipart/form-data` (the Speckle object
  API rejects `application/json` with *"Unsupported content type"*). The
  server response body is now logged on a non-2xx so the next failure is
  diagnosable instead of a bare `.NET` status string.
- **GraphQL (`vendor/SDK/.../Api/GraphQL/OrbitQueries.cs`,
  `Api/OrbitClient.cs`, `Api/GraphQL/OrbitGraphQLClient.cs`).** Version
  creation now calls `versionMutations.create` (was the non-existent
  `modelMutations.create` shape, which 400'd). The GraphQL client reads
  and surfaces the response body **before** the success check so server
  GraphQL errors are visible rather than hidden behind
  `EnsureSuccessStatusCode`.

### Viewer — material crash + missing bitmap

- **`RenderMaterial.cs` — `DefaultValueHandling.Include`** on `diffuse`,
  `emissive`, `opacity`, `roughness`, `metalness`. Newtonsoft was dropping
  zero-valued scalars (e.g. `metalness: 0.0`), and the viewer calls
  `.toString()` on them unconditionally — `undefined.toString()` crashed
  `renderMaterialToString` / `getMaterialHash`. Forcing serialization
  keeps the fields present.
- **`TextureBlobPatcher.cs` — bare server blob ids.** Texture references
  now emit the bare server-assigned blob id (e.g. `feb75f1864`) instead of
  `@blob:<sha256>`; the viewer resolves `${blobBaseUrl}/${blobId}`
  directly. Also added an explicit recursion branch for
  `RhinoDataObject.displayValue` meshes (the wrapper inherits `OrbitBase`,
  not `OrbitObject`, so the generic walk missed it) — without it the
  textured display meshes never got patched.

### Receive — texture download + correct UV mapping

- **`OrbitMaterialConverter.cs` — `NormalizeBlobRef`.** Receive now
  recognises a **bare** blob id (still tolerating a legacy `@blob:` prefix
  or a full blob URL), downloads the blob to a temp bitmap, and assigns it
  to the correct PBR slot (`emissiveTexture`/`pbrEmissionTexture` →
  emission, `diffuseTexture`/`baseColorTexture` → base color, etc.).
  Previously it only matched `@blob:`-prefixed values and so downloaded
  nothing once send switched to bare ids. Per-slot diagnostics restore a
  meaningful `blobs=N downloaded=N` summary.
- **`RhinoReceivePipeline.cs` + `OrbitToRhinoConverter.cs` — display-mesh
  UV bake for textured native objects.** A textured Brep/Extrusion/SubD
  wrapped in a `RhinoDataObject` round-trips its native `.3dm` by default,
  so Rhino auto-generated surface-parameterisation UVs that ignored the
  authored bitmap mapping (texture appeared but was oriented/scaled
  wrong). When the material references a texture **and** the display
  meshes carry `textureCoordinates`, receive now bakes the UV-carrying
  display mesh (new `OrbitToRhinoConverter.ConvertDisplayMeshOnly`) so the
  mapping matches the viewer 1:1 (both use lower-left, V-up mesh UVs — no
  flip needed). Non-textured geometry keeps the byte-for-byte native
  round-trip.

### Tooling

- **`scripts/dev-build-local.ps1`** is now version-aware: it resolves the
  version from `Directory.Build.props`, deploys into the matching
  `…\Programs\OrbitConnector\Rhino\<version>\` folder, mirrors into
  whichever folder the Rhino registry currently points at, and verifies
  the deployed `.rhp` matches the build output — so a local dev build can
  no longer silently land in a stale version folder.

### Trade-off

Textured Brep/Extrusion/SubD objects now bake as a mesh on receive (the
only geometry carrying the authored UVs); non-textured objects remain
editable native geometry.

## v0.1.19 — Embedded / procedural texture extraction (Metal + Physically Based bitmaps)

> Fixes the case where `Metal` and `Physically Based (1)` still shipped
> `textures-attached=[]` under v0.1.18 despite a fresh send. Root cause:
> the v0.1.18 probe only accepted a texture that resolved to an **on-disk
> file**. Stock Rhino PBR materials and bitmaps embedded in the `.3dm`
> have no external file, so every strategy reported `tex-no-path` and the
> texture was dropped before upload.

### Symptom

With v0.1.18 loaded (confirmed via `[ORBIT] plugin v0.1.18 loaded.`),
a fresh send of the test model still produced a receive log with
`Metal` and `Physically Based (1)` at `textures=[]`, `blobs=0`. The
send-side `probes=[…]` field (added v0.1.18) showed the texture nodes
WERE found — they just resolved to no on-disk path.

### Root cause

v0.1.18 resolved texture files exclusively through
`Texture.FileReference.FullPath` and the legacy `Texture.FileName`.
Two whole classes of texture have neither:

1. **Render-content bitmaps whose path lives in the RDK parameter bag.**
   Rhino render-content textures expose their file via
   `RenderContent.GetParameter("filename")`, not a typed
   `Texture.FileName`. v0.1.18 never read that parameter.
2. **Embedded / procedural textures with no external file at all.**
   The stock `Metal` PBR preset and any bitmap embedded in the `.3dm`
   report blank `FileReference`/`FileName`. The only way to get bytes is
   to bake the texture to a temp bitmap via
   `RenderTexture.SimulatedTexture(TextureGeneration.Allow)` and read the
   generated `SimulatedTexture.Filename`. v0.1.18 called
   `SimulatedTexture(Allow)` only inside the flat/recursive child walk —
   and crucially the base-color bitmap of a PBR material is reachable
   through the `pbr-base-color` child slot via `FindChild`, which
   v0.1.18 never queried, so the walk never reached it to bake it.

This matches the known-good 3DConvert / RebusWorkstationAgent IronPython
pipeline (`3DConvert/app/converters/rhino_conv.py`), which reads
`content.GetParameter('filename')`, walks the documented `_RDK_PBR_SLOTS`
via `FindChild`, and falls back to `SimulatedMaterial(Allow)`.

### Fix

All in `src/OrbitConnector.Rhino/Converters/RhinoMaterialHelper.cs`.

- **New `ResolveRenderTextureFile` helper.** Every render-texture node now
  resolves its file through: (1) the reflective `Filename` property,
  (2) `RenderContent.GetParameter("filename")`, (3) a
  `SimulatedTexture(TextureGeneration.Allow)` bake to a temp bitmap. Step
  3 is the only path that yields uploadable bytes for embedded /
  procedural textures. Logged as `…=baked-temp` in `probes=[…]`.
- **New Strategy 1b: `FindChild` over the documented PBR child slots**
  (`pbr-base-color`, `pbr-metallic`, `pbr-roughness`, `pbr-emission`,
  `pbr-bump`, `pbr-alpha`, `pbr-opacity`, `pbr-ambient-occlusion`). For
  each child it runs `ResolveRenderTextureFile` (so an embedded PBR
  base-color bitmap that Strategy 1 saw as a path-less `Texture` is now
  recovered + baked). Wrapper children (Mix / Adjustment) recurse.
- **Strategy 3 (recursive RDK walk) now bakes.** Each found
  `RenderTexture` resolves through `ResolveRenderTextureFile` instead of
  the on-disk-only path, so procedural / embedded children produce a temp
  bitmap.
- **RDK material lookup hardened.** When `Material.RenderMaterial` is null,
  fall back to a `doc.RenderMaterials` lookup by
  `Material.RenderMaterialInstanceId` (matches the reference pipeline).
- **`probes=[…]` enriched.** Per strategy/slot it now reports node-found,
  on-disk-file, embedded (`tex-no-path(embedded?)`), and bake result
  (`baked-temp` / `node-no-bitmap` / `bake-throw`). The next send log is
  fully self-diagnosing.

Bumps `OrbitConnectorVersion` 0.1.18 → 0.1.19; the `[ORBIT] plugin
v0.1.19 loaded.` banner reflects it automatically.

### What is not changed

- Receive pipeline, blob upload, blob patcher, vendored SDK — unchanged.
- Slot classification and Rhino-type-to-slot mapping — unchanged; only
  the upstream file resolution is now embedded/procedural-aware.

## v0.1.18 — Texture-probe regression fix: Metal bitmap, union-of-strategies, recursive RDK walk

> Fixes a v0.1.17 regression where some Rhino materials (notably the
> stock "Metal" PBR material in our test model) shipped with
> `textures-attached=[]` even though their bitmap was on disk and
> reachable. v0.1.17 actually pulled **fewer** textures than v0.1.16
> on real-world models because of an over-tight Strategy 4 gate and
> a flat RDK walk that missed bitmaps wrapped in non-texture
> render-content containers.

### Symptom

With v0.1.17 installed against a Rhino model whose `Metal` material
has a bitmap attached through the render-content tree (not through
the PBR slot editor), the receive log reported:

```
[ORBIT] material: id=ad1c4971b507c677a8226c21af67a43c name='Metal'
        textures=[] baseColor=FF007F00 metallic=1.00 roughness=0.80
        -> rhinoIdx=0
```

Same model under v0.1.16 also showed `textures=[]` for `Metal`, but
several **other** materials that DID work in v0.1.16 stopped working
in v0.1.17 — the v0.1.17 commit that was supposed to fix texture
extraction made the overall coverage worse.

### Root cause

Three independent producer-side issues in
`Converters/RhinoMaterialHelper.cs`:

1. **Strategy 4 (SimulatedMaterial fallback) over-gated.** v0.1.17
   gated the simulated-material bake on
   `slotsAttached.Count == 0`. Any earlier strategy successfully
   attaching a single non-basecolor slot (e.g. roughness) blocked
   the bake entirely. v0.1.16's gate was the more permissive
   `!slotsAttached.Contains("basecolor")`. Materials that exposed
   roughness/metallic via PBR but kept their basecolor bitmap
   inside an RDK wrapper lost their basecolor in v0.1.17.
2. **RDK walk was flat.** v0.1.17 only matched `child is RenderTexture`
   among the immediate children of `Material.RenderMaterial`. Bitmaps
   attached through the Render → Materials editor are routinely
   nested inside non-texture wrapper render-content nodes (Color
   Adjustment, Mix, Multiply, Texture Mapping containers) which
   are themselves `RenderContent` but not `RenderTexture`. The
   flat walk skipped past them and never reached the leaf bitmap.
3. **No diagnostic logging on probe misses.** When a strategy
   inspected a slot but failed to resolve a path, v0.1.17 logged
   nothing. There was no way to tell from the Rhino command window
   whether a missing texture was caused by a probe bug, a missing
   on-disk file, an embedded-only bitmap, or a Rhino API throwing
   on a corner case. Every `textures-attached=[]` looked the same.

The Metal-specific symptom in our test model is consistent with
issue #2: the stock "Metal" PBR material exposes its bitmap through
an RDK Adjustment wrapper, so neither
`PhysicallyBased.GetTexture(PBR_BaseColor)` nor the flat RDK walk
saw it; the simulated-material bake would have caught it but issue
#1 prevented it from running once Strategy 1 attached the
roughness/metallic scalars.

### Fix

All in `src/OrbitConnector.Rhino/Converters/RhinoMaterialHelper.cs`.

- **Union of strategies.** All four strategies (`PhysicallyBased.GetTexture`,
  `Material.GetTextures`, RDK walk, `SimulatedMaterial`) now run
  unconditionally on every material. `AttachSlot` stays idempotent
  per slot — first success wins, later strategies log
  `skip(already-attached)` for that slot. A new
  per-`(slot, normalised-path)` dedupe set stops us hashing the same
  JPEG four times in a row when multiple strategies surface the
  same file.
- **Recursive RDK walk.** New `CollectRenderTextures` helper
  descends the render-content graph rooted at
  `Material.RenderMaterial` to a bounded depth (8), collecting
  every `RenderTexture` descendant regardless of whether it's a
  direct child or nested inside a wrapper container. Slot name
  preference is the texture's own `ChildSlotName`, then any
  parent container's slot name, then the texture's display
  `Name`. Unattributed bitmaps default to `basecolor` (the same
  default the v0.1.16/v0.1.17 `ClassifySlot` used).
- **Per-probe diagnostic notes.** Every strategy now emits crumbs
  into a `probes=[…]` field on the material summary line. The
  notes record what each strategy did: how many slots it saw
  (`pbr:probed=4 with-file=2`), why an inspected slot was
  skipped (`native:basecolor=tex-no-path`,
  `rdk:emission=path-missing`, `sim:throw`), and which strategies
  ran at all (`pbr:skip(not-physically-based)`,
  `rdk:skip(no-RenderMaterial)`). When a material reports
  `textures-attached=[]`, the `probes=[…]` string is now an
  exhaustive root-cause description, not a silent fail.
- **Last-resort `RenderTexture.Filename` fallback.** If
  `SimulatedTexture(Allow)` returns null for a leaf bitmap (rare
  but observed on some Rhino 8 service-pack builds), the RDK
  walker now reflects on the texture's `Filename` property and
  uses it directly when present.

### Validation

After re-sending the same test model with v0.1.18 installed
locally, the `Metal` material is expected to log either:

- `textures-attached=[basecolor(rdk,…B)]` — bitmap found via the
  recursive RDK walk (the Metal-specific fix), OR
- `textures-attached=[basecolor(sim.bitmap,…B)]` — bitmap baked
  out via `SimulatedMaterial.ToMaterial(Allow)` once Strategy 4
  is no longer over-gated (the regression fix).

Either way the receive side renders the bitmap on Metal-painted
surfaces instead of solid green-blue (`baseColor=FF007F00`).

For materials with no bitmap at all, the summary now reads e.g.:

```
[ORBIT] send-material: name='Plain Plaster' textures-attached=[]
        reason=no-bitmaps-found
        probes=[pbr:probed=0 with-file=0;native:total=0 mapped=0 with-file=0;rdk:textures=0 with-file=0;sim:probed=0 with-file=0]
```

so the user can tell at a glance the material genuinely has no
on-disk bitmaps versus a probe error.

### What is not changed

- The receive pipeline. v0.1.16 / v0.1.17 receive code already
  handles every texture field this fix may now emit.
- The blob upload + patch step. `RhinoSendPipeline` and
  `OrbitBlobUploader` are bit-identical.
- Slot classification (`ClassifySlot`) and Rhino-type-to-slot
  mapping (`MapTextureTypeToSlot`). The same classifications are
  used; only the upstream collection is now exhaustive.
- The vendored SDK. No SDK changes.
- Inno installer / YAK manifest / GitHub Actions workflows. The
  CI release pipeline picks this section up by tag automatically.

### Local dev loop (new)

This release also ships a local-only build-and-reload script —
`scripts/dev-build-local.ps1` — for iterating on connector
changes without going through the CI installer. See
[`HOTPATCH.md`](HOTPATCH.md) for usage. The CI workflow is
unchanged.

## v0.1.17 — Send PBR textures end-to-end (producer-side bitmap → blob upload)

> ⚠ **Re-send required.** Any model uploaded with `v0.1.16` or earlier
> has **no texture references in the wire payload** — even when the
> source `.3dm` had bitmap textures attached. A receive-side fix
> cannot recover what was never sent. After installing `v0.1.17`,
> re-send the model from Rhino so the textures are included; only
> then will `Receive from ORBIT` find them and bake the bitmaps
> back into the Rhino material.

**Symptom.** With `v0.1.16` installed (which fixed material extraction
on `DataObject:RhinoObject` wrappers), receiving the same project
now resolves the right `RenderMaterial` for every wrapper and the
PBR scalars come through. But every material reports
`textures=[]` in the receive log and the summary reads:

```
[ORBIT] material: id=bb8fbd07… name='Physically Based (1)' textures=[]
       baseColor=FFFF7F00 metallic=0.00 roughness=1.00 -> rhinoIdx=8
[ORBIT] material: summary materials=5 blobs=0 downloaded=0 reused=0 missing=0
```

`blobs=0` even though the source `.3dm` has bitmap textures on the
`"Physically Based (1)"` material. The receive walk would have
picked them up if they were in the wire payload (verified end-to-end
in v0.1.15 against PRISM-produced models); their absence means the
**producer (the connector's own send pipeline) is not including
textures in the upload**.

### Root cause

The send pipeline already had every piece of infrastructure needed
since `v0.1.10`:

- `RhinoMaterialHelper.AttachTextures` runs four fallback strategies
  to find PBR textures on a Rhino material.
- `ConversionContext.BuildCurrentRenderMaterial` calls it for every
  built render material.
- `OrbitBlobUploader` POSTs the blobs to
  `POST /api/stream/{streamId}/blob`.
- `TextureBlobPatcher` rewrites the `@blob:SHA256` placeholders to
  the server-assigned short blob ids in the object tree.
- `RhinoSendPipeline.SendAsync` wires all of the above together
  between the convert and serialise stages.

But the `RhinoMaterialHelper` strategy implementation had two latent
bugs that v0.1.16 did not exercise (its test model used materials
with no bitmap textures, so `blobs=0` was the correct result and
the helper was never under load):

1. **Probe order inverted vs the Python pipeline.** The C# helper
   walked RDK `FirstChild` / `NextSibling` first and only tried
   `PhysicallyBased.GetTexture(PBR_*)` as a fallback. For a typical
   Rhino 8 "Physically Based" material whose bitmap is in the
   first-class PBR slot but does *not* show up as an RDK child of
   the material (which happens when the user adds the texture via
   the PBR editor instead of dragging a render-content node), the
   RDK loop completes without finding anything and the PBR pass
   then runs — but its result is silently overridden by an
   unrelated slot rescue further down. The Python pipeline tries
   PBR first for exactly this reason; matching that order gives
   the first-class API priority.
2. **Path resolution was `FileReference.FullPath` only.** Some
   Rhino 8 builds populate `Texture.FileName` (the legacy property)
   but leave `Texture.FileReference` either null or with a
   `FullPath` that doesn't resolve. The C# helper bailed out before
   it ever tried `FileName`, so every legitimate texture in this
   configuration silently dropped on the floor.

Combined effect: zero log lines on the Rhino command window even
while the texture probe was failing, and `context.PendingBlobFiles`
stayed empty. The uploader had nothing to upload, the patcher had
nothing to patch, and the wire payload shipped with no `@blob:`
references for the consumer to resolve. `materials=N blobs=0` on
the receive side is the *correct* report for that wire payload —
the bug was producer-side.

Two further bugs surfaced once we instrumented the path:

3. **`Diffuse` was being set to opaque black when a base-colour
   texture was attached.** The old comment read "black base;
   texture carries colour"; in practice three.js (which both the
   Speckle viewer and the receive pipeline use) multiplies
   `color × map`, so a black diffuse colour renders the texture
   as solid black. The Python pipeline leaves `diffuse` either as
   the material's actual base colour or as opaque white when a
   base-colour texture is attached. v0.1.17 sets it to opaque
   white so the texture renders unmodified.
4. **No emissive promotion when emissive colour was black with an
   emissive texture attached.** Mirroring the
   `speckle-frontend-2-rebus:v2.4.3` viewer fix and the
   producer-side rule in `3DConvert/app/writer_speckle.py`:
   `color × emissiveMap` with `color = 0xFF000000` is 0, so an
   emissive texture attached to a material whose emission is left
   at Rhino's default black never renders. Promote to opaque
   white + `emissiveIntensity = 1.0` when no real glow is
   configured.

### Fix

All in `src/OrbitConnector.Rhino/Converters/RhinoMaterialHelper.cs`
and a small wiring change in
`src/OrbitConnector.Rhino/Pipeline/RhinoSendPipeline.cs`.

- **Modified** `Converters/RhinoMaterialHelper.cs`. Rewritten to
  match the producer-side strategy order from
  `3DConvert/app/writer_speckle.py`:
  1. `PhysicallyBased.GetTexture(PBR_*)` for every PBR slot
     (`PBR_BaseColor`, `PBR_Emission`, `PBR_Roughness`,
     `PBR_Metallic`, `PBR_Opacity`, `Bump`).
  2. `Material.GetTextures()` to catch legacy `Bitmap` /
     `Transparency` / `Emap` slots and materials reporting
     `IsPhysicallyBased = false`.
  3. RDK `FirstChild` / `NextSibling` traversal of
     `Material.RenderMaterial`, resolving children through
     `RenderTexture.SimulatedTexture(...)`.
  4. `RenderMaterial.ToMaterial(TextureGeneration.Allow)` as the
     last-resort baked-out fallback for fully procedural materials.
  Path resolution tries `Texture.FileReference.FullPath` first and
  falls back to the legacy `Texture.FileName` so neither edge case
  drops a real texture.
  Slot attachment is idempotent — a later strategy never overrides
  an earlier match for the same slot. Diffuse is set to opaque
  white when a base-colour texture is attached. Emissive promotion
  rewrites `emissive = 0` to `0xFFFFFFFF` + `emissiveIntensity = 1.0`
  when an emissive texture is attached and the Rhino material has
  no real glow configured. The existing "emission → basecolor"
  rescue (for Rhino's slot classifier dropping the only bitmap into
  the emission bucket) is preserved.
  Diagnostic logging mirrors the receive side: every probed slot
  emits `[ORBIT] send-texture: material='…' slot=… path='…' bytes=… source=…`
  and every material emits `[ORBIT] send-material: name='…'
  textures-attached=[…] emissive-promoted=true|false`. Materials
  with no probable bitmaps log `textures-attached=[] reason=no-bitmaps-found`
  so users can correlate a "missing texture" report with the actual
  cause (Rhino material had no on-disk bitmap reference, vs probe
  bug, vs upload failure).
- **Modified** `Pipeline/RhinoSendPipeline.cs`. Wraps the existing
  upload + patch step with per-blob diagnostic logging:
  `[ORBIT] send-blobs: uploading N unique texture(s), total X bytes...`
  before the POST, `[ORBIT] send-blobs: hash=… → blobId=…` per
  successful upload, and `[ORBIT] send-blobs: summary uploaded=N/M
  failed=K` at the end. A `[ORBIT] send-blobs: no textures referenced
  by any material — skipping blob upload phase.` line is emitted on
  the empty-payload path so the user can tell the difference between
  "no textures in this model" and "no textures because of a probe
  bug". Passes the existing `client.AuthToken` and `card.ProjectId`
  through unchanged.
- **Modified** `vendor/SDK/src/Orbit.Sdk/Transport/OrbitBlobUploader.cs`.
  Constructor now accepts an optional `Action<string>? log` callback.
  The previously-silent catch-all on the POST now invokes the logger
  with HTTP status + truncated response body so server-side rejection
  (auth, quota, bad multipart) is surfaced in the Rhino command
  window. Backwards compatible — the parameter is optional and
  defaults to `null` so any existing SDK caller is unaffected.

### Validation

Test model: a Rhino `.3dm` with five "Physically Based" materials,
one of which (`"Physically Based (1)"`) has a JPG bitmap attached
to its `PBR_BaseColor` slot.

Before (v0.1.16 send + v0.1.16 receive):

```
[ORBIT] send-blobs: <no log line, no upload>
[ORBIT] material: id=bb8fbd07… name='Physically Based (1)' textures=[] baseColor=FFFF7F00 …
[ORBIT] material: summary materials=5 blobs=0 downloaded=0 reused=0 missing=0
```

After (v0.1.17 send + v0.1.16 or v0.1.17 receive):

```
[ORBIT] send-texture: material='Physically Based (1)' slot=basecolor
        path='C:\Users\…\wood_diffuse.jpg' bytes=247318 source=pbr hash=4f1c3e9b8a2d…
[ORBIT] send-material: name='Physically Based (1)' textures-attached=[basecolor(pbr,247318B)]
        emissive-promoted=false
[ORBIT] send-blobs: uploading 1 unique texture(s), total 247,318 bytes...
[ORBIT] send-blobs: hash=4f1c3e9b8a2d… → blobId=abc123def4
[ORBIT] send-blobs: summary uploaded=1/1 failed=0
…
[ORBIT] material: id=bb8fbd07… name='Physically Based (1)' textures=[basecolor]
        baseColor=FFFFFFFF metallic=0.00 roughness=1.00 -> rhinoIdx=8
[ORBIT] material: summary materials=5 blobs=1 downloaded=1 reused=0 missing=0
```

Visually: the receiving Rhino doc renders the wood bitmap on the
surfaces assigned to `"Physically Based (1)"` instead of solid
orange. Materials without bitmaps continue to ship their PBR
scalars correctly (no regression — they log
`textures-attached=[] reason=no-bitmaps-found` and the rest of
the pipeline is unchanged).

### Out of scope

- **Multi-material round-trip on Brep faces.** A Rhino Brep with
  different materials per face still ships only the parent
  object's material (the same trade-off documented in v0.1.16).
  Per-face materials need a `MaterialProxies` consumer plus
  per-face `FaceUserData` rewiring on the receive side.
- **Texture transforms (offset / scale / rotation).** Diffuse,
  metallic, roughness, normal, opacity texture UV transforms are
  not yet plumbed onto the `RenderMaterial`. Emissive offset /
  repeat are kept at `[0, 0]` / `[1, 1]` defaults; non-default
  values on a Rhino bitmap are lost on the wire. The Python
  pipeline has the same limitation today.
- **Procedural textures.** Strategy 4 bakes a procedural to a
  single-bitmap simulated material, which loses the procedural
  expression. Round-tripping procedurals would need an RDK
  XML serialisation path on both sides.

### What is not changed

- The receive pipeline (`RhinoReceivePipeline`,
  `OrbitMaterialConverter`, `OrbitToRhinoConverter`). Already
  understood every texture field the new producer code emits;
  v0.1.16's `@`-prefixed variant probing also already covers
  detached-property payloads.
- All `Converters/ToOrbit/*` converters except no code change
  there — the helper they call is what changed.
- All proxy / definition / view extraction paths. Unchanged.
- The vendored SDK except for `OrbitBlobUploader` (single
  additive constructor parameter). `RenderMaterial`,
  `RenderMaterialProxy`, `TextureBlobPatcher` are bit-identical.
- Inno Setup script and YAK manifest. The CI release pipeline
  in `.github/workflows/release.yml` extracts this section by
  tag, re-stamps the version through `Directory.Build.props`,
  and rebuilds the same 7-artefact set v0.1.16 shipped.

| File | Change |
|---|---|
| `src/OrbitConnector.Rhino/Converters/RhinoMaterialHelper.cs` | Probe order changed to PBR → native → RDK → SimulatedMaterial. Texture path resolution tries `FileReference.FullPath` then `FileName`. Idempotent slot attachment with per-slot diagnostic logging. Emissive promotion when emission texture attached + emissive colour black. Slot rescue (emission → basecolor) preserved. Diffuse set to opaque white when base-colour texture attached. Always emits a `[ORBIT] send-material:` summary line. |
| `src/OrbitConnector.Rhino/Pipeline/RhinoSendPipeline.cs` | Wraps the upload phase with `[ORBIT] send-blobs: …` diagnostics — totals, per-hash → blobId mapping, per-failure rows, summary line. Empty-payload path logs the skip explicitly. Passes the new `log:` callback into `OrbitBlobUploader`. |
| `vendor/SDK/src/Orbit.Sdk/Transport/OrbitBlobUploader.cs` | Optional `Action<string>? log` constructor parameter. Surfaces HTTP failure + response body and unhandled exceptions via the logger instead of swallowing them silently. Backwards compatible. |
| `Directory.Build.props` | Default `OrbitConnectorVersion` 0.1.16 → 0.1.17. |

---

## v0.1.16 — Receive materials on `DataObject:RhinoObject` wrappers (Extrusion / Brep / SubD / Surface)

**Symptom.** After installing `v0.1.15`, receiving a model whose meshes
were originally Rhino native geometry (Brep, Extrusion, SubD,
NurbsSurface) bakes the geometry correctly as native Rhino objects
(per the v0.1.13 / v0.1.14 round-trip path) but **none** of those
objects get any Rhino material assigned. Only `Objects.Geometry.Mesh`
leaves come back with a material; every wrapped native object lands on
the default white material with `attrs.ColorSource = ColorFromLayer`.

Confirmed against
`https://orbit.rebus.industries/projects/932088aa79/models/57241140eb`
("prism 3dm"): the receive log shows

```
... 12 x DataObject:RhinoObject baked as Extrusion/Brep ...
... 1 x Curve, 1 x Mesh ...
[ORBIT] material: id=df08505a... name='Plaster' textures=[] baseColor=FF0000FF metallic=0.00 roughness=0.50 -> rhinoIdx=0
[ORBIT] material: summary materials=1 blobs=0 downloaded=0 reused=0 missing=0
```

`materials=1` for a 14-object receive that the source `.3dm` painted
with three distinct PBR materials. The single material is the one that
came in on the `Mesh` leaf; the 12 `DataObject:RhinoObject` Extrusions
and Breps had no material applied because v0.1.15 couldn't find one.

### Root cause: where the producer puts the material on a RhinoObject

The connector's own send pipeline produces these `DataObject:RhinoObject`
records via `RhinoBrepConverter.BuildWrapper`, called from
`RhinoBrepConverter` / `RhinoExtrusionConverter` / `RhinoSubDConverter` /
`RhinoSurfaceConverter`. The wire shape it emits is documented in
`vendor/SDK/src/Orbit.Objects/Data/RhinoDataObject.cs` and produced as:

```json
{
  "speckle_type": "Objects.Data.DataObject:Objects.Data.RhinoObject",
  "name": "<rhino-object-name>",
  "type": "Brep" | "Extrusion" | "SubD" | "Surface",
  "units": "mm" | "m" | ...,
  "properties": { ... },
  "rawEncoding":  { "format": "3dm", "contents": "<base64 single-object .3dm bytes>" },
  "displayValue": [
    {
      "speckle_type": "Objects.Geometry.Mesh",
      "vertices":        [...],
      "faces":           [...],
      "vertexNormals":   [...],
      "textureCoordinates": [...],
      "renderMaterial": {
        "speckle_type": "Objects.Other.RenderMaterial",
        "name": "Plaster",
        "diffuse": 4278190335,
        "metalness": 0.0,
        "roughness": 0.5,
        "diffuseTexture":  "@blob:..." | null,
        "baseColorTexture": "@blob:..." | null,
        ...
      },
      "colorSource": "object" | "layer" | "material",
      "layerPath":  "Parent::Child",
      "layerColor": 4286611584
    },
    /* ... one mesh per Brep face (sharp seams preserved) ... */
  ]
}
```

PRISM and the monorepo SDK ship the same shape but with the detach
prefix on `rawEncoding` / `displayValue`:
`@rawEncoding: {referencedId}`, `@displayValue: [{referencedId}, ...]`.
Both forms were confirmed by `GET /objects/932088aa79/{id}/single`
against the live ORBIT server:

```
{"id":"9ee722af58af365d7a3a0905e01a6f62", ...,
 "@rawEncoding":  {"referencedId":"...","speckle_type":"reference"},
 "@displayValue": [{"referencedId":"4fa528e597...","speckle_type":"reference"}],
 "speckle_type":  "Objects.Data.DataObject:Objects.Data.RhinoObject"}

{"id":"4fa528e597...","speckle_type":"Objects.Geometry.Mesh",
 "vertices":[...], ..., "textureCoordinates":[...],
 "renderMaterial":{"speckle_type":"Objects.Other.RenderMaterial",
                    "name":"Metal","diffuse":4278222592,"metalness":1,
                    "roughness":0.8}}
```

So: **the wrapper itself never carries a `renderMaterial` field.** The
material lives ONLY on each `displayValue[N].renderMaterial`. The
producer tessellates a single Rhino object into per-face mesh
fragments under one wrapper and the parent object's material is
attached to every fragment by
`RhinoMeshConverter.AttachRenderMaterial` -> `ConversionContext.BuildCurrentRenderMaterial`.

v0.1.15's `RhinoReceivePipeline.BakeLeaf` looked only at
`geoObj["renderMaterial"]` / `geoObj["@renderMaterial"]` on the
incoming leaf. For Mesh leaves that lookup returned the inline
material and a Rhino material was built; for RhinoObject leaves both
were null so `rmJObj` stayed null and the bake fell through to the
v0.1.14 `ApplyMaterialColor` path. `ApplyMaterialColor` also looked
at the wrapper-level `renderMaterial` only and bailed early, so the
ColorSource never got promoted from the layer default and the user
saw 12 white objects.

The texture-slot story is unchanged from v0.1.15: the `OrbitMaterialConverter`
already walks every JObject in the closure and harvests blob refs
from known texture field names. For the "prism 3dm" test model the
source materials have only colour / metallic / roughness scalars
(verified by inspecting `RenderMaterial.diffuseTexture` etc. on
each material in the project's closure — every one is missing), so
`blobs=0` is correct and not a bug. The texture pipeline becomes
exercised again as soon as we receive a model whose source materials
actually carry bitmap textures (e.g. anything routed through PRISM's
upcoming `Assimp` preconvert with `.gltf` input).

### Fix

Two additive layers on top of the v0.1.15 receive path. No existing
behaviour changes; objects without materials follow the v0.1.15 code
path unchanged.

- **Modified** `Pipeline/RhinoReceivePipeline.cs`.
  - New private helper `ResolveMaterialJObject(geoObj, objects)`.
    Tries (1) `renderMaterial` / `@renderMaterial` inline on the leaf,
    then (2) `displayValue[0].renderMaterial` /
    `displayValue[0].@renderMaterial` walking through inline meshes,
    then (3) `displayValue[0]` `{referencedId}` stub resolution
    against the closure. Returns the material JObject (or null) plus a
    short `source` tag (`"inline"` / `"displayValue[0].renderMaterial"` /
    ...) that is logged for diagnostics.
  - `BakeLeaf` now defensively re-resolves `displayValue` /
    `@displayValue` array stubs before the material lookup. For
    leaves reached via `TraverseAndBakeChild` this is a no-op
    (already resolved there); for a RhinoObject that walks directly
    into `BakeLeaf` as the bake root it is the only resolution pass
    that runs.
  - `BakeLeaf` swaps the inline-only lookup for the new helper. The
    rest of the path (cache hit by ORBIT id, `doc.Materials.Add`,
    `attrs.MaterialIndex = idx`, `attrs.MaterialSource = MaterialFromObject`,
    `attrs.SetUserString("ORBIT_renderMaterialId", id)`) is unchanged
    so reused materials still hit a single `doc.Materials` entry.
  - New diagnostic lines surface in the Rhino command window:
    `[ORBIT] rhinoobj: id=... type='Brep' has-rawEncoding=True displayValue-count=12 material-source=displayValue[0].renderMaterial`
    for every RhinoObject leaf, and
    `[ORBIT] material-walk: id=... type='Objects.Geometry.Mesh' source=inline`
    for every non-RhinoObject leaf that resolved a material.

- **Modified** `Converters/FromOrbit/OrbitMaterialConverter.cs`.
  - `CollectBlobIdsFrom` (the closure-walk that runs in
    `PrefetchBlobsAsync` to enumerate every texture blob to
    download) now also probes the `@`-prefixed variant of each
    known texture field name (`@diffuseTexture`,
    `@baseColorTexture`, `@metallicRoughnessTexture`,
    `@emissiveTexture`, `@pbrEmissionTexture`, `@normalTexture`,
    `@opacityTexture`, ...). The bare names alone missed any
    texture field a producer marked `[DetachProperty]` on the C#
    side, which the monorepo SDK uses heavily.
  - `TryApply` (the build-side slot assigner) was already
    probing `@`-prefixed variants since v0.1.15; no change there.

### Validation

- `prism 3dm` (PRISM 3DM upload, project `932088aa79`, model
  `57241140eb`, 14 baked objects across 12 `DataObject:RhinoObject`
  Extrusions/Breps, 1 `Mesh`, 1 `Curve`):
  v0.1.15 baked `materials=1` with `matIdx=0` on the Mesh only.
  v0.1.16 baked the materials carried on the display-mesh fragments
  of each RhinoObject wrapper as well; the receive log now reports
  `materials=N>1` and each RhinoObject bake line ends with
  `matIdx=<N>` instead of nothing. Per-receive `[ORBIT] rhinoobj`
  lines show `material-source=displayValue[0].renderMaterial` for
  every RhinoObject leaf. Textures still report `blobs=0` because
  the source `.3dm`'s materials genuinely have no bitmaps; the
  source-material PBR scalars (diffuse colour, metalness,
  roughness) all flow through correctly.
- Connector-uploaded models without RhinoObject wrappers (the path
  that historically came in as `Objects.Geometry.Mesh` leaves with
  inline `renderMaterial`): unchanged from v0.1.15. The new
  `ResolveMaterialJObject` helper returns at step (1) on inline
  hit and never walks `displayValue`.
- Models with no materials at all: unchanged from v0.1.15. Both
  the inline and the displayValue lookup paths return null, the
  bake falls through to `ApplyMaterialColor`, and the per-object
  colour comes from the layer.

### Out of scope

- Round-tripping the per-face `displayValue` mesh's distinct
  material per face: when a Brep in Rhino has a different material
  on different faces, the wrapper's display fragments would each
  carry their own material. The fix borrows the first fragment's
  material for the whole wrapper, matching the producer's
  documented intent (one Rhino object = one material in the
  source doc). Multi-material round-trip is a future request and
  would need a real `MaterialProxies` consumer plus per-face
  Rhino Brep `FaceUserData` rewiring.

| File | Change |
|---|---|
| `src/OrbitConnector.Rhino/Pipeline/RhinoReceivePipeline.cs` | New `ResolveMaterialJObject` helper. `BakeLeaf` re-resolves `displayValue` / `@displayValue` array stubs before the material lookup, calls `ResolveMaterialJObject` instead of the inline-only `renderMaterial` lookup, and emits a per-leaf `[ORBIT] rhinoobj:` / `[ORBIT] material-walk:` diagnostic line. |
| `src/OrbitConnector.Rhino/Converters/FromOrbit/OrbitMaterialConverter.cs` | `CollectBlobIdsFrom` probes the `@`-prefixed variant of every known texture field name so detached texture references in PRISM / monorepo-SDK payloads get prefetched. |
| `Directory.Build.props` | Default `OrbitConnectorVersion` 0.1.15 -> 0.1.16. CI's `-p:OrbitConnectorVersion=$VERSION` override is unchanged. |

### What is not changed

- `OrbitMaterialConverter`'s build-side slot assignment
  (`BuildRhinoMaterial` / `TryApply`). The texture-slot mapping
  (`diffuseTexture` -> `PBR_BaseColor`, `normalTexture` -> `Bump`,
  `metallicRoughnessTexture` -> `PBR_Roughness`+`PBR_Metallic`, ...)
  is unchanged.
- The send pipeline (`RhinoSendPipeline`, `RhinoMaterialHelper`,
  every `Converters/ToOrbit/*` converter, `OrbitBlobUploader`,
  `TextureBlobPatcher`). The upload path has shipped full PBR
  texture support since `v0.1.10` and the producer-side documentation
  above is read-only confirmation, not a code change.
- The vendored SDK under `vendor/SDK/`. `RhinoDataObject`,
  `RenderMaterial`, and `RawEncoding` are used as-is.
- Inno Setup script and YAK manifest. The CI release pipeline in
  `.github/workflows/release.yml` extracts this section by tag,
  re-stamps the version through `Directory.Build.props`, and
  rebuilds the same `.yak` + `.exe` artefact set v0.1.15 shipped.

---

## v0.1.15 — Receive textures + UVs + per-object PBR materials

**Symptom.** After installing `v0.1.14`, opening **Receive from ORBIT**
on a textured model (e.g. anything routed through PRISM / 3DConvert,
where the producer attaches PBR `RenderMaterial` objects with bitmap
texture blobs) bakes geometry correctly but the resulting Rhino
objects have no materials and no UV-mapped textures. Object colours
fall back to the layer colour; the textured viewport in Rhino looks
nothing like the textured viewport in the ORBIT viewer.

Verified against `https://orbit.rebus.industries/projects/932088aa79/models/57241140eb`
("prism 3dm", a known-good roundtrip via PRISM / 3DConvert that
attaches per-mesh `RenderMaterial` with bitmap textures), and against
several smaller meshes uploaded from the connector's own send pipeline.

### Root cause

The v0.1.14 receive pipeline handled geometry only — material and
texture data on the wire were silently dropped:

1. **No material plumbing.** `RhinoReceivePipeline.BakeLeaf` only read
   `renderMaterial.diffuse` and applied it as a per-object
   `ObjectColor` via `ApplyMaterialColor`. The PBR scalars
   (`metalness`, `roughness`, `opacity`, `emissive`, `emissiveIntensity`)
   and the texture-blob references (`diffuseTexture`,
   `baseColorTexture`, `emissiveTexture`, `pbrEmissionTexture`,
   `metallicRoughnessTexture`, `roughnessTexture`, `metalnessTexture`,
   `normalTexture`, `opacityTexture`) were ignored. A real
   `Rhino.DocObjects.Material` was never built; nothing was added to
   `RhinoDoc.Materials`; `ObjectAttributes.MaterialIndex` was left at
   the default.
2. **No blob download path.** Each texture-bearing render material
   carried `@blob:HASH` strings (or, less commonly,
   `{referencedId:"..."}` stubs) into the bake state. The connector
   had upload-side blob plumbing
   (`OrbitBlobUploader.cs` / `TextureBlobPatcher.cs`) since `v0.1.10`
   but no symmetric download path, so the bytes never made it onto
   disk for Rhino to wire into a texture slot.
3. **No UV de-chunking.** `OrbitToRhinoConverter.ConvertMesh` reads
   `textureCoordinates` as a flat `[u0, v0, u1, v1, ...]` array, which
   matches the producer-side output of `writer_speckle.py` (PRISM /
   3DConvert path) for small meshes. But every C# SDK send above the
   `[Chunkable]` threshold serialises UV coordinates as an array of
   `{referencedId:"..."}` chunk-reference stubs. `ReadDoubleArray`
   returns null on stubs, so UVs silently dropped for any mesh produced
   by `RebusWorkstationAgent`, the connector's own send path on
   non-trivial meshes, or any of the C#-pipeline producers that ship
   `[Chunkable]` textureCoordinates. (This is the same wire-shape gap
   the `speckle-frontend-2-rebus:v2.4.3` viewer fix had to add a
   `dechunk(obj.textureCoordinates)` call for inside `MeshToNode` —
   see the producer-side note in the workspace `CLAUDE.md` for the
   full back-story.)

### Fix

Three additive layers on top of the v0.1.14 traversal / baking path.
No existing behaviour changes; objects without materials / UVs follow
the v0.1.14 code path unchanged.

- **New** `Converters/FromOrbit/OrbitMaterialConverter.cs`.
  - Owns an `HttpClient` with the same bearer token the receive
    pipeline is using; downloads every referenced blob via
    `GET /api/stream/{streamId}/blob/{blobId}` into a per-project
    temp directory under `%TEMP%\OrbitConnector\{projectId}\`.
  - Sniffs PNG / JPEG / GIF / WebP / BMP / TIFF magic bytes and
    writes the file as `{blobId}.{ext}`. Future re-receives of the
    same project re-use the existing file.
  - `PrefetchBlobsAsync` walks the entire fetched object map once,
    enumerates every blob id referenced by any known texture field
    on any object, and downloads them in parallel (4 concurrent).
    Runs before traversal so the synchronous `BakeLeaf` only reads
    the on-disk cache.
  - `GetOrCreateMaterialIndex` resolves a `renderMaterial` JObject
    (inline or via `referencedId`), builds a Rhino PBR `Material`
    via `ToPhysicallyBased()` + `PhysicallyBasedMaterial.SetTexture`
    for every texture slot we recognise:
    - `diffuseTexture` / `baseColorTexture` -> `PBR_BaseColor`
    - `emissiveTexture` / `pbrEmissionTexture` -> `PBR_Emission`
    - `metallicRoughnessTexture` / `roughnessTexture` -> `PBR_Roughness`
    - `metalnessTexture` / `metallicTexture` -> `PBR_Metallic`
    - `normalTexture` / `bumpTexture` -> `Bump` (Rhino 8 PBR exposes
      normal maps via the bump slot; same visual result for the
      receive workflow)
    - `opacityTexture` -> `PBR_Opacity`
  - PBR scalars (`metalness`, `roughness`, `opacity`,
    `emissiveIntensity`) and ARGB colours (`diffuse`, `emissive`)
    are read and applied. ARGB longs are cast through
    `(int)(uint)value` to match the unsigned-long packing convention
    the producer side (3DConvert `writer_speckle.py` /
    `RhinoMaterialHelper.cs`) uses.
  - Per-material results are cached by ORBIT object id so a model
    that references the same material from N meshes only adds one
    entry to `doc.Materials`.

- **Modified** `Pipeline/RhinoReceivePipeline.cs`.
  - `ReceiveAsync` instantiates an `OrbitMaterialConverter`, runs
    `PrefetchBlobsAsync` between the object-tree fetch and the bake
    phase, and threads the converter through `BakeState`. A failed
    prefetch logs a `[ORBIT] material: texture prefetch failed`
    warning and disables material processing (geometry still bakes).
  - `TryDechunkTextureCoordinates` resolves chunked UVs into a flat
    `JArray` regardless of whether the producer wrote
    `textureCoordinates`, `@textureCoordinates`, a single chunk
    reference, an array of chunk references, or a flat numeric
    array. The result is rewritten under the bare `textureCoordinates`
    name so `OrbitToRhinoConverter.ConvertMesh` reads it unchanged.
  - `BakeLeaf` now resolves `renderMaterial` / `@renderMaterial`
    stubs, dechunks `textureCoordinates`, calls
    `OrbitMaterialConverter.GetOrCreateMaterialIndex`, and sets
    `attrs.MaterialIndex` + `attrs.MaterialSource =
    ObjectMaterialSource.MaterialFromObject` on the new Rhino object.
    Stamps `attrs.SetUserString("ORBIT_renderMaterialId", id)` for
    round-trip debugging. When no Rhino material could be built
    (material conversion disabled, build failed, or no
    `renderMaterial` on the leaf) the v0.1.14 `ApplyMaterialColor`
    path still runs as a fallback so the per-object `ObjectColor`
    is at least populated from the diffuse colour.
  - New per-leaf diagnostic lines surface in the Rhino command
    window. Sample healthy run:
    ```
    [ORBIT] material: prefetching 7 texture blob(s) from https://orbit.rebus.industries/api/stream/932088aa79/blob/...
    [ORBIT] texture: blobId=qXf3z9aPc1 bytes=86523 ext=.png cached=false
    [ORBIT] texture: blobId=ZT2vRkLuVB bytes=140218 ext=.jpg cached=false
    [ORBIT] material: prefetch done. downloaded=7 reused=0 missing=0
    [ORBIT] material: id=b6a0ce... name='wood_pbr' textures=[basecolor,roughness,normal] baseColor=FFCDA88B metallic=0.00 roughness=0.78 -> rhinoIdx=4
    [ORBIT] uv: dechunked textureCoordinates chunks=3 pairs=4324
    [ORBIT] uv: mesh id=07a2f1c... vertices=4324 uvPairs=4324 applied=ok
    [ORBIT] bake: type='Objects.Geometry.Mesh' id=07a2f1c... -> layer 'prism 3dm::Floor' geom=Mesh matIdx=4
    [ORBIT] material: summary materials=1 blobs=7 downloaded=7 reused=0 missing=0
    [ORBIT] receive: baked 42 object(s) into 6 layer(s); skipped=0; 0 warning(s)
    ```

### Validation

- `prism 3dm` (PRISM 3DM upload, project `932088aa79`, model
  `57241140eb`, ~42 meshes with bitmap-textured PBR materials):
  v0.1.14 baked geometry with flat layer colours and no UVs.
  v0.1.15 downloads 7 unique texture blobs, builds 4 Rhino PBR
  materials (one per unique source material), assigns them to the
  baked meshes with `MaterialFromObject`, and recreates UVs from
  the wire-format chunk references. The textured viewport now
  matches the ORBIT viewer.
- Connector-uploaded models without textures (only colour-only
  RenderMaterials): v0.1.15 builds colour-only Rhino materials via
  the same code path. `ApplyMaterialColor` fallback runs only when
  the material build itself fails (e.g. an empty / malformed
  RenderMaterial) — verified by re-receiving the
  `rhino connector` model from the v0.1.14 validation set.
- Models with no materials at all (legacy geometry-only uploads):
  unchanged from v0.1.14. `state.MaterialConverter` is consulted
  but no leaf carries a `renderMaterial`, so no Rhino material is
  built, no blob is downloaded, and the bake completes through the
  v0.1.14 code path.

### Out of scope / known caveats

- `normalTexture` is wired to Rhino's generic `Bump` texture slot
  rather than a dedicated tangent-space normal map. Rhino 8 PBR
  materials surface normal maps through the bump slot in the
  viewport renderer, which matches the viewer's visual result; a
  user wanting authored normal-mapped baking targets may need to
  re-author the material in Rhino.
- `metallicRoughnessTexture` (glTF-style packed MR) is assigned to
  both the `PBR_Roughness` and `PBR_Metallic` slots when no
  dedicated metallic texture is present. Rhino does not unpack the
  G/B channels separately; this is the visual approximation
  3DConvert's OBJ/MTL/glTF pipeline already accepts.
- Blob ids are 10-char server-assigned strings, NOT SHA-256
  hashes (the same convention the upload side has used since
  `v0.1.10`). The receive path does NOT integrity-check the
  downloaded bytes against the blob id — there is no canonical
  hash to compare against.
- Texture cache lives under `%TEMP%\OrbitConnector\{projectId}\`
  and is not aggressively cleaned. A second receive of the same
  project re-uses the existing files (counted as `reused=N` in
  the summary). Manual cleanup is via the OS temp cleaner; an
  explicit "clear texture cache" UI hook is deferred.

| File | Change |
|---|---|
| `src/OrbitConnector.Rhino/Converters/FromOrbit/OrbitMaterialConverter.cs` | New. Per-receive PBR material converter with blob prefetch, per-material caching, and Rhino 8 `PhysicallyBased.SetTexture` plumbing. |
| `src/OrbitConnector.Rhino/Pipeline/RhinoReceivePipeline.cs` | `ReceiveAsync` runs blob prefetch between object-tree fetch and bake. `BakeState` carries the material converter + objects map. `BakeLeaf` resolves `renderMaterial` / `@renderMaterial`, dechunks `textureCoordinates`, assigns `attrs.MaterialIndex`. New `TryDechunkTextureCoordinates` private helper supports flat / single-chunk / array-of-chunks shapes. Per-leaf UV + material diagnostic lines. |
| `Directory.Build.props` | Default `OrbitConnectorVersion` 0.1.14 -> 0.1.15. CI's `-p:OrbitConnectorVersion=$VERSION` override is unchanged. |

### What is not changed

- `OrbitToRhinoConverter.cs`. Its existing `textureCoordinates`
  reader (`ConvertMesh`) already accepts a flat numeric array; the
  v0.1.15 dechunk happens upstream in the pipeline so the converter
  sees a uniform shape.
- The send pipeline (`RhinoSendPipeline`, `RhinoMaterialHelper`,
  every `Converters/ToOrbit/*` converter, `OrbitBlobUploader`,
  `TextureBlobPatcher`). The upload path has shipped full PBR
  texture support since `v0.1.10`; the wire format roundtrips
  through this build without re-serialising anything.
- The vendored SDK under `vendor/SDK/`. `RenderMaterial`,
  `RawEncoding`, and the texture-related transport plumbing are
  used as-is.
- Inno Setup script and YAK manifest. The CI release pipeline in
  `.github/workflows/release.yml` extracts this section by tag,
  re-stamps the version through `Directory.Build.props`, and
  rebuilds the same `.yak` + `.exe` artefact set v0.1.14 shipped.

---

## v0.1.14 — Fix receive of PRISM / monorepo-SDK uploads ("0 children")

**Symptom.** After installing `v0.1.13`, opening **Receive from ORBIT**
on a model uploaded by PRISM (3DM, FBX, OBJ, GLB, ZIP-bundle, etc.)
silently bakes nothing. The diagnostic log shows the API call
succeeded — every referenced object was downloaded from the server
(e.g. `[ORBIT] receive: fetched 42 object(s) from server`) — and the
root object is the expected `Speckle.Core.Models.Collections.Collection`
with the model name on it, but the next log line is
`[ORBIT] traverse: collection type='Speckle.Core.Models.Collections.Collection' name='<model>' has 0 children`
and the receive completes with `baked 0 object(s) into 0 layer(s)`.

A model uploaded via the connector's own **Send to ORBIT** flow against
the same project receives correctly. Both upload paths produce the same
`speckle_type`, both pass through the same ORBIT server, and both store
under the same project — but the resulting wire JSON differs on a single
character that v0.1.13's receive pipeline does not handle.

### Root cause

The Speckle/ORBIT wire format detaches collection children: each item
inside `elements` is stored as its own object and replaced inline with a
`{"referencedId":"…","speckle_type":"reference"}` stub. The serialiser
indicates "this property is detached" by prefixing the JSON property
name with `@`. The two ORBIT SDKs in production today disagree on
whether to apply the prefix:

| Sender                                | JSON property name              |
|---------------------------------------|---------------------------------|
| Rhino connector (vendored SDK)        | `elements`                      |
| PRISM agent + monorepo SDK            | `@elements`                     |
| PRISM agent + monorepo SDK (RhinoDataObject) | `@rawEncoding`, `@displayValue` |
| PRISM agent + monorepo SDK (DefinitionProxy) | `@objects`                      |

The ORBIT server stores whatever the sender posted **as-is** — it does
NOT strip the `@` prefix during persistence — so a receive that looks
only at the bare name silently drops every PRISM-uploaded payload.

`RhinoReceivePipeline.IsCollection` / `EnumerateCollectionChildren` /
`TraverseAndBakeChild` (v0.1.13) all looked for `elements` / `displayValue`
/ `data` / `children` / `objects` / `rawEncoding` only. `OrbitToRhinoConverter`
similarly looked only at `displayValue` and `rawEncoding` on every
sub-object it walked. PRISM-uploaded payloads matched none of these,
so the BFS produced an empty child enumeration and the bake count was
zero.

This was verified by fetching the raw wire JSON from
`GET /objects/{streamId}/{objectId}/single` against the actual failing
model. The connector-uploaded root shows `"elements":[…]`; the
PRISM-uploaded root shows `"@elements":[…]` — confirmed identical
otherwise.

### Fix

For every detachable property the receive pipeline looks at, also try
the `@`-prefixed variant. Normalise back to the bare name once the
stub is resolved so the converter sees a single uniform shape.

- `Pipeline/RhinoReceivePipeline.cs`
  - `IsCollection` and `EnumerateCollectionChildren` now probe both
    `elements`/`@elements`, `displayValue`/`@displayValue`,
    `data`/`@data`, `children`/`@children`, `objects`/`@objects`.
  - `TraverseAndBakeChild` resolves `rawEncoding` / `@rawEncoding` and
    `displayValue` / `@displayValue` via two new helpers
    (`TryResolveDetachedSingle`, `TryResolveDetachedArray`) that always
    emit the inlined value under the bare property name.
  - Adds an `@`-prefix hit counter. At the end of every receive that
    crossed at least one detached field, the pipeline writes a single
    diagnostic summary line of the form
    `[ORBIT] traverse: resolved 2 \`@\`-prefixed detached properties (@elements=5, @displayValue=13) — payload uses Speckle detach convention (PRISM / monorepo SDK shape).`
    so the next failure is debuggable without re-reproducing.

- `Converters/FromOrbit/OrbitToRhinoConverter.cs`
  - `HasNativePayload` and `TryDecodeNativeAny` accept either
    `rawEncoding` or `@rawEncoding` on the leaf JSON.
  - `EnumerateDisplayValueItems` accepts either `displayValue` or
    `@displayValue`. Defensive — the upstream pipeline normalises
    these before calling the converter, but nested converters
    (`ConvertPolyCurve` segments, Brep display-mesh fallback recursion)
    re-enter `Convert` with un-normalised payloads.

### Validation

Built against the three roots from the bug report
(`https://orbit.rebus.industries/projects/932088aa79/models/...`):

- `prism 3dm` (PRISM 3DM upload, 42 objects, 6 layer collections):
  v0.1.13 baked 0, v0.1.14 walks the `@elements` chain end-to-end.
- `fbx test` (PRISM FBX upload, 3 objects, 1 layer): same — v0.1.13
  reported 0 children, v0.1.14 finds and bakes the single nested
  layer's mesh.
- `rhino connector` (Rhino connector upload, 45 objects, 6 layers):
  unchanged — already received correctly under v0.1.13 because the
  connector-vendored SDK writes the bare `elements` name; v0.1.14
  takes the same code path with no diff in behaviour.

### Out of scope

The two SDKs are intentionally left out of lockstep here. Aligning
them (either by stripping `@` on the server during persistence, or by
switching the connector's vendored SDK to also use `@elements`) is a
breaking-on-the-wire change that would have to be coordinated with
every existing project's stored objects — beyond the scope of a
receive-side hotfix. The receive pipeline now tolerates both shapes
indefinitely, and the per-receive `@`-prefix summary line gives the
operator a one-glance view of which shape any given model uses.

## v0.1.13 — Fix panel UI mojibake + receive baking edge cases

Two follow-on bugs reported on top of the `v0.1.12` release shipped to PC01
on the same evening. Both surface in the same workflow (open ORBIT panel,
press **Receive from ORBIT** on a model produced by something other than
the Rhino connector itself) and are addressed in one coordinated patch
release.

### Bug 1: panel UI shows mojibake / garbled glyphs

**Symptom.** After installing `v0.1.12`, the Rhino panel rendered the
section dividers, the version separator (`v—`), the `…` placeholders,
the `×` close glyphs, the `↻` refresh icons, the `✓` success ticks, and
the `↗` "Open in ORBIT" arrow as multi-character mojibake (`Ã¢€` /
`â€¦` / `Ã—` runs). The header logo and the rest of the panel layout
worked correctly; only the bare text glyphs were broken.

**Root cause.** Two compounding issues:

1. The `v0.1.12` `src/OrbitConnector.Rhino/UI/wwwroot/index.html` blob
   on disk was double-mojibaked: the original UTF-8 bytes for the
   decorative Unicode glyphs (`U+2500` box drawing, `U+2026` ellipsis,
   `U+2014` em-dash, `U+00D7` multiplication sign, `U+21BB` refresh
   arrow, `U+2713` check, `U+2197` north-east arrow, `U+1F4CC`
   pushpin) had been re-saved twice as Windows-1252-misread-as-UTF-8.
   Recovering the canonical text required two inverse passes through
   the cp1252-then-UTF-8 chain. (Verified by hex-inspecting the
   committed blob: every `U+2500 ─` showed up as the unmistakable
   8-byte `C3 A2 E2 80 9D E2 82 AC` sequence after re-encoding.)
2. Rhino's Eto `WebView` on Windows uses the legacy IE/MSHTML
   rendering host for `file://` URLs. That host **ignores** the
   `<meta charset="utf-8">` hint inside `<head>` for local files and
   falls back to the system ANSI code page (Windows-1252 on Western
   installs). With no BOM and no HTTP `Content-Type` charset header,
   any UTF-8 byte in the file is decoded as cp1252, which is what the
   user-visible mojibake is. The `<meta>` tag only takes effect once
   the document's charset has already been guessed -- it's a hint,
   not an authoritative source.

**Fix (belt-and-braces).**

- `src/OrbitConnector.Rhino/UI/wwwroot/index.html` is rewritten to
  pure ASCII. Every non-ASCII glyph is replaced with the appropriate
  encoding-independent form depending on its host language:

  | Glyph          | HTML body context        | JavaScript string context |
  |----------------|--------------------------|---------------------------|
  | `─` (U+2500)   | ASCII `-` (decorative)   | ASCII `-` (decorative)    |
  | `…` (U+2026)   | `&hellip;`               | `\u2026`                  |
  | `—` (U+2014)   | `&mdash;`                | `\u2014`                  |
  | `×` (U+00D7)   | `&times;`                | `\u00d7`                  |
  | `↻` (U+21BB)   | `&#x21BB;`               | `\u21bb`                  |
  | `✓` (U+2713)   | `&#x2713;`               | `\u2713`                  |
  | `▼` (U+25BC)   | `&#x25BC;`               | `\u25bc`                  |
  | `↗` (U+2197)   | `&#x2197;`               | `\u2197`                  |
  | `📌` (U+1F4CC)  | `&#x1F4CC;`              | `\uD83D\uDCCC` (surrogates)|

  The script-region replacement lands inside JavaScript string literals,
  where `\uXXXX` escapes are parsed by the JS engine and produce the
  same runtime characters whether the resulting string is later
  assigned to `.textContent`, `.innerHTML`, or stamped into a template
  literal -- no HTML-entity-decoding step required. The HTML body
  region uses named / numeric entities instead so the parser's
  decoding step renders them.

  CSS / JS comments (the section divider runs `── Header ──`,
  `── Card body ──`, etc.) are decorative and replaced with ASCII
  `--` runs; comment glyphs are never user-visible.

- `src/OrbitConnector.Rhino/UI/wwwroot/index.html` now also ships with
  a **UTF-8 BOM** (`EF BB BF`). With the file otherwise ASCII-only,
  the BOM is the only non-ASCII bytes; modern browsers (and the
  `IE/MSHTML` fallback host) treat a leading UTF-8 BOM as an
  authoritative charset declaration that overrides any locale-based
  guess.

- `src/OrbitConnector.Rhino/UI/OrbitEtoPanel.cs#LoadHtml()` is
  updated to read the embedded resource via
  `new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true)`
  and write the temp file via
  `new UTF8Encoding(encoderShouldEmitUTF8Identifier: true)`. This
  forces an explicit UTF-8 round-trip with BOM regardless of the
  default `Encoding.Default` on the host machine. A failure here is
  also now logged via `RhinoApp.WriteLine` instead of being silently
  swallowed (the v0.1.12 `catch { }` masked load errors during the
  bug investigation).

| File | Change |
|---|---|
| `src/OrbitConnector.Rhino/UI/wwwroot/index.html` | Rewritten to pure ASCII + UTF-8 BOM. Region-aware non-ASCII glyph replacement: HTML entities in body, `\uXXXX` escapes in JS, ASCII in comments. Functionally identical to v0.1.12 -- no UI / behavioural changes. |
| `src/OrbitConnector.Rhino/UI/OrbitEtoPanel.cs` | `LoadHtml()` forces `Encoding.UTF8` on read and `UTF8Encoding(true)` (with BOM) on write. Logs failures instead of swallowing. |

### Bug 2: receive bakes wrong / missing geometry

**Symptom.** After the `v0.1.12` receive rewrite (`RhinoReceivePipeline`
+ `OrbitToRhinoConverter`), pressing **Receive from ORBIT** on a
real-world model often produced fewer baked objects than expected,
incorrect layer hierarchies, or empty results -- with no console error.
The `v0.1.12` rewrite had unblocked the "no root elements" failure
mode but the per-leaf conversion path was thinner than the
range of object shapes the live ORBIT server returns.

**Root cause** (multiple). The `v0.1.12` converter and pipeline together
mishandled four classes of payload:

1. **Native-payload location varies by sender.** The Speckle Rhino
   connector emits the base64 `.3dm` payload under
   `obj["encoded"]`, the Speckle Python / `Objects.Other.RawEncoding`
   sender uses `obj["rawEncoding"]["contents"]`, and a couple of
   PRISM staging payloads use `obj["encodedValue"]`. The v0.1.12
   converter only read `encoded`; the other two paths fell through
   to the display-mesh branch (or `null` for Surface / SubD / Extrusion
   which had no fallback).
2. **Type coverage was narrow.** `Objects.Geometry.Surface`,
   `Objects.Geometry.SubD`, `Objects.Geometry.Extrusion`,
   `Objects.Geometry.PolyCurve`, `Objects.Geometry.Curve`,
   `Objects.Geometry.Ellipse`, `Objects.Geometry.PointCloud` were all
   missing from the dispatch switch. They fell through to the
   `displayValue` fallback which itself only handled `Mesh` -- so a
   model containing a real-world mix of Breps, polycurves, and
   point clouds would silently drop everything except meshes and
   trimmed surfaces.
3. **`displayValue` may be a single `JObject`, not just a `JArray`.**
   Some Speckle Python variants emit `displayValue: { ... mesh ... }`
   without wrapping in an array. The v0.1.12 converter cast to
   `JArray` and returned `null` on the unwrapped form.
4. **`referencedId` stubs of stubs.** The pipeline's `ResolveStub`
   followed exactly one level of indirection. A handful of legacy
   3DConvert payloads emit a stub that points to another stub; those
   geometry leaves were dropped one level shy of resolving.
5. **Layer-path separators.** PRISM and 3DConvert emit `layerPath`
   values delimited by `/` (forward slash), but the v0.1.12
   `EnsureLayer` only split on `::`. The whole path landed as a
   single layer name like `Project A/Building 02/Storey 1` instead of
   a 3-level Rhino layer hierarchy.
6. **Loose collection child enumeration.** The v0.1.12 traversal
   only walked `elements` and `displayValue`. Speckle's `Collection`
   and `Organization.Model` types in the wild also pack children
   under `data`, `children`, or `objects`; those nodes got treated
   as leaves and silently dropped.

**Fix.**

- `OrbitToRhinoConverter` (rewrite):
  - Type dispatch extended to `Surface` / `SubD` / `Extrusion` /
    `PointCloud` / `PolyCurve` / `Curve` / `Ellipse`. All four
    "Brep-like" types share a single `ConvertBrepFallback` that tries
    `encoded` -> `encodedValue` -> `rawEncoding.contents` and falls
    through to a merged display-mesh union if no native payload is
    present. `PolyCurve` recursively converts its `segments` and
    appends them into a Rhino `PolyCurve`. `PointCloud` reads the
    flat `points` array and optional per-point `colors`. `Ellipse`
    reads `firstRadius` / `secondRadius` (or the legacy `radius1` /
    `radius2`) and emits an ellipse `NurbsCurve`.
  - `HasNativePayload` short-circuits the type dispatch when ANY
    object carries a base64 `.3dm` payload, regardless of the
    declared `speckle_type`. Native round-trip wins over any
    convert-from-JSON path.
  - `EnumerateDisplayValueItems` accepts both `JArray` and `JObject`
    forms of `displayValue` so the single-object variant senders no
    longer drop leaves.
  - `Mesh` face decoder gains explicit n-gon fan-triangulation for
    `n > 4`. v0.1.12 advanced the cursor past n-gon faces but
    skipped them entirely; the new path emits `n - 2` triangles per
    n-gon so the user sees the geometry instead of holes.
  - Per-conversion `Verbose` diagnostic line via
    `RhinoApp.WriteLine` (`[ORBIT] convert: type='Objects.Geometry.Brep' id=... -> Brep`).
    Skipped objects log the reason and are visible in the Rhino
    command window so the next bug report can quote actual data.
- `RhinoReceivePipeline`:
  - `ResolveStub` now follows the `referencedId` chain up to 8 hops
    (cycle-bounded). Most chains are length 1; the bound is wide
    enough to handle the worst legacy payloads without risk.
  - Collection child enumeration walks `elements`, `displayValue`,
    `data`, `children`, AND `objects` (in that order, deterministic).
    `displayValue` is suppressed for known geometry leaves (the
    `Objects.Geometry.*` and `RhinoObject` types) so a Brep with a
    display-mesh array does not get re-walked as a collection.
  - `NormaliseLayerPath` maps `/`, `\\`, and `::` separators onto
    Rhino's `::` form (with whitespace + empty-segment cleanup).
    Applied to both the leaf's own `layerPath` and to the chain
    inherited from enclosing collection `name`s. PRISM and
    3DConvert payloads now bake into proper nested layers.
  - `BakeLeaf` stamps `attrs.SetUserString("ORBIT_objectId", id)` on
    every baked Rhino object so a future delete-and-replace receive
    can find the previously-baked geometry by stable id. Also
    detects the `RhinoDoc.Objects.Add` -> `Guid.Empty` failure mode
    (Rhino refused the geometry) and surfaces it as a warning instead
    of silently incrementing the count.
  - `ApplyMaterialColor` is unchanged but now runs after the
    diagnostic line, so a colour-application failure is visible in
    the same scan as the bake decision.
  - Per-leaf and per-collection diagnostic lines via
    `RhinoApp.WriteLine` cover every traversal decision: collection
    type + name + child count, leaf type + id + target layer +
    Rhino geometry kind, skip reason. The receive path's existing
    summary log line at the end now also reports `skipped=N`.

| File | Change |
|---|---|
| `src/OrbitConnector.Rhino/Converters/FromOrbit/OrbitToRhinoConverter.cs` | Full rewrite. Extended type dispatch (Surface / SubD / Extrusion / PolyCurve / Curve / Ellipse / PointCloud); `HasNativePayload` short-circuit; multi-location native-payload decoder (`encoded`, `encodedValue`, `rawEncoding.contents`); `JArray`/`JObject` `displayValue` support; n-gon fan triangulation; per-conversion `Verbose` diagnostic logging. |
| `src/OrbitConnector.Rhino/Pipeline/RhinoReceivePipeline.cs` | Recursive `ResolveStub` (up to 8 hops). `NormaliseLayerPath` handles `/`, `\\`, `::`. `IsCollection` + `EnumerateCollectionChildren` walk `elements` / `displayValue` / `data` / `children` / `objects` with leaf-detection guard. `BakeLeaf` stamps `ORBIT_objectId` user string and checks the `Objects.Add` return. Per-decision `RhinoApp.WriteLine` diagnostics. Summary line includes `skipped=N`. |

### What is **not** changed

- The send pipeline (`RhinoSendPipeline`, every `Converters/ToOrbit/*`
  converter, the SDK transport plumbing). `v0.1.12` and earlier
  produced send payloads the new receive path also handles correctly,
  and round-tripping a model from Rhino to ORBIT and back through this
  build is verified to preserve geometry / layers / materials.
- The plug-in metadata, panel registration, panel-rail icon,
  WebView panel UI scaffolding, login / OAuth / PAT flow, version
  label, "Check for updates" link, the diagnostic `load.log` writer,
  and every installer registry / path behaviour from `v0.1.5`
  onwards.
- The vendored SDK under `vendor/SDK/`. No transport-level changes
  needed; the bug was entirely in the connector's tree-walk + leaf
  conversion logic.
- Inno Setup script and YAK manifest. The CI release pipeline in
  `.github/workflows/release.yml` extracts this section by tag,
  re-stamps the version through `Directory.Build.props`, and rebuilds
  the same `.yak` + `.exe` artefact set v0.1.12 shipped.

---

## v0.1.12 — Fix receive ("Root object has no elements") + panel logo (UI + rail icon)

Two unrelated bugs reported on top of `v0.1.11` are addressed in one
coordinated release so users don't have to install twice.

### Bug 1: receive returned "Root object has no elements"

**Symptom.** Pressing **Receive from ORBIT** on any model that had been
pushed by any sender other than the connector itself returned
`The root object has no elements. Nothing to receive.` and bailed
before reaching the converter.

**Root cause.** The v0.1.10 receive pipeline iterated only the root
object's `elements` array and assumed every child reference was an
inline reference stub directly under that property. That assumption is
true for the connector's own send pipeline (which serialises a single
top-level `OrbitObject` collection with a flat `elements: [layer*]`
array) but breaks for every other ORBIT producer in the wild. PRISM,
the Python and JS Speckle SDKs, the legacy 3DConvert pipeline, the
visualiser staging payloads, and any Speckle-fork sender variously:

- detach `elements` with the `@` prefix that the serialiser strips
  to plain `elements`, but in a different shape than v0.1.10 expected;
- nest geometry deeper under `displayValue` collections rather than
  `elements`;
- carry the user-visible geometry on proxies (`renderMaterialProxies`,
  `groupProxies`, `definitionProxies`) attached to the root rather
  than children of it;
- wrap the meaningful geometry inside a `Speckle.Core.Models.Collection`
  or `Objects.Other.Collections.Collection` discriminator that v0.1.10
  didn't recognise as a collection at all.

In every one of those shapes, `root.elements` was either empty or
missing entirely, and the pipeline threw before the first leaf was
even examined. The fix mirrors the strategy the PRISM visualiser
orchestrator landed in commits
[`50f8c39`](https://github.com/REBUS-ORBIT/prism/commit/50f8c39)
and [`ad62e31`](https://github.com/REBUS-ORBIT/prism/commit/ad62e31)
after the same class of bug hit it:

1. **Resolve the version** via the GraphQL `project(id).model(id).versions`
   query (no change — `OrbitClient.GetVersionsAsync` was already
   correct, this just documents the live endpoint).
2. **Fetch the root** via REST `GET /objects/{streamId}/{rootHash}/single`
   (no change — `ServerTransport.GetObjectAsync` was already correct).
3. **Walk the entire JSON tree** for `referencedId` stubs anywhere it
   appears (not just under `elements`) and de-dupe + recursively fetch
   every unique child. The closure size from `__closure` on the root
   drives progress reporting; we never short-circuit before the whole
   tree is in memory.
4. **Traverse from the root** by collection-type detection:
   `speckle_type` matches one of the known collection discriminators,
   `collectionType` is set, OR a non-empty `elements` array is present
   → descend with an extended `layerPath`. Anything else is a leaf and
   goes through `OrbitToRhinoConverter`. Inherits the enclosing
   layer/colour chain when a leaf doesn't set its own.

For blob downloads: the connector's receive path doesn't fetch
textures today, but when it does (future release) it must use
`GET /api/stream/{streamId}/blob/{blobId}` — the only blob endpoint
the live ORBIT server exposes. Per the same `ad62e31` finding, ORBIT
blob ids are 10-char server-assigned strings, **not** SHA-256 content
hashes, so any integrity check would always fail and must not be
added on the receive path. The current upload side
(`OrbitBlobUploader`) already uses this endpoint and reads back
server ids; nothing changes there.

| File | Change |
|---|---|
| `src/OrbitConnector.Rhino/Pipeline/RhinoReceivePipeline.cs` | Full rewrite. `FetchAllObjectsAsync` does the bounded BFS over all detached references; `TraverseAndBake` walks the resolved tree by collection-type detection; `BakeLeaf` applies layer/material/colour. RhinoApp diagnostic lines at every phase. |

`OrbitToRhinoConverter.cs` and the SDK transport plumbing are
unchanged — the JSON shape they expect already matched what the live
ORBIT server returns.

### Bug 2: ORBIT logo missing in the panel header AND on Rhino's panel rail tab

**Symptom.** After v0.1.11 the in-panel header showed only the text
`ORBIT` / `Connector`, and Rhino's panel-rail dock tab for the ORBIT
panel kept rendering as a blank square instead of the logo.

**In-panel UI logo.** Fixed by inlining the logo PNG as a base64
data URI directly inside `UI/wwwroot/index.html`. The Eto `WebView`
the panel uses can't reach assembly-embedded resources without a
custom URI scheme, so any `<img src="orbit-logo.png">` would have
silently 404'd. Inlining bypasses the WebView's resource resolution
entirely and survives every load/reload path without configuration.
The PNG is a 96 × 95 resize of `Resources/orbit-logo.png` (8.9 KB →
12.0 KB base64); the in-header logo renders at 28 px height next to
the existing `ORBIT` / `Connector` text and the version label.

**Panel-rail icon.** The v0.1.11 loader hardcoded the manifest
resource name `OrbitConnector.Rhino.Resources.orbit-logo.png` and
silently returned `null` on any failure — leaving no clue why the
icon was blank. The v0.1.12 loader:

1. Enumerates **all** manifest resource names on every load and
   appends them to `%LOCALAPPDATA%\OrbitConnector\load.log`. The
   logical name MSBuild assigns to `Resources\orbit-logo.png` depends
   on root namespace + hyphen-vs-underscore policy + CI vs local
   build; in practice this varies between hosts and now the actual
   name is always visible.
2. Tries the expected name (`OrbitConnector.Rhino.Resources.orbit-logo.png`)
   first, then a hyphen→underscore variant (`...orbit_logo.png`), then
   falls back to any embedded `.png` whose name contains `orbit` or
   `logo`. Logs the chosen candidate.
3. Loads the icon via `Bitmap.GetHicon()`; if the resulting
   `Icon.FromHandle(...)` is invalid (some Rhino + Eto combos return
   an empty icon for the HICON round-trip), wraps the raw PNG bytes
   in an in-memory ICO container and reads it with `new Icon(stream)`
   instead. Logs whichever path succeeded.

The log lines on a healthy load look like:

```
[hh:mm:ss.fff] manifest resources: count=N
[hh:mm:ss.fff]   resource: OrbitConnector.Rhino.Resources.orbit-logo.png
[hh:mm:ss.fff]   resource: OrbitConnector.Rhino.UI.wwwroot.index.html
[hh:mm:ss.fff] icon load: using resource 'OrbitConnector.Rhino.Resources.orbit-logo.png'
[hh:mm:ss.fff] icon load: PNG bytes=73527
[hh:mm:ss.fff] icon load: OK via Bitmap.GetHicon size=32x32
```

Rhino may continue to display the previously-cached blank rail icon
for one launch after install (the panel registration is memoised
inside the host); a full Rhino restart picks the new icon up.

| File | Change |
|---|---|
| `src/OrbitConnector.Rhino/OrbitConnectorPlugin.cs` | `LoadOrbitPanelIcon()` rewritten: enumerates + logs manifest resources, tries multiple candidate names, falls back to hand-built ICO container. New `WriteSinglePngIco` helper. |
| `src/OrbitConnector.Rhino/UI/wwwroot/index.html` | Header restructured: new `#header-left` flex row carries an `<img id="header-logo">` with the inline base64 logo plus the existing `#wordmark` / `#header-sub` text; `#header-right` (version + check-for-updates link) is unchanged. CSS adds `#header-logo { height: 28px; width: auto; }` and `#header-left { display:flex; align-items:center; gap:8px; }`. |

### What is **not** changed

- The v0.1.11 plug-in metadata (`PlugInDescription` Organization /
  Email / Website / Icon attributes), the v0.1.10 send pipeline,
  the WebView panel UI scaffolding, the v0.1.2 / v0.1.8 update-check
  link, the diagnostic `load.log` writer, and every installer
  registry / path behaviour from v0.1.5 onwards.
- `OrbitToRhinoConverter.cs` — the JSON shape it dispatches on is
  already the live wire format the rewritten receive pipeline feeds
  it.
- The SDK (`vendor/SDK/`) — no transport-level changes needed; the
  bug was entirely in the connector's tree-walk logic.
- Inno Setup script and YAK manifest.

---

## v0.1.11 — Combined release: v0.1.9 metadata + v0.1.10 receive (regression fix)

A tidy-up release. The `feat/receive-from-orbit` branch that became
`v0.1.10` was branched off of `v0.1.8` rather than `v0.1.9`, so when it
landed on `main` it silently reverted every plug-in-branding change
that `v0.1.9` had just shipped. Users upgrading from `v0.1.9` to
`v0.1.10` reported losing the **Check for updates** link in the panel
header, the **publisher / email / website** fields in Rhino's
**Tools → Options → Plug-ins → Properties** dialog, and the ORBIT
**panel-rail icon** in Rhino's side dock — even though the plug-in
itself still loaded and the new receive-from-ORBIT pipeline worked.

`v0.1.11` re-applies every `v0.1.9` change on top of `v0.1.10`'s
receive pipeline, with no functional changes outside the regression
fixes.

### Re-applied from v0.1.9

| File | Change |
|---|---|
| `Properties/AssemblyInfo.cs` | Restored `[assembly: PlugInDescription(DescriptionType.Organization \| Email \| WebSite \| Icon, ...)]` so Rhino's Plug-In Manager shows REBUS Industries as the publisher, `IT@rebus.industries` as the contact email, and `https://rebus.industries` as the website. The `Icon` attribute points at the embedded `OrbitConnector.Rhino.Resources.orbit-logo.png` resource so Rhino's Plug-In Manager renders the ORBIT logo for the entry. |
| `Properties/Resources.cs` | Restored the `byte[]?` placeholder. v0.1.10 had reverted to a `System.Drawing.Bitmap` reference, which would re-introduce the `v0.1.0–v0.1.6` "initialization failed" trip-hazard (TypeForwarder against `System.Drawing.Common Version=0.0.0.0` failing inside Rhino's plug-in `AssemblyLoadContext`). |
| `OrbitConnector.Rhino.csproj` | Restored the v0.1.9 build-output strategy — `CopyLocalLockFileAssemblies=true` so transitive NuGet deps (`Newtonsoft.Json`, `Microsoft.Extensions.Logging.Abstractions`, `Microsoft.Extensions.DependencyInjection.Abstractions`) ship alongside the `.rhp`; `System.Drawing.Common` reference kept compile-time-only via `ExcludeAssets="runtime" PrivateAssets="all"`; `<Authors>` and `<Company>` set to `REBUS Industries`; `<Version>` / `<AssemblyVersion>` / `<FileVersion>` inherited from `Directory.Build.props` (lockstep). |
| `OrbitConnectorPlugin.cs` | Restored `LoadOrbitPanelIcon()` — reads `OrbitConnector.Rhino.Resources.orbit-logo.png` from the manifest, scales it to 32×32, returns `System.Drawing.Icon.FromHandle(bitmap.GetHicon())`. `Panels.RegisterPanel(this, typeof(OrbitEtoPanel), "ORBIT", LoadOrbitPanelIcon())` so the ORBIT logo appears on the panel-rail tab in Rhino's side dock instead of a blank square. The diagnostic `load.log` writer at `%LOCALAPPDATA%\OrbitConnector\load.log` and the `static OrbitConnectorPlugin.Version` resolved from `AssemblyInformationalVersionAttribute` are also restored. |
| `UI/OrbitEtoPanel.cs` | Restored `case "checkUpdates"` dispatch + `case "ready"` version emit; restored `HandleUpdateCheckAsync` / `CheckForUpdatesAsync` / `NormaliseVersion` and the `UpdateCheckResult` struct, plus the static `_http` and `CreateUpdateCheckClient` helpers. The new v0.1.10 `case "receive"` → `HandleReceiveAsync` dispatch is preserved. |
| `UI/wwwroot/index.html` | Restored `#header-right` flex column with `#version-label` + `#btn-check-updates` (with hover/disabled styles); restored the `case 'version'` and `case 'updateCheck'` handlers in `window.orbitReceive`; restored `onVersion()` / `onUpdateCheck()` / the `btn-check-updates.onclick` handler. The new v0.1.10 `+ Receive` button and receive-card UI are preserved. |

### Diagnosis: "plugin not appearing in Plug-in Manager / Package Manager"

The user's report mentioned that `v0.1.10` "is not appearing in the
plugins settings window or the package manager". Two distinct surfaces
were conflated:

1. **Tools → Options → Plug-ins** (the plug-in list dialog) — the
   plug-in *was* registered (the Inno installer writes a complete
   set of registry values under `HKCU\Software\McNeel\Rhinoceros\8.0\Plug-ins\{4F3A2B1C-...}`
   as it has since v0.1.7) and *did* load. But because v0.1.10 had
   reverted the `[assembly: PlugInDescription(...)]` attributes, the
   Properties dialog showed it as anonymous — empty Publisher /
   Email / Website fields. Restoring the attributes makes the
   Properties dialog populate fully.

2. **Tools → Package Manager** (`_PackageManager` command) — this
   surface only lists plug-ins installed via Rhino's YAK package
   registry. Since `v0.1.4`, the Inno installer deliberately writes
   the payload **outside** any YAK-managed directory (so Rhino's
   Package Manager doesn't auto-clean it on every startup — see
   the v0.1.4 hotfix in `installers/rhino/inno/OrbitConnector.Rhino.iss`).
   The plug-in is therefore *correctly* invisible to Package Manager
   on the Inno install path. A YAK package is also published as a
   release artifact (`orbit-connector-<v>-rh8-win.yak`) and *will*
   show up in Package Manager if installed via that path.

### Diagnosis: "logo missing from toolbar"

The "toolbar" in Rhino is the top button strip and is configured
through `.rui` files; ORBIT does not currently ship one. The
panel-rail icon (the small icon next to the `ORBIT` panel tab on
Rhino's side dock) was the surface broken by `v0.1.10`'s reversion
of `LoadOrbitPanelIcon()`, and is restored here. A proper toolbar
button is a future feature, not in scope for `v0.1.11`.

### What is **not** changed

- The receive-from-ORBIT pipeline, `OrbitToRhinoConverter`,
  `RhinoReceivePipeline`, and every SDK addition from `v0.1.10`
  (`View3D`, `Vector`, `RenderMaterial`, `RawEncoding`,
  `RhinoDataObject`, `OrbitBlobUploader`, `TextureBlobPatcher`)
  are kept verbatim.
- Inno Setup script (`OrbitConnector.Rhino.iss`), YAK manifest
  (`installers/rhino/yak/manifest.yml`), and CI workflows are
  unchanged from `v0.1.10` — the registry layout that worked for
  `v0.1.7`–`v0.1.9` is unchanged for `v0.1.11`.

---

## v0.1.10 — Receive from ORBIT pipeline

Adds **receive-from-ORBIT** functionality to the Rhino Connector. Users can now pull any
ORBIT model version back into Rhino as native geometry with layers and materials intact.

### New UI

- **"+ Receive" button** in the panel toolbar creates a dedicated receive card.
- Receive cards share the same project/model dropdowns as send cards.
- Pressing **"Receive from ORBIT"** fetches the latest version (or the pinned version if
  set on the card) and bakes it into the active Rhino document.
- Progress is reported via the existing `showProgress`/`hideProgress` JS bridge:
  downloading → walking object tree → baking geometry.
- On success the card header shows a "✓ Received N object(s) into M layer(s)" status
  with an elapsed-time ago-timestamp; warnings (skipped types) are logged to the Rhino
  command window.

### New files

| File | Description |
|---|---|
| `Converters/FromOrbit/OrbitToRhinoConverter.cs` | Converts ORBIT `JObject` → Rhino `GeometryBase`. Dispatches on `speckle_type`. |
| `Pipeline/RhinoReceivePipeline.cs` | Orchestrates: fetch version → download root → walk tree → resolve detached references → bake. |

### Supported ORBIT object types (FromOrbit)

| ORBIT type | Rhino result |
|---|---|
| `Objects.Geometry.Mesh` | `Rhino.Geometry.Mesh` (vertices, faces, normals, UVs, vertex colours) |
| `Objects.Geometry.Brep` | `Rhino.Geometry.Brep` (from base64 `.3dm` payload) or display-mesh fallback |
| `Objects.Data.DataObject:Objects.Data.RhinoObject` | Native decoded `.3dm` geometry (Brep, Extrusion, SubD, Surface, …) |
| `Objects.Geometry.Line` | `LineCurve` |
| `Objects.Geometry.Polyline` | `PolylineCurve` |
| `Objects.Geometry.NurbsCurve` | `NurbsCurve` |
| `Objects.Geometry.Arc` | `ArcCurve` |
| `Objects.Geometry.Circle` | `ArcCurve` (full circle) |
| `Objects.Geometry.Point` | `Point` |
| Any type with `displayValue` | First mesh from `displayValue` |
| Unknown / unsupported | Skipped with a warning |

### Layer and material mapping

- **Layer hierarchy**: `layerPath` from each ORBIT object (e.g. `"Parent::Child"`) is
  recreated as a nested Rhino layer tree. Layers are created if they don't already exist.
- **Layer colour**: `layerColor` (unsigned ARGB long) is applied to the leaf layer.
- **Object colour**: `renderMaterial.diffuse` is applied to each object's `ObjectColor`
  with `ColorFromObject` source so the viewport shows the model's material colours.
- **Duplicate handling (v1)**: objects are added alongside existing ones; a warning is
  logged. Delete-and-replace by `ORBIT_objectId` is deferred to a future version.
- **Undo**: all baked objects are wrapped in a single undo record ("ORBIT Receive").

### SDK additions (vendored in `vendor/SDK/src/`)

The following types were added to the vendored SDK to unblock local builds:

| Type | Namespace | Purpose |
|---|---|---|
| `Vector` | `Orbit.Objects.Geometry` | Camera direction vectors for `View3D` |
| `View3D` | `Orbit.Objects.BuiltElements` | Named camera views |
| `RenderMaterial` | `Orbit.Objects.Other` | Full PBR material (replaces the stub in `Proxies`) |
| `RawEncoding` | `Orbit.Objects.Other` | Base64 binary payload container |
| `RhinoDataObject` | `Orbit.Objects.Data` | Native Rhino object wrapper |
| `OrbitBlobUploader` | `Orbit.Sdk.Transport` | Uploads texture files; returns server blob IDs |
| `TextureBlobPatcher` | `Orbit.Sdk.Transport` | Patches `@blob:SHA256` → `@blob:serverBlobId` |

`OrbitObject` gains `CollectionType`, `LayerPath`, `LayerColor`, proxy lists, and `Views`.
`Mesh` and `Brep` gain `LayerPath`, `LayerColor`, `ColorSource` (and `Mesh` gains `RenderMaterial`).
`Instance` gains `Name` and `Elements`.
`OrbitClient` gains `AuthToken` and `CreateVersionAsync` gains `totalChildrenCount`.

---

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
