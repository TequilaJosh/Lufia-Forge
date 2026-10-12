using LufiaForge.Modules.TileViewer;

namespace LufiaForge.Core.Maps;

/// <summary>
/// The tiles and 16x16 blocks a map draws with, for the Block editor: a town / dungeon tileset (tile graphics and
/// block resources, shared by every map using the tileset) or the world map (fixed tile resource 0x124, block
/// records inside the world map's data).
/// <para>Block record (9 bytes): four SNES tilemap words - top-left, bottom-left, top-right, bottom-right (bits 0-9
/// tile, 10-12 palette row, 13 priority, 14 flip X, 15 flip Y) - and an attribute byte. Attribute, checked in game:
/// the game tests the block being walked INTO. Bit 0 (01) = it can't be entered walking right (a wall on its left
/// side), 1 (02) = walking left (wall on its right side), 2 (04) = walking down (wall on its top), 3 (08) = walking
/// up (wall on its bottom), 5 (20) = it can't be entered at all; 4 (10) and 6 (40) don't change walking.</para>
/// </summary>
public sealed class BlockSet
{
    public const int Size = 9;
    public const int AttrRight = 0x01, AttrLeft = 0x02, AttrDown = 0x04, AttrUp = 0x08, AttrFlag10 = 0x10, AttrBlocked = 0x20, AttrFlag40 = 0x40, AttrFlag80 = 0x80;
    /// <summary>Order of the four tilemap words in a record: quadrant (x, y).</summary>
    public static readonly (int X, int Y)[] Quadrants = { (0, 0), (0, 1), (1, 0), (1, 1) };

    public string Title { get; }
    public byte[] Tiles { get; private set; }
    public byte[] Blocks { get; private set; }
    /// <summary>128 colours (rows 0-7) as BGR555.</summary>
    public ushort[] Palette { get; }
    public int TilesResource { get; }
    public int BlocksResource { get; }
    public int Tileset { get; }
    public LufiaMap Map { get; }
    public bool IsWorld => Map.IsWorld;
    /// <summary>First composite block (blocks from here on are (ground, overlay) pairs drawn by the game), or -1.</summary>
    public int CompositeThreshold { get; }
    public int TileCount => Tiles.Length / 32;
    public int BlockCount => Blocks.Length / Size;
    public bool TilesChanged { get; private set; }
    public bool BlocksChanged { get; private set; }

    private BlockSet(LufiaMap map, MapTileset ts, int tilesResource, int blocksResource)
    {
        Map = map;
        Tileset = map.Tileset;
        TilesResource = tilesResource;
        BlocksResource = blocksResource;
        Tiles = (byte[])ts.Tiles.Clone();
        Blocks = (byte[])ts.Blocks.Clone();
        Palette = new ushort[128];
        for (int i = 0; i < 128; i++)
        {
            uint c = ts.Palette[i];
            int r = (int)(c >> 16 & 0xFF), g = (int)(c >> 8 & 0xFF), b = (int)(c & 0xFF);
            Palette[i] = (ushort)((r * 31 + 127) / 255 | (g * 31 + 127) / 255 << 5 | (b * 31 + 127) / 255 << 10);
        }
        CompositeThreshold = ts.CompositeThreshold;
        Title = map.IsWorld ? "World map blocks (map 01)" : $"Tileset {map.Tileset} (used by every map with tileset {map.Tileset})";
    }

    public static BlockSet FromMap(RomBuffer rom, LufiaMap map)
    {
        var ts = MapTileset.ForMap(rom, map);
        return new BlockSet(map, ts, ts.TilesResource, ts.BlocksResource);
    }

    // ── block records ─────────────────────────────────────────────────────

    public ushort Word(int block, int quadrant) => (ushort)(Blocks[block * Size + quadrant * 2] | Blocks[block * Size + quadrant * 2 + 1] << 8);
    public void SetWord(int block, int quadrant, ushort w)
    {
        Blocks[block * Size + quadrant * 2] = (byte)w; Blocks[block * Size + quadrant * 2 + 1] = (byte)(w >> 8);
        BlocksChanged = true;
    }
    public byte Attribute(int block) => Blocks[block * Size + 8];
    public void SetAttribute(int block, byte a) { Blocks[block * Size + 8] = a; BlocksChanged = true; }

    public static int TileOf(ushort w) => w & 0x3FF;
    public static int RowOf(ushort w) => w >> 10 & 7;
    public static bool Priority(ushort w) => (w & 0x2000) != 0;
    public static bool FlipX(ushort w) => (w & 0x4000) != 0;
    public static bool FlipY(ushort w) => (w & 0x8000) != 0;
    public static ushort MakeWord(int tile, int row, bool priority, bool flipX, bool flipY) =>
        (ushort)(tile & 0x3FF | (row & 7) << 10 | (priority ? 0x2000 : 0) | (flipX ? 0x4000 : 0) | (flipY ? 0x8000 : 0));

    /// <summary>Blocks can be added at the end unless the tileset has composite blocks there (or it is the world map,
    /// whose block count is fixed by its chunk table).</summary>
    public bool CanAddBlocks => !IsWorld && CompositeThreshold < 0 && BlockCount < 1024;

