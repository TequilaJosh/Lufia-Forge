using LufiaForge.Core;

namespace LufiaForge.Modules.Disassembler;

// sweeps the whole ROM in a straight line and collects call/jump cross-references:
// for each target address, the SNES addresses that call or jump to it
// it's a best guess, data that happens to decode as JSR/JMP-like opcodes gives false hits.
// bookmarks marked as data regions get skipped in the sweep
public sealed class CrossReferenceBuilder
{
    // target -> callers, filled in by Build. it's built on the side and swapped in whole when the build is done:
    // when it was filled on the worker thread while the panel read it, the dictionary broke ("collection was modified")
    public Dictionary<int, List<int>> Xref { get; private set; } = new();

    // indirect jumps/calls (JMP (a), JMP (a,X), JML [a], JSR (a,X)) where the target depends on RAM/registers
    public List<int> Unresolved { get; private set; } = new();

    // forgets the index (for a new ROM)
    public void Clear() { Xref = new(); Unresolved = new(); }

    // runs the sweep on a background thread
    // bookmarks = used to skip regions marked as data, progress = 0-100
    public Task BuildAsync(
        RomBuffer     rom,
        BookmarkStore bookmarks,
        IProgress<int>? progress = null,
        CancellationToken ct = default)
    {
        return Task.Run(() => Build(rom, bookmarks, progress, ct), ct);
    }

    private void Build(
        RomBuffer      rom,
        BookmarkStore  bookmarks,
        IProgress<int>? progress,
        CancellationToken ct)
    {
        var xref = new Dictionary<int, List<int>>();
        var unresolved = new List<int>();

        // walk the ROM byte by byte, 32KB (LoROM) banks at a time.
        // at each spot try to decode one instruction and collect its target
        int total   = rom.Length;
        int lastPercent = -1;

        // default CpuState: we can't track M/X right over the whole ROM, so we go with 8-bit to be safe.
        // most of the false hits come from data anyway, not from M/X being wrong on the opcodes we care about (JSR/JSL/JMP/JML/Bxx)
        var state = CpuState.Default;

        int offset = 0;
        while (offset < total)
        {
            ct.ThrowIfCancellationRequested();

            int percent = offset * 100 / total;
            if (percent != lastPercent)
            {
                progress?.Report(percent);
                lastPercent = percent;
            }

            // skip regions the user marked as data
            if (bookmarks.DataRegionAtFile(offset) is { } data) { offset = data.End; continue; }

            var line = Cpu65816.DecodeInstruction(rom, offset, state);

            // carry the flag changes along (REP/SEP)
            if (line.StateAfter != null)
                state = line.StateAfter;

            // reset the state at the start of each 32KB bank
            int bankStart = (offset / 0x8000) * 0x8000;
            if (offset == bankStart && offset > 0)
                state = CpuState.Default;

            if (!line.IsData && line.RawBytes.Length > 0 && line.RawBytes[0] is 0x6C or 0x7C or 0xDC or 0xFC)
                unresolved.Add(line.SnesAddress);

            // add a cross-reference for instructions with a known target
            if (!line.IsData && line.JumpTarget.HasValue)
            {
                int caller = line.SnesAddress;
                int target = line.JumpTarget.Value;
                // in the lines' own banks ($80 up): a JSL $01xxxx was kept as $01xxxx while the line there is $81xxxx
                if ((target & 0xFFFF) >= 0x8000 && (target >> 16 & 0xFF) is not (0x7E or 0x7F)) target = (target & 0x7FFFFF) | 0x800000;

                if (!xref.TryGetValue(target, out var callers))
                {
                    callers     = new List<int>();
                    xref[target] = callers;
                }
                if (!callers.Contains(caller))
                    callers.Add(caller);
            }

            int step = line.RawBytes.Length > 0 ? line.RawBytes.Length : 1;
            offset += step;
        }

        ct.ThrowIfCancellationRequested();
        Xref = xref;
        Unresolved = unresolved;
        progress?.Report(100);
    }

    // lookups

    // addresses that call or jump to snesAddress
    public IReadOnlyList<int> GetCalledFrom(int snesAddress)
        => Xref.TryGetValue(snesAddress, out var list) ? list : Array.Empty<int>();

    // every target snesAddress calls or jumps to
    public IReadOnlyList<int> GetCallsTo(int snesAddress)
    {
        var result = new List<int>();
        foreach (var (target, callers) in Xref)
        {
            if (callers.Contains(snesAddress))
                result.Add(target);
        }
        return result;
    }

    public bool IsBuilt => Xref.Count > 0;
}
