using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LufiaForge.Core;
using LufiaForge.Core.Lufia2;

namespace LufiaForge.Modules.Lufia2;

public enum L2Tool { Select, Paint, Pick, Area, Collision, AddExit, AddTrigger, AddCharacter, AddPoint, AddChest, AddHidden }

/// <summary>What can be picked on the map: its list in the info block and the record's index there.</summary>
public enum SelKind { Exit, Door, Trigger, Character, Point, Treasure }

public sealed record L2Selection(SelKind Kind, int Index);

public partial class L2MapsPage
{
    // ---------------------------------------------------------------- tools

    [ObservableProperty] private L2Tool _tool = L2Tool.Select;
    public bool IsSelectTool { get => Tool == L2Tool.Select; set { if (value) Tool = L2Tool.Select; } }
    public bool IsPaintTool { get => Tool == L2Tool.Paint; set { if (value) Tool = L2Tool.Paint; } }
    public bool IsPickTool { get => Tool == L2Tool.Pick; set { if (value) Tool = L2Tool.Pick; } }
    public bool IsAreaTool { get => Tool == L2Tool.Area; set { if (value) Tool = L2Tool.Area; } }
    public bool IsCollisionTool { get => Tool == L2Tool.Collision; set { if (value) Tool = L2Tool.Collision; } }
    public bool IsAddExitTool { get => Tool == L2Tool.AddExit; set { if (value) Tool = L2Tool.AddExit; } }
    public bool IsAddTriggerTool { get => Tool == L2Tool.AddTrigger; set { if (value) Tool = L2Tool.AddTrigger; } }
    public bool IsAddCharacterTool { get => Tool == L2Tool.AddCharacter; set { if (value) Tool = L2Tool.AddCharacter; } }
    public bool IsAddPointTool { get => Tool == L2Tool.AddPoint; set { if (value) Tool = L2Tool.AddPoint; } }
    public bool IsAddChestTool { get => Tool == L2Tool.AddChest; set { if (value) Tool = L2Tool.AddChest; } }
    public bool IsAddHiddenTool { get => Tool == L2Tool.AddHidden; set { if (value) Tool = L2Tool.AddHidden; } }

    partial void OnToolChanged(L2Tool value)
    {
        foreach (var n in new[] { nameof(IsSelectTool), nameof(IsPaintTool), nameof(IsPickTool), nameof(IsAreaTool), nameof(IsCollisionTool),
                                  nameof(IsAddExitTool), nameof(IsAddTriggerTool), nameof(IsAddCharacterTool), nameof(IsAddPointTool), nameof(IsAddChestTool), nameof(IsAddHiddenTool) })
            OnPropertyChanged(n);
        if (value != L2Tool.Area) AreaRect = null;
        StopStamp();
        BrushPreviewVisible = false;
        OnPropertyChanged(nameof(ToolHint));
    }

    public string ToolHint => _world != null ? WorldHint : Stamping ? "Click to place the copied blocks (both layers and their walls); right-click or Esc stops." : Tool switch
    {
        L2Tool.Select => "Select (S): click an exit, step-on area, character, preset point, chest or door to edit it; drag to move it (an area's or exit's bottom-right block resizes it). Click again to reach what lies under it. Double-click an exit to go there.",
        L2Tool.Paint => $"Paint (B): paints the brush on the {(EditUpper ? "upper" : "ground")} layer; drag to paint more. Right-click or Ctrl + click picks the block under the mouse.",
        L2Tool.Pick => "Pick (I): click the map to take its block (on the layer being painted) as the brush.",
        L2Tool.Area => "Area (M): drag a rectangle, then Copy (Ctrl+C), Cut (Ctrl+X), Fill or Delete (Del); Paste (Ctrl+V) places what was copied, from any map with the same graphics.",
        L2Tool.Collision => "Collision (L): paints where the party can't walk (and doorways and walls). Drag to paint; right-click clears a block. The red, blue and orange marks show it.",
        L2Tool.AddExit => "Add exit (E): drag a rectangle where the party leaves the map, then pick where it leads.",
        L2Tool.AddTrigger => "Add step-on area (T): drag a rectangle; walking onto it plays a new event (made when the map is written, then edit it in Events).",
        L2Tool.AddCharacter => "Add character (C): click where it stands, then pick its sprite and how it moves.",
        L2Tool.AddPoint => "Add preset point (A): click a block. Scenes put the party or characters on preset points (Go to another map, Put a character on a point).",
        L2Tool.AddChest => "Add chest (K): click a block; it gets the picture of a chest of this map and its own opened flag, so it gives its item once. Pick the item in the inspector.",
        L2Tool.AddHidden => "Add hidden item (H): click a block; nothing shows there, and pressing A facing it finds the item, once. Pick the item in the inspector.",
        _ => "",
    };

