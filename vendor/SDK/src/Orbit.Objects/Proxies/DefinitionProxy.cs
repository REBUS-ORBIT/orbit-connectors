using Newtonsoft.Json;

namespace Orbit.Objects.Proxies;

/// <summary>
/// Stores a block definition — geometry that can be placed multiple times as instances.
/// Stored once at the root of the version tree; instances reference it via
/// <see cref="Base.OrbitBase.ApplicationId"/> matching
/// <see cref="Geometry.Instance.DefinitionId"/>.
/// </summary>
public class DefinitionProxy : Base.OrbitBase
{
    [JsonProperty("name")]
    public string? Name { get; set; }

    /// <summary>The geometry objects that make up this block definition.</summary>
    [JsonProperty("objects")]
    public List<Base.OrbitBase>? Objects { get; set; }

    [JsonProperty("basePoint")]
    public Geometry.Point? BasePoint { get; set; }

    [JsonProperty("units")]
    public string? Units { get; set; }
}
