namespace LufiaForge.Core.Lufia2;

// Lufia II: Rise of the Sinistrals (USA), 2.5 MB LoROM. addresses from LEdit (pdinklag, researched on the German rom)
// checked on the US rom (CRC of the clean dump: checksum $BA68).
//
// files: graphics, maps and music sit in 0x2A9 packed "files" reached through a table at $A7:8000 (file 0x138000),
// 3 bytes each, the file is at 0x138000 + that value. they're packed the same way as Lufia 1's resources
// (LufiaCompression), so we read and write them with the same code
public static class L2Rom
{
    // the file table's base: an entry is the file's offset from here (the game's table sits here too until New map
    // moves it, FileTableAt). FileCount is the game's files
    public const int FileTable = 0x138000, FileCount = 0x2A8;

    // file offset of the file table the game reads right now (its own, or the copy New map makes with room for the
    // new maps' files)
    public static int FileTableAt(RomBuffer rom) => L2NewMaps.Table(rom, L2MapTable.Files);

    // every file there is: the game's and the new maps'
    public static IEnumerable<int> FileIds(RomBuffer rom) => Enumerable.Range(0, FileCount).Concat(L2NewMaps.NewFiles(rom));

    // points file id at file offset at
    public static void SetFileOffset(RomBuffer rom, int id, int at)
    {
        int relative = at - FileTable;
        rom.WriteBytes(FileTableAt(rom) + id * 3, new[] { (byte)relative, (byte)(relative >> 8), (byte)(relative >> 16) });
    }

    // a snes address as a file offset. banks $80-$FF read the first 4 MB, banks $01-$7D (upper halves) read the upper
    // 4 MB of an 8 MB rom (ExpandTo8), and bank $00 is the copy of the first bank
    public static int SnesToFile(int snes)
    {
        int bank = snes >> 16 & 0xFF;
        return bank is >= 1 and < 0x7E && (snes & 0x8000) != 0 ? UpperStart + bank * 0x8000 + (snes & 0x7FFF) : (bank & 0x7F) * 0x8000 + (snes & 0x7FFF);
    }

    public static int FileToSnes(int file) =>
        file >= UpperStart ? ((file - UpperStart) / 0x8000) << 16 | (file % 0x8000 | 0x8000) : (file / 0x8000 | 0x80) << 16 | (file % 0x8000 | 0x8000);

    // file offset of the byte offset on from file offset file, the way the game adds a record's u16 offsets: a 16-bit
    // sum in the same bank ($0A42 + Y, INC $BB, the shop pointer...), so an offset past the bank's end wraps round to
    // its start (the fan hacks' monster records in bank $D0 use offsets like $FF79, which is 0x87 bytes back).
    // banks $C0-$FF show the rom at $0000-$7FFF as well, in banks $80-$BF that's RAM, so -1
    public static int InBank(int file, int offset)
    {
        int snesAddress = FileToSnes(file), bank = snesAddress >> 16, address = (snesAddress + offset) & 0xFFFF;
        if (address < 0x8000 && bank < 0xC0) return -1;
        return SnesToFile(bank << 16 | address);
    }

    // the u16 the game adds to from to get to to (same bank)
    public static int OffsetInBank(int from, int to) => (FileToSnes(to) - FileToSnes(from)) & 0xFFFF;

    // file offset of packed file id
    public static int FileOffset(RomBuffer rom, int id) => FileTable + (int)rom.ReadUInt24Le(FileTableAt(rom) + id * 3);

    // packed file id, unpacked
    public static byte[] ReadFile(RomBuffer rom, int id) => LufiaCompression.Decompress(rom, FileOffset(rom, id), out _);

