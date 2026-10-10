using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LufiaForge.Core.Audio;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Windows.Threading;

namespace LufiaForge.Modules.GameData;

/// <summary>A note in the editor: start and length in ticks (48 per quarter note), MIDI-style pitch, velocity 1-127.</summary>
public sealed class NoteItem
{
    public int Tick, Length, Pitch, Velocity;
    public NoteItem Clone() => (NoteItem)MemberwiseClone();
    public int End => Tick + Length;
}

/// <summary>One of the 8 game channels: its notes, its other events (instrument, volume, pan, effects) and its loop.</summary>
public sealed class EditChannel
{
    public List<NoteItem> Notes = new();
    public List<SongCodec.Event> Others = new();
    /// <summary>Where the channel jumps back to, and where it does; null = no repeat.</summary>
    public int? LoopStart, LoopEnd;
    /// <summary>Where the song stops (instead of repeating).</summary>
    public int? EndTick;
    public bool Drum;
    public bool Used => Notes.Count > 0 || Others.Count > 0;
    public EditChannel Clone() => new()
    {
        Notes = Notes.Select(n => n.Clone()).ToList(), Others = Others.ToList(),
        LoopStart = LoopStart, LoopEnd = LoopEnd, EndTick = EndTick, Drum = Drum,
    };
}

/// <summary>A channel button above the piano roll.</summary>
public partial class ChannelTab : ObservableObject
{
    [ObservableProperty] private string _label = "";
}

/// <summary>A row of the channel-events list.</summary>
public sealed record OtherRow(SongCodec.Event Event, string When, string What);

/// <summary>
/// The Music tab's note editor: the song decoded into notes per channel (<see cref="SongCodec"/>), edited on a piano
/// roll (<see cref="Views.PianoRoll"/>), encoded again when written to the ROM.
/// </summary>
public partial class NoteEditorViewModel : ObservableObject
{
    public const int TicksPerBeat = SongCodec.TicksPerQuarter, TicksPerBar = TicksPerBeat * 4;

