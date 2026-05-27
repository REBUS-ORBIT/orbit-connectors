using Newtonsoft.Json;

namespace Orbit.Objects.Geometry;

public class Point : Base.OrbitBase
{
    public override string OrbitType => "Objects.Geometry.Point";

    [JsonProperty("x")] public double X { get; set; }
    [JsonProperty("y")] public double Y { get; set; }
    [JsonProperty("z")] public double Z { get; set; }
    [JsonProperty("units")] public string? Units { get; set; }

    public Point() { }
    public Point(double x, double y, double z, string? units = null)
    {
        X = x; Y = y; Z = z; Units = units;
    }
}