    // the space we add after the game's 2.5 MB. the rom grows to 3 MB the first time something doesn't fit where it
    // was, and to 4 MB (the most a LoROM cartridge addresses, the header already says 4 MB) once that's full.
    // we use the largest empty run (all 00 or all FF) past 2.5 MB, so other hacks' data in that part of the rom (Frue,
    // Spekkio and Kureji Lufia use 0x280000-0x30DAC2 and 0x3FFC00-) is left alone. the last 16 bytes of the run hold
    // "LF2X" and the next free offset (u24). on a rom we grew ourselves that's at 0x2FFFF0 (SpaceMark)
    public const int OriginalSize = 0x280000, ExpandedSize = 0x300000, MaxSize = 0x400000, SpaceMark = ExpandedSize - 16;

    // 8 MB: past 4 MB a LoROM cartridge can be wired as "ExLoROM". banks $80-$FF read the first 4 MB like before and
    // banks $00-$7D (addresses $8000-$FFFF) the upper 4 MB. the game runs in $80-$FF so it's unchanged, only the
    // console's start and its interrupts go through bank $00, which holds a copy of the first bank
    // (RomBuffer.IsExLoRomCopy). measured in BizHawk (2026-10-27): the game reads nothing else from banks $00-$7D,
    // except for the battle window frame (2026-11-01, FixLowBankReads). banks $01-$3F also see the RAM at $0000-$1FFF,
    // like $80-$BF, so event scripts can live there (L2EventSpace), and other data goes in banks of its own among them
    // (Allocate with upper: true, DataBanks).
    // banks $40-$7D (file 0x600000-) are left alone: Snes9x up to 1.52 at least (1.51 still gets played) shows file
    // 0x400000-0x5FFFFF there a second time (its Map_JumboLoROMMap), so data lufia forge put there before 2026-11-14
    // read as the copy of bank $00, and a map whose info was there reset the game on entry (Artemis, map 78).
    // L2HighBanks moves it. works in Snes9x, bsnes / higan / BizHawk's "BSNESv115+" core and others that know
    // ExLoROM. BizHawk's older "BSNES" core can't run it
    public const int UpperStart = 0x400000, ExSize = RomBuffer.ExLoRomSize, UpperDataStart = 0x600000, UpperDataEnd = 0x7F0000,
        UpperMark = UpperDataEnd - 16;

    // Snes9x 1.51 mode (in our settings, on unless turned off): nothing gets written to the top 2 MB of an 8 MB rom.
    // with it off, Allocate with upper: true uses that first again
    public static bool Snes9x151Mode { get; set; } = true;

    // the top 2 MB of an 8 MB rom (file 0x600000 up), what Snes9x 1.51 can't read
    public static bool InTop2MB(int at) => at >= UpperDataStart;

    // the data banks' range (banks $01-$3F, shared with the event space) and their mark in the bank's last 16 bytes:
    // "LF2D" and the next free offset (u24), like the event space's "LF2V"
    private const int DataFirst = UpperStart + 0x8000, DataEnd = UpperStart + 0x200000, DataMark = 0x7FF0;

    // true if the rom is 8 MB (ExLoROM)
    public static bool IsExLoRom(RomBuffer rom) => rom.IsExLoRomCopy();

    // why the rom can't be made 8 MB, or null
    public static string? CantExpandTo8(RomBuffer rom)
    {
        if (IsExLoRom(rom)) return "The ROM is already 8 MB.";
        if (rom.Length > MaxSize) return "The ROM is bigger than 4 MB already (another hack's layout), so Lufia Forge leaves its size alone.";
        if (rom.ReadByte(0x7FD5) is var mapMode && (mapMode & 0x0F) != 0) return $"The ROM's header says map mode {mapMode:X2}, not a LoROM game.";
        return null;
    }

    // makes the rom 8 MB (ExLoROM): 4 MB first if it's smaller, then the upper 4 MB, empty except for the copy of the
    // first bank. the header says 8 MB
    public static void ExpandTo8(RomBuffer rom)
    {
        if (CantExpandTo8(rom) is { } why) throw new InvalidOperationException(why);
        if (rom.Length < MaxSize) rom.Expand(MaxSize);
        rom.Expand(ExSize, 0x00);
        rom.WriteBytes(UpperStart, rom.ReadBytes(0, 0x8000));
        FixLowBankReads(rom);
        rom.FixChecksum();
    }

