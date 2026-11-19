namespace LufiaForge.Core.Lufia2;

// writing lufia 2 event scripts back. a map's event block ("PH", the map header's first u24) starts "PH" and six u16
// offsets to lists of (id, u16 script offset) records ended by FF:
// - +2 run by the game itself (00 when the map is drawn, 01 on entering the map or another room of it, 02 before
//   the map loads, 03 is the key table of locked doors, not a script)
// - +4 scenes (started by command 16 from any map)
// - +6 talking to a character (id = the character)
// - +8 stepping on a trigger area (id = the map info list +0C record)
// - +A and +C (never read)
// jumps are offsets from the "PH"
//
// a script that keeps its size gets written over its old bytes. otherwise the whole block moves into the event
// space (L2EventSpace, a bank of $A8-$BF) once: [ "LF2B" | u16 room | u16 size of the copied part ] then the copy,
// so every script and offset of the game stays how it was. after the copy comes the "added part", laid out again on
// every save: the six lists, then each script that lives there (a changed or new event, written whole with the code
// it shares with others, so it never depends on anything else). the map header points at the copy
public static class L2EventBlocks
{
    public static readonly int[] Heads = { 2, 4, 6, 8, 0x0A, 0x0C };
    public const int TalkList = 6, TriggerList = 8, SceneList = 4, GameList = 2;

    // a record of an event list: the list (+2..+C), the id, the script offset from the "PH"
    public sealed record Entry(int Head, int Id, int Offset)
    {
        // the +2 list's id 03 is the locked-door key table, not a script
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

    // the event-space chunk a block got moved to: its room (from the "PH") and the size of the copied part
    public static (int Room, int Copied)? Chunk(RomBuffer rom, int block)
    {
        int h = block - 8;
        if (h < 0 || !L2EventSpace.Contains(rom, block)) return null;
        if (rom.ReadByte(h) != 'L' || rom.ReadByte(h + 1) != 'F' || rom.ReadByte(h + 2) != '2' || rom.ReadByte(h + 3) != 'B') return null;
        return (rom.ReadUInt16Le(h + 4), rom.ReadUInt16Le(h + 6));
    }

    // the u24 the map header holds for a block or start-up script at file (bank $87 + hi, address lo + $8000)
    public static int HeaderValue(int file) => ((L2Rom.FileToSnes(file) >> 16) - 0x87 & 0xFF) << 16 | file % 0x8000;

    private static void PointMaps(RomBuffer rom, int field, int oldAt, int newAt)
    {
        for (int m = 0; m < L2Maps.Count(rom); m++)
        {
            int entryAt = L2Events.HeaderAt(rom, m) + field;
            int value = (int)rom.ReadUInt24Le(entryAt);
            if (HeaderToFile(value) != oldAt) continue;
            int newValue = HeaderValue(newAt);
            rom.WriteBytes(entryAt, new[] { (byte)newValue, (byte)(newValue >> 8), (byte)(newValue >> 16) });
        }
    }

    // the start-up script of a map (file offset)
    public static int InitScript(RomBuffer rom, int map)
    {
        return HeaderToFile((int)rom.ReadUInt24Le(L2Events.HeaderAt(rom, map) + 3));
    }

    // the file offset a map header u24 points at
    public static int HeaderToFile(int value) => L2Rom.SnesToFile((0x87 + (value >> 16) & 0xFF) << 16 | (0x8000 + (value & 0xFFFF)) & 0xFFFF);

    // init-script jumps are offsets from the start of the bank the script runs in
    public static int InitBase(int init) => init / 0x8000 * 0x8000;

    // the bytes of the block the game's data uses: header, lists, key table, every script reachable from the lists
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
        foreach (var entry in ReadLists(rom, block))
        {
            if (!entry.IsScript)
            {
                int position = entry.Offset;
                while (rom.ReadByte(block + position) != 0 && position < entry.Offset + 0x300) position += 3;
                end = Math.Max(end, position + 1);
                continue;
            }
            foreach (var step in L2EventScript.Read(rom, block + entry.Offset, block).Steps)
                end = Math.Max(end, step.At + step.OriginalLength - block);
        }
        return end;
    }

    // for each step address of the block, the events that run it
    public static Dictionary<int, List<Entry>> StepOwners(RomBuffer rom, int block)
    {
        var owners = new Dictionary<int, List<Entry>>();
        foreach (var e in ReadLists(rom, block).Where(e => e.IsScript))
            foreach (var step in L2EventScript.Read(rom, block + e.Offset, block).Steps)
            {
                if (!owners.TryGetValue(step.At, out var list)) owners[step.At] = list = new();
                if (!list.Contains(e)) list.Add(e);
            }
        return owners;
    }

