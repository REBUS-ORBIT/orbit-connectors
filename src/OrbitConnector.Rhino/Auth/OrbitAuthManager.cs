using System.Net;
using System.Security.Cryptography;
using System.Text;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using OrbitConnector.Rhino.Models;

namespace OrbitConnector.Rhino.Auth;

/// <summary>
/// Handles OAuth2 PKCE authentication against the ORBIT server.
///
/// Flow:
/// 1. Generate code challenge + verifier
/// 2. Open system browser to server auth URL
/// 3. Listen on local port 29364 for the OAuth callback
/// 4. Exchange code for access token
/// 5. Validate token via activeUser GraphQL query
/// </summary>
public class OrbitAuthManager
{
    private const int CALLBACK_PORT = 29364;

    private readonly HttpClient _http;
    private readonly ServerConfig _config;

    public OrbitAuthManager(ServerConfig config)
    {
        _config = config;
        _http = new HttpClient();
    }

    /// <summary>
    /// Run the full OAuth2 PKCE flow. Returns the access token on success.
    /// </summary>
    public async Task<string> AuthenticateAsync(ServerTarget target, CancellationToken ct = default)
    {
        var serverUrl = _config.GetUrl(target);
        var appId     = _config.GetAppId(target);

        var (challenge, verifier) = GenerateChallenge();
        var callbackUrl = $"http://localhost:{CALLBACK_PORT}/";

        // Open browser to server auth page
        var authUrl = $"{serverUrl}/authn/verify/{appId}/{challenge}";
        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
        {
            FileName = authUrl,
            UseShellExecute = true
        });

        // Wait for callback
        var code = await WaitForCallbackAsync(ct);

        // Exchange code for token
        var token = await ExchangeCodeAsync(serverUrl, appId, code, verifier, callbackUrl, ct);

        return token;
    }

    private async Task<string> WaitForCallbackAsync(CancellationToken ct)
    {
        using var listener = new HttpListener();
        listener.Prefixes.Add($"http://localhost:{CALLBACK_PORT}/");
        listener.Start();

        // Wait for the single OAuth callback request
        var context = await Task.Run(() => listener.GetContext(), ct);
        var code = context.Request.QueryString["access_code"]
            ?? throw new OrbitAuthException("OAuth callback missing access_code");

        // Respond to browser
        var response = context.Response;
        var msg = Encoding.UTF8.GetBytes("<html><body><h2>ORBIT: Authenticated. You may close this tab.</h2></body></html>");
        response.ContentLength64 = msg.Length;
        response.ContentType = "text/html";
        await response.OutputStream.WriteAsync(msg, ct);
        response.Close();
        listener.Stop();

        return code;
    }

    private async Task<string> ExchangeCodeAsync(
        string serverUrl, string appId, string code,
        string verifier, string callbackUrl, CancellationToken ct)
    {
        var payload = JsonConvert.SerializeObject(new
        {
            appId,
            appSecret = verifier,
            accessCode = code,
            challenge = verifier
        });

        var content = new StringContent(payload, Encoding.UTF8, "application/json");
        var response = await _http.PostAsync($"{serverUrl}/auth/token", content, ct);
        response.EnsureSuccessStatusCode();

        var json = await response.Content.ReadAsStringAsync(ct);
        var obj = JObject.Parse(json);
        return obj["token"]?.Value<string>()
            ?? throw new OrbitAuthException("Token exchange returned no token");
    }

    private static (string challenge, string verifier) GenerateChallenge()
    {
        var verifier  = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32))
            .Replace("+", "-").Replace("/", "_").TrimEnd('=');
        var hash      = SHA256.HashData(Encoding.ASCII.GetBytes(verifier));
        var challenge = Convert.ToBase64String(hash)
            .Replace("+", "-").Replace("/", "_").TrimEnd('=');
        return (challenge, verifier);
    }
}

public class OrbitAuthException : Exception
{
    public OrbitAuthException(string message) : base(message) { }
}
