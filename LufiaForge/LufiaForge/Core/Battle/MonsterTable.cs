namespace LufiaForge.Core.Battle;

/// <summary>
/// The monster list and new monsters / monster pictures.
/// <para>Monster records: u16 offsets from $00:D800 (file 0x5800), read only by $01:F67D (Y = offset), whose callers
/// (the battle loader at $01:F570, the AI at $08:CAA5) read the record as $D800+Y / $D800+X with data bank 00 - so a
/// record must be in bank 00 at $D800 or above. The game's 165 records fill $D94A-$FD64. New monsters: the offset
/// table moves to the added space (block "LFMT", room for 255 ids; $01:F67D jumps to a copy that reads it), which frees
/// its old place ($D800-$D949) for new records; the end of bank 0 ($FDD0-$FFAF) is the other spot. AI scripts are
/// found as (record + offset) & FFFF, so they may sit anywhere in bank 00 - new monsters' scripts go to the unused
/// space before the table ($D2B0-$D7FF) first.</para>
/// <para>Pictures: resource 0x43 + graphic (record +14), computed at $08:D957. New pictures get new resource ids; a
/// hook there looks graphics 6C and up up in a table (block "LFMP").</para>
/// </summary>
public static class MonsterTable
{
    public const int OriginalTable = 0x5800, OriginalCount = 165, Capacity = 255;
    /// <summary>Fixed part of a record; the game's records are 39-48 bytes (see <see cref="RecordLength"/>).</summary>
    public const int FixedSize = 0x24, LargestRecord = FixedSize + 3 + 8 + 7 + 0x10;

    /// <summary>
    /// Length of a monster record, read the way the battle loader ($01:F59F-F638) reads it: 0x24 fixed bytes, a mask
    /// byte and one byte per set bit (8), a mask byte and one byte per set bit of its low 7, a count byte and that many
    /// bytes. (A script, when there is one, starts right after.)
    /// </summary>
    public static int RecordLength(RomBuffer rom, int record)
    {
        int p = record + FixedSize;
        int m1 = rom.ReadByte(p); p += 1 + System.Numerics.BitOperations.PopCount((uint)m1);
        int m2 = rom.ReadByte(p); p += 1 + System.Numerics.BitOperations.PopCount((uint)(m2 & 0x7F));
        int c = rom.ReadByte(p); p += 1 + c;
        return p - record;
    }
    public const int OriginalPictures = 0x6C, PictureCapacity = 0x100;
    private const int LookupAt = 0xF67D;                 // $01:F67D: PHP SEP #$20 XBA LDA #0 XBA REP #$30 ASL TAY LDA $D800,Y TAY SEP #$20 PLP RTL
    private static readonly byte[] LookupOriginal = Convert.FromHexString("08E220EBA900EBC2300AA8B900D8A8E22028 6B".Replace(" ", ""));
    private const int PictureAt = 0x45959;               // $08:D959: CLC ADC #$0043 STA $16
    private static readonly byte[] PictureOriginal = Convert.FromHexString("1869430085 16".Replace(" ", ""));
    private static readonly byte[] TableMagic = "LFMT"u8.ToArray(), PictureMagic = "LFMP"u8.ToArray();
    private const int Header = 8;

    /// <summary>Bank 00 places for new records (file offsets; they must be at $D800 or above).</summary>
    public static readonly (int Start, int End)[] RecordAreas = { (0x5802, 0x594A), (MonsterAi.FreeStart, MonsterAi.FreeEnd) };
    /// <summary>Bank 00 places for scripts (anywhere in bank 00; the space before the table first).</summary>
    public static readonly (int Start, int End)[] ScriptAreas = { (0x52B0, 0x5800), (0x5802, 0x594A), (MonsterAi.FreeStart, MonsterAi.FreeEnd) };

    private static int FileOffset(int snes) => (snes >> 16 & 0x7F) * 0x8000 + (snes & 0x7FFF);
    private static int SnesAddress(int file) => (file / 0x8000 | 0x80) << 16 | (file % 0x8000 | 0x8000);
    private static int Long(RomBuffer rom, int at) => rom.ReadByte(at) | rom.ReadByte(at + 1) << 8 | rom.ReadByte(at + 2) << 16;

