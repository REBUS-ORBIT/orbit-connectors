using System.Diagnostics;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Reflection;
using System.Threading;
using Eto.Forms;
using Orbit.Sdk.Api;
using Orbit.Sdk.Api.Models;
using Orbit.Sdk.Transport;
using OrbitConnector.Rhino.Auth;
using OrbitConnector.Rhino.Models;
using OrbitConnector.Rhino.Pipeline;
using Rhino;
using Rhino.UI;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace OrbitConnector.Rhino.UI;

[System.Runtime.InteropServices.Guid("21621060-21E4-4A81-8FF6-3E11FBEF5D04")]
public class OrbitEtoPanel : Panel, IPanel
{
    public static System.Guid PanelId => typeof(OrbitEtoPanel).GUID;

    // ── Update check (v0.1.2) ─────────────────────────────────────────────────

    /// <summary>GitHub URL the user is sent to when they want to download the latest release.</summary>
    private const string LatestReleasePageUrl =
        "https://github.com/REBUS-ORBIT/orbit-connectors/releases/latest";

    /// <summary>GitHub API endpoint that returns the latest release as JSON.</summary>
    private const string LatestReleaseApiUrl =
        "https://api.github.com/repos/REBUS-ORBIT/orbit-connectors/releases/latest";

    /// <summary>Process-wide HttpClient. Constructing one per click is an anti-pattern.</summary>
    private static readonly HttpClient _http = CreateUpdateCheckClient();

    // ── Core services ─────────────────────────────────────────────────────────

    private readonly ServerConfig     _config;
    private readonly OrbitTokenStore  _store;
    private readonly OrbitAuthManager _auth;
    private readonly RhinoSendPipeline    _pipeline        = new();
    private readonly RhinoReceivePipeline _receivePipeline = new();
    private CardStore _cardStore = null!;

    private OrbitClient? _client;
    private string _token     = string.Empty;
    private string _serverUrl = string.Empty;

    // ── WebView ───────────────────────────────────────────────────────────────

    private readonly WebView _webView;
    private bool _uiReady;

    // Tracks which document we last pushed cards for, so PanelShown only
    // re-pushes when the user actually switched Rhino documents. Without
    // this, every tab-switch in the Rhino panel host triggers SendCards
    // → JS renderCards → full DOM teardown, which wipes the user's
    // in-flight UI state (selected project/model, "converting geometry…"
    // progress, entered text, etc).
    private uint _lastSyncedDocSerial = 0;

    // ── Constructor ───────────────────────────────────────────────────────────

    public OrbitEtoPanel(uint documentSerialNumber)
    {
        _config = ServerConfig.Default;
        _store  = new OrbitTokenStore(OrbitConnectorPlugin.Instance!);
        _auth   = new OrbitAuthManager(_config);

        var activeDoc = RhinoDoc.ActiveDoc;
        _cardStore = activeDoc != null
            ? CardStore.LoadFromDocument(activeDoc)
            : (CardStore.Instance ?? new CardStore());
        _cardStore.CardsChanged += OnCardsChanged;

        _webView = new WebView();
        _webView.DocumentLoading += OnDocumentLoading;
        _webView.DocumentLoaded  += OnDocumentLoaded;

        Content = _webView;

        LoadHtml();
    }

    private static HttpClient CreateUpdateCheckClient()
    {
        var client = new HttpClient
        {
            Timeout = TimeSpan.FromSeconds(10),
        };
        // GitHub rate-limits anonymous requests harder when the User-Agent is
        // missing or generic; identifying as the connector is friendlier.
        client.DefaultRequestHeaders.UserAgent.ParseAdd(
            $"OrbitConnector-Rhino/{OrbitConnectorPlugin.Version}");
        client.DefaultRequestHeaders.Accept.Add(
            new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        return client;
    }

    // ── HTML loading ──────────────────────────────────────────────────────────

    private void LoadHtml()
    {
        const string resName = "OrbitConnector.Rhino.UI.wwwroot.index.html";
        try
        {
            using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(resName);
            if (stream != null)
            {
                using var reader = new System.IO.StreamReader(stream);
                var html = reader.ReadToEnd();
                // Write to temp file so the WebView can load it with a proper base URL
                var tmpDir  = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "orbit_connector");
                System.IO.Directory.CreateDirectory(tmpDir);
                var tmpPath = System.IO.Path.Combine(tmpDir, "index.html");
                System.IO.File.WriteAllText(tmpPath, html);
                _webView.Url = new Uri("file:///" + tmpPath.Replace('\\', '/'));
                return;
            }
        }
        catch { }

        // Fallback: minimal error page
        _webView.LoadHtml("<html><body style='background:#141414;color:#e8e8e8;font-family:sans-serif;padding:20px'>" +
                          "<p>ORBIT Connector could not load UI resources.</p></body></html>", new Uri("about:blank"));
    }

