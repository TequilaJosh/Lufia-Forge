using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LufiaForge.Core.Lufia2;

namespace LufiaForge.Modules.Lufia2;

/// <summary>A row of the feeding table: points for each group of price tiers in one form, and the tier it starts at.</summary>
public sealed class L2FeedRow
{
    public string Label { get; init; } = "";
    public List<L2Word> Cells { get; init; } = new();
    public L2Word Start { get; init; } = new();
}

/// <summary>Feeding (Capsule > Feed): the items the monsters ask for, the points an item gives toward the next form, the
/// price tiers, the wished-item bonus and the fruits (<see cref="L2CapsuleFeed"/>).</summary>
public partial class L2CapsulesPage
{
    private List<L2Battle.Item> _items = new();
    private List<int>[] _lists = Array.Empty<List<int>>();

    [ObservableProperty] private bool _feedEditable;
    [ObservableProperty] private string _feedNote = "";
    [ObservableProperty] private List<L2Option> _itemOptions = new();
    [ObservableProperty] private List<string> _groupHeaders = new();
    [ObservableProperty] private List<L2FeedRow> _feedRows = new();
    [ObservableProperty] private List<L2Word> _thresholds = new();
    [ObservableProperty] private List<L2Word> _feedValues = new();
    public List<L2Option> BonusOptions { get; } = Enumerable.Range(1, 4).Select(i => new L2Option(i, i == 1 ? "x1 (no bonus)" : $"x{i}" + (i == 3 ? " (the game's)" : ""))).ToList();
    [ObservableProperty] private int _wishBonus;
    [ObservableProperty] private int _magicFruit;
    // the shown monster's fruits
    [ObservableProperty] private string _fruitTitle = "";
    [ObservableProperty] private int _ownFruit;
    [ObservableProperty] private int _form5Fruit;
    [ObservableProperty] private int _fruitForm;
    public List<L2Option> FruitForms { get; } = Enumerable.Range(1, 4).Select(i => new L2Option(i, $"form {i}")).ToList();
    // wish lists
    [ObservableProperty] private List<L2Option> _tierOptions = new();
    [ObservableProperty] private int _wishTier;
    public ObservableCollection<L2Slot> WishItems { get; } = new();
    [ObservableProperty] private int _newWishItem = 1;
    [ObservableProperty] private string _wishNote = "";
    // what an item gives
    [ObservableProperty] private int _checkItem = 1;
    [ObservableProperty] private string _checkText = "";

    private bool _feedLoading;

    private void LoadFeed()
    {
        var rom = Rom!;
        _feedLoading = true;
        try
        {
            string? why = L2CapsuleFeed.Unsupported(rom);
            FeedEditable = why == null;
            FeedNote = why ?? "";
            if (why != null && rom.Length < 0x80000) return;
            _items = L2Battle.Items(rom);
            var options = _items.Select(i => new L2Option(i.Index, $"{i.Index:X3} {i.Name}  ({i.Price} G)")).ToList();
            if (!options.SequenceEqual(ItemOptions)) ItemOptions = options;
            _lists = L2CapsuleFeed.ReadLists(rom);
            GroupHeaders = Enumerable.Range(0, L2CapsuleFeed.Groups).Select(g =>
            {
                var (lo, _) = L2CapsuleFeed.TierPrices(rom, g * 4);
                var (_, hi) = L2CapsuleFeed.TierPrices(rom, g * 4 + 3);
                return $"Tiers {g * 4}-{g * 4 + 3}\n{lo}-{hi} G";
            }).ToList();
            FeedRows = Enumerable.Range(1, L2CapsuleFeed.FeedForms).Select(f => new L2FeedRow
            {
                Label = $"Form {f}",
                Cells = Enumerable.Range(0, L2CapsuleFeed.Groups).Select(g => new L2Word { Label = $"{f}:{g}", Value = L2CapsuleFeed.Gain(rom, f, g), Changed = FeedEdited }).ToList(),
                Start = new L2Word { Label = $"start{f}", Value = L2CapsuleFeed.Pickiness(rom, f), Changed = FeedEdited },
            }).ToList();
            var th = L2CapsuleFeed.ReadThresholds(rom);
            Thresholds = th.Select((v, i) => new L2Word { Label = $"Tier {i}: up to", Value = v, Changed = FeedEdited }).ToList();
            FeedValues = new List<L2Word>
            {
                new() { Label = "Lowest price that counts (G)", Value = L2CapsuleFeed.MinPrice(rom), Changed = FeedEdited },
                new() { Label = "Its own fruit gives (points)", Value = L2CapsuleFeed.OwnFruitPoints(rom), Changed = FeedEdited },
                new() { Label = "The Magic fruit gives (points)", Value = L2CapsuleFeed.MagicFruitPoints(rom), Changed = FeedEdited },
                new() { Label = "Most points one feeding gives", Value = L2CapsuleFeed.MaxPerFeeding(rom), Changed = FeedEdited },
            };
            WishBonus = Math.Max(1, L2CapsuleFeed.WishBonus(rom));
            MagicFruit = L2CapsuleFeed.MagicFruit(rom);
            if (_r != null)
            {
                int m = _r.Monster;
                FruitTitle = $"{L2Capsules.MonsterName(rom, m).ToUpperInvariant()}'S FRUITS";
                OwnFruit = L2CapsuleFeed.OwnFruitOf(rom, m);
                Form5Fruit = L2CapsuleFeed.Form5FruitOf(rom, m);
                FruitForm = L2CapsuleFeed.FruitFormOf(rom, m);
            }
            TierOptions = Enumerable.Range(0, L2CapsuleFeed.Tiers).Select(TierLabel).ToList();
            ShowWishList();
            ShowCheck();
            // lists picked from were made again: show the picks in them again
            foreach (var p in new[] { nameof(WishTier), nameof(WishBonus), nameof(MagicFruit), nameof(OwnFruit), nameof(Form5Fruit), nameof(FruitForm), nameof(NewWishItem), nameof(CheckItem) })
                OnPropertyChanged(p);
        }
        finally { _feedLoading = false; }
    }

