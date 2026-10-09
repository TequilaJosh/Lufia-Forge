using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using System.Collections.ObjectModel;

namespace LufiaForge.Modules.GameData;

/// <summary>Monster names, stats, rewards, drops, weakness and graphics.</summary>
public partial class MonsterEditorViewModel : GameDataEditorBase
{
    // Bytes in the fixed part of the record whose meaning is unknown; shown raw so they can be edited.
    private static readonly int[] UnknownFields = { 11, 13, 18, 19, 29 };

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

    public ByteOption[] MonsterTypeOptions => GameDataLabels.MonsterTypes;

    public string DropChanceText => $"{DropRate / 255.0 * 100:0.#}% per battle";
    partial void OnDropRateChanged(int value) => OnPropertyChanged(nameof(DropChanceText));

    private int _recordOffset;
    private int _typeMiddleBits;   // bits 3-5 of the type byte, preserved as-is
    private int _lastIndex;

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
        for (int i = 0; i < GameDataOffsets.MonsterCount; i++)
            Monsters.Add($"{i:X2}  {Ctx.ReadName(Ctx.MonsterOffset(i), GameDataOffsets.MonsterNameLen)}");
        SelectedIndex = keep;
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

        OffsetText = $"Record at 0x{o:X6}  (pointer at 0x{GameDataOffsets.MonsterPointerTable + SelectedIndex * 2:X6}). " +
                     "Its AI script is decoded below.";
    }

    private void LoadAi(LufiaForge.Core.RomBuffer rom, int record)
    {
        AiLines.Clear();
        var cmds = LufiaForge.Core.Battle.MonsterAi.Decode(rom, record,
            id => id < SpellNames.Count ? SpellNames[id][3..].Trim() : $"spell {id:X2}");
        if (cmds.Count == 0)
        {
            _aiStart = _aiLength = 0; AiHex = "";
            AiNote = "No AI script: this monster just attacks.";
            return;
        }
        // numbered steps in script order; jumps name the step they land on
        var stepOf = cmds.Select((c, i) => (c.Offset, i + 1)).ToDictionary(t => t.Offset, t => t.Item2);
        string Step(int offset) => stepOf.TryGetValue(offset, out int n) ? $"step {n}" : $"+{offset:X2}";
        Func<int, string> spell = id => id < SpellNames.Count ? SpellNames[id][3..].Trim() : $"spell {id:X2}";
        for (int i = 0; i < cmds.Count; i++)
            AiLines.Add($"{i + 1,2}.  {LufiaForge.Core.Battle.MonsterAi.Plain(cmds[i], Step, spell)}");
        _aiStart = cmds.Min(c => c.Offset);
        _aiLength = cmds.Max(c => c.Offset + 1 + c.Args.Length) - _aiStart;
        AiHex = ItemEditorViewModel.ToHex(Enumerable.Range(0, _aiLength).Select(k => rom.ReadByte(record + _aiStart + k)).ToArray());
        AiNote = "Every turn the monster starts at step 1 and works down the list until a step chooses what it does (attack, spell, ability...). " +
                 $"The bytes below are the script itself ({_aiLength} bytes at record +{_aiStart:X2}); edit them in place, the length can't change.";
    }

    [RelayCommand]
    private void Apply()
    {
        if (Ctx == null || SelectedIndex < 0) return;
        if (!ItemEditorViewModel.TryParseHex(UnknownHex, out var unknown) || unknown.Length != UnknownFields.Length)
        {
            Status = $"Unknown bytes must be exactly {UnknownFields.Length} hex bytes (+11 +13 +18 +19 +29).";
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
            for (int i = 0; i < ai.Length; i++) rom.WriteByte(o + _aiStart + i, ai[i]);
        });

        if (renamed) RefreshList();
        LoadSelected();
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
