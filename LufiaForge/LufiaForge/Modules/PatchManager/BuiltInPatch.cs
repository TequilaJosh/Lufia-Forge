using CommunityToolkit.Mvvm.ComponentModel;
using LufiaForge.Core;
using LufiaForge.Core.Battle;

namespace LufiaForge.Modules.PatchManager;

/// <summary>
/// A fix or feature Lufia Forge can add to the ROM with one click (the code lives in Core/Battle). Each one adds a
/// small routine to the expanded part of the ROM, so adding one expands the ROM to 2 MB first.
/// </summary>
public partial class BuiltInPatch : ObservableObject
{
    public required string Name { get; init; }
    public required string Description { get; init; }
    public required Func<RomBuffer, bool> IsApplied { get; init; }
    public required Func<RomBuffer, bool> IsOriginal { get; init; }
    public required Action<RomBuffer> Apply { get; init; }
    /// <summary>Null when the patch can't be taken out again.</summary>
    public Action<RomBuffer>? Remove { get; init; }

    [ObservableProperty] private bool _added;
    [ObservableProperty] private bool _canClick;
    [ObservableProperty] private string _state = "";
    [ObservableProperty] private string _buttonText = "Add";

    public void Refresh(RomBuffer? rom)
    {
        if (rom == null) { Added = false; CanClick = false; State = "Load a ROM first."; ButtonText = "Add"; return; }
        Added = IsApplied(rom);
        bool original = IsOriginal(rom);
        CanClick = Added ? Remove != null : original;
        ButtonText = Added ? (Remove != null ? "Remove" : "Added") : "Add";
        State = Added ? "✔ In this ROM." + (Remove == null ? " (It stays in: it can't be taken out again.)" : "")
              : original ? "Not added."
              : "Can't be added: the game code it changes was already changed (by another patch?).";
    }

    public static List<BuiltInPatch> All() => new()
    {
        new()
        {
            Name = "Party members retarget instead of missing",
            Description = "When a party member's target group is already beaten, the attack or spell goes to another monster instead of \"miss!\".",
            IsApplied = RetargetPatch.IsApplied, IsOriginal = RetargetPatch.IsOriginal, Apply = RetargetPatch.Apply, Remove = RetargetPatch.Remove,
        },
        new()
        {
            Name = "Hold L to avoid random battles",
            Description = "While the L button is held there are no random battles (boss and story battles still happen).",
            IsApplied = EncounterPatch.IsApplied, IsOriginal = EncounterPatch.IsOriginal, Apply = EncounterPatch.Apply, Remove = EncounterPatch.Remove,
        },
        new()
        {
            Name = "Rename command for events",
            Description = "Adds event command 6E \"Rename a party member\" (Events editor, Characters): e.g. show Maxim's name during a flashback, then give the Hero's own name back. Added by itself the first time an event uses it.",
            IsApplied = RenamePatch.IsApplied, IsOriginal = RenamePatch.IsOriginal, Apply = RenamePatch.Apply, Remove = RenamePatch.Remove,
        },
        new()
        {
            Name = "More than 67 encounter subgroups",
            Description = "Room for up to 255 monster subgroups in the Encounters tab (the game has room for 67).",
            IsApplied = SubgroupPatch.IsApplied, IsOriginal = SubgroupPatch.IsOriginal, Apply = SubgroupPatch.Apply,
        },
    };
}
