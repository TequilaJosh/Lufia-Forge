using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LufiaForge.Core;
using LufiaForge.ViewModels;
using System.Collections.ObjectModel;

namespace LufiaForge.Modules.GameData;

/// <summary>
/// Shared state for all Game Data editors: the ROM, name lists used by several editors' dropdowns,
/// and the fixed-length name codec. Names are stored as raw single-byte characters; '@' is the
/// in-game space inside item and monster names.
/// </summary>
public sealed class GameDataContext
{
    private readonly MainViewModel? _mainVm;

    public RomBuffer Rom { get; }

    /// <summary>"XX Name" for all 256 items (index = item id).</summary>
    public ObservableCollection<string> ItemNames { get; } = new();

    /// <summary>"XX Name" for all spells (index = spell id).</summary>
    public ObservableCollection<string> SpellNames { get; } = new();

    /// <summary>Raised after an editor renames items/spells/characters, so other editors refresh.</summary>
    public event Action? NamesChanged;

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

    /// <summary>Call after any write: records it in the history (false when nothing really changed).</summary>
    public bool MarkModified(bool namesChanged = false, string? what = null)
    {
        if (namesChanged)
        {
            RefreshNames();
            NamesChanged?.Invoke();
        }
        return _mainVm?.NotifyRomModified(what ?? "Game data edit (characters / items / shops / spells / monsters)") ?? true;
    }

    // ── Record locators ─────────────────────────────────────────────────────

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

    // ── Name codec ──────────────────────────────────────────────────────────

    public string ReadName(int offset, int length)
    {
        var bytes = Rom.ReadBytes(offset, length);
        var chars = bytes.Select(b => Core.DutchLetters.Show(Rom, b)).ToArray();
        return new string(chars).TrimEnd(' ', '\0');
    }

    /// <summary>Writes a fixed-length name, truncating or padding with <paramref name="pad"/>.</summary>
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
        // Update in place so ComboBoxes bound to the collection keep their selection.
        for (int i = 0; i < count; i++)
        {
            string s = make(i);
            if (i < target.Count) { if (target[i] != s) target[i] = s; }
            else target.Add(s);
        }
        while (target.Count > count) target.RemoveAt(target.Count - 1);
    }
}

