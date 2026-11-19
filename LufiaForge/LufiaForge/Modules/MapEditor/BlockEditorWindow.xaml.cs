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

// edits the 16x16 blocks of a tileset (or the world map): which 8x8 tile goes in each quarter, its colour row,
// flips, priority and the block's walls. also exports / imports the whole tile sheet as a PNG
public partial class BlockEditorWindow : Window
{
    private const int BlockZoom = 2, BlocksAcross = 16, TileZoom = 3;
    private readonly RomBuffer _rom;
    private readonly BlockSet _set;
    private int _block, _quarter, _sheetRow = 2;
    private bool _loading;
    // true once we wrote something to the ROM (the map editor reloads the map then)
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

    // the four ways a tile can face (the SNES flips tiles, it can't turn them 90 degrees):
    // normal, flipped across, turned 180 (both flips), flipped up/down
    private static readonly (bool X, bool Y, string Name)[] Turns =
        { (false, false, "normal"), (true, false, "flipped across"), (true, true, "turned 180°"), (false, true, "flipped up/down") };

    // alt + mouse wheel anywhere in the window steps the selected quarter through the four ways it can face
    private void TurnQuarter(object sender, MouseWheelEventArgs e)
    {
        if ((Keyboard.Modifiers & ModifierKeys.Alt) == 0) return;
        e.Handled = true;
        ushort word = _set.Word(_block, _quarter);
        int turn = Array.FindIndex(Turns, t => t.X == BlockSet.FlipX(word) && t.Y == BlockSet.FlipY(word));
        turn = ((turn + (e.Delta > 0 ? 1 : -1)) % 4 + 4) % 4;
        _set.SetWord(_block, _quarter, BlockSet.MakeWord(BlockSet.TileOf(word), BlockSet.RowOf(word), BlockSet.Priority(word), Turns[turn].X, Turns[turn].Y));
        string[] names = { "top-left", "bottom-left", "top-right", "bottom-right" };
        StatusText.Text = $"Block {_block:X3}, {names[_quarter]} quarter: {Turns[turn].Name} (not yet applied). Alt + mouse wheel turns it again.";
        DrawBlocks(); ShowBlock(); DrawTiles();
    }

    private int RowOfQuarter() => BlockSet.RowOf(_set.Word(_block, _quarter));

    private static WriteableBitmap Bitmap(uint[] px, int w, int h)
    {
        var bmp = new WriteableBitmap(w, h, 96, 96, PixelFormats.Bgra32, null);
        bmp.WritePixels(new Int32Rect(0, 0, w, h), px, w * 4, 0);
        return bmp;
    }

    // a selection frame you can see on any colours: a gold line `gold` pixels wide with a black line on each side,
    // around the cell at (x, y) of the given size (it spills into the grid gap and the neighbours)
    private static void Outline(uint[] px, int w, int h, int x, int y, int size, int gold)
    {
        void Ring(int distance, uint color)
        {
            int x1 = x - distance, y1 = y - distance, x2 = x + size - 1 + distance, y2 = y + size - 1 + distance;
            void Plot(int plotX, int plotY) { if (plotX >= 0 && plotY >= 0 && plotX < w && plotY < h) px[plotY * w + plotX] = color; }
            for (int i = x1; i <= x2; i++) { Plot(i, y1); Plot(i, y2); }
            for (int i = y1; i <= y2; i++) { Plot(x1, i); Plot(x2, i); }
        }
        int outer = gold / 2 + 1;   // gold rings straddle the cell edge
        Ring(outer, 0xFF000000);
        for (int d = outer - 1; d > outer - 1 - gold; d--) Ring(d, 0xFFFFD700);
        Ring(outer - 1 - gold, 0xFF000000);
    }

    private void DrawBlocks()
    {
        int count = _set.BlockCount, rows = (count + BlocksAcross - 1) / BlocksAcross, cell = 16 * BlockZoom + 1;
        int w = BlocksAcross * cell, h = rows * cell;
        var px = new uint[w * h];
        Array.Fill(px, 0xFF20203Au);
        for (int block = 0; block < count; block++)
        {
            var blockPixels = _set.BlockPixels(block);
            int ox = block % BlocksAcross * cell, oy = block / BlocksAcross * cell;
            for (int y = 0; y < 16 * BlockZoom; y++)
                for (int x = 0; x < 16 * BlockZoom; x++)
                    px[(oy + y) * w + ox + x] = blockPixels[y / BlockZoom * 16 + x / BlockZoom];
        }
        Outline(px, w, h, _block % BlocksAcross * cell, _block / BlocksAcross * cell, 16 * BlockZoom, 3);
        BlocksImage.Source = Bitmap(px, w, h);
        BlocksHeader.Text = $"BLOCKS ({count})";
    }

