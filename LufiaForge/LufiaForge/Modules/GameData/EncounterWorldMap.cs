using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LufiaForge.Core;
using LufiaForge.Core.Battle;
using LufiaForge.Core.Maps;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace LufiaForge.Modules.GameData;

/// <summary>
/// The world map's encounter zones (resource B0: 80 x 64 cells, one per 4x4 tiles, each a group number; 0 = no
/// battles, the game skips the encounter when the group is 0). Shown over the world map, picked, painted and filled.
/// </summary>
public partial class EncounterEditorViewModel
{
    /// <summary>Pixels per world map tile in <see cref="WorldImage"/> (16 would be full size).</summary>
    public const int TileScale = 4;
    public const int CellPixels = 4 * TileScale;
    public double WorldPixelWidth => Encounters.ZoneWidth * CellPixels;
    public double WorldPixelHeight => Encounters.ZoneHeight * CellPixels;

    private byte[] _zones = Array.Empty<byte>();
    [ObservableProperty] private ImageSource? _worldImage;
    [ObservableProperty] private WriteableBitmap? _zoneImage;
    [ObservableProperty] private bool _zonesDirty;
    [ObservableProperty] private bool _showAllZones = true;
    /// <summary>0 = pick (click a zone to select its group), 1 = paint, 2 = fill an area.</summary>
    [ObservableProperty] private int _toolIndex;
    [ObservableProperty] private int _brushSize = 1;
    /// <summary>Paint "no battles" (group 0) instead of the selected group.</summary>
    [ObservableProperty] private bool _eraseZones;
    [ObservableProperty] private string _hoverText = "";
    [ObservableProperty] private string _worldStatus = "";

    public string[] Tools { get; } = { "Pick (click a zone to open its group)", "Paint with the selected group", "Fill an area with the selected group" };
    public int[] BrushSizes { get; } = { 1, 2, 3, 4, 6 };

    partial void OnShowAllZonesChanged(bool value) => DrawZones();

    private void StartWorldMap()
    {
        DrawZones();
        if (Ctx == null) return;
        var rom = Ctx.Rom;
        WorldStatus = "Drawing the world map...";
        Task.Run(() =>
        {
            var map = LufiaMap.Load(rom, LufiaMap.WorldMap);
            var px = MapTileset.ForMap(rom, map).RenderMap(map);
            int w = map.Width * 16, h = map.Height * 16, ow = w / 4, oh = h / 4;
            var small = new uint[ow * oh];
            for (int y = 0; y < oh; y++)
                for (int x = 0; x < ow; x++)
                {
                    uint r = 0, g = 0, b = 0;
                    for (int dy = 0; dy < 4; dy++)
                        for (int dx = 0; dx < 4; dx++)
                        {
                            uint c = px[(y * 4 + dy) * w + x * 4 + dx];
                            r += c >> 16 & 255; g += c >> 8 & 255; b += c & 255;
                        }
                    small[y * ow + x] = 0xFF000000u | (r / 16) << 16 | (g / 16) << 8 | b / 16;
                }
            return (small, ow, oh);
        }).ContinueWith(t =>
        {
            if (t.IsFaulted) { WorldStatus = "The world map could not be drawn: " + t.Exception?.GetBaseException().Message; return; }
            var (small, ow, oh) = t.Result;
            var bmp = BitmapSource.Create(ow, oh, 96, 96, PixelFormats.Bgra32, null, small, ow * 4);
            bmp.Freeze();
            WorldImage = bmp;
            WorldStatus = "";
        }, TaskScheduler.FromCurrentSynchronizationContext());
    }

    private static uint Hue(int g, uint alpha)
    {
        double h = (g * 47 % 360) / 60.0, x = 1 - Math.Abs(h % 2 - 1);
        (double r, double gg, double b) = h switch
        {
            < 1 => (1.0, x, 0.0), < 2 => (x, 1.0, 0.0), < 3 => (0.0, 1.0, x),
            < 4 => (0.0, x, 1.0), < 5 => (x, 0.0, 1.0), _ => (1.0, 0.0, x),
        };
        return alpha << 24 | (uint)(r * 255) << 16 | (uint)(gg * 255) << 8 | (uint)(b * 255);
    }

    private void DrawZones()
    {
        int w = Encounters.ZoneWidth, h = Encounters.ZoneHeight;
        if (_zones.Length < w * h) { ZoneImage = null; return; }
        int sel = SelectedGroup;
        var px = new uint[w * h];
        for (int i = 0; i < px.Length; i++)
        {
            int g = _zones[i];
            px[i] = g == 0 ? 0u : g == sel ? 0xA8FFE040u : ShowAllZones ? Hue(g, 0x38) : 0u;
        }
        var bmp = new WriteableBitmap(w, h, 96, 96, PixelFormats.Bgra32, null);
        bmp.WritePixels(new Int32Rect(0, 0, w, h), px, w * 4, 0);
        ZoneImage = bmp;
    }

