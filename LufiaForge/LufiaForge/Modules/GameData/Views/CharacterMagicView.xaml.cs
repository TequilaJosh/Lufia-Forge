namespace LufiaForge.Modules.GameData.Views;

public partial class CharacterMagicView : UserControl
{
    public CharacterMagicView()
    {
        InitializeComponent();
        IsVisibleChanged += (_, e) => { if (e.NewValue is true && DataContext is GameDataEditorBase vm) vm.Recheck(); };
    }
}
