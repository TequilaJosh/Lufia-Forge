using LufiaForge.Modules.TextEditor;

namespace LufiaForge.Core.Maps;

// where a jump goes: another command of the same event, or a fixed place outside it
public sealed class JumpRef
{
    // target command inside this event (moves along when the event gets rewritten)
    public ScriptOp? Op { get; set; }
    // absolute file offset when the target is outside the decoded event
    public int Absolute { get; set; }
}

// one command or text segment of an event script
public sealed class ScriptOp
{
    // file offset in the rom (-1 for commands added in the editor)
    public int Offset { get; init; } = -1;
    // command bytes. for text it's only the opener (and its operand). jump operand bytes get filled in when saving
    public byte[] Bytes { get; set; } = Array.Empty<byte>();
    public byte[] OriginalBytes { get; init; } = Array.Empty<byte>();
    public bool IsText { get; init; }
    // one entry per jump operand (see EventScript.JumpPositions)
    public List<JumpRef> Targets { get; } = new();

    // text segments
    // editable text (see EventScript.DecodeText for the tag syntax)
    public string Text { get; set; } = "";
    public string OriginalText { get; init; } = "";
    // 0x04 (or 01-03) = back to script commands, 0x00 (or 08, 0E) = end of script
    public byte Terminator { get; init; } = 0x04;
    // raw length of opener + text + terminator in the rom
    public int RawLength { get; init; }
    // original bytes of opener + text + terminator (reused when nothing in the box changed)
    public byte[] Raw { get; init; } = Array.Empty<byte>();

    public bool IsNew => Offset < 0;
    public bool TextChanged => IsText && Text.Replace("\r", "") != OriginalText;
    public bool BytesChanged => IsNew || !Bytes.AsSpan().SequenceEqual(OriginalBytes);
    // size in the rom as decoded (opener + text + terminator for dialogue)
    public int Length => IsText ? RawLength : Bytes.Length;

    // true when the script never falls through to the next byte (end, goto, first-time branch, text that ends the script)
    public bool EndsFlow => IsText ? Terminator is 0x00 or 0x08 or 0x0E : Bytes.Length > 0 && Bytes[0] is 0x00 or 0x01 or 0x02 or 0x51;
}

// the editable state of every line of a script at one moment (for undo/redo)
public sealed class ScriptSnapshot
{
    internal List<(ScriptOp Op, byte[] Bytes, string Text, (ScriptOp? Op, int Absolute)[] Targets)> Items { get; } = new();

    // same lines, order, bytes, text and jump targets
    public bool SameAs(ScriptSnapshot other) =>
        Items.Count == other.Items.Count && Items.Zip(other.Items).All(p =>
            ReferenceEquals(p.First.Op, p.Second.Op) && p.First.Bytes.AsSpan().SequenceEqual(p.Second.Bytes) && p.First.Text == p.Second.Text &&
            p.First.Targets.Length == p.Second.Targets.Length &&
            p.First.Targets.Zip(p.Second.Targets).All(t => ReferenceEquals(t.First.Op, t.Second.Op) && t.First.Absolute == t.Second.Absolute));
}

// an event script (what runs when the party talks to a character or steps on a trigger), decoded into editable
// commands and dialogue. command lengths come from the handlers at $01:C78C (00-6D) and the range table at $01:C868
// (80-FF), checked against playthrough traces. jump operands are offsets from the event's own start (the engine sets
// its base, $0D0F, to the event start). see research-script-engine.md.
// saving: if nothing changes size the event is rewritten in place. otherwise the map's whole script block gets copied
// into the expanded rom area (so every other event and every jump into the old code keeps working), the rewritten
// event goes on the end of the copy, and the map's script table entry (0x18200 + map*5) points at the copy
public sealed class EventScript
{
    // records the editable state of every line
    public ScriptSnapshot TakeSnapshot()
    {
        var snapshot = new ScriptSnapshot();
        foreach (var op in Ops)
            snapshot.Items.Add((op, (byte[])op.Bytes.Clone(), op.Text, op.Targets.Select(t => (t.Op, t.Absolute)).ToArray()));
        return snapshot;
    }

    // puts every line back the way a snapshot recorded it (lines added since are dropped, removed ones come back)
    public void Restore(ScriptSnapshot snapshot)
    {
        Ops.Clear();
        foreach (var (op, bytes, text, targets) in snapshot.Items)
        {
            op.Bytes = (byte[])bytes.Clone();
            op.Text = text;
            op.Targets.Clear();
            foreach (var (targetOp, absolute) in targets) op.Targets.Add(new JumpRef { Op = targetOp, Absolute = absolute });
            Ops.Add(op);
        }
    }

    public int MapId { get; }
    public int Event { get; }
    public int BlockBase { get; }
    public int Start { get; }
    // where jump offsets count from: the start of the running event. same as Start for a whole event, different
    // when viewing code that an event jumps to (LoadAt)
    public int JumpBase { get; private set; }
    // true when this is a view of code inside another event's reach, not an event of its own
    public bool IsCodeView => JumpBase != Start;
    // end of the decoded region (exclusive)
    public int End { get; private set; }
    public List<ScriptOp> Ops { get; } = new();
    // set when decoding stopped at an unknown command, the rest is kept as it is when saving
    public string? StopReason { get; private set; }

    private readonly RomBuffer _rom;
    public RomBuffer Rom => _rom;
    private readonly List<ScriptOp> _original = new();

    private EventScript(RomBuffer rom, int mapId, int ev, int blockBase, int start)
    {
        _rom = rom; MapId = mapId; Event = ev; BlockBase = blockBase; Start = start; JumpBase = start;
    }

    public static int BlockOffset(RomBuffer rom, int mapId)
    {
        int entry = LufiaMap.MapScriptTable + mapId * 5;
        return 0x18000 + (rom.ReadByte(entry) | (rom.ReadByte(entry + 1) << 8) | (rom.ReadByte(entry + 2) << 16));
    }

    // decodes event ev of a map, or null if the event has no script
    public static EventScript? Load(RomBuffer rom, int mapId, int ev)
    {
        int start = LufiaMap.EventScriptOffset(rom, mapId, ev);
        if (start < 0 || start >= rom.Length) return null;
        var script = new EventScript(rom, mapId, ev, BlockOffset(rom, mapId), start);
        script.Decode();
        return script;
    }

    // decodes the code at start that event ev (whose script starts at jumpBase) jumps to.
    // edits that keep every line's size can be saved in place
    public static EventScript LoadAt(RomBuffer rom, int mapId, int ev, int start, int jumpBase)
    {
        var script = new EventScript(rom, mapId, ev, BlockOffset(rom, mapId), start) { JumpBase = jumpBase };
        script.Decode();
        return script;
    }

