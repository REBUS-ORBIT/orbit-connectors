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
/// The texture-probe strategy mirrors the known-good 3DConvert /
/// RebusWorkstationAgent producer pipeline. We try, in order:
/// </para>
///
/// <list type="number">
///   <item><description>
///     <b>PhysicallyBased.GetTexture(PBR_*)</b> — Rhino's first-class
///     PBR API. Handles base colour, emission, roughness, metallic,
///     opacity, and bump slots on any material the user authored
///     through the PBR editor.
///   </description></item>
///   <item><description>
///     <b>Material.GetTextures()</b> — enumerates all native texture
///     slots Rhino has on the material (PBR + legacy Bitmap / Bump /
///     Transparency / Emap). Catches the corner case where
///     <c>IsPhysicallyBased</c> is false but the material still has a
///     bitmap attached via the legacy slots.
///   </description></item>
///   <item><description>
///     <b>RDK <c>FirstChild</c> / <c>NextSibling</c> traversal</b> —
///     walks the render-content tree on the material's
///     <see cref="Material.RenderMaterial"/> and resolves any child
///     <see cref="RenderTexture"/> through
///     <see cref="RenderTexture.SimulatedTexture"/>. Catches procedural
///     texture children that the typed slot APIs miss.
///   </description></item>
///   <item><description>
///     <b>SimulatedMaterial fallback</b> —
///     <see cref="RenderMaterial.ToMaterial"/> with
///     <c>TextureGeneration.Allow</c> renders the material out to a
///     plain <see cref="Material"/> with baked-in textures. Used only
///     when none of the above found anything, so we still ship a
///     base-colour bitmap for fully-procedural materials.
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
/// Emissive policy. After the probe, the helper:
/// </para>
///
/// <list type="bullet">
///   <item><description>
///     Promotes a misclassified-emission bitmap to base colour when
///     Rhino's slot classifier dropped the only bitmap into the
///     emission bucket but the material's emission colour is black
///     (i.e. the user never meant for the surface to glow).
///   </description></item>
///   <item><description>
///     Mirrors the producer-side emissive promotion documented in
///     <c>3DConvert/app/writer_speckle.py</c>: when an emissive
///     texture is attached and the wire emissive colour is
///     <c>0xFF000000</c> (opaque black) or <c>0</c>, promotes the
///     emissive to <c>0xFFFFFFFF</c> (opaque white) with
///     <c>emissiveIntensity = 1.0</c>. three.js (which both the
///     Speckle viewer and the receive pipeline use) multiplies the
///     emission colour by the emission texture; a black emission
///     colour nullifies the entire texture.
///   </description></item>
/// </list>
///
/// <para>
/// Diagnostic logging mirrors the receive side. Every successful slot
/// attachment writes a line in the form:
/// </para>
///
/// <code>
/// [ORBIT] send-texture: material='...' slot=basecolor path='...' bytes=... source=pbr hash=...
/// </code>
///
/// <para>
/// And every material gets a one-line summary at the end of the
/// probe, regardless of whether any textures were found:
/// </para>
///
/// <code>
/// [ORBIT] send-material: name='...' textures-attached=[basecolor(pbr,12345B),...] emissive-promoted=false
/// </code>
///
/// <para>
/// The summary line with <c>textures-attached=[]</c> and a reason
/// (e.g. <c>reason=no-bitmaps-found</c>) is the user-visible signal
/// that the Rhino material has no probable PBR bitmaps. Surfaces
/// where the user expected a texture but the send log reports
/// <c>[]</c> point at a Rhino material that has no on-disk bitmap
/// reference even though the viewport renderer may still show
/// something (procedural shaders, embedded bitmaps without a
/// <c>FileReference</c>, etc.).
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
        catch { return; }
        if (mat is null) return;

        var matName = string.IsNullOrEmpty(mat.Name) ? "(unnamed)" : mat.Name;
        var slotsAttached = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var attachLog = new List<string>();
        var emissivePromoted = false;

        // Local helper: attach a single slot. Idempotent — re-attaching the
        // same slot is a no-op so later strategies don't overwrite better
        // matches found by earlier ones.
        void AttachSlot(string slot, string path, string source)
        {
            if (slotsAttached.Contains(slot)) return;
            if (string.IsNullOrWhiteSpace(path)) return;

            byte[] bytes;
            try
            {
                if (!File.Exists(path)) return;
                bytes = File.ReadAllBytes(path);
            }
            catch (Exception ex)
            {
                RhinoApp.WriteLine(
                    $"[ORBIT] send-texture: material='{matName}' slot={slot} " +
                    $"path='{path}' source={source} failed-to-read: {ex.Message}");
                return;
            }
            if (bytes.Length == 0) return;

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

        // ── Strategy 1: PhysicallyBased.GetTexture (matches Python pipeline) ──
        // The user-facing PBR slot API. Works for every material authored
        // through Rhino 8's "Physically Based" editor.
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

            foreach (var (texType, slot) in pbrMap)
            {
                if (slotsAttached.Contains(slot)) continue;
                try
                {
                    var tex = pbr.GetTexture(texType);
                    var path = ResolveTexturePath(tex);
                    if (path != null) AttachSlot(slot, path, "pbr");
                }
                catch (Exception ex)
                {
                    RhinoApp.WriteLine(
                        $"[ORBIT] send-texture: material='{matName}' slot={slot} " +
                        $"strategy=pbr failed: {ex.Message}");
                }
            }
        }

        // ── Strategy 2: Material.GetTextures() — every native texture slot ──
        // Catches legacy non-PBR Bitmap slots and PBR materials that report
        // IsPhysicallyBased=false because of API edge cases.
        try
        {
            var nativeTextures = mat.GetTextures();
            if (nativeTextures != null)
            {
                foreach (var nativeTex in nativeTextures)
                {
                    if (nativeTex == null) continue;
                    var slot = MapTextureTypeToSlot(nativeTex.TextureType);
                    if (slot == null) continue;
                    if (slotsAttached.Contains(slot)) continue;

                    var path = ResolveTexturePath(nativeTex);
                    if (path != null) AttachSlot(slot, path, "native");
                }
            }
        }
        catch (Exception ex)
        {
            RhinoApp.WriteLine(
                $"[ORBIT] send-texture: material='{matName}' " +
                $"strategy=Material.GetTextures failed: {ex.Message}");
        }

        // ── Strategy 3: RDK FirstChild / NextSibling traversal ───────────────
        // Walks the render-content tree of the material's RenderMaterial,
        // resolving any child RenderTexture through SimulatedTexture.
        var renderMat = mat.RenderMaterial;
        if (renderMat is not null)
        {
            try
            {
                var child = renderMat.FirstChild;
                while (child is not null)
                {
                    if (child is RenderTexture rt)
                    {
                        var slot = ClassifySlot(rt.ChildSlotName);
                        if (!slotsAttached.Contains(slot))
                        {
                            string? path = null;
                            try
                            {
                                var simTex = rt.SimulatedTexture(
                                    RenderTexture.TextureGeneration.Allow);
                                path = simTex?.Filename;
                            }
                            catch { /* probe failed for this child */ }

                            if (!string.IsNullOrEmpty(path))
                                AttachSlot(slot, path, "rdk");
                        }
                    }
                    child = child.NextSibling;
                }
            }
            catch (Exception ex)
            {
                RhinoApp.WriteLine(
                    $"[ORBIT] send-texture: material='{matName}' " +
                    $"strategy=rdk failed: {ex.Message}");
            }
        }

        // ── Strategy 4: SimulatedMaterial fallback (procedural-only materials) ─
        if (slotsAttached.Count == 0 && renderMat is not null)
        {
            try
            {
                var simMat = renderMat.ToMaterial(
                    RenderTexture.TextureGeneration.Allow);
                if (simMat is not null)
                {
                    // Try PBR slots first on the simulated material.
                    if (simMat.IsPhysicallyBased && simMat.PhysicallyBased is not null)
                    {
                        var simPbr = simMat.PhysicallyBased;
                        (TextureType type, string slot)[] simMap =
                        [
                            (TextureType.PBR_BaseColor, "basecolor"),
                            (TextureType.PBR_Emission,  "emission"),
                            (TextureType.PBR_Roughness, "roughness"),
                            (TextureType.PBR_Metallic,  "metallic"),
                        ];
                        foreach (var (texType, slot) in simMap)
                        {
                            if (slotsAttached.Contains(slot)) continue;
                            try
                            {
                                var tex = simPbr.GetTexture(texType);
                                var path = ResolveTexturePath(tex);
                                if (path != null) AttachSlot(slot, path, "sim.pbr");
                            }
                            catch { /* skip slot */ }
                        }
                    }

                    // Fall through to the legacy Bitmap slot on the
                    // simulated material when nothing PBR-shaped surfaced.
                    if (!slotsAttached.Contains("basecolor"))
                    {
                        try
                        {
                            var tex = simMat.GetTexture(TextureType.Bitmap);
                            var path = ResolveTexturePath(tex);
                            if (path != null) AttachSlot("basecolor", path, "sim.bitmap");
                        }
                        catch { /* skip */ }
                    }
                }
            }
            catch (Exception ex)
            {
                RhinoApp.WriteLine(
                    $"[ORBIT] send-texture: material='{matName}' " +
                    $"strategy=simulated failed: {ex.Message}");
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
                $"reason=no-bitmaps-found");
        }
        else
        {
            RhinoApp.WriteLine(
                $"[ORBIT] send-material: name='{matName}' " +
                $"textures-attached=[{string.Join(",", attachLog)}] " +
                $"emissive-promoted={emissivePromoted.ToString().ToLowerInvariant()}");
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
    /// <see cref="RenderTexture.ChildSlotName"/>) into the canonical ORBIT
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
