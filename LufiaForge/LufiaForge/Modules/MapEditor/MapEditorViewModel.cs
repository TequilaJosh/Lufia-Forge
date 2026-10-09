using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LufiaForge.Core;
using LufiaForge.Core.Maps;
using LufiaForge.Modules.TextEditor;
using LufiaForge.ViewModels;
using System.Collections.ObjectModel;
using System.Text;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Brush = System.Windows.Media.Brush;
using Brushes = System.Windows.Media.Brushes;
using Color = System.Windows.Media.Color;

namespace LufiaForge.Modules.MapEditor;

public enum MapTool { Select, Paint, Pick, AddExit, AddArrival }

/// <summary>A rectangle drawn over the map (NPC, exit, arrival point, selection).</summary>
public sealed class MapOverlay
{
    public double X { get; init; }
    public double Y { get; init; }
    public double W { get; init; }
    public double H { get; init; }
    public Brush Stroke { get; init; } = Brushes.Yellow;
    public double Thickness { get; init; } = 2;
    public string Label { get; init; } = "";
    public string Kind { get; init; } = "";
    /// <summary>Draw a line from (X, Y) by (LineDx, LineDy) instead of a box (exit → arrival link).</summary>
    public bool IsLine { get; init; }
    public bool IsBox => !IsLine;
    public double LineDx { get; init; }
    public double LineDy { get; init; }
    public int Index { get; init; }
}

/// <summary>One setup-script assignment of a treasure spot, editable from the inspector.</summary>
public partial class SpotEditVm : ObservableObject
{
    private readonly Action<SpotEditVm> _write;
    private bool _loading;

    public SpotContent Content { get; private set; }
    public string Label { get; }
    public bool CanBeGold { get; }

    [ObservableProperty] private int _item;
    [ObservableProperty] private bool _isGold;
    [ObservableProperty] private int _gold;

    public bool IsItem => !IsGold;

    public SpotEditVm(SpotContent c, string label, Action<SpotEditVm> write)
    {
        Content = c; Label = label; _write = write;
        CanBeGold = c.Kind is SpotKind.Chest or SpotKind.Gold or SpotKind.EmptyChest;
        _loading = true;
        IsGold = c.Kind == SpotKind.Gold;
        Item = IsGold ? 0 : c.Low;
        Gold = IsGold ? c.Value : 100;
        _loading = false;
    }

    /// <summary>Value word to store: item (FC/FD high byte) or a gold amount (high byte below FC).</summary>
    public int NewValue => IsGold
        ? Math.Clamp(Gold, 0, 0xFBFF)
        : (Item & 0xFF) | ((Content.Kind == SpotKind.Hidden ? 0xFD : 0xFC) << 8);

    public void Refresh(SpotContent c) => Content = c;

    partial void OnItemChanged(int value) { if (!_loading) _write(this); }
    partial void OnGoldChanged(int value) { if (!_loading && IsGold) _write(this); }
    partial void OnIsGoldChanged(bool value)
    {
        OnPropertyChanged(nameof(IsItem));
        if (!_loading) _write(this);
    }
}

public partial class MapEditorViewModel : ObservableObject, IEventHost
{
    private RomBuffer? _rom;
    public RomBuffer? Rom => _rom;
    private MainViewModel? _mainVm;
    private LufiaMap? _map;
    private MapTileset? _tileset;
    private uint[]? _pixels;
    private readonly Stack<List<(int x, int y, ushort before)>> _undo = new();
    private List<(int x, int y, ushort before)>? _stroke;
    private (string kind, int index)? _selection;
    private (int x, int y)? _dragStart;
    private bool _populating;   // true while the inspector fields are filled from a selection
    private MapLinks? _links;
    private List<SpotContent> _spotContents = new();
    private bool _dragMoved;
    private string? _dragMode;                    // "npc", "exit-move", "exit-resize", "arrival", "new-exit"
    private (int x, int y) _dragAnchor;
    private readonly Dictionary<int, List<MapArrival>> _arrivalCache = new();
    private readonly List<int> _destIds = new();   // map ids behind DestinationChoices (0 = this map)

    /// <summary>Asks the view to open the destination picker for the selected exit.</summary>
    public event Action<MapExit>? DestinationPickerRequested;

    public ObservableCollection<string> DestinationChoices { get; } = new();
    public ObservableCollection<string> ArrivalChoices { get; } = new();
    [ObservableProperty] private int _destinationIndex = -1;
    [ObservableProperty] private int _arrivalIndex = -1;
    [ObservableProperty] private int _exitX;
    [ObservableProperty] private int _exitY;
    [ObservableProperty] private int _exitW;
    [ObservableProperty] private int _exitH;
    [ObservableProperty] private bool _isArrivalSelected;
    [ObservableProperty] private int _arrivalX;
    [ObservableProperty] private int _arrivalY;
    [ObservableProperty] private bool _canDeleteArrival;
    private int _focusCycle;

    /// <summary>Asks the view to scroll so this map pixel is in the middle.</summary>
    public event Action<double, double>? FocusRequested;
    /// <summary>Asks the view to open the event editor.</summary>
    public event Action<EventScript, string>? EventEditorRequested;

    public ObservableCollection<SpotEditVm> SpotEdits { get; } = new();
    public ObservableCollection<string> ItemChoices { get; } = new();
    [ObservableProperty] private bool _isSpotSelected;
    [ObservableProperty] private string _spotWrittenText = "";

    public ObservableCollection<MapInfo>    Maps     { get; } = new();
    public ObservableCollection<MapOverlay> Overlays { get; } = new();

    [ObservableProperty] private MapInfo? _selectedMap;
    [ObservableProperty] private WriteableBitmap? _mapImage;
    [ObservableProperty] private WriteableBitmap? _paletteImage;
    [ObservableProperty] private double _zoom = 1.0;
    [ObservableProperty] private bool _showNpcs = true;
    [ObservableProperty] private bool _showExits = true;
    [ObservableProperty] private bool _showArrivals = true;
    [ObservableProperty] private bool _showItems = true;

    // ── world map encounter zones (resource B0: one battle group per 4x4 blocks) ──
    /// <summary>Show (and paint) the world map's encounter zones.</summary>
    [ObservableProperty] private bool _showZones;
    /// <summary>The battle group zone painting puts down (hex text).</summary>
    [ObservableProperty] private string _zoneBrushText = "01";
    [ObservableProperty] private WriteableBitmap? _zoneImage;
    [ObservableProperty] private bool _zonesDirty;
    public bool IsWorldMap => _map?.IsWorld == true;
    public double ZoneImageWidth => (_map?.Width ?? 0) * 16;
    public double ZoneImageHeight => (_map?.Height ?? 0) * 16;
    private byte[] _zones = Array.Empty<byte>();
    private const int ZoneCell = 4;   // blocks per zone cell

    private void LoadZones()
    {
        OnPropertyChanged(nameof(IsWorldMap)); OnPropertyChanged(nameof(ZoneImageWidth)); OnPropertyChanged(nameof(ZoneImageHeight));
        ZonesDirty = false;
        if (_rom == null || _map?.IsWorld != true) { ZoneImage = null; _zones = Array.Empty<byte>(); return; }
        try { _zones = Core.Battle.Encounters.ReadZones(_rom); } catch { _zones = Array.Empty<byte>(); }
        DrawZones();
    }

    private static uint ZoneColour(int g)
    {
        if (g == 0) return 0;
        double h = (g * 47 % 360) / 60.0; double x = 1 - Math.Abs(h % 2 - 1);
        (double r, double gg, double b) = h switch
        {
            < 1 => (1.0, x, 0.0), < 2 => (x, 1.0, 0.0), < 3 => (0.0, 1.0, x),
            < 4 => (0.0, x, 1.0), < 5 => (x, 0.0, 1.0), _ => (1.0, 0.0, x),
        };
        return 0x70000000u | (uint)(r * 255) << 16 | (uint)(gg * 255) << 8 | (uint)(b * 255);
    }

    private void DrawZones()
    {
        int w = Core.Battle.Encounters.ZoneWidth, h = Core.Battle.Encounters.ZoneHeight;
        if (_zones.Length < w * h) { ZoneImage = null; return; }
        var px = new uint[w * h];
        for (int i = 0; i < px.Length; i++) px[i] = ZoneColour(_zones[i]);
        var bmp = new WriteableBitmap(w, h, 96, 96, PixelFormats.Bgra32, null);
        bmp.WritePixels(new Int32Rect(0, 0, w, h), px, w * 4, 0);
        ZoneImage = bmp;
    }