    // decodes an event together with the code its jumps lead to inside the map's script block (not other events'
    // starts), as one script. the intro for one is a single jump to its real code. saving lays every part out again,
    // so lines can be added or removed anywhere, and same-size edits still get written in place
    public static EventScript? LoadWhole(RomBuffer rom, int mapId, int ev)
    {
        var script = Load(rom, mapId, ev);
        if (script == null || script.StopReason != null) return script;
        var whole = new EventScript(rom, mapId, ev, script.BlockBase, script.Start) { MergesJumps = true };
        whole.Decode();
        return whole.StopReason == null ? whole : script;
    }

    // loads the same event or code again (after saving or reverting)
    public static EventScript? Reload(RomBuffer rom, EventScript script) =>
        script.IsCodeView ? LoadAt(rom, script.MapId, script.Event, script.Start, script.JumpBase)
        : script.MergesJumps ? LoadWhole(rom, script.MapId, script.Event)
        : Load(rom, script.MapId, script.Event);

    // true when the code the event jumps to got decoded into this script too (see LoadWhole)
    public bool MergesJumps { get; private init; }

    // the rom ranges the decoded lines came from (just one unless jumps were followed)
    public List<(int From, int To)> Regions { get; } = new();

    private Dictionary<int, int>? _eventStarts;

    // which event of this map starts at a rom offset, or -1
    public int EventStartingAt(int offset)
    {
        if (_eventStarts == null)
        {
            _eventStarts = new();
            for (int ev = 0; ev < 256; ev++)
            {
                int eventStart = LufiaMap.EventScriptOffset(_rom, MapId, ev);
                if (eventStart >= 0) _eventStarts.TryAdd(eventStart, ev);
            }
        }
        return _eventStarts.TryGetValue(offset, out int found) ? found : -1;
    }

    // where each script block ends, for one rom, kept as a single object: the Cutscenes tab reads it on another
    // thread, and two fields set one after the other could pair one rom with another rom's ends
    private sealed record BlockEnds(RomBuffer Rom, Dictionary<int, int> Ends);
    private static BlockEnds? _blockEnds;

    // event numbers a map can really use: characters (0..count-1) and trigger/spot events (event base + area),
    // and only table entries that point inside the map's own script block. entries past that are other data
    // and decode as garbage
    public static List<int> PlausibleEvents(RomBuffer rom, int mapId)
    {
        var ends = _blockEnds;
        if (ends == null || !ReferenceEquals(ends.Rom, rom))
        {
            var bases = MapCatalog.Scan(rom).Select(m => BlockOffset(rom, m.MapId)).Distinct().OrderBy(b => b).ToList();
            var endOfBlock = new Dictionary<int, int>();
            for (int i = 0; i < bases.Count; i++) endOfBlock[bases[i]] = i + 1 < bases.Count ? bases[i + 1] : bases[i] + 0x8000;
            _blockEnds = ends = new BlockEnds(rom, endOfBlock);
        }
        int block = BlockOffset(rom, mapId);
        int end = ends.Ends.TryGetValue(block, out int knownEnd) ? knownEnd : block + 0x8000;
        // a block moved above 1 MB has its exact length in the space table
        if (block >= MapWriter.ExpansionStart && ExpansionSpace.Load(rom).At(block) is { } moved && moved.Start == block) end = moved.End;
        int npcs = 0, areas = 0;
        try { var map = LufiaMap.Load(rom, mapId); npcs = map.Npcs.Count; areas = Math.Max(map.SectionCounts[3], map.SectionCounts[4]); } catch { }
        int highestEvent = Math.Max(npcs - 1, LufiaMap.EventBase(rom, mapId) + areas + 1);
        // a block written in the added space (a new map's, or a cutscene's) can use any event number, its table runs
        // up to where the first event's code starts
        // (a map with no block has entry FFFFFF, which points past the rom)
        if (block >= MapWriter.ExpansionStart && block < rom.Length && end > block)
        {
            int tableEnd = Math.Min(end, rom.Length) - block;
            for (int ev = 0; ev * 2 + 2 <= tableEnd && ev <= 255; ev++)
            {
                int relative = rom.ReadUInt16Le(block + ev * 2);
                if (relative != 0xFFFF && relative >= ev * 2 + 2) tableEnd = Math.Min(tableEnd, relative);
                highestEvent = Math.Max(highestEvent, ev);
            }
        }
        var list = new List<int>();
        var texts = new List<(int From, int To, int Ev)>();
        for (int ev = 0; ev <= Math.Min(highestEvent, 255); ev++)
        {
            int eventStart = LufiaMap.EventScriptOffset(rom, mapId, ev);
            if (eventStart < block || eventStart >= end) continue;
            // table entries that point into dialogue decode into commands 6E-7F, which have no handler, so not events
            var probe = new EventScript(rom, mapId, ev, block, eventStart);
            probe.Decode();
            if (probe.StopReason?.StartsWith("unknown command") == true) continue;
            // jumps are relative to the event, real ones stay inside the map's script block (or the expanded area)
            if (probe.Ops.Any(op => op.Targets.Any(t => t.Absolute < block || t.Absolute >= end))) continue;
            list.Add(ev);
            foreach (var op in probe.Ops.Where(op => op.IsText)) texts.Add((op.Offset, op.Offset + op.RawLength, ev));
        }
        // entries that point into another event's dialogue aren't events either (text always ends before a real start),
        // and neither are entries right after a text character (the middle of a box nobody decodes) unless a
        // character or area uses them
        HashSet<int> used = new();
        try
        {
            used.UnionWith(LufiaMap.Load(rom, mapId).Npcs.Where(n => !n.IsUnused).Select(n => MapEvents.TalkEvent(n.Index)));
            used.UnionWith(MapSetupScript.ReadTriggers(rom, mapId).Select(t => t.Event));
        }
        catch { }
        // when a start sits inside another candidate's "text" and starts right after an end byte (00) while that other
        // candidate doesn't, the other one is the fake (like an entry pointing into the pointer table, decoded as text)
        var fakes = new HashSet<int>();
        foreach (int ev in list)
        {
            int eventStart = LufiaMap.EventScriptOffset(rom, mapId, ev);
            if (rom.ReadByte(eventStart - 1) != 0x00) continue;
            foreach (var text in texts.Where(t => t.Ev != ev && t.From < eventStart && eventStart < t.To))
                if (rom.ReadByte(LufiaMap.EventScriptOffset(rom, mapId, text.Ev) - 1) != 0x00) fakes.Add(text.Ev);
        }
        list.RemoveAll(fakes.Contains);
        texts.RemoveAll(t => fakes.Contains(t.Ev));
        list.RemoveAll(ev =>
        {
            int eventStart = LufiaMap.EventScriptOffset(rom, mapId, ev);
            if (texts.Any(t => t.Ev != ev && t.From < eventStart && eventStart < t.To)) return true;
            int previousByte = rom.ReadByte(eventStart - 1);
            return previousByte is >= 0x20 and < 0x7F && !used.Contains(ev);
        });
        return list;
    }

