using CommunityToolkit.Mvvm.ComponentModel;

namespace LufiaForge.Modules.Disassembler;

// one decoded 65816 instruction (or data line) in the disassembly
// it's an ObservableObject so IsCurrentPc can move the live PC highlight without rebuilding the whole list
public sealed partial class DisassemblyLine : ObservableObject
{
    // set once when decoding

    // file offset in the ROM where the instruction starts
    public int FileOffset { get; init; }

    // SNES (LoROM) address for display, like $808000
    public int SnesAddress { get; init; }

    // the instruction's bytes (opcode + operand bytes)
    public byte[] RawBytes { get; init; } = Array.Empty<byte>();

    public string Mnemonic { get; init; } = string.Empty;
    public string Operand  { get; init; } = string.Empty;

    // the user's note or one we made up (bookmark label, flag change note)
    public string? Comment { get; set; }

    // SNES address a branch or call goes to. null for indirect jumps or data lines
    public int? JumpTarget { get; init; }

    public bool IsJump        { get; init; }
    public bool IsCall        { get; init; }
    public bool IsReturn      { get; init; }
    public bool IsConditional { get; init; }

    // true when the byte couldn't be decoded as a real instruction
    public bool IsData { get; init; }

    // for a REP or SEP, the CPU state after it runs.
    // LinearDisassembler uses it to carry the flag state through the routine
    public CpuState? StateAfter { get; init; }

    // base cycle count (0 for data lines), 16-bit registers and page crossings add more
    public int BaseCycles { get; init; }
    public string CyclesText => IsData || BaseCycles == 0 ? "" : $"{BaseCycles} cycles (base; +1 per 16-bit byte, direct page not aligned or page crossed)";

    // live tracking

    // true when the live BizHawk PC is at this instruction's address
    [ObservableProperty] private bool _isCurrentPc;

    // for display

    // the raw bytes formatted, like "AD 10 21"
    public string RawBytesHex => string.Join(" ", RawBytes.Select(b => b.ToString("X2")));

    // the SNES address formatted, like "$808000"
    public string SnesAddressHex => $"${SnesAddress:X6}";

    // true when there's a known jump/call target to go to
    public bool HasJumpTarget => JumpTarget.HasValue && !IsData;
}