    private void DrawTiles()
    {
        int count = _set.TileCount, rows = (count + 15) / 16, cell = 8 * TileZoom + 1;
        int w = 16 * cell, h = rows * cell;
        var px = new uint[w * h];
        Array.Fill(px, 0xFF20203Au);
        int currentTile = BlockSet.TileOf(_set.Word(_block, _quarter));
        for (int tile = 0; tile < count; tile++)
        {
            var tileIndices = _set.TileIndices(tile);
            int ox = tile % 16 * cell, oy = tile / 16 * cell;
            for (int y = 0; y < 8 * TileZoom; y++)
                for (int x = 0; x < 8 * TileZoom; x++)
                {
                    int colorIndex = tileIndices[y / TileZoom * 8 + x / TileZoom];
                    px[(oy + y) * w + ox + x] = colorIndex == 0 ? 0xFF000000u : _set.Argb(_sheetRow * 16 + colorIndex);
                }
        }
        if (currentTile < count) Outline(px, w, h, currentTile % 16 * cell, currentTile / 16 * cell, 8 * TileZoom, 3);
        TilesImage.Source = Bitmap(px, w, h);
    }

    private void ShowBlock()
    {
        _loading = true;
        var blockPixels = _set.BlockPixels(_block);
        var big = new uint[64 * 64];
        for (int y = 0; y < 64; y++) for (int x = 0; x < 64; x++) big[y * 64 + x] = blockPixels[y / 4 * 16 + x / 4];
        var (qx, qy) = BlockSet.Quadrants[_quarter];
        Outline(big, 64, 64, qx * 32, qy * 32, 32, 2);
        BlockImage.Source = Bitmap(big, 64, 64);
        ushort word = _set.Word(_block, _quarter);
        string[] names = { "top-left", "bottom-left", "top-right", "bottom-right" };
        BlockHeader.Text = $"BLOCK {_block:X3}";
        QuarterText.Text = $"Quarter: {names[_quarter]}";
        TileBox.Text = BlockSet.TileOf(word).ToString("X3");
        RowBox.SelectedIndex = BlockSet.RowOf(word);
        FlipXBox.IsChecked = BlockSet.FlipX(word);
        FlipYBox.IsChecked = BlockSet.FlipY(word);
        PriorityBox.IsChecked = BlockSet.Priority(word);
        byte attr = _set.Attribute(_block);
        // a wall on a side stops walking INTO the block across that side (checked in game)
        WallUp.IsChecked = (attr & BlockSet.AttrDown) != 0;
        WallDown.IsChecked = (attr & BlockSet.AttrUp) != 0;
        WallLeft.IsChecked = (attr & BlockSet.AttrRight) != 0;
        WallRight.IsChecked = (attr & BlockSet.AttrLeft) != 0;
        Blocked.IsChecked = (attr & BlockSet.AttrBlocked) != 0;
        Flag10.IsChecked = (attr & BlockSet.AttrFlag10) != 0;
        Flag40.IsChecked = (attr & BlockSet.AttrFlag40) != 0;
        Flag80.IsChecked = (attr & BlockSet.AttrFlag80) != 0;
        AttrText.Text = $"Attribute byte {attr:X2}" + (_set.CompositeThreshold >= 0 && _block >= _set.CompositeThreshold ? "  (a composite block: the game draws a ground + overlay pair instead)" : "");
        _loading = false;
    }

    private void Blocks_Click(object sender, MouseButtonEventArgs e)
    {
        var pos = e.GetPosition(BlocksImage);
        int cell = 16 * BlockZoom + 1, block = (int)(pos.Y / cell) * BlocksAcross + (int)(pos.X / cell);
        if (block < 0 || block >= _set.BlockCount) return;
        _block = block; _quarter = 0;
        DrawBlocks(); ShowBlock(); DrawTiles();
    }

    private void Block_Click(object sender, MouseButtonEventArgs e)
    {
        var pos = e.GetPosition(BlockImage);
        int qx = pos.X < BlockImage.ActualWidth / 2 ? 0 : 1, qy = pos.Y < BlockImage.ActualHeight / 2 ? 0 : 1;
        _quarter = Array.IndexOf(BlockSet.Quadrants, (qx, qy));
        int row = RowOfQuarter();
        if (row != _sheetRow) { _sheetRow = row; SheetRowBox.SelectedIndex = row; }
        ShowBlock(); DrawTiles();
    }

    private void Tiles_Click(object sender, MouseButtonEventArgs e)
    {
        var pos = e.GetPosition(TilesImage);
        int cell = 8 * TileZoom + 1, tile = (int)(pos.Y / cell) * 16 + (int)(pos.X / cell);
        if (tile < 0 || tile >= _set.TileCount) return;
        ushort word = _set.Word(_block, _quarter);
        _set.SetWord(_block, _quarter, BlockSet.MakeWord(tile, _sheetRow, BlockSet.Priority(word), BlockSet.FlipX(word), BlockSet.FlipY(word)));
        StatusText.Text = $"Block {_block:X3}: tile {tile:X3} in row {_sheetRow} (not yet applied).";
        DrawBlocks(); ShowBlock(); DrawTiles();
    }

    private void Box_KeyDown(object sender, KeyEventArgs e) { if (e.Key == Key.Enter) Quarter_Changed(sender, e); }

