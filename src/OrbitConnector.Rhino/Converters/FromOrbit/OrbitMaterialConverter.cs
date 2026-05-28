using System.Net.Http.Headers;
using Newtonsoft.Json.Linq;
using Rhino;
using Rhino.DocObjects;
using Rhino.Render;

namespace OrbitConnector.Rhino.Converters.FromOrbit;

/// <summary>
/// Converts ORBIT <c>Objects.Other.RenderMaterial</c> objects to Rhino
/// PBR <see cref="Material"/> instances, downloading any referenced
/// texture blobs from the ORBIT server and caching them on disk for
/// the duration of the receive session.
///
/// <para>
/// <b>Wire format consumed</b> (mirrors the producer side in
/// 3DConvert's <c>writer_speckle.py</c> and the connector's own
/// send path via <c>RhinoMaterialHelper.AttachTextures</c>):
/// </para>
///
/// <list type="bullet">
///   <item><description>
///     PBR scalars on the render material:
///     <c>diffuse</c> (ARGB long) -> <c>PhysicallyBased.BaseColor</c>,
///     <c>emissive</c> (ARGB long) -> <c>PhysicallyBased.Emission</c>,
///     <c>opacity</c> (double) -> <c>PhysicallyBased.Opacity</c>,
///     <c>metalness</c> / <c>metallic</c> -> <c>PhysicallyBased.Metallic</c>,
///     <c>roughness</c> -> <c>PhysicallyBased.Roughness</c>,
///     <c>emissiveIntensity</c> -> mirrored on the Rhino material when present.
///   </description></item>
///   <item><description>
///     Texture fields on the render material (all optional, any string
///     may be either <c>@blob:HASH</c> or a <c>{referencedId:"..."}</c>
///     stub):
///     <c>diffuseTexture</c> / <c>baseColorTexture</c> -> PBR base colour map,
///     <c>emissiveTexture</c> / <c>pbrEmissionTexture</c> -> PBR emission map,
///     <c>metallicRoughnessTexture</c> / <c>roughnessTexture</c> -> PBR roughness map,
///     <c>metalnessTexture</c> / <c>metallicTexture</c> -> PBR metallic map,
///     <c>normalTexture</c> / <c>bumpTexture</c> -> Rhino bump slot (no dedicated
///     PBR_Normal TextureType across all Rhino 8 minor versions; bump is a
///     compatible super-set),
///     <c>opacityTexture</c> -> PBR opacity map.
///   </description></item>
/// </list>
///
/// <para>
/// <b>Blob download</b> hits <c>GET /api/stream/{streamId}/blob/{blobId}</c>
/// with the same bearer token the receive pipeline is using. The
/// <c>blobId</c> is the 10-char server-assigned ID (NOT a SHA-256), so
/// no integrity check is performed against the response — there is no
/// canonical hash to compare against. Per-blob results are cached
/// keyed by blob id in <see cref="_blobToPath"/>; per-material results
/// are cached keyed by ORBIT object id in
/// <see cref="_materialIndexCache"/>.
/// </para>
///
/// <para>
/// <b>Pre-fetch strategy.</b> All blob downloads run in
/// <see cref="PrefetchBlobsAsync"/> before traversal begins, in
/// parallel (up to 4 concurrent). The synchronous bake phase then
/// only reads the on-disk cache — no async/blocking dance inside
/// <see cref="Pipeline.RhinoReceivePipeline.BakeLeaf"/>.
/// </para>
///
/// <para>
/// <b>Trade-offs documented for the next bug report:</b>
/// </para>
///
/// <list type="bullet">
///   <item><description>
///     <c>normalTexture</c> is plumbed into Rhino's <c>Bump</c>
///     texture slot rather than a dedicated PBR_Normal map. Rhino 8
///     PBR materials surface normal maps via the bump slot in the
///     viewport renderer, which matches the visual result for the
///     receive workflow; users wanting tangent-space normal-mapped
///     baking targets may need to re-author the material.
///   </description></item>
///   <item><description>
///     <c>metallicRoughnessTexture</c> (glTF-style packed MR) is
///     mapped to Rhino's <c>PBR_Roughness</c> slot. Rhino does not
///     unpack the G/B channels separately; the same texture is also
///     applied to the metallic slot if a dedicated metallic texture
///     is not present. This matches the visual approximation 3DConvert
///     ships from its OBJ/MTL/glTF pipeline.
///   </description></item>
///   <item><description>
///     When the wire <c>emissive</c> colour is opaque black
///     (<c>0xFF000000</c>) and an emissive texture is attached, the
///     Rhino material is left at black emissive — same producer-side
///     convention as the viewer fix in
///     <c>speckle-frontend-2-rebus:v2.4.3</c>. We do <em>not</em>
///     emit synthetic white emission to "boost" the texture (mirrors
///     the conservative emission policy in
///     <c>Converters/RhinoMaterialHelper.cs</c> on the send side).
///   </description></item>
/// </list>
/// </summary>
public sealed class OrbitMaterialConverter : IDisposable
{
    private const string BlobPrefix = "@blob:";

