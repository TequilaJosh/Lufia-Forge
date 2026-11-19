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

// map sprites (the party and every character on the maps): their 8 frames as one sheet, exported to and
// imported from a picture (see NpcSprites.ImportSheet)
public partial class SpriteEditorViewModel : GameDataEditorBase
{
    public ObservableCollection<SpriteEntry> Sprites { get; } = new();
    [ObservableProperty] private SpriteEntry? _selected;
    [ObservableProperty] private ImageSource? _sheet;
    [ObservableProperty] private string _info = "";
    [ObservableProperty] private string _offsetText = "";
    [ObservableProperty] private int _paletteRow = -1;
    [ObservableProperty] private string _newNote = "";
    public ObservableCollection<string> PaletteRows { get; } = new(Enumerable.Range(0, 8).Select(r => $"Row {r}"));
    private bool _loading;

    protected override void OnRomLoaded() { Fill(); DrawBattleSheet(); }

    private void Fill()
    {
        if (Ctx == null) return;
        int keepNumber = Selected?.Number ?? 0;
        Sprites.Clear();
        for (int sprite = 0, count = NpcSprites.Count(Ctx.Rom); sprite < count; sprite++)
        {
            if (NpcSprites.Info(Ctx.Rom, sprite) is not { } info) continue;
            Sprites.Add(new SpriteEntry { Number = sprite, Label = $"Sprite {sprite:X2}  ({info.Width * 8}x{info.Height * 8}){(NpcSprites.IsNew(Ctx.Rom, sprite) ? "  new" : "")}", Picture = Image(NpcSprites.Render(Ctx.Rom, sprite)) });
        }
        Selected = Sprites.FirstOrDefault(x => x.Number == keepNumber) ?? Sprites.FirstOrDefault();
        NewNote = $"Room for {NpcSprites.Room(Ctx.Rom)} more sprites.";
    }

    private static ImageSource? Image((uint[] Px, int W, int H)? rendered)
    {
        if (rendered is not var (pixels, w, h)) return null;
        var bmp = BitmapSource.Create(w, h, 96, 96, PixelFormats.Bgra32, null, pixels, w * 4);
        bmp.Freeze();
        return bmp;
    }

    partial void OnSelectedChanged(SpriteEntry? value) => LoadSelected();

    protected override void LoadRecord()
    {
        if (Ctx == null || Selected == null) { Sheet = null; return; }
        var info = NpcSprites.Info(Ctx.Rom, Selected.Number)!;
        _loading = true; PaletteRow = info.Palette; _loading = false;
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
        if (Ctx == null || Selected == null) return;
        var dlg = new SaveFileDialog { Title = "Export sprite sheet", Filter = "PNG picture (*.png)|*.png", FileName = $"sprite {Selected.Number:X2}.png" };
        if (dlg.ShowDialog() == true) ExportTo(dlg.FileName);
    }

    // saves the selected sprite's 8 frames as an indexed picture (the 16 colours of its palette row)
    public void ExportTo(string path)
    {
        if (Ctx == null || Selected == null || NpcSprites.Info(Ctx.Rom, Selected.Number) is not { } info) return;
        var indices = NpcSprites.SheetIndices(Ctx.Rom, Selected.Number, out int w, out int h);
        IndexedPng.Save(path, w, h, indices, NpcSprites.Colours(Ctx.Rom, info.Palette), firstTransparent: true);
        Status = $"Saved {path}: 8 frames side by side, an indexed picture (16 colours, colour 0 = see-through). " +
                 "Keep it indexed when editing so the colour numbers stay; import it back at the same size.";
    }

    [RelayCommand]
    private void Import()
    {
        if (Ctx == null || Selected == null) return;
        var dlg = new OpenFileDialog { Title = "Import sprite sheet", Filter = "Pictures (*.png;*.bmp;*.gif)|*.png;*.bmp;*.gif" };
        if (dlg.ShowDialog() == true) ImportFrom(dlg.FileName);
    }

