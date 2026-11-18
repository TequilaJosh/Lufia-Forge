namespace LufiaForge.Core.Lufia2;

// lufia 2's battle animations (spells, IP attacks, weapons, monsters): the scripts the animation engine runs
// ($81:895E, opcode table $81:8C5C), the graphics and colours they load, their timing, and new animations
// (docs/lufia2/spell-animations.md)
// - animation n starts at $95:[$95:8000 + 2n] (0 = none). every script of an animation is in the same bank ($C5, set
//   to $95 at $81:89A4) and points at the others with 16-bit addresses
// - tracks (30, $7E:4F8F, 0x2C bytes) and objects (64, $7E:54B7, 0x2D bytes, one hardware sprite each) run scripts.
//   01 waits (the step length set by 02), 00 ends
// - 20 b w w: graphics. index w -> file ($85:EF53) unpacked to $7E:A000 and sent to VRAM $4000 (the sprites' 256
//   tiles), the second to $7E:DF00 -> VRAM $1800 (a background map), index 36 = none. 27 w a: file of index w to $7E:a.
//   DD / DE / DF w: 16 colours from $96:8000 + 32w into colours $10, $90 (sprite palette 1), $A0 (palette 2)
// new animations ("LF2AN", a bank of the added space): four hooks let an animation's scripts live in another bank,
// graphics indexes from $40 name packed data we keep, and colour rows from $200 come from that bank
public static class L2SpellAnims
{
    public const int Table = 0x958000, GfxIndex = 0x85EF53, GfxIndexCount = 0x40, PaletteBase = 0x968000;
    public const int GameBank = 0x95, FirstNewGfx = 0x40, FirstNewPalette = 0x200, NoGraphics = 0x36;

    // argument bytes of each opcode ('-' = not an opcode, a BRK or a dead loop in the game)
    private const string Lengths =
        "0012204-4251222E1111----0000----" +  // 00
        "5210---43---500-22-22-----------" +  // 20
        "----------------6220202--1006611" +  // 40
        "211102110201----101-------------" +  // 60
        "114223-224----5002--3462111111--" +  // 80
        "--------------------------------" +  // A0
        "331-------------2232222233100222" +  // C0
        "000000000230110000--------------";   // E0

    // op 0F's table: one script address per character, picked by the attacker's character number ($81:94CA: track
    // +$25 = the attacker's party slot, $153D + slot = its character: Maxim 0, Selan 1, Guy 2, Artea 3, Tia 4, Dekar 5,
    // Lexis 6. a monster or capsule monster there stops the game at a BRK)
    public const int CharacterTable = 7;
    public static readonly string[] Characters = { "Maxim", "Selan", "Guy", "Artea", "Tia", "Dekar", "Lexis" };

    public static int ArgCount(int op) => Lengths[op] == '-' ? -1 : Convert.ToInt32(Lengths[op].ToString(), 16);
    // opcodes whose first word is the address of another script (goto, call, if, start a track or object). 0F's
    // arguments are all script addresses (Targets)
    public static bool HasScriptPointer(int op) => op is 0x03 or 0x04 or 0x06 or 0x08 or 0x09 or 0x0A or 0x0C or 0x0D or 0x0E or 0x56 or 0x8E or 0x96
        or (>= 0xD0 and <= 0xD9) or 0xE9 or 0xEA;
    private static bool Ends(int op) => op is 0x00 or 0x03 or 0x05 or 0x0F;

    public sealed record Step(int Address, int Op, byte[] Args, int File)
    {
        public int Word(int i) => Args[i] | Args[i + 1] << 8;
    }

    // the script addresses a step names: the first word of a goto, call, if or start, and every word of 0F's table
    public static IEnumerable<int> Targets(Step st)
    {
        if (st.Op == 0x0F) { for (int i = 0; i < CharacterTable; i++) yield return st.Word(i * 2); }
        else if (HasScriptPointer(st.Op)) yield return st.Word(0);
    }

    public sealed class Script
    {
        public int Id, Bank, Start;
        public List<Step> Steps = new();
        public string? Problem;
    }

    // where things are