    private readonly HttpClient _http;
    private readonly string _serverUrl;
    private readonly string _projectId;
    private readonly string _tempDir;

    private readonly Dictionary<string, string> _blobToPath = new(StringComparer.Ordinal);
    private readonly HashSet<string> _missingBlobs        = new(StringComparer.Ordinal);
    private readonly Dictionary<string, int> _materialIndexCache = new(StringComparer.Ordinal);

    // Per-instance counters surfaced in the summary log line at end of receive.
    public int BlobsRequested { get; private set; }
    public int BlobsDownloaded { get; private set; }
    public int BlobsFromDisk { get; private set; }
    public int BlobsMissing => _missingBlobs.Count;
    public int MaterialsCreated { get; private set; }

    public OrbitMaterialConverter(string serverUrl, string projectId, string authToken)
    {
        _serverUrl = serverUrl.TrimEnd('/');
        _projectId = projectId;
        _http = new HttpClient
        {
            Timeout = TimeSpan.FromMinutes(5),
        };
        _http.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", authToken);
        // Per-project temp dir under %TEMP%\OrbitConnector\{projectId}\
        // survives a single receive session; not aggressively cleaned so
        // a second receive of the same project re-uses the same files.
        _tempDir = Path.Combine(Path.GetTempPath(), "OrbitConnector", projectId);
        Directory.CreateDirectory(_tempDir);
    }

    // -- Pre-fetch ----------------------------------------------------------

    /// <summary>
    /// Walks the entire object map, enumerates every blob id referenced
    /// by any known texture field on any object that looks like a
    /// render material, and downloads them all in parallel.
    /// </summary>
    public async Task PrefetchBlobsAsync(
        IReadOnlyDictionary<string, JObject> objects,
        IProgress<(int done, int total)>? progress,
        CancellationToken ct)
    {
        var blobIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var (_, obj) in objects)
            CollectBlobIdsFrom(obj, objects, blobIds, depth: 0);

        BlobsRequested = blobIds.Count;
        if (blobIds.Count == 0) return;

        RhinoApp.WriteLine(
            $"[ORBIT] material: prefetching {blobIds.Count} texture blob(s) " +
            $"from {_serverUrl}/api/stream/{_projectId}/blob/...");

        int done = 0;
        await Parallel.ForEachAsync(
            blobIds,
            new ParallelOptions
            {
                MaxDegreeOfParallelism = 4,
                CancellationToken = ct,
            },
            async (blobId, t) =>
            {
                var (path, fromDisk) = await TryDownloadBlobAsync(blobId, t);
                lock (_blobToPath)
                {
                    if (path != null)
                    {
                        _blobToPath[blobId] = path;
                        if (fromDisk) BlobsFromDisk++;
                        else BlobsDownloaded++;
                    }
                    else
                    {
                        _missingBlobs.Add(blobId);
                    }
                    done++;
                }
                progress?.Report((done, blobIds.Count));
            }).ConfigureAwait(false);