    // ── Message handling: JS → C# ─────────────────────────────────────────────

    private void OnDocumentLoading(object? sender, WebViewLoadingEventArgs e)
    {
        var url = e.Uri?.ToString() ?? "";
        if (!url.StartsWith("orbit://msg/", StringComparison.OrdinalIgnoreCase)) return;

        e.Cancel = true;

        // Parse: orbit://msg/{action}?d={json}
        var withoutScheme = url.Substring("orbit://msg/".Length);
        var qIdx    = withoutScheme.IndexOf('?');
        var action  = qIdx >= 0 ? withoutScheme.Substring(0, qIdx) : withoutScheme;
        var dataStr = "{}";
        if (qIdx >= 0)
        {
            var query = withoutScheme.Substring(qIdx + 1);
            foreach (var part in query.Split('&'))
            {
                if (part.StartsWith("d=", StringComparison.Ordinal))
                {
                    dataStr = Uri.UnescapeDataString(part.Substring(2));
                    break;
                }
            }
        }

        JObject data;
        try { data = JObject.Parse(dataStr); }
        catch { data = new JObject(); }

        // Dispatch on background thread so we don't block the browser
        Task.Run(() => DispatchMessage(action, data));
    }

    private async Task DispatchMessage(string action, JObject data)
    {
        try
        {
            switch (action)
            {
                case "ready":
                    _uiReady = true;
                    // v0.1.2: surface the connector version into the WebView so the
                    // header footer can render "v<X.Y.Z>" without a round-trip.
                    SendToJs(new { type = "version", version = OrbitConnectorPlugin.Version });
                    await TryRestoreSessionAsync();
                    break;

                case "login":
                    await HandleLoginAsync(data);
                    break;

                case "logout":
                    HandleLogout();
                    break;

                case "addCard":
                    AddCard(string.Equals(data.Value<string>("type"), "receive", StringComparison.OrdinalIgnoreCase)
                        ? CardType.Receive : CardType.Send);
                    break;

                case "removeCard":
                    RemoveCard(data.Value<string>("id") ?? "");
                    break;

                case "updateCard":
                    UpdateCardFromJs(data["card"] as JObject);
                    break;

                case "getProjects":
                    await GetProjectsAsync(data.Value<string>("cardId") ?? "");
                    break;

                case "getModels":
                    await GetModelsAsync(
                        data.Value<string>("cardId") ?? "",
                        data.Value<string>("projectId") ?? "");
                    break;

                case "createProject":
                    await CreateProjectAsync(
                        data.Value<string>("cardId") ?? "",
                        data.Value<string>("name") ?? "");
                    break;

                case "send":
                    await HandleSendAsync(data);
                    break;

                case "receive":
                    await HandleReceiveAsync(data);
                    break;

                case "requestLayers":
                    SendLayers();
                    break;

                case "captureSelection":
                    CaptureSelection(data.Value<string>("cardId") ?? "");
                    break;

                case "openUrl":
                    var urlToOpen = data.Value<string>("url") ?? "";
                    if (!string.IsNullOrEmpty(urlToOpen))
                        Application.Instance.AsyncInvoke(() =>
                            Process.Start(new ProcessStartInfo(urlToOpen) { UseShellExecute = true }));
                    break;

                case "checkUpdates":
                    // v0.1.2: GitHub-API-backed update check, mirrored back into JS so
                    // the header link can change its label / open a confirm dialog.
                    await HandleUpdateCheckAsync();
                    break;
            }
        }
        catch (Exception ex)
        {
            RhinoApp.WriteLine($"[OrbitConnector] dispatch error for '{action}': {ex.Message}");
        }
    }

