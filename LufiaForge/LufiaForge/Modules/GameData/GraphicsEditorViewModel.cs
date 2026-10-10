using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LufiaForge.Core;
using LufiaForge.Core.Maps;
using LufiaForge.Modules.TileViewer;
using Microsoft.Win32;
using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Color = System.Windows.Media.Color;

namespace LufiaForge.Modules.GameData;

public sealed class GraphicsSource
{
    public int Resource { get; init; } = -1;
    public string Label { get; init; } = "";
    public int Size { get; init; }
}

/// <summary>
/// Any graphics in the ROM: a packed resource (named when the game's tables say what it is) or raw bytes at an
/// offset, shown as 2/4/8bpp tiles in a chosen palette, exported to and imported from a PNG picture.
/// </summary>
public partial class GraphicsEditorViewModel : GameDataEditorBase
{
    /// <summary>(The editor footer shows a ROM offset; this editor has none of its own.)</summary>
    public string OffsetText => "";

    public ObservableCollection<GraphicsSource> Sources { get; } = new();
    [ObservableProperty] private GraphicsSource? _selectedSource;
    [ObservableProperty] private bool _rawMode;
    [ObservableProperty] private string _rawOffsetText = "0x054251";
    [ObservableProperty] private int _rawTiles = 128;
    [ObservableProperty] private int _depthIndex = 1;   // 2/4/8 bpp
    [ObservableProperty] private int _tilesPerRow = 16;
    /// <summary>0 = grey, 1 = map palette, 2 = colours at a ROM offset.</summary>
    [ObservableProperty] private int _paletteMode = 1;
    [ObservableProperty] private int _mapPalette;
    [ObservableProperty] private int _paletteRow = 2;
    [ObservableProperty] private string _paletteOffsetText = "0x011200";
    [ObservableProperty] private ImageSource? _picture;
    [ObservableProperty] private string _info = "";

    public string[] Depths { get; } = { "2bpp (4 colours)", "4bpp (16 colours)", "8bpp (256 colours)" };
    public string[] PaletteModes { get; } = { "Grey", "Map palette", "Colours at a ROM offset" };
    private BitDepth Depth => DepthIndex switch { 0 => BitDepth.Bpp2, 2 => BitDepth.Bpp8, _ => BitDepth.Bpp4 };
    private byte[] _data = Array.Empty<byte>();

    protected override void OnRomLoaded()
    {
        if (Ctx == null) return;
        var rom = Ctx.Rom;
        var names = new Dictionary<int, string>
        {
            [0xB1] = "Title screen graphics",
            [LufiaMap.WorldTilesResource] = "World map tiles",
            [0x13D] = "Battle sprites (party, command ring, numbers)",
        };
        for (int t = 0; t < 12; t++)
        {
            int r = 0xB1 + rom.ReadByte(LufiaMap.TilesSelectTable + t);
            if (!names.ContainsKey(r)) names[r] = $"Town / dungeon tiles (tileset {t})";
        }
        Sources.Clear();
        for (int id = 0; id < 0x200; id++)
        {
            int at = LufiaCompression.ResourceOffset(rom, id);
            if (at <= 0 || at >= rom.Length - 2) continue;
            int size;
            try { size = LufiaCompression.DecompressResource(rom, id, out _).Length; } catch { continue; }
            if (size < 256 || size % 16 != 0) continue;
            if (id >= 0x43 && id < 0x43 + Core.Battle.MonsterGraphics.Count) continue;   // monster pictures: Monsters tab
            Sources.Add(new GraphicsSource { Resource = id, Size = size, Label = $"{id:X3}  {names.GetValueOrDefault(id, "resource")}  ({size / 32} tiles at 4bpp)" });
        }
        var first = Sources.OrderBy(s => names.ContainsKey(s.Resource) ? 0 : 1).First();
        SelectedSource = first;
        Draw();
    }

    partial void OnSelectedSourceChanged(GraphicsSource? value) { if (!RawMode) Draw(); }
    partial void OnRawModeChanged(bool value) => Draw();
    partial void OnRawOffsetTextChanged(string value) { if (RawMode) Draw(); }
    partial void OnRawTilesChanged(int value) { if (RawMode) Draw(); }
    partial void OnDepthIndexChanged(int value) => Draw();
    partial void OnTilesPerRowChanged(int value) => Draw();
    partial void OnPaletteModeChanged(int value) => Draw();
    partial void OnMapPaletteChanged(int value) => Draw();
    partial void OnPaletteRowChanged(int value) => Draw();
    partial void OnPaletteOffsetTextChanged(string value) => Draw();

