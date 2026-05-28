using Newtonsoft.Json;

namespace Orbit.Objects.Primitives;

/// <summary>
/// 4×4 column-major transformation matrix.
/// Used for block instance transforms and coordinate system definitions.
/// Stored as a flat 16-element double array: [m00, m10, m20, m30, m01, m11, ...]
/// </summary>
public class Transform : Base.OrbitBase
{
    /// <summary>Flat 16-element column-major matrix values.</summary>
    [JsonProperty("matrix")]
    public double[]? Matrix { get; set; }

    /// <summary>Units the transform was authored in.</summary>
    [JsonProperty("units")]
    public string? Units { get; set; }

    public static Transform Identity => new()
    {
        Matrix = new double[] { 1, 0, 0, 0,  0, 1, 0, 0,  0, 0, 1, 0,  0, 0, 0, 1 }
    };
}
