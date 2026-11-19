namespace LufiaForge.Core.Battle;

// the monster list, plus new monsters / monster pictures.
// monster records: u16 offsets from $00:D800 (file 0x5800), only read by $01:F67D (Y = offset). its callers
// (the battle loader at $01:F570, the AI at $08:CAA5) read the record as $D800+Y / $D800+X with data bank 00, so a
// record has to be in bank 00 at $D800 or above. the game's 165 records fill $D94A-$FD64. new monsters: the offset
// table moves to the added space (block "LFMT", room for 255 ids, $01:F67D jumps to a copy that reads it), which frees
// its old place ($D800-$D949) for new records. the end of bank 0 ($FDD0-$FFAF) is the other spot. AI scripts are
// found as (record + offset) & FFFF so they can sit anywhere in bank 00, new monsters' scripts go to the unused
// space before the table ($D2B0-$D7FF) first.
// pictures: resource 0x43 + graphic (record +14), worked out at $08:D957. new pictures get new resource ids and a
// hook there looks graphics 6C and up up in a table (block "LFMP")
public static class MonsterTable
{
    public const int OriginalTable = 0x5800, OriginalCount = 165, Capacity = 255;
    // fixed part of a record. the game's records are 39-48 bytes (see RecordLength)
    public const int FixedSize = 0x24, LargestRecord = FixedSize + 3 + 8 + 7 + 0x10;

    // length of a monster record, read the way the battle loader ($01:F59F-F638) reads it: 0x24 fixed bytes, a mask
    // byte and one byte per set bit (8), a mask byte and one byte per set bit of its low 7, a count byte and that many
    // bytes. (a script, if there is one, starts right after)
    public static int RecordLength(RomBuffer rom, int record)
    {
        int position = record + FixedSize;
        int mask1 = rom.ReadByte(position); position += 1 + System.Numerics.BitOperations.PopCount((uint)mask1);
        int mask2 = rom.ReadByte(position); position += 1 + System.Numerics.BitOperations.PopCount((uint)(mask2 & 0x7F));
        int count = rom.ReadByte(position); position += 1 + count;
        return position - record;
    }
    public const int OriginalPictures = 0x6C, PictureCapacity = 0x100;
    private const int LookupAt = 0xF67D;                 // $01:F67D: PHP SEP #$20 XBA LDA #0 XBA REP #$30 ASL TAY LDA $D800,Y TAY SEP #$20 PLP RTL
    private static readonly byte[] LookupOriginal = Convert.FromHexString("08E220EBA900EBC2300AA8B900D8A8E22028 6B".Replace(" ", ""));
    private const int PictureAt = 0x45959;               // $08:D959: CLC ADC #$0043 STA $16
    private static readonly byte[] PictureOriginal = Convert.FromHexString("1869430085 16".Replace(" ", ""));
    private static readonly byte[] TableMagic = "LFMT"u8.ToArray(), PictureMagic = "LFMP"u8.ToArray();
    private const int Header = 8;

    // bank 00 places for new records (file offsets, they have to be at $D800 or above)
    public static readonly (int Start, int End)[] RecordAreas = { (0x5802, 0x594A), (MonsterAi.FreeStart, MonsterAi.FreeEnd) };
    // bank 00 places for scripts (anywhere in bank 00, the space before the table first)
    public static readonly (int Start, int End)[] ScriptAreas = { (0x52B0, 0x5800), (0x5802, 0x594A), (MonsterAi.FreeStart, MonsterAi.FreeEnd) };

    private static int FileOffset(int snesAddress) => (snesAddress >> 16 & 0x7F) * 0x8000 + (snesAddress & 0x7FFF);
    private static int SnesAddress(int fileOffset) => (fileOffset / 0x8000 | 0x80) << 16 | (fileOffset % 0x8000 | 0x8000);
    private static int Long(RomBuffer rom, int offset) => rom.ReadByte(offset) | rom.ReadByte(offset + 1) << 8 | rom.ReadByte(offset + 2) << 16;

    // the record table

    private static int TableBlock(RomBuffer rom)
    {
        if (rom.ReadByte(LookupAt) != 0x5C) return 0;
        int code = FileOffset(Long(rom, LookupAt + 1));
        int block = code - Header - Capacity * 2;
        return block > 0 && rom.ReadBytes(block, 4).SequenceEqual(TableMagic) ? block : 0;
    }
    public static bool IsMoved(RomBuffer rom) => TableBlock(rom) > 0;
    private static int TableOffset(RomBuffer rom) { int block = TableBlock(rom); return block > 0 ? block + Header : OriginalTable; }

