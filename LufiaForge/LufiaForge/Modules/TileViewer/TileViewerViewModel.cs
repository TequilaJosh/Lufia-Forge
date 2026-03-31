using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LufiaForge.Core;
using System.Globalization;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace LufiaForge.Modules.TileViewer;

public partial class TileViewerViewModel : ObservableObject
{
    private RomBuffer? _rom;

    // -------------------------------------------------------------------------
    // Toolbar properties
    // -------------------------------------------------------------------------

    [ObservableProperty] private string _offsetText         = "0x000000";
    [ObservableProperty] private string _paletteOffsetText  = "0x000000";
    [ObservableProperty] private int    _selectedBitDepthIndex = 1;  // default: 4bpp
    [ObservableProperty] private bool   _useRomPalette      = false;
    [ObservableProperty] private int    _tilesPerRow        = 16;
    [ObservableProperty] private int    _tileCount          = 256;
    [ObservableProperty] private int    _zoomLevel          = 2;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasImage))]
    private BitmapSource? _renderedImage;

    [ObservableProperty] private string _statusText = "Load a ROM, then configure the offset and click Render.";

    public bool HasImage => RenderedImage != null;

    public List<string> BitDepthOptions { get; } =
    [
        "2bpp  (4 colors)",
        "4bpp  (16 colors)",
        "8bpp  (256 colors)"
    ];

    public List<int> ZoomOptions { get; } = [1, 2, 3, 4];

    // -------------------------------------------------------------------------
    // Init
    // -------------------------------------------------------------------------

    public void SetRom(RomBuffer rom)
    {
        _rom        = rom;
        RenderedImage = null;
        StatusText  = "ROM loaded. Set an offset and click Render.";
    }

    // -------------------------------------------------------------------------
    // Render command
    // -------------------------------------------------------------------------

    [RelayCommand]
    private void Render()
    {
        if (_rom == null) { StatusText = "No ROM loaded."; return; }

        if (!TryParseHex(OffsetText, out int offset))
        {
            StatusText = $"Invalid ROM offset: \"{OffsetText}\"  (use hex, e.g. 0x050000)";
            return;
        }

        if (offset < 0 || offset >= _rom.Length)
        {
            StatusText = $"Offset 0x{offset:X6} is out of range (ROM size: 0x{_rom.Length:X6}).";
            return;
        }

        var depth = SelectedBitDepthIndex switch
        {
            0 => BitDepth.Bpp2,
            2 => BitDepth.Bpp8,
            _ => BitDepth.Bpp4
        };

        int bpp        = (int)depth;
        int paletteSize = 1 << bpp;  // 4, 16, or 256

        uint[] palette;
        if (UseRomPalette && TryParseHex(PaletteOffsetText, out int palOff))
            palette = SnesPalette.ReadFromRom(_rom, palOff, paletteSize);
        else
            palette = SnesPalette.Grayscale(paletteSize);

        int zoom  = Math.Clamp(ZoomLevel,  1,  4);
        int cols  = Math.Clamp(TilesPerRow, 1, 64);
        int count = Math.Clamp(TileCount,   1, 2048);
        int tileBytes = SnesTileDecoder.BytesPerTile(depth);

        // Cap to what actually fits in the ROM
        int maxTiles = Math.Max(0, (_rom.Length - offset) / tileBytes);
        count = Math.Min(count, maxTiles);

        if (count == 0)
        {
            StatusText = "No tiles fit at this offset with the selected bit depth.";
            return;
        }

        int rows = (count + cols - 1) / cols;
        int imgW = cols * SnesTileDecoder.TileWidth  * zoom;
        int imgH = rows * SnesTileDecoder.TileHeight * zoom;

        if (imgW > 4096 || imgH > 4096)
        {
            StatusText = "Requested image is too large (>4096px). Reduce tile count or zoom.";
            return;
        }

        // Read only the ROM bytes we need
        int readLen = count * tileBytes;
        byte[] tileData = _rom.ReadBytes(offset, readLen);

        // Build pixel buffer (BGRA32: each int = one pixel, value = 0xAARRGGBB)
        var pixelBuf = new int[imgW * imgH];

        for (int ti = 0; ti < count; ti++)
        {
            int tileOff = ti * tileBytes;
            byte[] pxIndices = SnesTileDecoder.DecodeTile(tileData, tileOff, depth);

            int tileCol = ti % cols;
            int tileRow = ti / cols;
            int baseX   = tileCol * SnesTileDecoder.TileWidth  * zoom;
            int baseY   = tileRow * SnesTileDecoder.TileHeight * zoom;

            for (int py = 0; py < SnesTileDecoder.TileHeight; py++)
            {
                for (int px = 0; px < SnesTileDecoder.TileWidth; px++)
                {
                    int colorIdx = pxIndices[py * SnesTileDecoder.TileWidth + px];
                    int rgba     = (int)palette[colorIdx];  // BGRA32 uint cast to int

                    for (int zy = 0; zy < zoom; zy++)
                    {
                        int destY = baseY + py * zoom + zy;
                        for (int zx = 0; zx < zoom; zx++)
                        {
                            int destX = baseX + px * zoom + zx;
                            pixelBuf[destY * imgW + destX] = rgba;
                        }
                    }
                }
            }
        }

        var bmp = new WriteableBitmap(imgW, imgH, 96, 96, PixelFormats.Bgra32, null);
        bmp.WritePixels(new Int32Rect(0, 0, imgW, imgH), pixelBuf, imgW * 4, 0);
        bmp.Freeze();

        RenderedImage = bmp;
        StatusText = $"Rendered {count} tiles  |  offset 0x{offset:X6}  |  {depth}  |  " +
                     $"{cols}×{rows} grid  |  zoom {zoom}×  |  " +
                     $"{imgW}×{imgH} px";
    }

    // -------------------------------------------------------------------------
    // Helpers
    // -------------------------------------------------------------------------

    private static bool TryParseHex(string text, out int value)
    {
        text = (text ?? "").Trim().TrimStart('$');
        if (text.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
            text = text[2..];
        return int.TryParse(text, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out value);
    }
}
