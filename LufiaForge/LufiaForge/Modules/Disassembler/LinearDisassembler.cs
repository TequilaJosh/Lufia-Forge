using LufiaForge.Core;

namespace LufiaForge.Modules.Disassembler;

// walks forward through the ROM from a start offset, decoding 65816 instructions one at a time
// until byteCount runs out or it hits an opcode that ends the routine
// what it can't do:
// - track M/X through indirect flag changes (like PLP). only REP and SEP are picked up and carried along
// - data decodes as garbage opcodes. mark it with BookmarkStore and disassemble again to clean it up
public static class LinearDisassembler
{
    // opcodes that end a pass (unconditional returns / break)
    private static readonly HashSet<byte> TerminatorOpcodes = [0x00, 0x40, 0x60, 0x6B];

    // disassembles up to byteCount bytes starting at startOffset (a file offset)
    // initialState = the CPU flag state at the entry point (M, X, D, DB)
    // returns the decoded lines in order, one per instruction
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
                    int count = Math.Min(8, stop - offset);
                    var bytes = rom.ReadBytes(offset, count);
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
                    offset += count;
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

            // move on by the instruction's size (or 1 on data lines)
            int step = line.RawBytes.Length > 0 ? line.RawBytes.Length : 1;
            offset += step;

            // carry the flag state from REP/SEP along
            if (line.StateAfter != null)
                state = line.StateAfter;

            // stop at unconditional ends
            if (!line.IsData && line.RawBytes.Length > 0 &&
                TerminatorOpcodes.Contains(line.RawBytes[0]))
                break;
        }

        return lines;
    }
}
