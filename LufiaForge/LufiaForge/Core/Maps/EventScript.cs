using LufiaForge.Modules.TextEditor;

namespace LufiaForge.Core.Maps;

/// <summary>Where a jump goes: another command of the same event, or a fixed place outside it.</summary>
public sealed class JumpRef
{
    /// <summary>Target command inside this event (moves along when the event is rewritten).</summary>
    public ScriptOp? Op { get; set; }
    /// <summary>Absolute file offset when the target is outside the decoded event.</summary>
    public int Absolute { get; set; }
}

/// <summary>One command or text segment of an event script.</summary>
public sealed class ScriptOp
{
    /// <summary>File offset in the ROM (-1 for commands added in the editor).</summary>
    public int Offset { get; init; } = -1;
    /// <summary>Command bytes; for text, the opener (and its operand) only. Jump operand bytes are filled in when saving.</summary>
    public byte[] Bytes { get; set; } = Array.Empty<byte>();
    public byte[] OriginalBytes { get; init; } = Array.Empty<byte>();
    public bool IsText { get; init; }
    /// <summary>One entry per jump operand (see <see cref="EventScript.JumpPositions"/>).</summary>
    public List<JumpRef> Targets { get; } = new();

    // Text segments
    /// <summary>Editable text (see <see cref="EventScript.DecodeText"/> for the tag syntax).</summary>
    public string Text { get; set; } = "";
    public string OriginalText { get; init; } = "";
    /// <summary>0x04 = back to script commands, 0x00 = end of script.</summary>
    public byte Terminator { get; init; } = 0x04;
    /// <summary>Raw length of opener + text + terminator in the ROM.</summary>
    public int RawLength { get; init; }
    /// <summary>Original bytes of opener + text + terminator (reused when nothing in the box changed).</summary>
    public byte[] Raw { get; init; } = Array.Empty<byte>();

    public bool IsNew => Offset < 0;
    public bool TextChanged => IsText && Text.Replace("\r", "") != OriginalText;
    public bool BytesChanged => IsNew || !Bytes.AsSpan().SequenceEqual(OriginalBytes);
    /// <summary>Size in the ROM as decoded (opener + text + terminator for dialogue).</summary>
    public int Length => IsText ? RawLength : Bytes.Length;

    /// <summary>True when execution never falls through to the next byte (end, goto, first-time branch, text ending the script).</summary>
    public bool EndsFlow => IsText ? Terminator == 0x00 : Bytes.Length > 0 && Bytes[0] is 0x00 or 0x01 or 0x02;
}

/// <summary>
/// An event script (what runs when the party talks to a character or steps on a trigger), decoded into
/// editable commands and dialogue. Command lengths come from the handlers at $01:C78C (00-6D) and the range
/// table at $01:C868 (80-FF), checked against playthrough traces. Jump operands are offsets from the
/// event's own start (the engine sets its base, $0D0F, to the event start). See research-script-engine.md.
///
/// Saving: when nothing changes size the event is rewritten in place. Otherwise the map's whole script
/// block is copied into the expanded ROM area (so every other event and every jump into the old code keeps
/// working), the rewritten event is appended to the copy, and the map's script table entry
/// (0x18200 + map*5) points at the copy.
/// </summary>
public sealed class EventScript
{
    public int MapId { get; }
    public int Event { get; }
    public int BlockBase { get; }
    public int Start { get; }
    /// <summary>
    /// Where jump offsets are counted from: the start of the running event. Equals <see cref="Start"/> for a
    /// whole event; differs when viewing code that an event jumps to (<see cref="LoadAt"/>).
    /// </summary>
    public int JumpBase { get; private set; }
    /// <summary>True when this is a view of code inside another event's reach, not an event of its own.</summary>
    public bool IsCodeView => JumpBase != Start;
    /// <summary>End of the decoded region (exclusive).</summary>
    public int End { get; private set; }
    public List<ScriptOp> Ops { get; } = new();
    /// <summary>Set when decoding stopped at an unknown command; the rest is kept as-is when saving.</summary>
    public string? StopReason { get; private set; }

