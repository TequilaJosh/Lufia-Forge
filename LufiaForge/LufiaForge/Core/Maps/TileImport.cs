namespace LufiaForge.Core.Maps;

// puts new graphics into a map tileset: a picture (multiple of 16 x 16 pixels) becomes blocks, either redrawing
// existing blocks or adding new ones. each 8x8 tile gets drawn in the palette row (2-7, the map's colours) that
// matches it best, and transparent pixels and the backdrop colour become colour 0 (see-through).
// tiles that already exist (mirrored too) get reused, new ones go into tile slots that no block of any tileset
// sharing the graphics uses. the graphics and block resources get written back compressed (ResourceWriter), so
// every map using the tileset sees the change
public sealed class TileImport
{
    private readonly RomBuffer _rom;
    private readonly MapTileset _tileset;
    private readonly byte[] _tiles;
    private readonly byte[] _blocks;
    private readonly ushort[] _palette;
    private readonly HashSet<int> _usedTiles;
    private readonly int _compositeThreshold;

    public int TileCount => _tiles.Length / 32;
    public int BlockCount => _blocks.Length / MapTileset.MetatileSize;
    public int FreeTiles => Enumerable.Range(0, TileCount).Count(t => !_usedTiles.Contains(t));
    // new blocks can only go at the end when the tileset has no composite blocks above a threshold
    public bool CanAppendBlocks => _compositeThreshold < 0;

    public TileImport(RomBuffer rom, LufiaMap map, MapTileset ts)
    {
        _rom = rom; _tileset = ts;
        _tiles = (byte[])ts.Tiles.Clone();
        _blocks = (byte[])ts.Blocks.Clone();
        _palette = ts.Palette555;
        _compositeThreshold = map.CompositeThreshold;
        // every block resource that draws with these graphics
        _usedTiles = new HashSet<int>();
        var blockResources = new HashSet<int> { ts.BlocksResource };
        foreach (var mapInfo in MapCatalog.Scan(rom))
        {
            try
            {
                var other = LufiaMap.Load(rom, mapInfo.MapId);
                if (other.IsWorld) continue;
                var otherTileset = MapTileset.ForMap(rom, other);
                if (otherTileset.TilesResource == ts.TilesResource) blockResources.Add(otherTileset.BlocksResource);
            }
            catch { }
        }
        foreach (int resource in blockResources)
        {
            var blocks = resource == ts.BlocksResource ? _blocks : LufiaCompression.DecompressResource(rom, resource, out _);
            for (int i = 0; i + 8 < blocks.Length; i += MapTileset.MetatileSize)
                for (int q = 0; q < 4; q++) _usedTiles.Add((blocks[i + q * 2] | blocks[i + q * 2 + 1] << 8) & 0x3FF);
        }
    }

    public sealed record Result(int Blocks, int NewTiles, int ReusedTiles, string Report);

    // draws the picture (BGRA, width and height multiples of 16) into blocks: block k of the picture (left to right,
    // top to bottom) goes to targets[k], and a target equal to the current block count (or more) adds a block at the
    // end. attribute is the 9th byte (walkability) of new blocks, redrawn blocks keep theirs
    public Result Draw(uint[] pixels, int width, int height, IReadOnlyList<int> targets, byte attribute)
    {
        if (width % 16 != 0 || height % 16 != 0 || width == 0 || height == 0)
            throw new ArgumentException("The picture must be a multiple of 16 x 16 pixels.");
        int blocksWide = width / 16, blocksHigh = height / 16;
        if (targets.Count < blocksWide * blocksHigh) throw new ArgumentException($"The picture has {blocksWide * blocksHigh} blocks but only {targets.Count} places were given.");
        var blocks = new List<byte>(_blocks);
        int newTiles = 0, reused = 0;
        for (int k = 0; k < blocksWide * blocksHigh; k++)
        {
            int bx = k % blocksWide * 16, by = k / blocksWide * 16;
            int target = targets[k];
            if (target >= blocks.Count / MapTileset.MetatileSize)
            {
                if (!CanAppendBlocks) throw new InvalidOperationException("This tileset has composite blocks, so new blocks can't be added at the end; redraw existing blocks instead.");
                target = blocks.Count / MapTileset.MetatileSize;
                if (target >= 0x400) throw new InvalidOperationException("A tileset can't have more than 1024 blocks.");
                blocks.AddRange(new byte[MapTileset.MetatileSize]);
                blocks[target * MapTileset.MetatileSize + 8] = attribute;
            }
            int record = target * MapTileset.MetatileSize;
            int priority = (blocks[record + 1] << 8) & 0x2000;
            for (int q = 0; q < 4; q++)
            {
                int tx = bx + (q >= 2 ? 8 : 0), ty = by + ((q & 1) == 1 ? 8 : 0);   // TL, BL, TR, BR
                var (paletteRow, indices) = Quantise(pixels, width, tx, ty);
                var (tile, flipH, flipV, isNew) = Place(indices);
                if (isNew) newTiles++; else reused++;
                int word = tile | paletteRow << 10 | priority | (flipH ? 0x4000 : 0) | (flipV ? 0x8000 : 0);
                blocks[record + q * 2] = (byte)word; blocks[record + q * 2 + 1] = (byte)(word >> 8);
            }
        }
        _newBlocks = blocks.ToArray();
        return new Result(blocksWide * blocksHigh, newTiles, reused,
            $"{blocksWide * blocksHigh} block{(blocksWide * blocksHigh == 1 ? "" : "s")} drawn: {newTiles} new tiles, {reused} reused; {FreeTiles} free tile slots left.");
    }