    private bool InGrid(int cx, int cy) => cx >= 0 && cy >= 0 && cx < Encounters.ZoneWidth && cy < Encounters.ZoneHeight && _zones.Length >= Encounters.ZoneWidth * Encounters.ZoneHeight;

    /// <summary>Mouse over cell (cx, cy).</summary>
    public void HoverCell(int cx, int cy)
    {
        if (!InGrid(cx, cy)) { HoverText = ""; return; }
        int g = _zones[cy * Encounters.ZoneWidth + cx];
        HoverText = $"Zone {cx},{cy} (tiles {cx * 4}-{cx * 4 + 3}, {cy * 4}-{cy * 4 + 3}): " + (g == 0 ? "no battles" : $"group {g:X2}" + (g <= Count ? $" - {GroupMonsterSummary(g)}" : " (no such group!)"));
    }

    private string GroupMonsterSummary(int g) =>
        string.Join(", ", _groups[g - 1].Distinct().SelectMany(s => Monsters(s)).Where(m => m != 0xFF).Select(MonsterName).Distinct().Take(6));

    /// <summary>A click (or drag, for painting) on cell (cx, cy).</summary>
    public void UseTool(int cx, int cy, bool dragging)
    {
        if (!InGrid(cx, cy)) return;
        int i = cy * Encounters.ZoneWidth + cx;
        if (ToolIndex == 0)
        {
            if (dragging) return;
            int g = _zones[i];
            if (g == 0) { Status = "No battles in this zone. Pick a group in the list and paint to add some."; return; }
            if (g <= Count) SelectedIndex = g - 1;
            return;
        }
        int brush = EraseZones ? 0 : SelectedGroup;
        if (brush == 0 && !EraseZones) { Status = "Select a battle group in the list first (boss formations can't be painted)."; return; }
        bool changed = false;
        if (ToolIndex == 1)
        {
            int r0 = (BrushSize - 1) / 2;
            for (int y = cy - r0; y < cy - r0 + BrushSize; y++)
                for (int x = cx - r0; x < cx - r0 + BrushSize; x++)
                    if (InGrid(x, y) && _zones[y * Encounters.ZoneWidth + x] != brush) { _zones[y * Encounters.ZoneWidth + x] = (byte)brush; changed = true; }
        }
        else if (!dragging)
        {
            // fill the connected area of the same group
            int from = _zones[i];
            if (from == brush) return;
            var todo = new Stack<(int, int)>(); todo.Push((cx, cy));
            while (todo.Count > 0)
            {
                var (x, y) = todo.Pop();
                if (!InGrid(x, y) || _zones[y * Encounters.ZoneWidth + x] != from) continue;
                _zones[y * Encounters.ZoneWidth + x] = (byte)brush; changed = true;
                todo.Push((x + 1, y)); todo.Push((x - 1, y)); todo.Push((x, y + 1)); todo.Push((x, y - 1));
            }
        }
        if (changed) { ZonesDirty = true; DrawZones(); HoverCell(cx, cy); }
    }

    [RelayCommand]
    private void WriteZones()
    {
        if (Ctx == null || _zones.Length == 0) return;
        var rom = Ctx.Rom;
        int size = LufiaCompression.Compress(_zones).Length;
        LufiaCompression.Decompress(rom, LufiaCompression.ResourceOffset(rom, Encounters.ZoneResource), out int slot);
        if (size > slot && rom.Length < MapWriter.ExpandedSize &&
            MessageBox.Show("The edited zones don't fit in their original space. Expand the ROM to 2 MB?", "Expand ROM?", MessageBoxButton.YesNo) != MessageBoxResult.Yes)
        { Status = "Zones not written: the ROM was not expanded."; return; }
        var zones = _zones.ToArray();
        bool ok = false;
        Commit("World map battle zones", () => { Encounters.WriteZones(rom, zones, allowExpand: true); ok = true; });
        if (!ok) return;
        ZonesDirty = false;
        int keep = SelectedIndex;
        BuildEntries();
        SelectedIndex = -1; SelectedIndex = keep;
    }

    [RelayCommand]
    private void RevertZones()
    {
        if (Ctx == null) return;
        try { _zones = Encounters.ReadZones(Ctx.Rom); } catch { _zones = Array.Empty<byte>(); }
        ZonesDirty = false;
        DrawZones();
        Status = "Zones reverted to the ROM.";
    }
}
