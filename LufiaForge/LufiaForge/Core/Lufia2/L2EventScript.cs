namespace LufiaForge.Core.Lufia2;

/// <summary>Where a jump of a step goes: another step of the script, or a fixed place outside it.</summary>
public sealed class L2Jump
{
    public L2Step? Step { get; set; }
    /// <summary>File offset when the target isn't a step of the script (-1: none yet).</summary>
    public int Absolute { get; set; } = -1;
}

/// <summary>One step of an event: a command, or text with the command that opens it (a speaker, a narration box,
/// a question).</summary>
public sealed class L2Step
{
    /// <summary>File offset in the ROM (-1 for a step added in the editor).</summary>
    public int At { get; init; } = -1;
    /// <summary>The command bytes (for text, the opening command). Jump operands are filled in when written.</summary>
    public byte[] Bytes { get; set; } = Array.Empty<byte>();
    public byte[] OriginalBytes { get; init; } = Array.Empty<byte>();
    public bool IsText { get; init; }
    /// <summary>The text as read (end code included); written back as is while the text and its place stay the same.</summary>
    public byte[] TextRaw { get; init; } = Array.Empty<byte>();
    public string Text { get; set; } = "";
    public string OriginalText { get; init; } = "";
    /// <summary>The code ending the text: 01 go on with the event, 00 end the event, 0B the question cursor.</summary>
    public byte End { get; set; } = 0x01;
    public byte OriginalEnd { get; init; } = 0x01;
    /// <summary>The text repeats earlier text (0A); moved, it is written out in full.</summary>
    public bool HasBackReference { get; init; }
    public List<L2Jump> Jumps { get; } = new();
    /// <summary>Bytes it takes in the ROM as read.</summary>
    public int OriginalLength { get; init; }
    /// <summary>Text written over its old bytes is filled up to this size (0: not).</summary>
    public int PadTo { get; set; }

    public bool IsNew => At < 0;
    public int Op => Bytes.Length > 0 ? Bytes[0] : -1;
    public bool TextChanged => IsText && (Text.Replace("\r", "") != OriginalText || End != OriginalEnd);

    /// <summary>The event doesn't go on with the next step after this one.</summary>
    public bool EndsFlow => IsText ? End == 0x00 : L2EventScript.StopsFlow(Bytes);

    public L2Step Clone() => new()
    {
        At = At, Bytes = (byte[])Bytes.Clone(), OriginalBytes = OriginalBytes, IsText = IsText, TextRaw = TextRaw, Text = Text,
        OriginalText = OriginalText, End = End, OriginalEnd = OriginalEnd, HasBackReference = HasBackReference, OriginalLength = OriginalLength, PadTo = PadTo,
    };
}

/// <summary>
/// A Lufia II event script read into steps: every command and text reachable from its start (following jumps,
/// choices and conditions), in address order, jumps kept as references to steps so the script can be changed and laid
/// out again. Jump operands are u16 offsets from <see cref="JumpBase"/>: the map's event block ("PH") for its events,
/// the start of the bank for a start-up script (<see cref="L2Script"/>).
/// </summary>
public sealed class L2EventScript
{
    public int Start { get; private set; }
    public int JumpBase { get; }
    public List<L2Step> Steps { get; } = new();
    /// <summary>Why the script can't be changed (a jump into the middle of a command, an unknown command), or null.</summary>
    public string? Problem { get; private set; }
    /// <summary>Where the steps were when read (for writing in place).</summary>
    public IReadOnlyList<int> ReadStarts { get; private set; } = Array.Empty<int>();

    private L2EventScript(int start, int jumpBase) { Start = start; JumpBase = jumpBase; }

    public static readonly HashSet<int> TextOpeners = new() { 0x08, 0x13, 0x61, 0x62, 0x63, 0x64, 0x65, 0x66, 0x67, 0x9E, 0x10 };

    /// <summary>Commands after which the event never goes on to the next byte.</summary>
    public static bool StopsFlow(byte[] b) => b.Length == 0 || b[0] switch
    {
        0x00 or 0x42 or 0x84 or 0x16 or 0x5E or 0x83 or 0xA5 or 0x1C or 0x12 or 0x02 or 0x11 or 0x5C or 0x5D => true,
        // a shop, the church, a capsule monster joining and BC end the event too ($80:A350 -> $80:A368 clears the
        // event's state; 73 of the game's shops are followed by a 00 it never reaches)
        0x18 or 0x19 or 0x81 or 0xBC => true,
        0x5B => b.Length > 1 && b[1] is 3 or 6,
        _ => !L2Script.Commands.ContainsKey(b[0]),
    };

