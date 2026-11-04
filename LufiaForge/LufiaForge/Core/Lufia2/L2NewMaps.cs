namespace LufiaForge.Core.Lufia2;

/// <summary>A table the game reads by map number (<see cref="L2NewMaps"/>).</summary>
public enum L2MapTable { BlockSet, TileSet, AnimIndex, PaletteSet, ColourAnim, Info, Header, Puzzle, Files, Encounters }

/// <summary>
/// New Lufia II maps (US ROM, from the decompiled code 2026-11-02). The game has 242 maps (00-F1) and uses every one, so a
/// new map takes a number from F2 to FF (exits and scripts keep the map in one byte; the visited bits at $097B have room
/// for 256). The game reads a map's data from these tables, each indexed by the map number and each followed at once by
/// other data, so none has room for more maps where it is:
/// <list type="bullet">
/// <item>the map file: file 0x3F + map, unpacked by $80:EAE7 (<c>ADC #$003F</c> at $80:EB24);</item>
/// <item>the file table $A7:8000 (3 bytes a file; $80:8EA9, $80:8EB1), which has no room for more files after it;</item>
/// <item>the map header $87:8010 + map * 8 (event block, start-up script, name; $83:B5BA, $83:B5C7, $80:BEB9,
/// $80:BEC6, $80:C894, $82:F4A3);</item>
/// <item>the graphics lists $CF:F72E (block set, $80:EB8B), $CF:F81E (tile set, $80:F03B), $CF:F90E (animation,
/// $80:EB4B, $80:F217), $CF:FA08 (palette set, $80:F2D2), $CF:FBA6 (colour animation, $80:EFC8);</item>
/// <item>the map info pointers $CF:FCBC (3 bytes; $83:B60C, $83:B601, $86:9F4E, $86:9F44);</item>
/// <item>the puzzle blocks $8F:8000 (3 bytes; $80:E86D, $80:E88C);</item>
/// <item>the field monster lists $97:B9AC (u16 offsets from $97:B9AC; $81:837D reads the offset with DB = $97 as
/// <c>LDA $B9AC,X</c> + <c>CLC</c>, the same 4 bytes as <c>LDA long,X</c>: the carry is clear after the <c>ASL</c>
/// of a map below 0x80);</item>
/// <item>the treasure lists $91:B8B5 (u16 offsets): the game left 0x1C bytes of FF after its 242 entries, room for
/// maps F2-FF, so that table stays (new maps start on the empty list at offset 0x201).</item>
/// </list>
/// A raw search of the whole ROM finds no other reads of these tables. Lufia Forge copies them all into one block of the
/// added space ("LF2MX", 256 entries each, the file table with 14 more files from <see cref="NewFileBase"/>) and points
/// those reads at it; a 6-byte hook at $80:EB23 (<c>CLC; ADC #$003F; STA $54</c> -> <c>JSL; NOP; NOP</c>) gives maps F2
/// and up the files <see cref="NewFileBase"/> + map - F2. The map's name stays a u16 from $87:8000 (the name readers
/// add bank $87 themselves), so a new map starts with its template's name. Two tables are left as they are: $94:C998
/// (each map's treasure group, read only by the treasure counter item, which counts maps up to F0) and the Ancient
/// Cave's (maps F0 and F1).
/// </summary>
public static class L2NewMaps
{
    public const int GameCount = 0xF2, MaxCount = 0x100, Slots = MaxCount - GameCount;
    /// <summary>The file number of map F2's file (the game's files end at 0x2A8; the ids up to 0x2AF are left unused).</summary>
    public const int NewFileBase = 0x2B0;
    /// <summary>The treasure list offset of an empty list (map 01's: one FF).</summary>
    public const int EmptyChestList = 0x201;
    /// <summary>The field monster list offset the game gives maps F0-F7 (no field monsters).</summary>
    public const int NoMonsterList = 0x4E7;

    private const string Magic = "LF2MX";
    private const int Version = 1;
    private const int CodeAt = 0x10, BlockSetAt = 0x30, TileSetAt = 0x130, AnimAt = 0x230, PaletteAt = 0x330, ColourAt = 0x430,
        InfoAt = 0x530, HeaderAt = 0x830, PuzzleAt = 0x1030, EncountersAt = 0x1330, FilesAt = 0x1530,
        BlockSize = FilesAt + (NewFileBase + Slots) * 3;
    private const int HookSite = 0x6B23;
    private static readonly byte[] HookOriginal = { 0x18, 0x69, 0x3F, 0x00, 0x85, 0x54 };
    private const int EncounterSite = 0x837D;
    private static readonly byte[] EncounterOriginal = { 0xBD, 0xAC, 0xB9, 0x18 };

