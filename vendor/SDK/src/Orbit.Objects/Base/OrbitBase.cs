using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace Orbit.Objects.Base;

/// <summary>
/// Root base class for all ORBIT objects.
/// Every object in the ORBIT system derives from this.
/// The <see cref="Id"/> is a deterministic SHA-256 hash of the object's content,
/// enabling automatic deduplication across sends.
/// </summary>
public class OrbitBase
{
    /// <summary>
    /// Deterministic SHA-256 content hash. Set by the serialiser — do not set manually.
    /// Two objects with identical content will produce the same id.
    /// </summary>
    [JsonProperty("id")]
    public string? Id { get; set; }

    /// <summary>
    /// Stable identifier from the source application (e.g. Rhino object GUID).
    /// Used by proxies to reference objects, and by receive pipelines to match
    /// incoming objects against existing ones (Update receive mode).
    /// </summary>
    [JsonProperty("applicationId")]
    public string? ApplicationId { get; set; }

    /// <summary>
    /// Fully qualified ORBIT type name. Used for deserialisation dispatch.
    /// Kept as "speckle_type" on the wire for compatibility with the ORBIT server
    /// (which is built on Speckle infrastructure).
    /// </summary>
    [JsonProperty("speckle_type")]
    public virtual string OrbitType => GetType().FullName ?? GetType().Name;

    /// <summary>
    /// Flat closure table: maps every descendant object id to its depth in the tree.
    /// Built by the serialiser during send. Used by the server for efficient bulk
    /// object retrieval during receive — the client can fetch all children in one call.
    /// </summary>
    [JsonProperty("__closure")]
    public Dictionary<string, int>? Closure { get; set; }

    /// <summary>
    /// Dynamic / arbitrary properties not defined in the schema.
    /// Populated from source application data (e.g. Rhino UserStrings, UserDictionary).
    /// Also used during deserialisation when the type is unknown — all fields land here.
    /// </summary>
    [JsonExtensionData]
    public Dictionary<string, JToken?>? DynamicProperties { get; set; }

    /// <summary>
    /// Gets or sets a dynamic property by key.
    /// </summary>
    public object? this[string key]
    {
        get
        {
            DynamicProperties ??= new Dictionary<string, JToken?>();
            return DynamicProperties.TryGetValue(key, out var val) ? val : null;
        }
        set
        {
            DynamicProperties ??= new Dictionary<string, JToken?>();
            DynamicProperties[key] = value != null ? JToken.FromObject(value) : null;
        }
    }
}
