using System.ComponentModel;
using System.IO;
using System.Windows.Data;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LufiaForge.Core;
using LufiaForge.Core.Lufia2;
using Microsoft.Win32;
using Brush = System.Windows.Media.Brush;
using Brushes = System.Windows.Media.Brushes;
using Color = System.Windows.Media.Color;

namespace LufiaForge.Modules.Lufia2;

/// <summary>A picture in the Graphics list (grouped by kind).</summary>
public sealed class L2GfxItem
{
    public string Group { get; init; } = "";
    public string Label { get; init; } = "";
    public override string ToString() => Label;
}

/// <summary>One colour of the palette shown.</summary>
public sealed class L2Swatch
{
    public Brush Brush { get; init; } = Brushes.Black;
    public string Tip { get; init; } = "";
}

/// <summary>Graphics: battle backgrounds, capsule monsters, hero battle sprites, the title screen, fonts and menu
/// graphics (<see cref="L2Gfx"/>), shown, saved as PNG and brought back from a PNG.</summary>
public partial class L2GraphicsPage : L2Page
{
    private List<L2Gfx.Asset> _assets = new();
    private L2Gfx.Asset? _asset;
    private L2Gfx.Picture? _pic;

    public ICollectionView? ItemsView { get; private set; }

    [ObservableProperty] private BitmapSource? _pictureImage;
    [ObservableProperty] private double _pictureWidth, _pictureHeight;
    [ObservableProperty] private List<L2Swatch> _swatches = new();
    [ObservableProperty] private List<L2Option> _paletteOptions = new();
    [ObservableProperty] private int _paletteChoice;
    [ObservableProperty] private bool _hasPaletteChoice;
    [ObservableProperty] private bool _takeColours = true;
    [ObservableProperty] private string _info = "";
    [ObservableProperty] private string _status = "";

    protected override IEnumerable<string> ReadNames()
    {
        _assets = L2Gfx.Assets(Rom!);
        var items = _assets.Select(a => new L2GfxItem { Group = a.Group, Label = a.Name }).ToList();
        var view = CollectionViewSource.GetDefaultView(items);
        view.GroupDescriptions.Add(new PropertyGroupDescription(nameof(L2GfxItem.Group)));
        ItemsView = view;
        OnPropertyChanged(nameof(ItemsView));
        return items.Select(i => i.Label);
    }

    protected override void Load(int index)
    {
        _asset = _assets[index];
        PaletteOptions = _asset.Palettes.Select((p, i) => new L2Option(i, p.Name)).ToList();
        HasPaletteChoice = PaletteOptions.Count > 1;
        PaletteChoice = _asset.Palettes.Count > 0 ? _asset.DefaultPalette : 0;
        Status = "";
        Draw();
    }

    // nothing here is a record field: the picture is written by Import only
    protected override bool IsField(string property) => false;
    protected override string? Write() => null;

    partial void OnPaletteChoiceChanged(int value) { if (!Loading) Draw(); }

    private void Draw()
    {
        if (Rom == null || _asset == null) return;
        try { _pic = L2Gfx.Decode(Rom, _asset, PaletteChoice); }
        catch (Exception ex) { _pic = null; PictureImage = null; Info = "Can't read this picture: " + ex.Message; return; }
        var p = _pic;
        var bmp = BitmapSource.Create(p.Width, p.Height, 96, 96, PixelFormats.Bgra32, null, p.Argb().Select(c => (int)c).ToArray(), p.Width * 4);
        bmp.Freeze();
        PictureImage = bmp;
        int scale = Math.Clamp(512 / Math.Max(p.Width, p.Height), 1, 8);
        PictureWidth = p.Width * scale; PictureHeight = p.Height * scale;
        Swatches = p.Colours.Select((c, i) => new L2Swatch
        {
            Brush = Frozen(c),
            Tip = $"Colour {i} (row {i / p.RowSize}, {i % p.RowSize}): {L2Gfx.Bgr555(c):X4}" + (i % p.RowSize == 0 && (p.Transparent0 || p.Rows > 1) ? ", see-through" : ""),
        }).ToList();
        string colours = p.Rows > 1 ? $"{p.Colours.Length} colours ({p.Rows} rows of {p.RowSize}; each 8x8 square uses one row)" : $"{p.Colours.Length} colours";
        Info = $"{p.Width} x {p.Height} pixels, {colours}{(p.RomColours ? "" : ", not stored with these graphics")}. {_asset.Note}";
    }

