using LufiaForge.Core.Maps;

namespace LufiaForge.Core;

/// <summary>
/// The resource table: 3-byte SNES pointers, one per compressed resource id (maps, tilesets, monster pictures,
/// music...). The game's table at 0x60000 ($0C:8000) holds ids 000-13E and other data follows it, so it can't grow.
/// The only code that reads it is the unpacker at $00:A47D:
/// <code>
/// $00:A483  LDA $16 / ASL / ADC $16 / TAX     (A16: id x 3)
/// $00:A487  LDA $0C8000,X / TAY               (address)
/// $00:A490  LDA $0C8002,X / STA $0B           (bank)
/// </code>
/// Moving the table only needs those two long operands changed: the copy lives in the added space (one bank) with
/// room for <see cref="Capacity"/> ids, the old entries copied, so every existing id keeps working.
/// Block: "LFRT", u16 capacity, u16 ids in use, then the entries.
/// </summary>
public static class ResourceIds
{
    public const int OriginalTable = 0x60000, OriginalCount = 0x13F;
    /// <summary>File offsets of the two long operands in the unpacker ($00:A488 and $00:A491).</summary>
    public const int OperandAddress = 0x2488, OperandBank = 0x2491;
    public const int Capacity = 0x400;
    private const int Header = 8;
    private static readonly byte[] Magic = "LFRT"u8.ToArray();

    private static int FileOffset(int snes) => (snes >> 16 & 0x7F) * 0x8000 + (snes & 0x7FFF);
    private static int SnesAddress(int file) => (file / 0x8000 | 0x80) << 16 | (file % 0x8000 | 0x8000);
    private static int Long(RomBuffer rom, int at) => rom.ReadByte(at) | rom.ReadByte(at + 1) << 8 | rom.ReadByte(at + 2) << 16;

    /// <summary>File offset of the table the game reads (0x60000, or the moved copy).</summary>
    public static int TableOffset(RomBuffer rom) => FileOffset(Long(rom, OperandAddress));

    /// <summary>The table has been moved (and can take new ids).</summary>
    public static bool IsMoved(RomBuffer rom)
    {
        int t = TableOffset(rom);
        return t != OriginalTable && t >= Header && rom.ReadBytes(t - Header, 4).SequenceEqual(Magic);
    }

    /// <summary>The unpacker still reads the game's own table.</summary>
    public static bool IsOriginal(RomBuffer rom) =>
        Long(rom, OperandAddress) == 0x0C8000 && Long(rom, OperandBank) == 0x0C8002;

    /// <summary>How many resource ids exist (000-13E in the game; more once the table moved and ids were added).</summary>
    public static int Count(RomBuffer rom) => IsMoved(rom) ? rom.ReadUInt16Le(TableOffset(rom) - Header + 6) : OriginalCount;

    /// <summary>File offset of resource <paramref name="id"/>'s table entry.</summary>
    public static int Entry(RomBuffer rom, int id)
    {
        if (id < 0 || id >= (IsMoved(rom) ? Capacity : 0x200)) throw new ArgumentOutOfRangeException(nameof(id), $"No resource {id:X3}.");
        return TableOffset(rom) + id * 3;
    }

    /// <summary>Points resource <paramref name="id"/> at a stream at file offset <paramref name="stream"/>.</summary>
    public static void Point(RomBuffer rom, int id, int stream)
    {
        int a = SnesAddress(stream);
        rom.WriteBytes(Entry(rom, id), new[] { (byte)a, (byte)(a >> 8), (byte)(a >> 16) });
    }

    /// <summary>Moves the table into the added space (expanding the ROM to 2 MB if needed). Existing ids keep their
    /// streams; the game's table stays where it was, unused.</summary>
    public static void Move(RomBuffer rom)
    {
        if (IsMoved(rom)) return;
        if (!IsOriginal(rom)) throw new InvalidOperationException("The game's resource loader ($00:A47D) isn't the original, so its table can't be moved safely.");
        if (rom.Length < MapWriter.ExpandedSize) rom.Expand(MapWriter.ExpandedSize);
        var space = ExpansionSpace.Load(rom);
        int at = space.AllocateInOneBank(Header + Capacity * 3, ExpansionSpace.Kind.Patch, 11);
        var block = new byte[Header + Capacity * 3];
        Magic.CopyTo(block, 0);
        block[4] = Capacity & 0xFF; block[5] = Capacity >> 8;
        block[6] = OriginalCount & 0xFF; block[7] = OriginalCount >> 8;
        rom.ReadBytes(OriginalTable, OriginalCount * 3).CopyTo(block, Header);
        rom.WriteBytes(at, block);
        space.Save();
        int table = SnesAddress(at + Header);
        rom.WriteBytes(OperandAddress, new[] { (byte)table, (byte)(table >> 8), (byte)(table >> 16) });
        int bankOp = table + 2;
        rom.WriteBytes(OperandBank, new[] { (byte)bankOp, (byte)(bankOp >> 8), (byte)(bankOp >> 16) });
        rom.FixChecksum();
    }

    /// <summary>A new resource id holding <paramref name="data"/> (compressed into the added space). Moves the table
    /// first when needed. Returns the id.</summary>
    public static int Add(RomBuffer rom, byte[] data)
    {
        Move(rom);
        int n = Count(rom);
        if (n >= Capacity) throw new InvalidOperationException($"All {Capacity} resource ids are in use.");
        var stream = LufiaCompression.Compress(data);
        if (!LufiaCompression.Decompress(stream).AsSpan().SequenceEqual(data))
            throw new InvalidOperationException("Compression self-check failed; nothing was written.");
        var space = ExpansionSpace.Load(rom);
        int at = space.Allocate(stream.Length, ExpansionSpace.Kind.Resource, n);
        rom.WriteBytes(at, stream);
        space.Save();
        Point(rom, n, at);
        int t = TableOffset(rom);
        rom.WriteUInt16Le(t - Header + 6, (ushort)(n + 1));
        rom.FixChecksum();
        return n;
    }
}
