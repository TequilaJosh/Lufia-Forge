using System.Collections.ObjectModel;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using LufiaForge.Core;
using LufiaForge.Core.Lufia2;
using LufiaForge.ViewModels;

namespace LufiaForge.Modules.Lufia2;

// a cutscene (an event of some map) in the Cutscenes page's list
public sealed class L2CutsceneItem
{
    public int Map { get; init; }
    public L2EventBlocks.Entry Entry { get; init; } = null!;
    // the map and what starts it
    public string Label { get; init; } = "";
    public string Title { get; init; } = "";
    // what it does: moves, lines, flags
    public string Hint { get; init; } = "";
    // its first line
    public string Details { get; init; } = "";
    public int Moves { get; init; }
    public ImageSource? Picture { get; init; }
    public override string ToString() => Label;
}

// the Lufia II Cutscenes page, laid out like the Lufia 1 one. every event of every map that moves characters, the
// camera or the screen (and the rest further down) goes in one list, with a search, a map filter and "only bigger
// scenes". the picked one opens in the event editor with the stage always on: the map is the stage, the steps are the
// timeline, and clicking the stage adds a walk, an appearance or a turn. New cutscene makes one on any map, Delete
// takes one off its map
public partial class L2CutscenesPage : ObservableObject
{
    private RomBuffer? _rom;
    private MainViewModel? _main;
    private List<L2CutsceneItem> _all = new();
    private readonly Dictionary<int, BitmapSource?> _sprites = new();
    private bool _filling;

    public L2ScriptEditor Editor { get; } = new();
    public ObservableCollection<L2CutsceneItem> Cutscenes { get; } = new();
    [ObservableProperty] private L2CutsceneItem? _selected;
    [ObservableProperty] private string _search = "";
    public ObservableCollection<string> MapFilters { get; } = new();
    [ObservableProperty] private int _mapFilterIndex;
    private readonly List<int> _filterMaps = new();
    [ObservableProperty] private bool _onlyBig;
    [ObservableProperty] private string _status = "";
    [ObservableProperty] private string _lockNote = "";
    [ObservableProperty] private bool _hasSelection;

    public RomBuffer? CurrentRom => _rom;
    public MainViewModel? CurrentMain => _main;

    public L2CutscenesPage()
    {
        Editor.Saved += _ => Reload();
        Editor.PropertyChanged += (_, e) => { if (e.PropertyName == nameof(L2ScriptEditor.Status)) Status = Editor.Status; };
    }

    public void SetRom(RomBuffer rom, MainViewModel main)
    {
        _rom = rom; _main = main;
        _sprites.Clear();
        Editor.Unload();
        LockNote = TableGuard.LockReason(rom, "Events") ?? "";
        Read();
    }

    // the name shown for a map: the project's own, otherwise the game's
    public static string MapName(RomBuffer rom, int map) => L2ScriptEditor.MapNameOf(rom, map);

    private BitmapSource? Sprite(int sprite)
    {
        if (_rom == null || sprite < 0) return null;
        if (_sprites.TryGetValue(sprite, out var cached)) return cached;
        BitmapSource? bitmap = null;
        try
        {
            if (L2Graphics.MapSprite(_rom, sprite) is var (width, height, pixels))
            {
                bitmap = BitmapSource.Create(width, height, 96, 96, PixelFormats.Bgra32, null, pixels.Select(c => (int)c).ToArray(), width * 4);
                bitmap.Freeze();
            }
        }
        catch (Exception) { }
        return _sprites[sprite] = bitmap;
    }

