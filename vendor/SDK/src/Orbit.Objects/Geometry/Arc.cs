using Newtonsoft.Json;

namespace Orbit.Objects.Geometry;

public class Arc : Base.OrbitBase
{
    [JsonProperty("radius")]      public double Radius     { get; set; }
    [JsonProperty("startAngle")]  public double StartAngle { get; set; }
    [JsonProperty("endAngle")]    public double EndAngle   { get; set; }
    [JsonProperty("angleRadians")]public double AngleRadians { get; set; }
    [JsonProperty("plane")]       public Plane? Plane      { get; set; }
    [JsonProperty("domain")]      public Primitives.Interval? Domain { get; set; }
    [JsonProperty("units")]       public string? Units     { get; set; }
    [JsonProperty("displayValue")]public Polyline? DisplayValue { get; set; }
}
