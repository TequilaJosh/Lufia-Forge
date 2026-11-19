using LufiaForge.Core;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LufiaForge.Core.Maps;
using Microsoft.Win32;
using System.Collections.ObjectModel;
using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Color = System.Windows.Media.Color;
using Brush = System.Windows.Media.Brush;
using Brushes = System.Windows.Media.Brushes;

namespace LufiaForge.Modules.GameData;

// a colour square in the title editor's palette
public partial class TitleSwatch : ObservableObject
{
    public int Index { get; init; }
    [ObservableProperty] private Brush _fill = Brushes.Black;
    [ObservableProperty] private bool _selected;
    public string Tip => $"Row {Index >> 4}, colour {Index & 15}" + ((Index & 15) == 0 ? " (see-through)" : Index < 32 ? " (shared with the whole game)" : "");
}

// the title screen (see TitleScreen): paint on its two layers, change its colours, animate it
// (colour cycling of palette rows 2 and 3, like the game does for water), export and import layer pictures
public partial class TitleEditorViewModel : GameDataEditorBase
{
    // the editor footer shows a ROM offset, this editor doesn't have one of its own
    public string OffsetText => "";

    public enum Tool { Pencil, Line, Box, Fill, Eraser, Picker }

    private TitleScreen? _title;
    private byte[] _front = Array.Empty<byte>(), _back = Array.Empty<byte>();
    public int LayerW => _title == null ? 288 : _title.Width * 16;
    public int LayerH => _title == null ? 256 : _title.Height * 16;

    [ObservableProperty] private ImageSource? _canvas;
    [ObservableProperty] private string _info = "";
    [ObservableProperty] private bool _dirty;

    public override bool HasPendingEdits => Dirty && Ctx != null;

    // painting
    [ObservableProperty] private bool _editFront = true;
    [ObservableProperty] private bool _showBoth = true;
    [ObservableProperty] private bool _showGrid;
    [ObservableProperty] private int _zoom = 2;
    [ObservableProperty] private Tool _currentTool = Tool.Pencil;
    [ObservableProperty] private int _brushSize = 1;
    [ObservableProperty] private int _colour = 0x22;
    public ObservableCollection<TitleSwatch> Swatches { get; } = new();
    [ObservableProperty] private string _colourInfo = "";
    [ObservableProperty] private int _red, _green, _blue;
    [ObservableProperty] private bool _colourEditable;

    // animation
    [ObservableProperty] private bool _cycle2, _cycle3;
    [ObservableProperty] private int _count2 = 5, _count3 = 14, _delay2 = 8, _delay3 = 4;
    [ObservableProperty] private bool _animating;
    private long _frame;
    private readonly DispatcherTimer _anim;

