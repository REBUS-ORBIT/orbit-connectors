using System.Security.Cryptography;
using Rhino;
using Rhino.DocObjects;
using Rhino.Render;
using OrbitRenderMaterial = Orbit.Objects.Other.RenderMaterial;

namespace OrbitConnector.Rhino.Converters;

/// <summary>
/// Extracts PBR texture maps from Rhino render materials and attaches
/// <c>@blob:SHA256</c> placeholders to <see cref="RenderMaterial"/>.
/// Mirrors RebusWorkstationAgent / 3DConvert writer_speckle.py logic.
/// </summary>
internal static class RhinoMaterialHelper
{
    public static void AttachTextures(
        RhinoObject rhinoObj,
        OrbitRenderMaterial rm,
        RhinoDoc doc,
        IDictionary<string, string> pendingBlobFiles)
    {
        try
        {
            var mat = rhinoObj.GetMaterial(true);
            if (mat is null) return;

            var renderMat = mat.RenderMaterial;
            var slotsAttached = new HashSet<string>();

            void AttachSlot(string slot, string path)
            {
                if (!File.Exists(path)) return;
                var hashHex = Convert.ToHexString(
                    SHA256.HashData(File.ReadAllBytes(path))).ToLowerInvariant();
                pendingBlobFiles.TryAdd(hashHex, path);
                var blobRef = $"@blob:{hashHex}";

                switch (slot)
                {
                    case "basecolor":
                        rm.BaseColorTexture = blobRef;
                        rm.DiffuseTexture   = blobRef;
                        rm.Diffuse          = 4278190080L; // black base; texture carries colour
                        slotsAttached.Add("basecolor");
                        break;
                    case "emission":
                        rm.EmissiveTexture       = blobRef;
                        rm.PbrEmissionTexture    = blobRef;
                        rm.EmissiveTextureOffset = new List<double> { 0.0, 0.0 };
                        rm.EmissiveTextureRepeat = new List<double> { 1.0, 1.0 };
                        slotsAttached.Add("emission");
                        break;
                    case "roughness":
                        rm.RoughnessTexture = blobRef;
                        slotsAttached.Add("roughness");
                        break;
                    case "metallic":
                        rm.MetalnessTexture = blobRef;
                        slotsAttached.Add("metallic");
                        break;
                    case "bump":
                        rm.NormalTexture = blobRef;
                        slotsAttached.Add("bump");
                        break;
                    case "opacity":
                        rm.OpacityTexture = blobRef;
                        slotsAttached.Add("opacity");
                        break;
                    default:
                        rm[$"{slot}Texture"] = blobRef;
                        slotsAttached.Add(slot);
                        break;
                }
            }

            static string ClassifySlot(string slotName)
            {
                var s = slotName.ToLowerInvariant();
                if (s.Contains("base") || s == "diffuse" || s == "color" || s == "bitmap")
                    return "basecolor";
                if (s.Contains("roughness")) return "roughness";
                if (s.Contains("metallic") || s.Contains("metalness")) return "metallic";
                if (s.Contains("emission") || s.Contains("emissive")) return "emission";
                if (s.Contains("bump") || s.Contains("normal")) return "bump";
                if (s.Contains("alpha") || s.Contains("opacity")) return "opacity";
                return $"other_{s}";
            }

            // Strategy 1: RDK FirstChild/NextSibling traversal
            if (renderMat is not null)
            {
                try
                {
                    var child = renderMat.FirstChild;
                    while (child is not null)
                    {
                        if (child is RenderTexture rt)
                        {
                            var slotName = rt.ChildSlotName ?? string.Empty;
                            var slot = ClassifySlot(slotName);
                            if (!slotsAttached.Contains(slot) || slot.StartsWith("other_"))
                            {
                                try
                                {
                                    var simTex = rt.SimulatedTexture(
                                        RenderTexture.TextureGeneration.Allow);
                                    var p = simTex?.Filename;
                                    if (!string.IsNullOrWhiteSpace(p) && File.Exists(p))
                                        AttachSlot(slot, p);
                                }
                                catch { /* skip slot */ }
                            }
                        }
                        child = child.NextSibling;
                    }
                }
                catch { /* RDK traversal failed */ }
            }

            // Strategy 2: PhysicallyBased channel API
            if (mat.IsPhysicallyBased && mat.PhysicallyBased is not null)
            {
                var pbr = mat.PhysicallyBased;
                (TextureType type, string slot)[] pbrMap =
                [
                    (TextureType.PBR_BaseColor, "basecolor"),
                    (TextureType.PBR_Emission,  "emission"),
                    (TextureType.PBR_Roughness, "roughness"),
                    (TextureType.PBR_Metallic,  "metallic"),
                ];
                foreach (var (texType, slot) in pbrMap)
                {
                    if (slotsAttached.Contains(slot)) continue;
                    try
                    {
                        var tex = pbr.GetTexture(texType);
                        var p = tex?.FileReference?.FullPath;
                        if (!string.IsNullOrWhiteSpace(p) && File.Exists(p))
                            AttachSlot(slot, p);
                    }
                    catch { /* skip channel */ }
                }
            }

            // Strategy 3: legacy Bitmap
            if (!slotsAttached.Contains("basecolor"))
            {
                try
                {
                    var tex = mat.GetTexture(TextureType.Bitmap);
                    var p = tex?.FileReference?.FullPath;
                    if (!string.IsNullOrWhiteSpace(p) && File.Exists(p))
                        AttachSlot("basecolor", p);
                }
                catch { /* skip */ }
            }

            // Strategy 4: SimulatedMaterial fallback
            if (!slotsAttached.Contains("basecolor") && renderMat is not null)
            {
                try
                {
                    var simMat = renderMat.ToMaterial(
                        RenderTexture.TextureGeneration.Allow);
                    global::Rhino.DocObjects.Texture? simTex = null;
                    if (mat.IsPhysicallyBased && simMat?.IsPhysicallyBased == true)
                        simTex = simMat.PhysicallyBased?.GetTexture(TextureType.PBR_BaseColor);
                    if (simTex == null)
                        simTex = simMat?.GetTexture(TextureType.Bitmap);
                    var p = simTex?.FileReference?.FullPath;
                    if (!string.IsNullOrWhiteSpace(p) && File.Exists(p))
                        AttachSlot("basecolor", p);
                }
                catch { /* skip */ }
            }

            if (slotsAttached.Count == 0)
                return;

            // Texture-slot rescue. If RDK / PBR strategies only found a texture
            // in the "emission" slot but the Rhino material has NO genuine
            // emission colour (i.e. the user never set the material to glow —
            // it's just a base-colour bitmap that Rhino's slot classifier put
            // in the wrong bucket), promote that texture to be the base-colour
            // texture instead. Otherwise the viewer renders the texture as
            // additive glow on top of an unrelated diffuse colour and the
            // model looks much brighter than the source Rhino viewport.
            var emissionIsRealGlow = mat.EmissionColor.R != 0
                                  || mat.EmissionColor.G != 0
                                  || mat.EmissionColor.B != 0;
            if (slotsAttached.Contains("emission")
                && !slotsAttached.Contains("basecolor")
                && !emissionIsRealGlow
                && !string.IsNullOrEmpty(rm.EmissiveTexture))
            {
                var blobRef = rm.EmissiveTexture!;
                rm.BaseColorTexture     = blobRef;
                rm.DiffuseTexture       = blobRef;
                rm.Diffuse              = 4278190080L; // black base; texture carries colour
                rm.EmissiveTexture      = null;
                rm.PbrEmissionTexture   = null;
                rm.EmissiveTextureOffset = null;
                rm.EmissiveTextureRepeat = null;
                slotsAttached.Add("basecolor");
                slotsAttached.Remove("emission");
            }

            // Conservative emissive handling: only mark the material as glowing
            // when Rhino itself reports a non-black emission colour. We never
            // synthesise a white emission to "boost" a base-colour texture —
            // doing so makes everything ~2× brighter than the source Rhino
            // viewport (the original v2.4.0 bug we shipped to old Speckle).
            if (slotsAttached.Contains("emission") && emissionIsRealGlow)
            {
                rm.EmissiveIntensity = 1.0;
            }
            else if (slotsAttached.Contains("emission"))
            {
                // Emission texture exists but Rhino's emission colour is black —
                // suppress glow so the viewer doesn't render an unintended
                // additive layer. (Anything in `emissiveTexture` will still be
                // multiplied by emissive colour = black = no glow.)
                rm.Emissive = 4278190080L;
                rm.EmissiveIntensity = 0.0;
            }

            // PBR scalars from Rhino material when available
            if (mat.IsPhysicallyBased && mat.PhysicallyBased is not null)
            {
                var pbr = mat.PhysicallyBased;
                rm.Roughness = pbr.Roughness;
                rm.Metalness = pbr.Metallic;
            }
        }
        catch
        {
            // Texture attachment is best-effort.
        }
    }
}
