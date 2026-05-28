using Newtonsoft.Json.Linq;
using Rhino;
using Rhino.DocObjects;
using Orbit.Sdk.Api;
using Orbit.Sdk.Transport;
using OrbitConnector.Rhino.Converters.FromOrbit;
using OrbitConnector.Rhino.Models;

namespace OrbitConnector.Rhino.Pipeline;

/// <summary>
/// Orchestrates the full receive pipeline:
/// Fetch version → Download tree → Resolve references → Convert → Bake into RhinoDoc
/// </summary>
public class RhinoReceivePipeline
{
    /// <summary>
    /// Result summary returned on successful receive.
    /// </summary>
    public record ReceiveResult(int ObjectCount, int LayerCount, List<string> Warnings);

    private readonly OrbitToRhinoConverter _converter = new();

    /// <summary>
    /// Execute a full receive. Downloads the latest version (or <paramref name="pinnedVersionId"/>
    /// if set on the card) of the selected model and bakes all geometry into the active
    /// Rhino document.
    /// </summary>
    public async Task<ReceiveResult> ReceiveAsync(
        ConnectorCard card,
        ServerConfig config,
        RhinoDoc doc,
        OrbitClient client,
        string token,
        IProgress<(string status, int percent)>? progress = null,
        CancellationToken ct = default)
    {
        var serverUrl = card.ServerUrl(config);
        var projectId = card.ProjectId ?? throw new InvalidOperationException("Card has no project selected.");
        var modelId   = card.ModelId   ?? throw new InvalidOperationException("Card has no model selected.");

        // 1. RESOLVE VERSION
        progress?.Report(("Fetching version…", 5));
        var versions = await client.GetVersionsAsync(projectId, modelId, ct);
        if (versions.Count == 0)
            throw new InvalidOperationException("No versions found for this model.");

        // Use pinned version if set, otherwise take latest (versions are newest-first)
        var version = !string.IsNullOrEmpty(card.PinnedVersionId)
            ? versions.FirstOrDefault(v => v.Id == card.PinnedVersionId) ?? versions[0]
            : versions[0];

        var rootObjectId = version.ReferencedObject
            ?? throw new InvalidOperationException("Version has no referenced object.");

        // 2. DOWNLOAD ROOT OBJECT
        progress?.Report(("Downloading…", 10));
        using var transport = new ServerTransport(serverUrl, projectId, token);
        Func<string, Task<string?>> fetcher = id => transport.GetObjectAsync(id, ct);

        var rootJson = await fetcher(rootObjectId)
            ?? throw new InvalidOperationException($"Root object '{rootObjectId}' not found on server.");

        var rootObj = JObject.Parse(rootJson);

        // 3. WALK OBJECT TREE
        progress?.Report(("Walking object tree…", 20));

        var warnings   = new List<string>();
        int objCount   = 0;
        int layerCount = 0;

        // Close tree: { id → depth } — use closure count as a size hint
        var closureToken = rootObj["__closure"];
        var totalRefs = closureToken is JObject closureObj ? closureObj.Count : 0;
        int resolved  = 0;

        void ReportTree()
        {
            if (totalRefs > 0)
                progress?.Report(($"Receiving… {resolved}/{totalRefs}", 20 + resolved * 60 / totalRefs));
        }

        // Walk layer collections in root.elements
        var rootElements = rootObj["elements"] as JArray;
        if (rootElements == null || rootElements.Count == 0)
            throw new InvalidOperationException("The root object has no elements. Nothing to receive.");

        doc.BeginUndoRecord("ORBIT Receive");
        try
        {
            foreach (var layerToken in rootElements)
            {
                ct.ThrowIfCancellationRequested();
                var layerJson = await ResolveTokenAsync(layerToken, fetcher, ct);
                resolved++;
                if (layerJson == null) continue;

                ReportTree();

                var layerObj = JObject.Parse(layerJson);
                var layerPath = GetLayerPath(layerObj);
                var layerColor = GetLayerColor(layerObj);

                // Create or find the layer in Rhino
                int rhinoLayerIdx = EnsureLayer(doc, layerPath, layerColor);
                layerCount++;

                // Walk geometry objects in this layer
                var layerElements = layerObj["elements"] as JArray;
                if (layerElements == null) continue;

                foreach (var geoToken in layerElements)
                {
                    ct.ThrowIfCancellationRequested();
                    var geoJson = await ResolveTokenAsync(geoToken, fetcher, ct);
                    resolved++;
                    if (geoJson == null) continue;

                    ReportTree();

                    var geoObj = JObject.Parse(geoJson);

                    // For RhinoDataObject: the rawEncoding and displayValue are also
                    // detached — resolve rawEncoding if present so the converter can
                    // decode native .3dm bytes.
                    await ResolveRawEncodingAsync(geoObj, fetcher, ct);

                    var geometry = _converter.Convert(geoObj);
                    if (geometry == null)
                    {
                        var spType = geoObj["speckle_type"]?.Value<string>() ?? "unknown";
                        warnings.Add($"Skipped unsupported object type: {spType}");
                        continue;
                    }

                    var attrs = new ObjectAttributes
                    {
                        LayerIndex = rhinoLayerIdx,
                    };

                    // Best-effort: apply object colour from renderMaterial.diffuse
                    ApplyMaterialColor(geoObj, attrs);

                    doc.Objects.Add(geometry, attrs);
                    objCount++;
                }
            }
        }
        finally
        {
            doc.EndUndoRecord(doc.CurrentUndoRecordSerialNumber);
        }

        progress?.Report(($"Received {objCount} object(s)", 95));
        doc.Views.Redraw();

        return new ReceiveResult(objCount, layerCount, warnings);
    }

