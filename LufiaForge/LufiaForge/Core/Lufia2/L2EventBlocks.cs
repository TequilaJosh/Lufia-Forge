namespace LufiaForge.Core.Lufia2;

/// <summary>
/// Writing Lufia II event scripts back. A map's event block ("PH", the map header's first u24) starts "PH" and six
/// u16 offsets to lists of (id, u16 script offset) records ended by FF: +2 run by the game itself (00 when the map is
/// drawn, 01 on entering the map or another room of it, 02 before the map loads; 03 is the key table of locked doors,
/// not a script), +4 scenes (started by command 16 from any map), +6 talking to a character (id = the character),
/// +8 stepping on a trigger area (id = the map info list +0C record), +A and +C (never read). Jumps are offsets from
/// the "PH".
/// <para>
/// A script that keeps its size is written over its old bytes. Otherwise the whole block moves into the event space
/// (<see cref="L2EventSpace"/>, a bank of $A8-$BF) once: [ "LF2B" | u16 room | u16 size of the copied part ] then the
/// copy, so every script and offset of the game stays as it was. After the copy comes the "added part", laid out again
/// on every save: the six lists, then each script that lives there (a changed or new event, written whole with the
/// code it shares with others, so it never depends on anything else). The map header points at the copy.
/// </para>
/// </summary>
public static class L2EventBlocks
{
    public static readonly int[] Heads = { 2, 4, 6, 8, 0x0A, 0x0C };
    public const int TalkList = 6, TriggerList = 8, SceneList = 4, GameList = 2;

    /// <summary>A record of an event list: the list (+2..+C), the id, the script offset from the "PH".</summary>
    public sealed record Entry(int Head, int Id, int Offset)
    {
        /// <summary>The +2 list's id 03 is the locked-door key table, not a script.</summary>
        public bool IsScript => !(Head == GameList && Id == 3);
    }

    public static List<Entry> ReadLists(RomBuffer rom, int block)
    {
        var list = new List<Entry>();
        foreach (int head in Heads)
        {
            int at = block + rom.ReadUInt16Le(block + head);
            for (int i = 0; i < 128; i++)
            {
                byte id = rom.ReadByte(at + i * 3);
                if (id == 0xFF) break;
                list.Add(new Entry(head, id, rom.ReadUInt16Le(at + i * 3 + 1)));
            }
        }
        return list;
    }

    /// <summary>The event-space chunk a block was moved to: its room (from the "PH") and the size of the copied part.</summary>
    public static (int Room, int Copied)? Chunk(RomBuffer rom, int block)
    {
        int h = block - 8;
        if (h < 0 || !L2EventSpace.Contains(rom, block)) return null;
        if (rom.ReadByte(h) != 'L' || rom.ReadByte(h + 1) != 'F' || rom.ReadByte(h + 2) != '2' || rom.ReadByte(h + 3) != 'B') return null;
        return (rom.ReadUInt16Le(h + 4), rom.ReadUInt16Le(h + 6));
    }

    /// <summary>The u24 the map header holds for a block or start-up script at <paramref name="file"/> (bank $87 + hi,
    /// address lo + $8000).</summary>
    public static int HeaderValue(int file) => ((file / 0x8000 | 0x80) - 0x87) << 16 | file % 0x8000;

    private static void PointMaps(RomBuffer rom, int field, int oldAt, int newAt)
    {
        for (int m = 0; m < L2Maps.Count; m++)
        {
            int e = L2Events.HeaderTable + 0x10 + m * 8 + field;
            int v = (int)rom.ReadUInt24Le(e);
            if (HeaderToFile(v) != oldAt) continue;
            int n = HeaderValue(newAt);
            rom.WriteBytes(e, new[] { (byte)n, (byte)(n >> 8), (byte)(n >> 16) });
        }
    }

    /// <summary>The start-up script of a map (file offset).</summary>
    public static int InitScript(RomBuffer rom, int map)
    {
        return HeaderToFile((int)rom.ReadUInt24Le(L2Events.HeaderTable + 0x10 + map * 8 + 3));
    }

    /// <summary>The file offset a map header u24 points at.</summary>
    public static int HeaderToFile(int v) => L2Rom.SnesToFile((0x87 + (v >> 16)) << 16 | (0x8000 + (v & 0xFFFF)) & 0xFFFF);

