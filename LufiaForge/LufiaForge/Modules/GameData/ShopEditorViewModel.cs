using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using System.Collections.ObjectModel;

namespace LufiaForge.Modules.GameData;

public partial class ShopSlot : ObservableObject
{
    public int Number { get; init; }
    [ObservableProperty] private int _item;
}

/// <summary>Shop type and the items each shop sells. Shops keep their original number of slots.</summary>
public partial class ShopEditorViewModel : GameDataEditorBase
{
    public ObservableCollection<string>   Shops { get; } = new();
    public ObservableCollection<ShopSlot> Slots { get; } = new();

    [ObservableProperty] private int    _selectedIndex = -1;
    [ObservableProperty] private int    _shopType;
    [ObservableProperty] private ByteOption[] _shopTypeOptions = GameDataLabels.ShopTypes;
    [ObservableProperty] private bool   _isEditable;
    [ObservableProperty] private string _offsetText = "";

    private int _recordOffset;

    protected override void OnRomLoaded()
    {
        Shops.Clear();
        for (int i = 0; i < GameDataOffsets.ShopCount; i++)
            Shops.Add($"{i:X2}  {GameDataLabels.ShopTowns[i]}");
        SelectedIndex = -1;
        SelectedIndex = 1;
    }

    partial void OnSelectedIndexChanged(int value) => LoadSelected();

    protected override void LoadSelected()
    {
        Slots.Clear();
        if (Ctx == null || SelectedIndex < 0) return;
        var rom = Ctx.Rom;

        int o    = _recordOffset = Ctx.ShopOffset(SelectedIndex);
        int next = Ctx.ShopOffset(SelectedIndex + 1);
        int size = next - o - 2;    // minus the type byte and the 00 terminator

        int type = rom.ReadByte(o);
        ShopTypeOptions = GameDataLabels.WithValue(GameDataLabels.ShopTypes, type);
        ShopType = type;

        IsEditable = size > 0 && size <= 32;
        if (IsEditable)
        {
            for (int i = 0; i < size; i++)
                Slots.Add(new ShopSlot { Number = i + 1, Item = rom.ReadByte(o + 1 + i) });
            OffsetText = $"Shop data at 0x{o:X6}: type + {size} item slots + 00 terminator.";
        }
        else
        {
            OffsetText = $"Shop data at 0x{o:X6}: this shop has no item slots.";
        }
    }

    [RelayCommand]
    private void Apply()
    {
        if (Ctx == null || SelectedIndex < 0 || !IsEditable) return;
        int o = _recordOffset;

        Commit($"Shop {SelectedIndex:X2}", () =>
        {
            Ctx.Rom.WriteByte(o, (byte)ShopType);
            for (int i = 0; i < Slots.Count; i++)
                Ctx.Rom.WriteByte(o + 1 + i, (byte)Clamp(Slots[i].Item, 0, 255));
        });

        if (Slots.Any(s => s.Item == 0))
            Status += " Note: an empty slot (00) ends the shop's list at that point.";
    }
}
