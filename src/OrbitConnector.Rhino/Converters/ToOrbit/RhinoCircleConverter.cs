using Rhino.Geometry;
using Orbit.Objects.Base;
using OM = Orbit.Objects.Geometry;
using OrbitInterval = Orbit.Objects.Primitives.Interval;

namespace OrbitConnector.Rhino.Converters.ToOrbit;

public class RhinoCircleConverter : IRhinoToOrbitConverter
{
    public bool CanConvert(GeometryBase geometry) =>
        geometry is ArcCurve arc && arc.IsCompleteCircle;

    public OrbitBase Convert(GeometryBase geometry, ConversionContext context)
    {
        var arcCurve = (ArcCurve)geometry;
        arcCurve.TryGetCircle(out var rhinoCircle);

        var circle = new OM.Circle
        {
            Radius = rhinoCircle.Radius,
            Plane  = RhinoArcConverter.ConvertPlane(rhinoCircle.Plane, context),
            Units  = context.Units,
            Domain = new OrbitInterval(arcCurve.Domain.T0, arcCurve.Domain.T1)
        };

        circle.DisplayValue = RhinoArcConverter.ToPolylineDisplayValue(arcCurve, context);

        return circle;
    }
}