    // ── the record table ──────────────────────────────────────────────────

    private static int TableBlock(RomBuffer rom)
    {
        if (rom.ReadByte(LookupAt) != 0x5C) return 0;
        int code = FileOffset(Long(rom, LookupAt + 1));
        int b = code - Header - Capacity * 2;
        return b > 0 && rom.ReadBytes(b, 4).SequenceEqual(TableMagic) ? b : 0;
    }
    public static bool IsMoved(RomBuffer rom) => TableBlock(rom) > 0;
    private static int TableOffset(RomBuffer rom) { int b = TableBlock(rom); return b > 0 ? b + Header : OriginalTable; }

    /// <summary>How many monsters the ROM has (165 in the game).</summary>
    public static int Count(RomBuffer rom) { int b = TableBlock(rom); return b > 0 ? rom.ReadByte(b + 4) : OriginalCount; }

    /// <summary>File offset of a monster's record.</summary>
    public static int RecordOffset(RomBuffer rom, int id) => OriginalTable + rom.ReadUInt16Le(TableOffset(rom) + id * 2);

    /// <summary>Moves the offset table to the added space (the game reads it through a copy of $01:F67D).</summary>
    public static void Move(RomBuffer rom)
    {
        if (IsMoved(rom)) return;
        if (!rom.ReadBytes(LookupAt, LookupOriginal.Length).SequenceEqual(LookupOriginal))
            throw new InvalidOperationException("The game's monster lookup isn't the original, so its table can't be moved safely.");
        if (rom.Length < Maps.MapWriter.ExpandedSize) rom.Expand(Maps.MapWriter.ExpandedSize);
        var space = Maps.ExpansionSpace.Load(rom);
        int size = Header + Capacity * 2 + 32;
        int at = space.AllocateInOneBank(size, Maps.ExpansionSpace.Kind.Patch, 15);
        space.Save();
        var block = new byte[size];
        TableMagic.CopyTo(block, 0);
        block[4] = OriginalCount;
        rom.ReadBytes(OriginalTable, OriginalCount * 2).CopyTo(block, Header);
        int table = SnesAddress(at + Header);
        var code = new byte[]
        {
            0x08, 0xE2, 0x20, 0xEB, 0xA9, 0x00, 0xEB, 0xC2, 0x30,     // PHP; SEP #$20; XBA; LDA #0; XBA; REP #$30
            0xDA, 0x0A, 0xAA,                                         // PHX; ASL; TAX
            0xBF, (byte)table, (byte)(table >> 8), (byte)(table >> 16), // LDA table,X
            0xA8, 0xFA, 0xE2, 0x20, 0x28, 0x6B,                       // TAY; PLX; SEP #$20; PLP; RTL
        };
        code.CopyTo(block, Header + Capacity * 2);
        rom.WriteBytes(at, block);
        int c = SnesAddress(at + Header + Capacity * 2);
        rom.WriteBytes(LookupAt, new byte[] { 0x5C, (byte)c, (byte)(c >> 8), (byte)(c >> 16) });   // JML
        // the old table's place becomes free (cleared so nothing reads stale offsets)
        rom.WriteBytes(OriginalTable, Enumerable.Repeat((byte)0xFF, OriginalCount * 2).ToArray());
        rom.FixChecksum();
    }

    /// <summary>Bank 00 bytes in use by records and scripts (file ranges), optionally leaving one monster's script out.</summary>
    /// <summary>Whether another monster's record or script uses any of these bytes (a script shared by several monsters).</summary>
    public static bool Overlaps(RomBuffer rom, int start, int length, int skipScriptOf) =>
        Used(rom, skipScriptOf).Any(u => u.Start < start + length && start < u.End);

