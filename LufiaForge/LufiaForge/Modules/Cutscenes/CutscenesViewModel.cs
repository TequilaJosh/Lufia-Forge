using CommunityToolkit.Mvvm.ComponentModel;
using LufiaForge.Core;
using LufiaForge.Core.Maps;
using LufiaForge.Modules.Common;
using LufiaForge.Modules.MapEditor;
using LufiaForge.ViewModels;
using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace LufiaForge.Modules.Cutscenes;

// a cutscene in the list
public sealed class CutsceneEntry
{
    public int MapId { get; init; }
    public int Event { get; init; }
    public string MapLabel { get; init; } = "";
    // a known name ("Opening intro") or empty
    public string Title { get; init; } = "";
    public string Hint { get; init; } = "";
    public int StageCommands { get; init; }
    public int Texts { get; init; }
    public ImageSource? Picture { get; init; }
    public string Label => Title.Length > 0 ? Title : $"{MapLabel} - event {Event}";
    public string Details => (Title.Length > 0 ? $"{MapLabel} - event {Event} · " : "") +
                             $"{StageCommands} stage command{(StageCommands == 1 ? "" : "s")}, {Texts} text box{(Texts == 1 ? "" : "es")}";
}

// the Cutscenes tab: every event that moves characters, the camera or the screen, opened in the event editor's
// cutscene layout (stage + timeline)
public partial class CutscenesViewModel : ObservableObject, IEventHost
{
    private RomBuffer? _rom;
    private MainViewModel? _mainVm;
    private List<CutsceneEntry> _all = new();

    public RomBuffer? Rom => _rom;
    public ObservableCollection<CutsceneEntry> Cutscenes { get; } = new();
    public ObservableCollection<string> MapFilters { get; } = new();
    private readonly List<int> _mapFilterIds = new();

    [ObservableProperty] private CutsceneEntry? _selected;
    [ObservableProperty] private string _search = "";
    [ObservableProperty] private int _mapFilterIndex;
    [ObservableProperty] private bool _onlyBig;
    [ObservableProperty] private string _status = "Open a ROM to list its cutscenes.";
    [ObservableProperty] private bool _isBusy;

    // fires to open a cutscene in the editor
    public event Action<EventScript, string>? OpenRequested;

    // names for the cutscenes everyone knows
    private static readonly Dictionary<(int Map, int Ev), string> KnownNames = new()
    {
        [(0x4F, 8)] = "Opening intro (before the title)",
        [(0x93, 2)] = "Prologue: the heroes enter the Fortress of Doom",
        [(0x2D, 12)] = "Ending (after the final battle)",
        [(0x2D, 13)] = "Ending credits (staff roll, THE END)",
    };

    public void SetRom(RomBuffer rom, MainViewModel mainVm)
    {
        _rom = rom; _mainVm = mainVm;
        _ = BuildAsync();
    }

    // the scan, wrapped so it can't fail silently (nothing awaits it, so an error left the tab stuck on
    // "Finding the cutscenes..." for good)
    private async Task BuildAsync()
    {
        try { await BuildCore(); }
        catch (Exception ex) { IsBusy = false; Status = "The cutscenes couldn't be listed: " + ex.Message; }
    }

