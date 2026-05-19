using Rhino;
using Rhino.Commands;
using Rhino.UI;

namespace OrbitConnector.Rhino.Commands;

/// <summary>
/// "Orbit" command — opens or focuses the ORBIT dockable panel.
/// </summary>
[CommandStyle(Style.Hidden)]
public class OrbitOpenPanelCommand : Command
{
    public override string EnglishName => "Orbit";

    protected override Result RunCommand(RhinoDoc doc, RunMode mode)
    {
        Panels.OpenPanel(typeof(UI.OrbitEtoPanel).GUID);
        return Result.Success;
    }
}
