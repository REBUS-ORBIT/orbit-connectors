using Eto.Forms;
using Eto.Drawing;
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
    private enum NavState { Home, Login, CardConfig, Progress }
    private NavState _state = NavState.Home;

    private readonly DynamicLayout _layout;
    private readonly Label _statusLabel;
    private readonly Button _addSendCardButton;
    private readonly Button _addReceiveCardButton;

    public static System.Guid PanelId => typeof(OrbitEtoPanel).GUID;

    public OrbitEtoPanel(uint documentSerialNumber)
    {
        _layout = new DynamicLayout { DefaultSpacing = new Size(4, 4), Padding = new Padding(8) };
        _statusLabel = new Label { Text = "ORBIT", Font = new Font(SystemFont.Bold, 13) };
        _addSendCardButton    = new Button { Text = "+ Send"    };
        _addReceiveCardButton = new Button { Text = "+ Receive" };

        BuildLayout();
        ApplyTheme();

        _addSendCardButton.Click    += (s, e) => OnAddCard(CardType.Send);
        _addReceiveCardButton.Click += (s, e) => OnAddCard(CardType.Receive);

        Content = _layout;
    }

    private void BuildLayout()
    {
        _layout.BeginVertical();
        _layout.Add(_statusLabel);
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
    }

    private void OnAddCard(CardType type)
    {
        // TODO: open card config view, pre-filled for type
        MessageBox.Show($"Adding {type} card — project picker coming soon.", "ORBIT");
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
