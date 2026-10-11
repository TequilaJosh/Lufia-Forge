namespace LufiaForge.Modules.GameData.Views;

public partial class SpellEditorView : UserControl
{
    public SpellEditorView()
    {
        InitializeComponent();
        IsVisibleChanged += (_, e) => { if (e.NewValue is true && DataContext is GameDataEditorBase vm) vm.Recheck(); };
    }
}
