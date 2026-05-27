using System;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Threading;
using System.Threading.Tasks;
using Eto.Forms;
using Eto.Drawing;
using Newtonsoft.Json.Linq;
using Rhino;
using Rhino.UI;
using OrbitConnector.Rhino.Models;
using OrbitConnector.Rhino.Auth;

namespace OrbitConnector.Rhino.UI;

/// <summary>
/// Main ORBIT dockable panel. Hosts the card list and navigation between views.
/// Phase 1: Pure Eto.Forms implementation.
/// Phase 2: Will host a WebView for richer UI.
/// </summary>
[System.Runtime.InteropServices.Guid("21621060-21E4-4A81-8FF6-3E11FBEF5D04")]
public class OrbitEtoPanel : Panel, IPanel
{
    /// <summary>Resource name (RootNamespace + path) of the embedded brand logo.</summary>
    private const string LogoResourceName = "OrbitConnector.Rhino.Resources.orbit-logo.png";

    /// <summary>Pixel height used to render the logo in the panel header.</summary>
    private const int LogoHeight = 32;

    /// <summary>GitHub URL the user is sent to when they want to download the latest release.</summary>
    private const string LatestReleasePageUrl =
        "https://github.com/REBUS-ORBIT/orbit-connectors/releases/latest";

    /// <summary>GitHub API endpoint that returns the latest release as JSON.</summary>
    private const string LatestReleaseApiUrl =
        "https://api.github.com/repos/REBUS-ORBIT/orbit-connectors/releases/latest";

    /// <summary>Process-wide HttpClient. Constructing one per click is an anti-pattern.</summary>
    private static readonly HttpClient _http = CreateUpdateCheckClient();

    private enum NavState { Home, Login, CardConfig, Progress }
    private NavState _state = NavState.Home;

    private readonly DynamicLayout _layout;
    private readonly Label _statusLabel;
    private readonly Label _versionLabel;
    private readonly LinkButton _updateCheckLink;
    private readonly ImageView? _logoView;
    private readonly Button _addSendCardButton;
    private readonly Button _addReceiveCardButton;

    public static System.Guid PanelId => typeof(OrbitEtoPanel).GUID;

