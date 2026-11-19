using LufiaForge.Core.Maps;

namespace LufiaForge.Core;

// writes new contents for a compressed resource (the table at 0x60000): compresses, checks the stream unpacks to the
// same bytes, and stores it in place if it fits, otherwise in the space above 1 MB (expanding the rom when that's
// allowed) with the resource pointer updated. same rules as MapWriter
public static class ResourceWriter
{
    public sealed record Result(int CompressedSize, int SlotSize, int FileOffset, bool Moved, bool Expanded);

    // bytes the resource can use where it is. a smaller stream written in place fills the rest of its old room with FF
    // (see Save), so the room runs on to the next resource's start when every byte in between is FF. that gives back
    // what an earlier smaller write left unused. anything else after a stream (in the original game, other data or
    // code) never counts. above 1 MB the space table knows the block
    public static int SlotSize(RomBuffer rom, int resourceId)
    {
        int streamOffset = LufiaCompression.ResourceOffset(rom, resourceId);
        LufiaCompression.Decompress(rom, streamOffset, out int used);
        if (streamOffset >= MapWriter.ExpansionStart)
        {
            // only a block we wrote for this stream counts (another hack's data above 1 MB is one big reserved block,
            // its "room" isn't this resource's)
            var block = rom.Length >= MapWriter.ExpandedSize ? ExpansionSpace.Load(rom).At(streamOffset) : null;
            return block != null && block.Start == streamOffset && block.Kind is ExpansionSpace.Kind.Resource or ExpansionSpace.Kind.MapData
                ? Math.Max(used, block.End - streamOffset) : used;
        }
        int next = int.MaxValue;
        for (int id = 0, count = ResourceIds.Count(rom); id < count; id++)
        {
            int otherOffset = LufiaCompression.ResourceOffset(rom, id);
            if (otherOffset > streamOffset && otherOffset < next) next = otherOffset;
        }
        if (next == int.MaxValue || next - streamOffset <= used || next - streamOffset - used >= 0x8000) return used;
        for (int i = streamOffset + used; i < next; i++) if (rom.ReadByte(i) != 0xFF) return used;
        return next - streamOffset;
    }

    public static Result Save(RomBuffer rom, int resourceId, byte[] data, bool allowExpand)
    {
        int oldOffset = LufiaCompression.ResourceOffset(rom, resourceId);
        int slotSize = SlotSize(rom, resourceId);
        var stream = LufiaCompression.Compress(data);
        if (!LufiaCompression.Decompress(stream).AsSpan().SequenceEqual(data))
            throw new InvalidOperationException("Compression self-check failed; nothing was written.");
        if (stream.Length <= slotSize)
        {
            rom.WriteBytes(oldOffset, stream);
            // the unused rest of the room becomes FF so a later, bigger write can have it back (SlotSize)
            if (slotSize > stream.Length) rom.WriteBytes(oldOffset + stream.Length, Enumerable.Repeat((byte)0xFF, slotSize - stream.Length).ToArray());
            return new Result(stream.Length, slotSize, oldOffset, false, false);
        }
        if (!allowExpand && rom.Length < MapWriter.ExpandedSize)
            throw new InvalidOperationException($"The new data needs {stream.Length} bytes but its slot holds {slotSize}; the ROM has to be expanded to 2 MB.");
        bool expanded = false;
        if (rom.Length < MapWriter.ExpandedSize) { rom.Expand(MapWriter.ExpandedSize); expanded = true; }
        var freeSpace = ExpansionSpace.Load(rom);
        int newOffset = freeSpace.Allocate(stream.Length, ExpansionSpace.Kind.Resource, resourceId);
        rom.WriteBytes(newOffset, stream);
        ResourceIds.Point(rom, resourceId, newOffset);
        if (oldOffset >= MapWriter.ExpansionStart && oldOffset != newOffset) freeSpace.Free(oldOffset);
        freeSpace.Save();
        rom.FixChecksum();
        return new Result(stream.Length, slotSize, newOffset, true, expanded);
    }
}