    private static Brush Frozen(uint c)
    {
        var b = new SolidColorBrush(Color.FromRgb((byte)(c >> 16), (byte)(c >> 8), (byte)c));
        b.Freeze();
        return b;
    }

    private string FileName() => string.Concat(("Lufia II " + _asset!.Group + " " + _asset.Name).Select(c => Path.GetInvalidFileNameChars().Contains(c) || c == ':' ? '_' : c)) + ".png";

    [RelayCommand]
    private void Export()
    {
        if (_pic == null || _asset == null) return;
        var dlg = new SaveFileDialog { Title = "Save the picture", Filter = "PNG picture (*.png)|*.png", FileName = FileName() };
        if (dlg.ShowDialog() != true) return;
        ExportTo(dlg.FileName);
    }

    /// <summary>Saves the picture shown as a palette PNG (the dialog's part split off so it can be tested).</summary>
    public void ExportTo(string path)
    {
        if (_pic == null) return;
        try { IndexedPng.Save(path, _pic.Width, _pic.Height, _pic.Pixels, _pic.Colours, _pic.Transparent0); }
        catch (Exception ex) { Status = "Not saved: " + ex.Message; return; }
        Status = $"Saved {Path.GetFileName(path)} ({_pic.Width} x {_pic.Height}, {_pic.Colours.Length} colours, colour numbers as in the game). Draw on it with the same colours and use Import PNG to bring it back.";
    }

    [RelayCommand]
    private void Import()
    {
        if (_pic == null || _asset == null) return;
        var dlg = new OpenFileDialog { Title = "Import a picture", Filter = "Pictures (*.png;*.bmp;*.gif)|*.png;*.bmp;*.gif|All files|*.*" };
        if (dlg.ShowDialog() != true) return;
        ImportFrom(dlg.FileName);
    }

    /// <summary>Writes a picture file into the ROM (the dialog's part split off so it can be tested).</summary>
    public void ImportFrom(string path)
    {
        if (Rom == null || Main == null || _pic == null || _asset == null) return;
        int w = _pic.Width, h = _pic.Height;
        var ip = IndexedPng.TryLoad(path);
        byte[] idx;
        uint[]? colours = null;
        string how;
        if (ip is { } png)
        {
            if (png.Width != w || png.Height != h) { Status = $"The picture must be {w} x {h} pixels (as saved by Export PNG)."; return; }
            idx = png.Indices;
            if (TakeColours && _pic.RomColours) colours = png.Palette;
            how = colours != null ? "colour numbers kept, colours taken from the picture" : "colour numbers kept";
        }
        else
        {
            BitmapSource src;
            try { src = new BitmapImage(new Uri(path)); }
            catch (Exception ex) { Status = "Can't read the picture: " + ex.Message; return; }
            if (src.PixelWidth != w || src.PixelHeight != h) { Status = $"The picture must be {w} x {h} pixels (as saved by Export PNG)."; return; }
            var bgra = new FormatConvertedBitmap(src, PixelFormats.Bgra32, null, 0);
            var px = new uint[w * h];
            bgra.CopyPixels(px, w * 4, 0);
            idx = L2Gfx.MatchColours(px, _pic);
            how = "colours matched to the ones shown" + (_pic.Rows > 1 ? " (each 8x8 square to its best row)" : "");
        }
        var before = Rom.ToArray();
        string what;
        try { what = L2Gfx.Import(Rom, _asset, PaletteChoice, idx, colours); }
        catch (Exception ex)
        {
            if (!Rom.SameAs(before)) Rom.ReplaceAll(before);
            Status = "Not imported: " + ex.Message;
            return;
        }
        if (Rom.SameAs(before)) { Status = $"Nothing changed: the picture is the same as the game's ({how})."; return; }
        bool grew = Rom.Length > before.Length;
        Main.NotifyRomModified($"Lufia II: graphics, {_asset.Name}");
        Draw();
        Status = $"Imported ({how}): {what}." + (grew ? " The ROM grew to 3 MB to make room." : "") +
                 (_asset.Kind == L2Gfx.Kind.BattleBackground && _asset.Note.Contains("Shares") ? " Backgrounds that share its layout or colours changed with it." : "") +
                 " Edit, Undo takes it back.";
    }
}
