using System.Windows.Controls;

namespace LufiaForge.Modules.Lufia2;

public partial class L2CharactersView : UserControl
{
    public L2CharactersView()
    {
        InitializeComponent();
        DataContextChanged += (_, _) => { if (DataContext is L2CharactersPage page) Elements.Attach(page,LufiaForge.Core.Lufia2.L2Elements.Who.Hero); };
    }

    private void Chart_HandleDropped(int band, double value) => (DataContext as L2CharactersPage)?.DragBand(band, value);
}
