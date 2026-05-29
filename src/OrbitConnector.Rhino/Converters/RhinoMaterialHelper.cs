using System.Reflection;
using System.Security.Cryptography;
using Rhino;
using Rhino.DocObjects;
using Rhino.Render;
using OrbitRenderMaterial = Orbit.Objects.Other.RenderMaterial;

namespace OrbitConnector.Rhino.Converters;

/// <summary>
/// Probes a Rhino render <see cref="Material"/> for PBR bitmap textures,
/// hashes each file, attaches <c>@blob:SHA256HEX</c> placeholders to the
/// matching <see cref="OrbitRenderMaterial"/> fields, and queues the file
/// paths so <see cref="Orbit.Sdk.Transport.OrbitBlobUploader"/> can upload
/// the bytes to the ORBIT server. After upload,
/// <see cref="Orbit.Sdk.Transport.TextureBlobPatcher"/> rewrites every
/// placeholder to the server-assigned short blob id.
///
/// <para>
/// <b>v0.1.19 changes (vs v0.1.18).</b> The v0.1.18 probe only accepted a
/// texture that resolved to an <i>on-disk file</i> via
/// <see cref="Texture.FileReference"/>.<c>FullPath</c> or the legacy
/// <see cref="Texture.FileName"/>. That silently dropped two whole classes
/// of texture that the known-good 3DConvert / RebusWorkstationAgent
/// IronPython pipeline (<c>3DConvert/app/converters/rhino_conv.py</c>) does
/// capture:
/// </para>
///
/// <list type="number">
///   <item><description>
///     <b>Render-content bitmaps whose path lives in the RDK parameter
///     bag.</b> Rhino's render-content textures frequently expose their
///     file via <c>RenderContent.GetParameter("filename")</c> rather than a
///     typed <c>Texture.FileName</c>. v0.1.19 reads that parameter.
///   </description></item>
///   <item><description>
///     <b>Embedded / procedural textures with no external file.</b> Stock
///     Rhino PBR materials (e.g. the "Metal" preset) and any texture
///     embedded in the <c>.3dm</c> have no on-disk path at all —
///     <c>FileReference.FullPath</c> and <c>FileName</c> are both blank.
///     The only way to get uploadable bytes is to ask Rhino to bake the
///     texture to a temp bitmap with
///     <see cref="RenderTexture.SimulatedTexture"/>(<see cref="RenderTexture.TextureGeneration.Allow"/>);
///     the returned <c>SimulatedTexture.Filename</c> points at a generated
///     temp file. v0.1.19 does this for every render-texture node it finds.
///   </description></item>
/// </list>
///
/// <para>
/// v0.1.19 also adds an explicit <see cref="RenderContent.FindChild"/>
/// pass over the documented PBR child-slot names
/// (<c>pbr-base-color</c>, <c>pbr-metallic</c>, …) because a PBR material's
/// base-color bitmap is reachable through that slot even when
/// <c>PhysicallyBased.GetTexture(PBR_BaseColor)</c> returns a typed
/// <see cref="Texture"/> with an unresolvable (embedded) path and the flat
/// <c>FirstChild</c> walk does not surface it.
/// </para>
///
/// <para>Strategies (all run unconditionally; first success per slot wins):</para>
///
/// <list type="number">
///   <item><description><b>1.</b> <c>PhysicallyBased.GetTexture(PBR_*)</c> → on-disk path.</description></item>
///   <item><description><b>1b.</b> <c>RenderMaterial.FindChild("pbr-*")</c> → render-texture file (parameter-bag path, then bake).</description></item>
///   <item><description><b>2.</b> <c>Material.GetTextures()</c> → on-disk path.</description></item>
///   <item><description><b>3.</b> Recursive RDK render-content walk → render-texture file (parameter-bag path, then bake).</description></item>
///   <item><description><b>4.</b> <c>RenderMaterial.ToMaterial(Allow)</c> simulated-material probe → on-disk path.</description></item>
/// </list>
///
/// <para>
/// Every render-texture file resolution goes through
/// <see cref="ResolveRenderTextureFile"/>, which tries (in order) the
/// reflective <c>Filename</c> property, <c>GetParameter("filename")</c>,
/// and finally the <see cref="RenderTexture.SimulatedTexture"/> bake. The
/// <c>probes=[…]</c> field on the per-material summary line records, per
/// strategy and slot, whether a node was found, whether an on-disk file
/// existed, and whether a temp bitmap had to be baked — so the next send
/// log is fully self-diagnosing.
/// </para>
/// </summary>
internal static class RhinoMaterialHelper
{
    /// <summary>
    /// Documented Rhino 8 PBR child-slot names mapped to the canonical ORBIT
    /// slot bucket. These match
    /// <c>Rhino.Render.RenderMaterial.PhysicallyBased.ChildSlotNames</c> and
    /// the <c>_RDK_PBR_SLOTS</c> table in the 3DConvert reference pipeline.
    /// </summary>
    private static readonly (string slotName, string slot)[] PbrChildSlots =
    {
        ("pbr-base-color",        "basecolor"),
        ("pbr-metallic",          "metallic"),
        ("pbr-roughness",         "roughness"),
        ("pbr-emission",          "emission"),
        ("pbr-bump",              "bump"),
        ("pbr-alpha",             "opacity"),
        ("pbr-opacity",           "opacity"),
        ("pbr-ambient-occlusion", "ao"),
    };