    /// <summary>Init-script jumps are offsets from the start of the bank the script runs in.</summary>
    public static int InitBase(int init) => init / 0x8000 * 0x8000;

    /// <summary>The bytes of the block the game's data uses: header, lists, key table, every script reachable from
    /// the lists.</summary>
    public static int Extent(RomBuffer rom, int block)
    {
        int end = 0x0E;
        foreach (int head in Heads)
        {
            int at = rom.ReadUInt16Le(block + head);
            int i = 0;
            while (rom.ReadByte(block + at + i * 3) != 0xFF && i < 128) i++;
            end = Math.Max(end, at + i * 3 + 1);
        }
        foreach (var e in ReadLists(rom, block))
        {
            if (!e.IsScript)
            {
                int p = e.Offset;
                while (rom.ReadByte(block + p) != 0 && p < e.Offset + 0x300) p += 3;
                end = Math.Max(end, p + 1);
                continue;
            }
            foreach (var s in L2EventScript.Read(rom, block + e.Offset, block).Steps)
                end = Math.Max(end, s.At + s.OriginalLength - block);
        }
        return end;
    }

    /// <summary>For each step address of the block, the events that run it.</summary>
    public static Dictionary<int, List<Entry>> StepOwners(RomBuffer rom, int block)
    {
        var owners = new Dictionary<int, List<Entry>>();
        foreach (var e in ReadLists(rom, block).Where(e => e.IsScript))
            foreach (var s in L2EventScript.Read(rom, block + e.Offset, block).Steps)
            {
                if (!owners.TryGetValue(s.At, out var l)) owners[s.At] = l = new();
                if (!l.Contains(e)) l.Add(e);
            }
        return owners;
    }

    /// <summary>Writes an event's script: over its old bytes when it fits there and changes nothing another event
    /// shares, else in the block's added part (moving the block into the event space first). Returns what was done.</summary>
    public static string Save(RomBuffer rom, int map, Entry entry, L2EventScript script)
    {
        int block = L2Events.Block(rom, map);
        if (block < 0) throw new InvalidOperationException("This map has no event block.");
        if (script.Steps.Count == 0) throw new InvalidOperationException("An event needs at least one step.");
        if (script.Start == block + entry.Offset && script.FitsInPlace(rom, script.ReadStarts))
        {
            var (changes, error) = script.InPlaceChanges(rom);
            if (error != null) throw new InvalidOperationException(error);
            if (changes!.Count == 0) return "Nothing changed.";
            var owners = StepOwners(rom, block);
            bool shared = changes.Any(c => owners.TryGetValue(c.Step.At, out var l) && l.Any(o => o != entry));
            if (!shared)
            {
                foreach (var (step, bytes) in changes) rom.WriteBytes(step.At, bytes);
                return $"Written over the old bytes ({changes.Count} step{(changes.Count == 1 ? "" : "s")}).";
            }
        }
        var lists = ReadLists(rom, block);
        int i = lists.IndexOf(entry);
        if (i < 0) throw new InvalidOperationException("The event is no longer in the map's lists.");
        return Relayout(rom, map, lists.Select((e, k) => (e, k == i ? script : null)).ToList());
    }

    /// <summary>Adds an event to a list (talk: id = the character, trigger: id = the trigger area).</summary>
    public static string Add(RomBuffer rom, int map, int head, int id, L2EventScript script)
    {
        int block = L2Events.Block(rom, map);
        if (block < 0) throw new InvalidOperationException("This map has no event block.");
        var lists = ReadLists(rom, block);
        if (lists.Any(e => e.Head == head && e.Id == id)) throw new InvalidOperationException("That event already exists.");
        var all = lists.Select(e => (e, (L2EventScript?)null)).ToList();
        all.Add((new Entry(head, id, -1), script));
        return Relayout(rom, map, all);
    }

