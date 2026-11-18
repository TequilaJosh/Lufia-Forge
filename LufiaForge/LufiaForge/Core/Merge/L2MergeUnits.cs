using LufiaForge.Core.Audio;
using LufiaForge.Core.Lufia2;

namespace LufiaForge.Core.Merge;

// the lufia 2 things a merge handles by what's in them, wherever they sit (see ProjectMerge): each event of each map
// and its start-up script, each list of a map's info block, each packed file. compared three ways (the base, this
// project, the other one) one at a time, so two projects that changed different events of one map both keep theirs.
// after the bytes are merged each one is made the way it was chosen: if the merge changed its bytes it gets written
// again in free room (events in the event space, info blocks and files in the added space) and pointed at.
// a = this project, ab = its base, b = the other project, bb = the other one's base
internal sealed class L2MergeUnits
{
    private readonly Content _a, _ab, _b, _bb;
    public List<MergeItem> Items { get; } = new();
    public int SameCount { get; private set; }
    public List<string> Warnings { get; } = new();

    private readonly Dictionary<(int Map, int Head, int Id), MergeItem> _events = new();
    private readonly Dictionary<(int Map, int List), MergeItem> _info = new();
    private readonly Dictionary<int, MergeItem> _files = new();

    // where scripts get laid out to be compared (past the end of every rom, so nothing gets mistaken for the old bytes there)
    private const int Probe = 0x7F8000;
    private const int InitKey = -1;

    public L2MergeUnits(RomBuffer a, RomBuffer ab, RomBuffer b, RomBuffer bb)
    {
        _a = new Content(a); _ab = new Content(ab); _b = new Content(b); _bb = ReferenceEquals(ab, bb) ? _ab : new Content(bb);
        MakeItems();
    }

    // what a rom holds

    // an event (or the key table, or a start-up script) the way we compare it: its bytes laid out at Probe
    private sealed class Ev
    {
        public byte[] Canon = Array.Empty<byte>();
        public L2EventScript? Script;
        public byte[]? Key;
        public string? Problem;
        public int Start, End;   // the bytes it takes up in its rom
        public string Snippet = "";
    }

    private sealed class MapEvents
    {
        public readonly Dictionary<(int Head, int Id), Ev> Entries = new();
        public readonly List<(int Head, int Id)> Order = new();
        public Ev? Init;
        public int Block = -1, BlockStart, BlockEnd, InitStart, InitEnd;
        public string? Problem;
    }

    private sealed class Content
    {
        public readonly RomBuffer Rom;
        public readonly int Maps;
        public readonly List<int> FileIds;
        private readonly Dictionary<int, MapEvents> _ev = new();
        private readonly Dictionary<int, (int Start, byte[]?[]? Lists)> _info = new();
        private readonly Dictionary<int, (byte[]? Data, int At, int Packed)> _files = new();

        public Content(RomBuffer rom)
        {
            Rom = rom;
            try { Maps = L2Maps.Count(rom); } catch (Exception) { Maps = L2NewMaps.GameCount; }
            try { FileIds = L2Rom.FileIds(rom).ToList(); } catch (Exception) { FileIds = Enumerable.Range(0, L2Rom.FileCount).ToList(); }
        }

        public MapEvents? Events(int map) => map >= Maps ? null : _ev.TryGetValue(map, out var events) ? events : _ev[map] = ReadEvents(Rom, map);
        public (int Start, byte[]?[]? Lists) Info(int map) => map >= Maps ? (-1, null) : _info.TryGetValue(map, out var info) ? info : _info[map] = ReadInfo(Rom, map);
        public (byte[]? Data, int At, int Packed) File(int id) => !FileIds.Contains(id) ? (null, -1, 0) : _files.TryGetValue(id, out var file) ? file : _files[id] = ReadFile(Rom, id);
    }