    private static List<(int Start, int End)> Used(RomBuffer rom, int skipScriptOf = -1)
    {
        var used = new List<(int, int)>();
        // the game's offset table is only free once it has moved to the added space (a new monster moves it)
        if (!IsMoved(rom)) used.Add((OriginalTable, OriginalTable + OriginalCount * 2));
        for (int m = 0, n = Count(rom); m < n; m++)
        {
            int r = RecordOffset(rom, m);
            used.Add((r, r + RecordLength(rom, r)));
            if (m == skipScriptOf) continue;
            // (a script in Lufia Forge's script space takes no room in bank 0)
            if (MonsterAi.HomeOf(rom, r, m).Far) continue;
            var (s, l) = MonsterAi.ScriptRange(rom, r);
            if (l > 0) { int a = MonsterAi.At(r, s); used.Add((a, a + l)); }
        }
        // bytes another hack put in these areas (they differ from the clean game in the ROM the project started from)
        foreach (var (start, end) in ScriptAreas) used.AddRange(RomIdentity.ForeignRanges(rom, start, end));
        return used;
    }

    /// <summary>A free spot of <paramref name="len"/> bytes in one of <paramref name="areas"/>, or -1.</summary>
    public static int FindSpace(RomBuffer rom, int len, (int Start, int End)[] areas, int skipScriptOf = -1) =>
        FindSpace(Used(rom, skipScriptOf), len, areas);

    /// <summary>Bank 00 bytes in use, for several FindSpace calls in a row.</summary>
    public static List<(int Start, int End)> UsedSpace(RomBuffer rom, int skipScriptOf = -1) => Used(rom, skipScriptOf);

    /// <summary>A free spot of <paramref name="len"/> bytes in one of <paramref name="areas"/>, given the bytes in use.</summary>
    public static int FindSpace(List<(int Start, int End)> used, int len, (int Start, int End)[] areas)
    {
        foreach (var (start, end) in areas)
            for (int p = start; p + len <= end; p++)
            {
                int q = p;
                var hit = used.Where(u => u.Start < q + len && u.End > q).Select(u => u.End).DefaultIfEmpty(-1).Max();
                if (hit < 0) return p;
                p = hit - 1;
            }
        return -1;
    }

    /// <summary>Room for how many more monsters (records only; their scripts need room too).</summary>
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

    /// <summary>
    /// A new monster: a copy of <paramref name="template"/>'s record (name, stats, picture, drops) and of its AI
    /// script (jumps adjusted). Returns its id.
    /// </summary>
    public static int Add(RomBuffer rom, int template)
    {
        Move(rom);
        int n = Count(rom);
        if (n >= Capacity) throw new InvalidOperationException($"The ROM already has {Capacity} monsters.");
        int src = RecordOffset(rom, template);
        Place(rom, n, rom.ReadBytes(src, RecordLength(rom, src)), src, isNew: true);
        return n;
    }

    /// <summary>
    /// Writes a monster's whole record (fixed part and variable tail). In place when it isn't longer than the old one;
    /// otherwise the record moves to bank 0's free space (the offset table moves first) with its own copy of the AI.
    /// </summary>
    public static void SetRecord(RomBuffer rom, int id, byte[] record)
    {
        int old = RecordOffset(rom, id);
        if (record.Length <= RecordLength(rom, old))
        {
            // (the script offset at +22 is kept: the script itself doesn't move)
            rom.WriteBytes(old, record);
            rom.FixChecksum();
            return;
        }
        Move(rom);
        old = RecordOffset(rom, id);
        Place(rom, id, record, old, isNew: false);
    }