    /// <summary>Every long read of a map table: the opcode's file offset, the bytes past the table start it reads
    /// (the info pointer's bank, the header's fields), and the address the game reads.</summary>
    private static readonly (L2MapTable Table, int At, int Delta, int Game)[] Sites =
    {
        (L2MapTable.BlockSet, 0x6B8B, 0, 0xCFF72E), (L2MapTable.TileSet, 0x703B, 0, 0xCFF81E),
        (L2MapTable.AnimIndex, 0x6B4B, 0, 0xCFF90E), (L2MapTable.AnimIndex, 0x7217, 0, 0xCFF90E),
        (L2MapTable.PaletteSet, 0x72D2, 0, 0xCFFA08), (L2MapTable.ColourAnim, 0x6FC8, 0, 0xCFFBA6),
        (L2MapTable.Info, 0x1B60C, 0, 0xCFFCBC), (L2MapTable.Info, 0x31F4E, 0, 0xCFFCBC),
        (L2MapTable.Info, 0x1B601, 2, 0xCFFCBC), (L2MapTable.Info, 0x31F44, 2, 0xCFFCBC),
        (L2MapTable.Header, 0x1B5BA, 0, 0x878010), (L2MapTable.Header, 0x1B5C7, 2, 0x878010),
        (L2MapTable.Header, 0x3EB9, 3, 0x878010), (L2MapTable.Header, 0x3EC6, 5, 0x878010),
        (L2MapTable.Header, 0x4894, 6, 0x878010), (L2MapTable.Header, 0x174A3, 6, 0x878010),
        (L2MapTable.Puzzle, 0x686D, 0, 0x8F8000), (L2MapTable.Puzzle, 0x688C, 2, 0x8F8000),
        (L2MapTable.Files, 0xEA9, 0, 0xA78000), (L2MapTable.Files, 0xEB1, 1, 0xA78000),
    };

    private static readonly Dictionary<L2MapTable, (int At, int Size)> Layout = new()
    {
        [L2MapTable.BlockSet] = (BlockSetAt, 1), [L2MapTable.TileSet] = (TileSetAt, 1), [L2MapTable.AnimIndex] = (AnimAt, 1),
        [L2MapTable.PaletteSet] = (PaletteAt, 1), [L2MapTable.ColourAnim] = (ColourAt, 1), [L2MapTable.Info] = (InfoAt, 3),
        [L2MapTable.Header] = (HeaderAt, 8), [L2MapTable.Puzzle] = (PuzzleAt, 3), [L2MapTable.Encounters] = (EncountersAt, 2),
        [L2MapTable.Files] = (FilesAt, 3),
    };

    /// <summary>File offset of the table's entry 0 as the game reads it now (its own table, or Lufia Forge's copy).</summary>
    public static int Table(RomBuffer rom, L2MapTable t)
    {
        if (t == L2MapTable.Encounters)
            return rom.ReadByte(EncounterSite) == 0xBF ? L2Rom.SnesToFile((int)rom.ReadUInt24Le(EncounterSite + 1)) : L2Encounters.MapLists;
        var s = Sites.First(x => x.Table == t);
        int snes = rom.ReadByte(s.At) == 0xBF ? (int)rom.ReadUInt24Le(s.At + 1) - s.Delta : s.Game;
        return L2Rom.SnesToFile(snes);
    }

    /// <summary>File offset of map <paramref name="map"/>'s entry of a table.</summary>
    public static int Entry(RomBuffer rom, L2MapTable t, int map) => Table(rom, t) + map * Layout[t].Size;

    /// <summary>The LF2MX block (file offset), or -1 when the game reads its own tables.</summary>
    public static int Block(RomBuffer rom)
    {
        if (rom.ReadByte(Sites[0].At) != 0xBF) return -1;
        int at = L2Rom.SnesToFile((int)rom.ReadUInt24Le(Sites[0].At + 1)) - BlockSetAt;
        if (at < 0 || at + BlockSize > rom.Length) return -1;
        return System.Text.Encoding.ASCII.GetString(rom.ReadBytes(at, Magic.Length)) == Magic ? at : -1;
    }

