using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using System.Collections.ObjectModel;
using System.Globalization;

namespace LufiaForge.Modules.GameData;

/// <summary>One 3-byte equipment property entry: id + 16-bit value.</summary>
public partial class ItemProperty : ObservableObject
{
    [ObservableProperty] private int _id;
    [ObservableProperty] private int _value;
}

/// <summary>Item names, type, equipment rules, icon, price and properties (ATP, DFP, ...).</summary>
public partial class ItemEditorViewModel : GameDataEditorBase
{
    [ObservableProperty] private int    _selectedIndex = -1;

    [ObservableProperty] private string _name = "";
    [ObservableProperty] private int    _type;
    [ObservableProperty] private ByteOption[] _typeOptions = GameDataLabels.ItemTypes;
    [ObservableProperty] private int    _slot;
    [ObservableProperty] private ByteOption[] _slotOptions = GameDataLabels.EquipSlots;
    [ObservableProperty] private bool   _equipHero;
    [ObservableProperty] private bool   _equipLufia;
    [ObservableProperty] private bool   _equipAguro;
    [ObservableProperty] private bool   _equipJerin;
    [ObservableProperty] private int    _target;
    [ObservableProperty] private ByteOption[] _targetOptions = GameDataLabels.Targets;
    [ObservableProperty] private int    _icon;
    [ObservableProperty] private ByteOption[] _iconOptions = GameDataLabels.ItemIcons;
    [ObservableProperty] private int    _price;
    [ObservableProperty] private string _headerHex = "";

    /// <summary>True when the data after +21 is a clean list of 3-byte properties.</summary>
    [ObservableProperty] private bool   _hasPropertyList;
    [ObservableProperty] private string _effectHex = "";
    [ObservableProperty] private string _offsetText = "";

    public ObservableCollection<ItemProperty> Properties { get; } = new();
    public ByteOption[] PropertyOptions => GameDataLabels.ItemProperties;

    // item descriptions (only in ROMs with the Restored hack, see ItemDescriptions)
    [ObservableProperty] private bool   _hasDescriptions;
    [ObservableProperty] private string _description = "";
    [ObservableProperty] private string _descriptionInfo = "";
    private Core.ItemDescriptions? _descriptions;

    private int _recordOffset;
    private int _tailLength;   // bytes from +21 to the end of the record (incl. terminator)

    protected override void OnRomLoaded()
    {
        _descriptions = Ctx == null ? null : Core.ItemDescriptions.Find(Ctx.Rom);
        HasDescriptions = _descriptions != null;
        SelectedIndex = -1;
        SelectedIndex = 1;
    }

    private int _lastIndex = 1;

    partial void OnSelectedIndexChanged(int value)
    {
        if (value >= 0) _lastIndex = value;
        LoadSelected();
    }

    // Replacing a renamed entry in the shared list clears the ListBox selection; restore it.
    protected override void OnNamesChanged()
    {
        if (SelectedIndex < 0) SelectedIndex = _lastIndex;
        else LoadSelected();
    }

    protected override void LoadSelected()
    {
        if (Ctx == null || SelectedIndex < 0) return;
        var rom = Ctx.Rom;
        int o   = _recordOffset = Ctx.ItemOffset(SelectedIndex);

        Name = Ctx.ReadName(o, GameDataOffsets.ItemNameLen);

        int type = rom.ReadByte(o + 12);
        TypeOptions = GameDataLabels.WithValue(GameDataLabels.ItemTypes, type);
        Type = type;

        int slot = rom.ReadByte(o + 13);
        SlotOptions = GameDataLabels.WithValue(GameDataLabels.EquipSlots, slot);
        Slot = slot;

        int equip  = rom.ReadByte(o + 14);
        EquipHero  = (equip & 0x10) != 0;
        EquipLufia = (equip & 0x20) != 0;
        EquipAguro = (equip & 0x40) != 0;
        EquipJerin = (equip & 0x80) != 0;
        TargetOptions = GameDataLabels.WithValue(GameDataLabels.Targets, equip & 0x0F);
        Target = equip & 0x0F;

        int icon = rom.ReadByte(o + 15);
        IconOptions = GameDataLabels.WithValue(GameDataLabels.ItemIcons, icon);
        Icon = icon;

        Price     = rom.ReadUInt16Le(o + 16);
        HeaderHex = ToHex(rom.ReadBytes(o + 18, 3));

        // The record ends where the next one starts (the last record: at its 00 terminator).
        int next = SelectedIndex < GameDataOffsets.ItemCount - 1 ? Ctx.ItemOffset(SelectedIndex + 1) : -1;
        int p = o + 21, count = 0;
        while (count < 16 && p < rom.Length && rom.ReadByte(p) != 0) { p += 3; count++; }
        int parsedEnd = p + 1;
        if (next < 0) next = parsedEnd;

        _tailLength     = next - (o + 21);
        HasPropertyList = parsedEnd == next && count < 16;

        Properties.Clear();
        if (HasPropertyList)
        {
            for (int i = 0; i < count; i++)
                Properties.Add(new ItemProperty
                {
                    Id    = rom.ReadByte(o + 21 + i * 3),
                    Value = rom.ReadUInt16Le(o + 22 + i * 3),
                });
        }
        EffectHex = _tailLength > 0 ? ToHex(rom.ReadBytes(o + 21, _tailLength)) : "";

        OffsetText = $"Record at 0x{o:X6}, {next - o} bytes  (pointer at 0x{GameDataOffsets.ItemPointerTable + SelectedIndex * 2:X6})";

        if (_descriptions != null)
        {
            Description = _descriptions.Read(SelectedIndex);
            DescriptionInfo = $"Shown when X is pressed on the item (added by the Restored hack). Up to {Core.ItemDescriptions.MaxLines} lines of {Core.ItemDescriptions.MaxLineLength} characters; " +
                              $"{_descriptions.Free(AllDescriptions()):N0} bytes of room left for longer texts.";
        }
    }

