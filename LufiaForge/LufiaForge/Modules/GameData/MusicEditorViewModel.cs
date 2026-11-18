using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LufiaForge.Core;
using LufiaForge.Core.Audio;
using Microsoft.Win32;
using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Windows;

namespace LufiaForge.Modules.GameData;

// the game's 37 songs (resources 00-24). listen to them on the emulated sound chip, change which instruments a
// song loads, export a song to a file and import one (yours, or from another ROM) into any song slot, or as MIDI
// (export, edit in any MIDI editor, import, see SongCodec)
//
// song data (unpacked): [length - 2 (u16)] "SFC" + a byte + the song's internal name (to 0x12), channel
// table, 8 channel pointers at 0x22 (sound RAM, the song gets loaded at $2800), the instruments to load at 0x32
// (8 bytes, 0 = end, below $38), then order lists and phrases. the driver gives songs $2800-$3FFF
public partial class MusicEditorViewModel : GameDataEditorBase, ISongEditorHost
{
    public const int MaxSongBytes = 0x1800;

    // English names for the songs' internal names
    private static readonly Dictionary<string, string> Names = new()
    {
        ["FIELD"] = "World map", ["MURA"] = "Village", ["NAME"] = "Name entry", ["CITY"] = "Town", ["SENTOU"] = "Battle",
        ["TOWER"] = "Tower", ["DOUKUTU"] = "Cave", ["shiro"] = "Castle", ["MINATO"] = "Port", ["HAKAISARETA_"] = "Destroyed town",
        ["HANA"] = "Flower", ["SAIKAI"] = "Reunion", ["ENDING"] = "Ending", ["Final War"] = "Final war", ["SEIREI"] = "Spirit",
        ["SKY"] = "Sky", ["SHIMA"] = "Island", ["DAMY"] = "(dummy)", ["SHINKAI"] = "Deep sea", ["KAITEI-SHIND"] = "Sea floor shrine",
        ["umi"] = "Sea", ["BOS2"] = "Boss 2", ["CHIKASHITHU"] = "Basement", ["BOS3"] = "Boss 3", ["OPENING2"] = "Opening 2",
        ["DENSETUNO TA"] = "Legend (intro)", ["DENSETUNOSHI"] = "Legend 2", ["OTASUMI"] = "Rest", ["SYOURI2"] = "Victory",
        ["KENKYUZYO"] = "Laboratory", ["OMISE"] = "Shop", ["HOKORA"] = "Shrine", ["EVENT"] = "Event", ["OPNING EVENT"] = "Opening event",
    };

    public ObservableCollection<string> Songs { get; } = new();
    [ObservableProperty] private int _selectedIndex = -1;
    [ObservableProperty] private string _instrumentsText = "";
    [ObservableProperty] private string _info = "";
    [ObservableProperty] private bool _isPlaying;
    [ObservableProperty] private string _offsetText = "";

    private LufiaSound? _sound;
    private AudioOut? _audio;

    // the note editor (Notes tab) for the selected song
    public NoteEditorViewModel Notes { get; }
    private readonly System.Windows.Threading.DispatcherTimer _playhead;
    // where the playing song started in the editor (ticks), for the playhead. -1 = playing the ROM's song
    private int _playFrom = -1;
    private (int Start, int End)? _playLoop;
    private int _playTempo = 83;

