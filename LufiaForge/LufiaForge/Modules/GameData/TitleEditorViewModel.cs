using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LufiaForge.Core.Maps;
using Microsoft.Win32;
using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Color = System.Windows.Media.Color;

namespace LufiaForge.Modules.GameData;

/// <summary>The title screen (see <see cref="TitleScreen"/>): preview, layer pictures out and in, colours.</summary>
public partial class TitleEditorViewModel : GameDataEditorBase
{
    private TitleScreen? _title;
    [ObservableProperty] private ImageSource? _preview;
    [ObservableProperty] private ImageSource? _swatches;
    [ObservableProperty] private string _info = "";
    [ObservableProperty] private bool _dirty;

    protected override void OnRomLoaded() => LoadSelected();

    protected override void LoadSelected()
    {
        if (Ctx == null) return;
        try { _title = TitleScreen.Load(Ctx.Rom); }
        catch (Exception ex) { _title = null; Info = "The title screen can't be read: " + ex.Message; return; }
        Dirty = false;
        Redraw();
        Info = $"18 x 16 blocks, {_title.BlockCount} blocks (room for {_title.MaxBlocks}), graphics resource B1, colours: map palette {TitleScreen.PaletteIndex}.";
    }

    private void Redraw()
    {
        if (_title == null) return;
        var bmp = BitmapSource.Create(256, 224, 96, 96, PixelFormats.Bgra32, null, _title.RenderScreen(), 256 * 4);
        bmp.Freeze();
        Preview = bmp;
        var sw = new uint[16 * 8 * 64];
        for (int r = 0; r < 8; r++)
            for (int c = 0; c < 16; c++)
                for (int y = 0; y < 8; y++)
                    for (int x = 0; x < 8; x++)
                        sw[(r * 8 + y) * 128 + c * 8 + x] = c == 0 ? (((x ^ y) & 4) != 0 ? 0xFF606060u : 0xFF404040u) : TitleScreen.Argb(_title.Palette555[r * 16 + c]);
        var s = BitmapSource.Create(128, 64, 96, 96, PixelFormats.Bgra32, null, sw, 128 * 4);
        s.Freeze();
        Swatches = s;
    }

    private List<Color> PngPalette()
    {
        var list = new List<Color>();
        for (int i = 0; i < 128; i++)
        {
            uint a = TitleScreen.Argb(_title!.Palette555[i]);
            list.Add(i % 16 == 0 ? Color.FromArgb(0, 0, 0, 0) : Color.FromRgb((byte)(a >> 16), (byte)(a >> 8), (byte)a));
        }
        return list;
    }

    [RelayCommand] private void ExportFront() => ExportLayer(true);
    [RelayCommand] private void ExportBack() => ExportLayer(false);
    [RelayCommand] private void ImportFront() => ImportLayer(true);
    [RelayCommand] private void ImportBack() => ImportLayer(false);

    private void ExportLayer(bool front)
    {
        if (_title == null) return;
        var dlg = new SaveFileDialog { Title = "Export title layer", Filter = "PNG picture (*.png)|*.png", FileName = front ? "title front layer.png" : "title back layer.png" };
        if (dlg.ShowDialog() != true) return;
        ExportLayerTo(dlg.FileName, front);
    }

    public void ExportLayerTo(string path, bool front)
    {
        if (_title == null) return;
        var dlg = new { FileName = path };
        int w = _title.Width * 16, h = _title.Height * 16;
        var bmp = BitmapSource.Create(w, h, 96, 96, PixelFormats.Indexed8, new BitmapPalette(PngPalette()), _title.LayerIndices(front), w);
        var enc = new PngBitmapEncoder(); enc.Frames.Add(BitmapFrame.Create(bmp));
        using (var fs = File.Create(dlg.FileName)) enc.Save(fs);
        Status = $"Saved {dlg.FileName} ({w} x {h}; the screen shows x {TitleScreen.ScreenX}-{TitleScreen.ScreenX + 255}, y {TitleScreen.ScreenY}-{TitleScreen.ScreenY + 223}). " +
                 "Keep it indexed: colour numbers 16 x row + colour, transparent = 0 of each row.";
    }

    [RelayCommand]
    private void ExportScreen()
    {
        if (_title == null) return;
        var dlg = new SaveFileDialog { Title = "Export title screen", Filter = "PNG picture (*.png)|*.png", FileName = "title screen.png" };
        if (dlg.ShowDialog() != true) return;
        var bmp = BitmapSource.Create(256, 224, 96, 96, PixelFormats.Bgra32, null, _title.RenderScreen(), 256 * 4);
        var enc = new PngBitmapEncoder(); enc.Frames.Add(BitmapFrame.Create(bmp));
        using (var fs = File.Create(dlg.FileName)) enc.Save(fs);
        Status = $"Saved {dlg.FileName}.";
    }

    private void ImportLayer(bool front)
    {
        if (_title == null) return;
        var dlg = new OpenFileDialog { Title = $"Import title {(front ? "front" : "back")} layer", Filter = "Pictures (*.png;*.bmp;*.gif)|*.png;*.bmp;*.gif" };
        if (dlg.ShowDialog() != true) return;
        ImportLayerFrom(dlg.FileName, front);
    }

