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
    }

    private int RowOfQuarter() => BlockSet.RowOf(_set.Word(_block, _quarter));

    private static WriteableBitmap Bitmap(uint[] px, int w, int h)
    {
        var bmp = new WriteableBitmap(w, h, 96, 96, PixelFormats.Bgra32, null);
        bmp.WritePixels(new Int32Rect(0, 0, w, h), px, w * 4, 0);
        return bmp;
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
            if (b == _block)
                for (int i = 0; i < 16 * BlockZoom; i++)
                {
                    px[oy * w + ox + i] = px[(oy + 16 * BlockZoom - 1) * w + ox + i] = 0xFFFFD700;
                    px[(oy + i) * w + ox] = px[(oy + i) * w + ox + 16 * BlockZoom - 1] = 0xFFFFD700;
                }
        }
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
            if (t == cur)
                for (int i = 0; i < 8 * TileZoom; i++)
                {
                    px[oy * w + ox + i] = px[(oy + 8 * TileZoom - 1) * w + ox + i] = 0xFFFFD700;
                    px[(oy + i) * w + ox] = px[(oy + i) * w + ox + 8 * TileZoom - 1] = 0xFFFFD700;
                }
        }
        TilesImage.Source = Bitmap(px, w, h);
    }

    private void ShowBlock()
    {
        _loading = true;
        var bp = _set.BlockPixels(_block);
        var big = new uint[64 * 64];
        for (int y = 0; y < 64; y++) for (int x = 0; x < 64; x++) big[y * 64 + x] = bp[y / 4 * 16 + x / 4];
        var (qx, qy) = BlockSet.Quadrants[_quarter];
        for (int i = 0; i < 32; i++)
        {
            big[qy * 32 * 64 + qx * 32 + i] = big[(qy * 32 + 31) * 64 + qx * 32 + i] = 0xFFFFD700;
            big[(qy * 32 + i) * 64 + qx * 32] = big[(qy * 32 + i) * 64 + qx * 32 + 31] = 0xFFFFD700;
        }
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
