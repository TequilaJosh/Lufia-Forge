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

/// <summary>One colour of the palette shown (click it to paint with it and change it).</summary>
public sealed partial class L2Swatch : ObservableObject
{
    public int Index { get; init; }
    public Brush Brush { get; init; } = Brushes.Black;
    public string Tip { get; init; } = "";
    [ObservableProperty] private bool _isSelected;
}

/// <summary>Graphics: battle backgrounds, capsule monsters, hero battle sprites, the title screen, fonts, menu graphics
/// and map sprites (<see cref="L2Gfx"/>), shown, painted pixel by pixel, saved as PNG and brought back from a PNG; new
/// map sprites and new map sprite palettes (<see cref="L2Sprites"/>).</summary>
public partial class L2GraphicsPage : L2Page
{
    private List<L2Gfx.Asset> _assets = new();
    private L2Gfx.Asset? _asset;
    private L2Gfx.Picture? _pic;

    public ICollectionView? ItemsView { get; private set; }

    [ObservableProperty] private BitmapSource? _pictureImage;
    [ObservableProperty] private List<L2Swatch> _swatches = new();
    [ObservableProperty] private List<L2Option> _paletteOptions = new();
    [ObservableProperty] private int _paletteChoice;
    [ObservableProperty] private bool _hasPaletteChoice;
    [ObservableProperty] private string _paletteTip = "";
    [ObservableProperty] private bool _takeColours = true;
    [ObservableProperty] private string _info = "";
    [ObservableProperty] private string _status = "";

    // the colour editor
    [ObservableProperty] private bool _hasColour;
    [ObservableProperty] private int _red, _green, _blue;
    [ObservableProperty] private string _hex = "";
    [ObservableProperty] private Brush _colourPreview = Brushes.Black;
    [ObservableProperty] private string _colourInfo = "Click a colour above to paint with it or change it.";
    private int _colour = -1;
    private bool _syncing;

    // the painter: the picture's colour numbers as painted, and the strokes to take back (until written)
    private byte[]? _work;
    private byte[]? _strokeStart;
    private string? _refused;   // why a pixel of this stroke wasn't painted
    private readonly List<byte[]> _undo = new(), _redo = new();
    private bool _refreshing;
    [ObservableProperty] private bool _canPaint;
    [ObservableProperty] [NotifyCanExecuteChangedFor(nameof(AddPaletteCommand), nameof(AddSpriteCommand))] private bool _isPainted;
    [ObservableProperty] private int _zoom = 4;
    [ObservableProperty] private bool _showGrid = true;
    [ObservableProperty] private bool _fillTool;
    [ObservableProperty] private bool _pencilTool = true;
    [ObservableProperty] private int _cellWidth = 8, _cellHeight = 8;
    [ObservableProperty] private int _paintColour = 1;
    [ObservableProperty] private Brush _paintBrush = Brushes.Black;
    [ObservableProperty] private string _paintInfo = "";
    [ObservableProperty] private string _pointer = "";

    // map sprites
    [ObservableProperty] private bool _isMapSprite;
    [ObservableProperty] private string _spriteInfo = "";

    protected override IEnumerable<string> ReadNames()
    {
        _assets = L2Gfx.Assets(Rom!);
        var items = _assets.Select(a => new L2GfxItem { Group = a.Group, Label = a.Name }).ToList();
        var view = CollectionViewSource.GetDefaultView(items);
        view.GroupDescriptions.Add(new PropertyGroupDescription(nameof(L2GfxItem.Group)));
        ItemsView = view;
        OnPropertyChanged(nameof(ItemsView));
        if (!_refreshing) ForgetPainting();   // a new ROM (or Undo): the painting was of the old one
        return items.Select(i => i.Label);
    }

