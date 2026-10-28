using LufiaForge.Core.Maps;

namespace LufiaForge.Core.Battle;

/// <summary>
/// Lufia 1 shops (found 2026-10-27). Event command 1C xx opens shop xx (the id goes to $1954). The shop builder at
/// $00:D1C4 reads a u16 per shop from $01:EC5B (file 0xEC5B, 128 entries: shops 00-55 are the game's, 56-7F point at
/// single 00 bytes, "nothing"), the shop's data in bank $01 at that address: a type byte (00 = nothing; see
/// <c>GameDataLabels.ShopTypes</c>), then item numbers (a byte each), 00 ends it; the builder copies up to 11 items to
/// $7E:5700.
/// <para>
/// Bank $01 has no room for more, so new shops (and game shops given more slots) live in the added space: a hook at
/// $00:D1D2 (where the builder reads the table and sets the pointer's bank, LDA #$01) looks the shop up in Lufia Forge's
/// table first (block "LFSH", 256 entries of a long pointer and a flag) and uses the game's table for the rest. Every
/// other read of the shop goes through that pointer ($1F-$21), so nothing else changes. Lufia Forge's shops have
/// room for the full 11 items.
/// </para>
/// </summary>
public static class ShopTable
{
    public const int GameTable = 0xEC5B, GameShops = 0x56, TableEntries = 0x80, MaxItems = 11, RecordSize = 16;
    private const int HookAt = 0x51D2;   // $00:D1D2
    private static readonly byte[] HookOriginal = Convert.FromHexString("C220BF5BEC01851FA9005785 22A9005885 25E220A9018521".Replace(" ", ""));
    private static readonly byte[] Magic = "LFSH"u8.ToArray();
    private const int PatchId = 42, Header = 16, CodeAt = Header, TableAt = 0x60, RecordsAt = TableAt + 0x400, BlockSize = RecordsAt + 0x100 * RecordSize;

    private static int SnesAddress(int file) => (file / 0x8000 | 0x80) << 16 | (file % 0x8000 | 0x8000);
    private static int FileOffset(int snes) => (snes >> 16 & 0x7F) * 0x8000 + (snes & 0x7FFF);

    /// <summary>File offset of Lufia Forge's shop block, or -1.</summary>
    public static int Block(RomBuffer rom)
    {
        if (rom.ReadByte(HookAt) != 0x22) return -1;
        int code = FileOffset(rom.ReadByte(HookAt + 1) | rom.ReadByte(HookAt + 2) << 8 | rom.ReadByte(HookAt + 3) << 16);
        int b = code - CodeAt;
        return b > 0 && b + BlockSize <= rom.Length && rom.ReadBytes(b, 4).SequenceEqual(Magic) ? b : -1;
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

    /// <summary>Whether shop <paramref name="id"/> is one of Lufia Forge's (new, or a game shop given more slots).</summary>
    public static bool IsOwn(RomBuffer rom, int id)
    {
        int b = Block(rom);
        return b >= 0 && id is >= 0 and <= 0xFF && rom.ReadByte(b + TableAt + id * 4 + 3) != 0;
    }

    /// <summary>File offset of shop <paramref name="id"/>'s data (type, items, 00), or -1 for an id the game has no entry for.</summary>
    public static int RecordOffset(RomBuffer rom, int id)
    {
        int b = Block(rom);
        if (b >= 0 && IsOwn(rom, id)) return b + RecordsAt + id * RecordSize;
        if (id < 0 || id >= TableEntries) return -1;
        return rom.ReadUInt16Le(GameTable + id * 2);   // bank $01: the address is the file offset
    }

    /// <summary>The shop's type and items (up to its 00).</summary>
    public static (int Type, List<int> Items) Read(RomBuffer rom, int id)
    {
        int o = RecordOffset(rom, id);
        if (o < 0) return (0, new List<int>());
        int type = rom.ReadByte(o);
        var items = new List<int>();
        if (type != 0)
            for (int i = 1; i <= MaxItems && rom.ReadByte(o + i) != 0; i++) items.Add(rom.ReadByte(o + i));
        return (type, items);
    }

    /// <summary>The shops the list shows: the game's 00-55 and Lufia Forge's (any id).</summary>
    public static List<int> Ids(RomBuffer rom) =>
        Enumerable.Range(0, 0x100).Where(i => i < GameShops || IsOwn(rom, i)).ToList();

    /// <summary>Puts the hook and the empty table in (once). Returns the block's file offset.</summary>
    public static int Install(RomBuffer rom)
    {
        int have = Block(rom);
        if (have >= 0) return have;
        if (!CanInstall(rom, out var why)) throw new InvalidOperationException(why);
        if (rom.Length < MapWriter.ExpandedSize) rom.Expand(MapWriter.ExpandedSize);
        var space = ExpansionSpace.Load(rom);
        int at = space.AllocateInOneBank(BlockSize, ExpansionSpace.Kind.Patch, PatchId);
        space.Save();
        int table = SnesAddress(at + TableAt);
        var block = new byte[BlockSize];
        Magic.CopyTo(block, 0);
        block[4] = 1;
        byte T(int k, int shift) => (byte)(table + k >> shift);
        var code = new List<byte>
        {
            0xC2, 0x20, 0xDA, 0x8A, 0x0A, 0xAA,                              // REP #$20; PHX; TXA; ASL; TAX (id * 4)
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
        code.CopyTo(block, CodeAt);
        rom.WriteBytes(at, block);
        int c = SnesAddress(at + CodeAt);
        var site = new byte[HookOriginal.Length];
        new byte[] { 0x22, (byte)c, (byte)(c >> 8), (byte)(c >> 16), 0x80, (byte)(HookOriginal.Length - 6) }.CopyTo(site, 0);
        for (int i = 6; i < site.Length; i++) site[i] = 0xEA;
        rom.WriteBytes(HookAt, site);
        rom.FixChecksum();
        return at;
    }

    /// <summary>Writes shop <paramref name="id"/> as Lufia Forge's (type and up to 11 items): a new shop, or a game shop
    /// that gets room for 11 items. Events open it with 1C xx as before.</summary>
    public static void Write(RomBuffer rom, int id, int type, IReadOnlyList<int> items)
    {
        if (id is < 1 or > 0xFF) throw new ArgumentOutOfRangeException(nameof(id), "Shop ids go from 01 to FF.");
        if (items.Count > MaxItems) throw new ArgumentException($"A shop sells up to {MaxItems} items.");
        if (items.Any(i => i is <= 0 or > 0xFF)) throw new ArgumentException("Item 00 ends the list, so a shop can't sell it.");
        int b = Install(rom);
        var rec = new byte[RecordSize];
        rec[0] = (byte)type;
        for (int i = 0; i < items.Count; i++) rec[1 + i] = (byte)items[i];
        int at = b + RecordsAt + id * RecordSize;
        rom.WriteBytes(at, rec);
        int p = SnesAddress(at);
        rom.WriteBytes(b + TableAt + id * 4, new[] { (byte)p, (byte)(p >> 8), (byte)(p >> 16), (byte)1 });
    }

    /// <summary>The id a new shop gets: the first one from 56 up that isn't Lufia Forge's yet and that no event opens.</summary>
    public static int NextId(RomBuffer rom, ISet<int>? usedByEvents = null)
    {
        for (int i = GameShops; i <= 0xFF; i++)
            if (!IsOwn(rom, i) && (usedByEvents == null || !usedByEvents.Contains(i))) return i;
        return -1;
    }

    /// <summary>A new shop: a copy of shop <paramref name="from"/> (type and items). Returns its id.</summary>
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