    /// <summary>
    /// Probes <paramref name="rhinoObj"/>'s effective render material for
    /// bitmap textures, attaches <c>@blob:SHA256</c> placeholders to
    /// <paramref name="rm"/>, and queues the on-disk file paths in
    /// <paramref name="pendingBlobFiles"/> for later upload by
    /// <see cref="Orbit.Sdk.Transport.OrbitBlobUploader"/>.
    /// </summary>
    public static void AttachTextures(
        RhinoObject rhinoObj,
        OrbitRenderMaterial rm,
        RhinoDoc doc,
        IDictionary<string, string> pendingBlobFiles)
    {
        Material? mat;
        try { mat = rhinoObj.GetMaterial(true); }
        catch (Exception ex)
        {
            RhinoApp.WriteLine(
                $"[ORBIT] send-material: GetMaterial threw {ex.GetType().Name}: {ex.Message}");
            return;
        }
        if (mat is null) return;

        var matName = string.IsNullOrEmpty(mat.Name) ? "(unnamed)" : mat.Name;
        var slotsAttached = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var attachLog = new List<string>();
        var probeNotes = new List<string>(); // per-strategy diagnostic crumbs
        var attemptedKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var emissivePromoted = false;

        // ─── AttachSlot ───────────────────────────────────────────────────
        void AttachSlot(string slot, string path, string source)
        {
            if (string.IsNullOrWhiteSpace(path)) return;

            var dedupeKey = slot + "\0" + Path.GetFullPath(path).ToLowerInvariant();
            if (!attemptedKeys.Add(dedupeKey)) return;

            if (slotsAttached.Contains(slot))
            {
                probeNotes.Add($"{source}:{slot}=skip(already-attached)");
                return;
            }

            byte[] bytes;
            try
            {
                if (!File.Exists(path))
                {
                    probeNotes.Add($"{source}:{slot}=skip(file-not-found)");
                    return;
                }
                bytes = File.ReadAllBytes(path);
            }
            catch (Exception ex)
            {
                RhinoApp.WriteLine(
                    $"[ORBIT] send-texture: material='{matName}' slot={slot} " +
                    $"path='{path}' source={source} failed-to-read: {ex.Message}");
                probeNotes.Add($"{source}:{slot}=skip(read-error)");
                return;
            }
            if (bytes.Length == 0)
            {
                probeNotes.Add($"{source}:{slot}=skip(empty-file)");
                return;
            }

            var hashHex = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
            pendingBlobFiles.TryAdd(hashHex, path);
            var blobRef = $"@blob:{hashHex}";

            switch (slot)
            {
                case "basecolor":
                    rm.BaseColorTexture = blobRef;
                    rm.DiffuseTexture   = blobRef;
                    rm.Diffuse          = unchecked((long)0xFF_FF_FF_FFu);
                    break;
                case "emission":
                    rm.EmissiveTexture       = blobRef;
                    rm.PbrEmissionTexture    = blobRef;
                    rm.EmissiveTextureOffset = new List<double> { 0.0, 0.0 };
                    rm.EmissiveTextureRepeat = new List<double> { 1.0, 1.0 };
                    break;
                case "roughness":
                    rm.RoughnessTexture = blobRef;
                    break;
                case "metallic":
                    rm.MetalnessTexture = blobRef;
                    break;
                case "metallicroughness":
                    rm.RoughnessTexture = blobRef;
                    rm.MetalnessTexture = blobRef;
                    rm["metallicRoughnessTexture"] = blobRef;
                    break;
                case "bump":
                    rm.NormalTexture = blobRef;
                    break;
                case "opacity":
                    rm.OpacityTexture = blobRef;
                    break;
                default:
                    rm[$"{slot}Texture"] = blobRef;
                    break;
            }

            slotsAttached.Add(slot);
            attachLog.Add($"{slot}({source},{bytes.Length}B)");
            RhinoApp.WriteLine(
                $"[ORBIT] send-texture: material='{matName}' slot={slot} " +
                $"path='{path}' bytes={bytes.Length} source={source} " +
                $"hash={hashHex.Substring(0, 16)}…");
        }

        // Probe a render-content child (typically from FindChild or a tree
        // walk) for a known slot. Bakes embedded/procedural maps to temp.
        void ProbeContentForSlot(RenderContent? content, string slot, string source)
        {
            if (content == null) return;
            if (slotsAttached.Contains(slot)) return;

            if (content is RenderTexture rt)
            {
                var path = ResolveRenderTextureFile(rt, probeNotes, $"{source}:{slot}");
                if (path != null) AttachSlot(slot, path, source);
                else probeNotes.Add($"{source}:{slot}=node-no-bitmap");
                return;
            }

            // Wrapper content (Mix / Adjustment / Mapping …) — recurse for any
            // texture descendant and attribute it to this slot.
            var sink = new List<(string slotName, RenderTexture rt)>();
            CollectRenderTextures(content, slot, 0, sink);
            foreach (var (_, crt) in sink)
            {
                if (slotsAttached.Contains(slot)) break;
                var path = ResolveRenderTextureFile(crt, probeNotes, $"{source}:{slot}");
                if (path != null) { AttachSlot(slot, path, source); break; }
            }
        }

        // Resolve the RDK render-content for this material. Prefer the
        // material's own RenderMaterial; fall back to a doc.RenderMaterials
        // lookup by instance id (matches the 3DConvert reference).
        RenderMaterial? renderMat = null;
        try { renderMat = mat.RenderMaterial; } catch { /* may throw */ }
        if (renderMat is null)
        {
            try
            {
                var rmid = mat.RenderMaterialInstanceId;
                if (rmid != Guid.Empty)
                {
                    foreach (var cand in doc.RenderMaterials)
                    {
                        try { if (cand.Id == rmid) { renderMat = cand; break; } }
                        catch { /* skip */ }
                    }
                }
            }
            catch { /* RenderMaterials table unavailable */ }
        }
        probeNotes.Add(renderMat is null ? "renderMat=null" : "renderMat=ok");

        // ── Strategy 1: PhysicallyBased.GetTexture (on-disk path) ────────────
        if (mat.IsPhysicallyBased && mat.PhysicallyBased is not null)
        {
            var pbr = mat.PhysicallyBased;
            var pbrMap = new List<(TextureType type, string slot)>
            {
                (TextureType.PBR_BaseColor, "basecolor"),
                (TextureType.PBR_Emission,  "emission"),
                (TextureType.PBR_Roughness, "roughness"),
                (TextureType.PBR_Metallic,  "metallic"),
                (TextureType.Bump,          "bump"),
            };
            if (Enum.TryParse<TextureType>("PBR_Alpha", out var pbrAlpha))
                pbrMap.Add((pbrAlpha, "opacity"));

            int seen = 0, withFile = 0, embedded = 0;
            foreach (var (texType, slot) in pbrMap)
            {
                try
                {
                    var tex = pbr.GetTexture(texType);
                    if (tex == null) continue;
                    seen++;
                    var path = ResolveTexturePath(tex);
                    if (path == null)
                    {
                        // Texture node exists but has no resolvable on-disk
                        // file (embedded / authoring path missing). Strategy
                        // 1b will recover it via the RDK child slot + bake.
                        embedded++;
                        probeNotes.Add($"pbr:{slot}=tex-no-path(embedded?)");
                        continue;
                    }
                    withFile++;
                    AttachSlot(slot, path, "pbr");
                }
                catch (Exception ex)
                {
                    RhinoApp.WriteLine(
                        $"[ORBIT] send-texture: material='{matName}' slot={slot} " +
                        $"strategy=pbr failed: {ex.Message}");
                    probeNotes.Add($"pbr:{slot}=throw");
                }
            }
            probeNotes.Add($"pbr:probed={seen} with-file={withFile} embedded={embedded}");
        }
        else
        {
            probeNotes.Add("pbr:skip(not-physically-based)");
        }

        // ── Strategy 1b: FindChild over documented PBR child slots ───────────
        // Recovers the embedded / parameter-bag bitmap that Strategy 1 saw as
        // a path-less Texture, plus any slot Strategy 1 didn't cover.
        if (renderMat is not null)
        {
            int found = 0;
            foreach (var (slotName, slot) in PbrChildSlots)
            {
                if (slotsAttached.Contains(slot)) continue;
                RenderContent? child = null;
                try { child = renderMat.FindChild(slotName); }
                catch (Exception ex) { probeNotes.Add($"findchild:{slot}=throw({ex.GetType().Name})"); }
                if (child == null) continue;
                found++;
                ProbeContentForSlot(child, slot, "findchild");
            }
            probeNotes.Add($"findchild:children={found}");
        }

        // ── Strategy 2: Material.GetTextures() — every native texture slot ──
        try
        {
            var nativeTextures = mat.GetTextures();
            if (nativeTextures != null)
            {
                int seen = 0, mapped = 0, withFile = 0;
                foreach (var nativeTex in nativeTextures)
                {
                    if (nativeTex == null) continue;
                    seen++;
                    var slot = MapTextureTypeToSlot(nativeTex.TextureType);
                    if (slot == null)
                    {
                        probeNotes.Add($"native:type={nativeTex.TextureType}=unmapped");
                        continue;
                    }
                    mapped++;
                    var path = ResolveTexturePath(nativeTex);
                    if (path == null)
                    {
                        probeNotes.Add($"native:{slot}=tex-no-path");
                        continue;
                    }
                    withFile++;
                    AttachSlot(slot, path, "native");
                }
                probeNotes.Add($"native:total={seen} mapped={mapped} with-file={withFile}");
            }
            else
            {
                probeNotes.Add("native:GetTextures-returned-null");
            }
        }
        catch (Exception ex)
        {
            RhinoApp.WriteLine(
                $"[ORBIT] send-texture: material='{matName}' " +
                $"strategy=Material.GetTextures failed: {ex.Message}");
            probeNotes.Add("native:throw");
        }

        // ── Strategy 3: recursive RDK render-content tree walk ───────────────
        if (renderMat is not null)
        {
            try
            {
                var found = new List<(string slotName, RenderTexture rt)>();
                CollectRenderTextures(renderMat, parentSlot: null, depth: 0, sink: found);
                int withFile = 0;
                foreach (var (slotName, rt) in found)
                {
                    var slot = ClassifySlot(slotName);
                    if (slotsAttached.Contains(slot)) continue;
                    var path = ResolveRenderTextureFile(rt, probeNotes, $"rdk:{slot}");
                    if (path == null)
                    {
                        probeNotes.Add($"rdk:{slot}=node-no-bitmap");
                        continue;
                    }
                    withFile++;
                    AttachSlot(slot, path, "rdk");
                }
                probeNotes.Add($"rdk:textures={found.Count} with-file={withFile}");
            }
            catch (Exception ex)
            {
                RhinoApp.WriteLine(
                    $"[ORBIT] send-texture: material='{matName}' " +
                    $"strategy=rdk failed: {ex.Message}");
                probeNotes.Add("rdk:throw");
            }
        }
        else
        {
            probeNotes.Add("rdk:skip(no-RenderMaterial)");
        }

        // ── Strategy 4: SimulatedMaterial bake → probe its textures ──────────
        if (renderMat is not null)
        {
            try
            {
                var simMat = renderMat.ToMaterial(RenderTexture.TextureGeneration.Allow);
                if (simMat is not null)
                {
                    int seen = 0, withFile = 0;

                    if (simMat.IsPhysicallyBased && simMat.PhysicallyBased is not null)
                    {
                        var simPbr = simMat.PhysicallyBased;
                        (TextureType type, string slot)[] simMap =
                        [
                            (TextureType.PBR_BaseColor, "basecolor"),
                            (TextureType.PBR_Emission,  "emission"),
                            (TextureType.PBR_Roughness, "roughness"),
                            (TextureType.PBR_Metallic,  "metallic"),
                            (TextureType.Bump,          "bump"),
                        ];
                        foreach (var (texType, slot) in simMap)
                        {
                            try
                            {
                                var tex = simPbr.GetTexture(texType);
                                if (tex == null) continue;
                                seen++;
                                var path = ResolveTexturePath(tex);
                                if (path == null) continue;
                                withFile++;
                                AttachSlot(slot, path, "sim.pbr");
                            }
                            catch { /* skip slot */ }
                        }
                    }

                    try
                    {
                        var simNatives = simMat.GetTextures();
                        if (simNatives != null)
                        {
                            foreach (var t in simNatives)
                            {
                                if (t == null) continue;
                                seen++;
                                var slot = MapTextureTypeToSlot(t.TextureType);
                                if (slot == null) continue;
                                var path = ResolveTexturePath(t);
                                if (path == null) continue;
                                withFile++;
                                AttachSlot(slot, path, "sim.native");
                            }
                        }
                    }
                    catch { /* skip native enumeration */ }

                    try
                    {
                        var tex = simMat.GetTexture(TextureType.Bitmap);
                        if (tex != null)
                        {
                            seen++;
                            var path = ResolveTexturePath(tex);
                            if (path != null)
                            {
                                withFile++;
                                AttachSlot("basecolor", path, "sim.bitmap");
                            }
                        }
                    }
                    catch { /* skip */ }

                    probeNotes.Add($"sim:probed={seen} with-file={withFile}");
                }
                else
                {
                    probeNotes.Add("sim:ToMaterial-returned-null");
                }
            }
            catch (Exception ex)
            {
                RhinoApp.WriteLine(
                    $"[ORBIT] send-texture: material='{matName}' " +
                    $"strategy=simulated failed: {ex.Message}");
                probeNotes.Add("sim:throw");
            }
        }

        // ── Texture-slot rescue: emission → basecolor (no real glow) ─────────
        var emissionIsRealGlow = mat.EmissionColor.R != 0
                              || mat.EmissionColor.G != 0
                              || mat.EmissionColor.B != 0;
        if (slotsAttached.Contains("emission")
            && !slotsAttached.Contains("basecolor")
            && !emissionIsRealGlow
            && !string.IsNullOrEmpty(rm.EmissiveTexture))
        {
            var blobRef = rm.EmissiveTexture!;
            rm.BaseColorTexture      = blobRef;
            rm.DiffuseTexture        = blobRef;
            rm.Diffuse               = unchecked((long)0xFF_FF_FF_FFu);
            rm.EmissiveTexture       = null;
            rm.PbrEmissionTexture    = null;
            rm.EmissiveTextureOffset = null;
            rm.EmissiveTextureRepeat = null;
            slotsAttached.Remove("emission");
            slotsAttached.Add("basecolor");
            attachLog.Add("rescue:emission→basecolor");
        }

        // ── Emissive promotion (mirrors writer_speckle.py / viewer v2.4.3) ───
        if (slotsAttached.Contains("emission"))
        {
            const long opaqueBlack = unchecked((long)0xFF_00_00_00u);
            if (rm.Emissive == opaqueBlack || rm.Emissive == 0L)
            {
                rm.Emissive          = unchecked((long)0xFF_FF_FF_FFu);
                rm.EmissiveIntensity = 1.0;
                emissivePromoted     = true;
            }
            else if (emissionIsRealGlow && rm.EmissiveIntensity is null)
            {
                rm.EmissiveIntensity = 1.0;
            }
        }

        // ── PBR scalar pass-through (Roughness / Metalness) ──────────────────
        if (mat.IsPhysicallyBased && mat.PhysicallyBased is not null)
        {
            var pbr = mat.PhysicallyBased;
            rm.Roughness = pbr.Roughness;
            rm.Metalness = pbr.Metallic;
        }

        // ── Summary line — always emitted, even when no textures found ──────
        if (slotsAttached.Count == 0)
        {
            RhinoApp.WriteLine(
                $"[ORBIT] send-material: name='{matName}' textures-attached=[] " +
                $"reason=no-bitmaps-found probes=[{string.Join(";", probeNotes)}]");
        }
        else
        {
            RhinoApp.WriteLine(
                $"[ORBIT] send-material: name='{matName}' " +
                $"textures-attached=[{string.Join(",", attachLog)}] " +
                $"emissive-promoted={emissivePromoted.ToString().ToLowerInvariant()} " +
                $"probes=[{string.Join(";", probeNotes)}]");
        }
    }

