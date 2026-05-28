using Newtonsoft.Json;

namespace Orbit.Objects.Proxies;

/// <summary>
/// Stores a render material and the set of object applicationIds that reference it.
/// Proxies are stored at the root of the version object tree, not nested within objects.
/// This keeps geometry objects lightweight and deduplicates material definitions.
/// </summary>
public class RenderMaterialProxy : Base.OrbitBase
{
    /// <summary>The full PBR material definition.</summary>
    [JsonProperty("value")]
    public Other.RenderMaterial? Value { get; set; }

    /// <summary>
    /// applicationIds of all objects that use this material.
    /// </summary>
    [JsonProperty("objectIds")]
    public List<string>? ObjectIds { get; set; }
}
