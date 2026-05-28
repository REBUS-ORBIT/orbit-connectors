namespace Orbit.Sdk.Transport;

/// <summary>
/// Disk-based transport for local testing and offline caching.
/// Stores each object as a separate JSON file named {id}.json under <see cref="RootPath"/>.
/// </summary>
public class LocalTransport : IOrbitTransport
{
    public string RootPath { get; }
    public string TransportName => $"LocalTransport [{RootPath}]";

    public LocalTransport(string? rootPath = null)
    {
        RootPath = rootPath ?? Path.Combine(Path.GetTempPath(), "orbit_local_transport");
        Directory.CreateDirectory(RootPath);
    }

    public Task<string> SaveObjectAsync(string objectId, string objectJson, CancellationToken ct = default)
    {
        var path = Path.Combine(RootPath, $"{objectId}.json");
        File.WriteAllText(path, objectJson);
        return Task.FromResult(objectId);
    }

    public async Task SaveObjectBatchAsync(
        IEnumerable<(string id, string json)> objects,
        IProgress<int>? progress = null,
        CancellationToken ct = default)
    {
        int count = 0;
        foreach (var (id, json) in objects)
        {
            ct.ThrowIfCancellationRequested();
            await SaveObjectAsync(id, json, ct);
            progress?.Report(++count);
        }
    }

    public Task<string?> GetObjectAsync(string objectId, CancellationToken ct = default)
    {
        var path = Path.Combine(RootPath, $"{objectId}.json");
        return Task.FromResult(File.Exists(path) ? File.ReadAllText(path) : (string?)null);
    }

    public Task<bool> HasObjectAsync(string objectId, CancellationToken ct = default)
    {
        var path = Path.Combine(RootPath, $"{objectId}.json");
        return Task.FromResult(File.Exists(path));
    }

    public void Dispose() { /* nothing to dispose */ }
}