    private static MapEvents ReadEvents(RomBuffer rom, int map)
    {
        var ev = new MapEvents();
        try
        {
            int block = L2Events.Block(rom, map);
            if (block >= 0)
            {
                ev.Block = block;
                var byOffset = new Dictionary<int, Ev>();
                int end = block + 0x0E;
                foreach (var e in L2EventBlocks.ReadLists(rom, block))
                {
                    var key = (e.Head, e.Id);
                    if (ev.Entries.ContainsKey(key)) continue;   // the game takes the first one
                    int offsetKey = e.IsScript ? e.Offset : 0x10000 + e.Offset;
                    if (!byOffset.TryGetValue(offsetKey, out var parsed)) byOffset[offsetKey] = parsed = e.IsScript ? ReadScript(rom, block + e.Offset, block) : ReadKey(rom, block, e.Offset);
                    ev.Entries[key] = parsed; ev.Order.Add(key);
                    end = Math.Max(end, parsed.End);
                }
                foreach (int head in L2EventBlocks.Heads)
                {
                    int listOffset = rom.ReadUInt16Le(block + head), i = 0;
                    while (i < 128 && rom.ReadByte(block + listOffset + i * 3) != 0xFF) i++;
                    end = Math.Max(end, block + listOffset + i * 3 + 1);
                }
                if (L2EventBlocks.Chunk(rom, block) is { } chunk) { ev.BlockStart = block - 8; ev.BlockEnd = Math.Max(end, block + chunk.Room); }
                else { ev.BlockStart = block; ev.BlockEnd = end; }
            }
            int init = L2EventBlocks.InitScript(rom, map);
            if (init > 0 && init < rom.Length)
            {
                ev.Init = ReadScript(rom, init, L2EventBlocks.InitBase(init));
                ev.InitStart = init; ev.InitEnd = ev.Init.End;
                if (init >= 8 && rom.ReadByte(init - 8) == 'L' && rom.ReadByte(init - 7) == 'F' && rom.ReadByte(init - 6) == '2' && rom.ReadByte(init - 5) == 'I')
                { ev.InitStart = init - 8; ev.InitEnd = Math.Max(ev.InitEnd, init + rom.ReadUInt16Le(init - 4)); }
            }
        }
        catch (Exception ex) { ev.Problem = ex.Message; }
        return ev;
    }

    private static Ev ReadScript(RomBuffer rom, int scriptOffset, int jumpBase)
    {
        var script = L2EventScript.Read(rom, scriptOffset, jumpBase);
        var ev = new Ev { Script = script, Start = scriptOffset, End = script.Steps.Count == 0 ? scriptOffset + 1 : script.Steps.Max(t => t.At + t.OriginalLength) };
        ev.Snippet = script.Steps.FirstOrDefault(t => t.IsText)?.Text.Replace("\n", " ").Replace("\r", "") ?? "";
        if (ev.Snippet.Length > 50) ev.Snippet = ev.Snippet[..50] + "...";
        if (script.Problem != null) { ev.Problem = script.Problem; ev.Canon = System.Text.Encoding.UTF8.GetBytes($"?{scriptOffset:X6} {script.Problem}"); return ev; }
        var (bytes, error) = script.Detached().WithJumpBase(Probe).Build(rom, Probe);
        if (error != null) { ev.Problem = error; ev.Canon = System.Text.Encoding.UTF8.GetBytes($"?{scriptOffset:X6} {error}"); return ev; }
        ev.Canon = bytes!;
        return ev;
    }

    private static Ev ReadKey(RomBuffer rom, int block, int offset)
    {
        int end = offset;
        while (rom.ReadByte(block + end) != 0 && end < offset + 0x300) end += 3;
        var bytes = rom.ReadBytes(block + offset, end + 1 - offset);
        return new Ev { Key = bytes, Canon = bytes, Start = block + offset, End = block + end + 1 };
    }

    // a map's info block: its 21 lists (the records up to FF where we know the record size, otherwise the bytes up to
    // the next list)
    private static (int Start, byte[]?[]? Lists) ReadInfo(RomBuffer rom, int map)
    {
        try
        {
            int blockOffset = L2MapInfo.Block(rom, map);
            if (blockOffset < 0 || blockOffset + 0x2C > rom.Length) return (-1, null);
            int size = rom.ReadUInt16Le(blockOffset);
            if (size < 0x2C || size > 0x1000 || blockOffset + size > rom.Length) return (-1, null);
            var listOffsets = Enumerable.Range(0, 21).Select(i => (int)rom.ReadUInt16Le(blockOffset + 2 + i * 2)).ToArray();
            var lists = new byte[]?[21];
            for (int i = 0; i < 21; i++)
            {
                int listOffset = listOffsets[i];
                if (listOffset <= 0 || listOffset >= size + 1) { lists[i] = Array.Empty<byte>(); continue; }
                int recordSize = L2MapInfo.RecordSize(2 + i * 2);
                if (recordSize > 0)
                {
                    int pos = blockOffset + listOffset;
                    for (int guard = 0; guard < 128 && pos + recordSize <= blockOffset + size + 1 && rom.ReadByte(pos) != 0xFF; guard++) pos += recordSize;
                    lists[i] = rom.ReadBytes(blockOffset + listOffset, Math.Min(pos + 1, blockOffset + size + 1) - (blockOffset + listOffset));
                }
                else
                {
                    int next = listOffsets.Where(other => other > listOffset && other <= size).DefaultIfEmpty(size).Min();
                    lists[i] = rom.ReadBytes(blockOffset + listOffset, next - listOffset);
                }
            }
            return (blockOffset, lists);
        }
        catch (Exception) { return (-1, null); }
    }

