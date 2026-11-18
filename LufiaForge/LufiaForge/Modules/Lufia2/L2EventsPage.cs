using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LufiaForge.Core;
using LufiaForge.Core.Lufia2;

namespace LufiaForge.Modules.Lufia2;

// the Events tab: a list of a map's events (talking to characters, stepping on spots, scenes, the start-up script).
// the picked one opens in the event editor (L2ScriptEditor, the same one the Maps page pops up and the Cutscenes page
// uses) with its step cards, the stage and the timeline. also holds the Flags tab (L2FlagsPanel): every kind of flag,
// where it's used, names and notes, new flags
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

    public L2FlagsPanel FlagsPanel { get; }
    public L2StoryCharsPanel StoryPanel { get; }
    public LufiaForge.ViewModels.MainViewModel? MainVm => Main;

    public RomBuffer? CurrentRom => Rom;
    public int CurrentMap => _map;
    public string CurrentMapName => _map >= 0 && _map < _maps.Length ? _maps[_map] : "";
    // the map's characters (actor id -> name, sprite or -1), for the New event window
    public IReadOnlyDictionary<int, (string Name, int Sprite)> Characters => Editor.Characters;
    public string Who(int id) => Editor.Characters.TryGetValue(id, out var character) ? character.Name : L2EventCommands.DefaultWho(id);

    public L2EventsPage()
    {
        FlagsPanel = new L2FlagsPanel(this);
        StoryPanel = new L2StoryCharsPanel(this);
        Editor.StoryCharRequested += id => { StoryPanel.ShowChar(id); StoryRequested?.Invoke(id); };
        // (the editor might be showing another map's event, opened from a "go to a scene" step. then the page's list is
        // read again without picking the event of its own map with the same number, which would load that one instead)
        Editor.Saved += _ => { FlagsPanel.Refresh(); StoryPanel.Refresh(); if (Editor.Map == _map) FillEvents(Editor.Event?.Entry, Editor.Event?.IsInit == true, keepEditor: true); else FillEvents(null, false, keepEditor: true); MarkRead(); };
        Editor.FlagRequested += flag => { FlagsPanel.ShowFlag(L2FlagIndex.Kind.Story, flag); FlagRequested?.Invoke(flag); };
        Editor.PropertyChanged += (_, e) => { if (e.PropertyName == nameof(L2ScriptEditor.Status)) Status = Editor.Status; };
    }

    // asks the view to show the Flags tab on a flag
    public event Action<int>? FlagRequested;
    // asks the view to show the Story characters tab on one
    public event Action<int>? StoryRequested;
    // asks the view to show the Events tab (a use picked on the Flags tab)
    public event Action? EventsRequested;

    protected override IEnumerable<string> ReadNames()
    {
        // a new ROM (another project, or Edit > Undo / Redo): unsaved steps belong to the old one, dropped without asking
        Editor.Unload();
        _maps = Enumerable.Range(0, L2Maps.Count(Rom!)).Select(m => L2ScriptEditor.MapNameOf(Rom!, m)).ToArray();
        return Enumerable.Range(0, L2Maps.Count(Rom!)).Select(m => $"{m:X2}  {_maps[m]}");
    }

    // true while the map list is being put back (leaving the event was refused)
    private bool _restoring;

    protected override void Load(int index)
    {
        if (_restoring) return;
        // the open event has steps that couldn't be written and the user keeps them: the list goes back to its map
        if (!_skipLeave && _map >= 0 && index != _map && !Editor.ConfirmLeave())
        {
            int previousMap = _map;
            System.Windows.Application.Current?.Dispatcher.BeginInvoke(() => { _restoring = true; SelectedIndex = previousMap; _restoring = false; });
            return;
        }
        _map = index;
        FlagsPanel.Refresh();
        StoryPanel.Refresh();
        FillEvents(null, false, keepEditor: false);
    }

    private void FillEvents(L2EventBlocks.Entry? select, bool selectInit, bool keepEditor)
    {
        var rom = Rom!;
        Events.Clear();
        foreach (var item in L2ScriptEditor.ItemsOf(rom, _map)) Events.Add(item);
        int count = Events.Count(e => e.Entry != null);
        int block = L2Events.Block(rom, _map);
        Summary = $"{count} events{(block < 0 ? " (this map has no event block)" : "")}. Pick one to edit it; \"New event\" adds one.";
        _skipLeave = true;
        SelectedEvent = selectInit ? Events[0] : select != null ? Events.FirstOrDefault(x => x.Entry?.Head == select.Head && x.Entry?.Id == select.Id) : null;
        _skipLeave = false;
        if (SelectedEvent == null && !keepEditor) { Editor.Unload(); Editor.UseMap(rom, Main!, _map); }
    }

    partial void OnSelectedEventChanged(L2EventItem? value)
    {
        if (Rom == null || Main == null || value == null) return;
        if (!_skipLeave && !Editor.ConfirmLeave())
        {
            // kept: the list shows the event the editor still has (otherwise Remove would take out the other one)
            var shown = Events.FirstOrDefault(e => Editor.Event != null && e.IsInit == Editor.Event.IsInit && e.Entry?.Head == Editor.Event.Entry?.Head && e.Entry?.Id == Editor.Event.Entry?.Id);
            System.Windows.Application.Current?.Dispatcher.BeginInvoke(() => { _skipLeave = true; SelectedEvent = shown; _skipLeave = false; });
            return;
        }
        // the editor already shows it (the list was read again after a write)
        if (Editor.Event != null && Editor.Map == _map && Editor.Event.IsInit == value.IsInit && Editor.Event.Title == value.Title
            && (Editor.Event.Entry == null) == (value.Entry == null) && (value.Entry == null || Editor.Event.Entry!.Head == value.Entry.Head && Editor.Event.Entry.Id == value.Entry.Id)
            && !Editor.IsDirty && _skipLeave) return;
        Editor.Load(Rom, Main, _map, value, L2ScriptEditor.TitleOf(Rom, _map, value));
    }

    // shows one event of a map (from another page)
    public void ShowEvent(int map, int head, int id)
    {
        if (Rom == null) return;
        if (!Editor.ConfirmLeave()) return;
        if (SelectedIndex != map) SelectedIndex = map; else FillEvents(null, false, keepEditor: false);
        var item = Events.FirstOrDefault(e => e.Entry?.Head == head && e.Entry?.Id == id);
        if (item != null) SelectedEvent = item;
    }

    // the page is shown: if the ROM changed since we read it (another page, Undo), the story flags, the open event
    // (unless it has unwritten steps) and the map's list get read again
    public override void Shown()
    {
        // maps made or deleted on the Maps page: read the list again (only when nothing is unwritten in the editor,
        // reading the list drops the open event)
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

    // opens an event of a map (head -1 = the start-up script) on the Events tab, at the step read from file offset stepOffset
    public void ShowEventAt(int map, int head, int id, int stepOffset)
    {
        if (Rom == null) return;
        if (!Editor.ConfirmLeave()) return;
        if (SelectedIndex != map) SelectedIndex = map; else FillEvents(null, false, keepEditor: false);
        var item = head == L2FlagIndex.InitHead ? Events.FirstOrDefault(e => e.IsInit) : Events.FirstOrDefault(e => e.Entry?.Head == head && e.Entry?.Id == id);
        if (item == null) return;
        SelectedEvent = item;
        EventsRequested?.Invoke();
        Editor.GoToStepAt(stepOffset);
    }

    // the extra story flags went in (the Flags tab): the editor reads its flag lists again
    public void FlagsChanged()
    {
        Editor.NamesChanged();
        MarkRead();
    }

    // reads the map's list again (another page wrote the map's events)
    public void Reload()
    {
        if (Rom == null || _map < 0) return;
        var kept = SelectedEvent;
        FillEvents(kept?.Entry, kept?.IsInit == true, keepEditor: true);
    }

    // before something that opens another event (the New event window): the edits get written. false = stay
    public bool AskBeforeLeaving()
    {
        if (!Editor.ConfirmLeave()) return false;
        // if the editor shows another map's event (opened from a scene step): the New event window lists this map's
        // characters, so the editor reads this map again (its edits were just written)
        if (Rom != null && Main != null && _map >= 0 && Editor.Map != _map) { Editor.Unload(); Editor.UseMap(Rom, Main, _map); }
        return true;
    }

    // opens the list on a new event made by L2NewEventWindow
    public void EventCreated(L2EventBlocks.Entry? entry, string what)
    {
        Main?.NotifyRomModified("Lufia II: " + what);
        L2FlagUses.Invalidate();
        FlagsPanel.Refresh();
        FillEvents(entry, false, keepEditor: false);
        Status = what;
        MarkRead();
    }

    // takes the picked event out of its list
    [RelayCommand]
    private void DeleteEvent()
    {
        if (Rom == null || Main == null || SelectedEvent?.Entry is not { } entry) { Status = "Pick an event of the map first (the start-up script and puzzles can't be removed)."; return; }
        var answer = System.Windows.MessageBox.Show($"Remove \"{SelectedEvent.Title}\" from the map? Its steps stay in the ROM, unused.", "Lufia II events", System.Windows.MessageBoxButton.OKCancel);
        if (answer != System.Windows.MessageBoxResult.OK) return;
        string title = SelectedEvent.Title;
        Editor.Unload();
        L2EventBlocks.Remove(Rom, _map, entry);
        Main.NotifyRomModified($"Lufia II: removed \"{title}\" from {CurrentMapName}");
        L2FlagUses.Invalidate();
        FillEvents(null, false, keepEditor: false);
        Status = "Removed.";
        MarkRead();
    }

    // adds steps at the end of an event (the "continue another event" choice of a new event)
    public void ContinueEvent(L2EventItem item, IEnumerable<L2Step> added)
    {
        // the event picked in the window has to be the open one (not some other map's that the editor went to from a scene step)
        bool isOpen = Editor.Map == _map && Editor.Event != null && Editor.Event.IsInit == item.IsInit &&
                      Editor.Event.Entry?.Head == item.Entry?.Head && Editor.Event.Entry?.Id == item.Entry?.Id;
        if (item != SelectedEvent || !isOpen)
        {
            if (!Editor.ConfirmLeave()) return;
            if (item == SelectedEvent && Rom != null && Main != null) Editor.Load(Rom, Main, _map, item, L2ScriptEditor.TitleOf(Rom, _map, item));
            else SelectedEvent = item;
        }
        Editor.ContinueWith(added);
    }

    // story flags

    // a story flag that no event script and no game code uses (for "plays once"), or -1
    public int FreeFlag(IEnumerable<int>? avoid = null) => Rom == null ? -1 : L2FlagUses.Free(Rom, avoid);

    protected override bool IsField(string p) => false;
    protected override string? Write() => null;
}
