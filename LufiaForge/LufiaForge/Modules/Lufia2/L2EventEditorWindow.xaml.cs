using System.ComponentModel;
using System.Windows;

namespace LufiaForge.Modules.Lufia2;

/// <summary>The pop-up Lufia II event editor (the Maps page's "Talk event..." / "Event...", double-clicking a character
/// or a step-on area): the same panel as the Events page, in a window of its own.</summary>
public partial class L2EventEditorWindow : Window
{
    private readonly L2ScriptEditor _editor;
    private bool _confirmed;

    public L2EventEditorWindow(L2ScriptEditor editor)
    {
        InitializeComponent();
        _editor = editor;
        Title = "Event editor - " + editor.Title;
        editor.PropertyChanged += (_, e) => { if (e.PropertyName == nameof(L2ScriptEditor.Title)) Title = "Event editor - " + editor.Title; };
        Panel.Bind(editor);
        Panel.ShowCloseButton = true;
        Panel.CloseRequested += () => { _confirmed = true; Close(); };
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        if (!_confirmed && !_editor.ConfirmLeave()) e.Cancel = true;
        if (!e.Cancel) _editor.Stop();
        base.OnClosing(e);
    }
}