    private static (byte[]? Data, int At, int Packed) ReadFile(RomBuffer rom, int id)
    {
        try
        {
            int fileOffset = L2Rom.FileOffset(rom, id);
            if (fileOffset < 0 || fileOffset >= rom.Length) return (null, -1, 0);
            var data = LufiaCompression.Decompress(rom, fileOffset, out int packed);
            return (data, fileOffset, packed);
        }
        catch (Exception) { return (null, -1, 0); }
    }

    private static bool Eq(byte[]? x, byte[]? y) => x == null ? y == null : y != null && x.AsSpan().SequenceEqual(y);
    private static bool Eq(Ev? x, Ev? y) => Eq(x?.Canon, y?.Canon);

    // which bytes belong to what

    // marks the bytes of a rom's events, info blocks and packed files (data) and the pointers to them
    // (ptr, and each pointer goes in slots too)
    public void Own(RomBuffer rom, bool[] data, bool[] ptr, List<(int, int)>? slots)
    {
        var content = ReferenceEquals(rom, _a.Rom) ? _a : ReferenceEquals(rom, _b.Rom) ? _b : new Content(rom);
        int size = data.Length;
        void MarkData(int s, int e) { for (int i = Math.Max(0, s); i < Math.Min(size, e); i++) data[i] = true; }
        void MarkPointer(int s, int len) { for (int i = Math.Max(0, s); i < Math.Min(size, s + len); i++) ptr[i] = true; slots?.Add((s, len)); }
        for (int m = 0; m < content.Maps; m++)
        {
            try
            {
                int headerAt = L2Events.HeaderAt(rom, m);
                MarkPointer(headerAt, 3); MarkPointer(headerAt + 3, 3);
                var ev = content.Events(m)!;
                if (ev.Block >= 0) MarkData(ev.BlockStart, ev.BlockEnd);
                foreach (var entry in ev.Entries.Values) MarkData(entry.Start, entry.End);
                if (ev.Init != null) MarkData(ev.InitStart, ev.InitEnd);
                MarkPointer(L2MapInfo.PointerAt(rom, m), 3);
                var (infoStart, lists) = content.Info(m);
                if (lists != null) MarkData(infoStart, infoStart + rom.ReadUInt16Le(infoStart) + 1);
            }
            catch (Exception) { }
        }
        foreach (int id in content.FileIds)
        {
            try
            {
                MarkPointer(L2Rom.FileTableAt(rom) + id * 3, 3);
                var (fileData, fileOffset, packed) = content.File(id);
                if (fileData != null) MarkData(fileOffset, fileOffset + packed);
            }
            catch (Exception) { }
        }
    }

    // what the other project changed

    private static string MapGroup(Content content, int map)
    {
        string name;
        try { name = L2Maps.Name(content.Rom, map); } catch (Exception) { name = ""; }
        return $"Map {map:X2}" + (string.IsNullOrWhiteSpace(name) ? "" : " " + name);
    }

    private static string EventName(Content content, int map, int head, int id) => head switch
    {
        InitKey => "Start-up script (makes the map's characters)",
        2 => id switch
        {
            0 => "Map event 00 (when the map is drawn)", 1 => "Map event 01 (on entering the map or a room of it)",
            2 => "Map event 02 (before the map loads)", 3 => "Locked doors' key table", _ => $"Map event {id:X2}",
        },
        4 => $"Scene {id:X2}",
        6 => id >= 0x50 ? $"Talking to character {id - 0x4F}" : $"Talking to {new L2EventCommands.Words().Who(id)}",
        8 => $"Stepping on area {id}" + Area(content, map, id),
        _ => $"Event {id:X2} of list +{head:X}",
    };