    // the one place the game reads rom through banks $01-$7D (found 2026-11-01 from every long address in the
    // decompiled code, plus every CPU read and DMA source bank in a battle, logged in BizHawk): the battle window frame
    // helpers $81:E542 and $81:E570 read their pieces ($81:87B7-) with LDA $01:0000,X / $01:0002,X / $01:0004,X.
    // on a 4 MB rom that's bank $81, on 8 MB it's the upper 4 MB (empty, or the events put there), so the hero's status
    // window in battle lost its border or showed garbage. reading bank $81 is the same on every size (its $0000-$1FFF
    // is RAM just like bank $01)
    private static readonly int[] LowBankReads = { 0xE548, 0xE54F, 0xE55D, 0xE570, 0xE580 };

    // true if an 8 MB rom still has the battle window reads through bank $01 (FixLowBankReads)
    public static bool NeedsLowBankFix(RomBuffer rom) =>
        IsExLoRom(rom) && LowBankReads.Any(site => rom.ReadByte(site) == 0xBF && rom.ReadByte(site + 3) == 0x01);

    // points the battle window frame reads at bank $81 (see LowBankReads). returns true if anything changed
    public static bool FixLowBankReads(RomBuffer rom)
    {
        bool changed = false;
        foreach (int site in LowBankReads)
            if (rom.ReadByte(site) == 0xBF && rom.ReadByte(site + 3) == 0x01) { rom.WriteByte(site + 3, 0x81); changed = true; }
        return changed;
    }

    // bytes still free in the data banks made so far (more get made from the empty banks of $01-$3F, which the event
    // space takes too, L2EventSpace.UpperBanksLeft). 0 on a smaller rom
    public static int UpperFree(RomBuffer rom) => !IsExLoRom(rom) ? 0 :
        DataBanks(rom).Sum(bank => bank + DataMark - (int)rom.ReadUInt24Le(bank + DataMark + 4));

    // the data banks made so far (file offsets of their starts), highest first
    public static List<int> DataBanks(RomBuffer rom)
    {
        var list = new List<int>();
        if (!IsExLoRom(rom)) return list;
        for (int bank = DataEnd - 0x8000; bank >= DataFirst; bank -= 0x8000)
            if (IsDataBank(rom, bank)) list.Add(bank);
        return list;
    }

    private static bool IsDataBank(RomBuffer rom, int bank) =>
        bank + DataMark + 8 <= rom.Length && rom.ReadByte(bank + DataMark) == 'L' && rom.ReadByte(bank + DataMark + 1) == 'F' &&
        rom.ReadByte(bank + DataMark + 2) == '2' && rom.ReadByte(bank + DataMark + 3) == 'D' &&
        rom.ReadUInt24Le(bank + DataMark + 4) is var next && next >= bank && next <= bank + DataMark;

    private static void WriteDataMark(RomBuffer rom, int bank, int next) =>
        rom.WriteBytes(bank + DataMark, new[] { (byte)'L', (byte)'F', (byte)'2', (byte)'D', (byte)next, (byte)(next >> 8), (byte)(next >> 16), (byte)0 });

    // room for length bytes in a data bank of the upper 4 MB: one made before, otherwise the highest empty bank of
    // $01-$3F (the event space takes them from the lowest). -1 if there's none
    private static int AllocateData(RomBuffer rom, int length)
    {
        if (length > DataMark) return -1;
        foreach (int bank in DataBanks(rom))
        {
            int next = (int)rom.ReadUInt24Le(bank + DataMark + 4);
            if (next + length <= bank + DataMark) { WriteDataMark(rom, bank, next + length); return next; }
        }
        for (int bank = DataEnd - 0x8000; bank >= DataFirst; bank -= 0x8000)
            if (!IsDataBank(rom, bank) && L2EventSpace.IsEmptyBank(rom, bank))
            {
                rom.WriteBytes(bank, new byte[0x8000]);
                WriteDataMark(rom, bank, bank + length);
                return bank;
            }
        return -1;
    }

