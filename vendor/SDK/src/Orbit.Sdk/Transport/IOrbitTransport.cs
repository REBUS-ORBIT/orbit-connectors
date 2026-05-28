namespace Orbit.Sdk.Transport;

/// <summary>
/// Abstraction over ORBIT object storage.
/// Implementations include <see cref="ServerTransport"/> (HTTP to ORBIT server)
/// and <see cref="LocalTransport"/> (disk-based, for testing and offline cache).
/// </summary>
public interface IOrbitTransport : IDisposable
{
    /// <summary>Store a single serialised object. Returns the object id.</summary>
    Task<string> SaveObjectAsync(string objectId, string objectJson, CancellationToken ct = default);

    /// <summary>Store a batch of serialised objects efficiently.</summary>
    Task SaveObjectBatchAsync(
        IEnumerable<(string id, string json)> objects,
        IProgress<int>? progress = null,
        CancellationToken ct = default);

    /// <summary>Fetch a single serialised object by id. Returns null if not found.</summary>
    Task<string?> GetObjectAsync(string objectId, CancellationToken ct = default);

    /// <summary>Check whether an object exists without fetching its content.</summary>
    Task<bool> HasObjectAsync(string objectId, CancellationToken ct = default);

    /// <summary>Friendly name for logging and UI display.</summary>
    string TransportName { get; }
}
