using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LufiaForge.Core.Lufia2;

namespace LufiaForge.Modules.Lufia2;

// a row of the feeding table: points for each group of price tiers in one form, plus the tier it starts at
public sealed class L2FeedRow
{
    public string Label { get; init; } = "";
    public List<L2Word> Cells { get; init; } = new();
    public L2Word Start { get; init; } = new();
}

// feeding (Capsule > Feed): the items the monsters ask for, the points an item gives toward the next form, the price
// tiers, the wished-item bonus and the fruits (L2CapsuleFeed)
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
    // whose lists are shown: -1 = the game's (all capsules share them), otherwise a monster with its own lists
    // (L2CapsuleWishes), for all its forms (0) or one form (1-3)
    private List<int>[]? _ownLists;
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
            var options = _items.Select(item => new L2Option(item.Index, $"{item.Index:X3} {item.Name}  ({item.Price} G)")).ToList();
            if (!options.SequenceEqual(ItemOptions)) ItemOptions = options;
            _lists = L2CapsuleFeed.ReadLists(rom);
            GroupHeaders = Enumerable.Range(0, L2CapsuleFeed.Groups).Select(group =>
            {
                var (lowPrice, _) = L2CapsuleFeed.TierPrices(rom, group * 4);
                var (_, highPrice) = L2CapsuleFeed.TierPrices(rom, group * 4 + 3);
                return $"Tiers {group * 4}-{group * 4 + 3}\n{lowPrice}-{highPrice} G";
            }).ToList();
            FeedRows = Enumerable.Range(1, L2CapsuleFeed.FeedForms).Select(form => new L2FeedRow
            {
                Label = $"Form {form}",
                Cells = Enumerable.Range(0, L2CapsuleFeed.Groups).Select(group => new L2Word { Label = $"{form}:{group}", Value = L2CapsuleFeed.Gain(rom, form, group), Changed = FeedEdited }).ToList(),
                Start = new L2Word { Label = $"start{form}", Value = L2CapsuleFeed.Pickiness(rom, form), Changed = FeedEdited },
            }).ToList();
            var thresholds = L2CapsuleFeed.ReadThresholds(rom);
            Thresholds = thresholds.Select((v, i) => new L2Word { Label = $"Tier {i}: up to", Value = v, Changed = FeedEdited }).ToList();
            FeedValues = new List<L2Word>
            {
                new() { Label = "Lowest price that counts (G)", Value = L2CapsuleFeed.MinPrice(rom), Changed = FeedEdited },
                new() { Label = "Its own fruit gives (points)", Value = L2CapsuleFeed.OwnFruitPoints(rom), Changed = FeedEdited },
                new() { Label = "The Magic fruit gives (points)", Value = L2CapsuleFeed.MagicFruitPoints(rom), Changed = FeedEdited },
                new() { Label = "Most points one feeding gives", Value = L2CapsuleFeed.MaxPerFeeding(rom), Changed = FeedEdited },
            };
            WishBonus = Math.Max(1, L2CapsuleFeed.WishBonus(rom));
            MagicFruit = L2CapsuleFeed.MagicFruit(rom);
            if (_record != null)
            {
                int monster = _record.Monster;
                FruitTitle = $"{L2Capsules.MonsterName(rom, monster).ToUpperInvariant()}'S FRUITS";
                OwnFruit = L2CapsuleFeed.OwnFruitOf(rom, monster);
                Form5Fruit = L2CapsuleFeed.Form5FruitOf(rom, monster);
                FruitForm = L2CapsuleFeed.FruitFormOf(rom, monster);
            }
            _wishOwnOk = L2CapsuleWishes.CanInstall(rom, out _wishOwnWhy);
            var sets = L2CapsuleWishes.ReadAll(rom);
            WishOwnerOptions = new[] { new L2Option(-1, "All capsules (the game's lists)") }
                .Concat(Enumerable.Range(0, L2Capsules.Monsters).Select(monster =>
                {
                    int setCount = sets.Keys.Count(s => s.M == monster);
                    return new L2Option(monster, L2Capsules.MonsterName(rom, monster) + (setCount == 0 ? "  (the game's lists)" : setCount == 1 ? "  (own lists)" : $"  (own lists, {setCount} sets)"));
                })).ToList();
            ShowWishOwner();
            ShowCheck();
            // the lists the picks come from got made again, so show the picks in them again
            foreach (var property in new[] { nameof(WishOwner), nameof(WishForm), nameof(WishTier), nameof(WishBonus), nameof(MagicFruit), nameof(OwnFruit), nameof(Form5Fruit), nameof(FruitForm), nameof(NewWishItem), nameof(CheckItem) })
                OnPropertyChanged(property);
        }
        finally { _feedLoading = false; }
    }

    // the lists shown: the game's, or the picked capsule's own (16 empty lists if it has none)
    private List<int>[] ShownLists => WishOwner < 0 ? _lists : _ownLists ??= Enumerable.Range(0, L2CapsuleFeed.Tiers).Select(_ => new List<int>()).ToArray();

    private L2Option TierLabel(int tier)
    {
        var (lowPrice, highPrice) = L2CapsuleFeed.TierPrices(Rom!, tier);
        int count = ShownLists[tier].Count;
        string what = count > 0 ? $"{count} item{(count == 1 ? "" : "s")}" : WishOwner < 0 ? "empty" : "the game's list";
        return new L2Option(tier, $"Tier {tier}  ({lowPrice}-{highPrice} G):  " + what);
    }

    // shows the picked owner's lists (the game's, or a capsule's own for all its forms or for one form)
    private void ShowWishOwner()
    {
        var rom = Rom!;
        int monster = WishOwner;
        WishOwnShown = monster >= 0;
        if (monster >= 0)
        {
            var forms = new List<L2Option> { new(0, "All its forms") };
            for (int form = 1; form <= L2CapsuleWishes.FeedForms; form++)
                forms.Add(new L2Option(form, $"Form {form}: {L2Capsules.Read(rom, monster * L2Capsules.Forms + form - 1).Name}" + (L2CapsuleWishes.Read(rom, monster, form) != null ? "  (own lists)" : "")));
            if (!forms.SequenceEqual(WishFormOptions)) WishFormOptions = forms;
            if (WishForm < 0 || WishForm > L2CapsuleWishes.FeedForms) WishForm = 0;
            _ownLists = L2CapsuleWishes.Read(rom, monster, WishForm);
            WishOwnHas = _ownLists != null;
            string name = L2Capsules.MonsterName(rom, monster), whose = WishForm == 0 ? name : $"{name} in form {WishForm}";
            bool hasAll = WishForm > 0 && L2CapsuleWishes.Read(rom, monster, 0) != null;
            string emptyUses = hasAll ? $"{name}'s list for all its forms (then the game's)" : "the game's list";
            WishOwnNote = !_wishOwnOk ? _wishOwnWhy :
                _ownLists == null
                    ? $"{whose} has no lists of its own, so it asks for items of {(hasAll ? $"{name}'s lists for all its forms (the game's where a tier is empty)" : "the game's lists")}. " +
                      "Add an item to a tier (or copy the game's lists) to give it its own; a tier left empty uses " + emptyUses + "."
                    : $"{whose} asks for items of these lists; a tier left empty uses {emptyUses}." +
                      (WishForm == 0 ? " A form with its own lists uses those first." : "");
        }
        else _ownLists = null;
        TierOptions = Enumerable.Range(0, L2CapsuleFeed.Tiers).Select(TierLabel).ToList();
        ShowWishList();
        OnPropertyChanged(nameof(WishTier));
        OnPropertyChanged(nameof(WishForm));
    }

    private void ShowWishList()
    {
        int tier = Math.Clamp(WishTier, 0, L2CapsuleFeed.Tiers - 1);
        var shown = ShownLists;
        WishItems.Clear();
        for (int i = 0; i < shown[tier].Count; i++)
            WishItems.Add(new L2Slot { Label = $"{i + 1}", Options = ItemOptions, Value = shown[tier][i], Changed = WishEdited });
        if (WishOwner >= 0)
        {
            // an empty tier in a form's lists uses the monster's lists for all its forms, then the game's
            var allForms = WishForm > 0 ? L2CapsuleWishes.Read(Rom!, WishOwner, 0) : null;
            bool fromAll = allForms != null && allForms[tier].Count > 0;
            var fallback = fromAll ? allForms![tier] : _lists[tier];
            string from = fromAll ? $"{L2Capsules.MonsterName(Rom!, WishOwner)}'s list for all its forms" : "the game's list";
            WishNote = shown[tier].Count > 0 ? "" :
                $"Empty: at this tier it asks for an item of {from}: " + (fallback.Count == 0 ? "(empty)" : string.Join(", ", fallback.Take(6).Select(ItemName)) + (fallback.Count > 6 ? ", ..." : "")) + ".";
            return;
        }
        int used = _lists.Sum(l => l.Count);
        string empty = _lists[tier].Count > 0 ? "" : "Empty: a monster at this tier asks for the first item of the next list that has items. ";
        WishNote = empty + $"All lists hold {used} item{(used == 1 ? "" : "s")}; " +
                   (used <= L2CapsuleFeed.OriginalCapacity ? $"up to {L2CapsuleFeed.OriginalCapacity} fit where the game keeps them, more go to the added space." : "more than the game's room, so they are kept in the added space.");
    }

    private string ItemName(int item) => item >= 0 && item < _items.Count ? _items[item].Name : $"item {item:X3}";

    partial void OnWishTierChanged(int value)
    {
        if (Rom == null || _lists.Length == 0) return;
        bool wasLoading = _feedLoading; _feedLoading = true;
        try { ShowWishList(); } finally { _feedLoading = wasLoading; }
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

    // what feeding CheckItem does, in each form
    private void ShowCheck()
    {
        var rom = Rom!;
        if (CheckItem < 0 || CheckItem >= _items.Count) { CheckText = ""; return; }
        var item = _items[CheckItem];
        int monster = _record?.Monster ?? 0;
        string name = L2Capsules.MonsterName(rom, monster);
        var lines = new List<string>();
        if (item.Index == L2CapsuleFeed.MagicFruit(rom)) lines.Add($"The Magic fruit: {L2CapsuleFeed.MagicFruitPoints(rom)} points for any monster, in forms 1-3.");
        for (int other = 0; other < L2Capsules.Monsters; other++)
        {
            string who = L2Capsules.MonsterName(rom, other);
            if (item.Index == L2CapsuleFeed.OwnFruitOf(rom, other)) lines.Add($"{who}'s own fruit: {L2CapsuleFeed.OwnFruitPoints(rom)} point(s) and its pickiness starts again; in its 5th form it turns {who} back to form {L2CapsuleFeed.FruitFormOf(rom, other)}.");
            if (item.Index == L2CapsuleFeed.Form5FruitOf(rom, other)) lines.Add($"Turns {who} into its 5th form when fed in form {L2CapsuleFeed.FruitFormOf(rom, other)}, if it can't be fed for points there (form 4, or a form below its highest).");
        }
        if (lines.Count > 0 && (item.Index == L2CapsuleFeed.MagicFruit(rom) || item.Index == L2CapsuleFeed.OwnFruitOf(rom, monster)))
        {
            CheckText = string.Join("\n", lines);
            return;
        }
        if ((item.Use & 0x20) != 0) lines.Add("It can't be dropped, so it can't be fed.");
        int tier = L2CapsuleFeed.Tier(rom, item.Price);
        if (tier < 0) lines.Add($"Price {item.Price} G is under {L2CapsuleFeed.MinPrice(rom)} G: always refused (the monster eats it, gets nothing and gets a little less picky).");
        else
        {
            lines.Add($"Price {item.Price} G: tier {tier}. Eaten when the monster's pickiness is {tier} or lower, which then becomes {tier}.");
            for (int form = 1; form <= L2CapsuleFeed.FeedForms; form++)
            {
                int points = L2CapsuleFeed.Points(rom, form, item.Price, 0, false), wishedPoints = L2CapsuleFeed.Points(rom, form, item.Price, 0, true);
                lines.Add($"  Form {form}: {points} point{(points == 1 ? "" : "s")}, {wishedPoints} when it is the item asked for (the meter is full at {L2CapsuleFeed.MeterFull}).");
            }
        }
        var asked = Enumerable.Range(0, L2CapsuleFeed.Tiers).Where(t => _lists[t].Contains(item.Index)).ToList();
        lines.Add(asked.Count == 0 ? "The game's lists don't have it." : $"In the game's lists at tier{(asked.Count == 1 ? "" : "s")} {string.Join(", ", asked)}.");
        foreach (var ((ownerMonster, ownerForm), set) in L2CapsuleWishes.ReadAll(rom).OrderBy(k => k.Key))
        {
            var ownTiers = Enumerable.Range(0, L2CapsuleFeed.Tiers).Where(t => set[t].Contains(item.Index)).ToList();
            if (ownTiers.Count > 0)
                lines.Add($"In {L2Capsules.MonsterName(rom, ownerMonster)}'s own lists{(ownerForm == 0 ? "" : $" for form {ownerForm}")} at tier{(ownTiers.Count == 1 ? "" : "s")} {string.Join(", ", ownTiers)}.");
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
        foreach (var (row, form) in FeedRows.Select((r, i) => (r, i + 1)))
        {
            for (int group = 0; group < row.Cells.Count; group++) L2CapsuleFeed.WriteGain(rom, form, group, row.Cells[group].Value);
            L2CapsuleFeed.WritePickiness(rom, form, row.Start.Value);
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
        if (!CanEditFeed || _record == null) return;
        int monster = _record.Monster;
        L2CapsuleFeed.WriteOwnFruit(Rom!, monster, OwnFruit);
        L2CapsuleFeed.WriteForm5Fruit(Rom!, monster, Form5Fruit);
        L2CapsuleFeed.WriteFruitForm(Rom!, monster, FruitForm);
        Notify($"{L2Capsules.MonsterName(Rom!, monster)}'s fruits");
    }

    // whose lists are shown, for the Undo list
    private string WishWhose => WishOwner < 0 ? "" : $"{L2Capsules.MonsterName(Rom!, WishOwner)}'s{(WishForm == 0 ? "" : $" form {WishForm}")} own ";

    private bool CanEditWishes => CanEditFeed && (WishOwner < 0 || _wishOwnOk);

    private void WriteWishLists(string what)
    {
        try
        {
            if (WishOwner < 0) L2CapsuleFeed.WriteLists(Rom!, _lists);
            else L2CapsuleWishes.Write(Rom!, WishOwner, WishForm, ShownLists);
        }
        catch (Exception e)
        {
            WishNote = "Not written: " + e.Message;
            _lists = L2CapsuleFeed.ReadLists(Rom!);
            _ownLists = WishOwner < 0 ? null : L2CapsuleWishes.Read(Rom!, WishOwner, WishForm);
            return;
        }
        Notify(what);
    }

    private void WishEdited()
    {
        if (!CanEditWishes) return;
        int tier = WishTier;
        ShownLists[tier] = WishItems.Select(s => s.Value).ToList();
        WriteWishLists($"{WishWhose}tier {tier} wish list");
    }

    [RelayCommand]
    private void AddWish()
    {
        if (!CanEditWishes) return;
        int tier = WishTier;
        if (ShownLists[tier].Count >= 255) { WishNote = "A list holds up to 255 items."; return; }
        ShownLists[tier].Add(NewWishItem);
        WriteWishLists($"{WishWhose}tier {tier} wish list");
    }

    [RelayCommand]
    private void RemoveWish(L2Slot? slot)
    {
        if (!CanEditWishes || slot == null) return;
        int slotIndex = WishItems.IndexOf(slot), tier = WishTier;
        if (slotIndex < 0) return;
        ShownLists[tier].RemoveAt(slotIndex);
        WriteWishLists($"{WishWhose}tier {tier} wish list");
    }

    // starts the picked capsule's own lists as a copy of the game's 16 lists
    [RelayCommand]
    private void CopyGameWishes()
    {
        if (!CanEditWishes || WishOwner < 0) return;
        _ownLists = _lists.Select(l => l.ToList()).ToArray();
        WriteWishLists($"{WishWhose}wish lists copied from the game's");
    }

    // the picked capsule (or form) asks for items from the game's lists again
    [RelayCommand]
    private void UseGameWishes()
    {
        if (!CanEditWishes || WishOwner < 0 || _ownLists == null) return;
        string whose = WishWhose;
        _ownLists = null;
        try { L2CapsuleWishes.Write(Rom!, WishOwner, WishForm, null); }
        catch (Exception e) { WishNote = "Not written: " + e.Message; return; }
        Notify($"{whose}wish lists removed (the game's lists again)");
    }
}
