using LufiaForge.Core;

namespace LufiaForge.Modules.Disassembler;

/// <summary>
/// Walks forward through ROM bytes from a start offset, decoding 65816 instructions
/// one by one until <paramref name="byteCount"/> is exhausted or a terminal opcode is reached.
///
/// Known limitations:
/// - Cannot perfectly track M/X state through indirect flag changes (e.g. via PLP).
///   REP and SEP are detected and propagated; all others are not.
/// - Data regions will decode as garbage opcodes. Mark them with <see cref="BookmarkStore"/>
///   and re-disassemble to clean up.
/// </summary>
public static class LinearDisassembler
{
    // Opcodes that terminate a linear disassembly pass (unconditional returns / break)
    private static readonly HashSet<byte> TerminatorOpcodes = [0x00, 0x40, 0x60, 0x6B];

    /// <summary>
    /// Disassemble up to <paramref name="byteCount"/> bytes starting at <paramref name="startOffset"/>.
    /// </summary>
    /// <param name="rom">Source ROM buffer.</param>
    /// <param name="startOffset">File offset to begin decoding from.</param>
    /// <param name="byteCount">Maximum number of ROM bytes to consume.</param>
    /// <param name="initialState">CPU flag state at the entry point (M, X, D, DB).</param>
    /// <returns>Ordered list of decoded lines, one per instruction.</returns>
    public static List<DisassemblyLine> Disassemble(
        RomBuffer rom,
        int       startOffset,
        int       byteCount,
        CpuState  initialState,
        Func<int, (int Start, int End)?>? dataRegion = null)
    {
        var lines = new List<DisassemblyLine>();
        var state = initialState;

        int offset = startOffset;
        int end    = Math.Min(startOffset + byteCount, rom.Length);

        while (offset < end)
        {
            // marked data: show the bytes, 8 per line, instead of decoding them
            if (dataRegion?.Invoke(offset) is { } region)
            {
                int stop = Math.Min(region.End, end);
                while (offset < stop)
                {
                    int n = Math.Min(8, stop - offset);
                    var bytes = rom.ReadBytes(offset, n);
                    lines.Add(new DisassemblyLine
                    {
                        FileOffset = offset,
                        SnesAddress = Core.Lufia1Constants.FileOffsetToSnesAddress(offset),
                        RawBytes = bytes,
                        Mnemonic = ".db",
                        Operand = string.Join(",", bytes.Select(b => $"${b:X2}")),
                        Comment = "; data",
                        IsData = true,
                    });
                    offset += n;
                }
                continue;
            }

            var line = Cpu65816.DecodeInstruction(rom, offset, state);

            // an instruction that would run into marked data: show its bytes up to the data instead
            int overlap = dataRegion == null ? 0 : Enumerable.Range(1, Math.Max(0, line.RawBytes.Length - 1)).FirstOrDefault(k => dataRegion(offset + k) != null);
            if (overlap > 0)
            {
                var bytes = rom.ReadBytes(offset, overlap);
                lines.Add(new DisassemblyLine
                {
                    FileOffset = offset, SnesAddress = line.SnesAddress, RawBytes = bytes, Mnemonic = ".db",
                    Operand = string.Join(",", bytes.Select(b => $"${b:X2}")), Comment = "; runs into data", IsData = true,
                });
                offset += overlap;
                continue;
            }
            lines.Add(line);

            // Advance by the size of the decoded instruction (or 1 on data lines)
            int step = line.RawBytes.Length > 0 ? line.RawBytes.Length : 1;
            offset += step;

            // Propagate flag-change state from REP/SEP
            if (line.StateAfter != null)
                state = line.StateAfter;

            // Stop at unconditional terminators
            if (!line.IsData && line.RawBytes.Length > 0 &&
                TerminatorOpcodes.Contains(line.RawBytes[0]))
                break;
        }

        return lines;
    }
}
