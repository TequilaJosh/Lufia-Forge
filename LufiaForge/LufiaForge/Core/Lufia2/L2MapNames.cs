namespace LufiaForge.Core.Lufia2;

/// <summary>
/// Map names in the game (US ROM, from the decompiled code 2026-11-02), and the box that shows a map's name on entering.
/// <para>
/// A name is the u16 at map header +6: its bytes are at $87:8000 + that u16, ended by 00, with the text dictionary
/// (05 / 06 / 80-FF) and 0A xx yy = the first (yyxx &gt;&gt; 12) + 2 bytes of the name at $87:8000 + (yyxx &amp; 0FFF).
/// Two places read them: <c>CopyMapName</c> ($80:C9C0: four <c>LDA $878000,X</c>, copying a name to RAM with the
/// 0A pieces put in; the save menu prints that copy) and <c>ShowMapNameOnEntry</c> ($80:C825: copies the name, and
/// when it differs from the last map's shows it with <c>ShowMapNameBox</c>, which prints from bank $09B9, set by
/// <c>LDA #$87</c> at $80:C8AD, with the data bank set to it). The game's 242 names fill bank $87 up to its start-up
/// scripts (15 bytes free), so to name maps Lufia Forge moves every name into a chunk of the event space
/// (<see cref="L2EventSpace"/>: banks where the data bank still reaches RAM, which the box's printing needs), with
/// the 0A pieces written out as <c>CopyMapName</c> does, and points those five bytes at the chunk's bank.
/// </para>
/// <para>
/// The box: <c>ShowMapNameOnEntry</c> compares the new name with the last one and skips the box when they are the same
/// (<c>BEQ</c> at $80:C8BE; "every map" points it at the box instead), then sets the box's place ($125D = 0x40: row 1,
/// <c>LDX #$0040</c> at $80:C8C9; "never" makes that <c>BRA</c> to the end). <c>ShowMapNameBox</c> centres every box it
/// draws by adding (0x1E - width) &amp; ~1 to $125D ($80:C8EF-$80:C900; width in tiles, the frame takes one more on each
/// side). For a corner the place is set to 0x41 (the other boxes, "Found ..." and the door messages, use even places)
/// and the centring is replaced by a JSL to code that puts an odd place in its corner and centres the others as before.
/// </para>
/// </summary>
public static class L2MapNames
{
    public const int MaxLetters = 20;
    private static readonly int[] CopySites = { 0x49CA, 0x49D5, 0x49DC, 0x49F6 };   // LDA $878000,X in CopyMapName
    private const int BoxBankAt = 0x48AE;   // the operand of LDA #$87 at $80:C8AD
    private const int SameAt = 0x48BF;      // BEQ operand at $80:C8BE: 13 = skip the box when the name is the same
    private const int PlaceAt = 0x48C9;     // LDX #$0040 at $80:C8C9
    private const int CentreAt = 0x48EF;    // SEC .. STA $125D, 18 bytes
    private static readonly byte[] Centre = { 0x38, 0xE9, 0x1E, 0x00, 0x49, 0xFF, 0xFF, 0x1A, 0x29, 0xFE, 0xFF, 0x18, 0x6D, 0x5D, 0x12, 0x8D, 0x5D, 0x12 };
    private const string ChunkMagic = "LF2N", HookMagic = "LF2NB";

    // ---------------------------------------------------------------- where the names are

    /// <summary>The bank the game reads names from ($87, or the event space bank they were moved to).</summary>
    public static int Bank(RomBuffer rom) => rom.ReadByte(CopySites[0] + 3);

    /// <summary>File offset of $xx:8000 for the names' bank: a name is here + its u16.</summary>
    public static int Base(RomBuffer rom) => L2Rom.SnesToFile(Bank(rom) << 16 | 0x8000);

    public static bool IsMoved(RomBuffer rom) => Bank(rom) != 0x87;

    /// <summary>File offset of map <paramref name="map"/>'s name.</summary>
    public static int NameAt(RomBuffer rom, int map) => Base(rom) + rom.ReadUInt16Le(L2Events.HeaderAt(rom, map) + 6);

    /// <summary>Why names can't be written on this ROM, or null.</summary>
    public static string? CantWrite(RomBuffer rom)
    {
        if (CopySites.Any(a => rom.ReadByte(a) != 0xBF || rom.ReadUInt16Le(a + 1) != 0x8000 || rom.ReadByte(a + 3) != Bank(rom)) || rom.ReadByte(BoxBankAt - 1) != 0xA9 || rom.ReadByte(BoxBankAt) != Bank(rom))
            return "Another hack changed how the game reads map names, so Lufia Forge doesn't write them.";
        return null;
    }

