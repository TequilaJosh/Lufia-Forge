namespace LufiaForge.Core.Lufia2;

// where a jump of a step goes: another step of the script, or a fixed place outside it
public sealed class L2Jump
{
    public L2Step? Step { get; set; }
    // file offset when the target isn't a step of the script (-1 = none yet)
    public int Absolute { get; set; } = -1;
}

// one step of an event: a command, or text with the command that opens it (a speaker, a narration box, a question)
public sealed class L2Step
{
    // file offset in the rom (-1 for a step added in the editor)
    public int At { get; init; } = -1;
    // the command bytes (for text, the opening command). jump operands get filled in when written
    public byte[] Bytes { get; set; } = Array.Empty<byte>();
    public byte[] OriginalBytes { get; init; } = Array.Empty<byte>();
    public bool IsText { get; init; }
    // the text as read (end code included), written back as is while the text and its place stay the same
    public byte[] TextRaw { get; init; } = Array.Empty<byte>();
    public string Text { get; set; } = "";
    public string OriginalText { get; init; } = "";
    // the code ending the text: 01 go on with the event, 00 end the event, 0B the question cursor
    public byte End { get; set; } = 0x01;
    public byte OriginalEnd { get; init; } = 0x01;
    // the text repeats earlier text (0A). once moved it gets written out in full
    public bool HasBackReference { get; init; }
    public List<L2Jump> Jumps { get; } = new();
    // bytes it takes in the rom as read
    public int OriginalLength { get; init; }
    // text written over its old bytes gets filled up to this size (0 = not)
    public int PadTo { get; set; }

    public bool IsNew => At < 0;
    public int Op => Bytes.Length > 0 ? Bytes[0] : -1;
    public bool TextChanged => IsText && (Text.Replace("\r", "") != OriginalText || End != OriginalEnd);

    // the event doesn't go on with the next step after this one
    public bool EndsFlow => IsText ? End == 0x00 : L2EventScript.StopsFlow(Bytes);

    public L2Step Clone() => new()
    {
        At = At, Bytes = (byte[])Bytes.Clone(), OriginalBytes = OriginalBytes, IsText = IsText, TextRaw = TextRaw, Text = Text,
        OriginalText = OriginalText, End = End, OriginalEnd = OriginalEnd, HasBackReference = HasBackReference, OriginalLength = OriginalLength, PadTo = PadTo,
    };
}

// a lufia 2 event script read into steps: every command and text reachable from its start (following jumps, choices
// and conditions), in address order, with jumps kept as references to steps so the script can be changed and laid out
// again. jump operands are u16 offsets from JumpBase: the map's event block ("PH") for its events, the start of the
// bank for a start-up script (L2Script)
public sealed class L2EventScript
{
    public int Start { get; private set; }
    public int JumpBase { get; }
    public List<L2Step> Steps { get; } = new();
    // why the script can't be changed (a jump into the middle of a command, an unknown command), or null
    public string? Problem { get; private set; }
    // where the steps were when read (for writing in place)
    public IReadOnlyList<int> ReadStarts { get; private set; } = Array.Empty<int>();

    private L2EventScript(int start, int jumpBase) { Start = start; JumpBase = jumpBase; }

    public static readonly HashSet<int> TextOpeners = new() { 0x08, 0x13, 0x61, 0x62, 0x63, 0x64, 0x65, 0x66, 0x67, 0x9E, 0x10 };

    // commands after which the event never goes on to the next byte
    public static bool StopsFlow(byte[] bytes) => bytes.Length == 0 || bytes[0] switch
    {
        0x00 or 0x42 or 0x84 or 0x16 or 0x5E or 0x83 or 0xA5 or 0x1C or 0x12 or 0x02 or 0x11 or 0x5C or 0x5D => true,
        // a shop, the church, a capsule monster joining and BC end the event too ($80:A350 -> $80:A368 clears the
        // event's state, and 73 of the game's shops are followed by a 00 it never reaches)
        0x18 or 0x19 or 0x81 or 0xBC => true,
        0x5B => bytes.Length > 1 && bytes[1] is 3 or 6,
        _ => !L2Script.Commands.ContainsKey(bytes[0]),
    };

    // where the u16 jump operands are in a command's bytes
    public static List<int> JumpPositions(byte[] bytes)
    {
        if (bytes.Length == 0) return new();
        return bytes[0] switch
        {
            0x10 when bytes.Length > 1 => Enumerable.Range(0, bytes[1]).Select(i => 2 + i * 2).Where(p => p + 1 < bytes.Length).ToList(),
            0x12 when bytes.Length > 2 => Enumerable.Range(0, bytes[2]).Select(i => 3 + i * 2).Where(p => p + 1 < bytes.Length).ToList(),
            0x15 or 0x6A or 0xCF or 0xD0 => new() { 2 },
            0x1C => new() { 1 },
            0x14 => L2Conditions.JumpPositions(bytes),
            _ => new(),
        };
    }

