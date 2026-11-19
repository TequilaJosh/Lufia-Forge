namespace LufiaForge.Core.Lufia2;

// a table the game reads by map number (L2NewMaps)
public enum L2MapTable { BlockSet, TileSet, AnimIndex, PaletteSet, ColourAnim, Info, Header, Puzzle, Files, Encounters }

// new lufia 2 maps (US rom, from the decompiled code 2026-11-02). the game has 242 maps (00-F1) and uses every one,
// so a new map takes a number from F2 to FF (exits and scripts keep the map in one byte, the visited bits at $097B
// have room for 256). the game reads a map's data from these tables, each indexed by map number and each followed
// right away by other data, so none of them has room for more maps where it is:
// - the map file: file 0x3F + map, unpacked by $80:EAE7 (ADC #$003F at $80:EB24)
// - the file table $A7:8000 (3 bytes a file, $80:8EA9, $80:8EB1), no room for more files after it
// - the map header $87:8010 + map * 8 (event block, start-up script, name: $83:B5BA, $83:B5C7, $80:BEB9, $80:BEC6,
//   $80:C894, $82:F4A3)
// - the graphics lists $CF:F72E (block set, $80:EB8B), $CF:F81E (tile set, $80:F03B), $CF:F90E (animation,
//   $80:EB4B, $80:F217), $CF:FA08 (palette set, $80:F2D2), $CF:FBA6 (colour animation, $80:EFC8)
// - the map info pointers $CF:FCBC (3 bytes, $83:B60C, $83:B601, $86:9F4E, $86:9F44)
// - the puzzle blocks $8F:8000 (3 bytes, $80:E86D, $80:E88C)
// - the field monster lists $97:B9AC (u16 offsets from $97:B9AC). $81:837D reads the offset with DB = $97 as
//   LDA $B9AC,X + CLC, the same 4 bytes as LDA long,X (the carry is clear after the ASL of a map below 0x80)
// - the treasure lists $91:B8B5 (u16 offsets). the game left 0x1C bytes of FF after its 242 entries, room for maps
//   F2-FF, so that table stays (new maps start on the empty list at offset 0x201)
// a raw search of the whole rom finds no other reads of these tables. we copy them all into one block of the added
// space ("LF2MX", 256 entries each, the file table with 14 more files from NewFileBase) and point those reads at it.
// a 6-byte hook at $80:EB23 (CLC; ADC #$003F; STA $54 -> JSL; NOP; NOP) gives maps F2 and up the files
// NewFileBase + map - F2. the map's name stays a u16 from $87:8000 (the name readers add bank $87 themselves), so a
// new map starts with its template's name. two tables are left alone: $94:C998 (each map's treasure group, only read
// by the treasure counter item, which counts maps up to F0) and the Ancient Cave's (maps F0 and F1)
public static class L2NewMaps
{
    public const int GameCount = 0xF2, MaxCount = 0x100, Slots = MaxCount - GameCount;
    // file number of map F2's file (the game's files end at 0x2A8, the ids up to 0x2AF are left unused)
    public const int NewFileBase = 0x2B0;
    // treasure list offset of an empty list (map 01's, one FF)
    public const int EmptyChestList = 0x201;
    // the field monster list offset the game gives maps F0-F7 (no field monsters)
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

    // every long read of a map table: the opcode's file offset, how many bytes past the table start it reads (the info
    // pointer's bank, the header's fields), and the address the game reads
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

    // file offset of the table's entry 0 as the game reads it right now (its own table, or our copy)
    public static int Table(RomBuffer rom, L2MapTable table)
    {
        if (table == L2MapTable.Encounters)
            return rom.ReadByte(EncounterSite) == 0xBF ? L2Rom.SnesToFile((int)rom.ReadUInt24Le(EncounterSite + 1)) : L2Encounters.MapLists;
        var site = Sites.First(x => x.Table == table);
        int snesAddress = rom.ReadByte(site.At) == 0xBF ? (int)rom.ReadUInt24Le(site.At + 1) - site.Delta : site.Game;
        return L2Rom.SnesToFile(snesAddress);
    }

    // file offset of the map's entry in a table
    public static int Entry(RomBuffer rom, L2MapTable table, int map) => Table(rom, table) + map * Layout[table].Size;

