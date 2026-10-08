using LufiaForge.Core.Maps;
using System.Windows;

namespace LufiaForge.Modules.Cutscenes;

public partial class CutscenesView : UserControl
{
    private CutscenesViewModel? _hooked;

    public CutscenesView()
    {
        InitializeComponent();
        Editor.ShowCloseButton = false;
        Editor.CutsceneMode = true;
        DataContextChanged += (_, _) => Hook();
        Hook();
    }

    private void Hook()
    {
        if (_hooked != null) _hooked.OpenRequested -= Open;
        _hooked = DataContext as CutscenesViewModel;
        if (_hooked != null) _hooked.OpenRequested += Open;
    }

    private void Open(EventScript script, string title)
    {
        if (_hooked?.Rom == null) return;
        if (Editor.IsLoadedScript && !Editor.ConfirmDiscard()) return;
        Editor.Visibility = Visibility.Visible;
        EmptyText.Visibility = Visibility.Collapsed;
        Editor.Load(_hooked.Rom, script, title, _hooked);
        Editor.SelectLine(0);
    }
}