    private const int Magic = 0, CountAt = 6, NextAt = 8, GfxCountAt = 0x0A, PalCountAt = 0x0C, CodeAt = 0x10,
        WordsAt = 0x100, BanksAt = 0x300, GfxAt = 0x500, NamesAt = 0x740, NameLen = 16, DataAt = 0x1800, PalettesAt = 0x4000, PalettesEnd = 0x8000;
    private static readonly byte[] MagicBytes = "LF2AN"u8.ToArray();

    private static int F(int snes) => L2Rom.SnesToFile(snes);

    // file offset of our animation bank, or -1
    public static int Block(RomBuffer rom)
    {
        int site = F(SiteStart);
        if (rom.ReadByte(site) != 0x22) return -1;
        int code = F(rom.ReadByte(site + 1) | rom.ReadByte(site + 2) << 8 | rom.ReadByte(site + 3) << 16);
        int block = code - CodeAt;
        return block >= 0 && block + 0x8000 <= rom.Length && rom.ReadBytes(block, 5).SequenceEqual(MagicBytes) ? block : -1;
    }
    public static bool IsInstalled(RomBuffer rom) => Block(rom) >= 0;

    // animation n's first script (bank, address), or null
    public static (int Bank, int Address)? Start(RomBuffer rom, int n)
    {
        if (n is < 0 or > 0xFF) return null;
        int block = Block(rom);
        if (block >= 0 && rom.ReadByte(block + BanksAt + n * 2) is var bank and > 0)
            return (bank, rom.ReadUInt16Le(block + WordsAt + n * 2));
        int address = rom.ReadUInt16Le(F(Table) + n * 2);
        return address == 0 ? null : (GameBank, address);
    }

    public static bool Exists(RomBuffer rom, int n) => n is >= 0 and < 0xFF && Start(rom, n) != null;
    public static bool IsNew(RomBuffer rom, int n) => Block(rom) is var block && block >= 0 && n is >= 0 and < 0x100 && rom.ReadByte(block + BanksAt + n * 2) > 0;

    public static string Name(RomBuffer rom, int n)
    {
        int block = Block(rom);
        if (block < 0 || !IsNew(rom, n)) return "";
        var bytes = rom.ReadBytes(block + NamesAt + n * NameLen, NameLen);
        int length = Array.IndexOf(bytes, (byte)0); if (length < 0) length = NameLen;
        return System.Text.Encoding.ASCII.GetString(bytes, 0, length);
    }

    public static void SetName(RomBuffer rom, int n, string name)
    {
        int block = Block(rom);
        if (block < 0 || !IsNew(rom, n)) return;
        var bytes = new byte[NameLen];
        var cleaned = new string(name.Where(c => c >= ' ' && c < 127).Take(NameLen).ToArray());
        System.Text.Encoding.ASCII.GetBytes(cleaned).CopyTo(bytes, 0);
        rom.WriteBytes(block + NamesAt + n * NameLen, bytes);
    }

    // reading scripts

    // every step of animation n, following its gotos, calls and the tracks and objects it starts
    public static Script Parse(RomBuffer rom, int n)
    {
        var script = new Script { Id = n };
        if (Start(rom, n) is not var (bank, start)) { script.Problem = "No such animation."; return script; }
        script.Bank = bank; script.Start = start;
        var seen = new Dictionary<int, Step>();
        var work = new Stack<int>();
        work.Push(start);
        while (work.Count > 0 && seen.Count < 20000)
        {
            int address = work.Pop();
            while (!seen.ContainsKey(address))
            {
                if (address < 0x8000) { script.Problem = $"A script points at ${bank:X2}:{address:X4}, outside the ROM's half of the bank."; break; }
                int file = F(bank << 16 | address);
                int op = rom.ReadByte(file), argCount = ArgCount(op);
                if (argCount < 0) { script.Problem = $"Opcode {op:X2} at ${bank:X2}:{address:X4} isn't one the game can run."; break; }
                var step = new Step(address, op, rom.ReadBytes(file + 1, argCount), file);
                seen[address] = step;
                foreach (int target in Targets(step)) work.Push(target);
                if (Ends(op)) break;
                address = (address + 1 + argCount) & 0xFFFF;
            }
        }
        script.Steps = seen.Values.OrderBy(x => x.Address).ToList();
        // a script that jumps into the middle of another step would read the same bytes two ways, so it isn't copied
        for (int i = 1; i < script.Steps.Count; i++)
            if (script.Steps[i - 1].Address + 1 + script.Steps[i - 1].Args.Length > script.Steps[i].Address)
            { script.Problem ??= $"Two steps share bytes at ${bank:X2}:{script.Steps[i].Address:X4}, so it can't be copied."; break; }
        return script;
    }

