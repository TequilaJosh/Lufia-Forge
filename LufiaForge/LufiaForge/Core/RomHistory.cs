namespace LufiaForge.Core;

// undo / redo for every change written to the rom, whatever tab made it. we keep a copy of the rom as of the last
// recorded change, Record compares the rom with it and keeps only the bytes that differ (before and after), so a step
// costs about as much memory as it changed. undo and redo put those bytes back (and the size, if a change grew the rom).
// writes no tab reported get folded into the next recorded step
public sealed class RomHistory
{
    public sealed record Chunk(int Offset, byte[] Before, byte[] After);
    public sealed record Step(string What, DateTime When, int SizeBefore, int SizeAfter, IReadOnlyList<Chunk> Chunks)
    {
        public int Bytes => Chunks.Sum(c => c.Before.Length);
        public override string ToString() => What;
    }

    // the most steps we keep, and roughly the most memory they can use
    public const int MaxSteps = 500;
    private const long MaxBytes = 256L << 20;

    private readonly List<Step> _undo = new(), _redo = new();
    private byte[] _snapshot = Array.Empty<byte>();
    private RomBuffer? _rom;

    public IReadOnlyList<Step> UndoSteps => _undo;
    public IReadOnlyList<Step> RedoSteps => _redo;
    public bool CanUndo => _undo.Count > 0;
    public bool CanRedo => _redo.Count > 0;

    // raised after a step is recorded, undone or redone
    public event Action? Changed;

    // whether we're tracking a rom
    public bool IsAttached => _rom != null;

    // starts a new history for a rom that was just opened
    public void Attach(RomBuffer? rom)
    {
        _rom = rom;
        _undo.Clear(); _redo.Clear();
        _snapshot = rom?.ToArray() ?? Array.Empty<byte>();
        Changed?.Invoke();
    }

    // records what changed since the last step (nothing if the rom didn't change). returns the step
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

    // takes back the last step (records anything not recorded yet first). returns it, or null
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

    // puts back the last undone step. returns it, or null
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

    // undoes steps until step is undone too
    public int UndoTo(Step step)
    {
        int steps = 0;
        while (_undo.Contains(step) && Undo() != null) steps++;
        return steps;
    }

    // redoes steps until step is redone too
    public int RedoTo(Step step)
    {
        int steps = 0;
        while (_redo.Contains(step) && Redo() != null) steps++;
        return steps;
    }

    private void Apply(Step step, bool back)
    {
        var data = _rom!.ToArray();
        int size = back ? step.SizeBefore : step.SizeAfter;
        if (data.Length != size) Array.Resize(ref data, size);
        foreach (var chunk in step.Chunks)
        {
            var bytes = back ? chunk.Before : chunk.After;
            int count = Math.Min(bytes.Length, data.Length - chunk.Offset);
            if (count > 0) Array.Copy(bytes, 0, data, chunk.Offset, count);
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

    // the ranges that differ (bytes past the end of the shorter array count as 0 there), merged when they're close
    public static List<Chunk> Diff(byte[] before, byte[] after)
    {
        var chunks = new List<Chunk>();
        int length = Math.Max(before.Length, after.Length);
        byte B(int i) => i < before.Length ? before[i] : (byte)0;
        byte A(int i) => i < after.Length ? after[i] : (byte)0;
        int common = Math.Min(before.Length, after.Length);
        int pos = 0;
        while (pos < length)
        {
            // skip equal 256-byte blocks quickly
            if (pos + 256 <= common && before.AsSpan(pos, 256).SequenceEqual(after.AsSpan(pos, 256))) { pos += 256; continue; }
            if (B(pos) == A(pos)) { pos++; continue; }
            int start = pos, end = pos + 1, gap = 0;
            for (int i = pos + 1; i < length; i++)
            {
                if (B(i) != A(i)) { end = i + 1; gap = 0; }
                else if (++gap > 32) break;
            }
            var beforeBytes = new byte[end - start];
            var afterBytes = new byte[end - start];
            for (int i = start; i < end; i++) { beforeBytes[i - start] = B(i); afterBytes[i - start] = A(i); }
            chunks.Add(new Chunk(start, beforeBytes, afterBytes));
            pos = end;
        }
        return chunks;
    }
}
