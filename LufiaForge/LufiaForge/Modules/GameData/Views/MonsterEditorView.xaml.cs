namespace LufiaForge.Modules.GameData.Views;

public partial class MonsterEditorView : UserControl
{
    public MonsterEditorView()
    {
        InitializeComponent();
        IsVisibleChanged += (_, e) => { if (e.NewValue is true && DataContext is GameDataEditorBase vm) vm.Recheck(); };
    }
}
