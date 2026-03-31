namespace LufiaForge.Modules.TileViewer;

/// <summary>
/// Decodes SNES planar tile data into 8x8 arrays of palette color indices.
///
/// SNES tile formats:
///   2bpp — 16 bytes/tile — 4 colors  — used for BG layer text/UI tiles
///   4bpp — 32 bytes/tile — 16 colors — used for most sprites and BG tiles
///   8bpp — 64 bytes/tile — 256 colors — used for Mode 7 and large BG tiles
///
/// Planar layout (pairs of bitplanes are interleaved per row):
///   2bpp: [bp0_r0][bp1_r0] [bp0_r1][bp1_r1] ... (16 bytes)
///   4bpp: bp0+bp1 group (rows 0-7), then bp2+bp3 group (rows 0-7)
///   8bpp: as 4bpp but with two more groups for bp4+bp5 and bp6+bp7
/// </summary>
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

    /// <summary>
    /// Decode one 8x8 tile from <paramref name="data"/> at byte offset <paramref name="offset"/>.
    /// Returns a 64-element array of palette indices (row-major, top-left first).
    /// Returns all-zero (transparent) pixels if data is insufficient.
    /// </summary>
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
                    // Each pair of bitplanes occupies a 16-byte group.
                    // Within the group, rows are interleaved: bp_even then bp_odd.
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
}

/// <summary>SNES tile bit depth options.</summary>
public enum BitDepth
{
    Bpp2 = 2,
    Bpp4 = 4,
    Bpp8 = 8
}
