using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using System.Collections.ObjectModel;

namespace LufiaForge.Modules.GameData;

/// <summary>A "cast spell" step of a monster's AI script (op 07), edited with a spell list.</summary>
public partial class AiSpellStep : ObservableObject
{
    public int Offset { get; init; }
    public int Original { get; init; }
    public string Label { get; init; } = "";
    [ObservableProperty] private int _spell;
}

/// <summary>One spell of the "give spells" builder: the share of turns it is tried.</summary>
public partial class GiveSpellRow : ObservableObject
{
    [ObservableProperty] private int _percent = 25;
    [ObservableProperty] private int _spell = 1;
}

/// <summary>Monster names, stats, rewards, drops, weakness and graphics.</summary>
public partial class MonsterEditorViewModel : GameDataEditorBase
{
    // Bytes in the fixed part of the record whose meaning is unknown; shown raw so they can be edited.
    private static readonly int[] UnknownFields = { 11, 13, 29 };

    public ObservableCollection<string> Monsters { get; } = new();

    [ObservableProperty] private int    _selectedIndex = -1;
    [ObservableProperty] private string _name = "";
    [ObservableProperty] private int    _monsterType;
    [ObservableProperty] private bool   _flag40;
    [ObservableProperty] private bool   _flag80;
    [ObservableProperty] private int    _weakness;
    [ObservableProperty] private ByteOption[] _weaknessOptions = GameDataLabels.Elements;
    [ObservableProperty] private int    _graphic;
    [ObservableProperty] private int    _palette;
    [ObservableProperty] private int    _hp;
    [ObservableProperty] private int    _mp;
    [ObservableProperty] private int    _atp;
    [ObservableProperty] private int    _dfp;
    [ObservableProperty] private int    _magicDefense;
    [ObservableProperty] private int    _agl;
    [ObservableProperty] private int    _mgr;
    [ObservableProperty] private int    _exp;
    [ObservableProperty] private int    _gold;
    [ObservableProperty] private int    _dropItem;
    [ObservableProperty] private int    _dropRate;
    [ObservableProperty] private string _unknownHex = "";
    /// <summary>The monster's AI script, one decoded command per line ("+27  With chance ...").</summary>
    public System.Collections.ObjectModel.ObservableCollection<string> AiLines { get; } = new();
    /// <summary>The script's bytes (hex), edited in place: same length.</summary>
    [ObservableProperty] private string _aiHex = "";
    [ObservableProperty] private string _aiNote = "";
    private int _aiStart, _aiLength;
    [ObservableProperty] private string _offsetText = "";
    /// <summary>The script's "cast spell" steps, each with a spell list.</summary>
    public ObservableCollection<AiSpellStep> SpellSteps { get; } = new();
    /// <summary>The "give spells" builder's rows.</summary>
    public ObservableCollection<GiveSpellRow> GiveSpells { get; } = new();
    [ObservableProperty] private string _giveSpellsNote = "";

    public ByteOption[] MonsterTypeOptions => GameDataLabels.MonsterTypes;

    public string DropChanceText => $"{DropRate / 255.0 * 100:0.#}% per battle";
    partial void OnDropRateChanged(int value) => OnPropertyChanged(nameof(DropChanceText));

    private int _recordOffset;
    private int _typeMiddleBits;   // bits 3-5 of the type byte, preserved as-is
    private int _lastIndex;

    [ObservableProperty] private string _newMonsterNote = "";

    /// <summary>A new monster: a copy of the selected one (see <see cref="Core.Battle.MonsterTable.Add"/>).</summary>
    [RelayCommand]
    private void NewMonster()
    {
        if (Ctx == null || SelectedIndex < 0) return;
        if (System.Windows.MessageBox.Show($"Make a new monster as a copy of {Name.Replace('@', ' ').Trim()} (name, stats, picture, drops and its own copy of the AI)?",
                "New monster", System.Windows.MessageBoxButton.OKCancel, System.Windows.MessageBoxImage.Question) != System.Windows.MessageBoxResult.OK) return;
        MakeMonster();
    }

