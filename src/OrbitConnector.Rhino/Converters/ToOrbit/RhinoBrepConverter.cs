using Rhino.Geometry;
using Orbit.Objects.Base;
using OM = Orbit.Objects.Geometry;

namespace OrbitConnector.Rhino.Converters.ToOrbit;

/// <summary>
/// Converts Rhino Brep to ORBIT Brep.
/// Always includes a display mesh fallback for viewers that cannot handle native Brep.
/// </summary>
public class RhinoBrepConverter : IRhinoToOrbitConverter
{
    private readonly RhinoMeshConverter _meshConverter = new();

    public bool CanConvert(GeometryBase geometry) => geometry is Brep;

    public OrbitBase Convert(GeometryBase geometry, ConversionContext context)
    {
        var rhinoBrep = (Brep)geometry;

        var orbitBrep = new OM.Brep
        {
            Units      = context.Units,
            Provenance = "Rhino8",
        };

        // Display mesh fallback — always present
        var meshParams = MeshingParameters.Default;
        var meshes = Mesh.CreateFromBrep(rhinoBrep, meshParams);
        if (meshes?.Length > 0)
        {
            orbitBrep.DisplayValue = meshes
                .Select(m => (OM.Mesh)_meshConverter.Convert(m, context))
                .ToList();
        }

        // NOTE: Full surface/curve data encoding is reserved for Phase 2.
        // For Phase 1, the display mesh ensures the geometry is visible in the viewer.

        return orbitBrep;
    }
}
