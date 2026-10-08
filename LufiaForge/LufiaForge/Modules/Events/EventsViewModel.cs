using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LufiaForge.Core;
using LufiaForge.Core.Maps;
using LufiaForge.Modules.Common;
using LufiaForge.Modules.MapEditor;
using LufiaForge.ViewModels;
using System.Collections.ObjectModel;
using System.Windows;

namespace LufiaForge.Modules.Events;

/// <summary>One event of a map in the list: what starts it and its first line of dialogue.</summary>
public sealed class EventEntry
{
    public int Event { get; init; }
    public string Source { get; init; } = "";
    public string Preview { get; init; } = "";
    /// <summary>What the event seems to do (church, inn, gives an item, cutscene…).</summary>
    public string Hint { get; init; } = "";
    /// <summary>Picture of the character whose event this is (or null).</summary>
    public System.Windows.Media.ImageSource? Picture { get; init; }
    public bool IsUsed { get; init; }
    public bool IsEmpty { get; init; }
    public string Label => $"Event {Event,3}   {Source}";
}

/// <summary>A story flag in the flags list.</summary>
public sealed class FlagRow
{
    public int Flag { get; init; }
    public string Name { get; set; } = "";
    public int Sets { get; init; }
    public int Clears { get; init; }
    public int Checks { get; init; }
    /// <summary>Automatic short name (where it is set) when the user hasn't named it.</summary>
    public string AutoName { get; init; } = "";
    public string Label => $"Flag {Flag:X2}  {(Name.Length > 0 ? Name : AutoName)}";
    public string Summary => $"set {Sets}×, cleared {Clears}×, checked {Checks}×";
}

/// <summary>One use of a flag in the uses list.</summary>
public sealed class FlagUseRow
{
    public FlagUse Use { get; init; } = null!;
    public string Label { get; init; } = "";
}

/// <summary>Events tab: every event script of a map, what triggers it, and the event editor.</summary>
public partial class EventsViewModel : ObservableObject, IEventHost
{
    private RomBuffer? _rom;
    private MainViewModel? _mainVm;
    private List<EventEntry> _all = new();

    public ObservableCollection<MapInfo> Maps { get; } = new();
    public ObservableCollection<EventEntry> Events { get; } = new();

    [ObservableProperty] private MapInfo? _selectedMap;
    [ObservableProperty] private EventEntry? _selectedEvent;
    [ObservableProperty] private bool _showUnused;
    [ObservableProperty] private string _goToEventText = "";
    [ObservableProperty] private string _status = "Open a ROM to browse events.";
    [ObservableProperty] private string _mapSummary = "";

    public RomBuffer? Rom => _rom;

    /// <summary>Asks the view to show an event in the editor panel (and outline a line, or -1).</summary>
    public event Action<EventScript, string, int>? OpenRequested;

    // ── story flags ──
    private List<FlagUse> _flagIndex = new();
    public ObservableCollection<FlagRow> Flags { get; } = new();
    public ObservableCollection<FlagUseRow> FlagUses { get; } = new();
    [ObservableProperty] private int _middleTab;
    [ObservableProperty] private string _flagFilter = "";
    [ObservableProperty] private FlagRow? _selectedFlag;
    [ObservableProperty] private FlagUseRow? _selectedFlagUse;
    [ObservableProperty] private string _flagNameText = "";
    /// <summary>What the selected flag is and does (worked out from its uses).</summary>
    [ObservableProperty] private string _flagDescription = "";
    private bool _fillingFlag;

    /// <summary>Live story-flag tracker (reads the running game through BizHawk).</summary>
    public LiveFlagsViewModel LiveFlags { get; } = new();

