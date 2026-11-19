using LufiaForge.Core.Maps;

namespace LufiaForge.Core.Battle;

// battle animations of spells and items (spell record +9, item tag 15). the engine ($08:DE9C setup, $08:DFA7 per
// frame) reads everything from bank 1F, $EE00-$FFFF:
// - $EE02 + 2 x id: header pointer. header = [resource][width px][height px, bit 7 = from the target's bottom]
//   [tracks (up to 8)][a script pointer per track]
// - the resource (unpacked to $7E:7000): a 32-byte palette (OBJ palette 7), 32 unused bytes, then 128 4bpp tiles
//   = a 128 x 64 sheet of 16 x 16 cells, uploaded to OBJ tiles $80-$FF
// - $EE00: pointer to the frame shape table. shape = 00 00 71 03, [columns << 4 | rows], then a tile byte per
//   16 x 16 cell row by row (7F = empty). a track draws it with a tile offset added
// - track scripts: 2n speed (frames per step), 3n xx yy place, 4n dx dy move, 5n shape slot offset draw (6n
//   flipped), 0F wait a step, Bn colour tint (BF ww, B0/BF 0000 back), 01 end, F0 jump, F3/F4 loop ...
// new animations: the whole area gets copied to $EE00 of a bank of its own in the added space (so every 16-bit
// pointer stays valid) and the code's bank bytes point there. the header table grows to 256 ids at the start of that
// bank, the shape table to 256, and a header whose resource byte is FF takes a 16-bit resource id from a table
// (hook at $08:DEDB), so imported sheets get new resource ids
public static partial class SpellAnimations
{
    public const int OriginalCount = 0x33, OriginalShapes = 36, Capacity = 256;
    private const int AreaFile = 0xFEE00, AreaSize = 0x1200, AreaInBank = 0x6E00;
    private const int HeaderTableOp = 0x45ED4;   // LDA $1FEE02,X
    // bank bytes of every operand that reads the area ($08:DED3-$08:E465), file offsets
    private static readonly int[] BankBytes = { 0x45ED6, 0x45F11, 0x45F68, 0x45F72, 0x45F79, 0x45FA0, 0x45FF2, 0x463AC, 0x46423, 0x46448, 0x4645F, 0x46463 };
    private const int ResourceSite = 0x45EDB;    // LDA $1F0000,X / STA $16 / STZ $17
    private static readonly byte[] ResourceOriginal = Convert.FromHexString("BF00001F85166417");
    // the new bank: header table, resource table, shape table, hook, state, then new data up to the old area's copy
    private const int Headers = 0x0000, Resources = 0x0200, Shapes = 0x0400, Hook = 0x0600, State = 0x0640, DataStart = 0x0700;
    private static readonly byte[] Magic = "LFAN"u8.ToArray();
    private static readonly byte[] ShapePrefix = { 0x00, 0x00, 0x71, 0x03 };

    private static int FileOffset(int snesAddress) => (snesAddress >> 16 & 0x7F) * 0x8000 + (snesAddress & 0x7FFF);
    private static int SnesAddress(int fileOffset) => (fileOffset / 0x8000 | 0x80) << 16 | (fileOffset % 0x8000 | 0x8000);

    // file offset of the bank the area is in (the game's is bank 1F)
    private static int AreaBank(RomBuffer rom) => rom.ReadByte(BankBytes[0]) == 0x1F ? 0xF8000 : FileOffset(rom.ReadByte(BankBytes[0]) << 16 | 0x8000);
    private static int Block(RomBuffer rom)
    {
        if (rom.ReadByte(BankBytes[0]) == 0x1F) return 0;
        int bank = AreaBank(rom);
        return bank + State + 4 <= rom.Length && rom.ReadBytes(bank + State, 4).SequenceEqual(Magic) ? bank : 0;
    }
    public static bool IsMoved(RomBuffer rom) => Block(rom) > 0;
    public static bool IsOriginal(RomBuffer rom) => BankBytes.All(offset => rom.ReadByte(offset) == 0x1F) && rom.ReadBytes(ResourceSite, 8).SequenceEqual(ResourceOriginal);

