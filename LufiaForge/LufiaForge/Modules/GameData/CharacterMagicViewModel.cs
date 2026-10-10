using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using System.Collections.ObjectModel;

namespace LufiaForge.Modules.GameData;

/// <summary>One (level, spell) entry of a character's spell-learning list.</summary>
public partial class SpellSlot : ObservableObject
{
    public int Number { get; init; }
    [ObservableProperty] private int _level;
    [ObservableProperty] private int _spell;
}

/// <summary>Which spells the Hero, Lufia and Jerin learn and at what level (Aguro learns none).</summary>
public partial class CharacterMagicViewModel : GameDataEditorBase
{
    private static readonly (int Slot, int Offset)[] Lists =
    {
        (0, GameDataOffsets.HeroSpellList),
        (1, GameDataOffsets.LufiaSpellList),
        (3, GameDataOffsets.JerinSpellList),
    };

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
        foreach (var (slot, _) in Lists) Casters.Add(Ctx.CharacterName(slot));
        SelectedIndex = keep < Lists.Length ? keep : 0;
    }

    partial void OnSelectedIndexChanged(int value) => LoadSelected();

    protected override void LoadSelected()
    {
        Slots.Clear();
        if (Ctx == null || SelectedIndex < 0 || SelectedIndex >= Lists.Length) return;

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
        if (Ctx == null || SelectedIndex < 0 || SelectedIndex >= Lists.Length) return;
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
