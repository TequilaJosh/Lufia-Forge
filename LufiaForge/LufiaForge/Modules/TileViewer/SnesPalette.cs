using LufiaForge.Core;

namespace LufiaForge.Modules.TileViewer;

/// <summary>
/// Reads and converts SNES BGR555 palette data.
///
/// SNES palette color format (16-bit little-endian word):
///   Bit 15   : unused
///   Bits 14-10 : Blue  (0-31)
///   Bits  9-5  : Green (0-31)
///   Bits  4-0  : Red   (0-31)
///
/// Colors are scaled to 8-bit range by left-shifting 3 bits (multiply by 8).
/// The returned uint values are in BGRA32 format:
///   (Alpha=0xFF) << 24 | (Red) << 16 | (Green) << 8 | Blue
/// which is the correct in-memory layout for WPF WriteableBitmap Bgra32.
/// </summary>
public static class SnesPalette
{
    /// <summary>
    /// Read <paramref name="colorCount"/> BGR555 colors from the ROM at the given file offset.
    /// Returns an array of BGRA32 uint values.
    /// </summary>
    public static uint[] ReadFromRom(RomBuffer rom, int fileOffset, int colorCount)
    {
        var palette = new uint[colorCount];
        for (int i = 0; i < colorCount; i++)
        {
            int off = fileOffset + i * 2;
            if (off + 1 >= rom.Length) break;
            palette[i] = Bgr555ToArgb32(rom.ReadUInt16Le(off));
        }
        return palette;
    }

    /// <summary>
    /// Generate a grayscale ramp palette (black → white) for rendering
    /// without a real ROM palette.
    /// </summary>
    public static uint[] Grayscale(int colorCount)
    {
        var palette = new uint[colorCount];
        for (int i = 0; i < colorCount; i++)
        {
            uint v = (uint)(colorCount <= 1 ? 255 : (i * 255) / (colorCount - 1));
            palette[i] = 0xFF000000u | (v << 16) | (v << 8) | v;
        }
        return palette;
    }

    /// <summary>Convert a single BGR555 word to BGRA32 uint.</summary>
    public static uint Bgr555ToArgb32(ushort color)
    {
        uint r = (uint)((color & 0x001F) << 3);
        uint g = (uint)(((color >> 5) & 0x1F) << 3);
        uint b = (uint)(((color >> 10) & 0x1F) << 3);
        return 0xFF000000 | (r << 16) | (g << 8) | b;
    }
}
