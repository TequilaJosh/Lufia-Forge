using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LufiaForge.Core;
using LufiaForge.Modules.TileViewer;
using Microsoft.Win32;
using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Color = System.Windows.Media.Color;
using Colors = System.Windows.Media.Colors;

namespace LufiaForge.Modules.GameData;

// one colour of a palette
public partial class PaletteSwatch : ObservableObject
{
    public int Index { get; init; }
    [ObservableProperty] private ushort _value;
    [ObservableProperty] private bool _isSelected;
    public SolidColorBrush Brush => new(ToColor(Value));
    partial void OnValueChanged(ushort value) => OnPropertyChanged(nameof(Brush));
    public string Tip => $"Colour {Index % 16} - SNES {Value:X4}";

    public static Color ToColor(ushort snesColor) =>
        Color.FromRgb((byte)((snesColor & 31) * 255 / 31), (byte)((snesColor >> 5 & 31) * 255 / 31), (byte)((snesColor >> 10 & 31) * 255 / 31));
    public static ushort FromRgb(byte r, byte g, byte b) => (ushort)((r * 31 + 127) / 255 | (g * 31 + 127) / 255 << 5 | (b * 31 + 127) / 255 << 10);
}

// one row of 16 colours
public sealed class PaletteRowVm
{
    public string Name { get; init; } = "";
    public ObservableCollection<PaletteSwatch> Swatches { get; } = new();
}

// every palette the game uses: map palettes, the shared map rows, character sprites, battle sprites and
// monster pictures. click a colour, change it, Apply. palettes export to and import from PNG
public partial class PaletteEditorViewModel : GameDataEditorBase
{
    public ObservableCollection<PaletteSource> Sources { get; } = new();
    public ObservableCollection<PaletteRowVm> Rows { get; } = new();

    [ObservableProperty] private PaletteSource? _selectedSource;
    [ObservableProperty] private PaletteSwatch? _selected;
    [ObservableProperty] private int _red, _green, _blue;
    [ObservableProperty] private string _hex = "";
    [ObservableProperty] private string _infoText = "";
    public string OffsetText => "";
    [ObservableProperty] private SolidColorBrush _preview = new(Colors.Black);
    private bool _syncing;

    protected override void OnRomLoaded()
    {
        if (Ctx == null) return;
        Sources.Clear();
        foreach (var source in Palettes.All(Ctx.Rom)) Sources.Add(source);
        SelectedSource = null;
        SelectedSource = Sources.FirstOrDefault(s => s.Name.StartsWith("Map palette")) ?? Sources.FirstOrDefault();
    }

    partial void OnSelectedSourceChanged(PaletteSource? value) => LoadSelected();

    protected override void LoadRecord()
    {
        Rows.Clear();
        Selected = null;
        if (Ctx == null || SelectedSource == null) return;
        ushort[] colors;
        try { colors = SelectedSource.Read(Ctx.Rom); }
        catch (Exception ex) { Status = $"Can't read this palette: {ex.Message}"; return; }
        for (int rowIndex = 0; rowIndex < SelectedSource.RowNames.Length; rowIndex++)
        {
            var row = new PaletteRowVm { Name = SelectedSource.RowNames[rowIndex] };
            for (int c = 0; c < 16; c++) row.Swatches.Add(new PaletteSwatch { Index = rowIndex * 16 + c, Value = colors[rowIndex * 16 + c] });
            Rows.Add(row);
        }
        InfoText = SelectedSource.Info + " Colour 0 of each row is see-through on sprites and background layers.";
        Pick(Rows[0].Swatches[1]);
    }

    private IEnumerable<PaletteSwatch> All => Rows.SelectMany(r => r.Swatches);

    [RelayCommand]
    private void Pick(PaletteSwatch? swatch)
    {
        if (swatch == null) return;
        foreach (var x in All) x.IsSelected = x == swatch;
        Selected = swatch;
        _syncing = true;
        Red = swatch.Value & 31; Green = swatch.Value >> 5 & 31; Blue = swatch.Value >> 10 & 31;
        var color = PaletteSwatch.ToColor(swatch.Value);
        Hex = $"#{color.R:X2}{color.G:X2}{color.B:X2}";
        Preview = new SolidColorBrush(color);
        _syncing = false;
    }

    partial void OnRedChanged(int value) => FromSliders();
    partial void OnGreenChanged(int value) => FromSliders();
    partial void OnBlueChanged(int value) => FromSliders();

    private void FromSliders()
    {
        if (_syncing || Selected == null) return;
        Selected.Value = (ushort)(Math.Clamp(Red, 0, 31) | Math.Clamp(Green, 0, 31) << 5 | Math.Clamp(Blue, 0, 31) << 10);
        var color = PaletteSwatch.ToColor(Selected.Value);
        _syncing = true; Hex = $"#{color.R:X2}{color.G:X2}{color.B:X2}"; _syncing = false;
        Preview = new SolidColorBrush(color);
        Status = "Colour changed (not yet applied).";
    }