    private static string Area(Content content, int map, int id)
    {
        try
        {
            var trigger = L2MapInfo.Records(content.Rom, map, L2MapInfo.Triggers, 5).FirstOrDefault(r => r.Bytes[0] == id).Bytes;
            return trigger == null ? "" : $" ({trigger[1]},{trigger[2]})-({trigger[3] - 1},{trigger[4] - 1})";
        }
        catch (Exception) { return ""; }
    }

    private void MakeItems()
    {
        int maps = Math.Min(_a.Maps, _b.Maps);
        if (_b.Maps > _a.Maps && _a.Maps <= L2NewMaps.GameCount)
            Warnings.Add($"The other project made new maps ({string.Join(", ", Enumerable.Range(_a.Maps, _b.Maps - _a.Maps).Select(m => m.ToString("X2")))}): they come along with its new-map tables, as they are.");
        else if (_b.Maps > L2NewMaps.GameCount && _a.Maps > L2NewMaps.GameCount && _b.Maps != _a.Maps)
            Warnings.Add("Both projects made new maps, so the new map numbers clash: only this project's new maps are kept as maps. Make the other project's new maps again after the merge.");

        for (int m = 0; m < maps; m++)
        {
            string group = MapGroup(_b, m);
            int order = 1000 + m * 1000;
            var a = _a.Events(m)!; var b = _b.Events(m)!;
            var ab = _ab.Events(m); var bb = _bb.Events(m);
            var keys = a.Order.Concat(b.Order).Concat(ab?.Order ?? new()).Distinct().ToList();
            int eventNumber = 0;
            foreach (var key in keys)
            {
                var ea = a.Entries.GetValueOrDefault(key); var eb = b.Entries.GetValueOrDefault(key);
                var eab = ab?.Entries.GetValueOrDefault(key); var ebb = bb?.Entries.GetValueOrDefault(key);
                AddEvent(m, key.Head, key.Id, group, order + 10 + eventNumber++, ea, eab, eb, ebb);
            }
            AddEvent(m, InitKey, InitKey, group, order + 1, a.Init, ab?.Init, b.Init, bb?.Init);

            var (_, la) = _a.Info(m); var (_, lb) = _b.Info(m);
            var lab = _ab.Info(m).Lists; var lbb = _bb.Info(m).Lists;
            if (la != null && lb != null)
                for (int i = 0; i < 21; i++)
                {
                    byte[]? xa = la[i], xb = lb[i], xab = lab?[i], xbb = lbb?[i];
                    bool theyChanged = !Eq(xb, xbb), weChanged = !Eq(xa, xab);
                    if (!theyChanged) continue;
                    if (Eq(xa, xb)) { SameCount++; continue; }
                    int list = 2 + i * 2;
                    var item = new MergeItem
                    {
                        Group = group, What = $"Map info: {L2MapInfo.ListName(list)}", Conflict = weChanged, Take = !weChanged, Order = order + 500 + i,
                        Why = weChanged ? "Both projects changed this list of the map's info, differently." : "Only the other project changed this list of the map's info.",
                        Details = () => InfoDetails(list, xab, xa, xb),
                    };
                    _info[(m, list)] = item; Items.Add(item);
                }
        }

        foreach (int id in _a.FileIds.Intersect(_b.FileIds))
        {
            var (da, _, _) = _a.File(id); var (db, _, _) = _b.File(id);
            var dab = _ab.File(id).Data; var dbb = _bb.File(id).Data;
            bool theyChanged = !Eq(db, dbb), weChanged = !Eq(da, dab);
            if (!theyChanged) continue;
            if (Eq(da, db)) { SameCount++; continue; }
            var (group, what, order) = FileLabel(id);
            var item = new MergeItem
            {
                Group = group, What = what, Conflict = weChanged, Take = !weChanged && db != null, Order = order,
                Why = db == null ? "The other project's copy of this file can't be unpacked: it isn't taken." :
                      weChanged ? "Both projects changed this file, differently." : "Only the other project changed this file.",
                Details = () => FileDetails(dab, da, db),
            };
            _files[id] = item; Items.Add(item);
        }
    }

