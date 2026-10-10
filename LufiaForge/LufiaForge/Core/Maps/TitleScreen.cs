namespace LufiaForge.Core.Maps;

/// <summary>
/// The title screen ("Lufia &amp; The Fortress of Doom"). It is map 02, loaded through the normal map code, but
/// unlike town maps its data (resource B2, named "USATITLE") carries its own 16x16 blocks (count at 0x1E, 9 bytes
/// each from 0x40) like the world map, its graphics are resource B1 (512 4bpp tiles) and its colours are map
/// palette 19 (rows 2-7). Two layers of 18 x 16 blocks: the back one is the rainbow gradient, the front one the
/// black screen with the letters cut out (so the gradient shows through them) and the subtitle in white.
/// Measured: drawn from these pieces it matches the console's title screen (screen at map pixel 16, 1).
/// Nothing else in the game uses B1 or palette 19.
///
/// Animation: like every map, the title can cycle colours (the water on town maps does this). When a map loads,
/// $01:C015 reads a record at map data [dword 0x30] + 0x18 (+ 0x08 when the word at 0x3E is below 3376): bit 2
/// of its first word cycles colours 1..n of palette row 2, bit 3 those of row 3; the words at +6 and +8 hold
/// n - 1 and the frames per step. The frame interrupt ($00:848B) then rotates those colours.
/// </summary>
public sealed class TitleScreen
{
    public const int MapId = 0x02;
    public const int TilesResource = 0xB1;
    public const int PaletteIndex = 19;
    public const int MaxTiles = 512;
    public const int BlocksOffset = 0x40;
    public const int ScreenX = 16, ScreenY = 1, ScreenW = 256, ScreenH = 224;

    public LufiaMap Map { get; }
    public byte[] Tiles { get; private set; }
    public byte[] Blocks { get; private set; }
    /// <summary>128 colours, BGR555 (rows 0-1 shared, 2-7 palette 19).</summary>
    public ushort[] Palette555 { get; } = new ushort[128];
    public int Width => Map.Width;
    public int Height => Map.Height;
    public int BlockCount => Blocks.Length / 9;
    /// <summary>Most blocks that fit between the header and the first layer.</summary>
    public int MaxBlocks => (Map.LayerOffset - BlocksOffset) / 9;

    private TitleScreen(LufiaMap map, byte[] tiles, byte[] blocks) { Map = map; Tiles = tiles; Blocks = blocks; }

    /// <summary>Colour cycling of palette rows 2 and 3 (index 0 = row 2): on, how many colours from colour 1, frames per step.</summary>
    public (bool On, int Count, int Delay)[] Cycles { get; } = new (bool, int, int)[2];

    /// <summary>Offset in the map data of the colour-cycle record (see the class notes).</summary>
    public static int CycleRecord(byte[] d)
    {
        int section = d[0x30] | d[0x31] << 8 | d[0x32] << 16;
        int alt = d[0x3E] | d[0x3F] << 8;
        return section + (alt >= 0x3376 ? 0x18 : 0x08);
    }

    private void ReadCycles()
    {
        var d = Map.Original;
        int r = CycleRecord(d);
        if (r + 10 > d.Length) return;
        int flags = d[r] | d[r + 1] << 8;
        for (int k = 0; k < 2; k++)
        {
            bool on = (flags >> (2 + k) & 1) != 0;
            Cycles[k] = (on, on ? Math.Clamp(d[r + 6 + k * 2] + 1, 2, 15) : 15, on ? Math.Max(1, (int)d[r + 7 + k * 2]) : 8);
        }
    }

    public static TitleScreen Load(RomBuffer rom)
    {
        var map = LufiaMap.Load(rom, MapId);
        var d = map.Original;
        int n = d[0x1E] | d[0x1F] << 8;
        var t = new TitleScreen(map, LufiaCompression.DecompressResource(rom, TilesResource, out _), d.AsSpan(BlocksOffset, n * 9).ToArray());
        t.ReadCycles();
        for (int i = 0; i < 32; i++) t.Palette555[i] = rom.ReadUInt16Le(LufiaMap.SharedPaletteRows + i * 2);
        for (int i = 0; i < 96; i++) t.Palette555[32 + i] = rom.ReadUInt16Le(PaletteRows + i * 2);
        return t;
    }

