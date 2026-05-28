using Rhino;
using Rhino.DocObjects;
using Orbit.Objects.Other;
using Orbit.Objects.Proxies;

namespace OrbitConnector.Rhino.Converters;

/// <summary>
/// Shared state passed through the conversion pipeline.
/// Holds the active document, units, the currently-being-converted Rhino
/// object (so converters can reach object attributes for material/colour
/// resolution), and collected proxy data.
/// </summary>
public class ConversionContext
{
    public RhinoDoc Doc { get; }
    public string Units { get; }

    /// <summary>
    /// The Rhino object currently being converted. Set by the pipeline before
    /// each call to a converter, so converters can reach object attributes
    /// (colour, layer, name, user strings) without losing track of the parent
    /// during pure-geometry recursion.
    /// </summary>
    public RhinoObject? CurrentObject { get; set; }

    // Proxy collections — populated during send, consumed by bakers during
    // receive. The current send pipeline emits colour/material inline on each
    // mesh (matching the working Speckle reference), so these stay empty for
    // typical sends but remain in the schema for future use.
    public List<RenderMaterialProxy> MaterialProxies { get; } = new();
    public List<ColorProxy> ColorProxies { get; } = new();
    public List<GroupProxy> GroupProxies { get; } = new();
    public List<DefinitionProxy> DefinitionProxies { get; } = new();

    /// <summary>
    /// Material-by-Rhino-material-index cache: avoids re-building the same
    /// <see cref="RenderMaterial"/> for every face on every mesh.
    /// </summary>
    public Dictionary<int, RenderMaterial> RegisteredMaterials { get; } = new();

    /// <summary>
    /// SHA-256 hex digest → on-disk texture file path. Populated during conversion;
    /// uploaded and patched to server blob ids before serialisation.
    /// </summary>
    public Dictionary<string, string> PendingBlobFiles { get; } = new();

    public ConversionContext(RhinoDoc doc)
    {
        Doc = doc;
        Units = doc.ModelUnitSystem switch
        {
            UnitSystem.Millimeters => "mm",
            UnitSystem.Centimeters => "cm",
            UnitSystem.Meters      => "m",
            UnitSystem.Feet        => "ft",
            UnitSystem.Inches      => "in",
            _                      => "none"
        };
    }

    /// <summary>
    /// Resolve the displayed colour for <see cref="CurrentObject"/> following
    /// Rhino's colour-source rules:
    ///   ColorFromObject  → the per-object override colour
    ///   ColorFromLayer   → the layer's colour (default Rhino behaviour)
    ///   ColorFromMaterial→ the assigned render material's diffuse colour
    ///   ColorFromParent  → fall back to the layer (good enough for sends)
    /// Returns the source label (<c>"object"</c>, <c>"layer"</c>,
    /// <c>"material"</c>) and an unsigned-ARGB-long-packed colour.
    /// Returns <c>null</c> when there is no <see cref="CurrentObject"/>.
    /// </summary>
    public (string source, long argb)? ResolveCurrentColor()
    {
        var obj = CurrentObject;
        if (obj == null) return null;

        var attrs = obj.Attributes;
        var src = attrs.ColorSource;

        System.Drawing.Color color;
        string sourceLabel;
        switch (src)
        {
            case ObjectColorSource.ColorFromObject:
                color = attrs.ObjectColor;
                sourceLabel = "object";
                break;

            case ObjectColorSource.ColorFromMaterial:
                {
                    var matIdx = attrs.MaterialIndex;
                    if (matIdx >= 0 && matIdx < Doc.Materials.Count)
                    {
                        var rmat = Doc.Materials[matIdx];
                        color = rmat.PreviewColor;
                    }
                    else
                    {
                        color = Doc.Layers[attrs.LayerIndex].Color;
                        sourceLabel = "layer";
                        return (sourceLabel, RenderMaterial.PackArgb(color));
                    }
                    sourceLabel = "material";
                    break;
                }

            case ObjectColorSource.ColorFromLayer:
            case ObjectColorSource.ColorFromParent:
            default:
                color = Doc.Layers[attrs.LayerIndex].Color;
                sourceLabel = "layer";
                break;
        }

        return (sourceLabel, RenderMaterial.PackArgb(color));
    }