    partial void OnHexChanged(string value)
    {
        if (_syncing || Selected == null) return;
        var digits = value.Trim().TrimStart('#');
        if (digits.Length != 6 || !int.TryParse(digits, System.Globalization.NumberStyles.HexNumber, null, out int rgb)) return;
        var snesColor = PaletteSwatch.FromRgb((byte)(rgb >> 16), (byte)(rgb >> 8), (byte)rgb);
        _syncing = true;
        Selected.Value = snesColor; Red = snesColor & 31; Green = snesColor >> 5 & 31; Blue = snesColor >> 10 & 31;
        _syncing = false;
        Preview = new SolidColorBrush(PaletteSwatch.ToColor(snesColor));
        Status = "Colour changed (not yet applied).";
    }

    [RelayCommand]
    private void Apply()
    {
        if (Ctx == null || SelectedSource == null) return;
        var colors = All.Select(s => s.Value).ToArray();
        var source = SelectedSource;
        string? note = null;
        bool allowMove = Ctx.Rom.Length >= Core.Maps.MapWriter.ExpandedSize;
        try { note = source.Write(Ctx.Rom, colors, allowMove); }
        catch (InvalidOperationException) when (!allowMove)
        {
            if (!AutoWriting && MessageBox.Show("This palette is stored compressed and no longer fits where it was. Expand the ROM to 2 MB (the game plays the same) so it can move?",
                    "Expand ROM?", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) { Refuse("Not written: the ROM wasn't expanded."); return; }
            Commit($"{source.Name}", () => note = source.Write(Ctx.Rom, colors, true));
            if (note != null) Status += $" ({note})";
            return;
        }
        Commit($"{source.Name}", () => { });
        if (note != null) Status += $" ({note})";
    }

    [RelayCommand]
    private void Export()
    {
        if (SelectedSource == null) return;
        var dlg = new SaveFileDialog { Filter = "PNG image (*.png)|*.png", FileName = SelectedSource.Name.Replace(' ', '_') + ".png" };
        if (dlg.ShowDialog() != true) return;
        int rows = Rows.Count, cell = 16;
        var indices = new byte[16 * cell * rows * cell];
        for (int y = 0; y < rows * cell; y++)
            for (int x = 0; x < 16 * cell; x++) indices[y * 16 * cell + x] = (byte)(y / cell * 16 + x / cell);
        var palette = All.Select(s => SnesPalette.Bgr555ToArgb32(s.Value)).ToArray();
        IndexedPng.Save(dlg.FileName, 16 * cell, rows * cell, indices, palette, false);
        Status = $"Exported {palette.Length} colours to {System.IO.Path.GetFileName(dlg.FileName)} (an indexed PNG: its palette is these colours, in order).";
    }

    [RelayCommand]
    private void Import()
    {
        if (SelectedSource == null) return;
        var dlg = new OpenFileDialog { Filter = "Images (*.png;*.bmp;*.gif)|*.png;*.bmp;*.gif|All files (*.*)|*.*" };
        if (dlg.ShowDialog() != true) return;
        int needed = Rows.Count * 16;
        uint[]? colors = null;
        var indexed = IndexedPng.TryLoad(dlg.FileName);
        if (indexed is { } png && png.Palette.Length >= needed) colors = png.Palette.Take(needed).ToArray();
        else
        {
            // a picture of colour cells: 16 across, one row of cells per palette row
            var bmp = new FormatConvertedBitmap(new BitmapImage(new Uri(dlg.FileName)), PixelFormats.Bgra32, null, 0);
            int w = bmp.PixelWidth, h = bmp.PixelHeight;
            var pixels = new byte[w * h * 4];
            bmp.CopyPixels(pixels, w * 4, 0);
            colors = new uint[needed];
            for (int i = 0; i < needed; i++)
            {
                int cx = (int)((i % 16 + 0.5) * w / 16), cy = (int)((i / 16 + 0.5) * h / Rows.Count);
                int offset = (cy * w + cx) * 4;
                colors[i] = 0xFF000000u | (uint)pixels[offset + 2] << 16 | (uint)pixels[offset + 1] << 8 | pixels[offset];
            }
        }
        var list = All.ToList();
        for (int i = 0; i < needed; i++)
            list[i].Value = PaletteSwatch.FromRgb((byte)(colors[i] >> 16), (byte)(colors[i] >> 8), (byte)colors[i]);
        if (Selected != null) Pick(Selected);
        Edited = true;   // the colours themselves aren't watched, and picking the same colour again shows no change
        Status = $"{needed} colours read from {System.IO.Path.GetFileName(dlg.FileName)} (not yet applied).";
    }
}