    private async Task BuildCore()
    {
        if (_rom == null) return;
        var rom = _rom;
        IsBusy = true;
        Status = "Finding the cutscenes…";
        var maps = MapCatalog.ScanNamed(rom);
        var labels = maps.ToDictionary(m => m.MapId, m => m.Label);
        EventScript.MapLabel ??= id => labels.TryGetValue(id, out var label) ? label : $"map {id:X2}";
        EventCommands.EventBase = id => LufiaMap.EventBase(rom, id);
        var list = await Task.Run(() =>
        {
            var found = new List<CutsceneEntry>();
            // events that other events continue with (command 51). no character or area reaches them, so find them first
            var chained = new HashSet<(int Map, int Ev)>();
            foreach (var mapInfo in maps)
                foreach (int ev in EventScript.PlausibleEvents(rom, mapInfo.MapId))
                {
                    var script = EventScript.Load(rom, mapInfo.MapId, ev);
                    if (script == null) continue;
                    foreach (var op in script.Ops.Where(o => !o.IsText && o.Bytes.Length == 3 && o.Bytes[0] == 0x51))
                        chained.Add((op.Bytes[1], EventCommands.ChainedEvent(op.Bytes)));
                }
            foreach (var mapInfo in maps)
            {
                LufiaMap? map = null;
                try { map = LufiaMap.Load(rom, mapInfo.MapId); } catch { }
                // known cutscenes the game starts from code (the intro) aren't reached by a character or area either
                var events = EventScript.PlausibleEvents(rom, mapInfo.MapId)
                    .Concat(KnownNames.Keys.Where(k => k.Map == mapInfo.MapId).Select(k => k.Ev))
                    .Concat(chained.Where(c => c.Map == mapInfo.MapId).Select(c => c.Ev)).Distinct().OrderBy(e => e);
                foreach (int ev in events)
                {
                    EventScript? script;
                    try { script = EventScript.Load(rom, mapInfo.MapId, ev); } catch { continue; }
                    if (script == null) continue;
                    int stage = CutsceneStage.StageCommandCount(script);
                    bool known = KnownNames.ContainsKey((mapInfo.MapId, ev));
                    // the intro starts with a jump into its code, so count what it jumps to
                    if (stage == 0 && known && script.Ops.Count > 0 && script.Ops[0].Targets.Count > 0)
                    {
                        var code = EventScript.LoadAt(rom, mapInfo.MapId, ev, script.Ops[0].Targets[0].Absolute, script.JumpBase);
                        stage = CutsceneStage.StageCommandCount(code);
                    }
                    if (stage == 0) continue;
                    string hint = "";
                    try { hint = EventHints.Summarize(rom, script); } catch { }
                    int sprite = map?.Npcs.FirstOrDefault(n => MapEvents.TalkEvent(n.Index) == ev && !n.IsUnused)?.Sprite ?? -1;
                    found.Add(new CutsceneEntry
                    {
                        MapId = mapInfo.MapId, Event = ev, MapLabel = mapInfo.Label, Title = KnownNames.GetValueOrDefault((mapInfo.MapId, ev), ""),
                        Hint = hint, StageCommands = stage, Texts = script.Ops.Count(o => o.IsText), Picture = Picture(rom, sprite),
                    });
                }
            }
            return found;
        });
        // known cutscenes first, in story order (intro, prologue, ending, credits), then the rest by map
        var order = KnownNames.Keys.ToList();
        _all = list.OrderBy(c => order.IndexOf((c.MapId, c.Event)) is var i && i >= 0 ? i : int.MaxValue)
                   .ThenBy(c => c.MapId).ThenBy(c => c.Event).ToList();
        MapFilters.Clear(); _mapFilterIds.Clear();
        MapFilters.Add("All maps"); _mapFilterIds.Add(-1);
        foreach (var id in _all.Select(c => c.MapId).Distinct().OrderBy(i => i)) { MapFilters.Add(labels[id]); _mapFilterIds.Add(id); }
        MapFilterIndex = 0;
        IsBusy = false;
        ApplyFilter();
        Selected = Cutscenes.FirstOrDefault();
    }

    private static ImageSource? Picture(RomBuffer rom, int sprite)
    {
        if (sprite < 0 || NpcSprites.Render(rom, sprite) is not var (pixels, width, height)) return null;
        var bitmap = BitmapSource.Create(width, height, 96, 96, PixelFormats.Bgra32, null, pixels, width * 4);
        bitmap.Freeze();
        return bitmap;
    }

    partial void OnSearchChanged(string value) => ApplyFilter();
    partial void OnMapFilterIndexChanged(int value) => ApplyFilter();
    partial void OnOnlyBigChanged(bool value) => ApplyFilter();

