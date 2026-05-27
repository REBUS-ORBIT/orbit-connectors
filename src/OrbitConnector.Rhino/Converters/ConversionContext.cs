using Rhino;
using Orbit.Objects.Proxies;

namespace OrbitConnector.Rhino.Converters;

/// <summary>
/// Shared state passed through the conversion pipeline.
/// Holds the active document, units, and collected proxy data.
/// </summary>
public class ConversionContext
{
    public RhinoDoc Doc { get; }
    public string Units { get; }

    // Proxy collections - populated during send, consumed by bakers during receive
    public List<RenderMaterialProxy> MaterialProxies { get; } = new();
    public List<ColorProxy> ColorProxies { get; } = new();
    public List<GroupProxy> GroupProxies { get; } = new();
    public List<DefinitionProxy> DefinitionProxies { get; } = new();

    // Track which Rhino materials have already been registered (by index)
    public Dictionary<int, string> RegisteredMaterials { get; } = new();

    public ConversionContext(RhinoDoc doc)
    {
        Doc = doc;
        // UnitSystem is in scope via 'using Rhino;'
        Units = doc.ModelUnitSystem switch
        {
            UnitSystem.Millimeters => "mm",
            UnitSystem.Centimeters => "cm",
            UnitSystem.Meters      => "m",
            UnitSystem.Feet        => "ft",
            UnitSystem.Inches      => "in",
            _                      => "none"
        };
    }
}
