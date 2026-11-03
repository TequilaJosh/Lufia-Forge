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

    /// <summary>The brush block's picture (after the brush or the tileset changes): <see cref="BrushSize"/> blocks square.</summary>
    private void RefreshBrushPreview()
    {
        if (_tileset == null || Brush < 0 || Brush >= _tileset.MetatileCount) { BrushPreviewImage = null; return; }
        var one = _tileset.MetatilePixels(Brush);
        int n = BrushSize, w = n * 16;
        var px = new uint[w * w];
        for (int y = 0; y < w; y++) for (int x = 0; x < w; x++) px[y * w + x] = one[y % 16 * 16 + x % 16];
        var bmp = BitmapSource.Create(w, w, 96, 96, PixelFormats.Bgra32, null, px, w * 4);
        bmp.Freeze();
        BrushPreviewImage = bmp;
    }

    /// <summary>On every mouse move over the map.</summary>
    private void BrushPreviewFollow(int x, int y)
    {
        bool show = Tool == MapTool.Paint && _stamp == null && !_picking && BrushPreviewImage != null;
        if (show) { BrushPreviewX = (x - BrushSize / 2) * 16; BrushPreviewY = (y - BrushSize / 2) * 16; }
        BrushPreviewVisible = show;
    }
}