    // draws a picture into the selected sprite. an indexed picture keeps its colour numbers, others get
    // matched to the sprite's palette row
    public void ImportFrom(string path)
    {
        if (Ctx == null || Selected == null) return;
        int sprite = Selected.Number;
        Commit($"Sprite {sprite:X2}", () =>
        {
            if (IndexedPng.TryLoad(path) is { } png && png.Indices.All(i => i < 16))
            {
                NpcSprites.ImportIndices(Ctx.Rom, sprite, png.Indices, png.Width, png.Height);
                return;
            }
            var bgra = new FormatConvertedBitmap(new BitmapImage(new Uri(path)), PixelFormats.Bgra32, null, 0);
            var pixels = new uint[bgra.PixelWidth * bgra.PixelHeight];
            bgra.CopyPixels(pixels, bgra.PixelWidth * 4, 0);
            NpcSprites.ImportSheet(Ctx.Rom, sprite, pixels, bgra.PixelWidth, bgra.PixelHeight);
        });
        Fill();
    }

    partial void OnPaletteRowChanged(int value)
    {
        if (_loading || Ctx == null || Selected == null || value < 0) return;
        if (NpcSprites.Info(Ctx.Rom, Selected.Number) is not { } info || info.Palette == value) return;
        int sprite = Selected.Number;
        Commit($"Sprite {sprite:X2} palette row", () => NpcSprites.SetPaletteRow(Ctx.Rom, sprite, value));
        Fill();
    }

    // a new sprite: a copy of the selected one (same size), with all 8 frames its own
    [RelayCommand]
    private void NewSprite()
    {
        if (Ctx == null || Selected == null) return;
        if (System.Windows.MessageBox.Show($"Make a new sprite as a copy of sprite {Selected.Number:X2} (same size, its own graphics for all 8 frames)? " +
                "Then export it, draw it and import it back, and give it to characters in the Map Editor.", "New sprite",
                System.Windows.MessageBoxButton.OKCancel, System.Windows.MessageBoxImage.Question) != System.Windows.MessageBoxResult.OK) return;
        MakeSprite();
    }

    public void MakeSprite()
    {
        if (Ctx == null || Selected == null) return;
        int template = Selected.Number, made = -1;
        Commit("New sprite", () => made = NpcSprites.Add(Ctx.Rom, template));
        if (made < 0) return;
        Fill();
        Selected = Sprites.FirstOrDefault(x => x.Number == made);
        Status = $"Sprite {made:X2} made (a copy of {template:X2}). Export it, draw it, import it back; give it to characters in the Map Editor.";
    }

    [RelayCommand]
    private void Apply() => Status = "Sprites are written when imported.";

    // battle sprites: resource 13D (party battle poses, command icons, damage digits, status icons)

    public const int BattleSheetResource = 0x13D;
    // battle sprite palettes 0-3 (uncompressed, 4 rows of 16 BGR555 colours)
    public const int BattlePalettes = 0x11200;
    public ObservableCollection<string> BattleRows { get; } = new() { "Row 0", "Row 1", "Row 2", "Row 3" };
    [ObservableProperty] private int _battleRow = 1;
    [ObservableProperty] private ImageSource? _battleSheet;

    partial void OnBattleRowChanged(int value) => DrawBattleSheet();

    private uint[] BattleColours(int row) =>
        Enumerable.Range(0, 16).Select(c =>
        {
            int snesColor = Ctx!.Rom.ReadUInt16Le(BattlePalettes + row * 32 + c * 2);
            uint r = (uint)(snesColor & 31) << 3, g = (uint)(snesColor >> 5 & 31) << 3, b = (uint)(snesColor >> 10 & 31) << 3;
            return c == 0 ? 0u : 0xFF000000u | r << 16 | g << 8 | b;
        }).ToArray();

    // colour numbers of the sheet: 16 tiles across (like in video memory), 8x8 tiles, 4bpp
    private byte[] BattleIndices(out int w, out int h)
    {
        var data = LufiaCompression.DecompressResource(Ctx!.Rom, BattleSheetResource, out _);
        int tiles = data.Length / 32; w = 128; h = (tiles + 15) / 16 * 8;
        var indices = new byte[w * h];
        for (int tile = 0; tile < tiles; tile++)
        {
            var tilePixels = Modules.TileViewer.SnesTileDecoder.DecodeTile(data, tile * 32, Modules.TileViewer.BitDepth.Bpp4);
            for (int y = 0; y < 8; y++) for (int x = 0; x < 8; x++) indices[(tile / 16 * 8 + y) * w + tile % 16 * 8 + x] = tilePixels[y * 8 + x];
        }
        return indices;
    }

