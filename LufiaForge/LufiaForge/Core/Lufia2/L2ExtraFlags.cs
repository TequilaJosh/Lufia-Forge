namespace LufiaForge.Core.Lufia2;

/// <summary>
/// 128 more story flags for Lufia II (tools/flags/extflags.asm, 2026-11-16): flags 100-17F, kept in the bytes of event
/// counters 10-1F ($7E:07AE-$7E:07BD), which the game never uses. Those bytes are in the field state a save keeps and a
/// new game clears, so the extra flags are saved, loaded and cleared with the story flags (old saves load: their
/// counters 10-1F are 0, every extra flag off).
/// <list type="bullet">
/// <item>Commands CD e / CE e: turn flag 100 + e on / off (the game's table ends at CC; the patch sends CD-D0 to the
/// added code). CF e w / D0 e w: if it is on / off, go to w (as 15 / 6A).</item>
/// <item>Command 14: a story flag check with bit 02 of its code set checks flag 100 + its byte (the game's checks never
/// set it).</item>
/// </list>
/// In the editor a flag field holds 000-17F and picks the command itself: 1A / CD, 1B / CE, 15 / CF, 6A / D0.
/// </summary>
public static partial class L2ExtraFlags
{
    public const int First = 0x100, Count = 0x80, Last = First + Count - 1;
    /// <summary>The counters whose bytes hold the extra flags.</summary>
    public const int FirstCounter = 0x10, LastCounter = 0x1F;
    public const byte OpOn = 0xCD, OpOff = 0xCE, OpIfOn = 0xCF, OpIfOff = 0xD0;
    /// <summary>Bit of a command 14 flag check's code: the flag is 100 + its byte.</summary>
    public const int CheckBit = 0x02;

    // (made on first use: the sites and code are in the other part of the class, whose fields may not be set yet here)
    private static HookPatch? _patch;
    private static HookPatch Patch => _patch ??= new(Sites, Code);

    /// <summary>The patch is in the ROM.</summary>
    public static bool IsApplied(RomBuffer rom) => Patch.AppliedAt(rom, L2Rom.SnesToFile) >= 0;

    /// <summary>File offset of the patch's added code, or -1.</summary>
    public static int BlockAt(RomBuffer rom) => Patch.AppliedAt(rom, L2Rom.SnesToFile);

    /// <summary>The patch can go in: the places it changes still hold the US game's bytes.</summary>
    public static bool CanApply(RomBuffer rom) => IsApplied(rom) || Patch.IsOriginal(rom);

    /// <summary>Adds the patch (nothing when it is in already).</summary>
    public static void Apply(RomBuffer rom)
    {
        if (IsApplied(rom)) return;
        if (!Patch.IsOriginal(rom))
            throw new InvalidOperationException("The event code the extra flags change isn't the US game's (another patch or a fan hack changed it), so they can't be added safely.");
        L2Rom.AllOrNothing(rom, () =>
        {
            int at = L2Rom.Allocate(rom, Patch.Code.Bytes.Length);
            Patch.Write(rom, at, L2Rom.FileToSnes(at));
            return at;
        });
    }

    public static bool IsExtra(int flag) => flag is >= First and <= Last;

    /// <summary>Counters 10-1F hold the extra flags once the patch is in.</summary>
    public static bool HoldsFlags(int counter) => counter is >= FirstCounter and <= LastCounter;

    /// <summary>The WRAM address ($7E:xxxx) and bit mask of a story flag (00-FF) or extra flag (100-17F).</summary>
    public static (int Address, int Mask) Ram(int flag) =>
        (flag < First ? 0x077E + (flag >> 3) : 0x07AE + ((flag - First) >> 3), 1 << (flag & 7));

    public enum Use { None, On, Off, IfOn, IfOff }

    /// <summary>What a step does with a story flag, and which flag (1A / 1B / 15 / 6A and the extra flag commands).</summary>
    public static (Use Use, int Flag) Of(byte[] b)
    {
        if (b.Length < 2) return (Use.None, -1);
        return b[0] switch
        {
            0x1A => (Use.On, b[1]),
            0x1B => (Use.Off, b[1]),
            0x15 when b.Length >= 4 => (Use.IfOn, b[1]),
            0x6A when b.Length >= 4 => (Use.IfOff, b[1]),
            OpOn => (Use.On, First + b[1]),
            OpOff => (Use.Off, First + b[1]),
            OpIfOn when b.Length >= 4 => (Use.IfOn, First + b[1]),
            OpIfOff when b.Length >= 4 => (Use.IfOff, First + b[1]),
            _ => (Use.None, -1),
        };
    }

    /// <summary>The flag of a flag step (<see cref="Of"/>), or -1.</summary>
    public static int FlagOf(byte[] b) => Of(b).Flag;

    /// <summary>Sets the flag of a flag step, switching between the story flag command and the extra flag one.</summary>
    public static void SetFlag(byte[] b, int flag)
    {
        var (use, _) = Of(b);
        if (use == Use.None) return;
        bool extra = flag >= First;
        b[0] = use switch
        {
            Use.On => extra ? OpOn : (byte)0x1A,
            Use.Off => extra ? OpOff : (byte)0x1B,
            Use.IfOn => extra ? OpIfOn : (byte)0x15,
            _ => extra ? OpIfOff : (byte)0x6A,
        };
        b[1] = (byte)(extra ? flag - First : flag);
    }

    /// <summary>The bytes of a step that does <paramref name="use"/> with a flag (jumps: step offset 0).</summary>
    public static byte[] Step(Use use, int flag)
    {
        var b = use is Use.IfOn or Use.IfOff ? new byte[4] : new byte[2];
        b[0] = use switch { Use.On => 0x1A, Use.Off => 0x1B, Use.IfOn => 0x15, _ => 0x6A };
        SetFlag(b, flag);
        return b;
    }

    /// <summary>"07", "105": how a flag is written.</summary>
    public static string Number(int flag) => flag >= First ? flag.ToString("X3") : flag.ToString("X2");

    /// <summary>The flag of a command 14 story flag check whose code is <paramref name="code"/> and byte <paramref name="value"/>.</summary>
    public static int CheckFlag(int code, int value) => (code & CheckBit) != 0 ? First + value : value;

    /// <summary>A command 14 check's code and byte for <paramref name="flag"/>, keeping the code's other bits.</summary>
    public static (int Code, int Value) CheckBytes(int code, int flag) =>
        flag >= First ? (code | CheckBit, flag - First) : (code & ~CheckBit, flag);

    /// <summary>A script (its steps' bytes) uses an extra flag: the patch has to be in.</summary>
    public static bool Uses(IEnumerable<byte[]> steps) => steps.Any(b =>
        b.Length > 0 && b[0] is OpOn or OpOff or OpIfOn or OpIfOff ||
        b.Length > 0 && b[0] == 0x14 && L2Conditions.Parse(b).Any(c => c.Kind == L2Conditions.KFlag && (c.Code & CheckBit) != 0));
}