    /// <summary>File offset of palette rows 2-7.</summary>
    public static int PaletteRows => LufiaMap.MapPaletteTable + (PaletteIndex + 1) * 0xC0;

    public static uint Argb(ushort v) => 0xFF000000u | (uint)((v & 31) << 3) << 16 | (uint)((v >> 5 & 31) << 3) << 8 | (uint)((v >> 10 & 31) << 3);

    /// <summary>The block layer: true = front (letters), false = back (gradient).</summary>
    public (ushort[] Layer, int W, int H) LayerOf(bool front) =>
        front ? (Map.Layer, Map.Width, Map.Height) : (Map.Layer2 ?? new ushort[Map.Width * Map.Height], Map.Layer2Width, Map.Layer2Height);

    /// <summary>Colour numbers (row * 16 + colour; 0 = see-through) of a layer, Width*16 x Height*16.</summary>
    public byte[] LayerIndices(bool front)
    {
        var (layer, lw, _) = LayerOf(front);
        int W = Width * 16, H = Height * 16;
        var idx = new byte[W * H];
        for (int by = 0; by < Height; by++)
            for (int bx = 0; bx < Width; bx++)
            {
                int b = layer[by * lw + bx] & 0x3FF;
                if (b >= BlockCount) continue;
                for (int q = 0; q < 4; q++)
                {
                    int word = Blocks[b * 9 + q * 2] | Blocks[b * 9 + q * 2 + 1] << 8;
                    int t = word & 0x3FF, row = word >> 10 & 7;
                    bool hf = (word & 0x4000) != 0, vf = (word & 0x8000) != 0;
                    int ox = bx * 16 + (q >> 1) * 8, oy = by * 16 + (q & 1) * 8;   // TL, BL, TR, BR
                    if (t * 32 + 32 > Tiles.Length) continue;
                    for (int y = 0; y < 8; y++)
                        for (int x = 0; x < 8; x++)
                        {
                            int c = Pixel(t, hf ? 7 - x : x, vf ? 7 - y : y);
                            if (c != 0) idx[(oy + y) * W + ox + x] = (byte)(row * 16 + c);
                        }
                }
            }
        return idx;
    }

    private int Pixel(int t, int x, int y)
    {
        int o = t * 32, bit = 7 - x;
        return (Tiles[o + y * 2] >> bit & 1) | (Tiles[o + y * 2 + 1] >> bit & 1) << 1 |
               (Tiles[o + 16 + y * 2] >> bit & 1) << 2 | (Tiles[o + 16 + y * 2 + 1] >> bit & 1) << 3;
    }

    /// <summary>The screen as the game shows it (back layer, then front layer), 256 x 224 BGRA.</summary>
    public uint[] RenderScreen() => RenderScreen(LayerIndices(true), LayerIndices(false), Palette555);

    /// <summary>The screen from layer pictures (colour numbers, Width*16 x Height*16) and colours, 256 x 224 BGRA.</summary>
    public uint[] RenderScreen(byte[] front, byte[] back, ushort[] palette)
    {
        int W = Width * 16;
        var px = new uint[ScreenW * ScreenH];
        for (int y = 0; y < ScreenH; y++)
            for (int x = 0; x < ScreenW; x++)
            {
                int i = (y + ScreenY) * W + x + ScreenX;
                int c = front[i] != 0 ? front[i] : back[i];
                px[y * ScreenW + x] = c == 0 ? 0xFF000000u : Argb(palette[c]);
            }
        return px;
    }

    /// <summary>The colours as the game shows them <paramref name="frames"/> frames after the title appears (cycling rows 2-3).</summary>
    public ushort[] PaletteAt(long frames)
    {
        var p = (ushort[])Palette555.Clone();
        for (int k = 0; k < 2; k++)
        {
            var (on, count, delay) = Cycles[k];
            if (!on) continue;
            int start = (2 + k) * 16 + 1, phase = (int)(frames / Math.Max(1, delay) % count);
            for (int j = 0; j < count; j++) p[start + j] = Palette555[start + (j + phase) % count];
        }
        return p;
    }

