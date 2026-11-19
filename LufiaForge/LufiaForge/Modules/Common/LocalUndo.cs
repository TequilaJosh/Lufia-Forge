using System.Windows;

namespace LufiaForge.Modules.Common;

// a view with its own undo for edits that aren't in the ROM yet (the map editors before Apply)
public interface ILocalUndo
{
    // takes back the view's last edit. false if it has none (then the app's history gets the key)
    bool LocalUndo();
    bool LocalRedo();
}

// the last view the user worked in that has its own undo. Ctrl+Z / Ctrl+Y go to it first, wherever the keyboard
// focus is (clicking a tab or a toolbar button takes the focus off the map), as long as it's on screen
public static class LocalUndoTarget
{
    private static WeakReference<FrameworkElement>? _current;

    public static void Set(FrameworkElement view) => _current = new WeakReference<FrameworkElement>(view);

    private static ILocalUndo? Visible =>
        _current != null && _current.TryGetTarget(out var view) && view.IsVisible && view is ILocalUndo undo ? undo : null;

    public static bool TryUndo() => Visible?.LocalUndo() == true;
    public static bool TryRedo() => Visible?.LocalRedo() == true;
}