    public void SetRom(RomBuffer rom, MainViewModel mainVm)
    {
        _rom = rom; _mainVm = mainVm;
        Maps.Clear();
        foreach (var m in MapCatalog.ScanNamed(rom)) Maps.Add(m);
        EventScript.FlagName = StoryFlags.DisplayName;
        var labels = Maps.ToDictionary(m => m.MapId, m => m.Label);
        EventScript.MapLabel = id => labels.TryGetValue(id, out var l) ? l : $"map {id:X2}";
        EventCommands.EventBase = id => LufiaMap.EventBase(rom, id);
        BuildFlagIndex();
        LiveFlags.SetRom(rom);
        Status = $"{Maps.Count} maps, {Flags.Count} story flags in use. Pick a map, then an event.";
        SelectedMap = Maps.FirstOrDefault(m => m.MapId == 0x04) ?? Maps.FirstOrDefault();
    }

    partial void OnSelectedMapChanged(MapInfo? value) => BuildList();
    partial void OnShowUnusedChanged(bool value) => ApplyFilter();

    private void BuildList()
    {
        _all = new();
        Events.Clear();
        if (_rom == null || SelectedMap == null) return;
        int mapId = SelectedMap.MapId;

        // what starts each event on this map
        var sources = new Dictionary<int, List<string>>();
        var npcSprites = new Dictionary<int, int>();
        void Add(int ev, string s) => (sources.TryGetValue(ev, out var l) ? l : sources[ev] = new()).Add(s);
        try
        {
            var map = LufiaMap.Load(_rom, mapId);
            foreach (var n in map.Npcs.Where(n => !n.IsUnused)) { Add(n.Index, $"Character {n.Index + 1} (talk)"); npcSprites[n.Index] = n.Sprite; }
        }
        catch { /* map without objects */ }
        foreach (var (area, ev, cond, _) in MapSetupScript.ReadTriggers(_rom, mapId))
            Add(ev, $"Step-on area D{area}" + (cond ? " (story-dependent)" : ""));

        var plausible = EventScript.PlausibleEvents(_rom, mapId).ToHashSet();
        foreach (int ev in sources.Keys) plausible.Add(ev);
        foreach (int ev in plausible.OrderBy(e => e))
        {
            EventScript? s = null;
            try { s = EventScript.Load(_rom, mapId, ev); } catch { }
            bool empty = s == null || s.Ops.Count == 0 || (s.Ops.Count == 1 && s.Ops[0].Bytes is [0x00]);
            var text = s?.Ops.FirstOrDefault(o => o.IsText && o.Text.Trim().Length > 0)?.Text.Replace("\n", " ") ?? "";
            if (text.Length > 70) text = text[..70] + "…";
            bool used = sources.ContainsKey(ev);
            _all.Add(new EventEntry
            {
                Event = ev, IsUsed = used, IsEmpty = empty,
                Source = used ? string.Join(", ", sources[ev]) : empty ? "(empty)" : "not started by anything on this map (cutscene, shared or unused)",
                Preview = empty ? "" : text.Length > 0 ? $"\"{text}\"" : "(no dialogue)",
                Hint = empty || s == null ? "" : EventHints.Summarize(_rom, s),
                Picture = npcSprites.TryGetValue(ev, out int spr) ? Picture(spr) : null,
            });
        }
        MapSummary = $"{SelectedMap.Label}: {_all.Count(e => e.IsUsed)} events used by characters / trigger areas, " +
                     $"{_all.Count(e => !e.IsUsed && !e.IsEmpty)} others, event base {LufiaMap.EventBase(_rom, mapId)}";
        ApplyFilter();
    }

    private void ApplyFilter()
    {
        int? keep = SelectedEvent?.Event;
        Events.Clear();
        foreach (var e in _all.Where(e => ShowUnused ? !e.IsEmpty || e.IsUsed : e.IsUsed)) Events.Add(e);
        if (keep != null) { _suppressOpen = true; SelectedEvent = Events.FirstOrDefault(e => e.Event == keep); _suppressOpen = false; }
    }

    partial void OnSelectedEventChanged(EventEntry? value)
    {
        if (value != null && !_suppressOpen) Open(value.Event, value.Source);
    }

