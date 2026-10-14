using LufiaForge.Core;
using LufiaForge.Core.Maps;
using Microsoft.Win32;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using KeyEventArgs = System.Windows.Input.KeyEventArgs;
using MouseButtonEventArgs = System.Windows.Input.MouseButtonEventArgs;

namespace LufiaForge.Modules.MapEditor;

/// <summary>
/// Edits the 16x16 blocks of a tileset (or the world map): which 8x8 tile goes in each quarter, its colour row,
/// flips and priority, and the block's walls; exports / imports the whole tile sheet as a PNG.
/// </summary>
public partial class BlockEditorWindow : Window
{
    private const int BlockZoom = 2, BlocksAcross = 16, TileZoom = 3;
    private readonly RomBuffer _rom;
    private readonly BlockSet _set;
    private int _block, _quarter, _sheetRow = 2;
    private bool _loading;
    /// <summary>True once something was written to the ROM (the map editor reloads the map).</summary>
    public bool Wrote { get; private set; }

    public BlockEditorWindow(RomBuffer rom, BlockSet set, int startBlock)
    {
        InitializeComponent();
        _rom = rom; _set = set;
        TitleText.Text = set.Title;
        for (int r = 0; r < 8; r++) { RowBox.Items.Add(r.ToString()); SheetRowBox.Items.Add(r.ToString()); }
        _block = Math.Clamp(startBlock, 0, set.BlockCount - 1);
        _sheetRow = RowOfQuarter();
        SheetRowBox.SelectedIndex = _sheetRow;
        AddBlockButton.IsEnabled = set.CanAddBlocks;
        AddBlockButton.ToolTip = set.CanAddBlocks ? "Adds a copy of the selected block at the end" :
            set.IsWorld ? "The world map has a fixed number of blocks" : "This tileset has composite blocks at the end, so new ones can't be added";
        DrawBlocks(); DrawTiles(); ShowBlock();
        PreviewMouseWheel += TurnQuarter;
    }

    /// <summary>The four ways a tile can face (the SNES can flip tiles, not turn them 90 degrees): normal, flipped
    /// across, turned 180 degrees (both flips), flipped up/down.</summary>
    private static readonly (bool X, bool Y, string Name)[] Turns =
        { (false, false, "normal"), (true, false, "flipped across"), (true, true, "turned 180°"), (false, true, "flipped up/down") };

    /// <summary>Alt + mouse wheel anywhere in the window: the selected quarter steps through the four ways it can face.</summary>
    private void TurnQuarter(object sender, MouseWheelEventArgs e)
    {
        if ((Keyboard.Modifiers & ModifierKeys.Alt) == 0) return;
        e.Handled = true;
        ushort w = _set.Word(_block, _quarter);
        int i = Array.FindIndex(Turns, t => t.X == BlockSet.FlipX(w) && t.Y == BlockSet.FlipY(w));
        i = ((i + (e.Delta > 0 ? 1 : -1)) % 4 + 4) % 4;
        _set.SetWord(_block, _quarter, BlockSet.MakeWord(BlockSet.TileOf(w), BlockSet.RowOf(w), BlockSet.Priority(w), Turns[i].X, Turns[i].Y));
        string[] names = { "top-left", "bottom-left", "top-right", "bottom-right" };
        StatusText.Text = $"Block {_block:X3}, {names[_quarter]} quarter: {Turns[i].Name} (not yet applied). Alt + mouse wheel turns it again.";
        DrawBlocks(); ShowBlock(); DrawTiles();
    }

    private int RowOfQuarter() => BlockSet.RowOf(_set.Word(_block, _quarter));

    private static WriteableBitmap Bitmap(uint[] px, int w, int h)
    {
        var bmp = new WriteableBitmap(w, h, 96, 96, PixelFormats.Bgra32, null);
        bmp.WritePixels(new Int32Rect(0, 0, w, h), px, w * 4, 0);
        return bmp;
    }

