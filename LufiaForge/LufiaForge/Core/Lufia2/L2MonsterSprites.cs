namespace LufiaForge.Core.Lufia2;

/// <summary>
/// Monster battle sprites (US ROM, read from the game's code 2026-10-27; docs/lufia2/graphics.md section 8).
/// <list type="bullet">
/// <item>A monster record names its sprite at +0F (1-based) and its palette at +10 (0-based, into the sprite's own
/// palettes); $81:FBC6 reads both.</item>
/// <item>$85:EB91 (battle set-up) unpacks packed file 0x1B6 + sprite (ADC #$01B7 at $85:EBB1 on sprite - 1) to
/// $7E:DF00: byte 0 = how many palettes, then the palettes (16 colours each, colour 0 see-through), then the tiles: 16x16
/// squares row by row, each square 0x80 bytes (top left, top right, bottom left, bottom right 8x8 tile, 4bpp).</item>
/// <item>The size in 8x8 tiles is at $97:CA64 + (sprite - 1) * 2 ($81:FB8E): byte 0 bits 0-4 tiles across, byte 1 bits 0-4
/// tiles down; the top 3 bits of each byte ($81:FBA2) are the sprite's movement flags in battle ($13DA).</item>
/// <item>$81:BCCC copies the squares one after the other into the battle sprite buffer $7E:6000 (sprite tiles
/// 0x100-0x1FF, 64 squares for every kind of monster in the battle together); $85:EC4D copies the palette the record
/// names to the sprite colours ($7E:4820 + 0x20 per kind).</item>
/// </list>
/// New sprites (0x87 and up: the sprite byte goes to 0xFF) take three operand changes and one hook, put in the first
/// time one is added: the size reads at $81:FB98, $81:FBAE and $81:FBB9 read Lufia Forge's copy of the size table
/// (the game's 0x200 bytes, new sprites' sizes written into it), and the unpack call at $85:EBC1 (JSL $808E9D) goes
/// through code that unpacks a new sprite from Lufia Forge's own pointer (the game's unpacker from $80:8EBD, after its
/// file table lookup). The game's sprites stay packed files.
/// </summary>
public static class L2MonsterSprites
{
    /// <summary>The game's sprites (1 to 0x86), the last number a record can hold, and how many can be added.</summary>
    public const int GameCount = 0x86, MaxNumber = 0xFF, MaxNew = MaxNumber - GameCount;
    /// <summary>Packed file of sprite n = <see cref="FileBase"/> + n.</summary>
    public const int FileBase = 0x1B6;
    /// <summary>The battle sprite buffer holds 64 squares of 16x16 (for all the kinds of monster in one battle); the
    /// game unpacks a sprite to $7E:DF00, so its data must end by $7E:FFFF; the size is 5 bits of 8x8 tiles.</summary>
    public const int MaxSquares = 64, MaxDataBytes = 0x2100, MaxTiles = 30;
    public static readonly int GameSizes = L2Rom.SnesToFile(0x97CA64);

    private const int SizeSite1 = 0xFB98, SizeSite2 = 0xFBAE, SizeSite3 = 0xFBB9, LoadSite = 0x2EBC1, FileSite = 0x2EBB1;
    private static readonly byte[] Size1Original = { 0xBF, 0x64, 0xCA, 0x97 }, Size3Original = { 0xBF, 0x65, 0xCA, 0x97 },
        LoadOriginal = { 0x22, 0x9D, 0x8E, 0x80 }, FileOriginal = { 0x69, 0xB7, 0x01 };

    // the block: "LF2MS", version, new sprite count; the code; the size table; u24 pointers of the new sprites' packed data
    private const string Magic = "LF2MS";
    private const int CodeAt = 0x10, SizesAt = 0x80, PointersAt = 0x280, BlockSize = 0x400;

    /// <summary>File offset of Lufia Forge's block, or -1 (no new sprite added yet).</summary>
    public static int Block(RomBuffer rom)
    {
        if (rom.Length <= LoadSite + 4 || rom.ReadByte(LoadSite) != 0x22) return -1;
        int block = L2Rom.SnesToFile((int)rom.ReadUInt24Le(LoadSite + 1)) - CodeAt;
        return block >= L2Rom.OriginalSize && block + BlockSize <= rom.Length &&
               System.Text.Encoding.ASCII.GetString(rom.ReadBytes(block, Magic.Length)) == Magic ? block : -1;
    }

