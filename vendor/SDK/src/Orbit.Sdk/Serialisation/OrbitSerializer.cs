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
///
/// The previous implementation detached every <c>speckle_type</c>'d node at depth &gt; 0
/// regardless of property name. That broke <c>views</c> (got detached and disappeared from
/// the viewer) and also failed to add <c>speckle_type: "reference"</c> to stubs.
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

        // Single pass: serialise the full C# tree to one JObject, then walk it,
        // detaching @-prefixed sub-objects and assigning content-hash ids inline.
        var rootJObj = JObject.FromObject(root, JsonSerializer.Create(OrbitJsonSettings.Default));
        await ProcessNodeAsync(rootJObj, currentDetachDepth: 0, parentIsDetached: false, ct);

        // Attach the global closure table to the root, then compute its final id.
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
    /// <paramref name="currentDetachDepth"/> is the closure depth a detached node would be
    /// stored at (only incremented when we cross a detach-marker property).
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
            // Walk children FIRST (depth-first) so they already have IDs / are
            // detached when the parent is hashed.
            foreach (var prop in jObj.Properties().ToList())
            {
                bool childIsDetached = prop.Name.StartsWith("@");
                int childDepth = childIsDetached ? currentDetachDepth + 1 : currentDetachDepth;
                await ProcessNodeAsync(prop.Value, childDepth, childIsDetached, ct);
            }

            // After children, deal with this object (skip arrays — handled in the JArray branch).
            if (jObj.ContainsKey("speckle_type") && !jObj.ContainsKey("referencedId"))
            {
                // Compute id over the content with the "id" field excluded.
                var forHash = (JObject)jObj.DeepClone();
                forHash.Remove("id");
                var id = ComputeHash(forHash.ToString(Formatting.None));

                if (parentIsDetached && currentDetachDepth > 0)
                {
                    // Detach: store as its own DB row, replace inline with a reference stub.
                    forHash["id"] = id;
                    _serialisedObjects[id] = forHash.ToString(Formatting.None);
                    _closure[id] = currentDetachDepth;

                    jObj.RemoveAll();
                    jObj["referencedId"] = id;
                    jObj["speckle_type"] = "reference";
                }
                else
                {
                    // Inline: keep contents, just assign the content-hash id.
                    jObj["id"] = id;
                }
            }
        }
        else if (token is JArray jArr)
        {
            // Array items inherit the parent property's detach context (e.g. items inside
            // an @elements array are themselves detach candidates, items inside a `views`
            // array are inline).
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
