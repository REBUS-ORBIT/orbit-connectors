using Newtonsoft.Json;

namespace Orbit.Objects.Proxies;

public class GroupProxy : Base.OrbitBase
{
    [JsonProperty("name")]      public string? Name      { get; set; }
    /// <summary>applicationIds of objects that belong to this group.</summary>
    [JsonProperty("objectIds")] public List<string>? ObjectIds { get; set; }
}