    // the LF2MX block (file offset), or -1 if the game reads its own tables
    public static int Block(RomBuffer rom)
    {
        if (rom.ReadByte(Sites[0].At) != 0xBF) return -1;
        int blockAt = L2Rom.SnesToFile((int)rom.ReadUInt24Le(Sites[0].At + 1)) - BlockSetAt;
        if (blockAt < 0 || blockAt + BlockSize > rom.Length) return -1;
        return System.Text.Encoding.ASCII.GetString(rom.ReadBytes(blockAt, Magic.Length)) == Magic ? blockAt : -1;
    }

    public static bool IsInstalled(RomBuffer rom) => Block(rom) >= 0;

    internal const int TableBlockSize = BlockSize;

    // moves the LF2MX block to to (room for TableBlockSize bytes) and points every read of the tables, the encounter
    // read and the file hook at it (L2HighBanks)
    internal static void MoveBlock(RomBuffer rom, int to)
    {
        int block = Block(rom);
        if (block < 0) return;
        rom.WriteBytes(to, rom.ReadBytes(block, BlockSize));
        foreach (var site in Sites)
        {
            int snesAddress = L2Rom.FileToSnes(to + Layout[site.Table].At) + site.Delta;
            rom.WriteBytes(site.At + 1, new[] { (byte)snesAddress, (byte)(snesAddress >> 8), (byte)(snesAddress >> 16) });
        }
        int encounters = L2Rom.FileToSnes(to + EncountersAt);
        rom.WriteBytes(EncounterSite + 1, new[] { (byte)encounters, (byte)(encounters >> 8), (byte)(encounters >> 16) });
        int code = L2Rom.FileToSnes(to + CodeAt);
        rom.WriteBytes(HookSite + 1, new[] { (byte)code, (byte)(code >> 8), (byte)(code >> 16) });
    }

    // how many new map numbers are in use (made ones, deleted ones in the middle included)
    public static int Made(RomBuffer rom) => Block(rom) is var block and >= 0 ? rom.ReadByte(block + 6) : 0;

    // how many maps the rom has: the game's 242 plus the new ones
    public static int Count(RomBuffer rom) => GameCount + Made(rom);

    // true if the map is a new one (F2 up) that was deleted. it stays a small blank map until New map takes its
    // number again
    public static bool IsFree(RomBuffer rom, int map) =>
        map >= GameCount && map < Count(rom) && Block(rom) is var block and >= 0 && (rom.ReadUInt16Le(block + 8) >> (map - GameCount) & 1) != 0;

    // the packed file that holds the map's blocks
    public static int FileOf(int map) => map < GameCount ? L2Maps.MapFileBase + map : NewFileBase + map - GameCount;

    // the files of the new maps (for scans over every file)
    public static IEnumerable<int> NewFiles(RomBuffer rom) => Enumerable.Range(0, Made(rom)).Select(i => NewFileBase + i);

    // why new maps can't be made on this rom, or null
    public static string? CantInstall(RomBuffer rom)
    {
        if (IsInstalled(rom)) return null;
        if (rom.Length < 0x200000) return "This isn't a Lufia II ROM Lufia Forge knows.";
        foreach (var site in Sites)
        {
            int game = site.Game + site.Delta;
            if (rom.ReadByte(site.At) != 0xBF || rom.ReadUInt24Le(site.At + 1) != game)
                return $"Another hack changed how the game reads its map tables (at ${L2Rom.FileToSnes(site.At):X6}), so Lufia Forge can't add maps to it.";
        }
        if (!rom.ReadBytes(HookSite, HookOriginal.Length).SequenceEqual(HookOriginal) || !rom.ReadBytes(EncounterSite, EncounterOriginal.Length).SequenceEqual(EncounterOriginal))
            return "Another hack changed how the game loads maps, so Lufia Forge can't add maps to it.";
        for (int m = GameCount; m < MaxCount; m++)
            if (rom.ReadUInt16Le(L2Chests.Table + m * 2) != 0xFFFF)
                return "Another hack uses the room after the treasure table, so Lufia Forge can't add maps to it.";
        return null;
    }

