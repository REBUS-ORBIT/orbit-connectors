using Rhino.Geometry;
using Orbit.Objects.Base;
using OM = Orbit.Objects.Geometry;
using Orbit.Objects.Proxies;
using OrbitTransform = Orbit.Objects.Primitives.Transform;

namespace OrbitConnector.Rhino.Converters.ToOrbit;

/// <summary>
/// Converts Rhino block instance references to ORBIT Instance + DefinitionProxy.
/// Each unique block definition is registered once in the ConversionContext; instances
/// reference the definition by its applicationId.
/// </summary>
public class RhinoInstanceConverter : IRhinoToOrbitConverter
{
    private readonly RhinoFallbackConverter _fallbackConverter = new();

    public bool CanConvert(GeometryBase geometry) => geometry is InstanceReferenceGeometry;

    public OrbitBase Convert(GeometryBase geometry, ConversionContext context)
    {
        var instanceRef = (InstanceReferenceGeometry)geometry;
        var xform = instanceRef.Xform;
        var idef = context.Doc.InstanceDefinitions.FindId(instanceRef.ParentIdefId);
        var defId = idef.Id.ToString();

        EnsureDefinitionRegistered(idef, defId, context);

        var orbitTransform = ConvertTransform(xform);

        var instance = new OM.Instance
        {
            DefinitionId = defId,
            Transform    = orbitTransform,
            Units        = context.Units
        };

        return instance;
    }

    private void EnsureDefinitionRegistered(
        global::Rhino.DocObjects.InstanceDefinition idef,
        string defId,
        ConversionContext context)
    {
        if (context.DefinitionProxies.Any(d => d.ApplicationId == defId))
            return;

        var objects = new List<OrbitBase>();
        foreach (var obj in idef.GetObjects())
        {
            if (obj.Geometry == null) continue;
            OrbitBase converted;
            try
            {
                converted = _fallbackConverter.Convert(obj.Geometry, context);
            }
            catch
            {
                continue;
            }
            converted.ApplicationId = obj.Id.ToString();
            objects.Add(converted);
        }

        var bbox = BoundingBox.Empty;
        foreach (var obj in idef.GetObjects())
        {
            if (obj.Geometry != null)
                bbox.Union(obj.Geometry.GetBoundingBox(true));
        }
        var basePoint = bbox.Center;

        var proxy = new DefinitionProxy
        {
            ApplicationId = defId,
            Name          = idef.Name,
            Objects       = objects,
            BasePoint     = new OM.Point(basePoint.X, basePoint.Y, basePoint.Z, context.Units),
            Units         = context.Units
        };

        context.DefinitionProxies.Add(proxy);
    }

    private static OrbitTransform ConvertTransform(global::Rhino.Geometry.Transform xform)
    {
        return new OrbitTransform
        {
            Matrix = new double[]
            {
                xform.M00, xform.M10, xform.M20, xform.M30,
                xform.M01, xform.M11, xform.M21, xform.M31,
                xform.M02, xform.M12, xform.M22, xform.M32,
                xform.M03, xform.M13, xform.M23, xform.M33,
            }
        };
    }
}
