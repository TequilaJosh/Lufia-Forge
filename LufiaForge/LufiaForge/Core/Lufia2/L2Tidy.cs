using System.IO;
using System.Text;

namespace LufiaForge.Core.Lufia2;

/// <summary>
/// "Tidy the hack's data" (2026-10-27): the Lufia II fan hacks (Spekkio, Kureji Lufia) keep monster battle scripts in
/// the unused end of other monsters' name fields and inside other records, and their spell scripts run on into the
/// spell table's spare entries (Frue, Spekkio, Kureji, Foomy). Lufia Forge reads and edits them where they are, but a
/// name can't grow over a script and the data is hard to take apart. Tidying moves them into clean space and points
/// the game at the new places:
/// <list type="bullet">
/// <item>Monster scripts that use bytes of a name field or of a record go to one block in the monsters' own bank (a
/// script's offset is added to its record in that bank, so it can't leave it): the largest empty run there (all 00 or
/// FF, at least 4 KB, the rule Lufia Forge's added space follows; the fan hacks have about 4.4 KB of zeros after the
/// monster attack names at $D0:8E35). Each script is laid out again from its steps for its own record (the steps a
/// path reaches; endings without jumps that several scripts share are written once, as they read the same from any
/// record), and the record's offset (tag 07 its turn, 08 when hit) is rewritten. Name field bytes no script uses any
/// more are cleared to 00, so every such name can have 13 letters.</item>
/// <item>Spell scripts that run into the spell table or another spell's record: the spells move to Lufia Forge's spell
/// block with each script right after its spell (<see cref="L2Magic.Install"/>, which moves the IP attacks and the
/// descriptions too).</item>
/// </list>
/// Nothing is written until the tidied ROM, built in memory, is proven to read the same: every monster's stats, name,
/// drop and both scripts (old and new walked side by side from the bytes, every path, each pair of steps the same
/// command and arguments, a "go to" counted as the step it leads to: <see cref="SameRun"/>), every item, spell, IP attack and IP effect with its scripts, every description, shop and monster attack name;
/// and every byte that changed is one of: a moved script's new bytes in empty space, a rewritten offset, a cleared name
/// byte no script uses, or (spells) the code places and space the spell move is known to change.
/// </summary>
public static class L2Tidy
{
    public sealed class Result
    {
        /// <summary>The tidied ROM, or null (nothing to tidy, or <see cref="Refused"/>).</summary>
        public byte[]? Tidied { get; set; }
        /// <summary>Why it can't be done, or null.</summary>
        public string? Refused { get; set; }
        /// <summary>What moves (lines for the preview).</summary>
        public List<string> Moves { get; } = new();
        /// <summary>What becomes possible.</summary>
        public List<string> Gains { get; } = new();
        /// <summary>What was proven to read the same.</summary>
        public List<string> Checked { get; } = new();
        /// <summary>What stays as it is (and why).</summary>
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
                foreach (var l in Left) sb.AppendLine("- " + l);
                return sb.ToString().TrimEnd();
            }
            sb.AppendLine("What moves:");
            foreach (var l in Moves) sb.AppendLine("- " + l);
            if (Gains.Count > 0) { sb.AppendLine(); sb.AppendLine("What it makes possible:"); foreach (var l in Gains) sb.AppendLine("- " + l); }
            if (Left.Count > 0) { sb.AppendLine(); sb.AppendLine("Left where it is:"); foreach (var l in Left) sb.AppendLine("- " + l); }
            sb.AppendLine(); sb.AppendLine("Checked on the tidied ROM before anything is written:");
            foreach (var l in Checked) sb.AppendLine("- " + l);
            return sb.ToString().TrimEnd();
        }
    }

    // ── monsters ─────────────────────────────────────────────────────────────

    private sealed record Rec(int At, int End, int NameLength, List<int> Ids);

    /// <summary>The monster records (each once, in table order of their first id): the record runs to the 00 ending its
    /// tags; its name to its 00 (13 letters when it has none).</summary>
    private static List<Rec> Records(RomBuffer rom)
    {
        var byAt = new Dictionary<int, Rec>();
        var list = new List<Rec>();
        for (int i = 0, n = L2Battle.MonsterCountIn(rom); i < n; i++)
        {
            int o = L2Battle.MonsterAt(rom, i);
            if (byAt.TryGetValue(o, out var r)) { r.Ids.Add(i); continue; }
            int p = o + 0x21;
            for (int g = 0; g < 16 && rom.ReadByte(p) != 0; g++) p += 3;
            int len = Array.IndexOf(rom.ReadBytes(o, L2Battle.MonsterNameLength), (byte)0);
            r = new Rec(o, p + 1, len < 0 ? L2Battle.MonsterNameLength : len, new List<int> { i });
            byAt[o] = r;
            list.Add(r);
        }
        return list;
    }

    /// <summary>Where tag <paramref name="tag"/> is in a record's tag list, or -1.</summary>
    private static int TagAt(RomBuffer rom, Rec r, byte tag)
    {
        for (int p = r.At + 0x21; p < r.End - 1; p += 3)
            if (rom.ReadByte(p) == tag) return p;
        return -1;
    }

    /// <summary>The file offsets a script's steps are read from (as <see cref="L2AiScripts.Parse"/> reads it).</summary>
    private static HashSet<int> Bytes(List<L2AiScripts.Step> steps, int baseAt)
    {
        var s = new HashSet<int>();
        foreach (var st in steps)
            if (st.Offset >= 0)
                for (int k = 0; k < st.Length; k++) s.Add(L2Rom.InBank(baseAt, st.Offset + k));
        return s;
    }

    private sealed record Move(Rec Rec, int Tag, int TagPos, int Start, List<L2AiScripts.Step> Steps);

    /// <summary>The steps the runner can reach from the first one (in their order): a script read straight on to its
    /// furthest jump can hold bytes no path runs (another script's tail, data), which a copy leaves out.</summary>
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
            var s = steps[i];
            foreach (var t in s.Targets) if (t != null && index.TryGetValue(t, out int k)) todo.Push(k);
            if (s.Def?.EndsPath != true && i + 1 < steps.Count) todo.Push(i + 1);
        }
        return steps.Where((s, i) => seen[i]).ToList();
    }

    /// <summary>Every monster script (record, tag 07/08) with its steps, read from the tag's offset; offset 0 (none) is
    /// left out. A script that can't be read comes back with null steps.</summary>
    private static List<(Rec Rec, int Tag, int TagPos, int Start, List<L2AiScripts.Step>? Steps)> Scripts(RomBuffer rom, List<Rec> recs)
    {
        var l = new List<(Rec, int, int, int, List<L2AiScripts.Step>?)>();
        foreach (var r in recs)
            foreach (byte tag in new byte[] { 7, 8 })
            {
                int p = TagAt(rom, r, tag);
                if (p < 0 || rom.ReadUInt16Le(p + 1) == 0) continue;
                int start = L2Rom.InBank(r.At, rom.ReadUInt16Le(p + 1));
                List<L2AiScripts.Step>? steps;
                try { steps = start < 0 ? null : L2AiScripts.Parse(rom, start, r.At); } catch (InvalidDataException) { steps = null; }
                l.Add((r, tag, p, start, steps));
            }
        return l;
    }

    private static string Count(int n, string what) => $"{n} {what}{(n == 1 ? "" : "s")}";

    private static string MonsterName(RomBuffer rom, Rec r) =>
        $"{r.Ids[0]:X2} {L2Rom.Name(rom, r.At, L2Battle.MonsterNameLength)}";

    /// <summary>Moves the monster scripts that use name or record bytes (see the class). Returns the bytes it may change.</summary>
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
            var b = Bytes(s.Steps, s.Rec.At);
            if (!b.Overlaps(names) && !b.Overlaps(data)) continue;
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
            byte v = w.ReadByte(p);
            if (v is not (0x00 or 0xFF) || used.Contains(p)) { p++; continue; }
            int q = p;
            while (q < bank + 0x8000 && w.ReadByte(q) == v && !used.Contains(q)) q++;
            // a run that ends at Lufia Forge's own space mark is its added space
            bool lfSpace = q + 4 <= w.Length && Encoding.ASCII.GetString(w.ReadBytes(q, 4)) == "LF2X";
            if (!lfSpace && q - p > bestLen) { bestStart = p; bestLen = q - p; }
            p = q;
        }
        if (bestLen < 0x1000)
            throw new InvalidOperationException($"The monsters' bank (${L2Rom.FileToSnes(bank) >> 16:X2}) has no empty space for the moved scripts (the largest empty run is {bestLen} bytes; 4 KB is the least Lufia Forge counts as unused).");
        int areaStart = (bestStart + 16 + 15) & ~15, areaEnd = bestStart + bestLen - 16, at = areaStart;

        var placedSame = new Dictionary<(int, int), int>();   // (record, old start) -> new start
        var pieces = new Dictionary<string, int>();          // a piece without jumps -> where it is
        var movedRecs = new HashSet<int>();
        foreach (var m in moves)
        {
            if (!placedSame.TryGetValue((m.Rec.At, m.Start), out int newStart))
            {
                newStart = LayOut(w, m.Steps, m.Rec.At, ref at, areaEnd, pieces);
                placedSame[(m.Rec.At, m.Start)] = newStart;
            }
            w.WriteUInt16Le(m.TagPos + 1, (ushort)L2Rom.OffsetInBank(m.Rec.At, newStart));
            changed.Add(m.TagPos + 1); changed.Add(m.TagPos + 2);
            movedRecs.Add(m.Rec.At);
        }
        for (int p = areaStart; p < at; p++) changed.Add(p);

        // name bytes no script uses any more: 00
        var after = new HashSet<int>();
        foreach (var s in Scripts(w, recs)) if (s.Steps != null) after.UnionWith(Bytes(s.Steps, s.Rec.At));
        int cleared = 0;
        foreach (var r in recs)
            for (int k = r.NameLength + 1; k < L2Battle.MonsterNameLength; k++)
            {
                int p = r.At + k;
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

    /// <summary>Writes a script for the record at <paramref name="recAt"/> from <paramref name="at"/> on and returns where it
    /// starts. It is laid out in pieces that end where a path ends (00, 4F, 43, a "go to"), so nothing runs from one
    /// piece into the next; a piece without jumps reads the same from any record, so one already written (by this or
    /// another script) is used again instead of written twice.</summary>
    internal static int LayOut(RomBuffer w, List<L2AiScripts.Step> steps, int recAt, ref int at, int areaEnd, Dictionary<string, int> pieces)
    {
        var blocks = new List<(int From, int To)>();
        for (int i = 1, b0 = 0; i <= steps.Count; i++)
            if (i == steps.Count || steps[i - 1].Def?.EndsPath == true) { blocks.Add((b0, i)); b0 = i; }
        var addr = new Dictionary<L2AiScripts.Step, int>(ReferenceEqualityComparer.Instance);
        var write = new List<(int From, int To, int At, bool End)>();
        byte[] Raw(int from, int to) => steps.Skip(from).Take(to - from).SelectMany(s => new[] { s.Op }.Concat(s.Args)).ToArray();
        foreach (var (from, to) in blocks)
        {
            bool ends = steps[to - 1].Def?.EndsPath == true;
            bool pure = ends && steps.Skip(from).Take(to - from).All(s => s.Def?.IsJump != true);
            int len = steps.Skip(from).Take(to - from).Sum(s => s.Length) + (ends ? 0 : 1);
            int place;
            string key = pure ? Convert.ToHexString(Raw(from, to)) : "";
            if (pure && pieces.TryGetValue(key, out place)) { }
            else
            {
                if (at + len > areaEnd)
                    throw new InvalidOperationException($"The moved monster scripts need more room than the monsters' bank has free ({areaEnd - at} bytes left).");
                place = at;
                at += len;
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
                var s = steps[i];
                var args = (byte[])s.Args.Clone();
                var d = s.Def!;
                for (int a = 0, j = 0; a < d.Args.Length; a++)
                {
                    if (d.Args[a] != L2AiScripts.Arg.Jump) continue;
                    var t = s.Targets[j++] ?? throw new InvalidOperationException("A \"go to\" has no step to go to.");
                    int off = L2Rom.OffsetInBank(recAt, addr[t]), k = L2AiScripts.ArgAt(d, a);
                    args[k] = (byte)off; args[k + 1] = (byte)(off >> 8);
                }
                bytes.Add(s.Op);
                bytes.AddRange(args);
            }
            if (end) bytes.Add(0);   // a last step that doesn't end the script still stops here
            w.WriteBytes(place, bytes.ToArray());
        }
        return addr[steps[0]];
    }

    /// <summary>Records whose name can have <paramref name="room"/> letters (by first id, placeholders left out).</summary>
    private static Dictionary<int, int> NameRooms(RomBuffer rom)
    {
        var used = L2AiScripts.MonsterScriptBytes(rom);
        var d = new Dictionary<int, int>();
        foreach (var r in Records(rom))
        {
            var m = L2Battle.ReadMonster(rom, r.Ids[0]);
            if (m.Unused) continue;
            d[r.At] = L2Battle.NameRoom(rom, m, used);
        }
        return d;
    }

    // ── spells ───────────────────────────────────────────────────────────────

    /// <summary>The spells whose script runs into the spell table or another spell's record.</summary>
    private static List<int> UntidySpells(RomBuffer rom)
    {
        var l = new List<int>();
        if (L2Magic.BlockA(rom) >= 0) return l;
        int n = L2Battle.SpellCount(rom);
        var recs = Enumerable.Range(0, n).Select(i => L2Battle.SpellOffset(rom, i)).ToList();
        int table = L2Rom.SnesToFile((int)rom.ReadUInt24Le(0xF451));
        int first = recs.Count == 0 ? table : recs.Min();
        for (int i = 0; i < n; i++)
        {
            int w = rom.ReadUInt16Le(recs[i] + 0x11);
            if (w == 0) continue;
            List<L2AiScripts.Step> steps;
            try { steps = L2AiScripts.Parse(rom, L2Rom.InBank(recs[i], w), recs[i]); } catch (InvalidDataException) { continue; }
            var b = Bytes(steps, recs[i]);
            if (b.Any(p => p >= table && p < first || recs.Any(o => o != recs[i] && p >= o && p < o + 0x13))) l.Add(i);
        }
        return l;
    }

    // ── checks ───────────────────────────────────────────────────────────────

    /// <summary>One step as the runner reads it at <paramref name="rel"/> (from the base): its command and arguments other
    /// than jumps, and where it can go on (each jump, then the next step unless the path ends there), a "go to" counted
    /// as the step it leads to.</summary>
    private static (string Text, int[] Next) StepAt(RomBuffer rom, int baseAt, int rel)
    {
        int At(int r)
        {
            int f = L2Rom.InBank(baseAt, r & 0xFFFF);
            return f >= 0 && f < rom.Length ? f : throw new InvalidDataException($"Offset {r & 0xFFFF:X4} from 0x{baseAt:X6} isn't in the ROM.");
        }
        byte op = rom.ReadByte(At(rel));
        var d = L2AiScripts.Def(op) ?? throw new InvalidDataException($"Unknown command {op:X2} at 0x{At(rel):X6}.");
        var sb = new StringBuilder(op.ToString("X2"));
        var next = new List<int>();
        int a = 1;
        foreach (var arg in d.Args)
        {
            int size = L2AiScripts.Size(arg);
            if (arg == L2AiScripts.Arg.Jump) { sb.Append(" j"); next.Add(Resolve(rom, baseAt, rom.ReadByte(At(rel + a)) | rom.ReadByte(At(rel + a + 1)) << 8)); }
            else for (int q = 0; q < size; q++) sb.Append(' ').Append(rom.ReadByte(At(rel + a + q)).ToString("X2"));
            a += size;
        }
        if (!d.EndsPath) { sb.Append(" >"); next.Add(Resolve(rom, baseAt, rel + d.Length)); }
        return (sb.ToString(), next.ToArray());
    }

    /// <summary>Where a "go to" (or a chain of them) at <paramref name="rel"/> leads.</summary>
    private static int Resolve(RomBuffer rom, int baseAt, int rel)
    {
        for (int g = 0; g < 64; g++)
        {
            int f = L2Rom.InBank(baseAt, rel & 0xFFFF);
            if (f < 0 || f + 2 >= rom.Length) throw new InvalidDataException($"Offset {rel & 0xFFFF:X4} from 0x{baseAt:X6} isn't in the ROM.");
            if (rom.ReadByte(f) != 0x03) return rel & 0xFFFF;
            int f1 = L2Rom.InBank(baseAt, (rel + 1) & 0xFFFF), f2 = L2Rom.InBank(baseAt, (rel + 2) & 0xFFFF);
            rel = rom.ReadByte(f1) | rom.ReadByte(f2) << 8;
        }
        throw new InvalidDataException("A loop of \"go to\"s.");
    }

    /// <summary>Whether two scripts do the same: walked side by side from their starts, every pair of steps reached the
    /// same way has the same command and arguments and goes on to pairs that do too (the same steps in the same order on
    /// every path, wherever their bytes are and however often a piece is shared). Returns how many steps the first has.</summary>
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

    /// <summary>Where a script runs from now: Lufia Forge's edited copy, or the game's (base, start); null for none.</summary>
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

    /// <summary>Proves the tidied ROM reads like the old one (throws <see cref="Mismatch"/> with what differs).</summary>
    /// <param name="moved">The records moved to another bank (<see cref="L2NewMonsters.MoveHackBank"/>): the new file
    /// offset of an old record's byte; every byte of a record (offsets too) must then read the same there.</param>
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

        // descriptions (by the number the game asks for: the move puts IP attacks at 0x300 + n)
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

    /// <summary>The bytes that differ between two ROMs (and the ones past the shorter one's end).</summary>
    private static List<int> Diff(byte[] a, byte[] b)
    {
        var l = new List<int>();
        int n = Math.Min(a.Length, b.Length);
        for (int i = 0; i < n; i++) if (a[i] != b[i]) l.Add(i);
        return l;
    }

    /// <summary>What a check of the spell move allows to change: the code places it changes, the descriptions' old space
    /// ($97:8000-$97:B0A0, where the IP attacks go), Lufia Forge's script block, and the added space (empty runs of
    /// at least 4 KB past the game's 2.5 MB, the "LF2X" marks, and anything past the ROM's old end).</summary>
    private static Func<int, bool> MagicAllowed(byte[] before, RomBuffer beforeRom)
    {
        var sites = new HashSet<int>();
        foreach (var (at, len) in L2Magic.CodeSites()) for (int k = 0; k < len; k++) sites.Add(at + k);
        var runs = new List<(int, int)>();
        for (int p = L2Rom.OriginalSize; p < Math.Min(before.Length, L2Rom.MaxSize);)
        {
            byte v = before[p];
            if (v is not (0x00 or 0xFF)) { p++; continue; }
            int q = p;
            while (q < before.Length && before[q] == v) q++;
            if (q - p >= 0x1000) runs.Add((p, q));
            p = q;
        }
        for (int p = L2Rom.OriginalSize; p + 16 <= before.Length; p += 16)
            if (before[p] == 'L' && before[p + 1] == 'F' && before[p + 2] == '2' && before[p + 3] == 'X') runs.Add((p, p + 16));
        var block = L2AiScripts.BlockRange(beforeRom);
        return i => sites.Contains(i) || i >= L2Descriptions.Table && i < L2Descriptions.DataEnd || i == Lufia1Constants.SnesRomSizeOffset
                    || block is { } bl && i >= bl.At && i < bl.At + bl.Length || i >= before.Length || runs.Any(r => i >= r.Item1 && i < r.Item2);
    }

    // ── the action ───────────────────────────────────────────────────────────

    /// <summary>Builds the tidied ROM in memory and proves it reads the same (see the class). Nothing in
    /// <paramref name="rom"/> changes.</summary>
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

    /// <summary>Puts a prepared result in the ROM (the caller records it as one Undo step).</summary>
    public static void Apply(RomBuffer rom, Result res)
    {
        if (res.Tidied == null) throw new InvalidOperationException(res.Refused ?? "Nothing to tidy.");
        rom.ReplaceAll(res.Tidied);
    }
}
