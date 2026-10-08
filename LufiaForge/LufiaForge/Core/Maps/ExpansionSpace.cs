using System.Runtime.CompilerServices;

namespace LufiaForge.Core.Maps;

/// <summary>
/// Bookkeeping for the space Lufia Forge uses above 1 MB in an expanded ROM. A table in the last 4 KB of the ROM
/// lists every block written there (start, length, what it is), so new data goes into real gaps and old copies of
/// moved data are freed. Nothing is ever placed by guessing from runs of FF bytes (event tables contain FF FF entries).
///
/// Table at <see cref="TableStart"/>: "LFAL", version (1), count (u16 LE), then count × 10-byte entries:
/// start (u24), length (u24), kind (u8), id (u16), reserved (u8).
///
/// ROMs expanded by earlier versions have no table: it is rebuilt from the pointers to moved map data and script
/// blocks, and every other used byte above 1 MB is reserved as unknown so it is never overwritten.
/// </summary>
public sealed class ExpansionSpace
{
    public const int TableStart = 0x1FF000;
    public const int TableSize = 0x1000;
    /// <summary>First byte handed out (the first 16 bytes above 1 MB stay unused, as before).</summary>
    public const int FirstUsable = MapWriter.ExpansionStart + 0x10;
    private const int EntrySize = 10;
    private const int MaxEntries = (TableSize - 8) / EntrySize;
    private static readonly byte[] Magic = "LFAL"u8.ToArray();

    public enum Kind : byte { Unknown = 0, MapData = 1, ScriptBlock = 2 }

    public sealed record Entry(int Start, int Length, Kind Kind, int Id)
    {
        public int End => Start + Length;
    }

    private readonly RomBuffer _rom;
    private readonly List<Entry> _entries;
    private static readonly ConditionalWeakTable<RomBuffer, ExpansionSpace> Cache = new();

    private ExpansionSpace(RomBuffer rom, List<Entry> entries) { _rom = rom; _entries = entries; }

    public IReadOnlyList<Entry> Entries => _entries;

    /// <summary>The space table of an expanded ROM (read, or rebuilt for ROMs from earlier versions). Not written until <see cref="Save"/>.</summary>
    public static ExpansionSpace Load(RomBuffer rom)
    {
        if (Cache.TryGetValue(rom, out var cached)) return cached;
        var space = new ExpansionSpace(rom, rom.Length < MapWriter.ExpandedSize ? new() : Read(rom) ?? Rebuild(rom));
        Cache.AddOrUpdate(rom, space);
        return space;
    }

    /// <summary>Drop the cached table (after the whole ROM was replaced, e.g. by a patch).</summary>
    public static void Forget(RomBuffer rom) => Cache.Remove(rom);

    private static List<Entry>? Read(RomBuffer rom)
    {
        if (!rom.ReadBytes(TableStart, 4).SequenceEqual(Magic)) return null;
        int count = rom.ReadUInt16Le(TableStart + 5);
        var list = new List<Entry>();
        for (int i = 0; i < Math.Min(count, MaxEntries); i++)
        {
            int p = TableStart + 8 + i * EntrySize;
            int start = rom.ReadByte(p) | (rom.ReadByte(p + 1) << 8) | (rom.ReadByte(p + 2) << 16);
            int len = rom.ReadByte(p + 3) | (rom.ReadByte(p + 4) << 8) | (rom.ReadByte(p + 5) << 16);
            list.Add(new Entry(start, len, (Kind)rom.ReadByte(p + 6), rom.ReadUInt16Le(p + 7)));
        }
        return list.OrderBy(e => e.Start).ToList();
    }

