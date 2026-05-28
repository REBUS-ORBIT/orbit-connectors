using Rhino.Geometry;
using Orbit.Objects.Base;
using OPoint = Orbit.Objects.Geometry.Point;

namespace OrbitConnector.Rhino.Converters.ToOrbit;

/// <summary>
/// Converts a Rhino <see cref="Point"/> object (the geometric primitive,
/// not <see cref="Point3d"/>) to an ORBIT <see cref="OPoint"/>.
/// </summary>
public class RhinoPointConverter : IRhinoToOrbitConverter
{
    public bool CanConvert(GeometryBase geometry) => geometry is Point;

    public OrbitBase Convert(GeometryBase geometry, ConversionContext context)
    {
        var rp = (Point)geometry;
        return new OPoint(rp.Location.X, rp.Location.Y, rp.Location.Z, context.Units);
    }
}