    public void MakeMonster()
    {
        if (Ctx == null || SelectedIndex < 0) return;
        int t = SelectedIndex, made = -1;
        Commit("New monster", () => made = Core.Battle.MonsterTable.Add(Ctx.Rom, t), namesChanged: true);
        if (made < 0) return;
        RefreshList();
        SelectedIndex = made;
        Status = $"Monster {made:X2} made (a copy of {t:X2}). Rename it, change it, then put it in encounters (Encounters tab).";
    }

    /// <summary>Gives the selected monster a picture of its own (a copy of its current one).</summary>
    [RelayCommand]
    private void NewPicture()
    {
        if (Ctx == null || SelectedIndex < 0) return;
        int id = SelectedIndex, old = Graphic, made = -1;
        Commit($"Monster {id:X2} picture", () =>
        {
            made = Core.Battle.MonsterTable.AddPicture(Ctx.Rom, old);
            Ctx.Rom.WriteByte(Ctx.MonsterOffset(id) + 14, (byte)made);
        });
        if (made < 0) return;
        LoadSelected();
        Status = $"Graphic {made:X2} made (a copy of {old:X2}) and given to this monster. Import its new picture.";
    }

    protected override void OnRomLoaded()
    {
        RefreshList();
        SelectedIndex = -1;
        SelectedIndex = 0;
    }

    private void RefreshList()
    {
        if (Ctx == null) return;
        int keep = SelectedIndex;
        Monsters.Clear();
        for (int i = 0, n = Ctx.MonsterCount; i < n; i++)
            Monsters.Add($"{i:X2}  {Ctx.ReadName(Ctx.MonsterOffset(i), GameDataOffsets.MonsterNameLen)}");
        SelectedIndex = keep;
        try { NewMonsterNote = $"Room for {Core.Battle.MonsterTable.Room(Ctx.Rom)} more monsters."; } catch { NewMonsterNote = ""; }
    }

    partial void OnSelectedIndexChanged(int value)
    {
        if (value >= 0) _lastIndex = value;
        LoadSelected();
    }

    protected override void LoadSelected()
    {
        if (Ctx == null || SelectedIndex < 0) return;
        var rom = Ctx.Rom;
        int o   = _recordOffset = Ctx.MonsterOffset(SelectedIndex);

        Name = Ctx.ReadName(o, GameDataOffsets.MonsterNameLen);
        int type        = rom.ReadByte(o + 10);
        MonsterType     = type & 0x07;
        _typeMiddleBits = type & 0x38;
        Flag40          = (type & 0x40) != 0;
        Flag80          = (type & 0x80) != 0;

        int weak = rom.ReadByte(o + 12);
        WeaknessOptions = GameDataLabels.WithValue(GameDataLabels.Elements, weak);
        Weakness = weak;

        Graphic      = rom.ReadByte(o + 14);
        Palette      = rom.ReadByte(o + 15);
        Hp           = rom.ReadUInt16Le(o + 16);
        Mp           = rom.ReadUInt16Le(o + 18);
        Atp          = rom.ReadUInt16Le(o + 20);
        Dfp          = rom.ReadUInt16Le(o + 22);
        MagicDefense = rom.ReadByte(o + 24);
        Agl          = rom.ReadByte(o + 25);
        Mgr          = rom.ReadByte(o + 26);
        Exp          = rom.ReadUInt16Le(o + 27);
        Gold         = rom.ReadUInt16Le(o + 30);
        DropItem     = rom.ReadByte(o + 32);
        DropRate     = rom.ReadByte(o + 33);
        UnknownHex   = ItemEditorViewModel.ToHex(UnknownFields.Select(f => rom.ReadByte(o + f)).ToArray());
        LoadAi(rom, o);

        OffsetText = $"Record at 0x{o:X6}{(SelectedIndex >= Core.Battle.MonsterTable.OriginalCount ? " (a new monster)" : "")}. " +
                     "Its AI script is decoded below.";
    }