    /// <summary>Puts <paramref name="record"/> in free bank 0 space as monster <paramref name="id"/>, with a copy of
    /// <paramref name="scriptFrom"/>'s AI script whose jumps (counted from the record) are moved with it.</summary>
    private static void Place(RomBuffer rom, int id, byte[] record, int scriptFrom, bool isNew)
    {
        // a script in Lufia Forge's script space stays there (keyed by monster id): a new monster gets a copy of it
        int fromId = MonsterAi.IdOf(rom, scriptFrom);
        bool far = MonsterAi.HomeOf(rom, scriptFrom).Far;
        var (sStart, sLen) = far ? (0, 0) : MonsterAi.ScriptRange(rom, scriptFrom);
        var cmds = MonsterAi.Decode(rom, scriptFrom);
        int oldAt = sLen > 0 ? MonsterAi.At(scriptFrom, sStart) : 0;
        var script = sLen > 0 ? rom.ReadBytes(oldAt, sLen) : Array.Empty<byte>();
        int rec = FindSpace(rom, record.Length, RecordAreas, skipScriptOf: isNew ? -1 : id);
        if (rec < 0) throw new InvalidOperationException("No room left in bank 0 for this monster record.");
        int b = TableBlock(rom);
        int oldPointer = rom.ReadUInt16Le(b + Header + id * 2);
        int n = Count(rom);
        // reserve the record before looking for script space
        rom.WriteBytes(rec, record);
        rom.WriteUInt16Le(b + Header + id * 2, (ushort)(rec - OriginalTable));
        if (isNew) rom.WriteByte(b + 4, (byte)(n + 1));
        if (sLen > 0)
        {
            int scr = FindSpace(rom, sLen, ScriptAreas, skipScriptOf: id);
            if (scr < 0 && AiFarScripts.CanInstall(rom))
            {
                // bank 0 is full: the copy goes to Lufia Forge's script space
                AiFarScripts.Save(rom, id, rec, AiScript.Build(AiScript.Read(rom, scriptFrom), 1));
                rom.FixChecksum();
                return;
            }
            if (scr < 0)
            {
                if (isNew) rom.WriteByte(b + 4, (byte)n);
                else rom.WriteUInt16Le(b + Header + id * 2, (ushort)oldPointer);
                throw new InvalidOperationException("No room left in bank 0 for the monster's AI script.");
            }
            rom.WriteBytes(scr, script);
            // offsets count from the record: shift every jump by the change of (script - record)
            int delta = (scr - rec) - (oldAt - scriptFrom);
            foreach (var c in cmds)
            {
                if (c.Jump is not int j) continue;
                int at = scr + (MonsterAi.At(scriptFrom, c.Offset) - oldAt) + 1 + MonsterAi.JumpArg(c.Op);
                rom.WriteUInt16Le(at, (ushort)((j + delta) & 0xFFFF));
            }
            rom.WriteUInt16Le(rec + 0x22, (ushort)((sStart + delta) & 0xFFFF));
        }
        else rom.WriteUInt16Le(rec + 0x22, 0);
        if (far && isNew && fromId >= 0) AiFarScripts.Copy(rom, fromId, id, rec);
        rom.FixChecksum();
    }

    // ── magic elements: the record's second masked group (bit n = element n + 1, one multiplier byte per set bit) ──

    /// <summary>Elements the battle code reads per target ($08:BFFD, 7 entries): 1 Fire .. 5 Blast, and 6 Light,
    /// 7 Dark, which the game never uses but handles the same way.</summary>
    public const int ElementCount = 7;

    /// <summary>Magic damage multipliers in 16ths for elements 1-7 (0 = normal damage).</summary>
    public static int[] ReadElements(RomBuffer rom, int record)
    {
        int p = record + FixedSize;
        int m1 = rom.ReadByte(p); p += 1 + System.Numerics.BitOperations.PopCount((uint)m1);
        int m2 = rom.ReadByte(p++);
        var v = new int[ElementCount];
        for (int e = 0; e < ElementCount; e++)
            if ((m2 >> e & 1) != 0) v[e] = rom.ReadByte(p++);
        return v;
    }

    /// <summary>The record with new magic multipliers (the length changes with the number of elements set).</summary>
    public static byte[] WithElements(RomBuffer rom, int record, IReadOnlyList<int> mult)
    {
        int len = RecordLength(rom, record);
        var r = rom.ReadBytes(record, len);
        int p = FixedSize;
        int m1 = r[p]; p += 1 + System.Numerics.BitOperations.PopCount((uint)m1);
        int m2 = r[p];
        int oldGroup = 1 + System.Numerics.BitOperations.PopCount((uint)(m2 & 0x7F));
        var group = new List<byte> { (byte)(m2 & 0x80) };
        for (int e = 0; e < ElementCount; e++)
            if (mult[e] > 0) { group[0] |= (byte)(1 << e); group.Add((byte)Math.Clamp(mult[e], 1, 255)); }
        return r.Take(p).Concat(group).Concat(r.Skip(p + oldGroup)).ToArray();
    }

