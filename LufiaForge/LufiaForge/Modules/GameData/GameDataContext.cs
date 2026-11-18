using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LufiaForge.Core;
using LufiaForge.ViewModels;
using System.Collections.ObjectModel;

namespace LufiaForge.Modules.GameData;

// what all the Game Data editors share: the ROM, the name lists several editors' dropdowns use,
// and reading/writing the fixed length names. names are raw single byte characters, '@' is the
// in-game space inside item and monster names
public sealed class GameDataContext
{
    private readonly MainViewModel? _mainVm;

    public RomBuffer Rom { get; }

    // "XX Name" for all 256 items (index = item id)
    public ObservableCollection<string> ItemNames { get; } = new();

    // "XX Name" for all spells (index = spell id)
    public ObservableCollection<string> SpellNames { get; } = new();

    // fires after an editor renames items/spells/characters, so the other editors refresh
    public event Action? NamesChanged;

    // fires before the name lists change so editors write their pending edits first (replacing a list entry
    // clears the choice boxes showing it, and refreshing re-reads the record)
    public event Action? NamesChanging;

    public GameDataContext(RomBuffer rom, MainViewModel? mainVm)
    {
        Rom     = rom;
        _mainVm = mainVm;
        RefreshNames();
    }

    public void RefreshNames()
    {
        Fill(ItemNames, GameDataOffsets.ItemCount,
             i => $"{i:X2} {ReadName(ItemOffset(i), GameDataOffsets.ItemNameLen)}");
        Fill(SpellNames, Core.Battle.SpellTablePatch.SpellCount(Rom),
             i => $"{i:X2} {ReadName(Core.Battle.SpellTablePatch.SpellOffset(Rom, i), GameDataOffsets.SpellNameLen)}");
    }

    // call after any write, it records it in the history. false when nothing really changed
    public bool MarkModified(bool namesChanged = false, string? what = null)
    {
        // the history step goes first, since other editors writing their edits below make steps of their own
        bool changed = _mainVm?.NotifyRomModified(what ?? "Game data edit (characters / items / shops / spells / monsters)") ?? true;
        if (namesChanged) NamesUpdate();
        return changed;
    }

    private void NamesUpdate()
    {
        NamesChanging?.Invoke();
        RefreshNames();
        NamesChanged?.Invoke();
    }

    // reads the names again if the ROM's don't match the lists (another tab renamed or added items or
    // spells, like the Patch Manager's bows or spells). the editors get told the same as after a rename
    public void RecheckNames()
    {
        int spellCount = Core.Battle.SpellTablePatch.SpellCount(Rom);
        bool same = ItemNames.Count == GameDataOffsets.ItemCount && SpellNames.Count == spellCount &&
                    Enumerable.Range(0, GameDataOffsets.ItemCount).All(i => ItemNames[i] == $"{i:X2} {ReadName(ItemOffset(i), GameDataOffsets.ItemNameLen)}") &&
                    Enumerable.Range(0, spellCount).All(i => SpellNames[i] == $"{i:X2} {ReadName(Core.Battle.SpellTablePatch.SpellOffset(Rom, i), GameDataOffsets.SpellNameLen)}");
        if (!same) NamesUpdate();
    }

    // finding records

    public int ItemOffset(int id) => Core.Battle.ItemTable.RecordOffset(Rom, id);

    public int MonsterOffset(int id) => Core.Battle.MonsterTable.RecordOffset(Rom, id);
    public int MonsterCount => Core.Battle.MonsterTable.Count(Rom);

    public int ShopOffset(int id) => Rom.ReadUInt16Le(GameDataOffsets.ShopPointerTable + id * 2);

    public string CharacterName(int slot)
    {
        if (slot == 0) return "Hero";
        return ReadName(GameDataOffsets.CharacterNames + (slot - 1) * GameDataOffsets.CharacterNameLen,
                        GameDataOffsets.CharacterNameLen);
    }

    // reading and writing names

    public string ReadName(int offset, int length)
    {
        var bytes = Rom.ReadBytes(offset, length);
        var chars = bytes.Select(b => Core.DutchLetters.Show(Rom, b)).ToArray();
        return new string(chars).TrimEnd(' ', '\0');
    }

    // writes a fixed length name, cut off or padded out with pad
    public void WriteName(int offset, int length, string name, byte pad = (byte)' ')
    {
        var bytes = new byte[length];
        for (int i = 0; i < length; i++)
        {
            if (i < name.Length)
                bytes[i] = Core.DutchLetters.Code(Rom, name[i]) ?? (byte)'?';
            else
                bytes[i] = pad;
        }
        Rom.WriteBytes(offset, bytes);
    }

