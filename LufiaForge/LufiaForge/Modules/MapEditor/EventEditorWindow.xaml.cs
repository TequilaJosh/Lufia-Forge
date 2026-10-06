using LufiaForge.Core;
using LufiaForge.Core.Maps;
using System.ComponentModel;
using System.Windows;

namespace LufiaForge.Modules.MapEditor;

/// <summary>Pop-up event editor (the same panel as the Events tab).</summary>
public partial class EventEditorWindow : Window
{
    public EventEditorWindow(RomBuffer rom, EventScript script, string title, IEventHost owner)
    {
        InitializeComponent();
        Title = "Event editor - " + title;
        Panel.Load(rom, script, title, owner);
        Panel.ShowCloseButton = true;
        Panel.CloseRequested += () => { _confirmed = true; Close(); };
    }

    private bool _confirmed;

    protected override void OnClosing(CancelEventArgs e)
    {
        if (!_confirmed && !Panel.ConfirmDiscard()) e.Cancel = true;
        base.OnClosing(e);
    }
}
