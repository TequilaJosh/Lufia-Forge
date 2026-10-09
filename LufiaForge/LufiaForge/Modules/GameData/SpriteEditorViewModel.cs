using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
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

    protected override void OnRomLoaded() { Fill(); }

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
}