    private (string Group, string What, int Order) FileLabel(int id)
    {
        int map = id - L2Maps.MapFileBase;
        if (map >= 0 && map < L2NewMaps.GameCount && map < _a.Maps) return (MapGroup(_b, map), $"Map layout (packed file {id:X3})", 1000 + map * 1000 + 400);
        if (id < L2Sound.SongCount) return ("Music", $"Song {id:X2} (packed file {id:X3})", 600000 + id);
        string what = id >= L2NewMaps.NewFileBase ? $"New map file {id:X3}"
            : id >= L2Graphics.MonsterSpriteFileBase && id < L2Graphics.MonsterSpriteFileBase + L2Graphics.MonsterSpriteCount ? $"Monster sprite {id - L2Graphics.MonsterSpriteFileBase:X2} (packed file {id:X3})"
            : id >= L2Maps.TileSetFileBase && id < L2Graphics.MonsterSpriteFileBase ? $"Tile set {id - L2Maps.TileSetFileBase:X2} (packed file {id:X3})"
            : id >= L2Maps.BlockSetFileBase && id < L2Maps.TileSetFileBase ? $"Block set {id - L2Maps.BlockSetFileBase:X2} (packed file {id:X3})"
            : $"Packed file {id:X3}";
        return ("Graphics and other packed files", what, 700000 + id);
    }

    private void AddEvent(int map, int head, int id, string group, int order, Ev? a, Ev? ab, Ev? b, Ev? bb)
    {
        bool theyChanged = !Eq(b, bb), weChanged = !Eq(a, ab);
        if (!theyChanged) return;
        if (Eq(a, b)) { SameCount++; return; }
        bool broken = b?.Problem != null;
        string why = broken ? $"The other project's version can't be read ({b!.Problem}), so it isn't taken."
            : b == null ? (weChanged ? "The other project took this event out; this project changed it." : "The other project took this event out.")
            : bb == null && a == null ? "New in the other project."
            : bb == null ? "Both projects added an event here (the same list and number), with different steps."
            : a == null ? "This project took the event out; the other project changed it."
            : weChanged ? "Both projects changed this event, differently." : "Only the other project changed this event.";
        string snippet = (b ?? a)?.Snippet ?? "";
        var item = new MergeItem
        {
            Group = group,
            What = EventName(b != null ? _b : _a, map, head, id) + (snippet.Length > 0 ? $": \"{snippet}\"" : ""),
            Conflict = weChanged && !broken, Take = !weChanged && !broken, Order = order, Why = why,
            Details = () => EventDetails(ab, a, b, a != null ? _a.Rom : _ab.Rom, b != null ? _b.Rom : _bb.Rom),
        };
        _events[(map, head, id)] = item; Items.Add(item);
    }

    // diffs

    private static List<string> Listing(Ev? ev, RomBuffer rom)
    {
        if (ev == null) return new() { "(no such event)" };
        if (ev.Problem != null) return new() { "(can't be read: " + ev.Problem + ")" };
        if (ev.Key != null) return Enumerable.Range(0, ev.Key.Length / 3).Select(i => Convert.ToHexString(ev.Key, i * 3, 3)).Append("00").ToList();
        var words = new L2EventCommands.Words();
        var steps = ev.Script!.Steps;
        var lines = new List<string>();
        foreach (var step in steps)
        {
            string line;
            try { line = L2EventCommands.Find(step.Op)?.Describe(step.Bytes, words) ?? $"command {step.Op:X2}"; } catch (Exception) { line = $"command {step.Op:X2}"; }
            if (step.IsText) line = line.TrimEnd(':', ' ') + ": \"" + step.Text.Replace("\r", "").Replace("\n", " | ") + "\"";
            var jumps = step.Jumps.Select(j => j.Step != null ? steps.IndexOf(j.Step) + 1 : -1).ToList();
            if (jumps.Count > 0) line += " -> " + string.Join(", ", jumps.Select(j => j > 0 ? $"step {j}" : "?"));
            lines.Add(line);
        }
        return lines;
    }

    private static List<DiffLine> EventDetails(Ev? ab, Ev? a, Ev? b, RomBuffer romA, RomBuffer romB)
    {
        var listingA = Listing(a, romA);
        var listingB = Listing(b, romB);
        var lines = new List<DiffLine>
        {
            new(DiffKind.Note, Eq(a, ab) ? "This project has the event as it started." : "This project changed the event too."),
            new(DiffKind.Head, "- this project's steps   + the other project's steps"),
        };
        lines.AddRange(MergeDiff.Lines(Number(listingA), Number(listingB)));
        return lines;
    }

    private static List<string> Number(List<string> lines) => lines.Select((s, i) => $"{i + 1,3}. {s}").ToList();