    // a script that uses an extra story flag (100-17F) needs the extra flags patch, so add it here if it isn't in
    // (a pasted step, a flag picked before the patch went in)
    public static void NeedExtraFlags(RomBuffer rom, L2EventScript script)
    {
        if (L2ExtraFlags.Uses(script.Steps.Where(s => !s.IsText).Select(s => s.Bytes)) && !L2ExtraFlags.IsApplied(rom)) L2ExtraFlags.Apply(rom);
    }

    // writes an event's script: over its old bytes if it fits there and changes nothing another event shares, else
    // in the block's added part (moving the block into the event space first). returns what was done
    public static string Save(RomBuffer rom, int map, Entry entry, L2EventScript script)
    {
        NeedExtraFlags(rom, script);
        int block = L2Events.Block(rom, map);
        if (block < 0) throw new InvalidOperationException("This map has no event block.");
        if (script.Steps.Count == 0) throw new InvalidOperationException("An event needs at least one step.");
        if (script.Start == block + entry.Offset && script.FitsInPlace(rom, script.ReadStarts))
        {
            var (changes, error) = script.InPlaceChanges(rom);
            if (error != null) throw new InvalidOperationException(error);
            if (changes!.Count == 0) return "Nothing changed.";
            var owners = StepOwners(rom, block);
            bool shared = changes.Any(c => owners.TryGetValue(c.Step.At, out var ownersThere) && ownersThere.Any(o => o != entry));
            // text that other text repeats (0A) keeps its bytes: the event is laid out again instead
            if (!shared && L2Events.CopiedFrom(rom, changes.Select(c => (c.Step.At, c.Bytes))) < 0)
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

    // writes several changed events of one map at once, in the block's added part (moving the block into the event
    // space first). events that share one script get the same script object
    public static string SaveAll(RomBuffer rom, int map, IReadOnlyDictionary<Entry, L2EventScript> scripts) => SaveAll(rom, map, scripts, null);

    // SaveAll(RomBuffer, int, IReadOnlyDictionary<Entry, L2EventScript>), with events given another id (newId: a talk
    // event that goes with a character whose actor id changed)
    public static string SaveAll(RomBuffer rom, int map, IReadOnlyDictionary<Entry, L2EventScript> scripts, Func<Entry, int>? newId)
    {
        foreach (var changedScript in scripts.Values) NeedExtraFlags(rom, changedScript);
        int block = L2Events.Block(rom, map);
        if (block < 0) throw new InvalidOperationException("This map has no event block.");
        var lists = ReadLists(rom, block);
        foreach (var e in scripts.Keys)
            if (!lists.Contains(e)) throw new InvalidOperationException("The event is no longer in the map's lists.");
        return Relayout(rom, map, lists.Select(e => (newId == null ? e : e with { Id = newId(e) }, scripts.TryGetValue(e, out var script) ? script : null)).ToList());
    }

    // adds an event to a list (talk: id = the character, trigger: id = the trigger area)
    public static string Add(RomBuffer rom, int map, int head, int id, L2EventScript script)
    {
        NeedExtraFlags(rom, script);
        int block = L2Events.Block(rom, map);
        if (block < 0) throw new InvalidOperationException("This map has no event block.");
        var lists = ReadLists(rom, block);
        if (lists.Any(e => e.Head == head && e.Id == id)) throw new InvalidOperationException("That event already exists.");
        var all = lists.Select(e => (e, (L2EventScript?)null)).ToList();
        all.Add((new Entry(head, id, -1), script));
        return Relayout(rom, map, all);
    }

    // takes an event out of its list (its code stays, unused). done in place, the list only gets shorter
    public static void Remove(RomBuffer rom, int map, Entry entry)
    {
        int block = L2Events.Block(rom, map);
        int at = block + rom.ReadUInt16Le(block + entry.Head);
        for (int i = 0; i < 128 && rom.ReadByte(at + i * 3) != 0xFF; i++)
        {
            if (rom.ReadByte(at + i * 3) != entry.Id) continue;
            int last = i;
            while (rom.ReadByte(at + (last + 1) * 3) != 0xFF) last++;
            var rest = rom.ReadBytes(at + (i + 1) * 3, (last - i) * 3 + 1);
            rom.WriteBytes(at + i * 3, rest);
            return;
        }
        throw new InvalidOperationException("The event is no longer in the map's lists.");
    }

    // lays the block's added part out again: the lists, then the scripts given (new or changed) and the ones already
    // living there. moves the block into the event space if it isn't there yet, and to a bigger chunk when the added
    // part outgrows its room
    private static string Relayout(RomBuffer rom, int map, List<(Entry Entry, L2EventScript? Script)> all)
    {
        int block = L2Events.Block(rom, map);
        var chunk = Chunk(rom, block);
        int copied = chunk?.Copied ?? Extent(rom, block);
        // read the scripts already in the added part now (they get laid out again), several records can share one
        var shared = new Dictionary<int, L2EventScript>();
        var items = new List<(Entry Entry, L2EventScript? Script)>();
        foreach (var (entry, script) in all)
        {
            if (script != null || !entry.IsScript || entry.Offset < copied) { items.Add((entry, script)); continue; }
            if (!shared.TryGetValue(entry.Offset, out var old)) shared[entry.Offset] = old = L2EventScript.Read(rom, block + entry.Offset, block);
            items.Add((entry, old));
        }
        foreach (var (entry, script) in items)
            if (script?.Problem != null && script.Start >= 0) throw new InvalidOperationException($"Event {entry.Id:X2} can't be laid out again: {script.Problem}");
        // nothing here is filled up to an old size (a script that fit in place but shares steps still has PadTo set,
        // laid out at its old address it would be padded, at the trial address not)
        foreach (var (_, script) in items) if (script != null) foreach (var step in script.Steps) step.PadTo = 0;

        // where things go, relative to the PH: lists first, then scripts
        int position = copied;
        var listAt = new Dictionary<int, int>();
        foreach (int head in Heads) { listAt[head] = position; position += items.Count(x => x.Entry.Head == head) * 3 + 1; }
        int scriptsFrom = position;

        byte[] Lay(int phAt, out Dictionary<L2EventScript, int> placed)
        {
            placed = new Dictionary<L2EventScript, int>();
            var output = new List<byte>();
            int offset = scriptsFrom;
            foreach (var s in items.Select(x => x.Script).Where(s => s != null).Distinct())
            {
                var script = Relocated(s!, phAt);
                var (bytes, error) = script.Build(rom, phAt + offset);
                if (error != null) throw new InvalidOperationException(error);
                placed[s!] = offset;
                output.AddRange(bytes!);
                offset += bytes!.Length;
            }
            return output.ToArray();
        }

        // first pass at a bank start to learn the size (with nothing padded the size doesn't depend on the address)
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
            int chunkAt = L2EventSpace.Allocate(rom, room + 8);
            phNew = chunkAt + 8;
            var copy = rom.ReadBytes(block, copied);
            rom.WriteBytes(chunkAt, new[] { (byte)'L', (byte)'F', (byte)'2', (byte)'B', (byte)room, (byte)(room >> 8), (byte)copied, (byte)(copied >> 8) });
            rom.WriteBytes(phNew, copy);
            PointMaps(rom, 0, block, phNew);
            if (chunk != null)
            {
                rom.WriteBytes(block - 8, new byte[8 + chunk.Value.Room]);
                L2EventSpace.Release(rom, block - 8, 8 + chunk.Value.Room);
            }
        }
        // the bytes we write are the ones of the layout the lists point into (a text kept with its 0A at its own
        // address can come out shorter than in the trial, never longer)
        scripts = Lay(phNew, out var placedAt);
        if (scriptsFrom + scripts.Length > Chunk(rom, phNew)!.Value.Room) throw new InvalidOperationException("The map's events came out bigger than their room.");
        // lists
        foreach (int head in Heads)
        {
            var bytes = new List<byte>();
            foreach (var (entry, script) in items.Where(x => x.Entry.Head == head))
            {
                int offset = script != null ? placedAt[script] : entry.Offset;
                bytes.Add((byte)entry.Id); bytes.Add((byte)offset); bytes.Add((byte)(offset >> 8));
            }
            bytes.Add(0xFF);
            rom.WriteBytes(phNew + listAt[head], bytes.ToArray());
            rom.WriteBytes(phNew + head, new[] { (byte)listAt[head], (byte)(listAt[head] >> 8) });
        }
        rom.WriteBytes(phNew + scriptsFrom, scripts);
        return phNew == block
            ? $"Written in the map's added event space ({scriptsFrom + scripts.Length} of {Chunk(rom, phNew)!.Value.Room} bytes used)."
            : $"The map's events moved to {L2Rom.FileToSnes(phNew):X6} in the event space (bank {L2Rom.FileToSnes(phNew) >> 16:X2}) to make room.";
    }

    // laying out every block again (a translation changes them all)

    // a block written from scratch at at: "PH", the six lists, the key data (the +2 list's id 03: 3-byte records up
    // to 00, copied as they are), then every script once (events sharing a script share it again). changed gives new
    // scripts by entry, the others get read from the old block
    private static byte[] BuildBlock(RomBuffer rom, int oldBlock, IReadOnlyDictionary<Entry, L2EventScript> changed, int at)
    {
        var lists = ReadLists(rom, oldBlock);
        var byOffset = new Dictionary<int, L2EventScript>();
        var scriptOf = new Dictionary<Entry, L2EventScript>();
        var keyData = new Dictionary<int, byte[]>();
        foreach (var entry in lists)
        {
            if (!entry.IsScript)
            {
                if (keyData.ContainsKey(entry.Offset)) continue;
                int position = entry.Offset;
                while (rom.ReadByte(oldBlock + position) != 0 && position < entry.Offset + 0x300) position += 3;
                keyData[entry.Offset] = rom.ReadBytes(oldBlock + entry.Offset, position + 1 - entry.Offset);
                continue;
            }
            if (changed.TryGetValue(entry, out var script)) { scriptOf[entry] = script; continue; }
            if (!byOffset.TryGetValue(entry.Offset, out var old)) byOffset[entry.Offset] = old = L2EventScript.Read(rom, oldBlock + entry.Offset, oldBlock);
            if (old.Problem != null) throw new InvalidOperationException($"Event {entry.Id:X2} can't be laid out again: {old.Problem}");
            scriptOf[entry] = old;
        }
        return LayOut(rom, lists.Select(entry => (entry, entry.IsScript ? scriptOf[entry] : null, entry.IsScript ? null : keyData[entry.Offset])).ToList(), at);
    }

    // a block laid out at at from its events in list order: "PH", the six lists, the key data, then every script once
    // (the same script object, or key data array, given for several events gets shared again). the entries' offsets
    // aren't used. text is encoded for rom
    private static byte[] LayOut(RomBuffer rom, IReadOnlyList<(Entry Entry, L2EventScript? Script, byte[]? Key)> events, int at)
    {
        int position = 0x0E;
        var listAt = new Dictionary<int, int>();
        foreach (int head in Heads) { listAt[head] = position; position += events.Count(x => x.Entry.Head == head) * 3 + 1; }
        var output = new List<byte>(new byte[position]);
        var keyAt = new Dictionary<byte[], int>(ReferenceEqualityComparer.Instance);
        foreach (var (_, _, key) in events)
            if (key != null && !keyAt.ContainsKey(key)) { keyAt[key] = output.Count; output.AddRange(key); }
        var placed = new Dictionary<L2EventScript, int>();
        foreach (var (_, script, _) in events)
        {
            if (script == null || placed.ContainsKey(script)) continue;
            var (bytes, error) = Relocated(script, at).Build(rom, at + output.Count);
            if (error != null) throw new InvalidOperationException(error);
            placed[script] = output.Count;
            output.AddRange(bytes!);
        }
        if (output.Count > 0xFFFF) throw new InvalidOperationException("A map's events would take more than 64 KB.");
        output[0] = (byte)'P'; output[1] = (byte)'H';
        foreach (int head in Heads)
        {
            int listPosition = listAt[head];
            output[head] = (byte)listPosition; output[head + 1] = (byte)(listPosition >> 8);
            foreach (var (entry, script, key) in events.Where(x => x.Entry.Head == head))
            {
                int offset = script != null ? placed[script] : keyAt[key!];
                output[listPosition++] = (byte)entry.Id; output[listPosition++] = (byte)offset; output[listPosition++] = (byte)(offset >> 8);
            }
            output[listPosition] = 0xFF;
        }
        return output.ToArray();
    }

    // a block laid out from scratch from these events (scripts read from any rom, key tables as bytes) in a chunk of
    // the event space of its own, like CopyBlock makes. the map header isn't changed. returns the file offset of its
    // "PH". scripts get copied (L2EventScript.Detached) so none is taken for bytes already in this rom. events given
    // the same script object share it
    public static int WriteBlock(RomBuffer rom, IReadOnlyList<(Entry Entry, L2EventScript? Script, byte[]? Key)> events)
    {
        var copies = new Dictionary<L2EventScript, L2EventScript>();
        var list = events.Select(x => (x.Entry, x.Script == null ? null : copies.TryGetValue(x.Script, out var copy) ? copy : copies[x.Script] = x.Script.Detached(), x.Key)).ToList();
        int size = LayOut(rom, list, 0x7F8000).Length;
        int room = Math.Min(L2EventSpace.Mark - 8, size + Math.Max(0x200, size / 8));
        if (size > room) throw new InvalidOperationException($"The map's events take {size} bytes; one bank holds {room}.");
        int chunk = L2EventSpace.Allocate(rom, room + 8);
        var built = LayOut(rom, list, chunk + 8);
        rom.WriteBytes(chunk, new[] { (byte)'L', (byte)'F', (byte)'2', (byte)'B', (byte)room, (byte)(room >> 8), (byte)built.Length, (byte)(built.Length >> 8) });
        rom.WriteBytes(chunk + 8, built);
        return chunk + 8;
    }

    // points map map's header at the event block at block
    public static void PointMap(RomBuffer rom, int map, int block)
    {
        int value = HeaderValue(block);
        rom.WriteBytes(L2Events.HeaderAt(rom, map), new[] { (byte)value, (byte)(value >> 8), (byte)(value >> 16) });
    }

    // lays out the event blocks again, all at once (L2Dutch.Import: a translation changes every map). changes gives,
    // by block (file offset), the new scripts of its events. the blocks where the game has them (one after another in
    // banks $87-$8E, nothing but 00 between them) get written again into that room first, never across a bank's end.
    // what doesn't fit goes into the event space as a moved block (like Save does), and blocks already there get laid
    // out again in place (SaveAll). every block is built before anything gets written. a block that can't be laid out
    // again (a script with steps that overlap, like one of Frue Lufia's) stays where it is, untouched, and goes in
    // stuck. returns how many blocks went to the event space
    public static int RepackAll(RomBuffer rom, IReadOnlyDictionary<int, IReadOnlyDictionary<Entry, L2EventScript>> changes, List<int>? stuck = null)
    {
        var mapsOf = new Dictionary<int, List<int>>();
        for (int m = 0; m < L2Maps.Count(rom); m++)
        {
            int block = L2Events.Block(rom, m);
            if (block < 0) continue;
            if (!mapsOf.TryGetValue(block, out var list)) mapsOf[block] = list = new();
            list.Add(m);
        }
        // blocks already moved: laid out again where they are
        foreach (var (block, maps) in mapsOf.Where(kv => Chunk(rom, kv.Key) != null))
            if (changes.TryGetValue(block, out var changed) && changed.Count > 0) SaveAll(rom, maps[0], changed);
        var noChange = new Dictionary<Entry, L2EventScript>();
        // sizes (they don't depend on the address). a block that can't be built stays as it is
        var size = new Dictionary<int, int>();
        foreach (int block in mapsOf.Keys.Where(block => Chunk(rom, block) == null).OrderBy(block => block))
        {
            try { size[block] = BuildBlock(rom, block, changes.GetValueOrDefault(block) ?? noChange, 0x1F8000).Length; }
            catch (InvalidOperationException) { stuck?.Add(block); }
        }
        var home = size.Keys.OrderBy(block => block).ToList();
        if (home.Count == 0) return 0;
        // the room: the old blocks, joined where they overlap (a block's scripts can lie past the next block's start, or
        // be shared with it) or where only 00 lies between them, cut at the banks' ends. joined, no two pieces of room
        // overlap, so no two blocks get put over each other
        var spans = home.Select(block => (Start: block, End: block + Extent(rom, block))).ToList();
        var runs = new List<(int Start, int End)>();
        foreach (var span in spans)
        {
            if (runs.Count > 0 && (span.Start <= runs[^1].End || Enumerable.Range(runs[^1].End, span.Start - runs[^1].End).All(position => rom.ReadByte(position) == 0)))
                runs[^1] = (runs[^1].Start, Math.Max(runs[^1].End, span.End));
            else runs.Add(span);
        }
        var room = new List<(int Start, int End)>();
        foreach (var (start, end) in runs)
            for (int position = start; position < end; position = (position / 0x8000 + 1) * 0x8000) room.Add((position, Math.Min(end, (position / 0x8000 + 1) * 0x8000)));
        // a block that couldn't be built stays where it is, so its bytes come out of the room (a neighbour's extent
        // can reach over it, and the room gets zeroed below while the map still points at it)
        foreach (int block in mapsOf.Keys.Where(block => Chunk(rom, block) == null && !size.ContainsKey(block)))
        {
            int keepStart = block, keepEnd = block + Extent(rom, block);
            room = room.SelectMany(r => r.End <= keepStart || r.Start >= keepEnd ? new[] { r }
                       : new[] { (r.Start, Math.Min(r.End, keepStart)), (Math.Max(r.Start, keepEnd), r.End) }.Where(x => x.Item2 > x.Item1).Select(x => (Start: x.Item1, End: x.Item2)))
                       .ToList();
        }
        // places: the biggest first
        var free = room.Select(r => (r.Start, r.End)).ToList();
        var placeAt = new Dictionary<int, int>();
        foreach (int block in home.OrderByDescending(block => size[block]))
        {
            int k = free.FindIndex(f => f.End - f.Start >= size[block]);
            if (k < 0) continue;
            placeAt[block] = free[k].Start;
            free[k] = (free[k].Start + size[block], free[k].End);
        }
        // the ones that didn't fit go into the event space (with room for later edits, like Save leaves it)
        var chunks = new Dictionary<int, (int Ph, int Room)>();
        foreach (int block in home.Where(block => !placeAt.ContainsKey(block)))
        {
            int roomBytes = Math.Min(L2EventSpace.Mark - 8, size[block] + Math.Max(0x200, size[block] / 8));
            if (size[block] > roomBytes) throw new InvalidOperationException($"A map's events would take {size[block]} bytes; one bank holds {roomBytes}.");
            int chunkAt = L2EventSpace.Allocate(rom, roomBytes + 8);
            chunks[block] = (chunkAt + 8, roomBytes);
            placeAt[block] = chunkAt + 8;
        }
        // every block built at its place before anything is written over
        var built = home.ToDictionary(block => block, block => BuildBlock(rom, block, changes.GetValueOrDefault(block) ?? noChange, placeAt[block]));
        foreach (var (start, end) in room) rom.WriteBytes(start, new byte[end - start]);
        foreach (int block in home)
        {
            int blockAt = placeAt[block];
            if (chunks.TryGetValue(block, out var chunk))
                rom.WriteBytes(blockAt - 8, new[] { (byte)'L', (byte)'F', (byte)'2', (byte)'B', (byte)chunk.Room, (byte)(chunk.Room >> 8), (byte)built[block].Length, (byte)(built[block].Length >> 8) });
            rom.WriteBytes(blockAt, built[block]);
        }
        // the maps point at their blocks' new places
        for (int m = 0; m < L2Maps.Count(rom); m++)
        {
            int headerAt = L2Events.HeaderAt(rom, m);
            int old = HeaderToFile((int)rom.ReadUInt24Le(headerAt));
            if (!placeAt.TryGetValue(old, out int now) || !home.Contains(old)) continue;
            int value = HeaderValue(now);
            rom.WriteBytes(headerAt, new[] { (byte)value, (byte)(value >> 8), (byte)(value >> 16) });
        }
        return chunks.Count;
    }

    // a script read from the rom keeps its jumps to its own steps, so laid out somewhere else its jumps stay right
    private static L2EventScript Relocated(L2EventScript script, int phAt) => script.JumpBase == phAt ? script : script.WithJumpBase(phAt);

    // writes a map's start-up script: over its old bytes if it fits (and no other text repeats bytes it changes,
    // L2Events.CopiedFrom), else into the event space (its jumps are offsets from the start of its own bank, so it
    // gets written whole within one bank)
    public static string SaveInit(RomBuffer rom, int map, L2EventScript script)
    {
        NeedExtraFlags(rom, script);
        int init = InitScript(rom, map);
        if (script.Start == init && script.FitsInPlace(rom, script.ReadStarts))
        {
            var (changes, error) = script.InPlaceChanges(rom);
            if (error != null) throw new InvalidOperationException(error);
            if (changes!.Count == 0) return "Nothing changed.";
            if (L2Events.CopiedFrom(rom, changes.Select(c => (c.Step.At, c.Bytes))) < 0)
            {
                foreach (var (step, bytes) in changes) rom.WriteBytes(step.At, bytes);
                return $"Written over the old bytes ({changes.Count} step{(changes.Count == 1 ? "" : "s")}).";
            }
        }
        foreach (var step in script.Steps) step.PadTo = 0;   // (written elsewhere, nothing is filled up to an old size)
        // the size first (any bank start gives the same sizes)
        var (probe, err) = script.WithJumpBase(0x200000 - 0x8000).Build(rom, 0x200000 - 0x8000);
        if (err != null) throw new InvalidOperationException(err);
        int size = probe!.Length;
        int headerAt = init - 8;
        int scriptAt;
        if (L2EventSpace.Contains(rom, init) && headerAt >= 0 && rom.ReadByte(headerAt) == 'L' && rom.ReadByte(headerAt + 1) == 'F' && rom.ReadByte(headerAt + 2) == '2' && rom.ReadByte(headerAt + 3) == 'I'
            && rom.ReadUInt16Le(headerAt + 4) >= size)
            scriptAt = init;
        else
        {
            int room = size + 0x40;
            int chunk = L2EventSpace.Allocate(rom, room + 8);
            rom.WriteBytes(chunk, new[] { (byte)'L', (byte)'F', (byte)'2', (byte)'I', (byte)room, (byte)(room >> 8), (byte)0, (byte)0 });
            scriptAt = chunk + 8;
        }
        var (built, buildError) = script.WithJumpBase(InitBase(scriptAt)).Build(rom, scriptAt);
        if (buildError != null) throw new InvalidOperationException(buildError);
        rom.WriteBytes(scriptAt, built!);
        if (scriptAt != init)
        {
            int value = HeaderValue(scriptAt), fieldAt = L2Events.HeaderAt(rom, map) + 3;
            rom.WriteBytes(fieldAt, new[] { (byte)value, (byte)(value >> 8), (byte)(value >> 16) });
        }
        return scriptAt == init ? "Written in the event space." : $"The start-up script moved to {L2Rom.FileToSnes(scriptAt):X6} in the event space to make room.";
    }

    // writes a start-up script for map into new room in the event space and points the map's header at it (a new
    // map's script, never written over the one it was copied from)
    public static void WriteInitNew(RomBuffer rom, int map, L2EventScript script)
    {
        NeedExtraFlags(rom, script);
        foreach (var step in script.Steps) step.PadTo = 0;
        var (probe, err) = script.WithJumpBase(0x200000 - 0x8000).Build(rom, 0x200000 - 0x8000);
        if (err != null) throw new InvalidOperationException(err);
        int room = probe!.Length + 0x40;
        int chunk = L2EventSpace.Allocate(rom, room + 8);
        rom.WriteBytes(chunk, new[] { (byte)'L', (byte)'F', (byte)'2', (byte)'I', (byte)room, (byte)(room >> 8), (byte)0, (byte)0 });
        int scriptAt = chunk + 8;
        var (built, buildError) = script.WithJumpBase(InitBase(scriptAt)).Build(rom, scriptAt);
        if (buildError != null) throw new InvalidOperationException(buildError);
        rom.WriteBytes(scriptAt, built!);
        int value = HeaderValue(scriptAt);
        rom.WriteBytes(L2Events.HeaderAt(rom, map) + 3, new[] { (byte)value, (byte)(value >> 8), (byte)(value >> 16) });
    }

    // an event block with six empty lists, in a chunk of the event space with room for events to be added (like a
    // block moved there). returns the file offset of its "PH"
    public static int NewBlock(RomBuffer rom)
    {
        var block = new byte[0x14];
        block[0] = (byte)'P'; block[1] = (byte)'H';
        for (int i = 0; i < Heads.Length; i++)
        {
            block[Heads[i]] = (byte)(0x0E + i);
            block[0x0E + i] = 0xFF;
        }
        return PutBlock(rom, block, 0x400);
    }

    // a copy of the event block at from (every event and the key table, laid out again) in a chunk of its own.
    // returns the file offset of its "PH"
    public static int CopyBlock(RomBuffer rom, int from)
    {
        if (from < 0) return NewBlock(rom);
        // laid out once to learn the size (nothing is padded, so it doesn't depend on the address), then where it goes
        int size = BuildBlock(rom, from, new Dictionary<Entry, L2EventScript>(), 0x1F8000).Length;
        int room = Math.Min(L2EventSpace.Mark - 8, size + Math.Max(0x200, size / 8));
        if (size > room) throw new InvalidOperationException($"The map's events take {size} bytes; one bank holds {room}.");
        int chunk = L2EventSpace.Allocate(rom, room + 8);
        var built = BuildBlock(rom, from, new Dictionary<Entry, L2EventScript>(), chunk + 8);
        rom.WriteBytes(chunk, new[] { (byte)'L', (byte)'F', (byte)'2', (byte)'B', (byte)room, (byte)(room >> 8), (byte)built.Length, (byte)(built.Length >> 8) });
        rom.WriteBytes(chunk + 8, built);
        return chunk + 8;
    }

    private static int PutBlock(RomBuffer rom, byte[] block, int room)
    {
        int chunk = L2EventSpace.Allocate(rom, room + 8);
        rom.WriteBytes(chunk, new[] { (byte)'L', (byte)'F', (byte)'2', (byte)'B', (byte)room, (byte)(room >> 8), (byte)block.Length, (byte)(block.Length >> 8) });
        rom.WriteBytes(chunk + 8, block);
        return chunk + 8;
    }

    // new events

    // a new event: (if onceFlag >= 0) "if the flag is on, go to the end" and "turn the flag on", then the text, then
    // the end
    public static L2EventScript NewScript(int jumpBase, byte[] opener, string text, int onceFlag)
    {
        var script = L2EventScript.New(jumpBase);
        var end = script.Steps[0];
        var say = new L2Step { Bytes = opener, IsText = true, Text = text, End = 0x01, OriginalEnd = 0xFF, OriginalText = "\u0001" };
        script.Steps.Insert(0, say);
        if (onceFlag >= 0)
        {
            var check = new L2Step { Bytes = L2ExtraFlags.Step(L2ExtraFlags.Use.IfOn, onceFlag) };
            check.Jumps.Add(new L2Jump { Step = end });
            script.Steps.Insert(0, check);
            script.Steps.Insert(1, new L2Step { Bytes = L2ExtraFlags.Step(L2ExtraFlags.Use.On, onceFlag) });
        }
        return script;
    }

    // the trigger-area ids of a map in use (map info list +0C and the event block's +8 list)
    public static int NextTriggerId(RomBuffer rom, int map)
    {
        var used = L2MapInfo.Records(rom, map, L2MapInfo.Triggers, 5).Select(r => (int)r.Bytes[0]).ToHashSet();
        int block = L2Events.Block(rom, map);
        if (block >= 0) used.UnionWith(ReadLists(rom, block).Where(e => e.Head == TriggerList).Select(e => e.Id));
        int id = 1;
        while (used.Contains(id)) id++;
        return id;
    }

    // a new event run by stepping onto tiles (x1, y1)-(x2, y2) (inclusive): a record in the map info's trigger list
    // and the script in the +8 list (both or neither, L2Rom.AllOrNothing). returns the id
    public static int AddTrigger(RomBuffer rom, int map, int x1, int y1, int x2, int y2, L2EventScript script) =>
        L2Rom.AllOrNothing(rom, () => AddTriggerCore(rom, map, x1, y1, x2, y2, script));

    private static int AddTriggerCore(RomBuffer rom, int map, int x1, int y1, int x2, int y2, L2EventScript script)
    {
        int id = NextTriggerId(rom, map);
        if (id > 0xFE) throw new InvalidOperationException("The map has no free trigger number.");
        if (!L2MapInfo.AddRecord(rom, map, L2MapInfo.Triggers, 5, new[] { (byte)id, (byte)x1, (byte)y1, (byte)(x2 + 1), (byte)(y2 + 1) }))
            throw new InvalidOperationException("The map's info block would be too big.");
        Add(rom, map, TriggerList, id, script);
        return id;
    }

    // the map characters' actor ids in use (character starts, the start-up script's 68/7B, the talk list)
    public static HashSet<int> CharacterIds(RomBuffer rom, int map)
    {
        var used = L2MapInfo.Records(rom, map, L2MapInfo.Characters, 8).Select(r => 0x4F + r.Bytes[0]).ToHashSet();
        int init = InitScript(rom, map);
        foreach (var step in L2EventScript.Read(rom, init, InitBase(init)).Steps)
            if (step.Op is 0x68 or 0x7B) used.Add(step.Bytes[1]);
        int block = L2Events.Block(rom, map);
        if (block >= 0) used.UnionWith(ReadLists(rom, block).Where(e => e.Head == TalkList && e.Id >= 0x50).Select(e => e.Id));
        return used;
    }

    // when the map's start-up script makes character id: always (flag -1), or only while story flag flag is on (on)
    // or off. its create (68 / 7B) gets taken out of where it was and put at the top of the script with its own check
    // (the game's maps do the same kind of check, Elcid's start-up script makes its people under "Check whether story
    // flag 0A is off: if not, go to" the end). a character made in more than one place (with other sprites under other
    // flags) is refused, edit the start-up script for that
    public static string SetCreateCondition(RomBuffer rom, int map, int id, int flag, bool on)
    {
        int init = InitScript(rom, map);
        var script = L2EventScript.Read(rom, init, InitBase(init));
        if (script.Problem != null) throw new InvalidOperationException("The map's start-up script can't be changed: " + script.Problem);
        var steps = script.Steps;
        var creates = steps.Where(step => !step.IsText && step.Op is 0x68 or 0x7B && step.Bytes.Length >= 3 && step.Bytes[1] == id).ToList();
        if (creates.Count == 0) throw new InvalidOperationException($"The start-up script doesn't make character {id:X2}.");
        if (creates.Count > 1) throw new InvalidOperationException($"The start-up script makes character {id:X2} in {creates.Count} places (other sprites under other story flags): change those steps in the start-up script itself.");
        var create = creates[0];
        int index = steps.IndexOf(create);
        var next = index + 1 < steps.Count ? steps[index + 1] : null;
        // what jumped to the create goes on with the step after it
        foreach (var step in steps) foreach (var j in step.Jumps) if (j.Step == create) j.Step = next;
        steps.RemoveAt(index);
        // at the top of the script (where "Add a character" puts creates): no check or jump of the script reaches
        // there, so the create depends on its own check only (put before the script's end, it sat inside the last
        // check's skip)
        if (steps.Count == 0) throw new InvalidOperationException("The start-up script has no end.");
        var made = new L2Step { Bytes = (byte[])create.Bytes.Clone() };
        var first = steps[0];
        steps.Insert(0, made);
        if (flag >= 0)
        {
            var (code, value) = L2ExtraFlags.CheckBytes(on ? 0x00 : 0x01, flag);
            var check = new L2Step { Bytes = new byte[] { 0x14, (byte)code, (byte)value, 0x30, 0x00, 0x00, 0xFF } };
            check.Jumps.Add(new L2Jump { Step = first });
            steps.Insert(0, check);
        }
        return SaveInit(rom, map, script);
    }

    // adds a character to a map: a character start (where it stands, the area it can walk in, how it moves) and
    // "Add a map character" at the top of the start-up script (both or neither). returns its actor id
    public static int AddCharacter(RomBuffer rom, int map, int x, int y, int sprite, int mode) =>
        L2Rom.AllOrNothing(rom, () => AddCharacterCore(rom, map, x, y, sprite, mode));

    private static int AddCharacterCore(RomBuffer rom, int map, int x, int y, int sprite, int mode)
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
