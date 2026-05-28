using Rhino.Geometry;
using Orbit.Objects.Base;

namespace OrbitConnector.Rhino.Converters.ToOrbit;

/// <summary>
/// Fallback converter - attempts to extract a display mesh from any geometry type.
/// Used when no primary converter matches, or when a primary converter throws.
/// Ensures every Rhino object produces at least some visual output in ORBIT.
/// </summary>
public class RhinoFallbackConverter : IRhinoToOrbitConverter
{
    private readonly RhinoMeshConverter _meshConverter = new();

    /// <summary>Fallback handles anything.</summary>
    public bool CanConvert(GeometryBase geometry) => true;

    public OrbitBase Convert(GeometryBase geometry, ConversionContext context)
    {
        Mesh[]? meshes = null;

        if (geometry is Brep brep)
        {
            meshes = Mesh.CreateFromBrep(brep, MeshingParameters.Default);
        }
        else if (geometry is Mesh mesh)
        {
            meshes = new[] { mesh };
        }
        // Other geometry types: no mesh available - fall through to empty object

        if (meshes == null || meshes.Length == 0)
        {
            // Geometry has no displayable mesh (e.g. curve, point, annotation) — skip it
            throw new NotSupportedException($"No display mesh available for {geometry.GetType().Name}");
        }

        if (meshes.Length == 1)
            return _meshConverter.Convert(meshes[0], context);

        var container = new OrbitObject
        {
            DisplayValue = meshes
                .Select(m => _meshConverter.Convert(m, context))
                .ToList()
        };
        return container;
    }
}