    // ── pictures ──────────────────────────────────────────────────────────

    private static int PictureBlock(RomBuffer rom)
    {
        if (rom.ReadByte(PictureAt) != 0x22) return 0;
        int code = FileOffset(Long(rom, PictureAt + 1));
        int b = code - Header - PictureCapacity * 2;
        return b > 0 && rom.ReadBytes(b, 4).SequenceEqual(PictureMagic) ? b : 0;
    }

    /// <summary>How many monster pictures (graphic numbers) the ROM has (0x6C in the game).</summary>
    public static int PictureCount(RomBuffer rom) { int b = PictureBlock(rom); return b > 0 ? rom.ReadUInt16Le(b + 4) : OriginalPictures; }

    /// <summary>The resource holding a graphic number's picture.</summary>
    public static int PictureResource(RomBuffer rom, int graphic)
    {
        if (graphic < OriginalPictures) return MonsterGraphics.FirstResource + graphic;
        int b = PictureBlock(rom);
        return b > 0 ? rom.ReadUInt16Le(b + Header + graphic * 2) : MonsterGraphics.FirstResource + graphic;
    }

    /// <summary>A new picture (graphic number) as a copy of <paramref name="template"/>'s, under a new resource id.</summary>
    public static int AddPicture(RomBuffer rom, int template)
    {
        HookPictures(rom);
        int b = PictureBlock(rom), n = PictureCount(rom);
        if (n >= PictureCapacity - 1) throw new InvalidOperationException("No graphic numbers left.");
        var data = LufiaCompression.DecompressResource(rom, PictureResource(rom, template), out _);
        int res = ResourceIds.Add(rom, data);
        b = PictureBlock(rom);
        rom.WriteUInt16Le(b + Header + n * 2, (ushort)res);
        rom.WriteUInt16Le(b + 4, (ushort)(n + 1));
        rom.FixChecksum();
        return n;
    }

    private static void HookPictures(RomBuffer rom)
    {
        if (PictureBlock(rom) > 0) return;
        if (!rom.ReadBytes(PictureAt, PictureOriginal.Length).SequenceEqual(PictureOriginal))
            throw new InvalidOperationException("The game's monster picture loader isn't the original, so it can't be changed safely.");
        if (rom.Length < Maps.MapWriter.ExpandedSize) rom.Expand(Maps.MapWriter.ExpandedSize);
        var space = Maps.ExpansionSpace.Load(rom);
        int size = Header + PictureCapacity * 2 + 32;
        int at = space.AllocateInOneBank(size, Maps.ExpansionSpace.Kind.Patch, 16);
        space.Save();
        var block = new byte[size];
        PictureMagic.CopyTo(block, 0);
        block[4] = OriginalPictures;
        for (int g = 0; g < OriginalPictures; g++) { block[Header + g * 2] = (byte)(MonsterGraphics.FirstResource + g); block[Header + g * 2 + 1] = 0; }
        int table = SnesAddress(at + Header);
        // entered with M = 16, A = graphic number; leaves the resource id in $16
        var code = new byte[]
        {
            0xDA, 0x0A, 0xAA,                                          // PHX; ASL; TAX
            0xBF, (byte)table, (byte)(table >> 8), (byte)(table >> 16),  // LDA table,X
            0x85, 0x16, 0xFA, 0x6B,                                    // STA $16; PLX; RTL
        };
        code.CopyTo(block, Header + PictureCapacity * 2);
        rom.WriteBytes(at, block);
        int c = SnesAddress(at + Header + PictureCapacity * 2);
        rom.WriteBytes(PictureAt, new byte[] { 0x22, (byte)c, (byte)(c >> 8), (byte)(c >> 16), 0xEA, 0xEA });   // JSL; NOP NOP
        rom.FixChecksum();
    }
}