    /// <summary>Imports a layer picture from a file (the dialog's part split off so it can be tested).</summary>
    public void ImportLayerFrom(string path, bool front)
    {
        if (_title == null) return;
        int w = _title.Width * 16, h = _title.Height * 16;
        BitmapSource src;
        try { src = new BitmapImage(new Uri(path)); } catch (Exception ex) { Status = "Can't read the picture: " + ex.Message; return; }
        if (src.PixelWidth != w || src.PixelHeight != h) { Status = $"The layer picture must be {w} x {h} pixels (as exported)."; return; }
        var idx = new byte[w * h];
        string how;
        if (src.Format == PixelFormats.Indexed8 && src.Palette != null)
        {
            src.CopyPixels(idx, w, 0);
            if (idx.Any(i => i >= 128)) { Status = "The picture uses colour numbers above 127; only rows 0-7 (128 colours) exist."; return; }
            // the picture's colours of rows 2-7 become the title's colours
            var cols = src.Palette.Colors;
            for (int i = 32; i < Math.Min(128, cols.Count); i++)
                if (i % 16 != 0) _title.Palette555[i] = Bgr555(cols[i]);
            how = "colour numbers kept, colours of rows 2-7 taken from the picture";
        }
        else
        {
            var bgra = new FormatConvertedBitmap(src, PixelFormats.Bgra32, null, 0);
            var px = new uint[w * h]; bgra.CopyPixels(px, w * 4, 0);
            idx = MatchColours(px, w, h);
            how = "colours matched to the title's palette (each 8x8 tile to its best row)";
        }
        var other = _title.LayerIndices(!front);
        try
        {
            string built = front ? _title.Rebuild(idx, other) : _title.Rebuild(other, idx);
            Dirty = true;
            Redraw();
            Status = $"{(front ? "Front" : "Back")} layer imported ({how}; {built}). Click Apply to write it to the ROM.";
        }
        catch (InvalidOperationException ex) { Status = "Not imported: " + ex.Message; LoadSelected(); }
    }

    private static ushort Bgr555(Color c) => (ushort)((c.R >> 3) | (c.G >> 3) << 5 | (c.B >> 3) << 10);

    /// <summary>Full-colour picture → colour numbers: each 8x8 tile uses the row (2-7) that fits it best.</summary>
    private byte[] MatchColours(uint[] px, int w, int h)
    {
        var idx = new byte[w * h];
        var pal = Enumerable.Range(0, 128).Select(i => TitleScreen.Argb(_title!.Palette555[i])).ToArray();
        long Err(uint a, uint b)
        {
            long dr = (long)(a >> 16 & 255) - (b >> 16 & 255), dg = (long)(a >> 8 & 255) - (b >> 8 & 255), db = (long)(a & 255) - (b & 255);
            return dr * dr * 3 + dg * dg * 4 + db * db * 2;
        }
        for (int ty = 0; ty < h; ty += 8)
            for (int tx = 0; tx < w; tx += 8)
            {
                int bestRow = 2; long bestErr = long.MaxValue;
                for (int row = 2; row < 8; row++)
                {
                    long e = 0;
                    for (int y = 0; y < 8; y++)
                        for (int x = 0; x < 8; x++)
                        {
                            uint p = px[(ty + y) * w + tx + x];
                            if (p >> 24 < 128) continue;
                            long m = long.MaxValue;
                            for (int c = 1; c < 16; c++) m = Math.Min(m, Err(p, pal[row * 16 + c]));
                            e += m;
                        }
                    if (e < bestErr) { bestErr = e; bestRow = row; }
                }
                for (int y = 0; y < 8; y++)
                    for (int x = 0; x < 8; x++)
                    {
                        uint p = px[(ty + y) * w + tx + x];
                        if (p >> 24 < 128) continue;
                        int best = 1; long m = long.MaxValue;
                        for (int c = 1; c < 16; c++) { long e = Err(p, pal[bestRow * 16 + c]); if (e < m) { m = e; best = c; } }
                        idx[(ty + y) * w + tx + x] = (byte)(bestRow * 16 + best);
                    }
            }
        return idx;
    }

    [RelayCommand]
    private void Apply()
    {
        if (Ctx == null || _title == null) return;
        var rom = Ctx.Rom;
        bool expand = rom.Length >= Core.Maps.MapWriter.ExpandedSize;
        if (_title.NeedsMoreSpace(rom) && !expand)
        {
            if (MessageBox.Show("The new title doesn't fit in the original space. Expand the ROM to 2 MB?", "Expand ROM?", MessageBoxButton.YesNo) != MessageBoxResult.Yes)
            { Status = "Not applied."; return; }
            expand = true;
        }
        string result = "";
        Commit("Title screen", () => result = _title.Save(rom, expand));
        if (result.Length > 0) { Dirty = false; Status = $"Title screen written ({result}); unsaved until File > Save ROM."; }
    }
}