    private string WorldHint => Stamping ? "Click to place the copied big blocks; right-click or Esc stops."
        : ShowZones && HasZones ? "Battle zones: pick a zone in the list on the right (or right-click the map), then click or drag to paint it (2 x 2 big blocks each). Turn Battle zones off to paint blocks or add entrances."
        : Tool switch
        {
            L2Tool.Paint => "Paint (B): paints big blocks (32 x 32 pixels in the game); right-click or Ctrl + click picks one from the map.",
            L2Tool.Pick => "Pick (I): click the map to take its big block as the brush.",
            L2Tool.Area => "Area (M): drag a rectangle, then Copy, Cut, Fill; Paste places it (an island, a forest, a coast).",
            L2Tool.Select => "Select (S): click an entrance (green, named after where it leads) to edit it; drag to move it; double-click goes into that map.",
            L2Tool.AddExit => "Add entrance (E): drag over the blocks the party walks onto (a town's picture), then pick the map and the place inside it the party arrives at.",
            L2Tool.AddPoint => "Add preset point (A): a place on the world map that scenes can put the party on.",
            _ => "On the world map: Paint, Pick and Area work on its big blocks; Select and + Exit edit the entrances to towns and dungeons; Battle zones paints where battles happen.",
        };

    [ObservableProperty] private bool _editUpper;
    partial void OnEditUpperChanged(bool value) { OnPropertyChanged(nameof(ToolHint)); OnPropertyChanged(nameof(BrushText)); }

    // collision paint
    public static readonly string[] CollisionKinds = { "Can't walk here (obstacle)", "Doorway", "Wall on the top edge", "Wall on the left edge", "Upper level", "Room border" };
    private static readonly byte[] CollisionBits = { 0x80, 0x20, 0x04, 0x08, 0x40, 0x10 };
    public IReadOnlyList<string> CollisionChoices => CollisionKinds;
    [ObservableProperty] private int _collisionKind;

    // ---------------------------------------------------------------- mouse

    private bool _dragging;
    private int _dragStartX, _dragStartY, _lastX = -1, _lastY = -1;
    private byte[]? _dragOriginal;
    private bool _dragResize, _dragMoved;
    private (int X, int Y)? _areaStart;
    private string _strokeWhat = "";

    /// <summary>A press on the map at pixel (<paramref name="px"/>, <paramref name="py"/>) of the unzoomed map.</summary>
    public void MouseDown(double px, double py, bool right, bool ctrl, bool doubleClick)
    {
        if (_world != null)
        {
            if (!IsEditableMap) return;
            if (WorldBlockTool) { WorldDown(px, py, right, ctrl); return; }
            if (Tool is not (L2Tool.Select or L2Tool.AddExit or L2Tool.AddPoint))
            {
                Status = _world.Map == 0 ? "On the world map: Select, + Exit (entrances to towns and dungeons) and + Point work, and Paint, Pick, Area and Battle zones; treasure, characters and step-on areas go on town and dungeon maps."
                                         : "The sea floor: Paint, Pick and Area work on its big blocks.";
                return;
            }
        }
        if (_edit == null || !IsEditableMap) return;
        int cx = (int)Math.Floor(px / 16), cy = (int)Math.Floor(py / 16);
        if (cx < 0 || cy < 0 || cx >= _edit.Width || cy >= _edit.Height) return;
        _lastX = cx; _lastY = cy;
        if (Stamping)
        {
            if (right) { StopStamp(); return; }
            Stamp(cx, cy);
            return;
        }
        if (right && Tool is L2Tool.Paint or L2Tool.Pick or L2Tool.Area) { PickBlock(cx, cy); return; }
        if (ctrl && Tool == L2Tool.Paint) { PickBlock(cx, cy); return; }
        switch (Tool)
        {
            case L2Tool.Paint:
                Record("painting"); _strokeWhat = "painting";
                _dragging = true;
                PaintBrush(cx, cy);
                break;
            case L2Tool.Pick:
                PickBlock(cx, cy);
                break;
            case L2Tool.Collision:
                Record(right ? "clearing collision" : "painting collision");
                _dragging = true; _collisionClear = right;
                PaintCollision(cx, cy);
                break;
            case L2Tool.Area:
            case L2Tool.AddExit:
            case L2Tool.AddTrigger:
                _areaStart = (cx, cy);
                AreaRect = (cx, cy, cx + 1, cy + 1);
                _dragging = true;
                break;
            case L2Tool.AddCharacter:
                AddCharacter(cx, cy);
                break;
            case L2Tool.AddPoint:
                AddPoint(cx, cy);
                break;
            case L2Tool.AddChest:
            case L2Tool.AddHidden:
                AddTreasure(cx, cy, Tool == L2Tool.AddHidden);
                break;
            case L2Tool.Select:
                SelectAt(cx, cy, doubleClick);
                break;
        }
    }

