using Rhino.DocObjects;
using Rhino.Geometry;
using Orbit.Objects.Base;
using OM = Orbit.Objects.Geometry;

namespace OrbitConnector.Rhino.Converters.ToOrbit;

/// <summary>
/// Last-resort converter. Tries hard not to drop any object on the floor:
///
///   1. <see cref="Brep"/>    → tessellate via <see cref="Mesh.CreateFromBrep"/>
///   2. <see cref="Mesh"/>    → forward straight to <see cref="RhinoMeshConverter"/>
///   3. anything else         → ask the parent <see cref="RhinoObject"/>
///                              for its render meshes via
///                              <see cref="ConversionContext.CurrentObject"/>.
///                              This catches text, hatches, blocks, and
///                              every other geometry type Rhino can render
///                              in the viewport.
///
/// Throws <see cref="NotSupportedException"/> only when even the render-mesh
/// strategy returns nothing (e.g. pure curves, points without a converter).
/// </summary>
public class RhinoFallbackConverter : IRhinoToOrbitConverter
{
    private readonly RhinoMeshConverter _meshConverter = new();

    public bool CanConvert(GeometryBase geometry) => true;

    public OrbitBase Convert(GeometryBase geometry, ConversionContext context)
    {
        Mesh[]? meshes = null;

        if (geometry is Brep brep)
        {
            var extracted = RhinoBrepDisplayMeshes.Extract(brep, context);
            meshes = extracted.Count > 0 ? extracted.ToArray() : null;
        }
        else if (geometry is Mesh mesh)
        {
            meshes = new[] { mesh };
        }
        else
        {
            var rhinoObj = context.CurrentObject;
            if (rhinoObj != null)
            {
                var extracted = RhinoObjectMeshes.ExtractFromObject(rhinoObj, context);
                if (extracted.Count > 0)
                    meshes = extracted.ToArray();
            }

            if (meshes == null || meshes.Length == 0)
                meshes = RhinoObjectMeshes.ExtractFromGeometry(geometry, context).ToArray();
        }

        if (meshes == null || meshes.Length == 0)
            throw new NotSupportedException(
                $"No display mesh available for {geometry.GetType().Name}");

        // Filter out empty meshes (Rhino sometimes returns null/empty entries).
        var nonEmpty = meshes.Where(m => m != null && m.Vertices.Count > 0).ToList();
        if (nonEmpty.Count == 0)
            throw new NotSupportedException(
                $"All display meshes were empty for {geometry.GetType().Name}");

        if (nonEmpty.Count == 1)
            return _meshConverter.Convert(nonEmpty[0], context);

        return new OrbitObject
        {
            DisplayValue = nonEmpty
                .Select(m => (OrbitBase)_meshConverter.Convert(m, context))
                .ToList(),
        };
    }
}