    private static void Fill(ObservableCollection<string> target, int count, Func<int, string> make)
    {
        // update in place so ComboBoxes bound to the collection keep their selection
        for (int i = 0; i < count; i++)
        {
            string entry = make(i);
            if (i < target.Count) { if (target[i] != entry) target[i] = entry; }
            else target.Add(entry);
        }
        while (target.Count > count) target.RemoveAt(target.Count - 1);
    }
}

// base class for the Game Data editors
public abstract partial class GameDataEditorBase : ObservableObject
{
    protected GameDataContext? Ctx { get; private set; }

    [ObservableProperty] private bool   _isLoaded;
    [ObservableProperty] private string _status = "Open a ROM to edit.";

    public ObservableCollection<string> ItemNames  => Ctx?.ItemNames  ?? EmptyList;
    public ObservableCollection<string> SpellNames => Ctx?.SpellNames ?? EmptyList;

    private static readonly ObservableCollection<string> EmptyList = new();

    public void Attach(GameDataContext ctx)
    {
        if (Ctx != null) { Ctx.NamesChanged -= HandleNamesChanged; Ctx.NamesChanging -= HandleNamesChanging; }
        Ctx = ctx;
        Ctx.NamesChanged += HandleNamesChanged;
        Ctx.NamesChanging += HandleNamesChanging;
        _loading++;
        try
        {
            OnPropertyChanged(nameof(ItemNames));
            OnPropertyChanged(nameof(SpellNames));
            IsLoaded = true;
            Status   = "";
            OnRomLoaded();
        }
        finally { _loading--; }
        Edited = false;
        _readVersion = Ctx.Rom.Version;
        HookCollections();
    }

    // edits get written on their own
    // anything typed, ticked or picked marks the editor edited (rows of its lists too). the edits get written when
    // another record is picked and when the project is saved (or Apply to ROM in the top bar is clicked)

    private int _loading;
    private bool _writing;

    // the app is writing the editor's edits (another record picked, the project saved, Apply to ROM), so no
    // questions then (growing the ROM to make room is harmless in a project)
    protected bool AutoWriting => _writing;

    // something changed since the record was loaded or written
    public bool Edited { get; protected set; }

    // true if this editor has edits that aren't in the ROM yet
    public virtual bool HasPendingEdits => Edited && Ctx != null;

    // writes the edits (the editor's Apply). returns null, or why they couldn't be written. then they stay pending
    // (and on screen) and Status says why
    public virtual string? WritePendingEdits()
    {
        if (!HasPendingEdits || _writing) return null;
        _writing = true;
        string oldStatus = Status;
        try
        {
            _changed = false;
            _failed = false;
            (GetType().GetProperty("ApplyCommand")?.GetValue(this) as System.Windows.Input.ICommand)?.Execute(null);
            if (_failed) { Edited = true; return $"{PageName}: {Status}"; }
            if (!_changed) Status = oldStatus;   // same bytes again, keep what the editor said
            Edited = false;
            return null;
        }
        catch (Exception ex) { Edited = true; Status = $"Error: {ex.Message}"; return $"{PageName}: {ex.Message}"; }
        finally { _writing = false; }
    }

    private bool _changed, _failed;

    // the page's name in messages ("Monster" for the Monster editor)
    protected virtual string PageName => GetType().Name.Replace("EditorViewModel", "").Replace("ViewModel", "");

    // the edits can't be written as they are (a value out of range, a write that failed). says why, and
    // they stay pending instead of counting as written
    protected void Refuse(string why)
    {
        Status = why;
        _failed = true;
    }

    // property names that don't count as edits (lists and stuff the editor just shows)
    private static readonly HashSet<string> NotEdits = new()
    {
        nameof(Status), nameof(IsLoaded), nameof(ItemNames), nameof(SpellNames), "TabIndex",
        "Animating", "Busy", "Canvas", "Counter", "CurrentChannel", "CurrentTool", "CursorTick", "CursorText", "FlashColour", "FollowPlayhead",
        "Loaded", "LoopAllChannels", "PaletteMode", "Picture", "PlayheadTick", "Preview", "PreviewNotes", "RawMode", "RawTiles",
        "ShowAllZones", "ShowBoth", "ShowGrid", "SnapIndex", "StepFrames", "TilesPerRow", "ToolIndex", "Zoom", "ZonesDirty", "Dirty",
        "BrushSize", "EraseZones", "DepthIndex", "LengthIndex", "SelLength", "SelPitch", "SelStart", "SelVelocity", "NewVelocity",
        "AddChoice", "NewSpellText", "Cycle", "FrameCount", "FrameHeight", "FrameWidth", "WorksLike", "Summary", "Warning", "Info",
        "EncounterText", "HoverText", "InfoText", "LimitText", "LoopText", "OffsetText", "PaletteOffsetText", "RawOffsetText",
        "RegionText", "RoomText", "SizeText", "TeleportText", "WhereText", "WorldStatus", "Count",
        "MonsterCount", "MonsterRangeIndex", "MonsterChases", "MonsterClickModeIndex", "MonsterPathModeIndex", "SwitchActionIndex", "EncounterTabIndex", "MiddleColumnSpan", "MonsterGroupText", "MonsterMapPixelWidth", "MonsterMapPixelHeight",
    };

