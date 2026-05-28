using Newtonsoft.Json;
using Orbit.Objects.Geometry;

namespace Orbit.Objects.BuiltElements;

/// <summary>
/// A named camera view from the source application (e.g. a Rhino named view).
///
/// IMPORTANT: <c>speckle_type</c> is <c>Objects.BuiltElements.View.View3D</c> (note the extra
/// <c>.View.</c> segment) — that is the exact value the Speckle/ORBIT viewer matches against
/// when populating the named-views panel. The previous value (<c>Objects.BuiltElements.View3D</c>)
/// caused all views to be silently dropped from the panel.
///
/// Origin/target are <see cref="Point"/> objects (Speckle <c>Objects.Geometry.Point</c>);
/// up/forward directions are <see cref="Vector"/> objects (<c>Objects.Geometry.Vector</c>).
/// All four are stored inline inside the View3D — they are NOT detached to the closure table.
/// </summary>
public class View3D : Base.OrbitBase
{
    public override string OrbitType => "Objects.BuiltElements.View.View3D";

    /// <summary>Display name of the view (Rhino named-view name).</summary>
    [JsonProperty("name")]
    public string? Name { get; set; }

    /// <summary>Camera position in world space.</summary>
    [JsonProperty("origin")]
    public Point? Origin { get; set; }

    /// <summary>Point the camera is aimed at.</summary>
    [JsonProperty("target")]
    public Point? Target { get; set; }

    /// <summary>Camera up-direction vector.</summary>
    [JsonProperty("upDirection")]
    public Vector? UpDirection { get; set; }

    /// <summary>
    /// Camera forward-direction vector (from camera toward target). Required by the viewer;
    /// the previous implementation omitted this field, which prevented the saved-views panel
    /// from positioning the camera correctly.
    /// </summary>
    [JsonProperty("forwardDirection")]
    public Vector? ForwardDirection { get; set; }

    /// <summary>True for parallel/orthographic projection; false for perspective.</summary>
    [JsonProperty("isOrthogonal")]
    public bool IsOrthogonal { get; set; }

    /// <summary>
    /// 35mm-equivalent lens length for perspective views (Rhino's <c>Camera35mmLensLength</c>),
    /// or 0 for orthographic projections.
    /// </summary>
    [JsonProperty("lens")]
    public double Lens { get; set; }

    /// <summary>Units of the coordinate system (inherited from the document).</summary>
    [JsonProperty("units")]
    public string? Units { get; set; }
}