    private static List<DiffLine> InfoDetails(int listId, byte[]? ab, byte[]? a, byte[]? b)
    {
        int recordSize = L2MapInfo.RecordSize(listId);
        List<string> Records(byte[]? list)
        {
            if (list == null) return new() { "(none)" };
            if (recordSize <= 0) return Enumerable.Range(0, (list.Length + 15) / 16).Select(i => Convert.ToHexString(list, i * 16, Math.Min(16, list.Length - i * 16))).ToList();
            var records = new List<string>();
            for (int pos = 0; pos + recordSize <= list.Length && list[pos] != 0xFF; pos += recordSize) records.Add(string.Join(" ", list.Skip(pos).Take(recordSize).Select(v => v.ToString("X2"))));
            return records;
        }
        var lines = new List<DiffLine>
        {
            new(DiffKind.Note, (Eq(a, ab) ? "This project has the list as it started." : "This project changed the list too.") +
                (recordSize > 0 ? $" One record a line ({recordSize} bytes: the first is its number)." : "")),
            new(DiffKind.Head, "- this project's   + the other project's"),
        };
        lines.AddRange(MergeDiff.Lines(Records(a), Records(b)));
        return lines;
    }

    private static List<DiffLine> FileDetails(byte[]? ab, byte[]? a, byte[]? b)
    {
        var lines = new List<DiffLine>();
        if (a == null || b == null) { lines.Add(new(DiffKind.Note, a == null ? "This project's copy can't be unpacked." : "The other project's copy can't be unpacked.")); return lines; }
        int differing = 0, firstDiff = -1;
        for (int i = 0; i < Math.Max(a.Length, b.Length); i++)
            if (i >= a.Length || i >= b.Length || a[i] != b[i]) { differing++; if (firstDiff < 0) firstDiff = i; }
        lines.Add(new(DiffKind.Note, (Eq(a, ab) ? "This project has the file as it started. " : "This project changed the file too. ") +
            $"Unpacked: this project's {a.Length} bytes, the other project's {b.Length}; {differing} byte{(differing == 1 ? "" : "s")} differ."));
        lines.Add(new(DiffKind.Head, "Rows that differ (offset in the unpacked file): - this project's   + the other project's"));
        int shown = 0;
        for (int row = 0; row < Math.Max(a.Length, b.Length) && shown < 48; row += 16)
        {
            bool differs = false;
            for (int i = row; i < row + 16; i++) if (i < a.Length != i < b.Length || i < a.Length && a[i] != b[i]) differs = true;
            if (!differs) continue;
            string Hex(byte[] x) => row >= x.Length ? "" : Convert.ToHexString(x, row, Math.Min(16, x.Length - row));
            lines.Add(new(DiffKind.Ours, $"{row:X5}  {Hex(a)}"));
            lines.Add(new(DiffKind.Theirs, $"{row:X5}  {Hex(b)}"));
            shown++;
        }
        if (shown == 48) lines.Add(new(DiffKind.Note, "(the first 48 rows that differ are shown)"));
        return lines;
    }

    // making them the way they were chosen

    private (List<((int Head, int Id) Key, Ev Ev)> Events, Ev? Init) DesiredEvents(int map)
    {
        var a = _a.Events(map); var b = _b.Events(map);
        if (a == null) return (b!.Order.Select(k => (k, b.Entries[k])).ToList(), b.Init);
        var list = new List<((int, int), Ev)>();
        foreach (var key in a.Order.Concat(b?.Order ?? new()).Distinct())
        {
            var source = _events.TryGetValue((map, key.Head, key.Id), out var item) && item.Take ? b : a;
            if (source?.Entries.GetValueOrDefault(key) is { } ev) list.Add((key, ev));
        }
        var init = _events.TryGetValue((map, InitKey, InitKey), out var initItem) && initItem.Take ? b?.Init : a.Init;
        return (list, init);
    }

    private byte[]?[]? DesiredInfo(int map)
    {
        var (_, la) = _a.Info(map); var (_, lb) = _b.Info(map);
        if (la == null) return lb;
        var lists = (byte[]?[])la.Clone();
        for (int i = 0; i < 21; i++)
            if (_info.TryGetValue((map, 2 + i * 2), out var item) && item.Take && lb != null) lists[i] = lb[i];
        return lists;
    }

    private byte[]? DesiredFile(int id)
    {
        var da = _a.File(id).Data;
        if (_files.TryGetValue(id, out var item) && item.Take) return _b.File(id).Data;
        return da ?? (_a.FileIds.Contains(id) ? null : _b.File(id).Data);
    }

