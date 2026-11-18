using System.Collections.ObjectModel;
using System.Windows.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LufiaForge.Core;
using LufiaForge.Core.Lufia2;
using Kind = LufiaForge.Core.Lufia2.L2FlagIndex.Kind;

namespace LufiaForge.Modules.Lufia2;

/// <summary>A story character in the Story characters tab's list.</summary>
public sealed partial class L2StoryRow : ObservableObject
{
    public int Id { get; init; }
    public string NumberText => Id.ToString("X2");
    [ObservableProperty] private string _label = "";
    [ObservableProperty] private string _detail = "";
    [ObservableProperty] private BitmapSource? _picture;
    /// <summary>Not in the game and nothing uses it: free to take.</summary>
    public bool Free { get; init; }
    public double Dim => Free ? 0.6 : 1.0;
}

/// <summary>A place a story character is used, in the Story characters tab.</summary>
public sealed record L2StoryUseRow(string Text, L2Persistent.Use? Use);

/// <summary>
/// The Story characters tab of the Events page: Lufia II's story characters (10-4E, <see cref="L2Persistent"/>), the
/// ones the game remembers the place of, such as Tia. Each one's name and note (kept in the project), its sprite,
/// where a new game puts it, and every event that sends it, talks to it or uses it; "New story character" takes a
/// free one.
/// </summary>
public sealed partial class L2StoryCharsPanel : ObservableObject
{
    private readonly L2EventsPage _page;
    private bool _filling;

    public L2StoryCharsPanel(L2EventsPage page) => _page = page;

    public static IReadOnlyList<L2Option> ShowChoices { get; } = new List<L2Option>
    {
        new(0, "In the game"), new(1, "All 10-4E"), new(2, "Free"), new(3, "Named by you"),
    };

    [ObservableProperty] private int _show;
    [ObservableProperty] private string _filter = "";
    public ObservableCollection<L2StoryRow> Rows { get; } = new();
    [ObservableProperty] private L2StoryRow? _selected;
    public ObservableCollection<L2StoryUseRow> Uses { get; } = new();
    [ObservableProperty] private L2StoryUseRow? _selectedUse;
    [ObservableProperty] private string _title = "";
    [ObservableProperty] private string _nameText = "";
    [ObservableProperty] private string _noteText = "";
    [ObservableProperty] private string _about = "";
    [ObservableProperty] private string _placeText = "";
    [ObservableProperty] private BitmapSource? _picture;
    [ObservableProperty] private string _countText = "";
    [ObservableProperty] private string _status = "";
    [ObservableProperty] private bool _inGame;
    [ObservableProperty] private bool _canRemove;

    private RomBuffer? Rom => _page.CurrentRom;
    private L2ScriptEditor Editor => _page.Editor;

    public string Help =>
        "Story characters are the people the game remembers the place of, such as Tia: a new game puts each one on its map, and from then on only an event moves it " +
        "(\"Send a story character to another map\"), to another map or away (as the game does with Tia when she joins). They aren't made by a map's start-up script, " +
        "so When… doesn't apply to them; to have someone else stand in their place, send them away and make a map character there that the start-up script makes " +
        "only while a story flag is on. A story character shows up on its map from the next time that map loads.";

    public string KeptText => L2FlagNotes.Kept ? "Names and notes are kept in the project." : "Names and notes last until Lufia Forge closes: open or start a project to keep them.";

    partial void OnShowChanged(int value) => Fill();
    partial void OnFilterChanged(string value) => Fill();

    /// <summary>Reads the list again (the ROM changed, or the page is shown).</summary>
    public void Refresh()
    {
        OnPropertyChanged(nameof(KeptText));
        Fill();
    }

    /// <summary>Shows one story character (from the event editor).</summary>
    public void ShowChar(int id)
    {
        _filling = true;
        Show = Rom != null && L2Persistent.Exists(Rom, id) ? 0 : 1; Filter = "";
        _filling = false;
        Fill();
        Selected = Rows.FirstOrDefault(r => r.Id == id);
    }

    private static string MapName(RomBuffer rom, int m) => $"{m:X2} {L2ScriptEditor.MapNameOf(rom, m)}";

