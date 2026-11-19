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

public enum MapTool { Select, Paint, Pick, AddExit, AddArrival, Area, AddTrigger, Fill }

// a rectangle drawn over the map (NPC, exit, arrival point, selection)
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
    // draw a line from (X, Y) by (LineDx, LineDy) instead of a box (exit -> arrival link)
    public bool IsLine { get; init; }
    public bool IsBox => !IsLine;
    public double LineDx { get; init; }
    public double LineDy { get; init; }
    public int Index { get; init; }
}

// one setup-script assignment of a treasure spot, editable from the inspector
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

    // the value word to store: an item (FC/FD high byte) or a gold amount (high byte below FC)
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

public partial class MapEditorViewModel : ObservableObject, IEventHost, LufiaForge.ViewModels.IPendingEdits
{
    private RomBuffer? _rom;
    public RomBuffer? Rom => _rom;
    private MainViewModel? _mainVm;
    private LufiaMap? _map;
    private MapTileset? _tileset;
    private uint[]? _pixels;
    private List<(int x, int y, ushort before)>? _stroke;
    private (string kind, int index)? _selection;
    private (int x, int y)? _dragStart;
    private bool _populating;   // true while we fill the inspector fields from a selection
    private MapLinks? _links;
    private List<SpotContent> _spotContents = new();
    private bool _dragMoved;
    private string? _dragMode;                    // "npc", "exit-move", "exit-resize", "arrival", "new-exit"
    private (int x, int y) _dragAnchor;
    private readonly Dictionary<int, List<MapArrival>> _arrivalCache = new();
    private int _arrivalVersion = -1;
    private readonly List<int> _destIds = new();   // map ids behind DestinationChoices (0 = this map)

    // asks the view to open the destination picker for the selected exit
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

    // asks the view to scroll so this map pixel is in the middle
    public event Action<double, double>? FocusRequested;
    // asks the view to open the event editor
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

    // world map encounter zones (resource B0: one battle group per 4x4 blocks)
    // show (and paint) the world map's encounter zones
    [ObservableProperty] private bool _showZones;
    // the battle group that zone painting puts down (hex text)
    [ObservableProperty] private string _zoneBrushText = "01";
    [ObservableProperty] private WriteableBitmap? _zoneImage;
    [ObservableProperty] private bool _zonesDirty;
    public bool IsWorldMap => _map?.IsWorld == true;
    public double ZoneImageWidth => (_map?.Width ?? 0) * 16;
    public double ZoneImageHeight => (_map?.Height ?? 0) * 16;
    private byte[] _zones = Array.Empty<byte>();
    // the zones as the ROM had them when we read them (Game Data's world map tab writes them too)
    private byte[] _zonesRead = Array.Empty<byte>();
    private const int ZoneCell = 4;   // blocks per zone cell

    private void LoadZones()
    {
        OnPropertyChanged(nameof(IsWorldMap)); OnPropertyChanged(nameof(ZoneImageWidth)); OnPropertyChanged(nameof(ZoneImageHeight));
        ZonesDirty = false;
        if (_rom == null || _map?.IsWorld != true) { ZoneImage = null; _zones = Array.Empty<byte>(); return; }
        try { _zones = Core.Battle.Encounters.ReadZones(_rom); } catch { _zones = Array.Empty<byte>(); }
        _zonesRead = (byte[])_zones.Clone();
        DrawZones();
    }

    private static uint ZoneColour(int group)
    {
        if (group == 0) return 0;
        double hue = (group * 47 % 360) / 60.0; double x = 1 - Math.Abs(hue % 2 - 1);
        (double r, double g, double b) = hue switch
        {
            < 1 => (1.0, x, 0.0), < 2 => (x, 1.0, 0.0), < 3 => (0.0, 1.0, x),
            < 4 => (0.0, x, 1.0), < 5 => (x, 0.0, 1.0), _ => (1.0, 0.0, x),
        };
        return 0x70000000u | (uint)(r * 255) << 16 | (uint)(g * 255) << 8 | (uint)(b * 255);
    }

    private void DrawZones()
    {
        int width = Core.Battle.Encounters.ZoneWidth, height = Core.Battle.Encounters.ZoneHeight;
        if (_zones.Length < width * height) { ZoneImage = null; return; }
        var pixels = new uint[width * height];
        for (int i = 0; i < pixels.Length; i++) pixels[i] = ZoneColour(_zones[i]);
        var bmp = new WriteableBitmap(width, height, 96, 96, PixelFormats.Bgra32, null);
        bmp.WritePixels(new Int32Rect(0, 0, width, height), pixels, width * 4, 0);
        ZoneImage = bmp;
    }

    private int ZoneAt(int x, int y)
    {
        int cellX = x / ZoneCell, cellY = y / ZoneCell, width = Core.Battle.Encounters.ZoneWidth;
        return cellX < width && cellY * width + cellX < _zones.Length ? cellY * width + cellX : -1;
    }

    // paints the zone under the block with the brush group. true if it handled the click
    private bool PaintZone(int x, int y)
    {
        if (!ShowZones || !IsWorldMap) return false;
        int zone = ZoneAt(x, y);
        if (zone < 0) return true;
        if (!int.TryParse(ZoneBrushText, System.Globalization.NumberStyles.HexNumber, null, out int group) || group < 0 || group > 255)
        { Status = "Zone brush: a battle group number in hex (00 = no battles)."; return true; }
        if (_zones[zone] != group) { _zones[zone] = (byte)group; ZonesDirty = true; DrawZones(); }
        return true;
    }

    // custom graphics: a picture into the tileset's blocks

    // the Block editor for this map's tileset (or the world map's blocks)
    [RelayCommand]
    private void EditBlocks()
    {
        if (_rom == null || _map == null) { Status = "Open a map first."; return; }
        if (!WritePendingFirst()) return;
        try
        {
            var set = BlockSet.FromMap(_rom, _map);
            var window = new BlockEditorWindow(_rom, set, Brush) { Owner = Application.Current.MainWindow };
            window.ShowDialog();
            if (!window.Wrote) return;
            int id = _map.MapId;
            LoadMap(id);
            _mainVm?.NotifyRomModified(_map?.IsWorld == true ? "World map blocks" : $"Tileset {_map?.Tileset} blocks");
            Status = "Blocks written. Every map using this tileset shows the change.";
        }
        catch (Exception ex) { Status = "Block editor: " + ex.Message; }
    }

    // redraw blocks from a picture: picture block k goes to block Brush + k
    [RelayCommand]
    private void ImportOverBlocks() => ImportPicture(addNew: false);

    // add the picture as new blocks at the end of the tileset
    [RelayCommand]
    private void ImportNewBlocks() => ImportPicture(addNew: true);

