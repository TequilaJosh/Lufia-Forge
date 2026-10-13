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
    /// <summary>Group shown in the Patch Manager (Battle, Magic, Items, Events, Testing).</summary>
    public string Category { get; init; } = "Other";
    /// <summary>One line for the card; the description is under Details.</summary>
    public string Summary { get; init; } = "";
    public string Icon { get; init; } = "🔧";
    /// <summary>Who made it (community patches), shown on the card.</summary>
    public string Credit { get; init; } = "";
    /// <summary>False for patches that don't put code in the added space (no 2 MB expansion first).</summary>
    public bool NeedsExpandedRom { get; init; } = true;
    /// <summary>The patch rewrites much of the ROM: every tab reloads afterwards.</summary>
    public bool ReloadsAll { get; init; }
    /// <summary>Asked before adding it (null: no question).</summary>
    public string? ConfirmText { get; init; }
    /// <summary>Shown instead of the general "can't be added" text.</summary>
    public string? BlockedText { get; init; }
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
    /// <summary>Added, Update, NotAdded or Blocked (the badge's colour).</summary>
    [ObservableProperty] private string _kind = "NotAdded";
    [ObservableProperty] private string _badge = "";

    public void Refresh(RomBuffer? rom)
    {
        if (rom == null) { Added = false; CanClick = false; State = "Load a ROM first."; ButtonText = "Add"; return; }
        Added = IsApplied(rom);
        bool original = IsOriginal(rom);
        CanClick = Added ? Remove != null : original;
        bool update = !Added && NeedsUpdate?.Invoke(rom) == true;
        ButtonText = Added ? (Remove != null ? "Remove" : "Added") : update ? "Update" : "Add";
        Kind = Added ? "Added" : update ? "Update" : original ? "NotAdded" : "Blocked";
        Badge = Added ? "✔ In this ROM" : update ? "⟳ Update available" : original ? "Not added" : "✖ Can't be added";
        State = Added ? "✔ In this ROM." + (Remove == null ? " (It stays in: it can't be taken out again.)" : "")
              : update ? "An older version is in this ROM: click Update to fix it."
              : original ? "Not added."
              : BlockedText ?? "Can't be added: the game code it changes was already changed (by another patch?).";
    }

    public static List<BuiltInPatch> All() => new()
    {
        new()
        {
            Name = "Party members retarget instead of missing", Category = "Battle", Icon = "⚔", Summary = "When a party member's target dies first, they hit another monster instead of missing.",
            Description = "When a party member's target group is already beaten, the attack or spell goes to another monster instead of \"miss!\".",
            IsApplied = RetargetPatch.IsApplied, IsOriginal = RetargetPatch.IsOriginal, Apply = RetargetPatch.Apply, Remove = RetargetPatch.Remove,
        },
        new()
        {
            Name = "Hold L to avoid random battles", Category = "Battle", Icon = "⚔", Summary = "Holding L stops random battles (handy for testing and exploring).",
            Description = "While the L button is held there are no random battles (boss and story battles still happen).",
            IsApplied = EncounterPatch.IsApplied, IsOriginal = EncounterPatch.IsOriginal, Apply = EncounterPatch.Apply, Remove = EncounterPatch.Remove,
        },
        new()
        {
            Name = "Spells and MP for Guy / Aguro", Category = "Magic", Icon = "✨", Summary = "Guy / Aguro gets MP and learns spells like the others, Trickstr included.",
            Description = "Aguro (and Guy in the prologue) gets MP and learns spells like the others: a tank/bruiser list (Shield, Strong, Bravery, Drain, Protect, Stronger, Trickstr, Courage, Dread, Mirror, Champion), editable in Character Magic. The Hero keeps up to 19 spells, Aguro up to 11.",
            IsApplied = AguroMagicPatch.IsApplied, IsOriginal = AguroMagicPatch.IsOriginal, Apply = AguroMagicPatch.Apply, Remove = AguroMagicPatch.Remove,
            NeedsUpdate = AguroMagicPatch.NeedsUpdate,
        },
        new()
        {
            Name = "Up to 128 spells, 64 per character", Category = "Magic", Icon = "✨", Summary = "Room for 72 new spells; 64 per character, in an order players set in the menu.",
            Description = "Room for 72 new spells (38-7F) next to the game's 56: make them in Spells (\"New spell\" copies a spell, then rename it and choose what it does), teach them in Character Magic, give them to monsters and bosses in Monsters. Every character can learn up to 64 spells, in an order the player sets with the field Magic menu (kept in the save); saves from before keep their spells. The Guy / Aguro magic patch adds it with a first new spell, Trickstr (Trick on the whole party).",
            IsApplied = SpellTablePatch.IsApplied, IsOriginal = SpellTablePatch.IsOriginal, Apply = SpellTablePatch.Apply, Remove = SpellTablePatch.Remove,
            NeedsUpdate = SpellTablePatch.NeedsUpdate,
        },
        new()
        {
            Name = "Test start (skip the opening)", Category = "Testing", Icon = "🧪", Summary = "Power-on goes straight to New Game / Continue, then the Fortress of Doom, the Hero's start, or any map.",
            Description = "For test runs: power-on skips the opening and goes straight to the file select, as when a button is pressed during the opening: Initial starts a new game (with the Hero's name) and Continue loads a save. A new game starts the prologue at the Fortress of Doom; the Map Editor (MAP settings > Test start) can instead skip the prologue or put the party on any map.",
            IsApplied = rom => Core.Maps.TestStartPatch.IsApplied(rom) && !Core.Maps.TestStartPatch.IsOldStyle(rom),
            IsOriginal = Core.Maps.TestStartPatch.IsOriginal,
            NeedsUpdate = Core.Maps.TestStartPatch.IsOldStyle,
            Apply = rom =>
            {
                var mode = Core.Maps.TestStartPatch.Current(rom);
                var (map, arrival) = Core.Maps.TestStartPatch.Target(rom);
                Core.Maps.TestStartPatch.Set(rom, mode == Core.Maps.TestStartPatch.Mode.Off ? Core.Maps.TestStartPatch.Mode.Fortress : mode, map, arrival);
            },
            Remove = rom => Core.Maps.TestStartPatch.Set(rom, Core.Maps.TestStartPatch.Mode.Off),
        },
        new()
        {
            Name = "Items in the added space", Category = "Items", Icon = "🎒", Summary = "22 KB of room for item records, and new items.",
            Description = "The game reads item records from a bank of their own (a copy of the game's, at the same addresses) with 22 KB free, so items can gain properties (stats, status effects...) and new items can be made (Items > New item). Added by itself when an item needs more room.",
            IsApplied = ItemTable.IsMoved, IsOriginal = ItemTable.IsOriginal, Apply = ItemTable.MoveToItemBank,
        },
        new()
        {
            Name = "Status effects on equipment", Category = "Items", Icon = "🎒", Summary = "Weapons can inflict ailments on hit; any equipment can resist them.",
            Description = "Weapons can inflict ailments on hit (Sleep, Poison, Confuse, Mute, Stun, Stone, Death...) with a chance you set, and armor, helmets, shields and accessories can resist ailments - in the game only an accessory's resistance counted. Set them on each item in Items > Status effects (added by itself the first time you use it).",
            IsApplied = ItemStatusPatch.IsApplied, IsOriginal = ItemStatusPatch.IsOriginal, Apply = ItemStatusPatch.Apply, Remove = ItemStatusPatch.Remove,
        },
        new()
        {
            Name = "Rename command for events", Category = "Events", Icon = "💬", Summary = "An event command to rename a party member (flashbacks, disguises...).",
            Description = "Adds event command 6E \"Rename a party member\" (Events editor, Characters): e.g. show Maxim's name during a flashback, then give the Hero's own name back. Added by itself the first time an event uses it.",
            IsApplied = RenamePatch.IsApplied, IsOriginal = RenamePatch.IsOriginal, Apply = RenamePatch.Apply, Remove = RenamePatch.Remove,
        },
        new()
        {
            Name = "More than 67 encounter subgroups", Category = "Battle", Icon = "⚔", Summary = "Room for up to 255 monster subgroups in the Encounters tab.",
            Description = "Room for up to 255 monster subgroups in the Encounters tab (the game has room for 67).",
            IsApplied = SubgroupPatch.IsApplied, IsOriginal = SubgroupPatch.IsOriginal, Apply = SubgroupPatch.Apply,
        },
        new()
        {
            Name = "Lufia & the Fortress of Doom Restored v3.2", Category = "Community", Icon = "📜", Credit = "by FlamePurge, with rainponcho, Ok Impala!, Justin3009 and D",
            Summary = "The fan definitive edition: names and terms from the series, faster walking, half the encounters, item descriptions.",
            Description = "Lufia Decensored as a base, names and terms matching the rest of the series, faster walking on the world map and in dungeons (the Falcon too), encounter rate cut by half, the final boss as in the Japanese original, crosses in churches, and item descriptions (press X on an item) - see licenses/patches/Lufia1Restored_v32.txt next to Lufia Forge for the full list. It rewrites much of the game and grows the ROM to 1.5 MB, so it goes on a clean, unedited ROM: add it first, then make your edits. Non-commercial use only.",
            IsApplied = CommunityPatches.RestoredApplied, IsOriginal = CommunityPatches.IsCleanRom, Apply = CommunityPatches.ApplyRestored,
            NeedsExpandedRom = false, ReloadsAll = true,
            ConfirmText = "Apply Lufia & the Fortress of Doom Restored v3.2 (FlamePurge) to this ROM?\n\nIt rewrites much of the game and grows the ROM to 1.5 MB. It can't be taken out again - keep a copy of the clean ROM.",
            BlockedText = "Can't be added here: Restored goes on a clean, unedited Lufia 1 (USA) ROM. Open a clean ROM, add it first, then make your edits.",
        },
        new()
        {
            Name = "Always Retry Mode", Category = "Community", Icon = "📜", Credit = "by Justin3009 (comes with Restored)",
            Summary = "New games can start in Retry Mode (4x EXP and gold) without beating the game first.",
            Description = "Retry Mode (4x experience and gold) can be chosen for a new game straight away, without unlocking it by finishing the game. Works with or without Restored.",
            IsApplied = CommunityPatches.RetryApplied, IsOriginal = CommunityPatches.RetryOriginal, Apply = CommunityPatches.ApplyRetry, Remove = CommunityPatches.RemoveRetry,
            NeedsExpandedRom = false,
        },
        new()
        {
            Name = "Lufia Recolor 1.0", Category = "Community", Icon = "🎨", Credit = "the Lufia Recolor patch (1 April 2026)",
            Summary = "Character, menu and dialog box colours closer to Lufia II; dark sprite shadows.",
            Description = "Re-colours the character sprites to better match Lufia II: Rise of the Sinistrals, with slight menu and dialog box re-colourings, and turns the sickly green shadows and thick grey highlights on character sprites black (Just the Shadows is included). Works with or without Restored. Colours you changed in the Palettes tab at the same places block it.",
            IsApplied = CommunityPatches.RecolorApplied, IsOriginal = CommunityPatches.RecolorOriginal, Apply = CommunityPatches.ApplyRecolor, Remove = CommunityPatches.RemoveRecolor,
            NeedsExpandedRom = false,
            BlockedText = "Can't be added: some of the colours it changes were already changed (in the Palettes tab?).",
        },
        new()
        {
            Name = "Just the Shadows", Category = "Community", Icon = "🎨", Credit = "part of the Lufia Recolor patch",
            Summary = "Only the sprite shadows: the green shadows and grey highlights become black.",
            Description = "Turns the sickly green shadow and the thick grey highlights on character sprites into a much darker, black colour, and changes nothing else. (Lufia Recolor 1.0 includes this.)",
            IsApplied = CommunityPatches.ShadowsApplied, IsOriginal = CommunityPatches.ShadowsOriginal, Apply = CommunityPatches.ApplyShadows, Remove = CommunityPatches.RemoveShadows,
            NeedsExpandedRom = false,
            BlockedText = "Not needed: Lufia Recolor is in this ROM and already darkens the shadows (or the colours were changed in the Palettes tab).",
        },
    };
}