    public static bool IsInstalled(RomBuffer rom) => Block(rom) >= 0;

    /// <summary>Whether new sprites can be added (the four places still hold the game's code).</summary>
    public static bool CanInstall(RomBuffer rom, out string why)
    {
        why = "";
        if (IsInstalled(rom)) return true;
        bool Same(int at, byte[] b) => at + b.Length <= rom.Length && rom.ReadBytes(at, b.Length).SequenceEqual(b);
        if (Same(SizeSite1, Size1Original) && Same(SizeSite2, Size1Original) && Same(SizeSite3, Size3Original) &&
            Same(LoadSite, LoadOriginal) && Same(FileSite, FileOriginal)) return true;
        why = "This ROM's monster sprite code isn't the US game's where Lufia Forge's hook goes (another patch or hack changed it), so new monster sprites can't be added. The game's sprites can still be painted and given new palettes.";
        return false;
    }

    /// <summary>Monster sprites there are (numbered from 1): the game's 134 and the new ones.</summary>
    public static int Count(RomBuffer rom) => Block(rom) is var b && b >= 0 ? GameCount + Math.Min((int)rom.ReadByte(b + 6), MaxNew) : GameCount;

    public static bool IsNew(int sprite) => sprite > GameCount;

    /// <summary>File offset of the size table the game reads (its own at $97:CA64, Lufia Forge's copy once installed,
    /// or wherever another hack pointed the read).</summary>
    public static int SizeTable(RomBuffer rom) =>
        rom.ReadByte(SizeSite1) == 0xBF ? L2Rom.SnesToFile((int)rom.ReadUInt24Le(SizeSite1 + 1)) : GameSizes;

    /// <summary>Sprite <paramref name="sprite"/>'s size in 8x8 tiles and its movement flags (6 bits, $13DA).</summary>
    public static (int Across, int Down, int Moves) Size(RomBuffer rom, int sprite)
    {
        int at = SizeTable(rom) + (sprite - 1 & 0xFF) * 2;
        int a = rom.ReadByte(at), d = rom.ReadByte(at + 1);
        return (a & 31, d & 31, a >> 5 | (d & 0xE0) >> 2);
    }

    /// <summary>The movement flags (6 bits: the top 3 bits of the size table's two bytes, read by $81:FBA2 into $13DA
    /// for each monster in battle). $85:894A moves the monsters every frame: 01 floats (up and down by 2 pixels, a
    /// 64-frame wave from $85:9E37 into the y offset $13E5), else 02 shakes (the x offset $13E3 flips between 0 and -1
    /// every 2 frames), else 20 twitches (every 12 frames the x and y offsets are each 0 or -1 at random). 04, 08 and 10
    /// are read by nothing (the game sets 10 on every sprite). A status can take over: $85:8905 gives a monster whose
    /// status byte (+0F) has 08 the shake and 20 the float instead.</summary>
    public const int MoveFloat = 0x01, MoveShake = 0x02, MoveTwitch = 0x20;

    /// <summary>The 6 movement flags: bit, name, what it does in battle.</summary>
    public static readonly (int Bit, string Name, string What)[] MoveFlags =
    {
        (MoveFloat, "Floats", "Bobs up and down by 2 pixels in a slow wave (64 frames). Wins over Shakes and Twitches."),
        (MoveShake, "Shakes", "Moves 1 pixel left and back every 2 frames, a fast tremble. Wins over Twitches."),
        (0x04, "Bit 04 (no effect)", "Nothing in the game reads it."),
        (0x08, "Bit 08 (no effect)", "Nothing in the game reads it."),
        (0x10, "Bit 10 (no effect)", "Nothing in the game reads it; the game sets it on every sprite."),
        (MoveTwitch, "Twitches", "Every 12 frames it jumps to a random spot 0 or 1 pixel left and up of its place, a restless twitch (most of the game's monsters)."),
    };

