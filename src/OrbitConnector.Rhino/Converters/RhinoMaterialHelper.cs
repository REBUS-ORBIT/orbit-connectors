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
/// <b>v0.1.18 changes (vs v0.1.17).</b> Strategies no longer short-circuit;
/// every probe runs unconditionally on every material and the union of
/// (slot, on-disk-path) pairs is fed into <c>AttachSlot</c>. First success
/// per slot wins. Identical paths probed by multiple strategies are
/// deduplicated. The RDK walk now recurses into non-texture wrapper nodes
/// (e.g. "Color Adjustment", "Multiply", "Texture Mapping") to find
/// nested <see cref="RenderTexture"/> children — these wrappers were
/// invisible to the v0.1.17 flat <c>FirstChild / NextSibling</c> walk
/// and they hold the bitmap on materials authored through the Render
/// → Materials editor. Diagnostic logging per probe is mandatory: every
/// strategy logs whether it ran, what slots it inspected, and why
/// each inspected slot did or did not yield a usable path.
/// </para>
///
/// <para>Strategies (all run unconditionally):</para>
///
/// <list type="number">
///   <item><description>
///     <b>PhysicallyBased.GetTexture(PBR_*)</b> — Rhino's first-class
///     PBR API. Handles base colour, emission, roughness, metallic,
///     opacity, and bump slots on any material the user authored
///     through the PBR editor.
///   </description></item>
///   <item><description>
///     <b>Material.GetTextures()</b> — enumerates every native texture
///     slot Rhino has on the material (PBR + legacy Bitmap / Bump /
///     Transparency / Emap). Catches the corner case where
///     <c>IsPhysicallyBased</c> is false but the material still has a
///     bitmap attached via the legacy slots.
///   </description></item>
///   <item><description>
///     <b>RDK render-content tree walk (recursive)</b> — descends the
///     render-content graph rooted at <see cref="Material.RenderMaterial"/>,
///     collecting every <see cref="RenderTexture"/> at any depth and
///     resolving it through <see cref="RenderTexture.SimulatedTexture"/>.
///     Captures procedural textures and bitmaps wrapped inside
///     non-texture render-content nodes (Color Adjustment, Mix, etc.)
///     that v0.1.17's flat walk missed.
///   </description></item>
///   <item><description>
///     <b>SimulatedMaterial fallback</b> —
///     <see cref="RenderMaterial.ToMaterial"/> with
///     <c>TextureGeneration.Allow</c> renders the material out to a
///     plain <see cref="Material"/> with baked-in textures, so we still
///     ship a base-colour bitmap for fully procedural materials. We
///     enumerate both PBR slots and <see cref="Material.GetTextures"/>
///     on the simulated material; final last-ditch is the legacy
///     <see cref="TextureType.Bitmap"/> probe.
///   </description></item>
/// </list>
///
/// <para>
/// For every probed slot the helper resolves a real on-disk file via
/// <see cref="Texture.FileReference"/> (Rhino 8.0+) with a fall-back
/// to the legacy <see cref="Texture.FileName"/> property. The file
/// bytes are hashed (SHA-256) and the digest is used as the placeholder
/// id. Identical bitmaps shared across materials de-duplicate at the
/// hash level; the uploader only POSTs each unique file once.
/// </para>
///
/// <para>
/// Emissive policy is unchanged from v0.1.17 — see the inline comments
/// near the rescue / promotion blocks.
/// </para>
/// </summary>
internal static class RhinoMaterialHelper
{
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
        // Idempotent per (slot). De-dupes (slot, normalised-path) attempts
        // across strategies so we only hash a given file once even when
        // multiple probes return the same path. First success per slot
        // wins; later strategies log a "skipped (already attached)" note.
        void AttachSlot(string slot, string path, string source)
        {
            if (string.IsNullOrWhiteSpace(path)) return;

            // Per-(slot,path) dedupe across strategies. Without this we'd
            // hash the same JPEG four times in a row when PBR / GetTextures
            // / RDK / SimulatedMaterial all surface the same file.
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
                    // When a base-colour texture is attached, set Diffuse to
                    // opaque white so the viewer's `color × map` multiplication
                    // renders the texture's actual colours. Leaving Diffuse at
                    // the material's solid-colour value would tint the texture
                    // (e.g. an orange material with a wood texture would render
                    // wood-tinted-orange instead of wood).
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
                    // glTF-style packed metallic-roughness texture. Aliased onto
                    // both Rhino slots so the receive pipeline picks it up
                    // regardless of which one its TryApply probes first.
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

        var renderMat = mat.RenderMaterial;

        // ── Strategy 1: PhysicallyBased.GetTexture (matches Python pipeline) ──
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
            // PBR_Alpha is the Rhino 8 PBR-opacity slot. It was added partway
            // through the 8.x line; older builds expose alpha only via the
            // legacy Transparency slot. Resolve at runtime so the code
            // compiles against the lowest-common Rhino 8 SDK surface.
            if (Enum.TryParse<TextureType>("PBR_Alpha", out var pbrAlpha))
                pbrMap.Add((pbrAlpha, "opacity"));

            int seen = 0, withFile = 0;
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
                        probeNotes.Add($"pbr:{slot}=tex-no-path");
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
            probeNotes.Add($"pbr:probed={seen} with-file={withFile}");
        }
        else
        {
            probeNotes.Add("pbr:skip(not-physically-based)");
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

        // ── Strategy 3: RDK render-content tree walk (recursive) ─────────────
        // v0.1.17 used a flat FirstChild / NextSibling walk. That misses any
        // RenderTexture wrapped inside non-texture render-content nodes
        // (Color Adjustment, Mix, Multiply, Texture Mapping containers, …)
        // which is how the Render → Materials editor often nests bitmaps.
        // Recurse the full tree and collect every RenderTexture descendant.
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
                    string? path = null;
                    try
                    {
                        var simTex = rt.SimulatedTexture(
                            RenderTexture.TextureGeneration.Allow);
                        path = simTex?.Filename;
                    }
                    catch (Exception ex)
                    {
                        probeNotes.Add($"rdk:{slot}=sim-throw({ex.GetType().Name})");
                    }

                    if (string.IsNullOrEmpty(path))
                    {
                        // Fallback: ask the texture itself for an on-disk
                        // file. RenderTexture exposes a Filename property
                        // for bitmap-typed children even when SimulatedTexture
                        // returns null (rare but observed on some Rhino 8
                        // service-pack builds).
                        try
                        {
                            path = rt.GetType()
                                     .GetProperty("Filename")?
                                     .GetValue(rt) as string;
                        }
                        catch { /* property may not exist */ }
                    }

                    if (string.IsNullOrEmpty(path))
                    {
                        probeNotes.Add($"rdk:{slot}=no-path");
                        continue;
                    }
                    if (!File.Exists(path))
                    {
                        probeNotes.Add($"rdk:{slot}=path-missing");
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

        // ── Strategy 4: SimulatedMaterial fallback (always runs in v0.1.18) ──
        // v0.1.17 only ran this when no other strategy attached anything. We
        // now run it unconditionally so a material that had only roughness /
        // metallic textures attached by earlier strategies can still pick up
        // a basecolor via the simulated bake. AttachSlot's idempotency stops
        // it overwriting a higher-priority match.
        if (renderMat is not null)
        {
            try
            {
                var simMat = renderMat.ToMaterial(
                    RenderTexture.TextureGeneration.Allow);
                if (simMat is not null)
                {
                    int seen = 0, withFile = 0;

                    // 4a. PBR slots on the simulated material.
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

                    // 4b. GetTextures() enumeration on the simulated material.
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

                    // 4c. Last-resort legacy Bitmap probe.
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
        // Rhino's slot classifier sometimes drops the only bitmap into the
        // emission bucket even when the material isn't meant to glow (e.g. a
        // base-colour-bitmap PBR material the user authored through the PBR
        // editor). If the Rhino material's emission colour is black, the user
        // never intended emission — promote to basecolor instead.
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
        // The viewer multiplies the emission colour by the emission texture.
        // If the user attaches an emission map but Rhino reports emissive=0
        // (the common case for materials where the bitmap *is* the glow),
        // the multiplication yields zero and the texture never renders.
        // Promote to opaque white + intensity 1.0 so the texture is visible.
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
    /// Recursively walks the render-content graph rooted at
    /// <paramref name="parent"/>, appending every <see cref="RenderTexture"/>
    /// descendant to <paramref name="sink"/> together with the slot name to
    /// classify it under. The slot name preference order is the texture's
    /// own <see cref="RenderContent.ChildSlotName"/>, then any parent
    /// container's <c>ChildSlotName</c>, then the <see cref="RenderContent.Name"/>
    /// of the texture as a last-ditch hint. Depth is bounded to stop
    /// pathological cyclic graphs.
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
            // Prefer the child's own slot name; fall back to the parent's
            // slot when the child is a wrapper that doesn't carry one.
            string? childSlot = null;
            try { childSlot = child.ChildSlotName; }
            catch { /* some custom render-contents throw on access */ }

            var slotForChild = !string.IsNullOrWhiteSpace(childSlot)
                ? childSlot
                : parentSlot;

            if (child is RenderTexture rt)
            {
                // If still no slot hint, fall back to the texture's display
                // name. ClassifySlot falls back to "basecolor" for completely
                // empty hints which is the safest default for an unattributed
                // bitmap on a single-texture material.
                if (string.IsNullOrWhiteSpace(slotForChild))
                {
                    try { slotForChild = rt.Name; } catch { /* ignore */ }
                }
                sink.Add((slotForChild ?? string.Empty, rt));
            }

            // Recurse — this is the v0.1.18 enhancement over the v0.1.17 walk.
            // Wrapper render-contents (Mix / Adjustment / Mapping / etc.) are
            // not RenderTexture themselves, but their descendants might be.
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
        // PBR_Alpha doesn't compile against the lowest-common Rhino 8 SDK
        // surface; check by name for forward-compatibility.
        var name = type.ToString();
        if (name == "PBR_Alpha") return "opacity";

        // Note: in Rhino 8, TextureType.Bitmap and TextureType.PBR_BaseColor
        // share the same underlying enum value (and similarly Emap may share
        // PBR_Emission). The compiler rejects duplicate case labels — list
        // each distinct value only once. The "Bitmap" name path is reached
        // because Rhino reports legacy slots via the PBR_BaseColor enum on
        // modern builds, so the mapping is still correct.
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
    /// Classify a render-content child slot name (from
    /// <see cref="RenderTexture.ChildSlotName"/> or any parent container's
    /// equivalent) into the canonical ORBIT slot bucket used by <c>AttachSlot</c>.
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
