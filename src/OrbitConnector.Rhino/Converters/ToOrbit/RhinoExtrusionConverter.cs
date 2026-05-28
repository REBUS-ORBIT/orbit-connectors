using Rhino.Geometry;
using Orbit.Objects.Base;

namespace OrbitConnector.Rhino.Converters.ToOrbit;

/// <summary>
/// Rhino's <see cref="Extrusion"/> is a separate native geometry type from
/// <see cref="Brep"/>. It encodes more compactly inside a <c>.3dm</c> file
/// than the equivalent Brep, so we wrap it directly (preserving the native
/// Extrusion form on round-trip) and only use <c>.ToBrep()</c> to generate
/// the display meshes.
/// </summary>
public class RhinoExtrusionConverter : IRhinoToOrbitConverter
{
    private readonly RhinoMeshConverter _meshConverter = new();

    public bool CanConvert(GeometryBase geometry) => geometry is Extrusion;

    public OrbitBase Convert(GeometryBase geometry, ConversionContext context)
    {
        var extrusion = (Extrusion)geometry;
        var brepForMesh = extrusion.ToBrep();
        return RhinoBrepConverter.BuildWrapper(
            geometry: extrusion,
            type: "Extrusion",
            context,
            _meshConverter,
            brepForDisplay: brepForMesh);
    }
}
