using Newtonsoft.Json;

namespace Orbit.Objects.Geometry;

/// <summary>
/// A sequence of connected curve segments of potentially mixed types.
/// Each segment is an OrbitBase that can be a Line, Arc, NurbsCurve, etc.
/// </summary>
public class PolyCurve : Base.OrbitBase
{
    [JsonProperty("segments")]    public List<Base.OrbitBase>? Segments { get; set; }
    [JsonProperty("closed")]      public bool Closed  { get; set; }
    [JsonProperty("domain")]      public Primitives.Interval? Domain { get; set; }
    [JsonProperty("units")]       public string? Units { get; set; }
    [JsonProperty("displayValue")]public Polyline? DisplayValue { get; set; }
}