    private bool _collisionClear;

    public void MouseMove(double px, double py)
    {
        if (_world != null) { WorldMove(px, py); if (!_dragging) return; }
        if (_edit == null) return;
        int cx = (int)Math.Floor(px / 16), cy = (int)Math.Floor(py / 16);
        bool inside = cx >= 0 && cy >= 0 && cx < _edit.Width && cy < _edit.Height;
        if (_world == null) UpdateHover(cx, cy, inside);
        if (!inside || !_dragging) return;
        if (cx == _lastX && cy == _lastY) return;
        _lastX = cx; _lastY = cy;
        switch (Tool)
        {
            case L2Tool.Paint: PaintBrush(cx, cy); break;
            case L2Tool.Collision: PaintCollision(cx, cy); break;
            case L2Tool.Area:
            case L2Tool.AddExit:
            case L2Tool.AddTrigger:
                if (_areaStart is { } s) AreaRect = (Math.Min(s.X, cx), Math.Min(s.Y, cy), Math.Max(s.X, cx) + 1, Math.Max(s.Y, cy) + 1);
                break;
            case L2Tool.Select: DragSelection(cx, cy); break;
        }
    }

    public void MouseUp()
    {
        if (_world != null) { WorldUp(); if (!_dragging) return; }
        if (!_dragging) return;
        _dragging = false;
        switch (Tool)
        {
            case L2Tool.Paint:
            case L2Tool.Collision:
                DropIfSame();
                break;
            case L2Tool.AddExit when AreaRect is { } r:
                AddExit(r.X1, r.Y1, r.X2, r.Y2); AreaRect = null; break;
            case L2Tool.AddTrigger when AreaRect is { } r:
                AddTrigger(r.X1, r.Y1, r.X2, r.Y2); AreaRect = null; break;
            case L2Tool.Select:
                if (_dragMoved) DropIfSame(); else if (_history.Count > 0 && _dragOriginal != null) DropIfSame();
                _dragOriginal = null;
                break;
        }
        _areaStart = null;
        UpdateUndoState();
    }

    private void EndDrag() { if (_dragging) MouseUp(); }

    public void MouseLeave() { HoverText = ""; BrushPreviewVisible = false; CameraVisible = false; }

    // ---------------------------------------------------------------- hover