    protected override void Load(int index)
    {
        if (!_refreshing && IsPainted && _asset != null) AskToWrite();
        _asset = _assets[index];
        PaletteOptions = _asset.Palettes.Select((p, i) => new L2Option(i, p.Name)).ToList();
        HasPaletteChoice = PaletteOptions.Count > 1;
        PaletteChoice = _asset.Palettes.Count > 0 ? _asset.DefaultPalette : 0;
        IsMapSprite = _asset.Kind == L2Gfx.Kind.MapSprite;
        PaletteTip = IsMapSprite
            ? "The colours the game shows this sprite with: one of the 8 sprite colour rows, or a new palette. Changing it writes the ROM (Edit, Undo takes it back)."
            : "The colours the picture is shown, saved and matched with. Import writes the picture's colours here when asked to.";
        CanPaint = _asset.Kind != L2Gfx.Kind.Palette;
        if (!_refreshing)
        {
            Status = "";
            _colour = -1;
        }
        Draw(keep: _refreshing);
        if (!_refreshing)
        {
            int big = Math.Max(_pic?.Width ?? 64, _pic?.Height ?? 64);
            Zoom = Math.Clamp(512 / big, 1, 12);
            PaintColour = _pic != null && _pic.Colours.Length > 1 ? 1 : 0;
            ShowPaintColour();
        }
        (CellWidth, CellHeight) = _asset.Kind switch
        {
            L2Gfx.Kind.MapSprite when Rom != null => (L2Gfx.MapSpriteOf(Rom, _asset.Id).FrameWidth * 8, L2Gfx.MapSpriteOf(Rom, _asset.Id).FrameHeight * 8),
            L2Gfx.Kind.Capsule => (16, 16),
            L2Gfx.Kind.Font8x16 => (8, 16),
            _ => (_pic?.Width ?? 8, _pic?.Height ?? 8),
        };
        ShowSpriteInfo();
    }

    // nothing here is a record field: the picture is written by Import and the painter only
    protected override bool IsField(string property) => false;
    protected override string? Write() => null;

    partial void OnPaletteChoiceChanged(int value)
    {
        if (Loading || _refreshing) return;
        if (_asset?.Kind == L2Gfx.Kind.MapSprite) { SetSpriteColours(value); return; }
        Draw(keep: true);
    }

    /// <summary>Decodes the picture again; <paramref name="keep"/> keeps what is being painted (when the size is the same).</summary>
    private void Draw(bool keep = false)
    {
        if (Rom == null || _asset == null) return;
        try { _pic = L2Gfx.Decode(Rom, _asset, PaletteChoice); }
        catch (Exception ex) { _pic = null; _work = null; PictureImage = null; Info = "Can't read this picture: " + ex.Message; return; }
        var p = _pic;
        if (!keep || _work == null || _work.Length != p.Pixels.Length) ResetPainting();
        Render();
        Swatches = p.Colours.Select((c, i) => new L2Swatch
        {
            Index = i,
            Brush = Frozen(c),
            IsSelected = i == _colour,
            Tip = SwatchTip(p, i),
        }).ToList();
        if (_asset.Kind == L2Gfx.Kind.Palette)
            Info = $"{p.RowNames?.Length ?? 0} rows, {p.ColourOffsets?.Count(o => o >= 0) ?? 0} colours. {_asset.Note}";
        else
        {
            string colours = p.Rows > 1 ? $"{p.Colours.Length} colours ({p.Rows} rows of {p.RowSize}; each 8x8 square uses one row)" : $"{p.Colours.Length} colours";
            Info = $"{p.Width} x {p.Height} pixels, {colours}{(p.RomColours ? "" : ", not stored with these graphics")}. {_asset.Note}";
        }
        ShowColour();
        ShowPaintColour();
    }

    /// <summary>The painted colour numbers as a picture.</summary>
    private void Render()
    {
        if (_pic == null || _work == null) { PictureImage = null; return; }
        var p = _pic;
        var px = new int[_work.Length];
        for (int i = 0; i < px.Length; i++)
        {
            int v = _work[i];
            px[i] = p.Transparent0 && v % p.RowSize == 0 || v >= p.Colours.Length ? 0 : (int)p.Colours[v];
        }
        var bmp = BitmapSource.Create(p.Width, p.Height, 96, 96, PixelFormats.Bgra32, null, px, p.Width * 4);
        bmp.Freeze();
        PictureImage = bmp;
    }

