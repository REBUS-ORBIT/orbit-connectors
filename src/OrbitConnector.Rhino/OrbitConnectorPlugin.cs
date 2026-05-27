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

    public OrbitConnectorPlugin()
    {
        Instance = this;
    }

    private static string ResolveVersion()
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

    public override PlugInLoadTime LoadTime => PlugInLoadTime.AtStartup;

    protected override LoadReturnCode OnLoad(ref string errorMessage)
    {
        try
        {
            // Register the dockable panel
            Panels.RegisterPanel(
                this,
                typeof(UI.OrbitEtoPanel),
                "ORBIT",
                null);

            // Wire Rhino document events
            RhinoDoc.BeginOpenDocument   += OnDocumentOpen;
            RhinoDoc.CloseDocument       += OnDocumentClose;
            RhinoDoc.EndSaveDocument     += OnDocumentSave;

            RhinoApp.WriteLine("ORBIT Connector loaded.");
            return LoadReturnCode.Success;
        }
        catch (Exception ex)
        {
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
