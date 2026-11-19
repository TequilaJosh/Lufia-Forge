using LufiaForge.Core.Maps;

namespace LufiaForge.Core.Battle;

// room for 128 spells (the game has 56, 00-37). spell records live in bank 1F and are found through 56 16-bit
// pointers at $1F:DB00, only read by $08:FABD (A8 X16, DB = 1F, A = spell, returns X = the record). that table
// can't grow (other data follows it), so we hook the lookup:
//   $08:FABD  08 22 xx xx xx 28 60  PHP / JSL lookup / PLP / RTS
// the new lookup reads spells 00-37 from the game's table like before. for 38-7F it reads a 3-byte pointer from its
// own table and also sets DB to the record's bank: the three callers ($08:FA2D, $08:FA66, $08:FA7D) read the record
// with DB-relative loads and restore DB themselves (PHB at entry, PLB at exit). a record's +15 word (the spell's
// script, relative to $1F:DB00) gets read by the caller from bank 1F, so new spells copied from an existing one share
// its animation and effect script.
//
// the block (one bank, in the added rom space): the lookup, then "LFSP", the number of new spells, their record
// lengths, the pointer table (unused entries point at spell 00) and 72 record slots of 32 bytes.
//
// the patch also installs SpellMemory: every character can learn up to 64 spells, and the four learn
// lists move to the added space (Character Magic can make them longer)
public static class SpellTablePatch
{
    public const int FirstNew = 0x38, MaxSpells = 0x80, NewSlots = MaxSpells - FirstNew, SlotSize = 32;
    public const int LookupSite = 0x47ABD;   // $08:FABD
    public const int Trick = 0x1A;
    public const string TricksterName = "Trickstr";
    private static readonly byte[] LookupOriginal = Convert.FromHexString("08EBA900EBC2200AAABD00DB186900DBAA2860");
    private const int Magic = 64, Count = 68, Lengths = 72, Table = Lengths + NewSlots, Records = Table + NewSlots * 3;
    public const int BlockSize = Records + NewSlots * SlotSize;

    private static readonly byte[] Routine =
    {
        0xC9, 0x38,                //  0 CMP #$38
        0xB0, 0x11,                //  2 BCS new
        0xEB, 0xA9, 0x00, 0xEB,    //  4 XBA / LDA #0 / XBA
        0xC2, 0x20,                //  8 REP #$20
        0x0A, 0xAA,                // 10 ASL / TAX
        0xBD, 0x00, 0xDB,          // 12 LDA $DB00,X        the game's table
        0x18, 0x69, 0x00, 0xDB,    // 15 CLC / ADC #$DB00
        0xAA,                      // 19 TAX
        0x6B,                      // 20 RTL
        0x22, 0x00, 0x00, 0x00,    // 21 new: JSL ensure     (spell memory in the new format before $15CF gets used)
        0x38, 0xE9, 0x38,          // 25 SEC / SBC #$38
        0xC2, 0x30,                // 28 REP #$30
        0x29, 0xFF, 0x00,          // 30 AND #$00FF
        0x48,                      // 33 PHA
        0x0A,                      // 34 ASL
        0x18, 0x63, 0x01,          // 35 CLC / ADC 1,S      x 3
        0xAA,                      // 38 TAX
        0x68,                      // 39 PLA
        0xBF, 0x00, 0x00, 0x00,    // 40 LDA table,X        the record (in this block's bank)
        0xAA,                      // 44 TAX
        0xA0, 0xCF, 0x15,          // 45 LDY #$15CF         work RAM, seen through bank 1F
        0xA9, 0x1E, 0x00,          // 48 LDA #$001E         31 bytes
        0x8B,                      // 51 PHB
        0x54, 0x00, 0x00,          // 52 MVN $00, block     (MVN sets DB)
        0xAB,                      // 55 PLB                DB = 1F again
        0xA2, 0xCF, 0x15,          // 56 LDX #$15CF
        0x6B,                      // 59 RTL
    };
    private const int EnsureOperand = 22, TableOperand = 41, MvnSource = 54;
    // an added spell's record gets copied here when it's looked up ($15CF-$15ED, free work RAM after the
    // spell memory's bits). bank 1F reads $0000-$1FFF from work RAM, so the game's code (which reads the record and a
    // pointer into it, $179E, from bank 1F) sees it like one of its own records
    public const int RecordBuffer = 0x15CF;

