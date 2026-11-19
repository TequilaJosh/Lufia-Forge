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

// a picture in the Graphics list (grouped by kind)
public sealed class L2GfxItem
{
    public string Group { get; init; } = "";
    public string Label { get; init; } = "";
    public override string ToString() => Label;
}

// one colour of the palette shown (click it to paint with it and change it)
public sealed partial class L2Swatch : ObservableObject
{
    public int Index { get; init; }
    public Brush Brush { get; init; } = Brushes.Black;
    public string Tip { get; init; } = "";
    [ObservableProperty] private bool _isSelected;
}

// Graphics page: battle backgrounds, capsule monsters, hero battle sprites, the title screen, fonts, menu graphics
// and map sprites (L2Gfx). shown, painted pixel by pixel, saved as PNG and brought back from a PNG. also new
// map sprites and new map sprite palettes (L2Sprites)
public partial class L2GraphicsPage : L2Page, LufiaForge.ViewModels.IPendingEdits
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

    // the painter: the picture's colour numbers as painted, plus the strokes to take back (until it's written)
    private byte[]? _work;
    private byte[]? _strokeStart;
    private string? _refused;   // why a pixel of this stroke wasn't painted
    private readonly List<byte[]> _undo = new(), _redo = new();
    private bool _refreshing;
    [ObservableProperty] private bool _canPaint;
    [ObservableProperty] [NotifyCanExecuteChangedFor(nameof(AddPaletteCommand), nameof(AddSpriteCommand), nameof(AddMonsterPaletteCommand), nameof(AddMonsterSpriteCommand), nameof(GiveToMonsterCommand))] private bool _isPainted;
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

    // monster battle sprites
    [ObservableProperty] private bool _isMonsterSprite;
    [ObservableProperty] private string _monsterInfo = "";

    // a painting that isn't written yet goes in by itself (leaving the page, saving the project, Apply to ROM), same
    // as the Maps page's edits
    protected override void OnListRead() => Main?.Register(this);

    public bool HasPendingEdits => Rom != null && _asset != null && IsPainted;
    public string? WritePendingEdits() => HasPendingEdits ? WritePaintingNow() : null;

    protected override IEnumerable<string> ReadNames()
    {
        _assets = L2Gfx.Assets(Rom!);
        MonsterOptions = L2Battle.Monsters(Rom!).Select(m => new L2Option(m.Index, $"{m.Index:X2} {m.Name}")).ToList();
        var items = _assets.Select(a => new L2GfxItem { Group = a.Group, Label = a.Name }).ToList();
        var view = CollectionViewSource.GetDefaultView(items);
        view.GroupDescriptions.Add(new PropertyGroupDescription(nameof(L2GfxItem.Group)));
        ItemsView = view;
        OnPropertyChanged(nameof(ItemsView));
        if (!_refreshing) ForgetPainting();   // new ROM (or Undo), the painting was of the old one
        return items.Select(i => i.Label);
    }

    protected override void Load(int index)
    {
        if (!_refreshing && IsPainted && _asset != null && !AskToWrite())
        {
            // the painting couldn't be written, so it stays along with its picture (and the reason on screen)
            int previousIndex = _assets.IndexOf(_asset);
            System.Windows.Threading.Dispatcher.CurrentDispatcher.BeginInvoke(() => { _refreshing = true; try { SelectedIndex = previousIndex; } finally { _refreshing = false; } });
            return;
        }
        _asset = _assets[index];
        PaletteOptions = _asset.Palettes.Select((p, i) => new L2Option(i, p.Name)).ToList();
        HasPaletteChoice = PaletteOptions.Count > 1;
        PaletteChoice = _asset.Palettes.Count > 0 ? _asset.DefaultPalette : 0;
        IsMapSprite = _asset.Kind == L2Gfx.Kind.MapSprite;
        IsMonsterSprite = _asset.Kind == L2Gfx.Kind.MonsterSprite;
        HasPaletteChoice = PaletteOptions.Count > 1 || IsMonsterSprite;
        PaletteTip = IsMapSprite
            ? "The colours the game shows this sprite with: one of the 8 sprite colour rows, or a new palette. Changing it writes the ROM (Edit, Undo takes it back)."
            : IsMonsterSprite
            ? "The sprite's own palettes (a monster names one with its Palette field on the Monsters page). The picture is shown, saved and matched with this one; Import writes the picture's colours into it when asked to."
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
            int longestSide = Math.Max(_pic?.Width ?? 64, _pic?.Height ?? 64);
            Zoom = Math.Clamp(512 / longestSide, 1, 12);
            PaintColour = _pic != null && _pic.Colours.Length > 1 ? 1 : 0;
            ShowPaintColour();
        }
        (CellWidth, CellHeight) = _asset.Kind switch
        {
            L2Gfx.Kind.MapSprite when Rom != null => (L2Gfx.MapSpriteOf(Rom, _asset.Id).FrameWidth * 8, L2Gfx.MapSpriteOf(Rom, _asset.Id).FrameHeight * 8),
            L2Gfx.Kind.Capsule or L2Gfx.Kind.MonsterSprite => (16, 16),
            L2Gfx.Kind.Font8x16 => (8, 16),
            _ => (_pic?.Width ?? 8, _pic?.Height ?? 8),
        };
        ShowSpriteInfo();
        ShowMonsterInfo();
    }

    // nothing here is a record field, only Import and the painter write the picture
    protected override bool IsField(string property) => false;
    protected override string? Write() => null;

    partial void OnPaletteChoiceChanged(int value)
    {
        if (Loading || _refreshing) return;
        if (_asset?.Kind == L2Gfx.Kind.MapSprite) { SetSpriteColours(value); return; }
        Draw(keep: true);
    }

    // decodes the picture again. keep = hold on to what's being painted (when the size is the same)
    private void Draw(bool keep = false)
    {
        if (Rom == null || _asset == null) return;
        try { _pic = L2Gfx.Decode(Rom, _asset, PaletteChoice); }
        catch (Exception ex) { _pic = null; _work = null; PictureImage = null; Info = "Can't read this picture: " + ex.Message; return; }
        var picture = _pic;
        if (!keep || _work == null || _work.Length != picture.Pixels.Length) ResetPainting();
        Render();
        Swatches = picture.Colours.Select((c, i) => new L2Swatch
        {
            Index = i,
            Brush = Frozen(c),
            IsSelected = i == _colour,
            Tip = SwatchTip(picture, i),
        }).ToList();
        if (_asset.Kind == L2Gfx.Kind.Palette)
            Info = $"{picture.RowNames?.Length ?? 0} rows, {picture.ColourOffsets?.Count(o => o >= 0) ?? 0} colours. {_asset.Note}";
        else
        {
            string colours = picture.Rows > 1 ? $"{picture.Colours.Length} colours ({picture.Rows} rows of {picture.RowSize}; each 8x8 square uses one row)" : $"{picture.Colours.Length} colours";
            Info = $"{picture.Width} x {picture.Height} pixels, {colours}{(picture.RomColours ? "" : ", not stored with these graphics")}. {_asset.Note}";
        }
        ShowColour();
        ShowPaintColour();
    }

    // turns the painted colour numbers into a picture
    private void Render()
    {
        if (_pic == null || _work == null) { PictureImage = null; return; }
        var picture = _pic;
        var pixels = new int[_work.Length];
        for (int i = 0; i < pixels.Length; i++)
        {
            int colourNumber = _work[i];
            pixels[i] = picture.Transparent0 && colourNumber % picture.RowSize == 0 || colourNumber >= picture.Colours.Length ? 0 : (int)picture.Colours[colourNumber];
        }
        var bmp = BitmapSource.Create(picture.Width, picture.Height, 96, 96, PixelFormats.Bgra32, null, pixels, picture.Width * 4);
        bmp.Freeze();
        PictureImage = bmp;
    }

    private string SwatchTip(L2Gfx.Picture picture, int i)
    {
        int offset = OffsetOf(i);
        if (picture.RowNames != null)
            return offset < 0 ? "Not a colour of this set" : $"{picture.RowNames[i / 16]}, colour {i % 16}: {Rom!.ReadUInt16Le(offset) & 0x7FFF:X4} at {offset:X6}";
        return $"Colour {i} (row {i / picture.RowSize}, {i % picture.RowSize}): {L2Gfx.Bgr555(picture.Colours[i]):X4}" + (i % picture.RowSize == 0 && (picture.Transparent0 || picture.Rows > 1) ? ", see-through" : "") +
               (offset >= 0 ? $" at {offset:X6}" : IsFileColour(i) ? $", palette {PaletteChoice} of the sprite" : "") + (CanPaint ? ". Click to paint with it." : "");
    }

    private int OffsetOf(int i) => _pic?.ColourOffsets is { } offsets && i >= 0 && i < offsets.Length ? offsets[i] : -1;

    // true if colour i is in a monster sprite's palette (kept in its packed data)
    private bool IsFileColour(int i) => _pic is { FileColours: true } && _asset?.Kind == L2Gfx.Kind.MonsterSprite && i >= 0 && i < 16;

    // the colour editor

    [RelayCommand]
    private void PickColour(L2Swatch? swatch)
    {
        if (swatch == null) return;
        _colour = swatch.Index;
        foreach (var other in Swatches) other.IsSelected = other.Index == _colour;
        if (CanPaint) { PaintColour = swatch.Index; ShowPaintColour(); }
        ShowColour();
    }

    // picks colour index of the picture shown (for tests and the swatches)
    public void PickColourAt(int index) => PickColour(Swatches.FirstOrDefault(s => s.Index == index));

    private void ShowColour()
    {
        int offset = OffsetOf(_colour);
        HasColour = (offset >= 0 || IsFileColour(_colour)) && Rom != null;
        _syncing = true;
        if (HasColour && offset < 0)
        {
            int bgr = L2MonsterSprites.Palette(L2MonsterSprites.Read(Rom!, _asset!.Id), PaletteChoice)[_colour];
            Red = bgr & 31; Green = bgr >> 5 & 31; Blue = bgr >> 10 & 31;
            Hex = bgr.ToString("X4");
            ColourInfo = $"Colour {_colour} of palette {PaletteChoice}, kept with the sprite's tiles ({L2MonsterSprites.Where(Rom!, _asset.Id)}).";
        }
        else if (HasColour)
        {
            int bgr = Rom!.ReadUInt16Le(offset) & 0x7FFF;
            Red = bgr & 31; Green = bgr >> 5 & 31; Blue = bgr >> 10 & 31;
            Hex = bgr.ToString("X4");
            int snes = L2Rom.FileToSnes(offset);
            ColourInfo = $"Colour {_colour % _pic!.RowSize} of row {_colour / _pic.RowSize}, at {offset:X6} (${snes >> 16:X2}:{snes & 0xFFFF:X4}).";
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
        var hexText = value.Trim().TrimStart('$', '#');
        if (hexText.Length is < 1 or > 4 || !int.TryParse(hexText, System.Globalization.NumberStyles.HexNumber, null, out int bgr) || bgr > 0x7FFF) return;
        _syncing = true; Red = bgr & 31; Green = bgr >> 5 & 31; Blue = bgr >> 10 & 31; _syncing = false;
        UpdatePreview();
    }

    // writes the colour in the editor to the ROM (one Undo step) and redraws the picture
    [RelayCommand]
    private void ApplyColour()
    {
        if (Rom == null || Main == null || _asset == null) return;
        int offset = OffsetOf(_colour);
        if (offset < 0 && IsFileColour(_colour))
        {
            var before = Rom.ToArray();
            bool changed;
            try { changed = L2MonsterSprites.SetColour(Rom, _asset.Id, PaletteChoice, _colour, EditedValue); }
            catch (Exception ex) { if (!Rom.SameAs(before)) Rom.ReplaceAll(before); Status = "Not written: " + ex.Message; return; }
            if (!changed) { Status = "Nothing changed: the colour is the same."; return; }
            Main.NotifyRomModified($"Lufia II: colour {_colour} of palette {PaletteChoice} of monster sprite {_asset.Id:X2}");
            MarkRead();
            Draw(keep: true);
            Status = $"Colour {_colour} of palette {PaletteChoice} written. Monsters using this palette of the sprite change too. Edit, Undo takes it back.";
            MonsterSpritesEdited();
            return;
        }
        if (offset < 0) { Status = "Pick a colour first."; return; }
        if (!L2Gfx.SetColour(Rom, offset, EditedValue)) { Status = "Nothing changed: the colour is the same."; return; }
        Main.NotifyRomModified($"Lufia II: colour {EditedValue:X4} at {offset:X6} ({_asset.Name})");
        MarkRead();
        Draw(keep: true);
        Status = $"Colour written at {offset:X6}. Every picture using it changes too. Edit, Undo takes it back.";
    }

    // the colour editor's Apply (for tests)
    public void ApplyColourNow() => ApplyColour();

    private static Brush Frozen(uint argb)
    {
        var brush = new SolidColorBrush(Color.FromRgb((byte)(argb >> 16), (byte)(argb >> 8), (byte)argb));
        brush.Freeze();
        return brush;
    }

    // the painter

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
        bool seeThrough = PaintColour % _pic.RowSize == 0 && (_pic.Transparent0 || _pic.Rows > 1);
        PaintBrush = seeThrough ? Brushes.Transparent : Frozen(_pic.Colours[PaintColour]);
    }

    [RelayCommand] private void ZoomIn() => Zoom = Math.Min(Zoom + (Zoom >= 8 ? 2 : 1), 24);
    [RelayCommand] private void ZoomOut() => Zoom = Math.Max(Zoom - (Zoom > 8 ? 2 : 1), 1);

    // the value colour gets painted as at (x, y), or -1 and why. on a tilemap screen each 8x8 square shows one
    // row of colours, so a colour from another row can't go in a square that already has colours of its own
    // (a see-through colour takes the square's row)
    private int Fit(byte[] pixels, int x, int y, int colour, out string why)
    {
        why = "";
        var picture = _pic!;
        if (picture.Rows <= 1 || _asset!.Kind is not (L2Gfx.Kind.BattleBackground or L2Gfx.Kind.Title)) return colour;
        int rowSize = picture.RowSize, w = picture.Width, squareX = x / 8 * 8, squareY = y / 8 * 8, row = -1;
        for (int i = 0; i < 64 && row < 0; i++)
        {
            int px = squareX + i % 8, py = squareY + i / 8;
            if (px == x && py == y) continue;
            int existing = pixels[py * w + px];
            if (existing % rowSize != 0) row = existing / rowSize;
        }
        if (row < 0 || colour / rowSize == row) return colour;
        if (colour % rowSize == 0) return row * rowSize;
        why = $"The 8x8 square at ({squareX}, {squareY}) uses colour row {row}: a square shows only one row of {rowSize} colours. Pick a colour of row {row}, or paint its other pixels with a see-through colour first.";
        return -1;
    }

    private void PaintPixel(int x, int y)
    {
        if (_pic == null || _work == null) return;
        int value = Fit(_work, x, y, PaintColour, out var why);
        if (value < 0) { PaintInfo = _refused = why; return; }
        int i = y * _pic.Width + x;
        if (_work[i] == value) return;
        _work[i] = (byte)value;
        Render();
    }

    // flood fills the area of one colour around (x, y), 4 ways and inside a map sprite's frame at most. all or nothing
    private void Fill(int x, int y)
    {
        if (_pic == null || _work == null) return;
        int w = _pic.Width, h = _pic.Height, fromColour = _work[y * w + x];
        bool inFrame = _asset?.Kind == L2Gfx.Kind.MapSprite;
        int x0 = inFrame ? x / CellWidth * CellWidth : 0, x1 = inFrame ? x0 + CellWidth : w;
        var filled = (byte[])_work.Clone();
        var seen = new bool[filled.Length];
        var todo = new Stack<int>();
        todo.Push(y * w + x); seen[y * w + x] = true;
        while (todo.Count > 0)
        {
            int i = todo.Pop(), px = i % w, py = i / w;
            int value = Fit(filled, px, py, PaintColour, out var why);
            if (value < 0) { PaintInfo = _refused = "Not filled: " + why; return; }
            filled[i] = (byte)value;
            void Next(int nx, int ny) { if (nx < x0 || ny < 0 || nx >= x1 || ny >= h) return; int j = ny * w + nx; if (!seen[j] && _work[j] == fromColour) { seen[j] = true; todo.Push(j); } }
            Next(px + 1, py); Next(px - 1, py); Next(px, py + 1); Next(px, py - 1);
        }
        _work = filled;
        Render();
    }

    // a mouse button went down on pixel (x, y) of the picture (the view's canvas)
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

    // Ctrl+Z / Ctrl+Y in the picture undo the painter's strokes while there are some. false lets the key go on
    // to the app's Undo
    public bool PaintKey(bool redo)
    {
        if (redo ? !CanPaintRedo() : !CanPaintUndo()) return false;
        if (redo) PaintRedo(); else PaintUndo();
        return true;
    }

    private bool CanWritePainting() => IsPainted;

    // writes the painting into the ROM the same way Import PNG does (packed files get packed again and move to the
    // added space when they grow). one step for Edit, Undo
    [RelayCommand(CanExecute = nameof(CanWritePainting))]
    private void WritePainting() => WritePaintingNow();

    // writes the painting now (tests use it too). returns null, or why it couldn't be written (the painting is kept then)
    public string? WritePaintingNow()
    {
        if (Rom == null || Main == null || _asset == null || _work == null || !IsPainted) return null;
        var before = Rom.ToArray();
        string what;
        try { what = L2Gfx.Import(Rom, _asset, PaletteChoice, _work, null); }
        catch (Exception ex)
        {
            if (!Rom.SameAs(before)) Rom.ReplaceAll(before);
            Status = "Not written: " + ex.Message + " The painting is kept.";
            return $"painting of {_asset.Name}: {ex.Message}";
        }
        if (Rom.SameAs(before)) { Status = "Nothing changed: the painting is the same as the game's picture."; Draw(); return null; }
        bool grew = Rom.Length > before.Length;
        Main.NotifyRomModified($"Lufia II: painted {_asset.Group.ToLowerInvariant()} {_asset.Name}");
        MarkRead();
        Draw();
        Status = $"Painting written: {what}." + (grew ? " The ROM grew to make room." : "") +
                 (_asset.Kind == L2Gfx.Kind.BattleBackground && _asset.Note.Contains("Shares") ? " Backgrounds that share its layout or tiles changed with it." : "") +
                 SharingNote() + " Edit, Undo takes it back.";
        if (_asset.Kind == L2Gfx.Kind.MonsterSprite) MonsterSpritesEdited();
        return null;
    }

    [RelayCommand(CanExecute = nameof(CanWritePainting))]
    private void DiscardPainting()
    {
        ResetPainting();
        Render();
        Status = "Painting thrown away: the picture is the game's again.";
    }

    // false when the painting was supposed to be written and couldn't be (it's kept then, Status says why)
    private bool AskToWrite()
    {
        var answer = System.Windows.MessageBox.Show($"Write your painting of {_asset!.Name} into the ROM before showing another picture?\n\nNo throws the painting away.",
            "Painting not written", System.Windows.MessageBoxButton.YesNo, System.Windows.MessageBoxImage.Question);
        if (answer == System.Windows.MessageBoxResult.Yes && WritePaintingNow() != null) return false;
        ForgetPainting();
        return true;
    }

    // map sprites: their colours, new palettes, new sprites

    private string SharingNote()
    {
        if (Rom == null || _asset?.Kind != L2Gfx.Kind.MapSprite) return "";
        var sharing = L2Sprites.SharingTiles(Rom, _asset.Id);
        return sharing.Count > 0 ? $" Sprite{(sharing.Count == 1 ? "" : "s")} {string.Join(", ", sharing.Select(L2Sprites.Name))} use{(sharing.Count == 1 ? "s" : "")} the same tiles and changed too." : "";
    }

    private void ShowSpriteInfo()
    {
        if (Rom == null || _asset?.Kind != L2Gfx.Kind.MapSprite) { SpriteInfo = ""; return; }
        int id = _asset.Id, added = L2Sprites.Count(Rom) - L2Sprites.GameCount;
        var parts = new List<string>
        {
            L2Sprites.IsNew(id)
                ? $"A character shows {L2Sprites.Name(id)} when the Maps tab gives it this sprite: on each map a new sprite is FB, FC or FD (the game keeps a sprite number in one byte, and FE and FF mean an event object and no sprite), so one map can show 3 new sprites. In the Events tab, 68 and 7B create a character (id, sprite) and 34 changes a character's sprite."
                : $"A character shows sprite {id:X2} when a script makes it with that number: in the Events tab, 68 and 7B create a character (id, sprite) and 34 changes a character's sprite.",
            $"New sprites: {added} of {L2Sprites.MaxNew} made, any 3 of them on one map.",
            $"New palettes: {L2Sprites.PaletteCount(Rom)}. On a map the game puts a new palette into one of the 8 sprite colour rows that no other character there is using (field monster rows first; row {L2Sprites.RowOf(Rom, id)}, this sprite's own, when all are taken).",
        };
        var sameTiles = L2Sprites.SharingTiles(Rom, id);
        if (sameTiles.Count > 0) parts.Add($"Uses the same tiles as {string.Join(", ", sameTiles.Select(L2Sprites.Name))}: painting one paints both.");
        var sameEntry = L2Sprites.SharingEntry(Rom, id);
        if (sameEntry.Count > 0) parts.Add($"Shares its entry with {string.Join(", ", sameEntry.Select(L2Sprites.Name))}: a colour row change changes both.");
        SpriteInfo = string.Join(" ", parts);
    }

    // reads the list again (after a sprite or palette was added) and shows the item select gives (an index
    // into the new list)
    private void Refresh(Func<int> select, bool keep)
    {
        _refreshing = keep;
        try
        {
            var names = ReadNames().ToList();
            Names.Clear();
            foreach (var name in names) Names.Add(name);
            if (!keep) ForgetPainting();
            SelectedIndex = -1;
            SelectedIndex = Math.Clamp(select(), 0, Names.Count - 1);
        }
        finally { _refreshing = false; }
    }

    private int IndexOfSprite(int id) => _assets.FindIndex(a => a.Kind == L2Gfx.Kind.MapSprite && a.Id == id);

    // sets the map sprite's colours from the Colours list: a sprite row, or a new palette
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
        Main.NotifyRomModified($"Lufia II: map sprite {L2Sprites.Name(id)} uses {what}");
        MarkRead();
        var shared = choice < 8 ? L2Sprites.SharingEntry(Rom, id) : new List<int>();
        int selected = SelectedIndex;
        Refresh(() => selected, keep: true);
        Status = $"Sprite {L2Sprites.Name(id)} now uses {what}." + (shared.Count > 0 ? $" Sprite{(shared.Count == 1 ? "" : "s")} {string.Join(", ", shared.Select(L2Sprites.Name))} share{(shared.Count == 1 ? "s" : "")} its entry and changed too." : "") + " Edit, Undo takes it back.";
    }

    private bool CanAdd() => !IsPainted;

    // adds a palette for map sprites (black, or the colours shown) and gives it to the sprite shown
    [RelayCommand(CanExecute = nameof(CanAdd))]
    private void AddPalette(string? how)
    {
        if (Rom == null || Main == null || _asset?.Kind != L2Gfx.Kind.MapSprite || _pic == null) return;
        bool copy = how == "copy";
        var colours = Enumerable.Range(0, 16).Select(c => !copy ? 0 : OffsetOf(c) is var offset && offset >= 0 ? Rom.ReadUInt16Le(offset) & 0x7FFF : L2Gfx.Bgr555(_pic.Colours[c])).ToArray();
        var before = Rom.ToArray();
        int palette, id = _asset.Id;
        try { palette = L2Sprites.AddPalette(Rom, colours); L2Sprites.SetPalette(Rom, id, palette); }
        catch (Exception ex)
        {
            if (!Rom.SameAs(before)) Rom.ReplaceAll(before);
            Status = "Not added: " + ex.Message;
            return;
        }
        bool grew = Rom.Length > before.Length;
        Main.NotifyRomModified($"Lufia II: new map sprite palette {palette} ({(copy ? "a copy of the colours of" : "black, for")} sprite {L2Sprites.Name(id)})");
        MarkRead();
        Refresh(() => IndexOfSprite(id), keep: false);
        Status = $"New palette {palette} made ({(copy ? "a copy of the colours shown" : "all black")}) and given to sprite {L2Sprites.Name(id)}. Click a colour above to change it." +
                 (grew ? " The ROM grew to make room." : "") + " Edit, Undo takes it back.";
    }

    // adds a map sprite ("New n") like the one shown: a copy of it, or empty with its size and frames
    [RelayCommand(CanExecute = nameof(CanAdd))]
    private void AddSprite(string? how)
    {
        if (Rom == null || Main == null || _asset?.Kind != L2Gfx.Kind.MapSprite) return;
        bool empty = how == "empty";
        var before = Rom.ToArray();
        int id, fromSprite = _asset.Id;
        try { id = L2Sprites.AddSprite(Rom, fromSprite, empty); }
        catch (Exception ex)
        {
            if (!Rom.SameAs(before)) Rom.ReplaceAll(before);
            Status = "Not added: " + ex.Message;
            return;
        }
        bool grew = Rom.Length > before.Length;
        Main.NotifyRomModified($"Lufia II: new map sprite {L2Sprites.Name(id)} ({(empty ? "empty, the size of" : "a copy of")} sprite {L2Sprites.Name(fromSprite)})");
        MarkRead();
        Refresh(() => IndexOfSprite(id), keep: false);
        Status = $"Map sprite {L2Sprites.Name(id)} made ({(empty ? $"empty, with the size, frames and colours of {L2Sprites.Name(fromSprite)}" : $"a copy of {L2Sprites.Name(fromSprite)}")}); paint it below. " +
                 "Give it to a character in the Maps tab: on that map it becomes FB, FC or FD (3 new sprites a map)." +
                 (grew ? " The ROM grew to make room." : "") + " Edit, Undo takes it back.";
    }

    // selects map sprite id in the list (for tests)
    public void ShowSprite(int id) { int i = IndexOfSprite(id); if (i >= 0) SelectedIndex = i; }

    // monster battle sprites: new palettes, new sprites, which monster shows one

    // the monsters for "Give it to"
    [ObservableProperty] private List<L2Option> _monsterOptions = new();
    [ObservableProperty] private int _giveTo = -1;
    // the shown sprite's movement flags in battle (L2MonsterSprites.MoveFlags)
    [ObservableProperty] private List<L2Flag> _moveFlags = new();
    [ObservableProperty] private string _moveNote = "";
    private bool _movesLoading;

    // fires when a monster sprite was added, painted, recoloured, given a palette or a movement, or given to a
    // monster (so the Monsters and Encounters pages show it again)
    public event Action? MonsterSpritesChanged;

    private void MonsterSpritesEdited() => MonsterSpritesChanged?.Invoke();

    private void ShowMoves()
    {
        if (Rom == null || _asset?.Kind != L2Gfx.Kind.MonsterSprite) { MoveFlags = new(); MoveNote = ""; return; }
        int moves = L2MonsterSprites.Size(Rom, _asset.Id).Moves;
        _movesLoading = true;
        MoveFlags = L2MonsterSprites.MoveFlags.Select(f => new L2Flag { Label = f.Name, Bit = f.Bit, IsChecked = (moves & f.Bit) != 0, Changed = MovesEdited }).ToList();
        _movesLoading = false;
        MoveNote = MoveNoteFor(moves);
    }

    private static string MoveNoteFor(int moves) =>
        $"In battle it {L2MonsterSprites.MovesText(moves)}. " +
        string.Join(" ", L2MonsterSprites.MoveFlags.Where(f => f.Bit is L2MonsterSprites.MoveFloat or L2MonsterSprites.MoveShake or L2MonsterSprites.MoveTwitch).Select(f => $"{f.Name}: {f.What}")) +
        " None of the three: it stands still. Every monster with this sprite moves the same way; a status can take over (the game makes some statuses shake or float).";

    private void MovesEdited()
    {
        if (_movesLoading || Rom == null || Main == null || _asset?.Kind != L2Gfx.Kind.MonsterSprite) return;
        int id = _asset.Id, moves = MoveFlags.Where(f => f.IsChecked).Sum(f => f.Bit);
        if (!L2MonsterSprites.SetMoves(Rom, id, moves)) return;
        Main.NotifyRomModified($"Lufia II: monster sprite {id:X2} {L2MonsterSprites.MovesText(moves)} in battle");
        MarkRead();
        MoveNote = MoveNoteFor(moves);
        Status = $"Sprite {id:X2} now {L2MonsterSprites.MovesText(moves)} in battle (movement flags {moves:X2}). Edit, Undo takes it back.";
        MonsterSpritesEdited();
    }

    // after monsters were added or renamed on the Monsters page, so "Give it to" lists them
    public void RefreshMonsterOptions()
    {
        if (Rom == null) return;
        int previousChoice = GiveTo;
        MonsterOptions = L2Battle.Monsters(Rom).Select(m => new L2Option(m.Index, $"{m.Index:X2} {m.Name}")).ToList();
        GiveTo = previousChoice;
    }

    // sets the shown sprite's movement flags (for tests)
    public void SetMovesNow(int moves) { foreach (var flag in MoveFlags) flag.IsChecked = (moves & flag.Bit) != 0; }

    private int IndexOfMonsterSprite(int id) => _assets.FindIndex(a => a.Kind == L2Gfx.Kind.MonsterSprite && a.Id == id);

    // selects monster sprite id in the list (for tests)
    public void ShowMonsterSprite(int id) { int i = IndexOfMonsterSprite(id); if (i >= 0) SelectedIndex = i; }

    private void ShowMonsterInfo()
    {
        if (Rom == null || _asset?.Kind != L2Gfx.Kind.MonsterSprite) { MonsterInfo = ""; return; }
        int id = _asset.Id, added = L2MonsterSprites.Count(Rom) - L2MonsterSprites.GameCount;
        var (tilesX, tilesY, _) = L2MonsterSprites.Size(Rom, id);
        var (squaresX, squaresY) = L2MonsterSprites.Squares(tilesX, tilesY);
        bool canInstall = L2MonsterSprites.CanInstall(Rom, out var why);
        MonsterInfo = $"A monster shows sprite {id:X2} ({id}) when its Battle sprite field on the Monsters page holds {id}, with the palette its Palette field names. " +
                      $"This one is {squaresX * squaresY} of the {L2MonsterSprites.MaxSquares} squares of 16 x 16 that all the kinds of monster in one battle share. " +
                      $"New sprites: {added} made, up to {L2MonsterSprites.MaxNew} more (numbers {L2MonsterSprites.GameCount + 1} to {L2MonsterSprites.MaxNumber}: a monster keeps its sprite in one byte)." +
                      (canInstall ? "" : " " + why);
        var user = L2MonsterSprites.Users(Rom, id).FirstOrDefault();
        GiveTo = user?.Index ?? (GiveTo >= 0 ? GiveTo : 0);
        ShowMoves();
    }

    // adds a palette to the monster sprite shown (black, or a copy of the colours shown) and shows it
    [RelayCommand(CanExecute = nameof(CanAdd))]
    private void AddMonsterPalette(string? how)
    {
        if (Rom == null || Main == null || _asset?.Kind != L2Gfx.Kind.MonsterSprite || _pic == null) return;
        bool copy = how == "copy";
        int id = _asset.Id;
        var colours = copy ? L2MonsterSprites.Palette(L2MonsterSprites.Read(Rom, id), PaletteChoice) : new int[16];
        var before = Rom.ToArray();
        int palette;
        try { palette = L2MonsterSprites.AddPalette(Rom, id, colours); }
        catch (Exception ex) { if (!Rom.SameAs(before)) Rom.ReplaceAll(before); Status = "Not added: " + ex.Message; return; }
        bool grew = Rom.Length > before.Length;
        Main.NotifyRomModified($"Lufia II: palette {palette} of monster sprite {id:X2} ({(copy ? "a copy" : "black")})");
        MarkRead();
        Refresh(() => IndexOfMonsterSprite(id), keep: false);
        PaletteChoice = palette;
        Status = $"Palette {palette} added to sprite {id:X2} ({(copy ? "a copy of the colours shown" : "all black")}). Click its colours above to change them; give it to a monster below or with the Palette field on the Monsters page." +
                 (grew ? " The ROM grew to make room." : "") + " Edit, Undo takes it back.";
        MonsterSpritesEdited();
    }

    // adds a monster sprite: a copy of the one shown, or from a picture file (the dialog)
    [RelayCommand(CanExecute = nameof(CanAdd))]
    private void AddMonsterSprite(string? how)
    {
        if (Rom == null || Main == null || _asset?.Kind != L2Gfx.Kind.MonsterSprite) return;
        if (how == "png")
        {
            var dlg = new OpenFileDialog { Title = "A picture for the new monster sprite", Filter = "Pictures (*.png;*.bmp;*.gif)|*.png;*.bmp;*.gif|All files|*.*" };
            if (dlg.ShowDialog() != true) return;
            AddMonsterSpriteFrom(dlg.FileName);
            return;
        }
        int fromSprite = _asset.Id;
        AddMonster(() => L2MonsterSprites.AddCopy(Rom, fromSprite), $"a copy of sprite {fromSprite:X2}");
    }

    // adds a monster sprite from a picture file (split off from the dialog so tests can call it). a palette picture
    // with up to 16 colours keeps its colour numbers and colours (colour 0 see-through), any other picture gets up to
    // 15 colours of its own (see-through pixels become colour 0). it moves in battle like the shown sprite
    public void AddMonsterSpriteFrom(string path)
    {
        if (Rom == null || _asset?.Kind != L2Gfx.Kind.MonsterSprite) return;
        byte[] pixels;
        int[] colours;
        int w, h;
        string how;
        var loaded = IndexedPng.TryLoad(path);
        if (loaded is { } png && png.Palette.Length <= 16 && png.Indices.All(i => i < 16))
        {
            (w, h, pixels) = (png.Width, png.Height, png.Indices);
            colours = Enumerable.Range(0, 16).Select(c => c < png.Palette.Length ? (int)L2Gfx.Bgr555(png.Palette[c]) : 0).ToArray();
            how = "colour numbers and colours kept";
        }
        else
        {
            BitmapSource source;
            try { source = new BitmapImage(new Uri(path)); }
            catch (Exception ex) { Status = "Can't read the picture: " + ex.Message; return; }
            (w, h) = (source.PixelWidth, source.PixelHeight);
            var bgra = new FormatConvertedBitmap(source, PixelFormats.Bgra32, null, 0);
            var argb = new uint[w * h];
            bgra.CopyPixels(argb, w * 4, 0);
            (pixels, colours) = L2MonsterSprites.Reduce(argb);
            how = "colours brought down to 15 and see-through";
        }
        if (L2MonsterSprites.SizeProblem(w, h) is { } why) { Status = "Not added: " + why; return; }
        int moves = L2MonsterSprites.Size(Rom, _asset.Id).Moves;
        AddMonster(() => L2MonsterSprites.AddPicture(Rom, w, h, pixels, colours, moves), $"{Path.GetFileName(path)}, {w} x {h}, {how}");
    }

    private void AddMonster(Func<int> add, string what)
    {
        if (Rom == null || Main == null) return;
        var before = Rom.ToArray();
        int id;
        try { id = add(); }
        catch (Exception ex) { if (!Rom.SameAs(before)) Rom.ReplaceAll(before); Status = "Not added: " + ex.Message; return; }
        bool grew = Rom.Length > before.Length;
        Main.NotifyRomModified($"Lufia II: new monster sprite {id:X2} ({what})");
        MarkRead();
        Refresh(() => IndexOfMonsterSprite(id), keep: false);
        Status = $"Monster sprite {id:X2} ({id}) made ({what}); paint it below, and give it to a monster below or with its Battle sprite field on the Monsters page." +
                 (grew ? " The ROM grew to make room." : "") + " Edit, Undo takes it back.";
        MonsterSpritesEdited();
    }

    // gives the sprite shown, with the palette shown, to the monster picked in "Give it to"
    [RelayCommand(CanExecute = nameof(CanAdd))]
    private void GiveToMonster()
    {
        if (Rom == null || Main == null || _asset?.Kind != L2Gfx.Kind.MonsterSprite || GiveTo < 0) return;
        var monster = L2Battle.ReadMonster(Rom, GiveTo);
        int id = _asset.Id, palette = PaletteChoice;
        if (monster.Sprite == id && monster.Palette == palette) { Status = $"{monster.Name} already shows sprite {id:X2} with palette {palette}."; return; }
        monster.Sprite = (byte)id; monster.Palette = (byte)palette;
        L2Battle.WriteMonster(Rom, monster);
        Main.NotifyRomModified($"Lufia II: monster {monster.Index:X2} {monster.Name} shows sprite {id:X2}, palette {palette}");
        MarkRead();
        int selected = SelectedIndex;
        Refresh(() => selected, keep: true);
        PaletteChoice = palette;
        Status = $"{monster.Index:X2} {monster.Name} now shows sprite {id:X2} with palette {palette}. Edit, Undo takes it back.";
        MonsterSpritesEdited();
    }

    // "Give it to" for tests
    public void GiveToMonsterNow(int monster) { GiveTo = monster; GiveToMonster(); }

    // PNG

    private string FileName() => string.Concat(("Lufia II " + _asset!.Group + " " + _asset.Name).Select(c => Path.GetInvalidFileNameChars().Contains(c) || c == ':' ? '_' : c)) + ".png";

    [RelayCommand]
    private void Export()
    {
        if (_pic == null || _asset == null) return;
        var dlg = new SaveFileDialog { Title = "Save the picture", Filter = "PNG picture (*.png)|*.png", FileName = FileName() };
        if (dlg.ShowDialog() != true) return;
        ExportTo(dlg.FileName);
    }

    // saves the picture shown (as painted) as a palette PNG (split off from the dialog so tests can call it)
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

    // writes a picture file into the ROM (split off from the dialog so tests can call it)
    public void ImportFrom(string path)
    {
        if (Rom == null || Main == null || _pic == null || _asset == null) return;
        int w = _pic.Width, h = _pic.Height;
        var loaded = IndexedPng.TryLoad(path);
        byte[] colourNumbers;
        uint[]? colours = null;
        string how;
        if (loaded is { } png)
        {
            if (png.Width != w || png.Height != h) { Status = $"The picture must be {w} x {h} pixels (as saved by Export PNG)."; return; }
            colourNumbers = png.Indices;
            if ((TakeColours || _asset.Kind == L2Gfx.Kind.Palette) && _pic.RomColours) colours = png.Palette;
            how = colours != null ? "colour numbers kept, colours taken from the picture" : "colour numbers kept";
        }
        else
        {
            BitmapSource source;
            try { source = new BitmapImage(new Uri(path)); }
            catch (Exception ex) { Status = "Can't read the picture: " + ex.Message; return; }
            if (source.PixelWidth != w || source.PixelHeight != h) { Status = $"The picture must be {w} x {h} pixels (as saved by Export PNG)."; return; }
            var bgra = new FormatConvertedBitmap(source, PixelFormats.Bgra32, null, 0);
            var pixels = new uint[w * h];
            bgra.CopyPixels(pixels, w * 4, 0);
            if (_asset.Kind == L2Gfx.Kind.Palette)
            {
                // a palette picture is just its squares, take the colour in the middle of each
                colourNumbers = _pic.Pixels;
                int cell = L2Gfx.PaletteCell;
                colours = Enumerable.Range(0, _pic.Colours.Length).Select(i => pixels[(i / 16 * cell + cell / 2) * w + i % 16 * cell + cell / 2]).ToArray();
                how = "colours read from the middle of each square";
            }
            else
            {
                colourNumbers = L2Gfx.MatchColours(pixels, _pic);
                how = "colours matched to the ones shown" + (_pic.Rows > 1 ? " (each 8x8 square to its best row)" : "");
            }
        }
        var before = Rom.ToArray();
        string what;
        try { what = L2Gfx.Import(Rom, _asset, PaletteChoice, colourNumbers, colours); }
        catch (Exception ex)
        {
            if (!Rom.SameAs(before)) Rom.ReplaceAll(before);
            Status = "Not imported: " + ex.Message;
            return;
        }
        if (Rom.SameAs(before)) { Status = $"Nothing changed: the picture is the same as the game's ({how})."; return; }
        bool grew = Rom.Length > before.Length;
        Main.NotifyRomModified($"Lufia II: graphics, {_asset.Name}");
        MarkRead();
        Draw();
        Status = $"Imported ({how}): {what}." + (grew ? " The ROM grew to 3 MB to make room." : "") +
                 (_asset.Kind == L2Gfx.Kind.BattleBackground && _asset.Note.Contains("Shares") ? " Backgrounds that share its layout or colours changed with it." : "") +
                 " Edit, Undo takes it back.";
        if (_asset.Kind == L2Gfx.Kind.MonsterSprite) MonsterSpritesEdited();
    }
}
