namespace LufiaForge.Core.Maps;

// brings blocks from other graphics (another map's tileset and palette) in as blocks of this map's tileset, to paint with.
// if the tileset already has a block that draws the same (same picture and walls) we just use that. otherwise we make a
// new block out of this tileset's tiles: each 8x8 tile goes in the palette row closest to it, reusing an existing tile
// when one matches (maybe mirrored) or a new one in a free tile slot, same as TileImport does for pictures. the block
// keeps its walls and priority.
// new blocks go at the end, or into spare blocks (BlockRoom) on a tileset with composite blocks.
// every map with this tileset gets the new blocks, the ones they already have keep their numbers
public static class BlockBring
{
    public sealed record Result(int[] Blocks, int Added, int NewTiles, string Where);

    // the tileset has composite blocks and no spare left, so room has to be made first (BlockRoom)
    public sealed class NeedsRoomException : InvalidOperationException
    {
        public NeedsRoomException(string message) : base(message) { }
    }

    // same graphics, so the blocks are already the map's own
    public static bool SameBlocks(RomBuffer rom, LufiaMap map, LufiaMap source) =>
        Tilesets.TilesResource(rom, map.Tileset) == Tilesets.TilesResource(rom, source.Tileset) &&
        Tilesets.BlocksResource(rom, map.Tileset) == Tilesets.BlocksResource(rom, source.Tileset);

    // source's blocks as blocks of map's tileset, written into the rom (all or nothing)
    public static Result Bring(RomBuffer rom, LufiaMap map, LufiaMap source, IReadOnlyList<int> blocks)
    {
        if (map.IsWorld || source.IsWorld) throw new InvalidOperationException("The world map's blocks work differently.");
        if (SameBlocks(rom, map, source)) return new Result(blocks.ToArray(), 0, 0, "");
        var sourceTiles = MapTileset.ForMap(rom, source);
        var mapTiles = MapTileset.ForMap(rom, map);
        var blockSet = BlockSet.FromMap(rom, map);
        var import = new TileImport(rom, map, mapTiles);
        int limit = map.CompositeThreshold >= 0 ? Math.Min(map.CompositeThreshold, blockSet.BlockCount) : blockSet.BlockCount;
        var blockLooks = new uint[]?[limit];
        uint[] Look(MapTileset tileset, int block)
        {
            // see-through pixels stay see-through (the two palettes have different backdrop colours)
            var pixels = (uint[])tileset.MetatilePixels(block).Clone();
            var indices = tileset.MetatileIndices(block);
            for (int i = 0; i < 256; i++) if ((indices[i] & 15) == 0) pixels[i] = 0;
            return pixels;
        }
        var alreadyBrought = new Dictionary<int, int>();
        var result = new int[blocks.Count];
        int added = 0, newTiles = 0;
        for (int i = 0; i < blocks.Count; i++)
        {
            int block = blocks[i] & 0x3FF;
            if (alreadyBrought.TryGetValue(block, out int brought)) { result[i] = brought; continue; }
            var pixels = Look(sourceTiles, block);
            byte attribute = sourceTiles.CompositeParts(block) is var (ground, overlay) ? (byte)(sourceTiles.Attribute(ground) | sourceTiles.Attribute(overlay)) : sourceTiles.Attribute(block);
            int match = -1;
            for (int k = 0; k < limit && match < 0; k++)
            {
                if (blockSet.Attribute(k) != attribute) continue;
                blockLooks[k] ??= Look(mapTiles, k);
                if (blockLooks[k]!.AsSpan().SequenceEqual(pixels)) match = k;
            }
            if (match < 0)
            {
                var priority = new int[4];
                if (sourceTiles.CompositeParts(block) == null && block < sourceTiles.MetatileCount)
                    for (int q = 0; q < 4; q++) priority[q] = sourceTiles.Blocks[block * MapTileset.MetatileSize + q * 2 + 1] << 8 & 0x2000;
                var words = import.Words(pixels, priority, out int tilesMade);
                for (int k = 0; k < limit && match < 0; k++)
                    if (blockSet.Attribute(k) == attribute && Enumerable.Range(0, 4).All(q => blockSet.Word(k, q) == words[q])) match = k;
                if (match < 0)
                {
                    if (!blockSet.CanAddBlocks)
                        throw map.CompositeThreshold >= 0
                            ? new NeedsRoomException("This map's tileset has composite blocks (roofs, tree tops) and no spare block left for new ones.")
                            : new InvalidOperationException($"Tileset {map.Tileset} is full (1024 blocks).");
                    match = blockSet.AddBlock(0);
                    for (int q = 0; q < 4; q++) blockSet.SetWord(match, q, words[q]);
                    blockSet.SetAttribute(match, attribute);
                    added++;
                    newTiles += tilesMade;
                }
            }
            alreadyBrought[block] = result[i] = match;
        }
        if (added == 0) return new Result(result, 0, 0, "");
        string where = Lufia2.L2Rom.AllOrNothing(rom, () =>
        {
            string blocksNote = blockSet.Save(rom, allowExpand: true);
            string tilesNote = import.SaveTiles(allowExpand: true);
            return string.Join("; ", new[] { blocksNote, tilesNote }.Select(note => note.TrimEnd('.')).Where(note => note.Length > 0));
        });
        return new Result(result, added, newTiles, where);
    }
}