    /// <summary>The movement in words.</summary>
    public static string MovesText(int moves) =>
        (moves & MoveFloat) != 0 ? "floats (bobs up and down)" : (moves & MoveShake) != 0 ? "shakes" : (moves & MoveTwitch) != 0 ? "twitches" : "stands still";

    /// <summary>Sets sprite <paramref name="sprite"/>'s movement flags (in the size table the game reads). True when
    /// they changed.</summary>
    public static bool SetMoves(RomBuffer rom, int sprite, int moves)
    {
        if (sprite < 1 || sprite > Count(rom)) throw new ArgumentOutOfRangeException(nameof(sprite));
        int at = SizeTable(rom) + (sprite - 1 & 0xFF) * 2;
        byte a = rom.ReadByte(at), d = rom.ReadByte(at + 1);
        byte na = (byte)(a & 31 | (moves & 7) << 5), nd = (byte)(d & 31 | (moves >> 3 & 7) << 5);
        if (na == a && nd == d) return false;
        rom.WriteByte(at, na); rom.WriteByte(at + 1, nd);
        return true;
    }

    /// <summary>16x16 squares across and down (the game rounds a size in tiles up).</summary>
    public static (int Across, int Down) Squares(int tilesAcross, int tilesDown) => ((tilesAcross + 1) / 2, (tilesDown + 1) / 2);

    private static int PointerAt(int block, int sprite) => block + PointersAt + (sprite - GameCount - 1) * 3;

    /// <summary>File offset of a new sprite's packed data.</summary>
    private static int NewDataAt(RomBuffer rom, int sprite)
    {
        int b = Block(rom);
        if (b < 0 || sprite <= GameCount || sprite > Count(rom)) throw new ArgumentOutOfRangeException(nameof(sprite));
        return L2Rom.SnesToFile((int)rom.ReadUInt24Le(PointerAt(b, sprite)));
    }

    /// <summary>Where the sprite is stored, in words.</summary>
    public static string Where(RomBuffer rom, int sprite) => IsNew(sprite)
        ? $"packed at {NewDataAt(rom, sprite):X6} (added by Lufia Forge)"
        : $"packed file {FileBase + sprite:X3}";

    /// <summary>The sprite's data unpacked: palette count, palettes, tiles.</summary>
    public static byte[] Read(RomBuffer rom, int sprite)
    {
        if (sprite < 1 || sprite > Count(rom)) throw new ArgumentOutOfRangeException(nameof(sprite));
        return IsNew(sprite) ? LufiaCompression.Decompress(rom, NewDataAt(rom, sprite), out _) : L2Rom.ReadFile(rom, FileBase + sprite);
    }

    /// <summary>Puts the sprite's data back (packed: in place when it fits, else in the added space).</summary>
    public static void Write(RomBuffer rom, int sprite, byte[] data)
    {
        if (data.Length > MaxDataBytes)
            throw new InvalidOperationException($"The sprite would be 0x{data.Length:X} bytes with its palettes; the game unpacks it to $7E:DF00, so it can be at most 0x{MaxDataBytes:X} (fewer palettes or a smaller picture).");
        if (!IsNew(sprite)) { L2Rom.WriteFile(rom, FileBase + sprite, data); return; }
        int b = Block(rom), old = NewDataAt(rom, sprite);
        byte[] packed = LufiaCompression.Compress(data);
        LufiaCompression.Decompress(rom, old, out int oldSize);
        int at = packed.Length <= oldSize ? old : L2Rom.Allocate(rom, packed.Length);
        rom.WriteBytes(at, packed);
        int snes = L2Rom.FileToSnes(at);
        rom.WriteBytes(PointerAt(b, sprite), new[] { (byte)snes, (byte)(snes >> 8), (byte)(snes >> 16) });
    }

    public static int PaletteCount(RomBuffer rom, int sprite) => Read(rom, sprite)[0];