    /// <summary>Where the u16 jump operands are in a command's bytes.</summary>
    public static List<int> JumpPositions(byte[] b)
    {
        if (b.Length == 0) return new();
        return b[0] switch
        {
            0x10 when b.Length > 1 => Enumerable.Range(0, b[1]).Select(i => 2 + i * 2).Where(p => p + 1 < b.Length).ToList(),
            0x12 when b.Length > 2 => Enumerable.Range(0, b[2]).Select(i => 3 + i * 2).Where(p => p + 1 < b.Length).ToList(),
            0x15 or 0x6A => new() { 2 },
            0x1C => new() { 1 },
            0x14 => L2Conditions.JumpPositions(b),
            _ => new(),
        };
    }

    /// <summary>Reads the script starting at <paramref name="start"/>.</summary>
    public static L2EventScript Read(RomBuffer rom, int start, int jumpBase)
    {
        var s = new L2EventScript(start, jumpBase);
        s.Decode(rom);
        return s;
    }

    private void Decode(RomBuffer rom)
    {
        var at = new SortedDictionary<int, L2Step>();
        var pending = new List<(L2Step Step, int Target)>();
        var todo = new Stack<int>();
        todo.Push(Start);
        while (todo.Count > 0)
        {
            int p = todo.Pop();
            for (int guard = 0; guard < 4000; guard++)
            {
                if (p <= 0 || p >= rom.Length) { Problem ??= $"A jump leads outside the ROM ({p:X6})."; break; }
                if (at.ContainsKey(p)) break;
                int c = rom.ReadByte(p);
                if (!L2Script.Commands.ContainsKey(c)) { Problem ??= $"Byte {c:X2} at {p:X6} isn't a command."; break; }
                int len = L2Script.Length(rom, p);
                var bytes = rom.ReadBytes(p, len);
                L2Step step;
                if (TextOpeners.Contains(c))
                {
                    string text = L2EventText.Decode(rom, p + len, out int next, out byte end, out bool back);
                    if (end == 0xFF) { Problem ??= $"The text at {p + len:X6} has no end."; break; }
                    step = new L2Step
                    {
                        At = p, Bytes = bytes, OriginalBytes = bytes, IsText = true, Text = text, OriginalText = text, End = end, OriginalEnd = end,
                        TextRaw = rom.ReadBytes(p + len, next - (p + len)), HasBackReference = back, OriginalLength = next - p,
                    };
                }
                else step = new L2Step { At = p, Bytes = bytes, OriginalBytes = bytes, OriginalLength = len };
                at[p] = step;
                foreach (int jp in JumpPositions(bytes))
                {
                    int target = Normalize(JumpBase + (bytes[jp] | bytes[jp + 1] << 8));
                    pending.Add((step, target));
                    todo.Push(target);
                }
                if (step.EndsFlow) break;
                p += step.OriginalLength;
            }
        }
        Steps.AddRange(at.Values);
        ReadStarts = Steps.Select(s => s.At).ToList();
        // jumps become step references; a target that isn't a step start is a jump into the middle of something
        foreach (var (step, target) in pending)
        {
            if (at.TryGetValue(target, out var t)) step.Jumps.Add(new L2Jump { Step = t });
            else
            {
                step.Jumps.Add(new L2Jump { Absolute = target });
                Problem ??= $"A jump leads into the middle of a step ({target:X6}).";
            }
        }
        // steps that overlap
        for (int i = 1; i < Steps.Count; i++)
            if (Steps[i - 1].At + Steps[i - 1].OriginalLength > Steps[i].At) Problem ??= $"Steps overlap at {Steps[i].At:X6}.";
        // the entry first: a script laid out again starts with its first step, and pages treat step 0 as the top. A step
        // above the start (a jump back into code placed before it) would otherwise become the entry. None of the US
        // game's 1322 events has one; an edited one can.
        int entry = Steps.FindIndex(x => x.At == Start);
        if (entry > 0)
        {
            var before = Steps.Take(entry).ToList();
            Steps.RemoveRange(0, entry);
            Steps.AddRange(before);
        }
    }

    /// <summary>A jump past the end of a bank (the game's $80:C102 makes it carry on in the next one): file offsets are
    /// linear, so nothing to do here; kept for clarity.</summary>
    private static int Normalize(int file) => file;