    private static bool IsShownOnly(string name) =>
        NotEdits.Contains(name) || name.StartsWith("Is") || name.StartsWith("Can") ||
        name.EndsWith("Options") || name.EndsWith("Image") || name.EndsWith("Sheet") || name.EndsWith("Info") ||
        name.EndsWith("Note") || name.EndsWith("Hint");

    protected override void OnPropertyChanged(System.ComponentModel.PropertyChangedEventArgs e)
    {
        base.OnPropertyChanged(e);
        if (_hold is { } hold && e.PropertyName == hold.Name)
        {
            // the record's edits couldn't be written, so go back to it (its edits are still on screen)
            if (_reverting) return;
            _reverting = true;
            try { GetType().GetProperty(hold.Name)?.SetValue(this, hold.Old); }
            catch { }
            finally { _reverting = false; _hold = null; }
            // a list that set the selection takes the old one back once its own change is done
            System.Windows.Application.Current?.Dispatcher.BeginInvoke(() => base.OnPropertyChanged(e), System.Windows.Threading.DispatcherPriority.Background);
            return;
        }
        if (_loading > 0 || _writing || e.PropertyName == null || IsShownOnly(e.PropertyName) || e.PropertyName.StartsWith("Selected")) return;
        Edited = true;
        if (GetType().GetProperty(e.PropertyName)?.GetValue(this) is System.Collections.Specialized.INotifyCollectionChanged) HookCollections();
    }

    // picking another record writes the edits of the one showing first. if they can't be written, the
    // record stays picked (see Holding) with its edits, and the reason goes in Status
    protected override void OnPropertyChanging(System.ComponentModel.PropertyChangingEventArgs e)
    {
        if (_loading == 0 && !_writing && _hold == null && e.PropertyName != null && e.PropertyName.StartsWith("Selected") && HasPendingEdits)
        {
            var property = GetType().GetProperty(e.PropertyName);
            object? old = property?.CanWrite == true ? property.GetValue(this) : null;
            if (WritePendingEdits() != null && property?.CanWrite == true) _hold = (e.PropertyName, old);
        }
        base.OnPropertyChanging(e);
    }

    private (string Name, object? Old)? _hold;
    private bool _reverting;

    // another record got picked but the showing one's edits couldn't be written. the pick gets taken back, so
    // nothing should load the other record meanwhile
    protected bool Holding => _hold != null;

    private readonly HashSet<object> _hooked = new(ReferenceEqualityComparer.Instance);

    // listens to the editor's lists and their rows, so changing a row counts as an edit
    private void HookCollections()
    {
        foreach (var property in GetType().GetProperties())
        {
            if (property.GetIndexParameters().Length > 0 || IsShownOnly(property.Name)) continue;
            if (!typeof(System.Collections.Specialized.INotifyCollectionChanged).IsAssignableFrom(property.PropertyType)) continue;
            if (property.GetValue(this) is not System.Collections.Specialized.INotifyCollectionChanged list || !_hooked.Add(list)) continue;
            list.CollectionChanged += (_, args) =>
            {
                if (args.NewItems != null) foreach (var item in args.NewItems) HookItem(item);
                if (_loading == 0 && !_writing) Edited = true;
            };
            if (list is System.Collections.IEnumerable items) foreach (var item in items) HookItem(item);
        }
    }

    private void HookItem(object? item)
    {
        if (item is not System.ComponentModel.INotifyPropertyChanged notifier || !_hooked.Add(notifier)) return;
        notifier.PropertyChanged += (_, _) => { if (_loading == 0 && !_writing) Edited = true; };
    }

