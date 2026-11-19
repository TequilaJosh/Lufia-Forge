using LufiaForge.Core.Maps;

namespace LufiaForge.Core.Battle;

// lufia 1 shops (found 2026-10-27). event command 1C xx opens shop xx (the id goes to $1954). the shop builder at
// $00:D1C4 reads a u16 per shop from $01:EC5B (file 0xEC5B, 128 entries: shops 00-55 are the game's, 56-7F point at
// single 00 bytes, "nothing"), which is the shop's data in bank $01 at that address: a type byte (00 = nothing, see
// GameDataLabels.ShopTypes), then item numbers (a byte each), 00 ends it. the builder copies up to 11 items to
// $7E:5700.
//
// bank $01 has no room for more, so new shops (and game shops given more slots) live in the added space. a hook at
// $00:D1D2 (where the builder reads the table and sets the pointer's bank, LDA #$01) looks the shop up in our
// table first (block "LFSH", 256 entries of a long pointer and a flag) and uses the game's table for the rest. every
// other read of the shop goes through that pointer ($1F-$21), so nothing else changes. our shops have room for the
// full 11 items.
//
// the builder doubles the id with an 8-bit ASL before the hook ($00:D1CF), so X has lost bit 7 for shops 80-FF (and
// the game's own table read aliases them onto 00-7F). the hook reads the id from $1954 itself (version 2. the first
// version used X, so our shops 80-FF opened another shop). Install brings a version 1 block up to date
public static class ShopTable
{
    public const int GameTable = 0xEC5B, GameShops = 0x56, TableEntries = 0x80, MaxItems = 11, RecordSize = 16;
    private const int HookAt = 0x51D2;   // $00:D1D2
    private static readonly byte[] HookOriginal = Convert.FromHexString("C220BF5BEC01851FA9005785 22A9005885 25E220A9018521".Replace(" ", ""));
    private static readonly byte[] Magic = "LFSH"u8.ToArray();
    private const int PatchId = 42, Header = 16, CodeAt = Header, TableAt = 0x60, RecordsAt = TableAt + 0x400, BlockSize = RecordsAt + 0x100 * RecordSize;

    private static int SnesAddress(int fileOffset) => (fileOffset / 0x8000 | 0x80) << 16 | (fileOffset % 0x8000 | 0x8000);
    private static int FileOffset(int snesAddress) => (snesAddress >> 16 & 0x7F) * 0x8000 + (snesAddress & 0x7FFF);

    // file offset of our shop block, or -1
    public static int Block(RomBuffer rom)
    {
        if (rom.ReadByte(HookAt) != 0x22) return -1;
        int code = FileOffset(rom.ReadByte(HookAt + 1) | rom.ReadByte(HookAt + 2) << 8 | rom.ReadByte(HookAt + 3) << 16);
        int block = code - CodeAt;
        return block > 0 && block + BlockSize <= rom.Length && rom.ReadBytes(block, 4).SequenceEqual(Magic) ? block : -1;
    }

    public static bool IsInstalled(RomBuffer rom) => Block(rom) >= 0;

    public static bool CanInstall(RomBuffer rom, out string why)
    {
        why = "";
        if (IsInstalled(rom)) return true;
        if (!rom.ReadBytes(HookAt, HookOriginal.Length).SequenceEqual(HookOriginal))
        {
            why = "This ROM's shop code isn't the original game's (another patch or hack changed it), so new shops can't be added.";
            return false;
        }
        return true;
    }

    // is shop id one of ours (new, or a game shop given more slots)
    public static bool IsOwn(RomBuffer rom, int id)
    {
        int block = Block(rom);
        return block >= 0 && id is >= 0 and <= 0xFF && rom.ReadByte(block + TableAt + id * 4 + 3) != 0;
    }

    // file offset of shop id's data (type, items, 00), or -1 for an id the game has no entry for
    public static int RecordOffset(RomBuffer rom, int id)
    {
        int block = Block(rom);
        if (block >= 0 && IsOwn(rom, id)) return block + RecordsAt + id * RecordSize;
        if (id < 0 || id >= TableEntries) return -1;
        return rom.ReadUInt16Le(GameTable + id * 2);   // bank $01, so the address is the file offset
    }

    // the shop's type and items (up to its 00)
    public static (int Type, List<int> Items) Read(RomBuffer rom, int id)
    {
        int recordOffset = RecordOffset(rom, id);
        if (recordOffset < 0) return (0, new List<int>());
        int type = rom.ReadByte(recordOffset);
        var items = new List<int>();
        if (type != 0)
            for (int i = 1; i <= MaxItems && rom.ReadByte(recordOffset + i) != 0; i++) items.Add(rom.ReadByte(recordOffset + i));
        return (type, items);
    }

    // the shops the list shows: the game's 00-55 and ours (any id)
    public static List<int> Ids(RomBuffer rom) =>
        Enumerable.Range(0, 0x100).Where(i => i < GameShops || IsOwn(rom, i)).ToList();

    // the block's hook is the first version's (it took the shop from X, which has lost bit 7 of the id)
    public static bool NeedsUpdate(RomBuffer rom) => Block(rom) is int block && block >= 0 && rom.ReadByte(block + 4) < Version;

    private const byte Version = 2;

