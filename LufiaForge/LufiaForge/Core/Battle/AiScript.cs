namespace LufiaForge.Core.Battle;

// a monster's AI script as a list of steps you can edit. "go to" commands point at a step (not an offset),
// so steps can be added, removed and moved around. gets built back into bytes in step order (jump words counted
// from the monster's record, which is what the game wants) and written in place when it fits, else in the free
// space of bank 0, and when that's used up in Lufia Forge's script space (AiFarScripts: no limit except the
// 511 bytes one script can have)
public static class AiScript
{
    public sealed class Step
    {
        public int Op;
        public byte[] Args = Array.Empty<byte>();
        // the step a "go to" lands on (for commands with MonsterAi.JumpArg >= 0)
        public Step? Target;
        public int Length => 1 + Args.Length;
    }

    // the monster's script as steps, in the order they sit in the rom (empty when it has none)
    public static List<Step> Read(RomBuffer rom, int record)
    {
        var commands = MonsterAi.Decode(rom, record).Where(c => c.Op == 0 || MonsterAi.ArgCount(c.Op) >= 0 && c.Text != $"Unknown command {c.Op:X2}")
                            .OrderBy(c => c.Offset).ToList();
        var steps = commands.Select(c => new Step { Op = c.Op, Args = (byte[])c.Args.Clone() }).ToList();
        var stepAtOffset = commands.Select((c, i) => (c.Offset, i)).GroupBy(t => t.Offset).ToDictionary(g => g.Key, g => g.First().i);
        for (int i = 0; i < commands.Count; i++)
            if (commands[i].Jump is int jumpOffset && stepAtOffset.TryGetValue(jumpOffset, out int targetIndex)) steps[i].Target = steps[targetIndex];
        return steps;
    }

    // the script's bytes when it starts at record offset start
    public static byte[] Build(IReadOnlyList<Step> steps, int start)
    {
        var offsets = new Dictionary<Step, int>();
        int position = start;
        foreach (var step in steps) { offsets[step] = position; position += step.Length; }
        var bytes = new List<byte>();
        foreach (var step in steps)
        {
            var args = (byte[])step.Args.Clone();
            int jumpArg = MonsterAi.JumpArg(step.Op);
            if (jumpArg >= 0 && args.Length >= jumpArg + 2)
            {
                if (step.Target == null || !offsets.TryGetValue(step.Target, out int to))
                    throw new InvalidOperationException($"A \"go to\" step ({MonsterAi.Name(step.Op)}) has no step to go to.");
                to &= 0xFFFF;
                args[jumpArg] = (byte)to; args[jumpArg + 1] = (byte)(to >> 8);
            }
            bytes.Add((byte)step.Op);
            bytes.AddRange(args);
        }
        return bytes.ToArray();
    }

    public static int Length(IReadOnlyList<Step> steps) => steps.Sum(s => s.Length);

    public static bool UsesNewCommands(IReadOnlyList<Step> steps) => steps.Any(s => MonsterAi.IsNew(s.Op));

    // bytes free for monster scripts in bank 0 (not counting this monster's own script)
    public static int FreeSpace(RomBuffer rom, int monster)
    {
        var used = MonsterTable.UsedSpace(rom, monster);
        int best = 0;
        for (int length = 8; length <= 0x800; length += 8)
            if (MonsterTable.FindSpace(used, length, MonsterTable.ScriptAreas) >= 0) best = length; else break;
        return best;
    }

    // writes monster id's script: in place when it fits where the old one was, otherwise in free
    // space in bank 0 (scripts have to be in bank 0). adds the new AI commands to the rom if the script uses them.
    // returns where it went (a file offset)
    public static int Write(RomBuffer rom, int id, IReadOnlyList<Step> steps)
    {
        if (steps.Count == 0)
        {
            AiFarScripts.Remove(rom, id);
            rom.WriteUInt16Le(MonsterTable.RecordOffset(rom, id) + 0x22, 0);   // no script = it just attacks
            return 0;
        }
        if (UsesNewCommands(steps)) AiCommandsPatch.Apply(rom);
        int record = MonsterTable.RecordOffset(rom, id);
        // a script that's already in Lufia Forge's space stays there
        if (MonsterAi.HomeOf(rom, record).Far) return AiFarScripts.Save(rom, id, record, Build(steps, 1));
        var (oldStart, oldLen) = MonsterAi.ScriptRange(rom, record);
        int length = Length(steps);
        int scriptOffset;
        // in place when it fits and no other monster uses the same bytes
        if (oldLen >= length && !MonsterTable.Overlaps(rom, MonsterAi.At(record, oldStart), oldLen, id)) scriptOffset = MonsterAi.At(record, oldStart);
        else
        {
            scriptOffset = MonsterTable.FindSpace(rom, length, MonsterTable.ScriptAreas, skipScriptOf: id);
            if (scriptOffset < 0)
            {
                if (!AiFarScripts.CanInstall(rom))
                    throw new InvalidOperationException($"No room left for a {length}-byte script: bank 0 has {FreeSpace(rom, id)} bytes free, and this ROM's AI code was changed by another patch, so scripts can't go to the added space. Make the script shorter.");
                return AiFarScripts.Save(rom, id, record, Build(steps, 1));
            }
        }
        int relativeStart = (scriptOffset - record) & 0xFFFF;
        rom.WriteBytes(scriptOffset, Build(steps, relativeStart));
        if (oldLen > length && scriptOffset == MonsterAi.At(record, oldStart)) rom.WriteBytes(scriptOffset + length, Enumerable.Repeat((byte)0x00, oldLen - length).ToArray());
        rom.WriteUInt16Le(record + 0x22, (ushort)relativeStart);
        return scriptOffset;
    }
}
