using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LufiaForge.Core;
using LufiaForge.Core.Lufia2;
using Kind = LufiaForge.Core.Lufia2.L2FlagIndex.Kind;

namespace LufiaForge.Modules.Lufia2;

// a flag in the Flags tab's list
public sealed partial class L2FlagRow : ObservableObject
{
    public Kind Kind { get; init; }
    public int Number { get; init; }
    public string NumberText { get; init; } = "";
    [ObservableProperty] private string _label = "";
    [ObservableProperty] private string _detail = "";
    // nothing uses it and nobody named it, so it's free to take
    public bool Free { get; set; }
    public bool Named { get; set; }
    public double Dim => Free ? 0.6 : 1.0;
}

// a place a flag is used, in the Flags tab
public sealed record L2FlagUseRow(string Text, L2FlagIndex.Use Use)
{
    public bool CanOpen => Use.Head is not (L2FlagIndex.PuzzleHead or L2FlagIndex.ChestHead or L2FlagIndex.DoorHead);
}

// the Flags tab of the Events page. every kind of Lufia II flag (story flags plus the extra ones, event counters,
// treasure chest flags, puzzle switches, doors) with where each one is used, your own name and note for it (kept in
// the project), "New flag" to grab a free one, and the extra story flags patch (L2ExtraFlags)
public sealed partial class L2FlagsPanel : ObservableObject
{
    private readonly L2EventsPage _page;
    private string[] _items = Array.Empty<string>();
    private int _itemsVersion = -1;
    private bool _filling;

    public L2FlagsPanel(L2EventsPage page)
    {
        _page = page;
        L2FlagNotes.Changed += () => { NamesDirty = true; if (!_filling) OnPropertyChanged(nameof(KeptText)); };
    }

    public static IReadOnlyList<L2Option> KindChoices { get; } = new List<L2Option>
    {
        new((int)Kind.Story, "Story flags"), new((int)Kind.Counter, "Event counters"), new((int)Kind.Chest, "Treasure chest flags"),
        new((int)Kind.Switch, "Puzzle switches"), new((int)Kind.Door, "Doors"),
    };
    public static IReadOnlyList<L2Option> ShowChoices { get; } = new List<L2Option>
    {
        new(0, "All"), new(1, "Used"), new(2, "Free"), new(3, "Named by you"),
    };

    [ObservableProperty] private int _kindIndex;
    [ObservableProperty] private int _show;
    [ObservableProperty] private string _filter = "";
    public ObservableCollection<L2FlagRow> Rows { get; } = new();
    [ObservableProperty] private L2FlagRow? _selected;
    public ObservableCollection<L2FlagUseRow> Uses { get; } = new();
    [ObservableProperty] private L2FlagUseRow? _selectedUse;
    [ObservableProperty] private string _title = "";
    [ObservableProperty] private string _nameText = "";
    [ObservableProperty] private string _noteText = "";
    [ObservableProperty] private string _about = "";
    [ObservableProperty] private string _countText = "";
    [ObservableProperty] private string _status = "";

    private Kind Current => (Kind)KindIndex;

    // a name or note changed since the event editor last showed them (it rebuilds its cards when shown)
    public bool NamesDirty { get; set; }
    private RomBuffer? Rom => _page.CurrentRom;

    public string KindHelp => Current switch
    {
        Kind.Story => "Story flags remember what has happened, for the whole game (on or off, kept in the save). Events turn them on and off and check them; " +
                      "a map's start-up script checks them to choose who stands where. The game uses 218 of the 256; F0-FF are cleared on every map change. " +
                      "The extra flags 100-17F (added by Lufia Forge) work the same everywhere a story flag does.",
        Kind.Counter => "Event counters hold a number 0-255 each (kept in the save): \"Set a counter\", \"Add to a counter\", the counter checks of \"Check conditions\", " +
                        "and \"Go by a counter\". The game uses 01-0E. With the extra story flags added, counters 10-1F hold those flags.",
        Kind.Chest => "Every treasure chest (and hidden treasure) has an opened flag, 512 of them, kept in the save: a chest gives its item while its flag is off, then turns it on. " +
                      "Two chests with one flag share it: opening one empties both. The Maps page gives new chests a free flag by itself.",
        Kind.Switch => "Puzzle switches are on/off flags of the puzzle scripts (Maps page, \"+ Puzzle\"). Switches 00-1F are kept in the save and shared by every map; " +
                       "20 and up are cleared whenever a map loads, so they belong to the map that uses them.",
        _ => "Doors belong to their map (map info list +02) and close again whenever the map loads. Events open, close and check them; puzzle scripts too. " +
             "Name them here so the event editor's door lists read better.",
    };

