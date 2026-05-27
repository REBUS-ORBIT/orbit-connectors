using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Orbit.Objects.Base;

namespace Orbit.Sdk.Serialisation;

/// <summary>
/// Deserialises ORBIT JSON back into typed objects.
/// Uses the "speckle_type" field to dispatch to the correct CLR type.
/// Falls back to <see cref="OrbitBase"/> with DynamicProperties populated
/// if the type is not registered.
/// </summary>
public class OrbitDeserializer
{
    private readonly Dictionary<string, Type> _typeRegistry = new();

    public OrbitDeserializer()
    {
        RegisterDefaultTypes();
    }

    /// <summary>Register all types from Orbit.Objects assemblies.</summary>
    private void RegisterDefaultTypes()
    {
        var assemblies = new[]
        {
            typeof(OrbitBase).Assembly,
            typeof(Objects.Geometry.Mesh).Assembly
        };

        foreach (var asm in assemblies.Distinct())
        {
            foreach (var type in asm.GetTypes())
            {
                if (type.IsAbstract || !type.IsSubclassOf(typeof(OrbitBase))) continue;
                var instance = (OrbitBase?)Activator.CreateInstance(type);
                if (instance != null)
                    _typeRegistry[instance.OrbitType] = type;
            }
        }
    }

    /// <summary>Register a custom type mapping.</summary>
    public void Register(string orbitType, Type clrType) =>
        _typeRegistry[orbitType] = clrType;

    /// <summary>
    /// Deserialise a JSON string to the appropriate OrbitBase subtype.
    /// </summary>
    public OrbitBase? Deserialise(string json)
    {
        var jObj = JObject.Parse(json);
        var typeName = jObj["speckle_type"]?.Value<string>();

        Type targetType = typeof(OrbitBase);
        if (typeName != null && _typeRegistry.TryGetValue(typeName, out var registeredType))
            targetType = registeredType;

        return (OrbitBase?)jObj.ToObject(targetType, OrbitJsonSettings.CreateSerializer());
    }

    /// <summary>
    /// Resolve a detached reference: if the token has "referencedId",
    /// fetch the object from the provided store and deserialise it.
    /// </summary>
    public async Task<OrbitBase?> ResolveReferenceAsync(
        JToken token,
        Func<string, Task<string?>> objectFetcher,
        CancellationToken ct = default)
    {
        if (token is JObject jObj && jObj.ContainsKey("referencedId"))
        {
            var refId = jObj["referencedId"]!.Value<string>()!;
            var refJson = await objectFetcher(refId);
            if (refJson == null) return null;
            return Deserialise(refJson);
        }

        return token is JObject obj2
            ? (OrbitBase?)obj2.ToObject(typeof(OrbitBase), OrbitJsonSettings.CreateSerializer())
            : null;
    }
}