    // how many monsters the rom has (165 in the game)
    public static int Count(RomBuffer rom) { int block = TableBlock(rom); return block > 0 ? rom.ReadByte(block + 4) : OriginalCount; }

    public static int RecordOffset(RomBuffer rom, int id) => OriginalTable + rom.ReadUInt16Le(TableOffset(rom) + id * 2);

    // moves the offset table to the added space (the game reads it through a copy of $01:F67D)
    public static void Move(RomBuffer rom)
    {
        if (IsMoved(rom)) return;
        if (!rom.ReadBytes(LookupAt, LookupOriginal.Length).SequenceEqual(LookupOriginal))
            throw new InvalidOperationException("The game's monster lookup isn't the original, so its table can't be moved safely.");
        if (rom.Length < Maps.MapWriter.ExpandedSize) rom.Expand(Maps.MapWriter.ExpandedSize);
        var freeSpace = Maps.ExpansionSpace.Load(rom);
        int size = Header + Capacity * 2 + 32;
        int blockOffset = freeSpace.AllocateInOneBank(size, Maps.ExpansionSpace.Kind.Patch, 15);
        freeSpace.Save();
        var block = new byte[size];
        TableMagic.CopyTo(block, 0);
        block[4] = OriginalCount;
        rom.ReadBytes(OriginalTable, OriginalCount * 2).CopyTo(block, Header);
        int table = SnesAddress(blockOffset + Header);
        var code = new byte[]
        {
            0x08, 0xE2, 0x20, 0xEB, 0xA9, 0x00, 0xEB, 0xC2, 0x30,     // PHP; SEP #$20; XBA; LDA #0; XBA; REP #$30
            0xDA, 0x0A, 0xAA,                                         // PHX; ASL; TAX
            0xBF, (byte)table, (byte)(table >> 8), (byte)(table >> 16), // LDA table,X
            0xA8, 0xFA, 0xE2, 0x20, 0x28, 0x6B,                       // TAY; PLX; SEP #$20; PLP; RTL
        };
        code.CopyTo(block, Header + Capacity * 2);
        rom.WriteBytes(blockOffset, block);
        int codeAddress = SnesAddress(blockOffset + Header + Capacity * 2);
        rom.WriteBytes(LookupAt, new byte[] { 0x5C, (byte)codeAddress, (byte)(codeAddress >> 8), (byte)(codeAddress >> 16) });   // JML
        // the old table's place becomes free (cleared so nothing reads stale offsets)
        rom.WriteBytes(OriginalTable, Enumerable.Repeat((byte)0xFF, OriginalCount * 2).ToArray());
        rom.FixChecksum();
    }

    // Used: bank 00 bytes in use by records and scripts (file ranges), optionally leaving one monster's script out.
    // Overlaps: does another monster's record or script use any of these bytes (a script shared by several monsters)
    public static bool Overlaps(RomBuffer rom, int start, int length, int skipScriptOf) =>
        Used(rom, skipScriptOf).Any(u => u.Start < start + length && start < u.End);

    private static List<(int Start, int End)> Used(RomBuffer rom, int skipScriptOf = -1)
    {
        var used = new List<(int, int)>();
        // the game's offset table is only free once it has moved to the added space (a new monster moves it)
        if (!IsMoved(rom)) used.Add((OriginalTable, OriginalTable + OriginalCount * 2));
        for (int id = 0, count = Count(rom); id < count; id++)
        {
            int record = RecordOffset(rom, id);
            used.Add((record, record + RecordLength(rom, record)));
            if (id == skipScriptOf) continue;
            // (a script in Lufia Forge's script space takes no room in bank 0)
            if (MonsterAi.HomeOf(rom, record, id).Far) continue;
            var (scriptStart, scriptLength) = MonsterAi.ScriptRange(rom, record);
            if (scriptLength > 0) { int scriptAt = MonsterAi.At(record, scriptStart); used.Add((scriptAt, scriptAt + scriptLength)); }
        }
        // bytes another hack put in these areas (they differ from the clean game in the rom the project started from)
        foreach (var (start, end) in ScriptAreas) used.AddRange(RomIdentity.ForeignRanges(rom, start, end));
        return used;
    }