    // a list of the steps to read: address, opcode, arguments
    public static string Listing(Script s) => string.Join(Environment.NewLine, s.Steps.Select(st =>
        $"{s.Bank:X2}:{st.Address:X4}  {st.Op:X2} {Convert.ToHexString(st.Args)}  {Describe(st)}"));

    public static string Describe(Step st) => st.Op switch
    {
        0x00 => "end",
        0x01 => "wait one step",
        0x02 => $"steps last {st.Args[0]} frames",
        >= 0xE0 and <= 0xE8 => $"steps last {ShortLengths[st.Op - 0xE0]} frames",
        0x03 => $"go to {st.Word(0):X4}",
        0x04 => $"call {st.Word(0):X4}",
        0x05 => "return",
        0x0F => "go to the attacker's character's script: " + string.Join(", ", Enumerable.Range(0, CharacterTable).Select(i => $"{Characters[i]} {st.Word(i * 2):X4}")),
        0x06 => $"if value {st.Args[3]:X2} at +{st.Args[2]:X2}: go to {st.Word(0):X4}",
        0x08 or 0x09 or 0x0C or 0x0D or 0x0E => $"start a track at {st.Word(0):X4}",
        0x0A or 0x8E or 0x96 or (>= 0xD0 and <= 0xD9) => $"start an object at {st.Word(0):X4}",
        0x56 or 0xE9 or 0xEA => $"start a track at {st.Word(0):X4}",
        0x10 or 0x11 or 0x12 or 0x13 => $"repeat {st.Args[0]} times (loop {st.Op - 0x10 + 1})",
        0x18 or 0x19 or 0x1A or 0x1B => $"end of loop {st.Op - 0x18 + 1}",
        0x20 => $"graphics {st.Word(1):X2}, background map {st.Word(3):X2}",
        0x27 => $"unpack graphics {st.Word(0):X2} to $7E:{st.Word(2):X4}",
        0x2C => $"colours row {st.Args[0]:X}, from {st.Args[1]:X}, {st.Args[2]} colours: palette {st.Word(3):X}",
        0x72 => $"sound {st.Args[0]:X2}",
        0x89 => $"sprite: tile {st.Args[1]:X2}, attributes {st.Args[0]:X2}, size {st.Args[3]}",
        0xDD => $"colours {st.Word(0):X} to background palette 1",
        0xDE => $"colours {st.Word(0):X} to sprite palette 1",
        0xDF => $"colours {st.Word(0):X} to sprite palette 2",
        _ => "",
    };

    // graphics and colours

    // a graphics index an animation loads (op 20's sprites or map, op 27)
    public sealed record GfxRef(Step Step, int ArgOffset, int Index, string What);

    public static List<GfxRef> Graphics(Script s)
    {
        var refs = new List<GfxRef>();
        foreach (var st in s.Steps)
        {
            if (st.Op == 0x20) { refs.Add(new GfxRef(st, 1, st.Word(1), "sprites")); refs.Add(new GfxRef(st, 3, st.Word(3), "background map")); }
            else if (st.Op == 0x27) refs.Add(new GfxRef(st, 0, st.Word(0), $"to $7E:{st.Word(2):X4}"));
        }
        return refs.Where(g => !IsNone(g.Index)).ToList();
    }

    private static bool IsNone(int index) => index == NoGraphics;

    // the packed file a game index names (0 = none), from the table at $85:EF53
    public static int GameFile(RomBuffer rom, int index) => index is >= 0 and < GfxIndexCount ? rom.ReadUInt16Le(F(GfxIndex) + index * 2) : 0;

