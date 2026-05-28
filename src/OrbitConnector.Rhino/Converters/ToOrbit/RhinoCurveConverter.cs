using Rhino.Geometry;
using Orbit.Objects.Base;
using OM       = Orbit.Objects.Geometry;
using OPoint   = Orbit.Objects.Geometry.Point;
using OInterval = Orbit.Objects.Primitives.Interval;
using OVector3d = Orbit.Objects.Primitives.Vector3d;

namespace OrbitConnector.Rhino.Converters.ToOrbit;

/// <summary>
/// Dispatches a Rhino curve (any <see cref="Curve"/> subclass) to the most
/// appropriate ORBIT geometry type.
///
/// Dispatch order:
///   1. <see cref="LineCurve"/>      → <see cref="OM.Line"/>
///   2. <see cref="ArcCurve"/>       → <see cref="OM.Arc"/> or
///                                     <see cref="OM.Circle"/>
///   3. <see cref="PolylineCurve"/>  → <see cref="OM.Polyline"/>
///   4. <see cref="PolyCurve"/>      → <see cref="OM.PolyCurve"/>
///   5. anything else (NURBS, etc.)  → <see cref="OM.NurbsCurve"/>
///
/// All curve outputs carry a polyline <c>displayValue</c> so even viewers
/// that don't render NURBS get a tessellated representation.
/// </summary>
public class RhinoCurveConverter : IRhinoToOrbitConverter
{
    public bool CanConvert(GeometryBase geometry) => geometry is Curve;

    public OrbitBase Convert(GeometryBase geometry, ConversionContext context)
    {
        var curve = (Curve)geometry;
        return ConvertCurve(curve, context);
    }

    public OrbitBase ConvertCurve(Curve curve, ConversionContext context)
    {
        return curve switch
        {
            LineCurve lc       => ToLine(lc, context),
            ArcCurve ac        => ToArcOrCircle(ac, context),
            PolylineCurve plc  => ToPolyline(plc, context),
            PolyCurve pc       => ToPolyCurve(pc, context),
            NurbsCurve nc      => ToNurbsCurve(nc, context),
            _                  => ToNurbsCurveFromAny(curve, context)
        };
    }

    private static OPoint ToOrbitPoint(Point3d p, string units)
        => new(p.X, p.Y, p.Z, units);

    private OM.Line ToLine(LineCurve lc, ConversionContext context) => new()
    {
        Start  = ToOrbitPoint(lc.PointAtStart, context.Units),
        End    = ToOrbitPoint(lc.PointAtEnd,   context.Units),
        Domain = new OInterval(lc.Domain.T0, lc.Domain.T1),
        Units  = context.Units,
    };

    private OrbitBase ToArcOrCircle(ArcCurve ac, ConversionContext context)
    {
        if (ac.IsCompleteCircle)
        {
            ac.TryGetCircle(out var circle);
            return new OM.Circle
            {
                Radius       = circle.Radius,
                Plane        = ToOrbitPlane(circle.Plane, context.Units),
                Domain       = new OInterval(ac.Domain.T0, ac.Domain.T1),
                Units        = context.Units,
                DisplayValue = TessellateToPolyline(ac, context.Units),
            };
        }

        var arc = ac.Arc;
        return new OM.Arc
        {
            Radius       = arc.Radius,
            StartAngle   = arc.StartAngle,
            EndAngle     = arc.EndAngle,
            AngleRadians = arc.Angle,
            Plane        = ToOrbitPlane(arc.Plane, context.Units),
            Domain       = new OInterval(ac.Domain.T0, ac.Domain.T1),
            Units        = context.Units,
            DisplayValue = TessellateToPolyline(ac, context.Units),
        };
    }

    private OM.Polyline ToPolyline(PolylineCurve plc, ConversionContext context)
    {
        var points = new List<double>(plc.PointCount * 3);
        for (int i = 0; i < plc.PointCount; i++)
        {
            var p = plc.Point(i);
            points.Add(p.X); points.Add(p.Y); points.Add(p.Z);
        }
        return new OM.Polyline
        {
            Value  = points,
            Closed = plc.IsClosed,
            Domain = new OInterval(plc.Domain.T0, plc.Domain.T1),
            Units  = context.Units,
        };
    }

    private OM.PolyCurve ToPolyCurve(PolyCurve pc, ConversionContext context)
    {
        var segments = new List<OrbitBase>();
        for (int i = 0; i < pc.SegmentCount; i++)
        {
            var seg = pc.SegmentCurve(i);
            if (seg != null)
                segments.Add(ConvertCurve(seg, context));
        }

        return new OM.PolyCurve
        {
            Segments     = segments,
            Closed       = pc.IsClosed,
            Domain       = new OInterval(pc.Domain.T0, pc.Domain.T1),
            Units        = context.Units,
            DisplayValue = TessellateToPolyline(pc, context.Units),
        };
    }

