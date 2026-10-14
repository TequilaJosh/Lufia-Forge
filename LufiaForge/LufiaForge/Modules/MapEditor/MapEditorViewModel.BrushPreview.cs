using System.Windows.Media;
using System.Windows.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;

namespace LufiaForge.Modules.MapEditor;

/// <summary>
/// With the Paint tool, a see-through copy of the brush block follows the mouse, snapped to the block it would paint.
/// (A multi-block or pasted brush shows its own preview: the stamp.)
/// </summary>
public partial class MapEditorViewModel
{
    [ObservableProperty] private ImageSource? _brushPreviewImage;
    [ObservableProperty] private double _brushPreviewX, _brushPreviewY;
    [ObservableProperty] private bool _brushPreviewVisible;

    /// <summary>The brush block's picture (after the brush or the tileset changes).</summary>
    private void RefreshBrushPreview()
    {
        if (_tileset == null || Brush < 0 || Brush >= _tileset.MetatileCount) { BrushPreviewImage = null; return; }
        var px = _tileset.MetatilePixels(Brush);
        var bmp = BitmapSource.Create(16, 16, 96, 96, PixelFormats.Bgra32, null, px, 16 * 4);
        bmp.Freeze();
        BrushPreviewImage = bmp;
    }

    /// <summary>On every mouse move over the map.</summary>
    private void BrushPreviewFollow(int x, int y)
    {
        bool show = Tool == MapTool.Paint && _stamp == null && !_picking && BrushPreviewImage != null;
        if (show) { BrushPreviewX = x * 16; BrushPreviewY = y * 16; }
        BrushPreviewVisible = show;
    }
}
