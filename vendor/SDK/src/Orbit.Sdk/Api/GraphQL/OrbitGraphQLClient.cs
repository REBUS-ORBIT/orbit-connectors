using System.Net.Http.Headers;
using System.Text;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace Orbit.Sdk.Api.GraphQL;

/// <summary>
/// Lightweight GraphQL client using HttpClient.
/// Handles query/mutation execution and navigates nested JSON paths to extract results.
/// </summary>
public class OrbitGraphQLClient
{
    private readonly HttpClient _http;
    private readonly string _graphqlUrl;

    public OrbitGraphQLClient(string serverUrl, string authToken)
    {
        _graphqlUrl = $"{serverUrl.TrimEnd('/')}/graphql";
        _http = new HttpClient();
        _http.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", authToken);
        _http.DefaultRequestHeaders.Accept.Add(
            new MediaTypeWithQualityHeaderValue("application/json"));
    }

    public async Task<T?> QueryAsync<T>(string query, string dataPath,
        CancellationToken ct = default, object? variables = null)
    {
        var data = await ExecuteAsync(query, variables, ct);
        return Navigate<T>(data, dataPath);
    }

    public async Task<List<T>> QueryListAsync<T>(string query, string dataPath,
        CancellationToken ct = default, object? variables = null)
    {
        var data = await ExecuteAsync(query, variables, ct);
        var token = NavigateToken(data, dataPath);
        return token?.ToObject<List<T>>() ?? new List<T>();
    }

    public async Task<T> MutateAsync<T>(string mutation, string dataPath,
        CancellationToken ct = default, object? variables = null)
    {
        var data = await ExecuteAsync(mutation, variables, ct);
        return Navigate<T>(data, dataPath)
            ?? throw new InvalidOperationException($"Mutation returned null at '{dataPath}'");
    }

    private async Task<JObject> ExecuteAsync(string query, object? variables, CancellationToken ct)
    {
        var body = JsonConvert.SerializeObject(new { query, variables });
        var content = new StringContent(body, Encoding.UTF8, "application/json");
        var response = await _http.PostAsync(_graphqlUrl, content, ct);
        response.EnsureSuccessStatusCode();

        var json = await response.Content.ReadAsStringAsync(ct);
        var result = JObject.Parse(json);

        if (result["errors"] is JArray errors && errors.Count > 0)
        {
            var msgs = string.Join("; ", errors.Select(e => e["message"]?.Value<string>()));
            throw new OrbitApiException($"GraphQL error: {msgs}");
        }

        return result["data"] as JObject
            ?? throw new OrbitApiException("GraphQL response missing 'data'");
    }

    private static T? Navigate<T>(JObject data, string path)
    {
        var token = NavigateToken(data, path);
        return token == null ? default : token.ToObject<T>();
    }

    private static JToken? NavigateToken(JObject data, string path)
    {
        JToken? current = data;
        foreach (var segment in path.Split('.'))
        {
            current = current?[segment];
            if (current == null) return null;
        }
        return current;
    }
}

public class OrbitApiException : Exception
{
    public OrbitApiException(string message) : base(message) { }
}
