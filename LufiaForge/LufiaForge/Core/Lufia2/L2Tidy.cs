using System.IO;
using System.Text;

namespace LufiaForge.Core.Lufia2;

// "Tidy the hack's data" (2026-10-27): the lufia 2 fan hacks (Spekkio, Kureji Lufia) keep monster battle scripts in
// the unused end of other monsters' name fields and inside other records, and their spell scripts run on into the
// spell table's spare entries (Frue, Spekkio, Kureji, Foomy). we read and edit them where they are, but a name can't
// grow over a script and the data is hard to take apart. tidying moves them into clean space and points the game at
// the new places:
// - monster scripts that use bytes of a name field or of a record go to one block in the monsters' own bank (a
//   script's offset is added to its record in that bank, so it can't leave it): the largest empty run there (all 00
//   or FF, at least 4 KB, the same rule our added space follows. the fan hacks have about 4.4 KB of zeros after the
//   monster attack names at $D0:8E35). each script is laid out again from its steps for its own record (the steps a
//   path reaches, endings without jumps that several scripts share get written once since they read the same from
//   any record), and the record's offset (tag 07 its turn, 08 when hit) is rewritten. name field bytes no script uses
//   any more get cleared to 00, so every such name can have 13 letters
// - spell scripts that run into the spell table or another spell's record: the spells move to our spell block with
//   each script right after its spell (L2Magic.Install, which moves the IP attacks and the descriptions too)
// nothing gets written until the tidied rom, built in memory, is proven to read the same: every monster's stats, name,
// drop and both scripts (old and new walked side by side from the bytes, every path, each pair of steps the same
// command and arguments, a "go to" counted as the step it leads to: SameRun), every item, spell, IP attack and IP
// effect with its scripts, every description, shop and monster attack name. and every byte that changed has to be one
// of: a moved script's new bytes in empty space, a rewritten offset, a cleared name byte no script uses, or (spells)
// the code places and space the spell move is known to change
public static class L2Tidy
{
    public sealed class Result
    {
        // the tidied rom, or null (nothing to tidy, or Refused)
        public byte[]? Tidied { get; set; }
        // why it can't be done, or null
        public string? Refused { get; set; }
        // what moves (lines for the preview)
        public List<string> Moves { get; } = new();
        // what becomes possible
        public List<string> Gains { get; } = new();
        // what was proven to read the same
        public List<string> Checked { get; } = new();
        // what stays as it is (and why)
        public List<string> Left { get; } = new();
        public int MonsterScripts { get; set; }
        public int MonsterBytes { get; set; }
        public int NamesFreed { get; set; }
        public int SpellScripts { get; set; }
        public bool NothingToDo => Tidied == null && Refused == null;

        public string Preview()
        {
            var sb = new StringBuilder();
            if (Refused != null) return Refused;
            if (Tidied == null)
            {
                sb.AppendLine("Nothing to tidy: every battle script already sits on its own, outside names and other records.");
                foreach (var line in Left) sb.AppendLine("- " + line);
                return sb.ToString().TrimEnd();
            }
            sb.AppendLine("What moves:");
            foreach (var line in Moves) sb.AppendLine("- " + line);
            if (Gains.Count > 0) { sb.AppendLine(); sb.AppendLine("What it makes possible:"); foreach (var line in Gains) sb.AppendLine("- " + line); }
            if (Left.Count > 0) { sb.AppendLine(); sb.AppendLine("Left where it is:"); foreach (var line in Left) sb.AppendLine("- " + line); }
            sb.AppendLine(); sb.AppendLine("Checked on the tidied ROM before anything is written:");
            foreach (var line in Checked) sb.AppendLine("- " + line);
            return sb.ToString().TrimEnd();
        }
    }

    // monsters

    private sealed record Rec(int At, int End, int NameLength, List<int> Ids);

    // the monster records (each once, in table order of their first id). a record runs to the 00 that ends its tags,
    // its name to its 00 (13 letters if it has none)
    private static List<Rec> Records(RomBuffer rom)
    {
        var byAt = new Dictionary<int, Rec>();
        var list = new List<Rec>();
        for (int i = 0, n = L2Battle.MonsterCountIn(rom); i < n; i++)
        {
            int recordAt = L2Battle.MonsterAt(rom, i);
            if (byAt.TryGetValue(recordAt, out var rec)) { rec.Ids.Add(i); continue; }
            int tag = recordAt + 0x21;
            for (int guard = 0; guard < 16 && rom.ReadByte(tag) != 0; guard++) tag += 3;
            int nameLength = Array.IndexOf(rom.ReadBytes(recordAt, L2Battle.MonsterNameLength), (byte)0);
            rec = new Rec(recordAt, tag + 1, nameLength < 0 ? L2Battle.MonsterNameLength : nameLength, new List<int> { i });
            byAt[recordAt] = rec;
            list.Add(rec);
        }
        return list;
    }