    public MusicEditorViewModel()
    {
        Notes = new NoteEditorViewModel(this);
        _playhead = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(30) };
        _playhead.Tick += (_, _) => UpdatePlayhead();
    }

    protected override void OnRomLoaded() { Fill(); SelectedIndex = -1; SelectedIndex = 0; }

    // a song's English name from its internal name ("" for one the game doesn't have)
    public static string SongName(RomBuffer rom, int song)
    {
        if (song < 0 || song >= LufiaSound.SongCount) return "";
        string tag;
        try { tag = Tag(LufiaCompression.DecompressResource(rom, song, out _)); } catch { return ""; }
        return Names.GetValueOrDefault(tag, tag.Count(char.IsLetter) >= 3 ? tag : "(no name)");
    }

    private static string Tag(byte[] songData) => new string(songData.Skip(6).Take(12).Select(b => b is >= 32 and < 127 ? (char)b : ' ').ToArray()).Trim();

    private void Fill()
    {
        if (Ctx == null) return;
        int keepIndex = SelectedIndex;
        Songs.Clear();
        for (int song = 0; song < LufiaSound.SongCount; song++)
        {
            string tag;
            try { tag = Tag(LufiaCompression.DecompressResource(Ctx.Rom, song, out _)); } catch { tag = "?"; }
            Songs.Add($"{song:X2}  {Names.GetValueOrDefault(tag, tag.Count(char.IsLetter) >= 3 ? tag : "(no name)")}");
        }
        SelectedIndex = keepIndex;
    }

    // 0 = Song tab, 1 = Notes tab (the footer's Apply writes what that tab edits)
    [ObservableProperty] private int _tabIndex;
    private bool _goingBack;

    partial void OnSelectedIndexChanged(int oldValue, int newValue)
    {
        if (_goingBack) return;
        StopPlaying(); LoadSelected();
    }

    protected override void LoadRecord()
    {
        if (Ctx == null || SelectedIndex < 0) return;
        var data = LufiaCompression.DecompressResource(Ctx.Rom, SelectedIndex, out int packed);
        InstrumentsText = string.Join(" ", data.Skip(0x32).Take(8).TakeWhile(b => b != 0).Select(b => b.ToString("X2")));
        Info = $"Internal name \"{Tag(data)}\", {data.Length:N0} bytes ({packed:N0} packed) of the {MaxSongBytes:N0} the sound driver has room for. " +
               "Instruments 00-0F and 30-3F are always loaded; a song adds up to 8 more (10-37).";
        OffsetText = $"Resource {SelectedIndex:X2} at 0x{LufiaCompression.ResourceOffset(Ctx.Rom, SelectedIndex):X6}";
        try { Notes.Load(data); } catch (Exception ex) { Status = "The note editor couldn't read this song: " + ex.Message; }
    }

    // listening

    [RelayCommand]
    private void Play()
    {
        StopPlaying();
        if (Ctx == null || SelectedIndex < 0 || !SpcNative.Available) { Status = "The sound chip emulator (lufia_spc.dll) isn't available."; return; }
        try
        {
            _sound = LufiaSound.Create(Ctx.Rom);
            if (_sound == null) { Status = "The sound driver couldn't start."; return; }
            _sound.PlaySong(SelectedIndex);
            var sound = _sound;
            Common.SoundPreview.Stop(); _audio = new AudioOut(LufiaSound.SampleRate, (buffer, pairs) => { lock (sound) sound.Render(buffer, pairs * 2); });
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
        _playhead.Stop();
        _audio?.Dispose(); _audio = null;
        if (_sound != null) { lock (_sound) _sound.Dispose(); _sound = null; }
        IsPlaying = false;
        _playFrom = -1;
        Notes.PlayheadTick = -1;
    }

    // starts the sound chip on song data (not the ROM's). true when it plays
    private bool StartData(byte[] data)
    {
        StopPlaying();
        if (Ctx == null || !SpcNative.Available) { Status = "The sound chip emulator (lufia_spc.dll) isn't available."; return false; }
        _sound = LufiaSound.Create(Ctx.Rom);
        if (_sound == null) { Status = "The sound driver couldn't start."; return false; }
        _sound.PlaySongData(data);
        var sound = _sound;
        Common.SoundPreview.Stop(); _audio = new AudioOut(LufiaSound.SampleRate, (buffer, pairs) => { lock (sound) sound.Render(buffer, pairs * 2); });
        if (!_audio.Start()) { StopPlaying(); Status = "No sound device."; return false; }
        IsPlaying = true;
        return true;
    }

    // plays the note editor's song from a tick (whatever is set before it, instrument, volume..., carries over)
    public void PlayEdited(int fromTick)
    {
        if (Ctx == null || SelectedIndex < 0) return;
        byte[] data;
        try { data = BuildSongData(Notes.ToSongFrom(fromTick), out _); }
        catch (Exception ex) { Status = "Can't play: " + ex.Message; return; }
        if (!StartData(data)) return;
        _playFrom = fromTick;
        var loop = Notes.SongLoop();
        _playLoop = loop is { } found && found.End > fromTick ? (Math.Max(found.Start, fromTick), found.End) : null;
        _playTempo = Notes.Tempo;
        _playhead.Start();
        Status = fromTick == 0 ? "Playing the edited song (not in the project yet)." : $"Playing the edited song from {NoteEditorViewModel.TimeText(fromTick)}.";
    }

    private void UpdatePlayhead()
    {
        if (_audio == null || _playFrom < 0) { Notes.PlayheadTick = -1; return; }
        double seconds = _audio.PlayedPairs / (double)LufiaSound.SampleRate;
        double tick = _playFrom + seconds * 1_000_000 / (_playTempo * 125.0);
        if (_playLoop is { } loop && tick >= loop.End) tick = loop.Start + (tick - loop.Start) % (loop.End - loop.Start);
        Notes.PlayheadTick = tick;
    }

    // one note on its own, with the channel's instrument, volume and pan at that point
    public void PreviewNote(int channel, int pitch, int tick)
    {
        if (Ctx == null || SelectedIndex < 0 || _playFrom >= 0 && IsPlaying) return;   // not while the song plays
        var editorChannel = Notes.Channels[channel];
        var song = new SongCodec.Song { Tempo = 100 };
        song.Drum[0] = editorChannel.Drum;
        var list = song.Channels[0];
        foreach (var kind in new[] { SongCodec.Kind.Program, SongCodec.Kind.Volume, SongCodec.Kind.Pan })
            if (editorChannel.Others.Where(e => e.Kind == kind && e.Tick <= tick).OrderBy(e => e.Tick).LastOrDefault() is { } last) list.Add(last with { Tick = 0 });
        list.Add(new SongCodec.Event(0, SongCodec.Kind.Note, pitch, 30, Math.Clamp(Notes.NewVelocity, 1, 127)));
        list.Add(new SongCodec.Event(40, SongCodec.Kind.End));
        try { if (StartData(BuildSongData(song, out _))) Status = $"{NoteEditorViewModel.NoteName(pitch)}"; }
        catch (Exception ex) { Status = "Can't preview: " + ex.Message; }
    }

    // the note editor's host (ISongEditorHost)
    public SongCodec.Song DecodeSong(byte[] songData) => SongCodec.Decode(songData);
    public int MaxSongSize => SongCodec.MaxSize;
    public int NoteNameOffset => SongCodec.Lufia1Transpose;
    public int DefaultInstrument() => LoadedInstruments().FirstOrDefault(0x01);
    public string? InstrumentNote(int instrument) => instrument is >= 0x10 and < 0x30 && !LoadedInstruments().Contains(instrument)
        ? $"Instrument {instrument:X2} is added to the song's load list when you write the notes to the ROM." : null;
    public string? DescribeRaw(byte[] raw) => null;
    public void StopEdited() => StopCommand.Execute(null);

    // the instruments the selected song loads (10-37), in order
    public List<int> LoadedInstruments()
    {
        if (Ctx == null || SelectedIndex < 0) return new List<int>();
        var data = LufiaCompression.DecompressResource(Ctx.Rom, SelectedIndex, out _);
        return data.Skip(0x32).Take(8).TakeWhile(b => b != 0).Select(b => (int)b).ToList();
    }

    public override bool HasPendingEdits => base.HasPendingEdits || Notes.Dirty && Ctx != null;

    // writes the note editor's song to the selected slot
    public void ApplyNotes() => TryApplyNotes();

    // writes the note editor's song. false when it couldn't (the notes stay as edited, Status says why)
    private bool TryApplyNotes()
    {
        if (Ctx == null || SelectedIndex < 0) return false;
        byte[] data; List<string> warnings;
        try { data = BuildSongData(Notes.ToSong(), out warnings); }
        catch (Exception ex) { Refuse("Not written: " + ex.Message); return false; }
        int leftOut = Notes.NotesPastEnd();
        if (leftOut > 0) warnings.Add($"{leftOut} note{(leftOut == 1 ? " starts" : "s start")} after the loop end or song end and {(leftOut == 1 ? "was" : "were")} left out (move the end mark to keep {(leftOut == 1 ? "it" : "them")}).");
        if (!AutoWriting) StopPlaying();
        Notes.Dirty = false;   // not pending while the list of songs gets refilled
        var history = Notes.SaveHistory();
        if (!Write(data, $"Song {SelectedIndex:X2} notes")) { Notes.Dirty = true; return false; }
        Notes.RestoreHistory(history);
        Status = $"Song {SelectedIndex:X2} written ({data.Length - 2:N0} bytes)." + (warnings.Count > 0 ? " " + string.Join(" ", warnings) : "");
        return true;
    }

    // throws away the note editor's changes
    public void ReloadNotes()
    {
        if (Ctx == null || SelectedIndex < 0) return;
        StopPlaying();
        Notes.Load(LufiaCompression.DecompressResource(Ctx.Rom, SelectedIndex, out _));
        Status = "Notes reloaded from the ROM.";
    }

    // instruments

    [RelayCommand]
    private void Apply()
    {
        if (Ctx == null || SelectedIndex < 0) return;
        // the notes go first. writing them reads the song back, so the typed instruments get put back after
        string typed = InstrumentsText;
        if (Notes.Dirty)
        {
            if (!TryApplyNotes()) return;
            Quietly(() => InstrumentsText = typed);
        }
        var data = LufiaCompression.DecompressResource(Ctx.Rom, SelectedIndex, out _);
        // the list the way the song has it isn't checked, some of the game's songs load numbers outside 10-37
        if (InstrumentsText.Trim() == string.Join(" ", data.Skip(0x32).Take(8).TakeWhile(b => b != 0).Select(b => b.ToString("X2")))) return;
        var ids = new List<int>();
        foreach (var part in InstrumentsText.Split(new[] { ' ', ',' }, StringSplitOptions.RemoveEmptyEntries))
        {
            if (!int.TryParse(part, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out int instrument) || instrument is < 0x10 or >= 0x38)
            { Refuse("Instruments: hex numbers 10-37 separated by spaces (00-0F and 30-37 are always loaded)."); return; }
            ids.Add(instrument);
        }
        if (ids.Count > 8) { Refuse("A song can load at most 8 instruments."); return; }
        var wanted = Enumerable.Range(0, 8).Select(i => (byte)(i < ids.Count ? ids[i] : 0)).ToArray();
        if (data.AsSpan(0x32, 8).SequenceEqual(wanted)) return;   // same instruments (packing the song again would move bytes)
        for (int i = 0; i < 8; i++) data[0x32 + i] = wanted[i];
        Write(data, $"Song {SelectedIndex:X2} instruments");
    }

    // writes song data to the selected slot and shows it again. false when it couldn't be written
    // (nothing changed, Status says why)
    private bool Write(byte[] data, string what)
    {
        int song = SelectedIndex;
        bool written = Commit(what, () =>
        {
            bool needsMoreRoom = LufiaCompression.Compress(data).Length > SlotSize(song);
            if (needsMoreRoom && Ctx!.Rom.Length < Core.Maps.MapWriter.ExpandedSize && !AutoWriting &&
                MessageBox.Show("The song doesn't fit in its original space. Expand the ROM to 2 MB and store it in the new space?", "Expand ROM?",
                    MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
                throw new InvalidOperationException("Not written: the ROM wasn't expanded.");
            ResourceWriter.Save(Ctx!.Rom, song, data, allowExpand: true);
        });
        if (!written) return false;
        Quietly(Fill);   // the list dropping its selection while it refills isn't a pick
        LoadSelected();
        return true;
    }

    private int SlotSize(int song)
    {
        int slot = ResourceWriter.SlotSize(Ctx!.Rom, song);
        return slot;
    }

    // files

    [RelayCommand]
    private void Export()
    {
        if (Ctx == null || SelectedIndex < 0) return;
        var data = LufiaCompression.DecompressResource(Ctx.Rom, SelectedIndex, out _);
        var dlg = new SaveFileDialog { Title = "Export song", Filter = "Lufia song (*.lfsong)|*.lfsong", FileName = $"{Songs[SelectedIndex][4..].Trim()}.lfsong" };
        if (dlg.ShowDialog() != true) return;
        File.WriteAllBytes(dlg.FileName, data);
        Status = $"Saved {dlg.FileName} ({data.Length} bytes): the song exactly as the game stores it, ready to import into any song slot.";
    }

    [RelayCommand]
    private void Import()
    {
        if (Ctx == null || SelectedIndex < 0) return;
        var dlg = new OpenFileDialog { Title = "Import song", Filter = "Lufia song (*.lfsong;*.bin)|*.lfsong;*.bin" };
        if (dlg.ShowDialog() != true) return;
        var data = File.ReadAllBytes(dlg.FileName);
        string? problem =
            data.Length < 0x42 ? "too short to be a song" :
            (data[0] | data[1] << 8) != data.Length - 2 ? "its length word doesn't match the file size" :
            data.Length > MaxSongBytes ? $"it's {data.Length} bytes; the sound driver has room for {MaxSongBytes}" :
            data.Skip(0x32).Take(8).TakeWhile(b => b != 0).Any(b => b >= 0x38) ? "it asks for instruments above 37" : null;
        if (problem != null) { Status = $"Not imported: {problem}."; return; }
        if (MessageBox.Show($"Replace song {Songs[SelectedIndex]} with \"{Tag(data)}\" from {Path.GetFileName(dlg.FileName)}?\n\nEverywhere the game plays this song, it plays the new one.",
                "Import song", MessageBoxButton.OKCancel, MessageBoxImage.Question) != MessageBoxResult.OK) return;
        Write(data, $"Song {SelectedIndex:X2} replaced");
    }

    // MIDI

    [RelayCommand]
    private void ExportMidi()
    {
        if (Ctx == null || SelectedIndex < 0) return;
        var dlg = new SaveFileDialog { Title = "Export song as MIDI", Filter = "MIDI file (*.mid)|*.mid", FileName = $"{Songs[SelectedIndex][4..].Trim()}.mid" };
        if (dlg.ShowDialog() != true) return;
        try { Status = ExportMidiTo(dlg.FileName); }
        catch (Exception ex) { Status = "Not exported: " + ex.Message; }
    }

    // writes the selected song as a MIDI file, returns a summary
    public string ExportMidiTo(string path)
    {
        var data = LufiaCompression.DecompressResource(Ctx!.Rom, SelectedIndex, out _);
        var song = SongCodec.Decode(data);
        File.WriteAllBytes(path, SongCodec.ToMidi(song, Songs[SelectedIndex][4..].Trim(), SongCodec.Lufia1Transpose, SongCodec.Lufia1GmProgram, fullLevel: true));
        int used = song.Channels.Count(c => c.Any(e => e.Kind == SongCodec.Kind.Note && e.B > 0));
        return $"Saved {Path.GetFileName(path)}: {used} channels (one track each), tempo {60_000_000.0 / song.MicrosPerQuarter:0.#} BPM. " +
               "Notes sound where the game plays them (an octave above the game's note numbers), each instrument gets a General MIDI program like it (the game's instrument is kept in a text, so the file imports back the same); the loop is marked with loopStart/loopEnd markers.";
    }

    [RelayCommand]
    private void ImportMidi()
    {
        if (Ctx == null || SelectedIndex < 0) return;
        var dlg = new OpenFileDialog { Title = "Import MIDI", Filter = "MIDI file (*.mid;*.midi)|*.mid;*.midi" };
        if (dlg.ShowDialog() != true) return;
        byte[] data; List<string> warnings;
        try { (data, warnings) = SongFromMidi(File.ReadAllBytes(dlg.FileName)); }
        catch (Exception ex) { Status = "Not imported: " + ex.Message; return; }
        string notes = warnings.Count == 0 ? "" : "\n\n" + string.Join("\n", warnings.Select(w => "• " + w));
        if (MessageBox.Show($"Replace song {Songs[SelectedIndex]} with {Path.GetFileName(dlg.FileName)} ({data.Length:N0} bytes)?{notes}",
                "Import MIDI", MessageBoxButton.OKCancel, MessageBoxImage.Question) != MessageBoxResult.OK) return;
        if (!Write(data, $"Song {SelectedIndex:X2} from MIDI")) return;
        Status =$"Imported {Path.GetFileName(dlg.FileName)} into song {SelectedIndex:X2}." + (warnings.Count > 0 ? " " + string.Join(" ", warnings) : "");
    }

    // song data for the selected slot from a MIDI file. the slot keeps its name, channel voices and instrument list,
    // and instruments the MIDI uses that aren't always loaded get added to the list
    public (byte[] Data, List<string> Warnings) SongFromMidi(byte[] midi)
    {
        var song = SongCodec.FromMidi(midi, out var warnings, SongCodec.Lufia1Transpose);
        if (!song.Channels.Any(c => c.Any(e => e.Kind == SongCodec.Kind.Note))) throw new InvalidDataException("the MIDI has no notes");
        var data = BuildSongData(song, out var more);
        warnings.AddRange(more);
        return (data, warnings);
    }

    // song data for the selected slot. keeps the slot's name and instrument list, and instruments the song uses that
    // aren't always loaded get added to the list
    public byte[] BuildSongData(SongCodec.Song song, out List<string> warnings)
    {
        warnings = new List<string>();
        var current = LufiaCompression.DecompressResource(Ctx!.Rom, SelectedIndex, out _);
        var header = current.Skip(2).Take(0x40).ToArray();
        // instruments 10-2F are only there when the song loads them
        var loadList = header.Skip(0x30).Take(8).TakeWhile(b => b != 0).Select(b => (int)b).ToList();
        var missing = song.Channels.SelectMany(c => c).Where(e => e.Kind == SongCodec.Kind.Program && e.A is >= 0x10 and < 0x30)
                          .Select(e => e.A).Distinct().Where(i => !loadList.Contains(i)).OrderBy(i => i).ToList();
        foreach (int instrument in missing)
        {
            if (loadList.Count < 8) loadList.Add(instrument);
            else warnings.Add($"Instrument {instrument:X2} isn't loaded (a song loads at most 8 of 10-37); its notes play with whatever is there.");
        }
        for (int k = 0; k < 8; k++) header[0x30 + k] = (byte)(k < loadList.Count ? loadList[k] : 0);
        return SongCodec.Encode(song, header);
    }

    [RelayCommand]
    private void SaveWav()
    {
        if (Ctx == null || SelectedIndex < 0 || !SpcNative.Available) return;
        var dlg = new SaveFileDialog { Title = "Save as WAV (90 seconds)", Filter = "WAV sound (*.wav)|*.wav", FileName = $"{Songs[SelectedIndex][4..].Trim()}.wav" };
        if (dlg.ShowDialog() != true) return;
        using var sound = LufiaSound.Create(Ctx.Rom);
        if (sound == null) return;
        sound.PlaySong(SelectedIndex);
        int pairs = LufiaSound.SampleRate * 90;
        var pcm = new short[pairs * 2];
        for (int done = 0; done < pairs; done += 4096)
        {
            int count = Math.Min(4096, pairs - done);
            var chunk = new short[count * 2];
            sound.Render(chunk, count * 2);
            Array.Copy(chunk, 0, pcm, done * 2, count * 2);
        }
        using var writer = new BinaryWriter(File.Create(dlg.FileName));
        int bytes = pcm.Length * 2;
        writer.Write("RIFF"u8); writer.Write(36 + bytes); writer.Write("WAVEfmt "u8); writer.Write(16); writer.Write((short)1); writer.Write((short)2);
        writer.Write(LufiaSound.SampleRate); writer.Write(LufiaSound.SampleRate * 4); writer.Write((short)4); writer.Write((short)16);
        writer.Write("data"u8); writer.Write(bytes);
        foreach (var sample in pcm) writer.Write(sample);
        Status = $"Saved {dlg.FileName}.";
    }
}