    private void UpdateHover(int cx, int cy, bool inside)
    {
        if (_edit == null || !inside) { HoverText = ""; BrushPreviewVisible = false; CameraVisible = false; return; }
        var g = _edit.Map.Layers[0]; var u = _edit.Map.Layers.Length > 1 ? _edit.Map.Layers[1] : null;
        int i = cy * _edit.Width + cx;
        string text = $"({cx},{cy})  ground {g.Blocks[i]:X3}" + (u != null ? $", upper {u.Blocks[i]:X3}" : "");
        byte f = u?.Flags[i] ?? 0;
        var walls = new List<string>();
        if ((f & 0x80) != 0) walls.Add("can't walk"); if ((f & 0x20) != 0) walls.Add("doorway"); if ((f & 0x04) != 0) walls.Add("wall on top");
        if ((f & 0x08) != 0) walls.Add("wall on the left"); if ((f & 0x40) != 0) walls.Add("upper level"); if ((f & 0x10) != 0) walls.Add("room border");
        if (walls.Count > 0) text += "; " + string.Join(", ", walls);
        foreach (var (kind, idx) in HitsAt(cx, cy)) text += "; " + Describe(kind, idx);
        HoverText = text;
        if (Tool == L2Tool.Paint && !Stamping)
        {
            BrushPreviewX = cx * 16; BrushPreviewY = cy * 16;
            BrushPreviewW = BrushW * 16; BrushPreviewH = BrushH * 16;
            BrushPreviewVisible = BrushPreviewImage != null;
        }
        else BrushPreviewVisible = false;
        if (Stamping) { StampX = cx * 16; StampY = cy * 16; }
        if (ShowCamera)
        {
            int mw = _edit.Width * 16, mh = _edit.Height * 16;
            CameraX = Math.Clamp(cx * 16 + 8 - 128, 0, Math.Max(0, mw - 256));
            CameraY = Math.Clamp(cy * 16 + 8 - 112, 0, Math.Max(0, mh - 224));
            var outer = new RectangleGeometry(new Rect(0, 0, mw, mh));
            var inner = new RectangleGeometry(new Rect(CameraX, CameraY, 256, 224));
            CameraShade = new CombinedGeometry(GeometryCombineMode.Exclude, outer, inner);
            CameraText = $"The screen with the party at ({cx},{cy})";
            CameraVisible = true;
        }
        else CameraVisible = false;
    }

    [ObservableProperty] private double _brushPreviewX, _brushPreviewY, _brushPreviewW = 16, _brushPreviewH = 16;
    [ObservableProperty] private bool _brushPreviewVisible;
    [ObservableProperty] private double _cameraX, _cameraY;
    [ObservableProperty] private Geometry? _cameraShade;
    [ObservableProperty] private string _cameraText = "";
    [ObservableProperty] private bool _cameraVisible;
    partial void OnShowCameraChanged(bool value) { if (!value) CameraVisible = false; }
    partial void OnShowGroundChanged(bool value) => Refresh();
    partial void OnShowUpperChanged(bool value) => Refresh();
    partial void OnShowCollisionChanged(bool value) => Refresh();
    partial void OnShowCharactersChanged(bool value) => Refresh();
    partial void OnShowExitsChanged(bool value) => DrawOverlays();
    partial void OnShowDoorsChanged(bool value) => DrawOverlays();
    partial void OnShowTreasureChanged(bool value) => DrawOverlays();
    partial void OnShowTriggersChanged(bool value) => DrawOverlays();
    partial void OnShowPointsChanged(bool value) => DrawOverlays();

    // ---------------------------------------------------------------- painting

    private void PaintBrush(int cx, int cy)
    {
        if (_edit == null) return;
        int layer = EditUpper ? 1 : 0;
        if (layer >= _edit.Map.Layers.Length) return;
        var L = _edit.Map.Layers[layer];
        bool any = false;
        for (int by = 0; by < BrushH; by++)
            for (int bx = 0; bx < BrushW; bx++)
            {
                int x = cx + bx, y = cy + by;
                if (x >= L.Width || y >= L.Height) continue;
                int b = _brush[by * BrushW + bx];
                if (L.Blocks[y * L.Width + x] == b) continue;
                L.Blocks[y * L.Width + x] = b; any = true;
            }
        if (any) { RememberBrush(); DrawMap(); UpdateUndoState(); }
    }

    private void PaintCollision(int cx, int cy)
    {
        if (_edit == null || _edit.Map.Layers.Length < 2) return;
        var L = _edit.Map.Layers[1];
        int i = cy * L.Width + cx;
        byte old = L.Flags[i];
        byte v = _collisionClear ? (byte)(old & 0x03) : (byte)(old | CollisionBits[Math.Clamp(CollisionKind, 0, CollisionBits.Length - 1)]);
        if (v == old) return;
        L.Flags[i] = v;
        if (!ShowCollision) ShowCollision = true; else DrawMap();
        UpdateUndoState();
    }

