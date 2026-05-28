using Newtonsoft.Json;

namespace Orbit.Objects.Proxies;

public class ColorProxy : Base.OrbitBase
{
    /// <summary>Colour as ARGB int.</summary>
    [JsonProperty("value")]
    public int Value { get; set; }

    /// <summary>Human-readable colour name (e.g. Rhino layer colour name).</summary>
    [JsonProperty("name")]
    public string? Name { get; set; }

    /// <summary>applicationIds of objects using this colour.</summary>
    [JsonProperty("objectIds")]
    public List<string>? ObjectIds { get; set; }
}
