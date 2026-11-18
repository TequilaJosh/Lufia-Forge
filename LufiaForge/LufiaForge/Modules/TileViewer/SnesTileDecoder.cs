namespace LufiaForge.Modules.TileViewer;

// turns SNES planar tile data into 8x8 arrays of palette color indexes
//
// SNES tile formats:
//   2bpp - 16 bytes/tile - 4 colors - BG layer text/UI tiles
//   4bpp - 32 bytes/tile - 16 colors - most sprites and BG tiles
//   8bpp - 64 bytes/tile - 256 colors - Mode 7 and big BG tiles
//
// planar layout (bitplanes go in pairs, interleaved per row):
//   2bpp: [bp0_r0][bp1_r0] [bp0_r1][bp1_r1] ... (16 bytes)
//   4bpp: bp0+bp1 group (rows 0-7), then bp2+bp3 group (rows 0-7)
//   8bpp: like 4bpp with two more groups for bp4+bp5 and bp6+bp7
public static class SnesTileDecoder
{
    public const int TileWidth  = 8;
    public const int TileHeight = 8;
    public const int PixelsPerTile = TileWidth * TileHeight;

    public static int BytesPerTile(BitDepth depth) => depth switch
    {
        BitDepth.Bpp2 => 16,
        BitDepth.Bpp4 => 32,
        BitDepth.Bpp8 => 64,
        _             => 16
    };

    // decodes one 8x8 tile from data at byte offset offset. returns 64 palette indexes (row by row, top left
    // first), or all zero (see-through) pixels if there isn't enough data
    public static byte[] DecodeTile(byte[] data, int offset, BitDepth depth)
    {
        var pixels  = new byte[PixelsPerTile];
        int bpp     = (int)depth;
        int tileEnd = offset + BytesPerTile(depth);

        if (tileEnd > data.Length)
            return pixels;

        for (int row = 0; row < TileHeight; row++)
        {
            for (int col = 0; col < TileWidth; col++)
            {
                int bit        = 7 - col;
                int colorIndex = 0;

                for (int plane = 0; plane < bpp; plane++)
                {
                    // each pair of bitplanes takes a 16-byte group. inside the group the rows are interleaved,
                    // bp_even then bp_odd
                    //   group 0 (bp 0,1): bytes [row*2 + 0],  [row*2 + 1]
                    //   group 1 (bp 2,3): bytes [16 + row*2 + 0], [16 + row*2 + 1]
                    //   group 2 (bp 4,5): bytes [32 + row*2 + 0], [32 + row*2 + 1]
                    //   group 3 (bp 6,7): bytes [48 + row*2 + 0], [48 + row*2 + 1]
                    int group     = plane / 2;
                    int pairByte  = plane % 2;
                    int byteIndex = group * 16 + row * 2 + pairByte;

                    int bitVal = (data[offset + byteIndex] >> bit) & 1;
                    colorIndex |= bitVal << plane;
                }

                pixels[row * TileWidth + col] = (byte)colorIndex;
            }
        }

        return pixels;
    }

    // the planar bytes of one 8x8 tile (DecodeTile backwards)
    public static byte[] EncodeTile(byte[] pixels, BitDepth depth)
    {
        int bpp = (int)depth;
        var data = new byte[BytesPerTile(depth)];
        for (int row = 0; row < TileHeight; row++)
            for (int col = 0; col < TileWidth; col++)
            {
                int colorIndex = pixels[row * TileWidth + col], bit = 7 - col;
                for (int plane = 0; plane < bpp; plane++)
                    data[plane / 2 * 16 + row * 2 + plane % 2] |= (byte)(((colorIndex >> plane) & 1) << bit);
            }
        return data;
    }
}

// SNES tile bit depths
public enum BitDepth
{
    Bpp2 = 2,
    Bpp4 = 4,
    Bpp8 = 8
}