    private string SwatchTip(L2Gfx.Picture p, int i)
    {
        int off = OffsetOf(i);
        if (p.RowNames != null)
            return off < 0 ? "Not a colour of this set" : $"{p.RowNames[i / 16]}, colour {i % 16}: {Rom!.ReadUInt16Le(off) & 0x7FFF:X4} at {off:X6}";
        return $"Colour {i} (row {i / p.RowSize}, {i % p.RowSize}): {L2Gfx.Bgr555(p.Colours[i]):X4}" + (i % p.RowSize == 0 && (p.Transparent0 || p.Rows > 1) ? ", see-through" : "") +
               (off >= 0 ? $" at {off:X6}" : "") + (CanPaint ? ". Click to paint with it." : "");
    }

    private int OffsetOf(int i) => _pic?.ColourOffsets is { } o && i >= 0 && i < o.Length ? o[i] : -1;

    // ── the colour editor ───────────────────────────────────────────────────

    [RelayCommand]
    private void PickColour(L2Swatch? s)
    {
        if (s == null) return;
        _colour = s.Index;
        foreach (var x in Swatches) x.IsSelected = x.Index == _colour;
        if (CanPaint) { PaintColour = s.Index; ShowPaintColour(); }
        ShowColour();
    }

    /// <summary>Picks colour <paramref name="index"/> of the picture shown (for tests and the swatches).</summary>
    public void PickColourAt(int index) => PickColour(Swatches.FirstOrDefault(s => s.Index == index));

    private void ShowColour()
    {
        int off = OffsetOf(_colour);
        HasColour = off >= 0 && Rom != null;
        _syncing = true;
        if (HasColour)
        {
            int v = Rom!.ReadUInt16Le(off) & 0x7FFF;
            Red = v & 31; Green = v >> 5 & 31; Blue = v >> 10 & 31;
            Hex = v.ToString("X4");
            int snes = L2Rom.FileToSnes(off);
            ColourInfo = $"Colour {_colour % _pic!.RowSize} of row {_colour / _pic.RowSize}, at {off:X6} (${snes >> 16:X2}:{snes & 0xFFFF:X4}).";
        }
        else
        {
            Red = Green = Blue = 0; Hex = "";
            ColourInfo = _colour < 0 ? "Click a colour above to paint with it or change it." : "This colour isn't stored in the ROM (these graphics have no colours of their own; pick a set of colours above).";
        }
        _syncing = false;
        UpdatePreview();
    }

    private int EditedValue => Math.Clamp(Red, 0, 31) | Math.Clamp(Green, 0, 31) << 5 | Math.Clamp(Blue, 0, 31) << 10;

    private void UpdatePreview() => ColourPreview = Frozen(L2Gfx.Argb(EditedValue));

    partial void OnRedChanged(int value) => FromSliders();
    partial void OnGreenChanged(int value) => FromSliders();
    partial void OnBlueChanged(int value) => FromSliders();

    private void FromSliders()
    {
        if (_syncing) return;
        _syncing = true; Hex = EditedValue.ToString("X4"); _syncing = false;
        UpdatePreview();
    }

    partial void OnHexChanged(string value)
    {
        if (_syncing) return;
        var t = value.Trim().TrimStart('$', '#');
        if (t.Length is < 1 or > 4 || !int.TryParse(t, System.Globalization.NumberStyles.HexNumber, null, out int v) || v > 0x7FFF) return;
        _syncing = true; Red = v & 31; Green = v >> 5 & 31; Blue = v >> 10 & 31; _syncing = false;
        UpdatePreview();
    }