    // plain description of a jump target outside the decoded lines
    public string DescribeOutside(int abs)
    {
        int ev = EventStartingAt(abs);
        return ev >= 0 ? $"event {ev}'s start (0x{abs:X6})" : $"code at 0x{abs:X6}";
    }

    // optional names for story flags (the app sets it from the user's flag names)
    public static Func<int, string?>? FlagName;

    private static string Flag(int flag) => FlagName?.Invoke(flag) is { Length: > 0 } name ? $"{flag:X2} \"{name}\"" : $"{flag:X2}";

    // command table

    // length of a command from its opcode (and for 03, its count byte). 0 = unknown,
    // negative = a text opener of that many bytes, then text
    public static int CommandLength(int op, int next = 0) => op switch
    {
        0x00 => 1,
        0x01 => 3,
        0x02 => 6,
        0x03 => 2 + 2 * next,
        0x04 or 0x05 => 4,
        0x06 or 0x07 => 2,
        0x08 => 1,                    // re-run the map setup (the handler's reads come from that script)
        0x09 => 5,
        0x0A or 0x0B => 2,
        0x0C or 0x0D => -1,
        0x0E or 0x0F => -2,
        >= 0x10 and <= 0x15 => 3,
        >= 0x16 and <= 0x19 => 2,
        0x1A => 3,
        0x1B or 0x1C => 2,
        0x1D => 3,
        0x1E => 1,                    // church menu / shared script call
        0x1F => 2,
        >= 0x20 and <= 0x27 => 4,
        >= 0x28 and <= 0x2F => 2,
        >= 0x30 and <= 0x37 => -2,
        >= 0x38 and <= 0x3A => 2,
        0x3B or 0x3C => 1,
        >= 0x3D and <= 0x41 => 3,
        >= 0x42 and <= 0x44 => 2,
        >= 0x45 and <= 0x47 => 5,
        0x48 or 0x49 => 6,
        0x4A => 1,
        0x4B => 6,
        0x4C => 3,
        0x4D or 0x4E => 2,
        0x4F => 4,
        0x50 => 2,
        0x51 => 3,                    // run event ee of map mm (never returns)
        >= 0x52 and <= 0x54 => 2,
        0x55 => 6,
        0x56 => 1,
        0x57 => 2,
        0x58 or 0x59 => 3,
        0x5A => 1,
        >= 0x5B and <= 0x5D => 3,
        >= 0x5E and <= 0x60 => 2,
        0x61 => 1,
        0x62 or 0x63 => 6,
        >= 0x64 and <= 0x66 => 2,
        0x67 => 1,
        0x68 => 4,
        0x69 => 2,
        0x6A => 1,
        0x6B => 3,
        0x6C or 0x6D => 2,
        0x6E => next <= 7 ? 7 : 0,      // rename a party member (Lufia Forge's command, see RenamePatch)
        0x6F => next is >= 1 and <= 0x3F ? 4 : 0,   // emote bubble (Lufia Forge's command, see EmotePatch)
        >= 0x80 and <= 0x87 => 1,
        >= 0x88 and <= 0xAF => -1,
        >= 0xB0 and <= 0xBF => 1,
        >= 0xC0 and <= 0xDF => 3,
        >= 0xE0 => 1,
        _ => 0,
    };

    // byte positions of the 16-bit jump operands (offsets from the event start) in a command
    public static IReadOnlyList<int> JumpPositions(byte[] bytes)
    {
        if (bytes.Length == 0) return Array.Empty<int>();
        return bytes[0] switch
        {
            0x01 => new[] { 1 },
            0x02 => new[] { 2, 4 },
            0x03 when bytes.Length >= 2 => Enumerable.Range(0, bytes[1]).Select(i => 2 + 2 * i).ToArray(),
            0x04 or 0x05 => new[] { 2 },
            0x45 or 0x46 or 0x47 => new[] { 3 },
            0x48 or 0x49 => new[] { 4 },
            0x4F => new[] { 2 },
            >= 0xC0 and <= 0xDF => new[] { 1 },
            _ => Array.Empty<int>(),
        };
    }

    public static bool IsTextOpener(int op) => CommandLength(op) < 0;

    // who's speaking for a text opener
    public static string SpeakerName(byte[] opener) => opener.Length == 0 ? "Text" : opener[0] switch
    {
        0x0C => "Narration",
        0x0D => "Text box",
        >= 0x88 and <= 0xAF => ActorSays(opener[0] <= 0x8F ? opener[0] - 0x88 : opener[0] - 0x90 + 8),
        0x0E or 0x31 or 0x33 or 0x35 or 0x37 when opener.Length > 1 => ActorSays(opener[1]),
        0x0F or 0x30 or 0x32 or 0x34 or 0x36 when opener.Length > 1 => ActorSays(opener[1] + 7),
        _ => $"Text ({string.Join(" ", opener.Select(x => x.ToString("X2")))})",
    };

    // short name of a jump's target line, so the jump reads the same wherever that line moves to
    private string TargetName(ScriptOp target)
    {
        if (target.IsText)
        {
            var text = target.Text.Replace("\n", " ").Trim();
            return "\"" + (text.Length > 28 ? text[..28].TrimEnd() + "…" : text) + "\"";
        }
        return target.Bytes.Length > 0 && EventCommands.Find(target.Bytes[0]) is { } def ? def.Name : $"command {(target.Bytes.Length > 0 ? target.Bytes[0] : 0):X2}";
    }

    private static string ActorSays(int actor)
    {
        var who = EventCommands.Actor(actor);
        return (who.Length > 0 ? char.ToUpper(who[0]) + who[1..] : who) + " says";
    }