    /// <summary>The 16 colours (BGR555) of palette <paramref name="palette"/> of a sprite's data.</summary>
    public static int[] Palette(byte[] data, int palette)
    {
        palette = Math.Clamp(palette, 0, Math.Max(0, data[0] - 1));
        return Enumerable.Range(0, 16).Select(c => 1 + palette * 32 + c * 2 + 1 < data.Length ? (data[1 + palette * 32 + c * 2] | data[2 + palette * 32 + c * 2] << 8) & 0x7FFF : 0).ToArray();
    }

    /// <summary>The sprite as colour numbers (0-15), with its size in pixels.</summary>
    public static (int Width, int Height, byte[] Pixels) Pixels(RomBuffer rom, int sprite, byte[]? data = null)
    {
        data ??= Read(rom, sprite);
        var (tx, ty, _) = Size(rom, sprite);
        var (sx, _) = Squares(tx, ty);
        int w = tx * 8, h = ty * 8, tiles = 1 + data[0] * 32;
        var px = new byte[w * h];
        for (int y = 0; y < ty; y++)
            for (int x = 0; x < tx; x++)
            {
                int at = tiles + ((y >> 1) * sx + (x >> 1)) * 0x80 + ((y & 1) * 2 + (x & 1)) * 0x20;
                if (at + 32 > data.Length) continue;
                var p = L2Gfx.DecodeTile(data, at, 4);
                for (int i = 0; i < 64; i++) px[(y * 8 + i / 8) * w + x * 8 + i % 8] = p[i];
            }
        return (w, h, px);
    }

    /// <summary>The tile data (squares of 0x80 bytes) of a picture of colour numbers.</summary>
    public static byte[] Tiles(int width, int height, byte[] pixels)
    {
        int tx = width / 8, ty = height / 8;
        var (sx, sy) = Squares(tx, ty);
        var d = new byte[sx * sy * 0x80];
        for (int y = 0; y < ty; y++)
            for (int x = 0; x < tx; x++)
            {
                var p = new byte[64];
                for (int i = 0; i < 64; i++) p[i] = (byte)(pixels[(y * 8 + i / 8) * width + x * 8 + i % 8] & 15);
                L2Gfx.EncodeTile(p, 4).CopyTo(d, ((y >> 1) * sx + (x >> 1)) * 0x80 + ((y & 1) * 2 + (x & 1)) * 0x20);
            }
        return d;
    }

    /// <summary>Writes a picture (colour numbers, the sprite's size) into the sprite; with <paramref name="colours"/>
    /// (16 BGR555 values) palette <paramref name="palette"/>'s colours 1-15 too. Returns how many colours changed.</summary>
    public static int WritePicture(RomBuffer rom, int sprite, byte[] pixels, int palette = 0, IReadOnlyList<int>? colours = null)
    {
        var data = Read(rom, sprite);
        var (tx, ty, _) = Size(rom, sprite);
        if (pixels.Length != tx * ty * 64) throw new InvalidOperationException($"The picture must be {tx * 8} x {ty * 8} pixels.");
        var tiles = Tiles(tx * 8, ty * 8, pixels);
        int head = 1 + data[0] * 32;
        var n = new byte[head + tiles.Length];
        Array.Copy(data, n, Math.Min(head, data.Length));
        tiles.CopyTo(n, head);
        int changed = 0;
        if (colours != null && data[0] > 0)
            changed = SetColours(n, palette, colours);
        if (!n.AsSpan().SequenceEqual(data)) Write(rom, sprite, n);
        return changed;
    }

    private static int SetColours(byte[] data, int palette, IReadOnlyList<int> colours)
    {
        int changed = 0;
        for (int c = 1; c < 16 && c < colours.Count; c++)
        {
            int at = 1 + palette * 32 + c * 2, old = data[at] | data[at + 1] << 8, v = old & 0x8000 | colours[c] & 0x7FFF;
            if (v == old) continue;
            data[at] = (byte)v; data[at + 1] = (byte)(v >> 8); changed++;
        }
        return changed;
    }

