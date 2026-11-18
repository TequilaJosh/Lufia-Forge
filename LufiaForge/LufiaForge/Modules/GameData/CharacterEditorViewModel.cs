using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using System.Collections.ObjectModel;

namespace LufiaForge.Modules.GameData;

// starting level, stats, EXP, equipment and names of the 8 playable characters
public partial class CharacterEditorViewModel : GameDataEditorBase
{
    public ObservableCollection<string> Characters { get; } = new();

    [ObservableProperty] private int _selectedIndex = -1;

    [ObservableProperty] private string _name = "";
    [ObservableProperty] private bool   _canRename;
    [ObservableProperty] private int    _level;
    [ObservableProperty] private int    _hp;
    [ObservableProperty] private int    _mp;
    [ObservableProperty] private int    _str;
    [ObservableProperty] private int    _agl;
    [ObservableProperty] private int    _int;
    [ObservableProperty] private int    _mgr;
    [ObservableProperty] private int    _exp;
    [ObservableProperty] private int    _weapon;
    [ObservableProperty] private int    _armor;
    [ObservableProperty] private int    _shield;
    [ObservableProperty] private int    _helmet;
    [ObservableProperty] private int    _shoes;
    [ObservableProperty] private int    _ring;
    [ObservableProperty] private string _offsetText = "";

    private int RecordOffset => GameDataOffsets.CharacterTable + SelectedIndex * GameDataOffsets.CharacterStride;

    protected override void OnRomLoaded()
    {
        RefreshCharacterList();
        SelectedIndex = -1;
        SelectedIndex = 0;
    }

    protected override void OnNamesChanged()
    {
        RefreshCharacterList();
        LoadSelected();
    }

    private void RefreshCharacterList()
    {
        if (Ctx == null) return;
        int keepIndex = SelectedIndex;
        Characters.Clear();
        for (int i = 0; i < GameDataOffsets.CharacterCount; i++)
            Characters.Add($"{i}  {Ctx.CharacterName(i)}  ({GameDataLabels.CharacterSlots[i]})");
        SelectedIndex = keepIndex;
    }

    partial void OnSelectedIndexChanged(int value) => LoadSelected();

    protected override void LoadRecord()
    {
        if (Ctx == null || SelectedIndex < 0) return;
        var rom = Ctx.Rom;
        int record = RecordOffset;

        CanRename = SelectedIndex > 0;
        Name      = Ctx.CharacterName(SelectedIndex);
        Level     = rom.ReadByte(record +0);
        Hp        = rom.ReadUInt16Le(record +2);
        Mp        = rom.ReadUInt16Le(record +4);
        Agl       = rom.ReadUInt16Le(record +6);
        Int       = rom.ReadUInt16Le(record +8);
        Str       = rom.ReadUInt16Le(record +10);
        Mgr       = rom.ReadUInt16Le(record +12);
        Exp       = (int)rom.ReadUInt24Le(record +14);
        Weapon    = rom.ReadByte(record +23);
        Armor     = rom.ReadByte(record +24);
        Shield    = rom.ReadByte(record +25);
        Helmet    = rom.ReadByte(record +26);
        Shoes     = rom.ReadByte(record +27);
        Ring      = rom.ReadByte(record +28);
        OffsetText = $"Record at 0x{record:X6} ({GameDataOffsets.CharacterStride} bytes)";
    }

    [RelayCommand]
    private void Apply()
    {
        if (Ctx == null || SelectedIndex < 0) return;
        var rom = Ctx.Rom;
        int record = RecordOffset;
        bool renamed = false;
        if (CanRename && Core.DutchLetters.Unwritable(Ctx.Rom, Name) is string badName) { Refuse("Name: " + badName + "."); return; }

        Commit(GameDataLabels.CharacterSlots[SelectedIndex], () =>
        {
            rom.WriteByte(record +0, (byte)Clamp(Level, 0, 255));
            rom.WriteUInt16Le(record +2,  (ushort)Clamp(Hp,  0, 0xFFFF));
            rom.WriteUInt16Le(record +4,  (ushort)Clamp(Mp,  0, 0xFFFF));
            rom.WriteUInt16Le(record +6,  (ushort)Clamp(Agl, 0, 0xFFFF));
            rom.WriteUInt16Le(record +8,  (ushort)Clamp(Int, 0, 0xFFFF));
            rom.WriteUInt16Le(record +10, (ushort)Clamp(Str, 0, 0xFFFF));
            rom.WriteUInt16Le(record +12, (ushort)Clamp(Mgr, 0, 0xFFFF));

            int exp = Clamp(Exp, 0, 0xFFFFFF);
            rom.WriteBytes(record +14, new[] { (byte)exp, (byte)(exp >> 8), (byte)(exp >> 16) });

            rom.WriteByte(record +23, (byte)Clamp(Weapon, 0, 255));
            rom.WriteByte(record +24, (byte)Clamp(Armor,  0, 255));
            rom.WriteByte(record +25, (byte)Clamp(Shield, 0, 255));
            rom.WriteByte(record +26, (byte)Clamp(Helmet, 0, 255));
            rom.WriteByte(record +27, (byte)Clamp(Shoes,  0, 255));
            rom.WriteByte(record +28, (byte)Clamp(Ring,   0, 255));

            if (CanRename && Name != Ctx.CharacterName(SelectedIndex))
            {
                // the original names are padded with 00, like "Guy\0\0"
                Ctx.WriteName(GameDataOffsets.CharacterNames + (SelectedIndex - 1) * GameDataOffsets.CharacterNameLen,
                              GameDataOffsets.CharacterNameLen, Name, pad: 0x00);
                renamed = true;
            }
        });

        if (renamed) Ctx.MarkModified(namesChanged: true);
    }
}
