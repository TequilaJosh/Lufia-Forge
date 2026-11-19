using LufiaForge.Core.Maps;

namespace LufiaForge.Core.Battle;

// room for lufia 1 monster AI scripts outside bank 0. the battle reads a script through a 16-bit pointer with data
// bank 00 ($08:CACD LDA ($09),Y, $09 = record + offset, "go to" sets $09 = $0C + offset with $0C = the record), so
// every script had to sit in bank 0, which only has about 1.3 KB free. with this patch a monster can keep its script
// in the rom's added space: at the start of its turn ($08:CAB5) a hook looks the monster up in a table and if it's
// there, copies the script into a buffer in low work RAM ($7E:1C00-$1DFF, bank 00 reaches it) and points $0C/$09
// at it. the game's interpreter and every command (ours too) then run it like before. the copy happens at every
// turn start so a monster that carries on after an extra action (command 25) still finds its own script.
//   $08:CAB5  5C Hook EA     (was LDA $D822,X / BEQ $CAEB: the record's script offset, 0 = it just attacks)
// the block: "LFAF", version, (spare), next free byte u16, code at +$10, table at +$100 (id u16, script address
// u16 in the block's bank, bytes to copy u16, FFFF ends it), scripts from +$710 as [capacity u16][00][script] with
// "go to"s counted from the 00 (the script starts at offset 1). a monster with a script here gets its record's
// word +$22 set to 0, so its old bank 0 bytes can be reused
public static class AiFarScripts
{
    public const int Site = 0x44AB5;                       // $08:CAB5
    private static readonly byte[] Original = { 0xBD, 0x22, 0xD8, 0xF0, 0x31 };
    public const int Buffer = 0x1C00, BufferSize = 0x200;   // $7E:1C00-$1DFF (RamMap: low RAM, free)
    // longest script (bytes) that fits the buffer. a 00 goes in front and one after it, so a script that
    // carries on past its last line (say after an extra action of command 25) ends there instead of running
    // whatever an earlier, longer script left in the buffer
    public const int MaxLength = BufferSize - 2;

    private static readonly byte[] Magic = "LFAF"u8.ToArray();
    private const byte Version = 1;
    private const int BlockSize = 0x4000, CodeAt = 0x10, TableAt = 0x100, TableCapacity = 255, EntrySize = 6, ScriptsAt = 0x710;

    private static int FileOffset(int snesAddress) => (snesAddress >> 16 & 0x7F) * 0x8000 + (snesAddress & 0x7FFF);
    private static int SnesAddress(int fileOffset) => (fileOffset / 0x8000 | 0x80) << 16 | (fileOffset % 0x8000 | 0x8000);

    // file offset of the block, or -1 when the patch isn't in the rom
    public static int Block(RomBuffer rom)
    {
        if (rom.Length <= Site + 4 || rom.ReadByte(Site) != 0x5C) return -1;
        int blockOffset = FileOffset(rom.ReadByte(Site + 1) | rom.ReadByte(Site + 2) << 8 | rom.ReadByte(Site + 3) << 16) - CodeAt;
        return blockOffset >= 0 && blockOffset + BlockSize <= rom.Length && rom.ReadBytes(blockOffset, 4).SequenceEqual(Magic) ? blockOffset : -1;
    }

    public static bool IsInstalled(RomBuffer rom) => Block(rom) >= 0;

    // can the hook go in: the turn start code still has the game's bytes
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
         .B(0xBD, 0x22, 0xD8).Br(0xD0, "has")              // the game's own: LDA $D822,X / BNE
         .Long(0x5C, 0x08CAEB)                             // JML: no script, it just attacks
         .Label("has").Long(0x5C, 0x08CABA)                // JML: its script is in bank 0
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

    // puts the hook and an empty table in (only once)
    public static int Install(RomBuffer rom)
    {
        int existing = Block(rom);
        if (existing >= 0) return existing;
        if (!CanInstall(rom)) throw new InvalidOperationException("The battle's AI code at $08:CAB5 isn't the game's (another patch changed it), so scripts can't go to the added space.");
        if (rom.Length < MapWriter.ExpandedSize) rom.Expand(MapWriter.ExpandedSize);
        var freeSpace = ExpansionSpace.Load(rom);
        int blockOffset = freeSpace.AllocateInOneBank(BlockSize, ExpansionSpace.Kind.Patch, 40);
        freeSpace.Save();
        int blockSnes = SnesAddress(blockOffset);
        var code = Code(blockSnes);
        if (CodeAt + code.Length > TableAt) throw new InvalidOperationException("The AI hook code doesn't fit.");
        rom.WriteBytes(blockOffset, new byte[BlockSize]);
        rom.WriteBytes(blockOffset, Magic);
        rom.WriteByte(blockOffset + 4, Version);
        rom.WriteUInt16Le(blockOffset + 6, ScriptsAt);
        rom.WriteBytes(blockOffset + CodeAt, code);
        rom.WriteBytes(blockOffset + TableAt, new byte[] { 0xFF, 0xFF, 0, 0, 0, 0 });
        int hookAddress = blockSnes + CodeAt;
        rom.WriteBytes(Site, new byte[] { 0x5C, (byte)hookAddress, (byte)(hookAddress >> 8), (byte)(hookAddress >> 16), 0xEA });
        return blockOffset;
    }

    private static IEnumerable<(int At, int Id, int Script, int Bytes)> Entries(RomBuffer rom, int block)
    {
        for (int i = 0; i < TableCapacity; i++)
        {
            int entryOffset = block + TableAt + i * EntrySize;
            int id = rom.ReadUInt16Le(entryOffset);
            if (id == 0xFFFF) yield break;
            yield return (entryOffset, id, block / 0x8000 * 0x8000 + (rom.ReadUInt16Le(entryOffset + 2) & 0x7FFF), rom.ReadUInt16Le(entryOffset + 4));
        }
    }