    // where tag is in a record's tag list, or -1
    private static int TagAt(RomBuffer rom, Rec rec, byte tag)
    {
        for (int p = rec.At + 0x21; p < rec.End - 1; p += 3)
            if (rom.ReadByte(p) == tag) return p;
        return -1;
    }

    // the file offsets a script's steps are read from (the way L2AiScripts.Parse reads it)
    private static HashSet<int> Bytes(List<L2AiScripts.Step> steps, int baseAt)
    {
        var offsets = new HashSet<int>();
        foreach (var st in steps)
            if (st.Offset >= 0)
                for (int k = 0; k < st.Length; k++) offsets.Add(L2Rom.InBank(baseAt, st.Offset + k));
        return offsets;
    }

    private sealed record Move(Rec Rec, int Tag, int TagPos, int Start, List<L2AiScripts.Step> Steps);

    // the steps the runner can reach from the first one (in their order). a script read straight on to its furthest
    // jump can hold bytes no path runs (another script's tail, data), and a copy leaves those out
    internal static List<L2AiScripts.Step> Reachable(List<L2AiScripts.Step> steps)
    {
        if (steps.Count == 0) return steps;
        var index = new Dictionary<L2AiScripts.Step, int>(ReferenceEqualityComparer.Instance);
        for (int i = 0; i < steps.Count; i++) index[steps[i]] = i;
        var seen = new bool[steps.Count];
        var todo = new Stack<int>();
        todo.Push(0);
        while (todo.Count > 0)
        {
            int i = todo.Pop();
            if (seen[i]) continue;
            seen[i] = true;
            var step = steps[i];
            foreach (var target in step.Targets) if (target != null && index.TryGetValue(target, out int k)) todo.Push(k);
            if (step.Def?.EndsPath != true && i + 1 < steps.Count) todo.Push(i + 1);
        }
        return steps.Where((s, i) => seen[i]).ToList();
    }

    // every monster script (record, tag 07/08) with its steps, read from the tag's offset. offset 0 (none) is left
    // out, and a script that can't be read comes back with null steps
    private static List<(Rec Rec, int Tag, int TagPos, int Start, List<L2AiScripts.Step>? Steps)> Scripts(RomBuffer rom, List<Rec> recs)
    {
        var scripts = new List<(Rec, int, int, int, List<L2AiScripts.Step>?)>();
        foreach (var rec in recs)
            foreach (byte tag in new byte[] { 7, 8 })
            {
                int tagPos = TagAt(rom, rec, tag);
                if (tagPos < 0 || rom.ReadUInt16Le(tagPos + 1) == 0) continue;
                int start = L2Rom.InBank(rec.At, rom.ReadUInt16Le(tagPos + 1));
                List<L2AiScripts.Step>? steps;
                try { steps = start < 0 ? null : L2AiScripts.Parse(rom, start, rec.At); } catch (InvalidDataException) { steps = null; }
                scripts.Add((rec, tag, tagPos, start, steps));
            }
        return scripts;
    }

    private static string Count(int n, string what) => $"{n} {what}{(n == 1 ? "" : "s")}";

    private static string MonsterName(RomBuffer rom, Rec r) =>
        $"{r.Ids[0]:X2} {L2Rom.Name(rom, r.At, L2Battle.MonsterNameLength)}";