    // a graphics index unpacked (null if it names nothing)
    public static byte[]? ReadGraphics(RomBuffer rom, int index)
    {
        if (index < GfxIndexCount) { int file = GameFile(rom, index); return file == 0 ? null : L2Rom.ReadFile(rom, file); }
        int at = GfxOffset(rom, index);
        return at < 0 ? null : LufiaCompression.Decompress(rom, at, out _);
    }

    private static int GfxOffset(RomBuffer rom, int index)
    {
        int block = Block(rom), newIndex = index - FirstNewGfx;
        if (block < 0 || newIndex < 0 || newIndex >= rom.ReadByte(block + GfxCountAt)) return -1;
        int snesAddress = (int)rom.ReadUInt24Le(block + GfxAt + newIndex * 3);
        return snesAddress == 0 ? -1 : F(snesAddress);
    }

    public static void WriteGraphics(RomBuffer rom, int index, byte[] data)
    {
        if (index < GfxIndexCount)
        {
            int file = GameFile(rom, index);
            if (file == 0) throw new InvalidOperationException("That graphics index names no file.");
            L2Rom.WriteFile(rom, file, data);
            return;
        }
        int at = GfxOffset(rom, index);
        if (at < 0) throw new InvalidOperationException("That graphics index isn't one of Lufia Forge's.");
        var packed = LufiaCompression.Compress(data);
        LufiaCompression.Decompress(rom, at, out int oldSize);
        if (packed.Length > oldSize) at = L2Rom.Allocate(rom, packed.Length);
        rom.WriteBytes(at, packed);
        int block = Block(rom), newIndex = index - FirstNewGfx, snesAddress = L2Rom.FileToSnes(at);
        rom.WriteBytes(block + GfxAt + newIndex * 3, new[] { (byte)snesAddress, (byte)(snesAddress >> 8), (byte)(snesAddress >> 16) });
    }

    // adds graphics of our own (packed into the added space). returns their index ($40 up)
    public static int AddGraphics(RomBuffer rom, byte[] data)
    {
        int block = Block(rom), newIndex = rom.ReadByte(block + GfxCountAt);
        if (newIndex >= 0xC0) throw new InvalidOperationException("No graphics indexes left (192 new ones at most).");
        var packed = LufiaCompression.Compress(data);
        int at = L2Rom.Allocate(rom, packed.Length);
        rom.WriteBytes(at, packed);
        int snesAddress = L2Rom.FileToSnes(at);
        block = Block(rom);
        rom.WriteBytes(block + GfxAt + newIndex * 3, new[] { (byte)snesAddress, (byte)(snesAddress >> 8), (byte)(snesAddress >> 16) });
        rom.WriteByte(block + GfxCountAt, (byte)(newIndex + 1));
        return FirstNewGfx + newIndex;
    }

    // a colour row an animation loads (DD / DE / DF w, or 2C)
    public sealed record PaletteRef(Step Step, int ArgOffset, int Index, string What);

    public static List<PaletteRef> Palettes(Script s)
    {
        var refs = new List<PaletteRef>();
        foreach (var st in s.Steps)
        {
            if (st.Op == 0xDE) refs.Add(new PaletteRef(st, 0, st.Word(0), "sprite palette 1"));
            else if (st.Op == 0xDF) refs.Add(new PaletteRef(st, 0, st.Word(0), "sprite palette 2"));
            else if (st.Op == 0xDD) refs.Add(new PaletteRef(st, 0, st.Word(0), "background palette 1"));
            else if (st.Op == 0x2C) refs.Add(new PaletteRef(st, 3, st.Word(3), $"colours {st.Args[0]:X}{st.Args[1]:X}"));
        }
        return refs;
    }

    // file offset of colour row w (16 colours, BGR555)
    public static int PaletteOffset(RomBuffer rom, int w)
    {
        if (w < FirstNewPalette) return F(PaletteBase + w * 32);
        int block = Block(rom);
        return block < 0 ? -1 : block + PalettesAt + (w - FirstNewPalette) * 32;
    }

