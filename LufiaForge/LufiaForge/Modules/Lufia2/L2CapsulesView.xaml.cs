using System.Windows.Controls;

namespace LufiaForge.Modules.Lufia2;

public partial class L2CapsulesView : UserControl
{
    public L2CapsulesView() => InitializeComponent();

    private void Chart_HandleDropped(int band, double value) => (DataContext as L2CapsulesPage)?.DragBand(band, value);
}
