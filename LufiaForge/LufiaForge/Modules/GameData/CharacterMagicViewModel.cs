using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LufiaForge.Core.Battle;
using System.Collections.ObjectModel;

namespace LufiaForge.Modules.GameData;

/// <summary>One (level, spell) entry of a character's spell-learning list.</summary>
public partial class SpellSlot : ObservableObject
{
    public int Number { get; init; }
    [ObservableProperty] private int _level;
    [ObservableProperty] private int _spell;
}

/// <summary>Which spells the Hero, Lufia and Jerin learn and at what level (Aguro too with the Guy / Aguro magic patch,
/// whose lists live in its own block).</summary>
public partial class CharacterMagicViewModel : GameDataEditorBase
{
    private static readonly (int Slot, int Offset)[] Lists =
    {
        (0, GameDataOffsets.HeroSpellList),
        (1, GameDataOffsets.LufiaSpellList),
        (3, GameDataOffsets.JerinSpellList),
    };
    // With the "Up to 128 spells, 64 per character" patch the lists live in its spell memory block (offset 0 here);
    // Aguro has one when the Guy / Aguro patch gave him magic. An earlier Guy / Aguro version is shown the same way
    // and brought up to date on Apply.
    private static readonly (int Slot, int Offset)[] Expanded4 = { (0, 0), (1, 0), (2, 0), (3, 0) };
    private static readonly (int Slot, int Offset)[] Expanded3 = { (0, 0), (1, 0), (3, 0) };
    private bool IsExpanded => Ctx != null && (SpellMemory.Installed(Ctx.Rom) || SpellMemory.OldBlock(Ctx.Rom) > 0);
    private (int Slot, int Offset)[] Current =>
        !IsExpanded ? Lists
        : AguroMagicPatch.IsApplied(Ctx!.Rom) || Ctx.Rom.ReadByte(AguroMagicPatch.MenuSkip) == 0xFF ? Expanded4 : Expanded3;

    /// <summary>The Patch Manager can add the patches while this editor is open: when it is shown again, it switches
    /// lists if that changed.</summary>
    public override void Recheck()
    {
        base.Recheck();
        if (Ctx == null) return;
        if (Casters.Count != Current.Length || _shownExpanded != IsExpanded) { RefreshCasters(); LoadSelected(); }
    }
    private bool _shownExpanded;

    public ObservableCollection<string>    Casters { get; } = new();
    public ObservableCollection<SpellSlot> Slots   { get; } = new();

    [ObservableProperty] private int    _selectedIndex = -1;
    [ObservableProperty] private string _offsetText = "";
    [ObservableProperty] private bool   _canResize;
    [ObservableProperty] private string _castersHint = "";

    protected override void OnRomLoaded()
    {
        RefreshCasters();
        SelectedIndex = -1;
        SelectedIndex = 0;
    }

    protected override void OnNamesChanged()
    {
        RefreshCasters();
        LoadSelected();
    }

    private void RefreshCasters()
    {
        if (Ctx == null) return;
        int keep = SelectedIndex;
        Casters.Clear();
        foreach (var (slot, _) in Current) Casters.Add(Ctx.CharacterName(slot));
        _shownExpanded = IsExpanded;
        CastersHint = !IsExpanded
            ? "The lists can't change length and Aguro doesn't learn magic: add \"Up to 128 spells, 64 per character\" (longer lists, up to 64 each) and \"Spells and MP for Guy / Aguro\" in the Patch Manager. Entries are checked in order; keep levels ascending. Level 0 = known from the start."
            : $"Entries are checked in order; keep levels ascending. Level 0 = known from the start. Add or remove entries with the buttons: up to {SpellMemory.MaxPerCharacter} spells each." +
              (Current == Expanded3 ? " Aguro learns no magic until the \"Spells and MP for Guy / Aguro\" patch is added." : "");
        SelectedIndex = keep < Current.Length ? keep : 0;
    }

    partial void OnSelectedIndexChanged(int value) => LoadSelected();

