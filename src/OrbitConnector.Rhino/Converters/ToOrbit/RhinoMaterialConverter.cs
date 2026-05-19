using Rhino.DocObjects;
using Orbit.Objects.Proxies;

namespace OrbitConnector.Rhino.Converters.ToOrbit;

/// <summary>
/// Extracts Rhino render materials and registers them as RenderMaterialProxy objects.
/// Called per-object during the send pipeline to build the material proxy list.
/// </summary>
public static class RhinoMaterialConverter
{
    /// <summary>
    /// Register the material for a Rhino object if it hasn't been registered yet.
    /// Returns immediately if the object uses the default material.
    /// </summary>
    public static void RegisterMaterial(
        RhinoObject rhinoObject,
        string objectApplicationId,
        ConversionContext context)
    {
        var materialIndex = rhinoObject.Attributes.MaterialIndex;

        if (rhinoObject.Attributes.MaterialSource == ObjectMaterialSource.MaterialFromLayer)
        {
            var layer = context.Doc.Layers[rhinoObject.Attributes.LayerIndex];
            materialIndex = layer.RenderMaterialIndex;
        }

        if (materialIndex < 0) return;

        var material = context.Doc.Materials[materialIndex];
        if (material == null) return;

        if (context.RegisteredMaterials.TryGetValue(materialIndex, out var existingProxyId))
        {
            var existing = context.MaterialProxies.Find(p => p.ApplicationId == existingProxyId);
            existing?.ObjectIds?.Add(objectApplicationId);
            return;
        }

        var proxyId = material.Id.ToString();
        context.RegisteredMaterials[materialIndex] = proxyId;

        var proxy = new RenderMaterialProxy
        {
            ApplicationId = proxyId,
            Value = new RenderMaterial
            {
                Name      = material.Name ?? $"Material-{materialIndex}",
                Opacity   = 1.0 - material.Transparency,
                Metalness = material.Reflectivity,
                Roughness = 1.0 - material.ReflectionGlossiness,
                Diffuse   = material.DiffuseColor.ToArgb(),
                Emissive  = material.EmissionColor.ToArgb()
            },
            ObjectIds = new List<string> { objectApplicationId }
        };

        context.MaterialProxies.Add(proxy);
    }
}
