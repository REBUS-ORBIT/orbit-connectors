using Rhino.Geometry;
using Orbit.Objects.Base;
using OM = Orbit.Objects.Geometry;
using OrbitInterval = Orbit.Objects.Primitives.Interval;
using OrbitVector3d = Orbit.Objects.Primitives.Vector3d;

namespace OrbitConnector.Rhino.Converters.ToOrbit;

public class RhinoArcConverter : IRhinoToOrbitConverter
{
    public bool CanConvert(GeometryBase geometry) =>
        geometry is ArcCurve;

    public OrbitBase Convert(GeometryBase geometry, ConversionContext context)
    {
        var arcCurve = (ArcCurve)geometry;
        var rhinoArc = arcCurve.Arc;

        var orbitArc = new OM.Arc
        {
            Radius       = rhinoArc.Radius,
            StartAngle   = rhinoArc.StartAngle,
            EndAngle     = rhinoArc.EndAngle,
            AngleRadians = rhinoArc.Angle,
            Plane        = ConvertPlane(rhinoArc.Plane, context),
            Units        = context.Units,
            Domain       = new OrbitInterval(arcCurve.Domain.T0, arcCurve.Domain.T1)
        };

        orbitArc.DisplayValue = ToPolylineDisplayValue(arcCurve, context);

        return orbitArc;
    }

    internal static OM.Plane ConvertPlane(Plane rhinoPlane, ConversionContext context)
    {
        return new OM.Plane
        {
            Origin = new OM.Point(rhinoPlane.Origin.X, rhinoPlane.Origin.Y, rhinoPlane.Origin.Z, context.Units),
            Normal = new OrbitVector3d(rhinoPlane.Normal.X, rhinoPlane.Normal.Y, rhinoPlane.Normal.Z),
            Xdir   = new OrbitVector3d(rhinoPlane.XAxis.X, rhinoPlane.XAxis.Y, rhinoPlane.XAxis.Z),
            Ydir   = new OrbitVector3d(rhinoPlane.YAxis.X, rhinoPlane.YAxis.Y, rhinoPlane.YAxis.Z),
            Units  = context.Units
        };
    }

    internal static OM.Polyline ToPolylineDisplayValue(Curve curve, ConversionContext context)
    {
        var polyline = curve.ToPolyline(0, 0, 0.1, 0, 0, 0, 0, 0, true);
        if (polyline == null) return new OM.Polyline { Value = new List<double>(), Units = context.Units };

        polyline.TryGetPolyline(out var pts);
        var value = new List<double>(pts.Count * 3);
        foreach (var pt in pts)
        {
            value.Add(pt.X);
            value.Add(pt.Y);
            value.Add(pt.Z);
        }

        return new OM.Polyline
        {
            Value  = value,
            Closed = pts.IsClosed,
            Units  = context.Units
        };
    }
}
