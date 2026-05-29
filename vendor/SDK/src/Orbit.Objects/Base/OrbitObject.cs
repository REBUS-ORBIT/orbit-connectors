using Newtonsoft.Json;
using Orbit.Objects.BuiltElements;
using Orbit.Objects.Proxies;

namespace Orbit.Objects.Base;

/// <summary>
/// A named container object — equivalent to a "Collection" in Speckle's data model.
/// Used to represent Rhino layers, project roots, and any logical grouping of geometry.
///
/// IMPORTANT: <c>speckle_type</c> is fixed to <c>Speckle.Core.Models.Collections.Collection</c>
/// because the Speckle/ORBIT viewer only renders the sidebar layer tree for collections
/// declared with that exact type.
///
/// Detachment convention: <c>@elements</c> is detached (each child becomes its own DB row,
/// referenced via a <c>{referencedId, speckle_type: "reference"}</c> stub). <c>views</c> is
/// inline (full View3D objects sit inside the root JSON, NOT in the closure table). The
/// serialiser decides this based on the <c>@</c> prefix of the property name.
/// </summary>
public class OrbitObject : OrbitBase
{
    public override string OrbitType => "Speckle.Core.Models.Collections.Collection";

    /// <summary>Human-readable name (e.g. layer name, project/model name).</summary>
    [JsonProperty("name")]
    public string? Name { get; set; }

    /// <summary>
    /// Speckle collection-type label. The root model collection uses <c>"model"</c>; nested
    /// layer collections use <c>"layer"</c>. The viewer uses this to pick the sidebar icon.
    /// </summary>
    [JsonProperty("collectionType")]
    public string CollectionType { get; set; } = "layer";

    /// <summary>
    /// Full Rhino layer path (e.g. <c>"Parent::Child"</c>). Required by the viewer to
    /// render layer-color swatches in the sidebar.
    /// </summary>
    [JsonProperty("layerPath")]
    public string? LayerPath { get; set; }

    /// <summary>Layer colour as unsigned ARGB packed into a long.</summary>
    [JsonProperty("layerColor")]
    public long? LayerColor { get; set; }

    /// <summary>
    /// Optional fallback display geometry for collections that should render directly.
    /// </summary>
    [JsonProperty("displayValue")]
    public List<OrbitBase>? DisplayValue { get; set; }

    /// <summary>
    /// Detached child objects (nested collections, geometry objects). The <c>@</c> prefix
    /// signals to the serialiser that each child should be stored as its own DB row and
    /// replaced inline with a <c>{referencedId, speckle_type: "reference"}</c> stub.
    /// </summary>
    [JsonProperty("@elements")]
    public List<OrbitBase>? Elements { get; set; }

    /// <summary>
    /// Source application identifier. Uses snake_case on the wire to match the working
    /// Speckle reference produced by the Python SDK.
    /// </summary>
    [JsonProperty("source_application")]
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

    /// <summary>
    /// Named views from the source application. Stored INLINE (no <c>@</c> prefix) so the
    /// full View3D objects appear directly inside the root JSON, matching the working
    /// reference. The viewer reads these to populate the named-views panel.
    /// </summary>
    [JsonProperty("views")]
    public List<View3D>? Views { get; set; }
}