    // moves the monster scripts that use name or record bytes (see the top of the class). returns the bytes it may change
    private static HashSet<int> TidyMonsters(RomBuffer w, Result res)
    {
        var changed = new HashSet<int>();
        var recs = Records(w);
        int table = L2Battle.MonsterTableAt(w), count = L2Battle.MonsterCountIn(w);
        var names = new HashSet<int>();
        var data = new HashSet<int>();
        for (int k = 0; k < count * 2; k++) data.Add(table + k);
        foreach (var r in recs)
        {
            for (int k = 0; k < L2Battle.MonsterNameLength; k++) names.Add(r.At + k);
            for (int k = L2Battle.MonsterNameLength; k < r.End - r.At; k++) data.Add(r.At + k);
        }
        var scripts = Scripts(w, recs);
        var allScriptBytes = new HashSet<int>();
        foreach (var s in scripts) if (s.Steps != null) allScriptBytes.UnionWith(Bytes(s.Steps, s.Rec.At));
        var moves = new List<Move>();
        foreach (var s in scripts)
        {
            string what = $"{MonsterName(w, s.Rec)}'s {(s.Tag == 7 ? "turn" : "when-hit")} script";
            if (s.Steps == null) { res.Left.Add($"{what} can't be read, so it stays where it is."); continue; }
            var scriptBytes = Bytes(s.Steps, s.Rec.At);
            if (!scriptBytes.Overlaps(names) && !scriptBytes.Overlaps(data)) continue;
            if (s.Steps.Any(st => st.OutsideTargets.Any(o => o >= 0)))
            {
                res.Left.Add($"{what} jumps into the middle of a step, so it stays where it is.");
                continue;
            }
            var reach = Reachable(s.Steps);
            moves.Add(new Move(s.Rec, s.Tag, s.TagPos, s.Start, reach));
        }
        if (moves.Count == 0) return changed;

        // the room: the largest empty run in the records' bank that nothing known uses
        int bank = table / 0x8000 * 0x8000;
        var used = new HashSet<int>(data); used.UnionWith(names); used.UnionWith(allScriptBytes);
        int bestStart = -1, bestLen = 0;
        for (int p = bank; p < bank + 0x8000;)
        {
            byte fill = w.ReadByte(p);
            if (fill is not (0x00 or 0xFF) || used.Contains(p)) { p++; continue; }
            int runEnd = p;
            while (runEnd < bank + 0x8000 && w.ReadByte(runEnd) == fill && !used.Contains(runEnd)) runEnd++;
            // a run that ends at our own space mark is our added space
            bool lfSpace = runEnd + 4 <= w.Length && Encoding.ASCII.GetString(w.ReadBytes(runEnd, 4)) == "LF2X";
            if (!lfSpace && runEnd - p > bestLen) { bestStart = p; bestLen = runEnd - p; }
            p = runEnd;
        }
        if (bestLen < 0x1000)
            throw new InvalidOperationException($"The monsters' bank (${L2Rom.FileToSnes(bank) >> 16:X2}) has no empty space for the moved scripts (the largest empty run is {bestLen} bytes; 4 KB is the least Lufia Forge counts as unused).");
        int areaStart = (bestStart + 16 + 15) & ~15, areaEnd = bestStart + bestLen - 16, at = areaStart;

        var placedSame = new Dictionary<(int, int), int>();   // (record, old start) -> new start
        var pieces = new Dictionary<string, int>();          // a piece without jumps -> where it is
        var movedRecs = new HashSet<int>();
        foreach (var move in moves)
        {
            if (!placedSame.TryGetValue((move.Rec.At, move.Start), out int newStart))
            {
                newStart = LayOut(w, move.Steps, move.Rec.At, ref at, areaEnd, pieces);
                placedSame[(move.Rec.At, move.Start)] = newStart;
            }
            w.WriteUInt16Le(move.TagPos + 1, (ushort)L2Rom.OffsetInBank(move.Rec.At, newStart));
            changed.Add(move.TagPos + 1); changed.Add(move.TagPos + 2);
            movedRecs.Add(move.Rec.At);
        }
        for (int p = areaStart; p < at; p++) changed.Add(p);

        // name bytes no script uses any more: 00
        var after = new HashSet<int>();
        foreach (var s in Scripts(w, recs)) if (s.Steps != null) after.UnionWith(Bytes(s.Steps, s.Rec.At));
        int cleared = 0;
        foreach (var rec in recs)
            for (int k = rec.NameLength + 1; k < L2Battle.MonsterNameLength; k++)
            {
                int p = rec.At + k;
                if (!allScriptBytes.Contains(p) || after.Contains(p) || w.ReadByte(p) == 0) continue;
                w.WriteByte(p, 0);
                changed.Add(p);
                cleared++;
            }

        res.MonsterScripts = moves.Count;
        res.MonsterBytes = at - areaStart;
        string from = $"${L2Rom.FileToSnes(areaStart):X6}", to = $"${L2Rom.FileToSnes(at - 1):X6}";
        res.Moves.Add($"Monster battle scripts in name fields or inside records: {Count(moves.Count, "script")} of {Count(movedRecs.Count, "monster record")}. " +
                      $"They move to one block in the monsters' bank ({from}-{to}, {at - areaStart} bytes; endings without jumps " +
                      $"that several scripts share are kept once) and their records point there. {Count(cleared, "name byte")} no script uses any more: cleared to 00.");
        return changed;
    }