    public OrbitEtoPanel(uint documentSerialNumber)
    {
        _layout = new DynamicLayout { DefaultSpacing = new Size(4, 4), Padding = new Padding(8) };
        _statusLabel = new Label { Text = "ORBIT", Font = new Font(SystemFont.Bold, 13) };
        _versionLabel = new Label
        {
            Text = $"v{OrbitConnectorPlugin.Version}",
            TextColor = Colors.Gray,
            Font = new Font(SystemFont.Default, 9f),
            TextAlignment = TextAlignment.Right,
            VerticalAlignment = VerticalAlignment.Center,
        };
        _updateCheckLink = new LinkButton
        {
            Text = "Check for updates",
            Font = new Font(SystemFont.Default, 9f),
        };
        _logoView = TryLoadLogo();
        _addSendCardButton    = new Button { Text = "+ Send"    };
        _addReceiveCardButton = new Button { Text = "+ Receive" };

        BuildLayout();
        ApplyTheme();

        _addSendCardButton.Click    += (s, e) => OnAddCard(CardType.Send);
        _addReceiveCardButton.Click += (s, e) => OnAddCard(CardType.Receive);
        _updateCheckLink.Click      += OnUpdateCheckClicked;

        Content = _layout;
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

    private static ImageView? TryLoadLogo()
    {
        try
        {
            var asm = typeof(OrbitConnectorPlugin).Assembly;
            using var stream = asm.GetManifestResourceStream(LogoResourceName);
            if (stream == null)
            {
                RhinoApp.WriteLine(
                    $"ORBIT: logo resource '{LogoResourceName}' not found in assembly.");
                return null;
            }

            // Bitmap takes ownership of the stream's data; copy to a memory
            // stream so we can dispose the manifest stream eagerly without
            // ripping the bytes out from under Eto.
            using var ms = new MemoryStream();
            stream.CopyTo(ms);
            ms.Position = 0;

            var bitmap = new Bitmap(ms);
            return new ImageView
            {
                Image = bitmap,
                Size = new Size((int)Math.Round(LogoHeight * (bitmap.Width / (double)bitmap.Height)), LogoHeight),
            };
        }
        catch (Exception ex)
        {
            RhinoApp.WriteLine($"ORBIT: failed to load brand logo: {ex.Message}");
            return null;
        }
    }

    private void BuildLayout()
    {
        // Header row:
        //
        //   [logo] [ORBIT title]                                          v0.1.x
        //          [optional subline]                            Check for updates
        //
        // The version + update-check column is right-aligned so the title can
        // grow leftward without colliding with the metadata.
        var versionRow = new StackLayout
        {
            Orientation = Orientation.Horizontal,
            Spacing = 6,
            HorizontalContentAlignment = HorizontalAlignment.Right,
            Items = { _versionLabel, new Label { Text = "·", TextColor = Color.FromArgb(120, 120, 120) }, _updateCheckLink },
        };

        var header = new DynamicLayout { DefaultSpacing = new Size(6, 0) };
        header.BeginHorizontal();
        if (_logoView != null)
        {
            header.Add(_logoView, xscale: false, yscale: false);
        }
        header.Add(_statusLabel, xscale: false, yscale: true);
        header.Add(null);                  // expanding spacer pushes metadata right
        header.Add(versionRow, xscale: false, yscale: true);
        header.EndHorizontal();

        _layout.BeginVertical();
        _layout.Add(header);
        _layout.Add(new Label { Text = "Connected to ORBIT", TextColor = Colors.Gray });
        _layout.Add((Eto.Forms.Control?)null); // separator
        _layout.Add(_addSendCardButton);
        _layout.Add(_addReceiveCardButton);
        // TODO: card list control goes here
        _layout.Add(null); // spacer
        _layout.EndVertical();
    }

    private void ApplyTheme()
    {
        BackgroundColor = Color.FromArgb(30, 30, 30);
        _statusLabel.TextColor = Colors.White;
        // Keep the version label a touch dimmer than the title so it reads as
        // metadata rather than a heading.
        _versionLabel.TextColor = Color.FromArgb(170, 170, 170);
        // Light-blue link colour that reads on the dark panel background.
        _updateCheckLink.TextColor = Color.FromArgb(102, 178, 255);
    }

    private void OnAddCard(CardType type)
    {
        // TODO: open card config view, pre-filled for type
        MessageBox.Show($"Adding {type} card — project picker coming soon.", "ORBIT");
    }

    // ---------------------------------------------------------------------
    // Update check
    // ---------------------------------------------------------------------

    private async void OnUpdateCheckClicked(object? sender, EventArgs e)
    {
        // Snapshot the original text so we can restore it on every exit path.
        var originalText = _updateCheckLink.Text;
        _updateCheckLink.Enabled = false;
        _updateCheckLink.Text = "Checking…";

        try
        {
            var result = await CheckForUpdatesAsync(CancellationToken.None).ConfigureAwait(true);
            HandleUpdateCheckResult(result);
        }
        catch (Exception ex)
        {
            // Defensive: CheckForUpdatesAsync already wraps its own failures
            // into an UpdateCheckResult. This is the "nothing else worked"
            // safety net so the UI never hangs in the disabled state.
            RhinoApp.WriteLine($"ORBIT: update check failed unexpectedly: {ex.Message}");
            MessageBox.Show(
                this,
                $"Couldn't check for updates: {ex.Message}. Try again later.",
                "ORBIT — update check failed",
                MessageBoxButtons.OK,
                MessageBoxType.Warning);
        }
        finally
        {
            _updateCheckLink.Text = originalText;
            _updateCheckLink.Enabled = true;
        }
    }

    private void HandleUpdateCheckResult(UpdateCheckResult result)
    {
        var current = OrbitConnectorPlugin.Version;
        switch (result.Kind)
        {
            case UpdateCheckKind.UpToDate:
                MessageBox.Show(
                    this,
                    $"You're up to date. Running v{current}.",
                    "ORBIT — up to date",
                    MessageBoxButtons.OK,
                    MessageBoxType.Information);
                break;

            case UpdateCheckKind.NewerAvailable:
                var answer = MessageBox.Show(
                    this,
                    $"ORBIT Connector v{result.LatestVersion} is available. " +
                    $"You're running v{current}.\n\nOpen the releases page to download?",
                    "ORBIT — update available",
                    MessageBoxButtons.YesNo,
                    MessageBoxType.Information,
                    MessageBoxDefaultButton.Yes);
                if (answer == DialogResult.Yes)
                {
                    OpenReleasesPage();
                }
                break;

            case UpdateCheckKind.Failed:
            default:
                MessageBox.Show(
                    this,
                    $"Couldn't check for updates: {result.ErrorMessage}. Try again later.",
                    "ORBIT — update check failed",
                    MessageBoxButtons.OK,
                    MessageBoxType.Warning);
                break;
        }
    }

    private static void OpenReleasesPage()
    {
        try
        {
            // UseShellExecute=true is required to open a URL via the default browser on .NET 5+.
            Process.Start(new ProcessStartInfo(LatestReleasePageUrl) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            RhinoApp.WriteLine($"ORBIT: failed to open releases page: {ex.Message}");
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

    // IPanel implementation
    void IPanel.PanelShown(uint documentSerialNumber, ShowPanelReason reason)
    {
        var doc = RhinoDoc.FromRuntimeSerialNumber(documentSerialNumber);
        if (doc != null)
            CardStore.LoadFromDocument(doc);
    }

    void IPanel.PanelHidden(uint documentSerialNumber, ShowPanelReason reason) { }
    void IPanel.PanelClosing(uint documentSerialNumber, bool onCloseDocument) { }
}