    /// <summary>A selection frame that shows on any colours: <paramref name="gold"/> pixels of gold with a black line
    /// on each side, around the cell at (x, y) of the given size (spilling into the grid gap and its neighbours).</summary>
    private static void Outline(uint[] px, int w, int h, int x, int y, int size, int gold)
    {
        void Ring(int d, uint c)
        {
            int x1 = x - d, y1 = y - d, x2 = x + size - 1 + d, y2 = y + size - 1 + d;
            void P(int px_, int py_) { if (px_ >= 0 && py_ >= 0 && px_ < w && py_ < h) px[py_ * w + px_] = c; }
            for (int i = x1; i <= x2; i++) { P(i, y1); P(i, y2); }
            for (int i = y1; i <= y2; i++) { P(x1, i); P(x2, i); }
        }
        int outer = gold / 2 + 1;   // gold rings straddle the cell edge
        Ring(outer, 0xFF000000);
        for (int d = outer - 1; d > outer - 1 - gold; d--) Ring(d, 0xFFFFD700);
        Ring(outer - 1 - gold, 0xFF000000);
    }

    private void DrawBlocks()
    {
        int n = _set.BlockCount, rows = (n + BlocksAcross - 1) / BlocksAcross, cell = 16 * BlockZoom + 1;
        int w = BlocksAcross * cell, h = rows * cell;
        var px = new uint[w * h];
        Array.Fill(px, 0xFF20203Au);
        for (int b = 0; b < n; b++)
        {
            var bp = _set.BlockPixels(b);
            int ox = b % BlocksAcross * cell, oy = b / BlocksAcross * cell;
            for (int y = 0; y < 16 * BlockZoom; y++)
                for (int x = 0; x < 16 * BlockZoom; x++)
                    px[(oy + y) * w + ox + x] = bp[y / BlockZoom * 16 + x / BlockZoom];
        }
        Outline(px, w, h, _block % BlocksAcross * cell, _block / BlocksAcross * cell, 16 * BlockZoom, 3);
        BlocksImage.Source = Bitmap(px, w, h);
        BlocksHeader.Text = $"BLOCKS ({n})";
    }

    private void DrawTiles()
    {
        int n = _set.TileCount, rows = (n + 15) / 16, cell = 8 * TileZoom + 1;
        int w = 16 * cell, h = rows * cell;
        var px = new uint[w * h];
        Array.Fill(px, 0xFF20203Au);
        int cur = BlockSet.TileOf(_set.Word(_block, _quarter));
        for (int t = 0; t < n; t++)
        {
            var ti = _set.TileIndices(t);
            int ox = t % 16 * cell, oy = t / 16 * cell;
            for (int y = 0; y < 8 * TileZoom; y++)
                for (int x = 0; x < 8 * TileZoom; x++)
                {
                    int ci = ti[y / TileZoom * 8 + x / TileZoom];
                    px[(oy + y) * w + ox + x] = ci == 0 ? 0xFF000000u : _set.Argb(_sheetRow * 16 + ci);
                }
        }
        if (cur < n) Outline(px, w, h, cur % 16 * cell, cur / 16 * cell, 8 * TileZoom, 3);
        TilesImage.Source = Bitmap(px, w, h);
    }

    private void ShowBlock()
    {
        _loading = true;
        var bp = _set.BlockPixels(_block);
        var big = new uint[64 * 64];
        for (int y = 0; y < 64; y++) for (int x = 0; x < 64; x++) big[y * 64 + x] = bp[y / 4 * 16 + x / 4];
        var (qx, qy) = BlockSet.Quadrants[_quarter];
        Outline(big, 64, 64, qx * 32, qy * 32, 32, 2);
        BlockImage.Source = Bitmap(big, 64, 64);
        ushort w = _set.Word(_block, _quarter);
        string[] names = { "top-left", "bottom-left", "top-right", "bottom-right" };
        BlockHeader.Text = $"BLOCK {_block:X3}";
        QuarterText.Text = $"Quarter: {names[_quarter]}";
        TileBox.Text = BlockSet.TileOf(w).ToString("X3");
        RowBox.SelectedIndex = BlockSet.RowOf(w);
        FlipXBox.IsChecked = BlockSet.FlipX(w);
        FlipYBox.IsChecked = BlockSet.FlipY(w);
        PriorityBox.IsChecked = BlockSet.Priority(w);
        byte a = _set.Attribute(_block);
        // a wall on a side stops walking INTO the block across that side (checked in game)
        WallUp.IsChecked = (a & BlockSet.AttrDown) != 0;
        WallDown.IsChecked = (a & BlockSet.AttrUp) != 0;
        WallLeft.IsChecked = (a & BlockSet.AttrRight) != 0;
        WallRight.IsChecked = (a & BlockSet.AttrLeft) != 0;
        Blocked.IsChecked = (a & BlockSet.AttrBlocked) != 0;
        Flag10.IsChecked = (a & BlockSet.AttrFlag10) != 0;
        Flag40.IsChecked = (a & BlockSet.AttrFlag40) != 0;
        Flag80.IsChecked = (a & BlockSet.AttrFlag80) != 0;
        AttrText.Text = $"Attribute byte {a:X2}" + (_set.CompositeThreshold >= 0 && _block >= _set.CompositeThreshold ? "  (a composite block: the game draws a ground + overlay pair instead)" : "");
        _loading = false;
    }

