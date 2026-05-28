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
/// Fetch version → walk entire object tree (deep ref discovery, cached) →
/// traverse tree by collection type → convert → bake into RhinoDoc.
///
/// <para>
/// <b>v0.1.12 rewrite.</b> The v0.1.10 / v0.1.11 pipeline assumed every
/// model root carried a non-empty <c>elements</c> array of direct
/// reference stubs. That assumption holds when receiving a model the
/// same connector pushed, but fails for models produced by other ORBIT
/// clients (Python, PRISM, the legacy 3DConvert pipeline, and any
/// Speckle-fork sender) that detach <c>elements</c> with @ prefixes,
/// nest references deeper inside the tree, or carry the geometry on
/// proxies / <c>displayValue</c> instead. The user-visible symptom was
/// <c>"The root object has no elements. Nothing to receive."</c>.
/// </para>
///
/// <para>
/// The new strategy mirrors the visualiser orchestrator
/// (REBUS-ORBIT/prism :: <c>OrbitReceivePipeline.cs</c> +
/// <c>HttpOrbitApi.cs</c>) which was hardened against live ORBIT
/// payloads in PRISM commits <c>50f8c39</c> and <c>ad62e31</c>:
/// </para>
///
/// <list type="number">
///   <item><description>
///     Resolve <c>version.referencedObject</c> via the GraphQL
///     <c>project(id).model(id).versions</c> query
///     (<see cref="OrbitClient.GetVersionsAsync"/>).
///   </description></item>
///   <item><description>
///     Fetch the root via REST <c>GET /objects/{streamId}/{rootHash}/single</c>
///     (<see cref="ServerTransport.GetObjectAsync"/>).
///   </description></item>
///   <item><description>
///     Walk <em>every</em> JSON node in the root, yielding every
///     <c>referencedId</c> regardless of which property holds it.
///     De-dupe and fetch each unique child via the same REST endpoint.
///     Recurse on the children — bounded only by the closure table size.
///   </description></item>
///   <item><description>
///     Traverse the now fully-resolved tree from the root. Collection
///     nodes (<c>collectionType</c> set, or <c>speckle_type</c> matches a
///     known collection discriminator, or <c>elements</c> is a non-empty
///     array) push their children. Leaf nodes go through
///     <see cref="OrbitToRhinoConverter"/> and are added to the
///     Rhino doc on the layer encoded by the leaf's
///     <c>layerPath</c> (or the nearest ancestor collection name).
///   </description></item>
/// </list>
///
/// <para>
/// All HTTP IO goes through <see cref="ServerTransport"/>; the only
/// blob endpoint the connector uses (when texture support is wired in)
/// is <c>GET /api/stream/{streamId}/blob/{blobId}</c> via the existing
/// <see cref="OrbitBlobUploader"/> sibling. ORBIT blob ids are 10-char
/// server-assigned strings, not SHA-256 hashes, so any download-side
/// integrity check would always fail — the connector has none, and
/// none must be added on the receive path.
/// </para>
/// </summary>
public class RhinoReceivePipeline
{
    /// <summary>
    /// Result summary returned on successful receive.
    /// </summary>
    public record ReceiveResult(int ObjectCount, int LayerCount, List<string> Warnings);

    /// <summary>
    /// <c>speckle_type</c> discriminators that mark a node as a
    /// container of nested children. Pulled from the visualiser's
    /// equivalent set plus the prefixes the C# Speckle-ORBIT SDK
    /// emits today. Anything not in this set <em>but</em> with a
    /// non-empty <c>elements</c> array is still treated as a
    /// collection — this is the loose fallback for legacy payloads.
    /// </summary>
    private static readonly HashSet<string> CollectionTypeDiscriminators = new(StringComparer.Ordinal)
    {
        "Speckle.Core.Models.Collection",
        "Speckle.Core.Models.Collections.Collection",
        "Objects.Other.Collections.Collection",
        "Objects.Organization.Model",
    };

    private readonly OrbitToRhinoConverter _converter = new();

    /// <summary>
    /// Execute a full receive. Downloads the latest version (or
    /// <c>PinnedVersionId</c> if set on the card) of the selected
    /// model and bakes all geometry into the active Rhino document.
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

        // 1. RESOLVE VERSION (GraphQL).
        //    OrbitClient.GetVersionsAsync wraps the same `project(id).model(id).versions`
        //    query the visualiser uses for its single-version variant — see
        //    REBUS-ORBIT/prism :: HttpOrbitApi.VersionQuery.
        progress?.Report(("Fetching version…", 5));
        var versions = await client.GetVersionsAsync(projectId, modelId, ct);
        if (versions.Count == 0)
            throw new InvalidOperationException("No versions found for this model.");

