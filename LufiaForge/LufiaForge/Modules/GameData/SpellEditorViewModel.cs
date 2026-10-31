using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using System.Collections.ObjectModel;

namespace LufiaForge.Modules.GameData;

public partial class AilmentFlag : ObservableObject
{
    public string Name { get; init; } = "";
    public int    Bit  { get; init; }
    [ObservableProperty] private bool _isSet;
}

/// <summary>
/// Spell names, MP cost, usage, targeting and the parameters of the spell's main effect.
/// Spell records are variable-length and have no pointer table, so the effect type itself is
/// shown read-only: changing it would change the record's size.
/// </summary>
public partial class SpellEditorViewModel : GameDataEditorBase
{
    [ObservableProperty] private int    _selectedIndex = -1;
    [ObservableProperty] private bool   _isEditable;

    [ObservableProperty] private string _name = "";
    [ObservableProperty] private int    _usage;
    [ObservableProperty] private ByteOption[] _usageOptions = GameDataLabels.SpellUsages;
    [ObservableProperty] private int    _target;
    [ObservableProperty] private ByteOption[] _targetOptions = GameDataLabels.Targets;
    [ObservableProperty] private int    _mp;
    [ObservableProperty] private string _effectText = "";

    [ObservableProperty] private bool   _isCure;
    [ObservableProperty] private bool   _isAilment;
    [ObservableProperty] private bool   _isStat;
    [ObservableProperty] private bool   _isElement;
    [ObservableProperty] private bool   _isTeleport;
    [ObservableProperty] private bool   _hasNoParameters;

    [ObservableProperty] private int    _subType;
    [ObservableProperty] private ByteOption[] _subTypeOptions = Array.Empty<ByteOption>();
    [ObservableProperty] private int    _power;
    [ObservableProperty] private int    _randomPower;
    [ObservableProperty] private int    _successRate;
    [ObservableProperty] private string _teleportText = "";
    [ObservableProperty] private string _rawHex = "";
    [ObservableProperty] private string _offsetText = "";
    [ObservableProperty] private string _newSpellText = "";
    /// <summary>An added spell (38 and up): what it does can be changed with "Works like".</summary>
    [ObservableProperty] private bool _isNewSpell;
    [ObservableProperty] private int _worksLike = 1;

    public ObservableCollection<AilmentFlag> Ailments { get; } = new(
        GameDataLabels.Ailments.Select((n, i) => new AilmentFlag { Name = n, Bit = 1 << i }));

    public string SuccessText => $"{SuccessRate / 255.0 * 100:0.#}%";
    partial void OnSuccessRateChanged(int value) => OnPropertyChanged(nameof(SuccessText));

    private int _recordOffset;
    private int _effect;
    private int _lastIndex = 1;

    protected override void OnRomLoaded()
    {
        SelectedIndex = -1;
        SelectedIndex = 1;
    }

    partial void OnSelectedIndexChanged(int value)
    {
        if (value >= 0) _lastIndex = value;
        LoadSelected();
    }

    protected override void OnNamesChanged()
    {
        if (SelectedIndex < 0) SelectedIndex = _lastIndex;
        else LoadSelected();
    }

