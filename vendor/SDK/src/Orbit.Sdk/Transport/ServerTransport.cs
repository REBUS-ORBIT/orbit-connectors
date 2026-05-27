using System.Net.Http.Headers;
using System.Text;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace Orbit.Sdk.Transport;

/// <summary>
/// HTTP transport to the ORBIT server (backed by Speckle infrastructure).
/// Uploads objects via POST /objects/{streamId} and downloads via GET /objects/{streamId}/{id}.
///
/// Batch upload: objects are POSTed in batches of up to <see cref="MaxBatchSizeBytes"/>
/// to stay within server limits and allow progress reporting.
/// </summary>
public class ServerTransport : IOrbitTransport
{
    private readonly HttpClient _http;
    private readonly string _serverUrl;
    private readonly string _streamId;

    /// <summary>Maximum batch payload size in bytes (default 1 MB).</summary>
    public int MaxBatchSizeBytes { get; set; } = 1_000_000;

    /// <summary>Maximum objects per batch (default 100).</summary>
    public int MaxBatchCount { get; set; } = 100;

    public string TransportName => $"ORBIT Server [{_serverUrl}]";

    public ServerTransport(string serverUrl, string streamId, string authToken)
    {
        _serverUrl = serverUrl.TrimEnd('/');
        _streamId  = streamId;
        _http = new HttpClient();
        _http.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", authToken);
    }

    public async Task<string> SaveObjectAsync(string objectId, string objectJson, CancellationToken ct = default)
    {
        await SaveObjectBatchAsync(new[] { (objectId, objectJson) }, null, ct);
        return objectId;
    }

    public async Task SaveObjectBatchAsync(
        IEnumerable<(string id, string json)> objects,
        IProgress<int>? progress = null,
        CancellationToken ct = default)
    {
        var batch = new List<(string id, string json)>();
        int batchBytes = 0;
        int total = 0;

        int batchIndex = 0;

        async Task FlushAsync()
        {
            if (batch.Count == 0) return;
            batchIndex++;
            var payload = "[" + string.Join(",", batch.Select(o => o.json)) + "]";

            // Speckle server parses multipart via busboy's 'file' event — the batch must be
            // sent as a file attachment (with filename), not a plain text form field.
            using var form       = new MultipartFormDataContent();
            var batchContent     = new ByteArrayContent(Encoding.UTF8.GetBytes(payload));
            batchContent.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/json");
            form.Add(batchContent, $"batch{batchIndex}", $"batch{batchIndex}");

            var url = $"{_serverUrl}/objects/{_streamId}";
            var response = await _http.PostAsync(url, form, ct);
            if (!response.IsSuccessStatusCode)
            {
                var body = await response.Content.ReadAsStringAsync(ct);
                throw new HttpRequestException(
                    $"Object upload failed ({(int)response.StatusCode}): {body}");
            }
            total += batch.Count;
            progress?.Report(total);
            batch.Clear();
            batchBytes = 0;
        }

        foreach (var (id, json) in objects)
        {
            ct.ThrowIfCancellationRequested();
            if (batch.Count >= MaxBatchCount || batchBytes + json.Length > MaxBatchSizeBytes)
                await FlushAsync();
            batch.Add((id, json));
            batchBytes += json.Length;
        }

        await FlushAsync();
    }

    public async Task<string?> GetObjectAsync(string objectId, CancellationToken ct = default)
    {
        var url = $"{_serverUrl}/objects/{_streamId}/{objectId}/single";
        var response = await _http.GetAsync(url, ct);
        if (!response.IsSuccessStatusCode) return null;
        return await response.Content.ReadAsStringAsync(ct);
    }

    public async Task<bool> HasObjectAsync(string objectId, CancellationToken ct = default)
    {
        // HEAD request to check existence without downloading content
        var url = $"{_serverUrl}/objects/{_streamId}/{objectId}/single";
        var request = new HttpRequestMessage(HttpMethod.Head, url);
        var response = await _http.SendAsync(request, ct);
        return response.IsSuccessStatusCode;
    }

    public void Dispose() => _http.Dispose();
}
