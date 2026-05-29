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
    ///
    /// <para>
    /// <b>v0.1.14 change:</b> for every name in this list we ALSO check
    /// the <c>@</c>-prefixed variant (e.g. <c>@elements</c>) when the
    /// bare name is absent. <c>@</c> is Speckle's detach-marker prefix
    /// (<c>[Chunkable]</c> / <c>[DetachProperty]</c> on the C# side):
    /// when an SDK marks <c>elements</c> as detached the field lands on
    /// the wire as <c>@elements</c>, and the ORBIT server stores it
    /// AS-IS (it does NOT strip the prefix when persisting). The
    /// connector's own send pipeline uses bare names today, but the
    /// PRISM / monorepo SDK uses <c>@elements</c> / <c>@displayValue</c>
    /// / <c>@rawEncoding</c> / <c>@objects</c> / <c>@definitionProxies</c>
    /// etc. — see <c>Orbit.Objects.Base.OrbitObject</c> in the monorepo.
    /// v0.1.13 only checked the bare names and therefore silently dropped
    /// every PRISM-uploaded collection ("0 children" in the receive log).
    /// </para>
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
                    ? Math.Min(65, 10 + resolved * 55 / Math.Max(total, resolved))
                    : 10;
                progress?.Report(($"Downloading... {resolved}/{Math.Max(total, resolved)}", pct));
            });

        RhinoApp.WriteLine($"[ORBIT] receive: fetched {objects.Count} object(s) from server");

        if (!objects.TryGetValue(rootObjectId, out var rootObj))
            throw new InvalidOperationException(
                $"Root object '{rootObjectId}' did not materialise after tree walk.");

        // 2.5. PRE-FETCH TEXTURE BLOBS (v0.1.15).
        // OrbitMaterialConverter walks the entire object tree, enumerates
        // every blob id referenced by any render material's texture field,
        // and downloads them in parallel into a per-project temp dir. The
        // synchronous bake phase below then only reads the on-disk cache,
        // which keeps BakeLeaf sync-friendly (no await inside RhinoDoc
        // mutations) and avoids the deadlock risk of blocking on HTTP
        // calls from a UI-context-captured continuation.
        OrbitMaterialConverter? materialConverter = null;
        try
        {
            materialConverter = new OrbitMaterialConverter(serverUrl, projectId, token);
            progress?.Report(("Downloading textures...", 68));
            await materialConverter.PrefetchBlobsAsync(
                objects,
                progress: new Progress<(int done, int total)>(p =>
                {
                    if (p.total <= 0) return;
                    var pct = 68 + (int)(p.done * 5.0 / p.total);
                    progress?.Report(($"Downloading textures... {p.done}/{p.total}", pct));
                }),
                ct: ct);
        }
        catch (OperationCanceledException)
        {
            materialConverter?.Dispose();
            throw;
        }
        catch (Exception ex)
        {
            // Texture prefetch is best-effort: a failure here must not
            // block the geometry bake. The diagnostic line lands in the
            // Rhino command window so the next bug report is actionable.
            RhinoApp.WriteLine(
                $"[ORBIT] material: texture prefetch failed (geometry will still bake): {ex.Message}");
            materialConverter?.Dispose();
            materialConverter = null;
        }

        // 3. TRAVERSE THE TREE AND BAKE.
        progress?.Report(("Baking geometry...", 75));

        var warnings   = new List<string>();
        var bakeState  = new BakeState(doc, _converter, materialConverter, objects, warnings);

        ResetAtPrefixHits();
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

        LogAtPrefixSummary();
        if (materialConverter is not null)
        {
            RhinoApp.WriteLine(
                $"[ORBIT] material: summary materials={materialConverter.MaterialsCreated} " +
                $"blobs={materialConverter.BlobsRequested} " +
                $"downloaded={materialConverter.BlobsDownloaded} " +
                $"reused={materialConverter.BlobsFromDisk} " +
                $"missing={materialConverter.BlobsMissing}");
        }
        RhinoApp.WriteLine(
            $"[ORBIT] receive: baked {bakeState.ObjectCount} object(s) into " +
            $"{bakeState.LayerCount} layer(s); skipped={bakeState.SkippedCount}; " +
            $"{warnings.Count} warning(s)");

        materialConverter?.Dispose();
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
        public OrbitMaterialConverter? MaterialConverter { get; }
        public IReadOnlyDictionary<string, JObject> Objects { get; }
        public List<string> Warnings { get; }
        public int ObjectCount { get; set; }
        public int SkippedCount { get; set; }
        public HashSet<int> LayersTouched { get; } = new();
        public int LayerCount => LayersTouched.Count;

        public BakeState(
            RhinoDoc doc,
            OrbitToRhinoConverter converter,
            OrbitMaterialConverter? materialConverter,
            IReadOnlyDictionary<string, JObject> objects,
            List<string> warnings)
        {
            Doc = doc;
            Converter = converter;
            MaterialConverter = materialConverter;
            Objects = objects;
            Warnings = warnings;
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
        //
        // v0.1.14: the monorepo SDK (PRISM uploads) uses `@rawEncoding`
        // and `@displayValue` — Speckle's `[DetachProperty]` convention
        // preserves the `@` prefix on the wire. Probe both names and, on
        // hit, normalise back to the un-prefixed name the converter
        // expects (OrbitToRhinoConverter reads `rawEncoding` / `displayValue`).
        if (TryResolveDetachedSingle(resolved, "rawEncoding", objects, out var rawResolved))
            resolved = rawResolved;

        if (TryResolveDetachedArray(resolved, "displayValue", objects, out var dvResolved))
            resolved = dvResolved;

        // v0.1.15: renderMaterial stub inlining + textureCoordinates dechunk
        // happen inside BakeLeaf so the same prep covers the root-as-leaf
        // path (TraverseAndBake -> BakeLeaf directly) without duplication.
        BakeLeaf(resolved, leafLayerPath, leafLayerColor, state);
    }

    /// <summary>
    /// De-chunk <c>textureCoordinates</c> (or its detached <c>@textureCoordinates</c>
    /// twin) into a flat <c>[u0, v0, u1, v1, ...]</c> <see cref="JArray"/> the
    /// mesh converter can consume directly.
    ///
    /// <para>
    /// Three input shapes are supported, in order of frequency on the wire:
    /// </para>
    ///
    /// <list type="bullet">
    ///   <item><description>
    ///     A flat array of numbers (3DConvert / PRISM Python writers and
    ///     the connector's own <c>RhinoMeshConverter</c> on small meshes).
    ///     Returned untouched.
    ///   </description></item>
    ///   <item><description>
    ///     An array of <c>{referencedId}</c> stubs (every C# SDK send
    ///     above the <c>[Chunkable]</c> threshold; this is the path the
    ///     <c>speckle-frontend-2-rebus:v2.4.3</c> viewer fix had to add a
    ///     <c>dechunk</c> call for). Each chunk's <c>data</c> array is
    ///     concatenated into the output.
    ///   </description></item>
    ///   <item><description>
    ///     A single <c>{referencedId}</c> stub resolving to a chunk
    ///     <c>{data:[...]}</c> (rare, but emitted by a couple of
    ///     visualiser staging payloads).
    ///   </description></item>
    /// </list>
    ///
    /// <para>
    /// When the bare property exists we update it in place; when only
    /// <c>@textureCoordinates</c> exists we move the result onto the
    /// bare name so the converter (which reads <c>textureCoordinates</c>)
    /// finds it.
    /// </para>
    /// </summary>
    private bool TryDechunkTextureCoordinates(
        JObject parent,
        IReadOnlyDictionary<string, JObject> objects,
        out JObject result)
    {
        result = parent;
        const string baseName = "textureCoordinates";
        const string atName   = "@" + baseName;
        var token = parent[baseName] ?? parent[atName];
        if (token is null) return false;

        bool usedAt = parent[atName] != null && parent[baseName] == null;

        // Already a flat numeric array — no dechunk work needed, but if
        // it was under the `@` name we still need to migrate it onto the
        // bare property for the converter.
        if (token is JArray arr && (arr.Count == 0 || arr[0].Type != JTokenType.Object))
        {
            if (!usedAt) return false;
            BumpAtPrefixHit(atName);
            var clone = (JObject)parent.DeepClone();
            clone.Remove(atName);
            clone[baseName] = arr;
            result = clone;
            return true;
        }

        // Array of chunk references / chunks.
        if (token is JArray chunkArr)
        {
            var concat = new JArray();
            int chunksResolved = 0;
            foreach (var item in chunkArr)
            {
                var data = ExtractChunkData(item, objects);
                if (data is null) continue;
                chunksResolved++;
                foreach (var d in data) concat.Add(d);
            }
            if (concat.Count == 0) return false;

            if (usedAt) BumpAtPrefixHit(atName);
            var clone = (JObject)parent.DeepClone();
            clone.Remove(atName);
            clone[baseName] = concat;
            result = clone;
            RhinoApp.WriteLine(
                $"[ORBIT] uv: dechunked textureCoordinates chunks={chunksResolved} pairs={concat.Count / 2}");
            return true;
        }

        // Single stub or chunk.
        if (token is JObject single)
        {
            var data = ExtractChunkData(single, objects);
            if (data is null) return false;

            if (usedAt) BumpAtPrefixHit(atName);
            var clone = (JObject)parent.DeepClone();
            clone.Remove(atName);
            clone[baseName] = data;
            result = clone;
            RhinoApp.WriteLine(
                $"[ORBIT] uv: dechunked textureCoordinates single-chunk pairs={data.Count / 2}");
            return true;
        }

        return false;
    }

    /// <summary>
    /// Resolve a chunk reference (or inline chunk JObject) to its
    /// <c>data</c> array. Returns null when the token is neither a
    /// known chunk nor a stub of one we can resolve.
    /// </summary>
    private static JArray? ExtractChunkData(
        JToken? token, IReadOnlyDictionary<string, JObject> objects)
    {
        if (token is null) return null;

        JObject? chunkObj = null;
        if (token is JObject obj)
        {
            var refId = obj["referencedId"]?.Value<string>();
            if (!string.IsNullOrEmpty(refId)
                && objects.TryGetValue(refId!, out var resolved))
                chunkObj = resolved;
            else if (obj["data"] is JArray)
                chunkObj = obj;
        }
        if (chunkObj is null) return null;

        return chunkObj["data"] as JArray;
    }

    /// <summary>
    /// Detached-single helper. Looks for <paramref name="baseName"/> or its
    /// <c>@</c>-prefixed variant on <paramref name="parent"/>. When the value
    /// is a <c>{referencedId}</c> stub, swaps in the resolved object from
    /// <paramref name="objects"/> under the un-prefixed name so downstream
    /// code (and the converter) sees a uniform shape. Returns true when
    /// <paramref name="result"/> is a fresh clone with the substitution
    /// applied; false when no work was needed.
    /// </summary>
    private bool TryResolveDetachedSingle(
        JObject parent,
        string baseName,
        IReadOnlyDictionary<string, JObject> objects,
        out JObject result)
    {
        result = parent;
        var atName = "@" + baseName;
        // Prefer bare name (connector shape); fall back to @-prefix (PRISM).
        JObject? stub = parent[baseName] as JObject ?? parent[atName] as JObject;
        if (stub == null) return false;
        var refId = stub["referencedId"]?.Value<string>();
        if (string.IsNullOrEmpty(refId)) return false;
        if (!objects.TryGetValue(refId, out var inlined)) return false;

        bool usedAt = parent[atName] != null && parent[baseName] == null;
        if (usedAt) BumpAtPrefixHit(atName);

        var clone = (JObject)parent.DeepClone();
        clone.Remove(atName);
        clone[baseName] = inlined;
        result = clone;
        return true;
    }

    /// <summary>
    /// Detached-array helper. Same contract as
    /// <see cref="TryResolveDetachedSingle"/> but for array-valued
    /// detached properties like <c>displayValue</c> / <c>@displayValue</c>.
    /// Resolves every <c>{referencedId}</c> stub it can, preserves any
    /// inline items unchanged, and emits the resolved array under the
    /// un-prefixed name.
    /// </summary>
    private bool TryResolveDetachedArray(
        JObject parent,
        string baseName,
        IReadOnlyDictionary<string, JObject> objects,
        out JObject result)
    {
        result = parent;
        var atName = "@" + baseName;
        JArray? arr = parent[baseName] as JArray ?? parent[atName] as JArray;
        if (arr == null) return false;

        bool usedAt = parent[atName] != null && parent[baseName] == null;

        var inlinedDv = new JArray();
        bool anyResolved = false;
        foreach (var item in arr)
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
        // Only mutate when we either resolved at least one stub or had to
        // migrate the field from `@baseName` -> `baseName`.
        if (!anyResolved && !usedAt) return false;
        if (usedAt) BumpAtPrefixHit(atName);

        var clone = (JObject)parent.DeepClone();
        clone.Remove(atName);
        clone[baseName] = inlinedDv;
        result = clone;
        return true;
    }

    private void BakeLeaf(JObject geoObj, string? layerPath, long? layerColor, BakeState state)
    {
        var spType = geoObj["speckle_type"]?.Value<string>() ?? "unknown";
        var orbitId = geoObj["id"]?.Value<string>() ?? "?";

        // v0.1.15: resolve detached renderMaterial + dechunk textureCoordinates.
        // Same private helpers used by TraverseAndBakeChild for the rest of
        // the wire-format normalisation; both are no-ops on already-resolved
        // payloads, so calling here covers the root-as-leaf path too.
        if (TryResolveDetachedSingle(geoObj, "renderMaterial", state.Objects, out var rmInlined))
            geoObj = rmInlined;
        if (TryDechunkTextureCoordinates(geoObj, state.Objects, out var uvResolved))
            geoObj = uvResolved;

        // v0.1.16: defensively resolve `@displayValue` array stubs on the leaf
        // itself so the RhinoObject-material lookup below sees inline mesh
        // objects (and so the display-mesh fallback in the converter has
        // something to render when there is no rawEncoding). For leaves
        // reached via TraverseAndBakeChild this is a no-op (already resolved
        // there); for a RhinoObject root that walks directly into BakeLeaf it
        // is the only resolution pass that runs.
        if (TryResolveDetachedArray(geoObj, "displayValue", state.Objects, out var dvResolvedLeaf))
            geoObj = dvResolvedLeaf;

        // Resolve the render material up-front (inline -> @-prefixed ->
        // displayValue[0]) so we can decide HOW to bake textured native
        // geometry below. ResolveMaterialJObject is a pure read.
        var (rmJObj, matSource) = ResolveMaterialJObject(geoObj, state.Objects);

        // TEXTURE-MAPPING FIX (v0.1.19): a textured native object (Brep /
        // Extrusion / SubD wrapped in a RhinoDataObject) round-trips its
        // `.3dm` payload by default. Rhino then auto-generates
        // surface-parameterisation UVs for the render mesh, which DO NOT match
        // the authored bitmap mapping the ORBIT viewer renders with — the
        // texture shows up but is oriented/scaled wrong. The display meshes
        // carry the exact `textureCoordinates` the viewer used, so when the
        // material references a texture AND we have display-mesh UVs, bake the
        // UV-carrying display mesh instead of the native surface. This makes
        // Rhino reproduce the viewer's mapping 1:1 (both use lower-left,
        // V-up mesh UVs, so no flip/transform is needed). Non-textured
        // geometry keeps the byte-for-byte native round-trip.
        global::Rhino.Geometry.GeometryBase? geometry;
        bool preferDisplayMesh = rmJObj is not null
            && MaterialHasTexture(rmJObj)
            && HasDisplayMeshUVs(geoObj);
        if (preferDisplayMesh)
        {
            geometry = state.Converter.ConvertDisplayMeshOnly(geoObj);
            if (geometry != null)
                RhinoApp.WriteLine(
                    $"[ORBIT] tex-map: id={orbitId} baked display-mesh (with UVs) " +
                    "instead of native surface so the bitmap maps like the viewer");
            else
                geometry = state.Converter.Convert(geoObj); // display mesh unusable -> native
        }
        else
        {
            geometry = state.Converter.Convert(geoObj);
        }
        if (geometry == null)
        {
            state.SkippedCount++;
            var msg = $"Skipped unsupported / unconvertible object: type='{spType}' id={orbitId}";
            state.Warnings.Add(msg);
            RhinoApp.WriteLine($"[ORBIT] bake skip: {msg}");
            return;
        }

        // UV diagnostic for meshes — confirms textureCoordinates either
        // arrived inline or made it through the dechunk pass. The
        // numbers correlate with the [ORBIT] uv: dechunked ... lines
        // emitted by TryDechunkTextureCoordinates for the same object.
        if (geometry is global::Rhino.Geometry.Mesh m)
        {
            var uvPairs = m.TextureCoordinates.Count;
            var verts   = m.Vertices.Count;
            var applied = uvPairs == verts && uvPairs > 0
                ? "applied=ok"
                : uvPairs == 0 ? "applied=none" : $"applied=skipped(count {uvPairs}!=verts {verts})";
            RhinoApp.WriteLine(
                $"[ORBIT] uv: mesh id={orbitId} vertices={verts} uvPairs={uvPairs} {applied}");
        }

        int layerIdx = EnsureLayer(state.Doc, layerPath ?? "ORBIT Receive", layerColor);
        state.LayersTouched.Add(layerIdx);

        var attrs = new ObjectAttributes
        {
            LayerIndex = layerIdx,
        };

        // v0.1.15: assign per-object Rhino material when the leaf carries
        // a renderMaterial. The OrbitMaterialConverter caches by ORBIT
        // material id so reused materials hit a single doc.Materials
        // entry. ApplyMaterialColor still runs as a fall-back: it
        // populates the per-object ObjectColor from renderMaterial.diffuse
        // for materials we couldn't build into a real Rhino material
        // (e.g. when MaterialConverter is null after a prefetch failure).
        //
        // v0.1.16: extend the material lookup to RhinoObject DataObjects.
        // The producer (RhinoBrepConverter.BuildWrapper, called by
        // RhinoBrepConverter / RhinoExtrusionConverter / RhinoSubDConverter /
        // RhinoSurfaceConverter) wraps native Rhino geometry in a
        // `Objects.Data.DataObject:Objects.Data.RhinoObject` carrying:
        //   - `rawEncoding` (or detached `@rawEncoding`): single-object .3dm
        //     bytes for byte-for-byte native round-trip
        //   - `displayValue` (or detached `@displayValue`): per-face mesh
        //     fragments tessellated from the same Brep, each one carrying
        //     ITS OWN `renderMaterial` (and `colorSource`, `layerPath`,
        //     `layerColor`, etc.) — set inside
        //     `RhinoMeshConverter.AttachRenderMaterial`
        // The wrapper itself NEVER carries `renderMaterial`. So when the
        // .3dm payload decodes successfully (the path that produces
        // `Extrusion` / `Brep` Rhino objects), v0.1.15's inline-only lookup
        // missed every material on the model. The v0.1.16 fix borrows the
        // material from the first display-value mesh, matching the
        // producer's intent: every face under one wrapper shares one
        // material in the source Rhino doc, so any displayValue mesh's
        // renderMaterial is the right one to assign to the round-tripped
        // native geometry. ResolveMaterialJObject handles the lookup
        // (inline -> @-prefixed -> displayValue[0]).
        int? matIdx = null;
        // rmJObj / matSource were resolved up-front (see the texture-mapping
        // bake decision above) so the geometry path could react to textures.
        var matOrbitId = rmJObj?["id"]?.Value<string>()
                      ?? rmJObj?["applicationId"]?.Value<string>();

        // Per-object diagnostic so the next debugger doesn't have to fetch
        // raw wire JSON to figure out where the material came from.
        var hasDv = (geoObj["displayValue"] ?? geoObj["@displayValue"]) is JArray dvDiag
            ? dvDiag.Count
            : (geoObj["displayValue"] ?? geoObj["@displayValue"]) is JObject ? 1 : 0;
        if (spType.Contains("RhinoObject"))
        {
            var hasRaw = (geoObj["rawEncoding"] ?? geoObj["@rawEncoding"]) is JObject;
            RhinoApp.WriteLine(
                $"[ORBIT] rhinoobj: id={orbitId} type='{geoObj["type"]?.Value<string>() ?? "?"}' " +
                $"has-rawEncoding={hasRaw} displayValue-count={hasDv} " +
                $"material-source={matSource}");
        }
        else if (rmJObj is not null)
        {
            RhinoApp.WriteLine(
                $"[ORBIT] material-walk: id={orbitId} type='{spType}' source={matSource}");
        }

        if (rmJObj is not null && state.MaterialConverter is not null)
        {
            try
            {
                matIdx = state.MaterialConverter.GetOrCreateMaterialIndex(
                    rmJObj, state.Objects, state.Doc);
            }
            catch (Exception ex)
            {
                RhinoApp.WriteLine(
                    $"[ORBIT] material: id={matOrbitId ?? "?"} build failed: {ex.Message}");
                matIdx = null;
            }
        }

        if (matIdx.HasValue)
        {
            attrs.MaterialIndex  = matIdx.Value;
            attrs.MaterialSource = ObjectMaterialSource.MaterialFromObject;
            if (!string.IsNullOrEmpty(matOrbitId))
            {
                try { attrs.SetUserString("ORBIT_renderMaterialId", matOrbitId); }
                catch { /* attribute store is best-effort */ }
            }
        }
        else
        {
            ApplyMaterialColor(geoObj, attrs);
        }

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
                $"[ORBIT] bake: type='{spType}' id={orbitId} -> layer '{layerFullPath}' " +
                $"geom={geometry.GetType().Name}" +
                (matIdx.HasValue ? $" matIdx={matIdx.Value}" : ""));
        }
        else
        {
            state.SkippedCount++;
            var msg = $"RhinoDoc.Objects.Add returned empty Guid for type='{spType}' id={orbitId}";
            state.Warnings.Add(msg);
            RhinoApp.WriteLine($"[ORBIT] bake skip: {msg}");
        }
    }

    /// <summary>
    /// Texture-reference fields a <c>RenderMaterial</c> may carry. Mirrors the
    /// slots recognised by <c>OrbitMaterialConverter</c>; used only to decide
    /// whether a textured leaf should be baked from its UV-carrying display
    /// mesh (see the texture-mapping fix in <see cref="BakeLeaf"/>).
    /// </summary>
    private static readonly string[] MaterialTextureFields =
    {
        "diffuseTexture", "baseColorTexture",
        "emissiveTexture", "pbrEmissionTexture",
        "metallicRoughnessTexture", "roughnessTexture",
        "metalnessTexture", "metallicTexture",
        "normalTexture", "bumpTexture", "opacityTexture",
    };

    /// <summary>
    /// True when the material references at least one texture (a non-empty
    /// blob string, or a stub object). Accepts both bare and <c>@</c>-prefixed
    /// field names.
    /// </summary>
    private static bool MaterialHasTexture(JObject rm)
    {
        foreach (var field in MaterialTextureFields)
        {
            var t = rm[field] ?? rm["@" + field];
            if (t == null) continue;
            if (t.Type == JTokenType.String && !string.IsNullOrWhiteSpace(t.Value<string>()))
                return true;
            if (t.Type == JTokenType.Object) // detached / nested texture reference
                return true;
        }
        return false;
    }

    /// <summary>
    /// True when the object exposes at least one <c>displayValue</c> mesh that
    /// carries a non-empty <c>textureCoordinates</c> array (inline). These are
    /// the authored UVs the viewer renders with; if present we prefer them over
    /// Rhino's auto-generated surface UVs for textured native geometry.
    /// </summary>
    private static bool HasDisplayMeshUVs(JObject geoObj)
    {
        var dv = geoObj["displayValue"] ?? geoObj["@displayValue"];
        IEnumerable<JToken> items = dv switch
        {
            JArray arr   => arr,
            JObject one  => new[] { (JToken)one },
            _            => Array.Empty<JToken>(),
        };
        foreach (var item in items)
        {
            if (item is not JObject mesh) continue;
            var uv = mesh["textureCoordinates"] ?? mesh["@textureCoordinates"];
            if (uv is JArray ua && ua.Count > 0)
                return true;
        }
        return false;
    }

    /// <summary>
    /// Find the best <c>renderMaterial</c> <see cref="JObject"/> for a
    /// baked leaf. Tries (in order):
    ///
    /// <list type="number">
    ///   <item><description>
    ///     The leaf's own <c>renderMaterial</c> / <c>@renderMaterial</c>.
    ///     This is the path Mesh / Brep / Curve / etc. leaves emitted by
    ///     <c>RhinoMeshConverter.AttachRenderMaterial</c> on the send side
    ///     follow — material is inline on the leaf.
    ///   </description></item>
    ///   <item><description>
    ///     The first inline <c>displayValue</c> mesh's
    ///     <c>renderMaterial</c> / <c>@renderMaterial</c>. This is the path
    ///     <c>RhinoDataObject</c> wrappers
    ///     (<c>Objects.Data.DataObject:Objects.Data.RhinoObject</c>) follow:
    ///     the wrapper carries native <c>.3dm</c> bytes plus a list of
    ///     display-mesh fragments, and each fragment owns the material.
    ///     The producer (<c>RhinoBrepConverter.BuildWrapper</c>) tessellates
    ///     a single Rhino object into per-face fragments — they all share
    ///     the parent object's material — so borrowing the material from
    ///     <c>displayValue[0]</c> matches the source Rhino doc's per-object
    ///     material assignment.
    ///   </description></item>
    ///   <item><description>
    ///     First inline <c>displayValue</c> mesh referenced via
    ///     <c>{referencedId}</c> stub that resolves through
    ///     <paramref name="objects"/>. v0.1.16's prior call to
    ///     <see cref="TryResolveDetachedArray"/> in <see cref="BakeLeaf"/>
    ///     should have inlined these already, but the resolver runs again
    ///     defensively against any stub that survived.
    ///   </description></item>
    /// </list>
    ///
    /// <para>
    /// Returns the material JObject (or null when none found) plus a
    /// short tag describing where the material was sourced from. The tag
    /// is logged in the per-leaf diagnostic line so a future bug report
    /// makes the lookup decision visible without fetching wire JSON.
    /// </para>
    /// </summary>
    private static (JObject? material, string source) ResolveMaterialJObject(
        JObject geoObj, IReadOnlyDictionary<string, JObject> objects)
    {
        // 1. Inline on the leaf itself (Mesh / Brep / Curve / ...).
        if (geoObj["renderMaterial"] is JObject rmInline)
            return (rmInline, "inline");
        if (geoObj["@renderMaterial"] is JObject rmInlineAt)
            return (rmInlineAt, "inline@");

        // 2. Borrow from displayValue[0].renderMaterial. RhinoObject
        // wrappers never carry a material on the wrapper itself; the
        // material lives on each per-face display mesh fragment.
        var dvToken = geoObj["displayValue"] ?? geoObj["@displayValue"];

        // displayValue is either an array of mesh objects (post-resolve) or
        // an array of {referencedId} stubs (when TryResolveDetachedArray
        // hasn't been called yet for this leaf).
        if (dvToken is JArray dvArr && dvArr.Count > 0)
        {
            foreach (var item in dvArr)
            {
                var dvMesh = item as JObject;
                if (dvMesh is null) continue;

                // Inline mesh — read the material directly.
                if (dvMesh["renderMaterial"] is JObject dvRm)
                    return (dvRm, "displayValue[0].renderMaterial");
                if (dvMesh["@renderMaterial"] is JObject dvRmAt)
                    return (dvRmAt, "displayValue[0].@renderMaterial");

                // Stub — resolve and recheck. This path catches the case
                // where a producer detached displayValue items individually
                // (rare; the connector + PRISM both detach the whole array).
                var refId = dvMesh["referencedId"]?.Value<string>();
                if (!string.IsNullOrEmpty(refId)
                    && objects.TryGetValue(refId!, out var resolvedDv))
                {
                    if (resolvedDv["renderMaterial"] is JObject resolvedRm)
                        return (resolvedRm, "displayValue[0].ref.renderMaterial");
                    if (resolvedDv["@renderMaterial"] is JObject resolvedRmAt)
                        return (resolvedRmAt, "displayValue[0].ref.@renderMaterial");
                }
            }
        }
        else if (dvToken is JObject dvSingle)
        {
            // Single-object displayValue (rare; some Speckle Python variants).
            if (dvSingle["renderMaterial"] is JObject dvRm)
                return (dvRm, "displayValue.renderMaterial");
            if (dvSingle["@renderMaterial"] is JObject dvRmAt)
                return (dvRmAt, "displayValue.@renderMaterial");
            var refId = dvSingle["referencedId"]?.Value<string>();
            if (!string.IsNullOrEmpty(refId)
                && objects.TryGetValue(refId!, out var resolvedDv))
            {
                if (resolvedDv["renderMaterial"] is JObject resolvedRm)
                    return (resolvedRm, "displayValue.ref.renderMaterial");
                if (resolvedDv["@renderMaterial"] is JObject resolvedRmAt)
                    return (resolvedRmAt, "displayValue.ref.@renderMaterial");
            }
        }

        return (null, "none");
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
        //
        // v0.1.14: also check `@`-prefixed variants — PRISM-uploaded payloads
        // emit `@elements`, `@displayValue`, `@objects`, etc., because the
        // monorepo SDK marks those properties as detached (Speckle's
        // `[DetachProperty]` convention preserves the `@` prefix on the wire
        // and the ORBIT server stores it as-is). See the comment on
        // CollectionChildProperties above.
        foreach (var prop in CollectionChildProperties)
        {
            var arr = (node[prop] ?? node["@" + prop]) as JArray;
            if (arr is { Count: > 0 })
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

    private IEnumerable<JToken> EnumerateCollectionChildren(JObject collection)
    {
        foreach (var prop in CollectionChildProperties)
        {
            // v0.1.14: prefer the bare name (connector-native shape) but
            // fall back to the `@`-prefixed variant (PRISM / monorepo SDK
            // shape). See CollectionChildProperties comment for why both
            // shapes co-exist on the same server.
            var bare = collection[prop];
            var atToken = bare == null ? collection["@" + prop] : null;
            var token = bare ?? atToken;
            if (token is JArray arr)
            {
                if (atToken != null) BumpAtPrefixHit("@" + prop);
                foreach (var c in arr) yield return c;
            }
            else if (token is JObject one)
            {
                if (atToken != null) BumpAtPrefixHit("@" + prop);
                yield return one;
            }
        }
    }

    // -- @-prefix diagnostic counter ----------------------------------------
    //
    // Bumped once per detected `@`-prefixed property name during a receive so
    // the user / operator can confirm at a glance that the v0.1.14 fix is
    // doing the work (otherwise a successful PRISM receive looks identical to
    // a successful connector receive). Reset at the start of every receive.

    private readonly Dictionary<string, int> _atPrefixHits = new(StringComparer.Ordinal);

    private void BumpAtPrefixHit(string name)
    {
        _atPrefixHits.TryGetValue(name, out var n);
        _atPrefixHits[name] = n + 1;
    }

    private void ResetAtPrefixHits() => _atPrefixHits.Clear();

    private void LogAtPrefixSummary()
    {
        if (_atPrefixHits.Count == 0) return;
        var summary = string.Join(", ", _atPrefixHits
            .OrderByDescending(kv => kv.Value)
            .Select(kv => $"{kv.Key}={kv.Value}"));
        RhinoApp.WriteLine(
            "[ORBIT] traverse: resolved {0} `@`-prefixed detached propert{1} ({2}) — payload " +
            "uses Speckle detach convention (PRISM / monorepo SDK shape).",
            _atPrefixHits.Count, _atPrefixHits.Count == 1 ? "y" : "ies", summary);
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
