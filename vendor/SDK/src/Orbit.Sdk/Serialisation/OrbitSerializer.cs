using System.Security.Cryptography;
using System.Text;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Orbit.Objects.Base;

namespace Orbit.Sdk.Serialisation;

/// <summary>
/// Serialises an ORBIT object tree to JSON, computing deterministic MD5 content
/// hashes for each object (matching the Speckle server's 32-char object ID format)
/// and building the <c>__closure</c> table on the root.
///
/// Detachment is property-name driven, exactly as Speckle does it:
///   <list type="bullet">
///     <item>A property named with a leading <c>@</c> (e.g. <c>@elements</c>) is DETACHED:
///       each <c>speckle_type</c>'d child becomes its own DB row and is replaced inline
///       with a stub <c>{ "referencedId": "...", "speckle_type": "reference" }</c>.</item>
///     <item>A property without the <c>@</c> prefix (e.g. <c>views</c>, <c>origin</c>,
///       <c>renderMaterial</c>) is INLINE: the child stays inside the parent's JSON. Inline
///       objects still get an <c>id</c> field (MD5 of their content), but they are NOT added
///       to the closure table.</item>
///   </list>
/// </summary>
public class OrbitSerializer
{
    /// <summary>
    /// Reserved. Kept for backward compatibility — current detach logic is purely
    /// property-name driven (the <c>@</c> prefix), not size-based.
    /// </summary>
    public int DetachThresholdBytes { get; set; } = 1024;

    private readonly Dictionary<string, string> _serialisedObjects = new();
    private readonly Dictionary<string, int> _closure = new();

    /// <summary>
    /// Serialise the root object and all descendants.
    /// Returns a dictionary of { id → json } for the root plus every detached descendant.
    /// Each stored JSON blob includes its own <c>id</c> field. The root object's
    /// <see cref="OrbitBase.Id"/> is set before returning.
    /// </summary>
    public async Task<Dictionary<string, string>> SerialiseAsync(
        OrbitBase root,
        CancellationToken ct = default)
    {
        _serialisedObjects.Clear();
        _closure.Clear();

        var rootJObj = JObject.FromObject(root, JsonSerializer.Create(OrbitJsonSettings.Default));
        await ProcessNodeAsync(rootJObj, currentDetachDepth: 0, parentIsDetached: false, ct);

        rootJObj["__closure"] = JToken.FromObject(_closure);
        rootJObj.Remove("id");
        var rootId = ComputeHash(rootJObj.ToString(Formatting.None));
        root.Id = rootId;
        rootJObj["id"] = rootId;

        _serialisedObjects[rootId] = rootJObj.ToString(Formatting.None);
        return new Dictionary<string, string>(_serialisedObjects);
    }

    /// <summary>
    /// Depth-first walk. <paramref name="parentIsDetached"/> tells us whether the current
    /// node lives directly inside a property whose name starts with <c>@</c> — only then
    /// does a <c>speckle_type</c>'d object get extracted into its own DB row.
    /// </summary>
    private async Task ProcessNodeAsync(
        JToken token,
        int currentDetachDepth,
        bool parentIsDetached,
        CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();

        if (token is JObject jObj)
        {
            foreach (var prop in jObj.Properties().ToList())
            {
                bool childIsDetached = prop.Name.StartsWith("@");
                int childDepth = childIsDetached ? currentDetachDepth + 1 : currentDetachDepth;
                await ProcessNodeAsync(prop.Value, childDepth, childIsDetached, ct);
            }

            if (jObj.ContainsKey("speckle_type") && !jObj.ContainsKey("referencedId"))
            {
                var forHash = (JObject)jObj.DeepClone();
                forHash.Remove("id");
                var id = ComputeHash(forHash.ToString(Formatting.None));

                if (parentIsDetached && currentDetachDepth > 0)
                {
                    forHash["id"] = id;
                    _serialisedObjects[id] = forHash.ToString(Formatting.None);
                    _closure[id] = currentDetachDepth;

                    jObj.RemoveAll();
                    jObj["referencedId"] = id;
                    jObj["speckle_type"] = "reference";
                }
                else
                {
                    jObj["id"] = id;
                }
            }
        }
        else if (token is JArray jArr)
        {
            foreach (var item in jArr)
                await ProcessNodeAsync(item, currentDetachDepth, parentIsDetached, ct);
        }

        await Task.CompletedTask;
    }

    /// <summary>
    /// Compute a deterministic MD5 hex hash of a JSON string.
    /// Matches the Speckle server's 32-character object ID format.
    /// </summary>
    public static string ComputeHash(string json)
    {
        var bytes = Encoding.UTF8.GetBytes(json);
        var hash  = MD5.HashData(bytes);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }
}
