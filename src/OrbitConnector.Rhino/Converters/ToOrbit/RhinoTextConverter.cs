using Rhino.DocObjects;
using Rhino.Geometry;
using Orbit.Objects.Base;
using OM = Orbit.Objects.Geometry;

namespace OrbitConnector.Rhino.Converters.ToOrbit;

/// <summary>
/// Converts a Rhino text annotation (<see cref="TextEntity"/>) by extracting
/// its rendered display geometry. Text doesn't have a clean ORBIT
/// equivalent, so we wrap the display meshes in an <see cref="OrbitObject"/>
/// container that the viewer can render directly via its <c>displayValue</c>.
///
/// We try several strategies in order:
///   1. Explode the TextEntity into Brep/curve geometry and mesh those
///      Breps. This gives a true 3D extrusion of the glyphs when the text
///      has draft height.
///   2. Fall back to <see cref="RhinoObject.GetMeshes(MeshType.Render)"/>
///      via <see cref="ConversionContext.CurrentObject"/>.
/// </summary>
public class RhinoTextConverter : IRhinoToOrbitConverter
{
    private readonly RhinoMeshConverter _meshConverter = new();

    public bool CanConvert(GeometryBase geometry) => geometry is TextEntity;

    public OrbitBase Convert(GeometryBase geometry, ConversionContext context)
    {
        var meshes = ExtractMeshes((TextEntity)geometry, context);

        if (meshes.Count == 0)
            throw new InvalidOperationException(
                "TextEntity has no displayable geometry.");

        if (meshes.Count == 1)
            return meshes[0];

        return new OrbitObject
        {
            Name         = "Text",
            DisplayValue = meshes.Cast<OrbitBase>().ToList(),
        };
    }

    private List<OM.Mesh> ExtractMeshes(TextEntity te, ConversionContext context)
    {
        var meshes = new List<OM.Mesh>();

        // Strategy 1: GetMeshes on the parent RhinoObject. This works for
        // anything Rhino can render in the viewport — including extruded
        // text glyphs, hatches, and other annotation geometry.
        var rhinoObj = context.CurrentObject;
        if (rhinoObj != null)
        {
            var renderMeshes = rhinoObj.GetMeshes(MeshType.Render);
            if (renderMeshes != null)
                foreach (var m in renderMeshes)
                    if (m != null && m.Vertices.Count > 0)
                        meshes.Add((OM.Mesh)_meshConverter.Convert(m, context));
        }

        if (meshes.Count > 0) return meshes;

        // Strategy 2: explode the TextEntity into its outline curves and
        // build a planar mesh from each closed curve loop. Useful for
        // single-line text with no extrusion (zero draft height) — Rhino's
        // render-mesh path returns nothing for those.
        try
        {
            var explodedCurves = te.Explode();
            if (explodedCurves != null && explodedCurves.Length > 0)
            {
                var planarBreps = Brep.CreatePlanarBreps(explodedCurves, 0.001);
                if (planarBreps != null)
                {
                    foreach (var brep in planarBreps)
                    {
                        var brepMeshes = RhinoBrepDisplayMeshes.TessellateBrep(brep);
                        if (brepMeshes.Count > 0)
                            foreach (var m in brepMeshes)
                                if (m != null && m.Vertices.Count > 0)
                                    meshes.Add((OM.Mesh)_meshConverter.Convert(m, context));
                    }
                }
            }
        }
        catch
        {
            // Some TextEntity instances fail to explode (no font, weird
            // settings); silent fall-through.
        }

        return meshes;
    }
}