    // reserves length bytes of added space, never across a 32 KB bank (so the game reads them as one piece), growing
    // the rom if needed. returns the file offset. upper = the data is read through a full 24-bit address, so on an
    // 8 MB rom it goes in a data bank of the upper 4 MB first (DataBanks, never banks $40-$7D, which old Snes9x
    // versions can't read)
    public static int Allocate(RomBuffer rom, int length, bool upper = false)
    {
        if (length <= 0 || length > 0x8000) throw new ArgumentOutOfRangeException(nameof(length));
        if (upper && IsExLoRom(rom) && !Snes9x151Mode)
        {
            // Snes9x 1.51 mode off (app settings): the top 2 MB first, like before 2026-11-14
            int upperAt = IsMark(rom, UpperMark) ? (int)rom.ReadUInt24Le(UpperMark + 4) : UpperDataStart;
            if (upperAt / 0x8000 != (upperAt + length - 1) / 0x8000) upperAt = (upperAt / 0x8000 + 1) * 0x8000;
            if (upperAt + length <= UpperMark)
            {
                WriteMark(rom, UpperMark, upperAt + length);
                return upperAt;
            }
        }
        if (upper && IsExLoRom(rom) && AllocateData(rom, length) is var dataAt and >= 0) return dataAt;
        if (rom.Length < ExpandedSize) rom.Expand(ExpandedSize);
        var (mark, next) = FindMark(rom) ?? NewSpace(rom, OriginalSize) ?? throw new InvalidOperationException("The ROM has no empty space left.");
        while (true)
        {
            int at = next;
            if (at / 0x8000 != (at + length - 1) / 0x8000) at = (at / 0x8000 + 1) * 0x8000;
            if (at + length <= mark)
            {
                WriteMark(rom, mark, at + length);
                return at;
            }
            // this run is full: grow the rom if it can, and carry on in the next empty run
            if (rom.Length < MaxSize) rom.Expand(MaxSize);
            var more = NewSpace(rom, mark + 16) ?? throw new InvalidOperationException(IsExLoRom(rom)
                ? "The first 4 MB of the ROM are full (what goes there can't use the upper 4 MB)."
                : "The ROM is full at 4 MB. Lufia II > Settings > Make the ROM 8 MB gives it 4 MB more.");
            rom.WriteBytes(mark, Enumerable.Repeat((byte)0xFF, 16).ToArray());
            (mark, next) = more;
        }
    }

    // where Allocate hands out room, with the "LF2X" mark (16 bytes) and the next free offset it holds: the added
    // space of the first 4 MB (from 2.5 MB), on an 8 MB rom each data bank ("LF2D", its mark at the bank's end), and
    // the old run of banks $40-$7D while a rom still has its mark (UpperMark). merging projects reads these, both
    // projects' new data starts at the same place
    public static List<(int Mark, int Start, int Next)> Spaces(RomBuffer rom)
    {
        var list = new List<(int, int, int)>();
        if (FindMark(rom) is var (mark, next)) list.Add((mark, OriginalSize, next));
        if (IsExLoRom(rom) && IsMark(rom, UpperMark)) list.Add((UpperMark, UpperDataStart, (int)rom.ReadUInt24Le(UpperMark + 4)));
        foreach (int bank in DataBanks(rom)) list.Add((bank + DataMark, bank, (int)rom.ReadUInt24Le(bank + DataMark + 4)));
        return list;
    }

    private static void WriteMark(RomBuffer rom, int mark, int next) =>
        rom.WriteBytes(mark, new[] { (byte)'L', (byte)'F', (byte)'2', (byte)'X', (byte)next, (byte)(next >> 8), (byte)(next >> 16) });