    private byte[] _newBlocks = Array.Empty<byte>();
    private int _tilesPlaced;

    // the four tilemap words (TL, BL, TR, BR) that draw a 16 x 16 picture with these graphics: each 8x8 tile in its
    // best palette row, an existing tile (maybe mirrored) or a new one in a free slot, same as Draw does.
    // priority[q] is quadrant q's priority bit (0x2000 or 0). only the tiles change here, the caller puts the words
    // into a block (BlockSet) and saves the tiles with SaveTiles
    public ushort[] Words(uint[] px16, IReadOnlyList<int> priority, out int newTiles)
    {
        var words = new ushort[4];
        newTiles = 0;
        for (int q = 0; q < 4; q++)
        {
            words[q] = QuarterWord(px16, q, priority[q], out bool isNew);
            if (isNew) newTiles++;
        }
        return words;
    }

    // the tilemap word for quarter q (TL, BL, TR, BR) of a 16 x 16 picture, same as Words
    public ushort QuarterWord(uint[] px16, int q, int priority, out bool isNew)
    {
        int tx = q >= 2 ? 8 : 0, ty = (q & 1) == 1 ? 8 : 0;
        var (paletteRow, indices) = Quantise(px16, 16, tx, ty);
        var (tile, flipH, flipV, placedNew) = PlaceTile(indices);
        isNew = placedNew;
        return (ushort)(tile | paletteRow << 10 | priority & 0x2000 | (flipH ? 0x4000 : 0) | (flipV ? 0x8000 : 0));
    }

    // a tile that draws these 64 colour numbers (0-15): one of the graphics' tiles (maybe mirrored), or a new one in
    // a free slot (SaveTiles writes it)
    public (int Tile, bool H, bool V, bool New) PlaceTile(byte[] idx)
    {
        var placed = Place(idx);
        if (placed.New) _tilesPlaced++;
        return placed;
    }

    // writes the graphics when Words put new tiles in
    public string SaveTiles(bool allowExpand)
    {
        if (_tilesPlaced == 0) return "";
        var saved = ResourceWriter.Save(_rom, _tileset.TilesResource, _tiles, allowExpand);
        return saved.Moved ? $"tile graphics moved to 0x{saved.FileOffset:X6}" : "tile graphics written in place";
    }

    // the best palette row (2-7) for an 8x8 tile, and its 4bpp colour indices
    private (int Pal, byte[] Index) Quantise(uint[] px, int width, int x0, int y0)
    {
        int bestRow = 2; long bestErr = long.MaxValue; byte[] best = new byte[64];
        for (int row = 2; row < 8; row++)
        {
            long err = 0; var indices = new byte[64];
            for (int y = 0; y < 8; y++)
                for (int x = 0; x < 8; x++)
                {
                    uint pixel = px[(y0 + y) * width + x0 + x];
                    if (pixel >> 24 < 128) { indices[y * 8 + x] = 0; continue; }
                    int r = (int)(pixel >> 16 & 255) >> 3, g = (int)(pixel >> 8 & 255) >> 3, b = (int)(pixel & 255) >> 3;
                    // the backdrop colour (colour 0) stays see-through, like in the game's own blocks
                    if ((r | g << 5 | b << 10) == _palette[0]) { indices[y * 8 + x] = 0; continue; }
                    int bestIndex = 1; long bestError = long.MaxValue;
                    for (int c = 1; c < 16; c++)
                    {
                        int paletteColour = _palette[row * 16 + c];
                        int dr = r - (paletteColour & 31), dg = g - (paletteColour >> 5 & 31), db = b - (paletteColour >> 10 & 31);
                        long error = dr * dr * 3 + dg * dg * 4 + db * db * 2;
                        if (error < bestError) { bestError = error; bestIndex = c; }
                    }
                    indices[y * 8 + x] = (byte)bestIndex; err += bestError;
                }
            if (err < bestErr) { bestErr = err; bestRow = row; best = indices; }
        }
        return (bestRow, best);
    }

