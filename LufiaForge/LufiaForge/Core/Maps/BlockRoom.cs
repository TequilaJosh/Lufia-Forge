namespace LufiaForge.Core.Maps;

/// <summary>
/// Room for new blocks on maps whose tileset has composite blocks (towns: tree tops, roofs...). On such a map every
/// block number from its threshold up is a composite (a ground block with an overlay, from the tileset's pair table),
/// so blocks can't simply be added at the end. Making room gives the map its own copy of the tileset (when other maps
/// share it), inserts blank spare blocks at the map's threshold (records after them move up), copies the pair table
/// with the moved records renumbered, raises the map's threshold and renumbers its composite cells. Blocks below the
/// threshold (the ones the game swaps at run time: chests, doors) keep their numbers. Adding a block then fills a spare.
/// </summary>
public static class BlockRoom
{
    public const int DefaultSpares = 32;

    /// <summary>Why room can't be made on this map, or null.</summary>
    public static string? CantMakeRoom(RomBuffer rom, LufiaMap map)
    {
        if (map.IsWorld) return "The world map has a fixed set of blocks.";
        if (map.CompositeThreshold < 0) return "This map's tileset can already take new blocks.";
        if (PatternEvent(rom, map) is { } why) return why;
        int max = map.Layer.Max(c => c & 0x3FF);
        if (max + 1 > 0x3FF) return "This map already uses every block number (0-3FF).";
        return null;
    }

    /// <summary>Why the map's composite blocks can't be renumbered, or null: an event that stamps tile patterns
    /// (event command 55, a puzzle) holds block numbers that aren't renumbered.</summary>
    public static string? PatternEvent(RomBuffer rom, LufiaMap map)
    {
        int slots = MapEvents.TableLength(rom, map.MapId);
        for (int ev = 0; ev < slots; ev++)
        {
            EventScript? s;
            try { s = MapEvents.HasEvent(rom, map.MapId, ev) ? EventScript.Load(rom, map.MapId, ev) : null; } catch { continue; }
            if (s != null && s.Ops.Any(o => !o.IsText && o.Bytes.Length > 0 && o.Bytes[0] == 0x55))
                return $"Event {ev} of this map stamps tile patterns (a puzzle), whose block numbers can't be moved yet.";
        }
        return null;
    }

    /// <summary>Makes room for <paramref name="count"/> new blocks on the map (see the class summary). Returns what was done.</summary>
    public static string MakeRoom(RomBuffer rom, int mapId, int count = DefaultSpares)
    {
        var map = LufiaMap.Load(rom, mapId);
        if (CantMakeRoom(rom, map) is { } why) throw new InvalidOperationException(why);
        int t = map.CompositeThreshold;
        int max = map.Layer.Max(c => c & 0x3FF);
        count = Math.Min(count, 0x3FF - max);
        if (count <= 0) throw new InvalidOperationException("This map already uses every block number (0-3FF).");
        if (rom.Length < MapWriter.ExpandedSize) rom.Expand(MapWriter.ExpandedSize);

        var notes = new List<string>();
        int ts = map.Tileset;
        bool shared = MapCatalog.Scan(rom).Any(m => m.MapId != mapId && m.MapId != LufiaMap.WorldMap && m.Tileset == ts);
        if (shared)
        {
            ts = Tilesets.Add(rom, map.Tileset);
            notes.Add($"the map now has its own tileset {ts} (a copy of tileset {map.Tileset}), so other maps don't change");
        }

        // block records: spares (blank: all zero) inserted at the threshold
        var blocks = LufiaCompression.DecompressResource(rom, Tilesets.BlocksResource(rom, ts), out _);
        int n = blocks.Length / BlockSet.Size;
        if (t > n) throw new InvalidOperationException($"The map's threshold ({t:X3}) is past the tileset's {n} blocks.");
        var nb = new byte[blocks.Length + count * BlockSet.Size];
        Array.Copy(blocks, 0, nb, 0, t * BlockSet.Size);
        Array.Copy(blocks, t * BlockSet.Size, nb, (t + count) * BlockSet.Size, blocks.Length - t * BlockSet.Size);

        // pair table: the entries any map of the tileset uses (so structures from those maps still fit here), with
        // blocks at or above the threshold renumbered
        int pairs = Math.Max(Math.Max(0, max - t + 1), Prefabs.PairCount(rom, map.Tileset, MapCatalog.Scan(rom)));
        int oldPairs = Tilesets.CompositePairs(rom, ts);
        var table = new byte[Math.Max(4, pairs * 4)];
        for (int i = 0; i < pairs; i++)
            for (int h = 0; h < 2; h++)
            {
                ushort w = rom.ReadUInt16Le(oldPairs + i * 4 + h * 2);
                if ((w & 0x3FF) >= t) w = (ushort)((w & 0xFC00) | ((w & 0x3FF) + count));
                table[i * 4 + h * 2] = (byte)w; table[i * 4 + h * 2 + 1] = (byte)(w >> 8);
            }
        var space = ExpansionSpace.Load(rom);
        int at = space.Allocate(table.Length, ExpansionSpace.Kind.Patch, 0x300 + ts, EventScript.ScriptLimit);
        space.Save();
        rom.WriteBytes(at, table);
        Tilesets.SetCompositePairs(rom, ts, at);

        var r = ResourceWriter.Save(rom, Tilesets.BlocksResource(rom, ts), nb, allowExpand: true);

        // the map: its tileset, threshold and composite cells
        if (shared) map.SetTilesetAndPalette(ts, map.PaletteIndex);
        map.ShiftComposites(count);
        MapWriter.CheckWram(rom, map.MapId, map.BuildData().Length, ts, map.Width, map.Height, true, nb);
        MapWriter.Save(rom, map, allowExpand: true);
        rom.FixChecksum();
        notes.Add($"{count} blank blocks ({t:X3}-{t + count - 1:X3}) are free for new blocks");
        return string.Join("; ", notes) + ".";
    }
}