    private L2Option TierLabel(int t)
    {
        var (lo, hi) = L2CapsuleFeed.TierPrices(Rom!, t);
        int n = _lists[t].Count;
        return new L2Option(t, $"Tier {t}  ({lo}-{hi} G):  " + (n == 0 ? "empty" : $"{n} item{(n == 1 ? "" : "s")}"));
    }

    private void ShowWishList()
    {
        int t = Math.Clamp(WishTier, 0, L2CapsuleFeed.Tiers - 1);
        WishItems.Clear();
        for (int i = 0; i < _lists[t].Count; i++)
            WishItems.Add(new L2Slot { Label = $"{i + 1}", Options = ItemOptions, Value = _lists[t][i], Changed = WishEdited });
        int used = _lists.Sum(l => l.Count);
        string empty = _lists[t].Count > 0 ? "" : "Empty: a monster at this tier asks for the first item of the next list that has items. ";
        WishNote = empty + $"All lists hold {used} item{(used == 1 ? "" : "s")}; " +
                   (used <= L2CapsuleFeed.OriginalCapacity ? $"up to {L2CapsuleFeed.OriginalCapacity} fit where the game keeps them, more go to the added space." : "more than the game's room, so they are kept in the added space.");
    }

    partial void OnWishTierChanged(int value)
    {
        if (Rom == null || _lists.Length == 0) return;
        bool l = _feedLoading; _feedLoading = true;
        try { ShowWishList(); } finally { _feedLoading = l; }
    }

    partial void OnCheckItemChanged(int value) { if (Rom != null && _items.Count > 0) ShowCheck(); }

    /// <summary>What feeding <see cref="CheckItem"/> does, in each form.</summary>
    private void ShowCheck()
    {
        var rom = Rom!;
        if (CheckItem < 0 || CheckItem >= _items.Count) { CheckText = ""; return; }
        var it = _items[CheckItem];
        int m = _r?.Monster ?? 0;
        string name = L2Capsules.MonsterName(rom, m);
        var lines = new List<string>();
        if (it.Index == L2CapsuleFeed.MagicFruit(rom)) lines.Add($"The Magic fruit: {L2CapsuleFeed.MagicFruitPoints(rom)} points for any monster, in forms 1-3.");
        for (int k = 0; k < L2Capsules.Monsters; k++)
        {
            string who = L2Capsules.MonsterName(rom, k);
            if (it.Index == L2CapsuleFeed.OwnFruitOf(rom, k)) lines.Add($"{who}'s own fruit: {L2CapsuleFeed.OwnFruitPoints(rom)} point(s) and its pickiness starts again; in its 5th form it turns {who} back to form {L2CapsuleFeed.FruitFormOf(rom, k)}.");
            if (it.Index == L2CapsuleFeed.Form5FruitOf(rom, k)) lines.Add($"Turns {who} into its 5th form when fed in form {L2CapsuleFeed.FruitFormOf(rom, k)}.");
        }
        if (lines.Count > 0 && (it.Index == L2CapsuleFeed.MagicFruit(rom) || it.Index == L2CapsuleFeed.OwnFruitOf(rom, m)))
        {
            CheckText = string.Join("\n", lines);
            return;
        }
        if ((it.Use & 0x20) != 0) lines.Add("It can't be dropped, so it can't be fed.");
        int tier = L2CapsuleFeed.Tier(rom, it.Price);
        if (tier < 0) lines.Add($"Price {it.Price} G is under {L2CapsuleFeed.MinPrice(rom)} G: always refused (the monster eats it, gets nothing and gets a little less picky).");
        else
        {
            lines.Add($"Price {it.Price} G: tier {tier}. Eaten when the monster's pickiness is {tier} or lower, which then becomes {tier}.");
            for (int f = 1; f <= L2CapsuleFeed.FeedForms; f++)
            {
                int p = L2CapsuleFeed.Points(rom, f, it.Price, 0, false), w = L2CapsuleFeed.Points(rom, f, it.Price, 0, true);
                lines.Add($"  Form {f}: {p} point{(p == 1 ? "" : "s")}, {w} when it is the item asked for (the meter is full at {L2CapsuleFeed.MeterFull}).");
            }
        }
        var asked = Enumerable.Range(0, L2CapsuleFeed.Tiers).Where(t => _lists[t].Contains(it.Index)).ToList();
        lines.Add(asked.Count == 0 ? "No monster asks for it." : $"Asked for at tier{(asked.Count == 1 ? "" : "s")} {string.Join(", ", asked)}.");
        CheckText = string.Join("\n", lines);
    }

