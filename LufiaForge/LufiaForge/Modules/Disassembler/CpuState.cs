namespace LufiaForge.Modules.Disassembler;

/// <summary>
/// The subset of 65816 CPU state that affects instruction decoding.
/// M=false means 16-bit accumulator; X=false means 16-bit index registers.
/// </summary>
public record CpuState
{
    /// <summary>Accumulator size flag. true = 8-bit, false = 16-bit.</summary>
    public bool M { get; init; } = true;

    /// <summary>Index register size flag. true = 8-bit, false = 16-bit.</summary>
    public bool X { get; init; } = true;

    /// <summary>Direct page register (D). Affects DP address display.</summary>
    public int DirectPage { get; init; } = 0;

    /// <summary>Data bank register (DB). Affects absolute address resolution for display.</summary>
    public int DataBank { get; init; } = 0;

    /// <summary>Default state: 8-bit M and X (native mode after reset / emulation mode).</summary>
    public static readonly CpuState Default = new();

    /// <summary>Fully 16-bit mode: M=false, X=false (after REP #$30).</summary>
    public static readonly CpuState Bits16 = new() { M = false, X = false };
}
