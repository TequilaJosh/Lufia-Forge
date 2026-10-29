using System.Windows.Controls;

namespace LufiaForge.Modules.Lufia2;

public partial class Lufia2View : UserControl
{
    public Lufia2View()
    {
        InitializeComponent();
        DataContextChanged += (_, e) =>
        {
            if (e.OldValue is Lufia2ViewModel old) old.ShowPage -= Show;
            if (e.NewValue is Lufia2ViewModel vm) vm.ShowPage += Show;
        };
        // the Game start page shows the start as it is now (Test in the game on the Cutscenes page changes it)
        Pages.SelectionChanged += (_, e) =>
        {
            if (e.OriginalSource == Pages && Pages.SelectedItem is TabItem { Header: "Game start" } && DataContext is Lufia2ViewModel vm) vm.GameStart.Refresh();
        };
    }

    /// <summary>Picks the page with this header.</summary>
    private void Show(string header)
    {
        foreach (var o in Pages.Items)
            if (o is TabItem t && t.Header as string == header) { t.IsSelected = true; return; }
    }
}
