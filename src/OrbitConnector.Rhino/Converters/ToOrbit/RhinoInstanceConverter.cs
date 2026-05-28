using Rhino.DocObjects;
using Rhino.Geometry;
using Orbit.Objects.Base;
using OM = Orbit.Objects.Geometry;

namespace OrbitConnector.Rhino.Converters.ToOrbit;

/// <summary>
/// Converts a Rhino block placement (<see cref="InstanceReferenceGeometry"/> /
/// <see cref="InstanceObject"/>) to <see cref="OM.Instance"/>. The instance's
/// <see cref="OM.Instance.Elements"/> carries one entry per definition member
/// (pre-transformed into the placement) so the viewer can expand the block in
/// the layer-tree sidebar and the user sees every Brep / SubD / Surface that
/// makes up the block.
/// </summary>
public class RhinoInstanceConverter : IRhinoToOrbitConverter
{
    private readonly RhinoMeshConverter      _meshConverter      = new();
    private readonly RhinoBrepConverter      _brepConverter      = new();
    private readonly RhinoExtrusionConverter _extrusionConverter = new();
    private readonly RhinoSubDConverter      _subdConverter      = new();
    private readonly RhinoSurfaceConverter   _surfaceConverter   = new();
    private readonly RhinoCurveConverter     _curveConverter     = new();
    private readonly RhinoPointConverter     _pointConverter     = new();

    public bool CanConvert(GeometryBase geometry) => geometry is InstanceReferenceGeometry;

    public OrbitBase Convert(GeometryBase geometry, ConversionContext context)
    {
        if (context.CurrentObject is not InstanceObject instObj)
            throw new InvalidOperationException(
                "Block instance conversion requires the parent InstanceObject on the context.");

        var def = instObj.InstanceDefinition
            ?? throw new InvalidOperationException("Block instance has no definition.");

        var defAppId = def.Id.ToString();

        // Build the per-member element list (transformed Brep/Extrusion/SubD
        // wrappers). Each one renders its own native + display mesh and
        // surfaces in the viewer's tree as a child of the instance — so the
        // user can expand "Block 01" in the Block layer and see every Brep
        // that makes up the placement. This is the user-visible structure
        // shipped in the wire; round-trip (recreating Rhino InstanceDefinitions
        // on receive) still needs a Speckle-compatible InstanceDefinitionProxy
        // that the viewer treats as template content. Until that's specced we
        // ship transformed members so each placement is self-contained.
        var elements = BuildTransformedElements(instObj, context);

        if (elements.Count == 0)
            throw new InvalidOperationException("Block instance produced no displayable members.");

        // Display name shown in the viewer tree — prefer the Rhino object's
        // name (set by the user), fall back to the definition's name
        // (typical for unnamed placements), then a generic "Block".
        var name = instObj.Name is { Length: > 0 } objName
            ? objName
            : def.Name is { Length: > 0 } defName
                ? defName
                : "Block";

        return new OM.Instance
        {
            Name         = name,
            DefinitionId = defAppId,
            Transform    = ToOrbitTransform(instObj.InstanceXform, context.Units),
            Units        = context.Units,
            // No DisplayValue — the elements carry the renderable geometry.
            // Setting both would double-render the block (placement meshes
            // from displayValue PLUS per-member meshes from elements).
            Elements     = elements,
        };
    }

    /// <summary>
    /// Walk the block's <see cref="InstanceDefinition"/> members, transform a
    /// copy of each geometry into the instance's placement, and convert it via
    /// the appropriate native-Rhino wrapper (Brep / Extrusion / SubD / Surface /
    /// Mesh / Curve / Point). Each member arrives as an <see cref="OrbitBase"/>
    /// ready to be attached to <see cref="OM.Instance.Elements"/>.
    /// </summary>
    private List<OrbitBase> BuildTransformedElements(
        InstanceObject instObj, ConversionContext context)
    {
        var result = new List<OrbitBase>();
        var def = instObj.InstanceDefinition;
        if (def == null) return result;

        var members = def.GetObjects();
        if (members == null) return result;

        var xform = instObj.InstanceXform;
        var saved = context.CurrentObject;

        try
        {
            foreach (var sub in members)
            {
                if (sub?.Geometry == null) continue;
                context.CurrentObject = sub;

                // Duplicate-and-transform so we don't mutate the definition's
                // geometry — block definitions are shared template data and
                // multiple instances can reference the same members.
                var src = sub.Geometry.Duplicate() as GeometryBase;
                if (src == null) continue;
                src.Transform(xform);

                OrbitBase? converted = src switch
                {
                    Brep brep        => _brepConverter.Convert(brep, context),
                    Extrusion ext    => _extrusionConverter.Convert(ext, context),
                    SubD subd        => _subdConverter.Convert(subd, context),
                    Surface srf      => _surfaceConverter.Convert(srf, context),
                    Mesh mesh        => _meshConverter.Convert(mesh, context),
                    Curve curve      => _curveConverter.Convert(curve, context),
                    Point pt         => _pointConverter.Convert(pt, context),
                    _                => null,
                };

                if (converted != null)
                {
                    converted.ApplicationId = sub.Id.ToString();
                    result.Add(converted);
                }
            }
        }
        finally
        {
            context.CurrentObject = saved;
        }

        return result;
    }

    /// <summary>Speckle column-major 4×4 from Rhino transform.</summary>
    private static Orbit.Objects.Primitives.Transform ToOrbitTransform(
        global::Rhino.Geometry.Transform xform, string? units) => new()
    {
        Units = units,
        Matrix = new[]
        {
            xform.M00, xform.M10, xform.M20, xform.M30,
            xform.M01, xform.M11, xform.M21, xform.M31,
            xform.M02, xform.M12, xform.M22, xform.M32,
            xform.M03, xform.M13, xform.M23, xform.M33,
        },
    };
}
