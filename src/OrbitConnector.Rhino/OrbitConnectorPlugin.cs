using System;
using System.IO;
using System.Reflection;
using Rhino;
using Rhino.PlugIns;
using Rhino.UI;

namespace OrbitConnector.Rhino;

/// <summary>
/// Entry point for the ORBIT Rhino 8 connector plugin.
/// Registers the dockable panel and wires document events.
/// </summary>
public class OrbitConnectorPlugin : PlugIn
{
    public static OrbitConnectorPlugin? Instance { get; private set; }

    /// <summary>
    /// The connector version baked into this build, read from the assembly's
    /// AssemblyInformationalVersionAttribute (or falling back to the
    /// AssemblyVersion). The value is stamped at build time from
    /// $(OrbitConnectorVersion) in Directory.Build.props -- see
    /// RELEASE_POLICY.md.
    /// </summary>
    // 'new' suppresses CS0108 -- Rhino.PlugIns.PlugIn exposes its own instance
    // Version property; ours is a static convenience that resolves the version
    // baked into the assembly via AssemblyInformationalVersionAttribute.
    public static new string Version { get; } = ResolveVersion();

    // ---- Publisher / contact info ------------------------------------------
    public override string Email   => "IT@rebus.industries";
    public override string Website => "https://rebus.industries";

    // ---- Plugin Manager icon (Rhino Options → Plug-ins) --------------------
    //
    // Loaded once from the embedded Resources/orbit-logo.png manifest resource
    // and cached. The null-on-failure path is intentional: Rhino shows a
    // generic icon when Icon returns null, which is preferable to crashing.
    //
    // System.Drawing.Icon is referenced here safely because:
    //   1. The csproj already references System.Drawing.Common for compile-time
    //      resolution of Panels.RegisterPanel's Icon parameter.
    //   2. System.Drawing.Common is NOT bundled with the plug-in payload
    //      (ExcludeAssets="runtime" PrivateAssets="all") — Rhino's shared
    //      Microsoft.WindowsDesktop.App framework provides it at runtime.
    // This is the same binding-safe pattern the Panels.RegisterPanel call uses.
    //
    private static System.Drawing.Icon? _pluginIcon;
    private static bool                 _pluginIconLoaded;

    public override System.Drawing.Icon Icon
    {
        get
        {
            if (!_pluginIconLoaded)
            {
                _pluginIconLoaded = true;
                _pluginIcon = LoadOrbitIcon();
            }
            return _pluginIcon ?? base.Icon;
        }
    }

    private static System.Drawing.Icon? LoadOrbitIcon()
    {
        try
        {
            var asm = typeof(OrbitConnectorPlugin).Assembly;
            using var stream = asm.GetManifestResourceStream(
                "OrbitConnector.Rhino.Resources.orbit-logo.png");
            if (stream == null) return null;
            using var bmp    = new System.Drawing.Bitmap(stream);
            using var scaled = new System.Drawing.Bitmap(bmp, 32, 32);
            return System.Drawing.Icon.FromHandle(scaled.GetHicon());
        }
        catch
        {
            return null;
        }
    }

    // ---- Diagnostic load log (v0.1.7) --------------------------------------
    //
    // The v0.1.0-v0.1.6 "initialization failed" regression silently broke the
    // plug-in for three releases because Rhino's plug-in error dialog truncates
    // the real exception. To make the next regression debuggable, every
    // significant load milestone (cctor, ctor, OnLoad enter/exit, exception
    // chain) is appended to %LOCALAPPDATA%\OrbitConnector\load.log. The log
    // is appended-only across multiple Rhino sessions so the most recent
    // entries are always at the bottom -- a user reporting a load failure can
    // paste the tail of this file.
    //
    // Logging is deliberately fire-and-forget: a failure to write the log
    // must never break plug-in load. All IO is wrapped in try/catch.
    // ------------------------------------------------------------------------

    private static readonly string LogPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "OrbitConnector",
        "load.log");

    private static void Log(string msg)
    {
        var stamped = $"[{DateTime.Now:HH:mm:ss.fff}] {msg}{Environment.NewLine}";
        try
        {
            var dir = Path.GetDirectoryName(LogPath);
            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                Directory.CreateDirectory(dir);
            File.AppendAllText(LogPath, stamped);
        }
        catch
        {
            // Logging must never fail plug-in load. Swallow silently.
        }
    }

    static OrbitConnectorPlugin()
    {
        Log($"cctor v{Version} runtime={System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription}");
    }

    public OrbitConnectorPlugin()
    {
        Instance = this;
        Log("ctor done");
    }

    private static string ResolveVersion()
    {
        try
        {
            var asm = typeof(OrbitConnectorPlugin).Assembly;
            var info = asm.GetCustomAttribute<AssemblyInformationalVersionAttribute>()
                ?.InformationalVersion;
            if (!string.IsNullOrWhiteSpace(info))
            {
                // SourceLink may append "+<sha>" -- strip it for the user-facing label.
                var plus = info.IndexOf('+');
                return plus >= 0 ? info[..plus] : info;
            }
            return asm.GetName().Version?.ToString() ?? "dev";
        }
        catch
        {
            return "dev";
        }
    }

    public override PlugInLoadTime LoadTime => PlugInLoadTime.AtStartup;

    protected override LoadReturnCode OnLoad(ref string errorMessage)
    {
        Log("OnLoad enter");
        try
        {
            Log("registering OrbitEtoPanel");
            Panels.RegisterPanel(
                this,
                typeof(UI.OrbitEtoPanel),
                "ORBIT",
                Icon);
            Log("panel registered");

            RhinoDoc.BeginOpenDocument   += OnDocumentOpen;
            RhinoDoc.CloseDocument       += OnDocumentClose;
            RhinoDoc.EndSaveDocument     += OnDocumentSave;
            Log("doc events wired");

            RhinoApp.WriteLine($"ORBIT Connector v{Version} loaded.");
            Log("OnLoad ok");
            return LoadReturnCode.Success;
        }
        catch (Exception ex)
        {
            Log($"OnLoad THREW: {ex.GetType().FullName}: {ex.Message}");
            for (var inner = ex.InnerException; inner != null; inner = inner.InnerException)
                Log($"  INNER: {inner.GetType().FullName}: {inner.Message}");
            Log($"  STACK: {ex.StackTrace}");
            errorMessage = $"ORBIT Connector failed to load: {ex.Message}";
            return LoadReturnCode.ErrorShowDialog;
        }
    }

    protected override void OnShutdown()
    {
        RhinoDoc.BeginOpenDocument -= OnDocumentOpen;
        RhinoDoc.CloseDocument     -= OnDocumentClose;
        RhinoDoc.EndSaveDocument   -= OnDocumentSave;
    }

    private void OnDocumentOpen(object? sender, DocumentOpenEventArgs e)
    {
        // Cards are loaded lazily when the panel first accesses the doc
    }

    private void OnDocumentClose(object? sender, DocumentEventArgs e)
    {
        Models.CardStore.Instance?.OnDocumentClose(e.Document);
    }

    private void OnDocumentSave(object? sender, DocumentSaveEventArgs e)
    {
        // CardStore persists automatically via RhinoDoc.Strings —
        // nothing extra needed here, but hook is here for future use.
    }
}