    public bool CanNew => Current is Kind.Story or Kind.Counter or Kind.Switch;
    public bool ShowExtra => Current == Kind.Story && Rom != null && !L2ExtraFlags.IsApplied(Rom);
    public string KeptText => L2FlagNotes.Kept ? "Names and notes are kept in the project." : "Names and notes last until Lufia Forge closes: open or start a project to keep them.";

    partial void OnKindIndexChanged(int value) { Status = ""; OnPropertyChanged(nameof(KindHelp)); OnPropertyChanged(nameof(CanNew)); OnPropertyChanged(nameof(ShowExtra)); Fill(); }
    partial void OnShowChanged(int value) => Fill();
    partial void OnFilterChanged(string value) => Fill();

    // reads the list again (the ROM changed, or the page is shown)
    public void Refresh()
    {
        OnPropertyChanged(nameof(ShowExtra));
        OnPropertyChanged(nameof(KeptText));
        Fill();
    }

    // shows one flag (the event editor calls this)
    public void ShowFlag(Kind kind, int number)
    {
        _filling = true;
        KindIndex = (int)kind; Show = 0; Filter = "";
        _filling = false;
        Fill();
        Selected = Rows.FirstOrDefault(r => r.Number == number);
    }

    private string ItemName(RomBuffer rom, int item)
    {
        if (_itemsVersion != rom.Version)
        {
            try { _items = L2Battle.Items(rom).Select(x => x.Name.Trim()).ToArray(); } catch (Exception) { _items = Array.Empty<string>(); }
            _itemsVersion = rom.Version;
        }
        return item < _items.Length && _items[item].Length > 0 ? _items[item] : $"item {item:X3}";
    }

    private static string MapName(RomBuffer rom, int map) => $"{map:X2} {L2ScriptEditor.MapNameOf(rom, map)}";

    public static string NumberOf(Kind kind, int number) => kind switch
    {
        Kind.Story => L2ExtraFlags.Number(number),
        Kind.Chest => number.ToString("X3"),
        Kind.Door => $"{number >> 8:X2}:{number & 0xFF}",
        _ => number.ToString("X2"),
    };

    private IEnumerable<int> Numbers(RomBuffer rom, Kind kind, ILookup<int, L2FlagIndex.Use> uses) => kind switch
    {
        Kind.Story => Enumerable.Range(0, L2ExtraFlags.IsApplied(rom) ? L2ExtraFlags.Last + 1 : 0x100),
        Kind.Counter => Enumerable.Range(0, 0x20),
        Kind.Chest => Enumerable.Range(0, L2Chests.FlagCount),
        Kind.Switch => Enumerable.Range(0, 0x20).Concat(uses.Select(g => g.Key)).Concat(L2FlagNotes.Named(Kind.Switch)).Distinct().OrderBy(x => x),
        _ => uses.Select(g => g.Key).Concat(L2FlagNotes.Named(Kind.Door)).Distinct().OrderBy(x => x),
    };

    // what the game itself does with it, or null
    private string? GameUse(RomBuffer rom, Kind kind, int number) => kind switch
    {
        Kind.Story => L2FlagIndex.Reserved(number),
        Kind.Counter => L2FlagIndex.CounterReserved(rom, number),
        Kind.Switch when number >= 0x20 => "this map only (cleared when a map loads)",
        _ => null,
    };