        var version = !string.IsNullOrEmpty(card.PinnedVersionId)
            ? versions.FirstOrDefault(v => v.Id == card.PinnedVersionId) ?? versions[0]
            : versions[0];

        var rootObjectId = version.ReferencedObject
            ?? throw new InvalidOperationException("Version has no referencedObject.");

        RhinoApp.WriteLine(
            $"[ORBIT] receive: project={projectId} model={modelId} " +
            $"version={version.Id} root={rootObjectId}");

        // 2. DOWNLOAD THE ENTIRE OBJECT TREE.
        //    REST GET /objects/{streamId}/{id}/single per object, recursively
        //    walking every detached reference no matter where it sits in the
        //    tree. Each id is fetched at most once.
        progress?.Report(("Downloading object tree…", 10));
        using var transport = new ServerTransport(serverUrl, projectId, token);

        var objects = await FetchAllObjectsAsync(
            rootObjectId,
            id => transport.GetObjectAsync(id, ct),
            ct,
            (resolved, total) =>
            {
                // 10 → 70 % for tree download; whatever closure size we
                // discover incrementally feeds the percent.
                int pct = total > 0
                    ? Math.Min(70, 10 + resolved * 60 / Math.Max(total, resolved))
                    : 10;
                progress?.Report(($"Downloading… {resolved}/{Math.Max(total, resolved)}", pct));
            });

        RhinoApp.WriteLine($"[ORBIT] receive: fetched {objects.Count} object(s) from server");

        if (!objects.TryGetValue(rootObjectId, out var rootObj))
            throw new InvalidOperationException(
                $"Root object '{rootObjectId}' did not materialise after tree walk.");

        // 3. TRAVERSE THE TREE AND BAKE.
        progress?.Report(("Baking geometry…", 75));

        var warnings   = new List<string>();
        var bakeState  = new BakeState(doc, _converter, warnings);

        doc.BeginUndoRecord("ORBIT Receive");
        try
        {
            // Root collection: descend into its children. If the root
            // itself is a leaf geometry, treat it as the only object.
            TraverseAndBake(rootObj, objects, currentLayerPath: null, bakeState, ct);
        }
        finally
        {
            doc.EndUndoRecord(doc.CurrentUndoRecordSerialNumber);
        }

        progress?.Report(($"Received {bakeState.ObjectCount} object(s)", 95));
        doc.Views.Redraw();

        RhinoApp.WriteLine(
            $"[ORBIT] receive: baked {bakeState.ObjectCount} object(s) into " +
            $"{bakeState.LayerCount} layer(s); {warnings.Count} warning(s)");

