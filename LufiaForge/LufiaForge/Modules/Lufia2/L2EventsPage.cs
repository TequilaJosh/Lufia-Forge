using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LufiaForge.Core;
using LufiaForge.Core.Lufia2;

namespace LufiaForge.Modules.Lufia2;

/// <summary>
/// The Events tab: a map's events (talking to characters, stepping on spots, scenes, the start-up script) in a list;
/// the picked one opens in the event editor (<see cref="L2ScriptEditor"/>, the same editor the Maps page pops up and
/// the Cutscenes page uses), with its steps as cards, the stage and the timeline. Also the Flags tab
/// (<see cref="L2FlagsPanel"/>): every kind of flag, where it is used, names and notes, new flags.
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

    /// <summary>The Flags tab.</summary>
    public L2FlagsPanel FlagsPanel { get; }
    /// <summary>The Story characters tab.</summary>
    public L2StoryCharsPanel StoryPanel { get; }
    public LufiaForge.ViewModels.MainViewModel? MainVm => Main;

    public RomBuffer? CurrentRom => Rom;
    public int CurrentMap => _map;
    public string CurrentMapName => _map >= 0 && _map < _maps.Length ? _maps[_map] : "";
    /// <summary>The map's characters (actor id: name, sprite or -1), for the New event window.</summary>
    public IReadOnlyDictionary<int, (string Name, int Sprite)> Characters => Editor.Characters;
    public string Who(int id) => Editor.Characters.TryGetValue(id, out var w) ? w.Name : L2EventCommands.DefaultWho(id);

    public L2EventsPage()
    {
        FlagsPanel = new L2FlagsPanel(this);
        StoryPanel = new L2StoryCharsPanel(this);
        Editor.StoryCharRequested += id => { StoryPanel.ShowChar(id); StoryRequested?.Invoke(id); };
        // (the editor may show another map's event, opened from a "go to a scene" step: then the page's list is read
        // again without picking an event of its own map with the same number, which loaded that one instead)
        Editor.Saved += _ => { FlagsPanel.Refresh(); StoryPanel.Refresh(); if (Editor.Map == _map) FillEvents(Editor.Event?.Entry, Editor.Event?.IsInit == true, keepEditor: true); else FillEvents(null, false, keepEditor: true); MarkRead(); };
        Editor.FlagRequested += f => { FlagsPanel.ShowFlag(L2FlagIndex.Kind.Story, f); FlagRequested?.Invoke(f); };
        Editor.PropertyChanged += (_, e) => { if (e.PropertyName == nameof(L2ScriptEditor.Status)) Status = Editor.Status; };
    }

    /// <summary>Asks the view to show the Flags tab on a flag.</summary>
    public event Action<int>? FlagRequested;
    /// <summary>Asks the view to show the Story characters tab on one.</summary>
    public event Action<int>? StoryRequested;
    /// <summary>Asks the view to show the Events tab (a use picked on the Flags tab).</summary>
    public event Action? EventsRequested;

    protected override IEnumerable<string> ReadNames()
    {
        // a new ROM (another project, or Edit > Undo / Redo): unsaved steps belong to the old one, dropped without asking
        Editor.Unload();
        _maps = Enumerable.Range(0, L2Maps.Count(Rom!)).Select(m => L2ScriptEditor.MapNameOf(Rom!, m)).ToArray();
        return Enumerable.Range(0, L2Maps.Count(Rom!)).Select(m => $"{m:X2}  {_maps[m]}");
    }

    /// <summary>The map list is being put back (leaving the event was refused).</summary>
    private bool _restoring;

    protected override void Load(int index)
    {
        if (_restoring) return;
        // the event open has steps that couldn't be written and the user keeps them: the list goes back to its map
        if (!_skipLeave && _map >= 0 && index != _map && !Editor.ConfirmLeave())
        {
            int back = _map;
            System.Windows.Application.Current?.Dispatcher.BeginInvoke(() => { _restoring = true; SelectedIndex = back; _restoring = false; });
            return;
        }
        _map = index;
        FlagsPanel.Refresh();
        StoryPanel.Refresh();
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
        if (SelectedEvent == null && !keepEditor) { Editor.Unload(); Editor.UseMap(rom, Main!, _map); }
    }

    partial void OnSelectedEventChanged(L2EventItem? value)
    {
        if (Rom == null || Main == null || value == null) return;
        if (!_skipLeave && !Editor.ConfirmLeave())
        {
            // kept: the list shows the event the editor still has (else Remove would take out the other one)
            var shown = Events.FirstOrDefault(e => Editor.Event != null && e.IsInit == Editor.Event.IsInit && e.Entry?.Head == Editor.Event.Entry?.Head && e.Entry?.Id == Editor.Event.Entry?.Id);
            System.Windows.Application.Current?.Dispatcher.BeginInvoke(() => { _skipLeave = true; SelectedEvent = shown; _skipLeave = false; });
            return;
        }
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

    /// <summary>The page is shown: when the ROM changed since it read it (another page, Undo), the story flags, the
    /// event open (unless it has unwritten steps) and the map's list are read again.</summary>
    public override void Shown()
    {
        // maps made or deleted on the Maps page: the list is read again (only with nothing unwritten in the editor:
        // reading the list drops the event open)
        if (Rom != null && L2Maps.Count(Rom) != _maps.Length && !Editor.HasPendingEdits)
        {
            RefreshList(Math.Min(SelectedIndex, L2Maps.Count(Rom) - 1));
            MarkRead();
            return;
        }
        if (!ChangedSinceRead || _map < 0) return;
        L2FlagUses.Invalidate();
        FlagsPanel.Refresh();
        StoryPanel.Refresh();
        Editor.Shown();
        FillEvents(Editor.Event?.Entry, Editor.Event?.IsInit == true, keepEditor: true);
        MarkRead();
    }

    /// <summary>Opens an event (head -1: the start-up script) of a map at the step read from file offset
    /// <paramref name="at"/>, on the Events tab.</summary>
    public void ShowEventAt(int map, int head, int id, int at)
    {
        if (Rom == null) return;
        if (!Editor.ConfirmLeave()) return;
        if (SelectedIndex != map) SelectedIndex = map; else FillEvents(null, false, keepEditor: false);
        var item = head == L2FlagIndex.InitHead ? Events.FirstOrDefault(e => e.IsInit) : Events.FirstOrDefault(e => e.Entry?.Head == head && e.Entry?.Id == id);
        if (item == null) return;
        SelectedEvent = item;
        EventsRequested?.Invoke();
        Editor.GoToStepAt(at);
    }

    /// <summary>The extra story flags went in (the Flags tab): the editor's flag lists are read again.</summary>
    public void FlagsChanged()
    {
        Editor.NamesChanged();
        MarkRead();
    }

    /// <summary>Reads the map's list again (another page wrote the map's events).</summary>
    public void Reload()
    {
        if (Rom == null || _map < 0) return;
        var keep = SelectedEvent;
        FillEvents(keep?.Entry, keep?.IsInit == true, keepEditor: true);
    }

    /// <summary>Before something that opens another event (the New event window): the edits are written; false = stay.</summary>
    public bool AskBeforeLeaving()
    {
        if (!Editor.ConfirmLeave()) return false;
        // the editor shows another map's event (opened from a scene step): the New event window lists this map's
        // characters, so the editor reads this map again (its edits were just written)
        if (Rom != null && Main != null && _map >= 0 && Editor.Map != _map) { Editor.Unload(); Editor.UseMap(Rom, Main, _map); }
        return true;
    }

    /// <summary>Opens the list on a new event made by <see cref="L2NewEventWindow"/>.</summary>
    public void EventCreated(L2EventBlocks.Entry? entry, string what)
    {
        Main?.NotifyRomModified("Lufia II: " + what);
        L2FlagUses.Invalidate();
        FlagsPanel.Refresh();
        FillEvents(entry, false, keepEditor: false);
        Status = what;
        MarkRead();
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
        MarkRead();
    }

    /// <summary>Adds steps at the end of an event (the "continue another event" choice of a new event).</summary>
    public void ContinueEvent(L2EventItem item, IEnumerable<L2Step> added)
    {
        // the event picked in the window must be the one open (not another map's the editor went to from a scene step)
        bool open = Editor.Map == _map && Editor.Event != null && Editor.Event.IsInit == item.IsInit &&
                    Editor.Event.Entry?.Head == item.Entry?.Head && Editor.Event.Entry?.Id == item.Entry?.Id;
        if (item != SelectedEvent || !open)
        {
            if (!Editor.ConfirmLeave()) return;
            if (item == SelectedEvent && Rom != null && Main != null) Editor.Load(Rom, Main, _map, item, L2ScriptEditor.TitleOf(Rom, _map, item));
            else SelectedEvent = item;
        }
        Editor.ContinueWith(added);
    }

    // ------------------------------------------------------------------ story flags

    /// <summary>A story flag no event script and no game code uses (for "plays once"), or -1.</summary>
    public int FreeFlag(IEnumerable<int>? avoid = null) => Rom == null ? -1 : L2FlagUses.Free(Rom, avoid);

    protected override bool IsField(string p) => false;
    protected override string? Write() => null;
}