    // attaches to a reloaded ROM (after undo / redo) and keeps the record that was selected: an int
    // SelectedIndex, or a selected list entry found again by its key (SelectionKey)
    public void Reattach(GameDataContext ctx)
    {
        var selectedProps = GetType().GetProperties().Where(p => p.CanRead && p.CanWrite && p.Name.StartsWith("Selected") && p.GetIndexParameters().Length == 0).ToList();
        var kept = selectedProps.Select(p => (p, v: p.GetValue(this))).ToList();
        Attach(ctx);
        foreach (var (property, value) in kept)
        {
            try
            {
                if (value is int or bool) { if (!Equals(property.GetValue(this), value)) property.SetValue(this, value); continue; }
                if (value == null || SelectionKey(value) is not { } key) continue;   // no key, so the entry OnRomLoaded picked stays
                var match = GetType().GetProperties()
                    .Where(q => q.GetIndexParameters().Length == 0 && typeof(System.Collections.IEnumerable).IsAssignableFrom(q.PropertyType) && q.PropertyType != typeof(string))
                    .SelectMany(q => (q.GetValue(this) as System.Collections.IEnumerable)?.Cast<object>() ?? Enumerable.Empty<object>())
                    .FirstOrDefault(o => o != null && property.PropertyType.IsInstanceOfType(o) && Equals(SelectionKey(o), key));
                if (match != null && !ReferenceEquals(match, property.GetValue(this))) property.SetValue(this, match);
            }
            catch { }
        }
    }

    // what identifies a list entry across reloads: its number (Number, Resource, Index), else its Name,
    // else its own text (if the type writes one). null when it has none, then it isn't looked for
    protected virtual object? SelectionKey(object entry)
    {
        var type = entry.GetType();
        foreach (var name in new[] { "Number", "Resource", "Index", "Name" })
            if (type.GetProperty(name) is { } property && property.GetIndexParameters().Length == 0 && property.GetValue(entry) is { } value and (int or string))
                return (name, value);
        return type.GetMethod("ToString", Type.EmptyTypes)?.DeclaringType != typeof(object) ? entry.ToString() : null;
    }

    // build the record lists and load the first record
    protected abstract void OnRomLoaded();

    // re-read the selected record from the ROM
    protected abstract void LoadRecord();

    // re-reads the selected record (what it shows isn't an edit)
    protected void LoadSelected()
    {
        if (_hold != null) return;   // the pick is being taken back, the record's edits stay
        _loading++;
        try { LoadRecord(); }
        finally { _loading--; }
        Edited = false;
        if (Ctx != null) _readVersion = Ctx.Rom.Version;
        HookCollections();
    }

    // runs code that changes the editor's lists and values without it counting as an edit (or writing
    // the edits when a list drops its selection)
    protected void Quietly(Action action)
    {
        _loading++;
        try { action(); }
        finally { _loading--; }
    }

    // renaming replaces entries in the shared name lists, which clears the ComboBox selections bound to
    // them, so every editor re-reads its current record afterwards
    protected virtual void OnNamesChanged() => LoadSelected();

    // another page is renaming, so this page's own edits go in first (re-reading the record afterwards would
    // throw them away)
    private void HandleNamesChanging()
    {
        if (HasPendingEdits && !_writing) WritePendingEdits();
    }

    private void HandleNamesChanged()
    {
        if (HasPendingEdits && !_writing) return;   // they couldn't be written, so they stay as typed and we don't re-read
        string oldStatus = Status;
        OnNamesChanged();
        Status = oldStatus;
    }

    [RelayCommand]
    private void Revert()
    {
        if (Ctx == null) return;
        LoadSelected();
        Status = "Put back as the project has it.";
    }

    // the ROM's Core.RomBuffer.Version when this page last read it
    private int _readVersion;

    // called when the editor shows. picks up changes other tabs made (the Patch Manager adding spells or
    // patches, another page writing the same bytes). with no edits of its own pending, a page reads the ROM again
    // (keeping the record picked) if it changed since the page read it
    public virtual void Recheck()
    {
        if (Ctx == null || Ctx.Rom.Version == _readVersion) return;
        Ctx.RecheckNames();
        if (HasPendingEdits) return;
        string oldStatus = Status;
        Reattach(Ctx);
        Status = oldStatus;
    }

    // runs a write and puts the result or the error in Status. false when it failed
    // (then the edits stay pending)
    protected bool Commit(string what, Action write, bool namesChanged = false)
    {
        if (Ctx == null) return false;
        int before = Ctx.Rom.Version;
        // a write that fails partway (a patch put in, then "no room" for the record) gets taken back whole. otherwise
        // its bytes would stay in the ROM with no Undo step and end up in the next change's
        var backup = Ctx.Rom.ToArray();
        try
        {
            write();
            // this page's own write isn't a change to read again, but one by another page meanwhile still is
            if (before == _readVersion) _readVersion = Ctx.Rom.Version;
            if (Ctx.MarkModified(namesChanged, what)) { _changed = true; Status = $"{what} saved in the project."; }
            else Status = $"{what}: nothing changed.";
            return true;
        }
        catch (Exception ex)
        {
            if (!Ctx.Rom.SameAs(backup)) Ctx.Rom.ReplaceAll(backup);
            Refuse($"Error: {ex.Message}");
            return false;
        }
    }

    protected static int Clamp(int value, int min, int max) => Math.Max(min, Math.Min(max, value));
}