    // an existing identical tile (maybe mirrored), or a free slot holding the new one
    private (int Tile, bool H, bool V, bool New) Place(byte[] idx)
    {
        var variants = new (byte[] Px, bool H, bool V)[] { (idx, false, false), (Flip(idx, true, false), true, false), (Flip(idx, false, true), false, true), (Flip(idx, true, true), true, true) };
        for (int tile = 0; tile < TileCount; tile++)
        {
            if (!_usedTiles.Contains(tile)) continue;
            var existing = Decode(tile);
            foreach (var (variant, flipH, flipV) in variants)
                if (variant.AsSpan().SequenceEqual(existing)) return (tile, flipH, flipV, false);
        }
        int slot = Enumerable.Range(0, TileCount).FirstOrDefault(t => !_usedTiles.Contains(t), -1);
        if (slot < 0) throw new InvalidOperationException("No free tile slots left in this tileset's graphics.");
        Encode(slot, idx);
        _usedTiles.Add(slot);
        return (slot, false, false, true);
    }

    private static byte[] Flip(byte[] idx, bool h, bool v)
    {
        var flipped = new byte[64];
        for (int y = 0; y < 8; y++) for (int x = 0; x < 8; x++) flipped[y * 8 + x] = idx[(v ? 7 - y : y) * 8 + (h ? 7 - x : x)];
        return flipped;
    }

    private byte[] Decode(int tile)
    {
        var indices = new byte[64]; int tileOffset = tile * 32;
        for (int y = 0; y < 8; y++)
            for (int x = 0; x < 8; x++)
            {
                int shift = 7 - x;
                indices[y * 8 + x] = (byte)(((_tiles[tileOffset + y * 2] >> shift) & 1) | ((_tiles[tileOffset + y * 2 + 1] >> shift) & 1) << 1 |
                                      ((_tiles[tileOffset + 16 + y * 2] >> shift) & 1) << 2 | ((_tiles[tileOffset + 16 + y * 2 + 1] >> shift) & 1) << 3);
            }
        return indices;
    }

    private void Encode(int tile, byte[] idx)
    {
        int tileOffset = tile * 32;
        for (int y = 0; y < 8; y++)
        {
            byte p0 = 0, p1 = 0, p2 = 0, p3 = 0;
            for (int x = 0; x < 8; x++)
            {
                int c = idx[y * 8 + x], bit = 7 - x;
                p0 |= (byte)((c & 1) << bit); p1 |= (byte)((c >> 1 & 1) << bit); p2 |= (byte)((c >> 2 & 1) << bit); p3 |= (byte)((c >> 3 & 1) << bit);
            }
            _tiles[tileOffset + y * 2] = p0; _tiles[tileOffset + y * 2 + 1] = p1; _tiles[tileOffset + 16 + y * 2] = p2; _tiles[tileOffset + 16 + y * 2 + 1] = p3;
        }
    }

    // writes the graphics and blocks back to the rom
    public string Save(bool allowExpand)
    {
        if (_newBlocks.Length == 0) return "Nothing to write.";
        var tilesSaved = ResourceWriter.Save(_rom, _tileset.TilesResource, _tiles, allowExpand);
        var blocksSaved = ResourceWriter.Save(_rom, _tileset.BlocksResource, _newBlocks, allowExpand);
        return $"Graphics: {(tilesSaved.Moved ? $"moved to 0x{tilesSaved.FileOffset:X6}" : "written in place")}; blocks: {(blocksSaved.Moved ? $"moved to 0x{blocksSaved.FileOffset:X6}" : "written in place")}.";
    }

    // compressed sizes against the original slots (to ask before expanding the rom)
    public bool NeedsMoreSpace()
    {
        if (_newBlocks.Length == 0) return false;
        bool Over(int resource, byte[] data)
        {
            int slot = ResourceWriter.SlotSize(_rom, resource);
            return LufiaCompression.Compress(data).Length > slot;
        }
        return Over(_tileset.TilesResource, _tiles) || Over(_tileset.BlocksResource, _newBlocks);
    }
}
