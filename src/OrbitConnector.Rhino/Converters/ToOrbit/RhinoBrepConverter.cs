using Rhino.Geometry;
using Orbit.Objects.Base;
using Orbit.Objects.Data;
using Orbit.Objects.Other;
using OM = Orbit.Objects.Geometry;

namespace OrbitConnector.Rhino.Converters.ToOrbit;

/// <summary>
/// Converts a Rhino <see cref="Brep"/> to a native-Rhino round-trip wrapper —
/// an <see cref="RhinoDataObject"/> (<c>Objects.Data.DataObject:Objects.Data.RhinoObject</c>)
/// containing:
/// <list type="bullet">
///   <item>A <see cref="RawEncoding"/> with the base64-encoded single-object
///   <c>.3dm</c> bytes of the source Brep — receivers decode this to recover
///   the native NURBS data.</item>
///   <item>A <see cref="DisplayValue"/> mesh array (one mesh per Brep face,
///   with sharp seams preserved) so the ORBIT viewer can render the geometry
///   without understanding the native format. Materials, vertex colours, UVs,
///   and texture coordinates live on these meshes.</item>
/// </list>
/// <para>
/// This is the same wire format the Speckle Rhino8 connector emits — and is
/// what enables the connector to act as a real round-trip transport between
/// Rhino, Grasshopper, Revit, and the ORBIT viewer.
/// </para>
/// </summary>
public class RhinoBrepConverter : IRhinoToOrbitConverter
{
    private readonly RhinoMeshConverter _meshConverter = new();

    public bool CanConvert(GeometryBase geometry) => geometry is Brep;

    public OrbitBase Convert(GeometryBase geometry, ConversionContext context)
    {
        var rhinoBrep = (Brep)geometry;
        return BuildWrapper(rhinoBrep, type: "Brep", context, _meshConverter);
    }

    /// <summary>
    /// Build a <see cref="RhinoDataObject"/> wrapper around the given Rhino
    /// geometry. Used by every native-geometry converter — Brep, Extrusion,
    /// SubD, Surface — to keep their wire output identical.
    /// </summary>
    /// <param name="geometry">The native Rhino geometry to wrap.</param>
    /// <param name="type">Rhino source type string (<c>"Brep"</c>, <c>"Extrusion"</c>, ...).</param>
    /// <param name="context">Conversion context (Rhino object, units, ...).</param>
    /// <param name="meshConverter">Mesh-to-ORBIT converter for the displayValue meshes.</param>
    /// <param name="brepForDisplay">
    /// Optional Brep to use for display mesh extraction when <paramref name="geometry"/>
    /// is not itself a Brep (e.g. when wrapping an Extrusion, convert it to Brep first
    /// so face creases survive).
    /// </param>
    internal static RhinoDataObject BuildWrapper(
        GeometryBase geometry,
        string type,
        ConversionContext context,
        RhinoMeshConverter meshConverter,
        Brep? brepForDisplay = null)
    {
        var wrapper = new RhinoDataObject
        {
            Name  = context.CurrentObject?.Name is { Length: > 0 } n ? n : type,
            Type  = type,
            Units = context.Units,
        };

        var encoded = RhinoNativeEncoder.Encode(geometry);
        if (encoded != null)
        {
            wrapper.RawEncoding = new RawEncoding
            {
                Format   = "3dm",
                Contents = encoded,
            };
        }

        // Display meshes — prefer the brep render mesh path because it produces
        // per-face fragments with sharp seams (matching the working Speckle
        // viewer output). If the wrapped geometry is not a Brep, fall through
        // to per-geometry meshing in the caller.
        var brepSrc = brepForDisplay ?? (geometry as Brep);
        if (brepSrc != null)
        {
            var meshes = RhinoBrepDisplayMeshes.Extract(brepSrc, context);
            if (meshes.Count > 0)
            {
                wrapper.DisplayValue = meshes
                    .Select(m => (OM.Mesh)meshConverter.Convert(m, context))
                    .ToList();
            }
        }

        return wrapper;
    }
}