    private int ZoneAt(int x, int y)
    {
        int cx = x / ZoneCell, cy = y / ZoneCell, w = Core.Battle.Encounters.ZoneWidth;
        return cx < w && cy * w + cx < _zones.Length ? cy * w + cx : -1;
    }

    /// <summary>Paints the zone under the block with the brush group; true when it handled the click.</summary>
    private bool PaintZone(int x, int y)
    {
        if (!ShowZones || !IsWorldMap) return false;
        int i = ZoneAt(x, y);
        if (i < 0) return true;
        if (!int.TryParse(ZoneBrushText, System.Globalization.NumberStyles.HexNumber, null, out int g) || g < 0 || g > 255)
        { Status = "Zone brush: a battle group number in hex (00 = no battles)."; return true; }
        if (_zones[i] != g) { _zones[i] = (byte)g; ZonesDirty = true; DrawZones(); }
        return true;
    }

    // ── custom graphics: a picture into the tileset's blocks ──────────────────

    /// <summary>Redraw blocks from a picture: picture block k goes to block Brush + k.</summary>
    [RelayCommand]
    private void ImportOverBlocks() => ImportPicture(addNew: false);

    /// <summary>Add the picture as new blocks at the end of the tileset.</summary>
    [RelayCommand]
    private void ImportNewBlocks() => ImportPicture(addNew: true);

    private void ImportPicture(bool addNew)
    {
        if (_rom == null || _map == null || _tileset == null || _map.IsWorld)
        { Status = "Open a town or dungeon map first (the world map's graphics work differently)."; return; }
        if (HasUnappliedChanges) { Status = "Apply or revert this map's changes first: importing reloads the map."; return; }
        var dlg = new Microsoft.Win32.OpenFileDialog { Title = "Picture to import (multiple of 16 x 16 pixels)", Filter = "Pictures (*.png;*.bmp;*.gif)|*.png;*.bmp;*.gif" };
        if (dlg.ShowDialog() != true) return;
        uint[] px; int w, h;
        try
        {
            var src = new BitmapImage(new Uri(dlg.FileName));
            var bgra = new FormatConvertedBitmap(src, PixelFormats.Bgra32, null, 0);
            w = bgra.PixelWidth; h = bgra.PixelHeight; px = new uint[w * h];
            bgra.CopyPixels(px, w * 4, 0);
        }
        catch (Exception ex) { Status = "Couldn't read the picture: " + ex.Message; return; }
        if (w % 16 != 0 || h % 16 != 0) { Status = $"The picture is {w}x{h}: it must be a multiple of 16 x 16 pixels (one block each)."; return; }
        int count = w / 16 * (h / 16);
        try
        {
            var import = new TileImport(_rom, _map, _tileset);
            List<int> targets;
            if (addNew)
            {
                if (!import.CanAppendBlocks)
                { Status = "This tileset has composite blocks after its normal ones, so blocks can't be added at the end. Redraw unused blocks instead."; return; }
                targets = Enumerable.Repeat(int.MaxValue, count).ToList();
            }
            else
            {
                targets = Enumerable.Range(Brush, count).ToList();
                int limit = _map.CompositeThreshold >= 0 ? _map.CompositeThreshold : import.BlockCount;
                if (targets[^1] >= limit) { Status = $"Blocks {Brush:X3}-{targets[^1]:X3} run past the last normal block ({limit - 1:X3})."; return; }
                if (MessageBox.Show($"Redraw {count} block{(count == 1 ? "" : "s")} starting at {Brush:X3} with this picture?\n\n" +
                                    $"Every map that uses tileset {_map.Tileset} shows the new graphics wherever these blocks are placed.",
                                    "Import picture", MessageBoxButton.OKCancel, MessageBoxImage.Question) != MessageBoxResult.OK) return;
            }
            byte attr = _tileset.Attribute(Brush);
            var r = import.Draw(px, w, h, targets, attr);
            if (import.NeedsMoreSpace() && _rom.Length < MapWriter.ExpandedSize && !ConfirmExpand("The tileset's graphics or blocks no longer fit in their original space.")) return;
            string where = import.Save(allowExpand: true);
            int id = _map.MapId, firstNew = addNew ? import.BlockCount : Brush;
            LoadMap(id);
            if (addNew) Brush = Math.Min(firstNew, (_tileset?.MetatileCount ?? 1) - 1);
            _mainVm?.NotifyRomModified($"Tileset {_map?.Tileset} graphics");
            Status = r.Report + " " + where + (addNew ? $" New blocks start at {firstNew:X3} (new blocks copy the walkability of the brush block)." : "");
        }
        catch (Exception ex) { Status = "Import failed: " + ex.Message; }
    }

    [RelayCommand]
    private void WriteZones()
    {
        if (_rom == null || !IsWorldMap || _zones.Length == 0) return;
        try
        {
            bool allow = _rom.Length >= MapWriter.ExpandedSize;
            var size = LufiaCompression.Compress(_zones).Length;
            LufiaCompression.Decompress(_rom, LufiaCompression.ResourceOffset(_rom, Core.Battle.Encounters.ZoneResource), out int slot);
            if (size > slot && !allow && !ConfirmExpand("The edited encounter zones don't fit in their original space.")) return;
            var r = Core.Battle.Encounters.WriteZones(_rom, _zones, allowExpand: true);
            ZonesDirty = false;
            _mainVm?.NotifyRomModified("World map encounter zones");
            Status = r.Moved ? $"Encounter zones written at 0x{r.FileOffset:X6} (moved; {r.CompressedSize} bytes)." : $"Encounter zones written in place ({r.CompressedSize} of {r.SlotSize} bytes).";
        }
        catch (Exception ex) { Status = "Couldn't write the zones: " + ex.Message; }
    }
    /// <summary>Text for the floating tooltip over the map (what's under the mouse).</summary>
    [ObservableProperty] private string _hoverTip = "";
    [ObservableProperty] private MapTool _tool = MapTool.Select;
    [ObservableProperty] private int _brush;
    [ObservableProperty] private string _hoverText = "";
    [ObservableProperty] private string _selectionTitle = "Nothing selected";
    [ObservableProperty] private string _selectionDetails = SelectHint;
    [ObservableProperty] private string _dialoguePreview = "";
    [ObservableProperty] private string _status = "Open a ROM to browse maps.";
    [ObservableProperty] private string _mapSummary = "";
    [ObservableProperty] private bool _isMapLoaded;
    [ObservableProperty] private bool _hasUnappliedChanges;

    // Editable fields of the current selection
    [ObservableProperty] private bool _isExitSelected;
    [ObservableProperty] private bool _isNpcSelected;
    [ObservableProperty] private int _exitDestMap;
    [ObservableProperty] private int _exitArrival;
    [ObservableProperty] private string _exitDestMapText = "";
    [ObservableProperty] private int _npcX;
    [ObservableProperty] private int _npcY;

    private const string SelectHint =
        "With Select / move: click a character (yellow), exit (red), arrival point (cyan) or treasure (gold = chest, " +
        "pink = hidden item) to edit it here — exits: where they lead and their size; arrival points: their position. " +
        "Drag to move. When things overlap, click the same block again to pick the next one. Ctrl + click picks a block " +
        "for painting. Exits leading to \"this map (00)\" are doors/stairs inside the map; the dotted line shows where each goes.";

    public bool IsSelectTool { get => Tool == MapTool.Select; set { if (value) Tool = MapTool.Select; } }
    public bool IsPaintTool  { get => Tool == MapTool.Paint;  set { if (value) Tool = MapTool.Paint; } }
    public bool IsPickTool   { get => Tool == MapTool.Pick;   set { if (value) Tool = MapTool.Pick; } }
    public bool IsAddExitTool    { get => Tool == MapTool.AddExit;    set { if (value) Tool = MapTool.AddExit; } }
    public bool IsAddArrivalTool { get => Tool == MapTool.AddArrival; set { if (value) Tool = MapTool.AddArrival; } }
    partial void OnToolChanged(MapTool value)
    {
        OnPropertyChanged(nameof(IsSelectTool));
        OnPropertyChanged(nameof(IsPaintTool));
        OnPropertyChanged(nameof(IsPickTool));
        OnPropertyChanged(nameof(IsAddExitTool));
        OnPropertyChanged(nameof(IsAddArrivalTool));
        if (value == MapTool.AddExit) Status = "Add exit: drag a rectangle on the map where the party should leave.";
        if (value == MapTool.AddArrival) Status = "Add arrival point: click where the party should appear.";
    }

