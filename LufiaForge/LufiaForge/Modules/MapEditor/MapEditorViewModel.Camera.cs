using System.Windows;
using System.Windows.Media;
using CommunityToolkit.Mvvm.ComponentModel;

namespace LufiaForge.Modules.MapEditor;

/// <summary>
/// Camera view: what the game screen (256 x 224 pixels = 16 x 14 blocks) shows when the party stands on the block
/// under the mouse. The camera keeps the party in the middle and stops at the map's edges, so near an edge the frame
/// stays inside the map. Everything outside the frame is dimmed.
/// </summary>
public partial class MapEditorViewModel
{
    public const int ScreenW = 256, ScreenH = 224;

    [ObservableProperty] private bool _showCamera;
    [ObservableProperty] private double _cameraX, _cameraY;
    [ObservableProperty] private Geometry? _cameraShade;
    [ObservableProperty] private string _cameraText = "";

    partial void OnShowCameraChanged(bool value)
    {
        if (value && _map != null) PlaceCamera(_map.Width / 2, _map.Height / 2);
        OnPropertyChanged(nameof(CameraVisible));
        Status = value ? "Camera: the frame shows what the screen sees with the party on the block under the mouse (16 x 14 blocks; it stops at the map's edges)." : Status;
    }

    public bool CameraVisible => ShowCamera && _map != null;

    /// <summary>The screen with the party on block (x, y).</summary>
    private void PlaceCamera(int x, int y)
    {
        if (_map == null) return;
        double mw = _map.Width * 16, mh = _map.Height * 16;
        // the screen's top-left is 112 pixels left of and 111 above the party's block (matched pixel for pixel to the
        // game); the camera can't show past the map's edges
        double left = Math.Clamp(x * 16 - 112, 0, Math.Max(0, mw - ScreenW));
        double top = Math.Clamp(y * 16 - 111, 0, Math.Max(0, mh - ScreenH));
        CameraX = left; CameraY = top;
        var shade = new GeometryGroup { FillRule = FillRule.EvenOdd };
        shade.Children.Add(new RectangleGeometry(new Rect(0, 0, mw, mh)));
        shade.Children.Add(new RectangleGeometry(new Rect(left, top, Math.Min(ScreenW, mw), Math.Min(ScreenH, mh))));
        shade.Freeze();
        CameraShade = shade;
        CameraText = $"Screen with the party at ({x}, {y}): blocks ({(int)(left / 16)}, {(int)(top / 16)}) to ({(int)(left / 16) + 15}, {(int)(top / 16) + 13})";
        OnPropertyChanged(nameof(CameraVisible));
    }

    /// <summary>Called on every mouse move over the map.</summary>
    private void CameraFollow(int x, int y)
    {
        if (ShowCamera) PlaceCamera(x, y);
    }
}