    private void LoadAi(LufiaForge.Core.RomBuffer rom, int record)
    {
        AiLines.Clear();
        SpellSteps.Clear();
        GiveSpells.Clear();
        GiveSpellsNote = "Writes a new AI script: each spell is tried that share of turns (a monster needs MP for it, or it attacks instead), the rest of the turns it attacks. " +
                         "Its old script (calling for help, special attacks...) is replaced. It fits in place when the old script is long enough, otherwise it goes in the free space at the end of bank 0.";
        var cmds = LufiaForge.Core.Battle.MonsterAi.Decode(rom, record,
            id => id < SpellNames.Count ? SpellNames[id][3..].Trim() : $"spell {id:X2}");
        if (cmds.Count == 0)
        {
            _aiStart = _aiLength = 0; AiHex = "";
            AiNote = "No AI script: this monster just attacks. Use Give spells to make it cast spells.";
            return;
        }
        // numbered steps in script order; jumps name the step they land on
        var stepOf = cmds.Select((c, i) => (c.Offset, i + 1)).ToDictionary(t => t.Offset, t => t.Item2);
        string Step(int offset) => stepOf.TryGetValue(offset, out int n) ? $"step {n}" : $"+{offset:X2}";
        Func<int, string> spell = id => id < SpellNames.Count ? SpellNames[id][3..].Trim() : $"spell {id:X2}";
        Func<int, string> item = id => id < ItemNames.Count ? ItemNames[id][3..].Trim() : $"item {id:X2}";
        for (int i = 0; i < cmds.Count; i++)
        {
            AiLines.Add($"{i + 1,2}.  {LufiaForge.Core.Battle.MonsterAi.Plain(cmds[i], Step, spell, item)}");
            if (cmds[i].Op == 0x07)
                SpellSteps.Add(new AiSpellStep { Offset = cmds[i].Offset, Original = cmds[i].Args[0], Spell = cmds[i].Args[0], Label = $"Step {i + 1} casts" });
        }
        // the builder starts from the spells it already casts, split evenly
        var known = SpellSteps.Select(x => x.Spell).Distinct().Take(8).ToList();
        foreach (int sp in known) GiveSpells.Add(new GiveSpellRow { Spell = sp, Percent = Math.Max(1, 60 / known.Count) });
        _aiStart = cmds.Min(c => c.Offset);
        _aiLength = cmds.Max(c => c.Offset + 1 + c.Args.Length) - _aiStart;
        AiHex = ItemEditorViewModel.ToHex(Enumerable.Range(0, _aiLength).Select(k => rom.ReadByte(Core.Battle.MonsterAi.At(record, _aiStart + k))).ToArray());
        AiNote = "Every turn the monster starts at step 1 and works down the list until a step chooses what it does (attack, spell, ability...). " +
                 $"The bytes below are the script itself ({_aiLength} bytes at record +{_aiStart:X2}); edit them in place, the length can't change.";
    }

    [RelayCommand]
    private void Apply()
    {
        if (Ctx == null || SelectedIndex < 0) return;
        if (!ItemEditorViewModel.TryParseHex(UnknownHex, out var unknown) || unknown.Length != UnknownFields.Length)
        {
            Status = $"Unknown bytes must be exactly {UnknownFields.Length} hex bytes (+11 +13 +29).";
            return;
        }

        byte[] ai = Array.Empty<byte>();
        if (_aiLength > 0 && (!ItemEditorViewModel.TryParseHex(AiHex, out ai) || ai.Length != _aiLength))
        {
            Status = $"AI script: exactly {_aiLength} hex bytes (edit in place; the length can't change).";
            return;
        }

        var rom = Ctx.Rom;
        int o   = _recordOffset;
        bool renamed = Name != Ctx.ReadName(o, GameDataOffsets.MonsterNameLen);

        Commit($"Monster {SelectedIndex:X2}", () =>
        {
            if (renamed) Ctx.WriteName(o, GameDataOffsets.MonsterNameLen, Name);
            int type = (MonsterType & 0x07) | _typeMiddleBits | (Flag40 ? 0x40 : 0) | (Flag80 ? 0x80 : 0);
            rom.WriteByte(o + 10, (byte)type);
            rom.WriteByte(o + 12, (byte)Weakness);
            rom.WriteByte(o + 14, (byte)Clamp(Graphic, 0, 255));
            rom.WriteByte(o + 15, (byte)Clamp(Palette, 0, 255));
            rom.WriteUInt16Le(o + 16, (ushort)Clamp(Hp,  0, 0xFFFF));
            rom.WriteUInt16Le(o + 18, (ushort)Clamp(Mp,  0, 0xFFFF));
            rom.WriteUInt16Le(o + 20, (ushort)Clamp(Atp, 0, 0xFFFF));
            rom.WriteUInt16Le(o + 22, (ushort)Clamp(Dfp, 0, 0xFFFF));
            rom.WriteByte(o + 24, (byte)Clamp(MagicDefense, 0, 255));
            rom.WriteByte(o + 25, (byte)Clamp(Agl, 0, 255));
            rom.WriteByte(o + 26, (byte)Clamp(Mgr, 0, 255));
            rom.WriteUInt16Le(o + 27, (ushort)Clamp(Exp,  0, 0xFFFF));
            rom.WriteUInt16Le(o + 30, (ushort)Clamp(Gold, 0, 0xFFFF));
            rom.WriteByte(o + 32, (byte)Clamp(DropItem, 0, 255));
            rom.WriteByte(o + 33, (byte)Clamp(DropRate, 0, 255));
            for (int i = 0; i < UnknownFields.Length; i++)
                rom.WriteByte(o + UnknownFields[i], unknown[i]);
            for (int i = 0; i < ai.Length; i++) rom.WriteByte(Core.Battle.MonsterAi.At(o, _aiStart + i), ai[i]);
            foreach (var st in SpellSteps)
                if (st.Spell != st.Original && st.Spell >= 0) rom.WriteByte(o + st.Offset + 1, (byte)st.Spell);
        });

        if (renamed) RefreshList();
        LoadSelected();
    }

