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

/// <summary>
/// The game's 37 songs (resources 00-24): listen to them on the emulated sound chip, change which instruments a
/// song loads, export a song to a file and import one (yours, or from another ROM) into any song slot.
///
/// Song data (unpacked): [length - 2 (u16)] "SFC" + a byte + the song's internal name (to 0x12), channel
/// table, 8 channel pointers at 0x22 (sound RAM, the song is loaded at $2800), the instruments to load at 0x32
/// (8 bytes, 0 = end, below $38), then order lists and phrases. The driver gives songs $2800-$3FFF.
/// </summary>
public partial class MusicEditorViewModel : GameDataEditorBase
{
    public const int MaxSongBytes = 0x1800;

    /// <summary>English names for the songs' internal names.</summary>
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

    protected override void OnRomLoaded() { Fill(); SelectedIndex = -1; SelectedIndex = 0; }

    private static string Tag(byte[] d) => new string(d.Skip(6).Take(12).Select(b => b is >= 32 and < 127 ? (char)b : ' ').ToArray()).Trim();

    private void Fill()
    {
        if (Ctx == null) return;
        int keep = SelectedIndex;
        Songs.Clear();
        for (int s = 0; s < LufiaSound.SongCount; s++)
        {
            string tag;
            try { tag = Tag(LufiaCompression.DecompressResource(Ctx.Rom, s, out _)); } catch { tag = "?"; }
            Songs.Add($"{s:X2}  {Names.GetValueOrDefault(tag, tag.Count(char.IsLetter) >= 3 ? tag : "(no name)")}");
        }
        SelectedIndex = keep;
    }

    partial void OnSelectedIndexChanged(int value) { StopPlaying(); LoadSelected(); }

    protected override void LoadSelected()
    {
        if (Ctx == null || SelectedIndex < 0) return;
        var d = LufiaCompression.DecompressResource(Ctx.Rom, SelectedIndex, out int packed);
        InstrumentsText = string.Join(" ", d.Skip(0x32).Take(8).TakeWhile(b => b != 0).Select(b => b.ToString("X2")));
        Info = $"Internal name \"{Tag(d)}\", {d.Length:N0} bytes ({packed:N0} packed) of the {MaxSongBytes:N0} the sound driver has room for. " +
               "Instruments 00-0F and 30-3F are always loaded; a song adds up to 8 more (10-37).";
        OffsetText = $"Resource {SelectedIndex:X2} at 0x{LufiaCompression.ResourceOffset(Ctx.Rom, SelectedIndex):X6}";
    }

    // ── listening ───────────────────────────────────────────────────────────

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
            var snd = _sound;
            _audio = new AudioOut(LufiaSound.SampleRate, (buf, pairs) => { lock (snd) snd.Render(buf, pairs * 2); });
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

    // ── instruments ─────────────────────────────────────────────────────────

    [RelayCommand]
    private void Apply()
    {
        if (Ctx == null || SelectedIndex < 0) return;
        var ids = new List<int>();
        foreach (var part in InstrumentsText.Split(new[] { ' ', ',' }, StringSplitOptions.RemoveEmptyEntries))
        {
            if (!int.TryParse(part, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out int v) || v is < 0x10 or >= 0x38)
            { Status = "Instruments: hex numbers 10-37 separated by spaces (00-0F and 30-37 are always loaded)."; return; }
            ids.Add(v);
        }
        if (ids.Count > 8) { Status = "A song can load at most 8 instruments."; return; }
        var d = LufiaCompression.DecompressResource(Ctx.Rom, SelectedIndex, out _);
        for (int i = 0; i < 8; i++) d[0x32 + i] = (byte)(i < ids.Count ? ids[i] : 0);
        Write(d, $"Song {SelectedIndex:X2} instruments");
    }

    private void Write(byte[] data, string what)
    {
        int song = SelectedIndex;
        Commit(what, () =>
        {
            bool needs = LufiaCompression.Compress(data).Length > SlotSize(song);
            if (needs && Ctx!.Rom.Length < Core.Maps.MapWriter.ExpandedSize &&
                MessageBox.Show("The song doesn't fit in its original space. Expand the ROM to 2 MB and store it in the new space?", "Expand ROM?",
                    MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
                throw new InvalidOperationException("Not written: the ROM wasn't expanded.");
            ResourceWriter.Save(Ctx!.Rom, song, data, allowExpand: true);
        });
        Fill();
        LoadSelected();
    }

    private int SlotSize(int song)
    {
        LufiaCompression.Decompress(Ctx!.Rom, LufiaCompression.ResourceOffset(Ctx.Rom, song), out int slot);
        return slot;
    }

    // ── files ───────────────────────────────────────────────────────────────

    [RelayCommand]
    private void Export()
    {
        if (Ctx == null || SelectedIndex < 0) return;
        var d = LufiaCompression.DecompressResource(Ctx.Rom, SelectedIndex, out _);
        var dlg = new SaveFileDialog { Title = "Export song", Filter = "Lufia song (*.lfsong)|*.lfsong", FileName = $"{Songs[SelectedIndex][4..].Trim()}.lfsong" };
        if (dlg.ShowDialog() != true) return;
        File.WriteAllBytes(dlg.FileName, d);
        Status = $"Saved {dlg.FileName} ({d.Length} bytes): the song exactly as the game stores it, ready to import into any song slot.";
    }

    [RelayCommand]
    private void Import()
    {
        if (Ctx == null || SelectedIndex < 0) return;
        var dlg = new OpenFileDialog { Title = "Import song", Filter = "Lufia song (*.lfsong;*.bin)|*.lfsong;*.bin" };
        if (dlg.ShowDialog() != true) return;
        var d = File.ReadAllBytes(dlg.FileName);
        string? problem =
            d.Length < 0x42 ? "too short to be a song" :
            (d[0] | d[1] << 8) != d.Length - 2 ? "its length word doesn't match the file size" :
            d.Length > MaxSongBytes ? $"it's {d.Length} bytes; the sound driver has room for {MaxSongBytes}" :
            d.Skip(0x32).Take(8).TakeWhile(b => b != 0).Any(b => b >= 0x38) ? "it asks for instruments above 37" : null;
        if (problem != null) { Status = $"Not imported: {problem}."; return; }
        if (MessageBox.Show($"Replace song {Songs[SelectedIndex]} with \"{Tag(d)}\" from {Path.GetFileName(dlg.FileName)}?\n\nEverywhere the game plays this song, it plays the new one.",
                "Import song", MessageBoxButton.OKCancel, MessageBoxImage.Question) != MessageBoxResult.OK) return;
        Write(d, $"Song {SelectedIndex:X2} replaced");
    }

    [RelayCommand]
    private void SaveWav()
    {
        if (Ctx == null || SelectedIndex < 0 || !SpcNative.Available) return;
        var dlg = new SaveFileDialog { Title = "Save as WAV (90 seconds)", Filter = "WAV sound (*.wav)|*.wav", FileName = $"{Songs[SelectedIndex][4..].Trim()}.wav" };
        if (dlg.ShowDialog() != true) return;
        using var snd = LufiaSound.Create(Ctx.Rom);
        if (snd == null) return;
        snd.PlaySong(SelectedIndex);
        int pairs = LufiaSound.SampleRate * 90;
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
        w.Write(LufiaSound.SampleRate); w.Write(LufiaSound.SampleRate * 4); w.Write((short)4); w.Write((short)16);
        w.Write("data"u8); w.Write(bytes);
        foreach (var s in pcm) w.Write(s);
        Status = $"Saved {dlg.FileName}.";
    }
}