    private void PickBlock(int cx, int cy)
    {
        if (_edit == null) return;
        int layer = EditUpper ? 1 : 0;
        var L = _edit.Map.Layers[Math.Min(layer, _edit.Map.Layers.Length - 1)];
        SetBrush(L.Blocks[cy * L.Width + cx]);
        Status = $"Block {SelectedBlock:X3} picked from ({cx},{cy}).";
    }

    // ---------------------------------------------------------------- areas, copy and paste

    /// <summary>The area being dragged or picked (x1, y1, x2, y2; x2 / y2 not included), in blocks.</summary>
    public (int X1, int Y1, int X2, int Y2)? AreaRect
    {
        get => _areaRect;
        set
        {
            _areaRect = value;
            AreaVisible = value != null;
            int u = AreaUnit;
            if (value is { } r) { AreaX = r.X1 * u; AreaY = r.Y1 * u; AreaW = (r.X2 - r.X1) * u; AreaH = (r.Y2 - r.Y1) * u; }
            OnPropertyChanged();
        }
    }
    private (int X1, int Y1, int X2, int Y2)? _areaRect;
    /// <summary>Pixels per unit of the area: big blocks for the world map's Area tool, else 16-pixel blocks.</summary>
    private int AreaUnit => _world != null && Tool == L2Tool.Area ? Cell : 16;
    [ObservableProperty] private double _areaX, _areaY, _areaW, _areaH;
    [ObservableProperty] private bool _areaVisible;

    /// <summary>Blocks copied (both layers, with their cell flags), shared by every map; the graphics they came from.</summary>
    public sealed record Clip(int W, int H, int[] Ground, int[] Upper, byte[] Flags, int BlockSet, int TileSet);
    private static Clip? _clip;

    [RelayCommand]
    private void CopyArea()
    {
        if (_world != null && AreaRect is { } wr) { _clip = WorldCut(wr); Status = $"Copied {wr.X2 - wr.X1} x {wr.Y2 - wr.Y1} big blocks."; return; }
        if (_edit == null || AreaRect is not { } r) { Status = "Drag an area with the Area tool first."; return; }
        _clip = Cut(r);
        Status = $"Copied {r.X2 - r.X1} x {r.Y2 - r.Y1} blocks. Paste (Ctrl+V) places them, on this map or another with the same graphics.";
    }

    private Clip Cut((int X1, int Y1, int X2, int Y2) r)
    {
        int w = r.X2 - r.X1, h = r.Y2 - r.Y1;
        var g = new int[w * h]; var u = new int[w * h]; var f = new byte[w * h];
        var L0 = _edit!.Map.Layers[0]; var L1 = _edit.Map.Layers.Length > 1 ? _edit.Map.Layers[1] : null;
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
            {
                int i = (r.Y1 + y) * L0.Width + r.X1 + x;
                g[y * w + x] = L0.Blocks[i];
                if (L1 != null) { u[y * w + x] = L1.Blocks[i]; f[y * w + x] = L1.Flags[i]; }
            }
        return new Clip(w, h, g, u, f, _edit.BlockSetIndex, _edit.TileSetIndex);
    }

    [RelayCommand]
    private void CutArea()
    {
        if (_world != null && AreaRect is { } wr) { _clip = WorldCut(wr); WorldRecord("cutting an area"); WorldFill(wr); return; }
        if (_edit == null || AreaRect is not { } r) { Status = "Drag an area with the Area tool first."; return; }
        _clip = Cut(r);
        Record("cutting an area");
        FillRect(r, SelectedBlock);
        Status = "Cut: copied, then filled with the brush block.";
    }

    [RelayCommand]
    private void FillArea()
    {
        if (_world != null && AreaRect is { } wr) { WorldRecord("filling an area"); WorldFill(wr); return; }
        if (_edit == null || AreaRect is not { } r) { Status = "Drag an area with the Area tool first."; return; }
        Record("filling an area");
        FillRect(r, SelectedBlock);
        Status = $"Filled with block {SelectedBlock:X3} on the {(EditUpper ? "upper" : "ground")} layer.";
    }