    [RelayCommand]
    private void AddGiveSpell()
    {
        if (GiveSpells.Count >= 8) { Status = "Up to 8 spells."; return; }
        GiveSpells.Add(new GiveSpellRow { Spell = Math.Max(1, SpellNames.Count - 1), Percent = 20 });
    }

    [RelayCommand]
    private void RemoveGiveSpell()
    {
        if (GiveSpells.Count > 0) GiveSpells.RemoveAt(GiveSpells.Count - 1);
    }

    [RelayCommand]
    private void WriteGiveSpells()
    {
        if (Ctx == null || SelectedIndex < 0) return;
        if (GiveSpells.Count == 0) { Status = "Add at least one spell."; return; }
        if (GiveSpells.Sum(g => g.Percent) > 100) { Status = "The shares add up to more than 100%."; return; }
        var rom = Ctx.Rom;
        int id = SelectedIndex, at = 0;
        bool gaveMp = false;
        Commit($"Monster {id:X2}'s spells", () =>
        {
            at = LufiaForge.Core.Battle.MonsterAi.WriteSpellScript(rom, id, GiveSpells.Select(g => (Clamp(g.Percent, 0, 100), Clamp(g.Spell, 0, SpellNames.Count - 1))).ToList());
            int o = Ctx.MonsterOffset(id);
            if (rom.ReadUInt16Le(o + 18) == 0) { rom.WriteUInt16Le(o + 18, 40); gaveMp = true; }   // a caster needs MP
        });
        LoadSelected();
        if (at > 0) Status = $"New AI script written at 0x{at:X6} (unsaved)." + (gaveMp ? " It had no MP, so it now has 40." : "");
    }

    [RelayCommand]
    private void RandomizeStats()
    {
        // Each stat becomes a random value between half and one and a half times the monster's value in the ROM.
        // Rolled from the ROM each click (not from the boxes), so clicking again re-rolls instead of piling up.
        if (Ctx == null || SelectedIndex < 0) return;
        var rom = Ctx.Rom;
        int o   = _recordOffset;
        int Roll(int stat, int max) => Clamp((int)Math.Round(stat * (0.5 + Random.Shared.NextDouble())), stat > 0 ? 1 : 0, max);
        Hp  = Roll(rom.ReadUInt16Le(o + 16), 0xFFFF);
        Atp = Roll(rom.ReadUInt16Le(o + 20), 0xFFFF);
        Dfp = Roll(rom.ReadUInt16Le(o + 22), 0xFFFF);
        Agl = Roll(rom.ReadByte(o + 25), 255);
        Mgr = Roll(rom.ReadByte(o + 26), 255);
        Status = "Stats randomized to 50-150% of the monster's current ROM values (not yet applied; click again to re-roll).";
    }

    protected override void OnNamesChanged()
    {
        if (SelectedIndex < 0) SelectedIndex = _lastIndex;
        else LoadSelected();
    }
}
