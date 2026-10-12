using LufiaForge.Core.Maps;

namespace LufiaForge.Core.Battle;

/// <summary>
/// Battle animations of spells and items (spell record +9, item tag 15). The engine ($08:DE9C setup, $08:DFA7 per
/// frame) reads everything from bank 1F, $EE00-$FFFF:
/// <list type="bullet">
/// <item>$EE02 + 2 x id: header pointer. Header = [resource][width px][height px, bit 7 = from the target's bottom]
/// [tracks (up to 8)][a script pointer per track].</item>
/// <item>The resource (unpacked to $7E:7000): a 32-byte palette (OBJ palette 7), 32 unused bytes, then 128 4bpp tiles
/// = a 128 x 64 sheet of 16 x 16 cells, uploaded to OBJ tiles $80-$FF.</item>
/// <item>$EE00: pointer to the frame-shape table. Shape = 00 00 71 03, [columns &lt;&lt; 4 | rows], then a tile byte per
/// 16 x 16 cell row by row (7F = empty); a track draws it with a tile offset added.</item>
/// <item>Track scripts: 2n speed (frames per step), 3n xx yy place, 4n dx dy move, 5n shape slot offset draw (6n
/// flipped), 0F wait a step, Bn colour tint (BF ww, B0/BF 0000 back), 01 end, F0 jump, F3/F4 loop ... </item>
/// </list>
/// New animations: the whole area is copied to $EE00 of a bank of its own in the added space (so every 16-bit
/// pointer stays valid) and the code's bank bytes point there; the header table grows to 256 ids at the start of that
/// bank, the shape table to 256, and a header whose resource byte is FF takes a 16-bit resource id from a table
/// (hook at $08:DEDB), so imported sheets get new resource ids.
/// </summary>
public static class SpellAnimations
{
    public const int OriginalCount = 0x33, OriginalShapes = 36, Capacity = 256;
    private const int AreaFile = 0xFEE00, AreaSize = 0x1200, AreaInBank = 0x6E00;
    private const int HeaderTableOp = 0x45ED4;   // LDA $1FEE02,X
    /// <summary>Bank bytes of every operand reading the area ($08:DED3-$08:E465), file offsets.</summary>
    private static readonly int[] BankBytes = { 0x45ED6, 0x45F11, 0x45F68, 0x45F72, 0x45F79, 0x45FA0, 0x45FF2, 0x463AC, 0x46423, 0x46448, 0x4645F, 0x46463 };
    private const int ResourceSite = 0x45EDB;    // LDA $1F0000,X / STA $16 / STZ $17
    private static readonly byte[] ResourceOriginal = Convert.FromHexString("BF00001F85166417");
    // the new bank: header table, resource table, shape table, hook, state, then new data up to the old area's copy
    private const int Headers = 0x0000, Resources = 0x0200, Shapes = 0x0400, Hook = 0x0600, State = 0x0640, DataStart = 0x0700;
    private static readonly byte[] Magic = "LFAN"u8.ToArray();
    private static readonly byte[] ShapePrefix = { 0x00, 0x00, 0x71, 0x03 };

    private static int FileOffset(int snes) => (snes >> 16 & 0x7F) * 0x8000 + (snes & 0x7FFF);
    private static int SnesAddress(int file) => (file / 0x8000 | 0x80) << 16 | (file % 0x8000 | 0x8000);

    /// <summary>File offset of the bank the area is in (the game's: bank 1F).</summary>
    private static int AreaBank(RomBuffer rom) => rom.ReadByte(BankBytes[0]) == 0x1F ? 0xF8000 : FileOffset(rom.ReadByte(BankBytes[0]) << 16 | 0x8000);
    private static int Block(RomBuffer rom)
    {
        if (rom.ReadByte(BankBytes[0]) == 0x1F) return 0;
        int b = AreaBank(rom);
        return b + State + 4 <= rom.Length && rom.ReadBytes(b + State, 4).SequenceEqual(Magic) ? b : 0;
    }
    public static bool IsMoved(RomBuffer rom) => Block(rom) > 0;
    public static bool IsOriginal(RomBuffer rom) => BankBytes.All(a => rom.ReadByte(a) == 0x1F) && rom.ReadBytes(ResourceSite, 8).SequenceEqual(ResourceOriginal);

