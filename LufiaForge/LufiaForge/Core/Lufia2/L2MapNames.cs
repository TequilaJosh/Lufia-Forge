namespace LufiaForge.Core.Lufia2;

// map names in the game (US rom, from the decompiled code 2026-11-02), and the box that shows a map's name when you
// walk in.
//
// a name is the u16 at map header +6. its bytes are at $87:8000 + that u16, ended by 00, with the text dictionary
// (05 / 06 / 80-FF) and 0A xx yy = the first (yyxx >> 12) + 2 bytes of the name at $87:8000 + (yyxx & 0FFF).
// two places read them:
// - CopyMapName ($80:C9C0, four LDA $878000,X) copies a name to RAM with the 0A pieces put in, the save menu prints that copy
// - ShowMapNameOnEntry ($80:C825) copies the name, and if it's different from the last map's shows it with
//   ShowMapNameBox, which prints from bank $09B9 (set by LDA #$87 at $80:C8AD) with the data bank set to it
// the game's 242 names fill bank $87 up to its start-up scripts (15 bytes free). so to name maps we move every name
// into a chunk of the event space (L2EventSpace: banks where the data bank still reaches RAM, the box's printing needs
// that), with the 0A pieces written out like CopyMapName does, and point those five bytes at the chunk's bank.
//
// the box: ShowMapNameOnEntry compares the new name with the last one and skips the box if they match (BEQ at
// $80:C8BE, "every map" points it at the box instead), then sets the box's place ($125D = 0x40 = row 1, LDX #$0040
// at $80:C8C9, "never" makes that a BRA to the end). ShowMapNameBox centres every box it draws by adding
// (0x1E - width) & ~1 to $125D ($80:C8EF-$80:C900, width in tiles, the frame takes one more on each side).
// for a corner we set the place to 0x41 (the other boxes, "Found ..." and the door messages, use even places) and
// swap the centring for a JSL to code that puts an odd place in its corner and centres the others like before
public static class L2MapNames
{
    public const int MaxLetters = 20;
    private static readonly int[] CopySites = { 0x49CA, 0x49D5, 0x49DC, 0x49F6 };   // LDA $878000,X in CopyMapName
    private const int BoxBankAt = 0x48AE;   // the operand of LDA #$87 at $80:C8AD
    private const int SameAt = 0x48BF;      // BEQ operand at $80:C8BE, 13 = skip the box if the name is the same
    private const int PlaceAt = 0x48C9;     // LDX #$0040 at $80:C8C9
    private const int CentreAt = 0x48EF;    // SEC .. STA $125D, 18 bytes
    private static readonly byte[] Centre = { 0x38, 0xE9, 0x1E, 0x00, 0x49, 0xFF, 0xFF, 0x1A, 0x29, 0xFE, 0xFF, 0x18, 0x6D, 0x5D, 0x12, 0x8D, 0x5D, 0x12 };
    private const string ChunkMagic = "LF2N", HookMagic = "LF2NB";

    // where the names are

    // the bank the game reads names from ($87, or the event space bank they got moved to)
    public static int Bank(RomBuffer rom) => rom.ReadByte(CopySites[0] + 3);

    // file offset of $xx:8000 for the names' bank, a name is here + its u16
    public static int Base(RomBuffer rom) => L2Rom.SnesToFile(Bank(rom) << 16 | 0x8000);

    public static bool IsMoved(RomBuffer rom) => Bank(rom) != 0x87;

    // file offset of the map's name
    public static int NameAt(RomBuffer rom, int map) => Base(rom) + rom.ReadUInt16Le(L2Events.HeaderAt(rom, map) + 6);

    // why names can't be written on this rom, or null
    public static string? CantWrite(RomBuffer rom)
    {
        if (CopySites.Any(site => rom.ReadByte(site) != 0xBF || rom.ReadUInt16Le(site + 1) != 0x8000 || rom.ReadByte(site + 3) != Bank(rom)) || rom.ReadByte(BoxBankAt - 1) != 0xA9 || rom.ReadByte(BoxBankAt) != Bank(rom))
            return "Another hack changed how the game reads map names, so Lufia Forge doesn't write them.";
        return null;
    }