    /// <summary>Table for a ROM expanded by an earlier version: live blocks from their pointers, everything else used kept as unknown.</summary>
    private static List<Entry> Rebuild(RomBuffer rom)
    {
        bool tableAreaFree = true;
        for (int p = TableStart; p < TableStart + TableSize && tableAreaFree; p++) tableAreaFree = rom.ReadByte(p) == 0xFF;
        int top = tableAreaFree ? TableStart : rom.Length;
        int lastUsed = top - 1;
        while (lastUsed >= MapWriter.ExpansionStart && rom.ReadByte(lastUsed) == 0xFF) lastUsed--;

        var live = new List<Entry>();
        var scriptStarts = new List<(int Start, int Map)>();
        foreach (var m in MapCatalog.Scan(rom))
        {
            try
            {
                var map = LufiaMap.Load(rom, m.MapId);
                if (map.CompressedOffset >= MapWriter.ExpansionStart && map.CompressedOffset + map.CompressedSize <= top &&
                    live.All(e => e.Start != map.CompressedOffset))
                    live.Add(new Entry(map.CompressedOffset, map.CompressedSize, Kind.MapData, map.ResourceId));
            }
            catch { }
            int block = EventScript.BlockOffset(rom, m.MapId);
            // (some map table entries are unused and point past the end of the ROM)
            if (block >= MapWriter.ExpansionStart && block < top && scriptStarts.All(s => s.Start != block)) scriptStarts.Add((block, m.MapId));
        }
        // a script block runs to the next live block (or the last used byte): copying too much is harmless, too little is not
        var starts = live.Select(e => e.Start).Concat(scriptStarts.Select(s => s.Start)).OrderBy(s => s).ToList();
        foreach (var (start, map) in scriptStarts)
        {
            int next = starts.FirstOrDefault(s => s > start, Math.Max(lastUsed + 1, start + 1));
            live.Add(new Entry(start, next - start, Kind.ScriptBlock, map));
        }

        // anything else used above 1 MB (old moved copies, other tools' data) stays reserved
        var all = new List<Entry>(live);
        int pos = MapWriter.ExpansionStart;
        while (pos <= lastUsed)
        {
            if (rom.ReadByte(pos) == 0xFF || live.Any(e => pos >= e.Start && pos < e.End))
            {
                var inside = live.FirstOrDefault(e => pos >= e.Start && pos < e.End);
                pos = inside != null ? inside.End : pos + 1;
                continue;
            }
            int runStart = pos, runEnd = pos, ff = 0;
            for (; pos <= lastUsed && !live.Any(e => pos >= e.Start && pos < e.End); pos++)
            {
                if (rom.ReadByte(pos) == 0xFF) { if (++ff >= 16) break; }
                else { ff = 0; runEnd = pos + 1; }
            }
            all.Add(new Entry(runStart, runEnd - runStart, Kind.Unknown, 0));
        }
        if (!tableAreaFree) all.Add(new Entry(TableStart, TableSize, Kind.Unknown, 0xFFFF));
        return all.OrderBy(e => e.Start).ToList();
    }

    /// <summary>The block containing <paramref name="offset"/>, or null.</summary>
    public Entry? At(int offset) => _entries.FirstOrDefault(e => offset >= e.Start && offset < e.End);

    /// <summary>Reserve <paramref name="size"/> bytes in the first gap that fits.</summary>
    public int Allocate(int size, Kind kind, int id)
    {
        if (_rom.Length < MapWriter.ExpandedSize) throw new InvalidOperationException("The ROM isn't expanded.");
        if (_entries.Count >= MaxEntries) throw new InvalidOperationException("The expanded ROM's space table is full.");
        int pos = FirstUsable;
        foreach (var e in _entries.OrderBy(e => e.Start))
        {
            if (e.End <= pos) continue;
            if (e.Start - pos >= size) break;
            pos = Math.Max(pos, e.End);
        }
        if (pos + size > TableStart)
            throw new InvalidOperationException($"Not enough free space left in the expanded ROM for {size} bytes.");
        _entries.Add(new Entry(pos, size, kind, id));
        _entries.Sort((a, b) => a.Start.CompareTo(b.Start));
        return pos;
    }

    /// <summary>Free the block that starts at <paramref name="start"/> (its bytes are reset to FF).</summary>
    public void Free(int start)
    {
        var e = _entries.FirstOrDefault(x => x.Start == start);
        if (e == null) return;
        _entries.Remove(e);
        _rom.WriteBytes(e.Start, Enumerable.Repeat((byte)0xFF, e.Length).ToArray());
    }

    /// <summary>Write the table into the ROM.</summary>
    public void Save()
    {
        if (_rom.Length < MapWriter.ExpandedSize) return;
        if (_entries.Any(e => e.Start == TableStart && e.Kind == Kind.Unknown))
            throw new InvalidOperationException("The last 4 KB of this expanded ROM hold other data, so Lufia Forge can't keep its space table there.");
        var t = new byte[TableSize];
        Array.Fill(t, (byte)0xFF);
        Magic.CopyTo(t, 0);
        t[4] = 1;
        var keep = _entries.Where(e => e.Start != TableStart).Take(MaxEntries).ToList();
        t[5] = (byte)keep.Count; t[6] = (byte)(keep.Count >> 8); t[7] = 0;
        for (int i = 0; i < keep.Count; i++)
        {
            var e = keep[i];
            int p = 8 + i * EntrySize;
            t[p] = (byte)e.Start; t[p + 1] = (byte)(e.Start >> 8); t[p + 2] = (byte)(e.Start >> 16);
            t[p + 3] = (byte)e.Length; t[p + 4] = (byte)(e.Length >> 8); t[p + 5] = (byte)(e.Length >> 16);
            t[p + 6] = (byte)e.Kind; t[p + 7] = (byte)e.Id; t[p + 8] = (byte)(e.Id >> 8); t[p + 9] = 0;
        }
        _rom.WriteBytes(TableStart, t);
    }
}
