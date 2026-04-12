using CommunityToolkit.Mvvm.ComponentModel;

namespace LufiaForge.Modules.Disassembler;

/// <summary>
/// One decoded 65816 instruction (or data annotation) in the disassembly.
/// Extends ObservableObject so IsCurrentPc can drive live-PC highlighting without a full list rebuild.
/// </summary>
public sealed partial class DisassemblyLine : ObservableObject
{
    // -------------------------------------------------------------------------
    // Immutable decode-time fields (set via init)
    // -------------------------------------------------------------------------

    /// <summary>File offset in the ROM where this instruction starts.</summary>
    public int FileOffset { get; init; }

    /// <summary>SNES (LoROM) address for display, e.g. $808000.</summary>
    public int SnesAddress { get; init; }

    /// <summary>Raw bytes of the instruction (opcode + operand bytes).</summary>
    public byte[] RawBytes { get; init; } = Array.Empty<byte>();

    public string Mnemonic { get; init; } = string.Empty;
    public string Operand  { get; init; } = string.Empty;

    /// <summary>User-attached or auto-generated annotation (bookmark label, flag-change note).</summary>
    public string? Comment { get; set; }

    /// <summary>
    /// Resolved target SNES address for branches and calls.
    /// Null for indirect jumps or data lines.
    /// </summary>
    public int? JumpTarget { get; init; }

    public bool IsJump        { get; init; }
    public bool IsCall        { get; init; }
    public bool IsReturn      { get; init; }
    public bool IsConditional { get; init; }

    /// <summary>true when this byte could not be decoded as a valid instruction.</summary>
    public bool IsData { get; init; }

    /// <summary>
    /// If this is a REP or SEP instruction, the CPU state after it executes.
    /// Used by LinearDisassembler to propagate flag state through the routine.
    /// </summary>
    public CpuState? StateAfter { get; init; }

    // -------------------------------------------------------------------------
    // Mutable live-tracking state
    // -------------------------------------------------------------------------

    /// <summary>Set to true when the live BizHawk PC is at this instruction's address.</summary>
    [ObservableProperty] private bool _isCurrentPc;

    // -------------------------------------------------------------------------
    // Derived display helpers
    // -------------------------------------------------------------------------

    /// <summary>Formatted raw bytes, e.g. "AD 10 21".</summary>
    public string RawBytesHex => string.Join(" ", RawBytes.Select(b => b.ToString("X2")));

    /// <summary>Formatted SNES address, e.g. "$808000".</summary>
    public string SnesAddressHex => $"${SnesAddress:X6}";

    /// <summary>True when there is a statically-resolved jump/call target to navigate to.</summary>
    public bool HasJumpTarget => JumpTarget.HasValue && !IsData;
}