    public static int[] ReadPalette(RomBuffer rom, int w)
    {
        int rowAt = PaletteOffset(rom, w);
        return Enumerable.Range(0, 16).Select(i => rowAt < 0 ? 0 : rom.ReadUInt16Le(rowAt + i * 2) & 0x7FFF).ToArray();
    }

    public static void SetColour(RomBuffer rom, int w, int colour, int bgr555)
    {
        int rowAt = PaletteOffset(rom, w);
        if (rowAt >= 0) rom.WriteUInt16Le(rowAt + colour * 2, (ushort)(bgr555 & 0x7FFF));
    }

    public static int AddPalette(RomBuffer rom, int[] colours)
    {
        int block = Block(rom), rowCount = rom.ReadUInt16Le(block + PalCountAt);
        if (PalettesAt + (rowCount + 1) * 32 > PalettesEnd) throw new InvalidOperationException("No room for more colour rows.");
        for (int i = 0; i < 16; i++) rom.WriteUInt16Le(block + PalettesAt + rowCount * 32 + i * 2, (ushort)(colours[i] & 0x7FFF));
        rom.WriteUInt16Le(block + PalCountAt, (ushort)(rowCount + 1));
        return FirstNewPalette + rowCount;
    }

    // timing

    // the step lengths E0-E8 set (in frames)
    public static readonly int[] ShortLengths = { 1, 2, 3, 4, 5, 6, 8, 10, 20 };

    // the steps that set how many frames each later "wait" lasts: 02 n, or E0-E8 (1, 2, 3, 4, 5, 6, 8, 10, 20)
    public static List<Step> Timings(Script s) => s.Steps.Where(st => st.Op == 0x02 || st.Op is >= 0xE0 and <= 0xE8).ToList();

    public static int TimingFrames(Step st) => st.Op == 0x02 ? st.Args[0] : ShortLengths[st.Op - 0xE0];

    // sets a step length. 02 n takes 1-255, E0-E8 (one byte, no room for a number) get the nearest of their nine
    public static void SetTiming(RomBuffer rom, Step st, int frames)
    {
        if (st.Op == 0x02) { rom.WriteByte(st.File + 1, (byte)Math.Clamp(frames, 1, 255)); return; }
        if (st.Op is < 0xE0 or > 0xE8) throw new ArgumentException("Not a timing step.");
        int best = 0;
        for (int k = 1; k < ShortLengths.Length; k++) if (Math.Abs(ShortLengths[k] - frames) < Math.Abs(ShortLengths[best] - frames)) best = k;
        rom.WriteByte(st.File, (byte)(0xE0 + best));
    }

    // the other animations that run any of these steps (they change too)
    public static List<int> SharedWith(RomBuffer rom, Script s, IEnumerable<int> files)
    {
        var mine = files.ToHashSet();
        var list = new List<int>();
        for (int n = 0; n < 0xFF; n++)
        {
            if (n == s.Id || !Exists(rom, n)) continue;
            var other = Parse(rom, n);
            if (other.Steps.Any(st => mine.Contains(st.File))) list.Add(n);
        }
        return list;
    }

    // the other animations that load graphics index index
    public static List<int> GraphicsUsers(RomBuffer rom, int index, int except = -1) =>
        Enumerable.Range(0, 0xFF).Where(n => n != except && Exists(rom, n) && Graphics(Parse(rom, n)).Any(g => g.Index == index)).ToList();

    public static List<int> PaletteUsers(RomBuffer rom, int w, int except = -1) =>
        Enumerable.Range(0, 0xFF).Where(n => n != except && Exists(rom, n) && Palettes(Parse(rom, n)).Any(g => g.Index == w)).ToList();

    // the hooks

    private const int SiteStart = 0x81899A, SiteBank = 0x8189A4, SitePal = 0x81913E;
    private static readonly int[] SitesIdx = { 0x8195CF, 0x81960D, 0x81966E }, SitesLoad = { 0x8195E6, 0x819624, 0x819684 };
    private static readonly byte[] StartOriginal = { 0xBF, 0x00, 0x80, 0x95, 0x8F, 0x92, 0x4F, 0x7E }, BankOriginal = { 0xA9, 0x95, 0x85, 0xC5 },
        IdxOriginal = { 0xBF, 0x53, 0xEF, 0x85 }, LoadOriginal = { 0x22, 0x9D, 0x8E, 0x80 }, PalOriginal = { 0xA9, 0x96, 0x85, 0x0A };