    /// <summary>File offset of a 16-bit address in the area's bank.</summary>
    private static int At(RomBuffer rom, int addr) => AreaBank(rom) + (addr & 0x7FFF);

    public static int Count(RomBuffer rom) { int b = Block(rom); return b > 0 ? rom.ReadByte(b + State + 4) : OriginalCount; }
    private static int ShapeCount(RomBuffer rom) { int b = Block(rom); return b > 0 ? rom.ReadByte(b + State + 5) : OriginalShapes; }

    private static int HeaderOffset(RomBuffer rom, int id)
    {
        int table = Block(rom) is int b and > 0 ? b + Headers : At(rom, 0xEE02);
        return At(rom, rom.ReadUInt16Le(table + id * 2));
    }

    /// <summary>The resource with an animation's sheet.</summary>
    public static int Resource(RomBuffer rom, int id)
    {
        int h = HeaderOffset(rom, id), r = rom.ReadByte(h);
        return r == 0xFF && Block(rom) is int b and > 0 ? rom.ReadUInt16Le(b + Resources + id * 2) : r;
    }

    public sealed record Info(int Resource, int Width, int Height, bool FromBottom, int Tracks);
    public static Info Describe(RomBuffer rom, int id)
    {
        int h = HeaderOffset(rom, id);
        return new Info(Resource(rom, id), rom.ReadByte(h + 1), rom.ReadByte(h + 2) & 0x7F, (rom.ReadByte(h + 2) & 0x80) != 0, rom.ReadByte(h + 3));
    }

    // ── sheets ────────────────────────────────────────────────────────────

    public const int SheetWidth = 128, SheetHeight = 64;

    /// <summary>The animation's sheet: colour numbers (128 x 64) and its 16 colours (BGRA, 0 transparent).</summary>
    public static (byte[] Indices, uint[] Palette) Sheet(RomBuffer rom, int id)
    {
        var d = LufiaCompression.DecompressResource(rom, Resource(rom, id), out _);
        var pal = new uint[16];
        for (int c = 1; c < 16 && c * 2 + 1 < d.Length; c++)
        {
            int v = d[c * 2] | d[c * 2 + 1] << 8;
            pal[c] = 0xFF000000u | (uint)((v & 31) << 3) << 16 | (uint)((v >> 5 & 31) << 3) << 8 | (uint)((v >> 10 & 31) << 3);
        }
        var idx = new byte[SheetWidth * SheetHeight];
        for (int t = 0; t < 128 && 0x40 + t * 32 + 32 <= d.Length; t++)
        {
            var px = Modules.TileViewer.SnesTileDecoder.DecodeTile(d, 0x40 + t * 32, Modules.TileViewer.BitDepth.Bpp4);
            int tx = t % 16 * 8, ty = t / 16 * 8;
            for (int y = 0; y < 8; y++) for (int x = 0; x < 8; x++) idx[(ty + y) * SheetWidth + tx + x] = px[y * 8 + x];
        }
        return (idx, pal);
    }

    /// <summary>A sheet as the resource the engine loads: palette, 32 unused bytes, 128 tiles.</summary>
    private static byte[] BuildResource(byte[] idx, IReadOnlyList<uint> palette)
    {
        var d = new byte[0x40 + 128 * 32];
        for (int c = 1; c < 16 && c < palette.Count; c++)
        {
            uint p = palette[c];
            int v = (int)(p >> 16 & 255) >> 3 | ((int)(p >> 8 & 255) >> 3) << 5 | ((int)(p & 255) >> 3) << 10;
            d[c * 2] = (byte)v; d[c * 2 + 1] = (byte)(v >> 8);
        }
        for (int t = 0; t < 128; t++)
        {
            var px = new byte[64];
            int tx = t % 16 * 8, ty = t / 16 * 8;
            for (int y = 0; y < 8; y++) for (int x = 0; x < 8; x++) px[y * 8 + x] = (byte)(idx[(ty + y) * SheetWidth + tx + x] & 15);
            Modules.TileViewer.SnesTileDecoder.EncodeTile(px, Modules.TileViewer.BitDepth.Bpp4).CopyTo(d, 0x40 + t * 32);
        }
        return d;
    }

    // ── new animations ────────────────────────────────────────────────────

