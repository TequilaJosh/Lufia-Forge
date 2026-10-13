namespace LufiaForge.Core;

/// <summary>
/// Undo / redo for every change written to the ROM, whichever tab made it. A copy of the ROM as of the last recorded
/// change is kept; <see cref="Record"/> compares the ROM with it and keeps only the bytes that differ (before and
/// after), so a step costs about as much memory as it changed. Undo and redo put those bytes back (and the size, when a
/// change grew the ROM). Writes no tab reported are folded into the next recorded step.
/// </summary>
public sealed class RomHistory
{
    public sealed record Chunk(int Offset, byte[] Before, byte[] After);
    public sealed record Step(string What, DateTime When, int SizeBefore, int SizeAfter, IReadOnlyList<Chunk> Chunks)
    {
        public int Bytes => Chunks.Sum(c => c.Before.Length);
        public override string ToString() => What;
    }

    /// <summary>Most steps kept, and roughly the most memory they may use.</summary>
    public const int MaxSteps = 500;
    private const long MaxBytes = 256L << 20;

    private readonly List<Step> _undo = new(), _redo = new();
    private byte[] _snapshot = Array.Empty<byte>();
    private RomBuffer? _rom;

    public IReadOnlyList<Step> UndoSteps => _undo;
    public IReadOnlyList<Step> RedoSteps => _redo;
    public bool CanUndo => _undo.Count > 0;
    public bool CanRedo => _redo.Count > 0;

    /// <summary>Raised after a step is recorded, undone or redone.</summary>
    public event Action? Changed;

    /// <summary>Starts a new history for a freshly opened ROM.</summary>
    public void Attach(RomBuffer? rom)
    {
        _rom = rom;
        _undo.Clear(); _redo.Clear();
        _snapshot = rom?.ToArray() ?? Array.Empty<byte>();
        Changed?.Invoke();
    }

    /// <summary>Records what changed since the last step (nothing when the ROM is unchanged). Returns the step.</summary>
    public Step? Record(string? what)
    {
        if (_rom == null) return null;
        var now = _rom.ToArray();
        var chunks = Diff(_snapshot, now);
        if (chunks.Count == 0 && now.Length == _snapshot.Length) return null;
        var step = new Step(string.IsNullOrWhiteSpace(what) ? "Edit" : what!, DateTime.Now, _snapshot.Length, now.Length, chunks);
        _undo.Add(step);
        _redo.Clear();
        Trim();
        _snapshot = now;
        Changed?.Invoke();
        return step;
    }

    /// <summary>Takes back the last step (first recording anything not yet recorded). Returns it, or null.</summary>
    public Step? Undo()
    {
        if (_rom == null) return null;
        Record("Other changes");
        if (_undo.Count == 0) return null;
        var step = _undo[^1];
        _undo.RemoveAt(_undo.Count - 1);
        Apply(step, back: true);
        _redo.Add(step);
        Changed?.Invoke();
        return step;
    }

    /// <summary>Puts back the last undone step. Returns it, or null.</summary>
    public Step? Redo()
    {
        if (_rom == null || _redo.Count == 0) return null;
        Record("Other changes");   // a new change after an undo clears the redo list
        if (_redo.Count == 0) return null;
        var step = _redo[^1];
        _redo.RemoveAt(_redo.Count - 1);
        Apply(step, back: false);
        _undo.Add(step);
        Changed?.Invoke();
        return step;
    }

    /// <summary>Undoes steps until <paramref name="step"/> is undone too.</summary>
    public int UndoTo(Step step)
    {
        int n = 0;
        while (_undo.Contains(step) && Undo() != null) n++;
        return n;
    }

    /// <summary>Redoes steps until <paramref name="step"/> is redone too.</summary>
    public int RedoTo(Step step)
    {
        int n = 0;
        while (_redo.Contains(step) && Redo() != null) n++;
        return n;
    }

    private void Apply(Step step, bool back)
    {
        var data = _rom!.ToArray();
        int size = back ? step.SizeBefore : step.SizeAfter;
        if (data.Length != size) Array.Resize(ref data, size);
        foreach (var c in step.Chunks)
        {
            var bytes = back ? c.Before : c.After;
            int n = Math.Min(bytes.Length, data.Length - c.Offset);
            if (n > 0) Array.Copy(bytes, 0, data, c.Offset, n);
        }
        _rom.ReplaceAll(data);
        _snapshot = data;
    }

    private void Trim()
    {
        long total = _undo.Sum(s => (long)s.Bytes * 2);
        while (_undo.Count > 1 && (_undo.Count > MaxSteps || total > MaxBytes))
        {
            total -= (long)_undo[0].Bytes * 2;
            _undo.RemoveAt(0);
        }
    }

    /// <summary>The ranges that differ (bytes past the end of the shorter array count as 0 there), merged when close.</summary>
    public static List<Chunk> Diff(byte[] before, byte[] after)
    {
        var chunks = new List<Chunk>();
        int len = Math.Max(before.Length, after.Length);
        byte B(int i) => i < before.Length ? before[i] : (byte)0;
        byte A(int i) => i < after.Length ? after[i] : (byte)0;
        int common = Math.Min(before.Length, after.Length);
        int pos = 0;
        while (pos < len)
        {
            // skip equal 256-byte blocks fast
            if (pos + 256 <= common && before.AsSpan(pos, 256).SequenceEqual(after.AsSpan(pos, 256))) { pos += 256; continue; }
            if (B(pos) == A(pos)) { pos++; continue; }
            int start = pos, end = pos + 1, gap = 0;
            for (int i = pos + 1; i < len; i++)
            {
                if (B(i) != A(i)) { end = i + 1; gap = 0; }
                else if (++gap > 32) break;
            }
            var b = new byte[end - start];
            var a = new byte[end - start];
            for (int i = start; i < end; i++) { b[i - start] = B(i); a[i - start] = A(i); }
            chunks.Add(new Chunk(start, b, a));
            pos = end;
        }
        return chunks;
    }
}