    // the "LF2X" mark (16-byte aligned, past 2.5 MB) and the next free offset it holds, or null
    private static (int Mark, int Next)? FindMark(RomBuffer rom)
    {
        if (rom.Length >= ExpandedSize && IsMark(rom, SpaceMark)) return (SpaceMark, (int)rom.ReadUInt24Le(SpaceMark + 4));
        for (int p = OriginalSize; p + 16 <= Math.Min(rom.Length, MaxSize); p += 16)
            if (IsMark(rom, p)) return (p, (int)rom.ReadUInt24Le(p + 4));
        return null;
    }

    private static bool IsMark(RomBuffer rom, int at) =>
        rom.ReadByte(at) == 'L' && rom.ReadByte(at + 1) == 'F' && rom.ReadByte(at + 2) == '2' && rom.ReadByte(at + 3) == 'X' &&
        rom.ReadUInt24Le(at + 4) is var next && next >= OriginalSize && next <= at;

    // the largest empty run (one value, 00 or FF, at least 4 KB) from from on. a mark goes in its last 16 bytes and
    // the space starts 16 bytes into it (data may end in a 00 or FF)
    private static (int Mark, int Next)? NewSpace(RomBuffer rom, int from)
    {
        int bestStart = -1, bestLen = 0, end = Math.Min(rom.Length, MaxSize);
        int p = from;
        while (p < end)
        {
            byte fill = rom.ReadByte(p);
            if (fill is not (0x00 or 0xFF)) { p++; continue; }
            int runEnd = p;
            while (runEnd < end && rom.ReadByte(runEnd) == fill) runEnd++;
            if (runEnd - p > bestLen) { bestStart = p; bestLen = runEnd - p; }
            p = runEnd;
        }
        if (bestLen < 0x1000) return null;
        int start = (bestStart == from ? bestStart : bestStart + 16 + 15) & ~15, mark = (bestStart + bestLen - 16) & ~15;
        WriteMark(rom, mark, start);
        return (mark, start);
    }

    // bytes of added space still free (counting the growth to 4 MB)
    public static int FreeSpace(RomBuffer rom)
    {
        if (FindMark(rom) is not var (mark, next))
        {
            if (rom.Length < ExpandedSize) return MaxSize - OriginalSize;
            // grown (Expand, or a fan hack) but we haven't placed anything yet: the largest empty run is what we'd use
            int best = 0;
            for (int p = OriginalSize; p < Math.Min(rom.Length, MaxSize);)
            {
                byte fill = rom.ReadByte(p);
                if (fill is not (0x00 or 0xFF)) { p++; continue; }
                int runEnd = p;
                while (runEnd < Math.Min(rom.Length, MaxSize) && rom.ReadByte(runEnd) == fill) runEnd++;
                best = Math.Max(best, runEnd - p);
                p = runEnd;
            }
            return Math.Max(0, best - 48) + Math.Max(0, MaxSize - rom.Length);
        }
        return Math.Max(0, mark - next) + Math.Max(0, MaxSize - Math.Max(rom.Length, ExpandedSize));
    }

    // packs data and puts it back as file id: where it was if it fits there, otherwise in the added space (never
    // across a 32 KB bank, so the game's unpacker reads it as one piece), with the file table pointing at the new place
    public static void WriteFile(RomBuffer rom, int id, byte[] data) => WriteFiles(rom, (id, data));

    // writes files that change together (a tile set and its block set). each goes back where it is if it still fits.
    // files that grew and were the last ones put in the added space (one after another up to its free end) get
    // written again from where the first of them starts, so a file that keeps growing (tiles added one piece at a
    // time) doesn't leave a copy behind every time. any other file that grew goes to new room
    public static void WriteFiles(RomBuffer rom, params (int Id, byte[] Data)[] files) =>
        // all or nothing: once the space mark is set back, a file written in new room may cover a file not moved yet,
        // so failing partway (the rom full) mustn't leave the file table pointing at it
        AllOrNothing(rom, () => { WriteFilesCore(rom, files); return 0; });