    private static int FileOffset(int snesAddress) => (snesAddress >> 16 & 0x7F) * 0x8000 + (snesAddress & 0x7FFF);
    private static int SnesAddress(int fileOffset) => (fileOffset / 0x8000 | 0x80) << 16 | (fileOffset % 0x8000 | 0x8000);

    public static bool IsOriginal(RomBuffer rom) =>
        (rom.ReadBytes(LookupSite, LookupOriginal.Length).SequenceEqual(LookupOriginal) || OldTrickster(rom) > 0 || BlockOffset(rom) > 0) &&
        (SpellMemory.SitesOriginal(rom) || SpellMemory.Installed(rom) || SpellMemory.OldBlock(rom) > 0);
    // the spell table and the 64-per-character spell memory are both in
    public static bool IsApplied(RomBuffer rom) => BlockOffset(rom) > 0 && SpellMemory.Installed(rom) && CurrentLookup(rom) && SpellMemory.SwapFixed(rom);

    // the block's lookup is this version's (copies added spells to RecordBuffer) and calls the
    // spell memory's ensure. the first version switched DB to the record's bank, which the effect code (reading
    // through $179E in bank 1F) didn't follow, so casting some added spells froze the game
    private static bool CurrentLookup(RomBuffer rom)
    {
        int block = BlockOffset(rom), memoryBlock = SpellMemory.BlockOffset(rom);
        if (block == 0 || memoryBlock == 0) return false;
        int ensure = SpellMemory.EnsureAddress(rom);
        return rom.ReadByte(block + 21) == 0x22 && rom.ReadByte(block + 52) == 0x54 &&
               (rom.ReadByte(block + EnsureOperand) | rom.ReadByte(block + EnsureOperand + 1) << 8 | rom.ReadByte(block + EnsureOperand + 2) << 16) == ensure;
    }

    // writes this version's lookup into an existing block (keeping its spells) and links it to the spell memory
    private static void UpdateLookup(RomBuffer rom)
    {
        int block = BlockOffset(rom);
        var code = (byte[])Routine.Clone();
        int table = SnesAddress(block) + Table, ensure = SpellMemory.EnsureAddress(rom);
        code[TableOperand] = (byte)table; code[TableOperand + 1] = (byte)(table >> 8); code[TableOperand + 2] = (byte)(table >> 16);
        code[EnsureOperand] = (byte)ensure; code[EnsureOperand + 1] = (byte)(ensure >> 8); code[EnsureOperand + 2] = (byte)(ensure >> 16);
        code[MvnSource] = (byte)(SnesAddress(block) >> 16);
        rom.WriteBytes(block, code);
        // unused entries pointed at spell 00 in bank 1F, now they point at their own (empty) slot in this bank
        for (int i = NewCount(rom); i < NewSlots; i++)
        {
            int slotAddress = SnesAddress(block + Records + i * SlotSize);
            rom.WriteBytes(block + Table + i * 3, new[] { (byte)slotAddress, (byte)(slotAddress >> 8), (byte)(slotAddress >> 16) });
        }
    }

    // the first version (a 0.4.0-beta.7 test build): only spell 38 Trickstr, its record at $1F:D040 and a 39-byte
    // lookup whose new-spell branch starts SBC #$38. returns that lookup's file offset, or 0. Add updates it
    public static int OldTrickster(RomBuffer rom)
    {
        if (rom.ReadByte(LookupSite) != 0x08 || rom.ReadByte(LookupSite + 1) != 0x22) return 0;
        int lookupOffset = FileOffset(rom.ReadByte(LookupSite + 2) | rom.ReadByte(LookupSite + 3) << 8 | rom.ReadByte(LookupSite + 4) << 16);
        return lookupOffset > 0 && lookupOffset + 39 <= rom.Length && rom.ReadByte(lookupOffset) == 0xC9 && rom.ReadByte(lookupOffset + 1) == FirstNew &&
               rom.ReadByte(lookupOffset + 21) == 0xE9 && rom.ReadByte(lookupOffset + 22) == 0x38 ? lookupOffset : 0;
    }
    private const int OldRecord = 0xFD040, OldRecordLength = 27;
    // an earlier version (the first Trickstr, a table without the spell memory, or the earlier Guy / Aguro
    // lists) is in the rom: Add (Update) brings it up to date and keeps its spells and lists
    public static bool NeedsUpdate(RomBuffer rom) =>
        !IsApplied(rom) && (OldTrickster(rom) > 0 || BlockOffset(rom) > 0 || SpellMemory.Installed(rom) || SpellMemory.OldBlock(rom) > 0);