    private static bool SameEvents(MapEvents now, List<((int Head, int Id) Key, Ev Ev)> want, Ev? init)
    {
        if (now.Problem != null || now.Entries.Count != want.Count) return false;
        foreach (var (key, ev) in want)
            if (!now.Entries.TryGetValue(key, out var current) || !Eq(current, ev)) return false;
        return init == null || Eq(now.Init, init);
    }

    // after the bytes are merged: makes every map's events, info lists and packed files the way they were chosen
    // (written again in free room where the merged bytes don't have them)
    public void Converge(RomBuffer rom, List<string> report)
    {
        var now = new Content(rom);
        int blocks = 0, inits = 0, infos = 0, files = 0;
        var problems = new List<string>();
        for (int map = 0; map < now.Maps; map++)
        {
            if (map >= _a.Maps && map >= _b.Maps) break;
            try
            {
                var (want, init) = DesiredEvents(map);
                var current = now.Events(map)!;
                if (!SameEvents(current, want, null))
                {
                    var broken = want.FirstOrDefault(w => w.Ev.Problem != null);
                    if (broken.Ev != null) problems.Add($"Map {map:X2}: event {broken.Key.Id:X2} of list +{broken.Key.Head:X} can't be read, so the map's events couldn't be laid out again.");
                    else
                    {
                        var events = want.Select(w => (new L2EventBlocks.Entry(w.Key.Head, w.Key.Id, 0), w.Ev.Script, w.Ev.Key)).ToList();
                        int blockOffset = L2EventBlocks.WriteBlock(rom, events);
                        L2EventBlocks.PointMap(rom, map, blockOffset);
                        blocks++;
                    }
                }
                if (init != null && !Eq(current.Init, init))
                {
                    if (init.Problem != null) problems.Add($"Map {map:X2}: the start-up script can't be read, so it couldn't be written again.");
                    else { L2EventBlocks.WriteInitNew(rom, map, init.Script!.Detached()); inits++; }
                }
                var wantInfo = DesiredInfo(map);
                var (_, curInfo) = now.Info(map);
                if (wantInfo != null && (curInfo == null || Enumerable.Range(0, 21).Any(i => !Eq(curInfo[i], wantInfo[i]))))
                {
                    WriteInfo(rom, map, wantInfo);
                    infos++;
                }
            }
            catch (Exception ex) { problems.Add($"Map {map:X2}: {ex.Message}"); }
        }
        foreach (int id in now.FileIds)
        {
            try
            {
                var want = DesiredFile(id);
                if (want == null) continue;
                if (Eq(now.File(id).Data, want)) continue;
                L2Rom.WriteNewFile(rom, id, want);
                files++;
            }
            catch (Exception ex) { problems.Add($"Packed file {id:X3}: {ex.Message}"); }
        }
        var moved = new List<string>();
        if (blocks > 0) moved.Add($"{blocks} map{(blocks == 1 ? "'s" : "s'")} events");
        if (inits > 0) moved.Add($"{inits} start-up script{(inits == 1 ? "" : "s")}");
        if (infos > 0) moved.Add($"{infos} map info block{(infos == 1 ? "" : "s")}");
        if (files > 0) moved.Add($"{files} packed file{(files == 1 ? "" : "s")}");
        if (moved.Count > 0) report.Add("Laid out again in free room (merged from both projects, or moved off bytes the other project used): " + string.Join(", ", moved) + ".");
        if (problems.Count > 0) throw new InvalidOperationException(string.Join("\n", problems));
    }

    private static void WriteInfo(RomBuffer rom, int map, byte[]?[] lists)
    {
        var data = new List<byte>(new byte[0x2C]);
        for (int i = 0; i < 21; i++)
        {
            var list = lists[i] ?? Array.Empty<byte>();
            if (list.Length == 0) continue;
            int listOffset = data.Count;
            data[2 + i * 2] = (byte)listOffset; data[3 + i * 2] = (byte)(listOffset >> 8);
            data.AddRange(list);
        }
        if (data.Count > 0x0FFF) throw new InvalidOperationException("the merged info block would be bigger than 4 KB.");
        data[0] = (byte)data.Count; data[1] = (byte)(data.Count >> 8);
        int blockOffset = L2Rom.Allocate(rom, data.Count + 1, upper: true);
        rom.WriteBytes(blockOffset, data.Append((byte)0).ToArray());
        int snes = L2Rom.FileToSnes(blockOffset);
        rom.WriteBytes(L2MapInfo.PointerAt(rom, map), new[] { (byte)snes, (byte)(snes >> 8), (byte)(snes >> 16) });
    }