    public string BrushText => _tileset == null ? "" :
        $"Brush: metatile {Brush:X3}  (attribute {_tileset.Attribute(Brush):X2}){CompositeText(Brush)}";

    private string CompositeText(int mt) =>
        _tileset?.CompositeParts(mt) is var (g, o) ? $"   = ground {g:X3} + overlay {o:X3}" : "";
    partial void OnBrushChanged(int value)
    {
        OnPropertyChanged(nameof(BrushText));
        OnPropertyChanged(nameof(BrushBoxX));
        OnPropertyChanged(nameof(BrushBoxY));
    }

    /// <summary>Where the brush's block sits in the palette image (16 blocks per row), for its highlight box.</summary>
    public double BrushBoxX => Brush % 16 * 16;
    public double BrushBoxY => Brush / 16 * 16;

    /// <summary>Zoom of the block palette on the right.</summary>
    [ObservableProperty] private double _paletteZoom = 2.0;

    public void SetRom(RomBuffer rom, MainViewModel mainVm)
    {
        _rom = rom;
        _mainVm = mainVm;
        Maps.Clear();
        foreach (var m in MapCatalog.ScanNamed(rom)) Maps.Add(m);
        _generatedNames = MapNames.Generate(rom, Maps.Select(m => m.MapId).ToList());
        _links = MapLinks.Build(rom, Maps.Select(m => m.MapId));
        _arrivalCache.Clear();
        BuildDestinationChoices();
        ItemChoices.Clear();
        for (int i = 0; i < 256; i++)
            ItemChoices.Add(i == 0 ? "00 (empty)" : $"{i:X2} {MapSetupScript.ItemName(rom, i)}");
        Status = $"{Maps.Count} maps found. Pick one on the left.";
        SelectedMap = Maps.FirstOrDefault(m => m.MapId == 0x04) ?? Maps.FirstOrDefault();
    }

    private bool _revertingSelection;

