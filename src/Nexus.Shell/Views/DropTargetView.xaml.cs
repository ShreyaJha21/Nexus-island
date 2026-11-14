using System.Windows.Controls;

namespace Nexus.Shell.Views;

public partial class DropTargetView : UserControl
{
    public DropTargetView() => InitializeComponent();

    /// <summary>"an hour", "30 minutes": how long dropped things stay.</summary>
    public string KeepText
    {
        set => Hint.Text = $"Stays on the island for {value} · then drag it into any app";
    }
}
