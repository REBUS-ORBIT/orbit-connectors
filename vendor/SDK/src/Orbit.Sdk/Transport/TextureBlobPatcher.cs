using Newtonsoft.Json.Linq;
using Orbit.Objects.Base;

namespace Orbit.Sdk.Transport;

/// <summary>
/// Walks an <see cref="OrbitBase"/> object tree and replaces every
/// <c>@blob:SHA256HEX</c> placeholder string with the corresponding server-assigned
/// short blob id from the <paramref name="hashToServerId"/> map.
///
/// Call this AFTER <see cref="OrbitBlobUploader.UploadAsync"/> and BEFORE
/// <see cref="Serialisation.OrbitSerializer.SerialiseAsync"/> so that content-hash
/// computation runs over the final (patched) JSON containing server ids.
/// </summary>
public static class TextureBlobPatcher
{
    private const string BlobPrefix = "@blob:";

    /// <summary>
    /// Patches all blob reference strings in <paramref name="root"/> and all
    /// descendants (via <see cref="OrbitBase.DynamicProperties"/> and typed collections).
    /// </summary>
    public static void Patch(OrbitBase root, IReadOnlyDictionary<string, string> hashToServerId)
    {
        if (hashToServerId.Count == 0) return;
        PatchObject(root, hashToServerId);
    }

    private static void PatchObject(OrbitBase obj, IReadOnlyDictionary<string, string> map)
    {
        if (obj.DynamicProperties != null)
        {
            foreach (var key in obj.DynamicProperties.Keys.ToList())
            {
                var token = obj.DynamicProperties[key];
                if (token != null)
                    obj.DynamicProperties[key] = PatchToken(token, map);
            }
        }

        // Recurse into OrbitObject children
        if (obj is Orbit.Objects.Base.OrbitObject col)
        {
            if (col.Elements != null)
                foreach (var child in col.Elements)
                    PatchObject(child, map);

            if (col.DisplayValue != null)
                foreach (var child in col.DisplayValue)
                    PatchObject(child, map);
        }

        // Recurse into RhinoDataObject display meshes. Brep / Extrusion / SubD
        // sends are wrapped in a RhinoDataObject (speckle_type
        // "Objects.Data.DataObject:Objects.Data.RhinoObject"), and the textured
        // renderMaterial lives on each mesh in its DisplayValue list — NOT on the
        // wrapper itself. RhinoDataObject derives from OrbitBase (not OrbitObject),
        // so without this branch the walker never descends into the meshes and the
        // "@blob:<localSHA256>" placeholders are never rewritten to the server blob
        // id, leaving the viewer with an unresolvable texture reference.
        if (obj is Orbit.Objects.Data.RhinoDataObject dataObj && dataObj.DisplayValue != null)
            foreach (var child in dataObj.DisplayValue)
                PatchObject(child, map);

        // Recurse into typed mesh render materials
        if (obj is Orbit.Objects.Geometry.Mesh mesh && mesh.RenderMaterial != null)
            PatchRenderMaterial(mesh.RenderMaterial, map);
    }

    private static void PatchRenderMaterial(
        Orbit.Objects.Other.RenderMaterial rm,
        IReadOnlyDictionary<string, string> map)
    {
        rm.BaseColorTexture   = PatchBlobRef(rm.BaseColorTexture,   map);
        rm.DiffuseTexture     = PatchBlobRef(rm.DiffuseTexture,     map);
        rm.EmissiveTexture    = PatchBlobRef(rm.EmissiveTexture,    map);
        rm.PbrEmissionTexture = PatchBlobRef(rm.PbrEmissionTexture, map);
        rm.RoughnessTexture   = PatchBlobRef(rm.RoughnessTexture,   map);
        rm.MetalnessTexture   = PatchBlobRef(rm.MetalnessTexture,   map);
        rm.NormalTexture      = PatchBlobRef(rm.NormalTexture,      map);
        rm.OpacityTexture     = PatchBlobRef(rm.OpacityTexture,     map);

        // Also patch any dynamic texture slot set via the indexer
        if (rm.DynamicProperties != null)
        {
            foreach (var key in rm.DynamicProperties.Keys.ToList())
            {
                var token = rm.DynamicProperties[key];
                if (token != null)
                    rm.DynamicProperties[key] = PatchToken(token, map);
            }
        }
    }

    private static string? PatchBlobRef(string? value, IReadOnlyDictionary<string, string> map)
    {
        if (string.IsNullOrEmpty(value) || !value.StartsWith(BlobPrefix, StringComparison.Ordinal))
            return value;

        var hash = value[BlobPrefix.Length..];
        // Emit the BARE server blob id (no "@blob:" prefix). The ORBIT/Speckle
        // viewer hydrates texture URLs as `${blobBaseUrl}/${encodeURIComponent(value)}`
        // and expects a bare hash (see TEXTURE_VIEWER_FIX.md §4.1 and the Python
        // 3DConvert writer, which sets `rm[field] = blob_id`). Leaving the prefix
        // produces a "%40blob%3A…"-mangled URL that 404s.
        return map.TryGetValue(hash, out var serverId)
            ? serverId
            : value;
    }

    private static JToken? PatchToken(JToken? token, IReadOnlyDictionary<string, string> map)
    {
        if (token == null) return null;

        if (token.Type == JTokenType.String)
        {
            var s = token.Value<string>() ?? "";
            if (s.StartsWith(BlobPrefix, StringComparison.Ordinal))
            {
                var hash = s[BlobPrefix.Length..];
                if (map.TryGetValue(hash, out var serverId))
                    return serverId;   // bare blob id — see PatchBlobRef note
            }
            return token;
        }

        if (token.Type == JTokenType.Object)
        {
            var obj = (JObject)token;
            foreach (var prop in obj.Properties().ToList())
                obj[prop.Name] = PatchToken(prop.Value, map);
            return obj;
        }

        if (token.Type == JTokenType.Array)
        {
            var arr = (JArray)token;
            for (int i = 0; i < arr.Count; i++)
                arr[i] = PatchToken(arr[i], map)!;
            return arr;
        }

        return token;
    }
}
