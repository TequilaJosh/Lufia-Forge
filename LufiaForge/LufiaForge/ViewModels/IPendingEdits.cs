namespace LufiaForge.ViewModels;

// a tab (or panel) with edits that aren't in the rom yet. the app writes them without asking: when the project gets
// saved (also by itself every 5 minutes), before closing, before a rom is exported or played in bizhawk, and when
// apply to rom in the top bar is clicked. register it with MainViewModel.Register
public interface IPendingEdits
{
    // whether there are edits that aren't in the rom yet
    bool HasPendingEdits { get; }

    // writes them to the rom without dialogs. returns null, or why they couldn't be written (they stay then)
    string? WritePendingEdits();
}