    /// <summary>The step a jump list position points at, by index into <see cref="Steps"/> (-1 outside).</summary>
    public int IndexOf(L2Jump j) => j.Step == null ? -1 : Steps.IndexOf(j.Step);

    /// <summary>Bytes of the whole script laid out from <paramref name="at"/> in step order (jumps filled in), or an error.</summary>
    public (byte[]? Bytes, string? Error) Build(RomBuffer rom, int at)
    {
        var parts = new List<byte[]>();
        var addr = new Dictionary<L2Step, int>();
        int p = at;
        foreach (var s in Steps)
        {
            var b = Encode(rom, s, at: p, sameAddress: p == s.At);
            parts.Add(b);
            addr[s] = p;
            p += b.Length;
        }
        var outp = new byte[p - at];
        int q = 0;
        for (int i = 0; i < Steps.Count; i++)
        {
            var s = Steps[i];
            var b = parts[i];
            var pos = JumpPositions(s.Bytes);
            for (int k = 0; k < pos.Count; k++)
            {
                var j = k < s.Jumps.Count ? s.Jumps[k] : null;
                int target = j?.Step != null ? addr.GetValueOrDefault(j.Step, -1) : j?.Absolute ?? -1;
                if (target < 0) return (null, $"Step {i + 1} ({Describe(s)}) has a jump that leads nowhere: pick its step.");
                int off = target - JumpBase;
                if (off < 0 || off > 0xFFFF || (JumpBase % 0x8000) + off >= 0x10000)
                    return (null, $"Step {i + 1} jumps too far for the game ({target:X6}).");
                b[pos[k]] = (byte)off;
                b[pos[k] + 1] = (byte)(off >> 8);
            }
            Array.Copy(b, 0, outp, q, b.Length);
            q += b.Length;
        }
        return (outp, null);
    }

    private static string Describe(L2Step s) => L2EventCommands.Find(s.Op)?.Name ?? $"command {s.Op:X2}";

    /// <summary>One step's bytes (jump operands as they are; <see cref="Build"/> fills them in).</summary>
    public static byte[] Encode(RomBuffer rom, L2Step s, int at, bool sameAddress)
    {
        if (!s.IsText) return (byte[])s.Bytes.Clone();
        byte[] text;
        if (!s.TextChanged && (sameAddress && s.Bytes.Length == s.OriginalBytes.Length || !s.HasBackReference))
            text = s.TextRaw.Take(s.TextRaw.Length - 1).ToArray();
        else text = L2EventText.Encode(rom, s.Text.Replace("\r", ""));
        // written over its old bytes, shorter text is filled up with pauses of no time (04 80) and a space
        int fill = sameAddress && s.PadTo > 0 ? s.PadTo - (s.Bytes.Length + text.Length + 1) : 0;
        if (fill > 0)
        {
            var t = text.ToList();
            if (fill % 2 == 1) t.Add((byte)' ');
            for (int k = 0; k < fill / 2; k++) { t.Add(0x04); t.Add(0x80); }
            text = t.ToArray();
        }
        var b = new byte[s.Bytes.Length + text.Length + 1];
        s.Bytes.CopyTo(b, 0);
        text.CopyTo(b, s.Bytes.Length);
        b[^1] = s.End;
        return b;
    }

    /// <summary>The step's size once written.</summary>
    public static int SizeOf(RomBuffer rom, L2Step s, bool sameAddress) => Encode(rom, s, 0, sameAddress).Length;

    /// <summary>True when every step keeps its place and size (then it can be written over the old bytes).</summary>
    public bool FitsInPlace(RomBuffer rom, IReadOnlyList<int> originalStarts)
    {
        foreach (var s in Steps) s.PadTo = 0;
        if (Steps.Count != originalStarts.Count) return false;
        for (int i = 0; i < Steps.Count; i++)
        {
            var s = Steps[i];
            if (s.At != originalStarts[i] || s.IsNew) return false;
            int size = SizeOf(rom, s, true);
            if (size != s.OriginalLength && !(s.IsText && size < s.OriginalLength)) return false;
        }
        foreach (var s in Steps) if (s.IsText) s.PadTo = s.OriginalLength;
        return true;
    }

