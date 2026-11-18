using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using System.Collections.ObjectModel;
using System.Globalization;

namespace LufiaForge.Modules.GameData;

// one 3 byte equipment property entry: id + 16-bit value
public partial class ItemProperty : ObservableObject
{
    [ObservableProperty] private int _id;
    [ObservableProperty] private int _value;
}

// item names, type, who can equip it, icon, price and properties (ATP, DFP, ...)
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

    // true when the data after +21 is a clean list of 3 byte properties
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
    private int _tailLength;   // bytes from +21 to the end of the record (with the 00 at the end)

    protected override void OnRomLoaded()
    {
        _descriptions = Ctx == null ? null : Core.ItemDescriptions.Find(Ctx.Rom);
        HasDescriptions = _descriptions != null;
        SelectedIndex = -1;
        SelectedIndex = 1;
        RefreshNewItemNote();
    }

    private int _lastIndex = 1;

    partial void OnSelectedIndexChanged(int value)
    {
        if (value >= 0) _lastIndex = value;
        LoadSelected();
    }

    // replacing a renamed entry in the shared list clears the ListBox selection, so put it back
    protected override void OnNamesChanged()
    {
        if (SelectedIndex < 0) SelectedIndex = _lastIndex;
        else LoadSelected();
    }

    protected override void LoadRecord()
    {
        if (Ctx == null || SelectedIndex < 0) return;
        var rom = Ctx.Rom;
        int record = _recordOffset = Ctx.ItemOffset(SelectedIndex);

        Name = Ctx.ReadName(record, GameDataOffsets.ItemNameLen);

        int type = rom.ReadByte(record + 12);
        TypeOptions = GameDataLabels.WithValue(GameDataLabels.ItemTypes, type);
        Type = type;

        int slot = rom.ReadByte(record + 13);
        SlotOptions = GameDataLabels.WithValue(GameDataLabels.EquipSlots, slot);
        Slot = slot;

        int equip  = rom.ReadByte(record + 14);
        EquipHero  = (equip & 0x10) != 0;
        EquipLufia = (equip & 0x20) != 0;
        EquipAguro = (equip & 0x40) != 0;
        EquipJerin = (equip & 0x80) != 0;
        TargetOptions = GameDataLabels.WithValue(GameDataLabels.Targets, equip & 0x0F);
        Target = equip & 0x0F;

        int icon = rom.ReadByte(record + 15);
        IconOptions = GameDataLabels.WithValue(GameDataLabels.ItemIcons, icon);
        Icon = icon;

        Price     = rom.ReadUInt16Le(record + 16);
        HeaderHex = ToHex(rom.ReadBytes(record + 18, 3));

        // the record ends where the next one starts (for the last record, at its 00 at the end).
        // that's the nearest record after this one, a record that grew may have moved to the end of the bank
        int next = record + Core.Battle.ItemTable.Length(rom, SelectedIndex);
        // a record in the item bank keeps its own length (ItemTable.Length). scanning 3 byte entries used to read past
        // its end into the next record, and Apply wrote them back there
        if (record >= Core.Battle.ItemTable.FreeStart && record < Core.Battle.ItemTable.FreeEnd && !Core.Battle.ItemTable.InItemBank(rom, SelectedIndex)) next = -1;
        int entry = record + 21, count = 0;
        while (count < 16 && entry < rom.Length && rom.ReadByte(entry) != 0) { entry += 3; count++; }
        int parsedEnd = entry + 1;
        if (next < 0) next = parsedEnd;

        _tailLength     = next - (record + 21);
        // a list that got shortened in place is followed by zeros up to the next record.
        // byte +20 is a mask of extra data pointers on usable items (that's how ItemTable.Properties reads it), so such
        // a record isn't a property list even if its bytes happen to parse as one. Arrows and Bombs are like that
        HasPropertyList = rom.ReadByte(record + 20) == 0 && count < 16 && (parsedEnd == next || parsedEnd < next && Enumerable.Range(parsedEnd, next - parsedEnd).All(i => rom.ReadByte(i) == 0));

        Properties.Clear();
        if (HasPropertyList)
        {
            for (int i = 0; i < count; i++)
                Properties.Add(new ItemProperty
                {
                    Id    = rom.ReadByte(record + 21 + i * 3),
                    Value = rom.ReadUInt16Le(record + 22 + i * 3),
                });
        }
        EffectHex = _tailLength > 0 ? ToHex(rom.ReadBytes(record + 21, _tailLength)) : "";

        OffsetText = $"Record at 0x{record:X6}, {next - record} bytes  (pointer at 0x{GameDataOffsets.ItemPointerTable + SelectedIndex * 2:X6})";
        LoadStatus();

        if (_descriptions != null)
        {
            Description = _descriptions.Read(SelectedIndex);
            DescriptionInfo = $"Shown when X is pressed on the item (added by the Restored hack). Up to {Core.ItemDescriptions.MaxLines} lines; the window shows {Core.ItemDescriptions.VisibleLineLength} letters a line (up to {Core.ItemDescriptions.MaxLineLength} are written)" +
                              (Core.DutchLetters.IsApplied(Ctx!.Rom) ? ", the Dutch letters included; " : "; ") +
                              $"{_descriptions.Free(AllDescriptions()):N0} bytes of room left for longer texts.";
        }
    }

    [RelayCommand]
    private void Apply()
    {
        if (Ctx == null || SelectedIndex < 0) return;
        var rom = Ctx.Rom;
        int record = _recordOffset;

        // check the hex fields before touching the ROM
        if (!TryParseHex(HeaderHex, out var header) || header.Length != 3)
        {
            Refuse("Header bytes (+18..+20) must be exactly 3 hex bytes.");
            return;
        }
        byte[]? effect = null;
        if (!HasPropertyList)
        {
            if (!TryParseHex(EffectHex, out effect) || effect.Length != _tailLength)
            {
                Refuse($"Effect data must be exactly {_tailLength} hex bytes (records can't change size).");
                return;
            }
        }
        if (HasPropertyList && Properties.Any(p => p.Id == 0))
        {
            Refuse("Property id 00 is the list terminator; pick another id.");
            return;
        }

        string? newDescription = null;
        string typed = Description.Replace("\r", "");
        if (_descriptions != null && typed != _descriptions.Read(SelectedIndex))
        {
            if (Core.ItemDescriptions.Problem(typed, Ctx.Rom) is string bad) { Refuse("Description: " + bad + "."); return; }
            newDescription = typed;
        }

        if (Core.DutchLetters.Unwritable(Ctx.Rom, Name) is string badName) { Refuse("Name: " + badName + "."); return; }
        bool renamed = Name != Ctx.ReadName(record, GameDataOffsets.ItemNameLen);
        bool written = Commit($"Item {SelectedIndex:X2}", () =>
        {
            if (renamed) Ctx.WriteName(record, GameDataOffsets.ItemNameLen, Name);
            rom.WriteByte(record + 12, (byte)Type);
            rom.WriteByte(record + 13, (byte)Slot);
            int equip = (EquipHero ? 0x10 : 0) | (EquipLufia ? 0x20 : 0) |
                        (EquipAguro ? 0x40 : 0) | (EquipJerin ? 0x80 : 0) | (Target & 0x0F);
            rom.WriteByte(record + 14, (byte)equip);
            rom.WriteByte(record + 15, (byte)Icon);
            rom.WriteUInt16Le(record + 16, (ushort)Clamp(Price, 0, 0xFFFF));
            rom.WriteBytes(record + 18, header);

            if (HasPropertyList)
            {
                for (int i = 0; i < Properties.Count; i++)
                {
                    rom.WriteByte(record + 21 + i * 3, (byte)Properties[i].Id);
                    rom.WriteUInt16Le(record + 22 + i * 3, (ushort)Clamp(Properties[i].Value, 0, 0xFFFF));
                }
            }
            else if (effect!.Length > 0)
            {
                rom.WriteBytes(record + 21, effect);
            }
            if (newDescription != null)
            {
                var all = AllDescriptions();
                all[SelectedIndex] = newDescription;
                _descriptions!.WriteAll(all);
            }
            Core.Battle.ItemTable.Sync(rom, SelectedIndex);   // the item bank's copy, the one the game reads
        }, namesChanged: renamed);
        if (!written) return;   // the edits stay, with the error

        if (!renamed) LoadSelected();
        if (newDescription != null && Core.ItemDescriptions.Warning(newDescription) is { Length: > 0 } warning) Status += " Warning, description: " + warning + ".";
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
