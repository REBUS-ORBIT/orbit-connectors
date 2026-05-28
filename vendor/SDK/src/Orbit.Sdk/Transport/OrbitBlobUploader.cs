using System.Net.Http.Headers;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace Orbit.Sdk.Transport;

/// <summary>
/// Uploads texture / blob files to the ORBIT server and returns a mapping from
/// the local SHA-256 content hash to the short blob id assigned by the server.
///
/// The server endpoint is:
///   <c>POST {serverUrl}/api/stream/{streamId}/blob</c>
/// Each file is sent as a multipart/form-data field named <c>"files"</c> with
/// the SHA-256 hex digest as the filename.
///
/// The server responds with:
/// <code>
/// { "uploadResults": [ { "blobId": "abc123", "fileName": "sha256hex" }, … ] }
/// </code>
/// </summary>
public sealed class OrbitBlobUploader : IDisposable
{
    private readonly HttpClient _http;
    private readonly string _uploadUrl;
    private readonly Action<string>? _log;

    public OrbitBlobUploader(
        string serverUrl,
        string streamId,
        string authToken,
        Action<string>? log = null)
    {
        _uploadUrl = $"{serverUrl.TrimEnd('/')}/api/stream/{streamId}/blob";
        _log = log;

        _http = new HttpClient();
        _http.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", authToken);
        _http.Timeout = TimeSpan.FromMinutes(10);
    }

    /// <summary>
    /// Uploads all files in <paramref name="hashToFilePath"/> and returns a mapping
    /// from SHA-256 hex digest → server-assigned blob id.
    ///
    /// Files that fail to upload are silently skipped (the texture placeholder remains
    /// unresolved and the viewer will render the object's fallback diffuse colour instead).
    /// </summary>
    public async Task<Dictionary<string, string>> UploadAsync(
        IReadOnlyDictionary<string, string> hashToFilePath,
        CancellationToken ct = default)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (hashToFilePath.Count == 0) return result;

        using var form = new MultipartFormDataContent();

        foreach (var (hash, filePath) in hashToFilePath)
        {
            if (!File.Exists(filePath)) continue;

            var fileBytes = await File.ReadAllBytesAsync(filePath, ct);
            var fileContent = new ByteArrayContent(fileBytes);
            fileContent.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
            form.Add(fileContent, "files", hash);
        }

        try
        {
            var response = await _http.PostAsync(_uploadUrl, form, ct);
            if (!response.IsSuccessStatusCode)
            {
                var body = string.Empty;
                try { body = await response.Content.ReadAsStringAsync(ct); }
                catch { /* response body diagnostic only */ }
                _log?.Invoke(
                    $"[ORBIT] blob-upload: POST {_uploadUrl} -> " +
                    $"HTTP {(int)response.StatusCode} {response.ReasonPhrase}. " +
                    $"body={(body.Length > 200 ? body.Substring(0, 200) + "…" : body)}");
                return result;
            }

            var json = await response.Content.ReadAsStringAsync(ct);
            var jObj = JObject.Parse(json);
            var uploadResults = jObj["uploadResults"] as JArray;
            if (uploadResults == null)
            {
                _log?.Invoke(
                    $"[ORBIT] blob-upload: POST {_uploadUrl} -> 2xx but no `uploadResults` " +
                    "array in response body. Server rejected the multipart payload silently.");
                return result;
            }

            foreach (var item in uploadResults)
            {
                var blobId   = item["blobId"]?.Value<string>();
                var fileName = item["fileName"]?.Value<string>();
                if (!string.IsNullOrEmpty(blobId) && !string.IsNullOrEmpty(fileName))
                    result[fileName] = blobId;
            }
        }
        catch (Exception ex)
        {
            _log?.Invoke(
                $"[ORBIT] blob-upload: POST {_uploadUrl} threw {ex.GetType().Name}: {ex.Message}");
        }

        return result;
    }

    public void Dispose() => _http.Dispose();
}