    protected override void LoadSelected() => Draw();

    private static bool Hex(string s, out int v)
    {
        s = (s ?? "").Trim().TrimStart('$');
        if (s.StartsWith("0x", StringComparison.OrdinalIgnoreCase)) s = s[2..];
        return int.TryParse(s, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out v);
    }

    /// <summary>The bytes being shown.</summary>
    private byte[] Load()
    {
        if (Ctx == null) return Array.Empty<byte>();
        var rom = Ctx.Rom;
        if (RawMode)
        {
            if (!Hex(RawOffsetText, out int off) || off < 0 || off >= rom.Length) return Array.Empty<byte>();
            int len = Math.Min(Math.Clamp(RawTiles, 1, 4096) * SnesTileDecoder.BytesPerTile(Depth), rom.Length - off);
            return rom.ReadBytes(off, len);
        }
        if (SelectedSource == null) return Array.Empty<byte>();
        return LufiaCompression.DecompressResource(rom, SelectedSource.Resource, out _);
    }

    /// <summary>The colours for the current depth (2^bpp entries).</summary>
    private uint[] Colours()
    {
        int n = 1 << (int)Depth;
        if (Ctx == null || PaletteMode == 0) return SnesPalette.Grayscale(n);
        var rom = Ctx.Rom;
        if (PaletteMode == 2)
            return Hex(PaletteOffsetText, out int po) && po >= 0 && po + n * 2 <= rom.Length ? SnesPalette.ReadFromRom(rom, po, n) : SnesPalette.Grayscale(n);
        // map palette: rows 0-1 shared, rows 2-7 from the map palette table
        var all = new uint[256];
        for (int i = 0; i < 32; i++) all[i] = SnesPalette.Bgr555ToArgb32(rom.ReadUInt16Le(LufiaMap.SharedPaletteRows + i * 2));
        int rows = LufiaMap.MapPaletteTable + (Math.Max(0, MapPalette) + 1) * 0xC0;
        for (int i = 0; i < 96; i++) if (rows + i * 2 + 1 < rom.Length) all[32 + i] = SnesPalette.Bgr555ToArgb32(rom.ReadUInt16Le(rows + i * 2));
        if (n == 256) return all;
        int start = n == 16 ? Math.Clamp(PaletteRow, 0, 7) * 16 : Math.Clamp(PaletteRow, 0, 63) * 4;
        return all.Skip(start).Take(n).ToArray();
    }

    private (int Tiles, int Cols, int Rows) Grid()
    {
        int tiles = _data.Length / SnesTileDecoder.BytesPerTile(Depth);
        int cols = Math.Clamp(TilesPerRow, 1, 64);
        return (tiles, cols, Math.Max(1, (tiles + cols - 1) / cols));
    }

    /// <summary>Colour numbers of the sheet (cols*8 x rows*8).</summary>
    private byte[] Indices()
    {
        var (tiles, cols, rows) = Grid();
        int w = cols * 8, bpt = SnesTileDecoder.BytesPerTile(Depth);
        var idx = new byte[w * rows * 8];
        for (int t = 0; t < tiles; t++)
        {
            var px = SnesTileDecoder.DecodeTile(_data, t * bpt, Depth);
            for (int y = 0; y < 8; y++) for (int x = 0; x < 8; x++) idx[(t / cols * 8 + y) * w + t % cols * 8 + x] = px[y * 8 + x];
        }
        return idx;
    }

    private void Draw()
    {
        if (Ctx == null) return;
        try { _data = Load(); } catch (Exception ex) { _data = Array.Empty<byte>(); Info = ex.Message; }
        if (_data.Length == 0) { Picture = null; Info = "Nothing to show here."; return; }
        var (tiles, cols, rows) = Grid();
        var pal = Colours();
        var idx = Indices();
        var bmp = BitmapSource.Create(cols * 8, rows * 8, 96, 96, PixelFormats.Bgra32, null, idx.Select(i => pal[i]).ToArray(), cols * 32);
        bmp.Freeze();
        Picture = bmp;
        Info = $"{tiles} tiles, {cols * 8} x {rows * 8} pixels" + (RawMode ? " (raw bytes in the ROM)" : $" (resource {SelectedSource?.Resource:X3}, {_data.Length} bytes unpacked)");
    }

    [RelayCommand]
    private void Export()
    {
        if (_data.Length == 0) return;
        var (_, cols, rows) = Grid();
        string name = RawMode ? $"graphics {RawOffsetText}.png" : $"graphics {SelectedSource?.Resource:X3}.png";
        var dlg = new SaveFileDialog { Title = "Export graphics", Filter = "PNG picture (*.png)|*.png", FileName = name };
        if (dlg.ShowDialog() != true) return;
        ExportTo(dlg.FileName);
    }