    protected override void LoadRecord()
    {
        if (Ctx == null || SelectedIndex < 0 || SelectedIndex >= Core.Battle.SpellTablePatch.SpellCount(Ctx.Rom)) return;
        var rom = Ctx.Rom;
        int o   = _recordOffset = Core.Battle.SpellTablePatch.SpellOffset(rom, SelectedIndex);
        int end = o + Core.Battle.SpellTablePatch.SpellLength(rom, SelectedIndex);

        Name = Ctx.ReadName(o, GameDataOffsets.SpellNameLen);

        int usage = rom.ReadByte(o + 8);
        UsageOptions = GameDataLabels.WithValue(GameDataLabels.SpellUsages, usage);
        Usage = usage;

        int target = rom.ReadByte(o + 10);
        TargetOptions = GameDataLabels.WithValue(GameDataLabels.Targets, target);
        Target = target;

        Mp      = rom.ReadByte(o + 11);
        _effect = rom.ReadByte(o + 20);
        EffectText = _effect < GameDataLabels.SpellEffects.Length
            ? GameDataLabels.SpellEffects[_effect] : $"{_effect:X2} ?";

        int sub = rom.ReadByte(o + 21);
        IsCure = IsAilment = IsStat = IsElement = IsTeleport = false;
        switch (_effect)
        {
            case 0x01 or 0x05:
                IsCure = true;
                SubTypeOptions = GameDataLabels.WithValue(GameDataLabels.CureKinds, sub);
                Power       = rom.ReadByte(o + 22);
                RandomPower = rom.ReadByte(o + 23);
                break;
            case 0x06 or 0x0E:
                IsAilment = true;
                foreach (var a in Ailments) a.IsSet = (sub & a.Bit) != 0;
                SuccessRate = rom.ReadByte(o + 22);
                break;
            case 0x07 or 0x0D:
                IsStat = true;
                SubTypeOptions = GameDataLabels.WithValue(GameDataLabels.StatKinds, sub);
                Power       = rom.ReadByte(o + 22);
                RandomPower = rom.ReadByte(o + 23);
                break;
            case 0x0B:
                IsElement = true;
                SubTypeOptions = GameDataLabels.WithValue(GameDataLabels.Elements, sub);
                Power       = rom.ReadUInt16Le(o + 22);
                RandomPower = rom.ReadByte(o + 24);   // (one byte: the effect's 00 end follows at +25)
                break;
            case 0x10:
                IsTeleport = true;
                TeleportText = sub < GameDataLabels.TeleportKinds.Length ? GameDataLabels.TeleportKinds[sub] : $"{sub:X2} ?";
                break;
        }
        SubType = sub;
        HasNoParameters = !(IsCure || IsAilment || IsStat || IsElement || IsTeleport);

        IsEditable = SelectedIndex > 0;   // spell 00 is the "Nothing" placeholder
        IsNewSpell = SelectedIndex >= Core.Battle.SpellTablePatch.FirstNew;
        int added = Core.Battle.SpellTablePatch.NewCount(rom);
        NewSpellText = Core.Battle.SpellTablePatch.IsApplied(rom)
            ? $"{added} new spells, room for {Core.Battle.SpellTablePatch.NewSlots - added} more. A new spell is a copy of the selected one (effect, animation, numbers): rename it and change its numbers."
            : "The game has 56 spells. \"New spell\" adds the \"Up to 128 spells\" patch (Patch Manager) first.";
        RawHex     = ItemEditorViewModel.ToHex(rom.ReadBytes(o + 8, end - o - 8));
        OffsetText = $"Record at 0x{o:X6}, {end - o} bytes";
        LoadAnimation(rom, o);
    }

    [RelayCommand]
    private void NewSpell()
    {
        if (Ctx == null || SelectedIndex < 0) return;
        var rom = Ctx.Rom;
        int template = SelectedIndex == 0 ? 1 : SelectedIndex, id = -1;
        Commit("New spell", () =>
        {
            if (!Core.Battle.SpellTablePatch.IsApplied(rom)) Core.Battle.SpellTablePatch.Apply(rom);   // also updates the first Trickstr version
            string name = Ctx.ReadName(Core.Battle.SpellTablePatch.SpellOffset(rom, template), GameDataOffsets.SpellNameLen).Trim();
            id = Core.Battle.SpellTablePatch.AddSpell(rom, template, name.Length > 6 ? name[..6] + "+" : name + "+");
            rom.FixChecksum();
        }, namesChanged: true);
        if (id >= 0) { SelectedIndex = id; Status = $"Spell {id:X2} added (a copy of {template:X2}). Rename it and change its numbers; it's written by itself (or click Apply in the top bar). Unsaved."; }
    }