    public static bool CanInstall(RomBuffer rom, out string why)
    {
        why = "";
        if (IsInstalled(rom)) return true;
        bool Is(int snes, byte[] expected) => rom.ReadBytes(F(snes), expected.Length).SequenceEqual(expected);
        var bad = new List<string>();
        if (!Is(SiteStart, StartOriginal) || !Is(SiteBank, BankOriginal)) bad.Add("where an animation starts ($81:899A)");
        if (SitesIdx.Any(site => !Is(site, IdxOriginal)) || SitesLoad.Any(site => !Is(site, LoadOriginal))) bad.Add("how its graphics are loaded ($81:95CF)");
        if (!Is(SitePal, PalOriginal)) bad.Add("how its colours are loaded ($81:913E)");
        if (bad.Count == 0) return true;
        why = "Another hack changed the game's battle animation code (" + string.Join(", ", bad) + "), so new animations can't be added safely.";
        return false;
    }

    // puts the hooks and the animation bank in (just once)
    public static void Install(RomBuffer rom)
    {
        if (IsInstalled(rom)) return;
        if (!CanInstall(rom, out var why)) throw new InvalidOperationException(why);
        int blockAt = L2Rom.Allocate(rom, 0x8000);
        int blockSnes = L2Rom.FileToSnes(blockAt), bank = blockSnes >> 16, lo = blockSnes & 0xFFFF;
        var block = new byte[0x8000];
        MagicBytes.CopyTo(block, Magic);
        block[5] = 1;
        block[NextAt] = DataAt & 0xFF; block[NextAt + 1] = DataAt >> 8;
        var code = new List<byte>();
        int Here() => lo + CodeAt + code.Count;
        void Long(int address) { code.Add((byte)address); code.Add((byte)(address >> 8)); code.Add((byte)(address >> 16)); }
        // AnimStart (A16 X16, X = id * 2): script address to $7E:4F92 and its bank to $C5
        int animStart = Here();
        code.Add(0xBF); Long(blockSnes + BanksAt);            // LDA banks,X
        code.AddRange(new byte[] { 0x29, 0xFF, 0x00, 0xF0, 0x0F, 0xE2, 0x20, 0x85, 0xC5, 0xC2, 0x20 });
        code.Add(0xBF); Long(blockSnes + WordsAt);            // LDA words,X
        code.AddRange(new byte[] { 0x8F, 0x92, 0x4F, 0x7E, 0x6B });
        code.AddRange(new byte[] { 0xE2, 0x20, 0xA9, 0x95, 0x85, 0xC5, 0xC2, 0x20, 0xBF, 0x00, 0x80, 0x95, 0x8F, 0x92, 0x4F, 0x7E, 0x6B });
        // GfxIdx (A16 X16, X = index * 2): the game's table below $40, otherwise $8000 | index
        int gfxIdx = Here();
        code.AddRange(new byte[] { 0xE0, 0x80, 0x00, 0xB0, 0x05, 0xBF, 0x53, 0xEF, 0x85, 0x6B, 0x8A, 0x4A, 0x09, 0x00, 0x80, 0x6B });
        // GfxLoad (m1 x0, $54 = file or $8000 | index): the game's unpacker, or our data through it ($80:8EBD)
        int gfxLoad = Here();
        code.AddRange(new byte[] { 0xA5, 0x55, 0x30, 0x04, 0x5C, 0x9D, 0x8E, 0x80,
            0x08, 0xC2, 0x30, 0x8B, 0xA5, 0x54, 0x29, 0xFF, 0x7F, 0x38, 0xE9, 0x40, 0x00, 0x85, 0x5D, 0x0A, 0x65, 0x5D, 0xAA, 0x64, 0x5D });
        code.Add(0xBF); Long(blockSnes + GfxAt);
        code.AddRange(new byte[] { 0x09, 0x00, 0x80, 0xA8, 0xE2, 0x20 });
        code.Add(0xBF); Long(blockSnes + GfxAt + 2);
        code.AddRange(new byte[] { 0x85, 0x5F, 0x5C, 0xBD, 0x8E, 0x80 });
        // PalHook (m1): colour rows from $C000 of the address ($200 up) are in this bank
        int palHook = Here();
        code.AddRange(new byte[] { 0xA5, 0x09, 0xC9, 0xC0, 0xB0, 0x05, 0xA9, 0x96, 0x85, 0x0A, 0x6B, 0xA9, (byte)bank, 0x85, 0x0A, 0x6B });
        if (CodeAt + code.Count > WordsAt) throw new InvalidOperationException("Hook code too long.");
        code.ToArray().CopyTo(block, CodeAt);
        rom.WriteBytes(blockAt, block);
        void Jsl(int site, int target, int pad)
        {
            var bytes = new List<byte> { 0x22, (byte)target, (byte)(target >> 8), (byte)bank };
            for (int i = 0; i < pad; i++) bytes.Add(0xEA);
            rom.WriteBytes(F(site), bytes.ToArray());
        }
        Jsl(SiteStart, animStart, 4);
        rom.WriteBytes(F(SiteBank), new byte[] { 0xEA, 0xEA, 0xEA, 0xEA });
        foreach (int site in SitesIdx) Jsl(site, gfxIdx, 0);
        foreach (int site in SitesLoad) Jsl(site, gfxLoad, 0);
        Jsl(SitePal, palHook, 0);
        rom.FixChecksum();
    }