    // copies the map tables into the LF2MX block and points the game at it (once, New map does it by itself).
    // the game plays like before: the 14 new entries are copies of map 02's, never used until a map is made
    public static void Install(RomBuffer rom)
    {
        if (IsInstalled(rom)) return;
        if (CantInstall(rom) is { } why) throw new InvalidOperationException(why);
        int block = L2Rom.Allocate(rom, BlockSize, upper: true);
        var data = new byte[BlockSize];
        System.Text.Encoding.ASCII.GetBytes(Magic).CopyTo(data, 0);
        data[5] = Version;
        foreach (var (table, (at, size)) in Layout)
        {
            int from = Table(rom, table);
            int count = table == L2MapTable.Files ? NewFileBase : GameCount;
            Array.Copy(rom.ReadBytes(from, count * size), 0, data, at, count * size);
            for (int i = 0; i < Slots; i++)
            {
                var filler = table switch
                {
                    L2MapTable.Puzzle => new byte[] { 0xFF, 0xFF, 0xFF },
                    L2MapTable.Encounters => new byte[] { NoMonsterList & 0xFF, NoMonsterList >> 8 },
                    L2MapTable.Files => rom.ReadBytes(from + L2Maps.MapFileBase * 3 + 2 * 3, 3),
                    _ => rom.ReadBytes(from + 2 * size, size),
                };
                filler.CopyTo(data, at + (count + i) * size);
            }
        }
        // the hook: maps F2 up use the files from NewFileBase (A = the map, 16 bits, $54 = the file to unpack)
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
        foreach (var site in Sites)
        {
            int snesAddress = L2Rom.FileToSnes(block + Layout[site.Table].At) + site.Delta;
            rom.WriteBytes(site.At + 1, new[] { (byte)snesAddress, (byte)(snesAddress >> 8), (byte)(snesAddress >> 16) });
        }
        int encounters = L2Rom.FileToSnes(block + EncountersAt);
        rom.WriteBytes(EncounterSite, new byte[] { 0xBF, (byte)encounters, (byte)(encounters >> 8), (byte)(encounters >> 16) });
        int code = L2Rom.FileToSnes(block + CodeAt);
        rom.WriteBytes(HookSite, new byte[] { 0x22, (byte)code, (byte)(code >> 8), (byte)(code >> 16), 0xEA, 0xEA });
        rom.FixChecksum();
    }