    private OM.NurbsCurve ToNurbsCurve(NurbsCurve nc, ConversionContext context)
    {
        var ctrlPoints = new List<double>(nc.Points.Count * 3);
        var weights    = new List<double>(nc.Points.Count);
        foreach (var cp in nc.Points)
        {
            var loc = cp.Location;
            ctrlPoints.Add(loc.X); ctrlPoints.Add(loc.Y); ctrlPoints.Add(loc.Z);
            weights.Add(cp.Weight);
        }

        var knots = new List<double>(nc.Knots.Count);
        foreach (var k in nc.Knots) knots.Add(k);

        return new OM.NurbsCurve
        {
            Degree       = nc.Degree,
            Periodic     = nc.IsPeriodic,
            Rational     = nc.IsRational,
            Closed       = nc.IsClosed,
            Points       = ctrlPoints,
            Weights      = weights,
            Knots        = knots,
            Domain       = new OInterval(nc.Domain.T0, nc.Domain.T1),
            Units        = context.Units,
            DisplayValue = TessellateToPolyline(nc, context.Units)
                           ?? UniformSample(nc, context.Units),
        };
    }

    private OM.NurbsCurve ToNurbsCurveFromAny(Curve curve, ConversionContext context)
    {
        var nc = curve.ToNurbsCurve();
        if (nc == null)
            throw new InvalidOperationException(
                $"Could not convert {curve.GetType().Name} to NurbsCurve.");
        return ToNurbsCurve(nc, context);
    }

    /// <summary>
    /// Tessellate any curve to a viewer-friendly <see cref="OM.Polyline"/>
    /// for the <c>displayValue</c> field. Uses Rhino's built-in adaptive
    /// polyline approximation; falls back to uniform parameter sampling
    /// when the adaptive call fails (e.g. on degenerate curves).
    /// </summary>
    private static OM.Polyline? TessellateToPolyline(Curve curve, string units)
    {
        try
        {
            var bbox = curve.GetBoundingBox(false);
            var tol  = Math.Max(0.001, bbox.Diagonal.Length * 0.001);

            // Curve.ToPolyline(...) returns a PolylineCurve. Both that and
            // Rhino.Geometry.Polyline expose PointCount + Point(i).
            var polyCurve = curve.ToPolyline(
                mainSegmentCount: 0,
                subSegmentCount: 0,
                maxAngleRadians: 0.05,
                maxChordLengthRatio: 1.0,
                maxAspectRatio: 0.0,
                tolerance: tol,
                minEdgeLength: 0.0,
                maxEdgeLength: 0.0,
                keepStartPoint: true);

            if (polyCurve == null || polyCurve.PointCount < 2)
                return UniformSample(curve, units);

            var pts = new List<double>(polyCurve.PointCount * 3);
            for (int i = 0; i < polyCurve.PointCount; i++)
            {
                var p = polyCurve.Point(i);
                pts.Add(p.X); pts.Add(p.Y); pts.Add(p.Z);
            }
            return new OM.Polyline
            {
                Value  = pts,
                Closed = curve.IsClosed,
                Domain = new OInterval(curve.Domain.T0, curve.Domain.T1),
                Units  = units,
            };
        }
        catch
        {
            return UniformSample(curve, units);
        }
    }

    /// <summary>Last-resort uniform parameter sampling.</summary>
    private static OM.Polyline UniformSample(Curve curve, string units)
    {
        const int samples = 64;
        var dom = curve.Domain;
        var pts = new List<double>(samples * 3);
        for (int i = 0; i < samples; i++)
        {
            var t = dom.T0 + (dom.T1 - dom.T0) * i / (samples - 1);
            var p = curve.PointAt(t);
            pts.Add(p.X); pts.Add(p.Y); pts.Add(p.Z);
        }
        return new OM.Polyline
        {
            Value  = pts,
            Closed = curve.IsClosed,
            Domain = new OInterval(dom.T0, dom.T1),
            Units  = units,
        };
    }

    private static OM.Plane ToOrbitPlane(Plane p, string units) => new()
    {
        Origin = ToOrbitPoint(p.Origin, units),
        Normal = new OVector3d(p.Normal.X, p.Normal.Y, p.Normal.Z),
        Xdir   = new OVector3d(p.XAxis.X,  p.XAxis.Y,  p.XAxis.Z),
        Ydir   = new OVector3d(p.YAxis.X,  p.YAxis.Y,  p.YAxis.Z),
        Units  = units,
    };
}