    [RelayCommand]
    private void WorldFill((int X1, int Y1, int X2, int Y2) r)
    {
        for (int y = r.Y1; y < r.Y2; y++) for (int x = r.X1; x < r.X2; x++) _world!.Cells[y * L2WorldMap.Size + x] = SelectedBlock;
        DrawWorld(); WorldUndoState();
    }

    private void DeleteArea()
    {
        if (_world != null) return;
        if (_edit == null || AreaRect is not { } r) return;
        Record("clearing an area");
        FillRect(r, 0);
        Status = $"Cleared the {(EditUpper ? "upper" : "ground")} layer there (block 000 draws nothing).";
    }

    private void FillRect((int X1, int Y1, int X2, int Y2) r, int block)
    {
        int layer = EditUpper ? 1 : 0;
        var L = _edit!.Map.Layers[Math.Min(layer, _edit.Map.Layers.Length - 1)];
        for (int y = r.Y1; y < r.Y2; y++) for (int x = r.X1; x < r.X2; x++) L.Blocks[y * L.Width + x] = block;
        DropIfSame();
        DrawMap(); UpdateUndoState();
    }

    // stamping what was copied
    [ObservableProperty] private bool _stamping;
    [ObservableProperty] private BitmapSource? _stampImage;
    [ObservableProperty] private double _stampX, _stampY;
    partial void OnStampingChanged(bool value) => OnPropertyChanged(nameof(ToolHint));

    [RelayCommand]
    private void Paste()
    {
        if (_world != null)
        {
            if (_clip == null || _clip.BlockSet != -1 - _world.Map) { Status = "Copy an area of this map first (big blocks can't go between the world map, the sea floor and towns)."; return; }
            StartStamp(_clip); return;
        }
        if (_edit == null || _clip == null) { Status = "Nothing copied yet: drag an area with the Area tool and Copy it (or use Pieces...)."; return; }
        StartStamp(_clip);
    }

    /// <summary>Starts placing <paramref name="clip"/> (from Copy, Pieces... or a preset).</summary>
    public void StartStamp(Clip clip)
    {
        if (_world != null)
        {
            _clip = clip;
            int ww = clip.W * Cell, wh = clip.H * Cell;
            var wpx = new uint[ww * wh];
            for (int y = 0; y < clip.H; y++)
                for (int x = 0; x < clip.W; x++)
                {
                    var bp = Big(clip.Ground[y * clip.W + x]);
                    for (int yy = 0; yy < Cell; yy++) Array.Copy(bp, yy * Cell, wpx, (y * Cell + yy) * ww + x * Cell, Cell);
                }
            StampImage = BitmapSource.Create(ww, wh, 96, 96, PixelFormats.Bgra32, null, wpx.Select(c => (int)c).ToArray(), ww * 4);
            Stamping = true;
            return;
        }
        if (_edit == null) return;
        _clip = clip;
        var px = new uint[clip.W * 16 * clip.H * 16];
        int w = clip.W * 16;
        for (int y = 0; y < clip.H; y++)
            for (int x = 0; x < clip.W; x++)
                foreach (int b in new[] { clip.Ground[y * clip.W + x], clip.Upper[y * clip.W + x] })
                {
                    if (b <= 0 || b >= _blocks.Length) continue;
                    var bp = _blocks[b];
                    for (int yy = 0; yy < 16; yy++) for (int xx = 0; xx < 16; xx++) if (bp[yy * 16 + xx] != 0) px[(y * 16 + yy) * w + x * 16 + xx] = bp[yy * 16 + xx];
                }
        StampImage = BitmapSource.Create(w, clip.H * 16, 96, 96, PixelFormats.Bgra32, null, px.Select(c => (int)c).ToArray(), w * 4);
        Stamping = true;
        Status = clip.BlockSet != _edit.BlockSetIndex || clip.TileSet != _edit.TileSetIndex
            ? $"These blocks come from block set {clip.BlockSet} / tile set {clip.TileSet}; this map uses {_edit.BlockSetIndex} / {_edit.TileSetIndex}, so they may look different here. Click to place them; Esc stops."
            : "Click to place the copied blocks; right-click or Esc stops.";
        OnPropertyChanged(nameof(ToolHint));
    }

