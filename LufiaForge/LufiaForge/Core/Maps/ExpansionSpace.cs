using System.Runtime.CompilerServices;

namespace LufiaForge.Core.Maps;

// keeps track of the space Lufia Forge uses above 1 MB in an expanded rom. a table in the last 4 KB of the rom lists
// every block written there (start, length, what it is), so new data goes into real gaps and old copies of moved data
// get freed. we never place anything by guessing from runs of FF bytes (event tables have FF FF entries).
// table at TableStart: "LFAL", version (1), count (u16 LE), then count x 10-byte entries:
// start (u24), length (u24), kind (u8), id (u16), reserved (u8).
// roms expanded by earlier versions have no table. it gets rebuilt from the pointers to moved map data and script
// blocks, and every other used byte above 1 MB is reserved as unknown so nothing ever overwrites it
public sealed class ExpansionSpace
{
    public const int TableStart = 0x1FF000;
    public const int TableSize = 0x1000;
    // first byte we hand out (the first 16 bytes above 1 MB stay unused, like before)
    public const int FirstUsable = MapWriter.ExpansionStart + 0x10;
    private const int EntrySize = 10;
    private const int MaxEntries = (TableSize - 8) / EntrySize;
    private static readonly byte[] Magic = "LFAL"u8.ToArray();

    public enum Kind : byte { Unknown = 0, MapData = 1, ScriptBlock = 2, Resource = 3, Patch = 4 }

    public sealed record Entry(int Start, int Length, Kind Kind, int Id)
    {
        public int End => Start + Length;
    }

    private readonly RomBuffer _rom;
    private readonly List<Entry> _entries;
    private static readonly ConditionalWeakTable<RomBuffer, ExpansionSpace> Cache = new();

    private ExpansionSpace(RomBuffer rom, List<Entry> entries) { _rom = rom; _entries = entries; }

    public IReadOnlyList<Entry> Entries => _entries;

    // records a block that's already written (merging projects: the other project's blocks keep their places). one
    // that's listed already doesn't get added again. Save writes the table
    public void Keep(Entry entry)
    {
        if (_entries.Contains(entry)) return;
        _entries.Add(entry);
        _entries.Sort((x, y) => x.Start.CompareTo(y.Start));
    }

    // the space table of an expanded rom (read, or rebuilt for roms from earlier versions). not written until Save
    public static ExpansionSpace Load(RomBuffer rom)
    {
        if (rom.Length < MapWriter.ExpandedSize) return new ExpansionSpace(rom, new());   // (not cached, it might get expanded next)
        if (Cache.TryGetValue(rom, out var cached)) return cached;
        var space = new ExpansionSpace(rom, Read(rom) ?? Rebuild(rom));
        Cache.AddOrUpdate(rom, space);
        return space;
    }

    // drops the cached table (after the whole rom got replaced, by a patch for one)
    public static void Forget(RomBuffer rom) => Cache.Remove(rom);

    private static List<Entry>? Read(RomBuffer rom)
    {
        if (!rom.ReadBytes(TableStart, 4).SequenceEqual(Magic)) return null;
        int count = rom.ReadUInt16Le(TableStart + 5);
        var list = new List<Entry>();
        for (int i = 0; i < Math.Min(count, MaxEntries); i++)
        {
            int entryOffset = TableStart + 8 + i * EntrySize;
            int start = rom.ReadByte(entryOffset) | (rom.ReadByte(entryOffset + 1) << 8) | (rom.ReadByte(entryOffset + 2) << 16);
            int length = rom.ReadByte(entryOffset + 3) | (rom.ReadByte(entryOffset + 4) << 8) | (rom.ReadByte(entryOffset + 5) << 16);
            list.Add(new Entry(start, length, (Kind)rom.ReadByte(entryOffset + 6), rom.ReadUInt16Le(entryOffset + 7)));
        }
        return list.OrderBy(e => e.Start).ToList();
    }

