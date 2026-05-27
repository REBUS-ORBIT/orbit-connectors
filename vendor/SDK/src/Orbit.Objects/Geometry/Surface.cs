using Newtonsoft.Json;

namespace Orbit.Objects.Geometry;

public class Surface : Base.OrbitBase
{
    [JsonProperty("degreeU")]   public int DegreeU   { get; set; }
    [JsonProperty("degreeV")]   public int DegreeV   { get; set; }
    [JsonProperty("rational")]  public bool Rational  { get; set; }
    [JsonProperty("closedU")]   public bool ClosedU   { get; set; }
    [JsonProperty("closedV")]   public bool ClosedV   { get; set; }
    [JsonProperty("countU")]    public int CountU    { get; set; }
    [JsonProperty("countV")]    public int CountV    { get; set; }

    /// <summary>
    /// Flat control point array: x,y,z,w for each point, row-major (U varies fastest).
    /// Total elements = CountU * CountV * 4.
    /// </summary>
    [JsonProperty("pointData")] public List<double>? PointData { get; set; }
    [JsonProperty("knotsU")]    public List<double>? KnotsU    { get; set; }
    [JsonProperty("knotsV")]    public List<double>? KnotsV    { get; set; }
    [JsonProperty("domainU")]   public Primitives.Interval? DomainU { get; set; }
    [JsonProperty("domainV")]   public Primitives.Interval? DomainV { get; set; }
    [JsonProperty("units")]     public string? Units { get; set; }
}
