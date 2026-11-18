using LufiaForge.Core;

namespace LufiaForge.Modules.TileViewer;

// reads SNES BGR555 palettes and converts them
//
// a SNES color is a 16-bit little-endian word:
//   bit 15     : unused
//   bits 14-10 : blue  (0-31)
//   bits  9-5  : green (0-31)
//   bits  4-0  : red   (0-31)
//
// each part gets shifted left 3 bits (x 8) to make it 8-bit. the uints we return are BGRA32:
//   (alpha=0xFF) << 24 | (red) << 16 | (green) << 8 | blue
// which is how a WPF WriteableBitmap in Bgra32 lays it out in memory
public static class SnesPalette
{
    // reads colorCount BGR555 colors from the ROM at fileOffset, returns them as BGRA32 uints
    public static uint[] ReadFromRom(RomBuffer rom, int fileOffset, int colorCount)
    {
        var palette = new uint[colorCount];
        for (int i = 0; i < colorCount; i++)
        {
            int colorOffset = fileOffset + i * 2;
            if (colorOffset + 1 >= rom.Length) break;
            palette[i] = Bgr555ToArgb32(rom.ReadUInt16Le(colorOffset));
        }
        return palette;
    }

    // a grey ramp (black -> white) for drawing without a real ROM palette
    public static uint[] Grayscale(int colorCount)
    {
        var palette = new uint[colorCount];
        for (int i = 0; i < colorCount; i++)
        {
            uint grey = (uint)(colorCount <= 1 ? 255 : (i * 255) / (colorCount - 1));
            palette[i] = 0xFF000000u | (grey << 16) | (grey << 8) | grey;
        }
        return palette;
    }

    // one BGR555 word -> BGRA32 uint
    public static uint Bgr555ToArgb32(ushort color)
    {
        uint red = (uint)((color & 0x001F) << 3);
        uint green = (uint)(((color >> 5) & 0x1F) << 3);
        uint blue = (uint)(((color >> 10) & 0x1F) << 3);
        return 0xFF000000 | (red << 16) | (green << 8) | blue;
    }
}