    [RelayCommand]
    private void GoToEvent()
    {
        if (!int.TryParse(GoToEventText.Trim(), out int ev) || ev is < 0 or > 255) { Status = "Enter an event number 0-255."; return; }
        Open(ev, _all.FirstOrDefault(e => e.Event == ev)?.Source ?? "");
    }

    private void Open(int ev, string source)
    {
        if (_rom == null || SelectedMap == null) return;
        EventScript? s;
        try { s = EventScript.Load(_rom, SelectedMap.MapId, ev); }
        catch (Exception ex) { Status = $"Event {ev} can't be read: {ex.Message}"; return; }
        if (s == null) { Status = $"Event {ev} of this map has no script."; return; }
        OpenRequested?.Invoke(s, $"{SelectedMap.Label} – event {ev}" + (source.Length > 0 ? $"  ({source})" : ""), -1);
        Status = $"Event {ev}: script at ROM 0x{s.Start:X6}.";
    }

    private readonly Dictionary<int, System.Windows.Media.ImageSource?> _pictures = new();

    private System.Windows.Media.ImageSource? Picture(int sprite)
    {
        if (_rom == null) return null;
        if (_pictures.TryGetValue(sprite, out var img)) return img;
        if (NpcSprites.Render(_rom, sprite) is var (px, w, h))
        {
            var bmp = System.Windows.Media.Imaging.BitmapSource.Create(w, h, 96, 96, System.Windows.Media.PixelFormats.Bgra32, null, px, w * 4);
            bmp.Freeze();
            img = bmp;
        }
        _pictures[sprite] = img;
        return img;
    }

    // ── Story flags ────────────────────────────────────────────────────────

    private void BuildFlagIndex()
    {
        if (_rom == null) return;
        int? keep = SelectedFlag?.Flag;
        _flagIndex = StoryFlags.BuildIndex(_rom, Maps.Select(m => m.MapId));
        FillFlags();
        if (keep != null) SelectedFlag = Flags.FirstOrDefault(f => f.Flag == keep);
    }

    private void FillFlags()
    {
        Flags.Clear();
        string q = FlagFilter.Trim();
        foreach (var g in _flagIndex.GroupBy(u => u.Flag).OrderBy(g => g.Key))
        {
            var row = new FlagRow
            {
                Flag = g.Key, Name = StoryFlags.NameOf(g.Key) ?? "", AutoName = StoryFlags.DisplayName(g.Key) ?? "",
                Sets = g.Count(u => u.Action == "sets"), Clears = g.Count(u => u.Action == "clears"),
                Checks = g.Count(u => !u.Sets),
            };
            if (q.Length > 0 && !row.Label.Contains(q, StringComparison.OrdinalIgnoreCase) && !$"{g.Key:X2}".Equals(q, StringComparison.OrdinalIgnoreCase)) continue;
            Flags.Add(row);
        }
    }

    partial void OnFlagFilterChanged(string value) => FillFlags();

    partial void OnSelectedFlagChanged(FlagRow? value)
    {
        FlagUses.Clear();
        _fillingFlag = true;
        FlagNameText = value?.Name ?? "";
        _fillingFlag = false;
        if (value == null) { FlagDescription = ""; return; }
        string MapLabel(int id) => Maps.FirstOrDefault(m => m.MapId == id)?.Label ?? $"{id:X2}";
        FlagDescription = StoryFlags.Describe(value.Flag, _flagIndex.Where(u => u.Flag == value.Flag).ToList(), MapLabel);
        foreach (var u in _flagIndex.Where(u => u.Flag == value.Flag)
                     .OrderBy(u => u.Sets ? 0 : 1).ThenBy(u => u.MapId).ThenBy(u => u.Event))
            FlagUses.Add(new FlagUseRow
            {
                Use = u,
                Label = u.IsSetup
                    ? $"{MapLabel(u.MapId)} · map setup script — {u.Action}"
                    : $"{MapLabel(u.MapId)} · event {u.Event}" + (u.RegionStart >= 0 && u.RegionStart != u.JumpBase ? " (jumped-to code)" : "") +
                      $" · line {u.Line + 1} — {u.Action}",
            });
    }

