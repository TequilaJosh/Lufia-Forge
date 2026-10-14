using System.ComponentModel;
using System.Windows.Data;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace LufiaForge.Modules.MapEditor;

/// <summary>The editor's layout helpers: map search, zoom text, tool hints and tool keys.</summary>
public partial class MapEditorViewModel
{
    [ObservableProperty] private string _mapSearch = "";
    private ICollectionView? _mapsView;

    /// <summary>The map list, filtered by the search box (name or number).</summary>
    public ICollectionView MapsView
    {
        get
        {
            if (_mapsView != null) return _mapsView;
            _mapsView = CollectionViewSource.GetDefaultView(Maps);
            _mapsView.Filter = o =>
            {
                if (string.IsNullOrWhiteSpace(MapSearch) || o is not Core.Maps.MapInfo m) return true;
                string q = MapSearch.Trim();
                return m.Label.Contains(q, StringComparison.OrdinalIgnoreCase) || m.MapId.ToString("X2").Equals(q, StringComparison.OrdinalIgnoreCase);
            };
            return _mapsView;
        }
    }

    partial void OnMapSearchChanged(string value) => _mapsView?.Refresh();

    public string ZoomText => $"{Zoom * 100:0}%";
    partial void OnZoomChanged(double value) => OnPropertyChanged(nameof(ZoomText));

    /// <summary>One line under the map: what the current tool does and its keys.</summary>
    public string ToolHint => Tool switch
    {
        MapTool.Select => "Select / move (S): click a character, exit, arrival point or treasure to edit it; drag to move; click the same block again for the next thing on it. Double-click an exit to go there.",
        MapTool.Paint => "Paint (B): click or drag to paint the brush block. Ctrl + click picks a block from the map. Pick a brush (or drag a rectangle of blocks) on the right.",
        MapTool.Pick => "Pick block (I): click the map to take its block as the brush.",
        MapTool.Area => "Area (M): drag a rectangle, then Copy (Ctrl+C), Cut (Ctrl+X), Fill or Delete; Paste (Ctrl+V) stamps copied blocks. Esc stops.",
        MapTool.AddExit => "Add exit (E): drag a rectangle where the party should leave, then choose where it leads.",
        MapTool.AddArrival => "Add arrival point (A): click where the party should appear.",
        MapTool.AddTrigger => "Add step-on area (T): drag a rectangle; walking into it plays a new event (Apply makes the event, then Event... on the right opens it).",
        _ => "",
    };

    /// <summary>Single-key tool shortcuts (when no text box has the keyboard).</summary>
    public bool ToolKey(System.Windows.Input.Key key)
    {
        MapTool? t = key switch
        {
            System.Windows.Input.Key.S or System.Windows.Input.Key.V => MapTool.Select,
            System.Windows.Input.Key.B or System.Windows.Input.Key.P => MapTool.Paint,
            System.Windows.Input.Key.I => MapTool.Pick,
            System.Windows.Input.Key.M => MapTool.Area,
            System.Windows.Input.Key.E => MapTool.AddExit,
            System.Windows.Input.Key.A => MapTool.AddArrival,
            System.Windows.Input.Key.T => MapTool.AddTrigger,
            _ => null,
        };
        if (t == null || !IsMapLoaded) return false;
        Tool = t.Value;
        return true;
    }

    [RelayCommand] private void ZoomReset() => Zoom = 1;
}