    private void Blocks_Click(object sender, MouseButtonEventArgs e)
    {
        var p = e.GetPosition(BlocksImage);
        int cell = 16 * BlockZoom + 1, b = (int)(p.Y / cell) * BlocksAcross + (int)(p.X / cell);
        if (b < 0 || b >= _set.BlockCount) return;
        _block = b; _quarter = 0;
        DrawBlocks(); ShowBlock(); DrawTiles();
    }

    private void Block_Click(object sender, MouseButtonEventArgs e)
    {
        var p = e.GetPosition(BlockImage);
        int qx = p.X < BlockImage.ActualWidth / 2 ? 0 : 1, qy = p.Y < BlockImage.ActualHeight / 2 ? 0 : 1;
        _quarter = Array.IndexOf(BlockSet.Quadrants, (qx, qy));
        int row = RowOfQuarter();
        if (row != _sheetRow) { _sheetRow = row; SheetRowBox.SelectedIndex = row; }
        ShowBlock(); DrawTiles();
    }

    private void Tiles_Click(object sender, MouseButtonEventArgs e)
    {
        var p = e.GetPosition(TilesImage);
        int cell = 8 * TileZoom + 1, t = (int)(p.Y / cell) * 16 + (int)(p.X / cell);
        if (t < 0 || t >= _set.TileCount) return;
        ushort w = _set.Word(_block, _quarter);
        _set.SetWord(_block, _quarter, BlockSet.MakeWord(t, _sheetRow, BlockSet.Priority(w), BlockSet.FlipX(w), BlockSet.FlipY(w)));
        StatusText.Text = $"Block {_block:X3}: tile {t:X3} in row {_sheetRow} (not yet applied).";
        DrawBlocks(); ShowBlock(); DrawTiles();
    }

    private void Box_KeyDown(object sender, KeyEventArgs e) { if (e.Key == Key.Enter) Quarter_Changed(sender, e); }

    private void Quarter_Changed(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        if (!int.TryParse(TileBox.Text.Trim(), System.Globalization.NumberStyles.HexNumber, null, out int tile) || tile < 0 || tile >= _set.TileCount)
        { StatusText.Text = $"Tile: a hex number 000-{_set.TileCount - 1:X3}."; return; }
        var nw = BlockSet.MakeWord(tile, Math.Max(0, RowBox.SelectedIndex), PriorityBox.IsChecked == true, FlipXBox.IsChecked == true, FlipYBox.IsChecked == true);
        if (nw == _set.Word(_block, _quarter)) return;
        _set.SetWord(_block, _quarter, nw);
        StatusText.Text = $"Block {_block:X3} changed (not yet applied).";
        DrawBlocks(); ShowBlock(); DrawTiles();
    }

    private void Attr_Changed(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        int a = (WallLeft.IsChecked == true ? BlockSet.AttrRight : 0) | (WallRight.IsChecked == true ? BlockSet.AttrLeft : 0) |
                (WallUp.IsChecked == true ? BlockSet.AttrDown : 0) | (WallDown.IsChecked == true ? BlockSet.AttrUp : 0) |
                (Blocked.IsChecked == true ? BlockSet.AttrBlocked : 0) | (Flag10.IsChecked == true ? BlockSet.AttrFlag10 : 0) |
                (Flag40.IsChecked == true ? BlockSet.AttrFlag40 : 0) | (Flag80.IsChecked == true ? BlockSet.AttrFlag80 : 0);
        _set.SetAttribute(_block, (byte)a);
        StatusText.Text = $"Block {_block:X3} walls changed (not yet applied).";
        ShowBlock();
    }