    // new animations

    // an animation number the game leaves free: no script, and no spell, IP attack or item names it (spells 25-27,
    // Escape and two others point at 37-39, which have no script, so their cast shows nothing). -1 if there's none
    public static int FreeId(RomBuffer rom)
    {
        var used = new HashSet<int>();
        foreach (var spell in L2Battle.Spells(rom)) used.Add(L2Magic.SpellAnimation(rom, spell.Index));
        foreach (var ip in L2Battle.IpAttacks(rom)) used.Add(ip.Animation);
        foreach (var item in L2Battle.Items(rom))
            if ((item.Values.TryGetValue(0x2000, out var word) ? word : L2AiScripts.ItemAddedWords(rom, item.Index).GetValueOrDefault(0x2000)) is var animPlusOne and > 0) used.Add(animPlusOne - 1);
        for (int n = 0x37; n < 0xFF; n++)
        {
            if (n is >= 0xE0 and <= 0xF1) continue;
            if (!Exists(rom, n) && !used.Contains(n)) return n;
        }
        return -1;
    }

    // bytes of the animation bank still free for scripts
    public static int Room(RomBuffer rom) { int block = Block(rom); return block < 0 ? PalettesAt - DataAt : PalettesAt - rom.ReadUInt16Le(block + NextAt); }