    private readonly MusicEditorViewModel _owner;
    public NoteEditorViewModel(MusicEditorViewModel owner)
    {
        _owner = owner;
        _sizeTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(300) };
        _sizeTimer.Tick += (_, _) => { _sizeTimer.Stop(); UpdateSize(); };
    }

    public EditChannel[] Channels { get; private set; } = Enumerable.Range(0, 8).Select(_ => new EditChannel()).ToArray();
    public EditChannel Current => Channels[Math.Clamp(CurrentChannel, 0, 7)];
    public HashSet<NoteItem> Selected { get; } = new();

    /// <summary>Raised whenever what the piano roll shows changes.</summary>
    public event Action? Changed;

    [ObservableProperty] private bool _loaded;
    [ObservableProperty] private int _currentChannel;
    [ObservableProperty] private int _tempo = 83;
    [ObservableProperty] private string _bpmText = "";
    [ObservableProperty] private string _sizeText = "";
    [ObservableProperty] private bool _dirty;
    [ObservableProperty] private int _cursorTick;
    [ObservableProperty] private string _cursorText = "1.1.00";
    /// <summary>Where playback is (ticks), -1 when not playing.</summary>
    [ObservableProperty] private double _playheadTick = -1;
    [ObservableProperty] private bool _followPlayhead = true;
    [ObservableProperty] private bool _loopAllChannels = true;
    [ObservableProperty] private bool _previewNotes = true;

    // snap and new-note settings
    public static readonly (string Label, int Ticks)[] SnapChoices =
    {
        ("1 bar", 192), ("1/2", 96), ("1/4", 48), ("1/8", 24), ("1/16", 12), ("1/32", 6),
        ("1/4 triplet", 32), ("1/8 triplet", 16), ("1/16 triplet", 8), ("off", 1),
    };
    public IEnumerable<string> SnapLabels => SnapChoices.Select(c => c.Label);
    [ObservableProperty] private int _snapIndex = 4;
    public int Snap => SnapChoices[Math.Clamp(SnapIndex, 0, SnapChoices.Length - 1)].Ticks;
    public IEnumerable<string> LengthLabels => SnapChoices.Take(9).Select(c => c.Label);
    [ObservableProperty] private int _lengthIndex = 3;
    public int NewLength { get => SnapChoices[Math.Clamp(LengthIndex, 0, 8)].Ticks; }
    /// <summary>Length of the last note drawn or resized (new notes get it when it isn't one of the choices).</summary>
    public int LastLength = 24;
    [ObservableProperty] private int _newVelocity = 100;

    public ObservableCollection<ChannelTab> ChannelLabels { get; } = new();

    // ── loading ─────────────────────────────────────────────────────────────

    public void Load(byte[] songData)
    {
        var song = SongCodec.Decode(songData);
        Loaded = false;
        Tempo = song.Tempo;
        for (int c = 0; c < 8; c++) Channels[c] = FromEvents(song.Channels[c], song.Drum[c]);
        Selected.Clear();
        _undo.Clear(); _redo.Clear();
        CursorTick = 0;
        Dirty = false;
        Loaded = true;
        if (!Current.Used) CurrentChannel = Enumerable.Range(0, 8).FirstOrDefault(c => Channels[c].Used);
        Refresh();
        UpdateSize();
    }

    private static EditChannel FromEvents(List<SongCodec.Event> ev, bool drum)
    {
        var ch = new EditChannel { Drum = drum };
        var end = ev.LastOrDefault(e => e.Kind == SongCodec.Kind.LoopEnd);
        if (end != null)
        {
            ch.LoopEnd = end.Tick;
            var start = ev.Take(ev.IndexOf(end)).LastOrDefault(e => e.Kind == SongCodec.Kind.LoopStart && e.A == end.A);
            ch.LoopStart = start?.Tick ?? 0;
        }
        var stop = ev.FirstOrDefault(e => e.Kind == SongCodec.Kind.End);
        if (stop != null && end == null) ch.EndTick = stop.Tick;
        foreach (var e in ev)
        {
            switch (e.Kind)
            {
                case SongCodec.Kind.Note:
                    if (e.B > 0) ch.Notes.Add(new NoteItem { Tick = e.Tick, Pitch = e.A, Length = e.B, Velocity = e.C });
                    break;   // (a rest is just silence: left out)
                case SongCodec.Kind.Volume or SongCodec.Kind.Pan or SongCodec.Kind.Program or SongCodec.Kind.Raw:
                    ch.Others.Add(e); break;
            }
        }
        return ch;
    }

    /// <summary>The song as the codec wants it (notes past a channel's loop end or song end are left out).</summary>
    public SongCodec.Song ToSong()
    {
        var song = new SongCodec.Song { Tempo = Tempo };
        for (int c = 0; c < 8; c++)
        {
            var ch = Channels[c];
            song.Drum[c] = ch.Drum;
            if (!ch.Used) continue;
            int limit = ch.EndTick ?? ch.LoopEnd ?? int.MaxValue;
            var list = new List<(SongCodec.Event E, int Rank)>();
            if (ch.LoopEnd != null) list.Add((new SongCodec.Event(ch.LoopStart ?? 0, SongCodec.Kind.LoopStart), 0));
            foreach (var o in ch.Others) if (o.Tick <= limit) list.Add((o, 1));
            foreach (var n in ch.Notes.Where(n => n.Tick < limit))
                list.Add((new SongCodec.Event(n.Tick, SongCodec.Kind.Note, n.Pitch, Math.Clamp(n.Length, 1, 255), Math.Clamp(n.Velocity, 0, 127)), 2));
            if (ch.LoopEnd != null) list.Add((new SongCodec.Event(ch.LoopEnd.Value, SongCodec.Kind.LoopEnd), 3));
            else if (ch.EndTick != null) list.Add((new SongCodec.Event(ch.EndTick.Value, SongCodec.Kind.End), 3));
            song.Channels[c].AddRange(list.OrderBy(x => x.E.Tick).ThenBy(x => x.Rank).Select(x => x.E));
        }
        return song;
    }

    /// <summary>
    /// The song from <paramref name="from"/> on (for playing from the cursor): what each channel had set by then
    /// (instrument, volume, pan, effects) moves to the start.
    /// </summary>
    public SongCodec.Song ToSongFrom(int from)
    {
        var song = ToSong();
        if (from <= 0) return song;
        for (int c = 0; c < 8; c++)
        {
            var src = song.Channels[c];
            if (src.Count == 0) continue;
            var before = src.Where(e => e.Tick < from).ToList();
            var keep = new List<SongCodec.Event>();
            foreach (var k in new[] { SongCodec.Kind.Program, SongCodec.Kind.Volume, SongCodec.Kind.Pan })
                if (before.LastOrDefault(e => e.Kind == k) is { } last) keep.Add(last with { Tick = 0 });
            keep.AddRange(before.Where(e => e.Kind == SongCodec.Kind.Raw).Select(e => e with { Tick = 0 }));
            var loopEnd = src.FirstOrDefault(e => e.Kind == SongCodec.Kind.LoopEnd);
            foreach (var e in src.Where(e => e.Tick >= from && e.Kind != SongCodec.Kind.LoopStart))
                keep.Add(e with { Tick = e.Tick - from });
            if (loopEnd != null && loopEnd.Tick > from)
            {
                var ls = src.First(e => e.Kind == SongCodec.Kind.LoopStart);
                keep.Insert(0, ls with { Tick = Math.Max(0, ls.Tick - from) });
            }
            else if (!keep.Any(e => e.Kind is SongCodec.Kind.LoopEnd or SongCodec.Kind.End))
                keep.Add(new SongCodec.Event(Math.Max(1, keep.Count == 0 ? 1 : keep.Max(e => e.Tick + (e.Kind == SongCodec.Kind.Note ? e.B : 0))), SongCodec.Kind.End));
            keep.RemoveAll(e => e.Kind == SongCodec.Kind.LoopEnd && e.Tick <= 0);
            src.Clear(); src.AddRange(keep);
        }
        return song;
    }

    /// <summary>The loop the playhead follows: the first channel that repeats.</summary>
    public (int Start, int End)? SongLoop()
    {
        var ch = Channels.FirstOrDefault(c => c.Used && c.LoopEnd != null);
        return ch == null ? null : (ch.LoopStart ?? 0, ch.LoopEnd!.Value);
    }

    /// <summary>The song's length in ticks (the furthest loop end, song end or note end).</summary>
    public int LengthTicks => Math.Max(TicksPerBar * 4, Channels.Where(c => c.Used).Select(c =>
        Math.Max(c.LoopEnd ?? c.EndTick ?? 0, c.Notes.Count == 0 ? 0 : c.Notes.Max(n => n.End))).DefaultIfEmpty(0).Max());

    // ── change tracking ─────────────────────────────────────────────────────

    private readonly Stack<(EditChannel[] Ch, int Tempo)> _undo = new(), _redo = new();
    private readonly DispatcherTimer _sizeTimer;

    /// <summary>Call before a change (the change can then be undone).</summary>
    public void Checkpoint()
    {
        _undo.Push((Channels.Select(c => c.Clone()).ToArray(), Tempo));
        if (_undo.Count > 200) { var keep = _undo.Take(150).Reverse().ToList(); _undo.Clear(); foreach (var k in keep) _undo.Push(k); }
        _redo.Clear();
    }

    /// <summary>Call after a change.</summary>
    public void Touch()
    {
        Dirty = true;
        Refresh();
        _sizeTimer.Stop(); _sizeTimer.Start();
    }

    public void Refresh()
    {
        Selected.RemoveWhere(n => !Current.Notes.Contains(n));
        RefreshChannelLabels();
        RefreshOthers();
        RefreshSelectionFields();
        BpmText = $"{60_000_000.0 / (Math.Max(1, Tempo) * 125.0 * TicksPerBeat):0.#} BPM";
        Changed?.Invoke();
    }

    private void UpdateSize()
    {
        if (!Loaded) return;
        try
        {
            var data = _owner.BuildSongData(ToSong(), out _);
            SizeText = $"{data.Length - 2:N0} of {SongCodec.MaxSize:N0} bytes";
        }
        catch (Exception ex) { SizeText = "Too big: " + ex.Message; }
    }

    [RelayCommand]
    private void Undo()
    {
        if (_undo.Count == 0) return;
        _redo.Push((Channels.Select(c => c.Clone()).ToArray(), Tempo));
        var (ch, t) = _undo.Pop(); Channels = ch;
        _settingTempo = true; Tempo = t; _settingTempo = false;
        Selected.Clear(); Touch();
    }

    [RelayCommand]
    private void Redo()
    {
        if (_redo.Count == 0) return;
        _undo.Push((Channels.Select(c => c.Clone()).ToArray(), Tempo));
        var (ch, t) = _redo.Pop(); Channels = ch;
        _settingTempo = true; Tempo = t; _settingTempo = false;
        Selected.Clear(); Touch();
    }

    partial void OnCurrentChannelChanged(int oldValue, int newValue)
    {
        if (newValue is < 0 or > 7) { CurrentChannel = Math.Clamp(oldValue, 0, 7); return; }
        Selected.Clear();
        if (Loaded) Refresh();
    }
    partial void OnCursorTickChanged(int value) => CursorText = TimeText(value);

    /// <summary>Tempo typed in the box (the game's tempo number: 125 µs per tick).</summary>
    partial void OnTempoChanged(int oldValue, int newValue)
    {
        if (!Loaded || _settingTempo) return;
        int v = Math.Clamp(newValue, 1, 255);
        _settingTempo = true;
        _undo.Push((Channels.Select(c => c.Clone()).ToArray(), oldValue)); _redo.Clear();
        Tempo = v;
        _settingTempo = false;
        Touch();
    }
    private bool _settingTempo;

    // ── notes ───────────────────────────────────────────────────────────────

    public NoteItem AddNote(int tick, int pitch, int length)
    {
        var ch = Current;
        if (!ch.Used) StartChannel(ch);
        var n = new NoteItem { Tick = Math.Max(0, tick), Pitch = Math.Clamp(pitch, 0, 127), Length = Math.Clamp(length, 1, 255), Velocity = Math.Clamp(NewVelocity, 1, 127) };
        ch.Notes.Add(n);
        return n;
    }

    /// <summary>A channel that had nothing gets an instrument, volume and pan, and the other channels' loop.</summary>
    private void StartChannel(EditChannel ch)
    {
        int ins = _owner.LoadedInstruments().FirstOrDefault(0x01);
        ch.Others.Add(new SongCodec.Event(0, SongCodec.Kind.Program, ins));
        ch.Others.Add(new SongCodec.Event(0, SongCodec.Kind.Volume, 100));
        ch.Others.Add(new SongCodec.Event(0, SongCodec.Kind.Pan, 64));
        var other = Channels.FirstOrDefault(c => c != ch && c.Used);
        if (other != null) { ch.LoopStart = other.LoopStart; ch.LoopEnd = other.LoopEnd; ch.EndTick = other.EndTick; }
        else { ch.LoopStart = 0; ch.LoopEnd = TicksPerBar * 4; }
    }

    [RelayCommand]
    private void DeleteSelected()
    {
        if (Selected.Count == 0) return;
        Checkpoint();
        Current.Notes.RemoveAll(Selected.Contains);
        Selected.Clear();
        Touch();
    }

    [RelayCommand]
    private void SelectAll() { Selected.Clear(); foreach (var n in Current.Notes) Selected.Add(n); Refresh(); }

    private static List<NoteItem> _clipboard = new();

    [RelayCommand]
    private void Copy()
    {
        if (Selected.Count == 0) return;
        int first = Selected.Min(n => n.Tick);
        _clipboard = Selected.Select(n => { var c = n.Clone(); c.Tick -= first; return c; }).ToList();
        _owner.Status = $"Copied {_clipboard.Count} notes. Click the ruler to put the cursor where they go, then Paste.";
    }

    [RelayCommand]
    private void Cut() { Copy(); DeleteSelected(); }

    [RelayCommand]
    private void Paste()
    {
        if (_clipboard.Count == 0) return;
        Checkpoint();
        if (!Current.Used) StartChannel(Current);
        Selected.Clear();
        foreach (var c in _clipboard)
        {
            var n = c.Clone(); n.Tick += CursorTick;
            Current.Notes.Add(n); Selected.Add(n);
        }
        CursorTick = Selected.Max(n => n.End);
        Touch();
    }

    public void Transpose(int semitones)
    {
        if (Selected.Count == 0) return;
        Checkpoint();
        foreach (var n in Selected) n.Pitch = Math.Clamp(n.Pitch + semitones, 0, 127);
        Touch();
    }

    public void Shift(int ticks)
    {
        if (Selected.Count == 0) return;
        Checkpoint();
        int least = Selected.Min(n => n.Tick);
        if (least + ticks < 0) ticks = -least;
        foreach (var n in Selected) n.Tick += ticks;
        Touch();
    }

    [RelayCommand] private void TransposeUp() => Transpose(1);
    [RelayCommand] private void TransposeDown() => Transpose(-1);
    [RelayCommand] private void OctaveUp() => Transpose(12);
    [RelayCommand] private void OctaveDown() => Transpose(-12);

    /// <summary>Moves the selected notes' starts and lengths to the snap grid.</summary>
    [RelayCommand]
    private void Quantize()
    {
        if (Selected.Count == 0 || Snap <= 1) return;
        Checkpoint();
        foreach (var n in Selected)
        {
            n.Tick = (int)Math.Round(n.Tick / (double)Snap) * Snap;
            n.Length = Math.Max(Snap, (int)Math.Round(n.Length / (double)Snap) * Snap);
        }
        Touch();
    }

    // selected-note fields (typing applies to every selected note)
    [ObservableProperty] private string _selPitch = "";
    [ObservableProperty] private string _selStart = "";
    [ObservableProperty] private string _selLength = "";
    [ObservableProperty] private string _selVelocity = "";
    [ObservableProperty] private string _selInfo = "No note selected.";
    private bool _fillingFields;

    private void RefreshSelectionFields()
    {
        _fillingFields = true;
        if (Selected.Count == 0)
        {
            SelPitch = SelStart = SelLength = SelVelocity = "";
            SelInfo = "No note selected. Click a note, or Shift+drag a box around several.";
        }
        else
        {
            string Same(Func<NoteItem, string> f) { var v = Selected.Select(f).Distinct().ToList(); return v.Count == 1 ? v[0] : ""; }
            SelPitch = Same(n => NoteName(n.Pitch));
            SelStart = Same(n => TimeText(n.Tick));
            SelLength = Same(n => n.Length.ToString(CultureInfo.InvariantCulture));
            SelVelocity = Same(n => n.Velocity.ToString(CultureInfo.InvariantCulture));
            SelInfo = Selected.Count == 1 ? "1 note selected." : $"{Selected.Count} notes selected (fields left blank differ; typing sets them all).";
        }
        _fillingFields = false;
    }

    partial void OnSelPitchChanged(string value)
    {
        if (_fillingFields || Selected.Count == 0 || ParseNote(value) is not int p) return;
        Checkpoint(); foreach (var n in Selected) n.Pitch = p; Touch();
    }
    partial void OnSelStartChanged(string value)
    {
        if (_fillingFields || Selected.Count == 0 || ParseTime(value) is not int t) return;
        Checkpoint();
        int first = Selected.Min(n => n.Tick);
        foreach (var n in Selected) n.Tick = Math.Max(0, n.Tick - first + t);
        Touch();
    }
    partial void OnSelLengthChanged(string value)
    {
        if (_fillingFields || Selected.Count == 0 || !int.TryParse(value, out int l) || l is < 1 or > 255) return;
        Checkpoint(); foreach (var n in Selected) n.Length = l; Touch();
    }
    partial void OnSelVelocityChanged(string value)
    {
        if (_fillingFields || Selected.Count == 0 || !int.TryParse(value, out int v) || v is < 0 or > 127) return;
        Checkpoint(); foreach (var n in Selected) n.Velocity = v; Touch();
    }

    // ── channel events (instrument, volume, pan, effects) ───────────────────

    public ObservableCollection<OtherRow> Others { get; } = new();
    [ObservableProperty] private OtherRow? _selectedOther;
    [ObservableProperty] private string _otherValue = "";
    [ObservableProperty] private string _otherWhen = "";
    public static readonly string[] OtherKinds = { "Instrument", "Volume", "Pan" };
    public IEnumerable<string> OtherKindLabels => OtherKinds;
    [ObservableProperty] private int _otherKind;

    private void RefreshOthers()
    {
        var keep = SelectedOther?.Event;
        Others.Clear();
        foreach (var e in Current.Others.OrderBy(e => e.Tick))
            Others.Add(new OtherRow(e, TimeText(e.Tick), Describe(e)));
        SelectedOther = Others.FirstOrDefault(r => ReferenceEquals(r.Event, keep));
    }

    partial void OnSelectedOtherChanged(OtherRow? value)
    {
        if (value == null) return;
        OtherWhen = value.When;
        OtherKind = value.Event.Kind switch { SongCodec.Kind.Volume => 1, SongCodec.Kind.Pan => 2, _ => 0 };
        OtherValue = value.Event.Kind switch
        {
            SongCodec.Kind.Program => value.Event.A.ToString("X2"),
            SongCodec.Kind.Raw => Convert.ToHexString(value.Event.Raw!),
            _ => value.Event.A.ToString(CultureInfo.InvariantCulture),
        };
    }

    public static string Describe(SongCodec.Event e) => e.Kind switch
    {
        SongCodec.Kind.Program => $"Instrument {e.A:X2}",
        SongCodec.Kind.Volume => $"Volume {e.A}",
        SongCodec.Kind.Pan => $"Pan {e.A} ({(e.A < 60 ? "left" : e.A > 68 ? "right" : "centre")})",
        SongCodec.Kind.Raw when e.Raw![0] == 0xF6 => $"Tuning {e.Raw[2]:X2} {e.Raw[3]:X2}",
        SongCodec.Kind.Raw when e.Raw![0] == 0xFF => $"Effect {e.Raw[1]:X2} {e.Raw[2]:X2}" + (e.Raw[1] switch
        {
            0x03 => " (echo on)", 0x04 => " (echo off)", >= 0x0C and <= 0x0F => " (envelope)", 0x10 or 0x11 or 0x17 => " (echo volume)",
            0x12 or 0x15 or 0x16 => " (noise)", 0x14 or 0x18 => " (pitch modulation)", _ => "",
        }),
        _ => "Command " + Convert.ToHexString(e.Raw ?? Array.Empty<byte>()),
    };

    /// <summary>The value typed for the kind chosen; null when it doesn't parse.</summary>
    private SongCodec.Event? OtherFromFields(int tick)
    {
        if (SelectedOther?.Event.Kind == SongCodec.Kind.Raw && OtherKind == 0 && OtherValue.Length >= 4 && OtherValue.All(Uri.IsHexDigit))
            return new SongCodec.Event(tick, SongCodec.Kind.Raw, Raw: Convert.FromHexString(OtherValue));
        return OtherKind switch
        {
            0 when int.TryParse(OtherValue, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out int i) && i is >= 0 and < 0x40 => new SongCodec.Event(tick, SongCodec.Kind.Program, i),
            1 when int.TryParse(OtherValue, out int v) && v is >= 0 and <= 127 => new SongCodec.Event(tick, SongCodec.Kind.Volume, v),
            2 when int.TryParse(OtherValue, out int p) && p is >= 0 and <= 127 => new SongCodec.Event(tick, SongCodec.Kind.Pan, p),
            _ => null,
        };
    }

    [RelayCommand]
    private void AddOther()
    {
        var e = OtherFromFields(CursorTick);
        if (e == null) { _owner.Status = "Value: instrument = hex 00-3F, volume and pan = 0-127 (pan 64 = centre)."; return; }
        Checkpoint();
        if (!Current.Used) StartChannel(Current);
        Current.Others.Add(e);
        Touch();
        SelectedOther = Others.FirstOrDefault(r => ReferenceEquals(r.Event, e));
        if (e.Kind == SongCodec.Kind.Program && e.A is >= 0x10 and < 0x30 && !_owner.LoadedInstruments().Contains(e.A))
            _owner.Status = $"Instrument {e.A:X2} is added to the song's load list when you write the notes to the ROM.";
    }

    [RelayCommand]
    private void UpdateOther()
    {
        if (SelectedOther == null) return;
        int tick = ParseTime(OtherWhen) ?? SelectedOther.Event.Tick;
        var e = OtherFromFields(tick);
        if (e == null) { _owner.Status = "Value: instrument = hex 00-3F, volume and pan = 0-127 (pan 64 = centre)."; return; }
        Checkpoint();
        int i = Current.Others.IndexOf(SelectedOther.Event);
        if (i >= 0) Current.Others[i] = e;
        Touch();
        SelectedOther = Others.FirstOrDefault(r => ReferenceEquals(r.Event, e));
    }

    [RelayCommand]
    private void RemoveOther()
    {
        if (SelectedOther == null) return;
        Checkpoint();
        Current.Others.Remove(SelectedOther.Event);
        SelectedOther = null;
        Touch();
    }

    // ── loop ────────────────────────────────────────────────────────────────

    [ObservableProperty] private string _loopText = "";

    private IEnumerable<EditChannel> LoopTargets => LoopAllChannels ? Channels.Where(c => c.Used || c == Current) : new[] { Current };

    public void SetLoopStart(int tick)
    {
        foreach (var c in LoopTargets)
        {
            if (c.LoopEnd == null) { c.LoopEnd = c.EndTick ?? Math.Max(tick + TicksPerBar, LengthTicks); c.EndTick = null; }
            c.LoopStart = Math.Min(tick, c.LoopEnd.Value - 1);
        }
    }

    public void SetLoopEnd(int tick)
    {
        foreach (var c in LoopTargets)
        {
            c.EndTick = null;
            c.LoopStart ??= 0;
            c.LoopEnd = Math.Max(tick, c.LoopStart.Value + 1);
        }
    }

    [RelayCommand] private void LoopStartHere() { Checkpoint(); SetLoopStart(CursorTick); Touch(); }
    [RelayCommand] private void LoopEndHere() { Checkpoint(); SetLoopEnd(Math.Max(1, CursorTick)); Touch(); }

    /// <summary>The song stops at the cursor instead of repeating.</summary>
    [RelayCommand]
    private void StopHere()
    {
        Checkpoint();
        foreach (var c in LoopTargets) { c.LoopStart = c.LoopEnd = null; c.EndTick = Math.Max(1, CursorTick); }
        Touch();
    }

    private void RefreshChannelLabels()
    {
        var ch = Current;
        LoopText = ch.LoopEnd != null ? $"Repeats from {TimeText(ch.LoopStart ?? 0)} to {TimeText(ch.LoopEnd.Value)}."
                 : ch.EndTick != null ? $"Stops at {TimeText(ch.EndTick.Value)} (no repeat)."
                 : "Repeats from the start when it runs out.";
        for (int c = 0; c < 8; c++)
        {
            string label = $"{c + 1}" + (Channels[c].Drum ? " drums" : "") + (Channels[c].Used ? "" : " (empty)");
            if (ChannelLabels.Count <= c) ChannelLabels.Add(new ChannelTab());
            ChannelLabels[c].Label = label;
        }
        OnPropertyChanged(nameof(CurrentDrum));
    }

    public bool CurrentDrum
    {
        get => Current.Drum;
        set { if (Current.Drum == value) return; Checkpoint(); Current.Drum = value; Touch(); }
    }

    // ── playing ─────────────────────────────────────────────────────────────

    [RelayCommand] private void PlayFromStart() => _owner.PlayEdited(0);
    [RelayCommand] private void PlayFromCursor() => _owner.PlayEdited(CursorTick);
    [RelayCommand] private void StopPlaying() => _owner.StopCommand.Execute(null);
    [RelayCommand] private void WriteToRom() => _owner.ApplyNotes();
    [RelayCommand] private void RevertNotes() => _owner.ReloadNotes();

    public void PreviewNote(int pitch, int tick)
    {
        if (PreviewNotes) _owner.PreviewNote(CurrentChannel, pitch, tick);
    }

    // ── names and times ─────────────────────────────────────────────────────

    private static readonly string[] Names = { "C", "C#", "D", "D#", "E", "F", "F#", "G", "G#", "A", "A#", "B" };
    public static string NoteName(int n) => $"{Names[(n % 12 + 12) % 12]}{n / 12 - 1}";
    public static int? ParseNote(string s)
    {
        s = s.Trim().ToUpperInvariant();
        if (int.TryParse(s, out int raw) && raw is >= 0 and <= 127) return raw;
        for (int k = 11; k >= 0; k--)
            if (s.StartsWith(Names[k]) && int.TryParse(s[Names[k].Length..], out int oct))
            {
                int n = (oct + 1) * 12 + k;
                return n is >= 0 and <= 127 ? n : null;
            }
        return null;
    }
    /// <summary>bar.beat.tick, counting from 1.1.00 (48 ticks per beat, 4 beats per bar).</summary>
    public static string TimeText(int t) => $"{t / TicksPerBar + 1}.{t % TicksPerBar / TicksPerBeat + 1}.{t % TicksPerBeat:D2}";
    public static int? ParseTime(string s)
    {
        var p = s.Trim().Split('.', ':');
        if (p.Length is < 1 or > 3 || !p.All(x => int.TryParse(x, out _))) return null;
        int bar = int.Parse(p[0]), beat = p.Length > 1 ? int.Parse(p[1]) : 1, tick = p.Length > 2 ? int.Parse(p[2]) : 0;
        if (bar < 1 || beat < 1) return null;
        return (bar - 1) * TicksPerBar + (beat - 1) * TicksPerBeat + tick;
    }
}
