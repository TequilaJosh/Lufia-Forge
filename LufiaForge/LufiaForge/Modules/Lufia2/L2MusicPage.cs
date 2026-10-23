using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LufiaForge.Core.Audio;
using LufiaForge.Core.Lufia2;
using Microsoft.Win32;

namespace LufiaForge.Modules.Lufia2;

/// <summary>The songs (packed files 00-3E): play them on the game's own sound driver, export them as MIDI.</summary>
public partial class L2MusicPage : L2Page
{
    [ObservableProperty] private string _info = "";
    [ObservableProperty] private string _status = "";
    [ObservableProperty] private bool _isPlaying;

    private L2Sound? _sound;
    private AudioOut? _audio;

    protected override IEnumerable<string> ReadNames()
    {
        StopPlaying();
        return Enumerable.Range(0, L2Songs.Count).Select(i =>
        {
            string n = L2Songs.Name(Rom!, i);
            return $"{i:X2}  {(n.Length > 0 ? n : "Song " + i.ToString("X2"))}";
        });
    }

    protected override void Load(int index)
    {
        var f = L2Songs.Read(Rom!, index);
        if (f.Length < 0x52) { Info = "This file is too short to be a song."; return; }
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
            var snd = _sound;
            _audio = new AudioOut(L2Sound.SampleRate, (buf, pairs) => { lock (snd) snd.Render(buf, pairs * 2); });
            if (!_audio.Start()) { StopPlaying(); Status = "No sound device."; return; }
            IsPlaying = true;
            Status = $"Playing song {SelectedIndex:X2} (the game's own sound driver on an emulated sound chip).";
        }
        catch (Exception ex) { StopPlaying(); Status = "Couldn't play: " + ex.Message; }
    }

    [RelayCommand]
    private void Stop() { StopPlaying(); Status = ""; }

    private void StopPlaying()
    {
        _audio?.Dispose(); _audio = null;
        if (_sound != null) { lock (_sound) _sound.Dispose(); _sound = null; }
        IsPlaying = false;
    }

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
            File.WriteAllBytes(dlg.FileName, SongCodec.ToMidi(song, name));
            int used = song.Channels.Count(c => c.Any(e => e.Kind == SongCodec.Kind.Note && e.B > 0));
            Status = $"Saved {Path.GetFileName(dlg.FileName)}: {used} channels (one track each), tempo {60_000_000.0 / song.MicrosPerQuarter:0.#} BPM. " +
                     "Program numbers are the game's sample slots; drums are on MIDI channel 10; the loop is marked with loopStart/loopEnd markers.";
        }
        catch (Exception ex) { Status = "Not exported: " + ex.Message; }
    }
}
