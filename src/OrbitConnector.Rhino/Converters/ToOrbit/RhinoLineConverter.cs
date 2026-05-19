using Rhino.Geometry;
using Orbit.Objects.Base;
using OM = Orbit.Objects.Geometry;
using OrbitInterval = Orbit.Objects.Primitives.Interval;

namespace OrbitConnector.Rhino.Converters.ToOrbit;

public class RhinoLineConverter : IRhinoToOrbitConverter
{
    public bool CanConvert(GeometryBase geometry) =>
        geometry is LineCurve;

    public OrbitBase Convert(GeometryBase geometry, ConversionContext context)
    {
        var lineCurve = (LineCurve)geometry;
        var rhinoLine = lineCurve.Line;

        return new OM.Line
        {
            Start = new OM.Point(rhinoLine.From.X, rhinoLine.From.Y, rhinoLine.From.Z, context.Units),
            End   = new OM.Point(rhinoLine.To.X, rhinoLine.To.Y, rhinoLine.To.Z, context.Units),
            Units = context.Units,
            Domain = new OrbitInterval(lineCurve.Domain.T0, lineCurve.Domain.T1)
        };
    }
}
