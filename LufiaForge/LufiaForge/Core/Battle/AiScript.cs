namespace LufiaForge.Core.Battle;

/// <summary>
/// A monster's AI script as a list of steps that can be edited: "go to" commands point at a step (not at an offset),
/// so steps can be added, removed and moved. Built back into bytes in step order (jump words counted from the
/// monster's record, as the game wants) and written in place when it fits, else in the free space of bank 0.
/// </summary>
public static class AiScript
{
    public sealed class Step
    {
        public int Op;
        public byte[] Args = Array.Empty<byte>();
        /// <summary>The step a "go to" lands on (for commands with <see cref="MonsterAi.JumpArg"/> >= 0).</summary>
        public Step? Target;
        public int Length => 1 + Args.Length;
    }

    /// <summary>The monster's script as steps, in the order they are in the ROM (empty when it has none).</summary>
    public static List<Step> Read(RomBuffer rom, int record)
    {
        var cmds = MonsterAi.Decode(rom, record).Where(c => c.Op == 0 || MonsterAi.ArgCount(c.Op) >= 0 && c.Text != $"Unknown command {c.Op:X2}")
                            .OrderBy(c => c.Offset).ToList();
        var steps = cmds.Select(c => new Step { Op = c.Op, Args = (byte[])c.Args.Clone() }).ToList();
        var at = cmds.Select((c, i) => (c.Offset, i)).GroupBy(t => t.Offset).ToDictionary(g => g.Key, g => g.First().i);
        for (int i = 0; i < cmds.Count; i++)
            if (cmds[i].Jump is int j && at.TryGetValue(j, out int k)) steps[i].Target = steps[k];
        return steps;
    }

    /// <summary>The script's bytes for a start at record offset <paramref name="start"/>.</summary>
    public static byte[] Build(IReadOnlyList<Step> steps, int start)
    {
        var offsets = new Dictionary<Step, int>();
        int p = start;
        foreach (var s in steps) { offsets[s] = p; p += s.Length; }
        var b = new List<byte>();
        foreach (var s in steps)
        {
            var args = (byte[])s.Args.Clone();
            int ja = MonsterAi.JumpArg(s.Op);
            if (ja >= 0 && args.Length >= ja + 2)
            {
                if (s.Target == null || !offsets.TryGetValue(s.Target, out int to))
                    throw new InvalidOperationException($"A \"go to\" step ({MonsterAi.Name(s.Op)}) has no step to go to.");
                to &= 0xFFFF;
                args[ja] = (byte)to; args[ja + 1] = (byte)(to >> 8);
            }
            b.Add((byte)s.Op);
            b.AddRange(args);
        }
        return b.ToArray();
    }

    public static int Length(IReadOnlyList<Step> steps) => steps.Sum(s => s.Length);

    public static bool UsesNewCommands(IReadOnlyList<Step> steps) => steps.Any(s => MonsterAi.IsNew(s.Op));

    /// <summary>Bytes free for monster scripts in bank 0 (not counting this monster's own script).</summary>
    public static int FreeSpace(RomBuffer rom, int monster)
    {
        int best = 0;
        for (int len = 8; len <= 0x800; len += 8)
            if (MonsterTable.FindSpace(rom, len, MonsterTable.ScriptAreas, skipScriptOf: monster) >= 0) best = len; else break;
        return best;
    }

    /// <summary>
    /// Writes monster <paramref name="id"/>'s script: in place when it fits where the old one was, otherwise in free
    /// space in bank 0 (scripts must be in bank 0). Adds the new AI commands to the ROM when the script uses them.
    /// Returns where it went (a file offset).
    /// </summary>
    public static int Write(RomBuffer rom, int id, IReadOnlyList<Step> steps)
    {
        if (steps.Count == 0)
        {
            rom.WriteUInt16Le(MonsterTable.RecordOffset(rom, id) + 0x22, 0);   // no script: it just attacks
            return 0;
        }
        if (UsesNewCommands(steps)) AiCommandsPatch.Apply(rom);
        int record = MonsterTable.RecordOffset(rom, id);
        var (oldStart, oldLen) = MonsterAi.ScriptRange(rom, record);
        int len = Length(steps);
        int at;
        // in place when it fits and no other monster uses the same bytes
        if (oldLen >= len && !MonsterTable.Overlaps(rom, MonsterAi.At(record, oldStart), oldLen, id)) at = MonsterAi.At(record, oldStart);
        else
        {
            at = MonsterTable.FindSpace(rom, len, MonsterTable.ScriptAreas, skipScriptOf: id);
            if (at < 0) throw new InvalidOperationException($"No room left for a {len}-byte script: monster scripts must be in bank 0, which has {FreeSpace(rom, id)} bytes free. Make the script shorter.");
        }
        int rel = (at - record) & 0xFFFF;
        rom.WriteBytes(at, Build(steps, rel));
        if (oldLen > len && at == MonsterAi.At(record, oldStart)) rom.WriteBytes(at + len, Enumerable.Repeat((byte)0x00, oldLen - len).ToArray());
        rom.WriteUInt16Le(record + 0x22, (ushort)rel);
        return at;
    }
}