    // links the lookup to the spell memory (for test copies placed by hand)
    public static void Link(RomBuffer rom) => UpdateLookup(rom);

    // file offset of the patch's block, or 0
    public static int BlockOffset(RomBuffer rom)
    {
        if (rom.ReadByte(LookupSite) != 0x08 || rom.ReadByte(LookupSite + 1) != 0x22) return 0;
        int block = FileOffset(rom.ReadByte(LookupSite + 2) | rom.ReadByte(LookupSite + 3) << 8 | rom.ReadByte(LookupSite + 4) << 16);
        return block > 0 && block + BlockSize <= rom.Length && rom.ReadByte(block) == 0xC9 && rom.ReadByte(block + 1) == FirstNew &&
               rom.ReadByte(block + Magic) == 'L' && rom.ReadByte(block + Magic + 3) == 'P' ? block : 0;
    }

    // how many spells were added (0-72)
    public static int NewCount(RomBuffer rom) { int block = BlockOffset(rom); return block == 0 ? (OldTrickster(rom) > 0 ? 1 : 0) : Math.Min((int)rom.ReadByte(block + Count), NewSlots); }

    // how many spells the rom has: 56, plus the ones added with this patch
    public static int SpellCount(RomBuffer rom) => FirstNew + NewCount(rom);

    // file offset of spell id's record
    public static int SpellOffset(RomBuffer rom, int id)
    {
        if (id < FirstNew) return GameDataSpells.Offsets[id];
        int block = BlockOffset(rom);
        if (block == 0 && id == FirstNew && OldTrickster(rom) > 0) return OldRecord;
        return block == 0 ? GameDataSpells.Offsets[0] : block + Records + (id - FirstNew) * SlotSize;
    }

    // size of spell id's record (the last of the game's own counts as 22)
    public static int SpellLength(RomBuffer rom, int id)
    {
        if (id >= FirstNew)
        {
            int block = BlockOffset(rom);
            if (block == 0) return id == FirstNew && OldTrickster(rom) > 0 ? OldRecordLength : 0;
            return rom.ReadByte(block + Lengths + id - FirstNew);
        }
        return id + 1 < FirstNew ? GameDataSpells.Offsets[id + 1] - GameDataSpells.Offsets[id] : 23;   // (Absobl, the last: its effect ends with 00 at +22)
    }

    public static void Apply(RomBuffer rom)
    {
        // (roms patched before the field menu's swap was turned off, the rest is current)
        if (SpellMemory.Installed(rom)) SpellMemory.FixSwap(rom);
        if (IsApplied(rom)) { rom.FixChecksum(); return; }
        if (!IsOriginal(rom)) throw new InvalidOperationException("The game's spell code isn't the original (another patch changed it), so spells can't be expanded safely.");
        if (rom.Length < MapWriter.ExpandedSize) rom.Expand(MapWriter.ExpandedSize);
        if (BlockOffset(rom) == 0) ApplyTable(rom);
        if (!SpellMemory.Installed(rom)) SpellMemory.Install(rom);
        UpdateLookup(rom);
        rom.FixChecksum();
    }