    private void Fill()
    {
        if (_filling) return;
        var keepNumber = Selected?.Number;
        var keepKind = Selected?.Kind;
        Rows.Clear();
        var rom = Rom;
        if (rom == null) { CountText = ""; return; }
        var kind = Current;
        var uses = L2FlagIndex.All(rom).Where(u => u.Kind == kind).ToLookup(u => u.Number);
        string search = Filter.Trim();
        int free = 0, named = 0;
        foreach (int n in Numbers(rom, kind, uses))
        {
            var flagUses = uses[n].ToList();
            string? name = L2FlagNotes.Name(kind, n);
            string? gameLabel = kind switch { Kind.Story => L2StoryFlags.GameLabel(n), Kind.Counter => L2StoryFlags.GameCounterLabel(n), _ => null };
            string? gameUse = GameUse(rom, kind, n);
            bool isFree = flagUses.Count == 0 && name == null && gameUse == null && !(kind == Kind.Story && n == 0) && !(kind == Kind.Counter && n is >= 1 and <= 0x0E);
            if (isFree) free++;
            if (name != null) named++;
            string detail = kind switch
            {
                Kind.Chest => flagUses.Count == 0 ? "free" : string.Join("; ", flagUses.Select(x => $"{MapName(rom, x.Map)}, {ItemName(rom, ItemOf(x.Where))}")) + (flagUses.Count > 1 ? $"  (shared by {flagUses.Count} chests)" : ""),
                Kind.Door => (flagUses.FirstOrDefault(x => x.Head == L2FlagIndex.DoorHead) is { } door ? MapName(rom, door.Map) : MapName(rom, n >> 8)) +
                             (flagUses.Count(x => x.Head != L2FlagIndex.DoorHead) is var useCount and > 0 ? $", used {useCount}x" : ", not used by events"),
                _ => flagUses.Count == 0 ? (gameUse ?? (isFree ? "free" : "not used by events")) :
                     $"{Count(flagUses, true)} {(kind == Kind.Counter ? "change" : "turn on/off")}, {Count(flagUses, false)} check" + (gameUse != null ? $"  (game: {gameUse})" : ""),
            };
            var row = new L2FlagRow
            {
                Kind = kind, Number = n, NumberText = NumberOf(kind, n), Free = isFree, Named = name != null,
                Label = name ?? gameLabel ?? (kind == Kind.Story && n >= L2ExtraFlags.First ? "extra flag" : kind == Kind.Door ? DoorPlace(flagUses) : ""), Detail = detail,
            };
            if (Show == 1 && flagUses.Count == 0 || Show == 2 && !isFree || Show == 3 && name == null) continue;
            if (search.Length > 0 && !row.NumberText.Equals(search, StringComparison.OrdinalIgnoreCase) && !row.Label.Contains(search, StringComparison.OrdinalIgnoreCase) &&
                !row.Detail.Contains(search, StringComparison.OrdinalIgnoreCase) && !(L2FlagNotes.Note(kind, n)?.Contains(search, StringComparison.OrdinalIgnoreCase) ?? false)) continue;
            Rows.Add(row);
        }
        CountText = kind switch
        {
            Kind.Story => $"{free} free story flag{(free == 1 ? "" : "s")}" + (L2ExtraFlags.IsApplied(rom) ? " (with the extra flags)" : "") + $", {named} named by you",
            Kind.Door => $"{Rows.Count} doors listed, {named} named by you",
            _ => $"{free} free, {named} named by you",
        };
        Selected = keepNumber != null && keepKind == kind ? Rows.FirstOrDefault(r => r.Number == keepNumber) : null;
        if (Selected == null) ShowSelected(null);
    }

    // "Door 3 at (12, 5)" from the door's record
    private static string DoorPlace(List<L2FlagIndex.Use> flagUses) => flagUses.FirstOrDefault(x => x.Head == L2FlagIndex.DoorHead)?.Where ?? "";

    private static int Count(List<L2FlagIndex.Use> flagUses, bool writes) => flagUses.Count(x => x.Writes == writes && x.Head != L2FlagIndex.DoorHead);

    private static int ItemOf(string where)
    {
        int itemAt = where.LastIndexOf("item ", StringComparison.Ordinal);
        return itemAt >= 0 && int.TryParse(where[(itemAt + 5)..], System.Globalization.NumberStyles.HexNumber, null, out int item) ? item : 0;
    }

    partial void OnSelectedChanged(L2FlagRow? value) => ShowSelected(value);

