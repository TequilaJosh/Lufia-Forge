using LufiaForge.Core.Audio;
using LufiaForge.Core.Lufia2;

namespace LufiaForge.Core.Merge;

/// <summary>
/// The Lufia II things a merge handles by what they hold, wherever they sit (see <see cref="ProjectMerge"/>): each event
/// of each map and its start-up script, each list of a map's info block, each packed file. Compared three ways (the
/// base, this project, the other one) one by one, so two projects that changed different events of one map both keep
/// theirs. After the bytes are merged, each is made as chosen: one whose bytes the merge changed is written again in
/// free room (events in the event space, info blocks and files in the added space) and pointed at.
/// </summary>
internal sealed class L2MergeUnits
{
    private readonly Content _a, _ab, _b, _bb;
    public List<MergeItem> Items { get; } = new();
    public int SameCount { get; private set; }
    public List<string> Warnings { get; } = new();

    private readonly Dictionary<(int Map, int Head, int Id), MergeItem> _events = new();
    private readonly Dictionary<(int Map, int List), MergeItem> _info = new();
    private readonly Dictionary<int, MergeItem> _files = new();

    /// <summary>Where scripts are laid out to be compared (past every ROM, so nothing is taken for the old bytes there).</summary>
    private const int Probe = 0x7F8000;
    private const int InitKey = -1;

    public L2MergeUnits(RomBuffer a, RomBuffer ab, RomBuffer b, RomBuffer bb)
    {
        _a = new Content(a); _ab = new Content(ab); _b = new Content(b); _bb = ReferenceEquals(ab, bb) ? _ab : new Content(bb);
        MakeItems();
    }

    // ── what a ROM holds ───────────────────────────────────────────────────────

    /// <summary>An event (or the key table, or a start-up script) as compared: its bytes laid out at <see cref="Probe"/>.</summary>
    private sealed class Ev
    {
        public byte[] Canon = Array.Empty<byte>();
        public L2EventScript? Script;
        public byte[]? Key;
        public string? Problem;
        public int Start, End;   // the bytes it takes in its ROM
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

        public MapEvents? Events(int m) => m >= Maps ? null : _ev.TryGetValue(m, out var e) ? e : _ev[m] = ReadEvents(Rom, m);
        public (int Start, byte[]?[]? Lists) Info(int m) => m >= Maps ? (-1, null) : _info.TryGetValue(m, out var i) ? i : _info[m] = ReadInfo(Rom, m);
        public (byte[]? Data, int At, int Packed) File(int id) => !FileIds.Contains(id) ? (null, -1, 0) : _files.TryGetValue(id, out var f) ? f : _files[id] = ReadFile(Rom, id);
    }