    // writes a script for the record at recAt from at on and returns where it starts. it's laid out in pieces that end
    // where a path ends (00, 4F, 43, a "go to"), so nothing runs from one piece into the next. a piece without jumps
    // reads the same from any record, so one already written (by this script or another) gets used again instead of
    // written twice
    internal static int LayOut(RomBuffer w, List<L2AiScripts.Step> steps, int recAt, ref int at, int areaEnd, Dictionary<string, int> pieces)
    {
        var blocks = new List<(int From, int To)>();
        for (int i = 1, blockStart = 0; i <= steps.Count; i++)
            if (i == steps.Count || steps[i - 1].Def?.EndsPath == true) { blocks.Add((blockStart, i)); blockStart = i; }
        var addr = new Dictionary<L2AiScripts.Step, int>(ReferenceEqualityComparer.Instance);
        var write = new List<(int From, int To, int At, bool End)>();
        byte[] Raw(int from, int to) => steps.Skip(from).Take(to - from).SelectMany(s => new[] { s.Op }.Concat(s.Args)).ToArray();
        foreach (var (from, to) in blocks)
        {
            bool ends = steps[to - 1].Def?.EndsPath == true;
            bool pure = ends && steps.Skip(from).Take(to - from).All(s => s.Def?.IsJump != true);
            int length = steps.Skip(from).Take(to - from).Sum(s => s.Length) + (ends ? 0 : 1);
            int place;
            string key = pure ? Convert.ToHexString(Raw(from, to)) : "";
            if (pure && pieces.TryGetValue(key, out place)) { }
            else
            {
                if (at + length > areaEnd)
                    throw new InvalidOperationException($"The moved monster scripts need more room than the monsters' bank has free ({areaEnd - at} bytes left).");
                place = at;
                at += length;
                write.Add((from, to, place, !ends));
                if (pure) pieces[key] = place;
            }
            for (int i = from, p = place; i < to; p += steps[i].Length, i++) addr[steps[i]] = p;
        }
        foreach (var (from, to, place, end) in write)
        {
            var bytes = new List<byte>();
            for (int i = from; i < to; i++)
            {
                var step = steps[i];
                var args = (byte[])step.Args.Clone();
                var def = step.Def!;
                for (int a = 0, j = 0; a < def.Args.Length; a++)
                {
                    if (def.Args[a] != L2AiScripts.Arg.Jump) continue;
                    var target = step.Targets[j++] ?? throw new InvalidOperationException("A \"go to\" has no step to go to.");
                    int offset = L2Rom.OffsetInBank(recAt, addr[target]), k = L2AiScripts.ArgAt(def, a);
                    args[k] = (byte)offset; args[k + 1] = (byte)(offset >> 8);
                }
                bytes.Add(step.Op);
                bytes.AddRange(args);
            }
            if (end) bytes.Add(0);   // a last step that doesn't end the script still stops here
            w.WriteBytes(place, bytes.ToArray());
        }
        return addr[steps[0]];
    }

    // how many letters each record's name can have (by record, placeholders left out)
    private static Dictionary<int, int> NameRooms(RomBuffer rom)
    {
        var used = L2AiScripts.MonsterScriptBytes(rom);
        var rooms = new Dictionary<int, int>();
        foreach (var rec in Records(rom))
        {
            var monster = L2Battle.ReadMonster(rom, rec.Ids[0]);
            if (monster.Unused) continue;
            rooms[rec.At] = L2Battle.NameRoom(rom, monster, used);
        }
        return rooms;
    }

    // spells

    // the spells whose script runs into the spell table or another spell's record
    private static List<int> UntidySpells(RomBuffer rom)
    {
        var list = new List<int>();
        if (L2Magic.BlockA(rom) >= 0) return list;
        int count = L2Battle.SpellCount(rom);
        var recs = Enumerable.Range(0, count).Select(i => L2Battle.SpellOffset(rom, i)).ToList();
        int table = L2Rom.SnesToFile((int)rom.ReadUInt24Le(0xF451));
        int first = recs.Count == 0 ? table : recs.Min();
        for (int i = 0; i < count; i++)
        {
            int scriptOffset = rom.ReadUInt16Le(recs[i] + 0x11);
            if (scriptOffset == 0) continue;
            List<L2AiScripts.Step> steps;
            try { steps = L2AiScripts.Parse(rom, L2Rom.InBank(recs[i], scriptOffset), recs[i]); } catch (InvalidDataException) { continue; }
            var scriptBytes = Bytes(steps, recs[i]);
            if (scriptBytes.Any(p => p >= table && p < first || recs.Any(o => o != recs[i] && p >= o && p < o + 0x13))) list.Add(i);
        }
        return list;
    }

    // checks

    // one step the way the runner reads it at rel (from the base): its command and arguments other than jumps, and
    // where it can go on (each jump, then the next step unless the path ends there), a "go to" counted as the step it
    // leads to
    private static (string Text, int[] Next) StepAt(RomBuffer rom, int baseAt, int rel)
    {
        int At(int offset)
        {
            int file = L2Rom.InBank(baseAt, offset & 0xFFFF);
            return file >= 0 && file < rom.Length ? file : throw new InvalidDataException($"Offset {offset & 0xFFFF:X4} from 0x{baseAt:X6} isn't in the ROM.");
        }
        byte op = rom.ReadByte(At(rel));
        var def = L2AiScripts.Def(op) ?? throw new InvalidDataException($"Unknown command {op:X2} at 0x{At(rel):X6}.");
        var sb = new StringBuilder(op.ToString("X2"));
        var next = new List<int>();
        int argAt = 1;
        foreach (var arg in def.Args)
        {
            int size = L2AiScripts.Size(arg);
            if (arg == L2AiScripts.Arg.Jump) { sb.Append(" j"); next.Add(Resolve(rom, baseAt, rom.ReadByte(At(rel + argAt)) | rom.ReadByte(At(rel + argAt + 1)) << 8)); }
            else for (int q = 0; q < size; q++) sb.Append(' ').Append(rom.ReadByte(At(rel + argAt + q)).ToString("X2"));
            argAt += size;
        }
        if (!def.EndsPath) { sb.Append(" >"); next.Add(Resolve(rom, baseAt, rel + def.Length)); }
        return (sb.ToString(), next.ToArray());
    }