    private void DrawBattleSheet()
    {
        if (Ctx == null) return;
        try
        {
            var indices = BattleIndices(out int w, out int h);
            var palette = BattleColours(BattleRow);
            BattleSheet = Image((indices.Select(i => palette[i]).ToArray(), w, h));
        }
        catch { BattleSheet = null; }
    }

    [RelayCommand]
    private void ExportBattle()
    {
        if (Ctx == null) return;
        var indices = BattleIndices(out int w, out int h);
        var palette = BattleColours(BattleRow);
        var dlg = new SaveFileDialog { Title = "Export battle sprites", Filter = "PNG picture (*.png)|*.png", FileName = "battle sprites.png" };
        if (dlg.ShowDialog() != true) return;
        IndexedPng.Save(dlg.FileName, w, h, indices, palette, firstTransparent: true);
        Status = $"Saved {dlg.FileName}: an indexed picture (16 colours). Keep it indexed when editing so every sprite keeps its own colours.";
    }

    [RelayCommand]
    private void ImportBattle()
    {
        if (Ctx == null) return;
        var dlg = new OpenFileDialog { Title = "Import battle sprites", Filter = "Pictures (*.png;*.bmp;*.gif)|*.png;*.bmp;*.gif" };
        if (dlg.ShowDialog() != true) return;
        var indexedPng = IndexedPng.TryLoad(dlg.FileName);
        BitmapSource? source = indexedPng == null ? new BitmapImage(new Uri(dlg.FileName)) : null;
        BattleIndices(out int w, out int h);
        if ((indexedPng?.Width ?? source!.PixelWidth) != w || (indexedPng?.Height ?? source!.PixelHeight) != h) { Status = $"The picture must be {w} x {h} pixels (the exported size)."; return; }
        byte[] indices = new byte[w * h];
        if (indexedPng is { } png)
        {
            // colour numbers as they are (the way it was exported)
            indices = png.Indices;
            if (indices.Any(i => i > 15)) { Status = "The picture uses more than 16 colours."; return; }
        }
        else
        {
            // a full colour picture: nearest colour of the picked row
            var bgra = new FormatConvertedBitmap(source!, PixelFormats.Bgra32, null, 0);
            var pixels = new uint[w * h]; bgra.CopyPixels(pixels, w * 4, 0);
            var palette = BattleColours(BattleRow);
            for (int i = 0; i < pixels.Length; i++)
            {
                if (pixels[i] >> 24 < 128) continue;
                long best = long.MaxValue;
                for (int color = 1; color < 16; color++)
                {
                    long dr = (long)(pixels[i] >> 16 & 255) - (palette[color] >> 16 & 255), dg = (long)(pixels[i] >> 8 & 255) - (palette[color] >> 8 & 255), db = (long)(pixels[i] & 255) - (palette[color] & 255);
                    long distance = dr * dr * 3 + dg * dg * 4 + db * db * 2;
                    if (distance < best) { best = distance; indices[i] = (byte)color; }
                }
            }
        }
        var data = new byte[w * h / 2];
        for (int tile = 0; tile < data.Length / 32; tile++)
            for (int y = 0; y < 8; y++)
                for (int x = 0; x < 8; x++)
                {
                    int color = indices[(tile / 16 * 8 + y) * w + tile % 16 * 8 + x], bit = 7 - x, tileOffset = tile * 32;
                    data[tileOffset + y * 2] |= (byte)((color & 1) << bit); data[tileOffset + y * 2 + 1] |= (byte)((color >> 1 & 1) << bit);
                    data[tileOffset + 16 + y * 2] |= (byte)((color >> 2 & 1) << bit); data[tileOffset + 16 + y * 2 + 1] |= (byte)((color >> 3 & 1) << bit);
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