    // reads every map's events: what starts each one, its moves and lines
    private void Read()
    {
        var rom = _rom!;
        _readVersion = rom.Version;
        _all = new List<L2CutsceneItem>();
        for (int map = 0; map < L2Maps.Count(rom); map++)
        {
            int block;
            try { block = L2Events.Block(rom, map); } catch (Exception) { continue; }
            if (block < 0) continue;
            string mapName = MapName(rom, map);
            Dictionary<int, int> sprites;
            Dictionary<int, byte[]> starts, triggers;
            try
            {
                sprites = L2MapInfo.CharacterSprites(rom, map);
                starts = L2MapInfo.Records(rom, map, L2MapInfo.Characters, 8).ToDictionary(r => 0x4F + r.Bytes[0], r => r.Bytes);
                triggers = L2MapInfo.Records(rom, map, L2MapInfo.Triggers, 5).ToDictionary(r => (int)r.Bytes[0], r => r.Bytes);
            }
            catch (Exception) { continue; }
            var speakers = new L2Speakers(rom, map);
            List<L2EventBlocks.Entry> entries;
            try { entries = L2EventBlocks.ReadLists(rom, block).Where(e => e.IsScript && e.Head != L2EventBlocks.GameList).OrderBy(e => e.Head == L2EventBlocks.SceneList ? 0 : e.Head).ThenBy(e => e.Id).ToList(); }
            catch (Exception) { continue; }
            foreach (var entry in entries)
            {
                string title = entry.Head switch
                {
                    L2EventBlocks.SceneList => L2Cutscene.Starter(rom, map, entry.Id) is { } starter
                        ? $"Scene {entry.Id:X2}: when the party enters" + (starter.Flag >= 0 ? $" (story flag {starter.Flag:X2} {(starter.MustBeOn ? "on" : "off")})" : "")
                        : $"Scene {entry.Id:X2}: on arrival from another cutscene",
                    L2EventBlocks.TalkList => "Talking to " + speakers.Label(entry.Id, sprite: false) + (entry.Id >= 0x50 && starts.TryGetValue(entry.Id, out var character) ? $" at ({character[1]},{character[2]})" : ""),
                    L2EventBlocks.TriggerList => triggers.TryGetValue(entry.Id, out var trigger) ? $"Stepping onto ({trigger[1]},{trigger[2]})-({trigger[3] - 1},{trigger[4] - 1})" : $"Stepping onto area {entry.Id} (not on the map)",
                    _ => $"Event {entry.Id:X2}",
                };
                int moves = 0, textBoxes = 0;
                string firstLine = "", flags = "";
                try
                {
                    var script = L2EventScript.Read(rom, block + entry.Offset, block);
                    moves = script.Steps.Count(x => !x.IsText && x.Op is 0x2F or 0x30 or 0xA3 or 0x7A or 0x2D or 0x2E or 0x98 or 0x99 or 0x3A or 0xC4 or 0x41 or 0x58 or 0x59);
                    textBoxes = script.Steps.Count(x => x.IsText);
                    firstLine = script.Steps.FirstOrDefault(x => x.IsText)?.Text.Replace("\n", " ") ?? "";
                    var flagsSet = script.Steps.Where(x => !x.IsText && L2ExtraFlags.Of(x.Bytes).Use == L2ExtraFlags.Use.On).Select(x => L2ExtraFlags.FlagOf(x.Bytes)).Distinct().ToList();
                    if (flagsSet.Count > 0) flags = " · sets story flag " + string.Join(", ", flagsSet.Select(L2ExtraFlags.Number));
                    if (script.Steps.Any(x => !x.IsText && x.Op == 0x16)) flags += " · goes to another map";
                }
                catch (Exception) { }
                int sprite = entry.Head == L2EventBlocks.TalkList ? (entry.Id < 7 ? entry.Id : sprites.GetValueOrDefault(entry.Id, -1)) : 0;
                _all.Add(new L2CutsceneItem
                {
                    Map = map, Entry = entry, Title = title, Label = $"{map:X2} {mapName} · {title}", Moves = moves,
                    Hint = $"{moves} stage command{(moves == 1 ? "" : "s")}, {textBoxes} text box{(textBoxes == 1 ? "" : "es")}{flags}",
                    Details = firstLine.Length > 0 ? "\"" + (firstLine.Length > 60 ? firstLine[..60] + "..." : firstLine) + "\"" : "",
                    Picture = Sprite(sprite),
                });
            }
        }
        // the map filter stays on its map (the list was read again)
        int filterMap = MapFilterIndex > 0 && MapFilterIndex < _filterMaps.Count ? _filterMaps[MapFilterIndex] : -1;
        _filling = true;
        MapFilters.Clear(); _filterMaps.Clear();
        MapFilters.Add("All maps"); _filterMaps.Add(-1);
        foreach (int mapWithCutscenes in _all.Select(c => c.Map).Distinct()) { MapFilters.Add($"{mapWithCutscenes:X2}  {MapName(rom, mapWithCutscenes)}"); _filterMaps.Add(mapWithCutscenes); }
        MapFilterIndex = Math.Max(0, _filterMaps.IndexOf(filterMap));
        _filling = false;
        Filter();
    }

    private void Filter()
    {
        if (_filling) return;
        var kept = Selected;
        _filling = true;
        Cutscenes.Clear();
        string query = Search.Trim();
        int map = MapFilterIndex >= 0 && MapFilterIndex < _filterMaps.Count ? _filterMaps[MapFilterIndex] : -1;
        foreach (var cutscene in _all)
        {
            if (map >= 0 && cutscene.Map != map) continue;
            if (OnlyBig && cutscene.Moves < 6) continue;
            if (query.Length > 0 && !cutscene.Label.Contains(query, StringComparison.OrdinalIgnoreCase) && !cutscene.Hint.Contains(query, StringComparison.OrdinalIgnoreCase) && !cutscene.Details.Contains(query, StringComparison.OrdinalIgnoreCase)) continue;
            Cutscenes.Add(cutscene);
        }
        Selected = kept != null ? Cutscenes.FirstOrDefault(c => c.Map == kept.Map && c.Entry.Head == kept.Entry.Head && c.Entry.Id == kept.Entry.Id) : null;
        _filling = false;
        Status = $"{Cutscenes.Count} of {_all.Count} cutscenes shown.";
    }

