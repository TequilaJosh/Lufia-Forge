using System.Windows;
using System.Windows.Controls;

namespace LufiaForge.Modules.Lufia2;

public partial class L2EventsView : UserControl
{
    public L2EventsView()
    {
        InitializeComponent();
        DataContextChanged += (_, e) =>
        {
            if (e.OldValue is L2EventsPage old) { old.FlagRequested -= ShowFlags; old.EventsRequested -= ShowEvents; }
            if (e.NewValue is L2EventsPage vm) { Editor.Bind(vm.Editor); vm.FlagRequested += ShowFlags; vm.EventsRequested += ShowEvents; }
        };
    }

    private L2EventsPage? Page => DataContext as L2EventsPage;

    private void ShowFlags(int flag)
    {
        foreach (var o in Tabs.Items) if (o is TabItem { Header: "Flags" } t) t.IsSelected = true;
    }

    // back on the Events tab after names were given on the Flags tab: the editor's cards and lists use them
    private void Tabs_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (e.OriginalSource != Tabs || Page is not { } p || !p.FlagsPanel.NamesDirty) return;
        if (Tabs.SelectedItem is TabItem { Header: "Events" }) { p.FlagsPanel.NamesDirty = false; p.Editor.NamesChanged(); }
    }

    private void FlagList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (FlagList.SelectedItem is { } item) FlagList.ScrollIntoView(item);
    }

    private void ShowEvents()
    {
        foreach (var o in Tabs.Items) if (o is TabItem { Header: "Events" } t) t.IsSelected = true;
    }

    private void UseList_DoubleClick(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (UseList.SelectedItem is L2FlagUseRow row) Page?.FlagsPanel.OpenUseCommand.Execute(row);
    }

    private void NewEvent_Click(object sender, RoutedEventArgs e)
    {
        if (Page?.CurrentRom == null || Page.CurrentMap < 0) return;
        if (!Page.AskBeforeLeaving()) return;   // the new event is opened after: unsaved steps are written first
        var w = new L2NewEventWindow(Page) { Owner = Window.GetWindow(this) };
        w.ShowDialog();
    }
}