    /// <summary>Sets colour <paramref name="colour"/> (1-15) of a palette. True when it changed.</summary>
    public static bool SetColour(RomBuffer rom, int sprite, int palette, int colour, int bgr555)
    {
        var data = Read(rom, sprite);
        if (palette < 0 || palette >= data[0] || colour is < 0 or > 15) throw new ArgumentOutOfRangeException(nameof(palette));
        int at = 1 + palette * 32 + colour * 2, old = data[at] | data[at + 1] << 8, v = old & 0x8000 | bgr555 & 0x7FFF;
        if (v == old) return false;
        data[at] = (byte)v; data[at + 1] = (byte)(v >> 8);
        Write(rom, sprite, data);
        return true;
    }

    /// <summary>Adds a palette (16 BGR555 colours) to a sprite. Returns its number (what a monster's palette field holds).</summary>
    public static int AddPalette(RomBuffer rom, int sprite, IReadOnlyList<int> colours)
    {
        var data = Read(rom, sprite);
        int n = data[0];
        if (n >= 255) throw new InvalidOperationException("This sprite already has 255 palettes.");
        var d = new byte[data.Length + 32];
        d[0] = (byte)(n + 1);
        Array.Copy(data, 1, d, 1, n * 32);
        for (int c = 0; c < 16; c++) { int v = c < colours.Count ? colours[c] & 0x7FFF : 0; d[1 + n * 32 + c * 2] = (byte)v; d[2 + n * 32 + c * 2] = (byte)(v >> 8); }
        Array.Copy(data, 1 + n * 32, d, 1 + (n + 1) * 32, data.Length - 1 - n * 32);
        Write(rom, sprite, d);
        return n;
    }

    /// <summary>Why a picture can't be a monster sprite, or null.</summary>
    public static string? SizeProblem(int width, int height, int palettes = 1)
    {
        if (width <= 0 || height <= 0 || width % 16 != 0 || height % 16 != 0)
            return $"A monster sprite is made of 16 x 16 squares: the picture must be a multiple of 16 pixels across and down (this one is {width} x {height}).";
        if (width / 8 > MaxTiles || height / 8 > MaxTiles)
            return $"A monster sprite can be at most {MaxTiles * 8} x {MaxTiles * 8} pixels (the game keeps its size in tiles in 5 bits).";
        int squares = width / 16 * (height / 16);
        if (squares > MaxSquares)
            return $"The picture is {squares} squares of 16 x 16; the battle sprite buffer holds {MaxSquares} for every kind of monster in a battle together (the biggest bosses fill it alone).";
        if (1 + palettes * 32 + squares * 0x80 > MaxDataBytes)
            return $"With {palettes} palette{(palettes == 1 ? "" : "s")} the sprite would not fit where the game unpacks it ($7E:DF00-$7E:FFFF).";
        return null;
    }

    /// <summary>Adds a sprite that is a copy of <paramref name="from"/> (its size, movement flags, palettes and tiles).
    /// Returns its number.</summary>
    public static int AddCopy(RomBuffer rom, int from)
    {
        if (from < 1 || from > Count(rom)) throw new ArgumentOutOfRangeException(nameof(from));
        var (tx, ty, moves) = Size(rom, from);
        return Add(rom, tx, ty, moves, Read(rom, from));
    }

    /// <summary>Adds a sprite from a picture: colour numbers 0-15 (0 see-through), 16 colours (BGR555, colour 0 kept
    /// for the see-through one), the movement flags of another sprite. Returns its number.</summary>
    public static int AddPicture(RomBuffer rom, int width, int height, byte[] pixels, IReadOnlyList<int> colours, int moves)
    {
        if (SizeProblem(width, height) is { } why) throw new InvalidOperationException(why);
        var data = new byte[1 + 32];
        data[0] = 1;
        for (int c = 0; c < 16; c++) { int v = c < colours.Count ? colours[c] & 0x7FFF : 0; data[1 + c * 2] = (byte)v; data[2 + c * 2] = (byte)(v >> 8); }
        return Add(rom, width / 8, height / 8, moves, data.Concat(Tiles(width, height, pixels)).ToArray());
    }

