using System.Windows;

namespace LufiaForge.Modules.Common;

/// <summary>A view with its own undo for edits not yet written to the ROM (the map editors before Apply).</summary>
public interface ILocalUndo
{
    /// <summary>Takes back the view's last edit; false when it has none (the app's history gets the key).</summary>
    bool LocalUndo();
    bool LocalRedo();
}

/// <summary>
/// The view the user last worked in that has its own undo: Ctrl+Z / Ctrl+Y go to it first wherever the keyboard focus
/// is (a click on a tab or a toolbar button takes the focus away from the map), as long as it is on screen.
/// </summary>
public static class LocalUndoTarget
{
    private static WeakReference<FrameworkElement>? _current;

    public static void Set(FrameworkElement view) => _current = new WeakReference<FrameworkElement>(view);

    private static ILocalUndo? Visible =>
        _current != null && _current.TryGetTarget(out var v) && v.IsVisible && v is ILocalUndo u ? u : null;

    public static bool TryUndo() => Visible?.LocalUndo() == true;
    public static bool TryRedo() => Visible?.LocalRedo() == true;
}