    // reads the script starting at start
    public static L2EventScript Read(RomBuffer rom, int start, int jumpBase)
    {
        var script = new L2EventScript(start, jumpBase);
        script.Decode(rom);
        return script;
    }

    private void Decode(RomBuffer rom)
    {
        var byAddress = new SortedDictionary<int, L2Step>();
        var pending = new List<(L2Step Step, int Target)>();
        var todo = new Stack<int>();
        todo.Push(Start);
        while (todo.Count > 0)
        {
            int position = todo.Pop();
            for (int guard = 0; guard < 4000; guard++)
            {
                if (position <= 0 || position >= rom.Length) { Problem ??= $"A jump leads outside the ROM ({position:X6})."; break; }
                if (byAddress.ContainsKey(position)) break;
                int op = rom.ReadByte(position);
                if (!L2Script.Commands.ContainsKey(op)) { Problem ??= $"Byte {op:X2} at {position:X6} isn't a command."; break; }
                int length = L2Script.Length(rom, position);
                var bytes = rom.ReadBytes(position, length);
                L2Step step;
                if (TextOpeners.Contains(op))
                {
                    string text = L2EventText.Decode(rom, position + length, out int next, out byte end, out bool back);
                    if (end == 0xFF) { Problem ??= $"The text at {position + length:X6} has no end."; break; }
                    step = new L2Step
                    {
                        At = position, Bytes = bytes, OriginalBytes = bytes, IsText = true, Text = text, OriginalText = text, End = end, OriginalEnd = end,
                        TextRaw = rom.ReadBytes(position + length, next - (position + length)), HasBackReference = back, OriginalLength = next - position,
                    };
                }
                else step = new L2Step { At = position, Bytes = bytes, OriginalBytes = bytes, OriginalLength = length };
                byAddress[position] = step;
                foreach (int jumpAt in JumpPositions(bytes))
                {
                    int target = Normalize(JumpBase + (bytes[jumpAt] | bytes[jumpAt + 1] << 8));
                    pending.Add((step, target));
                    todo.Push(target);
                }
                if (step.EndsFlow) break;
                position += step.OriginalLength;
            }
        }
        Steps.AddRange(byAddress.Values);
        ReadStarts = Steps.Select(s => s.At).ToList();
        // jumps become step references. a target that isn't a step start is a jump into the middle of something
        foreach (var (step, target) in pending)
        {
            if (byAddress.TryGetValue(target, out var targetStep)) step.Jumps.Add(new L2Jump { Step = targetStep });
            else
            {
                step.Jumps.Add(new L2Jump { Absolute = target });
                Problem ??= $"A jump leads into the middle of a step ({target:X6}).";
            }
        }
        // steps that overlap
        for (int i = 1; i < Steps.Count; i++)
            if (Steps[i - 1].At + Steps[i - 1].OriginalLength > Steps[i].At) Problem ??= $"Steps overlap at {Steps[i].At:X6}.";
        // the entry first: a script laid out again starts with its first step, and pages treat step 0 as the top. a
        // step above the start (a jump back into code placed before it) would otherwise become the entry. none of the
        // US game's 1322 events has one, an edited one can
        int entry = Steps.FindIndex(x => x.At == Start);
        if (entry > 0)
        {
            var before = Steps.Take(entry).ToList();
            Steps.RemoveRange(0, entry);
            Steps.AddRange(before);
        }
    }

    // a jump past the end of a bank (the game's $80:C102 makes it carry on in the next one): file offsets are linear,
    // so nothing to do here, kept for clarity
    private static int Normalize(int file) => file;

    // the step a jump list position points at, by index into Steps (-1 outside)
    public int IndexOf(L2Jump jump) => jump.Step == null ? -1 : Steps.IndexOf(jump.Step);