    private readonly RomBuffer _rom;
    private readonly List<ScriptOp> _original = new();

    private EventScript(RomBuffer rom, int mapId, int ev, int blockBase, int start)
    {
        _rom = rom; MapId = mapId; Event = ev; BlockBase = blockBase; Start = start; JumpBase = start;
    }

    public static int BlockOffset(RomBuffer rom, int mapId)
    {
        int p = LufiaMap.MapScriptTable + mapId * 5;
        return 0x18000 + (rom.ReadByte(p) | (rom.ReadByte(p + 1) << 8) | (rom.ReadByte(p + 2) << 16));
    }

    /// <summary>Decode event <paramref name="ev"/> of a map, or null if the event has no script.</summary>
    public static EventScript? Load(RomBuffer rom, int mapId, int ev)
    {
        int start = LufiaMap.EventScriptOffset(rom, mapId, ev);
        if (start < 0 || start >= rom.Length) return null;
        var s = new EventScript(rom, mapId, ev, BlockOffset(rom, mapId), start);
        s.Decode();
        return s;
    }

    /// <summary>
    /// Decode the code at <paramref name="start"/> that event <paramref name="ev"/> (whose script starts at
    /// <paramref name="jumpBase"/>) jumps to. Edits that keep every line's size can be saved in place.
    /// </summary>
    public static EventScript LoadAt(RomBuffer rom, int mapId, int ev, int start, int jumpBase)
    {
        var s = new EventScript(rom, mapId, ev, BlockOffset(rom, mapId), start) { JumpBase = jumpBase };
        s.Decode();
        return s;
    }

    private Dictionary<int, int>? _eventStarts;

    /// <summary>Which event of this map starts at a ROM offset, or -1.</summary>
    public int EventStartingAt(int offset)
    {
        if (_eventStarts == null)
        {
            _eventStarts = new();
            for (int ev = 0; ev < 256; ev++)
            {
                int o = LufiaMap.EventScriptOffset(_rom, MapId, ev);
                if (o >= 0) _eventStarts.TryAdd(o, ev);
            }
        }
        return _eventStarts.TryGetValue(offset, out int e) ? e : -1;
    }

    private static Dictionary<int, int>? _blockEnds;
    private static RomBuffer? _blockEndsRom;

    /// <summary>
    /// Event numbers that can really be used on a map: characters (0..count-1) and trigger/spot events
    /// (event base + area), and only table entries that point inside the map's own script block. Entries
    /// beyond that are other data and decode as garbage.
    /// </summary>
    public static List<int> PlausibleEvents(RomBuffer rom, int mapId)
    {
        if (!ReferenceEquals(_blockEndsRom, rom) || _blockEnds == null)
        {
            var bases = MapCatalog.Scan(rom).Select(m => BlockOffset(rom, m.MapId)).Distinct().OrderBy(b => b).ToList();
            _blockEnds = new();
            for (int i = 0; i < bases.Count; i++) _blockEnds[bases[i]] = i + 1 < bases.Count ? bases[i + 1] : bases[i] + 0x8000;
            _blockEndsRom = rom;
        }
        int block = BlockOffset(rom, mapId);
        int end = _blockEnds.TryGetValue(block, out int e) ? e : block + 0x8000;
        int npcs = 0, areas = 0;
        try { var m = LufiaMap.Load(rom, mapId); npcs = m.Npcs.Count; areas = Math.Max(m.SectionCounts[3], m.SectionCounts[4]); } catch { }
        int max = Math.Max(npcs - 1, LufiaMap.EventBase(rom, mapId) + areas + 1);
        var list = new List<int>();
        for (int ev = 0; ev <= Math.Min(max, 255); ev++)
        {
            int o = LufiaMap.EventScriptOffset(rom, mapId, ev);
            if (o >= block && o < end) list.Add(ev);
        }
        return list;
    }

