using Newtonsoft.Json;

namespace Orbit.Objects.Other;

/// <summary>
/// Opaque binary payload encoded as base64 with an associated format tag.
/// Used by <see cref="Orbit.Objects.Data.RhinoDataObject"/> to carry a single-object
/// <c>.3dm</c> file verbatim through ORBIT's closure table, enabling bit-perfect
/// round-trips between Rhino instances.
/// </summary>
public class RawEncoding : Base.OrbitBase
{
    public override string OrbitType => "Objects.Other.RawEncoding";

    /// <summary>
    /// Short format identifier, e.g. <c>"3dm"</c> for Rhino 3DM files.
    /// </summary>
    [JsonProperty("format")]
    public string? Format { get; set; }

    /// <summary>
    /// Base64-encoded binary content.
    /// </summary>
    [JsonProperty("contents")]
    public string? Contents { get; set; }
}