    // bytes of the whole script laid out from at in step order (jumps filled in), or an error
    public (byte[]? Bytes, string? Error) Build(RomBuffer rom, int at)
    {
        var parts = new List<byte[]>();
        var addresses = new Dictionary<L2Step, int>();
        int position = at;
        foreach (var step in Steps)
        {
            var bytes = Encode(rom, step, at: position, sameAddress: position == step.At);
            parts.Add(bytes);
            addresses[step] = position;
            position += bytes.Length;
        }
        var output = new byte[position - at];
        int outputAt = 0;
        for (int i = 0; i < Steps.Count; i++)
        {
            var step = Steps[i];
            var bytes = parts[i];
            var jumpPositions = JumpPositions(step.Bytes);
            for (int k = 0; k < jumpPositions.Count; k++)
            {
                var jump = k < step.Jumps.Count ? step.Jumps[k] : null;
                int target = jump?.Step != null ? addresses.GetValueOrDefault(jump.Step, -1) : jump?.Absolute ?? -1;
                if (target < 0) return (null, $"Step {i + 1} ({Describe(step)}) has a jump that leads nowhere: pick its step.");
                int offset = target - JumpBase;
                if (offset < 0 || offset > 0xFFFF || (JumpBase % 0x8000) + offset >= 0x10000)
                    return (null, $"Step {i + 1} jumps too far for the game ({target:X6}).");
                bytes[jumpPositions[k]] = (byte)offset;
                bytes[jumpPositions[k] + 1] = (byte)(offset >> 8);
            }
            Array.Copy(bytes, 0, output, outputAt, bytes.Length);
            outputAt += bytes.Length;
        }
        return (output, null);
    }

    private static string Describe(L2Step step) => L2EventCommands.Find(step.Op)?.Name ?? $"command {step.Op:X2}";

    // one step's bytes (jump operands as they are, Build fills them in)
    public static byte[] Encode(RomBuffer rom, L2Step step, int at, bool sameAddress)
    {
        if (!step.IsText) return (byte[])step.Bytes.Clone();
        byte[] text;
        if (!step.TextChanged && (sameAddress && step.Bytes.Length == step.OriginalBytes.Length || !step.HasBackReference))
            text = step.TextRaw.Take(step.TextRaw.Length - 1).ToArray();
        else text = L2EventText.Encode(rom, step.Text.Replace("\r", ""));
        // written over its old bytes, shorter text gets filled up with pauses of no time (04 80) and a space
        int fill = sameAddress && step.PadTo > 0 ? step.PadTo - (step.Bytes.Length + text.Length + 1) : 0;
        if (fill > 0)
        {
            var padded = text.ToList();
            if (fill % 2 == 1) padded.Add((byte)' ');
            for (int k = 0; k < fill / 2; k++) { padded.Add(0x04); padded.Add(0x80); }
            text = padded.ToArray();
        }
        var bytes = new byte[step.Bytes.Length + text.Length + 1];
        step.Bytes.CopyTo(bytes, 0);
        text.CopyTo(bytes, step.Bytes.Length);
        bytes[^1] = step.End;
        return bytes;
    }

    // the step's size once written
    public static int SizeOf(RomBuffer rom, L2Step step, bool sameAddress) => Encode(rom, step, 0, sameAddress).Length;

    // true if every step keeps its place and size (then it can be written over the old bytes)
    public bool FitsInPlace(RomBuffer rom, IReadOnlyList<int> originalStarts)
    {
        foreach (var step in Steps) step.PadTo = 0;
        if (Steps.Count != originalStarts.Count) return false;
        for (int i = 0; i < Steps.Count; i++)
        {
            var step = Steps[i];
            if (step.At != originalStarts[i] || step.IsNew) return false;
            int size = SizeOf(rom, step, true);
            if (size != step.OriginalLength && !(step.IsText && size < step.OriginalLength)) return false;
        }
        foreach (var step in Steps) if (step.IsText) step.PadTo = step.OriginalLength;
        return true;
    }

    // the steps written over their old bytes (only after FitsInPlace): every step whose bytes change, with its new
    // bytes, or an error
    public (List<(L2Step Step, byte[] Bytes)>? Changes, string? Error) InPlaceChanges(RomBuffer rom)
    {
        var changes = new List<(L2Step, byte[])>();
        foreach (var run in Runs())
        {
            var (bytes, error) = Sub(run).Build(rom, run[0].At);
            if (error != null) return (null, error);
            int offset = 0;
            foreach (var step in run)
            {
                var stepBytes = bytes!.AsSpan(offset, step.OriginalLength).ToArray();
                if (!stepBytes.AsSpan().SequenceEqual(rom.ReadBytes(step.At, step.OriginalLength))) changes.Add((step, stepBytes));
                offset += step.OriginalLength;
            }
        }
        return (changes, null);
    }

    // runs of steps that sit back to back in the rom
    private List<List<L2Step>> Runs()
    {
        var runs = new List<List<L2Step>>();
        foreach (var step in Steps)
        {
            if (runs.Count > 0 && runs[^1][^1] is var last && last.At + last.OriginalLength == step.At) runs[^1].Add(step);
            else runs.Add(new List<L2Step> { step });
        }
        return runs;
    }