    /// <summary>"Elcid (map 04), start 8 (12, 5)", or "away".</summary>
    private static string PlaceOf(RomBuffer rom, int map, int spot)
    {
        if (L2Persistent.IsAway(map, spot)) return "away (on no map the party sees)";
        if (map >= L2Maps.Count(rom)) return $"map {map:X2} (there is no such map), start {spot}";
        var at = L2MapInfo.Records(rom, map, L2MapInfo.Characters, 8).Select(r => r.Bytes).FirstOrDefault(b => b[0] == spot);
        return $"{MapName(rom, map)}, start {spot}" + (at != null ? $" ({at[1]}, {at[2]})" : " (the map has no such start: it stands in the top-left corner)");
    }

    private string LabelOf(int id) => L2Speakers.StoryName(id) ?? "";

    private void Fill()
    {
        if (_filling) return;
        var keep = Selected?.Id;
        Rows.Clear();
        var rom = Rom;
        if (rom == null) { CountText = ""; ShowSelected(null); return; }
        var uses = L2Persistent.Uses(rom).ToLookup(u => u.Id);
        string q = Filter.Trim();
        int inGame = 0, free = 0;
        for (int id = L2Persistent.First; id <= L2Persistent.LastUsable; id++)
        {
            bool has = L2Persistent.Exists(rom, id);
            string? name = L2FlagNotes.Name(Kind.Character, id);
            bool isFree = !has && !uses[id].Any() && name == null;
            if (has) inGame++;
            if (isFree) free++;
            if (Show == 0 && !has || Show == 2 && !isFree || Show == 3 && name == null) continue;
            var rec = has ? L2Persistent.Record(rom, id) : null;
            int sends = uses[id].Count(u => u.How == "sends");
            string detail = rec == null ? (isFree ? "free" : "not in the game: never shows up") :
                $"new game: {PlaceOf(rom, rec[2], rec[3])}" + (sends > 0 ? $"; {sends} send{(sends == 1 ? "" : "s")}" : "");
            var row = new L2StoryRow
            {
                Id = id, Free = isFree,
                Label = LabelOf(id) is { Length: > 0 } l ? l : rec != null ? $"sprite {L2Sprites.Name(rec[1])}" : "",
                Detail = detail,
                Picture = rec != null ? Editor.SpritePicture(rec[1]) : null,
            };
            if (q.Length > 0 && !row.NumberText.Equals(q, StringComparison.OrdinalIgnoreCase) && !row.Label.Contains(q, StringComparison.OrdinalIgnoreCase) &&
                !row.Detail.Contains(q, StringComparison.OrdinalIgnoreCase) && !(L2FlagNotes.Note(Kind.Character, id)?.Contains(q, StringComparison.OrdinalIgnoreCase) ?? false)) continue;
            Rows.Add(row);
        }
        CountText = $"{inGame} in the game, {free} free (10-{L2Persistent.LastUsable:X2}; 4F is the game's own)";
        Selected = keep != null ? Rows.FirstOrDefault(r => r.Id == keep) : null;
        if (Selected == null) ShowSelected(null);
    }

    partial void OnSelectedChanged(L2StoryRow? value) => ShowSelected(value);

    private void ShowSelected(L2StoryRow? r)
    {
        Uses.Clear();
        _filling = true;
        NameText = r != null ? L2FlagNotes.Name(Kind.Character, r.Id) ?? "" : "";
        NoteText = r != null ? L2FlagNotes.Note(Kind.Character, r.Id) ?? "" : "";
        _filling = false;
        var rom = Rom;
        if (r == null || rom == null) { Title = ""; About = ""; PlaceText = ""; Picture = null; InGame = false; CanRemove = false; return; }
        bool has = L2Persistent.Exists(rom, r.Id);
        var rec = has ? L2Persistent.Record(rom, r.Id) : null;
        var uses = L2Persistent.Uses(rom).Where(u => u.Id == r.Id).ToList();
        Title = $"Story character {r.Id:X2}" + (LabelOf(r.Id) is { Length: > 0 } l ? $": {l}" : "");
        Picture = rec != null ? Editor.SpritePicture(rec[1]) : null;
        InGame = has;
        CanRemove = has && uses.Count == 0;
        PlaceText = rec != null ? $"Sprite {L2Sprites.Name(rec[1])}. A new game puts it on {PlaceOf(rom, rec[2], rec[3])}." :
            "Not in the game: it never shows up, even when an event sends it. \"Pick its new-game place…\" puts it in the game, with a sprite and a place.";
        var about = new List<string>();
        if (L2Speakers.StoryName(r.Id) is { } gn && L2FlagNotes.Name(Kind.Character, r.Id) == null) about.Add($"In the game: {gn}.");
        about.Add($"RAM: map $7E:{0x085E + r.Id - 0x10:X4}, start $7E:{0x089E + r.Id - 0x10:X4}, sprite $7E:{0x08DE + r.Id - 0x10:X4} (kept in the save).");
        if (has && uses.Count == 0) about.Add("No event uses it yet: it stays on its new-game place for the whole game.");
        About = string.Join("\n", about);
        foreach (var u in uses.Where(u => u.How != "uses").OrderBy(u => u.How == "sends" ? 0 : 1).ThenBy(u => u.Map).ThenBy(u => u.At))
            Uses.Add(new L2StoryUseRow($"{MapName(rom, u.Map)} · {u.Where} · " + (u.How == "sends" ? "sends to " + PlaceOf(rom, u.ToMap, u.ToSpot) : "talk event"), u));
        // other steps (walks, turns, lines it says): one row per event, opened at its first step
        foreach (var g in uses.Where(u => u.How == "uses").GroupBy(u => (u.Map, u.Head, u.ListId)).OrderBy(g => g.Key.Map))
        {
            var first = g.OrderBy(u => u.At).First();
            int n = g.Count();
            Uses.Add(new L2StoryUseRow($"{MapName(rom, first.Map)} · {first.Where} · {n} step{(n == 1 ? "" : "s")} move or show it", first));
        }
        if (Uses.Count == 0) Uses.Add(new L2StoryUseRow("(no event uses it)", null));
    }