    partial void OnSelectedMapChanged(MapInfo? oldValue, MapInfo? newValue)
    {
        if (_revertingSelection || newValue == null || _rom == null) return;
        if (HasUnappliedChanges &&
            MessageBox.Show("This map has edits that weren't applied to the ROM. Discard them?", "Unapplied changes",
                MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes)
        {
            // keep showing the edited map; put the list selection back
            Application.Current.Dispatcher.BeginInvoke(() =>
            {
                _revertingSelection = true;
                SelectedMap = oldValue;
                _revertingSelection = false;
            });
            return;
        }
        LoadMap(newValue.MapId);
        _populatingName = true;
        MapNameText = newValue.Name;
        _populatingName = false;
    }

    private void LoadMap(int mapId)
    {
        if (_rom == null) return;
        ClearSelection();
        _undo.Clear();
        HasUnappliedChanges = false;
        try
        {
            _map = LufiaMap.Load(_rom, mapId);
            _spotContents = MapSetupScript.ReadSpots(_rom, mapId);
            _tileset = MapTileset.ForMap(_rom, _map);
            _pixels = _tileset.RenderMap(_map);
            int w = _map.Width * 16, h = _map.Height * 16;
            var bmp = new WriteableBitmap(w, h, 96, 96, PixelFormats.Bgra32, null);
            bmp.WritePixels(new Int32Rect(0, 0, w, h), _pixels, w * 4, 0);
            MapImage = bmp;
            LoadZones();
            BuildPalette();
            Brush = 0;
            IsMapLoaded = true;
            MapSummary = $"Map {_map.MapId:X2}: {_map.Width}x{_map.Height} blocks, " +
                         (_map.IsWorld ? "world map (chunked), " : $"tileset {_map.Tileset}, palette {_map.PaletteIndex}, ") +
                         $"{_map.Npcs.Count(n => !n.IsUnused)} characters, {_map.Exits.Count(e => !e.IsUnused)} exits, " +
                         $"{_map.Spots.Count(IsTreasureSpot)} treasures, " +
                         $"data resource {_map.ResourceId:X3} at 0x{_map.CompressedOffset:X6} ({_map.CompressedSize} bytes packed)";
            RebuildOverlays();
            Status = "Loaded. Select tool: click objects. Paint tool: paint the chosen block. Pick tool: copy a block from the map.";
        }
        catch (Exception ex)
        {
            IsMapLoaded = false;
            Status = $"Could not load map {mapId:X2}: {ex.Message}";
        }
    }

    private void BuildPalette()
    {
        if (_tileset == null) return;
        int count = _tileset.MetatileCount, cols = 16, rows = (count + cols - 1) / cols;
        int w = cols * 16, h = Math.Max(1, rows) * 16;
        var buf = new uint[w * h];
        for (int i = 0; i < count; i++) _tileset.DrawMetatile(buf, w, (i % cols) * 16, (i / cols) * 16, i);
        var bmp = new WriteableBitmap(w, h, 96, 96, PixelFormats.Bgra32, null);
        bmp.WritePixels(new Int32Rect(0, 0, w, h), buf, w * 4, 0);
        PaletteImage = bmp;
    }

    partial void OnShowNpcsChanged(bool value) => RebuildOverlays();
    partial void OnShowExitsChanged(bool value) => RebuildOverlays();
    partial void OnShowArrivalsChanged(bool value) => RebuildOverlays();
    partial void OnShowItemsChanged(bool value) => RebuildOverlays();

    // ── Treasure and exit/arrival links ────────────────────────────────────

    /// <summary>Setup-script assignments for a section E spot (several when it depends on story progress).</summary>
    private List<SpotContent> ContentsOf(int spot) => _spotContents.Where(c => c.Spot == spot).ToList();

    private bool IsTreasureSpot(MapSpot s) => _spotContents.Any(c => c.Spot == s.Index && c.IsTreasure);

    private string SpotText(MapSpot s)
    {
        if (_rom == null) return "";
        var c = ContentsOf(s.Index);
        if (c.Count == 0) return $"Spot {s.Index} (nothing assigned)";
        var parts = c.Select(x => x.Describe(_rom)).Distinct().ToList();
        return parts.Count == 1 ? parts[0] : string.Join("\n  or ", parts) + "\n  (depends on story progress)";
    }

    private string MapName(int id) =>
        Maps.FirstOrDefault(m => m.MapId == id) is { } info && !string.IsNullOrEmpty(info.Name)
            ? $"{info.Name} (map {id:X2})"
            : id == LufiaMap.WorldMap ? "World map" : $"map {id:X2}";

    // ── Map names ──────────────────────────────────────────────────────────

    private Dictionary<int, string> _generatedNames = new();
    [ObservableProperty] private string _mapNameText = "";

    /// <summary>Rename the selected map (empty = back to the generated name). Saved for all ROM copies.</summary>
    partial void OnMapNameTextChanged(string value)
    {
        if (_populatingName || SelectedMap is not { } info) return;
        string name = (value ?? "").Trim();
        var custom = MapNames.LoadCustom();
        if (name.Length == 0 || name == _generatedNames.GetValueOrDefault(info.MapId))
        {
            custom.Remove(info.MapId);
            info.Name = _generatedNames.GetValueOrDefault(info.MapId, "");
            info.IsCustomName = false;
        }
        else
        {
            custom[info.MapId] = name;
            info.Name = name;
            info.IsCustomName = true;
        }
        MapNames.SaveCustom(custom);
        int keep = DestinationIndex;
        _populating = true;
        BuildDestinationChoices();
        DestinationIndex = keep;
        _populating = false;
        RebuildOverlays();
    }
    private bool _populatingName;

    /// <summary>Exits leading to arrival <paramref name="arrival"/> of the current map (live edits for this map).</summary>
    private List<MapLinks.Source> IncomingExits(int arrival)
    {
        if (_map == null) return new();
        var list = _map.Exits
            .Where(e => !e.IsUnused && LeadsHere(e) && e.Arrival == arrival)
            .Select(e => new MapLinks.Source(_map.MapId, e.Index)).ToList();
        if (_links != null)
            list.AddRange(_links.Into(_map.MapId, arrival).Where(s => s.MapId != _map.MapId));
        return list;
    }

    private bool LeadsHere(MapExit e) => _map != null && (e.DestMap == 0 || e.DestMap == _map.MapId);

    private string ExitTargetText(MapExit e) =>
        LeadsHere(e) ? $"arrival point A{e.Arrival} on this map" : $"{MapName(e.DestMap)}, arrival point A{e.Arrival}";

    private string SourceText(MapLinks.Source s) =>
        _map != null && s.MapId == _map.MapId ? $"exit E{s.Exit} on this map" : $"{MapName(s.MapId)}, exit E{s.Exit}";

    private string ArrivalLabel(int arrival)
    {
        if (_map == null) return $"A{arrival}";
        var inc = IncomingExits(arrival);
        var local = inc.Where(s => s.MapId == _map.MapId).Select(s => $"E{s.Exit}").ToList();
        var others = inc.Where(s => s.MapId != _map.MapId).ToList();
        if (others.Count == 1) local.Add(others[0].MapId == LufiaMap.WorldMap ? "world" : $"{others[0].MapId:X2}:E{others[0].Exit}");
        else if (others.Count > 1) local.Add($"{others.Count} maps");
        return local.Count == 0 ? $"A{arrival}" : $"A{arrival}←{string.Join(",", local)}";
    }

    private void RebuildOverlays()
    {
        Overlays.Clear();
        if (_map == null) return;
        if (ShowExits)
            foreach (var e in _map.Exits.Where(e => !e.IsUnused))
                Overlays.Add(new MapOverlay
                {
                    Kind = "exit", Index = e.Index, X = e.X1 * 16, Y = e.Y1 * 16,
                    W = Math.Max(1, e.X2 - e.X1) * 16, H = Math.Max(1, e.Y2 - e.Y1) * 16,
                    Stroke = Brushes.Red, Thickness = 3,
                    Label = LeadsHere(e) ? $"E{e.Index}→A{e.Arrival}" : $"E{e.Index}→{e.DestMap:X2}:A{e.Arrival}",
                });
        if (ShowExits && ShowArrivals)
            foreach (var e in _map.Exits.Where(e => !e.IsUnused && LeadsHere(e) && e.Arrival < _map.Arrivals.Count))
            {
                var to = _map.Arrivals[e.Arrival];
                double sx = (e.X1 + Math.Max(1, e.X2 - e.X1) / 2.0) * 16, sy = (e.Y1 + Math.Max(1, e.Y2 - e.Y1) / 2.0) * 16;
                double tx = to.X * 16 + 8, ty = to.Y * 16 + 8;
                if (Math.Abs(tx - sx) + Math.Abs(ty - sy) < 4) continue;
                Overlays.Add(new MapOverlay
                {
                    Kind = "link", IsLine = true, X = sx, Y = sy, LineDx = tx - sx, LineDy = ty - sy,
                    Stroke = Brushes.Orange, Thickness = 1.5,
                });
            }
        if (ShowArrivals)
            foreach (var a in _map.Arrivals)
                Overlays.Add(new MapOverlay
                {
                    Kind = "arrival", Index = a.Index, X = a.X * 16 + 3, Y = a.Y * 16 + 3, W = 10, H = 10,
                    Stroke = Brushes.Cyan, Thickness = 2, Label = ArrivalLabel(a.Index),
                });
        if (ShowItems)
            foreach (var sp in _map.Spots.Where(IsTreasureSpot))
            {
                bool hidden = ContentsOf(sp.Index).Where(c => c.IsTreasure).All(c => c.Kind == SpotKind.Hidden);
                Overlays.Add(new MapOverlay
                {
                    Kind = "spot", Index = sp.Index, X = sp.X1 * 16 + 1, Y = sp.Y1 * 16 + 1,
                    W = Math.Max(1, sp.X2 - sp.X1) * 16 - 2, H = Math.Max(1, sp.Y2 - sp.Y1) * 16 - 2,
                    Stroke = hidden ? Brushes.HotPink : Brushes.Gold, Thickness = 2,
                });
            }
        if (ShowNpcs)
            foreach (var n in _map.Npcs.Where(n => !n.IsUnused))
            {
                if (n.BoxX2 - n.BoxX1 > 1 || n.BoxY2 - n.BoxY1 > 1)
                    Overlays.Add(new MapOverlay
                    {
                        Kind = "box", Index = n.Index, X = n.BoxX1 * 16, Y = n.BoxY1 * 16,
                        W = (n.BoxX2 - n.BoxX1 + 1) * 16, H = (n.BoxY2 - n.BoxY1 + 1) * 16,
                        Stroke = new SolidColorBrush(Color.FromArgb(150, 255, 255, 0)), Thickness = 1,
                    });
                Overlays.Add(new MapOverlay
                {
                    Kind = "npc", Index = n.Index, X = n.X * 16, Y = n.Y * 16, W = 16, H = 16,
                    Stroke = Brushes.Yellow, Thickness = 3, Label = n.Index.ToString(),
                });
            }
        if (_selection is { } sel)
        {
            var o = Overlays.FirstOrDefault(x => x.Kind == sel.kind && x.Index == sel.index);
            if (o != null)
                Overlays.Add(new MapOverlay { Kind = "sel", X = o.X - 3, Y = o.Y - 3, W = o.W + 6, H = o.H + 6, Stroke = Brushes.White, Thickness = 2 });

            // outline the other end of an exit/arrival link in green when it is on this map
            var linked = new List<MapOverlay>();
            if (sel.kind == "exit" && LeadsHere(_map.Exits[sel.index]))
                linked.AddRange(Overlays.Where(x => x.Kind == "arrival" && x.Index == _map.Exits[sel.index].Arrival));
            if (sel.kind == "arrival")
                foreach (var src in IncomingExits(sel.index).Where(src => src.MapId == _map.MapId))
                    linked.AddRange(Overlays.Where(x => x.Kind == "exit" && x.Index == src.Exit));
            foreach (var l in linked)
                Overlays.Add(new MapOverlay { Kind = "link", X = l.X - 4, Y = l.Y - 4, W = l.W + 8, H = l.H + 8, Stroke = Brushes.Lime, Thickness = 2 });
        }
    }

    // ── Mouse input from the view (coordinates in map pixels) ──────────────

    public void OnMouseMove(double px, double py, bool leftDown)
    {
        if (_map == null || _tileset == null) return;
        int x = (int)(px / 16), y = (int)(py / 16);
        if (x < 0 || y < 0 || x >= _map.Width || y >= _map.Height) { HoverText = ""; HoverTip = ""; return; }
        int mt = _map.GetTile(x, y) & 0x3FF;
        HoverText = $"Block ({x}, {y})   metatile {mt:X3}   attribute {_tileset.Attribute(mt):X2}{CompositeText(mt)}" +
                    (ShowZones && IsWorldMap && ZoneAt(x, y) is >= 0 and var zi ? $"   battle group {_zones[zi]:X2}" : "");
        if (leftDown && PaintZone(x, y)) return;
        HoverTip = leftDown ? "" : DescribeAt(x, y);

        if (!leftDown) return;
        if (_picking) { Brush = mt; return; }   // Ctrl held: keep sampling the block under the cursor
        if (Tool == MapTool.Paint) PaintAt(x, y);
        else if (_dragStart is { } start && _dragMode != null)
        {
            if ((x, y) != start) _dragMoved = true;
            switch (_dragMode)
            {
                case "npc": DragNpc(x, y, start); break;
                case "exit-move": DragExit(x, y, resize: false); break;
                case "exit-resize": case "new-exit": DragExit(x, y, resize: true); break;
                case "arrival": DragArrival(x, y); break;
            }
        }
    }

    public void OnMouseLeave() => HoverTip = "";

    /// <summary>Tooltip text for everything on a block: treasure, exits, arrival points, characters.</summary>
    private string DescribeAt(int x, int y)
    {
        if (_map == null) return "";
        var lines = new List<string>();
        if (ShowItems)
            foreach (var sp in _map.Spots.Where(s => IsTreasureSpot(s) && Inside(x, y, s.X1, s.Y1, s.X2, s.Y2)))
                lines.Add(SpotText(sp));
        if (ShowExits)
            foreach (var e in _map.Exits.Where(e => !e.IsUnused && Inside(x, y, e.X1, e.Y1, e.X2, e.Y2)))
                lines.Add($"Exit E{e.Index} → {ExitTargetText(e)}");
        if (ShowArrivals)
            foreach (var a in _map.Arrivals.Where(a => a.X == x && a.Y == y))
            {
                var inc = IncomingExits(a.Index);
                lines.Add($"Arrival point A{a.Index}" + (inc.Count == 0 ? " (no exit leads here)"
                    : " ← " + string.Join("; ", inc.Take(4).Select(SourceText)) + (inc.Count > 4 ? $"; +{inc.Count - 4} more" : "")));
            }
        if (ShowNpcs)
            foreach (var n in _map.Npcs.Where(n => !n.IsUnused && n.X == x && n.Y == y))
                lines.Add($"Character {n.Index + 1} (talking runs event {n.Index})");
        return string.Join("\n", lines);
    }

    private static bool Inside(int x, int y, int x1, int y1, int x2, int y2) =>
        x >= x1 && x < Math.Max(x2, x1 + 1) && y >= y1 && y < Math.Max(y2, y1 + 1);

    public void OnMouseDown(double px, double py, bool ctrl = false)
    {
        if (_map == null) return;
        int x = (int)(px / 16), y = (int)(py / 16);
        if (x < 0 || y < 0 || x >= _map.Width || y >= _map.Height) return;
        if (ShowZones && IsWorldMap)
        {
            if (ctrl && ZoneAt(x, y) is >= 0 and var zi) { ZoneBrushText = _zones[zi].ToString("X2"); Status = $"Zone brush: group {ZoneBrushText}."; return; }
            if (PaintZone(x, y)) return;
        }
        if (ctrl)
        {
            _picking = true;   // eyedropper: no painting until the button is released
            Brush = _map.GetTile(x, y) & 0x3FF;
            Status = $"Picked block {Brush:X3} from ({x}, {y}). Paint with it, or Ctrl + click another block.";
            if (Tool != MapTool.Paint) Tool = MapTool.Paint;
            return;
        }
        switch (Tool)
        {
            case MapTool.Pick:
                Brush = _map.GetTile(x, y) & 0x3FF;
                Tool = MapTool.Paint;
                Status = $"Picked metatile {Brush:X3}. Paint tool selected.";
                break;
            case MapTool.Paint:
                _stroke = new();
                PaintAt(x, y);
                break;
            case MapTool.AddExit:
            {
                var e = _map.AddExit(x, y, x + 1, y + 1, 0, 0);
                HasUnappliedChanges = true;
                SelectExit(e, focusLinked: false);
                _dragStart = (x, y); _dragAnchor = (x, y); _dragMode = "new-exit"; _dragMoved = false;
                break;
            }
            case MapTool.AddArrival:
            {
                var a = _map.AddArrival(x, y);
                HasUnappliedChanges = true;
                Tool = MapTool.Select;
                SelectArrival(a, focusLinked: false);
                Status = $"Added arrival point A{a.Index} at ({x}, {y}). Exits can now lead here.";
                break;
            }
            default:
                SelectAt(x, y);
                _dragMoved = false;
                _dragMode = null;
                _dragStart = null;
                if (_selection is { kind: "npc" }) { _dragMode = "npc"; _dragStart = (x, y); }
                else if (_selection is { kind: "arrival" }) { _dragMode = "arrival"; _dragStart = (x, y); }
                else if (_selection is { kind: "exit" } se)
                {
                    var ex = _map.Exits[se.index];
                    bool corner = x == Math.Max(ex.X2, ex.X1 + 1) - 1 && y == Math.Max(ex.Y2, ex.Y1 + 1) - 1 &&
                                  (ex.X2 - ex.X1 > 1 || ex.Y2 - ex.Y1 > 1);
                    _dragMode = corner ? "exit-resize" : "exit-move";
                    _dragStart = (x, y);
                    _dragAnchor = corner ? (ex.X1, ex.Y1) : (x - ex.X1, y - ex.Y1);
                }
                break;
        }
    }

    private bool _picking;

    public void OnMouseUp()
    {
        _picking = false;
        if (_stroke is { Count: > 0 }) _undo.Push(_stroke);
        _stroke = null;
        bool clickedNpc = _dragStart != null && !_dragMoved && _dragMode == "npc";
        if (_dragMode == "new-exit")
        {
            Tool = MapTool.Select;
            Status = "New exit added. Now choose where it leads: pick a destination map and arrival point, or use \"Pick on map…\".";
        }
        _dragStart = null;
        _dragMode = null;
        if (clickedNpc) OpenEventEditor();
    }

    /// <summary>Double-click: follow an exit to its destination map, or an arrival back to an exit that leads to it.</summary>
    public void OnDoubleClick(double px, double py)
    {
        if (_map == null || Tool != MapTool.Select) return;
        if (_selection is { kind: "exit" }) { GoToDestination(); return; }
        if (_selection is { kind: "arrival" } sel)
        {
            var sources = IncomingExits(sel.index).Where(s => s.MapId != _map.MapId && s.MapId != LufiaMap.WorldMap).ToList();
            if (sources.Count == 0) { Status = "No exit on another map (that the editor can show) leads here."; return; }
            var src = sources[_focusCycle++ % sources.Count];
            var info = Maps.FirstOrDefault(m => m.MapId == src.MapId);
            if (info == null) return;
            SelectedMap = info;
            if (_map?.MapId == src.MapId && src.Exit < _map.Exits.Count)
            {
                SelectExit(_map.Exits[src.Exit], focusLinked: false);
                FocusOn(_map.Exits[src.Exit]);
                Status = $"Exit E{src.Exit} of map {src.MapId:X2} leads to that arrival point." +
                         (sources.Count > 1 ? " Double-click the arrival point again to see the next one." : "");
            }
        }
    }

    [RelayCommand]
    private void OpenEventEditor()
    {
        if (_rom == null || _map == null || _selection is not { kind: "npc" } sel) return;
        var script = EventScript.Load(_rom, _map.MapId, sel.index);
        if (script == null || script.Ops.Count == 0 || (script.Ops.Count == 1 && script.Ops[0].Bytes is [0x00]))
        {
            Status = $"Character {sel.index + 1} has no event script (talking runs event {sel.index}).";
            return;
        }
        EventEditorRequested?.Invoke(script, $"Map {_map.MapId:X2} - character {sel.index + 1} (talking runs event {sel.index})");
    }

    /// <summary>
    /// Tell the user an edit is now in the ROM (in memory), where it went, and that it still has to be saved.
    /// </summary>
    public void ConfirmWritten(string heading, string details)
    {
        Modules.Common.InfoDialog.Show("Written to ROM", "✔ " + heading,
            details + "\n\nThe change is in the ROM loaded in Lufia Forge, but not on disk yet. " +
            "Use File > Save ROM (or the button below) to write a new dated copy; the ROM you opened is never overwritten.",
            path: _rom?.FilePath, pathLabel: "ROM being edited (the copy is saved next to it)",
            primaryText: _mainVm != null ? "💾 Save ROM now" : null, primary: () => _mainVm?.SaveNow());
    }

    /// <summary>Called by the event editor after it wrote to the ROM.</summary>
    public void EventSaved(string what)
    {
        _mainVm?.NotifyRomModified(what);
        if (_selection is { kind: "npc" } sel && _map != null && sel.index < _map.Npcs.Count) SelectNpc(_map.Npcs[sel.index]);
    }

    public bool ConfirmExpand(string what) =>
        MessageBox.Show(
            $"{what}\n\nTo store it, Lufia Forge needs to expand the ROM from 1 MB to 2 MB and use the new space. " +
            "Expanded ROMs work in BizHawk, Snes9x and bsnes. Note: savestates made before the expansion will undo " +
            "anything stored in the new space, so continue from an in-game save instead. Continue?",
            "Expand ROM?", MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes;

    private void FocusOn(MapExit e) =>
        FocusRequested?.Invoke((e.X1 + Math.Max(1, e.X2 - e.X1) / 2.0) * 16, (e.Y1 + Math.Max(1, e.Y2 - e.Y1) / 2.0) * 16);

    private void FocusOn(MapArrival a) => FocusRequested?.Invoke(a.X * 16 + 8, a.Y * 16 + 8);

    public void PickBrushFromPalette(double px, double py)
    {
        if (_tileset == null) return;
        int i = (int)(py / 16) * 16 + (int)(px / 16);
        if (i < 0 || i >= _tileset.MetatileCount) return;
        Brush = i;
        Tool = MapTool.Paint;
    }

    private void PaintAt(int x, int y)
    {
        if (_map == null || _tileset == null || MapImage == null || _pixels == null) return;
        ushort before = _map.GetTile(x, y);
        if ((before & 0x3FF) == Brush) return;
        _stroke ??= new();
        _stroke.Add((x, y, before));
        _map.SetTile(x, y, Brush);
        RedrawBlock(x, y);
        HasUnappliedChanges = true;
    }

    private void RedrawBlock(int x, int y)
    {
        if (_map == null || _tileset == null || MapImage == null) return;
        var px = _tileset.MetatilePixels(_map.GetTile(x, y));
        MapImage.WritePixels(new Int32Rect(x * 16, y * 16, 16, 16), px, 64, 0);
    }

    [RelayCommand]
    private void Undo()
    {
        if (_map == null || _undo.Count == 0) return;
        var stroke = _undo.Pop();
        for (int i = stroke.Count - 1; i >= 0; i--)
        {
            var (x, y, before) = stroke[i];
            _map.Layer[y * _map.Width + x] = before;
            RedrawBlock(x, y);
        }
        Status = $"Undid {stroke.Count} block change(s).";
    }

    // ── Selection ──────────────────────────────────────────────────────────

    private (int X, int Y)? _lastClick;

    private void SelectAt(int x, int y)
    {
        if (_map == null) return;
        // everything on this block, in click order; clicking the same block again picks the next one
        var here = new List<(string Kind, int Index)>();
        if (ShowNpcs) here.AddRange(_map.Npcs.Where(n => !n.IsUnused && n.X == x && n.Y == y).Reverse().Select(n => ("npc", n.Index)));
        if (ShowArrivals) here.AddRange(_map.Arrivals.Where(a => a.X == x && a.Y == y).Reverse().Select(a => ("arrival", a.Index)));
        if (ShowItems) here.AddRange(_map.Spots.Where(s => IsTreasureSpot(s) && Inside(x, y, s.X1, s.Y1, s.X2, s.Y2)).Reverse().Select(s => ("spot", s.Index)));
        if (ShowExits) here.AddRange(_map.Exits.Where(e => !e.IsUnused && Inside(x, y, e.X1, e.Y1, e.X2, e.Y2)).Reverse().Select(e => ("exit", e.Index)));
        if (here.Count == 0) { _lastClick = (x, y); ClearSelection(); RebuildOverlays(); return; }
        int pick = 0;
        if (_lastClick == (x, y) && _selection is { } cur)
        {
            int i = here.IndexOf((cur.kind, cur.index));
            if (i >= 0) pick = (i + 1) % here.Count;
        }
        _lastClick = (x, y);
        var (kind, index) = here[pick];
        switch (kind)
        {
            case "npc": SelectNpc(_map.Npcs[index]); break;
            case "arrival": SelectArrival(_map.Arrivals[index]); break;
            case "spot": SelectSpot(_map.Spots[index]); break;
            default: SelectExit(_map.Exits[index]); break;
        }
        if (here.Count > 1) Status = $"{here.Count} things on this block; click it again for the next one ({pick + 1} of {here.Count}).";
    }

    private void ClearSelection()
    {
        _selection = null;
        IsExitSelected = IsNpcSelected = IsSpotSelected = IsArrivalSelected = false;
        SpotEdits.Clear();
        SelectionTitle = "Nothing selected";
        SelectionDetails = SelectHint;
        DialoguePreview = "";
    }

    private void SelectNpc(MapNpc n)
    {
        if (_map == null || _rom == null) return;
        _selection = ("npc", n.Index);
        IsNpcSelected = true; IsExitSelected = IsSpotSelected = IsArrivalSelected = false;
        SpotEdits.Clear();
        _populating = true;
        NpcX = n.X; NpcY = n.Y;
        _populating = false;
        int script = LufiaMap.EventScriptOffset(_rom, _map.MapId, n.Index);
        SelectionTitle = $"Character {n.Index + 1}";
        SelectionDetails =
            $"Sprite {n.Sprite:X2}   flags {n.Flags:X2}\n" +
            $"Position ({n.X}, {n.Y})   walking area ({n.BoxX1},{n.BoxY1})-({n.BoxX2},{n.BoxY2})\n" +
            $"Talking runs event {n.Index} of map {_map.MapId:X2}" +
            (script >= 0 ? $": script at 0x{script:X6}" : " (no script)");
        DialoguePreview = script >= 0 ? FirstLine(script) : "";
        RebuildOverlays();
    }

    private void SelectExit(MapExit e, bool focusLinked = true)
    {
        if (_map == null) return;
        _selection = ("exit", e.Index);
        IsExitSelected = true; IsNpcSelected = IsSpotSelected = false;
        SpotEdits.Clear();
        IsArrivalSelected = false;
        _populating = true;
        ExitDestMap = e.DestMap; ExitArrival = e.Arrival;
        ExitDestMapText = e.DestMap.ToString("X2");
        ExitX = e.X1; ExitY = e.Y1; ExitW = Math.Max(1, e.X2 - e.X1); ExitH = Math.Max(1, e.Y2 - e.Y1);
        DestinationIndex = DestIndexFor(e.DestMap);
        FillArrivalChoices(e.DestMap == 0 ? _map.MapId : e.DestMap);
        ArrivalIndex = e.Arrival < ArrivalChoices.Count ? e.Arrival : -1;
        _populating = false;
        SelectionTitle = $"Exit {e.Index}";
        SelectionDetails =
            $"Area ({e.X1},{e.Y1}) to ({e.X2 - 1},{e.Y2 - 1})   type {e.Flags >> 4:X}\n" +
            $"Leads to {ExitTargetText(e)}" +
            (LeadsHere(e) ? "\n(the linked arrival point is outlined in green)" : "\nDouble-click the exit to go there.");
        DialoguePreview = "";
        RebuildOverlays();
        if (focusLinked && LeadsHere(e) && e.Arrival < _map.Arrivals.Count) FocusOn(_map.Arrivals[e.Arrival]);
    }

    private void SelectArrival(MapArrival a, bool focusLinked = true)
    {
        _selection = ("arrival", a.Index);
        IsExitSelected = IsNpcSelected = IsSpotSelected = false;
        IsArrivalSelected = true;
        CanDeleteArrival = _map?.CanRemoveArrival(a) == true;
        _populating = true;
        ArrivalX = a.X; ArrivalY = a.Y;
        _populating = false;
        SpotEdits.Clear();
        SelectionTitle = $"Arrival point {a.Index}";
        var inc = IncomingExits(a.Index);
        SelectionDetails = $"Position ({a.X}, {a.Y})   flags {a.Flags:X4}\n" +
            (inc.Count == 0
                ? "No exit in the game leads here (it may be used by a cutscene or warp)."
                : "The party arrives here from:\n  " + string.Join("\n  ", inc.Select(SourceText)) +
                  (inc.Any(src => src.MapId == _map?.MapId) ? "\n(linked exits on this map are outlined in green)" : "") +
                  (inc.Any(src => src.MapId != _map?.MapId) ? "\nDouble-click the arrival point to go to an exit on another map." : ""));
        DialoguePreview = "";
        RebuildOverlays();
        if (focusLinked && _map != null)
        {
            var local = inc.Where(src => src.MapId == _map.MapId && src.Exit < _map.Exits.Count).ToList();
            if (local.Count > 0) FocusOn(_map.Exits[local[_focusCycle++ % local.Count].Exit]);
        }
    }

    private void SelectSpot(MapSpot s)
    {
        if (_map == null || _rom == null) return;
        _selection = ("spot", s.Index);
        IsExitSelected = IsNpcSelected = IsArrivalSelected = false;
        IsSpotSelected = true;
        var c = ContentsOf(s.Index);
        bool hidden = c.Where(x => x.IsTreasure).All(x => x.Kind == SpotKind.Hidden);
        SelectionTitle = hidden ? $"Hidden item (spot {s.Index})" : $"Treasure chest (spot {s.Index})";
        SelectionDetails =
            $"Position ({s.X1}, {s.Y1})\n" + SpotText(s) + "\n\n" +
            string.Join("\n", c.Select(x =>
                $"Setup script 0x{x.ScriptOffset:X6}: value {x.Value:X4}, opened flag {x.Flag:X2}" +
                (x.Conditional ? " (after a story check)" : "")));
        DialoguePreview = "";
        SpotEdits.Clear();
        SpotWrittenText = "";
        for (int i = 0; i < c.Count; i++)
            if (c[i].IsTreasure)
                SpotEdits.Add(new SpotEditVm(c[i], c.Count > 1 ? $"Contents ({(i == 0 ? "earlier in the story" : "later in the story")})" : "Contents",
                    WriteSpot));
        RebuildOverlays();
    }

    /// <summary>Write a chest/hidden-item change straight into the map's setup script in the ROM.</summary>
    private void WriteSpot(SpotEditVm e)
    {
        if (_rom == null || _map == null) return;
        int v = e.NewValue;
        if (v == e.Content.Value) return;
        _rom.WriteByte(e.Content.ScriptOffset + 1, (byte)v);
        _rom.WriteByte(e.Content.ScriptOffset + 2, (byte)(v >> 8));
        _spotContents = MapSetupScript.ReadSpots(_rom, _map.MapId);
        var updated = _spotContents.FirstOrDefault(x => x.ScriptOffset == e.Content.ScriptOffset);
        if (updated != null) e.Refresh(updated);
        if (_selection is { kind: "spot" } sel)
        {
            var spot = _map.Spots[sel.index];
            SelectionDetails = $"Position ({spot.X1}, {spot.Y1})\n" + SpotText(spot);
        }
        RebuildOverlays();
        string what = $"Map {_map.MapId:X2} spot {e.Content.Spot}: {updated?.Describe(_rom)}";
        _mainVm?.NotifyRomModified(what);
        SpotWrittenText = $"✔ Written to the ROM at 0x{e.Content.ScriptOffset + 1:X6}: {updated?.Describe(_rom)}.\n" +
                          "Not on disk yet; use File > Save ROM to write a dated copy.";
        Status = $"Treasure changed in the ROM ({updated?.Describe(_rom)}). Use Save ROM to write a dated copy.";
    }

    private void DragNpc(int x, int y, (int x, int y) start)
    {
        if (_map == null || _selection is not { kind: "npc" } sel) return;
        var n = _map.Npcs[sel.index];
        int dx = x - n.X, dy = y - n.Y;
        if (dx == 0 && dy == 0) return;
        n.X += dx; n.Y += dy;
        n.BoxX1 += dx; n.BoxX2 += dx; n.BoxY1 += dy; n.BoxY2 += dy;
        _populating = true;
        NpcX = n.X; NpcY = n.Y;
        _populating = false;
        HasUnappliedChanges = true;
        RebuildOverlays();
    }

    partial void OnNpcXChanged(int value) => MoveSelectedNpcTo(value, NpcY);
    partial void OnNpcYChanged(int value) => MoveSelectedNpcTo(NpcX, value);

    private void MoveSelectedNpcTo(int x, int y)
    {
        if (_populating || _map == null || _selection is not { kind: "npc" } sel) return;
        var n = _map.Npcs[sel.index];
        if (n.X == x && n.Y == y) return;
        x = Math.Clamp(x, 0, _map.Width - 1); y = Math.Clamp(y, 0, _map.Height - 1);
        int dx = x - n.X, dy = y - n.Y;
        n.X = x; n.Y = y;
        n.BoxX1 += dx; n.BoxX2 += dx; n.BoxY1 += dy; n.BoxY2 += dy;
        HasUnappliedChanges = true;
        RebuildOverlays();
    }

    // ── Exit / arrival editing ─────────────────────────────────────────────

    private void BuildDestinationChoices()
    {
        DestinationChoices.Clear();
        _destIds.Clear();
        DestinationChoices.Add("This map (00)");
        _destIds.Add(0);
        foreach (var m in Maps)
        {
            DestinationChoices.Add(m.Label);
            _destIds.Add(m.MapId);
        }
    }

    private int DestIndexFor(int destMap)
    {
        int i = _destIds.IndexOf(destMap);
        return i >= 0 ? i : 0;
    }

    /// <summary>Arrival points of a map: the live list for the map being edited, otherwise read from the ROM.</summary>
    public List<MapArrival> ArrivalsOf(int mapId)
    {
        if (_map != null && mapId == _map.MapId) return _map.Arrivals;
        if (_rom == null) return new();
        if (!_arrivalCache.TryGetValue(mapId, out var list))
        {
            try { list = LufiaMap.Load(_rom, mapId).Arrivals; } catch { list = new(); }
            _arrivalCache[mapId] = list;
        }
        return list;
    }

    private void FillArrivalChoices(int mapId)
    {
        ArrivalChoices.Clear();
        var incoming = mapId == _map?.MapId;
        foreach (var a in ArrivalsOf(mapId))
            ArrivalChoices.Add($"A{a.Index}  at ({a.X}, {a.Y})");
    }

    partial void OnDestinationIndexChanged(int value)
    {
        if (_populating || value < 0 || value >= _destIds.Count || _map == null || _selection is not { kind: "exit" }) return;
        int dest = _destIds[value];
        if (dest == _map.MapId) dest = 0;
        _populating = true;
        FillArrivalChoices(dest == 0 ? _map.MapId : dest);
        ArrivalIndex = ArrivalChoices.Count > 0 ? 0 : -1;
        _populating = false;
        ExitArrival = Math.Max(0, ArrivalIndex);
        ExitDestMap = dest;
    }

    partial void OnArrivalIndexChanged(int value)
    {
        if (_populating || value < 0) return;
        ExitArrival = value;
    }

    partial void OnExitXChanged(int value) => ApplyExitRect();
    partial void OnExitYChanged(int value) => ApplyExitRect();
    partial void OnExitWChanged(int value) => ApplyExitRect();
    partial void OnExitHChanged(int value) => ApplyExitRect();

    private void ApplyExitRect()
    {
        if (_populating || _map == null || _selection is not { kind: "exit" } sel) return;
        var e = _map.Exits[sel.index];
        int x = Math.Clamp(ExitX, 0, _map.Width - 1), y = Math.Clamp(ExitY, 0, _map.Height - 1);
        int w = Math.Clamp(ExitW, 1, _map.Width - x), h = Math.Clamp(ExitH, 1, _map.Height - y);
        if (e.X1 == x && e.Y1 == y && e.X2 == x + w && e.Y2 == y + h) return;
        e.X1 = x; e.Y1 = y; e.X2 = x + w; e.Y2 = y + h;
        HasUnappliedChanges = true;
        RebuildOverlays();
    }

    private void DragExit(int x, int y, bool resize)
    {
        if (_map == null || _selection is not { kind: "exit" } sel) return;
        var e = _map.Exits[sel.index];
        x = Math.Clamp(x, 0, _map.Width - 1); y = Math.Clamp(y, 0, _map.Height - 1);
        if (resize)
        {
            var (ax, ay) = _dragAnchor;
            e.X1 = Math.Min(ax, x); e.Y1 = Math.Min(ay, y);
            e.X2 = Math.Max(ax, x) + 1; e.Y2 = Math.Max(ay, y) + 1;
        }
        else
        {
            int w = Math.Max(1, e.X2 - e.X1), h = Math.Max(1, e.Y2 - e.Y1);
            int nx = Math.Clamp(x - _dragAnchor.x, 0, _map.Width - w), ny = Math.Clamp(y - _dragAnchor.y, 0, _map.Height - h);
            if (nx == e.X1 && ny == e.Y1) return;
            e.X1 = nx; e.Y1 = ny; e.X2 = nx + w; e.Y2 = ny + h;
        }
        _populating = true;
        ExitX = e.X1; ExitY = e.Y1; ExitW = e.X2 - e.X1; ExitH = e.Y2 - e.Y1;
        _populating = false;
        HasUnappliedChanges = true;
        RebuildOverlays();
    }

    private void DragArrival(int x, int y)
    {
        if (_map == null || _selection is not { kind: "arrival" } sel) return;
        var a = _map.Arrivals[sel.index];
        x = Math.Clamp(x, 0, _map.Width - 1); y = Math.Clamp(y, 0, _map.Height - 1);
        if (a.X == x && a.Y == y) return;
        a.X = x; a.Y = y;
        _populating = true;
        ArrivalX = x; ArrivalY = y;
        _populating = false;
        HasUnappliedChanges = true;
        RebuildOverlays();
    }

    partial void OnArrivalXChanged(int value) => MoveSelectedArrival();
    partial void OnArrivalYChanged(int value) => MoveSelectedArrival();

    private void MoveSelectedArrival()
    {
        if (_populating || _map == null || _selection is not { kind: "arrival" } sel) return;
        var a = _map.Arrivals[sel.index];
        int x = Math.Clamp(ArrivalX, 0, _map.Width - 1), y = Math.Clamp(ArrivalY, 0, _map.Height - 1);
        if (a.X == x && a.Y == y) return;
        a.X = x; a.Y = y;
        HasUnappliedChanges = true;
        RebuildOverlays();
    }

    [RelayCommand]
    private void DeleteSelected()
    {
        if (_map == null) return;
        if (_selection is { kind: "exit" } se && se.index < _map.Exits.Count)
        {
            _map.RemoveExit(_map.Exits[se.index]);
            HasUnappliedChanges = true;
            ClearSelection();
            RebuildOverlays();
            Status = "Exit deleted (exits after it were renumbered). Apply to ROM to keep the change.";
        }
        else if (_selection is { kind: "arrival" } sa && sa.index < _map.Arrivals.Count)
        {
            var a = _map.Arrivals[sa.index];
            if (!_map.CanRemoveArrival(a))
            {
                Status = "Only the last arrival point can be deleted, because exits on other maps refer to arrival points by number.";
                return;
            }
            var inc = IncomingExits(a.Index);
            if (inc.Count > 0 && MessageBox.Show($"{inc.Count} exit(s) lead to this arrival point. Delete it anyway?", "Delete arrival point",
                    MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
            _map.RemoveArrival(a);
            HasUnappliedChanges = true;
            ClearSelection();
            RebuildOverlays();
            Status = "Arrival point deleted. Apply to ROM to keep the change.";
        }
    }

    [RelayCommand]
    private void PickDestination()
    {
        if (_map == null || _selection is not { kind: "exit" } sel) return;
        DestinationPickerRequested?.Invoke(_map.Exits[sel.index]);
    }

    /// <summary>The map being edited (live, with unapplied changes) or a fresh copy from the ROM.</summary>
    public LufiaMap? MapForPicker(int mapId)
    {
        if (_map != null && (mapId == _map.MapId || mapId == 0)) return _map;
        if (_rom == null) return null;
        try { return LufiaMap.Load(_rom, mapId); } catch { return null; }
    }

    /// <summary>
    /// Result of the destination picker. A new arrival point on another map is written to the ROM right
    /// away (that map has to be saved for the exit to work); on this map it is added like any other edit.
    /// </summary>
    public void SetExitDestination(MapExit exit, int destMap, int arrival, (int x, int y)? newArrival)
    {
        if (_map == null || _rom == null) return;
        int dest = destMap == _map.MapId ? 0 : destMap;
        if (newArrival is { } pos)
        {
            if (dest == 0)
                arrival = _map.AddArrival(pos.x, pos.y).Index;
            else
            {
                var other = LufiaMap.Load(_rom, destMap);
                arrival = other.AddArrival(pos.x, pos.y).Index;
                bool expand = false;
                if (MapWriter.NeedsMoreSpace(other, out _) && _rom.Length < MapWriter.ExpandedSize)
                {
                    if (!ConfirmExpand($"Adding the arrival point makes map {destMap:X2} too big for its slot.")) return;
                    expand = true;
                }
                var r = MapWriter.Save(_rom, other, expand || _rom.Length >= MapWriter.ExpandedSize);
                _arrivalCache.Remove(destMap);
                _mainVm?.NotifyRomModified($"Map {destMap:X2}: new arrival point A{arrival} at ({pos.x}, {pos.y})");
                ConfirmWritten($"Arrival point A{arrival} added to map {destMap:X2}",
                    $"New arrival point at ({pos.x}, {pos.y}). {r.Describe()}\n\nThe exit on this map points to it; " +
                    "apply this map to the ROM too so the exit is saved.");
            }
        }
        exit.DestMap = dest;
        exit.Arrival = arrival;
        HasUnappliedChanges = true;
        _links = MapLinks.Build(_rom, Maps.Select(m => m.MapId));
        SelectExit(exit, focusLinked: dest == 0);
        Status = $"Exit E{exit.Index} now leads to {ExitTargetText(exit)}. Apply to ROM to keep the change.";
    }

    partial void OnExitDestMapChanged(int value) => UpdateSelectedExit();
    partial void OnExitDestMapTextChanged(string value)
    {
        if (int.TryParse(value?.Trim(), System.Globalization.NumberStyles.HexNumber, null, out int v) && v is >= 0 and <= 0xFF)
            ExitDestMap = v;
    }
    partial void OnExitArrivalChanged(int value) => UpdateSelectedExit();

    private void UpdateSelectedExit()
    {
        if (_populating || _map == null || _selection is not { kind: "exit" } sel) return;
        var e = _map.Exits[sel.index];
        int dest = Math.Clamp(ExitDestMap, 0, 255), arr = Math.Clamp(ExitArrival, 0, 255);
        if (e.DestMap == dest && e.Arrival == arr) return;
        e.DestMap = dest; e.Arrival = arr;
        HasUnappliedChanges = true;
        RebuildOverlays();
        SelectionDetails =
            $"Area ({e.X1},{e.Y1}) to ({e.X2 - 1},{e.Y2 - 1})   type {e.Flags >> 4:X}\n" +
            $"Leads to {ExitTargetText(e)}";
    }

    [RelayCommand]
    private void GoToDestination()
    {
        if (_map == null || _selection is not { kind: "exit" } sel) return;
        var e = _map.Exits[sel.index];
        int target = e.DestMap == 0 ? _map.MapId : e.DestMap;
        var info = Maps.FirstOrDefault(m => m.MapId == target);
        if (info == null) { Status = $"Map {target:X2} isn't in the map list."; return; }
        int arrival = e.Arrival;
        SelectedMap = info;
        if (_map != null && _map.MapId == target && arrival < _map.Arrivals.Count)
        {
            SelectArrival(_map.Arrivals[arrival], focusLinked: false);
            FocusOn(_map.Arrivals[arrival]);
            Status = $"Exit leads here: arrival point {arrival} at ({_map.Arrivals[arrival].X}, {_map.Arrivals[arrival].Y}).";
        }
    }

    /// <summary>
    /// Approximate first line of dialogue in an event script: the first text opener (0C/0D narration,
    /// 88-AF "actor speaks") within the first bytes whose text contains real words.
    /// </summary>
    private string FirstLine(int script)
    {
        if (_rom == null) return "";
        for (int p = script; p < script + 128 && p < _rom.Length - 1; p++)
        {
            byte b = _rom.ReadByte(p);
            if (!(b is 0x0C or 0x0D || (b >= 0x88 && b <= 0xAF))) continue;
            var decoded = TextDecoder.Decode(_rom, p + 1, expandMte: true);
            var sb = new StringBuilder();
            foreach (var t in decoded.Tokens)
            {
                if (t.Kind is TextTokenKind.PageBreak or TextTokenKind.EndString) break;
                sb.Append(t.Kind == TextTokenKind.Newline ? " " : t.Display);
                if (sb.Length > 300) break;
            }
            string text = sb.ToString().Trim();
            if (text.Count(char.IsLetter) >= 4) return "\"" + text + "\"";
        }
        return "(no dialogue found near the start of this script)";
    }

    // ── Apply / revert ─────────────────────────────────────────────────────

    [RelayCommand]
    private void ApplyToRom()
    {
        if (_rom == null || _map == null) return;
        try
        {
            bool expand = false;
            if (MapWriter.NeedsMoreSpace(_map, out int size) && _rom.Length < MapWriter.ExpandedSize)
            {
                var answer = MessageBox.Show(
                    $"The edited map packs to {size} bytes, but its original space holds {_map.CompressedSize} bytes.\n\n" +
                    "To save it, Lufia Forge needs to expand the ROM from 1 MB to 2 MB and store the map in the new space. " +
                    "Expanded ROMs work in BizHawk, Snes9x and bsnes. Continue?",
                    "Expand ROM?", MessageBoxButton.YesNo, MessageBoxImage.Question);
                if (answer != MessageBoxResult.Yes) { Status = "Not applied."; return; }
                expand = true;
            }
            var result = MapWriter.Save(_rom, _map, allowExpand: expand || _rom.Length >= MapWriter.ExpandedSize);
            int id = _map.MapId;
            HasUnappliedChanges = false;
            _mainVm?.NotifyRomModified($"Map {id:X2}: blocks/characters/exits ({(result.Moved ? $"moved to 0x{result.FileOffset:X6}" : $"in place at 0x{result.FileOffset:X6}")})");
            _links = MapLinks.Build(_rom, Maps.Select(m => m.MapId));
            LoadMap(id);
            Status = result.Describe() + " Use Save ROM to write a dated copy.";
            ConfirmWritten($"Map {id:X2} written to the ROM", result.Describe());
        }
        catch (Exception ex)
        {
            Status = $"Not applied: {ex.Message}";
        }
    }

    [RelayCommand]
    private void RevertMap()
    {
        if (_map == null) return;
        HasUnappliedChanges = false;
        LoadMap(_map.MapId);
        Status = "Reverted to the map stored in the ROM.";
    }

    [RelayCommand] private void ZoomIn()  => Zoom = Math.Min(6, Zoom * 1.5);
    [RelayCommand] private void ZoomOut() => Zoom = Math.Max(0.25, Zoom / 1.5);
}