    /// <summary>Moves the animation area to a bank of its own (see the class notes).</summary>
    public static void Move(RomBuffer rom)
    {
        if (IsMoved(rom)) return;
        if (!IsOriginal(rom)) throw new InvalidOperationException("The game's battle animation code isn't the original, so new animations can't be added safely.");
        if (rom.Length < MapWriter.ExpandedSize) rom.Expand(MapWriter.ExpandedSize);
        var space = ExpansionSpace.Load(rom);
        int b = space.AllocateInOneBank(0x8000, ExpansionSpace.Kind.Patch, 18);
        space.Save();
        var bank = Enumerable.Repeat((byte)0xFF, 0x8000).ToArray();
        rom.ReadBytes(AreaFile, AreaSize).CopyTo(bank, AreaInBank);
        int bankByte = SnesAddress(b) >> 16, bankAddr = SnesAddress(b) & 0xFFFF;   // ($8000)
        // header table (256), resource table (256 words), shape table (256)
        for (int i = 0; i < Capacity * 2; i++) bank[Headers + i] = 0;
        Array.Copy(rom.ReadBytes(At(rom, 0xEE02), OriginalCount * 2), 0, bank, Headers, OriginalCount * 2);
        for (int i = 0; i < Capacity * 2; i++) bank[Resources + i] = 0;
        for (int i = 0; i < Capacity * 2; i++) bank[Shapes + i] = 0;
        int oldShapes = rom.ReadUInt16Le(At(rom, 0xEE00));
        Array.Copy(rom.ReadBytes(At(rom, oldShapes), OriginalShapes * 2), 0, bank, Shapes, OriginalShapes * 2);
        int shapes = bankAddr + Shapes;
        bank[AreaInBank] = (byte)shapes; bank[AreaInBank + 1] = (byte)(shapes >> 8);   // the copy's $EE00 word
        // hook: header resource byte FF = a 16-bit id from the table (A8 X16 in; X = header address; $16 = animation id)
        int res = bankAddr + Resources;
        var code = new byte[]
        {
            0xBF, 0x00, 0x00, (byte)bankByte,          // LDA bank:0000,X  (the header's resource byte)
            0xC9, 0xFF, 0xD0, 0x14,                    // CMP #$FF; BNE plain
            0xDA, 0xC2, 0x20, 0xA5, 0x16,              // PHX; REP #$20; LDA $16
            0x29, 0xFF, 0x00, 0x0A, 0xAA,              // AND #$00FF; ASL; TAX
            0xBF, (byte)res, (byte)(res >> 8), (byte)bankByte,   // LDA resources,X
            0x85, 0x16, 0xE2, 0x20, 0xFA, 0x6B,        // STA $16; SEP #$20; PLX; RTL
            0x85, 0x16, 0x64, 0x17, 0x6B,              // plain: STA $16; STZ $17; RTL
        };
        code.CopyTo(bank, Hook);
        Magic.CopyTo(bank, State);
        bank[State + 4] = OriginalCount; bank[State + 5] = OriginalShapes;
        bank[State + 6] = DataStart & 0xFF; bank[State + 7] = DataStart >> 8;
        rom.WriteBytes(b, bank);
        // point the code at the new bank
        foreach (int a in BankBytes) rom.WriteByte(a, (byte)bankByte);
        int headers = bankAddr + Headers;
        rom.WriteByte(HeaderTableOp, (byte)headers); rom.WriteByte(HeaderTableOp + 1, (byte)(headers >> 8));
        int hook = SnesAddress(b + Hook);
        rom.WriteBytes(ResourceSite, new byte[] { 0x22, (byte)hook, (byte)(hook >> 8), (byte)(hook >> 16), 0xEA, 0xEA, 0xEA, 0xEA });
        rom.FixChecksum();
    }

    /// <summary>Bytes of the new bank still free for headers, shapes and scripts.</summary>
    public static int Room(RomBuffer rom) { int b = Block(rom); return b > 0 ? AreaInBank - rom.ReadUInt16Le(b + State + 6) : AreaInBank - DataStart; }

    private static int Put(RomBuffer rom, byte[] data)
    {
        int b = Block(rom), free = rom.ReadUInt16Le(b + State + 6);
        if (free + data.Length > AreaInBank) throw new InvalidOperationException("No room left for more animations.");
        rom.WriteBytes(b + free, data);
        rom.WriteUInt16Le(b + State + 6, (ushort)(free + data.Length));
        return (SnesAddress(b) & 0xFFFF) + free;
    }

