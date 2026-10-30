using System.Collections.ObjectModel;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using LufiaForge.Core;
using LufiaForge.Core.Lufia2;
using LufiaForge.ViewModels;

namespace LufiaForge.Modules.Lufia2;

/// <summary>A cutscene (an event of some map) in the Cutscenes page's list.</summary>
public sealed class L2CutsceneItem
{
    public int Map { get; init; }
    public L2EventBlocks.Entry Entry { get; init; } = null!;
    /// <summary>The map and what starts it.</summary>
    public string Label { get; init; } = "";
    public string Title { get; init; } = "";
    /// <summary>What it does: moves, lines, flags.</summary>
    public string Hint { get; init; } = "";
    /// <summary>Its first line.</summary>
    public string Details { get; init; } = "";
    public int Moves { get; init; }
    public ImageSource? Picture { get; init; }
    public override string ToString() => Label;
}

/// <summary>
/// The Lufia II Cutscenes page, laid out like Lufia 1's: every event of every map that moves characters, the camera or
/// the screen (and the rest, further down) in one list with a search, a map filter and "only bigger scenes"; the
/// picked one opens in the event editor with the stage always on, so the map is the stage, the steps are the timeline
/// and a click on the stage adds a walk, an appearance or a turn. New cutscene makes one on any map; Delete takes one
/// off its map.
/// </summary>
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

    /// <summary>The name shown for a map: the project's own, else the game's.</summary>
    public static string MapName(RomBuffer rom, int m) => L2ScriptEditor.MapNameOf(rom, m);

    private BitmapSource? Sprite(int sprite)
    {
        if (_rom == null || sprite < 0) return null;
        if (_sprites.TryGetValue(sprite, out var p)) return p;
        BitmapSource? bmp = null;
        try
        {
            if (L2Graphics.MapSprite(_rom, sprite) is var (w, h, px))
            {
                bmp = BitmapSource.Create(w, h, 96, 96, PixelFormats.Bgra32, null, px.Select(c => (int)c).ToArray(), w * 4);
                bmp.Freeze();
            }
        }
        catch (Exception) { }
        return _sprites[sprite] = bmp;
    }

    /// <summary>Reads every map's events: what starts each, its moves and lines.</summary>
    private void Read()
    {
        var rom = _rom!;
        _all = new List<L2CutsceneItem>();
        for (int map = 0; map < L2Maps.Count; map++)
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
            List<L2EventBlocks.Entry> entries;
            try { entries = L2EventBlocks.ReadLists(rom, block).Where(e => e.IsScript && e.Head != L2EventBlocks.GameList).OrderBy(e => e.Head == L2EventBlocks.SceneList ? 0 : e.Head).ThenBy(e => e.Id).ToList(); }
            catch (Exception) { continue; }
            foreach (var e in entries)
            {
                string title = e.Head switch
                {
                    L2EventBlocks.SceneList => L2Cutscene.Starter(rom, map, e.Id) is { } st
                        ? $"Scene {e.Id:X2}: when the party enters" + (st.Flag >= 0 ? $" (story flag {st.Flag:X2} {(st.MustBeOn ? "on" : "off")})" : "")
                        : $"Scene {e.Id:X2}: on arrival from another cutscene",
                    L2EventBlocks.TalkList => "Talking to " + (e.Id < 7 ? L2EventCommands.HeroNames[e.Id] : e.Id < 0x50 ? $"story character {e.Id:X2}" : $"character {e.Id - 0x4F}" + (starts.TryGetValue(e.Id, out var c) ? $" at ({c[1]},{c[2]})" : "")),
                    L2EventBlocks.TriggerList => triggers.TryGetValue(e.Id, out var t) ? $"Stepping onto ({t[1]},{t[2]})-({t[3] - 1},{t[4] - 1})" : $"Stepping onto area {e.Id} (not on the map)",
                    _ => $"Event {e.Id:X2}",
                };
                int moves = 0, says = 0;
                string first = "", flags = "";
                try
                {
                    var s = L2EventScript.Read(rom, block + e.Offset, block);
                    moves = s.Steps.Count(x => !x.IsText && x.Op is 0x2F or 0x30 or 0xA3 or 0x7A or 0x2D or 0x2E or 0x98 or 0x99 or 0x3A or 0xC4 or 0x41 or 0x58 or 0x59);
                    says = s.Steps.Count(x => x.IsText);
                    first = s.Steps.FirstOrDefault(x => x.IsText)?.Text.Replace("\n", " ") ?? "";
                    var set = s.Steps.Where(x => !x.IsText && x.Op == 0x1A && x.Bytes.Length > 1).Select(x => x.Bytes[1]).Distinct().ToList();
                    if (set.Count > 0) flags = " · sets story flag " + string.Join(", ", set.Select(f => f.ToString("X2")));
                    if (s.Steps.Any(x => !x.IsText && x.Op == 0x16)) flags += " · goes to another map";
                }
                catch (Exception) { }
                int sprite = e.Head == L2EventBlocks.TalkList ? (e.Id < 7 ? e.Id : sprites.GetValueOrDefault(e.Id, -1)) : 0;
                _all.Add(new L2CutsceneItem
                {
                    Map = map, Entry = e, Title = title, Label = $"{map:X2} {mapName} · {title}", Moves = moves,
                    Hint = $"{moves} stage command{(moves == 1 ? "" : "s")}, {says} text box{(says == 1 ? "" : "es")}{flags}",
                    Details = first.Length > 0 ? "\"" + (first.Length > 60 ? first[..60] + "..." : first) + "\"" : "",
                    Picture = Sprite(sprite),
                });
            }
        }
        // the map filter stays on its map (the list was read again)
        int filterMap = MapFilterIndex > 0 && MapFilterIndex < _filterMaps.Count ? _filterMaps[MapFilterIndex] : -1;
        _filling = true;
        MapFilters.Clear(); _filterMaps.Clear();
        MapFilters.Add("All maps"); _filterMaps.Add(-1);
        foreach (int m in _all.Select(c => c.Map).Distinct()) { MapFilters.Add($"{m:X2}  {MapName(rom, m)}"); _filterMaps.Add(m); }
        MapFilterIndex = Math.Max(0, _filterMaps.IndexOf(filterMap));
        _filling = false;
        Filter();
    }

    private void Filter()
    {
        if (_filling) return;
        var keep = Selected;
        _filling = true;
        Cutscenes.Clear();
        string q = Search.Trim();
        int map = MapFilterIndex >= 0 && MapFilterIndex < _filterMaps.Count ? _filterMaps[MapFilterIndex] : -1;
        foreach (var c in _all)
        {
            if (map >= 0 && c.Map != map) continue;
            if (OnlyBig && c.Moves < 6) continue;
            if (q.Length > 0 && !c.Label.Contains(q, StringComparison.OrdinalIgnoreCase) && !c.Hint.Contains(q, StringComparison.OrdinalIgnoreCase) && !c.Details.Contains(q, StringComparison.OrdinalIgnoreCase)) continue;
            Cutscenes.Add(c);
        }
        Selected = keep != null ? Cutscenes.FirstOrDefault(c => c.Map == keep.Map && c.Entry.Head == keep.Entry.Head && c.Entry.Id == keep.Entry.Id) : null;
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
        if (!Editor.ConfirmLeave()) return;
        if (!Editor.Load(_rom, _main, value.Map, value.Entry.Head, value.Entry.Id)) Status = "That cutscene isn't on the map any more.";
    }

    /// <summary>Shows one cutscene (from another page).</summary>
    public void ShowCutscene(int map, int head, int id)
    {
        var c = _all.FirstOrDefault(x => x.Map == map && x.Entry.Head == head && x.Entry.Id == id);
        if (c == null) { Reload(); c = _all.FirstOrDefault(x => x.Map == map && x.Entry.Head == head && x.Entry.Id == id); }
        if (c == null) return;
        if (!Cutscenes.Contains(c)) { _filling = true; Search = ""; MapFilterIndex = 0; OnlyBig = false; _filling = false; Filter(); }
        Selected = c;
    }

    /// <summary>Reads every map again (events were written, here or on another page); the cutscene open stays open.</summary>
    public void Reload()
    {
        if (_rom == null) return;
        Read();   // (the filter keeps the picked cutscene, by its map and entry, without opening it again)
    }

    /// <summary>A cutscene was made (the New cutscene window): the list shows it, the editor opens it.</summary>
    public void CutsceneCreated(int map, int head, int id, string what)
    {
        _main?.NotifyRomModified("Lufia II: " + what);
        L2FlagUses.Invalidate();
        Read();
        ShowCutscene(map, head, id);
        Status = what + " Add its steps (the first line is a placeholder): ＋ on a card, or click the stage.";
    }

    /// <summary>What deleting the picked cutscene changes, or null when it can't be deleted.</summary>
    public string? DeleteDescription()
    {
        if (_rom == null || Selected is not { } c) return null;
        if (c.Entry.Head == L2EventBlocks.SceneList && L2Cutscene.Starter(_rom, c.Map, c.Entry.Id) != null)
            return null;
        return c.Entry.Head switch
        {
            L2EventBlocks.TalkList => $"Talking to {c.Title[11..]} on {MapName(_rom, c.Map)} will do nothing; the steps stay in the ROM, unused.",
            L2EventBlocks.TriggerList => $"{c.Title} on {MapName(_rom, c.Map)} will do nothing (the area stays on the Maps page); the steps stay in the ROM, unused.",
            _ => $"Scene {c.Entry.Id:X2} of {MapName(_rom, c.Map)} goes; a \"Go to another map\" step that sends the party to it would then start nothing. The steps stay in the ROM, unused.",
        };
    }

    /// <summary>Takes the picked cutscene off its map (after <see cref="DeleteDescription"/> was shown).</summary>
    public void Delete()
    {
        if (_rom == null || _main == null || Selected is not { } c) return;
        Editor.Unload();
        L2EventBlocks.Remove(_rom, c.Map, c.Entry);
        _main.NotifyRomModified($"Lufia II: removed \"{c.Title}\" from {MapName(_rom, c.Map)}");
        L2FlagUses.Invalidate();
        _filling = true; Selected = null; _filling = false;
        Read();
        Status = "Removed.";
    }
}