        return new ReceiveResult(bakeState.ObjectCount, bakeState.LayerCount, warnings);
    }

    // ── Tree fetch ───────────────────────────────────────────────────────────

    /// <summary>
    /// Fetch <paramref name="rootId"/> and every object reachable from
    /// it via <c>referencedId</c> stubs anywhere in the JSON body.
    /// Returns a dictionary keyed by content hash. Fetches the
    /// closure size from <c>__closure</c> on the root for progress
    /// reporting, falling back to the resolved-count when the root
    /// has no closure.
    /// </summary>
    private static async Task<Dictionary<string, JObject>> FetchAllObjectsAsync(
        string rootId,
        Func<string, Task<string?>> fetch,
        CancellationToken ct,
        Action<int, int> progress)
    {
        var fetched = new Dictionary<string, JObject>(StringComparer.Ordinal);
        var queue   = new Queue<string>();
        queue.Enqueue(rootId);
        int total = 0;

        while (queue.Count > 0)
        {
            ct.ThrowIfCancellationRequested();
            var id = queue.Dequeue();
            if (fetched.ContainsKey(id)) continue;

            string? json;
            try { json = await fetch(id); }
            catch (Exception ex)
            {
                throw new InvalidOperationException(
                    $"Failed to fetch ORBIT object '{id}' via " +
                    $"GET /objects/{{streamId}}/{id}/single: {ex.Message}", ex);
            }

            if (json == null)
                throw new InvalidOperationException(
                    $"ORBIT object '{id}' returned a null body (404 or empty response).");

            JObject obj;
            try { obj = JObject.Parse(json); }
            catch (Exception ex)
            {
                throw new InvalidOperationException(
                    $"ORBIT object '{id}' is not valid JSON: {ex.Message}", ex);
            }

            fetched[id] = obj;

            // The root carries __closure: { childId → depth }. Use its
            // size as the total for progress reporting on the first
            // fetch. Subsequent fetches use whichever number is larger
            // (we may discover more references than the closure listed
            // for older / inconsistent payloads).
            if (id == rootId && obj["__closure"] is JObject closure)
                total = closure.Count + 1; // + the root itself

            foreach (var refId in EnumerateReferenceIds(obj))
            {
                if (!fetched.ContainsKey(refId))
                    queue.Enqueue(refId);
            }

            progress(fetched.Count, total);
        }

        return fetched;
    }

    /// <summary>
    /// Yield every <c>referencedId</c> found anywhere in the JSON tree
    /// rooted at <paramref name="node"/>. Mirrors the visualiser's
    /// <c>OrbitObject.EnumerateReferenceIds</c> behaviour: a node that
    /// has its own <c>referencedId</c> is a stub and short-circuits
    /// (its children, if any, will be discovered when the referenced
    /// object is fetched).
    /// </summary>
    private static IEnumerable<string> EnumerateReferenceIds(JToken? node)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var refId in WalkRefs(node))
            if (seen.Add(refId)) yield return refId;
    }

    private static IEnumerable<string> WalkRefs(JToken? node)
    {
        switch (node)
        {
            case JObject obj:
                var refToken = obj["referencedId"];
                if (refToken != null && refToken.Type == JTokenType.String)
                {
                    var refId = refToken.Value<string>();
                    if (!string.IsNullOrEmpty(refId))
                    {
                        yield return refId;
                        yield break;
                    }
                }
                foreach (var prop in obj.Properties())
                {
                    // Skip the closure table; its keys are already child ids
                    // and the values are depth ints — not detached references.
                    if (prop.Name == "__closure") continue;
                    foreach (var r in WalkRefs(prop.Value)) yield return r;
                }
                break;
            case JArray arr:
                foreach (var item in arr)
                    foreach (var r in WalkRefs(item)) yield return r;
                break;
        }
    }

    // ── Tree traversal + bake ────────────────────────────────────────────────

    private sealed class BakeState
    {
        public RhinoDoc Doc { get; }
        public OrbitToRhinoConverter Converter { get; }
        public List<string> Warnings { get; }
        public int ObjectCount { get; set; }
        public HashSet<int> LayersTouched { get; } = new();
        public int LayerCount => LayersTouched.Count;

        public BakeState(RhinoDoc doc, OrbitToRhinoConverter converter, List<string> warnings)
        {
            Doc = doc; Converter = converter; Warnings = warnings;
        }
    }

    /// <summary>
    /// Recursively traverse a resolved object tree. Collections push
    /// their children with a deeper <paramref name="currentLayerPath"/>;
    /// leaf nodes are converted and baked.
    /// </summary>
    private void TraverseAndBake(
        JObject node,
        IReadOnlyDictionary<string, JObject> objects,
        string? currentLayerPath,
        BakeState state,
        CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();

        // Resolve a node that might itself be a reference stub.
        node = ResolveStub(node, objects);

        var speckleType = node["speckle_type"]?.Value<string>() ?? "";

        if (IsCollection(node, speckleType))
        {
            // Layer naming priority: explicit layerPath > name > falls through to parent.
            var ownLayerPath = node["layerPath"]?.Value<string>();
            var ownName      = node["name"]?.Value<string>();

            string? childLayerPath = currentLayerPath;
            if (!string.IsNullOrEmpty(ownLayerPath))
                childLayerPath = ownLayerPath;
            else if (!string.IsNullOrEmpty(ownName))
                childLayerPath = string.IsNullOrEmpty(currentLayerPath)
                    ? ownName
                    : $"{currentLayerPath}::{ownName}";

            var ownColor = node["layerColor"]?.Value<long?>();

            // Walk elements + displayValue (some collections carry geometry
            // directly under displayValue) + any other nested OrbitObject
            // collection-like children the SDK might add later.
            foreach (var childToken in EnumerateCollectionChildren(node))
            {
                if (childToken is not JObject childObj) continue;
                TraverseAndBakeChild(
                    childObj, objects, childLayerPath, ownColor, state, ct);
            }
        }
        else
        {
            // Root is itself a leaf — bake directly into the default layer.
            BakeLeaf(node, currentLayerPath, layerColor: null, state);
        }
    }

    private void TraverseAndBakeChild(
        JObject childObj,
        IReadOnlyDictionary<string, JObject> objects,
        string? layerPath,
        long? inheritedLayerColor,
        BakeState state,
        CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var resolved = ResolveStub(childObj, objects);
        var speckleType = resolved["speckle_type"]?.Value<string>() ?? "";

        if (IsCollection(resolved, speckleType))
        {
            // Nested collection: recurse, extending the layer path.
            TraverseAndBake(resolved, objects, layerPath, state, ct);
            return;
        }

        // Leaf geometry. Honour the leaf's own layerPath if it sets one;
        // otherwise inherit from the enclosing collection chain.
        var leafLayerPath = resolved["layerPath"]?.Value<string>() ?? layerPath;
        var leafLayerColor = resolved["layerColor"]?.Value<long?>() ?? inheritedLayerColor;

        // RhinoDataObject style: rawEncoding is a detached reference the
        // converter expects inline. Resolve it from our object cache so
        // OrbitToRhinoConverter can read its `contents`.
        if (resolved["rawEncoding"] is JObject rawRef
            && rawRef["referencedId"]?.Value<string>() is string rawId
            && !string.IsNullOrEmpty(rawId)
            && objects.TryGetValue(rawId, out var rawObj))
        {
            resolved = (JObject)resolved.DeepClone();
            resolved["rawEncoding"] = rawObj;
        }

        BakeLeaf(resolved, leafLayerPath, leafLayerColor, state);
    }

    private void BakeLeaf(JObject geoObj, string? layerPath, long? layerColor, BakeState state)
    {
        var geometry = state.Converter.Convert(geoObj);
        if (geometry == null)
        {
            var spType = geoObj["speckle_type"]?.Value<string>() ?? "unknown";
            state.Warnings.Add($"Skipped unsupported object type: {spType}");
            return;
        }

        int layerIdx = EnsureLayer(state.Doc, layerPath ?? "ORBIT Receive", layerColor);
        state.LayersTouched.Add(layerIdx);

        var attrs = new ObjectAttributes
        {
            LayerIndex = layerIdx,
        };
        ApplyMaterialColor(geoObj, attrs);

        state.Doc.Objects.Add(geometry, attrs);
        state.ObjectCount++;
    }

    private static bool IsCollection(JObject node, string speckleType)
    {
        if (CollectionTypeDiscriminators.Contains(speckleType)) return true;
        if (!string.IsNullOrEmpty(node["collectionType"]?.Value<string>())) return true;
        // Loose fallback: anything with a non-empty elements array
        // walks like a collection. Avoids dropping legacy payloads that
        // pre-date collectionType.
        if (node["elements"] is JArray arr && arr.Count > 0) return true;
        return false;
    }

    private static IEnumerable<JToken> EnumerateCollectionChildren(JObject collection)
    {
        if (collection["elements"] is JArray elements)
            foreach (var c in elements) yield return c;

        // Some senders pack the per-collection display geometry into
        // displayValue instead of elements. The receive path treats
        // them identically — both are children to walk.
        if (collection["displayValue"] is JArray displayValue)
            foreach (var c in displayValue) yield return c;
    }

    private static JObject ResolveStub(
        JObject node, IReadOnlyDictionary<string, JObject> objects)
    {
        var refId = node["referencedId"]?.Value<string>();
        if (string.IsNullOrEmpty(refId)) return node;
        return objects.TryGetValue(refId, out var resolved) ? resolved : node;
    }

    // ── Layer management ──────────────────────────────────────────────────────

    /// <summary>
    /// Finds or creates a Rhino layer hierarchy matching <paramref name="layerPath"/>
    /// (Rhino-style <c>Parent::Child</c> notation). Returns the leaf layer's index.
    /// </summary>
    private static int EnsureLayer(RhinoDoc doc, string layerPath, long? packedArgb)
    {
        var segments = layerPath.Split(new[] { "::" }, StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length == 0) segments = new[] { "ORBIT Receive" };

        int parentIdx = -1;

        for (int depth = 0; depth < segments.Length; depth++)
        {
            var seg = segments[depth].Trim();
            var fullPath = string.Join("::", segments.Take(depth + 1));

            var existing = doc.Layers
                .FirstOrDefault(l => !l.IsDeleted
                    && l.FullPath.Equals(fullPath, StringComparison.OrdinalIgnoreCase)
                    && (parentIdx < 0 || l.ParentLayerId == doc.Layers[parentIdx].Id));

            if (existing != null)
            {
                parentIdx = existing.Index;
                continue;
            }

            var newLayer = new Layer
            {
                Name         = seg,
                ParentLayerId = parentIdx >= 0 ? doc.Layers[parentIdx].Id : Guid.Empty,
            };

            if (packedArgb.HasValue && depth == segments.Length - 1)
            {
                var argb = (int)(uint)packedArgb.Value;
                newLayer.Color = System.Drawing.Color.FromArgb(argb);
            }

            parentIdx = doc.Layers.Add(newLayer);
        }

        return parentIdx;
    }

    // ── Material/colour ───────────────────────────────────────────────────────

    private static void ApplyMaterialColor(JObject geoObj, ObjectAttributes attrs)
    {
        var renderMat = geoObj["renderMaterial"] as JObject;
        if (renderMat == null) return;

        var diffuse = renderMat["diffuse"]?.Value<long?>();
        if (!diffuse.HasValue) return;

        var argb = (int)(uint)diffuse.Value;
        var color = System.Drawing.Color.FromArgb(argb);

        attrs.ObjectColor = color;
        attrs.ColorSource = ObjectColorSource.ColorFromObject;
    }
}
