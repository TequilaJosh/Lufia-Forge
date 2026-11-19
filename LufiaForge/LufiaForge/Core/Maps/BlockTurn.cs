using LufiaForge.Core.Lufia2;

namespace LufiaForge.Core.Maps;

// turned blocks for lufia 1 maps: a block turned a quarter clockwise, flipped across or flipped up/down, as a block of
// the map's tileset. a map cell can't turn its block, so the turned look has to be a block of its own, with its picture
// turned and its walls turned. each 8x8 tile gets turned too, and the snes can only mirror tiles, so a quarter turn
// draws new tiles unless the graphics already have the turned picture (maybe mirrored).
// if the tileset has a block that draws the same with the same walls we use that, otherwise one gets added at the end,
// or into a spare block (BlockRoom) on a tileset with composite blocks.
// a composite block (roof or tree top drawn over a ground block) turns into a composite the map has that looks the
// same, or else into a plain block that draws the two together
public static class BlockTurn
{
    public sealed record Result(int[] Blocks, int Added, int NewTiles, string Where);

    // a wall side as the cell next to it on a 3 x 3 grid around the block: 01 = wall on its left side (can't be entered
    // walking right), 02 = right side, 04 = top, 08 = bottom
    private static readonly (int Bit, int X, int Y)[] Sides =
        { (BlockSet.AttrRight, 0, 1), (BlockSet.AttrLeft, 2, 1), (BlockSet.AttrDown, 1, 0), (BlockSet.AttrUp, 1, 2) };

    // a block's attribute byte with its walls turned (the other bits stay)
    public static byte TurnWalls(byte attr, int quarters, bool flip)
    {
        int turned = attr & ~0x0F;
        foreach (var (bit, x, y) in Sides)
        {
            if ((attr & bit) == 0) continue;
            var (newX, newY) = L2BlockTurn.Point(x, y, 3, 3, quarters, flip);
            turned |= Sides.First(side => side.X == newX && side.Y == newY).Bit;
        }
        return (byte)turned;
    }