    /// <summary>
    /// Resolve an on-disk file for a render-texture node, baking embedded /
    /// procedural maps to a temp bitmap when no external file exists. Tries,
    /// in order:
    /// <list type="number">
    ///   <item><description>the reflective <c>Filename</c> property
    ///     (bitmap textures);</description></item>
    ///   <item><description><c>GetParameter("filename")</c> — where many
    ///     render-content bitmaps store their path;</description></item>
    ///   <item><description><see cref="RenderTexture.SimulatedTexture"/> with
    ///     <see cref="RenderTexture.TextureGeneration.Allow"/>, which writes
    ///     procedural / embedded textures out to a temp file and returns its
    ///     path via <c>SimulatedTexture.Filename</c>.</description></item>
    /// </list>
    /// Returns <c>null</c> if no uploadable file could be produced.
    /// </summary>
    private static string? ResolveRenderTextureFile(
        RenderTexture rt, List<string> probeNotes, string tag)
    {
        if (rt == null) return null;

        // 1. Reflective Filename property (RenderTexture / BitmapTexture).
        try
        {
            var fn = rt.GetType().GetProperty("Filename")?.GetValue(rt) as string;
            if (!string.IsNullOrWhiteSpace(fn) && File.Exists(fn)) return fn;
        }
        catch { /* property may not exist or throw */ }

        // 2. RDK parameter bag — render-content bitmaps store the path here.
        try
        {
            var p = rt.GetParameter("filename");
            var ps = p?.ToString();
            if (!string.IsNullOrWhiteSpace(ps) && File.Exists(ps)) return ps;
        }
        catch { /* GetParameter unsupported on this content type */ }

        // 3. Bake to a temp bitmap (procedural / embedded textures). This is
        //    the ONLY path that yields bytes when there is no external file.
        try
        {
            var sim = rt.SimulatedTexture(RenderTexture.TextureGeneration.Allow);
            var sf = sim?.Filename;
            if (!string.IsNullOrWhiteSpace(sf) && File.Exists(sf))
            {
                probeNotes.Add($"{tag}=baked-temp");
                return sf;
            }
        }
        catch (Exception ex)
        {
            probeNotes.Add($"{tag}=bake-throw({ex.GetType().Name})");
        }

        return null;
    }

