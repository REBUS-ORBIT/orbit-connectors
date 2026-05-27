using Newtonsoft.Json;

namespace Orbit.Objects.Geometry;

/// <summary>
/// Direction vector (x, y, z) with units. Mirrors the Speckle
/// <c>Objects.Geometry.Vector</c> type used inside <see cref="Orbit.Objects.BuiltElements.View3D"/>
/// for <c>upDirection</c> and <c>forwardDirection</c>.
/// </summary>
public class Vector : Base.OrbitBase
{
    public override string OrbitType => "Objects.Geometry.Vector";

    [JsonProperty("x")] public double X { get; set; }
    [JsonProperty("y")] public double Y { get; set; }
    [JsonProperty("z")] public double Z { get; set; }
    [JsonProperty("units")] public string? Units { get; set; }

    public Vector() { }
    public Vector(double x, double y, double z, string? units = null)
    {
        X = x; Y = y; Z = z; Units = units;
    }
}
