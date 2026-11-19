using LufiaForge.Modules.TileViewer;

namespace LufiaForge.Core.Maps;

// the tiles and 16x16 blocks a map draws with, for the Block editor. either a town / dungeon tileset (tile graphics and
// block resources, shared by every map using that tileset) or the world map (fixed tile resource 0x124, block records
// inside the world map's data).
// block record is 9 bytes: four snes tilemap words (top-left, bottom-left, top-right, bottom-right; bits 0-9 tile,
// 10-12 palette row, 13 priority, 14 flip X, 15 flip Y) then an attribute byte.
// attribute, checked in game: the game tests the block you're walking INTO.
// - bit 0 (01) = can't be entered walking right (wall on its left side)
// - bit 1 (02) = walking left (wall on its right side)
// - bit 2 (04) = walking down (wall on its top)
// - bit 3 (08) = walking up (wall on its bottom)
// - bit 5 (20) = can't be entered at all
// - 4 (10) and 6 (40) don't change walking
public sealed class BlockSet
{
    public const int Size = 9;
    public const int AttrRight = 0x01, AttrLeft = 0x02, AttrDown = 0x04, AttrUp = 0x08, AttrFlag10 = 0x10, AttrBlocked = 0x20, AttrFlag40 = 0x40, AttrFlag80 = 0x80;
    // order of the four tilemap words in a record, as quadrant (x, y)
    public static readonly (int X, int Y)[] Quadrants = { (0, 0), (0, 1), (1, 0), (1, 1) };

    public string Title { get; }
    public byte[] Tiles { get; private set; }
    public byte[] Blocks { get; private set; }
    // 128 colours (rows 0-7) as BGR555
    public ushort[] Palette { get; }
    public int TilesResource { get; }
    public int BlocksResource { get; }
    public int Tileset { get; }
    public LufiaMap Map { get; }
    public bool IsWorld => Map.IsWorld;
    // first composite block (from here on blocks are (ground, overlay) pairs the game draws), or -1
    public int CompositeThreshold { get; }
    public int TileCount => Tiles.Length / 32;
    public int BlockCount => Blocks.Length / Size;
    public bool TilesChanged { get; private set; }
    public bool BlocksChanged { get; private set; }

    private BlockSet(LufiaMap map, MapTileset tileset, int tilesResource, int blocksResource)
    {
        Map = map;
        Tileset = map.Tileset;
        TilesResource = tilesResource;
        BlocksResource = blocksResource;
        Tiles = (byte[])tileset.Tiles.Clone();
        Blocks = (byte[])tileset.Blocks.Clone();
        Palette = new ushort[128];
        for (int i = 0; i < 128; i++)
        {
            uint colour = tileset.Palette[i];
            int r = (int)(colour >> 16 & 0xFF), g = (int)(colour >> 8 & 0xFF), b = (int)(colour & 0xFF);
            Palette[i] = (ushort)((r * 31 + 127) / 255 | (g * 31 + 127) / 255 << 5 | (b * 31 + 127) / 255 << 10);
        }
        CompositeThreshold = tileset.CompositeThreshold;
        Title = map.IsWorld ? "World map blocks (map 01)" : $"Tileset {map.Tileset} (used by every map with tileset {map.Tileset})";
    }

    public static BlockSet FromMap(RomBuffer rom, LufiaMap map)
    {
        var tileset = MapTileset.ForMap(rom, map);
        var blockSet = new BlockSet(map, tileset, tileset.TilesResource, tileset.BlocksResource);
        if (!map.IsWorld && blockSet.CompositeThreshold >= 0) blockSet.FindSpares(rom);
        return blockSet;
    }

    // on a tileset with composite blocks: the blank blocks (all zero, from Make room) under every map's threshold
    // that no map with this tileset uses (in cells or composite pairs). new blocks go there
    public List<int> Spares { get; } = new();