    // makes a new map from template and returns its number.
    // a copy has the template's blocks, graphics, map info (exits, characters, treasure spots, doors), its own copy of
    // the event block and start-up script, its treasure with new opened flags, a copy of its puzzles (they share the
    // template's puzzle switches) and its field monster list.
    // a blank map has the template's graphics, music and map settings, width x height blocks of fill on the ground and
    // nothing on the upper layer, no characters, exits, treasure or events, and preset point 0 in the middle
    public static int Create(RomBuffer rom, int template, bool blank, int width = 0, int height = 0, int fill = 0) => L2Rom.AllOrNothing(rom, () =>
    {
        if (template < 2 || template >= Count(rom)) throw new InvalidOperationException("Pick a town or dungeon map to start from (the world maps can't be copied).");
        Install(rom);
        int block = Block(rom);
        int made = rom.ReadByte(block + 6), free = rom.ReadUInt16Le(block + 8);
        int slot = Enumerable.Range(0, made).FirstOrDefault(i => (free >> i & 1) != 0, made);
        if (slot >= Slots) throw new InvalidOperationException($"All {Slots} new map numbers (F2-FF) are used. Delete a new map to make room.");
        int map = GameCount + slot;
        // take the number first, so the tables below are read and written for it
        if (slot == made) rom.WriteByte(block + 6, (byte)(made + 1));
        rom.WriteUInt16Le(block + 8, (ushort)(free & ~(1 << slot)));

        // blocks
        var templateFile = L2Rom.ReadFile(rom, FileOf(template));
        byte[] file = blank ? BlankFile(templateFile, width, height, fill) : templateFile;
        L2Rom.WriteNewFile(rom, FileOf(map), file);
        // graphics
        foreach (var table in new[] { L2MapTable.BlockSet, L2MapTable.TileSet, L2MapTable.AnimIndex, L2MapTable.PaletteSet, L2MapTable.ColourAnim })
            rom.WriteByte(Entry(rom, table, map), rom.ReadByte(Entry(rom, table, template)));
        // map info: always its own copy (the editor writes over an info block no other map shares)
        int w = file[8], h = file[9];
        byte[] info = blank ? BlankInfo(w / 2, h / 2) : rom.ReadBytes(L2MapInfo.Block(rom, template), rom.ReadUInt16Le(L2MapInfo.Block(rom, template)));
        int infoAt = L2Rom.Allocate(rom, info.Length + 1, upper: true);
        rom.WriteBytes(infoAt, info.Append((byte)0).ToArray());
        int infoSnes = L2Rom.FileToSnes(infoAt);
        rom.WriteBytes(Entry(rom, L2MapTable.Info, map), new[] { (byte)infoSnes, (byte)(infoSnes >> 8), (byte)(infoSnes >> 16) });
        // header: event block, start-up script (both its own), the template's name
        int header = Entry(rom, L2MapTable.Header, map), templateHeader = Entry(rom, L2MapTable.Header, template);
        rom.WriteBytes(header, rom.ReadBytes(templateHeader, 8));
        int events = blank ? L2EventBlocks.NewBlock(rom) : L2EventBlocks.CopyBlock(rom, L2Events.Block(rom, template));
        int eventsValue = L2EventBlocks.HeaderValue(events);
        rom.WriteBytes(header, new[] { (byte)eventsValue, (byte)(eventsValue >> 8), (byte)(eventsValue >> 16) });
        var init = L2EventScript.Read(rom, L2EventBlocks.InitScript(rom, template), L2EventBlocks.InitBase(L2EventBlocks.InitScript(rom, template)));
        if (init.Problem != null) throw new InvalidOperationException($"The start-up script of map {template:X2} can't be copied: {init.Problem}");
        if (blank) init = BlankInit(init);
        L2EventBlocks.WriteInitNew(rom, map, init);
        // treasure: the same items, each with an opened flag of its own
        rom.WriteUInt16Le(L2Chests.Table + map * 2, EmptyChestList);
        if (!blank)
        {
            var records = new List<L2Chests.Rec>();
            foreach (var r in L2Chests.Records(rom, template))
            {
                int flag = L2Chests.FreeFlag(rom, records.Select(x => x.Flag), map);
                if (flag < 0) throw new InvalidOperationException("All 512 treasure opened flags are used, so the copy's treasure can't get flags of its own.");
                records.Add(r with { Flag = flag });
            }
            if (records.Count > 0) L2Chests.WriteList(rom, map, records);
        }
        // puzzles
        rom.WriteBytes(Entry(rom, L2MapTable.Puzzle, map), new byte[] { 0xFF, 0xFF, 0xFF });
        if (!blank)
        {
            var puzzleBlock = L2PuzzleBlock.Read(rom, template);
            if (puzzleBlock.Entries.Count > 0)
            {
                if (puzzleBlock.Problem != null) throw new InvalidOperationException($"The puzzles of map {template:X2} can't be copied: {puzzleBlock.Problem}");
                puzzleBlock.For(map).Write(rom);
            }
        }
        // field monsters: the template's list (shared, like the game's maps share them)
        rom.WriteUInt16Le(Entry(rom, L2MapTable.Encounters, map), rom.ReadUInt16Le(Entry(rom, L2MapTable.Encounters, template)));
        rom.FixChecksum();
        return map;
    });

    // deletes a new map. the last one gives its number back right away, one in the middle becomes a small blank map
    // marked free, which New map takes first (so the maps after it keep their numbers). exits and events that still
    // lead to it are left as they are (see References)
    public static void Delete(RomBuffer rom, int map) => L2Rom.AllOrNothing(rom, () =>
    {
        int block = Block(rom);
        if (block < 0 || map < GameCount || map >= Count(rom)) throw new InvalidOperationException("Only maps made with New map can be deleted.");
        int slot = map - GameCount, made = rom.ReadByte(block + 6), free = rom.ReadUInt16Le(block + 8) | 1 << slot;
        // free numbers at the end get given back
        while (made > 0 && (free >> (made - 1) & 1) != 0) { made--; free &= ~(1 << made); }
        rom.WriteByte(block + 6, (byte)made);
        rom.WriteUInt16Le(block + 8, (ushort)free);
        if (map < Count(rom))
        {
            // still counted: make it a small empty map of its own, so the pages that list every map read a real one
            var oldFile = L2Rom.ReadFile(rom, FileOf(map));
            L2Rom.WriteFile(rom, FileOf(map), BlankFile(oldFile, 16, 14, 0));
            rom.WriteUInt16Le(L2Chests.Table + map * 2, EmptyChestList);
            rom.WriteBytes(Entry(rom, L2MapTable.Puzzle, map), new byte[] { 0xFF, 0xFF, 0xFF });
            int infoAt = L2Rom.Allocate(rom, 0x80, upper: true);
            var info = BlankInfo(8, 7);
            rom.WriteBytes(infoAt, info.Append((byte)0).ToArray());
            int infoSnes = L2Rom.FileToSnes(infoAt);
            rom.WriteBytes(Entry(rom, L2MapTable.Info, map), new[] { (byte)infoSnes, (byte)(infoSnes >> 8), (byte)(infoSnes >> 16) });
            int eventsValue = L2EventBlocks.HeaderValue(L2EventBlocks.NewBlock(rom));
            rom.WriteBytes(Entry(rom, L2MapTable.Header, map), new[] { (byte)eventsValue, (byte)(eventsValue >> 8), (byte)(eventsValue >> 16) });
            L2EventBlocks.WriteInitNew(rom, map, L2EventScript.New(0));
        }
        rom.FixChecksum();
        return 0;
    });