    private void OnDocumentLoaded(object? sender, WebViewLoadedEventArgs e)
    {
        // Nothing needed here — JS calls orbit://msg/ready when it initialises
    }

    // ── C# → JS ──────────────────────────────────────────────────────────────

    private void SendToJs(object payload)
    {
        var json = JsonConvert.SerializeObject(payload);
        var escaped = json.Replace("\\", "\\\\").Replace("'", "\\'");
        Application.Instance.AsyncInvoke(() =>
        {
            try { _webView.ExecuteScript($"window.orbitReceive('{escaped}')"); }
            catch { }
        });
    }

    // ── Auth ──────────────────────────────────────────────────────────────────

    private async Task TryRestoreSessionAsync()
    {
        var url = _store.GetToken(_config.ProdUrl) != null ? _config.ProdUrl : _config.DevUrl;
        var token = _store.GetToken(url);
        if (string.IsNullOrEmpty(token)) return;
        await FinishLoginAsync(url, token);
    }

    private async Task HandleLoginAsync(JObject data)
    {
        var url    = (data.Value<string>("url") ?? "").TrimEnd('/');
        var method = data.Value<string>("method") ?? "oauth";
        if (string.IsNullOrEmpty(url)) return;

        try
        {
            string token;
            if (method == "pat")
            {
                token = data.Value<string>("token") ?? "";
                if (string.IsNullOrEmpty(token)) { SendToJs(new { type = "loginErr", message = "Token is empty." }); return; }
            }
            else
            {
                token = await _auth.AuthenticateAsync(
                    url.Contains("dev") ? ServerTarget.Dev : ServerTarget.Prod);
            }
            _store.SaveToken(url, token);
            await FinishLoginAsync(url, token);
        }
        catch (OperationCanceledException)
        {
            SendToJs(new { type = "loginErr", message = "Login cancelled." });
        }
        catch (Exception ex)
        {
            SendToJs(new { type = "loginErr", message = ex.Message });
        }
    }

    private async Task FinishLoginAsync(string url, string token)
    {
        try
        {
            var client = new OrbitClient(url, token);
            var user   = await client.GetActiveUserAsync();
            if (user == null)
            {
                _store.ClearToken(url);
                SendToJs(new { type = "loginErr", message = "Server rejected the token." });
                return;
            }
            _client    = client;
            _token     = token;
            _serverUrl = url;

            SendToJs(new
            {
                type      = "loginOk",
                name      = user.Name ?? user.Email,
                email     = user.Email,
                serverUrl = url
            });

            // Send current cards
            SendCards();
        }
        catch (Exception ex)
        {
            SendToJs(new { type = "loginErr", message = ex.Message });
        }
    }

    private void HandleLogout()
    {
        _store.ClearToken(_serverUrl);
        _client = null; _token = ""; _serverUrl = "";
        SendToJs(new { type = "logout" });
    }

    // ── Card management ───────────────────────────────────────────────────────

    private void AddCard(CardType type)
    {
        var card = new ConnectorCard { Type = type, Target = _store.LastTarget };
        _cardStore.AddCard(card);
        // CardsChanged fires → sends updated card list to JS
    }

    private void RemoveCard(string cardId)
    {
        _cardStore.RemoveCard(cardId);
    }

    private void UpdateCardFromJs(JObject? cardJson)
    {
        if (cardJson == null) return;
        var card = cardJson.ToObject<ConnectorCard>();
        if (card != null) _cardStore.UpdateCard(card);
    }

    private void OnCardsChanged(object? sender, EventArgs e) => SendCards();

    private void SendCards()
    {
        SendToJs(new { type = "cards", items = _cardStore.Cards });
    }

    // ── Projects / Models ─────────────────────────────────────────────────────

    private async Task GetProjectsAsync(string cardId)
    {
        if (_client == null) return;
        try
        {
            var projects = await _client.GetProjectsAsync();
            SendToJs(new
            {
                type   = "projects",
                cardId,
                items  = projects.Select(p => new { id = p.Id, name = p.Name })
            });
        }
        catch (Exception ex)
        {
            RhinoApp.WriteLine($"[OrbitConnector] getProjects: {ex.Message}");
        }
    }