    private void FindSpares(RomBuffer rom)
    {
        var used = new HashSet<int>();
        int lowestThreshold = int.MaxValue;
        int pairTable = Tilesets.CompositePairs(rom, Tileset);
        foreach (var info in MapCatalog.Scan(rom))
        {
            if (info.MapId == LufiaMap.WorldMap || info.Tileset != Tileset) continue;
            LufiaMap otherMap;
            try { otherMap = info.MapId == Map.MapId ? Map : LufiaMap.Load(rom, info.MapId); } catch { continue; }
            int threshold = otherMap.CompositeThreshold < 0 ? BlockCount : otherMap.CompositeThreshold;
            lowestThreshold = Math.Min(lowestThreshold, threshold);
            foreach (var cell in otherMap.Layer)
            {
                int block = cell & 0x3FF;
                if (block < threshold) used.Add(block);
                else if (otherMap.CompositeThreshold >= 0)
                {
                    int pairEntry = pairTable + (block - threshold) * 4;
                    if (pairEntry + 4 <= rom.Length) { used.Add(rom.ReadUInt16Le(pairEntry) & 0x3FF); used.Add(rom.ReadUInt16Le(pairEntry + 2) & 0x3FF); }
                }
            }
        }
        for (int block = 1; block < Math.Min(lowestThreshold, BlockCount); block++)
            if (!used.Contains(block) && Blocks.AsSpan(block * Size, Size).IndexOfAnyExcept((byte)0) < 0) Spares.Add(block);
    }

    // block records

    public ushort Word(int block, int quadrant) => (ushort)(Blocks[block * Size + quadrant * 2] | Blocks[block * Size + quadrant * 2 + 1] << 8);
    public void SetWord(int block, int quadrant, ushort word)
    {
        Blocks[block * Size + quadrant * 2] = (byte)word; Blocks[block * Size + quadrant * 2 + 1] = (byte)(word >> 8);
        BlocksChanged = true;
    }
    public byte Attribute(int block) => Blocks[block * Size + 8];
    public void SetAttribute(int block, byte attribute) { Blocks[block * Size + 8] = attribute; BlocksChanged = true; }

    public static int TileOf(ushort word) => word & 0x3FF;
    public static int RowOf(ushort word) => word >> 10 & 7;
    public static bool Priority(ushort word) => (word & 0x2000) != 0;
    public static bool FlipX(ushort word) => (word & 0x4000) != 0;
    public static bool FlipY(ushort word) => (word & 0x8000) != 0;
    public static ushort MakeWord(int tile, int row, bool priority, bool flipX, bool flipY) =>
        (ushort)(tile & 0x3FF | (row & 7) << 10 | (priority ? 0x2000 : 0) | (flipX ? 0x4000 : 0) | (flipY ? 0x8000 : 0));

    // blocks can go at the end unless the tileset has composite blocks there (or it's the world map,
    // whose block count is fixed by its chunk table)
    public bool CanAddBlocks => !IsWorld && (CompositeThreshold < 0 ? BlockCount < 1024 : Spares.Count > 0);

    // adds a copy of block at the end, returns its number
    public int AddBlock(int block)
    {
        if (!CanAddBlocks) throw new InvalidOperationException(IsWorld ? "The world map has a fixed number of blocks." : "This map's tileset has no spare blocks left: use Make room for new blocks.");
        if (CompositeThreshold >= 0)
        {
            // use a spare blank block (Make room) instead of the end, the end is composite on this map
            int spare = Spares[0];
            Spares.RemoveAt(0);
            Array.Copy(Blocks, block * Size, Blocks, spare * Size, Size);
            BlocksChanged = true;
            return spare;
        }
        var grown = new byte[Blocks.Length + Size];
        Blocks.CopyTo(grown, 0);
        Array.Copy(Blocks, block * Size, grown, Blocks.Length, Size);
        Blocks = grown;
        BlocksChanged = true;
        return BlockCount - 1;
    }

    // pixels

    public uint Argb(int index)
    {
        ushort colour = Palette[index & 127];
        return 0xFF000000u | (uint)((colour & 31) * 255 / 31) << 16 | (uint)((colour >> 5 & 31) * 255 / 31) << 8 | (uint)((colour >> 10 & 31) * 255 / 31);
    }

    // the 64 colour numbers (0-15) of a tile
    public byte[] TileIndices(int tile) =>
        tile < TileCount ? SnesTileDecoder.DecodeTile(Tiles, tile * 32, BitDepth.Bpp4) : new byte[64];

    // 16x16 ARGB pixels of a block as drawn (colour 0 = the backdrop, palette colour 0)
    public uint[] BlockPixels(int block)
    {
        var pixels = new uint[256];
        uint backdrop = Argb(0);
        for (int q = 0; q < 4; q++)
        {
            ushort word = Word(block, q);
            var tilePixels = TileIndices(TileOf(word));
            var (qx, qy) = Quadrants[q];
            for (int y = 0; y < 8; y++)
                for (int x = 0; x < 8; x++)
                {
                    int sourceX = FlipX(word) ? 7 - x : x, sourceY = FlipY(word) ? 7 - y : y;
                    int colourIndex = tilePixels[sourceY * 8 + sourceX];
                    pixels[(qy * 8 + y) * 16 + qx * 8 + x] = colourIndex == 0 ? backdrop : Argb(RowOf(word) * 16 + colourIndex);
                }
        }
        return pixels;
    }

