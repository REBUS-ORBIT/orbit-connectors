using Rhino.Geometry;
using Orbit.Objects.Base;
using OM = Orbit.Objects.Geometry;

namespace OrbitConnector.Rhino.Converters.ToOrbit;

/// <summary>
/// Converts a Rhino <see cref="SubD"/> using the native-Rhino round-trip
/// wrapper. The full SubD topology (vertices, edges, faces, smoothness) is
/// preserved in the embedded <c>.3dm</c> bytes so receivers in Rhino8+ can
/// recover a real SubD object. For the viewer (which has no native SubD
/// type) a tessellated display mesh is attached.
/// </summary>
public class RhinoSubDConverter : IRhinoToOrbitConverter
{
    private readonly RhinoMeshConverter _meshConverter = new();

    public bool CanConvert(GeometryBase geometry) => geometry is SubD;

    public OrbitBase Convert(GeometryBase geometry, ConversionContext context)
    {
        var subd = (SubD)geometry;

        // Build the wrapper without auto-extracted Brep display meshes — SubD's
        // ToBrep would lose the crease topology. We'll tessellate the SubD
        // directly into a single display mesh.
        var wrapper = RhinoBrepConverter.BuildWrapper(
            geometry: subd,
            type: "SubD",
            context,
            _meshConverter,
            brepForDisplay: null);

        var displayMesh = Mesh.CreateFromSubD(subd, 4);
        if (displayMesh != null)
        {
            wrapper.DisplayValue = new List<OM.Mesh>
            {
                (OM.Mesh)_meshConverter.Convert(displayMesh, context),
            };
        }

        return wrapper;
    }
}
