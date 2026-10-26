using System.Windows.Controls;

namespace LufiaForge.Modules.Lufia2;

public partial class L2CharactersView : UserControl
{
    public L2CharactersView() => InitializeComponent();

    private void Chart_HandleDropped(int band, double value) => (DataContext as L2CharactersPage)?.DragBand(band, value);
}