    private static void ApplyTable(RomBuffer rom)
    {
        var freeSpace = ExpansionSpace.Load(rom);
        // the first version: keep Trickstr as spell 38 (characters and monsters keep it), drop its lookup and record
        byte[]? trick = null;
        int oldLookup = OldTrickster(rom);
        if (oldLookup > 0)
        {
            trick = rom.ReadBytes(OldRecord, OldRecordLength);
            rom.WriteBytes(OldRecord, Enumerable.Repeat((byte)0xFF, OldRecordLength).ToArray());
            if (freeSpace.At(oldLookup) is { } entry && entry.Start == oldLookup) freeSpace.Free(oldLookup);
            rom.WriteBytes(LookupSite, LookupOriginal);
        }
        WriteAt(rom, freeSpace.AllocateInOneBank(BlockSize, ExpansionSpace.Kind.Patch, 8));
        freeSpace.Save();
        if (trick != null)
        {
            int block = BlockOffset(rom), recordOffset = block + Records;
            rom.WriteBytes(recordOffset, trick);
            rom.WriteByte(block + Lengths, OldRecordLength);
            int recordAddress = SnesAddress(recordOffset);
            rom.WriteBytes(block + Table, new[] { (byte)recordAddress, (byte)(recordAddress >> 8), (byte)(recordAddress >> 16) });
            rom.WriteByte(block + Count, 1);
        }
    }

    // writes an empty block (no new spells) at offset and hooks the game's lookup
    public static void WriteAt(RomBuffer rom, int offset)
    {
        var block = new byte[BlockSize];
        Routine.CopyTo(block, 0);
        int table = SnesAddress(offset) + Table;
        void Long(int operandAt, int address) { block[operandAt] = (byte)address; block[operandAt + 1] = (byte)(address >> 8); block[operandAt + 2] = (byte)(address >> 16); }
        Long(TableOperand, table);
        block[MvnSource] = (byte)(SnesAddress(offset) >> 16);
        "LFSP"u8.ToArray().CopyTo(block, Magic);
        for (int i = 0; i < NewSlots; i++) Long(Table + i * 3, SnesAddress(offset + Records + i * SlotSize));   // each slot (empty = zeros)
        rom.WriteBytes(offset, block);
        int lookupSnes = SnesAddress(offset);
        var hook = Enumerable.Repeat((byte)0xEA, LookupOriginal.Length).ToArray();
        new byte[] { 0x08, 0x22, (byte)lookupSnes, (byte)(lookupSnes >> 8), (byte)(lookupSnes >> 16), 0x28, 0x60 }.CopyTo(hook, 0);
        rom.WriteBytes(LookupSite, hook);
    }

    // adds a spell copied from template (its effect, animation and numbers) and names it. returns its id
    public static int AddSpell(RomBuffer rom, int template, string name)
    {
        int block = BlockOffset(rom);
        if (block == 0) throw new InvalidOperationException("Add the \"Up to 128 spells\" patch first.");
        int newIndex = NewCount(rom);
        if (newIndex >= NewSlots) throw new InvalidOperationException($"The ROM already has {MaxSpells} spells.");
        int length = SpellLength(rom, template);
        if (length < 20 || length > 31) throw new InvalidOperationException($"Spell {template:X2}'s record can't be copied.");
        var record = rom.ReadBytes(SpellOffset(rom, template), length);
        var paddedName = (name ?? "").PadRight(8).Substring(0, 8);
        for (int i = 0; i < 8; i++) record[i] = (byte)(paddedName[i] == ' ' ? 0x20 : paddedName[i]);
        int recordOffset = block + Records + newIndex * SlotSize;
        rom.WriteBytes(recordOffset, record);
        rom.WriteByte(block + Lengths + newIndex, (byte)length);
        int recordAddress = SnesAddress(recordOffset);
        rom.WriteBytes(block + Table + newIndex * 3, new[] { (byte)recordAddress, (byte)(recordAddress >> 8), (byte)(recordAddress >> 16) });
        rom.WriteByte(block + Count, (byte)(newIndex + 1));
        return FirstNew + newIndex;
    }