    // a free spot of len bytes in one of areas, or -1
    public static int FindSpace(RomBuffer rom, int len, (int Start, int End)[] areas, int skipScriptOf = -1) =>
        FindSpace(Used(rom, skipScriptOf), len, areas);

    // bank 00 bytes in use, for several FindSpace calls in a row
    public static List<(int Start, int End)> UsedSpace(RomBuffer rom, int skipScriptOf = -1) => Used(rom, skipScriptOf);

    // a free spot of len bytes in one of areas, given the bytes in use
    public static int FindSpace(List<(int Start, int End)> used, int len, (int Start, int End)[] areas)
    {
        foreach (var (start, end) in areas)
            for (int spot = start; spot + len <= end; spot++)
            {
                int q = spot;
                var blockedUntil = used.Where(u => u.Start < q + len && u.End > q).Select(u => u.End).DefaultIfEmpty(-1).Max();
                if (blockedUntil < 0) return spot;
                spot = blockedUntil - 1;
            }
        return -1;
    }

    // room for how many more monsters (records only, their scripts need room too)
    public static int Room(RomBuffer rom)
    {
        var used = Used(rom);
        int free = 0;
        foreach (var (start, end) in RecordAreas)
        {
            int run = 0;
            for (int p = start; p < end; p++)
            {
                if (used.Any(u => p >= u.Start && p < u.End)) { free += run / LargestRecord; run = 0; }
                else run++;
            }
            free += run / LargestRecord;
        }
        return Math.Min(free, Capacity - Count(rom));
    }

    // a new monster: a copy of template's record (name, stats, picture, drops) and of its AI
    // script (jumps adjusted). returns its id
    public static int Add(RomBuffer rom, int template)
    {
        Move(rom);
        int newId = Count(rom);
        if (newId >= Capacity) throw new InvalidOperationException($"The ROM already has {Capacity} monsters.");
        int templateRecord = RecordOffset(rom, template);
        Place(rom, newId, rom.ReadBytes(templateRecord, RecordLength(rom, templateRecord)), templateRecord, isNew: true);
        return newId;
    }

    // takes out monster id when it's the last one and a new one: its id is free again, and
    // so are the bank 0 bytes of its record and script (only live records count as used). false when it isn't
    public static bool RemoveLast(RomBuffer rom, int id)
    {
        int block = TableBlock(rom);
        if (block == 0 || id < OriginalCount || id != Count(rom) - 1) return false;
        AiFarScripts.Remove(rom, id);
        rom.WriteUInt16Le(block + Header + id * 2, 0);
        rom.WriteByte(block + 4, (byte)id);
        rom.FixChecksum();
        return true;
    }

    // writes a monster's whole record (fixed part and variable tail). in place when it isn't longer than the old one,
    // otherwise the record moves to bank 0's free space (the offset table moves first) with its own copy of the AI
    public static void SetRecord(RomBuffer rom, int id, byte[] record)
    {
        int oldRecord = RecordOffset(rom, id);
        if (record.Length <= RecordLength(rom, oldRecord))
        {
            // (the script offset at +22 stays, the script itself doesn't move)
            rom.WriteBytes(oldRecord, record);
            rom.FixChecksum();
            return;
        }
        Move(rom);
        oldRecord = RecordOffset(rom, id);
        Place(rom, id, record, oldRecord, isNew: false);
    }

