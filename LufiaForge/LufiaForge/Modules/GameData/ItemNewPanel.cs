using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LufiaForge.Core;
using LufiaForge.Core.Battle;
using LufiaForge.Core.Maps;

namespace LufiaForge.Modules.GameData;

/// <summary>New items: a free item number (one the game never uses) becomes a copy of the selected item, stored in the
/// item bank (see <see cref="ItemTable"/>).</summary>
public partial class ItemEditorViewModel
{
    [ObservableProperty] private string _newItemNote = "";

    /// <summary>Items the game hands out somewhere: shops, chests and hidden items, monster drops, events.</summary>
    public static HashSet<int> ReferencedItems(RomBuffer rom)
    {
        var set = new HashSet<int>();
        for (int s = 0; s < GameDataOffsets.ShopCount; s++)
        {
            int p = rom.ReadUInt16Le(GameDataOffsets.ShopPointerTable + s * 2) + 1;
            for (int k = 0; k < 64 && p < rom.Length && rom.ReadByte(p) != 0; k++, p++) set.Add(rom.ReadByte(p));
        }
        for (int m = 0, n = MonsterTable.Count(rom); m < n; m++) set.Add(rom.ReadByte(MonsterTable.RecordOffset(rom, m) + 32));
        foreach (var map in MapCatalog.Scan(rom))
        {
            try { foreach (var spot in MapSetupScript.ReadSpots(rom, map.MapId)) if (spot.Kind is SpotKind.Chest or SpotKind.Hidden) set.Add(spot.Low); } catch { }
            for (int ev = 0, slots = EventScript.SlotCount(rom, map.MapId); ev < slots; ev++)
            {
                try
                {
                    var s = EventScript.Load(rom, map.MapId, ev);
                    if (s == null) continue;
                    foreach (var o in s.Ops) if (!o.IsText && o.Bytes.Length >= 2 && o.Bytes[0] == 0x3E) set.Add(o.Bytes[1]);
                }
                catch { }
            }
        }
        return set;
    }

    /// <summary>(Cheap: the scan of shops, chests and events for free item numbers runs only when New item is clicked.)</summary>
    private void RefreshNewItemNote()
    {
        if (Ctx == null) return;
        try { NewItemNote = $"{ItemTable.Free(Ctx.Rom):N0} bytes of item space free."; }
        catch { NewItemNote = ""; }
    }

    [RelayCommand]
    private void NewItem()
    {
        if (Ctx == null || SelectedIndex < 0) return;
        var free = ItemTable.FreeIds(Ctx.Rom, ReferencedItems(Ctx.Rom));
        if (free.Count == 0) { Status = "No free item numbers left (item numbers are one byte: the game's unused \"Dummy\" / \"Not Use\" slots are all taken)."; return; }
        if (System.Windows.MessageBox.Show($"Make item {free[0]:X2} a copy of {Name.Replace('@', ' ').Trim()}? Then rename it and change it; give it out in shops, chests or events.",
                "New item", System.Windows.MessageBoxButton.OKCancel, System.Windows.MessageBoxImage.Question) != System.Windows.MessageBoxResult.OK) return;
        MakeItem(free[0]);
    }

    public void MakeItem(int id)
    {
        if (Ctx == null || SelectedIndex < 0) return;
        int template = SelectedIndex;
        bool ok = false;
        Commit($"New item {id:X2}", () => { ItemTable.NewItem(Ctx.Rom, id, template); ok = true; }, namesChanged: true);
        if (!ok) return;
        SelectedIndex = id;
        Status = $"Item {id:X2} is now a copy of item {template:X2}. Rename it and change it, then Apply.";
        RefreshNewItemNote();
    }
}
