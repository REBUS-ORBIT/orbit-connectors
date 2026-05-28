using Newtonsoft.Json;

namespace Orbit.Objects.Geometry;

public class Circle : Base.OrbitBase
{
    [JsonProperty("radius")] public double Radius { get; set; }
    [JsonProperty("plane")]  public Plane? Plane  { get; set; }
    [JsonProperty("domain")] public Primitives.Interval? Domain { get; set; }
    [JsonProperty("units")]  public string? Units { get; set; }
    [JsonProperty("displayValue")] public Polyline? DisplayValue { get; set; }
}
