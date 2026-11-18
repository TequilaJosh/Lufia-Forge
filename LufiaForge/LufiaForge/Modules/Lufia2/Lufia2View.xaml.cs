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
        // switched to a page: the other pages' edits go into the ROM and this page reads what changed meanwhile (the Game
        // start page shows the start as it is now, Test in the game on the Cutscenes page changes it)
        Pages.SelectionChanged += (_, e) => { if (e.OriginalSource == Pages) PageShown(); };
        // back from another tab of the app (its edits were written when it was left)
        IsVisibleChanged += (_, e) => { if (e.NewValue is true) PageShown(); };
    }

    private void PageShown()
    {
        if (Pages.SelectedItem is TabItem { Header: string header } && DataContext is Lufia2ViewModel vm) vm.PageShown(header);
    }

    // the pop-up event editor on one of a map's events (like Lufia 1's map editor opens its events). when it closes
    // the pages that list the map's events read it again
    private void OpenEditor(int map, int head, int id)
    {
        if (DataContext is not Lufia2ViewModel vm || System.Windows.Application.Current?.MainWindow?.DataContext is not LufiaForge.ViewModels.MainViewModel main || main.RomBuffer == null) return;
        var editor = new L2ScriptEditor();
        if (!editor.Load(main.RomBuffer, main, map, head, id)) { vm.Maps.Status = $"Map {map:X2} has no such event."; return; }
        var editorWindow = new L2EventEditorWindow(editor) { Owner = System.Windows.Window.GetWindow(this) };
        editorWindow.ShowDialog();
        // the pop-up may have written this map's info (characters, paths, points, areas), so the Maps page reads the map
        // again (it wrote its own edits before the pop-up opened) and doesn't write its older copy back later
        vm.Maps.Shown();
        vm.Maps.Refresh();
        vm.Events.Reload();
        vm.Cutscenes.Reload();
    }

    // picks the page with this header
    private void Show(string header)
    {
        foreach (var item in Pages.Items)
            if (item is TabItem tab && tab.Header as string == header) { tab.IsSelected = true; return; }
    }
}