    // a script of some of the steps (jumps to the others become fixed addresses)
    private L2EventScript Sub(List<L2Step> steps)
    {
        var sub = new L2EventScript(steps[0].At, JumpBase);
        var set = steps.ToHashSet();
        foreach (var step in steps)
        {
            var clone = step.Clone();
            foreach (var jump in step.Jumps)
                clone.Jumps.Add(jump.Step != null && !set.Contains(jump.Step) ? new L2Jump { Absolute = jump.Step.At } : new L2Jump { Step = jump.Step, Absolute = jump.Absolute });
            sub.Steps.Add(clone);
        }
        // the clones' jumps still point at the original steps inside the run, so map them
        var map = steps.Zip(sub.Steps).ToDictionary(p => p.First, p => p.Second);
        foreach (var clone in sub.Steps) foreach (var jump in clone.Jumps) if (jump.Step != null) jump.Step = map[jump.Step];
        return sub;
    }

    // the same steps, with jumps counted from another base (the script is about to be written somewhere else)
    public L2EventScript WithJumpBase(int jumpBase)
    {
        var script = new L2EventScript(Start, jumpBase) { Problem = Problem, ReadStarts = ReadStarts };
        script.Steps.AddRange(Steps);
        return script;
    }

    // a copy of the script whose steps belong nowhere in a rom (as if added in the editor): laid out anywhere, even at
    // the address it was read from, nothing is taken for the old bytes there (text repeating earlier text with 0A gets
    // written out in full). for copying a script into another rom (merging projects)
    public L2EventScript Detached()
    {
        var script = new L2EventScript(-1, JumpBase) { Problem = Problem };
        var copy = new Dictionary<L2Step, L2Step>();
        foreach (var step in Steps)
            copy[step] = new L2Step
            {
                Bytes = (byte[])step.Bytes.Clone(), OriginalBytes = step.OriginalBytes, IsText = step.IsText, TextRaw = step.TextRaw, Text = step.Text,
                OriginalText = step.OriginalText, End = step.End, OriginalEnd = step.OriginalEnd, HasBackReference = step.HasBackReference, OriginalLength = step.OriginalLength,
            };
        foreach (var step in Steps)
        {
            var stepCopy = copy[step];
            foreach (var jump in step.Jumps)
                stepCopy.Jumps.Add(jump.Step != null && copy.TryGetValue(jump.Step, out var target) ? new L2Jump { Step = target } : new L2Jump { Absolute = jump.Absolute });
            script.Steps.Add(stepCopy);
        }
        return script;
    }

    // a new, empty script (one "End" step) laid out later
    public static L2EventScript New(int jumpBase)
    {
        var script = new L2EventScript(-1, jumpBase);
        script.Steps.Add(new L2Step { Bytes = new byte[] { 0x00 } });
        return script;
    }

    // the editable state of every step (for undo in the editor)
    public sealed class Snapshot
    {
        internal List<(L2Step Step, byte[] Bytes, string Text, byte End, (L2Step? Step, int Absolute)[] Jumps)> Items { get; } = new();
    }

    public Snapshot TakeSnapshot()
    {
        var snapshot = new Snapshot();
        foreach (var step in Steps) snapshot.Items.Add((step, (byte[])step.Bytes.Clone(), step.Text, step.End, step.Jumps.Select(j => (j.Step, j.Absolute)).ToArray()));
        return snapshot;
    }

    public void Restore(Snapshot snapshot)
    {
        Steps.Clear();
        foreach (var (step, bytes, text, end, jumps) in snapshot.Items)
        {
            step.Bytes = (byte[])bytes.Clone();
            step.Text = text;
            step.End = end;
            step.Jumps.Clear();
            foreach (var (target, absolute) in jumps) step.Jumps.Add(new L2Jump { Step = target, Absolute = absolute });
            Steps.Add(step);
        }
    }

    // same steps, order, bytes, text and jumps
    public static bool Same(Snapshot a, Snapshot b) =>
        a.Items.Count == b.Items.Count && a.Items.Zip(b.Items).All(p =>
            ReferenceEquals(p.First.Step, p.Second.Step) && p.First.Bytes.AsSpan().SequenceEqual(p.Second.Bytes) && p.First.Text == p.Second.Text &&
            p.First.End == p.Second.End && p.First.Jumps.Length == p.Second.Jumps.Length &&
            p.First.Jumps.Zip(p.Second.Jumps).All(j => ReferenceEquals(j.First.Step, j.Second.Step) && j.First.Absolute == j.Second.Absolute));
}