    /// <summary>A name's bytes as <c>CopyMapName</c> copies them: 0A pieces written out, ended by 00.</summary>
    public static byte[] Expanded(RomBuffer rom, int at)
    {
        int b = Base(rom);
        var outp = new List<byte>();
        for (int p = at, guard = 0; guard < 64; guard++)
        {
            byte c = rom.ReadByte(p);
            if (c == 0) break;
            if (c == 0x0A)
            {
                int w = rom.ReadUInt16Le(p + 1);
                int n = (w >> 12) + 2, from = b + (w & 0x0FFF);
                // a piece with a 00 in it ends the name there, for the copy the save menu prints too (the US game has a
                // few names whose pieces point into the header table)
                for (int i = 0; i < n; i++) { byte x = rom.ReadByte(from + i); if (x == 0) { outp.Add(0); return outp.ToArray(); } outp.Add(x); }
                p += 3;
                continue;
            }
            outp.Add(c);
            p++;
        }
        outp.Add(0);
        return outp.ToArray();
    }

    /// <summary>Moves every map's name into a chunk of the event space (with room for more) and points the game there.
    /// The game shows the same names as before.</summary>
    public static void Move(RomBuffer rom)
    {
        if (IsMoved(rom)) return;
        if (CantWrite(rom) is { } why) throw new InvalidOperationException(why);
        var names = Enumerable.Range(0, L2Maps.Count(rom)).Select(m => Expanded(rom, NameAt(rom, m))).ToList();
        Pack(rom, names);
    }

    /// <summary>Writes every name into a new chunk (identical names share their bytes) and points the maps and the game
    /// at it.</summary>
    private static void Pack(RomBuffer rom, List<byte[]> names)
    {
        var blob = new List<byte>();
        var placed = new Dictionary<string, int>();
        var at = new int[names.Count];
        for (int m = 0; m < names.Count; m++)
        {
            string key = Convert.ToHexString(names[m]);
            if (!placed.TryGetValue(key, out int a)) { a = blob.Count; placed[key] = a; blob.AddRange(names[m]); }
            at[m] = a;
        }
        int room = Math.Min(L2EventSpace.Mark - 8, blob.Count + 0x800);
        int chunk = L2EventSpace.Allocate(rom, room + 8);
        int data = chunk + 8;
        rom.WriteBytes(chunk, System.Text.Encoding.ASCII.GetBytes(ChunkMagic).Concat(new[] { (byte)room, (byte)(room >> 8), (byte)blob.Count, (byte)(blob.Count >> 8) }).ToArray());
        rom.WriteBytes(data, blob.ToArray());
        int snes = L2Rom.FileToSnes(data), bank = snes >> 16;
        foreach (int a in CopySites) rom.WriteByte(a + 3, (byte)bank);
        rom.WriteByte(BoxBankAt, (byte)bank);
        for (int m = 0; m < names.Count; m++) rom.WriteUInt16Le(L2Events.HeaderAt(rom, m) + 6, (ushort)((snes & 0xFFFF) - 0x8000 + at[m]));
    }

    /// <summary>The chunk the names were moved to: (file offset of its data, room, used), or null.</summary>
    private static (int Data, int Room, int Used)? Chunk(RomBuffer rom)
    {
        if (!IsMoved(rom)) return null;
        int first = Enumerable.Range(0, L2Maps.Count(rom)).Min(m => NameAt(rom, m));
        int h = first - 8;
        if (h < 0 || System.Text.Encoding.ASCII.GetString(rom.ReadBytes(h, 4)) != ChunkMagic) return null;
        return (first, rom.ReadUInt16Le(h + 4), rom.ReadUInt16Le(h + 6));
    }

    /// <summary>The bytes for a name, or why it can't be one: one byte per letter of the game's font. (The box drawn on
    /// entering prints each byte as a letter, $80:C91F-$80:C981, so dictionary words would come out as other letters.)</summary>
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

    /// <summary>Gives map <paramref name="map"/> the name <paramref name="name"/> in the game (the save menu and the box on
    /// entering). The names are moved out of bank $87 the first time.</summary>
    public static void Write(RomBuffer rom, int map, string name) => L2Rom.AllOrNothing(rom, () =>
    {
        var (bytes, problem) = Encode(rom, name);
        if (bytes == null) throw new InvalidOperationException(problem);
        Move(rom);
        var c = Chunk(rom) ?? throw new InvalidOperationException("The moved map names weren't found.");
        // the same name as another map's: share it
        for (int m = 0; m < L2Maps.Count(rom); m++)
        {
            int a = NameAt(rom, m);
            if (rom.ReadBytes(a, bytes.Length).SequenceEqual(bytes)) { SetPointer(rom, map, a); return 0; }
        }
        if (c.Used + bytes.Length > c.Room)
        {
            // full: everything packed again into a bigger chunk (names no map uses any more are left out)
            var names = Enumerable.Range(0, L2Maps.Count(rom)).Select(m => m == map ? bytes : Expanded(rom, NameAt(rom, m))).ToList();
            Pack(rom, names);
            return 0;
        }
        int at = c.Data + c.Used;
        rom.WriteBytes(at, bytes);
        rom.WriteUInt16Le(c.Data - 2, (ushort)(c.Used + bytes.Length));
        SetPointer(rom, map, at);
        return 0;
    });

