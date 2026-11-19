namespace LufiaForge.Core.Lufia2;

// 128 more story flags for lufia 2 (tools/flags/extflags.asm, 2026-11-16): flags 100-17F, kept in the bytes of event
// counters 10-1F ($7E:07AE-$7E:07BD), which the game never uses. those bytes are in the field state a save keeps and a
// new game clears, so the extra flags get saved, loaded and cleared along with the story flags (old saves still load:
// their counters 10-1F are 0, every extra flag off)
// - commands CD e / CE e: turn flag 100 + e on / off (the game's table ends at CC, the patch sends CD-D0 to the added
//   code). CF e w / D0 e w: if it's on / off, go to w (like 15 / 6A)
// - command 14: a story flag check with bit 02 of its code set checks flag 100 + its byte (the game's checks never set it)
// in the editor a flag field holds 000-17F and picks the command itself: 1A / CD, 1B / CE, 15 / CF, 6A / D0
public static partial class L2ExtraFlags
{
    public const int First = 0x100, Count = 0x80, Last = First + Count - 1;
    // the counters whose bytes hold the extra flags
    public const int FirstCounter = 0x10, LastCounter = 0x1F;
    public const byte OpOn = 0xCD, OpOff = 0xCE, OpIfOn = 0xCF, OpIfOff = 0xD0;
    // bit of a command 14 flag check's code: the flag is 100 + its byte
    public const int CheckBit = 0x02;

    // (made on first use: the sites and code are in the other part of the class, whose fields might not be set yet here)
    private static HookPatch? _patch;
    private static HookPatch Patch => _patch ??= new(Sites, Code);

    // the patch is in the rom
    public static bool IsApplied(RomBuffer rom) => Patch.AppliedAt(rom, L2Rom.SnesToFile) >= 0;

    // file offset of the patch's added code, or -1
    public static int BlockAt(RomBuffer rom) => Patch.AppliedAt(rom, L2Rom.SnesToFile);

    // the patch can go in: the places it changes still hold the US game's bytes
    public static bool CanApply(RomBuffer rom) => IsApplied(rom) || Patch.IsOriginal(rom);

    // adds the patch (nothing if it's in already)
    public static void Apply(RomBuffer rom)
    {
        if (IsApplied(rom)) return;
        if (!Patch.IsOriginal(rom))
            throw new InvalidOperationException("The event code the extra flags change isn't the US game's (another patch or a fan hack changed it), so they can't be added safely.");
        L2Rom.AllOrNothing(rom, () =>
        {
            int codeAt = L2Rom.Allocate(rom, Patch.Code.Bytes.Length);
            Patch.Write(rom, codeAt, L2Rom.FileToSnes(codeAt));
            return codeAt;
        });
    }

    public static bool IsExtra(int flag) => flag is >= First and <= Last;

    // counters 10-1F hold the extra flags once the patch is in
    public static bool HoldsFlags(int counter) => counter is >= FirstCounter and <= LastCounter;

    // the WRAM address ($7E:xxxx) and bit mask of a story flag (00-FF) or extra flag (100-17F)
    public static (int Address, int Mask) Ram(int flag) =>
        (flag < First ? 0x077E + (flag >> 3) : 0x07AE + ((flag - First) >> 3), 1 << (flag & 7));

    public enum Use { None, On, Off, IfOn, IfOff }

    // what a step does with a story flag, and which flag (1A / 1B / 15 / 6A and the extra flag commands)
    public static (Use Use, int Flag) Of(byte[] bytes)
    {
        if (bytes.Length < 2) return (Use.None, -1);
        return bytes[0] switch
        {
            0x1A => (Use.On, bytes[1]),
            0x1B => (Use.Off, bytes[1]),
            0x15 when bytes.Length >= 4 => (Use.IfOn, bytes[1]),
            0x6A when bytes.Length >= 4 => (Use.IfOff, bytes[1]),
            OpOn => (Use.On, First + bytes[1]),
            OpOff => (Use.Off, First + bytes[1]),
            OpIfOn when bytes.Length >= 4 => (Use.IfOn, First + bytes[1]),
            OpIfOff when bytes.Length >= 4 => (Use.IfOff, First + bytes[1]),
            _ => (Use.None, -1),
        };
    }

    // the flag of a flag step (Of), or -1
    public static int FlagOf(byte[] bytes) => Of(bytes).Flag;

    // sets the flag of a flag step, switching between the story flag command and the extra flag one
    public static void SetFlag(byte[] bytes, int flag)
    {
        var (use, _) = Of(bytes);
        if (use == Use.None) return;
        bool extra = flag >= First;
        bytes[0] = use switch
        {
            Use.On => extra ? OpOn : (byte)0x1A,
            Use.Off => extra ? OpOff : (byte)0x1B,
            Use.IfOn => extra ? OpIfOn : (byte)0x15,
            _ => extra ? OpIfOff : (byte)0x6A,
        };
        bytes[1] = (byte)(extra ? flag - First : flag);
    }

    // the bytes of a step that does use with a flag (jumps: step offset 0)
    public static byte[] Step(Use use, int flag)
    {
        var bytes = use is Use.IfOn or Use.IfOff ? new byte[4] : new byte[2];
        bytes[0] = use switch { Use.On => 0x1A, Use.Off => 0x1B, Use.IfOn => 0x15, _ => 0x6A };
        SetFlag(bytes, flag);
        return bytes;
    }

    // "07", "105": how a flag gets written
    public static string Number(int flag) => flag >= First ? flag.ToString("X3") : flag.ToString("X2");

    // the flag of a command 14 story flag check whose code is code and byte is value
    public static int CheckFlag(int code, int value) => (code & CheckBit) != 0 ? First + value : value;

    // a command 14 check's code and byte for flag, keeping the code's other bits
    public static (int Code, int Value) CheckBytes(int code, int flag) =>
        flag >= First ? (code | CheckBit, flag - First) : (code & ~CheckBit, flag);

    // a script (its steps' bytes) uses an extra flag, so the patch has to be in
    public static bool Uses(IEnumerable<byte[]> steps) => steps.Any(b =>
        b.Length > 0 && b[0] is OpOn or OpOff or OpIfOn or OpIfOff ||
        b.Length > 0 && b[0] == 0x14 && L2Conditions.Parse(b).Any(c => c.Kind == L2Conditions.KFlag && (c.Code & CheckBit) != 0));
}
