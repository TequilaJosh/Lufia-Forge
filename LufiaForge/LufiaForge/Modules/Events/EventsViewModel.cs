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

// one event of a map in the list: what starts it and its first line of dialogue
public sealed class EventEntry
{
    public int Event { get; init; }
    public string Source { get; init; } = "";
    public string Preview { get; init; } = "";
    // what the event seems to do (church, inn, gives an item, cutscene...)
    public string Hint { get; init; } = "";
    // picture of the character whose event this is (or null)
    public System.Windows.Media.ImageSource? Picture { get; init; }
    public bool IsUsed { get; init; }
    public bool IsEmpty { get; init; }
    public string Label => $"Event {Event,3}   {Source}";
}

// a story flag in the flags list
public sealed class FlagRow
{
    public int Flag { get; init; }
    public string Name { get; set; } = "";
    public int Sets { get; init; }
    public int Clears { get; init; }
    public int Checks { get; init; }
    // automatic short name (where it gets set) if the user hasn't named it
    public string AutoName { get; init; } = "";
    public string Label => $"Flag {Flag:X2}  {(Name.Length > 0 ? Name : AutoName)}";
    public string Summary => $"set {Sets}×, cleared {Clears}×, checked {Checks}×";
}

// one use of a flag in the uses list
public sealed class FlagUseRow
{
    public FlagUse Use { get; init; } = null!;
    public string Label { get; init; } = "";
}