    private async Task GetModelsAsync(string cardId, string projectId)
    {
        if (_client == null || string.IsNullOrEmpty(projectId)) return;
        try
        {
            var models = await _client.GetModelsAsync(projectId);
            SendToJs(new
            {
                type   = "models",
                cardId,
                items  = models.Select(m => new { id = m.Id, name = m.Name })
            });
        }
        catch (Exception ex)
        {
            RhinoApp.WriteLine($"[OrbitConnector] getModels: {ex.Message}");
        }
    }

    private async Task CreateProjectAsync(string cardId, string name)
    {
        if (_client == null || string.IsNullOrEmpty(name)) return;
        try
        {
            var project = await _client.CreateProjectAsync(name);
            // Refresh project list for this card
            await GetProjectsAsync(cardId);
        }
        catch (Exception ex)
        {
            RhinoApp.WriteLine($"[OrbitConnector] createProject: {ex.Message}");
        }
    }

    // ── Layers ────────────────────────────────────────────────────────────────

    private void CaptureSelection(string cardId)
    {
        var doc = RhinoDoc.ActiveDoc;
        if (doc == null) return;
        var ids = doc.Objects.GetSelectedObjects(false, false)
                     .Select(o => o.Id.ToString())
                     .ToList();
        var card = _cardStore.Cards.FirstOrDefault(c => c.Id == cardId);
        if (card == null) return;
        card.SelectedObjectIds = ids;
        _cardStore.UpdateCard(card);
        SendToJs(new { type = "selectionCaptured", cardId, count = ids.Count });
    }

    private void SendLayers()
    {
        var doc = RhinoDoc.ActiveDoc;
        if (doc == null) return;
        var layers = doc.Layers
            .Where(l => !l.IsDeleted)
            .Select(l => new
            {
                fullPath = l.FullPath,
                name     = l.Name,
                depth    = l.FullPath.Split(new[] { "::" }, StringSplitOptions.None).Length - 1
            })
            .ToList();
        SendToJs(new { type = "layers", layers });
    }

    // ── Send ──────────────────────────────────────────────────────────────────

    private async Task HandleSendAsync(JObject data)
    {
        var cardId      = data.Value<string>("cardId") ?? "";
        var projId      = data.Value<string>("projId") ?? "";
        var mdlId       = data.Value<string>("mdlId") ?? "";
        var newMdlName  = data.Value<string>("newModelName");

        if (_client == null || string.IsNullOrEmpty(_token))
        {
            SendToJs(new { type = "sendErr", cardId, message = "Not logged in." });
            return;
        }

        var card = _cardStore.Cards.FirstOrDefault(c => c.Id == cardId);
        if (card == null)
        {
            SendToJs(new { type = "sendErr", cardId, message = "Card not found." });
            return;
        }

        var doc = RhinoDoc.ActiveDoc;
        if (doc == null)
        {
            SendToJs(new { type = "sendErr", cardId, message = "No active Rhino document." });
            return;
        }

        // Create model if a new name was provided
        if (!string.IsNullOrEmpty(newMdlName))
        {
            try
            {
                var model = await _client.CreateModelAsync(projId, newMdlName);
                card.ModelId   = model.Id ?? "";
                card.ModelName = model.Name ?? "";
                mdlId = card.ModelId;
                _cardStore.UpdateCard(card);
            }
            catch (Exception ex)
            {
                SendToJs(new { type = "sendErr", cardId, message = $"Create model: {ex.Message}" });
                return;
            }
        }

        if (string.IsNullOrEmpty(mdlId))
        {
            SendToJs(new { type = "sendErr", cardId, message = "Select or create a model." });
            return;
        }

        card.ProjectId = projId;
        card.ModelId   = mdlId;

        using var transport = new ServerTransport(_serverUrl, projId, _token);
        var progress = new Progress<(string s, int p)>(x =>
            SendToJs(new { type = "sendProgress", cardId, status = x.s, percent = x.p }));

        try
        {
            var versionId = await _pipeline.SendAsync(card, doc, transport, _client, progress);
            card.LastVersionId = versionId;
            card.LastSentAt    = DateTime.UtcNow;
            _cardStore.UpdateCard(card);
            var resultUrl = $"{_serverUrl}/projects/{projId}/models/{mdlId}";
            SendToJs(new { type = "sendOk", cardId, versionId, url = resultUrl });
        }
        catch (Exception ex)
        {
            SendToJs(new { type = "sendErr", cardId, message = ex.Message });
        }
    }