    public static bool IsInstalled(RomBuffer rom) => Block(rom) >= 0;

    /// <summary>How many new map numbers are in use (made, deleted ones in the middle included).</summary>
    public static int Made(RomBuffer rom) => Block(rom) is var b and >= 0 ? rom.ReadByte(b + 6) : 0;

    /// <summary>How many maps the ROM has: the game's 242 and the new ones.</summary>
    public static int Count(RomBuffer rom) => GameCount + Made(rom);

    /// <summary>Whether map <paramref name="map"/> is a new one (F2 up) that was deleted: it stays a small blank map
    /// until New map takes its number again.</summary>
    public static bool IsFree(RomBuffer rom, int map) =>
        map >= GameCount && map < Count(rom) && Block(rom) is var b and >= 0 && (rom.ReadUInt16Le(b + 8) >> (map - GameCount) & 1) != 0;

    /// <summary>The packed file that holds the map's blocks.</summary>
    public static int FileOf(int map) => map < GameCount ? L2Maps.MapFileBase + map : NewFileBase + map - GameCount;

    /// <summary>The files of the new maps (for scans over every file).</summary>
    public static IEnumerable<int> NewFiles(RomBuffer rom) => Enumerable.Range(0, Made(rom)).Select(i => NewFileBase + i);

    /// <summary>Why new maps can't be made on this ROM, or null.</summary>
    public static string? CantInstall(RomBuffer rom)
    {
        if (IsInstalled(rom)) return null;
        if (rom.Length < 0x200000) return "This isn't a Lufia II ROM Lufia Forge knows.";
        foreach (var s in Sites)
        {
            int game = s.Game + s.Delta;
            if (rom.ReadByte(s.At) != 0xBF || rom.ReadUInt24Le(s.At + 1) != game)
                return $"Another hack changed how the game reads its map tables (at ${L2Rom.FileToSnes(s.At):X6}), so Lufia Forge can't add maps to it.";
        }
        if (!rom.ReadBytes(HookSite, HookOriginal.Length).SequenceEqual(HookOriginal) || !rom.ReadBytes(EncounterSite, EncounterOriginal.Length).SequenceEqual(EncounterOriginal))
            return "Another hack changed how the game loads maps, so Lufia Forge can't add maps to it.";
        for (int m = GameCount; m < MaxCount; m++)
            if (rom.ReadUInt16Le(L2Chests.Table + m * 2) != 0xFFFF)
                return "Another hack uses the room after the treasure table, so Lufia Forge can't add maps to it.";
        return null;
    }

    /// <summary>Copies the map tables into the LF2MX block and points the game at it (once; New map does it by
    /// itself). The game plays as before: the 14 new entries are copies of map 02's, never used until a map is made.</summary>
    public static void Install(RomBuffer rom)
    {
        if (IsInstalled(rom)) return;
        if (CantInstall(rom) is { } why) throw new InvalidOperationException(why);
        int block = L2Rom.Allocate(rom, BlockSize, upper: true);
        var data = new byte[BlockSize];
        System.Text.Encoding.ASCII.GetBytes(Magic).CopyTo(data, 0);
        data[5] = Version;
        foreach (var (t, (at, size)) in Layout)
        {
            int from = Table(rom, t);
            int count = t == L2MapTable.Files ? NewFileBase : GameCount;
            Array.Copy(rom.ReadBytes(from, count * size), 0, data, at, count * size);
            for (int i = 0; i < Slots; i++)
            {
                var filler = t switch
                {
                    L2MapTable.Puzzle => new byte[] { 0xFF, 0xFF, 0xFF },
                    L2MapTable.Encounters => new byte[] { NoMonsterList & 0xFF, NoMonsterList >> 8 },
                    L2MapTable.Files => rom.ReadBytes(from + L2Maps.MapFileBase * 3 + 2 * 3, 3),
                    _ => rom.ReadBytes(from + 2 * size, size),
                };
                filler.CopyTo(data, at + (count + i) * size);
            }
        }
        // the hook: maps F2 up use the files from NewFileBase (A = the map, 16 bits; $54 = the file to unpack)
        int newFiles = NewFileBase - GameCount;
        new byte[]
        {
            0xC9, GameCount, 0x00,          // CMP #$00F2
            0xB0, 0x07,                     // BCS new
            0x18, 0x69, 0x3F, 0x00,         // CLC; ADC #$003F
            0x85, 0x54, 0x6B,               // STA $54; RTL
            0x18, 0x69, (byte)newFiles, (byte)(newFiles >> 8),   // new: CLC; ADC #(NewFileBase - F2)
            0x85, 0x54, 0x6B,               // STA $54; RTL
        }.CopyTo(data, CodeAt);
        rom.WriteBytes(block, data);
        for (int m = GameCount; m < MaxCount; m++) rom.WriteUInt16Le(L2Chests.Table + m * 2, EmptyChestList);
        foreach (var s in Sites)
        {
            int snes = L2Rom.FileToSnes(block + Layout[s.Table].At) + s.Delta;
            rom.WriteBytes(s.At + 1, new[] { (byte)snes, (byte)(snes >> 8), (byte)(snes >> 16) });
        }
        int enc = L2Rom.FileToSnes(block + EncountersAt);
        rom.WriteBytes(EncounterSite, new byte[] { 0xBF, (byte)enc, (byte)(enc >> 8), (byte)(enc >> 16) });
        int code = L2Rom.FileToSnes(block + CodeAt);
        rom.WriteBytes(HookSite, new byte[] { 0x22, (byte)code, (byte)(code >> 8), (byte)(code >> 16), 0xEA, 0xEA });
        rom.FixChecksum();
    }

