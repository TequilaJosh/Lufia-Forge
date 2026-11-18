namespace LufiaForge.Modules.Disassembler;

// the bits of 65816 CPU state that change how instructions decode
// M=false means a 16-bit accumulator, X=false means 16-bit index registers
public record CpuState
{
    // accumulator size flag. true = 8-bit, false = 16-bit
    public bool M { get; init; } = true;

    // index register size flag. true = 8-bit, false = 16-bit
    public bool X { get; init; } = true;

    // direct page register (D), changes how DP addresses are shown
    public int DirectPage { get; init; } = 0;

    // data bank register (DB), changes how absolute addresses are shown
    public int DataBank { get; init; } = 0;

    // default state: 8-bit M and X (native mode after reset / emulation mode)
    public static readonly CpuState Default = new();

    // all 16-bit: M=false, X=false (after REP #$30)
    public static readonly CpuState Bits16 = new() { M = false, X = false };
}