    /// <summary>
    /// Build (or fetch from cache) a <see cref="RenderMaterial"/> for the
    /// current Rhino object. Reads from the assigned Rhino material when one
    /// exists; otherwise synthesises a single-colour material from the
    /// resolved colour (object override or layer colour).
    /// </summary>
    public RenderMaterial? BuildCurrentRenderMaterial()
    {
        var obj = CurrentObject;
        if (obj == null) return null;

        // Effective material (object override, layer, or render material).
        var effective = obj.GetMaterial(true);
        if (effective != null)
        {
            // Mix the effective material's PBR scalars / textures with the
            // colour Rhino actually displays for this object. A "Metal" preset
            // material legitimately has DiffuseColor=black (metals reflect, do
            // not diffuse); the displayed colour comes from the LAYER. Using
            // the raw material colour for a `ColorFromLayer` object would ship
            // black diffuse and render an opaque black blob in the viewer.
            var resolvedColor = ResolveCurrentColor();
            var keyedColorSource = resolvedColor?.source ?? "material";

            // Cache per (material, colour-source). Two objects on different
            // layers share a Rhino material but legitimately need different
            // diffuse values, so the layer/object colour participates in the
            // cache key.
            var cacheKey = effective.Index * 31
                         + (resolvedColor?.argb ?? 0).GetHashCode();
            if (RegisteredMaterials.TryGetValue(cacheKey, out var cached))
                return cached;

            // For ColorFromLayer / ColorFromObject the displayed diffuse is
            // the resolved colour. For ColorFromMaterial we read the *PBR*
            // base colour when the Rhino material is physically based —
            // `Material.DiffuseColor` is Rhino's legacy approximation (often
            // a tint derived from the emission texture) and gives the wrong
            // value for PBR materials with `base = black, emission = textured`
            // (the user sees their black-base material shipped as orange
            // diffuse, which the viewer then adds on top of the emission
            // texture → ~2× brightness on the inside walls).
            var diffuse = keyedColorSource == "material"
                ? PackEffectiveBaseColor(effective)
                : resolvedColor!.Value.argb;
            var emissive = ResolveEmissionColor(effective);
            var opacity  = 1.0 - effective.Transparency;

            double roughness = 0.5;
            double metalness = 0.0;
            if (effective.IsPhysicallyBased && effective.PhysicallyBased is not null)
            {
                roughness = effective.PhysicallyBased.Roughness;
                metalness = effective.PhysicallyBased.Metallic;
            }

            var material = RenderMaterial.Create(
                string.IsNullOrWhiteSpace(effective.Name) ? "default" : effective.Name,
                diffuse,
                emissive: emissive,
                opacity: opacity,
                roughness: roughness,
                metalness: metalness);

            RhinoMaterialHelper.AttachTextures(obj, material, Doc, PendingBlobFiles);

            RegisteredMaterials[cacheKey] = material;
            return material;
        }

        var attrs = obj.Attributes;
        var matIdx = attrs.MaterialIndex;

        if (matIdx >= 0 && matIdx < Doc.Materials.Count)
        {
            if (RegisteredMaterials.TryGetValue(matIdx, out var cached))
                return cached;

            var rmat = Doc.Materials[matIdx];
            var diffuse  = rmat.DiffuseColor;
            var emissive = rmat.EmissionColor;
            var opacity = 1.0 - rmat.Transparency;

            double roughness = 0.5;
            double metalness = 0.0;

            var material = RenderMaterial.Create(
                string.IsNullOrWhiteSpace(rmat.Name) ? "default" : rmat.Name,
                RenderMaterial.PackArgb(diffuse),
                emissive: RenderMaterial.PackArgb(emissive),
                opacity: opacity,
                roughness: roughness,
                metalness: metalness);

            RhinoMaterialHelper.AttachTextures(obj, material, Doc, PendingBlobFiles);

            RegisteredMaterials[matIdx] = material;
            return material;
        }

        // No assigned material — build a single-colour material from the
        // resolved object/layer colour so the viewer still gets a tint.
        var resolved = ResolveCurrentColor();
        if (resolved == null) return null;

        var label = resolved.Value.source == "object" ? "Object Color" : "Layer Color";
        return RenderMaterial.Create(label, resolved.Value.argb);
    }

    /// <summary>
    /// Returns the packed ARGB base colour for the supplied Rhino material,
    /// preferring the PBR <c>BaseColor</c> when the material is physically
    /// based. <see cref="global::Rhino.DocObjects.Material.DiffuseColor"/> is a
    /// legacy approximation for non-PBR renderers and frequently reports a
    /// tint derived from a texture (e.g. a black-base material with an orange
    /// emission texture shows up as orange-ish diffuse — which the viewer then
    /// renders on top of the emission and the user sees the wrong colour).
    /// </summary>
    private static long PackEffectiveBaseColor(global::Rhino.DocObjects.Material mat)
    {
        if (mat.IsPhysicallyBased && mat.PhysicallyBased is { } pbr)
        {
            var c = pbr.BaseColor;
            int a = ClampByte(c.A * 255.0);
            int r = ClampByte(c.R * 255.0);
            int g = ClampByte(c.G * 255.0);
            int b = ClampByte(c.B * 255.0);
            // Ensure alpha is opaque when Rhino reports 0 alpha for a colour
            // that's clearly meant to be visible (PBR base colour A is often
            // unset → 0). Otherwise a black-base PBR material with no alpha
            // would ship as fully-transparent black.
            if (a == 0) a = 255;
            return ((long)a << 24) | ((long)r << 16) | ((long)g << 8) | (long)b;
        }
        return RenderMaterial.PackArgb(mat.DiffuseColor);
    }

    /// <summary>
    /// PBR emission colour preference identical to
    /// <see cref="PackEffectiveBaseColor"/>. Rhino's PBR
    /// <c>Emission</c> is the user-facing "Emission Color" swatch in the
    /// material editor; <see cref="global::Rhino.DocObjects.Material.EmissionColor"/>
    /// can lag behind PBR edits.
    /// </summary>
    private static long ResolveEmissionColor(global::Rhino.DocObjects.Material mat)
    {
        if (mat.IsPhysicallyBased && mat.PhysicallyBased is { } pbr)
        {
            var c = pbr.Emission;
            int a = ClampByte(c.A * 255.0);
            int r = ClampByte(c.R * 255.0);
            int g = ClampByte(c.G * 255.0);
            int b = ClampByte(c.B * 255.0);
            if (a == 0) a = 255;
            return ((long)a << 24) | ((long)r << 16) | ((long)g << 8) | (long)b;
        }
        return RenderMaterial.PackArgb(mat.EmissionColor);
    }

    private static int ClampByte(double v)
    {
        if (double.IsNaN(v)) return 0;
        if (v <= 0) return 0;
        if (v >= 255) return 255;
        return (int)Math.Round(v);
    }
}