    // file offset of monster id's script base (the 00 in front of it) if it has one here, else null
    public static int? Base(RomBuffer rom, int id)
    {
        int block = Block(rom);
        if (block < 0) return null;
        foreach (var entry in Entries(rom, block)) if (entry.Id == id) return entry.Script;
        return null;
    }

    // bytes the script area still has free
    public static int Free(RomBuffer rom) => Block(rom) is var block && block >= 0 ? BlockSize - rom.ReadUInt16Le(block + 6) : BlockSize - ScriptsAt;

    // writes script (built to start at offset 1) as monster id's script and sets the record's own
    // offset (+$22) to 0. returns the file offset of the script's base
    public static int Save(RomBuffer rom, int id, int record, byte[] script)
    {
        if (script.Length == 0) throw new InvalidOperationException("The script is empty.");
        if (script.Length > MaxLength) throw new InvalidOperationException($"The script is {script.Length} bytes; a monster's script can be at most {MaxLength}.");
        int block = Install(rom);
        var entry = Entries(rom, block).FirstOrDefault(e => e.Id == id);
        if (entry.At <= 0 && Entries(rom, block).Count() >= TableCapacity) throw new InvalidOperationException("Lufia Forge's AI script table is full.");
        int bytes = script.Length + 2;
        int baseAt;
        if (entry.At > 0 && rom.ReadUInt16Le(entry.Script - 2) >= bytes) baseAt = entry.Script;
        else
        {
            int slotCapacity = Math.Max(bytes + 8, 16);
            // slots of scripts that grew or got taken out come back when we pack the live ones together
            if (rom.ReadUInt16Le(block + 6) + 2 + slotCapacity > BlockSize) { Compact(rom, block); entry = Entries(rom, block).FirstOrDefault(e => e.Id == id); }
            int nextFree = rom.ReadUInt16Le(block + 6);
            if (nextFree + 2 + slotCapacity > BlockSize) throw new InvalidOperationException("Lufia Forge's AI script space is full.");
            rom.WriteUInt16Le(block + nextFree, (ushort)slotCapacity);
            baseAt = block + nextFree + 2;
            rom.WriteUInt16Le(block + 6, (ushort)(nextFree + 2 + slotCapacity));
        }
        int capacity = rom.ReadUInt16Le(baseAt - 2);
        rom.WriteBytes(baseAt, new byte[capacity]);
        rom.WriteBytes(baseAt + 1, script);
        int entryOffset = entry.At > 0 ? entry.At : block + TableAt + Entries(rom, block).Count() * EntrySize;
        if (entry.At <= 0)
        {
            if ((entryOffset - block - TableAt) / EntrySize >= TableCapacity) throw new InvalidOperationException("Lufia Forge's AI script table is full.");
            rom.WriteBytes(entryOffset + EntrySize, new byte[] { 0xFF, 0xFF, 0, 0, 0, 0 });
        }
        int scriptAddress = SnesAddress(baseAt) & 0xFFFF;
        rom.WriteBytes(entryOffset, new[] { (byte)id, (byte)(id >> 8), (byte)scriptAddress, (byte)(scriptAddress >> 8), (byte)bytes, (byte)(bytes >> 8) });
        rom.WriteUInt16Le(record + 0x22, 0);
        return baseAt;
    }

    // packs the live scripts together from the start of the script area (each keeps a little room to grow)
    private static void Compact(RomBuffer rom, int block)
    {
        var live = Entries(rom, block).Select(e => (e.At, Data: rom.ReadBytes(e.Script, e.Bytes))).ToList();
        rom.WriteBytes(block + ScriptsAt, new byte[BlockSize - ScriptsAt]);
        int nextFree = ScriptsAt;
        foreach (var (entryOffset, data) in live)
        {
            int slotCapacity = Math.Max(data.Length + 8, 16);
            rom.WriteUInt16Le(block + nextFree, (ushort)slotCapacity);
            rom.WriteBytes(block + nextFree + 2, data);
            rom.WriteUInt16Le(entryOffset + 2, (ushort)(SnesAddress(block + nextFree + 2) & 0xFFFF));
            nextFree += 2 + slotCapacity;
        }
        rom.WriteUInt16Le(block + 6, (ushort)nextFree);
    }

    // takes monster id's script out of the table (then it has none, unless its record
    // points at one in bank 0)
    public static bool Remove(RomBuffer rom, int id)
    {
        int block = Block(rom);
        if (block < 0) return false;
        var all = Entries(rom, block).ToList();
        int index = all.FindIndex(e => e.Id == id);
        if (index < 0) return false;
        var last = all[^1];
        if (index != all.Count - 1) rom.WriteBytes(all[index].At, rom.ReadBytes(last.At, EntrySize));
        rom.WriteBytes(last.At, new byte[] { 0xFF, 0xFF, 0, 0, 0, 0 });
        return true;
    }

    // gives monster to a copy of from's script here (for a new monster made as a copy).
    // false when from has none here
    public static bool Copy(RomBuffer rom, int from, int to, int toRecord)
    {
        if (Base(rom, from) is not int fromBase) return false;
        int block = Block(rom);
        int bytes = Entries(rom, block).First(e => e.Id == from).Bytes;
        Save(rom, to, toRecord, rom.ReadBytes(fromBase + 1, bytes - 2));
        return true;
    }

    // how many monsters have a script here
    public static int Count(RomBuffer rom) => Block(rom) is var block && block >= 0 ? Entries(rom, block).Count() : 0;
}