    /// <summary>
    /// Recursively walks the render-content graph rooted at
    /// <paramref name="parent"/>, appending every <see cref="RenderTexture"/>
    /// descendant to <paramref name="sink"/> together with the slot name to
    /// classify it under.
    /// </summary>
    private static void CollectRenderTextures(
        RenderContent parent,
        string? parentSlot,
        int depth,
        List<(string slotName, RenderTexture rt)> sink)
    {
        if (parent is null) return;
        if (depth > 8) return; // sanity bound

        var child = parent.FirstChild;
        while (child is not null)
        {
            string? childSlot = null;
            try { childSlot = child.ChildSlotName; }
            catch { /* some custom render-contents throw on access */ }

            var slotForChild = !string.IsNullOrWhiteSpace(childSlot)
                ? childSlot
                : parentSlot;

            if (child is RenderTexture rt)
            {
                if (string.IsNullOrWhiteSpace(slotForChild))
                {
                    try { slotForChild = rt.Name; } catch { /* ignore */ }
                }
                sink.Add((slotForChild ?? string.Empty, rt));
            }

            CollectRenderTextures(child, slotForChild, depth + 1, sink);

            child = child.NextSibling;
        }
    }

    /// <summary>
    /// Resolve a real on-disk file path for a Rhino <see cref="Texture"/>.
    /// Tries <see cref="Texture.FileReference"/>.<c>FullPath</c> (Rhino 8.0+)
    /// first, then the legacy <see cref="Texture.FileName"/> property.
    /// Returns <c>null</c> if neither resolves to an existing file.
    /// </summary>
    private static string? ResolveTexturePath(Texture? tex)
    {
        if (tex == null) return null;

        try
        {
            var fr = tex.FileReference;
            if (fr != null)
            {
                var p = fr.FullPath;
                if (!string.IsNullOrWhiteSpace(p) && File.Exists(p)) return p;
            }
        }
        catch { /* FileReference may throw on procedural textures */ }

        try
        {
            var p = tex.FileName;
            if (!string.IsNullOrWhiteSpace(p) && File.Exists(p)) return p;
        }
        catch { /* FileName may throw too */ }

        return null;
    }

