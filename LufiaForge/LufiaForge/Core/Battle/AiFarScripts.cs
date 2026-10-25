using LufiaForge.Core.Maps;

namespace LufiaForge.Core.Battle;

/// <summary>
/// Room for Lufia 1 monster AI scripts beyond bank 0. The battle reads a script through a 16-bit pointer with data
/// bank 00 ($08:CACD LDA ($09),Y; $09 = record + offset, "go to" sets $09 = $0C + offset with $0C = the record), so
/// every script had to sit in bank 0, which has about 1.3 KB free. With this patch a monster can have its script in
/// the ROM's added space: at the start of its turn ($08:CAB5) a hook looks the monster up in a table and, when it has
/// one, copies the script into a buffer in low work RAM ($7E:1C00-$1DFF, which bank 00 reaches) and points $0C/$09
/// there; the game's interpreter and every command (Lufia Forge's own too) then run it as before. The copy is made
/// at every turn start, so a monster that carries on after an extra action (command 25) finds its own script.
/// <code>
/// $08:CAB5  5C Hook EA     (was LDA $D822,X / BEQ $CAEB: the record's script offset, 0 = it just attacks)
/// </code>
/// The block: "LFAF", version, (spare), next free byte u16, code at +$10, table at +$100 (id u16, script address
/// u16 in the block's bank, bytes to copy u16; FFFF ends), scripts from +$710 as [capacity u16][00][script] with
/// "go to"s counted from the 00 (the script starts at offset 1). A monster with a script here has its record's
/// word +$22 set to 0, so its old bank-0 bytes can be reused.
/// </summary>
public static class AiFarScripts
{
    public const int Site = 0x44AB5;                       // $08:CAB5
    private static readonly byte[] Original = { 0xBD, 0x22, 0xD8, 0xF0, 0x31 };
    public const int Buffer = 0x1C00, BufferSize = 0x200;   // $7E:1C00-$1DFF (RamMap: low RAM, free)
    /// <summary>The longest script (bytes) that fits the buffer (one byte is the 00 in front).</summary>
    public const int MaxLength = BufferSize - 1;

    private static readonly byte[] Magic = "LFAF"u8.ToArray();
    private const byte Version = 1;
    private const int BlockSize = 0x4000, CodeAt = 0x10, TableAt = 0x100, TableCapacity = 255, EntrySize = 6, ScriptsAt = 0x710;

    private static int FileOffset(int snes) => (snes >> 16 & 0x7F) * 0x8000 + (snes & 0x7FFF);
    private static int SnesAddress(int file) => (file / 0x8000 | 0x80) << 16 | (file % 0x8000 | 0x8000);

    /// <summary>File offset of the block, or -1 when the patch isn't in the ROM.</summary>
    public static int Block(RomBuffer rom)
    {
        if (rom.Length <= Site + 4 || rom.ReadByte(Site) != 0x5C) return -1;
        int b = FileOffset(rom.ReadByte(Site + 1) | rom.ReadByte(Site + 2) << 8 | rom.ReadByte(Site + 3) << 16) - CodeAt;
        return b >= 0 && b + BlockSize <= rom.Length && rom.ReadBytes(b, 4).SequenceEqual(Magic) ? b : -1;
    }

    public static bool IsInstalled(RomBuffer rom) => Block(rom) >= 0;

    /// <summary>Whether the hook can go in: the turn-start code still has the game's bytes.</summary>
    public static bool CanInstall(RomBuffer rom) => IsInstalled(rom) || rom.Length > Site + Original.Length && rom.ReadBytes(Site, Original.Length).SequenceEqual(Original);

