using Newtonsoft.Json;

namespace Orbit.Objects.Geometry;

public class Polyline : Base.OrbitBase
{
    /// <summary>Flat point array: x0,y0,z0, x1,y1,z1, ...</summary>
    [JsonProperty("value")]   public List<double>? Value  { get; set; }
    [JsonProperty("closed")]  public bool Closed  { get; set; }
    [JsonProperty("domain")]  public Primitives.Interval? Domain { get; set; }
    [JsonProperty("units")]   public string? Units { get; set; }
}