    private void ApplyFilter()
    {
        var keep = Selected;
        Cutscenes.Clear();
        int map = MapFilterIndex >= 0 && MapFilterIndex < _mapFilterIds.Count ? _mapFilterIds[MapFilterIndex] : -1;
        string term = Search.Trim();
        foreach (var cutscene in _all)
        {
            if (map >= 0 && cutscene.MapId != map) continue;
            if (OnlyBig && cutscene.StageCommands < 6) continue;
            if (term.Length > 0 && !cutscene.Label.Contains(term, StringComparison.OrdinalIgnoreCase) && !cutscene.Hint.Contains(term, StringComparison.OrdinalIgnoreCase)) continue;
            Cutscenes.Add(cutscene);
        }
        if (keep != null && Cutscenes.Contains(keep)) { _suppressOpen = true; Selected = keep; _suppressOpen = false; }
        if (!IsBusy) Status = $"{Cutscenes.Count} of {_all.Count} cutscenes shown.";
    }

    private bool _suppressOpen;

    partial void OnSelectedChanged(CutsceneEntry? value)
    {
        if (value == null || _rom == null || _suppressOpen) return;
        // the whole event: the code its jumps go to is part of it (the intro is one jump to its real code)
        var script = EventScript.LoadWhole(_rom, value.MapId, value.Event);
        if (script == null) return;
        OpenRequested?.Invoke(script, value.Label);
    }

    // new / delete

    // a new cutscene: a new event on a map (New event window), listed at the top and opened on the stage
    public void NewCutscene(Window? owner)
    {
        if (_rom == null) return;
        var maps = MapCatalog.ScanNamed(_rom);
        int map = Selected?.MapId ?? (MapFilterIndex > 0 && MapFilterIndex < _mapFilterIds.Count ? _mapFilterIds[MapFilterIndex] : 0x04);
        var dialog = new Events.NewEventWindow(_rom, maps, map, "New cutscene", cutscene: true) { Owner = owner };
        if (dialog.ShowDialog() != true) return;
        _mainVm?.NotifyRomModified(dialog.ResultText);
        string label = maps.FirstOrDefault(m => m.MapId == dialog.ResultMap)?.Label ?? $"map {dialog.ResultMap:X2}";
        var entry = new CutsceneEntry { MapId = dialog.ResultMap, Event = dialog.ResultEvent, MapLabel = label, Title = $"New cutscene: {label}, event {dialog.ResultEvent}", Hint = dialog.ResultText };
        _all.Insert(0, entry);
        Search = ""; OnlyBig = false; MapFilterIndex = 0;
        ApplyFilter();
        Selected = entry;
        Status = dialog.ResultText + ". Add its moves, camera and dialogue on the stage; it's written by itself (or click Apply in the top bar).";
    }

    // deletes the picked cutscene's event (after saying what changes)
    public bool DeleteSelected(Window? owner)
    {
        if (_rom == null || Selected is not { } cutscene) { Status = "Pick a cutscene first."; return false; }
        var maps = MapCatalog.ScanNamed(_rom);
        string Label(int id) => maps.FirstOrDefault(m => m.MapId == id)?.Label ?? $"map {id:X2}";
        string? done = Events.EventDelete.Run(owner, _rom, cutscene.MapId, cutscene.Event, Label(cutscene.MapId), Label);
        if (done == null) return false;
        _mainVm?.NotifyRomModified(done);
        _all.Remove(cutscene);
        _suppressOpen = true; Selected = null; _suppressOpen = false;
        ApplyFilter();
        Status = done + ".";
        return true;
    }

    // IEventHost
    public bool ConfirmExpand(string what) =>
        MessageBox.Show(
            $"{what}\n\nTo store it, Lufia Forge needs to expand the ROM from 1 MB to 2 MB and use the new space. " +
            "Expanded ROMs work in BizHawk, Snes9x and bsnes. Note: savestates made before the expansion will undo " +
            "anything stored in the new space, so continue from an in-game save instead. Continue?",
            "Expand ROM?", MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes;

    public void EventSaved(string what) => _mainVm?.NotifyRomModified(what);

    public void ConfirmWritten(string heading, string details) =>
        InfoDialog.Show("Saved in the project", "✔ " + heading,
            details + "\n\nIt's in your project now: saving the project keeps it (Ctrl+S, or on its own every 5 minutes), and File > Export ROM writes a playable ROM.",
            path: null, pathLabel: null,
            primaryText: _mainVm != null ? "💾 Save project now" : null, primary: () => _mainVm?.SaveNow());
}
