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
    private static readonly (int Slot, int Offset)[] PatchedLists = { (0, 0), (1, 0), (2, 0), (3, 0) };
    private (int Slot, int Offset)[] Current => Ctx != null && AguroMagicPatch.IsApplied(Ctx.Rom) ? PatchedLists : Lists;

    public ObservableCollection<string>    Casters { get; } = new();
    public ObservableCollection<SpellSlot> Slots   { get; } = new();

    [ObservableProperty] private int    _selectedIndex = -1;
    [ObservableProperty] private string _offsetText = "";

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
        SelectedIndex = keep < Current.Length ? keep : 0;
    }

    partial void OnSelectedIndexChanged(int value) => LoadSelected();

    protected override void LoadSelected()
    {
        Slots.Clear();
        if (Ctx == null || SelectedIndex < 0 || SelectedIndex >= Current.Length) return;
        if (Current == PatchedLists)
        {
            int slot = PatchedLists[SelectedIndex].Slot, n = 1;
            foreach (var (lv, sp) in AguroMagicPatch.ReadLists(Ctx.Rom)[slot])
                Slots.Add(new SpellSlot { Number = n++, Level = lv, Spell = sp });
            OffsetText = $"Guy / Aguro magic patch: list {slot} at 0x{AguroMagicPatch.BlockOffset(Ctx.Rom):X6}, {Slots.Count} entries " +
                         $"(up to {AguroMagicPatch.MaxSpells(slot)}). Level 0 = known from the start.";
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
    private void Apply()
    {
        if (Ctx == null || SelectedIndex < 0 || SelectedIndex >= Current.Length) return;
        if (Current == PatchedLists)
        {
            int slot = PatchedLists[SelectedIndex].Slot;
            Commit($"{Casters[SelectedIndex]}'s spell list", () =>
            {
                var lists = AguroMagicPatch.ReadLists(Ctx.Rom);
                lists[slot] = Slots.Select(x => (Clamp(x.Level, 0, 0xFE), Clamp(x.Spell, 0, Ctx.SpellNames.Count - 1))).ToList();
                AguroMagicPatch.WriteLists(Ctx.Rom, lists);
            });
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