    private static byte[] Code(int blockSnes)
    {
        int table = blockSnes + TableAt;
        var a = new Asm65816();
        // M and X 16-bit, data bank 00, X = the record's offset from $D800
        a.B(0xDA, 0x5A)                                    // PHX / PHY
         .B(0xA5, 0x08, 0x29, 0xFF, 0x00, 0xA8)            // LDA $08 / AND #$00FF / TAY (the monster's slot)
         .B(0xB9, 0x7A, 0x13, 0x29, 0xFF, 0x00)            // LDA $137A,Y / AND #$00FF (its monster id)
         .B(0xA2, 0x00, 0x00)                              // LDX #0
         .Label("loop")
         .B(0x48).Long(0xBF, table)                        // PHA / LDA table,X
         .B(0xC9, 0xFF, 0xFF).Br(0xF0, "missing")          // CMP #$FFFF / BEQ
         .B(0x68).Long(0xDF, table).Br(0xF0, "found")      // PLA / CMP table,X / BEQ
         .B(0xE8, 0xE8, 0xE8, 0xE8, 0xE8, 0xE8).Br(0x80, "loop")
         .Label("missing")
         .B(0x68, 0x7A, 0xFA)                              // PLA / PLY / PLX
         .B(0xBD, 0x22, 0xD8).Br(0xD0, "has")              // the game's: LDA $D822,X / BNE
         .Long(0x5C, 0x08CAEB)                             // JML: no script, it just attacks
         .Label("has").Long(0x5C, 0x08CABA)                // JML: its script in bank 0
         .Label("found")
         .Long(0xBF, table + 4).B(0x3A, 0x48)              // LDA bytes,X / DEC / PHA
         .Long(0xBF, table + 2).B(0xAA)                    // LDA address,X / TAX
         .B(0xA0, (byte)(Buffer & 0xFF), (byte)(Buffer >> 8))      // LDY #buffer
         .B(0x68, 0x8B)                                    // PLA / PHB
         .B(0x54, 0x7E, (byte)(blockSnes >> 16))           // MVN $7E,bank
         .B(0xAB, 0x7A, 0xFA)                              // PLB / PLY / PLX
         .B(0xA9, (byte)(Buffer & 0xFF), (byte)(Buffer >> 8), 0x85, 0x0C)   // LDA #buffer / STA $0C (the "record")
         .B(0x1A, 0x85, 0x09)                              // INC / STA $09 (the script)
         .Long(0x5C, 0x08CAC6);                            // JML on with the turn
        return a.Build(blockSnes + CodeAt);
    }

    /// <summary>Puts the hook and an empty table in (once).</summary>
    public static int Install(RomBuffer rom)
    {
        int have = Block(rom);
        if (have >= 0) return have;
        if (!CanInstall(rom)) throw new InvalidOperationException("The battle's AI code at $08:CAB5 isn't the game's (another patch changed it), so scripts can't go to the added space.");
        if (rom.Length < MapWriter.ExpandedSize) rom.Expand(MapWriter.ExpandedSize);
        var space = ExpansionSpace.Load(rom);
        int at = space.AllocateInOneBank(BlockSize, ExpansionSpace.Kind.Patch, 40);
        space.Save();
        int snes = SnesAddress(at);
        var code = Code(snes);
        if (CodeAt + code.Length > TableAt) throw new InvalidOperationException("The AI hook code doesn't fit.");
        rom.WriteBytes(at, new byte[BlockSize]);
        rom.WriteBytes(at, Magic);
        rom.WriteByte(at + 4, Version);
        rom.WriteUInt16Le(at + 6, ScriptsAt);
        rom.WriteBytes(at + CodeAt, code);
        rom.WriteBytes(at + TableAt, new byte[] { 0xFF, 0xFF, 0, 0, 0, 0 });
        int hook = snes + CodeAt;
        rom.WriteBytes(Site, new byte[] { 0x5C, (byte)hook, (byte)(hook >> 8), (byte)(hook >> 16), 0xEA });
        return at;
    }

    private static IEnumerable<(int At, int Id, int Script, int Bytes)> Entries(RomBuffer rom, int block)
    {
        for (int i = 0; i < TableCapacity; i++)
        {
            int e = block + TableAt + i * EntrySize;
            int id = rom.ReadUInt16Le(e);
            if (id == 0xFFFF) yield break;
            yield return (e, id, block / 0x8000 * 0x8000 + (rom.ReadUInt16Le(e + 2) & 0x7FFF), rom.ReadUInt16Le(e + 4));
        }
    }