    /// <summary>
    /// Makes a new map from <paramref name="template"/> and returns its number. A copy has the template's blocks,
    /// graphics, map info (exits, characters, treasure spots, doors), its own copy of the event block and start-up
    /// script, its treasure with new opened flags, a copy of its puzzles (they share the template's puzzle switches) and
    /// its field monster list. A blank map has the template's graphics, music and map settings, <paramref name="width"/>
    /// x <paramref name="height"/> blocks of <paramref name="fill"/> on the ground and nothing on the upper layer, no
    /// characters, exits, treasure or events, and preset point 0 in the middle.
    /// </summary>
    public static int Create(RomBuffer rom, int template, bool blank, int width = 0, int height = 0, int fill = 0) => L2Rom.AllOrNothing(rom, () =>
    {
        if (template < 2 || template >= Count(rom)) throw new InvalidOperationException("Pick a town or dungeon map to start from (the world maps can't be copied).");
        Install(rom);
        int b = Block(rom);
        int made = rom.ReadByte(b + 6), free = rom.ReadUInt16Le(b + 8);
        int slot = Enumerable.Range(0, made).FirstOrDefault(i => (free >> i & 1) != 0, made);
        if (slot >= Slots) throw new InvalidOperationException($"All {Slots} new map numbers (F2-FF) are used. Delete a new map to make room.");
        int map = GameCount + slot;
        // the number is taken first, so the tables below are read and written for it
        if (slot == made) rom.WriteByte(b + 6, (byte)(made + 1));
        rom.WriteUInt16Le(b + 8, (ushort)(free & ~(1 << slot)));

        // blocks
        var src = L2Rom.ReadFile(rom, FileOf(template));
        byte[] file = blank ? BlankFile(src, width, height, fill) : src;
        L2Rom.WriteNewFile(rom, FileOf(map), file);
        // graphics
        foreach (var t in new[] { L2MapTable.BlockSet, L2MapTable.TileSet, L2MapTable.AnimIndex, L2MapTable.PaletteSet, L2MapTable.ColourAnim })
            rom.WriteByte(Entry(rom, t, map), rom.ReadByte(Entry(rom, t, template)));
        // map info: always its own copy (the editor writes over an info block no other map shares)
        int w = file[8], h = file[9];
        byte[] info = blank ? BlankInfo(w / 2, h / 2) : rom.ReadBytes(L2MapInfo.Block(rom, template), rom.ReadUInt16Le(L2MapInfo.Block(rom, template)));
        int infoAt = L2Rom.Allocate(rom, info.Length + 1, upper: true);
        rom.WriteBytes(infoAt, info.Append((byte)0).ToArray());
        int infoSnes = L2Rom.FileToSnes(infoAt);
        rom.WriteBytes(Entry(rom, L2MapTable.Info, map), new[] { (byte)infoSnes, (byte)(infoSnes >> 8), (byte)(infoSnes >> 16) });
        // header: event block, start-up script (both its own), the template's name
        int head = Entry(rom, L2MapTable.Header, map), tHead = Entry(rom, L2MapTable.Header, template);
        rom.WriteBytes(head, rom.ReadBytes(tHead, 8));
        int events = blank ? L2EventBlocks.NewBlock(rom) : L2EventBlocks.CopyBlock(rom, L2Events.Block(rom, template));
        int ev = L2EventBlocks.HeaderValue(events);
        rom.WriteBytes(head, new[] { (byte)ev, (byte)(ev >> 8), (byte)(ev >> 16) });
        var init = L2EventScript.Read(rom, L2EventBlocks.InitScript(rom, template), L2EventBlocks.InitBase(L2EventBlocks.InitScript(rom, template)));
        if (init.Problem != null) throw new InvalidOperationException($"The start-up script of map {template:X2} can't be copied: {init.Problem}");
        if (blank) init = BlankInit(init);
        L2EventBlocks.WriteInitNew(rom, map, init);
        // treasure: the same items, each with an opened flag of its own
        rom.WriteUInt16Le(L2Chests.Table + map * 2, EmptyChestList);
        if (!blank)
        {
            var recs = new List<L2Chests.Rec>();
            foreach (var r in L2Chests.Records(rom, template))
            {
                int flag = L2Chests.FreeFlag(rom, recs.Select(x => x.Flag), map);
                if (flag < 0) throw new InvalidOperationException("All 512 treasure opened flags are used, so the copy's treasure can't get flags of its own.");
                recs.Add(r with { Flag = flag });
            }
            if (recs.Count > 0) L2Chests.WriteList(rom, map, recs);
        }
        // puzzles
        rom.WriteBytes(Entry(rom, L2MapTable.Puzzle, map), new byte[] { 0xFF, 0xFF, 0xFF });
        if (!blank)
        {
            var pb = L2PuzzleBlock.Read(rom, template);
            if (pb.Entries.Count > 0)
            {
                if (pb.Problem != null) throw new InvalidOperationException($"The puzzles of map {template:X2} can't be copied: {pb.Problem}");
                pb.For(map).Write(rom);
            }
        }
        // field monsters: the template's list (shared, as the game's maps share them)
        rom.WriteUInt16Le(Entry(rom, L2MapTable.Encounters, map), rom.ReadUInt16Le(Entry(rom, L2MapTable.Encounters, template)));
        rom.FixChecksum();
        return map;
    });

