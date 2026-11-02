using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LufiaForge.Core.Audio;
using LufiaForge.Core.Lufia2;
using LufiaForge.Modules.GameData;
using LufiaForge.ViewModels;
using Microsoft.Win32;

namespace LufiaForge.Modules.Lufia2;

/// <summary>The songs (packed files 00-3E): play them on the game's own sound driver, edit their notes on the piano
/// roll Lufia 1 has (<see cref="NoteEditorViewModel"/>), export and import them as MIDI, save them as WAV.</summary>
public partial class L2MusicPage : L2Page, ISongEditorHost, IPendingEdits
{
    [ObservableProperty] private string _info = "";
    [ObservableProperty] private string _status = "";
    [ObservableProperty] private bool _isPlaying;

    private L2Sound? _sound;
    private AudioOut? _audio;

    /// <summary>The note editor (Notes tab) for the song it was loaded from (<see cref="_notesSong"/>).</summary>
    public NoteEditorViewModel Notes { get; }
    private int _notesSong = -1;
    private readonly System.Windows.Threading.DispatcherTimer _playhead;
    private int _playFrom = -1, _playTempo = 1;
    private (int Start, int End)? _playLoop;

    public L2MusicPage()
    {
        Notes = new NoteEditorViewModel(this);
        _playhead = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(30) };
        _playhead.Tick += (_, _) => UpdatePlayhead();
    }

    protected override void OnListRead() => Main?.Register(this);

    protected override IEnumerable<string> ReadNames()
    {
        StopPlaying();
        _notesSong = -1;
        return Enumerable.Range(0, L2Songs.Count).Select(i =>
        {
            string n = L2Songs.Name(Rom!, i);
            return $"{i:X2}  {(n.Length > 0 ? n : "Song " + i.ToString("X2"))}";
        });
    }

    protected override void Load(int index)
    {
        // notes edited on the song shown before go into the ROM first, as the other pages write when left
        if (Notes.Dirty && _notesSong >= 0 && _notesSong != index && WriteNotes(_notesSong) is { } why) Status = "The edited notes weren't written: " + why;
        var f = L2Songs.Read(Rom!, index);
        if (f.Length < 0x52) { Info = "This file is too short to be a song."; _notesSong = -1; return; }
        ShowInfo(index, f);
        _notesSong = index;
        StopPlaying();
        try { Notes.Load(f); } catch (Exception ex) { Status = "The note editor couldn't read this song: " + ex.Message; }
    }

    private void ShowInfo(int index, byte[] f)
    {
        int tempo = f[2 + 3];
        var song = L2Songs.Decode(f);
        int channels = song.Channels.Count(c => c.Any(e => e.Kind == SongCodec.Kind.Note && e.B > 0));
        int drums = Enumerable.Range(0, 8).Count(c => song.Drum[c] && song.Channels[c].Count > 0);
        var samples = L2Songs.SampleList(f);
        string own = string.Join(" ", samples.Where(s => s < 0x80).Select(s => s.ToString("X2")));
        string drumNotes = string.Join(" ", samples.Where(s => s >= 0x80).Select(s => (s & 0x7F).ToString("X2")));
        int loop = song.Channels.SelectMany(c => c).Where(e => e.Kind == SongCodec.Kind.LoopEnd).Select(e => e.Tick).DefaultIfEmpty(0).Max();
        Info = $"Internal name \"{L2Songs.Name(f)}\", {f.Length - 2:N0} bytes of the {L2Songs.MaxSize:N0} the sound driver has room for (sent to sound RAM $4400). " +
               $"Tempo {tempo} ({tempo * 0.125:0.###} ms per tick, {60_000_000.0 / song.MicrosPerQuarter:0.#} BPM). " +
               $"{channels} channels play notes{(drums > 0 ? $", {drums} of them drums" : "")}. " +
               (own.Length > 0 ? $"Own samples: {own} (slots 20 and up). " : "No samples of its own. ") +
               (drumNotes.Length > 0 ? $"Drum notes loaded again: {drumNotes}. " : "") +
               (f[2 + 0x18] != 0 ? "Its samples replace the shared ones (header byte 18 is set). " : "") +
               (loop > 0 ? $"Repeats forever (the channels' loops close by tick {loop})." : "Plays once.") +
               $"\nPacked file {index:X2} at 0x{L2Rom.FileOffset(Rom!, index):X6}.";
    }

    protected override bool IsField(string p) => false;
    protected override string? Write() => null;

    // ── listening ───────────────────────────────────────────────────────────

    [RelayCommand]
    private void Play()
    {
        StopPlaying();
        if (Rom == null || SelectedIndex < 0) return;
        if (!SpcNative.Available) { Status = "The sound chip emulator (lufia_spc.dll) isn't available."; return; }
        try
        {
            _sound = L2Sound.Create(Rom);
            if (_sound == null) { Status = "The sound driver couldn't start."; return; }
            if (!_sound.PlaySong(SelectedIndex)) { StopPlaying(); Status = "This file isn't a song the driver plays."; return; }
            if (!StartAudio()) return;
            Status = $"Playing song {SelectedIndex:X2} (the game's own sound driver on an emulated sound chip).";
        }
        catch (Exception ex) { StopPlaying(); Status = "Couldn't play: " + ex.Message; }
    }

    [RelayCommand]
    private void Stop() { StopPlaying(); Status = ""; }

    private void StopPlaying()
    {
        _playhead.Stop();
        _audio?.Dispose(); _audio = null;
        if (_sound != null) { lock (_sound) _sound.Dispose(); _sound = null; }
        IsPlaying = false;
        _playFrom = -1;
        Notes.PlayheadTick = -1;
    }

    private bool StartAudio()
    {
        var snd = _sound!;
        _audio = new AudioOut(L2Sound.SampleRate, (buf, pairs) => { lock (snd) snd.Render(buf, pairs * 2); });
        if (!_audio.Start()) { StopPlaying(); Status = "No sound device."; return false; }
        IsPlaying = true;
        return true;
    }

    /// <summary>Starts the sound chip on song data (not the ROM's); true when it plays.</summary>
    private bool StartData(byte[] data)
    {
        StopPlaying();
        if (Rom == null || !SpcNative.Available) { Status = "The sound chip emulator (lufia_spc.dll) isn't available."; return false; }
        _sound = L2Sound.Create(Rom);
        if (_sound == null) { Status = "The sound driver couldn't start."; return false; }
        if (!_sound.PlaySongData(data)) { StopPlaying(); Status = "The sound driver didn't take the song."; return false; }
        return StartAudio();
    }

    /// <summary>The selected song, 90 seconds of it as the game plays it, as a WAV file.</summary>
    [RelayCommand]
    private void SaveWav()
    {
        if (Rom == null || SelectedIndex < 0) return;
        if (!SpcNative.Available) { Status = "The sound chip emulator (lufia_spc.dll) isn't available."; return; }
        var dlg = new SaveFileDialog { Title = "Save as WAV (90 seconds)", Filter = "WAV sound (*.wav)|*.wav", FileName = $"{Names[SelectedIndex][4..].Trim()}.wav" };
        if (dlg.ShowDialog() != true) return;
        try
        {
            using var snd = L2Sound.Create(Rom);
            if (snd == null || !snd.PlaySong(SelectedIndex)) { Status = "The sound driver couldn't play this song."; return; }
            int pairs = L2Sound.SampleRate * 90;
            var pcm = new short[pairs * 2];
            for (int at = 0; at < pairs; at += 4096)
            {
                int n = Math.Min(4096, pairs - at);
                var chunk = new short[n * 2];
                snd.Render(chunk, n * 2);
                Array.Copy(chunk, 0, pcm, at * 2, n * 2);
            }
            using var w = new BinaryWriter(File.Create(dlg.FileName));
            int bytes = pcm.Length * 2;
            w.Write("RIFF"u8); w.Write(36 + bytes); w.Write("WAVEfmt "u8); w.Write(16); w.Write((short)1); w.Write((short)2);
            w.Write(L2Sound.SampleRate); w.Write(L2Sound.SampleRate * 4); w.Write((short)4); w.Write((short)16);
            w.Write("data"u8); w.Write(bytes);
            foreach (var s in pcm) w.Write(s);
            Status = $"Saved {dlg.FileName} (90 seconds, as the game sounds).";
        }
        catch (Exception ex) { Status = "Not saved: " + ex.Message; }
    }

    // ── the note editor's host ──────────────────────────────────────────────

    public SongCodec.Song DecodeSong(byte[] songData) => L2Songs.Decode(songData);
    public int MaxSongSize => L2Songs.MaxSize;
    public int NoteNameOffset => L2Songs.MidiTranspose;

    public byte[] BuildSongData(SongCodec.Song song, out List<string> warnings)
    {
        warnings = new List<string>();
        if (Rom == null || _notesSong < 0) throw new InvalidOperationException("no song is open");
        return L2Songs.Build(song, L2Songs.Read(Rom, _notesSong), warnings);
    }

    /// <summary>The first instrument the song picks (a channel that had nothing starts with it), else 1E.</summary>
    public int DefaultInstrument() =>
        Notes.Channels.SelectMany(c => c.Others).Where(e => e.Kind == SongCodec.Kind.Program).Select(e => e.A).DefaultIfEmpty(0x1E).First();

    public string? InstrumentNote(int instrument)
    {
        if (Rom == null || _notesSong < 0) return null;
        int own = L2Songs.SampleList(L2Songs.Read(Rom, _notesSong)).TakeWhile(x => x < 0x80).Count();
        return instrument >= 0x20 + own
            ? $"Slot {instrument:X2} isn't loaded for this song (shared samples 00-1F, then its own from 20: {own} of them); its notes play with whatever is there."
            : $"Instrument {instrument:X2}: sample {(instrument < 0x20 ? instrument : L2Songs.SampleList(L2Songs.Read(Rom, _notesSong))[instrument - 0x20]):X2}.";
    }

    /// <summary>Lufia II's commands (docs/lufia2/music.md section 3).</summary>
    public string? DescribeRaw(byte[] raw) => raw switch
    {
        [0xF0, _, var x] => x == 0 ? "Vibrato off" : $"Vibrato {x}",
        [0xF4, _, var t] => $"Tempo {t}",
        [0xF6, _, var m] => $"Pitch x{(m == 0 ? 2 : m / 128.0):0.##} (F6 {m:X2})",
        [0xFD, ..] => "Phrase end",
        [0xFF, var s, var p] => $"Effect {s:X2} {p:X2}" + (s switch
        {
            0x01 => " (echo feedback)", 0x02 => " (echo filter)", 0x03 => " (echo on)", 0x04 => " (echo off)",
            0x08 or 0x09 => " (attack / decay)", 0x0A or 0x0B => " (sustain)", >= 0x0D and <= 0x0F => " (echo volume)",
            0x10 => " (noise clock)", 0x11 => " (noise on)", 0x12 => " (noise off)", 0x13 => " (pitch modulation on)", 0x14 => " (pitch modulation off)",
            _ => "",
        }),
        _ => null,
    };

    public void PlayEdited(int fromTick)
    {
        if (Rom == null || _notesSong < 0) return;
        byte[] data;
        try { data = BuildSongData(Notes.ToSongFrom(fromTick), out _); }
        catch (Exception ex) { Status = "Can't play: " + ex.Message; return; }
        if (!StartData(data)) return;
        _playFrom = fromTick;
        var loop = Notes.SongLoop();
        _playLoop = loop is { } l && l.End > fromTick ? (Math.Max(l.Start, fromTick), l.End) : null;
        _playTempo = Notes.Tempo;
        _playhead.Start();
        Status = fromTick == 0 ? "Playing the edited song (not in the ROM yet)." : $"Playing the edited song from {NoteEditorViewModel.TimeText(fromTick)}.";
    }

    private void UpdatePlayhead()
    {
        if (_audio == null || _playFrom < 0) { Notes.PlayheadTick = -1; return; }
        double secs = _audio.PlayedPairs / (double)L2Sound.SampleRate;
        double t = _playFrom + secs * 1_000_000 / (Math.Max(1, _playTempo) * 125.0);
        if (_playLoop is { } l && t >= l.End) t = l.Start + (t - l.Start) % (l.End - l.Start);
        Notes.PlayheadTick = t;
    }

    /// <summary>One note on its own, with the channel's instrument, volume and pan at that point.</summary>
    public void PreviewNote(int channel, int pitch, int tick)
    {
        if (Rom == null || _notesSong < 0 || _playFrom >= 0 && IsPlaying) return;   // not while the song plays
        var ch = Notes.Channels[channel];
        var song = new SongCodec.Song { Tempo = 100 };
        song.Drum[0] = ch.Drum;
        var list = song.Channels[0];
        foreach (var k in new[] { SongCodec.Kind.Program, SongCodec.Kind.Volume, SongCodec.Kind.Pan })
            if (ch.Others.Where(e => e.Kind == k && e.Tick <= tick).OrderBy(e => e.Tick).LastOrDefault() is { } last) list.Add(last with { Tick = 0 });
        list.Add(new SongCodec.Event(0, SongCodec.Kind.Note, pitch, 30, Math.Clamp(Notes.NewVelocity, 1, 127)));
        list.Add(new SongCodec.Event(40, SongCodec.Kind.End));
        try { if (StartData(BuildSongData(song, out _))) Status = Notes.Name(pitch); }
        catch (Exception ex) { Status = "Can't preview: " + ex.Message; }
    }

    public void StopEdited() => Stop();

    public void ApplyNotes()
    {
        if (_notesSong < 0) return;
        int index = _notesSong;
        if (WriteNotes(index) is { } why) Status = "Not written: " + why;
    }

    /// <summary>Writes the note editor's song over song <paramref name="index"/>. Null, or why it couldn't be.</summary>
    private string? WriteNotes(int index)
    {
        if (Rom == null || Main == null) return "no ROM";
        byte[] data; List<string> warnings;
        try { data = BuildSongData(Notes.ToSong(), out warnings); }
        catch (Exception ex) { return ex.Message; }
        StopPlaying();
        try
        {
            int before = Rom.Length;
            L2Rom.WriteFile(Rom, index, data);
            Main.NotifyRomModified($"Lufia II: song {index:X2} notes");
            Notes.Dirty = false;
            if (index == SelectedIndex) ShowInfo(index, data);
            Status = $"Song {index:X2} written ({data.Length - 2:N0} of {L2Songs.MaxSize:N0} bytes)." +
                     (Rom.Length > before ? " The ROM grew to make room for it." : "") +
                     (warnings.Count > 0 ? " " + string.Join(" ", warnings) : "");
            return null;
        }
        catch (Exception ex) { return ex.Message; }
    }

    public void ReloadNotes()
    {
        if (Rom == null || _notesSong < 0) return;
        StopPlaying();
        Notes.Load(L2Songs.Read(Rom, _notesSong));
        Status = "Notes read from the ROM again.";
    }

    public bool HasPendingEdits => Rom != null && _notesSong >= 0 && Notes.Dirty;
    public string? WritePendingEdits() => HasPendingEdits ? WriteNotes(_notesSong) : null;

    // ── MIDI ────────────────────────────────────────────────────────────────

    [RelayCommand]
    private void ExportMidi()
    {
        if (Rom == null || SelectedIndex < 0) return;
        string name = L2Songs.Name(Rom, SelectedIndex);
        var dlg = new SaveFileDialog { Title = "Export song as MIDI", Filter = "MIDI file (*.mid)|*.mid", FileName = $"{(name.Length > 0 ? name : "song " + SelectedIndex.ToString("X2"))}.mid" };
        if (dlg.ShowDialog() != true) return;
        try
        {
            var f = L2Songs.Read(Rom, SelectedIndex);
            var song = L2Songs.Decode(f);
            File.WriteAllBytes(dlg.FileName, L2Songs.ToMidi(f));
            int used = song.Channels.Count(c => c.Any(e => e.Kind == SongCodec.Kind.Note && e.B > 0));
            Status = $"Saved {Path.GetFileName(dlg.FileName)}: {used} channels (one track each), tempo {60_000_000.0 / song.MicrosPerQuarter:0.#} BPM. " +
                     "Notes sound where the game plays them (an octave above the game's note numbers), each instrument gets a General MIDI program like its sample (the game's slot is kept in a text, so the file imports back the same); drums are on MIDI channel 10; the loop is marked with loopStart/loopEnd markers.";
        }
        catch (Exception ex) { Status = "Not exported: " + ex.Message; }
    }

    [RelayCommand]
    private void ImportMidi()
    {
        if (Rom == null || Main == null || SelectedIndex < 0) return;
        var dlg = new OpenFileDialog { Title = "Import MIDI", Filter = "MIDI file (*.mid;*.midi)|*.mid;*.midi" };
        if (dlg.ShowDialog() != true) return;
        int index = SelectedIndex;
        byte[] data; List<string> warnings;
        try { data = L2Songs.FromMidi(File.ReadAllBytes(dlg.FileName), L2Songs.Read(Rom, index), out warnings); }
        catch (Exception ex) { Status = "Not imported: " + ex.Message; return; }
        string notes = warnings.Count == 0 ? "" : "\n\n" + string.Join("\n", warnings.Select(w => "- " + w));
        if (System.Windows.MessageBox.Show($"Replace song {Names[index]} with {Path.GetFileName(dlg.FileName)} ({data.Length - 2:N0} of {L2Songs.MaxSize:N0} bytes)?{notes}",
                "Import MIDI", System.Windows.MessageBoxButton.OKCancel, System.Windows.MessageBoxImage.Question) != System.Windows.MessageBoxResult.OK) return;
        StopPlaying();
        try
        {
            int before = Rom.Length;
            L2Rom.WriteFile(Rom, index, data);
            Main.NotifyRomModified($"Lufia II: song {index:X2} from MIDI");
            Notes.Dirty = false;   // the imported song replaces any edits
            Load(index);
            Status = $"Imported {Path.GetFileName(dlg.FileName)} into song {index:X2}." +
                     (Rom.Length > before ? " The ROM grew to make room for it." : "") +
                     (warnings.Count > 0 ? " " + string.Join(" ", warnings) : "");
        }
        catch (Exception ex) { Status = "Not written: " + ex.Message; }
    }
}
