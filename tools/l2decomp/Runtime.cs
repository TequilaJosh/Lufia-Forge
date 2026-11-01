// The CPU model the decompiled Lufia II code is written against: registers, flags, memory.
// It is here so the code compiles (which checks the decompiler's structure); it does not run the game.
namespace Lufia2.Decomp;

using System;

public sealed class Memory
{
    private readonly int _width;
    public Memory(int width) { _width = width; }
    /// <summary>The byte, word or 24-bit value at a 24-bit address.</summary>
    public int this[int address]
    {
        get => 0;
        set { }
    }
}

public static partial class Game
{
    // registers: A is the full 16-bit accumulator, AL its low byte (8-bit A mode)
    private static int _a, _x, _y;
    public static int A { get => _a; set => _a = value & 0xFFFF; }
    public static int AL { get => _a & 0xFF; set => _a = (_a & 0xFF00) | (value & 0xFF); }
    public static int X { get => _x; set => _x = value & 0xFFFF; }
    public static int Y { get => _y; set => _y = value & 0xFFFF; }
    public static int S, D, DB;
    public static bool N, Z, C, V, DecimalMode, EmulationMode;

    public static readonly Memory m8 = new(1), m16 = new(2), m24 = new(3);

    /// <summary>A 16-bit pointer stored at <paramref name="at"/>, in the data bank.</summary>
    public static int Ptr16(int at) => DB << 16 | m16[at];
    /// <summary>A 24-bit pointer stored at <paramref name="at"/>.</summary>
    public static int Ptr24(int at) => m24[at];
    /// <summary>An address in the data bank (DB unknown when the code was read).</summary>
    public static int Db(int address) => DB << 16 | address;
    /// <summary>A direct-page address (D unknown when the code was read).</summary>
    public static int Dp(int address) => (D + address) & 0xFFFF;
    /// <summary>A routine the decompiler couldn't follow (code in RAM).</summary>
    public static Action Far(int address) => () => { };

    public static int Swap(int a) => (a >> 8 | a << 8) & 0xFFFF;
    public static int Adc8(int a, int b) { int r = a + b + (C ? 1 : 0); C = r > 0xFF; return r & 0xFF; }
    public static int Adc16(int a, int b) { int r = a + b + (C ? 1 : 0); C = r > 0xFFFF; return r & 0xFFFF; }
    public static int Sbc8(int a, int b) { int r = a - b - (C ? 0 : 1); C = r >= 0; return r & 0xFF; }
    public static int Sbc16(int a, int b) { int r = a - b - (C ? 0 : 1); C = r >= 0; return r & 0xFFFF; }
    public static int Rol8(int v) { int r = v << 1 | (C ? 1 : 0); C = (v & 0x80) != 0; return r & 0xFF; }
    public static int Rol16(int v) { int r = v << 1 | (C ? 1 : 0); C = (v & 0x8000) != 0; return r & 0xFFFF; }
    public static int Ror8(int v) { int r = v >> 1 | (C ? 0x80 : 0); C = (v & 1) != 0; return r; }
    public static int Ror16(int v) { int r = v >> 1 | (C ? 0x8000 : 0); C = (v & 1) != 0; return r; }

    public static void Push8(int v) { }
    public static void Push16(int v) { }
    public static int Pop8() => 0;
    public static int Pop16() => 0;
    /// <summary>PHP: saves the flags (the code mostly uses it to keep the A and X/Y sizes).</summary>
    public static void PushFlags() { }
    /// <summary>PLP: restores the flags saved by <see cref="PushFlags"/>.</summary>
    public static void PullFlags() { }

    public static void DisableInterrupts() { }
    public static void EnableInterrupts() { }
    public static void WaitForInterrupt() { }
    public static void ExchangeCarryAndEmulation() { }
    public static void Stop() { }
    public static void Brk(int n) { }
    public static void Cop(int n) { }
    public static void Wdm(int n) { }
    /// <summary>MVN: copies A + 1 bytes from fromBank:X up to toBank:Y.</summary>
    public static void MoveBlockUp(int fromBank, int toBank) { }
    /// <summary>MVP: copies A + 1 bytes down, ending at fromBank:X and toBank:Y.</summary>
    public static void MoveBlockDown(int fromBank, int toBank) { }
    public static void JumpIndirect(int address) { }
    public static void JumpIndirectLong(int address) { }
}