    public void StopStamp() { Stamping = false; StampImage = null; OnPropertyChanged(nameof(ToolHint)); }

    private void Stamp(int cx, int cy)
    {
        if (_edit == null || _clip is not { } c) return;
        Record("placing copied blocks");
        var L0 = _edit.Map.Layers[0]; var L1 = _edit.Map.Layers.Length > 1 ? _edit.Map.Layers[1] : null;
        for (int y = 0; y < c.H; y++)
            for (int x = 0; x < c.W; x++)
            {
                int mx = cx + x, my = cy + y;
                if (mx >= L0.Width || my >= L0.Height) continue;
                int i = my * L0.Width + mx;
                L0.Blocks[i] = c.Ground[y * c.W + x];
                if (L1 != null) { L1.Blocks[i] = c.Upper[y * c.W + x]; L1.Flags[i] = c.Flags[y * c.W + x]; }
            }
        DropIfSame();
        DrawMap(); UpdateUndoState();
    }

    /// <summary>Esc: stops placing, drops the area, the picked thing.</summary>
    public void Escape()
    {
        if (Stamping) { StopStamp(); return; }
        if (AreaRect != null) { AreaRect = null; return; }
        Selection = null; DrawOverlays(); UpdateInspector();
    }

    // ---------------------------------------------------------------- adding things

    private void AddExit(int x1, int y1, int x2, int y2)
    {
        if (_edit == null) return;
        var exits = _edit.Records(L2MapEdit.Exits);
        int id = exits.Select(e => (int)e.Bytes[0]).DefaultIfEmpty(-1).Max() + 1;
        // the arrival style most exits of the map use (F3: the usual)
        byte style = exits.GroupBy(e => e.Bytes[5]).OrderByDescending(g => g.Count()).Select(g => g.Key).DefaultIfEmpty(_edit.IsWorld ? (byte)0x40 : (byte)0xF3).First();
        if (id > 0xFE) { Status = "The map has no free exit number."; return; }
        Record(_edit.IsWorld ? "adding an entrance" : "adding an exit");
        try { _edit.AddRecord(L2MapEdit.Exits, new byte[] { (byte)id, (byte)x1, (byte)y1, (byte)x2, (byte)y2, style, (byte)x1, (byte)y1, (byte)_edit.MapId }); }
        catch (Exception ex) { Status = ex.Message; UndoPaint(); return; }
        Selection = new L2Selection(SelKind.Exit, _edit.Records(L2MapEdit.Exits).Count - 1);
        Tool = L2Tool.Select;
        Refresh();
        Status = _edit.IsWorld
            ? $"Entrance {id} added. Now click \"Pick on map...\" in the inspector: pick the town or dungeon and click where the party arrives in it."
            : $"Exit {id} added: it leads back here for now. Click \"Pick on map...\" in the inspector to pick the map and place it leads to.";
    }

    private void AddTrigger(int x1, int y1, int x2, int y2)
    {
        if (_edit == null || _rom == null) return;
        if (L2Events.Block(_rom, _edit.MapId) < 0) { Status = "This map has no event block, so it can't have step-on areas."; return; }
        int id = _edit.NextTriggerId(_rom);
        if (id > 0xFE) { Status = "The map has no free area number."; return; }
        Record("adding a step-on area");
        try { _edit.AddRecord(L2MapEdit.Triggers, new byte[] { (byte)id, (byte)x1, (byte)y1, (byte)x2, (byte)y2 }); }
        catch (Exception ex) { Status = ex.Message; UndoPaint(); return; }
        Selection = new L2Selection(SelKind.Trigger, _edit.Records(L2MapEdit.Triggers).Count - 1);
        Tool = L2Tool.Select;
        Refresh();
        Status = $"Step-on area {id} added. Its event is made when the map is written; \"Event...\" writes it and opens it in Events.";
    }

