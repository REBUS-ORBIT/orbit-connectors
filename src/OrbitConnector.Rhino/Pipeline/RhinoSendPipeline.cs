using Rhino;
using Rhino.Geometry;
using Rhino.DocObjects;
using Orbit.Objects.Base;
using Orbit.Sdk.Serialisation;
using Orbit.Sdk.Transport;
using Orbit.Sdk.Api;
using OrbitConnector.Rhino.Converters;
using OrbitConnector.Rhino.Converters.ToOrbit;
using OrbitConnector.Rhino.Models;

namespace OrbitConnector.Rhino.Pipeline;

/// <summary>
/// Orchestrates the full send pipeline:
/// Extract → Convert → Assemble tree → Serialise → Transport → Create version
/// </summary>
public class RhinoSendPipeline
{
    private readonly List<IRhinoToOrbitConverter> _converters;
    private readonly RhinoFallbackConverter _fallback = new();
    private readonly OrbitSerializer _serialiser = new();

    public RhinoSendPipeline()
    {
        _converters = new List<IRhinoToOrbitConverter>
        {
            new RhinoMeshConverter(),
            new RhinoBrepConverter(),
            // TODO: add curve, point, instance, text converters as built
        };
    }

    /// <summary>
    /// Execute a full send. Returns the created version id.
    /// </summary>
    public async Task<string> SendAsync(
        ConnectorCard card,
        RhinoDoc doc,
        IOrbitTransport transport,
        OrbitClient client,
        IProgress<(string status, int percent)>? progress = null,
        CancellationToken ct = default)
    {
        progress?.Report(("Extracting objects…", 0));
        var context = new ConversionContext(doc);

        // 1. EXTRACT objects from document
        var rhinoObjects = ExtractObjects(card, doc);
        if (rhinoObjects.Count == 0)
            throw new InvalidOperationException("No objects to send.");

        progress?.Report(("Converting geometry…", 10));

        // 2. CONVERT — build layer tree
        var root = BuildObjectTree(rhinoObjects, doc, context, card);

        // 3. SERIALISE
        progress?.Report(("Serialising…", 40));
        var objectBatch = await _serialiser.SerialiseAsync(root, ct);

        // 4. DEDUP + TRANSPORT
        progress?.Report(("Uploading…", 50));
        var toUpload = new List<(string id, string json)>();
        foreach (var (id, json) in objectBatch)
        {
            if (!await transport.HasObjectAsync(id, ct))
                toUpload.Add((id, json));
        }

        int uploaded = 0;
        await transport.SaveObjectBatchAsync(toUpload,
            new Progress<int>(n =>
            {
                uploaded = n;
                var pct = 50 + (int)(n * 40.0 / Math.Max(toUpload.Count, 1));
                progress?.Report(($"Uploading… {n}/{toUpload.Count}", pct));
            }), ct);

        // 5. CREATE VERSION
        progress?.Report(("Creating version…", 92));
        var version = await client.CreateVersionAsync(
            card.ProjectId!, card.ModelId!, root.Id!,
            message: $"Sent from ORBIT Rhino Connector",
            sourceApplication: "OrbitRhino", ct);

        progress?.Report(("Done", 100));
        return version.Id!;
    }

    private List<RhinoObject> ExtractObjects(ConnectorCard card, RhinoDoc doc)
    {
        return card.LayerMode switch
        {
            LayerMode.All => doc.Objects
                .Where(o => o.IsNormal && o.Geometry != null)
                .ToList(),
            LayerMode.ByLayer => doc.Objects
                .Where(o => o.IsNormal && o.Geometry != null &&
                    card.IncludedLayers.Contains(doc.Layers[o.Attributes.LayerIndex].FullPath))
                .ToList(),
            LayerMode.Selection => doc.Objects
                .Where(o => o.IsSelected(false) == 1 && o.Geometry != null)
                .ToList(),
            _ => new List<RhinoObject>()
        };
    }

    private OrbitObject BuildObjectTree(
        List<RhinoObject> rhinoObjects, RhinoDoc doc,
        ConversionContext context, ConnectorCard card)
    {
        var root = new OrbitObject
        {
            Name = card.ProjectName ?? "ORBIT Send",
            SourceApplication = "OrbitRhino",
            Units = context.Units,
            Elements = new List<OrbitBase>()
        };

        // Group by layer
        var byLayer = rhinoObjects.GroupBy(o => o.Attributes.LayerIndex);
        foreach (var group in byLayer)
        {
            var layer = doc.Layers[group.Key];
            var layerCollection = new OrbitObject
            {
                Name = layer.FullPath,
                Elements = new List<OrbitBase>()
            };

            foreach (var obj in group)
            {
                var converted = ConvertWithFallback(obj.Geometry!, context);
                if (converted == null) continue;
                converted.ApplicationId = obj.Id.ToString();
                layerCollection.Elements.Add(converted);
            }

            root.Elements.Add(layerCollection);
        }

        // Attach proxies at root
        root["renderMaterialProxies"] = context.MaterialProxies;
        root["colorProxies"]          = context.ColorProxies;
        root["groupProxies"]          = context.GroupProxies;
        root["definitionProxies"]     = context.DefinitionProxies;

        return root;
    }

    private OrbitBase? ConvertWithFallback(GeometryBase geometry, ConversionContext context)
    {
        try
        {
            var converter = _converters.FirstOrDefault(c => c.CanConvert(geometry));
            return converter != null
                ? converter.Convert(geometry, context)
                : _fallback.Convert(geometry, context);
        }
        catch
        {
            // If primary converter throws, try fallback
            try { return _fallback.Convert(geometry, context); }
            catch { return null; }
        }
    }
}