    [RelayCommand]
    private void Apply()
    {
        if (Ctx == null || SelectedIndex < 0) return;
        var rom = Ctx.Rom;
        int o   = _recordOffset;

        // Validate the hex fields before touching the ROM.
        if (!TryParseHex(HeaderHex, out var header) || header.Length != 3)
        {
            Status = "Header bytes (+18..+20) must be exactly 3 hex bytes.";
            return;
        }
        byte[]? effect = null;
        if (!HasPropertyList)
        {
            if (!TryParseHex(EffectHex, out effect) || effect.Length != _tailLength)
            {
                Status = $"Effect data must be exactly {_tailLength} hex bytes (records can't change size).";
                return;
            }
        }
        if (HasPropertyList && Properties.Any(p => p.Id == 0))
        {
            Status = "Property id 00 is the list terminator; pick another id.";
            return;
        }

        string? newDescription = null;
        string typed = Description.Replace("\r", "");
        if (_descriptions != null && typed != _descriptions.Read(SelectedIndex))
        {
            if (Core.ItemDescriptions.Problem(typed) is string bad) { Status = "Description: " + bad + "."; return; }
            newDescription = typed;
        }

        bool renamed = Name != Ctx.ReadName(o, GameDataOffsets.ItemNameLen);
        Commit($"Item {SelectedIndex:X2}", () =>
        {
            if (renamed) Ctx.WriteName(o, GameDataOffsets.ItemNameLen, Name);
            rom.WriteByte(o + 12, (byte)Type);
            rom.WriteByte(o + 13, (byte)Slot);
            int equip = (EquipHero ? 0x10 : 0) | (EquipLufia ? 0x20 : 0) |
                        (EquipAguro ? 0x40 : 0) | (EquipJerin ? 0x80 : 0) | (Target & 0x0F);
            rom.WriteByte(o + 14, (byte)equip);
            rom.WriteByte(o + 15, (byte)Icon);
            rom.WriteUInt16Le(o + 16, (ushort)Clamp(Price, 0, 0xFFFF));
            rom.WriteBytes(o + 18, header);

            if (HasPropertyList)
            {
                for (int i = 0; i < Properties.Count; i++)
                {
                    rom.WriteByte(o + 21 + i * 3, (byte)Properties[i].Id);
                    rom.WriteUInt16Le(o + 22 + i * 3, (ushort)Clamp(Properties[i].Value, 0, 0xFFFF));
                }
            }
            else if (effect!.Length > 0)
            {
                rom.WriteBytes(o + 21, effect);
            }
            if (newDescription != null)
            {
                var all = AllDescriptions();
                all[SelectedIndex] = newDescription;
                _descriptions!.WriteAll(all);
            }
        }, namesChanged: renamed);

        if (!renamed) LoadSelected();
    }

    private List<string> AllDescriptions() =>
        Enumerable.Range(0, Core.ItemDescriptions.Count).Select(i => _descriptions!.Read(i)).ToList();

    internal static string ToHex(byte[] bytes) => string.Join(" ", bytes.Select(b => b.ToString("X2")));

    internal static bool TryParseHex(string text, out byte[] bytes)
    {
        var parts = text.Split(new[] { ' ', ',', '\t', '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
        bytes = new byte[parts.Length];
        for (int i = 0; i < parts.Length; i++)
        {
            if (!byte.TryParse(parts[i], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out bytes[i]))
                return false;
        }
        return true;
    }
}