    /// <summary>Writes the colour in the editor to the ROM (one Undo step) and redraws the picture.</summary>
    [RelayCommand]
    private void ApplyColour()
    {
        if (Rom == null || Main == null || _asset == null) return;
        int off = OffsetOf(_colour);
        if (off < 0) { Status = "Pick a colour first."; return; }
        if (!L2Gfx.SetColour(Rom, off, EditedValue)) { Status = "Nothing changed: the colour is the same."; return; }
        Main.NotifyRomModified($"Lufia II: colour {EditedValue:X4} at {off:X6} ({_asset.Name})");
        Draw(keep: true);
        Status = $"Colour written at {off:X6}. Every picture using it changes too. Edit, Undo takes it back.";
    }

    /// <summary>The colour editor's Apply (for tests).</summary>
    public void ApplyColourNow() => ApplyColour();

    private static Brush Frozen(uint c)
    {
        var b = new SolidColorBrush(Color.FromRgb((byte)(c >> 16), (byte)(c >> 8), (byte)c));
        b.Freeze();
        return b;
    }

    // ── the painter ─────────────────────────────────────────────────────────

    private void ResetPainting()
    {
        _work = _pic == null ? null : (byte[])_pic.Pixels.Clone();
        _strokeStart = null;
        _undo.Clear(); _redo.Clear();
        UpdatePainted();
    }

    private void ForgetPainting()
    {
        _work = null; _strokeStart = null;
        _undo.Clear(); _redo.Clear();
        IsPainted = false;
        PaintUndoCommand.NotifyCanExecuteChanged(); PaintRedoCommand.NotifyCanExecuteChanged();
    }

    private void UpdatePainted()
    {
        IsPainted = _work != null && _pic != null && !_work.AsSpan().SequenceEqual(_pic.Pixels);
        PaintUndoCommand.NotifyCanExecuteChanged();
        PaintRedoCommand.NotifyCanExecuteChanged();
        WritePaintingCommand.NotifyCanExecuteChanged();
        DiscardPaintingCommand.NotifyCanExecuteChanged();
        if (IsPainted) PaintInfo = $"Painted, not written yet ({_undo.Count} stroke{(_undo.Count == 1 ? "" : "s")}). Write the painting puts it into the ROM.";
        else if (CanPaint) PaintInfo = "Left click paints, right click picks the colour under the mouse.";
        else PaintInfo = "A palette has no pixels to paint: click a colour to change it.";
    }

    partial void OnFillToolChanged(bool value) { if (PencilTool == value) PencilTool = !value; }
    partial void OnPencilToolChanged(bool value) { if (FillTool == value) FillTool = !value; }

    private void ShowPaintColour()
    {
        if (_pic == null || PaintColour < 0 || PaintColour >= _pic.Colours.Length) { PaintBrush = Brushes.Transparent; return; }
        bool clear = PaintColour % _pic.RowSize == 0 && (_pic.Transparent0 || _pic.Rows > 1);
        PaintBrush = clear ? Brushes.Transparent : Frozen(_pic.Colours[PaintColour]);
    }

    [RelayCommand] private void ZoomIn() => Zoom = Math.Min(Zoom + (Zoom >= 8 ? 2 : 1), 24);
    [RelayCommand] private void ZoomOut() => Zoom = Math.Max(Zoom - (Zoom > 8 ? 2 : 1), 1);

    /// <summary>The value colour <paramref name="c"/> is painted as at (x, y), or -1 with why: on a tilemap screen
    /// each 8x8 square shows one row of colours, so a colour of another row can't go into a square that already has
    /// colours of its own (a see-through colour takes the square's row).</summary>
    private int Fit(byte[] px, int x, int y, int c, out string why)
    {
        why = "";
        var p = _pic!;
        if (p.Rows <= 1 || _asset!.Kind is not (L2Gfx.Kind.BattleBackground or L2Gfx.Kind.Title)) return c;
        int rs = p.RowSize, w = p.Width, cx = x / 8 * 8, cy = y / 8 * 8, row = -1;
        for (int i = 0; i < 64 && row < 0; i++)
        {
            int xx = cx + i % 8, yy = cy + i / 8;
            if (xx == x && yy == y) continue;
            int v = px[yy * w + xx];
            if (v % rs != 0) row = v / rs;
        }
        if (row < 0 || c / rs == row) return c;
        if (c % rs == 0) return row * rs;
        why = $"The 8x8 square at ({cx}, {cy}) uses colour row {row}: a square shows only one row of {rs} colours. Pick a colour of row {row}, or paint its other pixels with a see-through colour first.";
        return -1;
    }

