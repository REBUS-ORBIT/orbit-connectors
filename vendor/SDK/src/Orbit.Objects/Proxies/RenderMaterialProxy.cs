using Newtonsoft.Json;

namespace Orbit.Objects.Proxies;

/// <summary>
/// Stores a render material and the set of object applicationIds that reference it.
/// Proxies are stored at the root of the version object tree, not nested within objects.
/// This keeps geometry objects lightweight and deduplicates material definitions.
/// </summary>
public class RenderMaterialProxy : Base.OrbitBase
{
    /// <summary>The material definition.</summary>
    [JsonProperty("value")]
    public RenderMaterial? Value { get; set; }

    /// <summary>
    /// applicationIds of all objects that use this material.
    /// </summary>
    [JsonProperty("objectIds")]
    public List<string>? ObjectIds { get; set; }
}

public class RenderMaterial : Base.OrbitBase
{
    [JsonProperty("name")]         public string? Name         { get; set; }
    [JsonProperty("opacity")]      public double  Opacity      { get; set; } = 1.0;
    [JsonProperty("metalness")]    public double  Metalness    { get; set; } = 0.0;
    [JsonProperty("roughness")]    public double  Roughness    { get; set; } = 1.0;
    /// <summary>Diffuse colour as ARGB int.</summary>
    [JsonProperty("diffuse")]      public int     Diffuse      { get; set; } = -1; // white
    /// <summary>Emissive colour as ARGB int.</summary>
    [JsonProperty("emissive")]     public int     Emissive     { get; set; } = -16777216; // black
}