    private static MapEvents ReadEvents(RomBuffer rom, int m)
    {
        var ev = new MapEvents();
        try
        {
            int block = L2Events.Block(rom, m);
            if (block >= 0)
            {
                ev.Block = block;
                var byOffset = new Dictionary<int, Ev>();
                int end = block + 0x0E;
                foreach (var e in L2EventBlocks.ReadLists(rom, block))
                {
                    var key = (e.Head, e.Id);
                    if (ev.Entries.ContainsKey(key)) continue;   // (the game takes the first one)
                    int k = e.IsScript ? e.Offset : 0x10000 + e.Offset;
                    if (!byOffset.TryGetValue(k, out var x)) byOffset[k] = x = e.IsScript ? ReadScript(rom, block + e.Offset, block) : ReadKey(rom, block, e.Offset);
                    ev.Entries[key] = x; ev.Order.Add(key);
                    end = Math.Max(end, x.End);
                }
                foreach (int head in L2EventBlocks.Heads)
                {
                    int at = rom.ReadUInt16Le(block + head), i = 0;
                    while (i < 128 && rom.ReadByte(block + at + i * 3) != 0xFF) i++;
                    end = Math.Max(end, block + at + i * 3 + 1);
                }
                if (L2EventBlocks.Chunk(rom, block) is { } c) { ev.BlockStart = block - 8; ev.BlockEnd = Math.Max(end, block + c.Room); }
                else { ev.BlockStart = block; ev.BlockEnd = end; }
            }
            int init = L2EventBlocks.InitScript(rom, m);
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

    private static Ev ReadScript(RomBuffer rom, int at, int jumpBase)
    {
        var s = L2EventScript.Read(rom, at, jumpBase);
        var x = new Ev { Script = s, Start = at, End = s.Steps.Count == 0 ? at + 1 : s.Steps.Max(t => t.At + t.OriginalLength) };
        x.Snippet = s.Steps.FirstOrDefault(t => t.IsText)?.Text.Replace("\n", " ").Replace("\r", "") ?? "";
        if (x.Snippet.Length > 50) x.Snippet = x.Snippet[..50] + "...";
        if (s.Problem != null) { x.Problem = s.Problem; x.Canon = System.Text.Encoding.UTF8.GetBytes($"?{at:X6} {s.Problem}"); return x; }
        var (bytes, error) = s.Detached().WithJumpBase(Probe).Build(rom, Probe);
        if (error != null) { x.Problem = error; x.Canon = System.Text.Encoding.UTF8.GetBytes($"?{at:X6} {error}"); return x; }
        x.Canon = bytes!;
        return x;
    }

    private static Ev ReadKey(RomBuffer rom, int block, int offset)
    {
        int p = offset;
        while (rom.ReadByte(block + p) != 0 && p < offset + 0x300) p += 3;
        var bytes = rom.ReadBytes(block + offset, p + 1 - offset);
        return new Ev { Key = bytes, Canon = bytes, Start = block + offset, End = block + p + 1 };
    }

    /// <summary>A map's info block: its 21 lists (the records up to FF where the record size is known, else the bytes up
    /// to the next list).</summary>
    private static (int Start, byte[]?[]? Lists) ReadInfo(RomBuffer rom, int m)
    {
        try
        {
            int b = L2MapInfo.Block(rom, m);
            if (b < 0 || b + 0x2C > rom.Length) return (-1, null);
            int size = rom.ReadUInt16Le(b);
            if (size < 0x2C || size > 0x1000 || b + size > rom.Length) return (-1, null);
            var offs = Enumerable.Range(0, 21).Select(i => (int)rom.ReadUInt16Le(b + 2 + i * 2)).ToArray();
            var lists = new byte[]?[21];
            for (int i = 0; i < 21; i++)
            {
                int off = offs[i];
                if (off <= 0 || off >= size + 1) { lists[i] = Array.Empty<byte>(); continue; }
                int rs = L2MapInfo.RecordSize(2 + i * 2);
                if (rs > 0)
                {
                    int p = b + off;
                    for (int guard = 0; guard < 128 && p + rs <= b + size + 1 && rom.ReadByte(p) != 0xFF; guard++) p += rs;
                    lists[i] = rom.ReadBytes(b + off, Math.Min(p + 1, b + size + 1) - (b + off));
                }
                else
                {
                    int next = offs.Where(o => o > off && o <= size).DefaultIfEmpty(size).Min();
                    lists[i] = rom.ReadBytes(b + off, next - off);
                }
            }
            return (b, lists);
        }
        catch (Exception) { return (-1, null); }
    }

    private static (byte[]? Data, int At, int Packed) ReadFile(RomBuffer rom, int id)
    {
        try
        {
            int at = L2Rom.FileOffset(rom, id);
            if (at < 0 || at >= rom.Length) return (null, -1, 0);
            var data = LufiaCompression.Decompress(rom, at, out int packed);
            return (data, at, packed);
        }
        catch (Exception) { return (null, -1, 0); }
    }

    private static bool Eq(byte[]? x, byte[]? y) => x == null ? y == null : y != null && x.AsSpan().SequenceEqual(y);
    private static bool Eq(Ev? x, Ev? y) => Eq(x?.Canon, y?.Canon);

    // ── which bytes belong to what ─────────────────────────────────────────────

    /// <summary>Marks the bytes of a ROM's events, info blocks and packed files (<paramref name="data"/>) and the pointers
    /// to them (<paramref name="ptr"/>; each pointer also in <paramref name="slots"/>).</summary>
    public void Own(RomBuffer rom, bool[] data, bool[] ptr, List<(int, int)>? slots)
    {
        var c = ReferenceEquals(rom, _a.Rom) ? _a : ReferenceEquals(rom, _b.Rom) ? _b : new Content(rom);
        int n = data.Length;
        void D(int s, int e) { for (int i = Math.Max(0, s); i < Math.Min(n, e); i++) data[i] = true; }
        void P(int s, int len) { for (int i = Math.Max(0, s); i < Math.Min(n, s + len); i++) ptr[i] = true; slots?.Add((s, len)); }
        for (int m = 0; m < c.Maps; m++)
        {
            try
            {
                int h = L2Events.HeaderAt(rom, m);
                P(h, 3); P(h + 3, 3);
                var ev = c.Events(m)!;
                if (ev.Block >= 0) D(ev.BlockStart, ev.BlockEnd);
                foreach (var x in ev.Entries.Values) D(x.Start, x.End);
                if (ev.Init != null) D(ev.InitStart, ev.InitEnd);
                P(L2MapInfo.PointerAt(rom, m), 3);
                var (b, lists) = c.Info(m);
                if (lists != null) D(b, b + rom.ReadUInt16Le(b) + 1);
            }
            catch (Exception) { }
        }
        foreach (int id in c.FileIds)
        {
            try
            {
                P(L2Rom.FileTableAt(rom) + id * 3, 3);
                var (d, at, packed) = c.File(id);
                if (d != null) D(at, at + packed);
            }
            catch (Exception) { }
        }
    }

    // ── what the other project changed ─────────────────────────────────────────

    private static string MapGroup(Content c, int m)
    {
        string name;
        try { name = L2Maps.Name(c.Rom, m); } catch (Exception) { name = ""; }
        return $"Map {m:X2}" + (string.IsNullOrWhiteSpace(name) ? "" : " " + name);
    }

    private static string EventName(Content c, int m, int head, int id) => head switch
    {
        InitKey => "Start-up script (makes the map's characters)",
        2 => id switch
        {
            0 => "Map event 00 (when the map is drawn)", 1 => "Map event 01 (on entering the map or a room of it)",
            2 => "Map event 02 (before the map loads)", 3 => "Locked doors' key table", _ => $"Map event {id:X2}",
        },
        4 => $"Scene {id:X2}",
        6 => id >= 0x50 ? $"Talking to character {id - 0x4F}" : $"Talking to {new L2EventCommands.Words().Who(id)}",
        8 => $"Stepping on area {id}" + Area(c, m, id),
        _ => $"Event {id:X2} of list +{head:X}",
    };

    private static string Area(Content c, int m, int id)
    {
        try
        {
            var t = L2MapInfo.Records(c.Rom, m, L2MapInfo.Triggers, 5).FirstOrDefault(r => r.Bytes[0] == id).Bytes;
            return t == null ? "" : $" ({t[1]},{t[2]})-({t[3] - 1},{t[4] - 1})";
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
            int k = 0;
            foreach (var key in keys)
            {
                var ea = a.Entries.GetValueOrDefault(key); var eb = b.Entries.GetValueOrDefault(key);
                var eab = ab?.Entries.GetValueOrDefault(key); var ebb = bb?.Entries.GetValueOrDefault(key);
                AddEvent(m, key.Head, key.Id, group, order + 10 + k++, ea, eab, eb, ebb);
            }
            AddEvent(m, InitKey, InitKey, group, order + 1, a.Init, ab?.Init, b.Init, bb?.Init);

            var (_, la) = _a.Info(m); var (_, lb) = _b.Info(m);
            var lab = _ab.Info(m).Lists; var lbb = _bb.Info(m).Lists;
            if (la != null && lb != null)
                for (int i = 0; i < 21; i++)
                {
                    byte[]? xa = la[i], xb = lb[i], xab = lab?[i], xbb = lbb?[i];
                    bool tc = !Eq(xb, xbb), oc = !Eq(xa, xab);
                    if (!tc) continue;
                    if (Eq(xa, xb)) { SameCount++; continue; }
                    int list = 2 + i * 2;
                    var item = new MergeItem
                    {
                        Group = group, What = $"Map info: {L2MapInfo.ListName(list)}", Conflict = oc, Take = !oc, Order = order + 500 + i,
                        Why = oc ? "Both projects changed this list of the map's info, differently." : "Only the other project changed this list of the map's info.",
                        Details = () => InfoDetails(list, xab, xa, xb),
                    };
                    _info[(m, list)] = item; Items.Add(item);
                }
        }

        foreach (int id in _a.FileIds.Intersect(_b.FileIds))
        {
            var (da, _, _) = _a.File(id); var (db, _, _) = _b.File(id);
            var dab = _ab.File(id).Data; var dbb = _bb.File(id).Data;
            bool tc = !Eq(db, dbb), oc = !Eq(da, dab);
            if (!tc) continue;
            if (Eq(da, db)) { SameCount++; continue; }
            var (group, what, order) = FileLabel(id);
            var item = new MergeItem
            {
                Group = group, What = what, Conflict = oc, Take = !oc && db != null, Order = order,
                Why = db == null ? "The other project's copy of this file can't be unpacked: it isn't taken." :
                      oc ? "Both projects changed this file, differently." : "Only the other project changed this file.",
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

    private void AddEvent(int m, int head, int id, string group, int order, Ev? a, Ev? ab, Ev? b, Ev? bb)
    {
        bool tc = !Eq(b, bb), oc = !Eq(a, ab);
        if (!tc) return;
        if (Eq(a, b)) { SameCount++; return; }
        bool broken = b?.Problem != null;
        string why = broken ? $"The other project's version can't be read ({b!.Problem}), so it isn't taken."
            : b == null ? (oc ? "The other project took this event out; this project changed it." : "The other project took this event out.")
            : bb == null && a == null ? "New in the other project."
            : bb == null ? "Both projects added an event here (the same list and number), with different steps."
            : a == null ? "This project took the event out; the other project changed it."
            : oc ? "Both projects changed this event, differently." : "Only the other project changed this event.";
        string snippet = (b ?? a)?.Snippet ?? "";
        var item = new MergeItem
        {
            Group = group,
            What = EventName(b != null ? _b : _a, m, head, id) + (snippet.Length > 0 ? $": \"{snippet}\"" : ""),
            Conflict = oc && !broken, Take = !oc && !broken, Order = order, Why = why,
            Details = () => EventDetails(ab, a, b, a != null ? _a.Rom : _ab.Rom, b != null ? _b.Rom : _bb.Rom),
        };
        _events[(m, head, id)] = item; Items.Add(item);
    }

    // ── differentials ──────────────────────────────────────────────────────────

    private static List<string> Listing(Ev? e, RomBuffer rom)
    {
        if (e == null) return new() { "(no such event)" };
        if (e.Problem != null) return new() { "(can't be read: " + e.Problem + ")" };
        if (e.Key != null) return Enumerable.Range(0, e.Key.Length / 3).Select(i => Convert.ToHexString(e.Key, i * 3, 3)).Append("00").ToList();
        var w = new L2EventCommands.Words();
        var steps = e.Script!.Steps;
        var lines = new List<string>();
        foreach (var s in steps)
        {
            string d;
            try { d = L2EventCommands.Find(s.Op)?.Describe(s.Bytes, w) ?? $"command {s.Op:X2}"; } catch (Exception) { d = $"command {s.Op:X2}"; }
            if (s.IsText) d = d.TrimEnd(':', ' ') + ": \"" + s.Text.Replace("\r", "").Replace("\n", " | ") + "\"";
            var jumps = s.Jumps.Select(j => j.Step != null ? steps.IndexOf(j.Step) + 1 : -1).ToList();
            if (jumps.Count > 0) d += " -> " + string.Join(", ", jumps.Select(j => j > 0 ? $"step {j}" : "?"));
            lines.Add(d);
        }
        return lines;
    }

    private static List<DiffLine> EventDetails(Ev? ab, Ev? a, Ev? b, RomBuffer ra, RomBuffer rb)
    {
        var la = Listing(a, ra);
        var lb = Listing(b, rb);
        var lines = new List<DiffLine>
        {
            new(DiffKind.Note, Eq(a, ab) ? "This project has the event as it started." : "This project changed the event too."),
            new(DiffKind.Head, "- this project's steps   + the other project's steps"),
        };
        lines.AddRange(MergeDiff.Lines(Number(la), Number(lb)));
        return lines;
    }

    private static List<string> Number(List<string> l) => l.Select((s, i) => $"{i + 1,3}. {s}").ToList();

    private static List<DiffLine> InfoDetails(int list, byte[]? ab, byte[]? a, byte[]? b)
    {
        int rs = L2MapInfo.RecordSize(list);
        List<string> Records(byte[]? x)
        {
            if (x == null) return new() { "(none)" };
            if (rs <= 0) return Enumerable.Range(0, (x.Length + 15) / 16).Select(i => Convert.ToHexString(x, i * 16, Math.Min(16, x.Length - i * 16))).ToList();
            var l = new List<string>();
            for (int p = 0; p + rs <= x.Length && x[p] != 0xFF; p += rs) l.Add(string.Join(" ", x.Skip(p).Take(rs).Select(v => v.ToString("X2"))));
            return l;
        }
        var lines = new List<DiffLine>
        {
            new(DiffKind.Note, (Eq(a, ab) ? "This project has the list as it started." : "This project changed the list too.") +
                (rs > 0 ? $" One record a line ({rs} bytes: the first is its number)." : "")),
            new(DiffKind.Head, "- this project's   + the other project's"),
        };
        lines.AddRange(MergeDiff.Lines(Records(a), Records(b)));
        return lines;
    }

    private static List<DiffLine> FileDetails(byte[]? ab, byte[]? a, byte[]? b)
    {
        var lines = new List<DiffLine>();
        if (a == null || b == null) { lines.Add(new(DiffKind.Note, a == null ? "This project's copy can't be unpacked." : "The other project's copy can't be unpacked.")); return lines; }
        int diff = 0, first = -1;
        for (int i = 0; i < Math.Max(a.Length, b.Length); i++)
            if (i >= a.Length || i >= b.Length || a[i] != b[i]) { diff++; if (first < 0) first = i; }
        lines.Add(new(DiffKind.Note, (Eq(a, ab) ? "This project has the file as it started. " : "This project changed the file too. ") +
            $"Unpacked: this project's {a.Length} bytes, the other project's {b.Length}; {diff} byte{(diff == 1 ? "" : "s")} differ."));
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

    // ── making them as chosen ──────────────────────────────────────────────────

    private (List<((int Head, int Id) Key, Ev Ev)> Events, Ev? Init) DesiredEvents(int m)
    {
        var a = _a.Events(m); var b = _b.Events(m);
        if (a == null) return (b!.Order.Select(k => (k, b.Entries[k])).ToList(), b.Init);
        var list = new List<((int, int), Ev)>();
        foreach (var key in a.Order.Concat(b?.Order ?? new()).Distinct())
        {
            var src = _events.TryGetValue((m, key.Head, key.Id), out var item) && item.Take ? b : a;
            if (src?.Entries.GetValueOrDefault(key) is { } e) list.Add((key, e));
        }
        var init = _events.TryGetValue((m, InitKey, InitKey), out var ii) && ii.Take ? b?.Init : a.Init;
        return (list, init);
    }

    private byte[]?[]? DesiredInfo(int m)
    {
        var (_, la) = _a.Info(m); var (_, lb) = _b.Info(m);
        if (la == null) return lb;
        var outp = (byte[]?[])la.Clone();
        for (int i = 0; i < 21; i++)
            if (_info.TryGetValue((m, 2 + i * 2), out var item) && item.Take && lb != null) outp[i] = lb[i];
        return outp;
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
        foreach (var (key, e) in want)
            if (!now.Entries.TryGetValue(key, out var x) || !Eq(x, e)) return false;
        return init == null || Eq(now.Init, init);
    }

    /// <summary>After the bytes are merged: every map's events, info lists and packed files made as chosen (written again
    /// in free room where the merged bytes don't hold them).</summary>
    public void Converge(RomBuffer r, List<string> report)
    {
        var now = new Content(r);
        int blocks = 0, inits = 0, infos = 0, files = 0;
        var problems = new List<string>();
        for (int m = 0; m < now.Maps; m++)
        {
            if (m >= _a.Maps && m >= _b.Maps) break;
            try
            {
                var (want, init) = DesiredEvents(m);
                var cur = now.Events(m)!;
                if (!SameEvents(cur, want, null))
                {
                    var broken = want.FirstOrDefault(w => w.Ev.Problem != null);
                    if (broken.Ev != null) problems.Add($"Map {m:X2}: event {broken.Key.Id:X2} of list +{broken.Key.Head:X} can't be read, so the map's events couldn't be laid out again.");
                    else
                    {
                        var events = want.Select(w => (new L2EventBlocks.Entry(w.Key.Head, w.Key.Id, 0), w.Ev.Script, w.Ev.Key)).ToList();
                        int ph = L2EventBlocks.WriteBlock(r, events);
                        L2EventBlocks.PointMap(r, m, ph);
                        blocks++;
                    }
                }
                if (init != null && !Eq(cur.Init, init))
                {
                    if (init.Problem != null) problems.Add($"Map {m:X2}: the start-up script can't be read, so it couldn't be written again.");
                    else { L2EventBlocks.WriteInitNew(r, m, init.Script!.Detached()); inits++; }
                }
                var wantInfo = DesiredInfo(m);
                var (_, curInfo) = now.Info(m);
                if (wantInfo != null && (curInfo == null || Enumerable.Range(0, 21).Any(i => !Eq(curInfo[i], wantInfo[i]))))
                {
                    WriteInfo(r, m, wantInfo);
                    infos++;
                }
            }
            catch (Exception ex) { problems.Add($"Map {m:X2}: {ex.Message}"); }
        }
        foreach (int id in now.FileIds)
        {
            try
            {
                var want = DesiredFile(id);
                if (want == null) continue;
                if (Eq(now.File(id).Data, want)) continue;
                L2Rom.WriteNewFile(r, id, want);
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

    private static void WriteInfo(RomBuffer r, int m, byte[]?[] lists)
    {
        var data = new List<byte>(new byte[0x2C]);
        for (int i = 0; i < 21; i++)
        {
            var l = lists[i] ?? Array.Empty<byte>();
            if (l.Length == 0) continue;
            int at = data.Count;
            data[2 + i * 2] = (byte)at; data[3 + i * 2] = (byte)(at >> 8);
            data.AddRange(l);
        }
        if (data.Count > 0x0FFF) throw new InvalidOperationException("the merged info block would be bigger than 4 KB.");
        data[0] = (byte)data.Count; data[1] = (byte)(data.Count >> 8);
        int to = L2Rom.Allocate(r, data.Count + 1, upper: true);
        r.WriteBytes(to, data.Append((byte)0).ToArray());
        int snes = L2Rom.FileToSnes(to);
        r.WriteBytes(L2MapInfo.PointerAt(r, m), new[] { (byte)snes, (byte)(snes >> 8), (byte)(snes >> 16) });
    }

    /// <summary>Reads the merged ROM again: every map's events, info and every packed file as chosen, every event readable.</summary>
    public void Verify(RomBuffer r, List<string> report)
    {
        var now = new Content(r);
        var bad = new List<string>();
        int maps = 0, events = 0;
        for (int m = 0; m < Math.Min(now.Maps, Math.Max(_a.Maps, _b.Maps)); m++)
        {
            var (want, init) = DesiredEvents(m);
            var cur = now.Events(m)!;
            maps++;
            events += want.Count;
            if (!SameEvents(cur, want, init)) bad.Add($"map {m:X2}'s events");
            foreach (var (key, e) in cur.Entries)
                if (e.Problem != null && want.FirstOrDefault(w => w.Key == key).Ev?.Problem == null) bad.Add($"map {m:X2} event {key.Id:X2} of list +{key.Head:X} can't be read ({e.Problem})");
            var wi = DesiredInfo(m);
            var (_, ci) = now.Info(m);
            if (wi != null && (ci == null || Enumerable.Range(0, 21).Any(i => !Eq(ci[i], wi[i])))) bad.Add($"map {m:X2}'s info");
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

/// <summary>Line differentials for the merge window.</summary>
public static class MergeDiff
{
    /// <summary>The lines of <paramref name="ours"/> and <paramref name="theirs"/> in order: kept by both, only ours
    /// (-), only theirs (+). Long runs of kept lines are cut short.</summary>
    public static List<DiffLine> Lines(IReadOnlyList<string> ours, IReadOnlyList<string> theirs)
    {
        static string Bare(string s) => s.Length > 5 && s[3] == '.' ? s[5..] : s;   // (step numbers don't count)
        int n = ours.Count, m = theirs.Count;
        var lcs = new int[n + 1, m + 1];
        for (int i = n - 1; i >= 0; i--)
            for (int j = m - 1; j >= 0; j--)
                lcs[i, j] = Bare(ours[i]) == Bare(theirs[j]) ? lcs[i + 1, j + 1] + 1 : Math.Max(lcs[i + 1, j], lcs[i, j + 1]);
        var raw = new List<DiffLine>();
        int x = 0, y = 0;
        while (x < n || y < m)
        {
            if (x < n && y < m && Bare(ours[x]) == Bare(theirs[y])) { raw.Add(new(DiffKind.Same, "  " + theirs[y])); x++; y++; }
            else if (y < m && (x >= n || lcs[x, y + 1] >= lcs[x + 1, y])) { raw.Add(new(DiffKind.Theirs, "+ " + theirs[y])); y++; }
            else { raw.Add(new(DiffKind.Ours, "- " + ours[x])); x++; }
        }
        // keep 3 lines of context round each change
        var keep = new bool[raw.Count];
        for (int i = 0; i < raw.Count; i++)
            if (raw[i].Kind != DiffKind.Same)
                for (int k = Math.Max(0, i - 3); k <= Math.Min(raw.Count - 1, i + 3); k++) keep[k] = true;
        var outp = new List<DiffLine>();
        bool cut = false;
        for (int i = 0; i < raw.Count; i++)
        {
            if (keep[i]) { outp.Add(raw[i]); cut = false; }
            else if (!cut) { outp.Add(new(DiffKind.Note, "  ...")); cut = true; }
        }
        if (outp.All(l => l.Kind is DiffKind.Same or DiffKind.Note)) outp.Add(new(DiffKind.Note, "(the steps read the same: they differ in bytes the listing doesn't show)"));
        return outp;
    }
}