    private void AddCharacter(int x, int y)
    {
        if (_edit == null || _rom == null) return;
        int id = _edit.NextCharacterId(_rom);
        if (id > 0x6F) { Status = "The map has 32 characters already."; return; }
        Record("adding a character");
        try
        {
            _edit.AddRecord(L2MapEdit.Characters, new byte[] { (byte)(id - 0x4F), (byte)x, (byte)y, (byte)Math.Max(0, x - 2), (byte)Math.Max(0, y - 2),
                (byte)Math.Min(_edit.Width, x + 3), (byte)Math.Min(_edit.Height, y + 3), 0x05 });
        }
        catch (Exception ex) { Status = ex.Message; UndoPaint(); return; }
        _edit.Sprites[id] = 0x0C;
        Selection = new L2Selection(SelKind.Character, _edit.Records(L2MapEdit.Characters).Count - 1);
        Tool = L2Tool.Select;
        Refresh();
        Status = $"Character {id - 0x4F} added (sprite 0C, standing facing down). Pick its sprite and how it moves; \"Talk event...\" gives it something to say.";
    }

    private void AddPoint(int x, int y)
    {
        if (_edit == null) return;
        int id = _edit.NextPointId();
        if (id > 0xFE) return;
        Record("adding a preset point");
        try { _edit.AddRecord(L2MapEdit.Points, new byte[] { (byte)id, (byte)x, (byte)y }); }
        catch (Exception ex) { Status = ex.Message; UndoPaint(); return; }
        ShowPoints = true;
        Selection = new L2Selection(SelKind.Point, _edit.Records(L2MapEdit.Points).Count - 1);
        Tool = L2Tool.Select;
        Refresh();
        Status = $"Preset point {id} added at ({x},{y}).";
    }

    private void AddTreasure(int x, int y, bool hidden)
    {
        if (_edit == null || _rom == null) return;
        if (TableGuard.LockReason(_rom, "Treasure") is { } locked) { Status = locked; return; }
        Record(hidden ? "adding a hidden item" : "adding a chest");
        int spot;
        try { spot = _edit.AddTreasure(_rom, x, y, hidden, 0x00); }
        catch (Exception ex) { Status = ex.Message; UndoPaint(); _redo.Clear(); _worldRedo.Clear(); UpdateUndoState(); return; }
        ShowTreasure = true;
        Selection = new L2Selection(SelKind.Treasure, _edit.Records(L2MapEdit.Treasure).Count - 1);
        Tool = L2Tool.Select;
        Refresh();
        Status = (hidden ? $"Hidden item {spot} added at ({x},{y}): pressing A facing that block finds it, once." : $"Chest {spot} added at ({x},{y}), with the picture of this map's chests; it opens once.")
                 + " Pick what it holds in the inspector.";
    }

    // ---------------------------------------------------------------- keys

    /// <summary>A key on the map (tool letters, Del, Esc, Ctrl+C / X / V). True when used.</summary>
    public bool Key(System.Windows.Input.Key key, bool ctrl)
    {
        if (ctrl)
        {
            switch (key)
            {
                case System.Windows.Input.Key.C: CopyArea(); return true;
                case System.Windows.Input.Key.X: CutArea(); return true;
                case System.Windows.Input.Key.V: Paste(); return true;
            }
            return false;
        }
        switch (key)
        {
            case System.Windows.Input.Key.S: case System.Windows.Input.Key.V: Tool = L2Tool.Select; return true;
            case System.Windows.Input.Key.B: case System.Windows.Input.Key.P: Tool = L2Tool.Paint; return true;
            case System.Windows.Input.Key.I: Tool = L2Tool.Pick; return true;
            case System.Windows.Input.Key.M: Tool = L2Tool.Area; return true;
            case System.Windows.Input.Key.L: Tool = L2Tool.Collision; return true;
            case System.Windows.Input.Key.E: Tool = L2Tool.AddExit; return true;
            case System.Windows.Input.Key.T: Tool = L2Tool.AddTrigger; return true;
            case System.Windows.Input.Key.C: Tool = L2Tool.AddCharacter; return true;
            case System.Windows.Input.Key.A: Tool = L2Tool.AddPoint; return true;
            case System.Windows.Input.Key.K: Tool = L2Tool.AddChest; return true;
            case System.Windows.Input.Key.H: Tool = L2Tool.AddHidden; return true;
            case System.Windows.Input.Key.Escape: Escape(); return true;
            case System.Windows.Input.Key.Delete:
                if (Tool == L2Tool.Area && AreaRect != null) DeleteArea(); else DeleteSelected();
                return true;
        }
        return false;
    }
}