    private void Notify(string what)
    {
        Main!.NotifyRomModified("Lufia II: capsule feeding, " + what);
        var keep = (WishTier, CheckItem);
        LoadFeed();
        _feedLoading = true;
        try { WishTier = keep.WishTier; CheckItem = keep.CheckItem; ShowWishList(); ShowCheck(); } finally { _feedLoading = false; }
    }

    private bool CanEditFeed => !_feedLoading && !Loading && Rom != null && Main != null && FeedEditable;

    private void FeedEdited()
    {
        if (!CanEditFeed) return;
        var rom = Rom!;
        foreach (var (row, f) in FeedRows.Select((r, i) => (r, i + 1)))
        {
            for (int g = 0; g < row.Cells.Count; g++) L2CapsuleFeed.WriteGain(rom, f, g, row.Cells[g].Value);
            L2CapsuleFeed.WritePickiness(rom, f, row.Start.Value);
        }
        for (int i = 0; i < Thresholds.Count; i++) L2CapsuleFeed.WriteThreshold(rom, i, Thresholds[i].Value);
        L2CapsuleFeed.WriteMinPrice(rom, FeedValues[0].Value);
        L2CapsuleFeed.WriteOwnFruitPoints(rom, FeedValues[1].Value);
        L2CapsuleFeed.WriteMagicFruitPoints(rom, FeedValues[2].Value);
        L2CapsuleFeed.WriteMaxPerFeeding(rom, FeedValues[3].Value);
        Notify("points and prices");
    }

    partial void OnWishBonusChanged(int value)
    {
        if (!CanEditFeed) return;
        L2CapsuleFeed.WriteWishBonus(Rom!, value);
        Notify($"wished item x{value}");
    }

    partial void OnMagicFruitChanged(int value)
    {
        if (!CanEditFeed) return;
        L2CapsuleFeed.WriteMagicFruit(Rom!, value);
        Notify("Magic fruit item");
    }

    partial void OnOwnFruitChanged(int value) => FruitEdited();
    partial void OnForm5FruitChanged(int value) => FruitEdited();
    partial void OnFruitFormChanged(int value) => FruitEdited();

    private void FruitEdited()
    {
        if (!CanEditFeed || _r == null) return;
        int m = _r.Monster;
        L2CapsuleFeed.WriteOwnFruit(Rom!, m, OwnFruit);
        L2CapsuleFeed.WriteForm5Fruit(Rom!, m, Form5Fruit);
        L2CapsuleFeed.WriteFruitForm(Rom!, m, FruitForm);
        Notify($"{L2Capsules.MonsterName(Rom!, m)}'s fruits");
    }

    private void WriteWishLists(string what)
    {
        try { L2CapsuleFeed.WriteLists(Rom!, _lists); }
        catch (Exception e) { WishNote = "Not written: " + e.Message; _lists = L2CapsuleFeed.ReadLists(Rom!); return; }
        Notify(what);
    }

    private void WishEdited()
    {
        if (!CanEditFeed) return;
        int t = WishTier;
        _lists[t] = WishItems.Select(s => s.Value).ToList();
        WriteWishLists($"tier {t} wish list");
    }

    [RelayCommand]
    private void AddWish()
    {
        if (!CanEditFeed) return;
        int t = WishTier;
        if (_lists[t].Count >= 255) { WishNote = "A list holds up to 255 items."; return; }
        _lists[t].Add(NewWishItem);
        WriteWishLists($"tier {t} wish list");
    }

    [RelayCommand]
    private void RemoveWish(L2Slot? slot)
    {
        if (!CanEditFeed || slot == null) return;
        int i = WishItems.IndexOf(slot), t = WishTier;
        if (i < 0) return;
        _lists[t].RemoveAt(i);
        WriteWishLists($"tier {t} wish list");
    }
}