    // file offset of a 16-bit address in the area's bank
    private static int At(RomBuffer rom, int addr) => AreaBank(rom) + (addr & 0x7FFF);

    public static int Count(RomBuffer rom) { int block = Block(rom); return block > 0 ? rom.ReadByte(block + State + 4) : OriginalCount; }
    private static int ShapeCount(RomBuffer rom) { int block = Block(rom); return block > 0 ? rom.ReadByte(block + State + 5) : OriginalShapes; }

    private static int HeaderOffset(RomBuffer rom, int id)
    {
        int table = Block(rom) is int block and > 0 ? block + Headers : At(rom, 0xEE02);
        return At(rom, rom.ReadUInt16Le(table + id * 2));
    }

    // the resource with an animation's sheet
    public static int Resource(RomBuffer rom, int id)
    {
        int header = HeaderOffset(rom, id), resourceByte = rom.ReadByte(header);
        return resourceByte == 0xFF && Block(rom) is int block and > 0 ? rom.ReadUInt16Le(block + Resources + id * 2) : resourceByte;
    }

    public sealed record Info(int Resource, int Width, int Height, bool FromBottom, int Tracks);
    public static Info Describe(RomBuffer rom, int id)
    {
        int header = HeaderOffset(rom, id);
        return new Info(Resource(rom, id), rom.ReadByte(header + 1), rom.ReadByte(header + 2) & 0x7F, (rom.ReadByte(header + 2) & 0x80) != 0, rom.ReadByte(header + 3));
    }

    // for an animation made by Import / ImportParts: its pictures in play order (the sheet resource each one is on
    // and where), their size and how many screen frames each one stays. null for the game's own animations (their
    // track scripts move, tint and combine shapes, we don't preview those)
    public static (int Width, int Height, int Step, List<(int Resource, int X, int Y)> Frames)? MadeFrames(RomBuffer rom, int id)
    {
        if (id <= 0 || id >= Count(rom) || Block(rom) == 0) return null;
        int header = HeaderOffset(rom, id);
        if (rom.ReadByte(header) != 0xFF || rom.ReadByte(header + 3) < 1) return null;
        int width = rom.ReadByte(header + 1), height = rom.ReadByte(header + 2) & 0x7F;
        int track = At(rom, rom.ReadUInt16Le(header + 4));
        int step = rom.ReadByte(track) & 0x0F;
        if ((rom.ReadByte(track) & 0xF0) != 0x20) return null;
        int resource = Resource(rom, id);
        var frames = new List<(int, int, int)>();
        for (int position = track + 1; frames.Count < 400;)
        {
            int op = rom.ReadByte(position);
            // a sheet kept unpacked (ImportParts): its address, negative to tell it apart from a resource
            if (op == 0x70) { resource = -(rom.ReadByte(position + 1) | rom.ReadByte(position + 2) << 8 | rom.ReadByte(position + 3) << 16); position += 4; continue; }
            if (op != 0x50 || rom.ReadByte(position + 4) != 0x0F) break;
            int tile = rom.ReadByte(position + 3);
            frames.Add((resource, tile % 16 * 8, tile / 16 * 8));
            position += 5;
        }
        return frames.Count > 0 ? (width, height, Math.Max(1, step), frames) : null;
    }

    // a sheet's pictures (like Sheet): a resource, or (negative) the address of one kept unpacked
    public static (byte[] Indices, uint[] Palette) SheetOfResource(RomBuffer rom, int resource) =>
        resource >= 0 ? SheetData(rom, resource) : SheetFromBytes(rom.ReadBytes(FileOffset(-resource), 0x40 + 128 * 32));

    // sheets

    public const int SheetWidth = 128, SheetHeight = 64;

    // the animation's sheet: colour numbers (128 x 64) and its 16 colours (BGRA, 0 transparent)
    public static (byte[] Indices, uint[] Palette) Sheet(RomBuffer rom, int id) => SheetData(rom, Resource(rom, id));