    // reads the merged rom back: every map's events and info and every packed file as chosen, every event readable
    public void Verify(RomBuffer rom, List<string> report)
    {
        var now = new Content(rom);
        var bad = new List<string>();
        int maps = 0, events = 0;
        for (int map = 0; map < Math.Min(now.Maps, Math.Max(_a.Maps, _b.Maps)); map++)
        {
            var (want, init) = DesiredEvents(map);
            var current = now.Events(map)!;
            maps++;
            events += want.Count;
            if (!SameEvents(current, want, init)) bad.Add($"map {map:X2}'s events");
            foreach (var (key, ev) in current.Entries)
                if (ev.Problem != null && want.FirstOrDefault(w => w.Key == key).Ev?.Problem == null) bad.Add($"map {map:X2} event {key.Id:X2} of list +{key.Head:X} can't be read ({ev.Problem})");
            var wantInfo = DesiredInfo(map);
            var (_, currentInfo) = now.Info(map);
            if (wantInfo != null && (currentInfo == null || Enumerable.Range(0, 21).Any(i => !Eq(currentInfo[i], wantInfo[i])))) bad.Add($"map {map:X2}'s info");
        }
        int files = 0;
        foreach (int id in now.FileIds)
        {
            var want = DesiredFile(id);
            if (want == null) continue;
            files++;
            if (!Eq(now.File(id).Data, want)) bad.Add($"packed file {id:X3}");
        }
        if (bad.Count == 0) report.Add($"Checked the merged ROM: {events} events and the start-up scripts of {maps} maps, their info, and {files} packed files read back as chosen.");
        else report.Add("Checked the merged ROM: these don't read back as chosen: " + string.Join("; ", bad.Take(20)) + (bad.Count > 20 ? $" and {bad.Count - 20} more" : "") + ".");
    }
}

// line diffs for the merge window
public static class MergeDiff
{
    // the lines of ours and theirs in order: kept by both, only ours (-), only theirs (+).
    // long runs of kept lines get cut short
    public static List<DiffLine> Lines(IReadOnlyList<string> ours, IReadOnlyList<string> theirs)
    {
        static string Bare(string s) => s.Length > 5 && s[3] == '.' ? s[5..] : s;   // step numbers don't count
        int ourCount = ours.Count, theirCount = theirs.Count;
        var lcs = new int[ourCount + 1, theirCount + 1];
        for (int i = ourCount - 1; i >= 0; i--)
            for (int j = theirCount - 1; j >= 0; j--)
                lcs[i, j] = Bare(ours[i]) == Bare(theirs[j]) ? lcs[i + 1, j + 1] + 1 : Math.Max(lcs[i + 1, j], lcs[i, j + 1]);
        var raw = new List<DiffLine>();
        int ourIndex = 0, theirIndex = 0;
        while (ourIndex < ourCount || theirIndex < theirCount)
        {
            if (ourIndex < ourCount && theirIndex < theirCount && Bare(ours[ourIndex]) == Bare(theirs[theirIndex])) { raw.Add(new(DiffKind.Same, "  " + theirs[theirIndex])); ourIndex++; theirIndex++; }
            else if (theirIndex < theirCount && (ourIndex >= ourCount || lcs[ourIndex, theirIndex + 1] >= lcs[ourIndex + 1, theirIndex])) { raw.Add(new(DiffKind.Theirs, "+ " + theirs[theirIndex])); theirIndex++; }
            else { raw.Add(new(DiffKind.Ours, "- " + ours[ourIndex])); ourIndex++; }
        }
        // keep 3 lines of context around each change
        var keep = new bool[raw.Count];
        for (int i = 0; i < raw.Count; i++)
            if (raw[i].Kind != DiffKind.Same)
                for (int k = Math.Max(0, i - 3); k <= Math.Min(raw.Count - 1, i + 3); k++) keep[k] = true;
        var shown = new List<DiffLine>();
        bool cut = false;
        for (int i = 0; i < raw.Count; i++)
        {
            if (keep[i]) { shown.Add(raw[i]); cut = false; }
            else if (!cut) { shown.Add(new(DiffKind.Note, "  ...")); cut = true; }
        }
        if (shown.All(l => l.Kind is DiffKind.Same or DiffKind.Note)) shown.Add(new(DiffKind.Note, "(the steps read the same: they differ in bytes the listing doesn't show)"));
        return shown;
    }
}
