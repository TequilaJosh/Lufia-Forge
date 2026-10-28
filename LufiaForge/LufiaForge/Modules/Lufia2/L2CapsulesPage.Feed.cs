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
    // whose lists are shown: -1 the game's (all capsules share them), else a monster with its own lists
    // (L2CapsuleWishes), for all its forms (0) or one form (1-3)
    private List<int>[]? _own;
    [ObservableProperty] private List<L2Option> _wishOwnerOptions = new();
    [ObservableProperty] private int _wishOwner = -1;
    [ObservableProperty] private List<L2Option> _wishFormOptions = new();
    [ObservableProperty] private int _wishForm;
    [ObservableProperty] private bool _wishOwnShown;
    [ObservableProperty] private bool _wishOwnHas;
    [ObservableProperty] private string _wishOwnNote = "";
    private bool _wishOwnOk;
    private string _wishOwnWhy = "";
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
            _wishOwnOk = L2CapsuleWishes.CanInstall(rom, out _wishOwnWhy);
            var sets = L2CapsuleWishes.ReadAll(rom);
            WishOwnerOptions = new[] { new L2Option(-1, "All capsules (the game's lists)") }
                .Concat(Enumerable.Range(0, L2Capsules.Monsters).Select(k =>
                {
                    int n = sets.Keys.Count(s => s.M == k);
                    return new L2Option(k, L2Capsules.MonsterName(rom, k) + (n == 0 ? "  (the game's lists)" : n == 1 ? "  (own lists)" : $"  (own lists, {n} sets)"));
                })).ToList();
            ShowWishOwner();
            ShowCheck();
            // lists picked from were made again: show the picks in them again
            foreach (var p in new[] { nameof(WishOwner), nameof(WishForm), nameof(WishTier), nameof(WishBonus), nameof(MagicFruit), nameof(OwnFruit), nameof(Form5Fruit), nameof(FruitForm), nameof(NewWishItem), nameof(CheckItem) })
                OnPropertyChanged(p);
        }
        finally { _feedLoading = false; }
    }

    /// <summary>The lists shown: the game's, or the picked capsule's own (16 empty lists when it has none).</summary>
    private List<int>[] Shown => WishOwner < 0 ? _lists : _own ??= Enumerable.Range(0, L2CapsuleFeed.Tiers).Select(_ => new List<int>()).ToArray();

    private L2Option TierLabel(int t)
    {
        var (lo, hi) = L2CapsuleFeed.TierPrices(Rom!, t);
        int n = Shown[t].Count;
        string what = n > 0 ? $"{n} item{(n == 1 ? "" : "s")}" : WishOwner < 0 ? "empty" : "the game's list";
        return new L2Option(t, $"Tier {t}  ({lo}-{hi} G):  " + what);
    }

    /// <summary>Shows the picked owner's lists (the game's, or a capsule's own for all its forms or one form).</summary>
    private void ShowWishOwner()
    {
        var rom = Rom!;
        int m = WishOwner;
        WishOwnShown = m >= 0;
        if (m >= 0)
        {
            var forms = new List<L2Option> { new(0, "All its forms") };
            for (int f = 1; f <= L2CapsuleWishes.FeedForms; f++)
                forms.Add(new L2Option(f, $"Form {f}: {L2Capsules.Read(rom, m * L2Capsules.Forms + f - 1).Name}" + (L2CapsuleWishes.Read(rom, m, f) != null ? "  (own lists)" : "")));
            if (!forms.SequenceEqual(WishFormOptions)) WishFormOptions = forms;
            if (WishForm < 0 || WishForm > L2CapsuleWishes.FeedForms) WishForm = 0;
            _own = L2CapsuleWishes.Read(rom, m, WishForm);
            WishOwnHas = _own != null;
            string name = L2Capsules.MonsterName(rom, m), whose = WishForm == 0 ? name : $"{name} in form {WishForm}";
            bool hasAll = WishForm > 0 && L2CapsuleWishes.Read(rom, m, 0) != null;
            string emptyUses = hasAll ? $"{name}'s list for all its forms (then the game's)" : "the game's list";
            WishOwnNote = !_wishOwnOk ? _wishOwnWhy :
                _own == null
                    ? $"{whose} has no lists of its own, so it asks for items of {(hasAll ? $"{name}'s lists for all its forms (the game's where a tier is empty)" : "the game's lists")}. " +
                      "Add an item to a tier (or copy the game's lists) to give it its own; a tier left empty uses " + emptyUses + "."
                    : $"{whose} asks for items of these lists; a tier left empty uses {emptyUses}." +
                      (WishForm == 0 ? " A form with its own lists uses those first." : "");
        }
        else _own = null;
        TierOptions = Enumerable.Range(0, L2CapsuleFeed.Tiers).Select(TierLabel).ToList();
        ShowWishList();
        OnPropertyChanged(nameof(WishTier));
        OnPropertyChanged(nameof(WishForm));
    }

    private void ShowWishList()
    {
        int t = Math.Clamp(WishTier, 0, L2CapsuleFeed.Tiers - 1);
        var shown = Shown;
        WishItems.Clear();
        for (int i = 0; i < shown[t].Count; i++)
            WishItems.Add(new L2Slot { Label = $"{i + 1}", Options = ItemOptions, Value = shown[t][i], Changed = WishEdited });
        if (WishOwner >= 0)
        {
            // an empty tier of a form's lists uses the monster's lists for all its forms, then the game's
            var all = WishForm > 0 ? L2CapsuleWishes.Read(Rom!, WishOwner, 0) : null;
            bool fromAll = all != null && all[t].Count > 0;
            var fallback = fromAll ? all![t] : _lists[t];
            string from = fromAll ? $"{L2Capsules.MonsterName(Rom!, WishOwner)}'s list for all its forms" : "the game's list";
            WishNote = shown[t].Count > 0 ? "" :
                $"Empty: at this tier it asks for an item of {from}: " + (fallback.Count == 0 ? "(empty)" : string.Join(", ", fallback.Take(6).Select(ItemName)) + (fallback.Count > 6 ? ", ..." : "")) + ".";
            return;
        }
        int used = _lists.Sum(l => l.Count);
        string empty = _lists[t].Count > 0 ? "" : "Empty: a monster at this tier asks for the first item of the next list that has items. ";
        WishNote = empty + $"All lists hold {used} item{(used == 1 ? "" : "s")}; " +
                   (used <= L2CapsuleFeed.OriginalCapacity ? $"up to {L2CapsuleFeed.OriginalCapacity} fit where the game keeps them, more go to the added space." : "more than the game's room, so they are kept in the added space.");
    }

    private string ItemName(int i) => i >= 0 && i < _items.Count ? _items[i].Name : $"item {i:X3}";

    partial void OnWishTierChanged(int value)
    {
        if (Rom == null || _lists.Length == 0) return;
        bool l = _feedLoading; _feedLoading = true;
        try { ShowWishList(); } finally { _feedLoading = l; }
    }

    partial void OnWishOwnerChanged(int value)
    {
        if (Rom == null || _lists.Length == 0 || _feedLoading) return;
        _feedLoading = true;
        try { WishForm = 0; ShowWishOwner(); } finally { _feedLoading = false; }
    }

    partial void OnWishFormChanged(int value)
    {
        if (Rom == null || _lists.Length == 0 || _feedLoading) return;
        _feedLoading = true;
        try { ShowWishOwner(); } finally { _feedLoading = false; }
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
            if (it.Index == L2CapsuleFeed.Form5FruitOf(rom, k)) lines.Add($"Turns {who} into its 5th form when fed in form {L2CapsuleFeed.FruitFormOf(rom, k)}, if it can't be fed for points there (form 4, or a form below its highest).");
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
        lines.Add(asked.Count == 0 ? "The game's lists don't have it." : $"In the game's lists at tier{(asked.Count == 1 ? "" : "s")} {string.Join(", ", asked)}.");
        foreach (var ((om, of), set) in L2CapsuleWishes.ReadAll(rom).OrderBy(k => k.Key))
        {
            var own = Enumerable.Range(0, L2CapsuleFeed.Tiers).Where(t => set[t].Contains(it.Index)).ToList();
            if (own.Count > 0)
                lines.Add($"In {L2Capsules.MonsterName(rom, om)}'s own lists{(of == 0 ? "" : $" for form {of}")} at tier{(own.Count == 1 ? "" : "s")} {string.Join(", ", own)}.");
        }
        CheckText = string.Join("\n", lines);
    }

    private void Notify(string what)
    {
        Main!.NotifyRomModified("Lufia II: capsule feeding, " + what);
        var keep = (WishTier, CheckItem, WishOwner, WishForm);
        LoadFeed();
        _feedLoading = true;
        try
        {
            WishOwner = keep.WishOwner; WishForm = keep.WishForm; WishTier = keep.WishTier; CheckItem = keep.CheckItem;
            ShowWishOwner(); ShowCheck();
        }
        finally { _feedLoading = false; }
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

    /// <summary>Whose lists the shown ones are, for the Undo list.</summary>
    private string WishWhose => WishOwner < 0 ? "" : $"{L2Capsules.MonsterName(Rom!, WishOwner)}'s{(WishForm == 0 ? "" : $" form {WishForm}")} own ";

    private bool CanEditWishes => CanEditFeed && (WishOwner < 0 || _wishOwnOk);

    private void WriteWishLists(string what)
    {
        try
        {
            if (WishOwner < 0) L2CapsuleFeed.WriteLists(Rom!, _lists);
            else L2CapsuleWishes.Write(Rom!, WishOwner, WishForm, Shown);
        }
        catch (Exception e)
        {
            WishNote = "Not written: " + e.Message;
            _lists = L2CapsuleFeed.ReadLists(Rom!);
            _own = WishOwner < 0 ? null : L2CapsuleWishes.Read(Rom!, WishOwner, WishForm);
            return;
        }
        Notify(what);
    }

    private void WishEdited()
    {
        if (!CanEditWishes) return;
        int t = WishTier;
        Shown[t] = WishItems.Select(s => s.Value).ToList();
        WriteWishLists($"{WishWhose}tier {t} wish list");
    }

    [RelayCommand]
    private void AddWish()
    {
        if (!CanEditWishes) return;
        int t = WishTier;
        if (Shown[t].Count >= 255) { WishNote = "A list holds up to 255 items."; return; }
        Shown[t].Add(NewWishItem);
        WriteWishLists($"{WishWhose}tier {t} wish list");
    }

    [RelayCommand]
    private void RemoveWish(L2Slot? slot)
    {
        if (!CanEditWishes || slot == null) return;
        int i = WishItems.IndexOf(slot), t = WishTier;
        if (i < 0) return;
        Shown[t].RemoveAt(i);
        WriteWishLists($"{WishWhose}tier {t} wish list");
    }

    /// <summary>Starts the picked capsule's own lists as a copy of the game's 16 lists.</summary>
    [RelayCommand]
    private void CopyGameWishes()
    {
        if (!CanEditWishes || WishOwner < 0) return;
        _own = _lists.Select(l => l.ToList()).ToArray();
        WriteWishLists($"{WishWhose}wish lists copied from the game's");
    }

    /// <summary>The picked capsule (form) asks for items of the game's lists again.</summary>
    [RelayCommand]
    private void UseGameWishes()
    {
        if (!CanEditWishes || WishOwner < 0 || _own == null) return;
        string whose = WishWhose;
        _own = null;
        try { L2CapsuleWishes.Write(Rom!, WishOwner, WishForm, null); }
        catch (Exception e) { WishNote = "Not written: " + e.Message; return; }
        Notify($"{whose}wish lists removed (the game's lists again)");
    }
}