    // where a "go to" (or a chain of them) at rel leads
    private static int Resolve(RomBuffer rom, int baseAt, int rel)
    {
        for (int guard = 0; guard < 64; guard++)
        {
            int file = L2Rom.InBank(baseAt, rel & 0xFFFF);
            if (file < 0 || file + 2 >= rom.Length) throw new InvalidDataException($"Offset {rel & 0xFFFF:X4} from 0x{baseAt:X6} isn't in the ROM.");
            if (rom.ReadByte(file) != 0x03) return rel & 0xFFFF;
            int lowAt = L2Rom.InBank(baseAt, (rel + 1) & 0xFFFF), highAt = L2Rom.InBank(baseAt, (rel + 2) & 0xFFFF);
            rel = rom.ReadByte(lowAt) | rom.ReadByte(highAt) << 8;
        }
        throw new InvalidDataException("A loop of \"go to\"s.");
    }

    // true if two scripts do the same: walked side by side from their starts, every pair of steps reached the same way
    // has the same command and arguments and goes on to pairs that do too (the same steps in the same order on every
    // path, wherever their bytes are and however often a piece is shared). steps = how many steps the first one has
    public static bool SameRun(RomBuffer a, int baseA, int startA, RomBuffer b, int baseB, int startB, out int steps, out string where)
    {
        var seen = new HashSet<(int, int)>();
        var stepsA = new HashSet<int>();
        var todo = new Queue<(int, int, string)>();
        todo.Enqueue((Resolve(a, baseA, L2Rom.OffsetInBank(baseA, startA)), Resolve(b, baseB, L2Rom.OffsetInBank(baseB, startB)), "the start"));
        where = "";
        while (todo.Count > 0)
        {
            var (x, y, path) = todo.Dequeue();
            if (!seen.Add((x, y))) continue;
            if (seen.Count > 200000) throw new InvalidDataException("The script is too big to compare.");
            stepsA.Add(x);
            var sa = StepAt(a, baseA, x); var sb = StepAt(b, baseB, y);
            if (sa.Text != sb.Text || sa.Next.Length != sb.Next.Length)
            {
                where = $"offset {x:X4}: {sa.Text} / {sb.Text} (after {path})";
                steps = stepsA.Count;
                return false;
            }
            for (int k = 0; k < sa.Next.Length; k++) todo.Enqueue((sa.Next[k], sb.Next[k], $"offset {x:X4}"));
        }
        steps = stepsA.Count;
        return true;
    }

    // where a script runs from now: our edited copy, or the game's (base, start). null for none
    private static (int Base, int Start)? Runs(RomBuffer rom, L2AiScripts.Kind kind, int index)
    {
        if (L2AiScripts.Override(rom, kind, index) is int far) return (far, far + 1);
        int at = L2AiScripts.OriginalAt(rom, kind, index);
        return at < 0 ? null : (L2AiScripts.RecordAt(rom, kind, index), at);
    }

    internal sealed class Mismatch(string what) : Exception(what);

    private static void Same<T>(T a, T b, string what)
    {
        if (!EqualityComparer<T>.Default.Equals(a, b)) throw new Mismatch(what);
    }

    private static int SameScript(RomBuffer a, RomBuffer b, (int Base, int Start)? x, (int Base, int Start)? y, string what)
    {
        if (x == null || y == null) { Same(x == null, y == null, what); return 0; }
        if (!SameRun(a, x.Value.Base, x.Value.Start, b, y.Value.Base, y.Value.Start, out int n, out string where))
            throw new Mismatch($"{what} (at {where})");
        return n;
    }