    private static int Add(RomBuffer rom, int tx, int ty, int moves, byte[] data)
    {
        int have = Count(rom);
        if (have >= MaxNumber) throw new InvalidOperationException($"There are already {MaxNew} new monster sprites: a monster record keeps its sprite in one byte, so 0xFF is the last number.");
        if (data.Length > MaxDataBytes) throw new InvalidOperationException($"The sprite is 0x{data.Length:X} bytes; the game unpacks it to $7E:DF00, so it can be at most 0x{MaxDataBytes:X}.");
        int b = Install(rom);
        int id = have + 1;
        byte[] packed = LufiaCompression.Compress(data);
        int at = L2Rom.Allocate(rom, packed.Length);
        rom.WriteBytes(at, packed);
        int snes = L2Rom.FileToSnes(at);
        rom.WriteBytes(PointerAt(b, id), new[] { (byte)snes, (byte)(snes >> 8), (byte)(snes >> 16) });
        int sz = b + SizesAt + (id - 1) * 2;
        rom.WriteBytes(sz, new[] { (byte)(tx & 31 | (moves & 7) << 5), (byte)(ty & 31 | (moves >> 3 & 7) << 5) });
        rom.WriteByte(b + 6, (byte)(id - GameCount));
        return id;
    }

    /// <summary>Puts the hook and the block in (once). Returns the block's file offset.</summary>
    public static int Install(RomBuffer rom)
    {
        int have = Block(rom);
        if (have >= 0) return have;
        if (!CanInstall(rom, out var why)) throw new InvalidOperationException(why);
        var sizes = rom.ReadBytes(SizeTable(rom), 0x200);
        int block = L2Rom.Allocate(rom, BlockSize);
        int snes = L2Rom.FileToSnes(block);
        rom.WriteBytes(block, new byte[BlockSize]);
        rom.WriteBytes(block, System.Text.Encoding.ASCII.GetBytes(Magic));
        rom.WriteByte(block + 5, 1);   // version
        rom.WriteBytes(block + CodeAt, Assemble(snes));
        rom.WriteBytes(block + SizesAt, sizes);
        int t = snes + SizesAt;
        rom.WriteBytes(SizeSite1 + 1, new[] { (byte)t, (byte)(t >> 8), (byte)(t >> 16) });
        rom.WriteBytes(SizeSite2 + 1, new[] { (byte)t, (byte)(t >> 8), (byte)(t >> 16) });
        rom.WriteBytes(SizeSite3 + 1, new[] { (byte)(t + 1), (byte)(t + 1 >> 8), (byte)(t + 1 >> 16) });
        int h = snes + CodeAt;
        rom.WriteBytes(LoadSite, new[] { (byte)0x22, (byte)h, (byte)(h >> 8), (byte)(h >> 16) });
        return block;
    }

    /// <summary>The load hook (JSL from $85:EBC1, m1 x0, $54 = the file number 0x1B7 + sprite - 1, $60-$62 the
    /// destination): a new sprite's data is unpacked from its pointer by the game's unpacker, entered after its file
    /// table lookup ($80:8EBD: P and DB pushed, [$5D],Y = the packed data); the others go to $80:8E9D as before.</summary>
    private static byte[] Assemble(int block)
    {
        int ptrs = block + PointersAt, first = 0x1B7 + GameCount;
        var c = new List<byte>
        {
            0x08,                                   // 00 PHP
            0xC2, 0x20,                             // 01 REP #$20
            0xA5, 0x54,                             // 03 LDA $54
            0x38,                                   // 05 SEC
            0xE9, (byte)first, (byte)(first >> 8),  // 06 SBC #first new file
            0x90, 0x22,                             // 09 BCC game (2D)
            0xC9, MaxNew, 0x00,                     // 0B CMP #MaxNew
            0xB0, 0x1D,                             // 0E BCS game
            0x48, 0x0A, 0x18, 0x63, 0x01, 0xAA, 0x68, // 10 PHA / ASL / CLC / ADC 1,S / TAX / PLA (X = index * 3)
            0xBF, (byte)ptrs, (byte)(ptrs >> 8), (byte)(ptrs >> 16),   // 17 LDA ptrs,X
            0xF0, 0x10,                             // 1B BEQ game (no pointer)
            0xA8,                                   // 1D TAY
            0x8B,                                   // 1E PHB
            0x64, 0x5D,                             // 1F STZ $5D
            0xE2, 0x20,                             // 21 SEP #$20
            0xBF, (byte)(ptrs + 2), (byte)(ptrs + 2 >> 8), (byte)(ptrs + 2 >> 16),   // 23 LDA ptrs+2,X
            0x85, 0x5F,                             // 27 STA $5F
            0x5C, 0xBD, 0x8E, 0x80,                 // 29 JML $808EBD
            0x28,                                   // 2D game: PLP
            0x5C, 0x9D, 0x8E, 0x80,                 // 2E JML $808E9D
        };
        return c.ToArray();
    }