    // tile sheet (16 tiles across)

    // colour numbers (0-15) of the whole tile sheet, 128 pixels wide
    public byte[] SheetIndices(out int width, out int height)
    {
        width = 128; height = (TileCount + 15) / 16 * 8;
        var pixels = new byte[width * height];
        for (int tile = 0; tile < TileCount; tile++)
        {
            var tilePixels = TileIndices(tile);
            int left = tile % 16 * 8, top = tile / 16 * 8;
            for (int y = 0; y < 8; y++) for (int x = 0; x < 8; x++) pixels[(top + y) * width + left + x] = tilePixels[y * 8 + x];
        }
        return pixels;
    }

    // replaces the tile graphics from the colour numbers (0-15) of a sheet 128 pixels wide. the tile count stays the
    // same (extra rows are ignored, missing ones keep their tiles)
    public int SetSheet(byte[] indices, int width, int height)
    {
        if (width != 128) throw new InvalidOperationException($"The tile sheet must be 128 pixels wide (16 tiles); this one is {width}.");
        int changed = 0;
        int rows = Math.Min(height / 8, (TileCount + 15) / 16);
        for (int tile = 0; tile < Math.Min(TileCount, rows * 16); tile++)
        {
            var tilePixels = new byte[64];
            int left = tile % 16 * 8, top = tile / 16 * 8;
            for (int y = 0; y < 8; y++) for (int x = 0; x < 8; x++) tilePixels[y * 8 + x] = (byte)(indices[(top + y) * width + left + x] & 15);
            var encoded = SnesTileDecoder.EncodeTile(tilePixels, BitDepth.Bpp4);
            if (!Tiles.AsSpan(tile * 32, 32).SequenceEqual(encoded)) { encoded.CopyTo(Tiles, tile * 32); changed++; }
        }
        if (changed > 0) TilesChanged = true;
        return changed;
    }

    // saving

    // writes the changed graphics and blocks, returns a short report. all or nothing: before, the tiles went in (and
    // stopped counting as changed) before the blocks could fail, which left half a write outside Undo
    public string Save(RomBuffer rom, bool allowExpand)
    {
        var before = rom.ToArray();
        bool tilesWereChanged = TilesChanged, blocksWereChanged = BlocksChanged;
        try { return SaveCore(rom, allowExpand); }
        catch
        {
            if (!rom.SameAs(before)) rom.ReplaceAll(before);
            TilesChanged = tilesWereChanged; BlocksChanged = blocksWereChanged;
            throw;
        }
    }

    private string SaveCore(RomBuffer rom, bool allowExpand)
    {
        if (BlocksChanged && !IsWorld)
        {
            // every map on this tileset unpacks these blocks after its own data, so each one still has to fit in work RAM
            foreach (var entry in MapCatalog.Scan(rom))
            {
                LufiaMap otherMap;
                try { otherMap = LufiaMap.Load(rom, entry.MapId); } catch { continue; }
                if (otherMap.IsWorld || otherMap.Tileset != Tileset) continue;
                MapWriter.CheckWram(rom, otherMap.MapId, otherMap.Original.Length, Tileset, otherMap.Width, otherMap.Height, otherMap.CompositeThreshold >= 0, Blocks);
            }
        }
        var notes = new List<string>();
        if (TilesChanged)
        {
            var saved = ResourceWriter.Save(rom, TilesResource, Tiles, allowExpand);
            notes.Add(saved.Moved ? $"tile graphics moved to 0x{saved.FileOffset:X6}" : "tile graphics written in place");
            TilesChanged = false;
        }
        if (BlocksChanged)
        {
            if (IsWorld)
            {
                Blocks.CopyTo(Map.WorldBlocks, 0);
                var saved = MapWriter.Save(rom, Map, allowExpand);
                notes.Add(saved.Moved ? "world map data moved" : "world map data written in place");
            }
            else
            {
                var saved = ResourceWriter.Save(rom, BlocksResource, Blocks, allowExpand);
                notes.Add(saved.Moved ? $"blocks moved to 0x{saved.FileOffset:X6}" : "blocks written in place");
            }
            BlocksChanged = false;
        }
        rom.FixChecksum();
        return notes.Count == 0 ? "Nothing to write." : string.Join(", ", notes) + ".";
    }
}