    // proves the tidied rom reads like the old one (throws Mismatch with what's different).
    // moved = for records moved to another bank (L2NewMonsters.MoveHackBank), the new file offset of an old record's
    // byte. every byte of a record (offsets too) then has to read the same there
    internal static void Prove(RomBuffer old, RomBuffer w, Result res, Func<int, int>? moved = null)
    {
        int At(int p) => moved == null || p < 0 ? p : moved(p);
        // monsters
        int mn = L2Battle.MonsterCountIn(old), mScripts = 0, mSteps = 0;
        Same(mn, L2Battle.MonsterCountIn(w), "the number of monsters");
        var oldRecs = Records(old).ToDictionary(r => r.At);
        for (int i = 0; i < mn; i++)
        {
            var a = L2Battle.ReadMonster(old, i); var b = L2Battle.ReadMonster(w, i);
            string m = $"monster {i:X2} ({a.Name})";
            Same(At(a.Offset), b.Offset, m + ": where its record is");
            Same((a.Name, a.Level, a.Sprite, a.Palette, a.Hp, a.Mp, a.Atp, a.Dfp), (b.Name, b.Level, b.Sprite, b.Palette, b.Hp, b.Mp, b.Atp, b.Dfp), m + ": its name or stats");
            Same((a.Agl, a.Int, a.Gut, a.Mgr, a.Exp, a.Gold, At(a.DropAt), a.DropItem, a.DropChance, a.SameAs, a.Unused),
                 (b.Agl, b.Int, b.Gut, b.Mgr, b.Exp, b.Gold, b.DropAt, b.DropItem, b.DropChance, b.SameAs, b.Unused), m + ": its stats or drop");
            var r = oldRecs[a.Offset];
            Same(Convert.ToHexString(old.ReadBytes(r.At, Math.Min(r.NameLength + 1, L2Battle.MonsterNameLength))),
                 Convert.ToHexString(w.ReadBytes(At(r.At), Math.Min(r.NameLength + 1, L2Battle.MonsterNameLength))), m + ": its name");
            for (int p = r.At + L2Battle.MonsterNameLength; p < r.End; p++)
            {
                bool operand = moved == null && p >= r.At + 0x21 && (p - r.At - 0x21) % 3 != 0 && old.ReadByte(p - (p - r.At - 0x21) % 3) is 7 or 8;
                if (!operand) Same(old.ReadByte(p), w.ReadByte(At(p)), m + ": its record");
            }
            foreach (var k in new[] { L2AiScripts.Kind.MonsterAttack, L2AiScripts.Kind.MonsterDefense })
            {
                mSteps += SameScript(old, w, Runs(old, k, i), Runs(w, k, i), $"{m}: its {(k == L2AiScripts.Kind.MonsterAttack ? "turn" : "when-hit")} script");
                if (Runs(old, k, i) != null) mScripts++;
            }
        }
        res.Checked.Add($"{mn} monsters: name, stats, drop and record the same; {mScripts} battle scripts ({mSteps} steps) do the same step by step");

        // capsule monsters
        int cScripts = 0;
        for (int i = 0; i < L2Capsules.Count; i++)
            foreach (var k in new[] { L2AiScripts.Kind.CapsuleAi, L2AiScripts.Kind.CapsuleDefense })
            {
                SameScript(old, w, Runs(old, k, i), Runs(w, k, i), $"capsule monster form {i}'s script");
                if (Runs(old, k, i) != null) cScripts++;
            }

        // items
        int itn = L2Battle.ItemCountIn(old), iScripts = 0;
        Same(itn, L2Battle.ItemCountIn(w), "the number of items");
        for (int i = 0; i < itn; i++)
        {
            var a = L2Battle.ReadItem(old, i); var b = L2Battle.ReadItem(w, i);
            string m = $"item {i:X3} ({a.Name})";
            Same((a.Name, a.Use, a.Class, a.Target, a.Icon, a.MapSprite, a.Price, a.EquipType, a.EquipBy, a.Words),
                 (b.Name, b.Use, b.Class, b.Target, b.Icon, b.MapSprite, b.Price, b.EquipType, b.EquipBy, b.Words), m);
            Same(string.Join(",", a.Values.Select(v => $"{v.Key}:{v.Value}")), string.Join(",", b.Values.Select(v => $"{v.Key}:{v.Value}")), m);
            foreach (var k in new[] { L2AiScripts.Kind.ItemMenu, L2AiScripts.Kind.ItemBattle, L2AiScripts.Kind.Weapon, L2AiScripts.Kind.Armor })
            {
                SameScript(old, w, Runs(old, k, i), Runs(w, k, i), $"{m}: its script");
                if (Runs(old, k, i) != null) iScripts++;
            }
        }

        // spells
        int sn = L2Battle.SpellCount(old), sSteps = 0;
        Same(sn, L2Battle.SpellCount(w), "the number of spells");
        for (int i = 0; i < sn; i++)
        {
            var a = L2Battle.ReadSpell(old, i); var b = L2Battle.ReadSpell(w, i);
            string m = $"spell {i:X2} ({a.Name})";
            Same((a.Name, a.Flags, a.LearnBy, a.Target, a.Mp, a.Price), (b.Name, b.Flags, b.LearnBy, b.Target, b.Mp, b.Price), m);
            Same(old.ReadByte(a.Offset + 9), w.ReadByte(b.Offset + 9), m);
            Same(L2Magic.SpellAnimation(old, i), L2Magic.SpellAnimation(w, i), m + ": its animation");
            sSteps += SameScript(old, w, Runs(old, L2AiScripts.Kind.Spell, i), Runs(w, L2AiScripts.Kind.Spell, i), m + ": its script");
        }

        // IP attacks and effects
        int ipn = L2Battle.IpCount(old);
        Same(ipn, L2Battle.IpCount(w), "the number of IP attacks");
        for (int i = 0; i < ipn; i++)
        {
            var a = L2Battle.ReadIp(old, i); var b = L2Battle.ReadIp(w, i);
            string m = $"IP attack {i:X2} ({a.Name})";
            Same((a.Name, a.Effect, a.Animation, a.Cursor, a.Targeting, a.Cost), (b.Name, b.Effect, b.Animation, b.Cursor, b.Targeting, b.Cost), m);
            SameScript(old, w, Runs(old, L2AiScripts.Kind.IpAttack, i), Runs(w, L2AiScripts.Kind.IpAttack, i), m + ": its effect");
        }
        int en = L2Magic.IpEffectCount(old);
        if (L2Magic.IpEffectCount(w) < en) throw new Mismatch("the number of IP effects");
        for (int e = 0; e < en; e++)
        {
            int x = L2AiScripts.IpEffectBase(old, e), y = L2AiScripts.IpEffectBase(w, e);
            Same(old.ReadByte(x), w.ReadByte(y), $"IP effect {e:X2}");
            SameScript(old, w, (x, x + 1), (y, y + 1), $"IP effect {e:X2}");
        }

        // descriptions (by the number the game asks for, the move puts IP attacks at 0x300 + n)
        bool movedA = L2Descriptions.IsMoved(old), movedB = L2Descriptions.IsMoved(w);
        int dn = L2Descriptions.EntryCount(old);
        for (int e = 0; e < dn; e++)
        {
            int f = !movedA && movedB && e >= L2Descriptions.IpBase ? e + 0xD0 : e;
            Same(Convert.ToHexString(L2Descriptions.Raw(old, e)), Convert.ToHexString(L2Descriptions.Raw(w, f)), $"description {e:X3}");
        }

        // shops
        var sa = L2Shops.All(old); var sb = L2Shops.All(w);
        string Shops(List<L2Shops.Shop> l) => string.Join("|", l.Select(s => $"{s.Id}:{s.Mask}:" + string.Join("/", s.Sections.Select(c => c.Bit + "=" + string.Join(",", c.Entries)))));
        Same(Shops(sa), Shops(sb), "the shops");

        // monster attack names
        Same(string.Join("|", L2AiScripts.MonsterAttackNames(old)), string.Join("|", L2AiScripts.MonsterAttackNames(w)), "the monster attack names");

        res.Checked.Add($"{L2Capsules.Count} capsule monster forms ({cScripts} scripts), {itn} items ({iScripts} scripts), {sn} spells ({sSteps} script steps), " +
                        $"{ipn} IP attacks, {en} IP effects: the same, every script step by step");
        res.Checked.Add($"{dn} descriptions, {sa.Count} shops and the monster attack names: the same");
    }