    /// <summary>Adds a copy of <paramref name="block"/> at the end; returns its number.</summary>
    public int AddBlock(int block)
    {
        if (!CanAddBlocks) throw new InvalidOperationException(IsWorld ? "The world map has a fixed number of blocks." : "This tileset has composite blocks after its normal ones, so blocks can't be added at the end.");
        var b = new byte[Blocks.Length + Size];
        Blocks.CopyTo(b, 0);
        Array.Copy(Blocks, block * Size, b, Blocks.Length, Size);
        Blocks = b;
        BlocksChanged = true;
        return BlockCount - 1;
    }

    // ── pixels ────────────────────────────────────────────────────────────

    public uint Argb(int index)
    {
        ushort c = Palette[index & 127];
        return 0xFF000000u | (uint)((c & 31) * 255 / 31) << 16 | (uint)((c >> 5 & 31) * 255 / 31) << 8 | (uint)((c >> 10 & 31) * 255 / 31);
    }

    /// <summary>The 64 colour numbers (0-15) of a tile.</summary>
    public byte[] TileIndices(int tile) =>
        tile < TileCount ? SnesTileDecoder.DecodeTile(Tiles, tile * 32, BitDepth.Bpp4) : new byte[64];

    /// <summary>16x16 ARGB pixels of a block as drawn (colour 0 = the backdrop, palette colour 0).</summary>
    public uint[] BlockPixels(int block)
    {
        var px = new uint[256];
        uint backdrop = Argb(0);
        for (int q = 0; q < 4; q++)
        {
            ushort w = Word(block, q);
            var t = TileIndices(TileOf(w));
            var (qx, qy) = Quadrants[q];
            for (int y = 0; y < 8; y++)
                for (int x = 0; x < 8; x++)
                {
                    int sx = FlipX(w) ? 7 - x : x, sy = FlipY(w) ? 7 - y : y;
                    int ci = t[sy * 8 + sx];
                    px[(qy * 8 + y) * 16 + qx * 8 + x] = ci == 0 ? backdrop : Argb(RowOf(w) * 16 + ci);
                }
        }
        return px;
    }

    // ── tile sheet (16 tiles across) ──────────────────────────────────────

    /// <summary>Colour numbers (0-15) of the whole tile sheet, 128 pixels wide.</summary>
    public byte[] SheetIndices(out int width, out int height)
    {
        width = 128; height = (TileCount + 15) / 16 * 8;
        var px = new byte[width * height];
        for (int t = 0; t < TileCount; t++)
        {
            var ti = TileIndices(t);
            int ox = t % 16 * 8, oy = t / 16 * 8;
            for (int y = 0; y < 8; y++) for (int x = 0; x < 8; x++) px[(oy + y) * width + ox + x] = ti[y * 8 + x];
        }
        return px;
    }

    /// <summary>Replaces the tile graphics from colour numbers (0-15) of a sheet 128 pixels wide, keeping the tile
    /// count (extra rows are ignored, missing ones keep their tiles).</summary>
    public int SetSheet(byte[] indices, int width, int height)
    {
        if (width != 128) throw new InvalidOperationException($"The tile sheet must be 128 pixels wide (16 tiles); this one is {width}.");
        int changed = 0;
        int rows = Math.Min(height / 8, (TileCount + 15) / 16);
        for (int t = 0; t < Math.Min(TileCount, rows * 16); t++)
        {
            var px = new byte[64];
            int ox = t % 16 * 8, oy = t / 16 * 8;
            for (int y = 0; y < 8; y++) for (int x = 0; x < 8; x++) px[y * 8 + x] = (byte)(indices[(oy + y) * width + ox + x] & 15);
            var enc = SnesTileDecoder.EncodeTile(px, BitDepth.Bpp4);
            if (!Tiles.AsSpan(t * 32, 32).SequenceEqual(enc)) { enc.CopyTo(Tiles, t * 32); changed++; }
        }
        if (changed > 0) TilesChanged = true;
        return changed;
    }

    // ── saving ────────────────────────────────────────────────────────────

    /// <summary>Writes the changed graphics and blocks; returns a short report.</summary>
    public string Save(RomBuffer rom, bool allowExpand)
    {
        var notes = new List<string>();
        if (TilesChanged)
        {
            var r = ResourceWriter.Save(rom, TilesResource, Tiles, allowExpand);
            notes.Add(r.Moved ? $"tile graphics moved to 0x{r.FileOffset:X6}" : "tile graphics written in place");
            TilesChanged = false;
        }
        if (BlocksChanged)
        {
            if (IsWorld)
            {
                Blocks.CopyTo(Map.WorldBlocks, 0);
                var r = MapWriter.Save(rom, Map, allowExpand);
                notes.Add(r.Moved ? "world map data moved" : "world map data written in place");
            }
            else
            {
                var r = ResourceWriter.Save(rom, BlocksResource, Blocks, allowExpand);
                notes.Add(r.Moved ? $"blocks moved to 0x{r.FileOffset:X6}" : "blocks written in place");
            }
            BlocksChanged = false;
        }
        rom.FixChecksum();
        return notes.Count == 0 ? "Nothing to write." : string.Join(", ", notes) + ".";
    }
}
