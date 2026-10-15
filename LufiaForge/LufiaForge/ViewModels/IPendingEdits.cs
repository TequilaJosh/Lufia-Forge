namespace LufiaForge.ViewModels;

/// <summary>
/// A tab (or panel) holding edits that aren't in the ROM yet. The app writes them without asking: when the project is
/// saved (also on its own every 5 minutes), before closing, before a ROM is exported or played in BizHawk, and when
/// Apply to ROM in the top bar is clicked. Register it with <see cref="MainViewModel.Register"/>.
/// </summary>
public interface IPendingEdits
{
    /// <summary>Whether there are edits that aren't in the ROM yet.</summary>
    bool HasPendingEdits { get; }

    /// <summary>Writes them to the ROM without dialogs. Null, or why they couldn't be written (they stay then).</summary>
    string? WritePendingEdits();
}