    partial void OnNameTextChanged(string value) => SaveNote();
    partial void OnNoteTextChanged(string value) => SaveNote();

    private void SaveNote()
    {
        if (_filling || Selected is not { } r) return;
        _filling = true;
        try
        {
            L2FlagNotes.Set(Kind.Character, r.Id, NameText, NoteText);
            var rom = Rom;
            r.Label = LabelOf(r.Id) is { Length: > 0 } l ? l : rom != null && L2Persistent.Exists(rom, r.Id) ? $"sprite {L2Sprites.Name(L2Persistent.Record(rom, r.Id)[1])}" : "";
            Title = $"Story character {r.Id:X2}" + (LabelOf(r.Id) is { Length: > 0 } t ? $": {t}" : "");
        }
        finally { _filling = false; }
        OnPropertyChanged(nameof(KeptText));
    }

    private bool Write(string what, Action<RomBuffer> edit)
    {
        var rom = Rom;
        if (rom == null || _page.MainVm is not { } main) return false;
        if (!_page.AskBeforeLeaving()) return false;   // the editor reads the map again after: its unsaved steps go first
        try
        {
            L2Rom.AllOrNothing(rom, () => { edit(rom); return 0; });
            main.NotifyRomModified("Lufia II: " + what);
            _page.FlagsChanged();
            return true;
        }
        catch (Exception ex) { Status = "Not changed: " + ex.Message; return false; }
    }

    /// <summary>Takes a free story character, with a sprite picked in a window; it starts away until it gets a place.</summary>
    [RelayCommand]
    private void NewChar()
    {
        var rom = Rom;
        if (rom == null) return;
        if (Editor.Rom == null) { Status = "Pick a map on the Events tab first."; return; }
        if (L2Persistent.FreeId(rom) < 0) { Status = $"All the story characters (10-{L2Persistent.LastUsable:X2}) are taken."; return; }
        int sprite = L2EventEditorPanel.PickSprite(Editor, System.Windows.Application.Current?.MainWindow, "New story character", "Use this sprite",
            "Pick the new story character's sprite (one of the game's: a story character goes from map to map).", gameOnly: true);
        if (sprite < 0) return;
        int id = -1;
        if (!Write("new story character", r => id = L2Persistent.New(r, sprite))) return;
        L2FlagNotes.Set(Kind.Character, id, $"My character {id:X2}", "");
        _filling = true; Show = 0; Filter = ""; _filling = false;
        Fill();
        Selected = Rows.FirstOrDefault(r => r.Id == id);
        Status = $"Story character {id:X2} is yours. It starts away: \"Pick its new-game place…\" puts it on a map from a new game, or send it with an event " +
                 "(the event editor's Characters section: \"＋ Story character here…\").";
    }