    /// <summary>The monsters that use sprite <paramref name="sprite"/> (index, name, palette).</summary>
    public static List<L2Battle.Monster> Users(RomBuffer rom, int sprite) => L2Battle.Monsters(rom).Where(m => m.Sprite == sprite).ToList();

    /// <summary>Colour numbers and 16 colours (BGR555, colour 0 = see-through) for a full-colour picture: its own
    /// colours when there are 15 or fewer, else 15 found by splitting the colour box with the most pixels (median cut).
    /// Pixels with alpha under 128 are see-through.</summary>
    public static (byte[] Pixels, int[] Colours) Reduce(uint[] argb)
    {
        var px = new byte[argb.Length];
        var values = argb.Select(p => p >> 24 < 128 ? -1 : L2Gfx.Bgr555(p)).ToArray();
        var distinct = values.Where(v => v >= 0).Distinct().ToList();
        List<int> palette;
        if (distinct.Count <= 15) palette = distinct;
        else
        {
            var boxes = new List<List<int>> { values.Where(v => v >= 0).ToList() };
            static int Ch(int v, int k) => v >> (5 * k) & 31;
            while (boxes.Count < 15)
            {
                // split the box with the widest channel (among boxes of more than one colour)
                int best = -1, bestK = 0, bestRange = 0;
                for (int i = 0; i < boxes.Count; i++)
                    for (int k = 0; k < 3; k++)
                    {
                        int lo = boxes[i].Min(v => Ch(v, k)), hi = boxes[i].Max(v => Ch(v, k));
                        if (hi - lo > bestRange) { bestRange = hi - lo; best = i; bestK = k; }
                    }
                if (best < 0) break;
                var sorted = boxes[best].OrderBy(v => Ch(v, bestK)).ToList();
                int mid = sorted.Count / 2, cut = Ch(sorted[mid], bestK);
                var a = sorted.Where(v => Ch(v, bestK) < cut).ToList();
                var b = sorted.Where(v => Ch(v, bestK) >= cut).ToList();
                if (a.Count == 0 || b.Count == 0) { a = sorted.Take(mid).ToList(); b = sorted.Skip(mid).ToList(); }
                if (a.Count == 0 || b.Count == 0) break;
                boxes[best] = a; boxes.Add(b);
            }
            palette = boxes.Select(bx => (int)Math.Round(bx.Average(v => Ch(v, 0))) | (int)Math.Round(bx.Average(v => Ch(v, 1))) << 5 | (int)Math.Round(bx.Average(v => Ch(v, 2))) << 10).Distinct().ToList();
        }
        var colours = new int[16];
        for (int i = 0; i < palette.Count && i < 15; i++) colours[i + 1] = palette[i];
        var cache = new Dictionary<int, byte>();
        for (int i = 0; i < px.Length; i++)
        {
            int v = values[i];
            if (v < 0) continue;
            if (!cache.TryGetValue(v, out byte best))
            {
                long bd = long.MaxValue;
                for (int c = 1; c <= Math.Min(15, palette.Count); c++)
                {
                    long dr = (v & 31) - (colours[c] & 31), dg = (v >> 5 & 31) - (colours[c] >> 5 & 31), db = (v >> 10 & 31) - (colours[c] >> 10 & 31);
                    long d = dr * dr * 3 + dg * dg * 4 + db * db * 2;
                    if (d < bd) { bd = d; best = (byte)c; }
                }
                cache[v] = best;
            }
            px[i] = best;
        }
        return (px, colours);
    }
}