    private static void SetPointer(RomBuffer rom, int map, int at) =>
        rom.WriteUInt16Le(L2Events.HeaderAt(rom, map) + 6, (ushort)((L2Rom.FileToSnes(at) & 0xFFFF) - 0x8000));

    // ---------------------------------------------------------------- the box on entering

    public enum When { Never, NameChanges, EveryMap }
    public enum Where { TopMiddle, TopLeft, TopRight, BottomLeft, BottomRight }
    public static readonly string[] WhenLabels = { "Never", "When the name changes (the game's)", "On every map" };
    public static readonly string[] WhereLabels = { "Top middle (the game's)", "Top left corner", "Top right corner", "Bottom left corner", "Bottom right corner" };

    /// <summary>Whether the box settings can be changed (the code is the game's or Lufia Forge's).</summary>
    public static bool CanSetBox(RomBuffer rom) =>
        rom.ReadByte(SameAt - 1) == 0xF0 && rom.ReadByte(SameAt) is 0x13 or 0x09
        && (rom.ReadByte(PlaceAt) == 0xA2 && rom.ReadByte(PlaceAt + 2) == 0x00 && rom.ReadByte(PlaceAt + 1) is 0x40 or 0x41 || rom.ReadByte(PlaceAt) == 0x80 && rom.ReadByte(PlaceAt + 1) == 0x08)
        && (rom.ReadBytes(CentreAt, Centre.Length).SequenceEqual(Centre) || Hook(rom) >= 0);

    public static When BoxWhen(RomBuffer rom) =>
        rom.ReadByte(PlaceAt) == 0x80 ? When.Never : rom.ReadByte(SameAt) == 0x09 ? When.EveryMap : When.NameChanges;

    public static Where BoxWhere(RomBuffer rom) => Hook(rom) is var h and >= 0 ? (Where)rom.ReadByte(h + 6) : Where.TopMiddle;

    /// <summary>The corner code (file offset of its magic), or -1.</summary>
    private static int Hook(RomBuffer rom)
    {
        if (rom.ReadByte(CentreAt) != 0x22) return -1;
        int at = L2Rom.SnesToFile((int)rom.ReadUInt24Le(CentreAt + 1)) - 8;
        return at >= 0 && at + 8 < rom.Length && System.Text.Encoding.ASCII.GetString(rom.ReadBytes(at, HookMagic.Length)) == HookMagic ? at : -1;
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
        int h = Hook(rom);
        var code = HookCode(where, h >= 0 ? h + 8 : -1, out int size);
        if (h < 0) h = L2Rom.Allocate(rom, size + 8, upper: true);
        code = HookCode(where, h + 8, out _);
        rom.WriteBytes(h, System.Text.Encoding.ASCII.GetBytes(HookMagic).Concat(new byte[] { 1, (byte)where, 0, 0 }).ToArray());
        rom.WriteBytes(h + 8, code);
        int snes = L2Rom.FileToSnes(h + 8);
        var jsl = new List<byte> { 0x22, (byte)snes, (byte)(snes >> 8), (byte)(snes >> 16) };
        while (jsl.Count < Centre.Length) jsl.Add(0xEA);
        rom.WriteBytes(CentreAt, jsl.ToArray());
    }

    /// <summary>Row and column (tiles) of a corner box's top-left: a box is the text's width + 2 tiles wide and 4 rows
    /// high (8 x 16 letters in a frame); a tile of space is kept to the screen's edge (rows 1 and 22, as the game's top box
    /// uses row 1).</summary>
    private static byte[] HookCode(Where where, int at, out int size)
    {
        bool right = where is Where.TopRight or Where.BottomRight;
        int row = where is Where.BottomLeft or Where.BottomRight ? 22 : 1;
        int left = row * 0x40 + 2, rightBase = row * 0x40 + 2 * 29;
        var c = new List<byte>
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
            c.AddRange(new byte[] { 0x0A, 0x49, 0xFF, 0xFF, 0x1A, 0x18, 0x69, (byte)rightBase, (byte)(rightBase >> 8) });   // ASL; EOR; INC: -2w; CLC; ADC #base
        else
            c.AddRange(new byte[] { 0xA9, (byte)left, (byte)(left >> 8) });                                                 // LDA #place
        c.AddRange(new byte[] { 0x8F, 0x5D, 0x12, 0x7E, 0x6B });   // STA $7E125D; RTL
        size = c.Count;
        return c.ToArray();
    }
}
