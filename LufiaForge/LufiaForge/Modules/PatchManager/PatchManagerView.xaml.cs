using System.Windows.Controls;

namespace LufiaForge.Modules.PatchManager;

public partial class PatchManagerView : UserControl
{
    public PatchManagerView()
    {
        InitializeComponent();
        // other tabs (Game Settings, Encounters) can add the built-in patches too
        IsVisibleChanged += (_, e) => { if (e.NewValue is true && DataContext is PatchManagerViewModel vm) vm.RefreshBuiltIn(); };
    }
}
