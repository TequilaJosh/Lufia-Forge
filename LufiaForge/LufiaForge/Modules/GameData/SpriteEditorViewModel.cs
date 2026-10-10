using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LufiaForge.Core;
using LufiaForge.Core.Maps;
using Microsoft.Win32;
using System.Collections.ObjectModel;
using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace LufiaForge.Modules.GameData;

public sealed class SpriteEntry
{
    public int Number { get; init; }
    public string Label { get; init; } = "";
    public ImageSource? Picture { get; init; }
}

/// <summary>
/// Map sprites (the party and every character on the maps): their 8 frames as one sheet, exported to and
/// imported from a picture (see <see cref="NpcSprites.ImportSheet"/>).
/// </summary>
public partial class SpriteEditorViewModel : GameDataEditorBase
{
    public ObservableCollection<SpriteEntry> Sprites { get; } = new();
    [ObservableProperty] private SpriteEntry? _selected;
    [ObservableProperty] private ImageSource? _sheet;
    [ObservableProperty] private string _info = "";
    [ObservableProperty] private string _offsetText = "";

    protected override void OnRomLoaded() { Fill(); DrawBattleSheet(); }

    private void Fill()
    {
        if (Ctx == null) return;
        int keep = Selected?.Number ?? 0;
        Sprites.Clear();
        for (int s = 0; s < NpcSprites.SpriteCount; s++)
        {
            if (NpcSprites.Info(Ctx.Rom, s) is not { } info) continue;
            Sprites.Add(new SpriteEntry { Number = s, Label = $"Sprite {s:X2}  ({info.Width * 8}x{info.Height * 8})", Picture = Image(NpcSprites.Render(Ctx.Rom, s)) });
        }
        Selected = Sprites.FirstOrDefault(x => x.Number == keep) ?? Sprites.FirstOrDefault();
    }

    private static ImageSource? Image((uint[] Px, int W, int H)? r)
    {
        if (r is not var (px, w, h)) return null;
        var bmp = BitmapSource.Create(w, h, 96, 96, PixelFormats.Bgra32, null, px, w * 4);
        bmp.Freeze();
        return bmp;
    }

    partial void OnSelectedChanged(SpriteEntry? value) => LoadSelected();

    protected override void LoadSelected()
    {
        if (Ctx == null || Selected == null) { Sheet = null; return; }
        var info = NpcSprites.Info(Ctx.Rom, Selected.Number)!;
        Sheet = Image(NpcSprites.RenderSheet(Ctx.Rom, Selected.Number));
        var frames = NpcSprites.FrameTiles(Ctx.Rom, Selected.Number);
        Info = $"{info.Width * 8} x {info.Height * 8} pixels per frame, 8 frames, sprite palette row {info.Palette}. " +
               $"Frames start at tiles {string.Join(" ", frames.Select(f => f.ToString("X2")))}" +
               (frames.Distinct().Count() < 8 ? " (some frames share their graphics)." : ".");
        OffsetText = $"Graphics at 0x{info.GraphicsOffset:X6} (4bpp, uncompressed)";
    }

    [RelayCommand]
    private void Export()
    {
        if (Ctx == null || Selected == null || NpcSprites.RenderSheet(Ctx.Rom, Selected.Number) is not var (px, w, h)) return;
        var dlg = new SaveFileDialog { Title = "Export sprite sheet", Filter = "PNG picture (*.png)|*.png", FileName = $"sprite {Selected.Number:X2}.png" };
        if (dlg.ShowDialog() != true) return;
        var bmp = BitmapSource.Create(w, h, 96, 96, PixelFormats.Bgra32, null, px, w * 4);
        var enc = new PngBitmapEncoder(); enc.Frames.Add(BitmapFrame.Create(bmp));
        using (var fs = File.Create(dlg.FileName)) enc.Save(fs);
        Status = $"Saved {dlg.FileName}: 8 frames side by side. Edit it and import it back (same size; transparent = see-through).";
    }

    [RelayCommand]
    private void Import()
    {
        if (Ctx == null || Selected == null) return;
        var dlg = new OpenFileDialog { Title = "Import sprite sheet", Filter = "Pictures (*.png;*.bmp;*.gif)|*.png;*.bmp;*.gif" };
        if (dlg.ShowDialog() != true) return;
        int n = Selected.Number;
        Commit($"Sprite {n:X2}", () =>
        {
            var bgra = new FormatConvertedBitmap(new BitmapImage(new Uri(dlg.FileName)), PixelFormats.Bgra32, null, 0);
            var px = new uint[bgra.PixelWidth * bgra.PixelHeight];
            bgra.CopyPixels(px, bgra.PixelWidth * 4, 0);
            NpcSprites.ImportSheet(Ctx.Rom, n, px, bgra.PixelWidth, bgra.PixelHeight);
        });
        Fill();
    }

    [RelayCommand]
    private void Apply() => Status = "Sprites are written when imported.";

    // ── battle sprites: resource 13D (party battle poses, command icons, damage digits, status icons) ──

    public const int BattleSheetResource = 0x13D;
    /// <summary>Battle sprite palettes 0-3 (uncompressed, 4 rows of 16 BGR555 colours).</summary>
    public const int BattlePalettes = 0x11200;
    public ObservableCollection<string> BattleRows { get; } = new() { "Row 0", "Row 1", "Row 2", "Row 3" };
    [ObservableProperty] private int _battleRow = 1;
    [ObservableProperty] private ImageSource? _battleSheet;

    partial void OnBattleRowChanged(int value) => DrawBattleSheet();

