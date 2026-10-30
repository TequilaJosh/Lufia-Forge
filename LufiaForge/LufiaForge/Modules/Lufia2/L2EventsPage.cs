using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LufiaForge.Core;
using LufiaForge.Core.Lufia2;

namespace LufiaForge.Modules.Lufia2;

/// <summary>
/// The Events tab: a map's events (talking to characters, stepping on spots, scenes, the start-up script) in a list;
/// the picked one opens in the event editor (<see cref="L2ScriptEditor"/>, the same editor the Maps page pops up and
/// the Cutscenes page uses), with its steps as cards, the stage and the timeline. Also the story flags with every
/// event that uses them.
/// </summary>
public partial class L2EventsPage : L2Page
{
    private int _map = -1;
    private string[] _maps = Array.Empty<string>();
    private bool _skipLeave;

    public L2ScriptEditor Editor { get; } = new();
    public ObservableCollection<L2EventItem> Events { get; } = new();
    [ObservableProperty] private L2EventItem? _selectedEvent;
    [ObservableProperty] private string _summary = "";
    [ObservableProperty] private string _status = "";

    public ObservableCollection<string> Flags { get; } = new();
    [ObservableProperty] private int _selectedFlag = -1;
    [ObservableProperty] private string _flagUsesText = "";

    public RomBuffer? CurrentRom => Rom;
    public int CurrentMap => _map;
    public string CurrentMapName => _map >= 0 && _map < _maps.Length ? _maps[_map] : "";
    /// <summary>The map's characters (actor id: name, sprite or -1), for the New event window.</summary>
    public IReadOnlyDictionary<int, (string Name, int Sprite)> Characters => Editor.Characters;
    public string Who(int id) => Editor.Characters.TryGetValue(id, out var w) ? w.Name : L2EventCommands.DefaultWho(id);

    public L2EventsPage()
    {
        Editor.Saved += _ => { Flags.Clear(); LoadFlags(); FillEvents(Editor.Event?.Entry, Editor.Event?.IsInit == true, keepEditor: true); };
        Editor.FlagRequested += f => { SelectedFlag = f; FlagRequested?.Invoke(f); };
        Editor.PropertyChanged += (_, e) => { if (e.PropertyName == nameof(L2ScriptEditor.Status)) Status = Editor.Status; };
    }

    /// <summary>Asks the view to show the Story flags tab on a flag.</summary>
    public event Action<int>? FlagRequested;

    protected override IEnumerable<string> ReadNames()
    {
        // a new ROM (another project, or Edit > Undo / Redo): unsaved steps belong to the old one, dropped without asking
        Editor.Unload();
        Flags.Clear();
        _maps = Enumerable.Range(0, L2Maps.Count).Select(m => L2ScriptEditor.MapNameOf(Rom!, m)).ToArray();
        return Enumerable.Range(0, L2Maps.Count).Select(m => $"{m:X2}  {_maps[m]}");
    }

    protected override void Load(int index)
    {
        if (!_skipLeave) Editor.ConfirmLeave();
        _map = index;
        if (Flags.Count == 0) LoadFlags();
        FillEvents(null, false, keepEditor: false);
    }

    private void FillEvents(L2EventBlocks.Entry? select, bool init, bool keepEditor)
    {
        var rom = Rom!;
        Events.Clear();
        foreach (var e in L2ScriptEditor.ItemsOf(rom, _map)) Events.Add(e);
        int count = Events.Count(e => e.Entry != null);
        int block = L2Events.Block(rom, _map);
        Summary = $"{count} events{(block < 0 ? " (this map has no event block)" : "")}. Pick one to edit it; \"New event\" adds one.";
        _skipLeave = true;
        SelectedEvent = init ? Events[0] : select != null ? Events.FirstOrDefault(x => x.Entry?.Head == select.Head && x.Entry?.Id == select.Id) : null;
        _skipLeave = false;
        if (SelectedEvent == null && !keepEditor) Editor.Unload();
    }

    partial void OnSelectedEventChanged(L2EventItem? value)
    {
        if (Rom == null || Main == null || value == null) return;
        if (!_skipLeave && !Editor.ConfirmLeave()) return;
        // the editor shows it already (after a write the list was read again)
        if (Editor.Event != null && Editor.Map == _map && Editor.Event.IsInit == value.IsInit && Editor.Event.Title == value.Title
            && (Editor.Event.Entry == null) == (value.Entry == null) && (value.Entry == null || Editor.Event.Entry!.Head == value.Entry.Head && Editor.Event.Entry.Id == value.Entry.Id)
            && !Editor.IsDirty && _skipLeave) return;
        Editor.Load(Rom, Main, _map, value, L2ScriptEditor.TitleOf(Rom, _map, value));
    }

