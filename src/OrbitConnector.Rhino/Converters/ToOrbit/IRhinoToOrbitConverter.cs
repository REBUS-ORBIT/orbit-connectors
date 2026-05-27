using Rhino.Geometry;
using Orbit.Objects.Base;

namespace OrbitConnector.Rhino.Converters.ToOrbit;

/// <summary>
/// Converts a Rhino geometry object to an ORBIT object.
/// Each geometry type has a dedicated converter; the dispatcher selects the right one.
/// </summary>
public interface IRhinoToOrbitConverter
{
    /// <summary>Returns true if this converter can handle the given geometry type.</summary>
    bool CanConvert(GeometryBase geometry);

    /// <summary>Convert the geometry to an ORBIT object.</summary>
    OrbitBase Convert(GeometryBase geometry, ConversionContext context);
}
