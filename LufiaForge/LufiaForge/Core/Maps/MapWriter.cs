namespace LufiaForge.Core.Maps;

/// <summary>Result of writing a map back to the ROM.</summary>
public sealed record MapSaveResult(int CompressedSize, int OriginalSlotSize, int FileOffset, bool Moved, bool RomExpanded)
{
    public string Describe() => Moved
        ? $"Map data ({CompressedSize} bytes) didn't fit its original {OriginalSlotSize}-byte slot, so it was moved to 0x{FileOffset:X6}" +
          (RomExpanded ? " in the newly expanded 2 MB ROM." : " in the expanded ROM area.")
        : $"Map data written in place at 0x{FileOffset:X6} ({CompressedSize} of {OriginalSlotSize} bytes used).";
}

/// <summary>
/// Writes an edited <see cref="LufiaMap"/> back into the ROM: rebuilds its unpacked data, compresses it,
/// verifies the stream unpacks to the same bytes, and stores it in its original slot if it fits.
/// Otherwise it goes into the space above 1 MB (expanding the ROM to 2 MB if needed) and the resource
/// table pointer at 0x60000 + id*3 is updated.
/// </summary>
public static class MapWriter
{
    public const int ExpandedSize = 0x200000;
    /// <summary>The largest ROM Lufia Forge makes: 4 MB LoROM (banks 80-FF; above 2 MB = banks C0-FF).</summary>
    public const int MaxSize = 0x400000;
    public const int ExpansionStart = 0x100000;

    /// <summary>True when the edited map would not fit in its current slot.</summary>
    public static bool NeedsMoreSpace(LufiaMap map, out int compressedSize)
    {
        compressedSize = LufiaCompression.Compress(map.BuildData()).Length;
        return compressedSize > map.CompressedSize;
    }

    /// <summary>
    /// A town / dungeon map is unpacked to $7F:0000, its tileset's blocks right after it ($01:BDD8) and, for maps with
    /// composite blocks, a width x height x 2 overlay after those ($01:BF2A). The unpacker's end address is 16 bits,
    /// so past $7F:FFFF it wraps onto the map's own header (crash). The game's biggest is 0xA3F8 bytes.
    /// </summary>
    public const int WramLimit = 0x10000;
    /// <summary>Different character sprites a map may use (the loader's 24 minus the party and followers).</summary>
    public const int MaxSpriteKinds = 16;

    public static int WramNeeded(int dataLength, int blocksLength, int width, int height, bool composite) =>
        dataLength + blocksLength + (composite ? width * height * 2 : 0);

    /// <summary>Throws when the map wouldn't fit in work RAM (pass new block bytes when the tileset's blocks change).</summary>
    public static void CheckWram(RomBuffer rom, int mapId, int dataLength, int tileset, int width, int height, bool composite, byte[]? newBlocks)
    {
        int blocks = newBlocks?.Length ?? LufiaCompression.DecompressResource(rom, Tilesets.BlocksResource(rom, tileset), out _).Length;
        int need = WramNeeded(dataLength, blocks, width, height, composite);
        if (need > WramLimit)
            throw new InvalidOperationException($"Map {mapId:X2} would need {need:N0} bytes of work RAM (map data, its tileset's blocks" +
                (composite ? ", the overlay layer" : "") + $"); the game has {WramLimit:N0} and would crash. Make it smaller" +
                (newBlocks != null ? " or use fewer blocks" : "") + ".");
    }

    public static MapSaveResult Save(RomBuffer rom, LufiaMap map, bool allowExpand)
    {
        var data = map.BuildData();
        if (!map.IsWorld)
        {
            CheckWram(rom, map.MapId, data.Length, map.Tileset, map.Width, map.Height, map.CompositeThreshold >= 0, null);
            // the sprite loader ($01:AD32) holds 24 different sprites (party and followers included) and hangs when
            // it's full; the game's busiest map uses 13 different character sprites
            int kinds = map.Npcs.Where(n => !n.IsUnused).Select(n => n.Sprite).Distinct().Count();
            if (kinds > MaxSpriteKinds)
                throw new InvalidOperationException($"Map {map.MapId:X2} uses {kinds} different character sprites; the game can load {MaxSpriteKinds} next to the party and freezes past that. Reuse some sprites.");
            SetupScripts.SyncCharacters(rom, map, allowExpand);
        }
        var stream = LufiaCompression.Compress(data);
        if (!LufiaCompression.Decompress(stream).AsSpan().SequenceEqual(data))
            throw new InvalidOperationException("Compression self-check failed; nothing was written.");

        if (stream.Length <= map.CompressedSize)
        {
            rom.WriteBytes(map.CompressedOffset, stream);
            return new MapSaveResult(stream.Length, map.CompressedSize, map.CompressedOffset, false, false);
        }

        if (!allowExpand && rom.Length < ExpandedSize)
            throw new InvalidOperationException(
                $"The edited map needs {stream.Length} bytes but its slot holds {map.CompressedSize}. " +
                "Expanding the ROM to 2 MB is required.");

        bool expanded = false;
        if (rom.Length < ExpandedSize)
        {
            rom.Expand(ExpandedSize);
            expanded = true;
        }

        var space = ExpansionSpace.Load(rom);
        int offset = space.Allocate(stream.Length, ExpansionSpace.Kind.MapData, map.ResourceId);
        rom.WriteBytes(offset, stream);

        // Point the resource at its new home (LoROM: file offset -> bank:addr, FastROM mirror $80+).
        ResourceIds.Point(rom, map.ResourceId, offset);
        // the previous copy above 1 MB isn't used any more
        if (map.CompressedOffset >= ExpansionStart && map.CompressedOffset != offset) space.Free(map.CompressedOffset);
        space.Save();
        rom.FixChecksum();
        return new MapSaveResult(stream.Length, map.CompressedSize, offset, true, expanded);
    }


}