    /// <summary>Takes an event out of its list (its code stays, unused). Done in place: the list only gets shorter.</summary>
    public static void Remove(RomBuffer rom, int map, Entry entry)
    {
        int block = L2Events.Block(rom, map);
        int at = block + rom.ReadUInt16Le(block + entry.Head);
        for (int i = 0; i < 128 && rom.ReadByte(at + i * 3) != 0xFF; i++)
        {
            if (rom.ReadByte(at + i * 3) != entry.Id) continue;
            int n = i;
            while (rom.ReadByte(at + (n + 1) * 3) != 0xFF) n++;
            var rest = rom.ReadBytes(at + (i + 1) * 3, (n - i) * 3 + 1);
            rom.WriteBytes(at + i * 3, rest);
            return;
        }
        throw new InvalidOperationException("The event is no longer in the map's lists.");
    }

    /// <summary>Lays the block's added part out again: the lists, then the scripts given (new or changed) and those
    /// already living there. Moves the block into the event space when it isn't there yet, and to a bigger chunk when
    /// the added part outgrows its room.</summary>
    private static string Relayout(RomBuffer rom, int map, List<(Entry Entry, L2EventScript? Script)> all)
    {
        int block = L2Events.Block(rom, map);
        var chunk = Chunk(rom, block);
        int copied = chunk?.Copied ?? Extent(rom, block);
        // scripts already in the added part are read now (they are laid out again); several records may share one
        var shared = new Dictionary<int, L2EventScript>();
        var items = new List<(Entry Entry, L2EventScript? Script)>();
        foreach (var (e, s) in all)
        {
            if (s != null || !e.IsScript || e.Offset < copied) { items.Add((e, s)); continue; }
            if (!shared.TryGetValue(e.Offset, out var old)) shared[e.Offset] = old = L2EventScript.Read(rom, block + e.Offset, block);
            items.Add((e, old));
        }
        foreach (var (e, s) in items)
            if (s?.Problem != null && s.Start >= 0) throw new InvalidOperationException($"Event {e.Id:X2} can't be laid out again: {s.Problem}");

        // where things go, relative to the PH: lists first, then scripts
        int p = copied;
        var listAt = new Dictionary<int, int>();
        foreach (int head in Heads) { listAt[head] = p; p += items.Count(x => x.Entry.Head == head) * 3 + 1; }
        int scriptsFrom = p;

        byte[] Lay(int phAt, out Dictionary<L2EventScript, int> placed)
        {
            placed = new Dictionary<L2EventScript, int>();
            var outp = new List<byte>();
            int q = scriptsFrom;
            foreach (var s in items.Select(x => x.Script).Where(s => s != null).Distinct())
            {
                var script = Relocated(s!, phAt);
                var (bytes, error) = script.Build(rom, phAt + q);
                if (error != null) throw new InvalidOperationException(error);
                placed[s!] = q;
                outp.AddRange(bytes!);
                q += bytes!.Length;
            }
            return outp.ToArray();
        }

        // first pass at a bank start to learn the size (sizes don't depend on the address)
        var scripts = Lay(0x1F8000, out _);
        int total = scriptsFrom + scripts.Length;
        int phNew = block;
        if (chunk != null && total > chunk.Value.Room)
        {
            // the chunk was the last thing put in its bank: it just grows
            int room = Math.Min(L2EventSpace.Mark - 8, total + 0x200);
            if (total <= room && L2EventSpace.Grow(rom, block - 8, 8 + chunk.Value.Room, 8 + room))
            {
                rom.WriteBytes(block - 4, new[] { (byte)room, (byte)(room >> 8) });
                chunk = (room, copied);
            }
        }
        if (chunk == null || total > chunk.Value.Room)
        {
            int room = Math.Min(L2EventSpace.Mark - 8, total + Math.Max(0x200, total / 8));
            if (total > room) throw new InvalidOperationException($"The map's events would take {total} bytes; one bank holds {room}.");
            int at = L2EventSpace.Allocate(rom, room + 8);
            phNew = at + 8;
            var copy = rom.ReadBytes(block, copied);
            rom.WriteBytes(at, new[] { (byte)'L', (byte)'F', (byte)'2', (byte)'B', (byte)room, (byte)(room >> 8), (byte)copied, (byte)(copied >> 8) });
            rom.WriteBytes(phNew, copy);
            PointMaps(rom, 0, block, phNew);
            if (chunk != null)
            {
                rom.WriteBytes(block - 8, new byte[8 + chunk.Value.Room]);
                L2EventSpace.Release(rom, block - 8, 8 + chunk.Value.Room);
            }
            scripts = Lay(phNew, out _);
        }
        Lay(phNew, out var placedAt);
        // lists
        foreach (int head in Heads)
        {
            var bytes = new List<byte>();
            foreach (var (e, s) in items.Where(x => x.Entry.Head == head))
            {
                int off = s != null ? placedAt[s] : e.Offset;
                bytes.Add((byte)e.Id); bytes.Add((byte)off); bytes.Add((byte)(off >> 8));
            }
            bytes.Add(0xFF);
            rom.WriteBytes(phNew + listAt[head], bytes.ToArray());
            rom.WriteBytes(phNew + head, new[] { (byte)listAt[head], (byte)(listAt[head] >> 8) });
        }
        rom.WriteBytes(phNew + scriptsFrom, scripts);
        return phNew == block
            ? $"Written in the map's added event space ({total} of {Chunk(rom, phNew)!.Value.Room} bytes used)."
            : $"The map's events moved to {L2Rom.FileToSnes(phNew):X6} in the event space (bank {phNew / 0x8000 | 0x80:X2}) to make room.";
    }