    /// <summary>Rename the selected flag (saved for every ROM copy).</summary>
    partial void OnFlagNameTextChanged(string value)
    {
        if (_fillingFlag || SelectedFlag == null) return;
        StoryFlags.SetName(SelectedFlag.Flag, value);
        int f = SelectedFlag.Flag;
        FillFlags();
        _fillingFlag = true;
        SelectedFlag = Flags.FirstOrDefault(r => r.Flag == f);
        _fillingFlag = false;
    }

    partial void OnSelectedFlagUseChanged(FlagUseRow? value)
    {
        if (value == null || _rom == null) return;
        var u = value.Use;
        if (u.IsSetup) { Status = $"Flag {u.Flag:X2} is checked by map {u.MapId:X2}'s setup script at 0x{u.Offset:X6} (decides what the map shows)."; return; }
        if (SelectedMap?.MapId != u.MapId)
        {
            _suppressOpen = true;
            SelectedMap = Maps.FirstOrDefault(m => m.MapId == u.MapId);
            _suppressOpen = false;
        }
        EventScript? s = u.RegionStart >= 0 && u.RegionStart != u.JumpBase
            ? EventScript.LoadAt(_rom, u.MapId, u.Event, u.RegionStart, u.JumpBase)
            : EventScript.Load(_rom, u.MapId, u.Event);
        if (s == null) return;
        string title = $"{SelectedMap?.Label} – event {u.Event}" + (s.IsCodeView ? $" (code at 0x{s.Start:X6})" : "");
        OpenRequested?.Invoke(s, title, u.Line);
        Status = $"Flag {u.Flag:X2} {u.Action} at line {u.Line + 1} (highlighted).";
    }

    /// <summary>From the editor's ⚑ button: show the flag in the Story flags list.</summary>
    public void ShowFlag(int flag)
    {
        FlagFilter = "";
        MiddleTab = 1;
        SelectedFlag = Flags.FirstOrDefault(f => f.Flag == flag);
        if (SelectedFlag == null) Status = $"Flag {flag:X2} isn't used anywhere else.";
    }

    // ── IEventHost ─────────────────────────────────────────────────────────

    public bool ConfirmExpand(string what) =>
        MessageBox.Show(
            $"{what}\n\nTo store it, Lufia Forge needs to expand the ROM from 1 MB to 2 MB and use the new space. " +
            "Expanded ROMs work in BizHawk, Snes9x and bsnes. Note: savestates made before the expansion will undo " +
            "anything stored in the new space, so continue from an in-game save instead. Continue?",
            "Expand ROM?", MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes;

    public void EventSaved(string what)
    {
        _mainVm?.NotifyRomModified(what);
        BuildFlagIndex();
        int? keep = SelectedEvent?.Event;
        BuildList();
        if (keep != null) { _suppressOpen = true; SelectedEvent = Events.FirstOrDefault(e => e.Event == keep); _suppressOpen = false; }
    }
    private bool _suppressOpen;

    public void ConfirmWritten(string heading, string details) =>
        InfoDialog.Show("Written to ROM", "✔ " + heading,
            details + "\n\nThe change is in the ROM loaded in Lufia Forge, but not on disk yet. " +
            "Use File > Save ROM (or the button below) to write a new dated copy; the ROM you opened is never overwritten.",
            path: _rom?.FilePath, pathLabel: "ROM being edited (the copy is saved next to it)",
            primaryText: _mainVm != null ? "💾 Save ROM now" : null, primary: () => _mainVm?.SaveNow());

    /// <summary>True while the list is rebuilt after a save (the editor keeps showing the saved event).</summary>
    public bool SuppressOpen => _suppressOpen;
}