    private static (byte[] Indices, uint[] Palette) SheetData(RomBuffer rom, int resource) =>
        SheetFromBytes(LufiaCompression.DecompressResource(rom, resource, out _));

    private static (byte[] Indices, uint[] Palette) SheetFromBytes(byte[] data)
    {
        var palette = new uint[16];
        for (int c = 1; c < 16 && c * 2 + 1 < data.Length; c++)
        {
            int bgr = data[c * 2] | data[c * 2 + 1] << 8;
            palette[c] = 0xFF000000u | (uint)((bgr & 31) << 3) << 16 | (uint)((bgr >> 5 & 31) << 3) << 8 | (uint)((bgr >> 10 & 31) << 3);
        }
        var indices = new byte[SheetWidth * SheetHeight];
        for (int t = 0; t < 128 && 0x40 + t * 32 + 32 <= data.Length; t++)
        {
            var tilePixels = Modules.TileViewer.SnesTileDecoder.DecodeTile(data, 0x40 + t * 32, Modules.TileViewer.BitDepth.Bpp4);
            int tx = t % 16 * 8, ty = t / 16 * 8;
            for (int y = 0; y < 8; y++) for (int x = 0; x < 8; x++) indices[(ty + y) * SheetWidth + tx + x] = tilePixels[y * 8 + x];
        }
        return (indices, palette);
    }

    // a sheet as the resource the engine loads: palette, 32 unused bytes, 128 tiles
    private static byte[] BuildResource(byte[] indices, IReadOnlyList<uint> palette)
    {
        var data = new byte[0x40 + 128 * 32];
        for (int c = 1; c < 16 && c < palette.Count; c++)
        {
            uint colour = palette[c];
            int bgr = (int)(colour >> 16 & 255) >> 3 | ((int)(colour >> 8 & 255) >> 3) << 5 | ((int)(colour & 255) >> 3) << 10;
            data[c * 2] = (byte)bgr; data[c * 2 + 1] = (byte)(bgr >> 8);
        }
        for (int t = 0; t < 128; t++)
        {
            var tilePixels = new byte[64];
            int tx = t % 16 * 8, ty = t / 16 * 8;
            for (int y = 0; y < 8; y++) for (int x = 0; x < 8; x++) tilePixels[y * 8 + x] = (byte)(indices[(ty + y) * SheetWidth + tx + x] & 15);
            Modules.TileViewer.SnesTileDecoder.EncodeTile(tilePixels, Modules.TileViewer.BitDepth.Bpp4).CopyTo(data, 0x40 + t * 32);
        }
        return data;
    }

    // new animations