    private void PaintPixel(int x, int y)
    {
        if (_pic == null || _work == null) return;
        int v = Fit(_work, x, y, PaintColour, out var why);
        if (v < 0) { PaintInfo = _refused = why; return; }
        int i = y * _pic.Width + x;
        if (_work[i] == v) return;
        _work[i] = (byte)v;
        Render();
    }

    /// <summary>Fills the area of one colour around (x, y) (4 ways; a map sprite's frame at most), all or nothing.</summary>
    private void Fill(int x, int y)
    {
        if (_pic == null || _work == null) return;
        int w = _pic.Width, h = _pic.Height, from = _work[y * w + x];
        bool frame = _asset?.Kind == L2Gfx.Kind.MapSprite;
        int x0 = frame ? x / CellWidth * CellWidth : 0, x1 = frame ? x0 + CellWidth : w;
        var n = (byte[])_work.Clone();
        var seen = new bool[n.Length];
        var todo = new Stack<int>();
        todo.Push(y * w + x); seen[y * w + x] = true;
        while (todo.Count > 0)
        {
            int i = todo.Pop(), px = i % w, py = i / w;
            int v = Fit(n, px, py, PaintColour, out var why);
            if (v < 0) { PaintInfo = _refused = "Not filled: " + why; return; }
            n[i] = (byte)v;
            void Next(int nx, int ny) { if (nx < x0 || ny < 0 || nx >= x1 || ny >= h) return; int j = ny * w + nx; if (!seen[j] && _work[j] == from) { seen[j] = true; todo.Push(j); } }
            Next(px + 1, py); Next(px - 1, py); Next(px, py + 1); Next(px, py - 1);
        }
        _work = n;
        Render();
    }

    /// <summary>A mouse button went down on pixel (x, y) of the picture (the view's canvas).</summary>
    public void PaintPress(int x, int y, bool right)
    {
        if (!CanPaint || _pic == null || _work == null || !IsEditable) return;
        int i = y * _pic.Width + x;
        if (i < 0 || i >= _work.Length) return;
        if (right)
        {
            PickColourAt(_work[i]);
            PaintColour = _work[i];
            ShowPaintColour();
            return;
        }
        _strokeStart = (byte[])_work.Clone();
        _refused = null;
        if (FillTool) { Fill(x, y); PaintRelease(); return; }
        PaintPixel(x, y);
    }

    public void PaintDrag(int x, int y) { if (_strokeStart != null && !FillTool) PaintPixel(x, y); }

    public void PaintRelease()
    {
        if (_strokeStart == null || _work == null) return;
        if (!_strokeStart.AsSpan().SequenceEqual(_work))
        {
            _undo.Add(_strokeStart);
            if (_undo.Count > 200) _undo.RemoveAt(0);
            _redo.Clear();
        }
        _strokeStart = null;
        UpdatePainted();
        if (_refused != null) PaintInfo = _refused;
    }

    public void PaintHover(int x, int y) =>
        Pointer = x < 0 || _work == null || _pic == null ? "" : $"({x}, {y}): colour {_work[y * _pic.Width + x]}";

    private bool CanPaintUndo() => _undo.Count > 0;
    private bool CanPaintRedo() => _redo.Count > 0;

    [RelayCommand(CanExecute = nameof(CanPaintUndo))]
    private void PaintUndo()
    {
        if (_undo.Count == 0 || _work == null) return;
        _redo.Add(_work);
        _work = _undo[^1]; _undo.RemoveAt(_undo.Count - 1);
        Render(); UpdatePainted();
    }

