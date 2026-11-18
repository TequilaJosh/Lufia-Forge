using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LufiaForge.Core.Audio;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Windows.Threading;

namespace LufiaForge.Modules.GameData;

// a note in the editor: start and length in ticks (48 per quarter note), MIDI style pitch, velocity 1-127
public sealed class NoteItem
{
    public int Tick, Length, Pitch, Velocity;
    public NoteItem Clone() => (NoteItem)MemberwiseClone();
    public int End => Tick + Length;
}

// one of the game's 8 channels: its notes, its other events (instrument, volume, pan, effects) and its loop
public sealed class EditChannel
{
    public List<NoteItem> Notes = new();
    public List<SongCodec.Event> Others = new();
    // where the channel jumps back to, and where it jumps. null = no repeat
    public int? LoopStart, LoopEnd;
    // where the song stops (instead of repeating)
    public int? EndTick;
    public bool Drum;
    public bool Used => Notes.Count > 0 || Others.Count > 0;
    public EditChannel Clone() => new()
    {
        Notes = Notes.Select(n => n.Clone()).ToList(), Others = Others.ToList(),
        LoopStart = LoopStart, LoopEnd = LoopEnd, EndTick = EndTick, Drum = Drum,
    };
}

// a channel button above the piano roll
public partial class ChannelTab : ObservableObject
{
    [ObservableProperty] private string _label = "";
}

// a row of the channel events list
public sealed record OtherRow(SongCodec.Event Event, string When, string What);

// what the note editor needs from the page it sits on: the game's song format, playing, writing
public interface ISongEditorHost
{
    string Status { get; set; }
    // the song data (2 byte length, then the sound RAM image) as events
    SongCodec.Song DecodeSong(byte[] songData);
    // song data for the selected song from the edited events
    byte[] BuildSongData(SongCodec.Song song, out List<string> warnings);
    // the most bytes a song can have
    int MaxSongSize { get; }
    // added to a note number for its name. Lufia II plays note n as MIDI note n + 12
    int NoteNameOffset { get; }
    // the instrument a channel that had nothing starts with
    int DefaultInstrument();
    // a note for the status when an instrument is added, or null
    string? InstrumentNote(int instrument);
    // a command's description in the channel events list, or null for the shared wording
    string? DescribeRaw(byte[] raw);
    void PlayEdited(int fromTick);
    void PreviewNote(int channel, int pitch, int tick);
    void StopEdited();
    void ApplyNotes();
    void ReloadNotes();
}

// the Music tab's note editor: the song decoded into notes per channel (SongCodec), edited on a piano
// roll (Views.PianoRoll), encoded again when it's written to the ROM. Lufia 1's and Lufia II's Music pages
// both use it (ISongEditorHost)
public partial class NoteEditorViewModel : ObservableObject
{
    public const int TicksPerBeat = SongCodec.TicksPerQuarter, TicksPerBar = TicksPerBeat * 4;