    // a name's bytes the way CopyMapName copies them: 0A pieces written out, ended by 00
    public static byte[] Expanded(RomBuffer rom, int at)
    {
        int nameBase = Base(rom);
        var expanded = new List<byte>();
        for (int offset = at, guard = 0; guard < 64; guard++)
        {
            byte nameByte = rom.ReadByte(offset);
            if (nameByte == 0) break;
            if (nameByte == 0x0A)
            {
                int pieceWord = rom.ReadUInt16Le(offset + 1);
                int pieceLength = (pieceWord >> 12) + 2, from = nameBase + (pieceWord & 0x0FFF);
                // a piece with a 00 in it ends the name there, for the copy the save menu prints too (the US game has a
                // few names whose pieces point into the header table)
                for (int i = 0; i < pieceLength; i++) { byte pieceByte = rom.ReadByte(from + i); if (pieceByte == 0) { expanded.Add(0); return expanded.ToArray(); } expanded.Add(pieceByte); }
                offset += 3;
                continue;
            }
            expanded.Add(nameByte);
            offset++;
        }
        expanded.Add(0);
        return expanded.ToArray();
    }

    // moves every map's name into a chunk of the event space (with room for more) and points the game there.
    // the game shows the same names as before
    public static void Move(RomBuffer rom)
    {
        if (IsMoved(rom)) return;
        if (CantWrite(rom) is { } why) throw new InvalidOperationException(why);
        var names = Enumerable.Range(0, L2Maps.Count(rom)).Select(m => Expanded(rom, NameAt(rom, m))).ToList();
        Pack(rom, names);
    }

    // writes every name into a new chunk (identical names share their bytes) and points the maps and the game at it
    private static void Pack(RomBuffer rom, List<byte[]> names)
    {
        var blob = new List<byte>();
        var placed = new Dictionary<string, int>();
        var nameOffsets = new int[names.Count];
        for (int m = 0; m < names.Count; m++)
        {
            string key = Convert.ToHexString(names[m]);
            if (!placed.TryGetValue(key, out int blobOffset)) { blobOffset = blob.Count; placed[key] = blobOffset; blob.AddRange(names[m]); }
            nameOffsets[m] = blobOffset;
        }
        int room = Math.Min(L2EventSpace.Mark - 8, blob.Count + 0x800);
        int chunk = L2EventSpace.Allocate(rom, room + 8);
        int data = chunk + 8;
        rom.WriteBytes(chunk, System.Text.Encoding.ASCII.GetBytes(ChunkMagic).Concat(new[] { (byte)room, (byte)(room >> 8), (byte)blob.Count, (byte)(blob.Count >> 8) }).ToArray());
        rom.WriteBytes(data, blob.ToArray());
        int snesAddress = L2Rom.FileToSnes(data), bank = snesAddress >> 16;
        foreach (int site in CopySites) rom.WriteByte(site + 3, (byte)bank);
        rom.WriteByte(BoxBankAt, (byte)bank);
        for (int m = 0; m < names.Count; m++) rom.WriteUInt16Le(L2Events.HeaderAt(rom, m) + 6, (ushort)((snesAddress & 0xFFFF) - 0x8000 + nameOffsets[m]));
    }

    // the chunk the names were moved to: (file offset of its data, room, used), or null
    private static (int Data, int Room, int Used)? Chunk(RomBuffer rom)
    {
        if (!IsMoved(rom)) return null;
        // the chunk's head is before every name, in the same bank. it's not just before the lowest name though: map
        // 00's name is the chunk's first, and once map 00 is renamed it points at the new name further on
        var names = Enumerable.Range(0, L2Maps.Count(rom)).Select(m => NameAt(rom, m)).ToList();
        int first = names.Min(), last = names.Max();
        for (int head = first - 8; head >= first / 0x8000 * 0x8000; head--)
        {
            if (System.Text.Encoding.ASCII.GetString(rom.ReadBytes(head, 4)) != ChunkMagic) continue;
            int room = rom.ReadUInt16Le(head + 4);
            if (last < head + 8 + room) return (head + 8, room, rom.ReadUInt16Le(head + 6));
        }
        return null;
    }

