using Rhino.DocObjects;
using Orbit.Objects.Proxies;

namespace OrbitConnector.Rhino.Converters.ToOrbit;

/// <summary>
/// Extracts display colours from Rhino objects and registers them as ColorProxy objects.
/// Resolves the effective colour based on object/layer colour source.
/// </summary>
public static class RhinoColorConverter
{
    private static readonly Dictionary<int, string> _colorProxyMap = new();

    public static void RegisterColor(
        RhinoObject rhinoObject,
        string objectApplicationId,
        ConversionContext context)
    {
        var color = rhinoObject.Attributes.DrawColor(context.Doc);
        var argb = color.ToArgb();

        if (_colorProxyMap.TryGetValue(argb, out var existingId))
        {
            var existing = context.ColorProxies.Find(p => p.ApplicationId == existingId);
            existing?.ObjectIds?.Add(objectApplicationId);
            return;
        }

        var proxyId = $"color-{argb:X8}";
        _colorProxyMap[argb] = proxyId;

        var proxy = new ColorProxy
        {
            ApplicationId = proxyId,
            Value = argb,
            Name  = $"#{(argb & 0xFFFFFF):X6}",
            ObjectIds = new List<string> { objectApplicationId }
        };

        context.ColorProxies.Add(proxy);
    }

    /// <summary>Clear the colour cache between sends.</summary>
    public static void Reset() => _colorProxyMap.Clear();
}