    protected override void LoadSelected()
    {
        Slots.Clear();
        CanResize = false;
        if (Ctx == null || SelectedIndex < 0 || SelectedIndex >= Current.Length) return;
        if (IsExpanded)
        {
            int slot = Current[SelectedIndex].Slot, n = 1;
            foreach (var (lv, sp) in SpellMemory.ReadLists(Ctx.Rom)[slot])
                Slots.Add(new SpellSlot { Number = n++, Level = lv, Spell = sp });
            CanResize = true;
            OffsetText = SpellMemory.Installed(Ctx.Rom)
                ? $"Spell memory block at 0x{SpellMemory.BlockOffset(Ctx.Rom):X6}: {Slots.Count} entries (up to {SpellMemory.MaxPerCharacter}). Level 0 = known from the start."
                : $"An earlier Guy / Aguro patch: Apply brings the spell patch up to date (64 spells each). {Slots.Count} entries.";
            return;
        }

        int start = Lists[SelectedIndex].Offset;
        int o = start;
        for (int n = 1; n <= 64 && Ctx.Rom.ReadByte(o) != 0xFF; n++, o += 2)
            Slots.Add(new SpellSlot { Number = n, Level = Ctx.Rom.ReadByte(o), Spell = Ctx.Rom.ReadByte(o + 1) });

        OffsetText = $"List at 0x{start:X6}, {Slots.Count} entries, FF terminator at 0x{o:X6}. " +
                     "Level 0 = known from the start.";
    }

    [RelayCommand]
    private void AddEntry()
    {
        if (Ctx == null || !CanResize) return;
        if (Slots.Count >= SpellMemory.MaxPerCharacter) { Status = $"{Casters[SelectedIndex]} can learn at most {SpellMemory.MaxPerCharacter} spells."; return; }
        int level = Slots.Count == 0 ? 0 : Math.Min(Slots[^1].Level + 1, 99);
        Slots.Add(new SpellSlot { Number = Slots.Count + 1, Level = level, Spell = Ctx.SpellNames.Count - 1 });
        Status = "Entry added (not yet applied): pick its level and spell, then Apply.";
    }

    [RelayCommand]
    private void RemoveEntry()
    {
        if (Ctx == null || !CanResize || Slots.Count == 0) return;
        Slots.RemoveAt(Slots.Count - 1);
        Status = "Last entry removed (not yet applied).";
    }

    [RelayCommand]
    private void Apply()
    {
        if (Ctx == null || SelectedIndex < 0 || SelectedIndex >= Current.Length) return;
        if (IsExpanded)
        {
            int slot = Current[SelectedIndex].Slot;
            bool updating = !SpellMemory.Installed(Ctx.Rom);
            Commit($"{Casters[SelectedIndex]}'s spell list", () =>
            {
                if (updating) { SpellTablePatch.Apply(Ctx.Rom); Ctx.Rom.FixChecksum(); }   // an earlier version: up to date first
                var lists = SpellMemory.ReadLists(Ctx.Rom);
                lists[slot] = Slots.Select(x => (Clamp(x.Level, 0, 0xFE), Clamp(x.Spell, 0, Ctx.SpellNames.Count - 1))).ToList();
                SpellMemory.WriteLists(Ctx.Rom, lists);
            });
            if (updating) { Status += " The spell patch was brought up to date (64 spells each)."; LoadSelected(); }
            return;
        }
        int start = Lists[SelectedIndex].Offset;

        Commit($"{Casters[SelectedIndex]}'s spell list", () =>
        {
            for (int i = 0; i < Slots.Count; i++)
            {
                // Level FF would be read as the list terminator.
                Ctx.Rom.WriteByte(start + i * 2,     (byte)Clamp(Slots[i].Level, 0, 0xFE));
                Ctx.Rom.WriteByte(start + i * 2 + 1, (byte)Clamp(Slots[i].Spell, 0, Ctx.SpellNames.Count - 1));
            }
        });
    }
}