    /// <summary>Plain description of a jump target outside the decoded lines.</summary>
    public string DescribeOutside(int abs)
    {
        int ev = EventStartingAt(abs);
        return ev >= 0 ? $"event {ev}'s start (0x{abs:X6})" : $"code at 0x{abs:X6}";
    }

    /// <summary>Optional names for story flags (set by the app from the user's flag names).</summary>
    public static Func<int, string?>? FlagName;

    private static string Flag(int f) => FlagName?.Invoke(f) is { Length: > 0 } n ? $"{f:X2} \"{n}\"" : $"{f:X2}";

    // ── Command table ───────────────────────────────────────────────────────

    /// <summary>
    /// Length of a command from its opcode (and, for 03, its count byte). 0 = unknown;
    /// negative = text opener of that many bytes, followed by text.
    /// </summary>
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
        0x51 => 0,                    // length not confirmed
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
        >= 0x80 and <= 0x87 => 1,
        >= 0x88 and <= 0xAF => -1,
        >= 0xB0 and <= 0xBF => 1,
        >= 0xC0 and <= 0xDF => 3,
        >= 0xE0 => 1,
        _ => 0,
    };

    /// <summary>Byte positions of 16-bit jump operands (offsets from the event start) in a command.</summary>
    public static IReadOnlyList<int> JumpPositions(byte[] b)
    {
        if (b.Length == 0) return Array.Empty<int>();
        return b[0] switch
        {
            0x01 => new[] { 1 },
            0x02 => new[] { 2, 4 },
            0x03 when b.Length >= 2 => Enumerable.Range(0, b[1]).Select(i => 2 + 2 * i).ToArray(),
            0x04 or 0x05 => new[] { 2 },
            _ => Array.Empty<int>(),
        };
    }

    public static bool IsTextOpener(int op) => CommandLength(op) < 0;

    /// <summary>Who speaks for a text opener.</summary>
    public static string SpeakerName(byte[] opener) => opener.Length == 0 ? "Text" : opener[0] switch
    {
        0x0C => "Narration",
        0x0D => "Text box",
        >= 0x88 and <= 0x8F => $"Actor {opener[0] - 0x88} says",
        >= 0x90 and <= 0xAF => $"Actor {opener[0] - 0x90 + 8} says",
        _ => $"Text ({string.Join(" ", opener.Select(x => x.ToString("X2")))})",
    };

    /// <summary>Human description of a command; jump targets are shown as line numbers when known.</summary>
    public string Describe(ScriptOp op)
    {
        if (op.IsText) return $"{SpeakerName(op.Bytes)}  ({Hex(op.Bytes)})";
        var b = op.Bytes;
        if (b.Length == 0) return "";
        string T(int i)
        {
            if (i >= op.Targets.Count) return "?";
            var t = op.Targets[i];
            int line = t.Op != null ? Ops.IndexOf(t.Op) : -1;
            return line >= 0 ? $"line {line + 1}" : DescribeOutside(t.Absolute);
        }
        string Item(int id) => MapSetupScript.ItemName(_rom, id);
        return b[0] switch
        {
            0x00 => "End",
            0x01 => $"Go to {T(0)}",
            0x02 => $"First time only (map-local flag {b[1]:X2}): first time go to {T(0)}, later go to {T(1)}",
            0x03 => $"Choice targets: {string.Join(", ", Enumerable.Range(0, b[1]).Select(T))}",
            0x04 => $"If story flag {Flag(b[1])} is set, go to {T(0)}",
            0x05 => $"If story flag {Flag(b[1])} is not set, go to {T(0)}",
            0x06 => $"Set story flag {Flag(b[1])}",
            0x07 => $"Clear story flag {Flag(b[1])}",
            0x08 => "Refresh the map's characters and objects",
            0x1A => $"Character {b[1]:X2} joins / appears ({b[2]:X2})",
            0x1E => "Church menu",
            0x1C => $"Open shop {b[1]:X2}",
            0x1D => $"Stay at the inn ({b[1] | (b[2] << 8)} GP)",
            0x3D => $"Character {b[1]} learns spell {b[2]:X2}",
            0x3E => $"Give item: {Item(b[1])} x{b[2]}",
            0x6C => $"Wait {b[1]} frames",
            >= 0x80 and <= 0x87 => "Short wait",
            _ => $"Command {b[0]:X2}",
        };
    }

    private static string Hex(byte[] b) => string.Join(" ", b.Select(x => x.ToString("X2")));

    // ── Decoding ────────────────────────────────────────────────────────────

    private void Decode()
    {
        int p = Start;
        var targets = new SortedSet<int>();
        for (int guard = 0; guard < 4000 && p < _rom.Length; guard++)
        {
            int op = _rom.ReadByte(p);
            int len = CommandLength(op, p + 1 < _rom.Length ? _rom.ReadByte(p + 1) : 0);
            if (len == 0) { StopReason = $"unknown command {op:X2} at 0x{p:X6}"; break; }

            ScriptOp sop;
            if (len < 0)
            {
                int openerLen = -len;
                var opener = _rom.ReadBytes(p, openerLen);
                var (text, raw, term) = DecodeText(_rom, p + openerLen);
                sop = new ScriptOp
                {
                    Offset = p, Bytes = opener, OriginalBytes = (byte[])opener.Clone(), IsText = true,
                    Text = text, OriginalText = text, Terminator = term, RawLength = openerLen + raw,
                    Raw = _rom.ReadBytes(p, openerLen + raw),
                };
            }
            else
            {
                var b = _rom.ReadBytes(p, len);
                sop = new ScriptOp { Offset = p, Bytes = b, OriginalBytes = (byte[])b.Clone() };
                foreach (int j in JumpPositions(b))
                {
                    int abs = JumpBase + (b[j] | (b[j + 1] << 8));
                    targets.Add(abs);
                    sop.Targets.Add(new JumpRef { Absolute = abs });
                }
            }
            Ops.Add(sop);
            p += sop.Length;

            // Keep decoding past an end only when a jump in this event lands right here.
            if (sop.EndsFlow && !targets.Contains(p)) break;
        }
        End = p;

        // Point jumps at the commands they land on.
        var byOffset = Ops.ToDictionary(o => o.Offset);
        foreach (var op in Ops)
            foreach (var t in op.Targets)
                if (byOffset.TryGetValue(t.Absolute, out var target)) t.Op = target;
        _original.AddRange(Ops);
    }

    /// <summary>
    /// Decode dialogue text up to its terminator (04 = back to commands, 00 = end of script).
    /// Returns editable text using these tags: \n new line, [Hero]..[Artea] names, [ITEM:xx], [SPELL:xx],
    /// [TOWN:xx], [CHAR:xx], and [xx] / [xx:yy] for other control codes.
    /// </summary>
    public static (string Text, int RawLength, byte Terminator) DecodeText(RomBuffer rom, int start)
    {
        var d = TextDecoder.Decode(rom, start, expandMte: true, stopAtPageBreak: true);
        var sb = new System.Text.StringBuilder();
        foreach (var t in d.Tokens)
        {
            byte b = rom.ReadByte(t.RomOffset);
            switch (b)
            {
                case 0x00:
                case 0x04:
                    return (sb.ToString(), t.RomOffset + 1 - start, b);
                case 0x05: sb.Append('\n'); break;
                case 0x07:
                {
                    byte id = rom.ReadByte(t.RomOffset + 1);
                    sb.Append(id < NameTags.Length ? $"[{NameTags[id]}]" : $"[CHAR:{id:X2}]");
                    break;
                }
                case 0x09: sb.Append($"[ITEM:{rom.ReadByte(t.RomOffset + 1):X2}]"); break;
                case 0x0A: sb.Append($"[SPELL:{rom.ReadByte(t.RomOffset + 1):X2}]"); break;
                case 0x0B: sb.Append($"[TOWN:{rom.ReadByte(t.RomOffset + 1):X2}]"); break;
                case 0x0C: case 0x0D:
                case >= 0x10:
                    sb.Append(t.Display); break;
                default:
                    sb.Append(t.ByteLength == 2 ? $"[{b:X2}:{rom.ReadByte(t.RomOffset + 1):X2}]" : $"[{b:X2}]");
                    break;
            }
        }
        // no terminator found within the decoder's window
        return (sb.ToString(), d.BytesConsumed, 0x00);
    }

    public static readonly string[] NameTags = { "Hero", "Lufia", "Aguro", "Jerin", "Maxim", "Selan", "Guy", "Artea" };

    /// <summary>Encode edited text plus its terminator.</summary>
    public byte[] EncodeText(string text, byte terminator)
    {
        var bytes = TextDecoder.Encode(text.Replace("\r", ""), _rom).ToList();
        if (bytes.Count > 0 && bytes[^1] == 0x00) bytes.RemoveAt(bytes.Count - 1);
        bytes.Add(terminator);
        return bytes.ToArray();
    }

    // ── Editing ─────────────────────────────────────────────────────────────

    public bool IsModified =>
        Ops.Count != _original.Count || Ops.Where((o, i) => !ReferenceEquals(o, _original[i])).Any() ||
        Ops.Any(o => o.TextChanged || o.BytesChanged || TargetsChanged(o));

    private static bool TargetsChanged(ScriptOp o) =>
        !o.IsNew && o.Targets.Any(t => t.Op != null ? t.Op.Offset != t.Absolute : false);

    /// <summary>A new command (jumps start pointing at the following line).</summary>
    public ScriptOp NewCommand(byte[] bytes, int insertAt)
    {
        var op = new ScriptOp { Bytes = bytes };
        Ops.Insert(insertAt, op);
        foreach (var _ in JumpPositions(bytes))
            op.Targets.Add(new JumpRef { Op = insertAt + 1 < Ops.Count ? Ops[insertAt + 1] : null, Absolute = End });
        return op;
    }

    /// <summary>A new dialogue box.</summary>
    public ScriptOp NewText(byte opener, string text, int insertAt)
    {
        var op = new ScriptOp { Bytes = new[] { opener }, IsText = true, Text = text, Terminator = 0x04 };
        Ops.Insert(insertAt, op);
        return op;
    }

    /// <summary>
    /// Replace a command's bytes. Jump targets are kept where possible; new jump operands point at the next line.
    /// Returns an error message, or null when the bytes are a valid command.
    /// </summary>
    public string? SetCommandBytes(ScriptOp op, byte[] bytes)
    {
        if (op.IsText) return "This line is a dialogue box.";
        if (bytes.Length == 0) return "Enter at least the command byte.";
        int len = CommandLength(bytes[0], bytes.Length > 1 ? bytes[1] : 0);
        if (len == 0) return $"Command {bytes[0]:X2} isn't known well enough to edit safely.";
        if (len < 0) return $"{bytes[0]:X2} starts a dialogue box; insert a text line instead.";
        if (bytes.Length != len) return $"Command {bytes[0]:X2} is {len} byte{(len == 1 ? "" : "s")} long.";
        var jumps = JumpPositions(bytes);
        int idx = Ops.IndexOf(op);
        while (op.Targets.Count < jumps.Count)
            op.Targets.Add(new JumpRef { Op = idx + 1 < Ops.Count ? Ops[idx + 1] : null, Absolute = End });
        while (op.Targets.Count > jumps.Count) op.Targets.RemoveAt(op.Targets.Count - 1);
        op.Bytes = bytes;
        return null;
    }

    /// <summary>Remove a line. Jumps that pointed at it move to the following line.</summary>
    public string? Remove(ScriptOp op)
    {
        int idx = Ops.IndexOf(op);
        if (idx < 0) return null;
        var next = idx + 1 < Ops.Count ? Ops[idx + 1] : null;
        bool targeted = Ops.Any(o => o != op && o.Targets.Any(t => t.Op == op));
        if (targeted && next == null) return "Other lines jump to this one and there's nothing after it to jump to instead.";
        foreach (var o in Ops)
            foreach (var t in o.Targets)
                if (t.Op == op) t.Op = next;
        Ops.RemoveAt(idx);
        return null;
    }

    // ── Saving ──────────────────────────────────────────────────────────────

    private byte[] Encode(ScriptOp o) =>
        o.IsText
            ? (o.TextChanged || o.BytesChanged ? o.Bytes.Concat(EncodeText(o.Text, o.Terminator)).ToArray() : (byte[])o.Raw.Clone())
            : (byte[])o.Bytes.Clone();

    /// <summary>
    /// Bytes of the edited event when placed at <paramref name="newStart"/> (file offset) inside a block
    /// whose base is <paramref name="newBase"/>. Jumps to commands of this event follow them; jumps to places
    /// outside it point into the copied block.
    /// </summary>
    private byte[] Build(int newStart, int newBase, out bool layoutUnchanged)
    {
        var encoded = new List<byte[]>();
        var newPos = new Dictionary<ScriptOp, int>();
        int pos = newStart;
        layoutUnchanged = Ops.Count == _original.Count && Ops.Where((o, i) => !ReferenceEquals(o, _original[i])).Count() == 0;
        foreach (var o in Ops)
        {
            newPos[o] = pos;
            var b = Encode(o);
            if (o.IsNew || b.Length != (o.IsText ? o.RawLength : o.OriginalBytes.Length)) layoutUnchanged = false;
            encoded.Add(b);
            pos += b.Length;
        }

        int newJump = newStart - (Start - JumpBase);   // = newStart for a whole event
        var outBytes = new List<byte>();
        for (int i = 0; i < Ops.Count; i++)
        {
            var o = Ops[i]; var b = encoded[i];
            if (!o.IsText)
            {
                var jumps = JumpPositions(b);
                for (int k = 0; k < jumps.Count; k++)
                {
                    int j = jumps[k];
                    var t = k < o.Targets.Count ? o.Targets[k] : null;
                    int rel;
                    if (t?.Op != null && newPos.TryGetValue(t.Op, out int nt)) rel = nt - newJump;
                    else
                    {
                        int abs = t?.Absolute ?? End;
                        if (abs >= Start && abs < End && t?.Op == null)
                            throw new InvalidOperationException($"A jump at line {i + 1} lands inside a command; fix it before saving.");
                        rel = abs - BlockBase + newBase - newJump;   // into the (copied) rest of the block
                    }
                    if (rel is < 0 or > 0xFFFF) throw new InvalidOperationException($"The jump at line {i + 1} can't reach its target from the new location.");
                    b[j] = (byte)rel; b[j + 1] = (byte)(rel >> 8);
                }
            }
            outBytes.AddRange(b);
        }
        // Decoding stopped early, or the event no longer ends: continue with the original code.
        if (StopReason != null || Ops.Count == 0 || !Ops[^1].EndsFlow)
        {
            int rel = End - BlockBase + newBase - newJump;
            if (rel is < 0 or > 0xFFFF) throw new InvalidOperationException("Can't continue into the original script from the new location.");
            outBytes.AddRange(new byte[] { 0x01, (byte)rel, (byte)(rel >> 8) });
        }
        return outBytes.ToArray();
    }

    /// <summary>True when the edits keep every line the same size (no ROM expansion needed).</summary>
    public bool FitsInPlace()
    {
        Build(Start, BlockBase, out bool same);
        return same;
    }

    /// <summary>Write the edited event to the ROM. Returns a description of what was done and where.</summary>
    public string Save(bool allowExpand)
    {
        var inPlace = Build(Start, BlockBase, out bool same);
        if (same)
        {
            // drop the continuation goto that Build may add; the original bytes after the event are still there
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

        int newBase, newStart, copyLen;
        if (BlockBase >= MapWriter.ExpansionStart && IsLastRegion(out int regionEnd))
        {
            // Block was already moved and nothing follows it: append the event to it.
            newBase = BlockBase; newStart = regionEnd; copyLen = 0;
        }
        else
        {
            copyLen = BlockLength();
            if (copyLen < End - BlockBase) copyLen = End - BlockBase;
            int size = Build(0, 0, out _).Length;   // only the length matters here
            if (copyLen + size > 0xFFF0)
                throw new InvalidOperationException("This map's script block is too large to copy.");
            newBase = MapWriter.FindFreeSpace(_rom, copyLen + size + 1);
            newStart = newBase + copyLen;
            _rom.WriteBytes(newBase, _rom.ReadBytes(BlockBase, copyLen));
        }
        var code = Build(newStart, newBase, out _);
        if (newStart + code.Length + 1 > _rom.Length || newStart + code.Length - newBase > 0xFFFF)
            throw new InvalidOperationException("Not enough room for the edited event.");
        _rom.WriteBytes(newStart, code);
        _rom.WriteByte(newStart + code.Length, 0x00);          // guard so the free-space search skips it

        int rel = newStart - newBase;
        _rom.WriteUInt16Le(newBase + Event * 2, (ushort)rel);
        int t = newBase - 0x18000;
        int p = LufiaMap.MapScriptTable + MapId * 5;
        _rom.WriteBytes(p, new[] { (byte)t, (byte)(t >> 8), (byte)(t >> 16) });
        _rom.FixChecksum();
        return copyLen == 0
            ? $"Event {Event} of map {MapId:X2} changed size, so it was added to the map's already-moved script block: " +
              $"new event at ROM offset 0x{newStart:X6} ({code.Length} bytes)."
            : $"Event {Event} of map {MapId:X2} changed size, so the map's script block was copied to ROM offset 0x{newBase:X6}" +
              $"{(expanded ? " in the newly expanded 2 MB ROM" : "")} and the event added at 0x{newStart:X6} ({code.Length} bytes). " +
              "The old copy is left untouched.";
    }

    /// <summary>Bytes from this block's start to the next map's script block (blocks are contiguous).</summary>
    private int BlockLength()
    {
        if (BlockBase >= MapWriter.ExpansionStart) return RegionEnd() - BlockBase;
        int next = int.MaxValue;
        foreach (var m in MapCatalog.Scan(_rom))
        {
            int b = BlockOffset(_rom, m.MapId);
            if (b > BlockBase && b < next) next = b;
        }
        int limit = Math.Min(_rom.Length, BlockBase + 0xC000);
        return Math.Min(next, limit) - BlockBase;
    }

    /// <summary>End of a block in the expanded area: regions there are separated by at least 16 unused (FF) bytes.</summary>
    private int RegionEnd()
    {
        int p = Math.Max(End, BlockBase + 2);
        int run = 0;
        for (; p < _rom.Length; p++)
        {
            run = _rom.ReadByte(p) == 0xFF ? run + 1 : 0;
            if (run == 16) return p - 15;
        }
        return _rom.Length;
    }

    private bool IsLastRegion(out int regionEnd)
    {
        regionEnd = RegionEnd();
        for (int p = regionEnd; p < _rom.Length; p++)
            if (_rom.ReadByte(p) != 0xFF) return false;
        return true;
    }
}
