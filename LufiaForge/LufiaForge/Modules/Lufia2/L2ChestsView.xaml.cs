using System.Windows.Controls;

namespace LufiaForge.Modules.Lufia2;

public partial class L2ChestsView : UserControl
{
    public L2ChestsView()
    {
        InitializeComponent();
        // shown again: the Maps tab's edits are written and the treasure read again (both pages edit the same lists)
        IsVisibleChanged += (_, e) => { if (e.NewValue is true && DataContext is L2ChestsPage p) p.Shown(); };
    }
}
