namespace LufiaForge.Core.Maps;

/// <summary>
/// Blocks of other graphics (another map's tileset and palette) as blocks of this map's tileset, to paint with: a block
/// of the tileset that draws the same (same picture and walls) is used as it is; otherwise a new block is made of this
/// tileset's tiles (each 8x8 tile in the palette row closest to it, an existing tile when one matches, maybe mirrored,
/// or a new one in a free tile slot, as <see cref="TileImport"/> does for pictures) with the block's walls and priority.
/// New blocks go at the end, or into spare blocks (<see cref="BlockRoom"/>) on a tileset with composite blocks.
/// Every map with this tileset gets the new blocks; the ones they have keep their numbers.
/// </summary>
public static class BlockBring
{
    public sealed record Result(int[] Blocks, int Added, int NewTiles, string Where);

    /// <summary>The tileset has composite blocks and no spare left: room has to be made first (<see cref="BlockRoom"/>).</summary>
    public sealed class NeedsRoomException : InvalidOperationException
    {
        public NeedsRoomException(string message) : base(message) { }
    }

    /// <summary>The same graphics: the blocks are the map's own already.</summary>
    public static bool SameBlocks(RomBuffer rom, LufiaMap map, LufiaMap source) =>
        Tilesets.TilesResource(rom, map.Tileset) == Tilesets.TilesResource(rom, source.Tileset) &&
        Tilesets.BlocksResource(rom, map.Tileset) == Tilesets.BlocksResource(rom, source.Tileset);

    /// <summary>Blocks <paramref name="blocks"/> of <paramref name="source"/>'s graphics as blocks of
    /// <paramref name="map"/>'s tileset, written into the ROM (all or nothing).</summary>
    public static Result Bring(RomBuffer rom, LufiaMap map, LufiaMap source, IReadOnlyList<int> blocks)
    {
        if (map.IsWorld || source.IsWorld) throw new InvalidOperationException("The world map's blocks work differently.");
        if (SameBlocks(rom, map, source)) return new Result(blocks.ToArray(), 0, 0, "");
        var src = MapTileset.ForMap(rom, source);
        var dst = MapTileset.ForMap(rom, map);
        var set = BlockSet.FromMap(rom, map);
        var import = new TileImport(rom, map, dst);
        int limit = map.CompositeThreshold >= 0 ? Math.Min(map.CompositeThreshold, set.BlockCount) : set.BlockCount;
        var looks = new uint[]?[limit];
        uint[] Look(MapTileset ts, int b)
        {
            // the picture with see-through pixels left see-through (the backdrop colours of the two palettes differ)
            var px = (uint[])ts.MetatilePixels(b).Clone();
            var idx = ts.MetatileIndices(b);
            for (int i = 0; i < 256; i++) if ((idx[i] & 15) == 0) px[i] = 0;
            return px;
        }
        var done = new Dictionary<int, int>();
        var result = new int[blocks.Count];
        int added = 0, newTiles = 0;
        for (int i = 0; i < blocks.Count; i++)
        {
            int b = blocks[i] & 0x3FF;
            if (done.TryGetValue(b, out int got)) { result[i] = got; continue; }
            var px = Look(src, b);
            byte attr = src.CompositeParts(b) is var (g, o) ? (byte)(src.Attribute(g) | src.Attribute(o)) : src.Attribute(b);
            int found = -1;
            for (int k = 0; k < limit && found < 0; k++)
            {
                if (set.Attribute(k) != attr) continue;
                looks[k] ??= Look(dst, k);
                if (looks[k]!.AsSpan().SequenceEqual(px)) found = k;
            }
            if (found < 0)
            {
                var prio = new int[4];
                if (src.CompositeParts(b) == null && b < src.MetatileCount)
                    for (int q = 0; q < 4; q++) prio[q] = src.Blocks[b * MapTileset.MetatileSize + q * 2 + 1] << 8 & 0x2000;
                var words = import.Words(px, prio, out int nt);
                for (int k = 0; k < limit && found < 0; k++)
                    if (set.Attribute(k) == attr && Enumerable.Range(0, 4).All(q => set.Word(k, q) == words[q])) found = k;
                if (found < 0)
                {
                    if (!set.CanAddBlocks)
                        throw map.CompositeThreshold >= 0
                            ? new NeedsRoomException("This map's tileset has composite blocks (roofs, tree tops) and no spare block left for new ones.")
                            : new InvalidOperationException($"Tileset {map.Tileset} is full (1024 blocks).");
                    found = set.AddBlock(0);
                    for (int q = 0; q < 4; q++) set.SetWord(found, q, words[q]);
                    set.SetAttribute(found, attr);
                    added++;
                    newTiles += nt;
                }
            }
            done[b] = result[i] = found;
        }
        if (added == 0) return new Result(result, 0, 0, "");
        string where = Lufia2.L2Rom.AllOrNothing(rom, () =>
        {
            string blocksNote = set.Save(rom, allowExpand: true);
            string tilesNote = import.SaveTiles(allowExpand: true);
            return string.Join("; ", new[] { blocksNote, tilesNote }.Select(s => s.TrimEnd('.')).Where(s => s.Length > 0));
        });
        return new Result(result, added, newTiles, where);
    }
}