    // plain description of a command, jump targets show as line numbers when we know them
    public string Describe(ScriptOp op)
    {
        if (op.IsText) return $"{SpeakerName(op.Bytes)}  ({Hex(op.Bytes)})";
        var bytes = op.Bytes;
        if (bytes.Length == 0) return "";
        string JumpTarget(int i)
        {
            if (i >= op.Targets.Count) return "?";
            var target = op.Targets[i];
            int line = target.Op != null ? Ops.IndexOf(target.Op) : -1;
            return line >= 0 ? $"line {line + 1} ({TargetName(target.Op!)})" : DescribeOutside(target.Absolute);
        }
        var def = EventCommands.Find(bytes[0]);
        if (def == null || bytes.Length < CommandLength(bytes[0], bytes.Length > 1 ? bytes[1] : 0)) return $"Command {bytes[0]:X2} (unknown)";
        var words = new EventCommands.Words
        {
            Item = id => MapSetupScript.ItemName(_rom, id),
            Spell = id => EventCommands.SpellName(_rom, id),
            Flag = Flag,
            Jump = JumpTarget,
            Map = m => MapLabel?.Invoke(m) ?? $"map {m:X2}",
        };
        try { return def.Describe(bytes, words); }
        catch (Exception) { return def.Name; }
    }

    // optional map names for descriptions (the app sets it)
    public static Func<int, string>? MapLabel { get; set; }

    private static string Hex(byte[] bytes) => string.Join(" ", bytes.Select(x => x.ToString("X2")));

    // decoding

    private void Decode()
    {
        var targets = new SortedSet<int>();
        DecodeRegion(Start, targets);
        End = Regions[0].To;
        if (MergesJumps)
        {
            int blockEnd = BlockBase + Math.Max(BlockLength(), End - BlockBase);
            for (int round = 0; round < 16 && StopReason == null; round++)
            {
                var known = Ops.Select(o => o.Offset).ToHashSet();
                int next = targets.FirstOrDefault(t => !known.Contains(t) && t >= BlockBase && t < blockEnd &&
                                                       !Regions.Any(r => t >= r.From && t < r.To) && EventStartingAt(t) < 0, -1);
                if (next < 0) break;
                DecodeRegion(next, targets);
            }
        }

        // point jumps at the commands they land on
        var byOffset = Ops.ToDictionary(o => o.Offset);
        foreach (var op in Ops)
            foreach (var jump in op.Targets)
                if (byOffset.TryGetValue(jump.Absolute, out var target)) jump.Op = target;
        _original.AddRange(Ops);
        _regionBytes = Regions.Select(r => _rom.ReadBytes(r.From, Math.Min(r.To, _rom.Length) - r.From)).ToList();
        _readVersion = _rom.Version;
    }

    private List<byte[]> _regionBytes = new();
    private int _readVersion;

    // true while the rom still holds what this script was decoded from: the map's block and the event's start where
    // they were, and the same bytes in every decoded region. false once something else wrote them (another tab, Undo,
    // a patch), because saving then would put old lines back, or write at offsets the map doesn't use anymore
    public bool MatchesRom()
    {
        if (_rom.Version == _readVersion) return true;
        if (BlockOffset(_rom, MapId) != BlockBase || LufiaMap.EventScriptOffset(_rom, MapId, Event) != JumpBase) return false;
        for (int i = 0; i < Regions.Count; i++)
        {
            var (from, to) = Regions[i];
            if (Math.Min(to, _rom.Length) - from != _regionBytes[i].Length ||
                !_rom.ReadBytes(from, _regionBytes[i].Length).AsSpan().SequenceEqual(_regionBytes[i])) return false;
        }
        _readVersion = _rom.Version;   // (other bytes changed, this event is still as it was read)
        return true;
    }

    private void DecodeRegion(int from, SortedSet<int> targets)
    {
        int pos = from;
        for (int guard = 0; guard < 4000 && pos < _rom.Length; guard++)
        {
            int op = _rom.ReadByte(pos);
            int len = CommandLength(op, pos + 1 < _rom.Length ? _rom.ReadByte(pos + 1) : 0);
            if (op == Battle.RenamePatch.Op && !Battle.RenamePatch.IsCommandAt(_rom, pos)) len = 0;   // (only with the patch, 'n' in text is 6E too)
            if (op == Battle.EmotePatch.Op && !Battle.EmotePatch.IsCommandAt(_rom, pos)) len = 0;     // ('o' is 6F)
            if (len == 0) { StopReason = $"unknown command {op:X2} at 0x{pos:X6}"; break; }

            ScriptOp scriptOp;
            if (len < 0)
            {
                int openerLen = -len;
                var opener = _rom.ReadBytes(pos, openerLen);
                var (text, raw, terminator) = DecodeText(_rom, pos + openerLen);
                scriptOp = new ScriptOp
                {
                    Offset = pos, Bytes = opener, OriginalBytes = (byte[])opener.Clone(), IsText = true,
                    Text = text, OriginalText = text, Terminator = terminator, RawLength = openerLen + raw,
                    Raw = _rom.ReadBytes(pos, openerLen + raw),
                };
            }
            else
            {
                var bytes = _rom.ReadBytes(pos, len);
                scriptOp = new ScriptOp { Offset = pos, Bytes = bytes, OriginalBytes = (byte[])bytes.Clone() };
                foreach (int j in JumpPositions(bytes))
                {
                    int abs = JumpBase + (bytes[j] | (bytes[j + 1] << 8));
                    targets.Add(abs);
                    scriptOp.Targets.Add(new JumpRef { Absolute = abs });
                }
            }
            Ops.Add(scriptOp);
            pos += scriptOp.Length;

            // only keep decoding past an end when a jump in this event lands right here, or a little further on
            // with whole commands in between (lines after a goto that the editor laid out, jumps skip over them)
            if (scriptOp.EndsFlow && !targets.Contains(pos) && !ContinuesTo(pos, targets)) break;
        }
        Regions.Add((from, pos));
    }

    // decode past an end to a nearby jump target (see ContinuesTo). only turned off for comparisons in tests
    public static bool DecodePastEnds { get; set; } = true;

    // true when a pending jump target lies shortly after pos, the bytes up to it decode as whole commands landing
    // exactly on it, and pos isn't another event's start
    private bool ContinuesTo(int pos, SortedSet<int> targets)
    {
        if (!DecodePastEnds) return false;
        int target = targets.FirstOrDefault(x => x > pos, -1);
        if (target < 0 || target - pos > 0x800 || EventStartingAt(pos) >= 0) return false;
        int scan = pos;
        for (int guard = 0; guard < 2000 && scan < target; guard++)
        {
            int op = _rom.ReadByte(scan);
            int len = CommandLength(op, scan + 1 < _rom.Length ? _rom.ReadByte(scan + 1) : 0);
            if (op == Battle.RenamePatch.Op && !Battle.RenamePatch.IsCommandAt(_rom, scan)) len = 0;
            if (op == Battle.EmotePatch.Op && !Battle.EmotePatch.IsCommandAt(_rom, scan)) len = 0;
            if (len == 0) return false;
            if (len < 0) { var (_, raw, _) = DecodeText(_rom, scan - len); scan += -len + raw; }
            else scan += len;
        }
        return scan == target;
    }