    // ── Reference resolution ──────────────────────────────────────────────────

    /// <summary>
    /// If the token is a detached reference stub (<c>{"referencedId": "...", "speckle_type": "reference"}</c>),
    /// fetches and returns the raw JSON string. If the token is an inline object, returns its JSON.
    /// </summary>
    private static async Task<string?> ResolveTokenAsync(
        JToken token,
        Func<string, Task<string?>> fetcher,
        CancellationToken ct)
    {
        if (token is not JObject obj) return null;
        ct.ThrowIfCancellationRequested();

        var refId = obj["referencedId"]?.Value<string>();
        if (!string.IsNullOrEmpty(refId))
            return await fetcher(refId);

        return obj.ToString(Newtonsoft.Json.Formatting.None);
    }

    /// <summary>
    /// For <c>RhinoDataObject</c>: the <c>rawEncoding</c> field is a detached reference.
    /// Resolve it in-place so <see cref="OrbitToRhinoConverter"/> can read its <c>contents</c>
    /// without needing access to the object store.
    /// </summary>
    private static async Task ResolveRawEncodingAsync(
        JObject obj,
        Func<string, Task<string?>> fetcher,
        CancellationToken ct)
    {
        if (obj["rawEncoding"] is not JObject rawRef) return;
        var refId = rawRef["referencedId"]?.Value<string>();
        if (string.IsNullOrEmpty(refId)) return;

        ct.ThrowIfCancellationRequested();
        var json = await fetcher(refId);
        if (json == null) return;

        obj["rawEncoding"] = JObject.Parse(json);
    }

    // ── Layer management ──────────────────────────────────────────────────────

    private static string GetLayerPath(JObject layerObj)
    {
        return layerObj["layerPath"]?.Value<string>()
            ?? layerObj["name"]?.Value<string>()
            ?? "ORBIT Receive";
    }

    private static long? GetLayerColor(JObject layerObj) =>
        layerObj["layerColor"]?.Value<long?>();

    /// <summary>
    /// Finds or creates a Rhino layer hierarchy matching <paramref name="layerPath"/>
    /// (Rhino-style <c>Parent::Child</c> notation). Returns the leaf layer's index.
    /// </summary>
    private static int EnsureLayer(RhinoDoc doc, string layerPath, long? packedArgb)
    {
        var segments = layerPath.Split(new[] { "::" }, StringSplitOptions.RemoveEmptyEntries);
        int parentIdx = -1;

        for (int depth = 0; depth < segments.Length; depth++)
        {
            var seg = segments[depth].Trim();
            var fullPath = string.Join("::", segments.Take(depth + 1));

            // Search for existing layer at this depth
            var existing = doc.Layers
                .FirstOrDefault(l => !l.IsDeleted
                    && l.FullPath.Equals(fullPath, StringComparison.OrdinalIgnoreCase)
                    && (parentIdx < 0 || l.ParentLayerId == doc.Layers[parentIdx].Id));

            if (existing != null)
            {
                parentIdx = existing.Index;
                continue;
            }

            // Create new layer
            var newLayer = new Layer
            {
                Name         = seg,
                ParentLayerId = parentIdx >= 0 ? doc.Layers[parentIdx].Id : Guid.Empty,
            };

            if (packedArgb.HasValue && depth == segments.Length - 1)
            {
                // Unpack the ARGB long (unsigned) → System.Drawing.Color
                var argb   = (int)(uint)packedArgb.Value;
                newLayer.Color = System.Drawing.Color.FromArgb(argb);
            }

            parentIdx = doc.Layers.Add(newLayer);
        }

        return parentIdx;
    }

    // ── Material/colour ───────────────────────────────────────────────────────

    private static void ApplyMaterialColor(JObject geoObj, ObjectAttributes attrs)
    {
        // Try renderMaterial.diffuse (inline object — not detached)
        var renderMat = geoObj["renderMaterial"] as JObject;
        if (renderMat == null) return;

        var diffuse = renderMat["diffuse"]?.Value<long?>();
        if (!diffuse.HasValue) return;

        var argb = (int)(uint)diffuse.Value;
        var color = System.Drawing.Color.FromArgb(argb);

        attrs.ObjectColor       = color;
        attrs.ColorSource       = ObjectColorSource.ColorFromObject;
    }
}
