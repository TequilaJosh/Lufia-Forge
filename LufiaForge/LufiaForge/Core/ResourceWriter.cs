using LufiaForge.Core.Maps;

namespace LufiaForge.Core;

/// <summary>
/// Writes new contents for a compressed resource (the table at 0x60000): compresses, checks the stream unpacks
/// to the same bytes, and stores it in place when it fits; otherwise in the space above 1 MB (expanding the ROM
/// when allowed) with the resource pointer updated. Same rules as <see cref="MapWriter"/>.
/// </summary>
public static class ResourceWriter
{
    public sealed record Result(int CompressedSize, int SlotSize, int FileOffset, bool Moved, bool Expanded);

    public static Result Save(RomBuffer rom, int resourceId, byte[] data, bool allowExpand)
    {
        int at = LufiaCompression.ResourceOffset(rom, resourceId);
        LufiaCompression.Decompress(rom, at, out int slot);
        var stream = LufiaCompression.Compress(data);
        if (!LufiaCompression.Decompress(stream).AsSpan().SequenceEqual(data))
            throw new InvalidOperationException("Compression self-check failed; nothing was written.");
        if (stream.Length <= slot)
        {
            rom.WriteBytes(at, stream);
            return new Result(stream.Length, slot, at, false, false);
        }
        if (!allowExpand && rom.Length < MapWriter.ExpandedSize)
            throw new InvalidOperationException($"The new data needs {stream.Length} bytes but its slot holds {slot}; the ROM has to be expanded to 2 MB.");
        bool expanded = false;
        if (rom.Length < MapWriter.ExpandedSize) { rom.Expand(MapWriter.ExpandedSize); expanded = true; }
        var space = ExpansionSpace.Load(rom);
        int offset = space.Allocate(stream.Length, ExpansionSpace.Kind.Resource, resourceId);
        rom.WriteBytes(offset, stream);
        int bank = (offset / 0x8000) | 0x80, addr = (offset % 0x8000) | 0x8000;
        rom.WriteBytes(LufiaCompression.ResourceTable + resourceId * 3, new[] { (byte)addr, (byte)(addr >> 8), (byte)bank });
        if (at >= MapWriter.ExpansionStart && at != offset) space.Free(at);
        space.Save();
        rom.FixChecksum();
        return new Result(stream.Length, slot, offset, true, expanded);
    }
}