    public void ExportTo(string path)
    {
        var (_, cols, rows) = Grid();
        IndexedPng.Save(path, cols * 8, rows * 8, Indices(), Colours(), firstTransparent: false);
        Status = $"Saved {path} ({cols * 8} x {rows * 8}, indexed). Draw on it and import it back with the same settings.";
    }

    [RelayCommand]
    private void Import()
    {
        if (_data.Length == 0) return;
        var dlg = new OpenFileDialog { Title = "Import graphics", Filter = "Pictures (*.png;*.bmp;*.gif)|*.png;*.bmp;*.gif" };
        if (dlg.ShowDialog() != true) return;
        ImportFrom(dlg.FileName);
    }

    public void ImportFrom(string path)
    {
        if (Ctx == null || _data.Length == 0) return;
        var (tiles, cols, rows) = Grid();
        int w = cols * 8, h = rows * 8, n = 1 << (int)Depth;
        var ip = IndexedPng.TryLoad(path);
        BitmapSource? src = null;
        if (ip == null) try { src = new BitmapImage(new Uri(path)); } catch (Exception ex) { Status = "Can't read the picture: " + ex.Message; return; }
        int pw = ip?.Width ?? src!.PixelWidth, ph = ip?.Height ?? src!.PixelHeight;
        if (pw != w || ph != h) { Status = $"The picture must be {w} x {h} pixels (as exported with these settings)."; return; }
        var idx = new byte[w * h];
        string how;
        if (ip is { } png)
        {
            idx = png.Indices;
            if (idx.Any(i => i >= n)) { Status = $"The picture uses colour numbers above {n - 1}; at this depth there are {n} colours."; return; }
            how = "colour numbers kept";
        }
        else
        {
            var bgra = new FormatConvertedBitmap(src, PixelFormats.Bgra32, null, 0);
            var px = new uint[w * h]; bgra.CopyPixels(px, w * 4, 0);
            var pal = Colours();
            for (int i = 0; i < px.Length; i++)
            {
                if (px[i] >> 24 < 128) { idx[i] = 0; continue; }
                long best = long.MaxValue;
                for (int c = 0; c < n; c++)
                {
                    long dr = (long)(px[i] >> 16 & 255) - (pal[c] >> 16 & 255), dg = (long)(px[i] >> 8 & 255) - (pal[c] >> 8 & 255), db = (long)(px[i] & 255) - (pal[c] & 255);
                    long e = dr * dr * 3 + dg * dg * 4 + db * db * 2;
                    if (e < best) { best = e; idx[i] = (byte)c; }
                }
            }
            how = "colours matched to the palette shown";
        }
        int bpt = SnesTileDecoder.BytesPerTile(Depth);
        var data = (byte[])_data.Clone();
        for (int t = 0; t < tiles; t++)
        {
            var px = new byte[64];
            for (int y = 0; y < 8; y++) for (int x = 0; x < 8; x++) px[y * 8 + x] = idx[(t / cols * 8 + y) * w + t % cols * 8 + x];
            SnesTileDecoder.EncodeTile(px, Depth).CopyTo(data, t * bpt);
        }
        var rom = Ctx.Rom;
        if (RawMode)
        {
            if (!Hex(RawOffsetText, out int off)) return;
            Commit($"Graphics at {off:X6}", () => rom.WriteBytes(off, data));
            Status = $"Graphics written at 0x{off:X6} ({how}); unsaved until File > Save ROM.";
        }
        else
        {
            int res = SelectedSource!.Resource;
            bool expand = rom.Length >= MapWriter.ExpandedSize;
            if (LufiaCompression.Compress(data).Length > ResourceWriter.SlotSize(rom, res) && !expand)
            {
                if (MessageBox.Show("The new graphics don't fit in their original space. Expand the ROM to 2 MB?", "Expand ROM?", MessageBoxButton.YesNo) != MessageBoxResult.Yes)
                { Status = "Not imported."; return; }
                expand = true;
            }
            ResourceWriter.Result? r = null;
            Commit($"Graphics resource {res:X3}", () => r = ResourceWriter.Save(rom, res, data, expand));
            if (r != null) Status = $"Resource {res:X3} written ({how}{(r.Moved ? ", moved to the expanded space" : "")}); unsaved until File > Save ROM.";
        }
        Draw();
    }

    [RelayCommand]
    private void Apply() => Status = "Graphics are written when imported.";
}