    // moves the animation area to a bank of its own (see the notes at the top)
    public static void Move(RomBuffer rom)
    {
        if (IsMoved(rom)) return;
        if (!IsOriginal(rom)) throw new InvalidOperationException("The game's battle animation code isn't the original, so new animations can't be added safely.");
        if (rom.Length < MapWriter.ExpandedSize) rom.Expand(MapWriter.ExpandedSize);
        var freeSpace = ExpansionSpace.Load(rom);
        int bankOffset = freeSpace.AllocateInOneBank(0x8000, ExpansionSpace.Kind.Patch, 18);
        freeSpace.Save();
        var bank = Enumerable.Repeat((byte)0xFF, 0x8000).ToArray();
        rom.ReadBytes(AreaFile, AreaSize).CopyTo(bank, AreaInBank);
        int bankByte = SnesAddress(bankOffset) >> 16, bankAddr = SnesAddress(bankOffset) & 0xFFFF;   // ($8000)
        // header table (256), resource table (256 words), shape table (256)
        for (int i = 0; i < Capacity * 2; i++) bank[Headers + i] = 0;
        Array.Copy(rom.ReadBytes(At(rom, 0xEE02), OriginalCount * 2), 0, bank, Headers, OriginalCount * 2);
        for (int i = 0; i < Capacity * 2; i++) bank[Resources + i] = 0;
        for (int i = 0; i < Capacity * 2; i++) bank[Shapes + i] = 0;
        int oldShapes = rom.ReadUInt16Le(At(rom, 0xEE00));
        Array.Copy(rom.ReadBytes(At(rom, oldShapes), OriginalShapes * 2), 0, bank, Shapes, OriginalShapes * 2);
        int shapes = bankAddr + Shapes;
        bank[AreaInBank] = (byte)shapes; bank[AreaInBank + 1] = (byte)(shapes >> 8);   // the copy's $EE00 word
        // hook: header resource byte FF = a 16-bit id from the table (A8 X16 in, X = header address, $16 = animation id)
        int resourceTable = bankAddr + Resources;
        var code = new byte[]
        {
            0xBF, 0x00, 0x00, (byte)bankByte,          // LDA bank:0000,X  (the header's resource byte)
            0xC9, 0xFF, 0xD0, 0x14,                    // CMP #$FF; BNE plain
            0xDA, 0xC2, 0x20, 0xA5, 0x16,              // PHX; REP #$20; LDA $16
            0x29, 0xFF, 0x00, 0x0A, 0xAA,              // AND #$00FF; ASL; TAX
            0xBF, (byte)resourceTable, (byte)(resourceTable >> 8), (byte)bankByte,   // LDA resources,X
            0x85, 0x16, 0xE2, 0x20, 0xFA, 0x6B,        // STA $16; SEP #$20; PLX; RTL
            0x85, 0x16, 0x64, 0x17, 0x6B,              // plain: STA $16; STZ $17; RTL
        };
        code.CopyTo(bank, Hook);
        Magic.CopyTo(bank, State);
        bank[State + 4] = OriginalCount; bank[State + 5] = OriginalShapes;
        bank[State + 6] = DataStart & 0xFF; bank[State + 7] = DataStart >> 8;
        rom.WriteBytes(bankOffset, bank);
        // point the code at the new bank
        foreach (int bankByteAt in BankBytes) rom.WriteByte(bankByteAt, (byte)bankByte);
        int headers = bankAddr + Headers;
        rom.WriteByte(HeaderTableOp, (byte)headers); rom.WriteByte(HeaderTableOp + 1, (byte)(headers >> 8));
        int hookAddress = SnesAddress(bankOffset + Hook);
        rom.WriteBytes(ResourceSite, new byte[] { 0x22, (byte)hookAddress, (byte)(hookAddress >> 8), (byte)(hookAddress >> 16), 0xEA, 0xEA, 0xEA, 0xEA });
        rom.FixChecksum();
    }

    // bytes of the new bank still free for headers, shapes and scripts
    public static int Room(RomBuffer rom) { int block = Block(rom); return block > 0 ? AreaInBank - rom.ReadUInt16Le(block + State + 6) : AreaInBank - DataStart; }

    private static int Put(RomBuffer rom, byte[] data)
    {
        int block = Block(rom), freeAt = rom.ReadUInt16Le(block + State + 6);
        if (freeAt + data.Length > AreaInBank) throw new InvalidOperationException("No room left for more animations.");
        rom.WriteBytes(block + freeAt, data);
        rom.WriteUInt16Le(block + State + 6, (ushort)(freeAt + data.Length));
        return (SnesAddress(block) & 0xFFFF) + freeAt;
    }

    // Order: the sheet frames to show, in play order (null = 0 to Frames - 1), so parts can repeat
    public sealed record ImportOptions(int FrameWidth, int FrameHeight, int Frames, int StepFrames, bool FromBottom, uint? FlashColour,
        IReadOnlyList<int>? Order = null);

    // the most frames that fit for a frame size (the sheet is 8 x 4 cells of 16 x 16)
    public static int MaxFrames(int width, int height) => width % 16 != 0 || height % 16 != 0 || width < 16 || height < 16 || width > 80 || height > 64 ? 0 : 8 / (width / 16) * (4 / (height / 16));

