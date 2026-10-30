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
            if (e.OldValue is L2EventsPage old) old.FlagRequested -= ShowFlags;
            if (e.NewValue is L2EventsPage vm) { Editor.Bind(vm.Editor); vm.FlagRequested += ShowFlags; }
        };
    }

    private L2EventsPage? Page => DataContext as L2EventsPage;

    private void ShowFlags(int flag)
    {
        foreach (var o in Tabs.Items) if (o is TabItem { Header: "Story flags" } t) t.IsSelected = true;
    }

    private void NewEvent_Click(object sender, RoutedEventArgs e)
    {
        if (Page?.CurrentRom == null || Page.CurrentMap < 0) return;
        if (!Page.AskBeforeLeaving()) return;   // the new event is opened after: unsaved steps are written first
        var w = new L2NewEventWindow(Page) { Owner = Window.GetWindow(this) };
        w.ShowDialog();
    }
}
