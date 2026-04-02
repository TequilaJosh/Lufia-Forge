namespace LufiaForge.Modules.Emulator;

// ── Search comparison modes ────────────────────────────────────────────────────
public enum SearchComparison
{
    Exact,          // cur == value
    Greater,        // cur >  value
    Less,           // cur <  value
    Changed,        // cur != prev  (no value needed)
    Unchanged,      // cur == prev  (no value needed)
    Increased,      // cur >  prev  (no value needed)
    Decreased,      // cur <  prev  (no value needed)
    Any,            // always true  (no value needed; useful for first-scan baseline)
}

// ── Single hit returned from a scan ───────────────────────────────────────────
public sealed record SearchResult(int RamOffset, string AddressHex,
                                  string CurrentValue, string PreviousValue);

/// <summary>
/// Stateful memory scanner. Keeps the previous-snapshot and the candidate list
/// between calls so successive Next Scans narrow the results.
///
/// All methods are synchronous — 128 KB iteration is < 1 ms and needs no
/// background threading.
/// </summary>
public sealed class MemoryScanner
{
    private byte[]?    _prevRam;
    private List<int>? _candidates;   // null = no scan started yet

    public bool HasScan        => _candidates != null;
    public int  CandidateCount => _candidates?.Count ?? 0;

    // -------------------------------------------------------------------------
    // First scan
    // Sets the baseline snapshot and returns all addresses that satisfy the
    // comparison. For relative modes (Changed/Increased/…) the first scan
    // captures all addresses as candidates — they cannot be filtered without a
    // previous value.
    // -------------------------------------------------------------------------
    public (IReadOnlyList<SearchResult> Results, int TotalCount) FirstScan(
        byte[] ram, long value, WatchType type, SearchComparison comp)
    {
        _prevRam    = (byte[])ram.Clone();
        _candidates = new List<int>();

        int stride = Stride(type);
        for (int i = 0; i <= ram.Length - stride; i++)
        {
            long cur = ReadValue(ram, i, type);
            if (MatchesFirst(cur, value, comp))
                _candidates.Add(i);
        }

        return BuildResults(ram, _prevRam, type);
    }

    // -------------------------------------------------------------------------
    // Next scan — filters existing candidates against the new snapshot
    // -------------------------------------------------------------------------
    public (IReadOnlyList<SearchResult> Results, int TotalCount) NextScan(
        byte[] ram, long value, WatchType type, SearchComparison comp)
    {
        if (_candidates == null || _prevRam == null)
            return (Array.Empty<SearchResult>(), 0);

        int stride = Stride(type);
        var keep   = new List<int>(_candidates.Count);

        foreach (int i in _candidates)
        {
            if (i + stride > ram.Length) continue;
            long cur  = ReadValue(ram,      i, type);
            long prev = ReadValue(_prevRam, i, type);
            if (MatchesNext(cur, prev, value, comp))
                keep.Add(i);
        }

        _candidates = keep;
        var result  = BuildResults(ram, _prevRam, type);
        _prevRam    = (byte[])ram.Clone();   // advance baseline for next call
        return result;
    }

    public void Reset() { _candidates = null; _prevRam = null; }

    // -------------------------------------------------------------------------
    // Internals
    // -------------------------------------------------------------------------

    private (IReadOnlyList<SearchResult> Results, int TotalCount) BuildResults(
        byte[] ram, byte[] prev, WatchType type)
    {
        int total = _candidates!.Count;
        var list  = new List<SearchResult>(Math.Min(total, 500));
        foreach (int i in _candidates)
        {
            if (list.Count >= 500) break;
            long cur = ReadValue(ram,  i, type);
            long prv = ReadValue(prev, i, type);
            int snes = 0x7E0000 + i;
            list.Add(new SearchResult(i, $"${snes:X6}", cur.ToString(), prv.ToString()));
        }
        return (list, total);
    }

    private static bool MatchesFirst(long cur, long value, SearchComparison comp) => comp switch
    {
        SearchComparison.Exact   => cur == value,
        SearchComparison.Greater => cur >  value,
        SearchComparison.Less    => cur <  value,
        _                        => true,   // relative modes: accept all on first pass
    };

    private static bool MatchesNext(long cur, long prev, long value, SearchComparison comp) => comp switch
    {
        SearchComparison.Exact     => cur == value,
        SearchComparison.Greater   => cur >  value,
        SearchComparison.Less      => cur <  value,
        SearchComparison.Changed   => cur != prev,
        SearchComparison.Unchanged => cur == prev,
        SearchComparison.Increased => cur >  prev,
        SearchComparison.Decreased => cur <  prev,
        SearchComparison.Any       => true,
        _                          => false,
    };

    private static int Stride(WatchType type) => type switch
    {
        WatchType.U16 => 2,
        WatchType.U32 => 4,
        _             => 1,
    };

    private static long ReadValue(byte[] ram, int offset, WatchType type) => type switch
    {
        WatchType.U8  => ram[offset],
        WatchType.U16 => offset + 1 < ram.Length
                            ? (long)(ram[offset] | (ram[offset + 1] << 8))
                            : 0,
        WatchType.U32 => offset + 3 < ram.Length
                            ? (long)(uint)(ram[offset]       | (ram[offset + 1] << 8)
                                         | (ram[offset + 2] << 16) | (ram[offset + 3] << 24))
                            : 0,
        _ => 0,
    };
}