    // ── Receive ───────────────────────────────────────────────────────────────

    private async Task HandleReceiveAsync(JObject data)
    {
        var cardId = data.Value<string>("cardId") ?? "";
        var projId = data.Value<string>("projId") ?? "";
        var mdlId  = data.Value<string>("mdlId")  ?? "";

        if (_client == null || string.IsNullOrEmpty(_token))
        {
            SendToJs(new { type = "receiveErr", cardId, message = "Not logged in." });
            return;
        }

        var card = _cardStore.Cards.FirstOrDefault(c => c.Id == cardId);
        if (card == null)
        {
            SendToJs(new { type = "receiveErr", cardId, message = "Card not found." });
            return;
        }

        var doc = RhinoDoc.ActiveDoc;
        if (doc == null)
        {
            SendToJs(new { type = "receiveErr", cardId, message = "No active Rhino document." });
            return;
        }

        if (string.IsNullOrEmpty(projId) || string.IsNullOrEmpty(mdlId))
        {
            SendToJs(new { type = "receiveErr", cardId, message = "Select a project and model first." });
            return;
        }

        card.ProjectId = projId;
        card.ModelId   = mdlId;

        var progress = new Progress<(string s, int p)>(x =>
            SendToJs(new { type = "receiveProgress", cardId, status = x.s, percent = x.p }));

        try
        {
            var result = await _receivePipeline.ReceiveAsync(
                card, _config, doc, _client, _token, progress);

            card.LastReceivedAt        = DateTime.UtcNow;
            card.LastReceivedVersionId = null; // updated next time we fetch versions
            _cardStore.UpdateCard(card);

            var summary = $"✓ Received {result.ObjectCount} object(s) into {result.LayerCount} layer(s)";
            if (result.Warnings.Count > 0)
                summary += $" ({result.Warnings.Count} warning(s))";

            SendToJs(new { type = "receiveOk", cardId, summary, warnings = result.Warnings });
        }
        catch (Exception ex)
        {
            SendToJs(new { type = "receiveErr", cardId, message = ex.Message });
        }
    }

    // ── Update check (v0.1.2) ─────────────────────────────────────────────────
    //
    // The header bar in the WebView UI ships a "Check for updates" link next to
    // the version label. Clicking it posts orbit://msg/checkUpdates?d={}; this
    // method hits the GitHub releases API, compares against the assembly
    // version, and sends one of three structured replies back to JS so the
    // panel can render the right toast / confirm dialog without bouncing
    // through any native MessageBox (which would block the panel's UI thread
    // and feel out of place on top of the WebView).

    private async Task HandleUpdateCheckAsync()
    {
        try
        {
            var result = await CheckForUpdatesAsync(CancellationToken.None);
            switch (result.Kind)
            {
                case UpdateCheckKind.UpToDate:
                    SendToJs(new
                    {
                        type    = "updateCheck",
                        kind    = "uptodate",
                        current = OrbitConnectorPlugin.Version,
                    });
                    break;
                case UpdateCheckKind.NewerAvailable:
                    SendToJs(new
                    {
                        type    = "updateCheck",
                        kind    = "newer",
                        current = OrbitConnectorPlugin.Version,
                        latest  = result.LatestVersion,
                        url     = LatestReleasePageUrl,
                    });
                    break;
                case UpdateCheckKind.Failed:
                default:
                    SendToJs(new
                    {
                        type    = "updateCheck",
                        kind    = "failed",
                        message = result.ErrorMessage ?? "unknown error",
                    });
                    break;
            }
        }
        catch (Exception ex)
        {
            // Defensive: CheckForUpdatesAsync wraps its own failures into a
            // result, but if the wrapper itself throws (allocation failure,
            // CTS misuse, etc.) we still report instead of leaving the JS
            // link disabled forever.
            SendToJs(new
            {
                type    = "updateCheck",
                kind    = "failed",
                message = ex.Message,
            });
        }
    }

