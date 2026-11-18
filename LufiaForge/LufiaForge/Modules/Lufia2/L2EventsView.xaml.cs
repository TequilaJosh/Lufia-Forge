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
            if (e.OldValue is L2EventsPage old) { old.FlagRequested -= ShowFlags; old.EventsRequested -= ShowEvents; old.StoryRequested -= ShowStory; }
            if (e.NewValue is L2EventsPage vm) { Editor.Bind(vm.Editor); vm.FlagRequested += ShowFlags; vm.EventsRequested += ShowEvents; vm.StoryRequested += ShowStory; }
        };
    }

    private L2EventsPage? Page => DataContext as L2EventsPage;

    private void ShowFlags(int flag)
    {
        foreach (var tab in Tabs.Items) if (tab is TabItem { Header: "Flags" } flagsTab) flagsTab.IsSelected = true;
    }

    // back on the Events tab after names were given on the Flags tab: the editor's cards and lists pick them up
    private void Tabs_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (e.OriginalSource != Tabs || Page is not { } page || !page.FlagsPanel.NamesDirty) return;
        if (Tabs.SelectedItem is TabItem { Header: "Events" }) { page.FlagsPanel.NamesDirty = false; page.Editor.NamesChanged(); }
        else if (Tabs.SelectedItem is TabItem { Header: "Story characters" }) page.StoryPanel.Refresh();
    }

    private void FlagList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (FlagList.SelectedItem is { } item) FlagList.ScrollIntoView(item);
    }

    private void ShowStory(int id)
    {
        foreach (var tab in Tabs.Items) if (tab is TabItem { Header: "Story characters" } storyTab) storyTab.IsSelected = true;
    }

    private void StoryList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (StoryList.SelectedItem is { } item) StoryList.ScrollIntoView(item);
    }

    private void StoryUseList_DoubleClick(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (StoryUseList.SelectedItem is L2StoryUseRow row) Page?.StoryPanel.OpenUseCommand.Execute(row);
    }

    private void StoryPlace_Click(object sender, RoutedEventArgs e)
    {
        if (Page?.CurrentRom is not { } rom || Page.StoryPanel.Selected == null) return;
        var (map, x, y) = Page.StoryPanel.PlaceForWindow();
        var window = new L2PickPlaceWindow(rom, map, x, y, m => Core.Lufia2.L2Maps.Name(rom, m)) { Owner = Window.GetWindow(this), Title = "Where a new game puts the story character - Lufia Forge" };
        if (window.ShowDialog() != true) return;
        Page.StoryPanel.SetPlace(window.Map, window.X, window.Y);
    }

    private void ShowEvents()
    {
        foreach (var tab in Tabs.Items) if (tab is TabItem { Header: "Events" } eventsTab) eventsTab.IsSelected = true;
    }

    private void UseList_DoubleClick(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (UseList.SelectedItem is L2FlagUseRow row) Page?.FlagsPanel.OpenUseCommand.Execute(row);
    }

    private void NewEvent_Click(object sender, RoutedEventArgs e)
    {
        if (Page?.CurrentRom == null || Page.CurrentMap < 0) return;
        if (!Page.AskBeforeLeaving()) return;   // the new event gets opened afterwards, so unsaved steps are written first
        var window = new L2NewEventWindow(Page) { Owner = Window.GetWindow(this) };
        window.ShowDialog();
    }
}
