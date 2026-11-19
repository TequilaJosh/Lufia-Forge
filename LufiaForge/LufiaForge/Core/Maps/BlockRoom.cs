namespace LufiaForge.Core.Maps;

// makes room for new blocks on maps whose tileset has composite blocks (towns: tree tops, roofs...). on those maps every
// block number from the threshold up is a composite (a ground block with an overlay, from the tileset's pair table),
// so we can't just add blocks at the end.
// making room: the map gets its own copy of the tileset (if other maps share it), blank spare blocks go in at the map's
// threshold (the records after them move up), the pair table is copied with the moved records renumbered, the map's
// threshold goes up and its composite cells get renumbered. blocks under the threshold (the ones the game swaps at run
// time, chests and doors) keep their numbers. adding a block after that fills a spare
public static class BlockRoom
{
    public const int DefaultSpares = 32;

    // why room can't be made on this map, or null
    public static string? CantMakeRoom(RomBuffer rom, LufiaMap map)
    {
        if (map.IsWorld) return "The world map has a fixed set of blocks.";
        if (map.CompositeThreshold < 0) return "This map's tileset can already take new blocks.";
        if (PatternEvent(rom, map) is { } why) return why;
        int highestBlock = map.Layer.Max(c => c & 0x3FF);
        if (highestBlock + 1 > 0x3FF) return "This map already uses every block number (0-3FF).";
        return null;
    }

    // why the map's composite blocks can't be renumbered, or null. an event that stamps tile patterns
    // (event command 55, a puzzle) holds block numbers we don't renumber
    public static string? PatternEvent(RomBuffer rom, LufiaMap map)
    {
        int slots = MapEvents.TableLength(rom, map.MapId);
        for (int ev = 0; ev < slots; ev++)
        {
            EventScript? script;
            try { script = MapEvents.HasEvent(rom, map.MapId, ev) ? EventScript.Load(rom, map.MapId, ev) : null; } catch { continue; }
            if (script != null && script.Ops.Any(o => !o.IsText && o.Bytes.Length > 0 && o.Bytes[0] == 0x55))
                return $"Event {ev} of this map stamps tile patterns (a puzzle), whose block numbers can't be moved yet.";
        }
        return null;
    }

    // makes room for count new blocks on the map (see the top of the file), returns what was done
    public static string MakeRoom(RomBuffer rom, int mapId, int count = DefaultSpares)
    {
        var map = LufiaMap.Load(rom, mapId);
        if (CantMakeRoom(rom, map) is { } why) throw new InvalidOperationException(why);
        int threshold = map.CompositeThreshold;
        int highestBlock = map.Layer.Max(c => c & 0x3FF);
        count = Math.Min(count, 0x3FF - highestBlock);
        if (count <= 0) throw new InvalidOperationException("This map already uses every block number (0-3FF).");
        if (rom.Length < MapWriter.ExpandedSize) rom.Expand(MapWriter.ExpandedSize);

        var notes = new List<string>();
        int tileset = map.Tileset;
        bool shared = MapCatalog.Scan(rom).Any(m => m.MapId != mapId && m.MapId != LufiaMap.WorldMap && m.Tileset == tileset);
        if (shared)
        {
            tileset = Tilesets.Add(rom, map.Tileset);
            notes.Add($"the map now has its own tileset {tileset} (a copy of tileset {map.Tileset}), so other maps don't change");
        }

        // block records, the spares (blank = all zero) go in at the threshold
        var blocks = LufiaCompression.DecompressResource(rom, Tilesets.BlocksResource(rom, tileset), out _);
        int blockCount = blocks.Length / BlockSet.Size;
        if (threshold > blockCount) throw new InvalidOperationException($"The map's threshold ({threshold:X3}) is past the tileset's {blockCount} blocks.");
        var newBlocks = new byte[blocks.Length + count * BlockSet.Size];
        Array.Copy(blocks, 0, newBlocks, 0, threshold * BlockSet.Size);
        Array.Copy(blocks, threshold * BlockSet.Size, newBlocks, (threshold + count) * BlockSet.Size, blocks.Length - threshold * BlockSet.Size);

        // pair table: the entries any map of the tileset uses (so structures from those maps still fit here), with
        // blocks at or over the threshold renumbered
        int pairCount = Math.Max(Math.Max(0, highestBlock - threshold + 1), Prefabs.PairCount(rom, map.Tileset, MapCatalog.Scan(rom)));
        int oldPairs = Tilesets.CompositePairs(rom, tileset);
        var table = new byte[Math.Max(4, pairCount * 4)];
        for (int i = 0; i < pairCount; i++)
            for (int half = 0; half < 2; half++)
            {
                ushort word = rom.ReadUInt16Le(oldPairs + i * 4 + half * 2);
                if ((word & 0x3FF) >= threshold) word = (ushort)((word & 0xFC00) | ((word & 0x3FF) + count));
                table[i * 4 + half * 2] = (byte)word; table[i * 4 + half * 2 + 1] = (byte)(word >> 8);
            }
        var freeSpace = ExpansionSpace.Load(rom);
        int tableOffset = freeSpace.Allocate(table.Length, ExpansionSpace.Kind.Patch, 0x300 + tileset, EventScript.ScriptLimit);
        freeSpace.Save();
        rom.WriteBytes(tableOffset, table);
        Tilesets.SetCompositePairs(rom, tileset, tableOffset);

        var saveNote = ResourceWriter.Save(rom, Tilesets.BlocksResource(rom, tileset), newBlocks, allowExpand: true);

        // the map: its tileset, threshold and composite cells
        if (shared) map.SetTilesetAndPalette(tileset, map.PaletteIndex);
        map.ShiftComposites(count);
        MapWriter.CheckWram(rom, map.MapId, map.BuildData().Length, tileset, map.Width, map.Height, true, newBlocks);
        MapWriter.Save(rom, map, allowExpand: true);
        rom.FixChecksum();
        notes.Add($"{count} blank blocks ({threshold:X3}-{threshold + count - 1:X3}) are free for new blocks");
        return string.Join("; ", notes) + ".";
    }
}