    private void ShowSelected(L2FlagRow? row)
    {
        Uses.Clear();
        _filling = true;
        NameText = row != null ? L2FlagNotes.Name(row.Kind, row.Number) ?? "" : "";
        NoteText = row != null ? L2FlagNotes.Note(row.Kind, row.Number) ?? "" : "";
        _filling = false;
        var rom = Rom;
        if (row == null || rom == null) { Title = ""; About = ""; return; }
        string kindName = row.Kind switch { Kind.Story => row.Number >= L2ExtraFlags.First ? "Extra story flag" : "Story flag", Kind.Counter => "Event counter", Kind.Chest => "Chest flag", Kind.Switch => "Puzzle switch", _ => "Door" };
        Title = $"{kindName} {row.NumberText}";
        var about = new List<string>();
        if (row.Kind == Kind.Story)
        {
            var (address, mask) = L2ExtraFlags.Ram(row.Number);
            if (L2StoryFlags.GameLabel(row.Number) is { } gameLabel) about.Add($"In the game: {gameLabel}." + (L2StoryFlags.Note(row.Number) is { } gameNote ? " " + gameNote : ""));
            if (L2FlagIndex.Reserved(row.Number) is { } why) about.Add($"The game's own code uses it: {why}. Don't use it for your own story.");
            about.Add($"RAM $7E:{address:X4}, bit {mask:X2}" + (row.Number >= L2ExtraFlags.First ? $" (counter {0x10 + (row.Number - L2ExtraFlags.First) / 8:X2}'s byte)." : "."));
        }
        else if (row.Kind == Kind.Counter)
        {
            if (L2StoryFlags.GameCounterLabel(row.Number) is { } gameLabel) about.Add($"In the game: {gameLabel}." + (L2StoryFlags.CounterNote(row.Number) is { } gameNote ? " " + gameNote : ""));
            if (L2FlagIndex.CounterReserved(rom, row.Number) is { } why) about.Add($"It {why}: don't use it as a counter.");
            about.Add($"RAM $7E:{0x079E + row.Number:X4}.");
        }
        else if (row.Kind == Kind.Chest) about.Add($"RAM $7E:{0x093B + (row.Number >> 3):X4}, bit {1 << (row.Number & 7):X2}. Chests are changed on the Maps page.");
        else if (row.Kind == Kind.Switch) about.Add(row.Number < 0x20 ? "Kept in the save, shared by every map." : "Cleared whenever a map loads: the map's own.");
        if (row.Free) about.Add("Free: nothing uses it.");
        if (row.Kind == Kind.Door && L2FlagIndex.Of(rom, row.Kind, row.Number).FirstOrDefault(u => u.Head == L2FlagIndex.DoorHead) is { } doorRecord)
            about.Insert(0, $"{doorRecord.Where} on {MapName(rom, doorRecord.Map)}.");
        About = string.Join("\n", about);
        foreach (var use in L2FlagIndex.Of(rom, row.Kind, row.Number).Where(u => u.Head != L2FlagIndex.DoorHead || row.Kind != Kind.Door)
                     .OrderBy(u => u.Writes ? 0 : 1).ThenBy(u => u.Map).ThenBy(u => u.At))
            Uses.Add(new L2FlagUseRow($"{use.How,-11} {MapName(rom, use.Map)} · " +
                                      (use.Kind == Kind.Chest ? use.Where[..use.Where.LastIndexOf(", item ", StringComparison.Ordinal)] + ", " + ItemName(rom, ItemOf(use.Where)) : use.Where), use));
        if (Uses.Count == 0 && row.Kind != Kind.Door) Uses.Add(new L2FlagUseRow("(nothing uses it)", new L2FlagIndex.Use(row.Kind, row.Number, "", -1, L2FlagIndex.DoorHead, -1, -1, "")));
    }

    partial void OnNameTextChanged(string value) => SaveNote();
    partial void OnNoteTextChanged(string value) => SaveNote();

    private void SaveNote()
    {
        if (_filling || Selected is not { } row) return;
        _filling = true;
        try
        {
            L2FlagNotes.Set(row.Kind, row.Number, NameText, NoteText);
            row.Named = NameText.Trim().Length > 0;
            row.Label = L2FlagNotes.Name(row.Kind, row.Number) ?? (row.Kind switch { Kind.Story => L2StoryFlags.GameLabel(row.Number), Kind.Counter => L2StoryFlags.GameCounterLabel(row.Number), _ => null }) ?? "";
        }
        finally { _filling = false; }
        OnPropertyChanged(nameof(KeptText));
    }

