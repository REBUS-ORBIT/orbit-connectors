using Newtonsoft.Json;

namespace Orbit.Objects.Geometry;

public class Line : Base.OrbitBase
{
    [JsonProperty("start")]  public Point? Start  { get; set; }
    [JsonProperty("end")]    public Point? End    { get; set; }
    [JsonProperty("units")]  public string? Units { get; set; }
    [JsonProperty("domain")] public Primitives.Interval? Domain { get; set; }
}