    /// <summary>The steps written over their old bytes (only after <see cref="FitsInPlace"/>): every step whose bytes
    /// change, with its new bytes; or an error.</summary>
    public (List<(L2Step Step, byte[] Bytes)>? Changes, string? Error) InPlaceChanges(RomBuffer rom)
    {
        var changes = new List<(L2Step, byte[])>();
        foreach (var run in Runs())
        {
            var (bytes, error) = Sub(run).Build(rom, run[0].At);
            if (error != null) return (null, error);
            int q = 0;
            foreach (var s in run)
            {
                var b = bytes!.AsSpan(q, s.OriginalLength).ToArray();
                if (!b.AsSpan().SequenceEqual(rom.ReadBytes(s.At, s.OriginalLength))) changes.Add((s, b));
                q += s.OriginalLength;
            }
        }
        return (changes, null);
    }

    /// <summary>Runs of steps that sit back to back in the ROM.</summary>
    private List<List<L2Step>> Runs()
    {
        var runs = new List<List<L2Step>>();
        foreach (var s in Steps)
        {
            if (runs.Count > 0 && runs[^1][^1] is var last && last.At + last.OriginalLength == s.At) runs[^1].Add(s);
            else runs.Add(new List<L2Step> { s });
        }
        return runs;
    }

    /// <summary>A script of some of the steps (jumps to the others become fixed addresses).</summary>
    private L2EventScript Sub(List<L2Step> steps)
    {
        var sub = new L2EventScript(steps[0].At, JumpBase);
        var set = steps.ToHashSet();
        foreach (var s in steps)
        {
            var c = s.Clone();
            foreach (var j in s.Jumps)
                c.Jumps.Add(j.Step != null && !set.Contains(j.Step) ? new L2Jump { Absolute = j.Step.At } : new L2Jump { Step = j.Step, Absolute = j.Absolute });
            sub.Steps.Add(c);
        }
        // the clones' jumps still point at the original steps inside the run: map them
        var map = steps.Zip(sub.Steps).ToDictionary(p => p.First, p => p.Second);
        foreach (var c in sub.Steps) foreach (var j in c.Jumps) if (j.Step != null) j.Step = map[j.Step];
        return sub;
    }

    /// <summary>The same steps, with jumps counted from another base (the script is about to be written elsewhere).</summary>
    public L2EventScript WithJumpBase(int jumpBase)
    {
        var s = new L2EventScript(Start, jumpBase) { Problem = Problem, ReadStarts = ReadStarts };
        s.Steps.AddRange(Steps);
        return s;
    }

    /// <summary>A new, empty script (one "End" step) laid out later.</summary>
    public static L2EventScript New(int jumpBase)
    {
        var s = new L2EventScript(-1, jumpBase);
        s.Steps.Add(new L2Step { Bytes = new byte[] { 0x00 } });
        return s;
    }

    /// <summary>The editable state of every step (for undo in the editor).</summary>
    public sealed class Snapshot
    {
        internal List<(L2Step Step, byte[] Bytes, string Text, byte End, (L2Step? Step, int Absolute)[] Jumps)> Items { get; } = new();
    }

    public Snapshot TakeSnapshot()
    {
        var s = new Snapshot();
        foreach (var st in Steps) s.Items.Add((st, (byte[])st.Bytes.Clone(), st.Text, st.End, st.Jumps.Select(j => (j.Step, j.Absolute)).ToArray()));
        return s;
    }

    public void Restore(Snapshot s)
    {
        Steps.Clear();
        foreach (var (step, bytes, text, end, jumps) in s.Items)
        {
            step.Bytes = (byte[])bytes.Clone();
            step.Text = text;
            step.End = end;
            step.Jumps.Clear();
            foreach (var (t, a) in jumps) step.Jumps.Add(new L2Jump { Step = t, Absolute = a });
            Steps.Add(step);
        }
    }

    /// <summary>Same steps, order, bytes, text and jumps.</summary>
    public static bool Same(Snapshot a, Snapshot b) =>
        a.Items.Count == b.Items.Count && a.Items.Zip(b.Items).All(p =>
            ReferenceEquals(p.First.Step, p.Second.Step) && p.First.Bytes.AsSpan().SequenceEqual(p.Second.Bytes) && p.First.Text == p.Second.Text &&
            p.First.End == p.Second.End && p.First.Jumps.Length == p.Second.Jumps.Length &&
            p.First.Jumps.Zip(p.Second.Jumps).All(j => ReferenceEquals(j.First.Step, j.Second.Step) && j.First.Absolute == j.Second.Absolute));
}