    // decodes dialogue text up to its terminator (04 = back to commands, 00 = end of script).
    // returns editable text with these tags: \n new line, [Hero]..[Artea] names, [ITEM:xx], [SPELL:xx],
    // [TOWN:xx], [CHAR:xx], and [xx] / [xx:yy] for other control codes
    public static (string Text, int RawLength, byte Terminator) DecodeText(RomBuffer rom, int start)
    {
        var decoded = TextDecoder.Decode(rom, start, expandMte: true, stopAtPageBreak: true);
        var sb = new System.Text.StringBuilder();
        foreach (var token in decoded.Tokens)
        {
            byte code = rom.ReadByte(token.RomOffset);
            switch (code)
            {
                // 04 (and 01-03, which close the box in other styles) go back to commands, 00, 08 and 0E end the script
                case 0x00: case 0x04:
                case 0x01: case 0x02: case 0x03:
                case 0x08: case 0x0E:
                    return (sb.ToString(), token.RomOffset + 1 - start, code);
                case 0x05: sb.Append('\n'); break;
                case 0x07:
                {
                    byte id = rom.ReadByte(token.RomOffset + 1);
                    sb.Append(id < NameTags.Length ? $"[{NameTags[id]}]" : $"[CHAR:{id:X2}]");
                    break;
                }
                case 0x09: sb.Append($"[ITEM:{rom.ReadByte(token.RomOffset + 1):X2}]"); break;
                case 0x0A: sb.Append($"[SPELL:{rom.ReadByte(token.RomOffset + 1):X2}]"); break;
                case 0x0B: sb.Append($"[TOWN:{rom.ReadByte(token.RomOffset + 1):X2}]"); break;
                case 0x0C: case 0x0D:
                case >= 0x10:
                    sb.Append(token.Display); break;
                default:
                    sb.Append(token.ByteLength == 2 ? $"[{code:X2}:{rom.ReadByte(token.RomOffset + 1):X2}]" : $"[{code:X2}]");
                    break;
            }
        }
        // no terminator found within the decoder's window
        return (sb.ToString(), decoded.BytesConsumed, 0x00);
    }

    public static readonly string[] NameTags = { "Hero", "Lufia", "Aguro", "Jerin", "Maxim", "Selan", "Guy", "Artea" };

    // a tag in typed text that encodes a byte that ends the text (00-04, 08, 0E: DecodeText stops there and the game
    // goes back to commands), so the rest would run as commands. or null
    public static string? EndingTag(string text)
    {
        foreach (System.Text.RegularExpressions.Match match in System.Text.RegularExpressions.Regex.Matches(text, @"\[([^\]\[]+)\]"))
        {
            string tag = match.Groups[1].Value.Trim().ToUpperInvariant();
            if (tag == "PAGE") return match.Value;
            string code = tag.Split(':')[0];
            if (code.Length is 1 or 2 && byte.TryParse(code, System.Globalization.NumberStyles.HexNumber, null, out byte value) && value is <= 0x04 or 0x08 or 0x0E)
                return match.Value;
        }
        return null;
    }

    // encodes edited text plus its terminator
    public byte[] EncodeText(string text, byte terminator)
    {
        var bytes = TextDecoder.Encode(text.Replace("\r", ""), _rom).ToList();
        if (bytes.Count > 0 && bytes[^1] == 0x00) bytes.RemoveAt(bytes.Count - 1);
        bytes.Add(terminator);
        return bytes.ToArray();
    }

    // editing

    public bool IsModified =>
        Ops.Count != _original.Count || Ops.Where((o, i) => !ReferenceEquals(o, _original[i])).Any() ||
        Ops.Any(o => o.TextChanged || o.BytesChanged || TargetsChanged(o));

    private static bool TargetsChanged(ScriptOp op) =>
        !op.IsNew && op.Targets.Any(t => t.Op != null ? t.Op.Offset != t.Absolute : false);

    // a new command (its jumps start out pointing at the next line)
    public ScriptOp NewCommand(byte[] bytes, int insertAt)
    {
        var op = new ScriptOp { Bytes = bytes };
        Ops.Insert(insertAt, op);
        foreach (var _ in JumpPositions(bytes))
            op.Targets.Add(new JumpRef { Op = insertAt + 1 < Ops.Count ? Ops[insertAt + 1] : null, Absolute = End });
        return op;
    }

    // a new dialogue box
    public ScriptOp NewText(byte opener, string text, int insertAt)
    {
        var op = new ScriptOp { Bytes = new[] { opener }, IsText = true, Text = text, Terminator = 0x04 };
        Ops.Insert(insertAt, op);
        return op;
    }

    // replaces a command's bytes. jump targets are kept where we can, new jump operands point at the next line.
    // returns an error message, or null if the bytes are a valid command
    public string? SetCommandBytes(ScriptOp op, byte[] bytes)
    {
        if (op.IsText) return "This line is a dialogue box.";
        if (bytes.Length == 0) return "Enter at least the command byte.";
        int len = CommandLength(bytes[0], bytes.Length > 1 ? bytes[1] : 0);
        if (len == 0) return $"Command {bytes[0]:X2} isn't known well enough to edit safely.";
        if (len < 0) return $"{bytes[0]:X2} starts a dialogue box; insert a text line instead.";
        if (bytes.Length != len) return $"Command {bytes[0]:X2} is {len} byte{(len == 1 ? "" : "s")} long.";
        var jumps = JumpPositions(bytes);
        int index = Ops.IndexOf(op);
        while (op.Targets.Count < jumps.Count)
            op.Targets.Add(new JumpRef { Op = index + 1 < Ops.Count ? Ops[index + 1] : null, Absolute = End });
        while (op.Targets.Count > jumps.Count) op.Targets.RemoveAt(op.Targets.Count - 1);
        op.Bytes = bytes;
        return null;
    }

    // moves a line to another position (the index it ends up at). jumps keep pointing at the same lines wherever they go
    public void Move(ScriptOp op, int toIndex)
    {
        int from = Ops.IndexOf(op);
        if (from < 0) return;
        Ops.RemoveAt(from);
        Ops.Insert(Math.Clamp(toIndex, 0, Ops.Count), op);
    }