    // a new animation from a sheet (128 x 64, colour numbers 0-15, 0 = see-through): frames of the given size taken
    // left to right, top to bottom, each shown ImportOptions.StepFrames frames, centred on the target
    // (or standing on its bottom), with an optional screen tint while it plays. returns its id
    public static int Import(RomBuffer rom, byte[] indices, IReadOnlyList<uint> palette, ImportOptions options)
    {
        if (indices.Length != SheetWidth * SheetHeight) throw new InvalidOperationException($"The sheet must be {SheetWidth} x {SheetHeight} pixels.");
        int max = MaxFrames(options.FrameWidth, options.FrameHeight);
        if (max == 0) throw new InvalidOperationException("Frames are 16-80 pixels wide and 16-64 high, in steps of 16.");
        if (options.Frames < 1 || options.Frames > max) throw new InvalidOperationException($"A {options.FrameWidth} x {options.FrameHeight} frame fits {max} times on the sheet.");
        Move(rom);
        int id = Count(rom), shape = ShapeCount(rom);
        // (the counts are kept in a byte, 255 at most, so the last number is FE)
        if (id >= Capacity - 1 || shape >= Capacity - 1) throw new InvalidOperationException("No animation numbers left.");
        int block = Block(rom);
        int resource = ResourceIds.Add(rom, BuildResource(indices, palette));
        block = Block(rom);
        // the frame shape: the cells of the first frame (the script shifts it to the others)
        int cols = options.FrameWidth / 16, rows = options.FrameHeight / 16;
        var shapeBytes = new List<byte>(ShapePrefix) { (byte)(cols << 4 | rows) };
        for (int row = 0; row < rows; row++) for (int col = 0; col < cols; col++) shapeBytes.Add((byte)(0x80 + 2 * col + 0x20 * row));
        int shapeAt = Put(rom, shapeBytes.ToArray());
        rom.WriteUInt16Le(block + Shapes + shape * 2, (ushort)shapeAt);
        // the frames
        var track = new List<byte> { (byte)(0x20 | Math.Clamp(options.StepFrames, 1, 15)) };
        int perRow = 8 / cols;
        var order = options.Order ?? Enumerable.Range(0, options.Frames).ToList();
        if (order.Count > 400) throw new InvalidOperationException("An animation can show at most 400 pictures (repeats included).");
        foreach (int frame in order)
        {
            if (frame < 0 || frame >= options.Frames) throw new InvalidOperationException($"Frame {frame} isn't on the sheet.");
            int cx = frame % perRow * cols, cy = frame / perRow * rows;
            track.AddRange(new byte[] { 0x50, (byte)shape, 0x03, (byte)(2 * cx + 0x20 * cy), 0x0F });
        }
        track.Add(0x01);
        int track0 = Put(rom, track.ToArray());
        var tracks = new List<int> { track0 };
        if (options.FlashColour is uint flashColour)
        {
            int bgr = (int)(flashColour >> 16 & 255) >> 3 | ((int)(flashColour >> 8 & 255) >> 3) << 5 | ((int)(flashColour & 255) >> 3) << 10;
            int steps = Math.Max(1, order.Count * Math.Clamp(options.StepFrames, 1, 15) / 2);
            var flash = new List<byte> { 0x22, 0xBF, (byte)bgr, (byte)(bgr >> 8) };
            for (int i = 0; i < Math.Min(steps, 30); i++) flash.Add(0x0F);
            flash.AddRange(new byte[] { 0xBF, 0x00, 0x00, 0x01 });
            tracks.Add(Put(rom, flash.ToArray()));
        }
        var headerBytes = new List<byte> { 0xFF, (byte)options.FrameWidth, (byte)(options.FrameHeight | (options.FromBottom ? 0x80 : 0)), (byte)tracks.Count };
        foreach (int trackAddress in tracks) { headerBytes.Add((byte)trackAddress); headerBytes.Add((byte)(trackAddress >> 8)); }
        int header = Put(rom, headerBytes.ToArray());
        rom.WriteUInt16Le(block + Headers + id * 2, (ushort)header);
        rom.WriteUInt16Le(block + Resources + id * 2, (ushort)resource);
        rom.WriteByte(block + State + 4, (byte)(id + 1));
        rom.WriteByte(block + State + 5, (byte)(shape + 1));
        rom.FixChecksum();
        return id;
    }
}
