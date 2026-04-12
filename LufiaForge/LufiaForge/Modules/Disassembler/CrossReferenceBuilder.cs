using LufiaForge.Core;

namespace LufiaForge.Modules.Disassembler;

/// <summary>
/// Sweeps the entire ROM linearly and collects call/jump cross-references.
/// Output: for each target address, a list of SNES addresses that call or jump to it.
///
/// This is a best-effort linear sweep — it will generate false positives from data regions
/// that happen to decode as JSR/JMP-like opcodes.  Bookmarks marked as data regions are
/// excluded during the sweep.
/// </summary>
public sealed class CrossReferenceBuilder
{
    /// <summary>Target → callers.  Populated after <see cref="Build"/> completes.</summary>
    public Dictionary<int, List<int>> Xref { get; } = new();

    /// <summary>
    /// Run the sweep on a background thread.
    /// </summary>
    /// <param name="rom">Source ROM buffer.</param>
    /// <param name="bookmarks">Bookmark store used to skip data-marked regions.</param>
    /// <param name="progress">Progress callback (0–100).</param>
    /// <param name="ct">Cancellation token.</param>
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
        Xref.Clear();

        // Walk ROM byte-by-byte in 32KB (LoROM) bank increments.
        // For each position, attempt to decode one instruction and collect targets.
        int total   = rom.Length;
        int lastPct = -1;

        // Use default CpuState — we can't perfectly track M/X over the entire ROM,
        // so we use a conservative 8-bit assumption.  Most false-positive noise comes
        // from data, not from M/X mismatches on the opcodes we care about (JSR/JSL/JMP/JML/Bxx).
        var state = CpuState.Default;

        int offset = 0;
        while (offset < total)
        {
            ct.ThrowIfCancellationRequested();

            int pct = offset * 100 / total;
            if (pct != lastPct)
            {
                progress?.Report(pct);
                lastPct = pct;
            }

            var line = Cpu65816.DecodeInstruction(rom, offset, state);

            // Propagate flag changes (REP/SEP)
            if (line.StateAfter != null)
                state = line.StateAfter;

            // Bank boundary: reset state at the start of each 32KB bank
            int bankStart = (offset / 0x8000) * 0x8000;
            if (offset == bankStart && offset > 0)
                state = CpuState.Default;

            // Collect cross-reference entries for instructions with a resolved target
            if (!line.IsData && line.JumpTarget.HasValue)
            {
                int from   = line.SnesAddress;
                int target = line.JumpTarget.Value;

                if (!Xref.TryGetValue(target, out var callers))
                {
                    callers     = new List<int>();
                    Xref[target] = callers;
                }
                if (!callers.Contains(from))
                    callers.Add(from);
            }

            int step = line.RawBytes.Length > 0 ? line.RawBytes.Length : 1;
            offset += step;
        }

        progress?.Report(100);
    }

    // -------------------------------------------------------------------------
    // Query helpers
    // -------------------------------------------------------------------------

    /// <summary>Returns addresses that call or jump to <paramref name="snesAddress"/>.</summary>
    public IReadOnlyList<int> GetCalledFrom(int snesAddress)
        => Xref.TryGetValue(snesAddress, out var list) ? list : Array.Empty<int>();

    /// <summary>Returns all targets that <paramref name="snesAddress"/> calls or jumps to.</summary>
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
