using LufiaForge.Core.Maps;
using System.Windows;

namespace LufiaForge.Modules.Events;

public partial class EventsView : UserControl
{
    private EventsViewModel? _hooked;

    public EventsView()
    {
        InitializeComponent();
        Editor.ShowCloseButton = false;
        DataContextChanged += (_, _) => Hook();
        Hook();
    }

    private void Hook()
    {
        if (_hooked != null) { _hooked.OpenRequested -= Open; _hooked.LiveFlags.Logged -= ScrollLog; }
        _hooked = DataContext as EventsViewModel;
        if (_hooked != null) { _hooked.OpenRequested += Open; _hooked.LiveFlags.Logged += ScrollLog; }
    }

    private void ScrollLog(FlagLogEntry entry)
    {
        if (_hooked?.LiveFlags.AutoScroll == true) FlagLogList.ScrollIntoView(entry);
    }

    private void ScrollSelectedIntoView(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if (sender is System.Windows.Controls.ListBox lb && lb.SelectedItem != null) lb.ScrollIntoView(lb.SelectedItem);
    }

    private void Open(EventScript script, string title, int highlightLine)
    {
        if (_hooked?.Rom == null) return;
        if (Editor.IsLoadedScript && !Editor.ConfirmDiscard()) return;
        Editor.Load(_hooked.Rom, script, title, _hooked);
        Editor.Visibility = Visibility.Visible;
        EmptyText.Visibility = Visibility.Collapsed;
        if (highlightLine >= 0) Editor.HighlightLine(highlightLine);
    }
}