    /// <summary>Deletes a new map: the last one gives its number back at once; one in the middle becomes a small blank
    /// map marked free, which New map takes first (so the maps after it keep their numbers). Exits and events that
    /// still lead to it are left as they are (see <see cref="References"/>).</summary>
    public static void Delete(RomBuffer rom, int map) => L2Rom.AllOrNothing(rom, () =>
    {
        int b = Block(rom);
        if (b < 0 || map < GameCount || map >= Count(rom)) throw new InvalidOperationException("Only maps made with New map can be deleted.");
        int slot = map - GameCount, made = rom.ReadByte(b + 6), free = rom.ReadUInt16Le(b + 8) | 1 << slot;
        // trailing free numbers are given back
        while (made > 0 && (free >> (made - 1) & 1) != 0) { made--; free &= ~(1 << made); }
        rom.WriteByte(b + 6, (byte)made);
        rom.WriteUInt16Le(b + 8, (ushort)free);
        if (map < Count(rom))
        {
            // still counted: a small empty map of its own, so the pages that list every map read a real one
            var f = L2Rom.ReadFile(rom, FileOf(map));
            L2Rom.WriteFile(rom, FileOf(map), BlankFile(f, 16, 14, 0));
            rom.WriteUInt16Le(L2Chests.Table + map * 2, EmptyChestList);
            rom.WriteBytes(Entry(rom, L2MapTable.Puzzle, map), new byte[] { 0xFF, 0xFF, 0xFF });
            int infoAt = L2Rom.Allocate(rom, 0x80, upper: true);
            var info = BlankInfo(8, 7);
            rom.WriteBytes(infoAt, info.Append((byte)0).ToArray());
            int snes = L2Rom.FileToSnes(infoAt);
            rom.WriteBytes(Entry(rom, L2MapTable.Info, map), new[] { (byte)snes, (byte)(snes >> 8), (byte)(snes >> 16) });
            int ev = L2EventBlocks.HeaderValue(L2EventBlocks.NewBlock(rom));
            rom.WriteBytes(Entry(rom, L2MapTable.Header, map), new[] { (byte)ev, (byte)(ev >> 8), (byte)(ev >> 16) });
            L2EventBlocks.WriteInitNew(rom, map, L2EventScript.New(0));
        }
        rom.FixChecksum();
        return 0;
    });