    // what still leads to a map: exits of other maps and events that send the party there (16, 35, A9)
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
                foreach (var entry in L2EventBlocks.ReadLists(rom, block).Where(e => e.IsScript))
                {
                    var script = L2EventScript.Read(rom, block + entry.Offset, block);
                    if (script.Steps.Any(st => !st.IsText && st.Op is 0x16 or 0xA9 && st.Bytes.Length > 1 && st.Bytes[1] == map || !st.IsText && st.Op == 0x35 && st.Bytes.Length > 2 && st.Bytes[2] == map))
                        refs.Add($"Event {entry.Id:X2} of {m:X2} {name(m)}");
                }
            }
            catch (Exception) { }
        }
        var start = L2MapEdit.NewGame(rom);
        if (start.Map == map) refs.Add("The new game (the test start puts the party on this map)");
        return refs.Distinct().ToList();
    }

    // a map file with the template's head (both layers scrolling with the map): w x h blocks, the ground all fill,
    // the upper layer empty (block 0, no walls)
    public static byte[] BlankFile(byte[] template, int w, int h, int fill)
    {
        if (w is < 16 or > 96 || h is < 14 or > 88 || w * h > 7744)
            throw new InvalidOperationException("A map is 16 to 96 blocks wide and 14 to 88 high (7744 blocks at most, as the game's biggest).");
        var output = new List<byte>(template.Take(6));
        int offset = 6;
        for (int l = 0; l < 2; l++)
        {
            // the layer's scroll mode is 0, scrolls with the map. a template's ground layer may be a parallax background
            // far below (like on the game's mountains and towers), which draws a blank map out of line with its walls
            output.Add(0); output.Add(0);
            output.Add((byte)w); output.Add((byte)h);
            if (offset + 3 < template.Length) offset += 4 + template[offset + 2] * template[offset + 3] * 2;
            int cell = l == 0 ? fill & 0x3FF : 0;
            for (int i = 0; i < w * h; i++) { output.Add((byte)cell); output.Add((byte)(cell >> 8 & 3)); }
        }
        return output.ToArray();
    }

    // a map info block with every list empty (each with its own FF, so one can grow without the others) except
    // preset point 0 at (x, y)
    public static byte[] BlankInfo(int x, int y)
    {
        var data = new List<byte>(new byte[0x2C]);
        for (int list = 2; list < 0x2C; list += 2)
        {
            data[list] = (byte)data.Count; data[list + 1] = (byte)(data.Count >> 8);
            if (list == L2MapInfo.Points) data.AddRange(new byte[] { 0, (byte)x, (byte)y });
            data.Add(0xFF);
        }
        data[0] = (byte)data.Count; data[1] = (byte)(data.Count >> 8);
        return data.ToArray();
    }

    // a blank map's start-up script: the template's map settings (69), battle background (74), where Escape leads
    // (A9) and its first music (4B). no characters
    public static L2EventScript BlankInit(L2EventScript template)
    {
        var script = L2EventScript.New(template.JumpBase);
        var keep = template.Steps.Where(st => !st.IsText && st.Op is 0x69 or 0x74 or 0xA9).ToList();
        if (template.Steps.FirstOrDefault(st => !st.IsText && st.Op == 0x4B) is { } song) keep.Add(song);
        int i = 0;
        foreach (var step in keep) script.Steps.Insert(i++, new L2Step { Bytes = (byte[])step.Bytes.Clone() });
        return script;
    }
}