    // takes a free flag of the kind shown and gives it a name you can change
    [RelayCommand]
    private void NewFlag()
    {
        var rom = Rom;
        if (rom == null) return;
        var kind = Current;
        int number = -1;
        string added = "";
        switch (kind)
        {
            case Kind.Story:
                number = L2FlagIndex.FreeStory(rom, L2ExtraFlags.IsApplied(rom));
                if (number < 0 && !L2ExtraFlags.IsApplied(rom) && L2ExtraFlags.CanApply(rom))
                {
                    if (System.Windows.MessageBox.Show("Every free story flag is taken. Add 128 extra story flags (100-17F) to the game? They are kept in counters 10-1F, which the game never uses.",
                            "New story flag", System.Windows.MessageBoxButton.OKCancel) != System.Windows.MessageBoxResult.OK) return;
                    if (!AddExtra()) return;
                    number = L2FlagIndex.FreeStory(rom, true);
                    added = " (the extra flags were added to the game for it)";
                }
                break;
            case Kind.Counter:
                for (int c = 0x1F; c >= 0 && number < 0; c--)
                    if (c is not (>= 1 and <= 0x0E) && L2FlagIndex.CounterReserved(rom, c) == null && L2FlagNotes.Name(kind, c) == null && !L2FlagIndex.Of(rom, kind, c).Any()) number = c;
                break;
            case Kind.Switch:
                for (int c = 0; c < 0x20 && number < 0; c++)
                    if (L2FlagNotes.Name(kind, c) == null && !L2FlagIndex.Of(rom, kind, c).Any()) number = c;
                break;
        }
        if (number < 0) { Status = "None is free."; return; }
        L2FlagNotes.Set(kind, number, $"My {(kind == Kind.Story ? "flag" : kind == Kind.Counter ? "counter" : "switch")} {NumberOf(kind, number)}", "");
        _filling = true; Show = 0; Filter = ""; _filling = false;
        Fill();
        Selected = Rows.FirstOrDefault(r => r.Number == number);
        Status = $"{NumberOf(kind, number)} is yours{added}: give it a name and a note on the right, then use it in events.";
    }

    // adds the extra story flags patch
    [RelayCommand]
    private void AddExtraFlags()
    {
        if (Rom == null) return;
        if (AddExtra()) { Fill(); Status = "Story flags 100-17F were added: pick them in any event's story flag fields."; }
    }

    private bool AddExtra()
    {
        var rom = Rom;
        if (rom == null || _page.MainVm is not { } main) return false;
        if (L2ExtraFlags.IsApplied(rom)) return true;
        var counters = Enumerable.Range(L2ExtraFlags.FirstCounter, 0x10).Where(c => L2FlagIndex.Of(rom, Kind.Counter, c).Any()).ToList();
        if (counters.Count > 0 && System.Windows.MessageBox.Show(
                $"Events use counter{(counters.Count == 1 ? "" : "s")} {string.Join(", ", counters.Select(c => c.ToString("X2")))}, which will hold the extra flags. " +
                "Those events would turn extra flags on and off by themselves. Add the extra flags anyway? (Better: move those counters to 0F or below first.)",
                "Extra story flags", System.Windows.MessageBoxButton.OKCancel, System.Windows.MessageBoxImage.Warning) != System.Windows.MessageBoxResult.OK) return false;
        try
        {
            L2ExtraFlags.Apply(rom);
            main.NotifyRomModified("Lufia II: 128 extra story flags (100-17F, in counters 10-1F)");
            OnPropertyChanged(nameof(ShowExtra));
            _page.FlagsChanged();
            return true;
        }
        catch (Exception ex) { Status = "Not added: " + ex.Message; return false; }
    }

    // opens the event that uses the flag, at that step
    [RelayCommand]
    private void OpenUse(L2FlagUseRow? row)
    {
        row ??= SelectedUse;
        if (row == null || Rom == null) return;
        var use = row.Use;
        if (!row.CanOpen)
        {
            Status = use.Head switch
            {
                L2FlagIndex.PuzzleHead => $"A puzzle script of map {use.Map:X2}: the Maps page shows the map's puzzles.",
                L2FlagIndex.ChestHead => $"A treasure of map {use.Map:X2}: the Maps page changes it.",
                _ => "",
            };
            return;
        }
        _page.ShowEventAt(use.Map, use.Head, use.Id, use.At);
    }
}