    private readonly ISongEditorHost _owner;
    public NoteEditorViewModel(ISongEditorHost owner)
    {
        _owner = owner;
        _sizeTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(300) };
        _sizeTimer.Tick += (_, _) => { _sizeTimer.Stop(); UpdateSize(); };
    }

    public EditChannel[] Channels { get; private set; } = Enumerable.Range(0, 8).Select(_ => new EditChannel()).ToArray();
    public EditChannel Current => Channels[Math.Clamp(CurrentChannel, 0, 7)];
    public HashSet<NoteItem> Selected { get; } = new();

    // fires whenever what the piano roll shows changes
    public event Action? Changed;

    [ObservableProperty] private bool _loaded;
    [ObservableProperty] private int _currentChannel;
    [ObservableProperty] private int _tempo = 83;
    [ObservableProperty] private string _bpmText = "";
    [ObservableProperty] private string _sizeText = "";
    [ObservableProperty] private bool _dirty;
    [ObservableProperty] private int _cursorTick;
    [ObservableProperty] private string _cursorText = "1.1.00";
    // where playback is (ticks), -1 when not playing
    [ObservableProperty] private double _playheadTick = -1;
    [ObservableProperty] private bool _followPlayhead = true;
    [ObservableProperty] private bool _loopAllChannels = true;
    [ObservableProperty] private bool _previewNotes = true;

    // snap and new note settings
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
    // length of the last note drawn or resized (new notes get it when it isn't one of the choices)
    public int LastLength = 24;
    [ObservableProperty] private int _newVelocity = 100;

    public ObservableCollection<ChannelTab> ChannelLabels { get; } = new();

    // loading

    public void Load(byte[] songData)
    {
        var song = _owner.DecodeSong(songData);
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

    private static EditChannel FromEvents(List<SongCodec.Event> events, bool drum)
    {
        var channel = new EditChannel { Drum = drum };
        var end = events.LastOrDefault(e => e.Kind == SongCodec.Kind.LoopEnd);
        if (end != null)
        {
            channel.LoopEnd = end.Tick;
            var start = events.Take(events.IndexOf(end)).LastOrDefault(e => e.Kind == SongCodec.Kind.LoopStart && e.A == end.A);
            channel.LoopStart = start?.Tick ?? 0;
        }
        var stop = events.FirstOrDefault(e => e.Kind == SongCodec.Kind.End);
        if (stop != null && end == null) channel.EndTick = stop.Tick;
        foreach (var e in events)
        {
            switch (e.Kind)
            {
                case SongCodec.Kind.Note:
                    if (e.B > 0) channel.Notes.Add(new NoteItem { Tick = e.Tick, Pitch = e.A, Length = e.B, Velocity = e.C });
                    break;   // a rest is just silence, so it's left out
                case SongCodec.Kind.Volume or SongCodec.Kind.Pan or SongCodec.Kind.Program or SongCodec.Kind.Raw:
                    channel.Others.Add(e); break;
            }
        }
        return channel;
    }

    // the song the way the codec wants it (notes past a channel's loop end or song end are left out)
    public SongCodec.Song ToSong()
    {
        var song = new SongCodec.Song { Tempo = Tempo };
        for (int c = 0; c < 8; c++)
        {
            var channel = Channels[c];
            song.Drum[c] = channel.Drum;
            if (!channel.Used) continue;
            int limit = channel.EndTick ?? channel.LoopEnd ?? int.MaxValue;
            var list = new List<(SongCodec.Event E, int Rank)>();
            if (channel.LoopEnd != null) list.Add((new SongCodec.Event(channel.LoopStart ?? 0, SongCodec.Kind.LoopStart), 0));
            foreach (var other in channel.Others) if (other.Tick <= limit) list.Add((other, 1));
            foreach (var note in channel.Notes.Where(n => n.Tick < limit))
                list.Add((new SongCodec.Event(note.Tick, SongCodec.Kind.Note, note.Pitch, Math.Clamp(note.Length, 1, 255), Math.Clamp(note.Velocity, 0, 127)), 2));
            if (channel.LoopEnd != null) list.Add((new SongCodec.Event(channel.LoopEnd.Value, SongCodec.Kind.LoopEnd), 3));
            else if (channel.EndTick != null) list.Add((new SongCodec.Event(channel.EndTick.Value, SongCodec.Kind.End), 3));
            song.Channels[c].AddRange(list.OrderBy(x => x.E.Tick).ThenBy(x => x.Rank).Select(x => x.E));
        }
        return song;
    }

    // the notes ToSong leaves out: they start at or after their channel's loop end or song end
    public int NotesPastEnd() => Channels.Where(c => c.Used).Sum(c => c.Notes.Count(n => n.Tick >= (c.EndTick ?? c.LoopEnd ?? int.MaxValue)));

    // the song from fromTick on (for playing from the cursor). whatever each channel had set by then
    // (instrument, volume, pan, effects) moves to the start
    public SongCodec.Song ToSongFrom(int fromTick)
    {
        var song = ToSong();
        if (fromTick <= 0) return song;
        for (int c = 0; c < 8; c++)
        {
            var events = song.Channels[c];
            if (events.Count == 0) continue;
            var before = events.Where(e => e.Tick < fromTick).ToList();
            var kept = new List<SongCodec.Event>();
            foreach (var kind in new[] { SongCodec.Kind.Program, SongCodec.Kind.Volume, SongCodec.Kind.Pan })
                if (before.LastOrDefault(e => e.Kind == kind) is { } last) kept.Add(last with { Tick = 0 });
            kept.AddRange(before.Where(e => e.Kind == SongCodec.Kind.Raw).Select(e => e with { Tick = 0 }));
            var loopEnd = events.FirstOrDefault(e => e.Kind == SongCodec.Kind.LoopEnd);
            foreach (var e in events.Where(e => e.Tick >= fromTick && e.Kind != SongCodec.Kind.LoopStart))
                kept.Add(e with { Tick = e.Tick - fromTick });
            if (loopEnd != null && loopEnd.Tick > fromTick)
            {
                var loopStart = events.First(e => e.Kind == SongCodec.Kind.LoopStart);
                kept.Insert(0, loopStart with { Tick = Math.Max(0, loopStart.Tick - fromTick) });
            }
            else if (!kept.Any(e => e.Kind is SongCodec.Kind.LoopEnd or SongCodec.Kind.End))
                kept.Add(new SongCodec.Event(Math.Max(1, kept.Count == 0 ? 1 : kept.Max(e => e.Tick + (e.Kind == SongCodec.Kind.Note ? e.B : 0))), SongCodec.Kind.End));
            kept.RemoveAll(e => e.Kind == SongCodec.Kind.LoopEnd && e.Tick <= 0);
            events.Clear(); events.AddRange(kept);
        }
        return song;
    }

    // the loop the playhead follows: the first channel that repeats
    public (int Start, int End)? SongLoop()
    {
        var channel = Channels.FirstOrDefault(c => c.Used && c.LoopEnd != null);
        return channel == null ? null : (channel.LoopStart ?? 0, channel.LoopEnd!.Value);
    }

    // the song's length in ticks (the furthest loop end, song end or note end)
    public int LengthTicks => Math.Max(TicksPerBar * 4, Channels.Where(c => c.Used).Select(c =>
        Math.Max(c.LoopEnd ?? c.EndTick ?? 0, c.Notes.Count == 0 ? 0 : c.Notes.Max(n => n.End))).DefaultIfEmpty(0).Max());

    // change tracking

    private readonly Stack<(EditChannel[] Ch, int Tempo)> _undo = new(), _redo = new();

    // the undo / redo history and the cursor, kept while the song is written and read back
    public object SaveHistory() => (_undo.Reverse().ToList(), _redo.Reverse().ToList(), CursorTick);

    public void RestoreHistory(object saved)
    {
        if (saved is not (List<(EditChannel[], int)> undo, List<(EditChannel[], int)> redo, int cursor)) return;
        _undo.Clear(); foreach (var step in undo) _undo.Push(step);
        _redo.Clear(); foreach (var step in redo) _redo.Push(step);
        CursorTick = cursor;
        UndoCommand.NotifyCanExecuteChanged(); RedoCommand.NotifyCanExecuteChanged();
    }
    private readonly DispatcherTimer _sizeTimer;

    // call before a change so it can be undone
    public void Checkpoint()
    {
        _undo.Push((Channels.Select(c => c.Clone()).ToArray(), Tempo));
        if (_undo.Count > 200) { var kept = _undo.Take(150).Reverse().ToList(); _undo.Clear(); foreach (var step in kept) _undo.Push(step); }
        _redo.Clear();
    }

    // call after a change
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
            SizeText = $"{data.Length - 2:N0} of {_owner.MaxSongSize:N0} bytes";
        }
        catch (Exception ex) { SizeText = "Too big: " + ex.Message; }
    }

    [RelayCommand]
    private void Undo()
    {
        if (_undo.Count == 0) return;
        _redo.Push((Channels.Select(c => c.Clone()).ToArray(), Tempo));
        var (channels, tempo) = _undo.Pop(); Channels = channels;
        _settingTempo = true; Tempo = tempo; _settingTempo = false;
        Selected.Clear(); Touch();
    }

    [RelayCommand]
    private void Redo()
    {
        if (_redo.Count == 0) return;
        _undo.Push((Channels.Select(c => c.Clone()).ToArray(), Tempo));
        var (channels, tempo) = _redo.Pop(); Channels = channels;
        _settingTempo = true; Tempo = tempo; _settingTempo = false;
        Selected.Clear(); Touch();
    }

    partial void OnCurrentChannelChanged(int oldValue, int newValue)
    {
        if (newValue is < 0 or > 7) { CurrentChannel = Math.Clamp(oldValue, 0, 7); return; }
        Selected.Clear();
        if (Loaded) Refresh();
    }
    partial void OnCursorTickChanged(int value) => CursorText = TimeText(value);

    // tempo typed in the box (the game's tempo number, 125 us per tick)
    partial void OnTempoChanged(int oldValue, int newValue)
    {
        if (!Loaded || _settingTempo) return;
        int clamped = Math.Clamp(newValue, 1, 255);
        _settingTempo = true;
        _undo.Push((Channels.Select(c => c.Clone()).ToArray(), oldValue)); _redo.Clear();
        Tempo = clamped;
        _settingTempo = false;
        Touch();
    }
    private bool _settingTempo;

    // notes

    public NoteItem AddNote(int tick, int pitch, int length)
    {
        var channel = Current;
        if (!channel.Used) StartChannel(channel);
        var note = new NoteItem { Tick = Math.Max(0, tick), Pitch = Math.Clamp(pitch, 0, 127), Length = Math.Clamp(length, 1, 255), Velocity = Math.Clamp(NewVelocity, 1, 127) };
        channel.Notes.Add(note);
        return note;
    }

    // a channel that had nothing gets an instrument, volume and pan, and the other channels' loop
    private void StartChannel(EditChannel channel)
    {
        int instrument = _owner.DefaultInstrument();
        channel.Others.Add(new SongCodec.Event(0, SongCodec.Kind.Program, instrument));
        channel.Others.Add(new SongCodec.Event(0, SongCodec.Kind.Volume, 100));
        channel.Others.Add(new SongCodec.Event(0, SongCodec.Kind.Pan, 64));
        var other = Channels.FirstOrDefault(c => c != channel && c.Used);
        if (other != null) { channel.LoopStart = other.LoopStart; channel.LoopEnd = other.LoopEnd; channel.EndTick = other.EndTick; }
        else { channel.LoopStart = 0; channel.LoopEnd = TicksPerBar * 4; }
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
    private void SelectAll() { Selected.Clear(); foreach (var note in Current.Notes) Selected.Add(note); Refresh(); }

    private static List<NoteItem> _clipboard = new();

    [RelayCommand]
    private void Copy()
    {
        if (Selected.Count == 0) return;
        int first = Selected.Min(n => n.Tick);
        _clipboard = Selected.Select(n => { var copy = n.Clone(); copy.Tick -= first; return copy; }).ToList();
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
        foreach (var copied in _clipboard)
        {
            var note = copied.Clone(); note.Tick += CursorTick;
            Current.Notes.Add(note); Selected.Add(note);
        }
        CursorTick = Selected.Max(n => n.End);
        Touch();
    }

    public void Transpose(int semitones)
    {
        if (Selected.Count == 0) return;
        Checkpoint();
        foreach (var note in Selected) note.Pitch = Math.Clamp(note.Pitch + semitones, 0, 127);
        Touch();
    }

    public void Shift(int ticks)
    {
        if (Selected.Count == 0) return;
        Checkpoint();
        int least = Selected.Min(n => n.Tick);
        if (least + ticks < 0) ticks = -least;
        foreach (var note in Selected) note.Tick += ticks;
        Touch();
    }

    [RelayCommand] private void TransposeUp() => Transpose(1);
    [RelayCommand] private void TransposeDown() => Transpose(-1);
    [RelayCommand] private void OctaveUp() => Transpose(12);
    [RelayCommand] private void OctaveDown() => Transpose(-12);

    // moves the selected notes' starts and lengths onto the snap grid
    [RelayCommand]
    private void Quantize()
    {
        if (Selected.Count == 0 || Snap <= 1) return;
        Checkpoint();
        int longest = 255 / Snap * Snap;   // a note is at most 255 ticks, so the longest length on the grid
        foreach (var note in Selected)
        {
            note.Tick = (int)Math.Round(note.Tick / (double)Snap) * Snap;
            note.Length = Math.Clamp((int)Math.Round(note.Length / (double)Snap) * Snap, Snap, longest);
        }
        Touch();
    }

    // selected note fields (typing applies to every selected note)
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
            string Same(Func<NoteItem, string> field) { var values = Selected.Select(field).Distinct().ToList(); return values.Count == 1 ? values[0] : ""; }
            SelPitch = Same(n => Name(n.Pitch));
            SelStart = Same(n => TimeText(n.Tick));
            SelLength = Same(n => n.Length.ToString(CultureInfo.InvariantCulture));
            SelVelocity = Same(n => n.Velocity.ToString(CultureInfo.InvariantCulture));
            SelInfo = Selected.Count == 1 ? "1 note selected." : $"{Selected.Count} notes selected (fields left blank differ; typing sets them all).";
        }
        _fillingFields = false;
    }

    partial void OnSelPitchChanged(string value)
    {
        if (_fillingFields || Selected.Count == 0 || Parse(value) is not int pitch) return;
        Checkpoint(); foreach (var note in Selected) note.Pitch = pitch; Touch();
    }
    partial void OnSelStartChanged(string value)
    {
        if (_fillingFields || Selected.Count == 0 || ParseTime(value) is not int start) return;
        Checkpoint();
        int first = Selected.Min(n => n.Tick);
        foreach (var note in Selected) note.Tick = Math.Max(0, note.Tick - first + start);
        Touch();
    }
    partial void OnSelLengthChanged(string value)
    {
        if (_fillingFields || Selected.Count == 0 || !int.TryParse(value, out int length) || length is < 1 or > 255) return;
        Checkpoint(); foreach (var note in Selected) note.Length = length; Touch();
    }
    partial void OnSelVelocityChanged(string value)
    {
        if (_fillingFields || Selected.Count == 0 || !int.TryParse(value, out int velocity) || velocity is < 0 or > 127) return;
        Checkpoint(); foreach (var note in Selected) note.Velocity = velocity; Touch();
    }

    // channel events (instrument, volume, pan, effects)

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
            Others.Add(new OtherRow(e, TimeText(e.Tick), e.Kind == SongCodec.Kind.Raw && _owner.DescribeRaw(e.Raw!) is { } description ? description : Describe(e)));
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

    // the value typed for the kind picked. null when it doesn't parse
    private SongCodec.Event? OtherFromFields(int tick)
    {
        if (SelectedOther?.Event.Kind == SongCodec.Kind.Raw && OtherKind == 0 && OtherValue.Length >= 4 && OtherValue.All(Uri.IsHexDigit))
            return new SongCodec.Event(tick, SongCodec.Kind.Raw, Raw: Convert.FromHexString(OtherValue));
        return OtherKind switch
        {
            0 when int.TryParse(OtherValue, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out int instrument) && instrument is >= 0 and < 0x40 => new SongCodec.Event(tick, SongCodec.Kind.Program, instrument),
            1 when int.TryParse(OtherValue, out int volume) && volume is >= 0 and <= 127 => new SongCodec.Event(tick, SongCodec.Kind.Volume, volume),
            2 when int.TryParse(OtherValue, out int pan) && pan is >= 0 and <= 127 => new SongCodec.Event(tick, SongCodec.Kind.Pan, pan),
            _ => null,
        };
    }

    [RelayCommand]
    private void AddOther()
    {
        var added = OtherFromFields(CursorTick);
        if (added == null) { _owner.Status = "Value: instrument = hex 00-3F, volume and pan = 0-127 (pan 64 = centre)."; return; }
        Checkpoint();
        if (!Current.Used) StartChannel(Current);
        Current.Others.Add(added);
        Touch();
        SelectedOther = Others.FirstOrDefault(r => ReferenceEquals(r.Event, added));
        if (added.Kind == SongCodec.Kind.Program && _owner.InstrumentNote(added.A) is { } note) _owner.Status = note;
    }

    [RelayCommand]
    private void UpdateOther()
    {
        if (SelectedOther == null) return;
        int tick = ParseTime(OtherWhen) ?? SelectedOther.Event.Tick;
        var updated = OtherFromFields(tick);
        if (updated == null) { _owner.Status = "Value: instrument = hex 00-3F, volume and pan = 0-127 (pan 64 = centre)."; return; }
        Checkpoint();
        int i = Current.Others.IndexOf(SelectedOther.Event);
        if (i >= 0) Current.Others[i] = updated;
        Touch();
        SelectedOther = Others.FirstOrDefault(r => ReferenceEquals(r.Event, updated));
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

    // loop

    [ObservableProperty] private string _loopText = "";

    private IEnumerable<EditChannel> LoopTargets => LoopAllChannels ? Channels.Where(c => c.Used || c == Current) : new[] { Current };

    public void SetLoopStart(int tick)
    {
        foreach (var channel in LoopTargets)
        {
            if (channel.LoopEnd == null) { channel.LoopEnd = channel.EndTick ?? Math.Max(tick + TicksPerBar, LengthTicks); channel.EndTick = null; }
            channel.LoopStart = Math.Min(tick, channel.LoopEnd.Value - 1);
        }
    }

    public void SetLoopEnd(int tick)
    {
        foreach (var channel in LoopTargets)
        {
            channel.EndTick = null;
            channel.LoopStart ??= 0;
            channel.LoopEnd = Math.Max(tick, channel.LoopStart.Value + 1);
        }
    }

    [RelayCommand] private void LoopStartHere() { Checkpoint(); SetLoopStart(CursorTick); Touch(); }
    [RelayCommand] private void LoopEndHere() { Checkpoint(); SetLoopEnd(Math.Max(1, CursorTick)); Touch(); }

    // the song stops at the cursor instead of repeating
    [RelayCommand]
    private void StopHere()
    {
        Checkpoint();
        foreach (var channel in LoopTargets) { channel.LoopStart = channel.LoopEnd = null; channel.EndTick = Math.Max(1, CursorTick); }
        Touch();
    }

    private void RefreshChannelLabels()
    {
        var current = Current;
        LoopText = current.LoopEnd != null ? $"Repeats from {TimeText(current.LoopStart ?? 0)} to {TimeText(current.LoopEnd.Value)}."
                 : current.EndTick != null ? $"Stops at {TimeText(current.EndTick.Value)} (no repeat)."
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

    // playing

    [RelayCommand] private void PlayFromStart() => _owner.PlayEdited(0);
    [RelayCommand] private void PlayFromCursor() => _owner.PlayEdited(CursorTick);
    [RelayCommand] private void StopPlaying() => _owner.StopEdited();
    [RelayCommand] private void WriteToRom() => _owner.ApplyNotes();
    [RelayCommand] private void RevertNotes() => _owner.ReloadNotes();

    public void PreviewNote(int pitch, int tick)
    {
        if (PreviewNotes) _owner.PreviewNote(CurrentChannel, pitch, tick);
    }

    // names and times

    private static readonly string[] Names = { "C", "C#", "D", "D#", "E", "F", "F#", "G", "G#", "A", "A#", "B" };
    public static string NoteName(int note) => $"{Names[(note % 12 + 12) % 12]}{note / 12 - 1}";
    // the name of the note the game plays for this note number (with ISongEditorHost.NoteNameOffset added)
    public string Name(int note) => NoteName(note + _owner.NoteNameOffset);
    // a typed note name (or number) as the game's note number
    public int? Parse(string text) => ParseNote(text) is int parsed && (text.Trim().All(char.IsDigit) ? parsed : parsed - _owner.NoteNameOffset) is int note && note is >= 0 and <= 127 ? note : null;
    public static int? ParseNote(string text)
    {
        text = text.Trim().ToUpperInvariant();
        if (int.TryParse(text, out int raw) && raw is >= 0 and <= 127) return raw;
        for (int k = 11; k >= 0; k--)
            if (text.StartsWith(Names[k]) && int.TryParse(text[Names[k].Length..], out int octave))
            {
                int note = (octave + 1) * 12 + k;
                return note is >= 0 and <= 127 ? note : null;
            }
        return null;
    }
    // bar.beat.tick, counting from 1.1.00 (48 ticks per beat, 4 beats per bar)
    public static string TimeText(int tick) => $"{tick / TicksPerBar + 1}.{tick % TicksPerBar / TicksPerBeat + 1}.{tick % TicksPerBeat:D2}";
    public static int? ParseTime(string text)
    {
        var parts = text.Trim().Split('.', ':');
        if (parts.Length is < 1 or > 3 || !parts.All(x => int.TryParse(x, out _))) return null;
        int bar = int.Parse(parts[0]), beat = parts.Length > 1 ? int.Parse(parts[1]) : 1, tick = parts.Length > 2 ? int.Parse(parts[2]) : 0;
        if (bar < 1 || beat < 1) return null;
        return (bar - 1) * TicksPerBar + (beat - 1) * TicksPerBeat + tick;
    }
}
