using Rhino.Geometry;
using Orbit.Objects.Base;
using OM = Orbit.Objects.Geometry;
using OrbitInterval = Orbit.Objects.Primitives.Interval;

namespace OrbitConnector.Rhino.Converters.ToOrbit;

public class RhinoPolylineConverter : IRhinoToOrbitConverter
{
    public bool CanConvert(GeometryBase geometry) =>
        geometry is PolylineCurve;

    public OrbitBase Convert(GeometryBase geometry, ConversionContext context)
    {
        var polylineCurve = (PolylineCurve)geometry;
        polylineCurve.TryGetPolyline(out var rhinoPoly);

        var value = new List<double>(rhinoPoly.Count * 3);
        foreach (var pt in rhinoPoly)
        {
            value.Add(pt.X);
            value.Add(pt.Y);
            value.Add(pt.Z);
        }

        return new OM.Polyline
        {
            Value  = value,
            Closed = rhinoPoly.IsClosed,
            Units  = context.Units,
            Domain = new OrbitInterval(polylineCurve.Domain.T0, polylineCurve.Domain.T1)
        };
    }
}
