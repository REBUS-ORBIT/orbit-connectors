using Newtonsoft.Json;
using Orbit.Objects.BuiltElements;
using Orbit.Objects.Proxies;

namespace Orbit.Objects.Base;

/// <summary>
/// A named container object — equivalent to a "DataObject" or "Collection" in the ORBIT data model.
/// Used to represent Rhino layers, project roots, and any logical grouping of geometry.
///
/// Geometry is stored in <see cref="DisplayValue"/> as an array of displayable primitives
/// (typically <see cref="Orbit.Objects.Geometry.Mesh"/> objects) for viewers that cannot
/// handle native geometry types. Native geometry types (Brep, NurbsCurve etc.) are stored
/// as typed children and referenced via the closure table.
/// </summary>
public class OrbitObject : OrbitBase
{
    /// <summary>Human-readable name (e.g. layer name, project name).</summary>
    [JsonProperty("name")]
    public string? Name { get; set; }

    /// <summary>
    /// Collection type classifier. Common values:
    /// <list type="bullet">
    ///   <item><c>"model"</c> — root version object (what the viewer sees as the top-level tree node)</item>
    ///   <item><c>"layer"</c> — a Rhino layer group</item>
    /// </list>
    /// </summary>
    [JsonProperty("collectionType")]
    public string? CollectionType { get; set; }

    /// <summary>
    /// Full Rhino layer path (e.g. <c>"ParentLayer::ChildLayer"</c>).
    /// Set on layer collection objects so receivers can recreate the exact layer hierarchy.
    /// </summary>
    [JsonProperty("layerPath")]
    public string? LayerPath { get; set; }

    /// <summary>Layer colour as unsigned ARGB packed into a long.</summary>
    [JsonProperty("layerColor")]
    public long? LayerColor { get; set; }

    /// <summary>
    /// Array of displayable geometry primitives. Used by the 3D viewer and by
    /// host applications that receive an unknown type — they fall back to rendering
    /// whatever is in displayValue.
    /// </summary>
    [JsonProperty("displayValue")]
    public List<OrbitBase>? DisplayValue { get; set; }

    /// <summary>Child objects (nested collections, geometry objects).</summary>
    [JsonProperty("elements")]
    public List<OrbitBase>? Elements { get; set; }

    /// <summary>Source application identifier (e.g. "OrbitRhino").</summary>
    [JsonProperty("sourceApplication")]
    public string? SourceApplication { get; set; }

    /// <summary>Schema units (e.g. "mm", "m", "ft").</summary>
    [JsonProperty("units")]
    public string? Units { get; set; }

    // ── Proxy collections ──────────────────────────────────────────────────────
    // Stored inline at the root of the version object tree. Available for
    // advanced receive scenarios (material proxies, group proxies, etc.).

    [JsonProperty("renderMaterialProxies")]
    public List<RenderMaterialProxy>? RenderMaterialProxies { get; set; }

    [JsonProperty("colorProxies")]
    public List<ColorProxy>? ColorProxies { get; set; }

    [JsonProperty("groupProxies")]
    public List<GroupProxy>? GroupProxies { get; set; }

    [JsonProperty("definitionProxies")]
    public List<DefinitionProxy>? DefinitionProxies { get; set; }

    /// <summary>Named camera views extracted from the source document.</summary>
    [JsonProperty("views")]
    public List<View3D>? Views { get; set; }
}