    // puts record in free bank 0 space as monster id, with a copy of
    // scriptFrom's AI script whose jumps (counted from the record) get moved with it
    private static void Place(RomBuffer rom, int id, byte[] record, int scriptFrom, bool isNew)
    {
        // a script in Lufia Forge's script space stays there (keyed by monster id), a new monster gets a copy of it
        int fromId = MonsterAi.IdOf(rom, scriptFrom);
        bool far = MonsterAi.HomeOf(rom, scriptFrom).Far;
        var (scriptStart, scriptLength) = far ? (0, 0) : MonsterAi.ScriptRange(rom, scriptFrom);
        var commands = MonsterAi.Decode(rom, scriptFrom);
        int oldAt = scriptLength > 0 ? MonsterAi.At(scriptFrom, scriptStart) : 0;
        var script = scriptLength > 0 ? rom.ReadBytes(oldAt, scriptLength) : Array.Empty<byte>();
        int recordAt = FindSpace(rom, record.Length, RecordAreas, skipScriptOf: isNew ? -1 : id);
        if (recordAt < 0) throw new InvalidOperationException("No room left in bank 0 for this monster record.");
        int block = TableBlock(rom);
        int oldPointer = rom.ReadUInt16Le(block + Header + id * 2);
        int count = Count(rom);
        // reserve the record before looking for script space
        rom.WriteBytes(recordAt, record);
        rom.WriteUInt16Le(block + Header + id * 2, (ushort)(recordAt - OriginalTable));
        if (isNew) rom.WriteByte(block + 4, (byte)(count + 1));
        if (scriptLength > 0)
        {
            int scriptAt = FindSpace(rom, scriptLength, ScriptAreas, skipScriptOf: id);
            if (scriptAt < 0 && AiFarScripts.CanInstall(rom))
            {
                // bank 0 is full, the copy goes to Lufia Forge's script space
                AiFarScripts.Save(rom, id, recordAt, AiScript.Build(AiScript.Read(rom, scriptFrom), 1));
                rom.FixChecksum();
                return;
            }
            if (scriptAt < 0)
            {
                if (isNew) rom.WriteByte(block + 4, (byte)count);
                else rom.WriteUInt16Le(block + Header + id * 2, (ushort)oldPointer);
                throw new InvalidOperationException("No room left in bank 0 for the monster's AI script.");
            }
            rom.WriteBytes(scriptAt, script);
            // offsets count from the record, so shift every jump by how much (script - record) changed
            int delta = (scriptAt - recordAt) - (oldAt - scriptFrom);
            foreach (var command in commands)
            {
                if (command.Jump is not int jump) continue;
                int jumpWordAt = scriptAt + (MonsterAi.At(scriptFrom, command.Offset) - oldAt) + 1 + MonsterAi.JumpArg(command.Op);
                rom.WriteUInt16Le(jumpWordAt, (ushort)((jump + delta) & 0xFFFF));
            }
            rom.WriteUInt16Le(recordAt + 0x22, (ushort)((scriptStart + delta) & 0xFFFF));
        }
        else rom.WriteUInt16Le(recordAt + 0x22, 0);
        if (far && isNew && fromId >= 0) AiFarScripts.Copy(rom, fromId, id, recordAt);
        rom.FixChecksum();
    }

    // magic elements: the record's second masked group (bit n = element n + 1, one multiplier byte per set bit)

    // elements the battle code reads per target ($08:BFFD, 7 entries): 1 Fire .. 5 Blast, plus 6 Light and
    // 7 Dark, which the game never uses but handles the same way
    public const int ElementCount = 7;

    // magic damage multipliers in 16ths for elements 1-7 (0 = normal damage)
    public static int[] ReadElements(RomBuffer rom, int record)
    {
        int position = record + FixedSize;
        int mask1 = rom.ReadByte(position); position += 1 + System.Numerics.BitOperations.PopCount((uint)mask1);
        int mask2 = rom.ReadByte(position++);
        var multipliers = new int[ElementCount];
        for (int element = 0; element < ElementCount; element++)
            if ((mask2 >> element & 1) != 0) multipliers[element] = rom.ReadByte(position++);
        return multipliers;
    }

    // the record with new magic multipliers (the length changes with the number of elements set)
    public static byte[] WithElements(RomBuffer rom, int record, IReadOnlyList<int> mult)
    {
        int length = RecordLength(rom, record);
        var recordBytes = rom.ReadBytes(record, length);
        int position = FixedSize;
        int mask1 = recordBytes[position]; position += 1 + System.Numerics.BitOperations.PopCount((uint)mask1);
        int mask2 = recordBytes[position];
        int oldGroup = 1 + System.Numerics.BitOperations.PopCount((uint)(mask2 & 0x7F));
        var group = new List<byte> { (byte)(mask2 & 0x80) };
        for (int element = 0; element < ElementCount; element++)
            if (mult[element] > 0) { group[0] |= (byte)(1 << element); group.Add((byte)Math.Clamp(mult[element], 1, 255)); }
        return recordBytes.Take(position).Concat(group).Concat(recordBytes.Skip(position + oldGroup)).ToArray();
    }

    // pictures