    // table for a rom expanded by an earlier version: live blocks from their pointers, everything else that's used kept as unknown
    private static List<Entry> Rebuild(RomBuffer rom)
    {
        bool tableAreaFree = true;
        for (int pos = TableStart; pos < TableStart + TableSize && tableAreaFree; pos++) tableAreaFree = rom.ReadByte(pos) == 0xFF;
        int top = tableAreaFree ? TableStart : rom.Length;
        int lastUsed = top - 1;
        while (lastUsed >= MapWriter.ExpansionStart && rom.ReadByte(lastUsed) == 0xFF) lastUsed--;

        var live = new List<Entry>();
        var scriptStarts = new List<(int Start, int Map)>();
        foreach (var mapInfo in MapCatalog.Scan(rom))
        {
            try
            {
                var map = LufiaMap.Load(rom, mapInfo.MapId);
                if (map.CompressedOffset >= MapWriter.ExpansionStart && map.CompressedOffset + map.CompressedSize <= top &&
                    live.All(e => e.Start != map.CompressedOffset))
                    live.Add(new Entry(map.CompressedOffset, map.CompressedSize, Kind.MapData, map.ResourceId));
            }
            catch { }
            int block = EventScript.BlockOffset(rom, mapInfo.MapId);
            // (some map table entries are unused and point past the end of the rom)
            if (block >= MapWriter.ExpansionStart && block < top && scriptStarts.All(s => s.Start != block)) scriptStarts.Add((block, mapInfo.MapId));
        }
        // a script block runs to the next live block (or the last used byte). copying too much is harmless, too little isn't
        var starts = live.Select(e => e.Start).Concat(scriptStarts.Select(s => s.Start)).OrderBy(s => s).ToList();
        foreach (var (start, map) in scriptStarts)
        {
            int next = starts.FirstOrDefault(s => s > start, Math.Max(lastUsed + 1, start + 1));
            live.Add(new Entry(start, next - start, Kind.ScriptBlock, map));
        }

        // anything else used above 1 MB (old moved copies, other tools' data) stays reserved
        var all = new List<Entry>(live);
        // a rom another hack already made bigger (1.5 MB say): all of that is the hack's, whatever the bytes are
        if (rom.SizeBeforeExpansion > MapWriter.ExpansionStart)
            all.Add(new Entry(MapWriter.ExpansionStart, rom.SizeBeforeExpansion - MapWriter.ExpansionStart, Kind.Unknown, 0xFFFE));
        int scan = MapWriter.ExpansionStart;
        while (scan <= lastUsed)
        {
            if (rom.ReadByte(scan) == 0xFF || live.Any(e => scan >= e.Start && scan < e.End))
            {
                var inside = live.FirstOrDefault(e => scan >= e.Start && scan < e.End);
                scan = inside != null ? inside.End : scan + 1;
                continue;
            }
            int runStart = scan, runEnd = scan, ffRun = 0;
            for (; scan <= lastUsed && !live.Any(e => scan >= e.Start && scan < e.End); scan++)
            {
                // (only a long run of FF counts as free space, other tools' graphics and tables have short ones)
                if (rom.ReadByte(scan) == 0xFF) { if (++ffRun >= 256) break; }
                else { ffRun = 0; runEnd = scan + 1; }
            }
            if (!all.Any(e => e.Kind == Kind.Unknown && runStart >= e.Start && runEnd <= e.End))   // (already reserved whole)
                all.Add(new Entry(runStart, runEnd - runStart, Kind.Unknown, 0));
        }
        if (!tableAreaFree) all.Add(new Entry(TableStart, TableSize, Kind.Unknown, 0xFFFF));
        // a 4 MB rom with no table (another tool's): anything used above 2 MB stays reserved
        if (rom.Length > TableStart + TableSize)
        {
            int last = rom.Length - 1;
            while (last >= TableStart + TableSize && rom.ReadByte(last) == 0xFF) last--;
            if (last >= TableStart + TableSize) all.Add(new Entry(TableStart + TableSize, last + 1 - (TableStart + TableSize), Kind.Unknown, 0xFFFD));
        }
        return all.OrderBy(e => e.Start).ToList();
    }

    // the block that has offset in it, or null
    public Entry? At(int offset) => _entries.FirstOrDefault(e => offset >= e.Start && offset < e.End);

    // reserves size bytes in the first gap that fits (a full 2 MB rom grows to 4 MB).
    // limit = where the block has to end by (event scripts: their bank number is 3 + offset / 32 KB, so they can't
    // reach banks 7E-7F, which are work RAM)
    public int Allocate(int size, Kind kind, int id, int limit = int.MaxValue) => Reserve(size, kind, id, oneBank: false, limit);