    public sealed record ImportOptions(int FrameWidth, int FrameHeight, int Frames, int StepFrames, bool FromBottom, uint? FlashColour);

    /// <summary>The largest frame count for a frame size (the sheet is 8 x 4 cells of 16 x 16).</summary>
    public static int MaxFrames(int w, int h) => w % 16 != 0 || h % 16 != 0 || w < 16 || h < 16 || w > 80 || h > 64 ? 0 : 8 / (w / 16) * (4 / (h / 16));

    /// <summary>
    /// A new animation from a sheet (128 x 64, colour numbers 0-15, 0 = see-through): frames of the given size taken
    /// left to right, top to bottom, each shown <see cref="ImportOptions.StepFrames"/> frames, centred on the target
    /// (or standing on its bottom), with an optional screen tint while it plays. Returns its id.
    /// </summary>
    public static int Import(RomBuffer rom, byte[] idx, IReadOnlyList<uint> palette, ImportOptions o)
    {
        if (idx.Length != SheetWidth * SheetHeight) throw new InvalidOperationException($"The sheet must be {SheetWidth} x {SheetHeight} pixels.");
        int max = MaxFrames(o.FrameWidth, o.FrameHeight);
        if (max == 0) throw new InvalidOperationException("Frames are 16-80 pixels wide and 16-64 high, in steps of 16.");
        if (o.Frames < 1 || o.Frames > max) throw new InvalidOperationException($"A {o.FrameWidth} x {o.FrameHeight} frame fits {max} times on the sheet.");
        Move(rom);
        int id = Count(rom), shape = ShapeCount(rom);
        if (id >= Capacity || shape >= Capacity) throw new InvalidOperationException("No animation numbers left.");
        int b = Block(rom);
        int res = ResourceIds.Add(rom, BuildResource(idx, palette));
        b = Block(rom);
        // the frame shape: cells of the first frame (the script shifts it to the others)
        int cols = o.FrameWidth / 16, rows = o.FrameHeight / 16;
        var s = new List<byte>(ShapePrefix) { (byte)(cols << 4 | rows) };
        for (int r = 0; r < rows; r++) for (int c = 0; c < cols; c++) s.Add((byte)(0x80 + 2 * c + 0x20 * r));
        int shapeAt = Put(rom, s.ToArray());
        rom.WriteUInt16Le(b + Shapes + shape * 2, (ushort)shapeAt);
        // the frames
        var t = new List<byte> { (byte)(0x20 | Math.Clamp(o.StepFrames, 1, 15)) };
        int perRow = 8 / cols;
        for (int f = 0; f < o.Frames; f++)
        {
            int cx = f % perRow * cols, cy = f / perRow * rows;
            t.AddRange(new byte[] { 0x50, (byte)shape, 0x03, (byte)(2 * cx + 0x20 * cy), 0x0F });
        }
        t.Add(0x01);
        int track0 = Put(rom, t.ToArray());
        var tracks = new List<int> { track0 };
        if (o.FlashColour is uint fc)
        {
            int v = (int)(fc >> 16 & 255) >> 3 | ((int)(fc >> 8 & 255) >> 3) << 5 | ((int)(fc & 255) >> 3) << 10;
            int steps = Math.Max(1, o.Frames * Math.Clamp(o.StepFrames, 1, 15) / 2);
            var fl = new List<byte> { 0x22, 0xBF, (byte)v, (byte)(v >> 8) };
            for (int k = 0; k < Math.Min(steps, 30); k++) fl.Add(0x0F);
            fl.AddRange(new byte[] { 0xBF, 0x00, 0x00, 0x01 });
            tracks.Add(Put(rom, fl.ToArray()));
        }
        var h = new List<byte> { 0xFF, (byte)o.FrameWidth, (byte)(o.FrameHeight | (o.FromBottom ? 0x80 : 0)), (byte)tracks.Count };
        foreach (int p in tracks) { h.Add((byte)p); h.Add((byte)(p >> 8)); }
        int header = Put(rom, h.ToArray());
        rom.WriteUInt16Le(b + Headers + id * 2, (ushort)header);
        rom.WriteUInt16Le(b + Resources + id * 2, (ushort)res);
        rom.WriteByte(b + State + 4, (byte)(id + 1));
        rom.WriteByte(b + State + 5, (byte)(shape + 1));
        rom.FixChecksum();
        return id;
    }
}