    private static int PictureBlock(RomBuffer rom)
    {
        if (rom.ReadByte(PictureAt) != 0x22) return 0;
        int code = FileOffset(Long(rom, PictureAt + 1));
        int block = code - Header - PictureCapacity * 2;
        return block > 0 && rom.ReadBytes(block, 4).SequenceEqual(PictureMagic) ? block : 0;
    }

    // how many monster pictures (graphic numbers) the rom has (0x6C in the game)
    public static int PictureCount(RomBuffer rom) { int block = PictureBlock(rom); return block > 0 ? rom.ReadUInt16Le(block + 4) : OriginalPictures; }

    // the resource holding a graphic number's picture
    public static int PictureResource(RomBuffer rom, int graphic)
    {
        if (graphic < OriginalPictures) return MonsterGraphics.FirstResource + graphic;
        int block = PictureBlock(rom);
        return block > 0 ? rom.ReadUInt16Le(block + Header + graphic * 2) : MonsterGraphics.FirstResource + graphic;
    }

    // a new picture (graphic number) as a copy of template's, under a new resource id
    public static int AddPicture(RomBuffer rom, int template)
    {
        HookPictures(rom);
        int block = PictureBlock(rom), newGraphic = PictureCount(rom);
        if (newGraphic >= PictureCapacity - 1) throw new InvalidOperationException("No graphic numbers left.");
        var data = LufiaCompression.DecompressResource(rom, PictureResource(rom, template), out _);
        int resource = ResourceIds.Add(rom, data);
        block = PictureBlock(rom);
        rom.WriteUInt16Le(block + Header + newGraphic * 2, (ushort)resource);
        rom.WriteUInt16Le(block + 4, (ushort)(newGraphic + 1));
        rom.FixChecksum();
        return newGraphic;
    }

    // takes out picture graphic when it's the last one and a new one (with its resource id
    // when that's the last too). the caller checks that no monster shows it. false when it isn't the last
    public static bool RemoveLastPicture(RomBuffer rom, int graphic)
    {
        int block = PictureBlock(rom);
        if (block == 0 || graphic < OriginalPictures || graphic != PictureCount(rom) - 1) return false;
        ResourceIds.RemoveLast(rom, PictureResource(rom, graphic));   // (when a later id is in use, this one stays as it is)
        rom.WriteUInt16Le(block + Header + graphic * 2, 0);
        rom.WriteUInt16Le(block + 4, (ushort)graphic);
        rom.FixChecksum();
        return true;
    }

    private static void HookPictures(RomBuffer rom)
    {
        if (PictureBlock(rom) > 0) return;
        if (!rom.ReadBytes(PictureAt, PictureOriginal.Length).SequenceEqual(PictureOriginal))
            throw new InvalidOperationException("The game's monster picture loader isn't the original, so it can't be changed safely.");
        if (rom.Length < Maps.MapWriter.ExpandedSize) rom.Expand(Maps.MapWriter.ExpandedSize);
        var freeSpace = Maps.ExpansionSpace.Load(rom);
        int size = Header + PictureCapacity * 2 + 32;
        int blockOffset = freeSpace.AllocateInOneBank(size, Maps.ExpansionSpace.Kind.Patch, 16);
        freeSpace.Save();
        var block = new byte[size];
        PictureMagic.CopyTo(block, 0);
        block[4] = OriginalPictures;
        for (int g = 0; g < OriginalPictures; g++) { block[Header + g * 2] = (byte)(MonsterGraphics.FirstResource + g); block[Header + g * 2 + 1] = 0; }
        int table = SnesAddress(blockOffset + Header);
        // entered with M = 16, A = graphic number, leaves the resource id in $16
        var code = new byte[]
        {
            0xDA, 0x0A, 0xAA,                                          // PHX; ASL; TAX
            0xBF, (byte)table, (byte)(table >> 8), (byte)(table >> 16),  // LDA table,X
            0x85, 0x16, 0xFA, 0x6B,                                    // STA $16; PLX; RTL
        };
        code.CopyTo(block, Header + PictureCapacity * 2);
        rom.WriteBytes(blockOffset, block);
        int codeAddress = SnesAddress(blockOffset + Header + PictureCapacity * 2);
        rom.WriteBytes(PictureAt, new byte[] { 0x22, (byte)codeAddress, (byte)(codeAddress >> 8), (byte)(codeAddress >> 16), 0xEA, 0xEA });   // JSL; NOP NOP
        rom.FixChecksum();
    }
}