    [RelayCommand(CanExecute = nameof(CanPaintRedo))]
    private void PaintRedo()
    {
        if (_redo.Count == 0 || _work == null) return;
        _undo.Add(_work);
        _work = _redo[^1]; _redo.RemoveAt(_redo.Count - 1);
        Render(); UpdatePainted();
    }

    /// <summary>Ctrl+Z / Ctrl+Y in the picture: the painter's strokes while there are some; false lets the key go on
    /// to the app's Undo.</summary>
    public bool PaintKey(bool redo)
    {
        if (redo ? !CanPaintRedo() : !CanPaintUndo()) return false;
        if (redo) PaintRedo(); else PaintUndo();
        return true;
    }

    private bool CanWritePainting() => IsPainted;

    /// <summary>Writes the painting into the ROM the way Import PNG does (packed files are packed again and move to the
    /// added space when they grow); one step for Edit, Undo.</summary>
    [RelayCommand(CanExecute = nameof(CanWritePainting))]
    private void WritePainting()
    {
        if (Rom == null || Main == null || _asset == null || _work == null || !IsPainted) return;
        var before = Rom.ToArray();
        string what;
        try { what = L2Gfx.Import(Rom, _asset, PaletteChoice, _work, null); }
        catch (Exception ex)
        {
            if (!Rom.SameAs(before)) Rom.ReplaceAll(before);
            Status = "Not written: " + ex.Message + " The painting is kept.";
            return;
        }
        if (Rom.SameAs(before)) { Status = "Nothing changed: the painting is the same as the game's picture."; Draw(); return; }
        bool grew = Rom.Length > before.Length;
        Main.NotifyRomModified($"Lufia II: painted {_asset.Group.ToLowerInvariant()} {_asset.Name}");
        Draw();
        Status = $"Painting written: {what}." + (grew ? " The ROM grew to make room." : "") +
                 (_asset.Kind == L2Gfx.Kind.BattleBackground && _asset.Note.Contains("Shares") ? " Backgrounds that share its layout or tiles changed with it." : "") +
                 SharingNote() + " Edit, Undo takes it back.";
    }

    /// <summary>Writes the painting now (for tests).</summary>
    public void WritePaintingNow() => WritePainting();

    [RelayCommand(CanExecute = nameof(CanWritePainting))]
    private void DiscardPainting()
    {
        ResetPainting();
        Render();
        Status = "Painting thrown away: the picture is the game's again.";
    }

    private void AskToWrite()
    {
        var r = System.Windows.MessageBox.Show($"Write your painting of {_asset!.Name} into the ROM before showing another picture?\n\nNo throws the painting away.",
            "Painting not written", System.Windows.MessageBoxButton.YesNo, System.Windows.MessageBoxImage.Question);
        if (r == System.Windows.MessageBoxResult.Yes) WritePainting();
        ForgetPainting();
    }

    // ── map sprites: their colours, new palettes, new sprites ───────────────

    private string SharingNote()
    {
        if (Rom == null || _asset?.Kind != L2Gfx.Kind.MapSprite) return "";
        var t = L2Sprites.SharingTiles(Rom, _asset.Id);
        return t.Count > 0 ? $" Sprite{(t.Count == 1 ? "" : "s")} {string.Join(", ", t.Select(s => s.ToString("X2")))} use{(t.Count == 1 ? "s" : "")} the same tiles and changed too." : "";
    }

