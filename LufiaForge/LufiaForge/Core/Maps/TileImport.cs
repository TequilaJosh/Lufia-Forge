namespace LufiaForge.Core.Maps;

/// <summary>
/// Puts new graphics into a map tileset: a picture (multiple of 16 x 16 pixels) becomes blocks, either
/// redrawing existing blocks or adding new ones. Each 8x8 tile is drawn in the palette row (2-7, the map's
/// colours) that matches it best; transparent pixels and the backdrop colour become colour 0 (see-through).
/// Tiles that already exist (also mirrored) are reused; new ones go into tile slots no block of any tileset
/// sharing the graphics uses. The graphics and block resources are written back compressed
/// (<see cref="ResourceWriter"/>), so every map using the tileset sees the change.
/// </summary>
public sealed class TileImport
{
    private readonly RomBuffer _rom;
    private readonly MapTileset _ts;
    private readonly byte[] _tiles;
    private readonly byte[] _blocks;
    private readonly ushort[] _palette;
    private readonly HashSet<int> _usedTiles;
    private readonly int _compositeThreshold;

    public int TileCount => _tiles.Length / 32;
    public int BlockCount => _blocks.Length / MapTileset.MetatileSize;
    public int FreeTiles => Enumerable.Range(0, TileCount).Count(t => !_usedTiles.Contains(t));
    /// <summary>New blocks can go at the end only when the tileset has no composite blocks above a threshold.</summary>
    public bool CanAppendBlocks => _compositeThreshold < 0;

    public TileImport(RomBuffer rom, LufiaMap map, MapTileset ts)
    {
        _rom = rom; _ts = ts;
        _tiles = (byte[])ts.Tiles.Clone();
        _blocks = (byte[])ts.Blocks.Clone();
        _palette = ts.Palette555;
        _compositeThreshold = map.CompositeThreshold;
        // every block resource that draws with these graphics
        _usedTiles = new HashSet<int>();
        var blockResources = new HashSet<int> { ts.BlocksResource };
        foreach (var m in MapCatalog.Scan(rom))
        {
            try
            {
                var other = LufiaMap.Load(rom, m.MapId);
                if (other.IsWorld) continue;
                var ots = MapTileset.ForMap(rom, other);
                if (ots.TilesResource == ts.TilesResource) blockResources.Add(ots.BlocksResource);
            }
            catch { }
        }
        foreach (int res in blockResources)
        {
            var b = res == ts.BlocksResource ? _blocks : LufiaCompression.DecompressResource(rom, res, out _);
            for (int i = 0; i + 8 < b.Length; i += MapTileset.MetatileSize)
                for (int q = 0; q < 4; q++) _usedTiles.Add((b[i + q * 2] | b[i + q * 2 + 1] << 8) & 0x3FF);
        }
    }

    public sealed record Result(int Blocks, int NewTiles, int ReusedTiles, string Report);

    /// <summary>
    /// Draws the picture (BGRA, width and height multiples of 16) into blocks: block k of the picture (left to
    /// right, top to bottom) goes to <paramref name="targets"/>[k]; a target equal to the current block count
    /// (or more) adds a block at the end. <paramref name="attribute"/> is the 9th byte (walkability) of new
    /// blocks; redrawn blocks keep theirs.
    /// </summary>
    public Result Draw(uint[] pixels, int width, int height, IReadOnlyList<int> targets, byte attribute)
    {
        if (width % 16 != 0 || height % 16 != 0 || width == 0 || height == 0)
            throw new ArgumentException("The picture must be a multiple of 16 x 16 pixels.");
        int bw = width / 16, bh = height / 16;
        if (targets.Count < bw * bh) throw new ArgumentException($"The picture has {bw * bh} blocks but only {targets.Count} places were given.");
        var blocks = new List<byte>(_blocks);
        int newTiles = 0, reused = 0;
        for (int k = 0; k < bw * bh; k++)
        {
            int bx = k % bw * 16, by = k / bw * 16;
            int target = targets[k];
            if (target >= blocks.Count / MapTileset.MetatileSize)
            {
                if (!CanAppendBlocks) throw new InvalidOperationException("This tileset has composite blocks, so new blocks can't be added at the end; redraw existing blocks instead.");
                target = blocks.Count / MapTileset.MetatileSize;
                if (target >= 0x400) throw new InvalidOperationException("A tileset can't have more than 1024 blocks.");
                blocks.AddRange(new byte[MapTileset.MetatileSize]);
                blocks[target * MapTileset.MetatileSize + 8] = attribute;
            }
            int rec = target * MapTileset.MetatileSize;
            int priority = (blocks[rec + 1] << 8) & 0x2000;
            for (int q = 0; q < 4; q++)
            {
                int tx = bx + (q >= 2 ? 8 : 0), ty = by + ((q & 1) == 1 ? 8 : 0);   // TL, BL, TR, BR
                var (pal, idx) = Quantise(pixels, width, tx, ty);
                var (tile, hf, vf, isNew) = Place(idx);
                if (isNew) newTiles++; else reused++;
                int word = tile | pal << 10 | priority | (hf ? 0x4000 : 0) | (vf ? 0x8000 : 0);
                blocks[rec + q * 2] = (byte)word; blocks[rec + q * 2 + 1] = (byte)(word >> 8);
            }
        }
        _newBlocks = blocks.ToArray();
        return new Result(bw * bh, newTiles, reused,
            $"{bw * bh} block{(bw * bh == 1 ? "" : "s")} drawn: {newTiles} new tiles, {reused} reused; {FreeTiles} free tile slots left.");
    }

