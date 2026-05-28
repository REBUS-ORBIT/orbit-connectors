using Newtonsoft.Json;

namespace Orbit.Objects.Other;

/// <summary>
/// PBR render material. Used to carry full material definitions (colours, PBR scalars,
/// and texture blob references) from the sending application to the ORBIT viewer and
/// any receiving connector.
///
/// Texture fields hold either <c>null</c> (no texture) or a blob reference in the form
/// <c>@blob:SHA256HEX</c> that the ORBIT server and viewer resolve to image URLs.
/// The <see cref="OrbitBase"/> dynamic indexer allows setting arbitrary texture slots.
/// </summary>
public class RenderMaterial : Base.OrbitBase
{
    public override string OrbitType => "Objects.Other.RenderMaterial";

    [JsonProperty("name")]       public string? Name      { get; set; }

    /// <summary>Diffuse colour as unsigned ARGB packed into a long.</summary>
    [JsonProperty("diffuse")]    public long    Diffuse   { get; set; } = unchecked((long)0xFF_FF_FF_FF);

    /// <summary>Emissive colour as unsigned ARGB packed into a long.</summary>
    [JsonProperty("emissive")]   public long    Emissive  { get; set; } = unchecked((long)0xFF_00_00_00);

    [JsonProperty("opacity")]    public double  Opacity   { get; set; } = 1.0;
    [JsonProperty("roughness")]  public double  Roughness { get; set; } = 0.5;
    [JsonProperty("metalness")]  public double  Metalness { get; set; } = 0.0;

    /// <summary>Emissive intensity multiplier (0 = off, 1 = full glow).</summary>
    [JsonProperty("emissiveIntensity")]    public double? EmissiveIntensity   { get; set; }

    [JsonProperty("baseColorTexture")]     public string? BaseColorTexture    { get; set; }
    [JsonProperty("diffuseTexture")]       public string? DiffuseTexture      { get; set; }
    [JsonProperty("emissiveTexture")]      public string? EmissiveTexture     { get; set; }
    [JsonProperty("pbrEmissionTexture")]   public string? PbrEmissionTexture  { get; set; }
    [JsonProperty("roughnessTexture")]     public string? RoughnessTexture    { get; set; }
    [JsonProperty("metalnessTexture")]     public string? MetalnessTexture    { get; set; }
    [JsonProperty("normalTexture")]        public string? NormalTexture       { get; set; }
    [JsonProperty("opacityTexture")]       public string? OpacityTexture      { get; set; }

    /// <summary>Emissive texture UV offset: [u, v].</summary>
    [JsonProperty("emissiveTextureOffset")] public List<double>? EmissiveTextureOffset { get; set; }

    /// <summary>Emissive texture UV repeat/scale: [u, v].</summary>
    [JsonProperty("emissiveTextureRepeat")] public List<double>? EmissiveTextureRepeat { get; set; }

    // ── Factory ────────────────────────────────────────────────────────────────

    /// <summary>
    /// Convenience factory — mirrors the Python SDK's <c>RenderMaterial</c> constructor.
    /// </summary>
    public static RenderMaterial Create(
        string name,
        long diffuse,
        long   emissive  = unchecked((long)0xFF_00_00_00),
        double opacity   = 1.0,
        double roughness = 0.5,
        double metalness = 0.0) => new()
    {
        Name      = name,
        Diffuse   = diffuse,
        Emissive  = emissive,
        Opacity   = opacity,
        Roughness = roughness,
        Metalness = metalness,
    };

    // ── Colour helpers ─────────────────────────────────────────────────────────

    /// <summary>
    /// Packs a <see cref="System.Drawing.Color"/> into an unsigned ARGB long,
    /// matching the Python SDK convention (avoids sign-bit mismatch for colours
    /// with alpha 255 that would otherwise serialize as negative integers).
    /// </summary>
    public static long PackArgb(System.Drawing.Color color) =>
        (long)(uint)color.ToArgb();

    /// <summary>Packs a signed 32-bit ARGB int into an unsigned long.</summary>
    public static long PackArgb(int argb) =>
        (long)(uint)argb;
}
