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

    public OrbitConnectorPlugin()
    {
        Instance = this;
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