    // the bytes that differ between two roms (and the ones past the shorter one's end)
    private static List<int> Diff(byte[] a, byte[] b)
    {
        var differ = new List<int>();
        int length = Math.Min(a.Length, b.Length);
        for (int i = 0; i < length; i++) if (a[i] != b[i]) differ.Add(i);
        return differ;
    }

    // what a check of the spell move allows to change: the code places it changes, the descriptions' old space
    // ($97:8000-$97:B0A0, where the IP attacks go), our script block, and the added space (empty runs of at least 4 KB
    // past the game's 2.5 MB, the "LF2X" marks, and anything past the rom's old end)
    private static Func<int, bool> MagicAllowed(byte[] before, RomBuffer beforeRom)
    {
        var sites = new HashSet<int>();
        foreach (var (at, length) in L2Magic.CodeSites()) for (int k = 0; k < length; k++) sites.Add(at + k);
        var runs = new List<(int, int)>();
        for (int p = L2Rom.OriginalSize; p < Math.Min(before.Length, L2Rom.MaxSize);)
        {
            byte fill = before[p];
            if (fill is not (0x00 or 0xFF)) { p++; continue; }
            int runEnd = p;
            while (runEnd < before.Length && before[runEnd] == fill) runEnd++;
            if (runEnd - p >= 0x1000) runs.Add((p, runEnd));
            p = runEnd;
        }
        for (int p = L2Rom.OriginalSize; p + 16 <= before.Length; p += 16)
            if (before[p] == 'L' && before[p + 1] == 'F' && before[p + 2] == '2' && before[p + 3] == 'X') runs.Add((p, p + 16));
        var block = L2AiScripts.BlockRange(beforeRom);
        return i => sites.Contains(i) || i >= L2Descriptions.Table && i < L2Descriptions.DataEnd || i == Lufia1Constants.SnesRomSizeOffset
                    || block is { } bl && i >= bl.At && i < bl.At + bl.Length || i >= before.Length || runs.Any(r => i >= r.Item1 && i < r.Item2);
    }