    // the bytes for a name, or why it can't be one: one byte per letter of the game's font. the box drawn when you
    // walk in prints each byte as a letter ($80:C91F-$80:C981), so dictionary words would come out as other letters
    public static (byte[]? Bytes, string? Problem) Encode(RomBuffer rom, string name)
    {
        name = name.Trim();
        if (name.Length == 0) return (null, "Type a name.");
        if (name.Length > MaxLetters) return (null, $"A map name has at most {MaxLetters} letters (the box is one line).");
        var bad = name.Where(c => !L2Charset.CanShow(rom, c)).Distinct().ToList();
        if (bad.Count > 0) return (null, $"The game's font has no {string.Join(" ", bad.Select(c => $"\"{c}\""))}.");
        var bytes = name.Select(c => L2Charset.ToByte(rom, c)!.Value).ToList();
        if (bytes.Any(b => b < 0x10 || b >= 0x80)) return (null, "That letter is a text code in a map name.");
        return (bytes.Append((byte)0).ToArray(), null);
    }

    // gives the map that name in the game (the save menu and the box when you walk in). the names get moved out of
    // bank $87 the first time
    public static void Write(RomBuffer rom, int map, string name) => L2Rom.AllOrNothing(rom, () =>
    {
        var (bytes, problem) = Encode(rom, name);
        if (bytes == null) throw new InvalidOperationException(problem);
        Move(rom);
        var chunk = Chunk(rom) ?? throw new InvalidOperationException("The moved map names weren't found.");
        // same name as another map's: share it
        for (int m = 0; m < L2Maps.Count(rom); m++)
        {
            int nameAt = NameAt(rom, m);
            if (rom.ReadBytes(nameAt, bytes.Length).SequenceEqual(bytes)) { SetPointer(rom, map, nameAt); return 0; }
        }
        if (chunk.Used + bytes.Length > chunk.Room)
        {
            // full: pack everything again into a bigger chunk (names no map uses any more get left out)
            var names = Enumerable.Range(0, L2Maps.Count(rom)).Select(m => m == map ? bytes : Expanded(rom, NameAt(rom, m))).ToList();
            Pack(rom, names);
            return 0;
        }
        int newAt = chunk.Data + chunk.Used;
        rom.WriteBytes(newAt, bytes);
        rom.WriteUInt16Le(chunk.Data - 2, (ushort)(chunk.Used + bytes.Length));
        SetPointer(rom, map, newAt);
        return 0;
    });

    private static void SetPointer(RomBuffer rom, int map, int nameAt) =>
        rom.WriteUInt16Le(L2Events.HeaderAt(rom, map) + 6, (ushort)((L2Rom.FileToSnes(nameAt) & 0xFFFF) - 0x8000));

    // the box when you walk in

    public enum When { Never, NameChanges, EveryMap }
    public enum Where { TopMiddle, TopLeft, TopRight, BottomLeft, BottomRight }
    public static readonly string[] WhenLabels = { "Never", "When the name changes (the game's)", "On every map" };
    public static readonly string[] WhereLabels = { "Top middle (the game's)", "Top left corner", "Top right corner", "Bottom left corner", "Bottom right corner" };

    // true if the box settings can be changed (the code is the game's or ours)
    public static bool CanSetBox(RomBuffer rom) =>
        rom.ReadByte(SameAt - 1) == 0xF0 && rom.ReadByte(SameAt) is 0x13 or 0x09
        && (rom.ReadByte(PlaceAt) == 0xA2 && rom.ReadByte(PlaceAt + 2) == 0x00 && rom.ReadByte(PlaceAt + 1) is 0x40 or 0x41 || rom.ReadByte(PlaceAt) == 0x80 && rom.ReadByte(PlaceAt + 1) == 0x08)
        && (rom.ReadBytes(CentreAt, Centre.Length).SequenceEqual(Centre) || Hook(rom) >= 0);

    public static When BoxWhen(RomBuffer rom) =>
        rom.ReadByte(PlaceAt) == 0x80 ? When.Never : rom.ReadByte(SameAt) == 0x09 ? When.EveryMap : When.NameChanges;

    public static Where BoxWhere(RomBuffer rom) => Hook(rom) is var hook and >= 0 ? (Where)rom.ReadByte(hook + 6) : Where.TopMiddle;

    // the corner code's file offset (its 8-byte head first) and size, or -1 (L2HighBanks)
    internal static (int At, int Size) CornerCode(RomBuffer rom)
    {
        int hook = Hook(rom);
        if (hook < 0) return (-1, 0);
        HookCode((Where)rom.ReadByte(hook + 6), hook + 8, out int size);
        return (hook, size + 8);
    }

    // makes the corner code again at to (room for CornerCode's size) and calls it there
    internal static void MoveCornerCode(RomBuffer rom, int to)
    {
        int hook = Hook(rom);
        if (hook < 0) return;
        var where = (Where)rom.ReadByte(hook + 6);
        rom.WriteBytes(to, rom.ReadBytes(hook, 8));
        rom.WriteBytes(to + 8, HookCode(where, to + 8, out int size));
        rom.WriteByte(to + 7, (byte)size);   // the room made for it (CornerCode's size)
        int snesAddress = L2Rom.FileToSnes(to + 8);
        rom.WriteBytes(CentreAt + 1, new[] { (byte)snesAddress, (byte)(snesAddress >> 8), (byte)(snesAddress >> 16) });
    }

    // the longest corner code (new room holds any corner)
    private static int MaxCornerSize => new[] { Where.TopLeft, Where.TopRight, Where.BottomLeft, Where.BottomRight }
        .Max(w => { HookCode(w, 0x8000, out int codeSize); return codeSize; });

    // the corner code (file offset of its magic), or -1
    private static int Hook(RomBuffer rom)
    {
        if (rom.ReadByte(CentreAt) != 0x22) return -1;
        int hookAt = L2Rom.SnesToFile((int)rom.ReadUInt24Le(CentreAt + 1)) - 8;
        return hookAt >= 0 && hookAt + 8 < rom.Length && System.Text.Encoding.ASCII.GetString(rom.ReadBytes(hookAt, HookMagic.Length)) == HookMagic ? hookAt : -1;
    }

    public static void SetBox(RomBuffer rom, When when, Where where)
    {
        if (!CanSetBox(rom)) throw new InvalidOperationException("Another hack changed the map name box, so Lufia Forge leaves it alone.");
        rom.WriteByte(SameAt, (byte)(when == When.EveryMap ? 0x09 : 0x13));
        rom.WriteBytes(PlaceAt, when == When.Never ? new byte[] { 0x80, 0x08, 0x00 } : new byte[] { 0xA2, (byte)(where == Where.TopMiddle ? 0x40 : 0x41), 0x00 });
        if (where == Where.TopMiddle)
        {
            rom.WriteBytes(CentreAt, Centre);   // the corner code stays where it is, unused
            return;
        }
        int hook = Hook(rom);
        HookCode(where, hook >= 0 ? hook + 8 : 0, out int size);
        // the room there is its head's last byte (0 in roms from before 2026-11-16, then it's the size of the corner it
        // holds). a corner whose code is longer (a right corner after a left one) gets new room, enough for any corner
        int room = hook < 0 ? 0 : rom.ReadByte(hook + 7) is var roomByte and > 0 ? roomByte : CornerCode(rom).Size - 8;
        if (hook < 0 || size > room)
        {
            room = MaxCornerSize;
            hook = L2Rom.Allocate(rom, room + 8, upper: true);
        }
        var code = HookCode(where, hook + 8, out _);
        rom.WriteBytes(hook, System.Text.Encoding.ASCII.GetBytes(HookMagic).Concat(new byte[] { 1, (byte)where, 0, (byte)room }).ToArray());
        rom.WriteBytes(hook + 8, code);
        int snesAddress = L2Rom.FileToSnes(hook + 8);
        var jsl = new List<byte> { 0x22, (byte)snesAddress, (byte)(snesAddress >> 8), (byte)(snesAddress >> 16) };
        while (jsl.Count < Centre.Length) jsl.Add(0xEA);
        rom.WriteBytes(CentreAt, jsl.ToArray());
    }

    // row and column (tiles) of a corner box's top-left. a box is the text's width + 2 tiles wide and 4 rows high
    // (8 x 16 letters in a frame), and we keep a tile of space to the screen's edge (rows 1 and 22, the game's top box
    // uses row 1)
    private static byte[] HookCode(Where where, int at, out int size)
    {
        bool right = where is Where.TopRight or Where.BottomRight;
        int row = where is Where.BottomLeft or Where.BottomRight ? 22 : 1;
        int left = row * 0x40 + 2, rightBase = row * 0x40 + 2 * 29;
        var code = new List<byte>
        {
            0x48,                               // PHA (the width, 16 bits)
            0xAF, 0x5D, 0x12, 0x7E,             // LDA $7E125D
            0x4A,                               // LSR A: an odd place is the map name box
            0xB0, 0x16,                         // BCS corner
            0x68,                               // PLA
            0x38, 0xE9, 0x1E, 0x00,             // SEC; SBC #$001E   (the game's centring)
            0x49, 0xFF, 0xFF, 0x1A,             // EOR #$FFFF; INC A
            0x29, 0xFE, 0xFF, 0x18,             // AND #$FFFE; CLC
            0x6F, 0x5D, 0x12, 0x7E,             // ADC $7E125D
            0x8F, 0x5D, 0x12, 0x7E,             // STA $7E125D
            0x6B,                               // RTL
            // corner:
            0x68,                               // PLA
        };
        if (right)
            code.AddRange(new byte[] { 0x0A, 0x49, 0xFF, 0xFF, 0x1A, 0x18, 0x69, (byte)rightBase, (byte)(rightBase >> 8) });   // ASL; EOR; INC: -2w; CLC; ADC #base
        else
            code.AddRange(new byte[] { 0xA9, (byte)left, (byte)(left >> 8) });                                                 // LDA #place
        code.AddRange(new byte[] { 0x8F, 0x5D, 0x12, 0x7E, 0x6B });   // STA $7E125D; RTL
        size = code.Count;
        return code.ToArray();
    }
}