    /// <summary>A script read from the ROM keeps its jumps to its own steps; laid out elsewhere its jumps stay right.</summary>
    private static L2EventScript Relocated(L2EventScript s, int phAt) => s.JumpBase == phAt ? s : s.WithJumpBase(phAt);

    /// <summary>Writes a map's start-up script: over its old bytes when it fits, else into the event space (its jumps are
    /// offsets from the start of its own bank, so it is written whole within one bank).</summary>
    public static string SaveInit(RomBuffer rom, int map, L2EventScript script)
    {
        int init = InitScript(rom, map);
        if (script.Start == init && script.FitsInPlace(rom, script.ReadStarts))
        {
            var (changes, error) = script.InPlaceChanges(rom);
            if (error != null) throw new InvalidOperationException(error);
            if (changes!.Count == 0) return "Nothing changed.";
            foreach (var (step, bytes) in changes) rom.WriteBytes(step.At, bytes);
            return $"Written over the old bytes ({changes.Count} step{(changes.Count == 1 ? "" : "s")}).";
        }
        // the size first (any bank start gives the same sizes)
        var (probe, err) = script.WithJumpBase(0x200000 - 0x8000).Build(rom, 0x200000 - 0x8000);
        if (err != null) throw new InvalidOperationException(err);
        int size = probe!.Length;
        int h = init - 8;
        int at;
        if (L2EventSpace.Contains(rom, init) && h >= 0 && rom.ReadByte(h) == 'L' && rom.ReadByte(h + 1) == 'F' && rom.ReadByte(h + 2) == '2' && rom.ReadByte(h + 3) == 'I'
            && rom.ReadUInt16Le(h + 4) >= size)
            at = init;
        else
        {
            int room = size + 0x40;
            int chunk = L2EventSpace.Allocate(rom, room + 8);
            rom.WriteBytes(chunk, new[] { (byte)'L', (byte)'F', (byte)'2', (byte)'I', (byte)room, (byte)(room >> 8), (byte)0, (byte)0 });
            at = chunk + 8;
        }
        var (built, e2) = script.WithJumpBase(InitBase(at)).Build(rom, at);
        if (e2 != null) throw new InvalidOperationException(e2);
        rom.WriteBytes(at, built!);
        if (at != init)
        {
            int n = HeaderValue(at), e = L2Events.HeaderTable + 0x10 + map * 8 + 3;
            rom.WriteBytes(e, new[] { (byte)n, (byte)(n >> 8), (byte)(n >> 16) });
        }
        return at == init ? "Written in the event space." : $"The start-up script moved to {L2Rom.FileToSnes(at):X6} in the event space to make room.";
    }

    // ------------------------------------------------------------------ new events

