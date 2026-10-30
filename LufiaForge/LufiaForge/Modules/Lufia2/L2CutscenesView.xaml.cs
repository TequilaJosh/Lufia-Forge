using System.Windows;
using System.Windows.Controls;

namespace LufiaForge.Modules.Lufia2;

public partial class L2CutscenesView : UserControl
{
    public L2CutscenesView()
    {
        InitializeComponent();
        Editor.CutsceneMode = true;
        DataContextChanged += (_, e) => { if (e.NewValue is L2CutscenesPage vm) Editor.Bind(vm.Editor); };
    }

    private L2CutscenesPage? Page => DataContext as L2CutscenesPage;

    private void NewCutscene_Click(object sender, RoutedEventArgs e)
    {
        if (Page?.CurrentRom == null || Page.CurrentMain == null) return;
        if (!Page.Editor.ConfirmLeave()) return;
        var w = new L2NewCutsceneWindow(Page, Page.Selected?.Map ?? Page.Editor.Map) { Owner = Window.GetWindow(this) };
        w.ShowDialog();
    }

    private void DeleteCutscene_Click(object sender, RoutedEventArgs e)
    {
        if (Page == null || Page.Selected == null) return;
        string? what = Page.DeleteDescription();
        if (what == null)
        {
            MessageBox.Show("This scene is started by the map's own \"on entering\" event, so it can't be taken off here. Change the steps instead (or take out the steps of the \"On entering\" event on the Events page that go to it).",
                "Delete cutscene", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        if (MessageBox.Show(what + "\n\nDelete it?", "Delete cutscene", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
        Page.Delete();
    }
}
