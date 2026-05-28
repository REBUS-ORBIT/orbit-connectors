using Rhino;
using Rhino.Geometry;
using Rhino.DocObjects;
using Orbit.Objects.Base;
using Orbit.Objects.BuiltElements;
using Orbit.Sdk.Serialisation;
using OrbitPoint  = Orbit.Objects.Geometry.Point;
using OrbitVector = Orbit.Objects.Geometry.Vector;
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
        progress?.Report(($"Found {rhinoObjects.Count} objects…", 5));
        if (rhinoObjects.Count == 0)
            throw new InvalidOperationException(
                $"No objects to send. Layer mode: {card.LayerMode}, doc objects: {doc.Objects.Count}");

        progress?.Report(("Converting geometry…", 10));

        // 2. CONVERT — build layer tree
        var root = BuildObjectTree(rhinoObjects, doc, context, card);
        var totalConverted = root.Elements?.Sum(e => (e as OrbitObject)?.Elements?.Count ?? 0) ?? 0;
        progress?.Report(($"Converted {totalConverted} objects, serialising…", 35));

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
            message: "Sent from ORBIT Rhino Connector",
            sourceApplication: "OrbitRhino",
            totalChildrenCount: objectBatch.Count,
            ct);

        progress?.Report(("Done", 100));
        return version.Id!;
    }

    private List<RhinoObject> ExtractObjects(ConnectorCard card, RhinoDoc doc)
    {
        // Use ObjectEnumeratorSettings — the correct Rhino API for filtering doc objects
        var settings = new ObjectEnumeratorSettings()
        {
            NormalObjects     = true,
            LockedObjects     = false,
            HiddenObjects     = false,
            DeletedObjects    = false,
            IncludeLights     = false,
            IncludeGrips      = false,
        };

        var allNormal = doc.Objects.GetObjectList(settings)
                          .Where(o => o.Geometry != null)
                          .ToList();

        return card.LayerMode switch
        {
            LayerMode.All => allNormal,

            LayerMode.ByLayer => allNormal
                .Where(o => card.IncludedLayers.Contains(
                    doc.Layers[o.Attributes.LayerIndex].FullPath))
                .ToList(),

            // Selection: use the GUIDs snapshotted when the user set the selection filter
            LayerMode.Selection => card.SelectedObjectIds.Count > 0
                ? allNormal
                    .Where(o => card.SelectedObjectIds.Contains(o.Id.ToString()))
                    .ToList()
                : doc.Objects.GetSelectedObjects(false, false)
                    .Where(o => o.Geometry != null)
                    .ToList(),

            _ => new List<RhinoObject>()
        };
    }

    private OrbitObject BuildObjectTree(
        List<RhinoObject> rhinoObjects, RhinoDoc doc,
        ConversionContext context, ConnectorCard card)
    {
        // Root collection — must be CollectionType="model" to match the working Speckle reference
        // (the previous "root" value prevented the viewer from rendering the sidebar tree).
        var root = new OrbitObject
        {
            Name              = card.ModelName ?? card.ProjectName ?? "ORBIT Send",
            CollectionType    = "model",
            SourceApplication = "OrbitRhino",
            Units             = context.Units,
            Elements          = new List<OrbitBase>()
        };

        // Group by layer, build one Collection per layer
        var byLayer = rhinoObjects.GroupBy(o => o.Attributes.LayerIndex);
        foreach (var group in byLayer)
        {
            var layer = doc.Layers[group.Key];
            var layerColor = ArgbToUnsignedLong(layer.Color.ToArgb());
            var layerCollection = new OrbitObject
            {
                Name           = layer.FullPath,
                CollectionType = "layer",
                LayerPath      = layer.FullPath,
                LayerColor     = layerColor,
                Elements       = new List<OrbitBase>()
            };

            foreach (var obj in group)
            {
                var converted = ConvertWithFallback(obj.Geometry!, context);
                if (converted == null) continue;
                converted.ApplicationId = obj.Id.ToString();

                // Tag every leaf with its layer info so the viewer can colour it.
                // Done via the dynamic-properties indexer so this works for any OrbitBase
                // (Mesh, Brep, future curve/point types) without N specific setters.
                converted["layerPath"]   = layer.FullPath;
                converted["layerColor"]  = layerColor;
                converted["colorSource"] = "layer";

                layerCollection.Elements.Add(converted);
            }

            if (layerCollection.Elements.Count > 0)
                root.Elements.Add(layerCollection);
        }

        // Attach proxies at root (kept for future use by bake/receive)
        if (context.MaterialProxies.Count   > 0) root["renderMaterialProxies"] = context.MaterialProxies;
        if (context.ColorProxies.Count      > 0) root["colorProxies"]          = context.ColorProxies;
        if (context.GroupProxies.Count      > 0) root["groupProxies"]          = context.GroupProxies;
        if (context.DefinitionProxies.Count > 0) root["definitionProxies"]     = context.DefinitionProxies;

        // Named views — stored INLINE under `views` (no @ prefix). The full View3D objects
        // sit directly in the root JSON, matching the working Speckle reference.
        var views = ExtractNamedViews(doc, context.Units);
        if (views.Count > 0)
            root.Views = views;

        return root;
    }

    /// <summary>
    /// Extracts all named views from the Rhino document as <see cref="View3D"/> objects.
    /// Each view carries inline <c>origin</c>/<c>target</c> Points and <c>upDirection</c>/
    /// <c>forwardDirection</c> Vectors, matching the structure the Speckle/ORBIT viewer
    /// expects to populate its saved-views panel.
    /// </summary>
    private static List<View3D> ExtractNamedViews(RhinoDoc doc, string? units)
    {
        var views = new List<View3D>();
        foreach (var info in doc.NamedViews)
        {
            try
            {
                var vp = info.Viewport;

                // CameraLocation — world position of the camera.
                var camLoc = vp.CameraLocation;
                var origin = new OrbitPoint(camLoc.X, camLoc.Y, camLoc.Z, units);

                // Target = origin + (forward direction). Rhino's CameraZ points FROM target
                // TOWARD camera, so the look direction is -CameraZ.
                var camZ = vp.CameraZ;
                var targetPt = camLoc - camZ;
                var target = new OrbitPoint(targetPt.X, targetPt.Y, targetPt.Z, units);

                // Up vector (Rhino's CameraY).
                var camY = vp.CameraY;
                var up   = new OrbitVector(camY.X, camY.Y, camY.Z, units);

                // Forward vector (from camera to target). This is the field the previous
                // implementation was missing entirely.
                var forward = new OrbitVector(-camZ.X, -camZ.Y, -camZ.Z, units);

                double lens = vp.IsParallelProjection ? 0 : vp.Camera35mmLensLength;

                views.Add(new View3D
                {
                    Name             = info.Name,
                    Origin           = origin,
                    Target           = target,
                    UpDirection      = up,
                    ForwardDirection = forward,
                    IsOrthogonal     = vp.IsParallelProjection,
                    Lens             = lens,
                    Units            = units,
                });
            }
            catch
            {
                // Skip malformed views rather than aborting the whole send.
            }
        }
        return views;
    }

    /// <summary>
    /// Packs a signed 32-bit ARGB integer (as returned by <c>System.Drawing.Color.ToArgb</c>)
    /// into an unsigned long, matching the Speckle Python SDK convention. This avoids the
    /// sign-bit mismatch that would otherwise produce wrong colours in the viewer.
    /// </summary>
    private static long ArgbToUnsignedLong(int argb) => (long)(uint)argb;

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
