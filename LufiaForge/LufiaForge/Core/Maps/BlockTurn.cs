using LufiaForge.Core.Lufia2;

namespace LufiaForge.Core.Maps;

/// <summary>
/// Turned blocks for Lufia 1 maps: a block turned a quarter clockwise, flipped across or flipped up/down, as a block of
/// the map's tileset. A map cell can't turn its block, so the turned look is a block of its own: its picture turned (each
/// 8x8 tile turned too; the SNES can only mirror tiles, so a quarter turn draws new tiles unless the graphics have the
/// turned picture already, maybe mirrored) and its walls turned. A block of the tileset that draws the same with the
/// same walls is used as it is; otherwise one is added at the end, or into a spare block (<see cref="BlockRoom"/>) on a
/// tileset with composite blocks. A composite block (a roof or tree top drawn over a ground block) turns into a
/// composite the map has that looks the same, else into a plain block drawing the two together.
/// </summary>
public static class BlockTurn
{
    public sealed record Result(int[] Blocks, int Added, int NewTiles, string Where);

    // a wall side as the cell beside it on a 3 x 3 grid around the block: 01 = a wall on its left side (can't be entered
    // walking right), 02 = right side, 04 = top, 08 = bottom
    private static readonly (int Bit, int X, int Y)[] Sides =
        { (BlockSet.AttrRight, 0, 1), (BlockSet.AttrLeft, 2, 1), (BlockSet.AttrDown, 1, 0), (BlockSet.AttrUp, 1, 2) };

    /// <summary>A block's attribute byte with its walls turned (the other bits kept).</summary>
    public static byte TurnWalls(byte attr, int quarters, bool flip)
    {
        int o = attr & ~0x0F;
        foreach (var (bit, x, y) in Sides)
        {
            if ((attr & bit) == 0) continue;
            var (nx, ny) = L2BlockTurn.Point(x, y, 3, 3, quarters, flip);
            o |= Sides.First(s => s.X == nx && s.Y == ny).Bit;
        }
        return (byte)o;
    }

