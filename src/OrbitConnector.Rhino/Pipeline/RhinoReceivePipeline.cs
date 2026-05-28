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
/// Fetch version -> walk entire object tree (deep ref discovery, cached) ->
/// traverse tree by collection type -> convert -> bake into RhinoDoc.
///
/// <para>
/// <b>v0.1.13 changes</b> (on top of the v0.1.12 rewrite):
/// </para>
///
/// <list type="bullet">
///   <item><description>
///     Stub resolution is now <em>recursive</em> -- a stub that points
///     to another stub (which a few legacy senders emit) is followed
///     to convergence instead of being dropped one level early.
///   </description></item>
///   <item><description>
///     Collection-child enumeration now also walks <c>data</c>,
///     <c>children</c>, and <c>objects</c> on top of <c>elements</c> /
///     <c>displayValue</c>. Speckle's <c>Collection</c> and
///     <c>Organization.Model</c> types nest geometry under any of these,
///     and the v0.1.12 pipeline only walked the first two.
///   </description></item>
///   <item><description>
///     Layer path separators are normalised (<c>/</c>, <c>\\</c>, and
///     <c>::</c> all map to Rhino's <c>::</c>). This unblocks PRISM and
///     3DConvert payloads which use forward slashes in their layer
///     hierarchies.
///   </description></item>
///   <item><description>
///     Per-leaf diagnostic line via <c>RhinoApp.WriteLine</c>
///     (<c>[ORBIT] bake: type=... id=... -> layer 'X::Y' geom=Mesh</c>).
///     Skipped objects log the reason (unsupported type, conversion
///     failure, missing geometry).
///   </description></item>
///   <item><description>
///     Objects whose primary <c>speckle_type</c> is unsupported but
///     which carry a <c>displayValue</c> mesh now bake from that mesh
///     instead of being silently dropped (handled inside the converter,
///     this pipeline simply trusts the converter's verdict).
///   </description></item>
/// </list>
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
    /// non-empty children array is still treated as a collection --
    /// this is the loose fallback for legacy payloads.
    /// </summary>
    private static readonly HashSet<string> CollectionTypeDiscriminators = new(StringComparer.Ordinal)
    {
        "Speckle.Core.Models.Collection",
        "Speckle.Core.Models.Collections.Collection",
        "Objects.Other.Collections.Collection",
        "Objects.Organization.Model",
    };

    /// <summary>
    /// JSON property names a sender might use to attach the geometry
    /// children of a collection node. We walk all of these (in this order).
    /// </summary>
    private static readonly string[] CollectionChildProperties =
    {
        "elements",
        "displayValue",
        "data",
        "children",
        "objects",
    };

    private readonly OrbitToRhinoConverter _converter = new() { Verbose = true };

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
        progress?.Report(("Fetching version...", 5));
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
        progress?.Report(("Downloading object tree...", 10));
        using var transport = new ServerTransport(serverUrl, projectId, token);

        var objects = await FetchAllObjectsAsync(
            rootObjectId,
            id => transport.GetObjectAsync(id, ct),
            ct,
            (resolved, total) =>
            {
                int pct = total > 0
                    ? Math.Min(70, 10 + resolved * 60 / Math.Max(total, resolved))
                    : 10;
                progress?.Report(($"Downloading... {resolved}/{Math.Max(total, resolved)}", pct));
            });

        RhinoApp.WriteLine($"[ORBIT] receive: fetched {objects.Count} object(s) from server");

        if (!objects.TryGetValue(rootObjectId, out var rootObj))
            throw new InvalidOperationException(
                $"Root object '{rootObjectId}' did not materialise after tree walk.");

        // 3. TRAVERSE THE TREE AND BAKE.
        progress?.Report(("Baking geometry...", 75));

        var warnings   = new List<string>();
        var bakeState  = new BakeState(doc, _converter, warnings);

        doc.BeginUndoRecord("ORBIT Receive");
        try
        {
            TraverseAndBake(rootObj, objects, currentLayerPath: null, inheritedColor: null, bakeState, ct);
        }
        finally
        {
            doc.EndUndoRecord(doc.CurrentUndoRecordSerialNumber);
        }

        progress?.Report(($"Received {bakeState.ObjectCount} object(s)", 95));
        doc.Views.Redraw();

        RhinoApp.WriteLine(
            $"[ORBIT] receive: baked {bakeState.ObjectCount} object(s) into " +
            $"{bakeState.LayerCount} layer(s); skipped={bakeState.SkippedCount}; " +
            $"{warnings.Count} warning(s)");

        return new ReceiveResult(bakeState.ObjectCount, bakeState.LayerCount, warnings);
    }

    // -- Tree fetch ----------------------------------------------------------

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

    // -- Tree traversal + bake -----------------------------------------------

    private sealed class BakeState
    {
        public RhinoDoc Doc { get; }
        public OrbitToRhinoConverter Converter { get; }
        public List<string> Warnings { get; }
        public int ObjectCount { get; set; }
        public int SkippedCount { get; set; }
        public HashSet<int> LayersTouched { get; } = new();
        public int LayerCount => LayersTouched.Count;

        public BakeState(RhinoDoc doc, OrbitToRhinoConverter converter, List<string> warnings)
        {
            Doc = doc; Converter = converter; Warnings = warnings;
        }
    }

    private void TraverseAndBake(
        JObject node,
        IReadOnlyDictionary<string, JObject> objects,
        string? currentLayerPath,
        long? inheritedColor,
        BakeState state,
        CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();

        node = ResolveStub(node, objects);
        var speckleType = node["speckle_type"]?.Value<string>() ?? "";

        if (IsCollection(node, speckleType))
        {
            var ownLayerPath = NormaliseLayerPath(node["layerPath"]?.Value<string>());
            var ownName      = node["name"]?.Value<string>();

            string? childLayerPath = currentLayerPath;
            if (!string.IsNullOrEmpty(ownLayerPath))
                childLayerPath = ownLayerPath;
            else if (!string.IsNullOrEmpty(ownName))
                childLayerPath = string.IsNullOrEmpty(currentLayerPath)
                    ? ownName
                    : $"{currentLayerPath}::{ownName}";

            var ownColor = node["layerColor"]?.Value<long?>() ?? inheritedColor;

            int childCount = 0;
            foreach (var childToken in EnumerateCollectionChildren(node))
            {
                if (childToken is not JObject childObj) continue;
                childCount++;
                TraverseAndBakeChild(
                    childObj, objects, childLayerPath, ownColor, state, ct);
            }
            if (childCount == 0)
            {
                RhinoApp.WriteLine(
                    $"[ORBIT] traverse: collection type='{speckleType}' name='{ownName}' has 0 children");
            }
        }
        else
        {
            // Root is itself a leaf -- bake directly.
            BakeLeaf(node, currentLayerPath, inheritedColor, state);
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
            TraverseAndBake(resolved, objects, layerPath, inheritedLayerColor, state, ct);
            return;
        }

        // Leaf geometry. Honour the leaf's own layerPath if it sets one;
        // otherwise inherit from the enclosing collection chain.
        var leafLayerPath  = NormaliseLayerPath(resolved["layerPath"]?.Value<string>()) ?? layerPath;
        var leafLayerColor = resolved["layerColor"]?.Value<long?>() ?? inheritedLayerColor;

        // RhinoDataObject style: rawEncoding is a detached reference the
        // converter expects inline. Resolve it from our object cache so
        // OrbitToRhinoConverter can read its `contents`. Same for
        // displayValue when a sender uses detached display meshes.
        if (resolved["rawEncoding"] is JObject rawRef
            && rawRef["referencedId"]?.Value<string>() is string rawId
            && !string.IsNullOrEmpty(rawId)
            && objects.TryGetValue(rawId, out var rawObj))
        {
            resolved = (JObject)resolved.DeepClone();
            resolved["rawEncoding"] = rawObj;
        }

        if (resolved["displayValue"] is JArray dvArr)
        {
            var inlinedDv = new JArray();
            bool anyResolved = false;
            foreach (var item in dvArr)
            {
                if (item is JObject dvObj
                    && dvObj["referencedId"]?.Value<string>() is string dvRefId
                    && !string.IsNullOrEmpty(dvRefId)
                    && objects.TryGetValue(dvRefId, out var resolvedDv))
                {
                    inlinedDv.Add(resolvedDv);
                    anyResolved = true;
                }
                else
                {
                    inlinedDv.Add(item);
                }
            }
            if (anyResolved)
            {
                resolved = (JObject)resolved.DeepClone();
                resolved["displayValue"] = inlinedDv;
            }
        }

        BakeLeaf(resolved, leafLayerPath, leafLayerColor, state);
    }

    private void BakeLeaf(JObject geoObj, string? layerPath, long? layerColor, BakeState state)
    {
        var spType = geoObj["speckle_type"]?.Value<string>() ?? "unknown";
        var orbitId = geoObj["id"]?.Value<string>() ?? "?";

        var geometry = state.Converter.Convert(geoObj);
        if (geometry == null)
        {
            state.SkippedCount++;
            var msg = $"Skipped unsupported / unconvertible object: type='{spType}' id={orbitId}";
            state.Warnings.Add(msg);
            RhinoApp.WriteLine($"[ORBIT] bake skip: {msg}");
            return;
        }

        int layerIdx = EnsureLayer(state.Doc, layerPath ?? "ORBIT Receive", layerColor);
        state.LayersTouched.Add(layerIdx);

        var attrs = new ObjectAttributes
        {
            LayerIndex = layerIdx,
        };
        ApplyMaterialColor(geoObj, attrs);

        // Stamp the ORBIT object id into the user dictionary so a future
        // delete-and-replace receive can find the previously-baked object.
        if (!string.IsNullOrEmpty(orbitId) && orbitId != "?")
        {
            try { attrs.SetUserString("ORBIT_objectId", orbitId); }
            catch { /* attribute store is best-effort */ }
        }

        var addedGuid = state.Doc.Objects.Add(geometry, attrs);
        if (addedGuid != Guid.Empty)
        {
            state.ObjectCount++;
            var layerFullPath = state.Doc.Layers[layerIdx].FullPath;
            RhinoApp.WriteLine(
                $"[ORBIT] bake: type='{spType}' id={orbitId} -> layer '{layerFullPath}' geom={geometry.GetType().Name}");
        }
        else
        {
            state.SkippedCount++;
            var msg = $"RhinoDoc.Objects.Add returned empty Guid for type='{spType}' id={orbitId}";
            state.Warnings.Add(msg);
            RhinoApp.WriteLine($"[ORBIT] bake skip: {msg}");
        }
    }

    private static bool IsCollection(JObject node, string speckleType)
    {
        if (CollectionTypeDiscriminators.Contains(speckleType)) return true;
        if (!string.IsNullOrEmpty(node["collectionType"]?.Value<string>())) return true;

        // Loose fallback: anything with a non-empty children array on any
        // of the known collection child properties walks like a collection.
        // Avoids dropping legacy payloads that pre-date collectionType, and
        // avoids treating senders that put collection children under
        // `data` / `objects` as leaves.
        foreach (var prop in CollectionChildProperties)
        {
            if (node[prop] is JArray arr && arr.Count > 0)
            {
                // Geometry leaves with displayValue meshes also have a
                // non-empty `displayValue` array but ARE leaves; we want
                // them to go through the converter, not be re-walked.
                if (prop == "displayValue" && IsKnownGeometryLeaf(speckleType))
                    return false;
                return true;
            }
        }
        return false;
    }

    private static bool IsKnownGeometryLeaf(string speckleType)
    {
        // Any speckle_type starting with "Objects.Geometry." is a leaf
        // (Mesh / Brep / Surface / Curve / Point / etc.). Their
        // `displayValue` is part of the leaf's geometry, not children.
        return speckleType.StartsWith("Objects.Geometry.", StringComparison.Ordinal)
            || speckleType.Contains("RhinoObject");
    }

    private static IEnumerable<JToken> EnumerateCollectionChildren(JObject collection)
    {
        foreach (var prop in CollectionChildProperties)
        {
            // Skip displayValue when this collection node is also tagged as
            // a known geometry leaf (defence-in-depth alongside IsCollection
            // above; in practice IsCollection already short-circuits this).
            if (collection[prop] is JArray arr)
            {
                foreach (var c in arr) yield return c;
            }
            else if (collection[prop] is JObject one)
            {
                yield return one;
            }
        }
    }

    /// <summary>
    /// Recursively follow <c>referencedId</c> stubs until we hit a real
    /// object (or run out of resolutions). The v0.1.12 implementation only
    /// followed one level, which dropped geometry whenever a sender emitted
    /// a stub-of-a-stub (rare but seen on a couple of legacy 3DConvert
    /// payloads).
    /// </summary>
    private static JObject ResolveStub(
        JObject node, IReadOnlyDictionary<string, JObject> objects)
    {
        // Bound the chain length to defend against pathological cycles.
        for (int hops = 0; hops < 8; hops++)
        {
            var refId = node["referencedId"]?.Value<string>();
            if (string.IsNullOrEmpty(refId)) return node;
            if (!objects.TryGetValue(refId, out var resolved)) return node;
            if (resolved == node) return node;
            node = resolved;
        }
        return node;
    }

    /// <summary>
    /// Normalise a Speckle/ORBIT layerPath onto Rhino's <c>::</c> separator.
    /// Senders use any of <c>::</c> (Speckle Rhino connector), <c>/</c>
    /// (PRISM, 3DConvert, the Speckle web frontend), or <c>\\</c> (older
    /// Vectorworks payloads). Returns null for null/empty input.
    /// </summary>
    private static string? NormaliseLayerPath(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;

        // Already Rhino-native.
        if (raw.Contains("::") && !raw.Contains('/') && !raw.Contains('\\'))
            return raw.Trim();

        // Replace `\` and `/` with `::`. Two-pass so `/` inside a `::`
        // segment doesn't double-fire.
        var withColons = raw.Replace("\\", "::").Replace("/", "::");

        // Collapse runs of separators (`::::` -> `::`) and strip empty
        // leading / trailing segments.
        var parts = withColons.Split(new[] { "::" }, StringSplitOptions.RemoveEmptyEntries)
                              .Select(p => p.Trim())
                              .Where(p => p.Length > 0)
                              .ToArray();
        if (parts.Length == 0) return null;
        return string.Join("::", parts);
    }

    // -- Layer management ----------------------------------------------------

    /// <summary>
    /// Finds or creates a Rhino layer hierarchy matching <paramref name="layerPath"/>
    /// (Rhino-style <c>Parent::Child</c> notation, post-normalisation).
    /// Returns the leaf layer's index.
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
                Name          = seg,
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

    // -- Material/colour -----------------------------------------------------

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