    /// <summary>File offset of monster <paramref name="id"/>'s script base (the 00 in front of it) when it has one
    /// here, else null.</summary>
    public static int? Base(RomBuffer rom, int id)
    {
        int b = Block(rom);
        if (b < 0) return null;
        foreach (var e in Entries(rom, b)) if (e.Id == id) return e.Script;
        return null;
    }

    /// <summary>Bytes the script area still has free.</summary>
    public static int Free(RomBuffer rom) => Block(rom) is var b && b >= 0 ? BlockSize - rom.ReadUInt16Le(b + 6) : BlockSize - ScriptsAt;

    /// <summary>Writes <paramref name="script"/> (built for a start at offset 1) as monster <paramref name="id"/>'s
    /// script; the record's own offset (+$22) is set to 0. Returns the file offset of the script's base.</summary>
    public static int Save(RomBuffer rom, int id, int record, byte[] script)
    {
        if (script.Length == 0) throw new InvalidOperationException("The script is empty.");
        if (script.Length > MaxLength) throw new InvalidOperationException($"The script is {script.Length} bytes; a monster's script can be at most {MaxLength}.");
        int block = Install(rom);
        var entry = Entries(rom, block).FirstOrDefault(e => e.Id == id);
        int baseAt;
        if (entry.At > 0 && rom.ReadUInt16Le(entry.Script - 2) >= script.Length + 1) baseAt = entry.Script;
        else
        {
            int next = rom.ReadUInt16Le(block + 6), cap = Math.Max(script.Length + 1 + 8, 16);
            if (next + 2 + cap > BlockSize) throw new InvalidOperationException("Lufia Forge's AI script space is full.");
            rom.WriteUInt16Le(block + next, (ushort)cap);
            baseAt = block + next + 2;
            rom.WriteUInt16Le(block + 6, (ushort)(next + 2 + cap));
        }
        int capacity = rom.ReadUInt16Le(baseAt - 2);
        rom.WriteBytes(baseAt, new byte[capacity]);
        rom.WriteBytes(baseAt + 1, script);
        int e = entry.At > 0 ? entry.At : block + TableAt + Entries(rom, block).Count() * EntrySize;
        if (entry.At <= 0)
        {
            if ((e - block - TableAt) / EntrySize >= TableCapacity) throw new InvalidOperationException("Lufia Forge's AI script table is full.");
            rom.WriteBytes(e + EntrySize, new byte[] { 0xFF, 0xFF, 0, 0, 0, 0 });
        }
        int addr = SnesAddress(baseAt) & 0xFFFF;
        rom.WriteBytes(e, new[] { (byte)id, (byte)(id >> 8), (byte)addr, (byte)(addr >> 8), (byte)(script.Length + 1), (byte)((script.Length + 1) >> 8) });
        rom.WriteUInt16Le(record + 0x22, 0);
        return baseAt;
    }

    /// <summary>Takes monster <paramref name="id"/>'s script out of the table (it then has none, unless its record
    /// points at one in bank 0).</summary>
    public static bool Remove(RomBuffer rom, int id)
    {
        int block = Block(rom);
        if (block < 0) return false;
        var all = Entries(rom, block).ToList();
        int i = all.FindIndex(e => e.Id == id);
        if (i < 0) return false;
        var last = all[^1];
        if (i != all.Count - 1) rom.WriteBytes(all[i].At, rom.ReadBytes(last.At, EntrySize));
        rom.WriteBytes(last.At, new byte[] { 0xFF, 0xFF, 0, 0, 0, 0 });
        return true;
    }

    /// <summary>Gives monster <paramref name="to"/> a copy of <paramref name="from"/>'s script here (for a new monster
    /// made as a copy). False when <paramref name="from"/> has none here.</summary>
    public static bool Copy(RomBuffer rom, int from, int to, int toRecord)
    {
        if (Base(rom, from) is not int b) return false;
        int block = Block(rom);
        int bytes = Entries(rom, block).First(e => e.Id == from).Bytes;
        Save(rom, to, toRecord, rom.ReadBytes(b + 1, bytes - 1));
        return true;
    }

    /// <summary>How many monsters have a script here.</summary>
    public static int Count(RomBuffer rom) => Block(rom) is var b && b >= 0 ? Entries(rom, b).Count() : 0;
}