    private static async Task<UpdateCheckResult> CheckForUpdatesAsync(CancellationToken ct)
    {
        try
        {
            using var resp = await _http.GetAsync(LatestReleaseApiUrl, ct).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode)
            {
                return UpdateCheckResult.Fail(
                    $"GitHub returned HTTP {(int)resp.StatusCode} {resp.ReasonPhrase}");
            }

            var body = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            var json = JObject.Parse(body);
            var tagName = (string?)json["tag_name"];
            if (string.IsNullOrWhiteSpace(tagName))
            {
                return UpdateCheckResult.Fail("response did not include tag_name");
            }

            var latest = NormaliseVersion(tagName);
            var current = NormaliseVersion(OrbitConnectorPlugin.Version);
            if (latest == null)
            {
                return UpdateCheckResult.Fail($"could not parse latest tag '{tagName}'");
            }

            if (current != null && latest > current)
            {
                return UpdateCheckResult.Newer(tagName.TrimStart('v', 'V'));
            }
            return UpdateCheckResult.UpToDate();
        }
        catch (TaskCanceledException)
        {
            return UpdateCheckResult.Fail("request timed out");
        }
        catch (HttpRequestException ex)
        {
            return UpdateCheckResult.Fail(ex.Message);
        }
        catch (Exception ex)
        {
            return UpdateCheckResult.Fail(ex.Message);
        }
    }

    /// <summary>
    /// Parse "v0.1.2", "0.1.2", or "0.1.2+sha" into a System.Version. Returns
    /// null if the leading numeric portion isn't a recognisable version.
    /// </summary>
    private static System.Version? NormaliseVersion(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        var trimmed = raw.Trim().TrimStart('v', 'V');
        var plus = trimmed.IndexOf('+');
        if (plus >= 0) trimmed = trimmed[..plus];
        var dash = trimmed.IndexOf('-');
        if (dash >= 0) trimmed = trimmed[..dash];   // strip pre-release suffix
        return System.Version.TryParse(trimmed, out var v) ? v : null;
    }

    private enum UpdateCheckKind { UpToDate, NewerAvailable, Failed }

    private readonly struct UpdateCheckResult
    {
        public UpdateCheckKind Kind { get; }
        public string? LatestVersion { get; }
        public string? ErrorMessage { get; }

        private UpdateCheckResult(UpdateCheckKind kind, string? latest, string? error)
        {
            Kind = kind; LatestVersion = latest; ErrorMessage = error;
        }
        public static UpdateCheckResult UpToDate() => new(UpdateCheckKind.UpToDate, null, null);
        public static UpdateCheckResult Newer(string latest) => new(UpdateCheckKind.NewerAvailable, latest, null);
        public static UpdateCheckResult Fail(string message) => new(UpdateCheckKind.Failed, null, message);
    }

    // ── IPanel ────────────────────────────────────────────────────────────────

    void IPanel.PanelShown(uint documentSerialNumber, ShowPanelReason reason)
    {
        var doc = RhinoDoc.FromRuntimeSerialNumber(documentSerialNumber);
        var docChanged = doc != null && documentSerialNumber != _lastSyncedDocSerial;

        if (doc != null && docChanged)
        {
            _cardStore.CardsChanged -= OnCardsChanged;
            _cardStore = CardStore.LoadFromDocument(doc);
            _cardStore.CardsChanged += OnCardsChanged;
            _lastSyncedDocSerial = documentSerialNumber;
        }

        // Only push cards back to JS when the document actually changed
        // (or on the very first show). Pure visibility changes — e.g. the
        // user switching to another Rhino panel tab and back — leave the
        // existing JS state intact, preserving in-progress uploads, the
        // currently-selected project/model dropdowns, expanded cards, and
        // any text the user has typed into "new project / new model".
        if (_uiReady && docChanged) SendCards();
    }

    void IPanel.PanelHidden(uint documentSerialNumber, ShowPanelReason reason) { }
    void IPanel.PanelClosing(uint documentSerialNumber, bool onCloseDocument) { }
}