    [RelayCommand]
    private void ApplyWorksLike()
    {
        if (Ctx == null || !IsNewSpell || WorksLike <= 0 || WorksLike >= Core.Battle.SpellTablePatch.SpellCount(Ctx.Rom)) return;
        int id = SelectedIndex, template = WorksLike;
        Commit($"Spell {id:X2} works like {template:X2}", () => { Core.Battle.SpellTablePatch.SetWorksLike(Ctx.Rom, id, template); Ctx.Rom.FixChecksum(); });
        LoadSelected();
        Status = $"Spell {id:X2} now works like {SpellNames[template][3..].Trim()} (effect, numbers, animation, usage and target); it keeps its name and MP. Change its numbers below; it's written by itself (or click Apply in the top bar). Unsaved.";
    }

    [RelayCommand]
    private void RemoveLastSpell()
    {
        if (Ctx == null) return;
        var rom = Ctx.Rom;
        int n = Core.Battle.SpellTablePatch.NewCount(rom);
        if (n == 0) { Status = "There are no new spells to remove."; return; }
        int id = Core.Battle.SpellTablePatch.FirstNew + n - 1;
        if (System.Windows.MessageBox.Show($"Remove spell {id:X2}? Characters stop learning it and monsters that cast it attack instead.",
                "Remove spell", System.Windows.MessageBoxButton.YesNo, System.Windows.MessageBoxImage.Question) != System.Windows.MessageBoxResult.Yes) return;
        Commit($"Spell {id:X2} removed", () => { Core.Battle.SpellTablePatch.RemoveLastSpell(rom); rom.FixChecksum(); }, namesChanged: true);
        if (SelectedIndex >= Core.Battle.SpellTablePatch.SpellCount(rom)) SelectedIndex = Core.Battle.SpellTablePatch.SpellCount(rom) - 1;
    }

    [RelayCommand]
    private void Apply()
    {
        if (Ctx == null || !IsEditable) return;
        var rom = Ctx.Rom;
        int o   = _recordOffset;
        if (Core.DutchLetters.Unwritable(Ctx.Rom, Name) is string badName) { Status = "Name: " + badName + "."; return; }
        bool renamed = Name != Ctx.ReadName(o, GameDataOffsets.SpellNameLen);

        Commit($"Spell {SelectedIndex:X2}", () =>
        {
            if (renamed) Ctx.WriteName(o, GameDataOffsets.SpellNameLen, Name);
            rom.WriteByte(o + 8,  (byte)Usage);
            rom.WriteByte(o + 10, (byte)Target);
            rom.WriteByte(o + 11, (byte)Clamp(Mp, 0, 255));
            rom.WriteByte(o + 9, (byte)Clamp(Animation, 0, Core.Battle.SpellAnimations.Count(rom) - 1));
            rom.WriteByte(o + 19, (byte)Clamp(Sound, 0, 255));

            if (IsCure || IsStat)
            {
                rom.WriteByte(o + 21, (byte)SubType);
                rom.WriteByte(o + 22, (byte)Clamp(Power, 0, 255));
                rom.WriteByte(o + 23, (byte)Clamp(RandomPower, 0, 255));
            }
            else if (IsAilment)
            {
                int bits = Ailments.Where(a => a.IsSet).Sum(a => a.Bit);
                rom.WriteByte(o + 21, (byte)bits);
                rom.WriteByte(o + 22, (byte)Clamp(SuccessRate, 0, 255));
            }
            else if (IsElement)
            {
                rom.WriteByte(o + 21, (byte)SubType);
                rom.WriteUInt16Le(o + 22, (ushort)Clamp(Power, 0, 0xFFFF));
                rom.WriteByte(o + 24, (byte)Clamp(RandomPower, 0, 255));
            }
        }, namesChanged: renamed);

        if (!renamed) LoadSelected();
    }
}