    // removes a line. jumps that pointed at it move to the next line
    public string? Remove(ScriptOp op)
    {
        int index = Ops.IndexOf(op);
        if (index < 0) return null;
        var next = index + 1 < Ops.Count ? Ops[index + 1] : null;
        bool targeted = Ops.Any(o => o != op && o.Targets.Any(t => t.Op == op));
        if (targeted && next == null) return "Other lines jump to this one and there's nothing after it to jump to instead.";
        foreach (var line in Ops)
            foreach (var jump in line.Targets)
                if (jump.Op == op) jump.Op = next;
        Ops.RemoveAt(index);
        return null;
    }

    // saving

    private byte[] Encode(ScriptOp op) =>
        op.IsText
            ? (op.TextChanged || op.BytesChanged ? op.Bytes.Concat(EncodeText(op.Text, op.Terminator)).ToArray() : (byte[])op.Raw.Clone())
            : (byte[])op.Bytes.Clone();

    // bytes of the edited event when placed at newStart (file offset) inside a block whose base is newBase.
    // jumps to commands of this event follow them, jumps to places outside it point into the copied block
    private byte[] Build(int newStart, int newBase, out bool layoutUnchanged) => Build(newStart, newBase, out layoutUnchanged, null);

    // pieces: when given, lines keep their original rom offsets (in-place save of a script with several regions)
    // and every line's bytes come back with its offset
    private byte[] Build(int newStart, int newBase, out bool layoutUnchanged, List<(int Offset, byte[] Bytes)>? pieces,
                         List<(ScriptOp Op, int Offset, byte[] Bytes)>? layout = null)
    {
        var encoded = new List<byte[]>();
        var newOffsets = new Dictionary<ScriptOp, int>();
        int pos = newStart;
        layoutUnchanged = Ops.Count == _original.Count && Ops.Where((o, i) => !ReferenceEquals(o, _original[i])).Count() == 0;
        foreach (var op in Ops)
        {
            if (pieces != null && !op.IsNew) pos = op.Offset;
            newOffsets[op] = pos;
            var bytes = Encode(op);
            if (op.IsNew || bytes.Length != (op.IsText ? op.RawLength : op.OriginalBytes.Length)) layoutUnchanged = false;
            encoded.Add(bytes);
            pos += bytes.Length;
        }

        int newJump = newStart - (Start - JumpBase);   // = newStart for a whole event
        var outBytes = new List<byte>();
        for (int i = 0; i < Ops.Count; i++)
        {
            var op = Ops[i]; var bytes = encoded[i];
            if (!op.IsText)
            {
                var jumps = JumpPositions(bytes);
                for (int k = 0; k < jumps.Count; k++)
                {
                    int j = jumps[k];
                    var target = k < op.Targets.Count ? op.Targets[k] : null;
                    int relative;
                    if (target?.Op != null && newOffsets.TryGetValue(target.Op, out int targetOffset)) relative = targetOffset - newJump;
                    else
                    {
                        int abs = target?.Absolute ?? End;
                        if (target?.Op == null && Regions.Any(r => abs >= r.From && abs < r.To))
                            throw new InvalidOperationException($"A jump at line {i + 1} lands inside a command; fix it before saving.");
                        relative = abs - BlockBase + newBase - newJump;   // into the (copied) rest of the block
                    }
                    if (relative is < 0 or > 0xFFFF) throw new InvalidOperationException($"The jump at line {i + 1} can't reach its target from the new location.");
                    bytes[j] = (byte)relative; bytes[j + 1] = (byte)(relative >> 8);
                }
            }
            outBytes.AddRange(bytes);
            pieces?.Add((newOffsets[op], bytes));
            layout?.Add((op, newOffsets[op], bytes));
        }
        // decoding stopped early, or the event doesn't end anymore, so carry on into the original code
        if (StopReason != null || Ops.Count == 0 || !Ops[^1].EndsFlow)
        {
            int relative = End - BlockBase + newBase - newJump;
            if (relative is < 0 or > 0xFFFF) throw new InvalidOperationException("Can't continue into the original script from the new location.");
            outBytes.AddRange(new byte[] { 0x01, (byte)relative, (byte)(relative >> 8) });
        }
        return outBytes.ToArray();
    }

    // every line's bytes as they'd be written with the lines laid out from the event's start (jumps already
    // worked out), or null when the event can't be built yet (like a jump landing inside a command)
    public List<(ScriptOp Op, int Offset, byte[] Bytes)>? PreviewLayout()
    {
        var layout = new List<(ScriptOp Op, int Offset, byte[] Bytes)>();
        try { Build(Start, BlockBase, out _, null, layout); return layout; }
        catch (InvalidOperationException) { return null; }
    }

    // total size of the event as it would be written
    public int BuiltLength() { try { return Build(Start, BlockBase, out _).Length; } catch (InvalidOperationException) { return -1; } }

    // the line at an absolute jump target, or null
    public ScriptOp? OpAtJumpValue(int value) { int abs = JumpBase + value; return Ops.FirstOrDefault(o => o.Offset == abs && !o.IsNew); }

    // true when the edits keep every line the same size (no rom expansion needed)
    public bool FitsInPlace()
    {
        var bytes = Build(Start, BlockBase, out bool same);
        return same || SameLengthInPlace(bytes);
    }

    // lines got reordered (or swapped for others) but the event is exactly as long as before, so it can be rewritten where it is
    private bool SameLengthInPlace(byte[] built) => Regions.Count <= 1 && built.Length == End - Start && !OthersJumpInside();

    private bool? _othersJumpInside;

    // another event of this map jumps into the middle of this one (then its lines have to keep their offsets)
    private bool OthersJumpInside()
    {
        if (_othersJumpInside is bool known) return known;
        bool found = false;
        try
        {
            foreach (int ev in PlausibleEvents(_rom, MapId))
            {
                var other = Load(_rom, MapId, ev);
                if (other == null || other.Start == Start) continue;
                if (other.Ops.Any(o => o.Targets.Any(t => t.Absolute > Start && t.Absolute < End))) { found = true; break; }
            }
        }
        catch { found = true; }
        _othersJumpInside = found;
        return found;
    }

    // writes the edited event to the rom, returns a description of what was done and where
    public string Save(bool allowExpand)
    {
        // all or nothing: expanding, the rename / emote patches and the block copy go in before the lines are laid out,
        // and a failure after them (a go to that can't reach its step from the new place) can't leave them behind
        var before = _rom.ToArray();
        try { return SaveCore(allowExpand); }
        catch
        {
            if (!_rom.SameAs(before)) _rom.ReplaceAll(before);
            throw;
        }
    }

