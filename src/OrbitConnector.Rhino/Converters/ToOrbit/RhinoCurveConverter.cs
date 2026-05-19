using Rhino.Geometry;
using Orbit.Objects.Base;
using OM = Orbit.Objects.Geometry;

namespace OrbitConnector.Rhino.Converters.ToOrbit;

/// <summary>
/// Dispatches curve conversion to the appropriate sub-converter based on curve type.
/// Handles LineCurve, PolylineCurve, ArcCurve (including circles), NurbsCurve, and PolyCurve.
/// Unknown curve types are converted to NurbsCurve via ToNurbsCurve().
/// </summary>
public class RhinoCurveConverter : IRhinoToOrbitConverter
{
    private readonly RhinoLineConverter _lineConverter = new();
    private readonly RhinoPolylineConverter _polylineConverter = new();
    private readonly RhinoCircleConverter _circleConverter = new();
    private readonly RhinoArcConverter _arcConverter = new();
    private readonly RhinoNurbsCurveConverter _nurbsConverter = new();

    public bool CanConvert(GeometryBase geometry) => geometry is Curve;

    public OrbitBase Convert(GeometryBase geometry, ConversionContext context)
    {
        var curve = (Curve)geometry;
        return ConvertCurve(curve, context);
    }

    internal OrbitBase ConvertCurve(Curve curve, ConversionContext context)
    {
        switch (curve)
        {
            case LineCurve lineCurve:
                return _lineConverter.Convert(lineCurve, context);

            case PolylineCurve polylineCurve:
                return _polylineConverter.Convert(polylineCurve, context);

            case ArcCurve arcCurve when arcCurve.IsCompleteCircle:
                return _circleConverter.Convert(arcCurve, context);

            case ArcCurve arcCurve:
                return _arcConverter.Convert(arcCurve, context);

            case PolyCurve polyCurve:
                return ConvertPolyCurve(polyCurve, context);

            case NurbsCurve nurbsCurve:
                return _nurbsConverter.Convert(nurbsCurve, context);

            default:
                var asNurbs = curve.ToNurbsCurve();
                if (asNurbs != null)
                    return _nurbsConverter.Convert(asNurbs, context);
                return new OrbitBase { ApplicationId = curve.GetHashCode().ToString() };
        }
    }

    private OrbitBase ConvertPolyCurve(PolyCurve polyCurve, ConversionContext context)
    {
        var segments = new List<OrbitBase>();
        for (int i = 0; i < polyCurve.SegmentCount; i++)
        {
            var segment = polyCurve.SegmentCurve(i);
            segments.Add(ConvertCurve(segment, context));
        }

        var result = new OM.PolyCurve
        {
            Segments = segments,
            Closed   = polyCurve.IsClosed,
            Units    = context.Units,
            Domain   = new Orbit.Objects.Primitives.Interval(polyCurve.Domain.T0, polyCurve.Domain.T1)
        };

        result.DisplayValue = RhinoArcConverter.ToPolylineDisplayValue(polyCurve, context);

        return result;
    }
}