// the Events tab: every event script of a map, what triggers it, and the event editor
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

    // asks the view to show an event in the editor panel (and outline a line, or -1)
    public event Action<EventScript, string, int>? OpenRequested;

    // story flags
    private List<FlagUse> _flagIndex = new();
    public ObservableCollection<FlagRow> Flags { get; } = new();
    public ObservableCollection<FlagUseRow> FlagUses { get; } = new();
    [ObservableProperty] private int _middleTab;
    [ObservableProperty] private string _flagFilter = "";
    [ObservableProperty] private FlagRow? _selectedFlag;
    [ObservableProperty] private FlagUseRow? _selectedFlagUse;
    [ObservableProperty] private string _flagNameText = "";
    // what the picked flag is and does (worked out from its uses)
    [ObservableProperty] private string _flagDescription = "";
    private bool _fillingFlag;

    // live story flag tracker (reads the running game through BizHawk)
    public LiveFlagsViewModel LiveFlags { get; } = new();

    public void SetRom(RomBuffer rom, MainViewModel mainVm)
    {
        // the same ROM again (undo / redo): stay on the map that was open
        int? keepMap = ReferenceEquals(rom, _rom) ? SelectedMap?.MapId : null;
        _rom = rom; _mainVm = mainVm;
        Maps.Clear();
        foreach (var map in MapCatalog.ScanNamed(rom)) Maps.Add(map);
        EventScript.FlagName = StoryFlags.DisplayName;
        var labels = Maps.ToDictionary(m => m.MapId, m => m.Label);
        EventScript.MapLabel = id => labels.TryGetValue(id, out var label) ? label : $"map {id:X2}";
        EventCommands.EventBase = id => LufiaMap.EventBase(rom, id);
        BuildFlagIndex();
        LiveFlags.SetRom(rom);
        Status = $"{Maps.Count} maps, {Flags.Count} story flags in use. Pick a map, then an event.";
        SelectedMap = Maps.FirstOrDefault(m => m.MapId == (keepMap ?? 0x04)) ?? Maps.FirstOrDefault();
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
        void Add(int ev, string source) => (sources.TryGetValue(ev, out var list) ? list : sources[ev] = new()).Add(source);
        try
        {
            var map = LufiaMap.Load(_rom, mapId);
            foreach (var npc in map.Npcs.Where(n => !n.IsUnused)) { Add(MapEvents.TalkEvent(npc.Index), $"Character {npc.Index + 1} (talk)"); npcSprites[MapEvents.TalkEvent(npc.Index)] = npc.Sprite; }
        }
        catch { /* map without objects */ }
        foreach (var (area, ev, storyDependent, _) in MapSetupScript.ReadTriggers(_rom, mapId))
            Add(ev, $"Step-on area D{area}" + (storyDependent ? " (story-dependent)" : ""));

        var plausible = EventScript.PlausibleEvents(_rom, mapId).ToHashSet();
        foreach (int ev in sources.Keys) plausible.Add(ev);
        foreach (int ev in plausible.OrderBy(e => e))
        {
            EventScript? script = null;
            try { script = EventScript.Load(_rom, mapId, ev); } catch { }
            bool empty = script == null || script.Ops.Count == 0 || (script.Ops.Count == 1 && script.Ops[0].Bytes is [0x00]);
            var text = script?.Ops.FirstOrDefault(o => o.IsText && o.Text.Trim().Length > 0)?.Text.Replace("\n", " ") ?? "";
            if (text.Length > 70) text = text[..70] + "…";
            bool used = sources.ContainsKey(ev);
            _all.Add(new EventEntry
            {
                Event = ev, IsUsed = used, IsEmpty = empty,
                Source = used ? string.Join(", ", sources[ev]) : empty ? "(empty)" : "not started by anything on this map (cutscene, shared or unused)",
                Preview = empty ? "" : text.Length > 0 ? $"\"{text}\"" : "(no dialogue)",
                Hint = empty || script == null ? "" : EventHints.Summarize(_rom, script),
                Picture = npcSprites.TryGetValue(ev, out int sprite) ? Picture(sprite) : null,
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
        foreach (var entry in _all.Where(e => ShowUnused ? !e.IsEmpty || e.IsUsed : e.IsUsed)) Events.Add(entry);
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
        EventScript? script;
        try { script = EventScript.Load(_rom, SelectedMap.MapId, ev); }
        catch (Exception ex) { Status = $"Event {ev} can't be read: {ex.Message}"; return; }
        if (script == null) { Status = $"Event {ev} of this map has no script."; return; }
        OpenRequested?.Invoke(script, $"{SelectedMap.Label} - event {ev}" + (source.Length > 0 ? $"  ({source})" : ""), -1);
        Status = $"Event {ev}: script at ROM 0x{script.Start:X6}.";
    }

    private readonly Dictionary<int, System.Windows.Media.ImageSource?> _pictures = new();

    private System.Windows.Media.ImageSource? Picture(int sprite)
    {
        if (_rom == null) return null;
        if (_pictures.TryGetValue(sprite, out var picture)) return picture;
        if (NpcSprites.Render(_rom, sprite) is var (pixels, width, height))
        {
            var bitmap = System.Windows.Media.Imaging.BitmapSource.Create(width, height, 96, 96, System.Windows.Media.PixelFormats.Bgra32, null, pixels, width * 4);
            bitmap.Freeze();
            picture = bitmap;
        }
        _pictures[sprite] = picture;
        return picture;
    }

    // new / delete events

    // makes a new event (New event window) on the picked map and opens it
    public void NewEvent(Window? owner)
    {
        if (_rom == null || SelectedMap == null) return;
        var dialog = new NewEventWindow(_rom, Maps, SelectedMap.MapId, "New event", cutscene: false) { Owner = owner };
        if (dialog.ShowDialog() != true) return;
        _mainVm?.NotifyRomModified(dialog.ResultText);
        int map = dialog.ResultMap, ev = dialog.ResultEvent;
        if (SelectedMap.MapId != map) SelectedMap = Maps.FirstOrDefault(m => m.MapId == map);
        else BuildList();
        BuildFlagIndex();
        Open(ev, "new");
        Status = dialog.ResultText + ". It's open on the right: add its lines; it's written by itself (or click Apply in the top bar).";
    }

    // deletes the picked event (after saying what changes)
    public void DeleteSelectedEvent(Window? owner)
    {
        if (_rom == null || SelectedMap == null || SelectedEvent == null) { Status = "Pick an event first."; return; }
        string? done = EventDelete.Run(owner, _rom, SelectedMap.MapId, SelectedEvent.Event, SelectedMap.Label, MapLabelOf);
        if (done == null) return;
        _mainVm?.NotifyRomModified(done);
        BuildList();
        Status = done + ".";
    }

    private string MapLabelOf(int id) => Maps.FirstOrDefault(m => m.MapId == id)?.Label ?? $"map {id:X2}";

    // story flags

    // claims the first free flag (nothing in the game uses it) and gives it a name of its own, ready to rename
    [RelayCommand]
    private void NewFlag()
    {
        if (_rom == null) return;
        int flag = Enumerable.Range(0, 0xF0).FirstOrDefault(x => StoryFlags.IsFree(_rom, x) && StoryFlags.NameOf(x) == null, -1);
        if (flag < 0) { Status = "No free story flags are left."; return; }
        StoryFlags.SetName(flag, $"My flag {flag:X2}");
        FlagFilter = "";
        FillFlags();
        OnPropertyChanged(nameof(FreeFlagsText));
        SelectedFlag = Flags.FirstOrDefault(r => r.Flag == flag);
        if (SelectedFlag == null) { ShowFreeFlags = true; SelectedFlag = Flags.FirstOrDefault(r => r.Flag == flag); }
        Status = $"Story flag {flag:X2} is yours: rename it below, then use it in events (Set story flag / If flag is set).";
    }

    // gives the picked flag back: its name goes (events that use it stay as they are)
    [RelayCommand]
    private void FreeFlag()
    {
        if (_rom == null || SelectedFlag == null) return;
        int flag = SelectedFlag.Flag;
        int uses = _flagIndex.Count(u => u.Flag == flag);
        if (StoryFlags.NameOf(flag) == null && uses == 0) { Status = $"Flag {flag:X2} is already free."; return; }
        if (uses > 0 && MessageBox.Show($"Flag {flag:X2} is still used in {uses} place(s) (listed below). Free it anyway? Its name goes; the events keep using it until you change them.",
                "Free story flag", MessageBoxButton.OKCancel, MessageBoxImage.Warning) != MessageBoxResult.OK) return;
        StoryFlags.SetName(flag, null);
        FillFlags();
        OnPropertyChanged(nameof(FreeFlagsText));
        SelectedFlag = Flags.FirstOrDefault(r => r.Flag == flag);
        Status = uses == 0 ? $"Flag {flag:X2} is free again." : $"Flag {flag:X2} lost its name; {uses} use(s) remain in events.";
    }

    // story flags (the index)

    private void BuildFlagIndex()
    {
        if (_rom == null) return;
        int? keep = SelectedFlag?.Flag;
        _flagIndex = StoryFlags.BuildIndex(_rom, Maps.Select(m => m.MapId));
        FillFlags();
        if (keep != null) SelectedFlag = Flags.FirstOrDefault(f => f.Flag == keep);
        OnPropertyChanged(nameof(FreeFlagsText));
    }

    private void FillFlags()
    {
        Flags.Clear();
        string term = FlagFilter.Trim();
        var groups = _flagIndex.GroupBy(u => u.Flag).ToDictionary(g => g.Key, g => g.ToList());
        // the free flags (nothing in the game uses them) can be listed too, to pick one for your own quest
        var flagNumbers = ShowFreeFlags && _rom != null
            ? Enumerable.Range(0, 256).Where(f => groups.ContainsKey(f) || StoryFlags.IsFree(_rom, f))
            : groups.Keys.OrderBy(k => k);
        foreach (int flag in flagNumbers)
        {
            var uses = groups.TryGetValue(flag, out var list) ? list : new List<FlagUse>();
            bool free = _rom != null && StoryFlags.IsFree(_rom, flag);
            var row = new FlagRow
            {
                Flag = flag, Name = StoryFlags.NameOf(flag) ?? "", AutoName = StoryFlags.DisplayName(flag) ?? (free ? "free - not used by the game" : ""),
                Sets = uses.Count(u => u.Action == "sets"), Clears = uses.Count(u => u.Action == "clears"),
                Checks = uses.Count(u => !u.Sets),
            };
            if (term.Length > 0 && !row.Label.Contains(term, StringComparison.OrdinalIgnoreCase) && !$"{flag:X2}".Equals(term, StringComparison.OrdinalIgnoreCase)) continue;
            Flags.Add(row);
        }
    }

    partial void OnFlagFilterChanged(string value) => FillFlags();

    // lists the flags nothing in the game uses, so you can name one and use it for a new quest
    [ObservableProperty] private bool _showFreeFlags;
    partial void OnShowFreeFlagsChanged(bool value) => FillFlags();

    // free flags left (the game doesn't use them and you haven't named them yet)
    public string FreeFlagsText => _rom == null || StoryFlags.UsedByScripts == null ? "" :
        $"{Enumerable.Range(0, 256).Count(f => StoryFlags.IsFree(_rom, f) && StoryFlags.NameOf(f) == null)} free flags left for your own quests";

    partial void OnSelectedFlagChanged(FlagRow? value)
    {
        FlagUses.Clear();
        _fillingFlag = true;
        FlagNameText = value?.Name ?? "";
        _fillingFlag = false;
        if (value == null) { FlagDescription = ""; return; }
        string MapLabel(int id) => Maps.FirstOrDefault(m => m.MapId == id)?.Label ?? $"{id:X2}";
        FlagDescription = StoryFlags.Describe(value.Flag, _flagIndex.Where(u => u.Flag == value.Flag).ToList(), MapLabel);
        foreach (var use in _flagIndex.Where(u => u.Flag == value.Flag)
                     .OrderBy(u => u.Sets ? 0 : 1).ThenBy(u => u.MapId).ThenBy(u => u.Event))
            FlagUses.Add(new FlagUseRow
            {
                Use = use,
                Label = use.IsSetup
                    ? $"{MapLabel(use.MapId)} · map setup script: {use.Action}"
                    : $"{MapLabel(use.MapId)} · event {use.Event}" + (use.RegionStart >= 0 && use.RegionStart != use.JumpBase ? " (jumped-to code)" : "") +
                      $" · line {use.Line + 1}: {use.Action}",
            });
    }

    // renames the picked flag (saved for every copy of the ROM)
    partial void OnFlagNameTextChanged(string value)
    {
        if (_fillingFlag || SelectedFlag == null) return;
        StoryFlags.SetName(SelectedFlag.Flag, value);
        int flag = SelectedFlag.Flag;
        FillFlags();
        OnPropertyChanged(nameof(FreeFlagsText));
        _fillingFlag = true;
        SelectedFlag = Flags.FirstOrDefault(r => r.Flag == flag);
        _fillingFlag = false;
    }

    partial void OnSelectedFlagUseChanged(FlagUseRow? value)
    {
        if (value == null || _rom == null) return;
        var use = value.Use;
        if (use.IsSetup) { Status = $"Flag {use.Flag:X2} is checked by map {use.MapId:X2}'s setup script at 0x{use.Offset:X6} (decides what the map shows)."; return; }
        if (SelectedMap?.MapId != use.MapId)
        {
            _suppressOpen = true;
            SelectedMap = Maps.FirstOrDefault(m => m.MapId == use.MapId);
            _suppressOpen = false;
        }
        EventScript? script = use.RegionStart >= 0 && use.RegionStart != use.JumpBase
            ? EventScript.LoadAt(_rom, use.MapId, use.Event, use.RegionStart, use.JumpBase)
            : EventScript.Load(_rom, use.MapId, use.Event);
        if (script == null) return;
        string title = $"{SelectedMap?.Label} - event {use.Event}" + (script.IsCodeView ? $" (code at 0x{script.Start:X6})" : "");
        OpenRequested?.Invoke(script, title, use.Line);
        Status = $"Flag {use.Flag:X2} {use.Action} at line {use.Line + 1} (highlighted).";
    }

    // from the editor's flag button: show the flag in the Story flags list
    public void ShowFlag(int flag)
    {
        FlagFilter = "";
        MiddleTab = 1;
        SelectedFlag = Flags.FirstOrDefault(f => f.Flag == flag);
        if (SelectedFlag == null) Status = $"Flag {flag:X2} isn't used anywhere else.";
    }

    // IEventHost

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
        InfoDialog.Show("Saved in the project", "✔ " + heading,
            details + "\n\nIt's in your project now: saving the project keeps it (Ctrl+S, or on its own every 5 minutes), and File > Export ROM writes a playable ROM.",
            path: null, pathLabel: null,
            primaryText: _mainVm != null ? "💾 Save project now" : null, primary: () => _mainVm?.SaveNow());

    // true while the list gets rebuilt after a save (the editor keeps showing the saved event)
    public bool SuppressOpen => _suppressOpen;
}
