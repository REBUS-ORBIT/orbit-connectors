using Newtonsoft.Json;

namespace Orbit.Objects.Geometry;

/// <summary>
/// ORBIT mesh — the primary display primitive.
/// Vertices are stored as a flat double array: [x0,y0,z0, x1,y1,z1, ...]
/// Faces are stored as a flat int array with variable-length encoding:
///   - Triangle: [3, i0, i1, i2]
///   - Quad:     [4, i0, i1, i2, i3]
/// Normals and texture coordinates follow the same flat-array pattern.
/// </summary>
public class Mesh : Base.OrbitBase
{
    /// <summary>Flat vertex array: x0,y0,z0, x1,y1,z1, ...</summary>
    [JsonProperty("vertices")]
    public List<double>? Vertices { get; set; }

    /// <summary>
    /// Variable-length face encoding: n, i0..i(n-1), n, i0..., ...
    /// where n is the face vertex count (3 = triangle, 4 = quad).
    /// </summary>
    [JsonProperty("faces")]
    public List<int>? Faces { get; set; }

    /// <summary>Flat vertex normal array: nx0,ny0,nz0, nx1,ny1,nz1, ...</summary>
    [JsonProperty("vertexNormals")]
    public List<double>? VertexNormals { get; set; }

    /// <summary>
    /// Per-vertex texture coordinates: u0,v0, u1,v1, ...
    /// Not yet utilised by the viewer — reserved for future use.
    /// </summary>
    [JsonProperty("textureCoordinates")]
    public List<double>? TextureCoordinates { get; set; }

    /// <summary>Per-face colour as ARGB int array. Optional.</summary>
    [JsonProperty("colors")]
    public List<int>? Colors { get; set; }

    [JsonProperty("units")] public string? Units { get; set; }

    // ── Layer / colour metadata ────────────────────────────────────────────────

    /// <summary>Full Rhino layer path (e.g. <c>"Parent::Child"</c>). Set during send.</summary>
    [JsonProperty("layerPath")]
    public string? LayerPath { get; set; }

    /// <summary>Layer colour as unsigned ARGB packed into a long.</summary>
    [JsonProperty("layerColor")]
    public long? LayerColor { get; set; }

    /// <summary>Colour source: <c>"object"</c>, <c>"layer"</c>, or <c>"material"</c>.</summary>
    [JsonProperty("colorSource")]
    public string? ColorSource { get; set; }

    /// <summary>Inline PBR render material. Carries diffuse/emissive scalars and texture blob refs.</summary>
    [JsonProperty("renderMaterial")]
    public Other.RenderMaterial? RenderMaterial { get; set; }

    /// <summary>Returns the number of vertices (Vertices.Count / 3).</summary>
    public int VertexCount => (Vertices?.Count ?? 0) / 3;
}