    // ── import ──

    /// <summary>
    /// Rebuilds the tiles and blocks from both layers' colour numbers (each Width*16 x Height*16; row * 16 + colour,
    /// 0 = see-through). Each 8x8 tile must use one colour row. Identical tiles (also mirrored) and blocks are shared.
    /// Returns a message about what was built, or throws when it doesn't fit.
    /// </summary>
    public string Rebuild(byte[] front, byte[] back)
    {
        int W = Width * 16;
        var tiles = new List<byte[]> { new byte[32] };       // tile 0 stays empty
        var tileKey = new Dictionary<string, int> { [Key(new byte[64])] = 0 };
        var blocks = new List<byte[]>();
        var blockKey = new Dictionary<string, int>();
        var layers = new Dictionary<bool, ushort[]>();
        foreach (bool isFront in new[] { true, false })
        {
            var idx = isFront ? front : back;
            var (old, lw, lh) = LayerOf(isFront);
            var layer = (ushort[])old.Clone();
            for (int by = 0; by < Height; by++)
                for (int bx = 0; bx < Width; bx++)
                {
                    var rec = new byte[9];
                    for (int q = 0; q < 4; q++)
                    {
                        int ox = bx * 16 + (q >> 1) * 8, oy = by * 16 + (q & 1) * 8;
                        var pix = new byte[64];
                        int row = -1;
                        for (int y = 0; y < 8; y++)
                            for (int x = 0; x < 8; x++)
                            {
                                int v = idx[(oy + y) * W + ox + x];
                                if (v == 0) continue;
                                if (row < 0) row = v >> 4;
                                else if (v >> 4 != row)
                                    throw new InvalidOperationException($"The {(isFront ? "front" : "back")} layer's 8x8 tile at pixel ({ox}, {oy}) uses two colour rows ({row} and {v >> 4}); every 8x8 tile can use only one row of 16 colours.");
                                pix[y * 8 + x] = (byte)(v & 15);
                            }
                        int word = TileWord(pix, tiles, tileKey) | Math.Max(row, 0) << 10;
                        rec[q * 2] = (byte)word; rec[q * 2 + 1] = (byte)(word >> 8);
                    }
                    string bk = Convert.ToHexString(rec);
                    if (!blockKey.TryGetValue(bk, out int b)) { b = blocks.Count; blocks.Add(rec); blockKey[bk] = b; }
                    layer[by * lw + bx] = (ushort)b;
                }
            // columns past the picture (the back layer is 32 wide, off-screen there) get an empty block
            if (lw > Width)
            {
                var empty = new byte[9];
                string ek = Convert.ToHexString(empty);
                if (!blockKey.TryGetValue(ek, out int eb)) { eb = blocks.Count; blocks.Add(empty); blockKey[ek] = eb; }
                for (int by = 0; by < lh; by++)
                    for (int bx = Width; bx < lw; bx++) layer[by * lw + bx] = (ushort)eb;
            }
            layers[isFront] = layer;
        }
        if (tiles.Count > MaxTiles) throw new InvalidOperationException($"The title needs {tiles.Count} different 8x8 tiles; the game has room for {MaxTiles}. Use fewer different details.");
        if (blocks.Count > MaxBlocks) throw new InvalidOperationException($"The title needs {blocks.Count} different 16x16 blocks; there is room for {MaxBlocks}.");
        var t = new byte[MaxTiles * 32];
        for (int i = 0; i < tiles.Count; i++) tiles[i].CopyTo(t, i * 32);
        Tiles = t;
        Blocks = blocks.SelectMany(b => b).ToArray();
        Array.Copy(layers[true], Map.Layer, Map.Layer.Length);
        if (Map.Layer2 != null) Array.Copy(layers[false], Map.Layer2, Map.Layer2.Length);
        return $"{tiles.Count} tiles of {MaxTiles}, {blocks.Count} blocks of {MaxBlocks}";
    }

    private static string Key(byte[] pix) => Convert.ToHexString(pix);