    // takes out the last added spell, and every character's, event's and monster's use of it (the Ancient
    // Cave's spell chests too). if that fails the rom is left as it was
    public static void RemoveLastSpell(RomBuffer rom)
    {
        int block = BlockOffset(rom), newCount = NewCount(rom);
        if (block == 0 || newCount == 0) return;
        int id = FirstNew + newCount - 1;
        Atomic(rom, () =>
        {
            ForgetSpells(rom, s => s == id);
            rom.WriteBytes(block + Records + (newCount - 1) * SlotSize, new byte[SlotSize]);
            rom.WriteByte(block + Lengths + newCount - 1, 0);
            rom.WriteByte(block + Count, (byte)(newCount - 1));
            Cave.AncientCave.Refresh(rom);
        });
    }

    // runs edit. if it throws, the rom gets its bytes from before back
    private static void Atomic(RomBuffer rom, Action edit)
    {
        var before = rom.ToArray();
        try { edit(); }
        catch (Exception)
        {
            rom.ReplaceAll(before);
            throw;
        }
    }

    // makes added spell id work like template: its effect and the effect's numbers, animation and script,
    // usage and target get copied (the record becomes the template's size). the new spell keeps its name and MP cost
    public static void SetWorksLike(RomBuffer rom, int id, int template)
    {
        int block = BlockOffset(rom);
        if (block == 0 || id < FirstNew || id >= SpellCount(rom)) throw new InvalidOperationException("Only spells added with the patch (38 and up) can change what they do.");
        int length = SpellLength(rom, template);
        if (length < 20 || length > 31) throw new InvalidOperationException($"Spell {template:X2}'s record can't be copied.");
        int recordOffset = SpellOffset(rom, id);
        var name = rom.ReadBytes(recordOffset, 8);
        byte mp = rom.ReadByte(recordOffset + 11);
        var record = rom.ReadBytes(SpellOffset(rom, template), length);
        name.CopyTo(record, 0);
        record[11] = mp;
        rom.WriteBytes(recordOffset, new byte[SlotSize]);
        rom.WriteBytes(recordOffset, record);
        rom.WriteByte(block + Lengths + id - FirstNew, (byte)length);
    }

    // the id of an added spell with this name, or -1
    public static int Find(RomBuffer rom, string name)
    {
        for (int id = FirstNew; id < SpellCount(rom); id++)
            if (new string(rom.ReadBytes(SpellOffset(rom, id), 8).Select(c => (char)c).ToArray()).TrimEnd() == name) return id;
        return -1;
    }

    // Trickstr: Trick (raises ATP) on the whole party, 9 MP. only added once, returns its id
    public static int AddTrickster(RomBuffer rom)
    {
        int id = Find(rom, TricksterName);
        if (id >= 0) return id;
        id = AddSpell(rom, Trick, TricksterName);
        int recordOffset = SpellOffset(rom, id);
        rom.WriteByte(recordOffset + 10, 0x01);   // target: all of the party (Trick: one member)
        rom.WriteByte(recordOffset + 11, 9);      // MP (Trick 5)
        return id;
    }

    // takes the patch out (and the spells 38 and up with it, see ForgetSpells). if that fails the
    // rom is left as it was
    public static void Remove(RomBuffer rom)
    {
        int block = BlockOffset(rom);
        if (block == 0 && !SpellMemory.Installed(rom)) return;
        Atomic(rom, () =>
        {
            if (AguroMagicPatch.IsApplied(rom)) AguroMagicPatch.Remove(rom);
            ForgetSpells(rom, s => s >= FirstNew);
            SpellMemory.Remove(rom);   // the game's own lists come back (as they are in the rom)
            if (block > 0)
            {
                rom.WriteBytes(LookupSite, LookupOriginal);
                var freeSpace = ExpansionSpace.Load(rom);
                freeSpace.Free(block);
                freeSpace.Save();
            }
            // the Ancient Cave's spell chests teach from the learn lists, so rebuild them from the game's own
            Cave.AncientCave.Refresh(rom);
            rom.FixChecksum();
        });
    }

