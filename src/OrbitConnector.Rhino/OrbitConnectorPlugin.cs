using System;
using System.IO;
using System.Linq;
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

    // ---- Plugin Manager icon ------------------------------------------------
    //
    // The [assembly: PlugInDescription(DescriptionType.Icon, "<resource>")] in
    // Properties/AssemblyInfo.cs tells Rhino's scanner which embedded manifest
    // resource to use for the Plugin Manager icon. No PlugIn.Icon override is
    // needed here: the NuGet stub for RhinoCommon 8 does not mark Icon(Size)
    // as virtual, so overriding it would break CI compilation.
    //
    // The panel rail icon (shown in Rhino's side dock) is handled separately
    // via LoadOrbitPanelIcon() in OnLoad → Panels.RegisterPanel.
    //

    // Returns a System.Drawing.Icon version of orbit-logo.png for use as the
    // dockable-panel rail icon passed to Panels.RegisterPanel.
    //
    // v0.1.12: previous attempts (v0.1.9 .. v0.1.11) loaded the PNG via a
    // single hard-coded manifest resource name and silently returned null
    // on any failure -- the user then saw a blank rail icon with no clue
    // why. We now:
    //
    //   1. Enumerate every manifest resource name first and log them to
    //      load.log so the actual embedded name is always visible after
    //      the next install. The name MSBuild assigns to
    //      `Resources\orbit-logo.png` depends on the LogicalName scheme,
    //      hyphen-vs-underscore policy, and root namespace; on some
    //      Rhino+SDK combos it has been observed to land at
    //      `OrbitConnector.Rhino.Resources.orbit_logo.png` (underscore)
    //      rather than `orbit-logo.png` (hyphen). Logging removes the
    //      guesswork.
    //
    //   2. Try multiple candidate names, then fall back to finding any
    //      embedded PNG whose name ends in `logo.png` or contains
    //      `orbit` (case-insensitive). This survives a renamed resource
    //      and surfaces a clear log line either way.
    //
    //   3. Try the high-DPI `Icon.FromHandle(bitmap.GetHicon())` path
    //      first; if that returns a non-valid icon (some Rhino host
    //      combos), fall back to building an ICO container from the
    //      raw PNG bytes and reading it with `new Icon(stream)`.
    //
    // Logging is appended to load.log so it survives plug-in reload.
    private static System.Drawing.Icon? LoadOrbitPanelIcon()
    {
        try
        {
            var asm = typeof(OrbitConnectorPlugin).Assembly;
            var names = asm.GetManifestResourceNames();
            Log($"manifest resources: count={names.Length}");
            foreach (var n in names) Log($"  resource: {n}");

            var candidates = new[]
            {
                "OrbitConnector.Rhino.Resources.orbit-logo.png",
                "OrbitConnector.Rhino.Resources.orbit_logo.png",
            };

            string? matchedName = candidates.FirstOrDefault(c =>
                names.Any(n => string.Equals(n, c, StringComparison.Ordinal)));

            if (matchedName == null)
            {
                // Last-ditch: any embedded PNG that looks like an
                // ORBIT logo. Hand-pick the candidate so a future
                // `Resources/dialog-bg.png` doesn't get picked up.
                matchedName = names.FirstOrDefault(n =>
                    n.EndsWith(".png", StringComparison.OrdinalIgnoreCase) &&
                    (n.IndexOf("orbit", StringComparison.OrdinalIgnoreCase) >= 0 ||
                     n.IndexOf("logo",  StringComparison.OrdinalIgnoreCase) >= 0));
            }

            if (matchedName == null)
            {
                Log("icon load: no matching embedded PNG found");
                return null;
            }

            Log($"icon load: using resource '{matchedName}'");

            byte[] pngBytes;
            using (var rs = asm.GetManifestResourceStream(matchedName))
            {
                if (rs == null) { Log("icon load: stream was null"); return null; }
                using var ms = new MemoryStream();
                rs.CopyTo(ms);
                pngBytes = ms.ToArray();
            }
            Log($"icon load: PNG bytes={pngBytes.Length}");

            // Pass 1: Bitmap.GetHicon round-trip.
            try
            {
                using var bmpStream = new MemoryStream(pngBytes);
                using var bmp = new System.Drawing.Bitmap(bmpStream);
                using var scaled = new System.Drawing.Bitmap(bmp, 32, 32);
                var icon = System.Drawing.Icon.FromHandle(scaled.GetHicon());
                if (icon != null && icon.Width > 0 && icon.Height > 0)
                {
                    Log($"icon load: OK via Bitmap.GetHicon size={icon.Width}x{icon.Height}");
                    return icon;
                }
                Log("icon load: Bitmap.GetHicon returned an empty icon, trying ICO fallback");
            }
            catch (Exception ex)
            {
                Log($"icon load: Bitmap.GetHicon THREW: {ex.GetType().Name}: {ex.Message}");
            }

            // Pass 2: Wrap the PNG bytes in an ICO container and read it
            // with `new Icon(stream)`. Works around hosts where
            // GetHicon's HICON handle doesn't survive the Icon.FromHandle
            // round-trip (the icon is technically valid but Rhino's
            // panel-rail renderer sees it as empty).
            try
            {
                using var icoBytes = new MemoryStream();
                WriteSinglePngIco(icoBytes, pngBytes, 32, 32);
                icoBytes.Position = 0;
                var icon = new System.Drawing.Icon(icoBytes);
                Log($"icon load: OK via ICO fallback size={icon.Width}x{icon.Height}");
                return icon;
            }
            catch (Exception ex)
            {
                Log($"icon load: ICO fallback THREW: {ex.GetType().Name}: {ex.Message}");
            }

            return null;
        }
        catch (Exception ex)
        {
            Log($"icon load: outer THREW: {ex.GetType().Name}: {ex.Message}");
            return null;
        }
    }

    // Build a minimal ICO container holding a single PNG image entry.
    // ICO format: 6-byte header + 16-byte directory entry + payload.
    // Reference: https://learn.microsoft.com/en-us/previous-versions/ms997538(v=msdn.10)
    private static void WriteSinglePngIco(Stream dst, byte[] pngBytes, int width, int height)
    {
        using var w = new BinaryWriter(dst, System.Text.Encoding.ASCII, leaveOpen: true);
        // ICONDIR
        w.Write((ushort)0);   // reserved
        w.Write((ushort)1);   // type = 1 (icon)
        w.Write((ushort)1);   // image count
        // ICONDIRENTRY
        w.Write((byte)(width  >= 256 ? 0 : width));
        w.Write((byte)(height >= 256 ? 0 : height));
        w.Write((byte)0);     // colour palette
        w.Write((byte)0);     // reserved
        w.Write((ushort)1);   // colour planes
        w.Write((ushort)32);  // bits per pixel
        w.Write((uint)pngBytes.Length);
        w.Write((uint)(6 + 16)); // image offset (header + dir entry)
        w.Write(pngBytes);
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
                LoadOrbitPanelIcon());
            Log("panel registered");

            RhinoDoc.BeginOpenDocument   += OnDocumentOpen;
            RhinoDoc.CloseDocument       += OnDocumentClose;
            RhinoDoc.EndSaveDocument     += OnDocumentSave;
            Log("doc events wired");

            // Grep-able load banner. Uses the same `[ORBIT]` prefix as every
            // send/receive diagnostic so a user filtering the command line on
            // `[ORBIT]` instantly sees WHICH build is live. If a re-test shows
            // no `[ORBIT] plugin v0.1.18 loaded` line, Rhino is still running
            // an older in-memory assembly — close Rhino fully and relaunch.
            RhinoApp.WriteLine($"[ORBIT] plugin v{Version} loaded.");
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
