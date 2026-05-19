using Rhino.Geometry;
using Orbit.Objects.Base;
using OM = Orbit.Objects.Geometry;
using OrbitInterval = Orbit.Objects.Primitives.Interval;

namespace OrbitConnector.Rhino.Converters.ToOrbit;

public class RhinoNurbsCurveConverter : IRhinoToOrbitConverter
{
    public bool CanConvert(GeometryBase geometry) =>
        geometry is NurbsCurve;

    public OrbitBase Convert(GeometryBase geometry, ConversionContext context)
    {
        var rhinoCurve = (NurbsCurve)geometry;

        var points = new List<double>(rhinoCurve.Points.Count * 3);
        var weights = new List<double>(rhinoCurve.Points.Count);

        foreach (var cp in rhinoCurve.Points)
        {
            points.Add(cp.Location.X);
            points.Add(cp.Location.Y);
            points.Add(cp.Location.Z);
            weights.Add(cp.Weight);
        }

        var knots = new List<double>(rhinoCurve.Knots.Count);
        foreach (var k in rhinoCurve.Knots)
            knots.Add(k);

        var nurbsCurve = new OM.NurbsCurve
        {
            Degree   = rhinoCurve.Degree,
            Periodic = rhinoCurve.IsPeriodic,
            Rational = rhinoCurve.IsRational,
            Closed   = rhinoCurve.IsClosed,
            Points   = points,
            Weights  = weights,
            Knots    = knots,
            Domain   = new OrbitInterval(rhinoCurve.Domain.T0, rhinoCurve.Domain.T1),
            Units    = context.Units
        };

        nurbsCurve.DisplayValue = RhinoArcConverter.ToPolylineDisplayValue(rhinoCurve, context);

        return nurbsCurve;
    }
}
