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

// any graphics in the ROM: a packed resource (named when the game's tables say what it is) or raw bytes at an
// offset. shown as 2/4/8bpp tiles in a palette you pick, exported to and imported from a PNG picture
public partial class GraphicsEditorViewModel : GameDataEditorBase
{
    // the editor footer shows a ROM offset, this editor doesn't have one of its own
    public string OffsetText => "";

    public ObservableCollection<GraphicsSource> Sources { get; } = new();
    [ObservableProperty] private GraphicsSource? _selectedSource;
    [ObservableProperty] private bool _rawMode;
    [ObservableProperty] private string _rawOffsetText = "0x054251";
    [ObservableProperty] private int _rawTiles = 128;
    [ObservableProperty] private int _depthIndex = 1;   // 2/4/8 bpp
    [ObservableProperty] private int _tilesPerRow = 16;
    // 0 = grey, 1 = map palette, 2 = colours at a ROM offset
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
        for (int tileset = 0, count = Tilesets.Count(rom); tileset < count; tileset++)
        {
            int resource = Tilesets.TilesResource(rom, tileset);
            if (!names.ContainsKey(resource)) names[resource] = $"Town / dungeon tiles (tileset {tileset})";
        }
        Sources.Clear();
        for (int id = 0, count = ResourceIds.Count(rom); id < count; id++)
        {
            int offset = LufiaCompression.ResourceOffset(rom, id);
            if (offset <= 0 || offset >= rom.Length - 2) continue;
            int size;
            try { size = LufiaCompression.DecompressResource(rom, id, out _).Length; } catch { continue; }
            if (size < 256 || size % 16 != 0) continue;
            if (id >= 0x43 && id < 0x43 + Core.Battle.MonsterTable.OriginalPictures) continue;   // monster pictures go in the Monsters tab
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

    protected override void LoadRecord() => Draw();

    private static bool Hex(string text, out int value)
    {
        text = (text ?? "").Trim().TrimStart('$');
        if (text.StartsWith("0x", StringComparison.OrdinalIgnoreCase)) text = text[2..];
        return int.TryParse(text, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out value);
    }

    // the bytes being shown
    private byte[] Load()
    {
        if (Ctx == null) return Array.Empty<byte>();
        var rom = Ctx.Rom;
        if (RawMode)
        {
            if (!Hex(RawOffsetText, out int offset) || offset < 0 || offset >= rom.Length) return Array.Empty<byte>();
            int length = Math.Min(Math.Clamp(RawTiles, 1, 4096) * SnesTileDecoder.BytesPerTile(Depth), rom.Length - offset);
            return rom.ReadBytes(offset, length);
        }
        if (SelectedSource == null) return Array.Empty<byte>();
        return LufiaCompression.DecompressResource(rom, SelectedSource.Resource, out _);
    }

    // the colours for the current depth (2^bpp of them)
    private uint[] Colours()
    {
        int count = 1 << (int)Depth;
        if (Ctx == null || PaletteMode == 0) return SnesPalette.Grayscale(count);
        var rom = Ctx.Rom;
        if (PaletteMode == 2)
            return Hex(PaletteOffsetText, out int paletteOffset) && paletteOffset >= 0 && paletteOffset + count * 2 <= rom.Length ? SnesPalette.ReadFromRom(rom, paletteOffset, count) : SnesPalette.Grayscale(count);
        // map palette: rows 0-1 are shared, rows 2-7 come from the map palette table
        var all = new uint[256];
        for (int i = 0; i < 32; i++) all[i] = SnesPalette.Bgr555ToArgb32(rom.ReadUInt16Le(LufiaMap.SharedPaletteRows + i * 2));
        int mapRows = LufiaMap.MapPaletteTable + (Math.Max(0, MapPalette) + 1) * 0xC0;
        for (int i = 0; i < 96; i++) if (mapRows + i * 2 + 1 < rom.Length) all[32 + i] = SnesPalette.Bgr555ToArgb32(rom.ReadUInt16Le(mapRows + i * 2));
        if (count == 256) return all;
        int start = count == 16 ? Math.Clamp(PaletteRow, 0, 7) * 16 : Math.Clamp(PaletteRow, 0, 63) * 4;
        return all.Skip(start).Take(count).ToArray();
    }

    private (int Tiles, int Cols, int Rows) Grid()
    {
        int tiles = _data.Length / SnesTileDecoder.BytesPerTile(Depth);
        int cols = Math.Clamp(TilesPerRow, 1, 64);
        return (tiles, cols, Math.Max(1, (tiles + cols - 1) / cols));
    }

    // the sheet's colour numbers (cols*8 x rows*8)
    private byte[] Indices()
    {
        var (tiles, cols, rows) = Grid();
        int w = cols * 8, bytesPerTile = SnesTileDecoder.BytesPerTile(Depth);
        var indices = new byte[w * rows * 8];
        for (int tile = 0; tile < tiles; tile++)
        {
            var tilePixels = SnesTileDecoder.DecodeTile(_data, tile * bytesPerTile, Depth);
            for (int y = 0; y < 8; y++) for (int x = 0; x < 8; x++) indices[(tile / cols * 8 + y) * w + tile % cols * 8 + x] = tilePixels[y * 8 + x];
        }
        return indices;
    }

    private void Draw()
    {
        if (Ctx == null) return;
        try { _data = Load(); } catch (Exception ex) { _data = Array.Empty<byte>(); Info = ex.Message; }
        if (_data.Length == 0) { Picture = null; Info = "Nothing to show here."; return; }
        var (tiles, cols, rows) = Grid();
        var palette = Colours();
        var indices = Indices();
        var bmp = BitmapSource.Create(cols * 8, rows * 8, 96, 96, PixelFormats.Bgra32, null, indices.Select(i => palette[i]).ToArray(), cols * 32);
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
        int w = cols * 8, h = rows * 8, colorCount = 1 << (int)Depth;
        var indexedPng = IndexedPng.TryLoad(path);
        BitmapSource? picture = null;
        if (indexedPng == null) try { picture = new BitmapImage(new Uri(path)); } catch (Exception ex) { Status = "Can't read the picture: " + ex.Message; return; }
        int pictureWidth = indexedPng?.Width ?? picture!.PixelWidth, pictureHeight = indexedPng?.Height ?? picture!.PixelHeight;
        if (pictureWidth != w || pictureHeight != h) { Status = $"The picture must be {w} x {h} pixels (as exported with these settings)."; return; }
        var indices = new byte[w * h];
        string how;
        if (indexedPng is { } png)
        {
            indices = png.Indices;
            if (indices.Any(i => i >= colorCount)) { Status = $"The picture uses colour numbers above {colorCount - 1}; at this depth there are {colorCount} colours."; return; }
            how = "colour numbers kept";
        }
        else
        {
            var bgra = new FormatConvertedBitmap(picture, PixelFormats.Bgra32, null, 0);
            var pixels = new uint[w * h]; bgra.CopyPixels(pixels, w * 4, 0);
            var palette = Colours();
            for (int i = 0; i < pixels.Length; i++)
            {
                if (pixels[i] >> 24 < 128) { indices[i] = 0; continue; }
                long best = long.MaxValue;
                for (int color = 0; color < colorCount; color++)
                {
                    long dr = (long)(pixels[i] >> 16 & 255) - (palette[color] >> 16 & 255), dg = (long)(pixels[i] >> 8 & 255) - (palette[color] >> 8 & 255), db = (long)(pixels[i] & 255) - (palette[color] & 255);
                    long distance = dr * dr * 3 + dg * dg * 4 + db * db * 2;
                    if (distance < best) { best = distance; indices[i] = (byte)color; }
                }
            }
            how = "colours matched to the palette shown";
        }
        int bytesPerTile = SnesTileDecoder.BytesPerTile(Depth);
        var data = (byte[])_data.Clone();
        for (int tile = 0; tile < tiles; tile++)
        {
            var tilePixels = new byte[64];
            for (int y = 0; y < 8; y++) for (int x = 0; x < 8; x++) tilePixels[y * 8 + x] = indices[(tile / cols * 8 + y) * w + tile % cols * 8 + x];
            SnesTileDecoder.EncodeTile(tilePixels, Depth).CopyTo(data, tile * bytesPerTile);
        }
        var rom = Ctx.Rom;
        if (RawMode)
        {
            if (!Hex(RawOffsetText, out int offset)) return;
            Commit($"Graphics at {offset:X6}", () => rom.WriteBytes(offset, data));
            Status = $"Graphics written at 0x{offset:X6} ({how}); Save Project keeps it.";
        }
        else
        {
            int resource = SelectedSource!.Resource;
            bool expand = rom.Length >= MapWriter.ExpandedSize;
            if (LufiaCompression.Compress(data).Length > ResourceWriter.SlotSize(rom, resource) && !expand)
            {
                if (MessageBox.Show("The new graphics don't fit in their original space. Expand the ROM to 2 MB?", "Expand ROM?", MessageBoxButton.YesNo) != MessageBoxResult.Yes)
                { Status = "Not imported."; return; }
                expand = true;
            }
            ResourceWriter.Result? result = null;
            Commit($"Graphics resource {resource:X3}", () => result = ResourceWriter.Save(rom, resource, data, expand));
            if (result != null) Status = $"Resource {resource:X3} written ({how}{(result.Moved ? ", moved to the expanded space" : "")}); Save Project keeps it.";
        }
        Draw();
    }

    [RelayCommand]
    private void Apply() => Status = "Graphics are written when imported.";
}
