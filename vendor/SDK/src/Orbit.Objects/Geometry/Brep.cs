using Newtonsoft.Json;

namespace Orbit.Objects.Geometry;

/// <summary>
/// NURBS boundary representation (solid or surface).
/// This is the primary native geometry type for Rhino. When received by a host
/// application that cannot handle Brep, the <see cref="DisplayValue"/> mesh is used.
/// </summary>
public class Brep : Base.OrbitBase
{
    public override string OrbitType => "Objects.Geometry.Brep";

    /// <summary>
    /// Encoded Brep data. Format is host-application dependent.
    /// For Rhino: base64-encoded .3dm Brep serialisation.
    /// </summary>
    [JsonProperty("encoded")]
    public string? Encoded { get; set; }

    [JsonProperty("provenance")]
    public string? Provenance { get; set; }

    [JsonProperty("units")]
    public string? Units { get; set; }

    /// <summary>
    /// NURBS surfaces that make up the Brep. Optional — populated when full
    /// native data is available.
    /// </summary>
    [JsonProperty("surfaces")]
    public List<Surface>? Surfaces { get; set; }

    /// <summary>
    /// Curve edges of the Brep (3D curves in model space).
    /// </summary>
    [JsonProperty("curve3D")]
    public List<Base.OrbitBase>? Curve3D { get; set; }

    /// <summary>
    /// Mesh display fallback. Always populated so viewers can render without
    /// understanding the native Brep format.
    /// </summary>
    [JsonProperty("displayValue")]
    public List<Mesh>? DisplayValue { get; set; }

    /// <summary>Full Rhino layer path of the source object.</summary>
    [JsonProperty("layerPath")]
    public string? LayerPath { get; set; }

    /// <summary>Rhino layer colour as unsigned ARGB packed into a long.</summary>
    [JsonProperty("layerColor")]
    public long? LayerColor { get; set; }

    /// <summary>Colour source: <c>"layer"</c> or <c>"object"</c>.</summary>
    [JsonProperty("colorSource")]
    public string? ColorSource { get; set; }
}
