using Newtonsoft.Json;
using Newtonsoft.Json.Serialization;

namespace Orbit.Sdk.Serialisation;

public static class OrbitJsonSettings
{
    public static JsonSerializerSettings Default => new()
    {
        NullValueHandling = NullValueHandling.Ignore,
        DefaultValueHandling = DefaultValueHandling.Ignore,
        ContractResolver = new CamelCasePropertyNamesContractResolver(),
        Formatting = Formatting.None,
        TypeNameHandling = TypeNameHandling.None,
    };

    public static JsonSerializer CreateSerializer() =>
        JsonSerializer.Create(Default);
}