    // reserves size bytes that stay inside one 32 KB rom bank (code and tables read with a bank byte plus a 16-bit
    // index can't run over a bank's end)
    public int AllocateInOneBank(int size, Kind kind, int id, int limit = int.MaxValue)
    {
        if (size > 0x8000) throw new ArgumentOutOfRangeException(nameof(size));
        return Reserve(size, kind, id, oneBank: true, limit);
    }

    private int Reserve(int size, Kind kind, int id, bool oneBank, int limit)
    {
        if (_rom.Length < MapWriter.ExpandedSize) throw new InvalidOperationException("The ROM isn't expanded.");
        // (checked before anything gets written, otherwise Save would fail afterwards and leave the edit half done)
        if (_entries.Any(e => e.Start == TableStart && e.Kind == Kind.Unknown))
            throw new InvalidOperationException("The last 4 KB of this expanded ROM hold other data, so Lufia Forge can't keep its space table there.");
        if (_entries.Count >= MaxEntries) throw new InvalidOperationException("The expanded ROM's space table is full.");
        int pos = Find(size, oneBank, limit);
        if (pos < 0 && _rom.Length < MapWriter.MaxSize)
        {
            // 2 MB is full, so the rom grows to 4 MB (banks C0-FF), same way 1 MB grew to 2 MB
            _rom.Expand(MapWriter.MaxSize);
            Cache.AddOrUpdate(_rom, this);   // (Expand forgets the cached table, this one is still the live one)
            pos = Find(size, oneBank, limit);
        }
        if (pos < 0)
            throw new InvalidOperationException($"Not enough free space left in the expanded ROM for {size} bytes.");
        _entries.Add(new Entry(pos, size, kind, id));
        _entries.Sort((a, b) => a.Start.CompareTo(b.Start));
        return pos;
    }

    // the first free spot for size bytes (never the table's own 4 KB), or -1
    private int Find(int size, bool oneBank, int limit)
    {
        var taken = _entries.Append(new Entry(TableStart, TableSize, Kind.Unknown, 0xFFFF)).ToList();
        int pos = FirstUsable;
        while (true)
        {
            if (oneBank && (pos & 0x7FFF) + size > 0x8000) pos = (pos | 0x7FFF) + 1;   // would run over the bank end, go to the next bank
            if (pos + size > Math.Min(_rom.Length, limit)) return -1;
            var clash = taken.Where(e => e.Start < pos + size && e.End > pos).OrderBy(e => e.End).LastOrDefault();
            if (clash == null) return pos;
            pos = clash.End;
        }
    }

    // frees the block that starts at start (its bytes go back to FF)
    public void Free(int start)
    {
        var entry = _entries.FirstOrDefault(x => x.Start == start);
        if (entry == null) return;
        _entries.Remove(entry);
        _rom.WriteBytes(entry.Start, Enumerable.Repeat((byte)0xFF, entry.Length).ToArray());
    }

    // writes the table into the rom
    public void Save()
    {
        if (_rom.Length < MapWriter.ExpandedSize) return;
        if (_entries.Any(e => e.Start == TableStart && e.Kind == Kind.Unknown))
            throw new InvalidOperationException("The last 4 KB of this expanded ROM hold other data, so Lufia Forge can't keep its space table there.");
        var table = new byte[TableSize];
        Array.Fill(table, (byte)0xFF);
        Magic.CopyTo(table, 0);
        table[4] = 1;
        var keep = _entries.Where(e => e.Start != TableStart).Take(MaxEntries).ToList();
        table[5] = (byte)keep.Count; table[6] = (byte)(keep.Count >> 8); table[7] = 0;
        for (int i = 0; i < keep.Count; i++)
        {
            var entry = keep[i];
            int entryOffset = 8 + i * EntrySize;
            table[entryOffset] = (byte)entry.Start; table[entryOffset + 1] = (byte)(entry.Start >> 8); table[entryOffset + 2] = (byte)(entry.Start >> 16);
            table[entryOffset + 3] = (byte)entry.Length; table[entryOffset + 4] = (byte)(entry.Length >> 8); table[entryOffset + 5] = (byte)(entry.Length >> 16);
            table[entryOffset + 6] = (byte)entry.Kind; table[entryOffset + 7] = (byte)entry.Id; table[entryOffset + 8] = (byte)(entry.Id >> 8); table[entryOffset + 9] = 0;
        }
        _rom.WriteBytes(TableStart, table);
    }
}