    private byte[] _newBlocks = Array.Empty<byte>();

    /// <summary>Best palette row (2-7) for an 8x8 tile and its 4bpp colour indices.</summary>
    private (int Pal, byte[] Index) Quantise(uint[] px, int width, int x0, int y0)
    {
        int bestRow = 2; long bestErr = long.MaxValue; byte[] best = new byte[64];
        for (int row = 2; row < 8; row++)
        {
            long err = 0; var idx = new byte[64];
            for (int y = 0; y < 8; y++)
                for (int x = 0; x < 8; x++)
                {
                    uint p = px[(y0 + y) * width + x0 + x];
                    if (p >> 24 < 128) { idx[y * 8 + x] = 0; continue; }
                    int r = (int)(p >> 16 & 255) >> 3, g = (int)(p >> 8 & 255) >> 3, b = (int)(p & 255) >> 3;
                    // the backdrop colour (colour 0) stays see-through, as in the game's own blocks
                    if ((r | g << 5 | b << 10) == _palette[0]) { idx[y * 8 + x] = 0; continue; }
                    int bi = 1; long be = long.MaxValue;
                    for (int c = 1; c < 16; c++)
                    {
                        int pc = _palette[row * 16 + c];
                        int dr = r - (pc & 31), dg = g - (pc >> 5 & 31), db = b - (pc >> 10 & 31);
                        long e = dr * dr * 3 + dg * dg * 4 + db * db * 2;
                        if (e < be) { be = e; bi = c; }
                    }
                    idx[y * 8 + x] = (byte)bi; err += be;
                }
            if (err < bestErr) { bestErr = err; bestRow = row; best = idx; }
        }
        return (bestRow, best);
    }

    /// <summary>An existing identical tile (maybe mirrored), or a free slot holding the new one.</summary>
    private (int Tile, bool H, bool V, bool New) Place(byte[] idx)
    {
        var variants = new (byte[] Px, bool H, bool V)[] { (idx, false, false), (Flip(idx, true, false), true, false), (Flip(idx, false, true), false, true), (Flip(idx, true, true), true, true) };
        for (int t = 0; t < TileCount; t++)
        {
            if (!_usedTiles.Contains(t)) continue;
            var existing = Decode(t);
            foreach (var (px, h, v) in variants)
                if (px.AsSpan().SequenceEqual(existing)) return (t, h, v, false);
        }
        int slot = Enumerable.Range(0, TileCount).FirstOrDefault(t => !_usedTiles.Contains(t), -1);
        if (slot < 0) throw new InvalidOperationException("No free tile slots left in this tileset's graphics.");
        Encode(slot, idx);
        _usedTiles.Add(slot);
        return (slot, false, false, true);
    }

    private static byte[] Flip(byte[] idx, bool h, bool v)
    {
        var o = new byte[64];
        for (int y = 0; y < 8; y++) for (int x = 0; x < 8; x++) o[y * 8 + x] = idx[(v ? 7 - y : y) * 8 + (h ? 7 - x : x)];
        return o;
    }

    private byte[] Decode(int t)
    {
        var o = new byte[64]; int a = t * 32;
        for (int y = 0; y < 8; y++)
            for (int x = 0; x < 8; x++)
            {
                int s = 7 - x;
                o[y * 8 + x] = (byte)(((_tiles[a + y * 2] >> s) & 1) | ((_tiles[a + y * 2 + 1] >> s) & 1) << 1 |
                                      ((_tiles[a + 16 + y * 2] >> s) & 1) << 2 | ((_tiles[a + 16 + y * 2 + 1] >> s) & 1) << 3);
            }
        return o;
    }

    private void Encode(int t, byte[] idx)
    {
        int a = t * 32;
        for (int y = 0; y < 8; y++)
        {
            byte p0 = 0, p1 = 0, p2 = 0, p3 = 0;
            for (int x = 0; x < 8; x++)
            {
                int c = idx[y * 8 + x], bit = 7 - x;
                p0 |= (byte)((c & 1) << bit); p1 |= (byte)((c >> 1 & 1) << bit); p2 |= (byte)((c >> 2 & 1) << bit); p3 |= (byte)((c >> 3 & 1) << bit);
            }
            _tiles[a + y * 2] = p0; _tiles[a + y * 2 + 1] = p1; _tiles[a + 16 + y * 2] = p2; _tiles[a + 16 + y * 2 + 1] = p3;
        }
    }

    /// <summary>Writes the graphics and blocks back to the ROM.</summary>
    public string Save(bool allowExpand)
    {
        if (_newBlocks.Length == 0) return "Nothing to write.";
        var t = ResourceWriter.Save(_rom, _ts.TilesResource, _tiles, allowExpand);
        var b = ResourceWriter.Save(_rom, _ts.BlocksResource, _newBlocks, allowExpand);
        return $"Graphics: {(t.Moved ? $"moved to 0x{t.FileOffset:X6}" : "written in place")}; blocks: {(b.Moved ? $"moved to 0x{b.FileOffset:X6}" : "written in place")}.";
    }

    /// <summary>Compressed sizes against the original slots (to ask before expanding the ROM).</summary>
    public bool NeedsMoreSpace()
    {
        if (_newBlocks.Length == 0) return false;
        bool Over(int res, byte[] data)
        {
            int slot = ResourceWriter.SlotSize(_rom, res);
            return LufiaCompression.Compress(data).Length > slot;
        }
        return Over(_ts.TilesResource, _tiles) || Over(_ts.BlocksResource, _newBlocks);
    }
}