    // map's blocks turned (quarters quarter turns clockwise, after flipping across if flip), with the new blocks and
    // tiles written into the rom (all or nothing). throws BlockBring.NeedsRoomException when the tileset has composite
    // blocks and no spare
    public static Result Turn(RomBuffer rom, LufiaMap map, IReadOnlyList<int> blocks, int quarters, bool flip)
    {
        quarters &= 3;
        if (map.IsWorld) throw new InvalidOperationException("World map blocks can't be turned (the world map has a fixed set of blocks).");
        if (quarters == 0 && !flip) return new Result(blocks.ToArray(), 0, 0, "");
        var tileset = MapTileset.ForMap(rom, map);
        var blockSet = BlockSet.FromMap(rom, map);
        int limit = map.CompositeThreshold >= 0 ? Math.Min(map.CompositeThreshold, blockSet.BlockCount) : blockSet.BlockCount;

        int Priority(int block, int q)
        {
            if (tileset.CompositeParts(block) is var (ground, overlay)) return (Word(ground, q) | Word(overlay, q)) & 0x2000;
            return Word(block, q) & 0x2000;
        }
        int Word(int block, int q) => block < blockSet.BlockCount ? blockSet.Word(block, q) : 0;
        byte Attr(int block) => tileset.CompositeParts(block) is var (ground, overlay) ? (byte)(tileset.Attribute(ground) | tileset.Attribute(overlay)) : tileset.Attribute(block);
        string Key(byte[] indices, byte attr, int[] priority) => Convert.ToHexString(indices) + attr.ToString("X2") + string.Concat(priority.Select(p => p != 0 ? '1' : '0'));
        string LookOf(int block) => Key(tileset.MetatileIndices(block), Attr(block), Enumerable.Range(0, 4).Select(q => Priority(block, q)).ToArray());

        // what a turned block can become: the tileset's plain blocks (not the blank spares, new blocks fill those)
        // and the composites this map and these blocks use
        var existing = new Dictionary<string, int>();
        var spares = blockSet.Spares.ToHashSet();
        for (int k = 0; k < limit; k++)
            if (!spares.Contains(k)) existing.TryAdd(LookOf(k), k);
        if (map.CompositeThreshold >= 0)
            foreach (int k in map.Layer.Select(c => c & 0x3FF).Concat(blocks.Select(b => b & 0x3FF)).Where(c => c >= map.CompositeThreshold).Distinct().Order())
                if (tileset.CompositeParts(k) is var (ground, overlay) && ground < limit && overlay < limit) existing.TryAdd(LookOf(k), k);

        TileImport? import = null;
        var made = new Dictionary<string, int>();
        var alreadyTurned = new Dictionary<int, int>();
        int added = 0, newTiles = 0;
        var result = new int[blocks.Count];
        for (int i = 0; i < blocks.Count; i++)
        {
            int block = blocks[i] & 0x3FF;
            if (alreadyTurned.TryGetValue(block, out int turnedBefore)) { result[i] = turnedBefore; continue; }
            // turn the picture (palette row * 16 + colour, 0 = see-through), each quarter's priority and the walls
            var indices = tileset.MetatileIndices(block);
            var turnedPicture = new byte[256];
            for (int y = 0; y < 16; y++)
                for (int x = 0; x < 16; x++)
                {
                    var (newX, newY) = L2BlockTurn.Point(x, y, 16, 16, quarters, flip);
                    turnedPicture[newY * 16 + newX] = indices[y * 16 + x];
                }
            var priority = new int[4];
            for (int q = 0; q < 4; q++)
            {
                var (qx, qy) = BlockSet.Quadrants[q];
                var (newX, newY) = L2BlockTurn.Point(qx, qy, 2, 2, quarters, flip);
                priority[Array.IndexOf(BlockSet.Quadrants, (newX, newY))] = Priority(block, q);
            }
            byte attr = TurnWalls(Attr(block), quarters, flip);
            string key = Key(turnedPicture, attr, priority);
            if (!existing.TryGetValue(key, out int match) && !made.TryGetValue(key, out match))
            {
                // new block: each quarter is one tile of the graphics (or a new one), in its own palette row
                import ??= new TileImport(rom, map, tileset);
                var words = new ushort[4];
                uint[]? argb = null;
                for (int q = 0; q < 4; q++)
                {
                    var (qx, qy) = BlockSet.Quadrants[q];
                    var tilePixels = new byte[64];
                    for (int y = 0; y < 8; y++) for (int x = 0; x < 8; x++) tilePixels[y * 8 + x] = turnedPicture[(qy * 8 + y) * 16 + qx * 8 + x];
                    var rows = tilePixels.Where(v => v != 0).Select(v => v >> 4).Distinct().ToList();
                    bool isNew;
                    if (rows.Count <= 1)
                    {
                        var (tile, flipH, flipV, placedNew) = import.PlaceTile(tilePixels.Select(c => (byte)(c & 15)).ToArray());
                        isNew = placedNew;
                        words[q] = BlockSet.MakeWord(tile, rows.Count == 0 ? 0 : rows[0], priority[q] != 0, flipH, flipV);
                    }
                    else
                    {
                        // a composite's two blocks in different palette rows meet in this tile, so take the closest colours of one row
                        argb ??= turnedPicture.Select(c => c == 0 ? 0u : tileset.Palette[c & 127]).ToArray();
                        words[q] = import.QuarterWord(argb, q, priority[q], out isNew);
                    }
                    if (isNew) newTiles++;
                }
                if (!blockSet.CanAddBlocks)
                    throw map.CompositeThreshold >= 0
                        ? new BlockBring.NeedsRoomException("This map's tileset has composite blocks (roofs, tree tops) and no spare block left for the turned ones.")
                        : new InvalidOperationException($"Tileset {map.Tileset} is full (1024 blocks).");
                match = blockSet.AddBlock(0);
                for (int q = 0; q < 4; q++) blockSet.SetWord(match, q, words[q]);
                blockSet.SetAttribute(match, attr);
                made[key] = match;
                added++;
            }
            alreadyTurned[block] = result[i] = match;
        }
        if (added == 0) return new Result(result, 0, 0, "");
        string where = L2Rom.AllOrNothing(rom, () =>
        {
            string blocksNote = blockSet.Save(rom, allowExpand: true);
            string tilesNote = import?.SaveTiles(allowExpand: true) ?? "";
            return string.Join("; ", new[] { blocksNote, tilesNote }.Select(note => note.TrimEnd('.')).Where(note => note.Length > 0));
        });
        return new Result(result, added, newTiles, where);
    }
}
