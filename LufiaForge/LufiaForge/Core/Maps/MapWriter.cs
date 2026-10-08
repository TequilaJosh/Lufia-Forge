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
    public const int ExpansionStart = 0x100000;

    /// <summary>True when the edited map would not fit in its current slot.</summary>
    public static bool NeedsMoreSpace(LufiaMap map, out int compressedSize)
    {
        compressedSize = LufiaCompression.Compress(map.BuildData()).Length;
        return compressedSize > map.CompressedSize;
    }

    public static MapSaveResult Save(RomBuffer rom, LufiaMap map, bool allowExpand)
    {
        var data = map.BuildData();
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
        int bank = (offset / 0x8000) | 0x80;
        int addr = (offset % 0x8000) | 0x8000;
        int p = LufiaCompression.ResourceTable + map.ResourceId * 3;
        rom.WriteBytes(p, new[] { (byte)addr, (byte)(addr >> 8), (byte)bank });
        // the previous copy above 1 MB isn't used any more
        if (map.CompressedOffset >= ExpansionStart && map.CompressedOffset != offset) space.Free(map.CompressedOffset);
        space.Save();
        rom.FixChecksum();
        return new MapSaveResult(stream.Length, map.CompressedSize, offset, true, expanded);
    }


}
