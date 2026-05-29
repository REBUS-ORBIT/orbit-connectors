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

        // Read the body BEFORE checking the status code. GraphQL validation
        // failures come back as HTTP 400 with a JSON body whose `errors[]`
        // carry the real reason (e.g. "Cannot query field X on type Y").
        // Calling EnsureSuccessStatusCode() first would discard that and throw
        // the opaque ".NET" 400 message.
        var json = await response.Content.ReadAsStringAsync(ct);

        if (!response.IsSuccessStatusCode)
            throw new OrbitApiException(
                $"GraphQL request failed ({(int)response.StatusCode} {response.StatusCode}): " +
                ExtractGraphQlErrors(json));

        var result = JObject.Parse(json);

        if (result["errors"] is JArray errors && errors.Count > 0)
            throw new OrbitApiException(errors[0]["message"]?.Value<string>() ?? "GraphQL error");

        return result["data"] as JObject
            ?? throw new OrbitApiException("GraphQL response missing 'data'");
    }

    /// <summary>
    /// Pulls human-readable messages out of a GraphQL error response body,
    /// falling back to the raw body when it isn't the expected JSON shape.
    /// </summary>
    private static string ExtractGraphQlErrors(string body)
    {
        try
        {
            var obj = JObject.Parse(body);
            if (obj["errors"] is JArray errs && errs.Count > 0)
            {
                var msgs = errs
                    .Select(e => e["message"]?.Value<string>() ?? e.ToString())
                    .Where(m => !string.IsNullOrWhiteSpace(m));
                var joined = string.Join("; ", msgs);
                if (!string.IsNullOrWhiteSpace(joined)) return joined;
            }
        }
        catch
        {
            // Body wasn't JSON — return it raw (trimmed) below.
        }
        return string.IsNullOrWhiteSpace(body) ? "(empty response body)" : body.Trim();
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