    private void ShowSpriteInfo()
    {
        if (Rom == null || _asset?.Kind != L2Gfx.Kind.MapSprite) { SpriteInfo = ""; return; }
        int id = _asset.Id, added = L2Sprites.Count(Rom) - L2Sprites.GameCount;
        var parts = new List<string>
        {
            $"A character shows sprite {id:X2} when a script makes it with that number: in the Events tab, 68 and 7B create a character (id, sprite) and 34 changes a character's sprite.",
            $"New sprites: {added} of {L2Sprites.MaxNew} made (FB, FC and FD are the only free numbers: the game keeps a sprite number in one byte, FE is an event object and FF no sprite).",
            $"New palettes: {L2Sprites.PaletteCount(Rom)}. On a map the game puts a new palette into one of the 8 sprite colour rows that no other character there is using (field monster rows first; row {L2Sprites.RowOf(Rom, id)}, this sprite's own, when all are taken).",
        };
        var t = L2Sprites.SharingTiles(Rom, id);
        if (t.Count > 0) parts.Add($"Uses the same tiles as {string.Join(", ", t.Select(s => s.ToString("X2")))}: painting one paints both.");
        var e = L2Sprites.SharingEntry(Rom, id);
        if (e.Count > 0) parts.Add($"Shares its entry with {string.Join(", ", e.Select(s => s.ToString("X2")))}: a colour row change changes both.");
        SpriteInfo = string.Join(" ", parts);
    }

    /// <summary>The list read again (after a sprite or palette was added), showing the item <paramref name="select"/>
    /// gives (an index into the new list).</summary>
    private void Refresh(Func<int> select, bool keep)
    {
        _refreshing = keep;
        try
        {
            var names = ReadNames().ToList();
            Names.Clear();
            foreach (var n in names) Names.Add(n);
            if (!keep) ForgetPainting();
            SelectedIndex = -1;
            SelectedIndex = Math.Clamp(select(), 0, Names.Count - 1);
        }
        finally { _refreshing = false; }
    }

    private int IndexOfSprite(int id) => _assets.FindIndex(a => a.Kind == L2Gfx.Kind.MapSprite && a.Id == id);

    /// <summary>The map sprite's colours from the Colours list: a sprite row, or a new palette.</summary>
    private void SetSpriteColours(int choice)
    {
        if (Rom == null || Main == null || _asset == null) return;
        int id = _asset.Id;
        bool changed;
        string what;
        try
        {
            if (choice < 8) { changed = L2Sprites.SetPalette(Rom, id, 0); changed |= L2Sprites.SetRow(Rom, id, choice); what = $"colour row {choice}"; }
            else { changed = L2Sprites.SetPalette(Rom, id, choice - 7); what = $"new palette {choice - 7}"; }
        }
        catch (Exception ex) { Status = "Not changed: " + ex.Message; return; }
        if (!changed) { Draw(keep: true); return; }
        Main.NotifyRomModified($"Lufia II: map sprite {id:X2} uses {what}");
        var shared = choice < 8 ? L2Sprites.SharingEntry(Rom, id) : new List<int>();
        int at = SelectedIndex;
        Refresh(() => at, keep: true);
        Status = $"Sprite {id:X2} now uses {what}." + (shared.Count > 0 ? $" Sprite{(shared.Count == 1 ? "" : "s")} {string.Join(", ", shared.Select(s => s.ToString("X2")))} share{(shared.Count == 1 ? "s" : "")} its entry and changed too." : "") + " Edit, Undo takes it back.";
    }

    private bool CanAdd() => !IsPainted;

    /// <summary>Adds a palette for map sprites (black, or the colours shown) and gives it to the sprite shown.</summary>
    [RelayCommand(CanExecute = nameof(CanAdd))]
    private void AddPalette(string? how)
    {
        if (Rom == null || Main == null || _asset?.Kind != L2Gfx.Kind.MapSprite || _pic == null) return;
        bool copy = how == "copy";
        var colours = Enumerable.Range(0, 16).Select(c => !copy ? 0 : OffsetOf(c) is var o && o >= 0 ? Rom.ReadUInt16Le(o) & 0x7FFF : L2Gfx.Bgr555(_pic.Colours[c])).ToArray();
        var before = Rom.ToArray();
        int p, id = _asset.Id;
        try { p = L2Sprites.AddPalette(Rom, colours); L2Sprites.SetPalette(Rom, id, p); }
        catch (Exception ex)
        {
            if (!Rom.SameAs(before)) Rom.ReplaceAll(before);
            Status = "Not added: " + ex.Message;
            return;
        }
        bool grew = Rom.Length > before.Length;
        Main.NotifyRomModified($"Lufia II: new map sprite palette {p} ({(copy ? "a copy of the colours of" : "black, for")} sprite {id:X2})");
        Refresh(() => IndexOfSprite(id), keep: false);
        Status = $"New palette {p} made ({(copy ? "a copy of the colours shown" : "all black")}) and given to sprite {id:X2}. Click a colour above to change it." +
                 (grew ? " The ROM grew to make room." : "") + " Edit, Undo takes it back.";
    }

