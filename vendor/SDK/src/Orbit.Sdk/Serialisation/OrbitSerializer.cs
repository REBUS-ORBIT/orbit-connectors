using System.Security.Cryptography;
using System.Text;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Orbit.Objects.Base;

namespace Orbit.Sdk.Serialisation;

/// <summary>
/// Serialises an ORBIT object tree to JSON, computing deterministic SHA-256 content
/// hashes for each object and building the __closure table on the root.
///
/// Detachment: objects above <see cref="DetachThresholdBytes"/> are stored as separate
/// objects and replaced in the parent with a reference token { "referencedId": "..." }.
/// </summary>
public class OrbitSerializer
{
    /// <summary>Objects larger than this (serialised bytes) are stored separately.</summary>
    public int DetachThresholdBytes { get; set; } = 1024;

    private readonly Dictionary<string, string> _serialisedObjects = new();
    private readonly Dictionary<string, int> _closure = new();

    /// <summary>
    /// Serialise the root object and all descendants.
    /// Returns a dictionary of { id → json } for all objects (root + detached children).
    /// The root object will have its __closure populated.
    /// </summary>
    public async Task<Dictionary<string, string>> SerialiseAsync(
        OrbitBase root,
        CancellationToken ct = default)
    {
        _serialisedObjects.Clear();
        _closure.Clear();

        await SerialiseObjectAsync(root, depth: 0, ct);

        // Attach closure to root
        root.Closure = new Dictionary<string, int>(_closure);

        // Re-serialise root now that closure is set
        var rootJson = JsonConvert.SerializeObject(root, OrbitJsonSettings.Default);
        var rootId = ComputeHash(rootJson);
        root.Id = rootId;
        _serialisedObjects[rootId] = rootJson;

        return new Dictionary<string, string>(_serialisedObjects);
    }

    private async Task<string> SerialiseObjectAsync(OrbitBase obj, int depth, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();

        // Walk all JObject properties to find nested OrbitBase instances
        var jObj = JObject.FromObject(obj, JsonSerializer.Create(OrbitJsonSettings.Default));
        await WalkAndDetachAsync(jObj, depth + 1, ct);

        var json = jObj.ToString(Formatting.None);
        var id = ComputeHash(json);
        obj.Id = id;

        // Register in closure at this depth
        _closure[id] = depth;
        _serialisedObjects[id] = json;

        return id;
    }

    private async Task WalkAndDetachAsync(JToken token, int depth, CancellationToken ct)
    {
        if (token is JObject jObj)
        {
            // Check if this looks like a nested OrbitBase
            if (jObj.ContainsKey("speckle_type") && !jObj.ContainsKey("referencedId"))
            {
                var nestedJson = jObj.ToString(Formatting.None);
                if (nestedJson.Length > DetachThresholdBytes)
                {
                    var nestedId = ComputeHash(nestedJson);
                    _closure[nestedId] = depth;
                    _serialisedObjects[nestedId] = nestedJson;

                    // Replace in-place with reference
                    jObj.RemoveAll();
                    jObj["referencedId"] = nestedId;
                    return;
                }
            }

            foreach (var prop in jObj.Properties().ToList())
                await WalkAndDetachAsync(prop.Value, depth, ct);
        }
        else if (token is JArray jArr)
        {
            foreach (var item in jArr)
                await WalkAndDetachAsync(item, depth, ct);
        }

        await Task.CompletedTask;
    }

    /// <summary>Compute a deterministic SHA-256 hex hash of a JSON string.</summary>
    public static string ComputeHash(string json)
    {
        var bytes = Encoding.UTF8.GetBytes(json);
        var hash = SHA256.HashData(bytes);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }
}