    public TitleEditorViewModel()
    {
        for (int i = 0; i < 128; i++) Swatches.Add(new TitleSwatch { Index = i });
        _anim = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(1000.0 / 60) };
        _anim.Tick += (_, _) => { _frame++; if (_frame % 2 == 0) Redraw(); };
    }

    protected override void OnRomLoaded() => LoadSelected();

    protected override void LoadRecord()
    {
        if (Ctx == null) return;
        try { _title = TitleScreen.Load(Ctx.Rom); }
        catch (Exception ex) { _title = null; Info = "The title screen can't be read: " + ex.Message; return; }
        _front = _title.LayerIndices(true);
        _back = _title.LayerIndices(false);
        _undo.Clear(); _redo.Clear();
        _loadingCycles = true;
        (Cycle2, Count2, Delay2) = _title.Cycles[0];
        (Cycle3, Count3, Delay3) = _title.Cycles[1];
        _loadingCycles = false;
        Dirty = false;
        RefreshSwatches();
        SelectColour(Colour);
        Redraw();
        Info = $"Layers 288 x 256 (the screen shows x 16-271, y 1-224), {_title.BlockCount} blocks (room for {_title.MaxBlocks}), graphics resource B1, colours: map palette {TitleScreen.PaletteIndex}.";
    }

    // drawing the canvas

    private void Redraw()
    {
        if (_title == null) return;
        int w = LayerW, h = LayerH;
        var palette = Animating ? _title.PaletteAt(_frame) : _title.Palette555;
        var argb = new uint[128];
        for (int i = 0; i < 128; i++) argb[i] = TitleScreen.Argb(palette[i]);
        var pixels = new uint[w * h];
        var top = EditFront ? _front : _back;
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
            {
                int i = y * w + x;
                int color = ShowBoth ? (_front[i] != 0 ? _front[i] : _back[i]) : top[i];
                pixels[i] = color != 0 ? argb[color]
                      : ShowBoth ? 0xFF000000u
                      : ((x >> 2 ^ y >> 2) & 1) != 0 ? 0xFF505050u : 0xFF383838u;
            }
        // the part the screen shows. everything outside it gets a little darker
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
                if (x < TitleScreen.ScreenX || x >= TitleScreen.ScreenX + TitleScreen.ScreenW || y < TitleScreen.ScreenY || y >= TitleScreen.ScreenY + TitleScreen.ScreenH)
                {
                    uint pixel = pixels[y * w + x];
                    pixels[y * w + x] = 0xFF000000u | (pixel >> 1 & 0x7F7F7F);
                }
        if (ShowGrid)
            for (int y = 0; y < h; y++)
                for (int x = 0; x < w; x++)
                    if ((x & 7) == 0 || (y & 7) == 0) pixels[y * w + x] = 0xFF000000u | (pixels[y * w + x] & 0xFFFFFF) ^ 0x303030;
        var bmp = BitmapSource.Create(w, h, 96, 96, PixelFormats.Bgra32, null, pixels, w * 4);
        bmp.Freeze();
        Canvas = bmp;
    }

    partial void OnEditFrontChanged(bool value) => Redraw();
    partial void OnShowBothChanged(bool value) => Redraw();
    partial void OnShowGridChanged(bool value) => Redraw();
    partial void OnAnimatingChanged(bool value) { if (value) { _frame = 0; _anim.Start(); } else { _anim.Stop(); Redraw(); } }

    // colours

    private void RefreshSwatches()
    {
        if (_title == null) return;
        foreach (var swatch in Swatches)
        {
            uint argb = TitleScreen.Argb(_title.Palette555[swatch.Index]);
            var brush = (swatch.Index & 15) == 0
                ? (Brush)new SolidColorBrush(Color.FromRgb(0x40, 0x40, 0x40))
                : new SolidColorBrush(Color.FromRgb((byte)(argb >> 16), (byte)(argb >> 8), (byte)argb));
            brush.Freeze();
            swatch.Fill = brush;
            swatch.Selected = swatch.Index == Colour;
        }
    }

    private bool _fillingColour;

    [RelayCommand]
    private void SelectColour(int index)
    {
        if (_title == null) return;
        Colour = Math.Clamp(index, 0, 127);
        foreach (var swatch in Swatches) swatch.Selected = swatch.Index == Colour;
        ushort snesColor = _title.Palette555[Colour];
        _fillingColour = true;
        Red = snesColor & 31; Green = snesColor >> 5 & 31; Blue = snesColor >> 10 & 31;
        _fillingColour = false;
        ColourEditable = Colour >= 32 && (Colour & 15) != 0;
        ColourInfo = $"Row {Colour >> 4}, colour {Colour & 15}" + ((Colour & 15) == 0 ? " = see-through (paints holes)" : "") +
                     (Colour < 32 ? " - shared with the whole game, can't be changed here" : "") +
                     (Cycle2 && Colour >> 4 == 2 && (Colour & 15) <= Count2 || Cycle3 && Colour >> 4 == 3 && (Colour & 15) <= Count3 ? " - animated" : "");
        if (CurrentTool is Tool.Eraser or Tool.Picker) CurrentTool = Tool.Pencil;
    }

    partial void OnRedChanged(int value) => ColourEdited();
    partial void OnGreenChanged(int value) => ColourEdited();
    partial void OnBlueChanged(int value) => ColourEdited();

    private void ColourEdited()
    {
        if (_fillingColour || _title == null || !ColourEditable) return;
        ushort snesColor = (ushort)(Math.Clamp(Red, 0, 31) | Math.Clamp(Green, 0, 31) << 5 | Math.Clamp(Blue, 0, 31) << 10);
        if (_title.Palette555[Colour] == snesColor) return;
        if (!_colourUndoPending) { Checkpoint(); _colourUndoPending = true; }
        _title.Palette555[Colour] = snesColor;
        Dirty = true;
        RefreshSwatches();
        Redraw();
    }
    private bool _colourUndoPending;

    // painting

    private readonly Stack<(byte[] F, byte[] B, ushort[] P)> _undo = new(), _redo = new();

    private void Checkpoint()
    {
        if (_title == null) return;
        _undo.Push(((byte[])_front.Clone(), (byte[])_back.Clone(), (ushort[])_title.Palette555.Clone()));
        if (_undo.Count > 60) { var kept = _undo.Take(50).Reverse().ToList(); _undo.Clear(); foreach (var step in kept) _undo.Push(step); }
        _redo.Clear();
    }

    private void Restore((byte[] F, byte[] B, ushort[] P) state)
    {
        _front = state.F; _back = state.B; state.P.CopyTo(_title!.Palette555, 0);
        Dirty = true; RefreshSwatches(); SelectColour(Colour); Redraw();
    }

    [RelayCommand]
    private void Undo()
    {
        if (_undo.Count == 0 || _title == null) return;
        _redo.Push(((byte[])_front.Clone(), (byte[])_back.Clone(), (ushort[])_title.Palette555.Clone()));
        Restore(_undo.Pop());
    }

    [RelayCommand]
    private void Redo()
    {
        if (_redo.Count == 0 || _title == null) return;
        _undo.Push(((byte[])_front.Clone(), (byte[])_back.Clone(), (ushort[])_title.Palette555.Clone()));
        Restore(_redo.Pop());
    }

    [RelayCommand] private void SetTool(Tool tool) => CurrentTool = tool;

    private byte[] Layer => EditFront ? _front : _back;
    private byte[]? _strokeBase;
    private int _strokeX, _strokeY;
    private bool _stroking;
    private int _strokeColour;
    private readonly HashSet<int> _tilesTouched = new();

    // mouse down on the canvas at layer pixel (x, y). right = pick the colour there
    public void StrokeStart(int x, int y, bool right)
    {
        if (_title == null || x < 0 || y < 0 || x >= LayerW || y >= LayerH) return;
        _colourUndoPending = false;
        if (right || CurrentTool == Tool.Picker) { Pick(x, y); return; }
        Checkpoint();
        _strokeColour = CurrentTool == Tool.Eraser ? 0 : Colour;
        _tilesTouched.Clear();
        _strokeX = x; _strokeY = y; _stroking = true;
        _strokeBase = CurrentTool is Tool.Line or Tool.Box ? (byte[])Layer.Clone() : null;
        switch (CurrentTool)
        {
            case Tool.Fill: FloodFill(x, y); _stroking = false; break;
            case Tool.Line or Tool.Box: Shape(x, y); break;
            default: Dab(x, y); break;
        }
        Dirty = true;
        Redraw();
    }

    public void StrokeMove(int x, int y)
    {
        if (!_stroking || _title == null) return;
        x = Math.Clamp(x, 0, LayerW - 1); y = Math.Clamp(y, 0, LayerH - 1);
        if (CurrentTool is Tool.Line or Tool.Box) { Array.Copy(_strokeBase!, Layer, Layer.Length); Shape(x, y); }
        else { Line(_strokeX, _strokeY, x, y, Dab); _strokeX = x; _strokeY = y; }
        Redraw();
    }

    public void StrokeEnd()
    {
        if (!_stroking) return;
        _stroking = false;
        _strokeBase = null;
        if (_tilesTouched.Count > 0)
            Status = $"Painted. {_tilesTouched.Count} 8x8 square(s) switched to row {_strokeColour >> 4} (each 8x8 square uses one colour row; their other colours were matched to it).";
        _tilesTouched.Clear();
        Redraw();
    }

    private void Pick(int x, int y)
    {
        int color = Layer[y * LayerW + x];
        if (color == 0 && ShowBoth && EditFront) color = _back[y * LayerW + x];
        SelectColour(color);
        Status = $"Picked row {color >> 4}, colour {color & 15}.";
    }

    private void Shape(int x, int y)
    {
        if (CurrentTool == Tool.Line) Line(_strokeX, _strokeY, x, y, Dab);
        else
        {
            int x0 = Math.Min(_strokeX, x), x1 = Math.Max(_strokeX, x), y0 = Math.Min(_strokeY, y), y1 = Math.Max(_strokeY, y);
            for (int yy = y0; yy <= y1; yy++) for (int xx = x0; xx <= x1; xx++) Put(xx, yy);
        }
    }

    private static void Line(int x0, int y0, int x1, int y1, Action<int, int> plot)
    {
        int dx = Math.Abs(x1 - x0), dy = -Math.Abs(y1 - y0), sx = x0 < x1 ? 1 : -1, sy = y0 < y1 ? 1 : -1, err = dx + dy;
        while (true)
        {
            plot(x0, y0);
            if (x0 == x1 && y0 == y1) break;
            int e2 = 2 * err;
            if (e2 >= dy) { err += dy; x0 += sx; }
            if (e2 <= dx) { err += dx; y0 += sy; }
        }
    }

    private void Dab(int x, int y)
    {
        int size = BrushSize, half = (size - 1) / 2;
        for (int yy = y - half; yy < y - half + size; yy++) for (int xx = x - half; xx < x - half + size; xx++) Put(xx, yy);
    }

    // sets one pixel of the edited layer. a colour from another row first moves its 8x8 square to that row
    private void Put(int x, int y)
    {
        if (x < 0 || y < 0 || x >= LayerW || y >= LayerH) return;
        var layer = Layer;
        int color = _strokeColour;
        if (color != 0) MoveTileToRow(layer, x >> 3, y >> 3, color >> 4);
        layer[y * LayerW + x] = (byte)color;
    }

    private void MoveTileToRow(byte[] layer, int tx, int ty, int row)
    {
        int w = LayerW;
        int current = -1;
        for (int y = 0; y < 8 && current < 0; y++)
            for (int x = 0; x < 8; x++) { int color = layer[(ty * 8 + y) * w + tx * 8 + x]; if (color != 0) { current = color >> 4; break; } }
        if (current < 0 || current == row) return;
        for (int y = 0; y < 8; y++)
            for (int x = 0; x < 8; x++)
            {
                int i = (ty * 8 + y) * w + tx * 8 + x, color = layer[i];
                if (color != 0) layer[i] = (byte)Nearest(_title!.Palette555[color], row);
            }
        _tilesTouched.Add(ty * 64 + tx);
    }

    // the colour of row (1-15) that's closest to a BGR555 colour
    private int Nearest(ushort snesColor, int row)
    {
        int best = row * 16 + 1; long bestDistance = long.MaxValue;
        for (int c = 1; c < 16; c++)
        {
            ushort candidate = _title!.Palette555[row * 16 + c];
            long dr = (snesColor & 31) - (candidate & 31), dg = (snesColor >> 5 & 31) - (candidate >> 5 & 31), db = (snesColor >> 10 & 31) - (candidate >> 10 & 31);
            long distance = dr * dr * 3 + dg * dg * 4 + db * db * 2;
            if (distance < bestDistance) { bestDistance = distance; best = row * 16 + c; }
        }
        return best;
    }

    private void FloodFill(int x, int y)
    {
        var layer = Layer;
        int w = LayerW, h = LayerH, target = layer[y * w + x];
        if (target == _strokeColour) return;
        var stack = new Stack<(int, int)>();
        var seen = new bool[w * h];
        stack.Push((x, y));
        while (stack.Count > 0)
        {
            var (fillX, fillY) = stack.Pop();
            if (fillX < 0 || fillY < 0 || fillX >= w || fillY >= h || seen[fillY * w + fillX] || layer[fillY * w + fillX] != target) continue;
            seen[fillY * w + fillX] = true;
            Put(fillX, fillY);
            stack.Push((fillX + 1, fillY)); stack.Push((fillX - 1, fillY)); stack.Push((fillX, fillY + 1)); stack.Push((fillX, fillY - 1));
        }
    }

    // animation

    private bool _loadingCycles;
    partial void OnCycle2Changed(bool value) => CyclesEdited();
    partial void OnCycle3Changed(bool value) => CyclesEdited();
    partial void OnCount2Changed(int value) => CyclesEdited();
    partial void OnCount3Changed(int value) => CyclesEdited();
    partial void OnDelay2Changed(int value) => CyclesEdited();
    partial void OnDelay3Changed(int value) => CyclesEdited();

    private void CyclesEdited()
    {
        if (_loadingCycles || _title == null) return;
        _title.Cycles[0] = (Cycle2, Math.Clamp(Count2, 2, 15), Math.Clamp(Delay2, 1, 255));
        _title.Cycles[1] = (Cycle3, Math.Clamp(Count3, 2, 15), Math.Clamp(Delay3, 1, 255));
        Dirty = true;
        if ((Cycle2 || Cycle3) && !Animating) Animating = true;
        Redraw();
    }

    // moves the back layer's rainbow into palette row 3 (14 colours by hue, colour 15 = black) and turns on row 3's
    // colour cycling, so the rainbow flows through the letters
    [RelayCommand]
    private void MakeRainbowFlow()
    {
        if (_title == null) return;
        Checkpoint();
        const int Bands = 14;
        int w = LayerW;
        var palette = _title.Palette555;
        static (double H, double S, double V) Hsv(ushort c)
        {
            double r = (c & 31) / 31.0, g = (c >> 5 & 31) / 31.0, b = (c >> 10 & 31) / 31.0;
            double max = Math.Max(r, Math.Max(g, b)), min = Math.Min(r, Math.Min(g, b)), d = max - min, h = 0;
            if (d > 0) h = max == r ? (g - b) / d % 6 : max == g ? (b - r) / d + 2 : (r - g) / d + 4;
            return ((h * 60 + 360) % 360, max == 0 ? 0 : d / max, max);
        }
        // which back layer colours are the rainbow: anything not in row 2 (row 2 = the black filler)
        bool IsRainbow(int color) => color != 0 && color >> 4 != 2;
        var sum = new double[Bands, 3]; var counts = new int[Bands];
        int Band(ushort c) => (int)(Hsv(c).H / 360 * Bands) % Bands;
        for (int i = 0; i < _back.Length; i++)
            if (IsRainbow(_back[i])) { var color = palette[_back[i]]; int band = Band(color); sum[band, 0] += color & 31; sum[band, 1] += color >> 5 & 31; sum[band, 2] += color >> 10 & 31; counts[band]++; }
        if (counts.Sum() == 0) { Status = "The back layer has no rainbow colours (rows 3-7) to animate."; return; }
        for (int band = 0; band < Bands; band++)
        {
            ushort colour;
            if (counts[band] > 0) colour = (ushort)((int)Math.Round(sum[band, 0] / counts[band]) | (int)Math.Round(sum[band, 1] / counts[band]) << 5 | (int)Math.Round(sum[band, 2] / counts[band]) << 10);
            else
            {
                // a hue the picture doesn't have, so make one up at full colour
                double h = (band + 0.5) / Bands * 6, x = 1 - Math.Abs(h % 2 - 1);
                var (r, g, bl) = (int)h switch { 0 => (1.0, x, 0.0), 1 => (x, 1.0, 0.0), 2 => (0.0, 1.0, x), 3 => (0.0, x, 1.0), 4 => (x, 0.0, 1.0), _ => (1.0, 0.0, x) };
                colour = (ushort)((int)(r * 31) | (int)(g * 31) << 5 | (int)(bl * 31) << 10);
            }
            palette[0x31 + band] = colour;
        }
        palette[0x3F] = 0;   // colour 15 = black, not cycled
        for (int ty = 0; ty < LayerH / 8; ty++)
            for (int tx = 0; tx < w / 8; tx++)
            {
                bool any = false;
                for (int y = 0; y < 8; y++) for (int x = 0; x < 8; x++) any |= IsRainbow(_back[(ty * 8 + y) * w + tx * 8 + x]);
                if (!any) continue;
                for (int y = 0; y < 8; y++)
                    for (int x = 0; x < 8; x++)
                    {
                        int i = (ty * 8 + y) * w + tx * 8 + x, color = _back[i];
                        if (color == 0) continue;
                        _back[i] = (byte)(IsRainbow(color) ? 0x31 + Band(palette[color]) : 0x3F);
                    }
            }
        _loadingCycles = true;
        Cycle3 = true; Count3 = Bands; Delay3 = Math.Clamp(Delay3, 1, 255);
        _loadingCycles = false;
        CyclesEdited();
        RefreshSwatches(); SelectColour(Colour);
        Status = "The rainbow now uses row 3 (14 colours by hue) and row 3 cycles: it flows through the letters. Change the speed below; it's written by itself.";
    }

    // files

    private List<Color> PngPalette()
    {
        var list = new List<Color>();
        for (int i = 0; i < 128; i++)
        {
            uint argb = TitleScreen.Argb(_title!.Palette555[i]);
            list.Add(i % 16 == 0 ? Color.FromArgb(0, 0, 0, 0) : Color.FromRgb((byte)(argb >> 16), (byte)(argb >> 8), (byte)argb));
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
        int w = LayerW, h = LayerH;
        IndexedPng.Save(path, w, h, front ? _front : _back, Enumerable.Range(0, 128).Select(i => TitleScreen.Argb(_title.Palette555[i])).ToArray(), firstTransparent: true);
        Status = $"Saved {path} ({w} x {h}; the screen shows x {TitleScreen.ScreenX}-{TitleScreen.ScreenX + 255}, y {TitleScreen.ScreenY}-{TitleScreen.ScreenY + 223}). " +
                 "Keep it indexed: colour numbers 16 x row + colour, transparent = 0 of each row.";
    }

    [RelayCommand]
    private void ExportScreen()
    {
        if (_title == null) return;
        var dlg = new SaveFileDialog { Title = "Export title screen", Filter = "PNG picture (*.png)|*.png", FileName = "title screen.png" };
        if (dlg.ShowDialog() != true) return;
        var bmp = BitmapSource.Create(256, 224, 96, 96, PixelFormats.Bgra32, null, _title.RenderScreen(_front, _back, _title.Palette555), 256 * 4);
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bmp));
        using (var file = File.Create(dlg.FileName)) encoder.Save(file);
        Status = $"Saved {dlg.FileName}.";
    }

    private void ImportLayer(bool front)
    {
        if (_title == null) return;
        var dlg = new OpenFileDialog { Title = $"Import title {(front ? "front" : "back")} layer", Filter = "Pictures (*.png;*.bmp;*.gif)|*.png;*.bmp;*.gif" };
        if (dlg.ShowDialog() != true) return;
        ImportLayerFrom(dlg.FileName, front);
    }

    // imports a layer picture from a file (the dialog part is split off so this can be tested)
    public void ImportLayerFrom(string path, bool front)
    {
        if (_title == null) return;
        int w = LayerW, h = LayerH;
        var indexedPng = IndexedPng.TryLoad(path);
        BitmapSource? source = null;
        if (indexedPng == null) try { source = new BitmapImage(new Uri(path)); } catch (Exception ex) { Status = "Can't read the picture: " + ex.Message; return; }
        int pictureWidth = indexedPng?.Width ?? source!.PixelWidth, pictureHeight = indexedPng?.Height ?? source!.PixelHeight;
        if (pictureWidth != w || pictureHeight != h) { Status = $"The layer picture must be {w} x {h} pixels (as exported)."; return; }
        byte[] indices;
        string how;
        var paletteBefore = (ushort[])_title.Palette555.Clone();
        if (indexedPng is { } png)
        {
            indices = png.Indices;
            if (indices.Any(i => i >= 128)) { Status = "The picture uses colour numbers above 127; only rows 0-7 (128 colours) exist."; return; }
            if (RowConflict(indices) is string bad) { Status = "Not imported: " + bad; return; }
            Checkpoint();
            // the picture's colours of rows 2-7 become the title's colours
            for (int i = 32; i < Math.Min(128, png.Palette.Length); i++)
                if (i % 16 != 0) _title.Palette555[i] = Core.Battle.MonsterGraphics.ToBgr555(png.Palette[i]);
            how = "colour numbers kept, colours of rows 2-7 taken from the picture";
        }
        else
        {
            var bgra = new FormatConvertedBitmap(source!, PixelFormats.Bgra32, null, 0);
            var pixels = new uint[w * h]; bgra.CopyPixels(pixels, w * 4, 0);
            Checkpoint();
            indices = MatchColours(pixels, w, h);
            how = "colours matched to the title's palette (each 8x8 square to its best row)";
        }
        if (front) _front = indices; else _back = indices;
        Dirty = true;
        RefreshSwatches(); SelectColour(Colour);
        Redraw();
        Status = $"{(front ? "Front" : "Back")} layer imported ({how}). Paint on it here; it's written by itself.";
    }

    // a message about the first 8x8 square that uses two colour rows, or null
    private string? RowConflict(byte[] indices)
    {
        int w = LayerW;
        for (int ty = 0; ty < LayerH / 8; ty++)
            for (int tx = 0; tx < w / 8; tx++)
            {
                int row = -1;
                for (int y = 0; y < 8; y++)
                    for (int x = 0; x < 8; x++)
                    {
                        int color = indices[(ty * 8 + y) * w + tx * 8 + x];
                        if (color == 0) continue;
                        if (row < 0) row = color >> 4;
                        else if (color >> 4 != row) return $"the 8x8 square at pixel ({tx * 8}, {ty * 8}) uses two colour rows ({row} and {color >> 4}); every 8x8 square can use only one row of 16 colours.";
                    }
            }
        return null;
    }

    // full colour picture -> colour numbers. each 8x8 tile uses the row (2-7) that fits it best
    private byte[] MatchColours(uint[] pixels, int w, int h)
    {
        var indices = new byte[w * h];
        var palette = Enumerable.Range(0, 128).Select(i => TitleScreen.Argb(_title!.Palette555[i])).ToArray();
        long Distance(uint a, uint b)
        {
            long dr = (long)(a >> 16 & 255) - (b >> 16 & 255), dg = (long)(a >> 8 & 255) - (b >> 8 & 255), db = (long)(a & 255) - (b & 255);
            return dr * dr * 3 + dg * dg * 4 + db * db * 2;
        }
        for (int ty = 0; ty < h; ty += 8)
            for (int tx = 0; tx < w; tx += 8)
            {
                int bestRow = 2; long bestRowError = long.MaxValue;
                for (int row = 2; row < 8; row++)
                {
                    long rowError = 0;
                    for (int y = 0; y < 8; y++)
                        for (int x = 0; x < 8; x++)
                        {
                            uint pixel = pixels[(ty + y) * w + tx + x];
                            if (pixel >> 24 < 128) continue;
                            long nearest = long.MaxValue;
                            for (int c = 1; c < 16; c++) nearest = Math.Min(nearest, Distance(pixel, palette[row * 16 + c]));
                            rowError += nearest;
                        }
                    if (rowError < bestRowError) { bestRowError = rowError; bestRow = row; }
                }
                for (int y = 0; y < 8; y++)
                    for (int x = 0; x < 8; x++)
                    {
                        uint pixel = pixels[(ty + y) * w + tx + x];
                        if (pixel >> 24 < 128) continue;
                        int best = 1; long nearest = long.MaxValue;
                        for (int c = 1; c < 16; c++) { long distance = Distance(pixel, palette[bestRow * 16 + c]); if (distance < nearest) { nearest = distance; best = c; } }
                        indices[(ty + y) * w + tx + x] = (byte)(bestRow * 16 + best);
                    }
            }
        return indices;
    }

    // writing

    [RelayCommand]
    private void Apply()
    {
        if (Ctx == null || _title == null) return;
        var rom = Ctx.Rom;
        string built;
        try { built = _title.Rebuild(_front, _back); }
        catch (InvalidOperationException ex) { Refuse("Not applied: " + ex.Message); return; }
        bool expand = rom.Length >= Core.Maps.MapWriter.ExpandedSize;
        if (_title.NeedsMoreSpace(rom) && !expand)
        {
            if (!AutoWriting && MessageBox.Show("The new title doesn't fit in the original space. Expand the ROM to 2 MB?", "Expand ROM?", MessageBoxButton.YesNo) != MessageBoxResult.Yes)
            { Refuse("Not applied: the ROM wasn't expanded."); return; }
            expand = true;
        }
        string result = "";
        Commit("Title screen", () => result = _title.Save(rom, expand));
        if (result.Length > 0)
        {
            bool wasAnimating = Animating;
            var undo = _undo.Reverse().ToList(); var redo = _redo.Reverse().ToList();
            LoadSelected();
            // writing isn't a step, the paint history carries on
            foreach (var step in undo) _undo.Push(step);
            foreach (var step in redo) _redo.Push(step);
            UndoCommand.NotifyCanExecuteChanged(); RedoCommand.NotifyCanExecuteChanged();
            Animating = wasAnimating;
            Status = $"Title screen written ({built}; {result}); Save Project keeps it.";
        }
    }
}