    /// <summary>Shows one event of a map (from another page).</summary>
    public void ShowEvent(int map, int head, int id)
    {
        if (Rom == null) return;
        if (!Editor.ConfirmLeave()) return;
        if (SelectedIndex != map) SelectedIndex = map; else FillEvents(null, false, keepEditor: false);
        var item = Events.FirstOrDefault(e => e.Entry?.Head == head && e.Entry?.Id == id);
        if (item != null) SelectedEvent = item;
    }

    /// <summary>Reads the map's list again (another page wrote the map's events).</summary>
    public void Reload()
    {
        if (Rom == null || _map < 0) return;
        var keep = SelectedEvent;
        FillEvents(keep?.Entry, keep?.IsInit == true, keepEditor: true);
    }

    /// <summary>Before something that opens another event (the New event window): the edits are written; false = stay.</summary>
    public bool AskBeforeLeaving() => Editor.ConfirmLeave();

    /// <summary>Opens the list on a new event made by <see cref="L2NewEventWindow"/>.</summary>
    public void EventCreated(L2EventBlocks.Entry? entry, string what)
    {
        Main?.NotifyRomModified("Lufia II: " + what);
        L2FlagUses.Invalidate();
        Flags.Clear(); LoadFlags();
        FillEvents(entry, false, keepEditor: false);
        Status = what;
    }

    /// <summary>Takes the picked event out of its list.</summary>
    [RelayCommand]
    private void DeleteEvent()
    {
        if (Rom == null || Main == null || SelectedEvent?.Entry is not { } e) { Status = "Pick an event of the map first (the start-up script and puzzles can't be removed)."; return; }
        var r = System.Windows.MessageBox.Show($"Remove \"{SelectedEvent.Title}\" from the map? Its steps stay in the ROM, unused.", "Lufia II events", System.Windows.MessageBoxButton.OKCancel);
        if (r != System.Windows.MessageBoxResult.OK) return;
        string title = SelectedEvent.Title;
        Editor.Unload();
        L2EventBlocks.Remove(Rom, _map, e);
        Main.NotifyRomModified($"Lufia II: removed \"{title}\" from {CurrentMapName}");
        L2FlagUses.Invalidate();
        FillEvents(null, false, keepEditor: false);
        Status = "Removed.";
    }

    /// <summary>Adds steps at the end of an event (the "continue another event" choice of a new event).</summary>
    public void ContinueEvent(L2EventItem item, IEnumerable<L2Step> added)
    {
        if (item != SelectedEvent)
        {
            if (!Editor.ConfirmLeave()) return;
            SelectedEvent = item;
        }
        Editor.ContinueWith(added);
    }

    // ------------------------------------------------------------------ story flags

    private void LoadFlags()
    {
        if (Rom == null) return;
        var uses = L2FlagUses.Get(Rom);
        foreach (var f in Enumerable.Range(0, 256))
        {
            var u = uses.GetValueOrDefault(f);
            int sets = u?.Count(x => x.StartsWith("set")) ?? 0, tests = u?.Count(x => x.StartsWith("test")) ?? 0;
            string engine = L2Flags.Reserved(f) is { } why ? $" (game: {why})" : "";
            Flags.Add($"{f:X2}  {(u == null ? "unused" : $"set {sets}x, tested {tests}x")}{engine}");
        }
        OnSelectedFlagChanged(SelectedFlag);
    }

    /// <summary>A story flag no event script and no game code uses (for "plays once"), or -1.</summary>
    public int FreeFlag(IEnumerable<int>? avoid = null) => Rom == null ? -1 : L2FlagUses.Free(Rom, avoid);

    partial void OnSelectedFlagChanged(int value)
    {
        if (Rom == null || value < 0) { FlagUsesText = ""; return; }
        string engine = L2Flags.Reserved(value) is { } why ? $"Used by the game's own code: {why}.\n\n" : "";
        FlagUsesText = engine + (L2FlagUses.Get(Rom).TryGetValue(value, out var l) ? string.Join("\n", l) : "No event script uses this flag.");
    }

    protected override bool IsField(string p) => false;
    protected override string? Write() => null;
}
