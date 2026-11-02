using System.Windows.Controls;

namespace LufiaForge.Modules.Lufia2;

public partial class L2ChestsView : UserControl
{
    public L2ChestsView()
    {
        // (shown again: Lufia2View calls the page's Shown, which writes the Maps tab's edits and reads the treasure again)
        InitializeComponent();
    }
}