    private string SaveCore(bool allowExpand)
    {
        if (!MatchesRom())
            throw new InvalidOperationException($"{(IsCodeView ? "This code" : $"Event {Event} of map {MapId:X2}")} was changed somewhere else since it was " +
                                                "opened here (another tab, Undo or a patch), so these lines would write over the newer version. " +
                                                "Revert to read it again, then make the changes again.");
        foreach (var o in Ops.Where(o => o.IsText && (o.IsNew || o.TextChanged)))
        {
            if (DutchLetters.Unwritable(_rom, o.Text) is string bad)
                throw new InvalidOperationException(bad + ".");
            if (EndingTag(o.Text) is string end)
                throw new InvalidOperationException($"{end} in the text \"{(o.Text.Length > 40 ? o.Text[..40] + "..." : o.Text)}\" would end the text there: " +
                                                    "the game would run the rest of the line as event commands. Take it out (use a new line, or a new text step for a new box).");
        }
        // the rename command needs its engine patch (added the first time an event uses it)
        if (Ops.Any(o => !o.IsText && o.Bytes.Length > 0 && o.Bytes[0] == Battle.RenamePatch.Op) && !Battle.RenamePatch.IsApplied(_rom))
        {
            if (!allowExpand && _rom.Length < MapWriter.ExpandedSize)
                throw new InvalidOperationException("The rename command needs a small patch in the expanded part of the ROM; the ROM must be expanded to 2 MB.");
            Battle.RenamePatch.Apply(_rom);
        }
        // so does the emote command
        if (Ops.Any(o => !o.IsText && o.Bytes.Length > 0 && o.Bytes[0] == Battle.EmotePatch.Op) && !Battle.EmotePatch.IsApplied(_rom))
        {
            if (!allowExpand && _rom.Length < MapWriter.ExpandedSize)
                throw new InvalidOperationException("The emote command needs a small patch in the expanded part of the ROM; the ROM must be expanded to 2 MB.");
            Battle.EmotePatch.Apply(_rom);
        }
        if (Regions.Count > 1)
        {
            var pieces = new List<(int Offset, byte[] Bytes)>();
            Build(Start, BlockBase, out bool sameLayout, pieces);
            if (sameLayout)
            {
                foreach (var (offset, bytes) in pieces) _rom.WriteBytes(offset, bytes);
                _rom.FixChecksum();
                return $"Event {Event} of map {MapId:X2} (and the code it jumps to) was rewritten in place.";
            }
        }
        var inPlace = Build(Start, BlockBase, out bool same);
        if ((same || SameLengthInPlace(inPlace)) && Regions.Count <= 1)
        {
            // leave off the carry-on goto that Build might add, the original bytes after the event are still there
            _rom.WriteBytes(Start, inPlace.AsSpan(0, End - Start).ToArray());
            return IsCodeView
                ? $"Code at ROM offset 0x{Start:X6} (reached from event {Event} of map {MapId:X2}) was rewritten in place ({End - Start} bytes)."
                : $"Event {Event} of map {MapId:X2} was rewritten in place at ROM offset 0x{Start:X6} ({End - Start} bytes).";
        }

        if (IsCodeView)
            throw new InvalidOperationException("This is code another part of the event jumps to; it can only be saved when every line keeps its size. " +
                                                "Open the event itself to make it longer or shorter.");
        if (!allowExpand && _rom.Length < MapWriter.ExpandedSize)
            throw new InvalidOperationException("The edited event changed size; the ROM must be expanded to 2 MB to store it.");
        bool expanded = false;
        if (_rom.Length < MapWriter.ExpandedSize) { _rom.Expand(MapWriter.ExpandedSize); expanded = true; }

        // copy the map's whole script block (other events jump into it) to a new block above 1 MB, put the rewritten
        // event after it and repoint the map. the space table knows the exact block lengths
        var freeSpace = ExpansionSpace.Load(_rom);
        int copyLen = BlockLength();
        if (copyLen < End - BlockBase) copyLen = End - BlockBase;
        int size = Build(0, 0, out _).Length;   // only the length matters here
        if (copyLen + size > 0xFFF0)
            throw new InvalidOperationException("This map's script block is too large to copy.");
        int newBase = freeSpace.Allocate(copyLen + size + 1, ExpansionSpace.Kind.ScriptBlock, MapId, ScriptLimit);
        int newStart = newBase + copyLen;
        _rom.WriteBytes(newBase, _rom.ReadBytes(BlockBase, copyLen));
        var code = Build(newStart, newBase, out _);
        if (newStart + code.Length + 1 > _rom.Length || newStart + code.Length - newBase > 0xFFFF)
            throw new InvalidOperationException("Not enough room for the edited event.");
        _rom.WriteBytes(newStart, code);
        _rom.WriteByte(newStart + code.Length, 0x00);          // guard so the free-space search skips it

        int relative = newStart - newBase;
        _rom.WriteUInt16Le(newBase + Event * 2, (ushort)relative);
        int blockPointer = newBase - 0x18000;
        int tableEntry = LufiaMap.MapScriptTable + MapId * 5;
        _rom.WriteBytes(tableEntry, new[] { (byte)blockPointer, (byte)(blockPointer >> 8), (byte)(blockPointer >> 16) });

        // the previous copy above 1 MB is free again, unless another map still uses it
        bool oldFreed = false;
        if (BlockBase >= MapWriter.ExpansionStart && freeSpace.At(BlockBase)?.Start == BlockBase &&
            !MapCatalog.Scan(_rom).Any(m => m.MapId != MapId && BlockOffset(_rom, m.MapId) == BlockBase))
        {
            freeSpace.Free(BlockBase);
            oldFreed = true;
        }
        freeSpace.Save();
        _rom.FixChecksum();
        return $"Event {Event} of map {MapId:X2} changed size, so the map's script block was copied to ROM offset 0x{newBase:X6}" +
               $"{(expanded ? " in the newly expanded 2 MB ROM" : "")} and the event added at 0x{newStart:X6} ({code.Length} bytes). " +
               (oldFreed ? "The block's previous copy above 1 MB was freed." : "The original block is left untouched.");
    }

    // scripts run with the data bank set to their own bank (3 + offset from 0x18000 / 32 KB = file offset / 32 KB,
    // $01:C292), and the command handlers reach RAM through it ($0Dxx, $158F,X...). only banks 00-3F mirror the
    // work RAM, so event script blocks have to stay below 2 MB (file 0x200000), even in a 4 MB rom
    public const int ScriptLimit = MapWriter.ExpandedSize;

