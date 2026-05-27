using Newtonsoft.Json;

namespace Orbit.Objects.Geometry;

/// <summary>
/// A placed instance of a block definition.
/// The definition is stored once as a <see cref="Proxies.DefinitionProxy"/> at the root
/// of the version object tree, referenced here by <see cref="DefinitionId"/> which matches
/// the proxy's <see cref="Base.OrbitBase.ApplicationId"/>.
/// </summary>
public class Instance : Base.OrbitBase
{
    /// <summary>
    /// The applicationId of the corresponding DefinitionProxy.
    /// </summary>
    [JsonProperty("definitionId")]
    public string? DefinitionId { get; set; }

    /// <summary>
    /// 4×4 world transform for this instance.
    /// </summary>
    [JsonProperty("transform")]
    public Primitives.Transform? Transform { get; set; }

    [JsonProperty("units")]
    public string? Units { get; set; }

    /// <summary>Mesh display fallback for this instance placement.</summary>
    [JsonProperty("displayValue")]
    public List<Mesh>? DisplayValue { get; set; }
}
