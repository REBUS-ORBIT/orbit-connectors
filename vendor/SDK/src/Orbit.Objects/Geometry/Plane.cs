using Newtonsoft.Json;

namespace Orbit.Objects.Geometry;

public class Plane : Base.OrbitBase
{
    [JsonProperty("origin")] public Point? Origin { get; set; }
    [JsonProperty("normal")] public Primitives.Vector3d? Normal { get; set; }
    [JsonProperty("xdir")]   public Primitives.Vector3d? Xdir   { get; set; }
    [JsonProperty("ydir")]   public Primitives.Vector3d? Ydir   { get; set; }
    [JsonProperty("units")]  public string? Units { get; set; }
}
