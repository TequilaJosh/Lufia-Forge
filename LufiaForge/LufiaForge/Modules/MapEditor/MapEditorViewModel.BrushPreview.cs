using System.Windows.Media;
using System.Windows.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;

namespace LufiaForge.Modules.MapEditor;

// with the Paint tool a see-through copy of the brush block follows the mouse, snapped to the block it would paint
// (a multi-block or pasted brush shows its own preview, the stamp)
public partial class MapEditorViewModel
{
    [ObservableProperty] private ImageSource? _brushPreviewImage;
    [ObservableProperty] private double _brushPreviewX, _brushPreviewY;
    [ObservableProperty] private bool _brushPreviewVisible;

    // the brush block's picture, BrushSize blocks square (redone when the brush or tileset changes)
    private void RefreshBrushPreview()
    {
        if (_tileset == null || Brush < 0 || Brush >= _tileset.MetatileCount) { BrushPreviewImage = null; return; }
        var blockPixels = _tileset.MetatilePixels(Brush);
        int brushBlocks = BrushSize, sizePx = brushBlocks * 16;
        var pixels = new uint[sizePx * sizePx];
        for (int y = 0; y < sizePx; y++) for (int x = 0; x < sizePx; x++) pixels[y * sizePx + x] = blockPixels[y % 16 * 16 + x % 16];
        var bmp = BitmapSource.Create(sizePx, sizePx, 96, 96, PixelFormats.Bgra32, null, pixels, sizePx * 4);
        bmp.Freeze();
        BrushPreviewImage = bmp;
    }

    // runs on every mouse move over the map
    private void BrushPreviewFollow(int x, int y)
    {
        bool show = Tool == MapTool.Paint && _stamp == null && !_picking && BrushPreviewImage != null;
        if (show) { BrushPreviewX = (x - BrushSize / 2) * 16; BrushPreviewY = (y - BrushSize / 2) * 16; }
        BrushPreviewVisible = show;
    }
}
