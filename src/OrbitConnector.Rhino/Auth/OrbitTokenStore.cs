using System.Security.Cryptography;
using System.Text;
using Rhino.PlugIns;
using OrbitConnector.Rhino.Models;

namespace OrbitConnector.Rhino.Auth;

/// <summary>
/// Persists auth tokens in Rhino plugin settings (per-user, per-machine).
/// Keys are MD5 hashes of the server URL to keep them stable and non-human-readable.
/// </summary>
public class OrbitTokenStore
{
    private readonly PlugIn _plugin;

    public string? LastProjectId { get => Get("LastProjectId"); set => Set("LastProjectId", value); }
    public string? LastModelName  { get => Get("LastModelName");  set => Set("LastModelName", value); }
    public ServerTarget LastTarget
    {
        get => Enum.TryParse<ServerTarget>(Get("LastTarget"), out var t) ? t : ServerTarget.Prod;
        set => Set("LastTarget", value.ToString());
    }
    public string ThemeMode { get => Get("ThemeMode") ?? "dark"; set => Set("ThemeMode", value); }

    public OrbitTokenStore(PlugIn plugin)
    {
        _plugin = plugin;
    }

    public string? GetToken(string serverUrl) =>
        _plugin.Settings.GetString(HashKey(serverUrl, "token"), string.Empty) is { Length: > 0 } t ? t : null;

    public void SaveToken(string serverUrl, string token) =>
        _plugin.Settings.SetString(HashKey(serverUrl, "token"), token);

    public void ClearToken(string serverUrl) =>
        _plugin.Settings.SetString(HashKey(serverUrl, "token"), string.Empty);

    public bool HasToken(string serverUrl) =>
        GetToken(serverUrl) != null;

    private string? Get(string key) =>
        _plugin.Settings.GetString(key, string.Empty) is { Length: > 0 } v ? v : null;

    private void Set(string key, string? value) =>
        _plugin.Settings.SetString(key, value ?? string.Empty);

    private static string HashKey(string serverUrl, string suffix)
    {
        var bytes = MD5.HashData(Encoding.UTF8.GetBytes(serverUrl));
        return Convert.ToHexString(bytes).ToLowerInvariant() + "_" + suffix;
    }
}