    /// <summary>Tile number (with flip bits) for 8x8 colour numbers, sharing identical or mirrored tiles.</summary>
    private static int TileWord(byte[] pix, List<byte[]> tiles, Dictionary<string, int> keys)
    {
        foreach (var (h, v) in new[] { (false, false), (true, false), (false, true), (true, true) })
        {
            var f = new byte[64];
            for (int y = 0; y < 8; y++) for (int x = 0; x < 8; x++) f[y * 8 + x] = pix[(v ? 7 - y : y) * 8 + (h ? 7 - x : x)];
            if (keys.TryGetValue(Key(f), out int n)) return n | (h ? 0x4000 : 0) | (v ? 0x8000 : 0);
        }
        var data = new byte[32];
        for (int y = 0; y < 8; y++)
            for (int x = 0; x < 8; x++)
            {
                int c = pix[y * 8 + x], bit = 7 - x;
                data[y * 2] |= (byte)((c & 1) << bit); data[y * 2 + 1] |= (byte)((c >> 1 & 1) << bit);
                data[16 + y * 2] |= (byte)((c >> 2 & 1) << bit); data[16 + y * 2 + 1] |= (byte)((c >> 3 & 1) << bit);
            }
        int id = tiles.Count;
        tiles.Add(data);
        keys[Key(pix)] = id;
        return id;
    }

    /// <summary>The title's map data with the current blocks and layers (the rest of the original kept).</summary>
    public byte[] BuildMapData()
    {
        var d = (byte[])Map.Original.Clone();
        Array.Clear(d, BlocksOffset, MaxBlocks * 9);
        Blocks.CopyTo(d, BlocksOffset);
        d[0x1E] = (byte)BlockCount; d[0x1F] = (byte)(BlockCount >> 8);
        for (int i = 0; i < Map.Layer.Length; i++) { d[Map.LayerOffset + i * 2] = (byte)Map.Layer[i]; d[Map.LayerOffset + i * 2 + 1] = (byte)(Map.Layer[i] >> 8); }
        if (Map.Layer2 != null)
        {
            int l2 = (int)(d[0x2C] | d[0x2D] << 8 | d[0x2E] << 16 | (uint)d[0x2F] << 24);
            for (int i = 0; i < Map.Layer2.Length; i++) { d[l2 + i * 2] = (byte)Map.Layer2[i]; d[l2 + i * 2 + 1] = (byte)(Map.Layer2[i] >> 8); }
        }
        int r = CycleRecord(d);
        if (r + 10 <= d.Length)
        {
            int flags = d[r] | d[r + 1] << 8;
            for (int k = 0; k < 2; k++)
            {
                var (on, count, delay) = Cycles[k];
                flags = on ? flags | 4 << k : flags & ~(4 << k);
                if (on) { d[r + 6 + k * 2] = (byte)(Math.Clamp(count, 2, 15) - 1); d[r + 7 + k * 2] = (byte)Math.Clamp(delay, 1, 255); }
            }
            d[r] = (byte)flags; d[r + 1] = (byte)(flags >> 8);
        }
        return d;
    }

    /// <summary>Writes tiles, map data and colours (rows 2-7) to the ROM; returns what moved.</summary>
    public string Save(RomBuffer rom, bool allowExpand)
    {
        var r1 = ResourceWriter.Save(rom, TilesResource, Tiles, allowExpand);
        var r2 = ResourceWriter.Save(rom, Map.ResourceId, BuildMapData(), allowExpand);
        for (int i = 0; i < 96; i++) rom.WriteUInt16Le(PaletteRows + i * 2, Palette555[32 + i]);
        return $"graphics {(r1.Moved ? "moved to the expanded space" : "in place")}, layout {(r2.Moved ? "moved to the expanded space" : "in place")}";
    }

    /// <summary>Bytes the graphics and layout need packed, against the room they have.</summary>
    public bool NeedsMoreSpace(RomBuffer rom) =>
        LufiaCompression.Compress(Tiles).Length > ResourceWriter.SlotSize(rom, TilesResource) ||
        LufiaCompression.Compress(BuildMapData()).Length > ResourceWriter.SlotSize(rom, Map.ResourceId);
}