    /// <summary>A sprite for the picked one (the same on every map).</summary>
    [RelayCommand]
    private void ChangeSprite()
    {
        var rom = Rom;
        if (rom == null || Selected is not { } r || !L2Persistent.Exists(rom, r.Id)) return;
        if (Editor.Rom == null) { Status = "Pick a map on the Events tab first."; return; }
        int sprite = L2EventEditorPanel.PickSprite(Editor, System.Windows.Application.Current?.MainWindow, $"Story character {r.Id:X2}: sprite", "Use this sprite",
            "Pick its sprite: it has it on every map, from a new game on (an event can still change it for one scene).", gameOnly: true);
        if (sprite < 0) return;
        var rec = L2Persistent.Record(rom, r.Id);
        if (Write($"story character {r.Id:X2} sprite", x => L2Persistent.Set(x, r.Id, sprite, rec[2], rec[3]))) { Fill(); Status = "Sprite changed (from a new game on)."; }
    }

    /// <summary>Where a new game puts the picked one: a tile picked on a map (a character start there is used or made).</summary>
    public void SetPlace(int map, int x, int y)
    {
        var rom = Rom;
        if (rom == null || Selected is not { } r) return;
        bool has = L2Persistent.Exists(rom, r.Id);
        int sprite = has ? L2Persistent.Record(rom, r.Id)[1] : -1;
        if (sprite < 0)
        {
            if (Editor.Rom == null) { Status = "Pick a map on the Events tab first."; return; }
            sprite = L2EventEditorPanel.PickSprite(Editor, System.Windows.Application.Current?.MainWindow, $"Story character {r.Id:X2}: sprite", "Use this sprite",
                "Pick its sprite (one of the game's).", gameOnly: true);
            if (sprite < 0) return;
        }
        int spot = -1;
        if (!Write($"story character {r.Id:X2} new-game place", x2 => { spot = L2Persistent.StartAt(x2, map, x, y); L2Persistent.Set(x2, r.Id, sprite, map, spot); })) return;
        Fill();
        Status = $"A new game puts it on {MapName(rom, map)} at ({x}, {y}) (start {spot}).";
    }

    /// <summary>The place the picked one has from a new game, for the map window: (map, x, y).</summary>
    public (int Map, int X, int Y) PlaceForWindow()
    {
        var rom = Rom;
        int map = Math.Max(2, _page.CurrentMap);
        if (rom == null || Selected is not { } r || !L2Persistent.Exists(rom, r.Id)) return (map, 0, 0);
        var rec = L2Persistent.Record(rom, r.Id);
        if (L2Persistent.IsAway(rec[2], rec[3]) || rec[2] >= L2Maps.Count(rom)) return (map, 0, 0);
        var at = L2MapInfo.Records(rom, rec[2], L2MapInfo.Characters, 8).Select(b => b.Bytes).FirstOrDefault(b => b[0] == rec[3]);
        return (rec[2], at?[1] ?? 0, at?[2] ?? 0);
    }

    /// <summary>A new game puts the picked one away: it shows up once an event sends it somewhere.</summary>
    [RelayCommand]
    private void StartAway()
    {
        var rom = Rom;
        if (rom == null || Selected is not { } r || !L2Persistent.Exists(rom, r.Id)) return;
        int sprite = L2Persistent.Record(rom, r.Id)[1];
        if (Write($"story character {r.Id:X2} away from a new game", x => L2Persistent.Set(x, r.Id, sprite, L2Persistent.AwayMap, L2Persistent.AwaySpot)))
        {
            Fill();
            Status = "A new game puts it away: it shows up once an event sends it to a map.";
        }
    }

    /// <summary>Takes the picked one out of the game (only when no event uses it).</summary>
    [RelayCommand]
    private void RemoveChar()
    {
        var rom = Rom;
        if (rom == null || Selected is not { } r || !L2Persistent.Exists(rom, r.Id)) return;
        if (L2Persistent.Uses(rom).Any(u => u.Id == r.Id)) { Status = "Events use it: take those steps out first (the list shows them)."; return; }
        if (System.Windows.MessageBox.Show($"Take story character {r.Id:X2} out of the game? It no longer shows up anywhere, and its number is free again.",
                "Story characters", System.Windows.MessageBoxButton.OKCancel) != System.Windows.MessageBoxResult.OK) return;
        if (Write($"story character {r.Id:X2} taken out", x => L2Persistent.Remove(x, r.Id)))
        {
            L2FlagNotes.Set(Kind.Character, r.Id, null, null);
            Fill();
            Status = $"{r.Id:X2} is out of the game.";
        }
    }

    /// <summary>Opens the event that uses it, at the step.</summary>
    [RelayCommand]
    private void OpenUse(L2StoryUseRow? row)
    {
        row ??= SelectedUse;
        if (row?.Use is not { } u || Rom == null) return;
        _page.ShowEventAt(u.Map, u.Head, u.ListId, u.At);
    }
}