    // a new animation: a copy of animation from (its scripts, and its own copies of the graphics and colour rows they
    // load, so changing it changes nothing else), with the first free number. returns that number
    public static int Copy(RomBuffer rom, int from, string name = "")
    {
        var script = Parse(rom, from);
        if (script.Problem != null) throw new InvalidOperationException($"Animation {from:X2} can't be copied: {script.Problem}");
        Install(rom);
        int id = FreeId(rom);
        if (id < 0) throw new InvalidOperationException("No animation numbers left.");
        // runs of steps that follow each other, laid out one after another
        var runs = new List<List<Step>>();
        foreach (var st in script.Steps)
        {
            if (runs.Count > 0 && runs[^1][^1] is var last && ((last.Address + 1 + last.Args.Length) & 0xFFFF) == st.Address) runs[^1].Add(st);
            else runs.Add(new List<Step> { st });
        }
        int total = script.Steps.Sum(st => 1 + st.Args.Length);
        int block = Block(rom), next = rom.ReadUInt16Le(block + NextAt);
        if (next + total > PalettesAt) throw new InvalidOperationException($"No room left for the animation's scripts ({total} bytes, {PalettesAt - next} free).");
        int bankSnes = L2Rom.FileToSnes(block), bank = bankSnes >> 16, baseAddr = bankSnes & 0xFFFF;
        var newAddress = new Dictionary<int, int>();
        int at = next;
        foreach (var run in runs)
            foreach (var st in run) { newAddress[st.Address] = baseAddr + at; at += 1 + st.Args.Length; }
        // own copies of graphics and colours
        var gfxMap = new Dictionary<int, int>();
        foreach (var gfx in Graphics(script))
            if (!gfxMap.ContainsKey(gfx.Index) && ReadGraphics(rom, gfx.Index) is { } data) gfxMap[gfx.Index] = AddGraphics(rom, data);
        var palMap = new Dictionary<int, int>();
        foreach (var palette in Palettes(script))
            if (palette.Step.Op != 0x2C && !palMap.ContainsKey(palette.Index)) palMap[palette.Index] = AddPalette(rom, ReadPalette(rom, palette.Index));
        block = Block(rom);
        foreach (var st in script.Steps)
        {
            var args = (byte[])st.Args.Clone();
            if (st.Op == 0x0F)
                for (int i = 0; i < CharacterTable * 2; i += 2) { int word = newAddress[st.Word(i)]; args[i] = (byte)word; args[i + 1] = (byte)(word >> 8); }
            else if (HasScriptPointer(st.Op)) { int word = newAddress[st.Word(0)]; args[0] = (byte)word; args[1] = (byte)(word >> 8); }
            void Swap(int offset, Dictionary<int, int> map) { int word = args[offset] | args[offset + 1] << 8; if (map.TryGetValue(word, out int newWord)) { args[offset] = (byte)newWord; args[offset + 1] = (byte)(newWord >> 8); } }
            if (st.Op == 0x20) { Swap(1, gfxMap); Swap(3, gfxMap); }
            if (st.Op == 0x27) Swap(0, gfxMap);
            if (st.Op is 0xDD or 0xDE or 0xDF) Swap(0, palMap);
            int stepAt = block + (newAddress[st.Address] - baseAddr);
            rom.WriteByte(stepAt, (byte)st.Op);
            rom.WriteBytes(stepAt + 1, args);
        }
        rom.WriteUInt16Le(block + NextAt, (ushort)(next + total));
        rom.WriteUInt16Le(block + WordsAt + id * 2, (ushort)newAddress[script.Start]);
        rom.WriteUInt16Le(block + BanksAt + id * 2, (ushort)bank);
        rom.WriteByte(block + CountAt, (byte)(rom.ReadByte(block + CountAt) + 1));
        SetName(rom, id, string.IsNullOrWhiteSpace(name) ? $"Copy of {from:X2}" : name);
        rom.FixChecksum();
        return id;
    }

    // sheets (the sprites' tiles)

    // 4bpp tiles as a picture 16 tiles across: colour numbers, width 128
    public static (int Width, int Height, byte[] Pixels) Sheet(byte[] tiles)
    {
        int count = tiles.Length / 32, rows = (count + 15) / 16;
        int w = 128, h = rows * 8;
        var pixels = new byte[w * h];
        for (int t = 0; t < count; t++)
        {
            var tilePixels = L2Gfx.DecodeTile(tiles, t * 32, 4);
            int tx = t % 16 * 8, ty = t / 16 * 8;
            for (int i = 0; i < 64; i++) pixels[(ty + i / 8) * w + tx + i % 8] = tilePixels[i];
        }
        return (w, h, pixels);
    }

    public static byte[] SheetTiles(int width, int height, byte[] px, int length)
    {
        var tiles = new byte[length];
        int count = length / 32;
        for (int t = 0; t < count; t++)
        {
            int tx = t % 16 * 8, ty = t / 16 * 8;
            var tilePixels = new byte[64];
            for (int i = 0; i < 64; i++)
            {
                int x = tx + i % 8, y = ty + i / 8;
                tilePixels[i] = x < width && y < height ? (byte)(px[y * width + x] & 15) : (byte)0;
            }
            L2Gfx.EncodeTile(tilePixels, 4).CopyTo(tiles, t * 32);
        }
        return tiles;
    }
}