    /// <summary>
    /// Maps a Rhino <see cref="TextureType"/> to the canonical ORBIT slot
    /// name used by <c>AttachSlot</c>. Returns <c>null</c> for slots that
    /// don't have a meaningful ORBIT counterpart (e.g. displacement).
    /// </summary>
    private static string? MapTextureTypeToSlot(TextureType type)
    {
        var name = type.ToString();
        if (name == "PBR_Alpha") return "opacity";

        return type switch
        {
            TextureType.PBR_BaseColor => "basecolor",
            TextureType.PBR_Emission  => "emission",
            TextureType.PBR_Roughness => "roughness",
            TextureType.PBR_Metallic  => "metallic",
            TextureType.Bump          => "bump",
            TextureType.Transparency  => "opacity",
            _                         => null,
        };
    }

    /// <summary>
    /// Classify a render-content child slot name into the canonical ORBIT
    /// slot bucket used by <c>AttachSlot</c>.
    /// </summary>
    private static string ClassifySlot(string? slotName)
    {
        var s = (slotName ?? string.Empty).ToLowerInvariant();
        if (s.Contains("base") || s == "diffuse" || s == "color" || s == "bitmap")
            return "basecolor";
        if (s.Contains("metallic-roughness") || s.Contains("metallicroughness"))
            return "metallicroughness";
        if (s.Contains("roughness")) return "roughness";
        if (s.Contains("metallic") || s.Contains("metalness")) return "metallic";
        if (s.Contains("emission") || s.Contains("emissive")) return "emission";
        if (s.Contains("bump") || s.Contains("normal")) return "bump";
        if (s.Contains("alpha") || s.Contains("opacity") || s.Contains("transparency"))
            return "opacity";
        return string.IsNullOrEmpty(s) ? "basecolor" : $"other_{s}";
    }
}