    /// <summary>Blocks <paramref name="blocks"/> of <paramref name="map"/> turned (<paramref name="quarters"/> quarter turns
    /// clockwise, after flipping across when <paramref name="flip"/>), new blocks and tiles written into the ROM (all or
    /// nothing). Throws <see cref="BlockBring.NeedsRoomException"/> when the tileset has composite blocks and no spare.</summary>
    public static Result Turn(RomBuffer rom, LufiaMap map, IReadOnlyList<int> blocks, int quarters, bool flip)
    {
        quarters &= 3;
        if (map.IsWorld) throw new InvalidOperationException("World map blocks can't be turned (the world map has a fixed set of blocks).");
        if (quarters == 0 && !flip) return new Result(blocks.ToArray(), 0, 0, "");
        var ts = MapTileset.ForMap(rom, map);
        var set = BlockSet.FromMap(rom, map);
        int limit = map.CompositeThreshold >= 0 ? Math.Min(map.CompositeThreshold, set.BlockCount) : set.BlockCount;

        int Priority(int b, int q)
        {
            if (ts.CompositeParts(b) is var (g, o)) return (Word(g, q) | Word(o, q)) & 0x2000;
            return Word(b, q) & 0x2000;
        }
        int Word(int b, int q) => b < set.BlockCount ? set.Word(b, q) : 0;
        byte Attr(int b) => ts.CompositeParts(b) is var (g, o) ? (byte)(ts.Attribute(g) | ts.Attribute(o)) : ts.Attribute(b);
        string Key(byte[] idx, byte attr, int[] prio) => Convert.ToHexString(idx) + attr.ToString("X2") + string.Concat(prio.Select(p => p != 0 ? '1' : '0'));
        string LookOf(int b) => Key(ts.MetatileIndices(b), Attr(b), Enumerable.Range(0, 4).Select(q => Priority(b, q)).ToArray());

        // the blocks a turned block may become: the tileset's plain blocks (not the blank spares, which new blocks fill)
        // and the composites this map and these blocks use
        var have = new Dictionary<string, int>();
        var spares = set.Spares.ToHashSet();
        for (int k = 0; k < limit; k++)
            if (!spares.Contains(k)) have.TryAdd(LookOf(k), k);
        if (map.CompositeThreshold >= 0)
            foreach (int k in map.Layer.Select(c => c & 0x3FF).Concat(blocks.Select(b => b & 0x3FF)).Where(c => c >= map.CompositeThreshold).Distinct().Order())
                if (ts.CompositeParts(k) is var (g, o) && g < limit && o < limit) have.TryAdd(LookOf(k), k);

        TileImport? import = null;
        var made = new Dictionary<string, int>();
        var done = new Dictionary<int, int>();
        int added = 0, newTiles = 0;
        var result = new int[blocks.Count];
        for (int i = 0; i < blocks.Count; i++)
        {
            int b = blocks[i] & 0x3FF;
            if (done.TryGetValue(b, out int got)) { result[i] = got; continue; }
            // the picture (palette row * 16 + colour, 0 see-through), each quarter's priority and the walls, turned
            var idx = ts.MetatileIndices(b);
            var t = new byte[256];
            for (int y = 0; y < 16; y++)
                for (int x = 0; x < 16; x++)
                {
                    var (nx, ny) = L2BlockTurn.Point(x, y, 16, 16, quarters, flip);
                    t[ny * 16 + nx] = idx[y * 16 + x];
                }
            var prio = new int[4];
            for (int q = 0; q < 4; q++)
            {
                var (qx, qy) = BlockSet.Quadrants[q];
                var (nx, ny) = L2BlockTurn.Point(qx, qy, 2, 2, quarters, flip);
                prio[Array.IndexOf(BlockSet.Quadrants, (nx, ny))] = Priority(b, q);
            }
            byte attr = TurnWalls(Attr(b), quarters, flip);
            string key = Key(t, attr, prio);
            if (!have.TryGetValue(key, out int found) && !made.TryGetValue(key, out found))
            {
                // a new block: each quarter one tile of the graphics (or a new one), in its own palette row
                import ??= new TileImport(rom, map, ts);
                var words = new ushort[4];
                uint[]? argb = null;
                for (int q = 0; q < 4; q++)
                {
                    var (qx, qy) = BlockSet.Quadrants[q];
                    var px = new byte[64];
                    for (int y = 0; y < 8; y++) for (int x = 0; x < 8; x++) px[y * 8 + x] = t[(qy * 8 + y) * 16 + qx * 8 + x];
                    var rows = px.Where(v => v != 0).Select(v => v >> 4).Distinct().ToList();
                    bool isNew;
                    if (rows.Count <= 1)
                    {
                        var (tile, h, v, n) = import.PlaceTile(px.Select(c => (byte)(c & 15)).ToArray());
                        isNew = n;
                        words[q] = BlockSet.MakeWord(tile, rows.Count == 0 ? 0 : rows[0], prio[q] != 0, h, v);
                    }
                    else
                    {
                        // a composite's two blocks in other palette rows meet in this tile: the closest colours of one row
                        argb ??= t.Select(c => c == 0 ? 0u : ts.Palette[c & 127]).ToArray();
                        words[q] = import.QuarterWord(argb, q, prio[q], out isNew);
                    }
                    if (isNew) newTiles++;
                }
                if (!set.CanAddBlocks)
                    throw map.CompositeThreshold >= 0
                        ? new BlockBring.NeedsRoomException("This map's tileset has composite blocks (roofs, tree tops) and no spare block left for the turned ones.")
                        : new InvalidOperationException($"Tileset {map.Tileset} is full (1024 blocks).");
                found = set.AddBlock(0);
                for (int q = 0; q < 4; q++) set.SetWord(found, q, words[q]);
                set.SetAttribute(found, attr);
                made[key] = found;
                added++;
            }
            done[b] = result[i] = found;
        }
        if (added == 0) return new Result(result, 0, 0, "");
        string where = L2Rom.AllOrNothing(rom, () =>
        {
            string blocksNote = set.Save(rom, allowExpand: true);
            string tilesNote = import?.SaveTiles(allowExpand: true) ?? "";
            return string.Join("; ", new[] { blocksNote, tilesNote }.Select(s => s.TrimEnd('.')).Where(s => s.Length > 0));
        });
        return new Result(result, added, newTiles, where);
    }
}