/// <summary>Base class for the individual Game Data editors.</summary>
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
        if (Ctx != null) Ctx.NamesChanged -= HandleNamesChanged;
        Ctx = ctx;
        Ctx.NamesChanged += HandleNamesChanged;
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
        HookCollections();
    }

    // ── edits are written by themselves ──
    // Anything typed, ticked or picked marks the editor edited (also rows of its lists); the edits are written when
    // another record is picked and when the project is saved (or Apply to ROM in the top bar is clicked).

    private int _loading;
    private bool _writing;

    /// <summary>The editor's edits are being written by the app (another record picked, the project saved, Apply to
    /// ROM): no questions then (growing the ROM to make room is harmless in a project).</summary>
    protected bool AutoWriting => _writing;

    /// <summary>Something was changed since the record was loaded or written.</summary>
    public bool Edited { get; protected set; }

    /// <summary>Whether this editor has edits that aren't in the ROM yet.</summary>
    public virtual bool HasPendingEdits => Edited && Ctx != null;

    /// <summary>Writes the edits (the editor's Apply). Null, or why they couldn't be written.</summary>
    public virtual string? WritePendingEdits()
    {
        if (!HasPendingEdits || _writing) return null;
        _writing = true;
        string status = Status;
        try
        {
            _changed = false;
            (GetType().GetProperty("ApplyCommand")?.GetValue(this) as System.Windows.Input.ICommand)?.Execute(null);
            if (!_changed) Status = status;   // the same bytes again: keep what the editor said
            Edited = false;
            return null;
        }
        catch (Exception ex) { return ex.Message; }
        finally { _writing = false; }
    }

    private bool _changed;

    /// <summary>The property names that don't count as edits (lists and what the editor shows).</summary>
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
        if (_loading > 0 || _writing || e.PropertyName == null || IsShownOnly(e.PropertyName) || e.PropertyName.StartsWith("Selected")) return;
        Edited = true;
        if (GetType().GetProperty(e.PropertyName)?.GetValue(this) is System.Collections.Specialized.INotifyCollectionChanged) HookCollections();
    }

    /// <summary>Picking another record: the edits of the one shown are written first.</summary>
    protected override void OnPropertyChanging(System.ComponentModel.PropertyChangingEventArgs e)
    {
        if (_loading == 0 && !_writing && e.PropertyName != null && e.PropertyName.StartsWith("Selected") && HasPendingEdits)
            WritePendingEdits();
        base.OnPropertyChanging(e);
    }

    private readonly HashSet<object> _hooked = new(ReferenceEqualityComparer.Instance);

    /// <summary>Listens to the editor's lists and their rows, so changing a row counts as an edit.</summary>
    private void HookCollections()
    {
        foreach (var p in GetType().GetProperties())
        {
            if (p.GetIndexParameters().Length > 0 || IsShownOnly(p.Name)) continue;
            if (!typeof(System.Collections.Specialized.INotifyCollectionChanged).IsAssignableFrom(p.PropertyType)) continue;
            if (p.GetValue(this) is not System.Collections.Specialized.INotifyCollectionChanged list || !_hooked.Add(list)) continue;
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
        if (item is not System.ComponentModel.INotifyPropertyChanged n || !_hooked.Add(n)) return;
        n.PropertyChanged += (_, _) => { if (_loading == 0 && !_writing) Edited = true; };
    }

    /// <summary>Attaches to a reloaded ROM (after undo / redo) keeping the record that was selected: an int
    /// SelectedIndex, or a selected list entry found again by its text.</summary>
    public void Reattach(GameDataContext ctx)
    {
        var props = GetType().GetProperties().Where(p => p.CanRead && p.CanWrite && p.Name.StartsWith("Selected") && p.GetIndexParameters().Length == 0).ToList();
        var kept = props.Select(p => (p, v: p.GetValue(this))).ToList();
        Attach(ctx);
        foreach (var (p, v) in kept)
        {
            try
            {
                if (v is int or bool) { if (!Equals(p.GetValue(this), v)) p.SetValue(this, v); continue; }
                if (v == null) continue;
                string text = v.ToString() ?? "";
                var match = GetType().GetProperties()
                    .Where(q => q.GetIndexParameters().Length == 0 && typeof(System.Collections.IEnumerable).IsAssignableFrom(q.PropertyType) && q.PropertyType != typeof(string))
                    .SelectMany(q => (q.GetValue(this) as System.Collections.IEnumerable)?.Cast<object>() ?? Enumerable.Empty<object>())
                    .FirstOrDefault(o => o != null && p.PropertyType.IsInstanceOfType(o) && o.ToString() == text);
                if (match != null) p.SetValue(this, match);
            }
            catch { }
        }
    }

    /// <summary>Build record lists and load the first record.</summary>
    protected abstract void OnRomLoaded();

    /// <summary>Re-read the currently selected record from the ROM.</summary>
    protected abstract void LoadRecord();

    /// <summary>Re-reads the selected record (what it shows isn't an edit).</summary>
    protected void LoadSelected()
    {
        _loading++;
        try { LoadRecord(); }
        finally { _loading--; }
        Edited = false;
        HookCollections();
    }

    /// <summary>
    /// Renaming replaces entries in the shared name lists, which clears ComboBox selections bound to
    /// them, so every editor re-reads its current record afterwards.
    /// </summary>
    protected virtual void OnNamesChanged() => LoadSelected();

    private void HandleNamesChanged()
    {
        string status = Status;
        OnNamesChanged();
        Status = status;
    }

    [RelayCommand]
    private void Revert()
    {
        if (Ctx == null) return;
        LoadSelected();
        Status = "Put back as the project has it.";
    }

    /// <summary>Called when the editor is shown: picks up changes other tabs made (the Patch Manager adding spells).</summary>
    public virtual void Recheck()
    {
        if (Ctx != null && Ctx.SpellNames.Count != Core.Battle.SpellTablePatch.SpellCount(Ctx.Rom)) Ctx.RefreshNames();
    }

    /// <summary>Runs a write action, reporting success or the error in <see cref="Status"/>.</summary>
    protected void Commit(string what, Action write, bool namesChanged = false)
    {
        if (Ctx == null) return;
        try
        {
            write();
            if (Ctx.MarkModified(namesChanged, what)) { _changed = true; Status = $"{what} saved in the project."; }
            else Status = $"{what}: nothing changed.";
        }
        catch (Exception ex)
        {
            Status = $"Error: {ex.Message}";
        }
    }

    protected static int Clamp(int v, int min, int max) => Math.Max(min, Math.Min(max, v));
}