    /// <summary>Adds a map sprite (FB, FC, FD) like the one shown: a copy of it, or empty with its size and frames.</summary>
    [RelayCommand(CanExecute = nameof(CanAdd))]
    private void AddSprite(string? how)
    {
        if (Rom == null || Main == null || _asset?.Kind != L2Gfx.Kind.MapSprite) return;
        bool empty = how == "empty";
        var before = Rom.ToArray();
        int id, from = _asset.Id;
        try { id = L2Sprites.AddSprite(Rom, from, empty); }
        catch (Exception ex)
        {
            if (!Rom.SameAs(before)) Rom.ReplaceAll(before);
            Status = "Not added: " + ex.Message;
            return;
        }
        bool grew = Rom.Length > before.Length;
        Main.NotifyRomModified($"Lufia II: new map sprite {id:X2} ({(empty ? "empty, the size of" : "a copy of")} sprite {from:X2})");
        Refresh(() => IndexOfSprite(id), keep: false);
        Status = $"Map sprite {id:X2} made ({(empty ? $"empty, with the size, frames and colours of {from:X2}" : $"a copy of {from:X2}")}); paint it below. " +
                 $"A character shows it when a script makes it with sprite {id:X2} (Events tab: 68 or 7B create a character, 34 changes its sprite)." +
                 (grew ? " The ROM grew to make room." : "") + " Edit, Undo takes it back.";
    }

    /// <summary>Picks the list item of map sprite <paramref name="id"/> (for tests).</summary>
    public void ShowSprite(int id) { int i = IndexOfSprite(id); if (i >= 0) SelectedIndex = i; }

    // ── PNG ──────────────────────────────────────────────────────────────────

    private string FileName() => string.Concat(("Lufia II " + _asset!.Group + " " + _asset.Name).Select(c => Path.GetInvalidFileNameChars().Contains(c) || c == ':' ? '_' : c)) + ".png";

    [RelayCommand]
    private void Export()
    {
        if (_pic == null || _asset == null) return;
        var dlg = new SaveFileDialog { Title = "Save the picture", Filter = "PNG picture (*.png)|*.png", FileName = FileName() };
        if (dlg.ShowDialog() != true) return;
        ExportTo(dlg.FileName);
    }

    /// <summary>Saves the picture shown (as painted) as a palette PNG (the dialog's part split off so it can be tested).</summary>
    public void ExportTo(string path)
    {
        if (_pic == null) return;
        try { IndexedPng.Save(path, _pic.Width, _pic.Height, _work ?? _pic.Pixels, _pic.Colours, _pic.Transparent0); }
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
            if ((TakeColours || _asset.Kind == L2Gfx.Kind.Palette) && _pic.RomColours) colours = png.Palette;
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
            if (_asset.Kind == L2Gfx.Kind.Palette)
            {
                // a palette is its squares: the colour in the middle of each
                idx = _pic.Pixels;
                int cell = L2Gfx.PaletteCell;
                colours = Enumerable.Range(0, _pic.Colours.Length).Select(i => px[(i / 16 * cell + cell / 2) * w + i % 16 * cell + cell / 2]).ToArray();
                how = "colours read from the middle of each square";
            }
            else
            {
                idx = L2Gfx.MatchColours(px, _pic);
                how = "colours matched to the ones shown" + (_pic.Rows > 1 ? " (each 8x8 square to its best row)" : "");
            }
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