    // the action

    // builds the tidied rom in memory and proves it reads the same (see the top of the class). nothing in rom changes
    public static Result Prepare(RomBuffer rom)
    {
        var res = new Result();
        if (!rom.IsLufia2) { res.Refused = "This isn't a Lufia II ROM."; return res; }
        byte[] start = rom.ToArray();
        var old = new RomBuffer((byte[])start.Clone(), rom.FilePath);
        var w = new RomBuffer((byte[])start.Clone(), rom.FilePath);
        foreach (var p in rom.Protected) w.Protected.Add(p);
        try
        {
            var roomsBefore = NameRooms(old);

            // 1. monster scripts
            var allowed = TidyMonsters(w, res);
            var afterMonsters = w.ToArray();
            foreach (int i in Diff(start, afterMonsters))
                if (!allowed.Contains(i)) throw new Mismatch($"a byte at 0x{i:X6} that the monster scripts' move shouldn't change");
            if (afterMonsters.Length != start.Length) throw new Mismatch("the ROM's size");

            // 2. spells
            var spells = UntidySpells(w);
            if (spells.Count > 0)
            {
                string list = string.Join(", ", spells.Select(i => L2Battle.ReadSpell(w, i).Name.Trim()));
                if (!L2Magic.CanInstall(w, out var why))
                    res.Left.Add($"The scripts of {list} run into the spell table's spare entries; they stay there: {why}");
                else
                {
                    var beforeRom = new RomBuffer((byte[])afterMonsters.Clone(), rom.FilePath);
                    L2Magic.Install(w);
                    var magicOk = MagicAllowed(afterMonsters, beforeRom);
                    foreach (int i in Diff(afterMonsters, w.ToArray()))
                        if (!magicOk(i)) throw new Mismatch($"a byte at 0x{i:X6} that the spell move shouldn't change");
                    res.SpellScripts = spells.Count;
                    res.Moves.Add($"Spells: the scripts of {list} run on into the spell table's spare entries or other spells' records. " +
                                  "The spells move to Lufia Forge's spell block, each script laid out right after its spell; the IP attacks, " +
                                  "IP effects and descriptions move with them (the same move as adding a spell). The old spell table is no longer read.");
                    res.Gains.Add("Spells and IP attacks can be added (the move adding them needs is done), and every spell script sits after its own spell.");
                }
            }

            if (res.Moves.Count == 0) return res;

            // 3. prove it
            Prove(old, w, res);
            var roomsAfter = NameRooms(w);
            int freed = roomsBefore.Count(kv => kv.Value < L2Battle.MonsterNameLength && roomsAfter.TryGetValue(kv.Key, out int n) && n == L2Battle.MonsterNameLength);
            int still = roomsAfter.Count(kv => kv.Value < L2Battle.MonsterNameLength);
            res.NamesFreed = freed;
            if (freed > 0)
            {
                var was = roomsBefore.Where(kv => kv.Value < L2Battle.MonsterNameLength && roomsAfter[kv.Key] == L2Battle.MonsterNameLength).Select(kv => kv.Value).ToList();
                res.Gains.Insert(0, $"{freed} monster names can be 13 letters again (now {was.Min()} to {was.Max()}).");
            }
            if (res.MonsterScripts > 0)
                res.Gains.Add("Every monster script sits on its own: names, stats and drops can be edited without touching a script, and the scripts export as one block.");
            if (still > 0) res.Left.Add($"{still} monster names stay shorter than 13 letters (a script that can't be moved uses the rest).");
            res.Checked.Add("every byte that changed is a moved script's new place, a rewritten offset, a cleared name byte or (spells) a place the spell move is known to change");
            res.Tidied = w.ToArray();
        }
        catch (Mismatch m) { res.Refused = $"Not tidied: on the tidied copy {m.Message} would not be the same, so nothing was changed."; res.Tidied = null; }
        catch (Exception e) when (e is InvalidOperationException or InvalidDataException)
        {
            res.Refused = "Not tidied: " + e.Message;
            res.Tidied = null;
        }
        catch (Exception e)
        {
            res.Refused = "Not tidied: this ROM's data can't be read the way the check needs (" + e.Message + "), so nothing was changed.";
            res.Tidied = null;
        }
        return res;
    }

    // puts a prepared result in the rom (the caller records it as one Undo step)
    public static void Apply(RomBuffer rom, Result res)
    {
        if (res.Tidied == null) throw new InvalidOperationException(res.Refused ?? "Nothing to tidy.");
        rom.ReplaceAll(res.Tidied);
    }
}
