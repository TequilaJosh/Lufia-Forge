using LufiaForge.Core.Maps;

namespace LufiaForge.Core;

// the resource table: 3-byte snes pointers, one per compressed resource id (maps, tilesets, monster pictures,
// music...). the game's table at 0x60000 ($0C:8000) holds ids 000-13E and other data comes right after it, so it
// can't grow. the only code that reads it is the unpacker at $00:A47D:
//   $00:A483  LDA $16 / ASL / ADC $16 / TAX     (A16: id x 3)
//   $00:A487  LDA $0C8000,X / TAY               (address)
//   $00:A490  LDA $0C8002,X / STA $0B           (bank)
// so moving the table just means changing those two long operands. the copy lives in the added space (one bank) with
// room for Capacity ids and the old entries copied over, so every existing id keeps working.
// block: "LFRT", u16 capacity, u16 ids in use, then the entries
public static class ResourceIds
{
    public const int OriginalTable = 0x60000, OriginalCount = 0x13F;
    // file offsets of the two long operands in the unpacker ($00:A488 and $00:A491)
    public const int OperandAddress = 0x2488, OperandBank = 0x2491;
    public const int Capacity = 0x400;
    private const int Header = 8;
    private static readonly byte[] Magic = "LFRT"u8.ToArray();

    private static int FileOffset(int snesAddress) => (snesAddress >> 16 & 0x7F) * 0x8000 + (snesAddress & 0x7FFF);
    private static int SnesAddress(int fileOffset) => (fileOffset / 0x8000 | 0x80) << 16 | (fileOffset % 0x8000 | 0x8000);
    private static int Long(RomBuffer rom, int offset) => rom.ReadByte(offset) | rom.ReadByte(offset + 1) << 8 | rom.ReadByte(offset + 2) << 16;

    // file offset of the table the game reads (0x60000, or the moved copy)
    public static int TableOffset(RomBuffer rom) => FileOffset(Long(rom, OperandAddress));

    // the table got moved (and can take new ids)
    public static bool IsMoved(RomBuffer rom)
    {
        int tableOffset = TableOffset(rom);
        return tableOffset != OriginalTable && tableOffset >= Header && rom.ReadBytes(tableOffset - Header, 4).SequenceEqual(Magic);
    }

    // the unpacker still reads the game's own table
    public static bool IsOriginal(RomBuffer rom) =>
        Long(rom, OperandAddress) == 0x0C8000 && Long(rom, OperandBank) == 0x0C8002;

    // how many resource ids there are (000-13E in the game, more once the table moved and ids got added)
    public static int Count(RomBuffer rom) => IsMoved(rom) ? rom.ReadUInt16Le(TableOffset(rom) - Header + 6) : OriginalCount;

    // file offset of resource id's table entry
    public static int Entry(RomBuffer rom, int id)
    {
        if (id < 0 || id >= (IsMoved(rom) ? Capacity : OriginalCount)) throw new ArgumentOutOfRangeException(nameof(id), $"No resource {id:X3}.");
        return TableOffset(rom) + id * 3;
    }

    // points resource id at a stream at file offset stream
    public static void Point(RomBuffer rom, int id, int stream)
    {
        int address = SnesAddress(stream);
        rom.WriteBytes(Entry(rom, id), new[] { (byte)address, (byte)(address >> 8), (byte)(address >> 16) });
    }

    // moves the table into the added space (expands the rom to 2 MB if it has to). existing ids keep their streams,
    // the game's table stays where it was, unused
    public static void Move(RomBuffer rom)
    {
        if (IsMoved(rom)) return;
        if (!IsOriginal(rom)) throw new InvalidOperationException("The game's resource loader ($00:A47D) isn't the original, so its table can't be moved safely.");
        if (rom.Length < MapWriter.ExpandedSize) rom.Expand(MapWriter.ExpandedSize);
        var freeSpace = ExpansionSpace.Load(rom);
        int blockOffset = freeSpace.AllocateInOneBank(Header + Capacity * 3, ExpansionSpace.Kind.Patch, 11);
        var block = new byte[Header + Capacity * 3];
        Magic.CopyTo(block, 0);
        block[4] = Capacity & 0xFF; block[5] = Capacity >> 8;
        block[6] = OriginalCount & 0xFF; block[7] = OriginalCount >> 8;
        rom.ReadBytes(OriginalTable, OriginalCount * 3).CopyTo(block, Header);
        rom.WriteBytes(blockOffset, block);
        freeSpace.Save();
        int table = SnesAddress(blockOffset + Header);
        rom.WriteBytes(OperandAddress, new[] { (byte)table, (byte)(table >> 8), (byte)(table >> 16) });
        int bankOp = table + 2;
        rom.WriteBytes(OperandBank, new[] { (byte)bankOp, (byte)(bankOp >> 8), (byte)(bankOp >> 16) });
        rom.FixChecksum();
    }

    // takes out resource id if it's the last one and a new one (added with Add). its stream in the added space gets
    // freed and the id can be handed out again. false if it isn't the last
    public static bool RemoveLast(RomBuffer rom, int id)
    {
        if (!IsMoved(rom) || id < OriginalCount || id != Count(rom) - 1) return false;
        int stream = LufiaCompression.ResourceOffset(rom, id);
        var freeSpace = ExpansionSpace.Load(rom);
        if (freeSpace.At(stream) is { } block && block.Start == stream && block.Kind is ExpansionSpace.Kind.Resource or ExpansionSpace.Kind.MapData) freeSpace.Free(stream);
        freeSpace.Save();
        rom.WriteBytes(Entry(rom, id), new byte[3]);
        rom.WriteUInt16Le(TableOffset(rom) - Header + 6, (ushort)id);
        rom.FixChecksum();
        return true;
    }

    // a new resource id holding data (compressed into the added space). moves the table first if needed, returns the id
    public static int Add(RomBuffer rom, byte[] data)
    {
        Move(rom);
        int newId = Count(rom);
        if (newId >= Capacity) throw new InvalidOperationException($"All {Capacity} resource ids are in use.");
        var stream = LufiaCompression.Compress(data);
        if (!LufiaCompression.Decompress(stream).AsSpan().SequenceEqual(data))
            throw new InvalidOperationException("Compression self-check failed; nothing was written.");
        var freeSpace = ExpansionSpace.Load(rom);
        int streamOffset = freeSpace.Allocate(stream.Length, ExpansionSpace.Kind.Resource, newId);
        rom.WriteBytes(streamOffset, stream);
        freeSpace.Save();
        Point(rom, newId, streamOffset);
        int tableOffset = TableOffset(rom);
        rom.WriteUInt16Le(tableOffset - Header + 6, (ushort)(newId + 1));
        rom.FixChecksum();
        return newId;
    }
}