    private void Quarter_Changed(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        if (!int.TryParse(TileBox.Text.Trim(), System.Globalization.NumberStyles.HexNumber, null, out int tile) || tile < 0 || tile >= _set.TileCount)
        { StatusText.Text = $"Tile: a hex number 000-{_set.TileCount - 1:X3}."; return; }
        var newWord = BlockSet.MakeWord(tile, Math.Max(0, RowBox.SelectedIndex), PriorityBox.IsChecked == true, FlipXBox.IsChecked == true, FlipYBox.IsChecked == true);
        if (newWord == _set.Word(_block, _quarter)) return;
        _set.SetWord(_block, _quarter, newWord);
        StatusText.Text = $"Block {_block:X3} changed (not yet applied).";
        DrawBlocks(); ShowBlock(); DrawTiles();
    }

    private void Attr_Changed(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        int attr = (WallLeft.IsChecked == true ? BlockSet.AttrRight : 0) | (WallRight.IsChecked == true ? BlockSet.AttrLeft : 0) |
                (WallUp.IsChecked == true ? BlockSet.AttrDown : 0) | (WallDown.IsChecked == true ? BlockSet.AttrUp : 0) |
                (Blocked.IsChecked == true ? BlockSet.AttrBlocked : 0) | (Flag10.IsChecked == true ? BlockSet.AttrFlag10 : 0) |
                (Flag40.IsChecked == true ? BlockSet.AttrFlag40 : 0) | (Flag80.IsChecked == true ? BlockSet.AttrFlag80 : 0);
        _set.SetAttribute(_block, (byte)attr);
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
        var indices = _set.SheetIndices(out int w, out int h);
        var palette = Enumerable.Range(0, 16).Select(i => _set.Argb(_sheetRow * 16 + i)).ToArray();
        IndexedPng.Save(dlg.FileName, w, h, indices, palette, true);
        StatusText.Text = $"Exported {_set.TileCount} tiles in colour row {_sheetRow} (an indexed PNG: draw with its 16 colours and keep it indexed).";
    }

    private void ImportSheet_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFileDialog { Filter = "Pictures (*.png;*.bmp;*.gif)|*.png;*.bmp;*.gif" };
        if (dlg.ShowDialog(this) != true) return;
        try
        {
            byte[] indices; int w, h;
            var indexedPng = IndexedPng.TryLoad(dlg.FileName);
            if (indexedPng is { } png) { w = png.Width; h = png.Height; indices = png.Indices.Select(i => (byte)(i & 15)).ToArray(); }
            else
            {
                var bmp = new FormatConvertedBitmap(new BitmapImage(new Uri(dlg.FileName)), PixelFormats.Bgra32, null, 0);
                w = bmp.PixelWidth; h = bmp.PixelHeight;
                var px = new uint[w * h];
                bmp.CopyPixels(px, w * 4, 0);
                var palette = Enumerable.Range(0, 16).Select(i => _set.Argb(_sheetRow * 16 + i)).ToArray();
                indices = px.Select(c => (byte)Nearest(c, palette)).ToArray();
            }
            int changed = _set.SetSheet(indices, w, h);
            StatusText.Text = $"{changed} tiles changed from {System.IO.Path.GetFileName(dlg.FileName)} (not yet applied).";
            DrawBlocks(); ShowBlock(); DrawTiles();
        }
        catch (Exception ex) { StatusText.Text = "Couldn't import: " + ex.Message; }
    }

    // nearest of colours 1-15 (see-through pixels become 0)
    private static int Nearest(uint color, uint[] palette)
    {
        if ((color >> 24) < 128) return 0;
        int best = 1, bestDistance = int.MaxValue;
        for (int i = 1; i < 16; i++)
        {
            int dr = (int)(color >> 16 & 255) - (int)(palette[i] >> 16 & 255), dg = (int)(color >> 8 & 255) - (int)(palette[i] >> 8 & 255), db = (int)(color & 255) - (int)(palette[i] & 255);
            int distance = dr * dr * 3 + dg * dg * 4 + db * db * 2;
            if (distance < bestDistance) { bestDistance = distance; best = i; }
        }
        return best;
    }

    private void Apply_Click(object sender, RoutedEventArgs e)
    {
        if (!_set.TilesChanged && !_set.BlocksChanged) { StatusText.Text = "Nothing changed."; return; }
        bool allowExpand = _rom.Length >= MapWriter.ExpandedSize;
        try
        {
            StatusText.Text = "Written: " + _set.Save(_rom, allowExpand);
            Wrote = true;
        }
        // only for "no room", the work RAM check fails the same way expanded or not
        catch (InvalidOperationException ex) when (!allowExpand && !ex.Message.Contains("work RAM", StringComparison.OrdinalIgnoreCase))
        {
            if (MessageBox.Show(this, "The changed graphics or blocks no longer fit in their original space. Expand the ROM to 2 MB (the game plays the same)?",
                    "Expand ROM?", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) { StatusText.Text = "Not written: the ROM wasn't expanded."; return; }
            try { StatusText.Text = "Written: " + _set.Save(_rom, true); Wrote = true; }
            catch (Exception ex2) { StatusText.Text = "Couldn't write: " + ex2.Message; }
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