    // how many event slots the map's block has (its offset table runs up to the first event's code)
    public static int SlotCount(RomBuffer rom, int mapId)
    {
        int block = BlockOffset(rom, mapId);
        int blockLength = new EventScript(rom, mapId, 0, block, block).BlockLength(), first = blockLength, baseEv = LufiaMap.EventBase(rom, mapId);
        for (int i = 0; i < 0x100 && i * 2 + 1 < first && block + i * 2 + 1 < rom.Length; i++)
        {
            int relative = rom.ReadUInt16Le(block + i * 2);
            // (a block shared by maps with an event base: its entry 0 holds the base, not an event. map 97's 000C
            // for one, whose events are 12-17: read as an event, the table looked 6 entries long)
            if (i == 0 && baseEv > 0 && relative == baseEv) continue;
            if (relative != 0xFFFF && relative >= 2 && relative < first) first = relative;
        }
        return Math.Min(0x100, first / 2);
    }

    // gives map mapId its own copy of the script block it uses (in the added space), so editing its events in place
    // doesn't change another map that shares the block. for new maps copied from another one
    public static void CopyBlockFor(RomBuffer rom, int mapId)
    {
        var script = new EventScript(rom, mapId, 0, BlockOffset(rom, mapId), BlockOffset(rom, mapId));
        int blockLength = script.BlockLength();
        if (blockLength <= 0 || blockLength > 0xFFF0) throw new InvalidOperationException("This map's script block can't be copied.");
        var freeSpace = ExpansionSpace.Load(rom);
        int newBlock = freeSpace.Allocate(blockLength + 1, ExpansionSpace.Kind.ScriptBlock, mapId, ScriptLimit);
        rom.WriteBytes(newBlock, rom.ReadBytes(script.BlockBase, blockLength));
        rom.WriteByte(newBlock + blockLength, 0x00);
        freeSpace.Save();
        int blockPointer = newBlock - 0x18000, tableEntry = LufiaMap.MapScriptTable + mapId * 5;
        rom.WriteBytes(tableEntry, new[] { (byte)blockPointer, (byte)(blockPointer >> 8), (byte)(blockPointer >> 16) });
    }

    // a new empty script block for a map: slots events, none of them with a script yet
    public static void NewBlockFor(RomBuffer rom, int mapId, int slots)
    {
        var freeSpace = ExpansionSpace.Load(rom);
        int newBlock = freeSpace.Allocate(slots * 2 + 1, ExpansionSpace.Kind.ScriptBlock, mapId, ScriptLimit);
        rom.WriteBytes(newBlock, Enumerable.Repeat((byte)0xFF, slots * 2).ToArray());
        rom.WriteByte(newBlock + slots * 2, 0x00);
        freeSpace.Save();
        int blockPointer = newBlock - 0x18000, tableEntry = LufiaMap.MapScriptTable + mapId * 5;
        rom.WriteBytes(tableEntry, new[] { (byte)blockPointer, (byte)(blockPointer >> 8), (byte)(blockPointer >> 16) });
    }

    // creates event ev on a map that doesn't have it yet (a slot with no script). the map's block gets copied to the
    // added space with a one-command event (end) after it, and the event editor can fill that in
    public static void CreateEvent(RomBuffer rom, int mapId, int ev)
    {
        if (LufiaMap.EventScriptOffset(rom, mapId, ev) >= 0) throw new InvalidOperationException($"Event {ev} already has a script.");
        if (ev >= SlotCount(rom, mapId)) throw new InvalidOperationException($"This map's script block has {SlotCount(rom, mapId)} event slots; event {ev} doesn't fit.");
        if (rom.Length < MapWriter.ExpandedSize) rom.Expand(MapWriter.ExpandedSize);
        var script = new EventScript(rom, mapId, ev, BlockOffset(rom, mapId), BlockOffset(rom, mapId));
        int blockLength = script.BlockLength(), oldBase = script.BlockBase;
        var freeSpace = ExpansionSpace.Load(rom);
        int newBlock = freeSpace.Allocate(blockLength + 2, ExpansionSpace.Kind.ScriptBlock, mapId, ScriptLimit);
        rom.WriteBytes(newBlock, rom.ReadBytes(oldBase, blockLength));
        rom.WriteByte(newBlock + blockLength, 0x00);       // the new event: end
        rom.WriteByte(newBlock + blockLength + 1, 0x00);   // guard
        rom.WriteUInt16Le(newBlock + ev * 2, (ushort)blockLength);
        int blockPointer = newBlock - 0x18000, tableEntry = LufiaMap.MapScriptTable + mapId * 5;
        rom.WriteBytes(tableEntry, new[] { (byte)blockPointer, (byte)(blockPointer >> 8), (byte)(blockPointer >> 16) });
        if (oldBase >= MapWriter.ExpansionStart && freeSpace.At(oldBase)?.Start == oldBase &&
            !MapCatalog.Scan(rom).Any(m => m.MapId != mapId && BlockOffset(rom, m.MapId) == oldBase))
            freeSpace.Free(oldBase);
        freeSpace.Save();
        rom.FixChecksum();
    }

    // bytes from this block's start to the next map's script block (the blocks sit right after each other)
    private int BlockLength()
    {
        if (BlockBase >= MapWriter.ExpansionStart)
        {
            // exact length from the space table (rebuilt for roms from earlier versions)
            var entry = ExpansionSpace.Load(_rom).At(BlockBase);
            if (entry != null) return entry.End - BlockBase;
            return Math.Min(0xC000, ExpansionSpace.TableStart - BlockBase);
        }
        int next = NextBlockStart(_rom, BlockBase);
        int limit = Math.Min(_rom.Length, BlockBase + 0xC000);
        return Math.Min(next, limit) - BlockBase;
    }

    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<RomBuffer, Tuple<byte[], int[]>> _blockStarts = new();

    // the script blocks of the game's maps, sorted. cached and only rebuilt when the map tables change, so asking for
    // every map's block length doesn't list every map each time anymore
    public static int[] BlockStarts(RomBuffer rom)
    {
        // the tables a map's existence and block depend on: map data select, script table
        var key = rom.ReadBytes(LufiaMap.MapScriptTable, 256 * 5).Concat(rom.ReadBytes(LufiaMap.MapDataSelectTable, 256)).ToArray();
        if (_blockStarts.TryGetValue(rom, out var cached) && cached.Item1.AsSpan().SequenceEqual(key)) return cached.Item2;
        var starts = MapCatalog.Scan(rom).Select(m => BlockOffset(rom, m.MapId)).Distinct().OrderBy(x => x).ToArray();
        _blockStarts.AddOrUpdate(rom, Tuple.Create(key, starts));
        return starts;
    }

    // where the next map's script block starts after blockBase (int.MaxValue for none)
    public static int NextBlockStart(RomBuffer rom, int blockBase)
    {
        foreach (int start in BlockStarts(rom)) if (start > blockBase) return start;
        return int.MaxValue;
    }

}
