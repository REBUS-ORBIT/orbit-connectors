using Rhino.Geometry;
using Orbit.Objects.Base;

namespace OrbitConnector.Rhino.Converters.ToOrbit;

/// <summary>
/// Converts a native Rhino <see cref="Surface"/> (untrimmed face) into a
/// <see cref="Orbit.Objects.Data.RhinoDataObject"/> wrapper. The original
/// surface is preserved natively in the embedded <c>.3dm</c> bytes; the
/// display meshes are produced from a Brep promotion of the surface so the
/// viewer can render it.
/// </summary>
public class RhinoSurfaceConverter : IRhinoToOrbitConverter
{
    private readonly RhinoMeshConverter _meshConverter = new();

    public bool CanConvert(GeometryBase geometry) => geometry is Surface;

    public OrbitBase Convert(GeometryBase geometry, ConversionContext context)
    {
        var surface = (Surface)geometry;
        var brepForMesh = surface.ToBrep();
        return RhinoBrepConverter.BuildWrapper(
            geometry: surface,
            type: "Surface",
            context,
            _meshConverter,
            brepForDisplay: brepForMesh);
    }
}