    partial void OnSearchChanged(string value) => Filter();
    partial void OnMapFilterIndexChanged(int value) => Filter();
    partial void OnOnlyBigChanged(bool value) => Filter();

    partial void OnSelectedChanged(L2CutsceneItem? value)
    {
        HasSelection = value != null;
        if (_filling || value == null || _rom == null || _main == null) return;
        if (Editor.Event?.Entry is { } open && Editor.Map == value.Map && open.Head == value.Entry.Head && open.Id == value.Entry.Id) return;
        if (!Editor.ConfirmLeave())
        {
            // kept: the list shows the cutscene the editor still has (otherwise Delete would take out the other one)
            var shown = Cutscenes.FirstOrDefault(c => Editor.Event?.Entry is { } e && c.Map == Editor.Map && c.Entry.Head == e.Head && c.Entry.Id == e.Id);
            System.Windows.Application.Current?.Dispatcher.BeginInvoke(() => { _filling = true; Selected = shown; _filling = false; HasSelection = shown != null; });
            return;
        }
        if (!Editor.Load(_rom, _main, value.Map, value.Entry.Head, value.Entry.Id)) Status = "That cutscene isn't on the map any more.";
    }

    // shows one cutscene (from another page)
    public void ShowCutscene(int map, int head, int id)
    {
        var cutscene = _all.FirstOrDefault(x => x.Map == map && x.Entry.Head == head && x.Entry.Id == id);
        if (cutscene == null) { Reload(); cutscene = _all.FirstOrDefault(x => x.Map == map && x.Entry.Head == head && x.Entry.Id == id); }
        if (cutscene == null) return;
        if (!Cutscenes.Contains(cutscene)) { _filling = true; Search = ""; MapFilterIndex = 0; OnlyBig = false; _filling = false; Filter(); }
        Selected = cutscene;
    }

    // reads every map again (events were written, here or on another page). the open cutscene stays open
    public void Reload()
    {
        if (_rom == null) return;
        // the open cutscene too (the pop-up editor may have changed it or its map), unless it has unwritten steps
        Editor.Shown();
        Read();   // (the filter keeps the picked cutscene, by its map and entry, without opening it again)
    }

    // the ROM's version when the list was read
    private int _readVersion = -1;

    // the page is shown: if the ROM changed since the list was read (another page, Undo), the open cutscene
    // (unless it has unwritten steps) and the list get read again
    public void Shown()
    {
        if (_rom == null || _rom.Version == _readVersion) return;
        _sprites.Clear();
        Editor.Shown();
        Read();
    }

    // a cutscene was made (the New cutscene window): the list shows it and the editor opens it
    public void CutsceneCreated(int map, int head, int id, string what)
    {
        _main?.NotifyRomModified("Lufia II: " + what);
        L2FlagUses.Invalidate();
        Read();
        ShowCutscene(map, head, id);
        Status = what + " Add its steps (the first line is a placeholder): ＋ on a card, or click the stage.";
    }

    // what deleting the picked cutscene changes, or null when it can't be deleted
    public string? DeleteDescription()
    {
        if (_rom == null || Selected is not { } cutscene) return null;
        if (cutscene.Entry.Head == L2EventBlocks.SceneList && L2Cutscene.Starter(_rom, cutscene.Map, cutscene.Entry.Id) != null)
            return null;
        return cutscene.Entry.Head switch
        {
            L2EventBlocks.TalkList => $"Talking to {cutscene.Title[11..]} on {MapName(_rom, cutscene.Map)} will do nothing; the steps stay in the ROM, unused.",
            L2EventBlocks.TriggerList => $"{cutscene.Title} on {MapName(_rom, cutscene.Map)} will do nothing (the area stays on the Maps page); the steps stay in the ROM, unused.",
            _ => $"Scene {cutscene.Entry.Id:X2} of {MapName(_rom, cutscene.Map)} goes; a \"Go to another map\" step that sends the party to it would then start nothing. The steps stay in the ROM, unused.",
        };
    }

    // takes the picked cutscene off its map (after DeleteDescription was shown)
    public void Delete()
    {
        if (_rom == null || _main == null || Selected is not { } cutscene) return;
        Editor.Unload();
        L2EventBlocks.Remove(_rom, cutscene.Map, cutscene.Entry);
        _main.NotifyRomModified($"Lufia II: removed \"{cutscene.Title}\" from {MapName(_rom, cutscene.Map)}");
        L2FlagUses.Invalidate();
        _filling = true; Selected = null; _filling = false;
        Read();
        Status = "Removed.";
    }
}