        RhinoApp.WriteLine(
            $"[ORBIT] material: prefetch done. downloaded={BlobsDownloaded} " +
            $"reused={BlobsFromDisk} missing={BlobsMissing}");
    }

    private static void CollectBlobIdsFrom(
        JToken? token,
        IReadOnlyDictionary<string, JObject> objects,
        HashSet<string> blobIds,
        int depth)
    {
        // Bound recursion against pathological payloads.
        if (depth > 24) return;
        if (token is null) return;

        if (token is JObject obj)
        {
            // Any JObject can be a render material; check known texture
            // fields and harvest their blob refs regardless of whether the
            // object is annotated as a RenderMaterial. We pay a few extra
            // string lookups per non-material object; in exchange we don't
            // depend on the producer setting `speckle_type` correctly.
            //
            // v0.1.16: also probe the `@`-prefixed variants. The monorepo
            // SDK (PRISM / visualiser shape) marks texture-bearing fields
            // as `[DetachProperty]` so they land on the wire as
            // `@diffuseTexture`, `@baseColorTexture`, etc. (same convention
            // CollectionChildProperties / RawEncoding follow). The bare
            // names alone missed every PRISM-uploaded texture in v0.1.15.
            foreach (var fieldName in TextureFieldNames)
            {
                var blobId = ExtractBlobId(obj[fieldName] ?? obj["@" + fieldName], objects);
                if (!string.IsNullOrEmpty(blobId))
                    blobIds.Add(blobId);
            }
            foreach (var prop in obj.Properties())
                CollectBlobIdsFrom(prop.Value, objects, blobIds, depth + 1);
        }
        else if (token is JArray arr)
        {
            foreach (var item in arr) CollectBlobIdsFrom(item, objects, blobIds, depth + 1);
        }
    }

    /// <summary>
    /// Pull the blob id out of either an <c>@blob:HASH</c> string or a
    /// <c>{referencedId:"..."}</c> stub. Returns null for any other token shape.
    /// </summary>
    private static string? ExtractBlobId(
        JToken? token,
        IReadOnlyDictionary<string, JObject> objects)
    {
        if (token is null) return null;

        if (token.Type == JTokenType.String)
        {
            var s = token.Value<string>() ?? "";
            if (s.StartsWith(BlobPrefix, StringComparison.Ordinal))
                return s.Substring(BlobPrefix.Length);
            return null;
        }

        if (token is JObject stub)
        {
            var refId = stub["referencedId"]?.Value<string>();
            if (string.IsNullOrEmpty(refId)) return null;

            // The referencedId may itself resolve to an object that
            // holds the actual @blob:HASH string in some legacy
            // payloads (PRISM's older Visualiser path), so follow one
            // hop before giving up.
            if (objects.TryGetValue(refId, out var resolved))
            {
                var inner = resolved["value"] ?? resolved["data"];
                if (inner is JValue v && v.Type == JTokenType.String)
                {
                    var s2 = v.Value<string>() ?? "";
                    if (s2.StartsWith(BlobPrefix, StringComparison.Ordinal))
                        return s2.Substring(BlobPrefix.Length);
                }
            }

            // Otherwise treat the referencedId itself as the blob id
            // (rarely correct, but harmless — the GET will 404 and the
            // material falls back to its colour-only form).
            return refId;
        }

        return null;
    }

    private async Task<(string? path, bool fromDisk)> TryDownloadBlobAsync(
        string blobId, CancellationToken ct)
    {
        // Reuse any prior on-disk copy regardless of extension. The
        // glob is bounded — temp dir is per-project so we never see
        // unrelated files.
        try
        {
            var existing = Directory.GetFiles(_tempDir, blobId + ".*")
                                    .FirstOrDefault();
            if (existing != null) return (existing, fromDisk: true);
        }
        catch { /* fall through to fresh download */ }

        var url = $"{_serverUrl}/api/stream/{_projectId}/blob/{blobId}";
        try
        {
            using var resp = await _http.GetAsync(url, ct).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode)
            {
                RhinoApp.WriteLine(
                    $"[ORBIT] texture: blobId={blobId} HTTP {(int)resp.StatusCode} {resp.ReasonPhrase}");
                return (null, false);
            }
            var bytes = await resp.Content.ReadAsByteArrayAsync(ct).ConfigureAwait(false);
            var ext = SniffExtension(bytes);
            var path = Path.Combine(_tempDir, blobId + ext);
            await File.WriteAllBytesAsync(path, bytes, ct).ConfigureAwait(false);
            RhinoApp.WriteLine(
                $"[ORBIT] texture: blobId={blobId} bytes={bytes.Length} ext={ext} cached=false");
            return (path, fromDisk: false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            RhinoApp.WriteLine($"[ORBIT] texture: blobId={blobId} download failed: {ex.Message}");
            return (null, false);
        }
    }

    private static string SniffExtension(byte[] bytes)
    {
        // PNG: 89 50 4E 47 0D 0A 1A 0A
        if (bytes.Length >= 8
            && bytes[0] == 0x89 && bytes[1] == 0x50
            && bytes[2] == 0x4E && bytes[3] == 0x47)
            return ".png";
        // JPEG: FF D8 FF
        if (bytes.Length >= 3
            && bytes[0] == 0xFF && bytes[1] == 0xD8 && bytes[2] == 0xFF)
            return ".jpg";
        // GIF: 47 49 46 38 (GIF8)
        if (bytes.Length >= 4
            && bytes[0] == 0x47 && bytes[1] == 0x49
            && bytes[2] == 0x46 && bytes[3] == 0x38)
            return ".gif";
        // WebP: 'RIFF' .... 'WEBP'
        if (bytes.Length >= 12
            && bytes[0] == 0x52 && bytes[1] == 0x49
            && bytes[2] == 0x46 && bytes[3] == 0x46
            && bytes[8] == 0x57 && bytes[9] == 0x45
            && bytes[10] == 0x42 && bytes[11] == 0x50)
            return ".webp";
        // BMP: 42 4D
        if (bytes.Length >= 2 && bytes[0] == 0x42 && bytes[1] == 0x4D)
            return ".bmp";
        // TIFF (little- or big-endian)
        if (bytes.Length >= 4
            && ((bytes[0] == 0x49 && bytes[1] == 0x49
                && bytes[2] == 0x2A && bytes[3] == 0x00)
             || (bytes[0] == 0x4D && bytes[1] == 0x4D
                && bytes[2] == 0x00 && bytes[3] == 0x2A)))
            return ".tif";
        // Unknown — default to PNG; Rhino's TextureLoader handles it as a
        // bitmap regardless of extension once we hand it a real file path.
        return ".png";
    }

    // -- Material build -----------------------------------------------------

    /// <summary>
    /// Resolve the <paramref name="rmJObj"/> (which may be a stub) to a
    /// concrete render-material object and convert it to a Rhino
    /// <see cref="Material"/> registered on <paramref name="doc"/>.
    /// Returns the new (or cached) material index, or null when no
    /// material can be built.
    /// </summary>
    public int? GetOrCreateMaterialIndex(
        JObject? rmJObj,
        IReadOnlyDictionary<string, JObject> objects,
        RhinoDoc doc)
    {
        if (rmJObj is null) return null;

        var resolved = ResolveStub(rmJObj, objects);
        if (resolved is null) return null;

        var matId = resolved["id"]?.Value<string>()
                 ?? resolved["applicationId"]?.Value<string>();
        if (!string.IsNullOrEmpty(matId)
            && _materialIndexCache.TryGetValue(matId!, out var existing))
            return existing;

        var mat = BuildRhinoMaterial(resolved, out var diag);
        if (mat is null) return null;

        var idx = doc.Materials.Add(mat);
        if (idx < 0)
        {
            RhinoApp.WriteLine($"[ORBIT] material: RhinoDoc.Materials.Add returned {idx} for {diag}");
            return null;
        }

        if (!string.IsNullOrEmpty(matId))
            _materialIndexCache[matId!] = idx;
        MaterialsCreated++;

        RhinoApp.WriteLine($"[ORBIT] material: id={matId ?? "?"} {diag} -> rhinoIdx={idx}");
        return idx;
    }

    private static JObject? ResolveStub(
        JObject node, IReadOnlyDictionary<string, JObject> objects)
    {
        for (int hops = 0; hops < 8; hops++)
        {
            var refId = node["referencedId"]?.Value<string>();
            if (string.IsNullOrEmpty(refId)) return node;
            if (!objects.TryGetValue(refId!, out var resolved)) return node;
            if (resolved == node) return node;
            node = resolved;
        }
        return node;
    }

    private static readonly string[] TextureFieldNames =
    {
        "diffuseTexture",
        "baseColorTexture",
        "emissiveTexture",
        "pbrEmissionTexture",
        "metallicRoughnessTexture",
        "roughnessTexture",
        "metalnessTexture",
        "metallicTexture",
        "normalTexture",
        "bumpTexture",
        "opacityTexture",
    };

    private Material? BuildRhinoMaterial(JObject rm, out string diag)
    {
        var matName = rm["name"]?.Value<string>() ?? "ORBIT Material";
        var diffuse   = ReadArgbLong(rm["diffuse"]);
        var emissive  = ReadArgbLong(rm["emissive"]);
        var opacity   = ReadDouble(rm["opacity"], 1.0);
        var metalness = ReadDouble(rm["metalness"], ReadDouble(rm["metallic"], 0.0));
        var roughness = ReadDouble(rm["roughness"], 0.5);
        var emissiveIntensity = rm["emissiveIntensity"]?.Value<double?>();

        var mat = new Material { Name = matName };

        // Promote to PBR. ToPhysicallyBased is the supported Rhino 8
        // path and is idempotent for materials that are already PBR.
        try { mat.ToPhysicallyBased(); }
        catch { /* legacy material is fine too */ }

        var pbr = mat.PhysicallyBased;

        if (diffuse.HasValue)
        {
            var c = System.Drawing.Color.FromArgb((int)(uint)diffuse.Value);
            mat.DiffuseColor = c;
            if (pbr is not null)
                pbr.BaseColor = new global::Rhino.Display.Color4f(c);
        }

        if (emissive.HasValue)
        {
            var c = System.Drawing.Color.FromArgb((int)(uint)emissive.Value);
            mat.EmissionColor = c;
            if (pbr is not null)
                pbr.Emission = new global::Rhino.Display.Color4f(c);
        }

        if (pbr is not null)
        {
            pbr.Metallic  = Math.Clamp(metalness, 0.0, 1.0);
            pbr.Roughness = Math.Clamp(roughness, 0.0, 1.0);
            pbr.Opacity   = Math.Clamp(opacity, 0.0, 1.0);
            // emissiveIntensity is a wire-format float that maps to glTF's
            // emissiveStrength. Rhino 8's PhysicallyBasedMaterial does not
            // expose a dedicated scalar for it across all minor versions,
            // so we leave intensity baked into the Emission colour and
            // skip the setter. Re-evaluate when McNeel exposes a stable
            // emissive-strength property.
            _ = emissiveIntensity;
        }

        if (opacity < 1.0)
            mat.Transparency = 1.0 - opacity;

        // Texture slots — apply each known field, recording which ones
        // landed and which ones were absent / missing-on-server.
        var slotsApplied = new List<string>();
        var slotsMissing = new List<string>();

        TryApply(mat, pbr, rm, slotsApplied, slotsMissing,
            slot: "basecolor",
            texType: TextureType.PBR_BaseColor,
            fallbackBitmap: true,
            fieldNames: new[] { "diffuseTexture", "baseColorTexture" });

        TryApply(mat, pbr, rm, slotsApplied, slotsMissing,
            slot: "emission",
            texType: TextureType.PBR_Emission,
            fallbackBitmap: false,
            fieldNames: new[] { "emissiveTexture", "pbrEmissionTexture" });

        TryApply(mat, pbr, rm, slotsApplied, slotsMissing,
            slot: "roughness",
            texType: TextureType.PBR_Roughness,
            fallbackBitmap: false,
            fieldNames: new[] { "metallicRoughnessTexture", "roughnessTexture" });

        TryApply(mat, pbr, rm, slotsApplied, slotsMissing,
            slot: "metallic",
            texType: TextureType.PBR_Metallic,
            fallbackBitmap: false,
            fieldNames: new[] { "metalnessTexture", "metallicTexture", "metallicRoughnessTexture" });

        TryApply(mat, pbr, rm, slotsApplied, slotsMissing,
            slot: "normal",
            texType: TextureType.Bump,
            fallbackBitmap: false,
            fieldNames: new[] { "normalTexture", "bumpTexture" });

        // Rhino 8's PhysicallyBasedMaterial exposes alpha through
        // PBR_Alpha (Rhino 8.0+) on most builds; the legacy Transparency
        // texture slot is the cross-version fallback. We attempt PBR_Alpha
        // first via the type lookup below and fall back to Transparency
        // if the enum value is missing on this Rhino build.
        TryApply(mat, pbr, rm, slotsApplied, slotsMissing,
            slot: "opacity",
            texType: ResolveOpacityTextureType(),
            fallbackBitmap: false,
            fieldNames: new[] { "opacityTexture" });

        mat.CommitChanges();

        diag = $"name='{matName}' textures=[{string.Join(",", slotsApplied)}]"
             + (slotsMissing.Count > 0 ? $" missing=[{string.Join(",", slotsMissing)}]" : "")
             + $" baseColor={diffuse?.ToString("X8") ?? "-"} metallic={metalness:F2} roughness={roughness:F2}";
        return mat;
    }

    private void TryApply(
        Material mat,
        global::Rhino.DocObjects.PhysicallyBasedMaterial? pbr,
        JObject rm,
        List<string> slotsApplied,
        List<string> slotsMissing,
        string slot,
        TextureType texType,
        bool fallbackBitmap,
        string[] fieldNames)
    {
        foreach (var fieldName in fieldNames)
        {
            var token = rm[fieldName] ?? rm["@" + fieldName];
            var blobId = TokenBlobId(token);
            if (string.IsNullOrEmpty(blobId)) continue;

            if (!_blobToPath.TryGetValue(blobId!, out var path))
            {
                // Field references a blob we couldn't download. Surface
                // it in the diagnostic so users can correlate with the
                // texture: failed log lines from the prefetch phase.
                if (!slotsMissing.Contains(slot))
                    slotsMissing.Add(slot);
                continue;
            }

            var tex = new Texture
            {
                FileName = path,
                Enabled  = true,
                TextureType = texType,
            };

            // Prefer the PBR slot when the material is in PBR mode.
            var assigned = false;
            if (pbr is not null)
            {
                try
                {
                    pbr.SetTexture(tex, texType);
                    assigned = true;
                }
                catch { /* fall through */ }
            }

            if (!assigned)
            {
                try { mat.SetTexture(tex, texType); assigned = true; }
                catch { /* fall through */ }
            }

            if (!assigned && fallbackBitmap)
            {
                try { mat.SetBitmapTexture(path); assigned = true; }
                catch { /* nothing else to try */ }
            }

            if (assigned && !slotsApplied.Contains(slot))
                slotsApplied.Add(slot);
            return;
        }
    }

    private static string? TokenBlobId(JToken? token)
    {
        if (token is null) return null;
        if (token.Type == JTokenType.String)
        {
            var s = token.Value<string>() ?? "";
            if (s.StartsWith(BlobPrefix, StringComparison.Ordinal))
                return s.Substring(BlobPrefix.Length);
            return null;
        }
        if (token is JObject stub)
        {
            var refId = stub["referencedId"]?.Value<string>();
            return string.IsNullOrEmpty(refId) ? null : refId;
        }
        return null;
    }

    private static long? ReadArgbLong(JToken? token)
    {
        if (token is null || token.Type == JTokenType.Null) return null;
        try { return token.Value<long?>(); }
        catch { return null; }
    }

    private static double ReadDouble(JToken? token, double fallback)
    {
        if (token is null || token.Type == JTokenType.Null) return fallback;
        try
        {
            var v = token.Value<double?>();
            return v.HasValue && !double.IsNaN(v.Value) ? v.Value : fallback;
        }
        catch { return fallback; }
    }

    /// <summary>
    /// Resolve the right <see cref="TextureType"/> enum value for the
    /// opacity / alpha slot. Rhino 8 introduced <c>PBR_Alpha</c> at some
    /// point during the 8.x line; older builds only expose
    /// <c>Transparency</c>. We probe via <see cref="Enum.TryParse{TEnum}(string, out TEnum)"/>
    /// so this code compiles on the lowest-common Rhino 8 SDK surface
    /// and degrades to the legacy slot at runtime when needed.
    /// </summary>
    private static TextureType ResolveOpacityTextureType()
    {
        if (Enum.TryParse<TextureType>("PBR_Alpha", out var pbrAlpha))
            return pbrAlpha;
        if (Enum.TryParse<TextureType>("Transparency", out var transparency))
            return transparency;
        // Last-ditch fallback. Bitmap is always available across Rhino 7/8;
        // the texture won't render as a true alpha mask but won't crash either.
        return TextureType.Bitmap;
    }

    public void Dispose() => _http.Dispose();
}