    // nobody gets to keep learning or casting a spell that doesn't exist anymore: spell lists (with the Guy /
    // Aguro patch), events that teach it (3D cc ss, the prologue party's spells too: the line gets taken out,
    // jumps to it go to the next line), and monsters' "cast spell" steps (they become "attack"). the Ancient Cave's
    // own events are left to Cave.AncientCave.Refresh. throws before changing anything if a line
    // can't be taken out
    private static void ForgetSpells(RomBuffer rom, Func<int, bool> gone)
    {
        var teaching = TeachingEvents(rom, gone);
        if (SpellMemory.Installed(rom))
        {
            var lists = SpellMemory.ReadLists(rom);
            if (lists.Sum(l => l.RemoveAll(e => gone(e.Spell))) > 0) SpellMemory.WriteLists(rom, lists);
        }
        foreach (var (map, eventId) in teaching)
        {
            EventScript? script;
            try { script = EventScript.Load(rom, map, eventId); } catch (Exception) { continue; }
            var lines = script?.Ops.Where(o => Teaches(o, gone)).ToList();
            if (script == null || lines!.Count == 0) continue;   // (a slot sharing an event that was saved before)
            foreach (var line in lines) script.Remove(line);
            script.Save(true);
        }
        foreach (var (record, cmd) in MonsterAi.AllCommands(rom))
            if (cmd.Op == 0x07 && gone(cmd.Args[0])) { rom.WriteByte(MonsterAi.ByteAt(rom, record, cmd.Offset), 0x05); rom.WriteByte(MonsterAi.ByteAt(rom, record, cmd.Offset + 1), 0x00); }
    }

    // a "learn spell" line (3D cc ss) for a spell that's going
    private static bool Teaches(ScriptOp op, Func<int, bool> gone) => !op.IsText && op.Bytes.Length == 3 && op.Bytes[0] == 0x3D && gone(op.Bytes[2]);

    // the events (map, event) with a line teaching a spell that's going, outside the Ancient Cave's maps.
    // throws when one of those lines can't be taken out (nothing changed yet)
    private static List<(int Map, int Event)> TeachingEvents(RomBuffer rom, Func<int, bool> gone)
    {
        var list = new List<(int, int)>();
        var cave = Cave.AncientCave.Read(rom);
        foreach (var map in MapCatalog.Scan(rom))
        {
            if (cave != null && (map.MapId == cave.Map1 || map.MapId == cave.Map2)) continue;
            for (int eventId = 0, slotCount = EventScript.SlotCount(rom, map.MapId); eventId < slotCount; eventId++)
            {
                EventScript? script;
                try { script = EventScript.Load(rom, map.MapId, eventId); } catch (Exception) { continue; }
                if (script == null) continue;
                var lines = script.Ops.Where(o => Teaches(o, gone)).ToList();
                if (lines.Count == 0) continue;
                // (tried on this copy, the one saved later gets loaded again)
                foreach (var line in lines)
                    if (script.Remove(line) is string why)
                        throw new InvalidOperationException($"Map {map.MapId:X2} event {eventId} teaches spell {line.Bytes[2]:X2}, and that line can't be taken out by itself ({why}). Change the event in the Events editor first.");
                list.Add((map.MapId, eventId));
            }
        }
        return list;
    }
}

// the game's 56 spell records (bank 1F, variable length). the Game Data editors use the same list
internal static class GameDataSpells
{
    public static readonly int[] Offsets =
    {
        0xFE354, 0xFE368, 0xFE382, 0xFE39C, 0xFE3B6, 0xFE3D0, 0xFE3EA, 0xFE404,
        0xFE41E, 0xFE438, 0xFE452, 0xFE46C, 0xFE486, 0xFE4A0, 0xFE4BA, 0xFE4D4,
        0xFE4EE, 0xFE506, 0xFE51E, 0xFE536, 0xFE54E, 0xFE567, 0xFE580, 0xFE598,
        0xFE5B0, 0xFE5C7, 0xFE5E2, 0xFE5FD, 0xFE615, 0xFE630, 0xFE64B, 0xFE666,
        0xFE681, 0xFE699, 0xFE6B1, 0xFE6CA, 0xFE6E3, 0xFE6FC, 0xFE715, 0xFE72E,
        0xFE746, 0xFE75E, 0xFE77A, 0xFE796, 0xFE7AE, 0xFE7C6, 0xFE7DD, 0xFE7F4,
        0xFE80B, 0xFE822, 0xFE83B, 0xFE853, 0xFE86B, 0xFE885, 0xFE89F, 0xFE8B9,
    };
}