    /// <summary>A new event: (when <paramref name="onceFlag"/> &gt;= 0) "if the flag is on, go to the end" and "turn the flag
    /// on", then the text, then the end.</summary>
    public static L2EventScript NewScript(int jumpBase, byte[] opener, string text, int onceFlag)
    {
        var s = L2EventScript.New(jumpBase);
        var end = s.Steps[0];
        var say = new L2Step { Bytes = opener, IsText = true, Text = text, End = 0x01, OriginalEnd = 0xFF, OriginalText = "\u0001" };
        s.Steps.Insert(0, say);
        if (onceFlag >= 0)
        {
            var check = new L2Step { Bytes = new byte[] { 0x15, (byte)onceFlag, 0, 0 } };
            check.Jumps.Add(new L2Jump { Step = end });
            s.Steps.Insert(0, check);
            s.Steps.Insert(1, new L2Step { Bytes = new byte[] { 0x1A, (byte)onceFlag } });
        }
        return s;
    }

    /// <summary>The trigger-area ids of a map in use (map info list +0C and the event block's +8 list).</summary>
    public static int NextTriggerId(RomBuffer rom, int map)
    {
        var used = L2MapInfo.Records(rom, map, L2MapInfo.Triggers, 5).Select(r => (int)r.Bytes[0]).ToHashSet();
        int block = L2Events.Block(rom, map);
        if (block >= 0) used.UnionWith(ReadLists(rom, block).Where(e => e.Head == TriggerList).Select(e => e.Id));
        int id = 1;
        while (used.Contains(id)) id++;
        return id;
    }

    /// <summary>A new event run by stepping onto tiles (x1, y1)-(x2, y2) (inclusive): a record in the map info's trigger
    /// list and the script in the +8 list. Returns the id.</summary>
    public static int AddTrigger(RomBuffer rom, int map, int x1, int y1, int x2, int y2, L2EventScript script)
    {
        int id = NextTriggerId(rom, map);
        if (id > 0xFE) throw new InvalidOperationException("The map has no free trigger number.");
        if (!L2MapInfo.AddRecord(rom, map, L2MapInfo.Triggers, 5, new[] { (byte)id, (byte)x1, (byte)y1, (byte)(x2 + 1), (byte)(y2 + 1) }))
            throw new InvalidOperationException("The map's info block would be too big.");
        Add(rom, map, TriggerList, id, script);
        return id;
    }

    /// <summary>The map characters' actor ids in use (character starts, the start-up script's 68/7B, the talk list).</summary>
    public static HashSet<int> CharacterIds(RomBuffer rom, int map)
    {
        var used = L2MapInfo.Records(rom, map, L2MapInfo.Characters, 8).Select(r => 0x4F + r.Bytes[0]).ToHashSet();
        int init = InitScript(rom, map);
        foreach (var s in L2EventScript.Read(rom, init, InitBase(init)).Steps)
            if (s.Op is 0x68 or 0x7B) used.Add(s.Bytes[1]);
        int block = L2Events.Block(rom, map);
        if (block >= 0) used.UnionWith(ReadLists(rom, block).Where(e => e.Head == TalkList && e.Id >= 0x50).Select(e => e.Id));
        return used;
    }

    /// <summary>Adds a character to a map: a character start (where it stands, the area it may walk in, how it moves) and
    /// "Add a map character" at the top of the start-up script. Returns its actor id.</summary>
    public static int AddCharacter(RomBuffer rom, int map, int x, int y, int sprite, int mode)
    {
        var used = CharacterIds(rom, map);
        int id = 0x50;
        while (used.Contains(id)) id++;
        if (id > 0x6F) throw new InvalidOperationException("The map has 32 characters already.");
        int init = InitScript(rom, map);
        var script = L2EventScript.Read(rom, init, InitBase(init));
        if (script.Problem != null) throw new InvalidOperationException("The map's start-up script can't be changed: " + script.Problem);
        int creates = script.Steps.Count(s => s.Op is 0x68 or 0x7B);
        if (creates >= 31) throw new InvalidOperationException("The map has as many characters as the game can show.");
        if (!L2MapInfo.AddRecord(rom, map, L2MapInfo.Characters, 8, new[]
            { (byte)(id - 0x4F), (byte)x, (byte)y, (byte)Math.Max(0, x - 2), (byte)Math.Max(0, y - 2), (byte)(x + 3), (byte)(y + 3), (byte)mode }))
            throw new InvalidOperationException("The map's info block would be too big.");
        script.Steps.Insert(0, new L2Step { Bytes = new byte[] { 0x68, (byte)id, (byte)sprite } });
        SaveInit(rom, map, script);
        return id;
    }
}
