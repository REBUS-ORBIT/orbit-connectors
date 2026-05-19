using Rhino.Geometry;
using Orbit.Objects.Base;
using OM = Orbit.Objects.Geometry;

namespace OrbitConnector.Rhino.Converters.ToOrbit;

public class RhinoPointConverter : IRhinoToOrbitConverter
{
    public bool CanConvert(GeometryBase geometry) => geometry is Point;

    public OrbitBase Convert(GeometryBase geometry, ConversionContext context)
    {
        var rhinoPoint = (Point)geometry;
        var loc = rhinoPoint.Location;

        return new OM.Point(loc.X, loc.Y, loc.Z, context.Units);
    }
}