    // puts the hook and the empty table in (only once, a first version block gets the current hook). returns the
    // block's file offset
    public static int Install(RomBuffer rom)
    {
        int existing = Block(rom);
        if (existing >= 0)
        {
            if (rom.ReadByte(existing + 4) < Version)
            {
                rom.WriteBytes(existing + CodeAt, Code(SnesAddress(existing + TableAt)));
                rom.WriteByte(existing + 4, Version);
                rom.FixChecksum();
            }
            return existing;
        }
        if (!CanInstall(rom, out var why)) throw new InvalidOperationException(why);
        if (rom.Length < MapWriter.ExpandedSize) rom.Expand(MapWriter.ExpandedSize);
        var freeSpace = ExpansionSpace.Load(rom);
        int blockOffset = freeSpace.AllocateInOneBank(BlockSize, ExpansionSpace.Kind.Patch, PatchId);
        freeSpace.Save();
        var block = new byte[BlockSize];
        Magic.CopyTo(block, 0);
        block[4] = Version;
        Code(SnesAddress(blockOffset + TableAt)).CopyTo(block, CodeAt);
        rom.WriteBytes(blockOffset, block);
        int codeAddress = SnesAddress(blockOffset + CodeAt);
        var site = new byte[HookOriginal.Length];
        new byte[] { 0x22, (byte)codeAddress, (byte)(codeAddress >> 8), (byte)(codeAddress >> 16), 0x80, (byte)(HookOriginal.Length - 6) }.CopyTo(site, 0);
        for (int i = 6; i < site.Length; i++) site[i] = 0xEA;
        rom.WriteBytes(HookAt, site);
        rom.FixChecksum();
        return blockOffset;
    }

    // the hook, for the table at SNES address table. entered with M = 8, X = 16, X = the
    // game's (shop id x 2) & FF, which the game's path keeps
    private static byte[] Code(int table)
    {
        byte T(int add, int shift) => (byte)(table + add >> shift);
        var code = new List<byte>
        {
            0xC2, 0x20, 0xDA,                                                // REP #$20; PHX
            0xAD, 0x54, 0x19, 0x29, 0xFF, 0x00, 0x0A, 0x0A, 0xAA,            // LDA $1954; AND #$00FF; ASL; ASL; TAX (id * 4)
            0xBF, T(2, 0), T(2, 8), T(2, 16),                                // LDA table+2,X (bank, flag)
            0xF0, 0x0B,                                                      // BEQ game
            0x85, 0x21,                                                      // STA $21
            0xBF, T(0, 0), T(0, 8), T(0, 16),                                // LDA table,X
            0x85, 0x1F, 0xFA, 0x80, 0x0F,                                    // STA $1F; PLX; BRA common
            // game: the game's own lines
            0xFA, 0xBF, 0x5B, 0xEC, 0x01, 0x85, 0x1F,                        // PLX; LDA $01EC5B,X; STA $1F
            0xE2, 0x20, 0xA9, 0x01, 0x85, 0x21, 0xC2, 0x20,                  // SEP #$20; LDA #$01; STA $21; REP #$20
            // common
            0xA9, 0x00, 0x57, 0x85, 0x22, 0xA9, 0x00, 0x58, 0x85, 0x25,      // LDA #$5700; STA $22; LDA #$5800; STA $25
            0xE2, 0x20, 0x6B,                                                // SEP #$20; RTL
        };
        if (CodeAt + code.Count > TableAt) throw new InvalidOperationException("The shop hook doesn't fit.");
        return code.ToArray();
    }

    // writes shop id as one of ours (type and up to 11 items): a new shop, or a game shop
    // that gets room for 11 items. events still open it with 1C xx
    public static void Write(RomBuffer rom, int id, int type, IReadOnlyList<int> items)
    {
        if (id is < 1 or > 0xFF) throw new ArgumentOutOfRangeException(nameof(id), "Shop ids go from 01 to FF.");
        if (items.Count > MaxItems) throw new ArgumentException($"A shop sells up to {MaxItems} items.");
        if (items.Any(i => i is <= 0 or > 0xFF)) throw new ArgumentException("Item 00 ends the list, so a shop can't sell it.");
        int block = Install(rom);
        var record = new byte[RecordSize];
        record[0] = (byte)type;
        for (int i = 0; i < items.Count; i++) record[1 + i] = (byte)items[i];
        int recordOffset = block + RecordsAt + id * RecordSize;
        rom.WriteBytes(recordOffset, record);
        int recordSnes = SnesAddress(recordOffset);
        rom.WriteBytes(block + TableAt + id * 4, new[] { (byte)recordSnes, (byte)(recordSnes >> 8), (byte)(recordSnes >> 16), (byte)1 });
    }

    // the id a new shop gets: the first one from 56 up that isn't ours yet and that no event opens
    public static int NextId(RomBuffer rom, ISet<int>? usedByEvents = null)
    {
        for (int i = GameShops; i <= 0xFF; i++)
            if (!IsOwn(rom, i) && (usedByEvents == null || !usedByEvents.Contains(i))) return i;
        return -1;
    }

    // a new shop: a copy of shop from (type and items). returns its id
    public static int Add(RomBuffer rom, int from, ISet<int>? usedByEvents = null)
    {
        if (!CanInstall(rom, out var why)) throw new InvalidOperationException(why);
        int id = NextId(rom, usedByEvents);
        if (id < 0) throw new InvalidOperationException("Every shop id is used.");
        var (type, items) = Read(rom, from);
        Write(rom, id, type == 0 ? 5 : type, items.Take(MaxItems).ToList());
        return id;
    }
}
