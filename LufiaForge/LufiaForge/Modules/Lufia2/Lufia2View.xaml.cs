using System.Windows.Controls;

namespace LufiaForge.Modules.Lufia2;

public partial class Lufia2View : UserControl
{
    public Lufia2View()
    {
        InitializeComponent();
        DataContextChanged += (_, e) =>
        {
            if (e.OldValue is Lufia2ViewModel old) { old.ShowPage -= Show; old.OpenEditor -= OpenEditor; }
            if (e.NewValue is Lufia2ViewModel vm) { vm.ShowPage += Show; vm.OpenEditor += OpenEditor; }
        };
        // the Game start page shows the start as it is now (Test in the game on the Cutscenes page changes it)
        Pages.SelectionChanged += (_, e) =>
        {
            if (e.OriginalSource == Pages && Pages.SelectedItem is TabItem { Header: "Game start" } && DataContext is Lufia2ViewModel vm) vm.GameStart.Refresh();
        };
    }

    /// <summary>The pop-up event editor on an event of a map (as Lufia 1's map editor opens its events). When it closes,
    /// the pages that list the map's events read it again.</summary>
    private void OpenEditor(int map, int head, int id)
    {
        if (DataContext is not Lufia2ViewModel vm || System.Windows.Application.Current?.MainWindow?.DataContext is not LufiaForge.ViewModels.MainViewModel main || main.RomBuffer == null) return;
        var editor = new L2ScriptEditor();
        if (!editor.Load(main.RomBuffer, main, map, head, id)) { vm.Maps.Status = $"Map {map:X2} has no such event."; return; }
        var win = new L2EventEditorWindow(editor) { Owner = System.Windows.Window.GetWindow(this) };
        win.ShowDialog();
        vm.Maps.Refresh();
        vm.Events.Reload();
        vm.Cutscenes.Reload();
    }

    /// <summary>Picks the page with this header.</summary>
    private void Show(string header)
    {
        foreach (var o in Pages.Items)
            if (o is TabItem t && t.Header as string == header) { t.IsSelected = true; return; }
    }
}