    private void SheetRow_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (SheetRowBox.SelectedIndex < 0) return;
        _sheetRow = SheetRowBox.SelectedIndex;
        DrawTiles();
    }

    private void AddBlock_Click(object sender, RoutedEventArgs e)
    {
        try { _block = _set.AddBlock(_block); _quarter = 0; StatusText.Text = $"Block {_block:X3} added (a copy; not yet applied)."; }
        catch (Exception ex) { StatusText.Text = ex.Message; return; }
        DrawBlocks(); ShowBlock(); DrawTiles();
    }

    private void ExportSheet_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new SaveFileDialog { Filter = "PNG image (*.png)|*.png", FileName = _set.IsWorld ? "world_tiles.png" : $"tileset{_set.Tileset}_tiles.png" };
        if (dlg.ShowDialog(this) != true) return;
        var idx = _set.SheetIndices(out int w, out int h);
        var pal = Enumerable.Range(0, 16).Select(i => _set.Argb(_sheetRow * 16 + i)).ToArray();
        IndexedPng.Save(dlg.FileName, w, h, idx, pal, true);
        StatusText.Text = $"Exported {_set.TileCount} tiles in colour row {_sheetRow} (an indexed PNG: draw with its 16 colours and keep it indexed).";
    }

    private void ImportSheet_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFileDialog { Filter = "Pictures (*.png;*.bmp;*.gif)|*.png;*.bmp;*.gif" };
        if (dlg.ShowDialog(this) != true) return;
        try
        {
            byte[] idx; int w, h;
            var ip = IndexedPng.TryLoad(dlg.FileName);
            if (ip is { } p) { w = p.Width; h = p.Height; idx = p.Indices.Select(i => (byte)(i & 15)).ToArray(); }
            else
            {
                var bmp = new FormatConvertedBitmap(new BitmapImage(new Uri(dlg.FileName)), PixelFormats.Bgra32, null, 0);
                w = bmp.PixelWidth; h = bmp.PixelHeight;
                var px = new uint[w * h];
                bmp.CopyPixels(px, w * 4, 0);
                var pal = Enumerable.Range(0, 16).Select(i => _set.Argb(_sheetRow * 16 + i)).ToArray();
                idx = px.Select(c => (byte)Nearest(c, pal)).ToArray();
            }
            int changed = _set.SetSheet(idx, w, h);
            StatusText.Text = $"{changed} tiles changed from {System.IO.Path.GetFileName(dlg.FileName)} (not yet applied).";
            DrawBlocks(); ShowBlock(); DrawTiles();
        }
        catch (Exception ex) { StatusText.Text = "Couldn't import: " + ex.Message; }
    }

    /// <summary>Nearest of colours 1-15 (transparent pixels become 0).</summary>
    private static int Nearest(uint c, uint[] pal)
    {
        if ((c >> 24) < 128) return 0;
        int best = 1, bd = int.MaxValue;
        for (int i = 1; i < 16; i++)
        {
            int dr = (int)(c >> 16 & 255) - (int)(pal[i] >> 16 & 255), dg = (int)(c >> 8 & 255) - (int)(pal[i] >> 8 & 255), db = (int)(c & 255) - (int)(pal[i] & 255);
            int d = dr * dr * 3 + dg * dg * 4 + db * db * 2;
            if (d < bd) { bd = d; best = i; }
        }
        return best;
    }

    private void Apply_Click(object sender, RoutedEventArgs e)
    {
        if (!_set.TilesChanged && !_set.BlocksChanged) { StatusText.Text = "Nothing changed."; return; }
        bool allow = _rom.Length >= MapWriter.ExpandedSize;
        try
        {
            StatusText.Text = "Written: " + _set.Save(_rom, allow);
            Wrote = true;
        }
        catch (InvalidOperationException) when (!allow)
        {
            if (MessageBox.Show(this, "The changed graphics or blocks no longer fit in their original space. Expand the ROM to 2 MB (the game plays the same)?",
                    "Expand ROM?", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) { StatusText.Text = "Not written: the ROM wasn't expanded."; return; }
            StatusText.Text = "Written: " + _set.Save(_rom, true);
            Wrote = true;
        }
        catch (Exception ex) { StatusText.Text = "Couldn't write: " + ex.Message; }
    }

    private void Close_Click(object sender, RoutedEventArgs e)
    {
        if ((_set.TilesChanged || _set.BlocksChanged) &&
            MessageBox.Show(this, "Close without writing the changes?", "Block editor", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
        Close();
    }
}