    private void ImportPicture(bool addNew)
    {
        if (_rom == null || _map == null || _tileset == null || _map.IsWorld)
        { Status = "Open a town or dungeon map first (the world map's graphics work differently)."; return; }
        if (!WritePendingFirst()) return;
        var dlg = new Microsoft.Win32.OpenFileDialog { Title = "Picture to import (multiple of 16 x 16 pixels)", Filter = "Pictures (*.png;*.bmp;*.gif)|*.png;*.bmp;*.gif" };
        if (dlg.ShowDialog() != true) return;
        uint[] pixels; int width, height;
        try
        {
            var picture = new BitmapImage(new Uri(dlg.FileName));
            var bgra = new FormatConvertedBitmap(picture, PixelFormats.Bgra32, null, 0);
            width = bgra.PixelWidth; height = bgra.PixelHeight; pixels = new uint[width * height];
            bgra.CopyPixels(pixels, width * 4, 0);
        }
        catch (Exception ex) { Status = "Couldn't read the picture: " + ex.Message; return; }
        if (width % 16 != 0 || height % 16 != 0) { Status = $"The picture is {width}x{height}: it must be a multiple of 16 x 16 pixels (one block each)."; return; }
        int count = width / 16 * (height / 16);
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
            var drawResult = import.Draw(pixels, width, height, targets, attr);
            if (import.NeedsMoreSpace() && _rom.Length < MapWriter.ExpandedSize && !ConfirmExpand("The tileset's graphics or blocks no longer fit in their original space.")) return;
            string where = import.Save(allowExpand: true);
            int id = _map.MapId, firstNew = addNew ? import.BlockCount : Brush;
            LoadMap(id);
            if (addNew) Brush = Math.Min(firstNew, (_tileset?.MetatileCount ?? 1) - 1);
            _mainVm?.NotifyRomModified($"Tileset {_map?.Tileset} graphics");
            Status = drawResult.Report + " " + where + (addNew ? $" New blocks start at {firstNew:X3} (new blocks copy the walkability of the brush block)." : "");
        }
        catch (Exception ex) { Status = "Import failed: " + ex.Message; }
    }

    [RelayCommand]
    private void WriteZones()
    {
        if (_rom == null || !IsWorldMap || _zones.Length == 0) return;
        try
        {
            if (!Core.Battle.Encounters.ReadZones(_rom).AsSpan().SequenceEqual(_zonesRead))
            { Status = "The encounter zones were changed on another page since they were read here: Revert the map to read them again, then paint again."; return; }
            bool alreadyExpanded = _rom.Length >= MapWriter.ExpandedSize;
            var size = LufiaCompression.Compress(_zones).Length;
            int slot = Core.ResourceWriter.SlotSize(_rom, Core.Battle.Encounters.ZoneResource);
            if (size > slot && !alreadyExpanded && !ConfirmExpand("The edited encounter zones don't fit in their original space.")) return;
            var result = Core.Battle.Encounters.WriteZones(_rom, _zones, allowExpand: true);
            _zonesRead = (byte[])_zones.Clone();
            ZonesDirty = false;
            _mainVm?.NotifyRomModified("World map encounter zones");
            Status = result.Moved ? $"Encounter zones written at 0x{result.FileOffset:X6} (moved; {result.CompressedSize} bytes)." : $"Encounter zones written in place ({result.CompressedSize} of {result.SlotSize} bytes).";
        }
        catch (Exception ex) { Status = "Couldn't write the zones: " + ex.Message; }
    }
    // text for the floating tooltip over the map (what's under the mouse)
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

    // editable fields of the current selection
    [ObservableProperty] private bool _isExitSelected;
    [ObservableProperty] private bool _isNpcSelected;
    [ObservableProperty] private int _exitDestMap;
    [ObservableProperty] private int _exitArrival;
    [ObservableProperty] private string _exitDestMapText = "";
    [ObservableProperty] private int _npcX;
    [ObservableProperty] private int _npcY;

    private const string SelectHint =
        "Click something on the map to edit it here: a character (yellow), exit (red), arrival point (cyan) or treasure " +
        "(gold = chest, pink = hidden item).";

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
        OnPropertyChanged(nameof(IsAreaTool));
        OnPropertyChanged(nameof(IsAddTriggerTool));
        OnPropertyChanged(nameof(IsFillTool));
        OnPropertyChanged(nameof(ToolHint));
        if (value != MapTool.Paint) BrushPreviewVisible = false;
        if (value != MapTool.Paint) CancelStamp();
        if (value == MapTool.Area) Status = "Area: drag a rectangle of blocks, then Copy / Cut / Fill (or Ctrl+C, Ctrl+X, Del). Paste (Ctrl+V) stamps copied blocks.";
        if (value == MapTool.AddExit) Status = "Add exit: drag a rectangle on the map where the party should leave.";
        if (value == MapTool.AddArrival) Status = "Add arrival point: click where the party should appear.";
    }

    public string BrushText => _tileset == null ? "" :
        $"Brush: metatile {Brush:X3}  (attribute {_tileset.Attribute(Brush):X2}){CompositeText(Brush)}";

    private string CompositeText(int metatile) =>
        _tileset?.CompositeParts(metatile) is var (ground, overlay) ? $"   = ground {ground:X3} + overlay {overlay:X3}" : "";
    partial void OnBrushChanged(int value)
    {
        if (!_turning) _turnBase = -1;   // brush picked by hand, so the next Alt + wheel turns this block
        RefreshBrushPreview();
        BrushHelpers();
        OnPropertyChanged(nameof(BrushText));
        OnPropertyChanged(nameof(BrushBoxX));
        OnPropertyChanged(nameof(BrushBoxY));
        OnPropertyChanged(nameof(PaletteBoxX));
        OnPropertyChanged(nameof(PaletteBoxY));
    }

    // where the brush's block sits in the palette image (16 blocks per row), for its highlight box. that's its place
    // in the chosen order, off the palette if it isn't listed
    private int BrushCell => _srcTileset != null ? _srcPick : _paletteOrder == null ? Brush : Array.IndexOf(_paletteOrder, Brush);
    public double BrushBoxX => BrushCell < 0 ? -100 : BrushCell % 16 * 16;
    public double BrushBoxY => BrushCell < 0 ? -100 : BrushCell / 16 * 16;

    // zoom of the block palette on the right
    [ObservableProperty] private double _paletteZoom = 2.0;

    public void SetRom(RomBuffer rom, MainViewModel mainVm)
    {
        // the same ROM again (undo / redo): stay on the map that was open
        int? keepMap = ReferenceEquals(rom, _rom) ? SelectedMap?.MapId : null;
        // edits of another ROM's map are never written into this one
        if (!ReferenceEquals(rom, _rom)) HasUnappliedChanges = false;
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
        _rereading = keepMap != null;
        try { SelectedMap = Maps.FirstOrDefault(m => m.MapId == (keepMap ?? 0x04)) ?? Maps.FirstOrDefault(); }
        finally { _rereading = false; }
    }

    private bool _revertingSelection;
    // the tabs are reading the ROM again (after Undo / Redo, a patch), so the open map isn't written from here
    private bool _rereading;

    partial void OnSelectedMapChanged(MapInfo? oldValue, MapInfo? newValue)
    {
        if (_revertingSelection || newValue == null || _rom == null) return;
        if (_rereading && _map != null && newValue.MapId == _map.MapId)
        {
            // Undo writes the tabs' edits first. edits still unwritten here (they couldn't be) stay, and never get written
            // over the ROM as it is now from a map read before it (Apply refuses once the map changed underneath them)
            if (!HasUnappliedChanges) LoadMap(newValue.MapId);
            else if (ChangedElsewhere()) Status = $"Map {_map.MapId:X2} was changed (Undo, a patch) while its edits here weren't written: Revert to read it again.";
            _populatingName = true;
            MapNameText = newValue.Name;
            _populatingName = false;
            return;
        }
        // the map's edits (and painted world map battle zones) go into the project on their own, we only ask if they
        // can't be written. (zones painted without Write zones used to get dropped here without a word, LoadMap reads them again)
        if (HasPendingEdits && WritePendingEdits() is string flushError &&
            MessageBox.Show($"This map's edits couldn't be written ({flushError}). Discard them?", "Unsaved map edits",
                MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes)
        {
            // keep showing the edited map and put the list selection back
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

    // the open map's data as we read it and where it was, so we can tell if something else wrote it since
    // (another tab, Undo, a patch). its edits here would put the older map back over that
    private byte[]? _loadedData;
    private int _loadedOffset = -1, _loadedVersion = -1;

    private void RememberLoaded()
    {
        if (_rom == null || _map == null) return;
        _loadedData = (byte[])_map.Original.Clone();
        _loadedOffset = _map.CompressedOffset;
        _loadedVersion = _rom.Version;
    }

    // true if the ROM's copy of the open map differs from the one we read
    private bool ChangedElsewhere()
    {
        if (_rom == null || _map == null || _loadedData == null || _rom.Version == _loadedVersion) return false;
        try
        {
            var current = LufiaMap.Load(_rom, _map.MapId);
            bool changed = current.CompressedOffset != _loadedOffset || !current.Original.AsSpan().SequenceEqual(_loadedData);
            if (!changed) _loadedVersion = _rom.Version;   // it was other bytes that changed
            return changed;
        }
        catch { return true; }
    }

    private const string ChangedElsewhereText = "this map was changed somewhere else since it was opened here (another tab, Undo or a patch), " +
                                                "so these edits would put the older map back over it. Revert to read it again, then make them again";

    // the tab is shown again. if another tab, Undo or a patch changed the open map in the meantime, read it again
    // (with edits not written yet it stays, and Apply refuses to write them over the newer map)
    public void RefreshIfStale()
    {
        if (_map == null || !ChangedElsewhere()) return;
        if (HasUnappliedChanges) { Status = "Not applied: " + ChangedElsewhereText + "."; return; }
        var selected = _selection;
        LoadMap(_map.MapId);
        Reselect(selected);
        Status = $"Map {_map?.MapId:X2} was read again: it was changed somewhere else meanwhile.";
    }

    private void LoadMap(int mapId)
    {
        if (_rom == null) return;
        ClearSelection();
        CancelStamp();
        ClearAreaSelection();
        HasUnappliedChanges = false;
        try
        {
            _statsTileset = -999;   // count again, maps might have changed since
            _map = LufiaMap.Load(_rom, mapId);
            _spotContents = MapSetupScript.ReadSpots(_rom, mapId);
            _tileset = MapTileset.ForMap(_rom, _map);
            int recentKey = _map.IsWorld ? -1 : _map.Tileset;
            if (recentKey != _recentKey) { RecentBrushes.Clear(); _recentKey = recentKey; }
            _pixels = _tileset.RenderMap(_map);
            int widthPx = _map.Width * 16, heightPx = _map.Height * 16;
            var bmp = new WriteableBitmap(widthPx, heightPx, 96, 96, PixelFormats.Bgra32, null);
            bmp.WritePixels(new Int32Rect(0, 0, widthPx, heightPx), _pixels, widthPx * 4, 0);
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
            RefreshMapSettings();
            StartHistory();
            RememberLoaded();
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
        RefreshBrushPreview();
        if (_tileset == null) return;
        // the blocks of other graphics (From) in their tileset's order, or this map's
        var shown = _srcTileset ?? _tileset;
        _paletteOrder = _srcTileset != null ? null : PaletteOrder();
        int count = _paletteOrder?.Length ?? shown.MetatileCount, cols = 16, rows = (count + cols - 1) / cols;
        int widthPx = cols * 16, heightPx = Math.Max(1, rows) * 16;
        var pixels = new uint[widthPx * heightPx];
        for (int i = 0; i < count; i++) shown.DrawMetatile(pixels, widthPx, (i % cols) * 16, (i / cols) * 16, _paletteOrder?[i] ?? i);
        var bmp = new WriteableBitmap(widthPx, heightPx, 96, 96, PixelFormats.Bgra32, null);
        bmp.WritePixels(new Int32Rect(0, 0, widthPx, heightPx), pixels, widthPx * 4, 0);
        PaletteImage = bmp;
        BrushHelpers();
    }

    partial void OnShowNpcsChanged(bool value) => RebuildOverlays();
    partial void OnShowExitsChanged(bool value) => RebuildOverlays();
    partial void OnShowArrivalsChanged(bool value) => RebuildOverlays();
    partial void OnShowItemsChanged(bool value) => RebuildOverlays();

    // treasure and exit/arrival links

    // setup-script assignments for a section E spot (several when it depends on story progress)
    private List<SpotContent> ContentsOf(int spot) => _spotContents.Where(c => c.Spot == spot).ToList();

    private bool IsTreasureSpot(MapSpot spot) => _spotContents.Any(c => c.Spot == spot.Index && c.IsTreasure);

    private string SpotText(MapSpot spot)
    {
        if (_rom == null) return "";
        var contents = ContentsOf(spot.Index);
        if (contents.Count == 0) return $"Spot {spot.Index} (nothing assigned)";
        var parts = contents.Select(x => x.Describe(_rom)).Distinct().ToList();
        return parts.Count == 1 ? parts[0] : string.Join("\n  or ", parts) + "\n  (depends on story progress)";
    }

    private string MapName(int id) =>
        Maps.FirstOrDefault(m => m.MapId == id) is { } info && !string.IsNullOrEmpty(info.Name)
            ? $"{info.Name} (map {id:X2})"
            : id == LufiaMap.WorldMap ? "World map" : $"map {id:X2}";

    // map names

    private Dictionary<int, string> _generatedNames = new();
    [ObservableProperty] private string _mapNameText = "";

    // renames the selected map (empty = back to the generated name), kept in the open project
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
        int keepIndex = DestinationIndex;
        _populating = true;
        BuildDestinationChoices();
        DestinationIndex = keepIndex;
        _populating = false;
        RebuildOverlays();
    }
    private bool _populatingName;

    // exits leading to arrival point arrival of the current map (with the live edits for this map)
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

    private bool LeadsHere(MapExit exit) => _map != null && (exit.DestMap == 0 || exit.DestMap == _map.MapId);

    private string ExitTargetText(MapExit exit) =>
        LeadsHere(exit) ? $"arrival point A{exit.Arrival} on this map" : $"{MapName(exit.DestMap)}, arrival point A{exit.Arrival}";

    private string SourceText(MapLinks.Source source) =>
        _map != null && source.MapId == _map.MapId ? $"exit E{source.Exit} on this map" : $"{MapName(source.MapId)}, exit E{source.Exit}";

    private string ArrivalLabel(int arrival)
    {
        if (_map == null) return $"A{arrival}";
        var incoming = IncomingExits(arrival);
        var local = incoming.Where(s => s.MapId == _map.MapId).Select(s => $"E{s.Exit}").ToList();
        var others = incoming.Where(s => s.MapId != _map.MapId).ToList();
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
                var target = _map.Arrivals[e.Arrival];
                double fromX = (e.X1 + Math.Max(1, e.X2 - e.X1) / 2.0) * 16, fromY = (e.Y1 + Math.Max(1, e.Y2 - e.Y1) / 2.0) * 16;
                double toX = target.X * 16 + 8, toY = target.Y * 16 + 8;
                if (Math.Abs(toX - fromX) + Math.Abs(toY - fromY) < 4) continue;
                Overlays.Add(new MapOverlay
                {
                    Kind = "link", IsLine = true, X = fromX, Y = fromY, LineDx = toX - fromX, LineDy = toY - fromY,
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
        AddTriggerOverlays();
        if (_selection is { } sel)
        {
            var selectedBox = Overlays.FirstOrDefault(x => x.Kind == sel.kind && x.Index == sel.index);
            if (selectedBox != null)
                Overlays.Add(new MapOverlay { Kind = "sel", X = selectedBox.X - 3, Y = selectedBox.Y - 3, W = selectedBox.W + 6, H = selectedBox.H + 6, Stroke = Brushes.White, Thickness = 2 });

            // outline the other end of an exit/arrival link in green if it's on this map
            var linked = new List<MapOverlay>();
            if (sel.kind == "exit" && LeadsHere(_map.Exits[sel.index]))
                linked.AddRange(Overlays.Where(x => x.Kind == "arrival" && x.Index == _map.Exits[sel.index].Arrival));
            if (sel.kind == "arrival")
                foreach (var src in IncomingExits(sel.index).Where(src => src.MapId == _map.MapId))
                    linked.AddRange(Overlays.Where(x => x.Kind == "exit" && x.Index == src.Exit));
            foreach (var box in linked)
                Overlays.Add(new MapOverlay { Kind = "link", X = box.X - 4, Y = box.Y - 4, W = box.W + 8, H = box.H + 8, Stroke = Brushes.Lime, Thickness = 2 });
        }
    }

    // mouse input from the view (coordinates in map pixels)

    public void OnMouseMove(double px, double py, bool leftDown)
    {
        if (_map == null || _tileset == null) return;
        int x = (int)(px / 16), y = (int)(py / 16);
        if (x < 0 || y < 0 || x >= _map.Width || y >= _map.Height) { HoverText = ""; HoverTip = ""; BrushPreviewVisible = false; return; }
        CameraFollow(x, y);
        BrushPreviewFollow(x, y);
        int metatile = _map.GetTile(x, y) & 0x3FF;
        HoverText = $"Block ({x}, {y})   metatile {metatile:X3}   attribute {_tileset.Attribute(metatile):X2}{CompositeText(metatile)}" +
                    (ShowZones && IsWorldMap && ZoneAt(x, y) is >= 0 and var zone ? $"   battle group {_zones[zone]:X2}" : "");
        if (_stamp != null) { StampHover(x, y); HoverTip = ""; return; }
        if (leftDown && Tool == MapTool.Area) { AreaDrag(x, y); return; }
        if (leftDown && PaintZone(x, y)) return;
        HoverTip = leftDown ? "" : DescribeAt(x, y);

        if (!leftDown) return;
        if (_picking) { Brush = metatile; return; }   // Ctrl held, keep sampling the block under the cursor
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
                case "trigger-move": DragTrigger(x, y, resize: false); break;
                case "trigger-resize": case "new-trigger": DragTrigger(x, y, resize: true); break;
            }
        }
    }

    public void OnMouseLeave() { HoverTip = ""; BrushPreviewVisible = false; }

    // tooltip text for everything on a block: treasure, exits, arrival points, characters
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
                var incoming = IncomingExits(a.Index);
                lines.Add($"Arrival point A{a.Index}" + (incoming.Count == 0 ? " (no exit leads here)"
                    : " ← " + string.Join("; ", incoming.Take(4).Select(SourceText)) + (incoming.Count > 4 ? $"; +{incoming.Count - 4} more" : "")));
            }
        if (ShowNpcs)
            foreach (var n in _map.Npcs.Where(n => !n.IsUnused && n.X == x && n.Y == y))
                lines.Add($"Character {n.Index + 1} (talking runs event {MapEvents.TalkEvent(n.Index)})");
        return string.Join("\n", lines);
    }

    private static bool Inside(int x, int y, int x1, int y1, int x2, int y2) =>
        x >= x1 && x < Math.Max(x2, x1 + 1) && y >= y1 && y < Math.Max(y2, y1 + 1);

    public void OnMouseDown(double px, double py, bool ctrl = false, bool shift = false)
    {
        if (_map == null) return;
        _inGesture = true;
        int x = (int)(px / 16), y = (int)(py / 16);
        if (x < 0 || y < 0 || x >= _map.Width || y >= _map.Height) return;
        if (ShowZones && IsWorldMap)
        {
            if (ctrl && ZoneAt(x, y) is >= 0 and var zone) { ZoneBrushText = _zones[zone].ToString("X2"); Status = $"Zone brush: group {ZoneBrushText}."; return; }
            if (PaintZone(x, y)) return;
        }
        if (_stamp != null) { StampAt(x, y); return; }
        if (ctrl)
        {
            _picking = true;   // eyedropper, no painting until the button is let go
            Brush = _map.GetTile(x, y) & 0x3FF;
            Status = $"Picked block {Brush:X3} from ({x}, {y}). Paint with it, or Ctrl + click another block.";
            if (Tool is not (MapTool.Paint or MapTool.Fill)) Tool = MapTool.Paint;
            return;
        }
        switch (Tool)
        {
            case MapTool.Area:
                AreaDown(x, y);
                break;
            case MapTool.Pick:
                Brush = _map.GetTile(x, y) & 0x3FF;
                Tool = MapTool.Paint;
                Status = $"Picked metatile {Brush:X3}. Paint tool selected.";
                break;
            case MapTool.Paint:
                _stroke = new();
                PaintAt(x, y);
                break;
            case MapTool.Fill:
                FillAt(x, y, shift);
                break;
            case MapTool.AddExit:
            {
                var exit = _map.AddExit(x, y, x + 1, y + 1, 0, 0);
                MarkEdited();
                SelectExit(exit, focusLinked: false);
                _dragStart = (x, y); _dragAnchor = (x, y); _dragMode = "new-exit"; _dragMoved = false;
                break;
            }
            case MapTool.AddTrigger:
                AddTriggerAt(x, y);
                break;
            case MapTool.AddArrival:
            {
                var arrival = _map.AddArrival(x, y);
                MarkEdited();
                Tool = MapTool.Select;
                SelectArrival(arrival, focusLinked: false);
                Status = $"Added arrival point A{arrival.Index} at ({x}, {y}). Exits can now lead here.";
                break;
            }
            default:
                SelectAt(x, y);
                _dragMoved = false;
                _dragMode = null;
                _dragStart = null;
                if (_selection is { kind: "npc" }) { _dragMode = "npc"; _dragStart = (x, y); }
                else if (_selection is { kind: "arrival" }) { _dragMode = "arrival"; _dragStart = (x, y); }
                else if (_selection is { kind: "trigger" }) StartTriggerDrag(x, y);
                else if (_selection is { kind: "exit" } selectedExit)
                {
                    var exit = _map.Exits[selectedExit.index];
                    bool corner = x == Math.Max(exit.X2, exit.X1 + 1) - 1 && y == Math.Max(exit.Y2, exit.Y1 + 1) - 1 &&
                                  (exit.X2 - exit.X1 > 1 || exit.Y2 - exit.Y1 > 1);
                    _dragMode = corner ? "exit-resize" : "exit-move";
                    _dragStart = (x, y);
                    _dragAnchor = corner ? (exit.X1, exit.Y1) : (x - exit.X1, y - exit.Y1);
                }
                break;
        }
    }

    private bool _picking;

    // a mouse stroke (paint, drag) is going on
    public bool InGesture => _inGesture;

    public void OnMouseUp()
    {
        AreaUp();
        _picking = false;
        _stroke = null;
        _inGesture = false;
        Checkpoint();
        bool clickedNpc = _dragStart != null && !_dragMoved && _dragMode == "npc";
        if (_dragMode is "new-trigger" or "trigger-move" or "trigger-resize" && _selection is { kind: "trigger" } selectedTrigger && _map != null && selectedTrigger.index < _map.Triggers.Count)
            SelectTrigger(_map.Triggers[selectedTrigger.index]);   // updates its size and position in the panel
        if (_dragMode == "new-trigger")
        {
            Tool = MapTool.Select;
            Status = "New step-on area added. Writing the map (by itself, or Apply in the top bar) switches it on and makes its event; then Event... (on the right) opens it.";
        }
        if (_dragMode == "new-exit")
        {
            Tool = MapTool.Select;
            Status = "New exit added. Now choose where it leads: pick a destination map and arrival point, or use \"Pick on map…\".";
        }
        _dragStart = null;
        _dragMode = null;
        if (clickedNpc) OpenEventEditor();
    }

    // double-click: follow an exit to its destination map, or an arrival back to an exit that leads to it
    public void OnDoubleClick(double px, double py)
    {
        if (_map == null || Tool != MapTool.Select) return;
        if (_selection is { kind: "exit" }) { GoToDestination(); return; }
        if (_selection is { kind: "arrival" } sel)
        {
            var sources = IncomingExits(sel.index).Where(s => s.MapId != _map.MapId && s.MapId != LufiaMap.WorldMap).ToList();
            if (sources.Count == 0) { Status = "No exit on another map (that the editor can show) leads here."; return; }
            var source = sources[_focusCycle++ % sources.Count];
            var info = Maps.FirstOrDefault(m => m.MapId == source.MapId);
            if (info == null) return;
            SelectedMap = info;
            if (_map?.MapId == source.MapId && source.Exit < _map.Exits.Count)
            {
                SelectExit(_map.Exits[source.Exit], focusLinked: false);
                FocusOn(_map.Exits[source.Exit]);
                Status = $"Exit E{source.Exit} of map {source.MapId:X2} leads to that arrival point." +
                         (sources.Count > 1 ? " Double-click the arrival point again to see the next one." : "");
            }
        }
    }

    [RelayCommand]
    private void OpenEventEditor()
    {
        if (_rom == null || _map == null || _selection is not { kind: "npc" } sel) return;
        // write pending map edits first, the event editor's stage can write this map's walks
        if (!WritePendingFirst()) return;
        int eventNumber = MapEvents.TalkEvent(sel.index);
        var script = EventScript.Load(_rom, _map.MapId, eventNumber);
        if (script == null)
        {
            if (!CreateTalkEvent(sel.index)) return;
            script = EventScript.Load(_rom, _map.MapId, eventNumber);
            if (script == null) { Status = $"Character {sel.index + 1}'s event couldn't be made."; return; }
        }
        EventEditorRequested?.Invoke(script, $"Map {_map.MapId:X2} - character {sel.index + 1} (talking runs event {eventNumber})");
    }

    // tells the user an edit is in the ROM now (in memory), where it went, and that it still has to be saved
    public void ConfirmWritten(string heading, string details)
    {
        Modules.Common.InfoDialog.Show("Saved in the project", "✔ " + heading,
            details + "\n\nIt's in your project now: saving the project keeps it (Ctrl+S, or on its own every 5 minutes), and File > Export ROM writes a playable ROM.",
            path: null, pathLabel: null,
            primaryText: _mainVm != null ? "💾 Save project now" : null, primary: () => _mainVm?.SaveNow());
    }

    // the event editor calls this after it wrote to the ROM
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

    private void FocusOn(MapExit exit) =>
        FocusRequested?.Invoke((exit.X1 + Math.Max(1, exit.X2 - exit.X1) / 2.0) * 16, (exit.Y1 + Math.Max(1, exit.Y2 - exit.Y1) / 2.0) * 16);

    private void FocusOn(MapArrival arrival) => FocusRequested?.Invoke(arrival.X * 16 + 8, arrival.Y * 16 + 8);

    public void PickBrushFromPalette(double px, double py)
    {
        if (_tileset == null) return;
        int block = (int)(py / 16) * 16 + (int)(px / 16);
        if (_paletteOrder != null) { if (px < 0 || px >= 256 || block < 0 || block >= _paletteOrder.Length) return; block = _paletteOrder[block]; }
        if (block < 0 || block >= _tileset.MetatileCount) return;
        Brush = block;
        if (Tool != MapTool.Fill) Tool = MapTool.Paint;
    }

    // paints the brush on (x, y), or the BrushSize square around it
    private void PaintAt(int x, int y)
    {
        int radius = BrushSize / 2;
        for (int yy = y - radius; yy <= y + radius; yy++)
            for (int xx = x - radius; xx <= x + radius; xx++)
                PaintCell(xx, yy);
    }

    private void PaintCell(int x, int y)
    {
        if (_map == null || _tileset == null || MapImage == null || _pixels == null) return;
        if (x < 0 || y < 0 || x >= _map.Width || y >= _map.Height) return;
        ushort before = _map.GetTile(x, y);
        if ((before & 0x3FF) == Brush) return;
        _stroke ??= new();
        _stroke.Add((x, y, before));
        _map.SetTile(x, y, Brush);
        RedrawBlock(x, y);
        MarkEdited();
    }

    private void RedrawBlock(int x, int y)
    {
        if (_map == null || _tileset == null || MapImage == null) return;
        var pixels = _tileset.MetatilePixels(_map.GetTile(x, y));
        MapImage.WritePixels(new Int32Rect(x * 16, y * 16, 16, 16), pixels, 64, 0);
    }



    // selection

    private (int X, int Y)? _lastClick;

    private void SelectAt(int x, int y)
    {
        if (_map == null) return;
        // everything on this block in click order, clicking the same block again picks the next one
        var here = new List<(string Kind, int Index)>();
        if (ShowNpcs) here.AddRange(_map.Npcs.Where(n => !n.IsUnused && n.X == x && n.Y == y).Reverse().Select(n => ("npc", n.Index)));
        if (ShowArrivals) here.AddRange(_map.Arrivals.Where(a => a.X == x && a.Y == y).Reverse().Select(a => ("arrival", a.Index)));
        if (ShowItems) here.AddRange(_map.Spots.Where(s => IsTreasureSpot(s) && Inside(x, y, s.X1, s.Y1, s.X2, s.Y2)).Reverse().Select(s => ("spot", s.Index)));
        if (ShowExits) here.AddRange(_map.Exits.Where(e => !e.IsUnused && Inside(x, y, e.X1, e.Y1, e.X2, e.Y2)).Reverse().Select(e => ("exit", e.Index)));
        here.AddRange(TriggersAt(x, y));
        if (here.Count == 0) { _lastClick = (x, y); ClearSelection(); RebuildOverlays(); return; }
        int pick = 0;
        if (_lastClick == (x, y) && _selection is { } current)
        {
            int currentIndex = here.IndexOf((current.kind, current.index));
            if (currentIndex >= 0) pick = (currentIndex + 1) % here.Count;
        }
        _lastClick = (x, y);
        var (kind, index) = here[pick];
        switch (kind)
        {
            case "npc": SelectNpc(_map.Npcs[index]); break;
            case "arrival": SelectArrival(_map.Arrivals[index]); break;
            case "spot": SelectSpot(_map.Spots[index]); break;
            case "trigger": SelectTrigger(_map.Triggers[index]); break;
            default: SelectExit(_map.Exits[index]); break;
        }
        if (here.Count > 1) Status = $"{here.Count} things on this block; click it again for the next one ({pick + 1} of {here.Count}).";
    }

    private void ClearSelection()
    {
        _selection = null;
        IsExitSelected = IsNpcSelected = IsSpotSelected = IsArrivalSelected = IsTriggerSelected = false;
        SpotEdits.Clear();
        SelectionTitle = "Nothing selected";
        SelectionDetails = SelectHint;
        DialoguePreview = "";
    }

    private void SelectNpc(MapNpc npc)
    {
        if (_map == null || _rom == null) return;
        _selection = ("npc", npc.Index);
        IsNpcSelected = true; IsExitSelected = IsSpotSelected = IsArrivalSelected = false;
        SpotEdits.Clear();
        _populating = true;
        NpcX = npc.X; NpcY = npc.Y;
        NpcSprite = npc.IsUnused ? -1 : npc.Sprite;
        _populating = false;
        int scriptOffset = LufiaMap.EventScriptOffset(_rom, _map.MapId, npc.Index);
        SelectionTitle = $"Character {npc.Index + 1}";
        SelectionDetails =
            $"Sprite {npc.Sprite:X2}   flags {npc.Flags:X2}\n" +
            $"Position ({npc.X}, {npc.Y})   walking area ({npc.BoxX1},{npc.BoxY1})-({npc.BoxX2},{npc.BoxY2})\n" +
            $"Talking runs event {npc.Index} of map {_map.MapId:X2}" +
            (scriptOffset >= 0 ? $": script at 0x{scriptOffset:X6}" : " (no script)");
        DialoguePreview = scriptOffset >= 0 ? FirstLine(scriptOffset) : "";
        RebuildOverlays();
    }

    private void SelectExit(MapExit exit, bool focusLinked = true)
    {
        if (_map == null) return;
        _selection = ("exit", exit.Index);
        IsExitSelected = true; IsNpcSelected = IsSpotSelected = false;
        SpotEdits.Clear();
        IsArrivalSelected = false;
        _populating = true;
        ExitDestMap = exit.DestMap; ExitArrival = exit.Arrival;
        ExitDestMapText = exit.DestMap.ToString("X2");
        ExitX = exit.X1; ExitY = exit.Y1; ExitW = Math.Max(1, exit.X2 - exit.X1); ExitH = Math.Max(1, exit.Y2 - exit.Y1);
        DestinationIndex = DestIndexFor(exit.DestMap);
        FillArrivalChoices(exit.DestMap == 0 ? _map.MapId : exit.DestMap);
        ArrivalIndex = exit.Arrival < ArrivalChoices.Count ? exit.Arrival : -1;
        _populating = false;
        SelectionTitle = $"Exit {exit.Index}";
        SelectionDetails =
            $"Area ({exit.X1},{exit.Y1}) to ({exit.X2 - 1},{exit.Y2 - 1})   type {exit.Flags >> 4:X}\n" +
            $"Leads to {ExitTargetText(exit)}" +
            (LeadsHere(exit) ? "\n(the linked arrival point is outlined in green)" : "\nDouble-click the exit to go there.");
        DialoguePreview = "";
        RebuildOverlays();
        if (focusLinked && LeadsHere(exit) && exit.Arrival < _map.Arrivals.Count) FocusOn(_map.Arrivals[exit.Arrival]);
    }

    private void SelectArrival(MapArrival arrival, bool focusLinked = true)
    {
        _selection = ("arrival", arrival.Index);
        IsExitSelected = IsNpcSelected = IsSpotSelected = false;
        IsArrivalSelected = true;
        CanDeleteArrival = _map?.CanRemoveArrival(arrival) == true;
        _populating = true;
        ArrivalX = arrival.X; ArrivalY = arrival.Y;
        _populating = false;
        SpotEdits.Clear();
        SelectionTitle = $"Arrival point {arrival.Index}";
        var incoming = IncomingExits(arrival.Index);
        SelectionDetails = $"Position ({arrival.X}, {arrival.Y})   flags {arrival.Flags:X4}\n" +
            (incoming.Count == 0
                ? "No exit in the game leads here (it may be used by a cutscene or warp)."
                : "The party arrives here from:\n  " + string.Join("\n  ", incoming.Select(SourceText)) +
                  (incoming.Any(src => src.MapId == _map?.MapId) ? "\n(linked exits on this map are outlined in green)" : "") +
                  (incoming.Any(src => src.MapId != _map?.MapId) ? "\nDouble-click the arrival point to go to an exit on another map." : ""));
        DialoguePreview = "";
        RebuildOverlays();
        if (focusLinked && _map != null)
        {
            var local = incoming.Where(src => src.MapId == _map.MapId && src.Exit < _map.Exits.Count).ToList();
            if (local.Count > 0) FocusOn(_map.Exits[local[_focusCycle++ % local.Count].Exit]);
        }
    }

    private void SelectSpot(MapSpot spot)
    {
        if (_map == null || _rom == null) return;
        _selection = ("spot", spot.Index);
        IsExitSelected = IsNpcSelected = IsArrivalSelected = false;
        IsSpotSelected = true;
        var contents = ContentsOf(spot.Index);
        bool hidden = contents.Where(x => x.IsTreasure).All(x => x.Kind == SpotKind.Hidden);
        SelectionTitle = hidden ? $"Hidden item (spot {spot.Index})" : $"Treasure chest (spot {spot.Index})";
        SelectionDetails =
            $"Position ({spot.X1}, {spot.Y1})\n" + SpotText(spot) + "\n\n" +
            string.Join("\n", contents.Select(x =>
                $"Setup script 0x{x.ScriptOffset:X6}: value {x.Value:X4}, opened flag {x.Flag:X2}" +
                (x.Conditional ? " (after a story check)" : "")));
        DialoguePreview = "";
        SpotEdits.Clear();
        SpotWrittenText = "";
        for (int i = 0; i < contents.Count; i++)
            if (contents[i].IsTreasure)
                SpotEdits.Add(new SpotEditVm(contents[i], contents.Count > 1 ? $"Contents ({(i == 0 ? "earlier in the story" : "later in the story")})" : "Contents",
                    WriteSpot));
        RebuildOverlays();
    }

    // writes a chest / hidden item change straight into the map's setup script in the ROM
    private void WriteSpot(SpotEditVm edit)
    {
        if (_rom == null || _map == null) return;
        int value = edit.NewValue;
        if (value == edit.Content.Value) return;
        // use the setup script as the ROM has it now. switching an area on (its event opened) or another tab moves it,
        // and then the offset read when the map was opened pointed at freed space or 2 bytes off. so we find the same
        // record (spot, flag, value) again, and if it isn't there any more nothing gets written
        var current = MapSetupScript.ReadSpots(_rom, _map.MapId);
        var same = current.Where(x => x.Spot == edit.Content.Spot && x.Flag == edit.Content.Flag && x.Value == edit.Content.Value).ToList();
        var target = same.FirstOrDefault(x => x.ScriptOffset == edit.Content.ScriptOffset) ?? (same.Count == 1 ? same[0] : null);
        if (target == null)
        {
            _spotContents = current;
            Status = "The map's setup script changed since this spot was shown: pick the spot again, then change it.";
            return;
        }
        _rom.WriteByte(target.ScriptOffset + 1, (byte)value);
        _rom.WriteByte(target.ScriptOffset + 2, (byte)(value >> 8));
        _spotContents = MapSetupScript.ReadSpots(_rom, _map.MapId);
        var updated = _spotContents.FirstOrDefault(x => x.ScriptOffset == target.ScriptOffset);
        if (updated != null) edit.Refresh(updated);
        if (_selection is { kind: "spot" } sel)
        {
            var spot = _map.Spots[sel.index];
            SelectionDetails = $"Position ({spot.X1}, {spot.Y1})\n" + SpotText(spot);
        }
        RebuildOverlays();
        string what = $"Map {_map.MapId:X2} spot {edit.Content.Spot}: {updated?.Describe(_rom)}";
        _mainVm?.NotifyRomModified(what);
        SpotWrittenText = $"✔ Saved in the project at 0x{target.ScriptOffset + 1:X6}: {updated?.Describe(_rom)}.\n" +
                          "Save Project keeps it.";
        Status = $"Treasure changed in the ROM ({updated?.Describe(_rom)}). Save Project keeps it.";
    }

    private void DragNpc(int x, int y, (int x, int y) start)
    {
        if (_map == null || _selection is not { kind: "npc" } sel) return;
        var npc = _map.Npcs[sel.index];
        int dx = x - npc.X, dy = y - npc.Y;
        if (dx == 0 && dy == 0) return;
        npc.X += dx; npc.Y += dy;
        ShiftBox(npc, dx, dy);
        _populating = true;
        NpcX = npc.X; NpcY = npc.Y;
        _populating = false;
        MarkEdited();
        RebuildOverlays();
    }

    partial void OnNpcXChanged(int value) => MoveSelectedNpcTo(value, NpcY);
    partial void OnNpcYChanged(int value) => MoveSelectedNpcTo(NpcX, value);

    private void MoveSelectedNpcTo(int x, int y)
    {
        if (_populating || _map == null || _selection is not { kind: "npc" } sel) return;
        var npc = _map.Npcs[sel.index];
        if (npc.X == x && npc.Y == y) return;
        x = Math.Clamp(x, 0, _map.Width - 1); y = Math.Clamp(y, 0, _map.Height - 1);
        int dx = x - npc.X, dy = y - npc.Y;
        npc.X = x; npc.Y = y;
        ShiftBox(npc, dx, dy);
        MarkEdited();
        RebuildOverlays();
    }

    // moves a character's walking box with it, kept on the map (like AddNpc does). before this a character moved to
    // the edge took its box to -2 (written as FFFE) or past the map's width
    private void ShiftBox(MapNpc npc, int dx, int dy)
    {
        if (_map == null) return;
        npc.BoxX1 = Math.Clamp(npc.BoxX1 + dx, 0, _map.Width); npc.BoxX2 = Math.Clamp(npc.BoxX2 + dx, 0, _map.Width);
        npc.BoxY1 = Math.Clamp(npc.BoxY1 + dy, 0, _map.Height); npc.BoxY2 = Math.Clamp(npc.BoxY2 + dy, 0, _map.Height);
    }

    // exit / arrival editing

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
        int index = _destIds.IndexOf(destMap);
        return index >= 0 ? index : 0;
    }

    // arrival points of a map: the live list for the map being edited, otherwise read from the ROM
    public List<MapArrival> ArrivalsOf(int mapId)
    {
        if (_map != null && mapId == _map.MapId) return _map.Arrivals;
        if (_rom == null) return new();
        // the other maps' arrivals as the ROM has them now (one deleted and applied there isn't offered any more)
        if (_arrivalVersion != _rom.Version) { _arrivalCache.Clear(); _arrivalVersion = _rom.Version; }
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
        var exit = _map.Exits[sel.index];
        int x = Math.Clamp(ExitX, 0, _map.Width - 1), y = Math.Clamp(ExitY, 0, _map.Height - 1);
        int width = Math.Clamp(ExitW, 1, _map.Width - x), height = Math.Clamp(ExitH, 1, _map.Height - y);
        if (exit.X1 == x && exit.Y1 == y && exit.X2 == x + width && exit.Y2 == y + height) return;
        exit.X1 = x; exit.Y1 = y; exit.X2 = x + width; exit.Y2 = y + height;
        MarkEdited();
        RebuildOverlays();
    }

    private void DragExit(int x, int y, bool resize)
    {
        if (_map == null || _selection is not { kind: "exit" } sel) return;
        var exit = _map.Exits[sel.index];
        x = Math.Clamp(x, 0, _map.Width - 1); y = Math.Clamp(y, 0, _map.Height - 1);
        if (resize)
        {
            var (anchorX, anchorY) = _dragAnchor;
            exit.X1 = Math.Min(anchorX, x); exit.Y1 = Math.Min(anchorY, y);
            exit.X2 = Math.Max(anchorX, x) + 1; exit.Y2 = Math.Max(anchorY, y) + 1;
        }
        else
        {
            int width = Math.Max(1, exit.X2 - exit.X1), height = Math.Max(1, exit.Y2 - exit.Y1);
            int newX = Math.Clamp(x - _dragAnchor.x, 0, _map.Width - width), newY = Math.Clamp(y - _dragAnchor.y, 0, _map.Height - height);
            if (newX == exit.X1 && newY == exit.Y1) return;
            exit.X1 = newX; exit.Y1 = newY; exit.X2 = newX + width; exit.Y2 = newY + height;
        }
        _populating = true;
        ExitX = exit.X1; ExitY = exit.Y1; ExitW = exit.X2 - exit.X1; ExitH = exit.Y2 - exit.Y1;
        _populating = false;
        MarkEdited();
        RebuildOverlays();
    }

    private void DragArrival(int x, int y)
    {
        if (_map == null || _selection is not { kind: "arrival" } sel) return;
        var arrival = _map.Arrivals[sel.index];
        x = Math.Clamp(x, 0, _map.Width - 1); y = Math.Clamp(y, 0, _map.Height - 1);
        if (arrival.X == x && arrival.Y == y) return;
        arrival.X = x; arrival.Y = y;
        _populating = true;
        ArrivalX = x; ArrivalY = y;
        _populating = false;
        MarkEdited();
        RebuildOverlays();
    }

    partial void OnArrivalXChanged(int value) => MoveSelectedArrival();
    partial void OnArrivalYChanged(int value) => MoveSelectedArrival();

    private void MoveSelectedArrival()
    {
        if (_populating || _map == null || _selection is not { kind: "arrival" } sel) return;
        var arrival = _map.Arrivals[sel.index];
        int x = Math.Clamp(ArrivalX, 0, _map.Width - 1), y = Math.Clamp(ArrivalY, 0, _map.Height - 1);
        if (arrival.X == x && arrival.Y == y) return;
        arrival.X = x; arrival.Y = y;
        MarkEdited();
        RebuildOverlays();
    }

    [RelayCommand]
    private void DeleteSelected()
    {
        if (_map == null) return;
        if (_selection is { kind: "trigger" } selectedTrigger && selectedTrigger.index < _map.Triggers.Count) { DeleteTrigger(_map.Triggers[selectedTrigger.index]); return; }
        if (_selection is { kind: "exit" } selectedExit && selectedExit.index < _map.Exits.Count)
        {
            _map.RemoveExit(_map.Exits[selectedExit.index]);
            MarkEdited();
            ClearSelection();
            RebuildOverlays();
            Status = "Exit deleted (exits after it were renumbered). It goes into the ROM with the map's other edits.";
        }
        else if (_selection is { kind: "arrival" } selectedArrival && selectedArrival.index < _map.Arrivals.Count)
        {
            var arrival = _map.Arrivals[selectedArrival.index];
            if (!_map.CanRemoveArrival(arrival))
            {
                Status = "Only the last arrival point can be deleted, because exits on other maps refer to arrival points by number.";
                return;
            }
            var incoming = IncomingExits(arrival.Index);
            if (incoming.Count > 0 && MessageBox.Show($"{incoming.Count} exit(s) lead to this arrival point. Delete it anyway?", "Delete arrival point",
                    MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
            _map.RemoveArrival(arrival);
            MarkEdited();
            ClearSelection();
            RebuildOverlays();
            Status = "Arrival point deleted. It goes into the ROM with the map's other edits.";
        }
    }

    [RelayCommand]
    private void PickDestination()
    {
        if (_map == null || _selection is not { kind: "exit" } sel) return;
        DestinationPickerRequested?.Invoke(_map.Exits[sel.index]);
    }

    // the map being edited (live, with unapplied changes) or a fresh copy from the ROM
    public LufiaMap? MapForPicker(int mapId)
    {
        if (_map != null && (mapId == _map.MapId || mapId == 0)) return _map;
        if (_rom == null) return null;
        try { return LufiaMap.Load(_rom, mapId); } catch { return null; }
    }

    // what the destination picker picked. a new arrival point on another map gets written to the ROM right away
    // (that map has to be saved for the exit to work), on this map it's added like any other edit
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
                var otherMap = LufiaMap.Load(_rom, destMap);
                arrival = otherMap.AddArrival(pos.x, pos.y).Index;
                bool expand = false;
                if (MapWriter.NeedsMoreSpace(otherMap, out _) && _rom.Length < MapWriter.ExpandedSize)
                {
                    if (!ConfirmExpand($"Adding the arrival point makes map {destMap:X2} too big for its slot.")) return;
                    expand = true;
                }
                var saveResult = MapWriter.Save(_rom, otherMap, expand || _rom.Length >= MapWriter.ExpandedSize);
                _arrivalCache.Remove(destMap);
                _mainVm?.NotifyRomModified($"Map {destMap:X2}: new arrival point A{arrival} at ({pos.x}, {pos.y})");
                ConfirmWritten($"Arrival point A{arrival} added to map {destMap:X2}",
                    $"New arrival point at ({pos.x}, {pos.y}). {saveResult.Describe()}\n\nThe exit on this map points to it; " +
                    "apply this map to the ROM too so the exit is saved.");
            }
        }
        exit.DestMap = dest;
        exit.Arrival = arrival;
        MarkEdited();
        _links = MapLinks.Build(_rom, Maps.Select(m => m.MapId));
        SelectExit(exit, focusLinked: dest == 0);
        Status = $"Exit E{exit.Index} now leads to {ExitTargetText(exit)}. It goes into the ROM with the map's other edits.";
    }

    partial void OnExitDestMapChanged(int value) => UpdateSelectedExit();
    partial void OnExitDestMapTextChanged(string value)
    {
        if (int.TryParse(value?.Trim(), System.Globalization.NumberStyles.HexNumber, null, out int mapId) && mapId is >= 0 and <= 0xFF)
            ExitDestMap = mapId;
    }
    partial void OnExitArrivalChanged(int value) => UpdateSelectedExit();

    private void UpdateSelectedExit()
    {
        if (_populating || _map == null || _selection is not { kind: "exit" } sel) return;
        var exit = _map.Exits[sel.index];
        int dest = Math.Clamp(ExitDestMap, 0, 255), arrival = Math.Clamp(ExitArrival, 0, 255);
        if (exit.DestMap == dest && exit.Arrival == arrival) return;
        exit.DestMap = dest; exit.Arrival = arrival;
        MarkEdited();
        RebuildOverlays();
        SelectionDetails =
            $"Area ({exit.X1},{exit.Y1}) to ({exit.X2 - 1},{exit.Y2 - 1})   type {exit.Flags >> 4:X}\n" +
            $"Leads to {ExitTargetText(exit)}";
    }

    [RelayCommand]
    private void GoToDestination()
    {
        if (_map == null || _selection is not { kind: "exit" } sel) return;
        var exit = _map.Exits[sel.index];
        int target = exit.DestMap == 0 ? _map.MapId : exit.DestMap;
        var info = Maps.FirstOrDefault(m => m.MapId == target);
        if (info == null) { Status = $"Map {target:X2} isn't in the map list."; return; }
        int arrival = exit.Arrival;
        SelectedMap = info;
        if (_map != null && _map.MapId == target && arrival < _map.Arrivals.Count)
        {
            SelectArrival(_map.Arrivals[arrival], focusLinked: false);
            FocusOn(_map.Arrivals[arrival]);
            Status = $"Exit leads here: arrival point {arrival} at ({_map.Arrivals[arrival].X}, {_map.Arrivals[arrival].Y}).";
        }
    }

    // rough first line of dialogue in an event script: the first text opener (0C/0D narration, 88-AF "actor speaks")
    // in the first bytes whose text has real words in it
    private string FirstLine(int scriptOffset)
    {
        if (_rom == null) return "";
        for (int pos = scriptOffset; pos < scriptOffset + 128 && pos < _rom.Length - 1; pos++)
        {
            byte opcode = _rom.ReadByte(pos);
            if (!(opcode is 0x0C or 0x0D || (opcode >= 0x88 && opcode <= 0xAF))) continue;
            var decoded = TextDecoder.Decode(_rom, pos + 1, expandMte: true);
            var sb = new StringBuilder();
            foreach (var token in decoded.Tokens)
            {
                if (token.Kind is TextTokenKind.PageBreak or TextTokenKind.EndString) break;
                sb.Append(token.Kind == TextTokenKind.Newline ? " " : token.Display);
                if (sb.Length > 300) break;
            }
            string text = sb.ToString().Trim();
            if (text.Count(char.IsLetter) >= 4) return "\"" + text + "\"";
        }
        return "(no dialogue found near the start of this script)";
    }

    // apply / revert

    [RelayCommand]
    private void ApplyToRom()
    {
        if (_rom == null || _map == null) return;
        if (ChangedElsewhere()) { Status = "Not applied: " + ChangedElsewhereText + "."; return; }
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
            Status = result.Describe() + " Save Project keeps it.";
            ConfirmWritten($"Map {id:X2} saved in the project", result.Describe());
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
        Status = "Put back as the project has it.";
    }

    [RelayCommand] private void ZoomIn()  => Zoom = Math.Min(6, Zoom * 1.5);
    [RelayCommand] private void ZoomOut() => Zoom = Math.Max(0.25, Zoom / 1.5);
}