    /// <summary>What still leads to a map: exits of other maps and events that send the party there (16, 35, A9).</summary>
    public static List<string> References(RomBuffer rom, int map, Func<int, string> name)
    {
        var refs = new List<string>();
        for (int m = 0; m < Count(rom); m++)
        {
            if (m == map || IsFree(rom, m)) continue;
            try
            {
                foreach (var e in L2MapInfo.Read(rom, m))
                    if (e.Kind == "Exit" && e.DestMap == map) refs.Add($"Exit {e.Id} of {m:X2} {name(m)}");
            }
            catch (Exception) { }
            try
            {
                int block = L2Events.Block(rom, m);
                foreach (var en in L2EventBlocks.ReadLists(rom, block).Where(e => e.IsScript))
                {
                    var s = L2EventScript.Read(rom, block + en.Offset, block);
                    if (s.Steps.Any(st => !st.IsText && st.Op is 0x16 or 0xA9 && st.Bytes.Length > 1 && st.Bytes[1] == map || !st.IsText && st.Op == 0x35 && st.Bytes.Length > 2 && st.Bytes[2] == map))
                        refs.Add($"Event {en.Id:X2} of {m:X2} {name(m)}");
                }
            }
            catch (Exception) { }
        }
        var start = L2MapEdit.NewGame(rom);
        if (start.Map == map) refs.Add("The new game (the test start puts the party on this map)");
        return refs.Distinct().ToList();
    }

    /// <summary>A map file with the template's head (both layers scrolling with the map): <paramref name="w"/> x <paramref name="h"/> blocks, the ground
    /// all <paramref name="fill"/>, the upper layer empty (block 0, no walls).</summary>
    public static byte[] BlankFile(byte[] template, int w, int h, int fill)
    {
        if (w is < 16 or > 96 || h is < 14 or > 88 || w * h > 7744)
            throw new InvalidOperationException("A map is 16 to 96 blocks wide and 14 to 88 high (7744 blocks at most, as the game's biggest).");
        var outp = new List<byte>(template.Take(6));
        int p = 6;
        for (int l = 0; l < 2; l++)
        {
            // the layer's scroll mode: 0, scrolls with the map (a template's ground layer may be a parallax background
            // far below, as on the game's mountains and towers, which draws a blank map out of line with its walls)
            outp.Add(0); outp.Add(0);
            outp.Add((byte)w); outp.Add((byte)h);
            if (p + 3 < template.Length) p += 4 + template[p + 2] * template[p + 3] * 2;
            int cell = l == 0 ? fill & 0x3FF : 0;
            for (int i = 0; i < w * h; i++) { outp.Add((byte)cell); outp.Add((byte)(cell >> 8 & 3)); }
        }
        return outp.ToArray();
    }

    /// <summary>A map info block with every list empty (each its own FF, so one can grow without the others) but for
    /// preset point 0 at (<paramref name="x"/>, <paramref name="y"/>).</summary>
    public static byte[] BlankInfo(int x, int y)
    {
        var d = new List<byte>(new byte[0x2C]);
        for (int list = 2; list < 0x2C; list += 2)
        {
            d[list] = (byte)d.Count; d[list + 1] = (byte)(d.Count >> 8);
            if (list == L2MapInfo.Points) d.AddRange(new byte[] { 0, (byte)x, (byte)y });
            d.Add(0xFF);
        }
        d[0] = (byte)d.Count; d[1] = (byte)(d.Count >> 8);
        return d.ToArray();
    }

    /// <summary>A blank map's start-up script: the template's map settings (69), battle background (74), where Escape
    /// leads (A9) and its first music (4B); no characters.</summary>
    public static L2EventScript BlankInit(L2EventScript template)
    {
        var s = L2EventScript.New(template.JumpBase);
        var keep = template.Steps.Where(st => !st.IsText && st.Op is 0x69 or 0x74 or 0xA9).ToList();
        if (template.Steps.FirstOrDefault(st => !st.IsText && st.Op == 0x4B) is { } song) keep.Add(song);
        int i = 0;
        foreach (var st in keep) s.Steps.Insert(i++, new L2Step { Bytes = (byte[])st.Bytes.Clone() });
        return s;
    }
}
