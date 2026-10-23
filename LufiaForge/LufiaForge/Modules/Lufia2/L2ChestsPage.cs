using CommunityToolkit.Mvvm.ComponentModel;
using LufiaForge.Core.Lufia2;

namespace LufiaForge.Modules.Lufia2;

/// <summary>Treasure chests: the maps that have some, and for the picked map each chest's place and item.</summary>
public partial class L2ChestsPage : L2Page
{
    private List<int> _maps = new();
    private List<L2Chests.Chest> _chests = new();
    [ObservableProperty] private List<L2Slot> _slots = new();
    [ObservableProperty] private string _info = "";

    protected override IEnumerable<string> ReadNames()
    {
        _maps = Enumerable.Range(2, L2Maps.Count - 2).Where(m => L2Chests.Read(Rom!, m).Count > 0).ToList();
        var items = L2Battle.Items(Rom!);
        return _maps.Select(m =>
        {
            var c = L2Chests.Read(Rom!, m);
            return $"{m:X2}  {L2Maps.Name(Rom!, m)}  ({c.Count})";
        });
    }

    protected override void Load(int index)
    {
        var items = L2Battle.Items(Rom!).Select(i => new L2Option(i.Index, $"{i.Index:X3} {i.Name}")).ToList();
        _chests = L2Chests.Read(Rom!, _maps[index]);
        Slots = _chests.Select(c => new L2Slot
        {
            Label = $"Chest {c.Spot:X2}{(c.X >= 0 ? $" at ({c.X},{c.Y})" : "")}{(c.Special ? " *" : "")}",
            Options = items, Value = c.Item, Changed = Edited,
        }).ToList();
        int total = _maps.Sum(m => L2Chests.Read(Rom!, m).Count);
        Info = $"{_chests.Count} chests on this map ({total} in the game). Places are in 16x16 blocks, as on the Maps page. " +
               "Each chest has its own opened flag, so the item can be anything. * = left out of the game's opened-chests list (probably not a plain chest). Read from the game's code; not yet checked by opening one in the game.";
    }

    protected override bool IsField(string p) => false;

    protected override string? Write()
    {
        for (int i = 0; i < _chests.Count && i < Slots.Count; i++)
        {
            if (_chests[i].Item == Slots[i].Value) continue;
            _chests[i].Item = Slots[i].Value;
            L2Chests.WriteItem(Rom!, _chests[i]);
        }
        return $"chests of map {_maps[SelectedIndex]:X2}";
    }
}
