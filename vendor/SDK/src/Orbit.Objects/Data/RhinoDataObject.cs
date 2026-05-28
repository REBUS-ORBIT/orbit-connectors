using Newtonsoft.Json;

namespace Orbit.Objects.Data;

/// <summary>
/// A wrapper that carries a native Rhino object through ORBIT's closure table.
///
/// <para>
/// The primary payload is <see cref="RawEncoding"/> — a base64-encoded single-object
/// <c>.3dm</c> file that allows Rhino connectors to reconstruct the exact geometry
/// (Brep, Extrusion, SubD, NurbsSurface, …) byte-for-byte on receive. A Rhino
/// connector MUST try the raw encoding first and fall back to <see cref="DisplayValue"/>
/// only if decoding fails or if the receiving application is not Rhino.
/// </para>
///
/// <para>
/// <see cref="DisplayValue"/> meshes are always populated so that viewers and
/// non-Rhino connectors can still render the object without understanding the native format.
/// </para>
/// </summary>
public class RhinoDataObject : Base.OrbitBase
{
    public override string OrbitType => "Objects.Data.DataObject:Objects.Data.RhinoObject";

    /// <summary>Display name of the object (from Rhino object attributes).</summary>
    [JsonProperty("name")]
    public string? Name { get; set; }

    /// <summary>Rhino object type description (e.g. "Brep", "Extrusion", "SubD").</summary>
    [JsonProperty("type")]
    public string? Type { get; set; }

    /// <summary>Schema units inherited from the document (e.g. "mm", "m").</summary>
    [JsonProperty("units")]
    public string? Units { get; set; }

    /// <summary>
    /// Native base64-encoded .3dm payload. Stored as a detached ORBIT object
    /// (resolved separately from the closure table during receive).
    /// </summary>
    [JsonProperty("rawEncoding")]
    public Other.RawEncoding? RawEncoding { get; set; }

    /// <summary>
    /// Tessellated mesh representations of the object, always populated so
    /// viewers can render without understanding the native format.
    /// </summary>
    [JsonProperty("displayValue")]
    public List<Geometry.Mesh>? DisplayValue { get; set; }

    /// <summary>Arbitrary user-defined properties from the Rhino object's UserDictionary.</summary>
    [JsonProperty("properties")]
    public Dictionary<string, object?>? Properties { get; set; }
}
