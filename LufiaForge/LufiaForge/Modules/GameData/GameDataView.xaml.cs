namespace LufiaForge.Modules.GameData;

public partial class GameDataView : UserControl
{
    public GameDataView()
    {
        InitializeComponent();
        // a page shown (picked here, or the Game Data tab shown again) picks up what other pages and tabs changed
        foreach (var item in Pages.Items.OfType<System.Windows.Controls.TabItem>())
            if (item.Content is System.Windows.FrameworkElement page)
                page.IsVisibleChanged += (_, e) => { if (e.NewValue is true && page.DataContext is GameDataEditorBase vm) vm.Recheck(); };
    }
}