    private uint[] BattleColours(int row) =>
        Enumerable.Range(0, 16).Select(c =>
        {
            int v = Ctx!.Rom.ReadUInt16Le(BattlePalettes + row * 32 + c * 2);
            uint r = (uint)(v & 31) << 3, g = (uint)(v >> 5 & 31) << 3, b = (uint)(v >> 10 & 31) << 3;
            return c == 0 ? 0u : 0xFF000000u | r << 16 | g << 8 | b;
        }).ToArray();

    /// <summary>Colour indices of the sheet: 16 tiles across (as in video memory), 8x8 tiles, 4bpp.</summary>
    private byte[] BattleIndices(out int w, out int h)
    {
        var d = LufiaCompression.DecompressResource(Ctx!.Rom, BattleSheetResource, out _);
        int tiles = d.Length / 32; w = 128; h = (tiles + 15) / 16 * 8;
        var idx = new byte[w * h];
        for (int t = 0; t < tiles; t++)
        {
            var px = Modules.TileViewer.SnesTileDecoder.DecodeTile(d, t * 32, Modules.TileViewer.BitDepth.Bpp4);
            for (int y = 0; y < 8; y++) for (int x = 0; x < 8; x++) idx[(t / 16 * 8 + y) * w + t % 16 * 8 + x] = px[y * 8 + x];
        }
        return idx;
    }

    private void DrawBattleSheet()
    {
        if (Ctx == null) return;
        try
        {
            var idx = BattleIndices(out int w, out int h);
            var pal = BattleColours(BattleRow);
            BattleSheet = Image((idx.Select(i => pal[i]).ToArray(), w, h));
        }
        catch { BattleSheet = null; }
    }

    [RelayCommand]
    private void ExportBattle()
    {
        if (Ctx == null) return;
        var idx = BattleIndices(out int w, out int h);
        var pal = BattleColours(BattleRow);
        var dlg = new SaveFileDialog { Title = "Export battle sprites", Filter = "PNG picture (*.png)|*.png", FileName = "battle sprites.png" };
        if (dlg.ShowDialog() != true) return;
        IndexedPng.Save(dlg.FileName, w, h, idx, pal, firstTransparent: true);
        Status = $"Saved {dlg.FileName}: an indexed picture (16 colours). Keep it indexed when editing so every sprite keeps its own colours.";
    }

    [RelayCommand]
    private void ImportBattle()
    {
        if (Ctx == null) return;
        var dlg = new OpenFileDialog { Title = "Import battle sprites", Filter = "Pictures (*.png;*.bmp;*.gif)|*.png;*.bmp;*.gif" };
        if (dlg.ShowDialog() != true) return;
        var ip = IndexedPng.TryLoad(dlg.FileName);
        BitmapSource? src = ip == null ? new BitmapImage(new Uri(dlg.FileName)) : null;
        BattleIndices(out int w, out int h);
        if ((ip?.Width ?? src!.PixelWidth) != w || (ip?.Height ?? src!.PixelHeight) != h) { Status = $"The picture must be {w} x {h} pixels (the exported size)."; return; }
        byte[] idx = new byte[w * h];
        if (ip is { } png)
        {
            // colour numbers as they are (the way it was exported)
            idx = png.Indices;
            if (idx.Any(i => i > 15)) { Status = "The picture uses more than 16 colours."; return; }
        }
        else
        {
            // a full-colour picture: nearest colour of the chosen row
            var bgra = new FormatConvertedBitmap(src!, PixelFormats.Bgra32, null, 0);
            var px = new uint[w * h]; bgra.CopyPixels(px, w * 4, 0);
            var pal = BattleColours(BattleRow);
            for (int i = 0; i < px.Length; i++)
            {
                if (px[i] >> 24 < 128) continue;
                long best = long.MaxValue;
                for (int c = 1; c < 16; c++)
                {
                    long dr = (long)(px[i] >> 16 & 255) - (pal[c] >> 16 & 255), dg = (long)(px[i] >> 8 & 255) - (pal[c] >> 8 & 255), db = (long)(px[i] & 255) - (pal[c] & 255);
                    long e = dr * dr * 3 + dg * dg * 4 + db * db * 2;
                    if (e < best) { best = e; idx[i] = (byte)c; }
                }
            }
        }
        var data = new byte[w * h / 2];
        for (int t = 0; t < data.Length / 32; t++)
            for (int y = 0; y < 8; y++)
                for (int x = 0; x < 8; x++)
                {
                    int c = idx[(t / 16 * 8 + y) * w + t % 16 * 8 + x], bit = 7 - x, a = t * 32;
                    data[a + y * 2] |= (byte)((c & 1) << bit); data[a + y * 2 + 1] |= (byte)((c >> 1 & 1) << bit);
                    data[a + 16 + y * 2] |= (byte)((c >> 2 & 1) << bit); data[a + 16 + y * 2 + 1] |= (byte)((c >> 3 & 1) << bit);
                }
        Commit("Battle sprites", () =>
        {
            bool big = LufiaCompression.Compress(data).Length > SlotSize();
            if (big && Ctx.Rom.Length < Core.Maps.MapWriter.ExpandedSize &&
                System.Windows.MessageBox.Show("The new graphics do not fit in their original space. Expand the ROM to 2 MB?", "Expand ROM?",
                    System.Windows.MessageBoxButton.YesNo) != System.Windows.MessageBoxResult.Yes)
                throw new InvalidOperationException("Not written: the ROM was not expanded.");
            ResourceWriter.Save(Ctx.Rom, BattleSheetResource, data, allowExpand: true);
        });
        DrawBattleSheet();
    }

    private int SlotSize()
    {
        int slot = ResourceWriter.SlotSize(Ctx!.Rom, BattleSheetResource);
        return slot;
    }
}
