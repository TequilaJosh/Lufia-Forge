using LufiaForge.Core;
using LufiaForge.Core.Maps;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace LufiaForge.Modules.MapEditor;

/// <summary>Names a piece of the map (the Area tool's selection) and saves it as a preset (<see cref="CustomPresets"/>).</summary>
public partial class SavePresetWindow : Window
{
    private readonly RomBuffer _rom;
    private readonly LufiaMap _map;
    private readonly int _x, _y, _w, _h;

    public CustomPresets.Preset? Result { get; private set; }

    public SavePresetWindow(RomBuffer rom, LufiaMap map, int x, int y, int w, int h)
    {
        InitializeComponent();
        _rom = rom; _map = map; _x = x; _y = y; _w = w; _h = h;
        SizeText.Text = $"{w} x {h} blocks from map {map.MapId:X2} at ({x}, {y}).";
        NameBox.Text = $"Piece of map {map.MapId:X2}";
        Loaded += (_, _) => { NameBox.Focus(); NameBox.SelectAll(); };
        Preview();
    }

    private CustomPresets.Preset Make() =>
        CustomPresets.FromArea(_rom, _map, _x, _y, _w, _h, NameBox.Text.Trim(), GroundBox.IsChecked == true);

    /// <summary>What will be saved: the left-out cells are see-through.</summary>
    private void Preview()
    {
        if (PreviewImage == null || _rom == null) return;   // (the checkbox fires while the window is still being built)
        var p = Make();
        var ts = MapTileset.ForMap(_rom, _map);
        int pw = _w * 16, ph = _h * 16;
        var px = new uint[pw * ph];
        for (int i = 0; i < p.Cells.Length; i++)
        {
            int bx = i % _w * 16, by = i / _w * 16;
            if (p.Cells[i].Length == 0)
            {
                for (int y = 0; y < 16; y++) for (int x = 0; x < 16; x++) px[(by + y) * pw + bx + x] = ((x / 4 + y / 4) % 2 == 0) ? 0xFF202030u : 0xFF2A2A3Cu;
                continue;
            }
            var block = ts.MetatilePixels(_map.GetTile(_x + i % _w, _y + i / _w) & 0x3FF);
            for (int y = 0; y < 16; y++) for (int x = 0; x < 16; x++) px[(by + y) * pw + bx + x] = block[y * 16 + x] | 0xFF000000;
        }
        var bmp = BitmapSource.Create(pw, ph, 96, 96, PixelFormats.Bgra32, null, px, pw * 4);
        bmp.Freeze();
        PreviewImage.Source = bmp;
        PreviewImage.Width = Math.Min(460, pw * 2); PreviewImage.Height = Math.Min(300, ph * 2);
    }

    private void GroundBox_Changed(object sender, RoutedEventArgs e) => Preview();
    private void NameBox_TextChanged(object sender, System.Windows.Controls.TextChangedEventArgs e) { if (OkButton != null) OkButton.IsEnabled = NameBox.Text.Trim().Length > 0; }

    private void Ok_Click(object sender, RoutedEventArgs e)
    {
        var p = Make();
        if (p.Count == 0) { SizeText.Text = "Nothing would be left: untick \"Leave out the ground\"."; return; }
        Result = p;
        DialogResult = true;
    }
}
