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
    /// <summary>When it returns true, an older version of the patch is in the ROM and Add brings it up to date.</summary>
    public Func<RomBuffer, bool>? NeedsUpdate { get; init; }

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
        bool update = !Added && NeedsUpdate?.Invoke(rom) == true;
        ButtonText = Added ? (Remove != null ? "Remove" : "Added") : update ? "Update" : "Add";
        State = Added ? "✔ In this ROM." + (Remove == null ? " (It stays in: it can't be taken out again.)" : "")
              : update ? "An older version is in this ROM: click Update to fix it."
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
            Name = "Spells and MP for Guy / Aguro",
            Description = "Aguro (and Guy in the prologue) gets MP and learns spells like the others: a tank/bruiser list (Shield, Strong, Bravery, Drain, Protect, Stronger, Trickstr, Courage, Dread, Mirror, Champion), editable in Character Magic. The Hero keeps up to 19 spells, Aguro up to 11.",
            IsApplied = AguroMagicPatch.IsApplied, IsOriginal = AguroMagicPatch.IsOriginal, Apply = AguroMagicPatch.Apply, Remove = AguroMagicPatch.Remove,
            NeedsUpdate = AguroMagicPatch.NeedsUpdate,
        },
        new()
        {
            Name = "Up to 128 spells, 64 per character",
            Description = "Room for 72 new spells (38-7F) next to the game's 56: make them in Spells (\"New spell\" copies a spell, then rename it and choose what it does), teach them in Character Magic, give them to monsters and bosses in Monsters. Every character can learn up to 64 spells, in an order the player sets with the field Magic menu (kept in the save); saves from before keep their spells. The Guy / Aguro magic patch adds it with a first new spell, Trickstr (Trick on the whole party).",
            IsApplied = SpellTablePatch.IsApplied, IsOriginal = SpellTablePatch.IsOriginal, Apply = SpellTablePatch.Apply, Remove = SpellTablePatch.Remove,
            NeedsUpdate = SpellTablePatch.NeedsUpdate,
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