    private static void WriteFilesCore(RomBuffer rom, (int Id, byte[] Data)[] files)
    {
        bool crosses(int offset, int length) => offset / 0x8000 != (offset + length - 1) / 0x8000;
        var grow = new List<(int Id, byte[] Packed, int At, int OldSize)>();
        foreach (var (id, data) in files)
        {
            byte[] packed = LufiaCompression.Compress(data);
            int at = FileOffset(rom, id);
            LufiaCompression.Decompress(rom, at, out int oldSize);
            if (packed.Length <= oldSize && !(at >= OriginalSize && crosses(at, packed.Length))) { rom.WriteBytes(at, packed); continue; }
            grow.Add((id, packed, at, oldSize));
        }
        if (grow.Count == 0) return;
        // the files at the end of the added space: walking back from its free end, each one ends where the next starts
        // (or in the bank before, if the next got moved to a bank start, nothing is ever put in that gap)
        if (FindMark(rom) is var (mark, next))
        {
            int end = next, start = -1;
            foreach (var file in grow.Where(f => f.At >= OriginalSize && f.At < mark).OrderByDescending(f => f.At))
            {
                int fileEnd = file.At + file.OldSize;
                // the gap before a bank start only counts while it's still untouched fill. something else put in after
                // the file, in the same bank, sits there, and writing the file again from its start would go over it
                bool gapFree() { int fill = rom.ReadByte(fileEnd); if (fill is not (0xFF or 0x00)) return false; for (int p = fileEnd; p < end; p++) if (rom.ReadByte(p) != fill) return false; return true; }
                bool touches = fileEnd == end || fileEnd < end && end % 0x8000 == 0 && (fileEnd - 1) / 0x8000 == end / 0x8000 - 1 && gapFree();
                if (!touches) break;
                start = end = file.At;
            }
            if (start >= 0) WriteMark(rom, mark, start);
        }
        foreach (var file in grow)
        {
            int to = Allocate(rom, file.Packed.Length);
            rom.WriteBytes(to, file.Packed);
            SetFileOffset(rom, file.Id, to);
        }
    }

    // packs data into new room in the added space as file id. never over the bytes the file table pointed at, since a
    // new map's file starts as a copy of another map's entry
    public static void WriteNewFile(RomBuffer rom, int id, byte[] data)
    {
        byte[] packed = LufiaCompression.Compress(data);
        int next = Allocate(rom, packed.Length);
        rom.WriteBytes(next, packed);
        SetFileOffset(rom, id, next);
    }

    // runs an edit that writes in several steps (an info block, then a start-up script, then events...). if it
    // throws, the rom gets back the bytes and size it had before, so failing partway leaves nothing half written (a
    // character start without its create, a trigger area without its event)
    public static T AllOrNothing<T>(RomBuffer rom, Func<T> edit)
    {
        var before = rom.ToArray();
        try { return edit(); }
        catch
        {
            if (!rom.SameAs(before)) rom.ReplaceAll(before);
            throw;
        }
    }

    // text the game shows for fixed-width names (monsters, items, spells): plain ASCII for the US game, 00 ends it
    public static string Name(RomBuffer rom, int offset, int length)
    {
        var sb = new System.Text.StringBuilder();
        for (int i = 0; i < length; i++)
        {
            byte nameByte = rom.ReadByte(offset + i);
            if (nameByte == 0) break;
            sb.Append(L2Charset.ToChar(rom, nameByte) ?? '?');
        }
        return sb.ToString().TrimEnd();
    }

    // writes a fixed-width name, space padded (characters the game can't show become spaces)
    public static void WriteName(RomBuffer rom, int offset, int length, string name)
    {
        var bytes = new byte[length];
        for (int i = 0; i < length; i++)
        {
            char c = i < name.Length ? name[i] : ' ';
            bytes[i] = L2Charset.ToByte(rom, c) ?? (byte)' ';
        }
        rom.WriteBytes(offset, bytes);
    }

    // the seven heroes the way the equip / learn bits name them
    public static readonly string[] Heroes = { "Maxim", "Selan", "Guy", "Artea", "Tia", "Dekar", "Lexis" };
}
