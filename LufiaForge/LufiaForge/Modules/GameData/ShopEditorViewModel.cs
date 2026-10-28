using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LufiaForge.Core.Battle;
using System.Collections.ObjectModel;

namespace LufiaForge.Modules.GameData;

public partial class ShopSlot : ObservableObject
{
    public int Number { get; init; }
    [ObservableProperty] private int _item;
}

/// <summary>Shop type and the items each shop sells. The game's shops keep their number of slots unless they are given
/// more (then Lufia Forge keeps them, with room for 11); new shops are made with Add a new shop
/// (<see cref="ShopTable"/>).</summary>
public partial class ShopEditorViewModel : GameDataEditorBase
{
    public ObservableCollection<string>   Shops { get; } = new();
    public ObservableCollection<ShopSlot> Slots { get; } = new();

    [ObservableProperty] private int    _selectedIndex = -1;
    [ObservableProperty] private int    _shopType;
    [ObservableProperty] private ByteOption[] _shopTypeOptions = GameDataLabels.ShopTypes;
    [ObservableProperty] private bool   _isEditable;
    [ObservableProperty] private string _offsetText = "";
    [ObservableProperty] private string _newShopNote = "";
    [ObservableProperty] private bool   _canAddShop;
    [ObservableProperty] private bool   _canGiveMoreSlots;

    private List<int> _ids = new();
    private int _recordOffset;
    private bool _own;

    private int Id => SelectedIndex >= 0 && SelectedIndex < _ids.Count ? _ids[SelectedIndex] : -1;

    protected override void OnRomLoaded()
    {
        RefreshList();
        SelectedIndex = -1;
        SelectedIndex = 1;
    }

    private void RefreshList()
    {
        if (Ctx == null) return;
        int keep = SelectedIndex;
        _ids = ShopTable.Ids(Ctx.Rom);
        Shops.Clear();
        foreach (int i in _ids)
            Shops.Add($"{i:X2}  " + (i < GameDataLabels.ShopTowns.Length ? GameDataLabels.ShopTowns[i] : "") +
                      (i >= ShopTable.GameShops ? "(added)" : ShopTable.IsOwn(Ctx.Rom, i) ? "  (more slots)" : ""));
        CanAddShop = ShopTable.CanInstall(Ctx.Rom, out var why) && ShopTable.NextId(Ctx.Rom) > 0;
        NewShopNote = CanAddShop
            ? $"a copy of the shop shown as shop {ShopTable.NextId(Ctx.Rom):X2}, with room for {ShopTable.MaxItems} items. An event opens it with \"Open a shop\" (1C {ShopTable.NextId(Ctx.Rom):X2}) on the Events tab, for example in a character's talk event."
            : why.Length > 0 ? why : "Every shop id is used.";
        SelectedIndex = keep;
    }

    partial void OnSelectedIndexChanged(int value) => LoadSelected();

    protected override void LoadRecord()
    {
        Slots.Clear();
        if (Ctx == null || Id < 0) return;
        var rom = Ctx.Rom;
        int id = Id;
        _own = ShopTable.IsOwn(rom, id);
        int o = _recordOffset = ShopTable.RecordOffset(rom, id);
        int size;
        if (_own) size = ShopTable.MaxItems;
        else
        {
            int next = Ctx.ShopOffset(id + 1);
            size = next - o - 2;    // minus the type byte and the 00 terminator
        }

        int type = rom.ReadByte(o);
        ShopTypeOptions = GameDataLabels.WithValue(GameDataLabels.ShopTypes, type);
        ShopType = type;

        IsEditable = size > 0 && size <= 32;
        CanGiveMoreSlots = !_own && ShopTable.CanInstall(rom, out _) && size < ShopTable.MaxItems;
        if (IsEditable)
        {
            for (int i = 0; i < size; i++)
                Slots.Add(new ShopSlot { Number = i + 1, Item = rom.ReadByte(o + 1 + i) });
            OffsetText = _own
                ? $"Kept by Lufia Forge at 0x{o:X6}: type + {size} item slots. Opened by event command 1C {id:X2} (Open a shop)."
                : $"Shop data at 0x{o:X6}: type + {size} item slots + 00 terminator. Opened by event command 1C {id:X2} (Open a shop).";
        }
        else
        {
            OffsetText = $"Shop data at 0x{o:X6}: this shop has no item slots.";
        }
    }

    [RelayCommand]
    private void Apply()
    {
        if (Ctx == null || Id < 0 || !IsEditable) return;
        int o = _recordOffset, id = Id;

        if (_own)
        {
            var items = Slots.Select(s => Clamp(s.Item, 0, 255)).TakeWhile(i => i != 0).ToList();
            Commit($"Shop {id:X2}", () => ShopTable.Write(Ctx.Rom, id, Clamp(ShopType, 0, 255), items));
        }
        else
            Commit($"Shop {id:X2}", () =>
            {
                Ctx.Rom.WriteByte(o, (byte)ShopType);
                for (int i = 0; i < Slots.Count; i++)
                    Ctx.Rom.WriteByte(o + 1 + i, (byte)Clamp(Slots[i].Item, 0, 255));
            });

        if (Slots.Any(s => s.Item == 0))
            Status += " Note: an empty slot (00) ends the shop's list at that point.";
    }

    /// <summary>A new shop: a copy of the one shown (type and items) with an id of its own and room for 11 items.</summary>
    [RelayCommand]
    private void AddShop()
    {
        if (Ctx == null || Id < 0) return;
        if (Edited) WritePendingEdits();
        int from = Id, made = -1;
        Commit("New shop", () => made = ShopTable.Add(Ctx.Rom, from, UsedByEvents()));
        if (made < 0) return;
        RefreshList();
        SelectedIndex = _ids.IndexOf(made);
        Status = $"Shop {made:X2} made (a copy of shop {from:X2}). Pick its items, then open it from an event: \"Open a shop\" (1C {made:X2}) on the Events tab.";
    }

    /// <summary>The game shop shown gets room for 11 items (Lufia Forge keeps it from then on; events open it as before).</summary>
    [RelayCommand]
    private void GiveMoreSlots()
    {
        if (Ctx == null || Id < 0 || _own) return;
        if (Edited) WritePendingEdits();
        int id = Id;
        var (type, items) = ShopTable.Read(Ctx.Rom, id);
        Commit($"Shop {id:X2} given {ShopTable.MaxItems} slots", () => ShopTable.Write(Ctx.Rom, id, type, items));
        RefreshList();
        LoadSelected();
    }

    /// <summary>Shop ids events already open (1C xx), so a new shop doesn't take one of them.</summary>
    private HashSet<int> UsedByEvents()
    {
        var used = new HashSet<int>();
        if (Ctx == null) return used;
        try
        {
            foreach (var map in Core.Maps.MapCatalog.Scan(Ctx.Rom))
                for (int ev = 0, n = Core.Maps.EventScript.SlotCount(Ctx.Rom, map.MapId); ev < n; ev++)
                {
                    try
                    {
                        var s = Core.Maps.EventScript.Load(Ctx.Rom, map.MapId, ev);
                        if (s == null) continue;
                        foreach (var o in s.Ops) if (!o.IsText && o.Bytes.Length >= 2 && o.Bytes[0] == 0x1C) used.Add(o.Bytes[1]);
                    }
                    catch { }
                }
        }
        catch { }
        return used;
    }
}
