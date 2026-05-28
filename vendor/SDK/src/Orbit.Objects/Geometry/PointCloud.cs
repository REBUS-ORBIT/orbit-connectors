using Newtonsoft.Json;

namespace Orbit.Objects.Geometry;

public class PointCloud : Base.OrbitBase
{
    /// <summary>Flat point array: x0,y0,z0, x1,y1,z1, ...</summary>
    [JsonProperty("points")]  public List<double>? Points  { get; set; }
    /// <summary>Per-point colour as ARGB int. Optional.</summary>
    [JsonProperty("colors")]  public List<int>?    Colors  { get; set; }
    /// <summary>Per-point normal: nx0,ny0,nz0, ... Optional.</summary>
    [JsonProperty("normals")] public List<double>? Normals { get; set; }
    [JsonProperty("units")]   public string? Units { get; set; }
}
