namespace LufiaForge.Core.Lufia2;

/// <summary>
/// Event command 14, "check conditions" ($80:A074): a list of checks ended by FF. Each check sets the result (story
/// flag checks can join it with "or" / "and"); "if true, go to" and "if false, go to" jump on the result so far. When no
/// jump is taken the event carries on after the FF. Checks are edited as fields: a kind, then its values.
/// </summary>
public static class L2Conditions
{
    public const int KFlag = 0, KCounter = 1, KItems = 2, KHp = 3, KMp = 4, KSpell = 5, KLevel = 6, KFacing = 7, KDefeated = 8,
        KIfTrue = 9, KIfFalse = 10, KUnused = 11, KRemove = 99;

    public static readonly (int, string)[] Kinds =
    {
        (KFlag, "story flag"), (KCounter, "counter"), (KItems, "number of an item"), (KHp, "a hero's HP"), (KMp, "a hero's MP"),
        (KSpell, "a hero knows a spell"), (KLevel, "a hero's level"), (KFacing, "arrived from the world map facing"),
        (KDefeated, "a field monster was beaten"), (KIfTrue, "if so, go to"), (KIfFalse, "if not, go to"),
    };

    /// <summary>A check: where its code byte is in the command's bytes, the code, and its length (code included).</summary>
    public sealed record Check(int At, int Code, int Length)
    {
        public int Kind => Code switch
        {
            >= 0xC0 and < 0xC8 => KItems, >= 0xC8 and < 0xD0 => KHp, >= 0xD0 and < 0xD8 => KMp, >= 0xD8 and < 0xE0 => KUnused,
            >= 0xE0 and < 0xE8 => KSpell, >= 0xE8 and < 0xF0 => KLevel, >= 0xF0 and < 0xF8 => KFacing, >= 0xF8 => KDefeated,
            _ => (Code & 0x30) switch { 0x00 => KFlag, 0x10 => KCounter, 0x20 => KIfTrue, _ => KIfFalse },
        };
        public bool IsJump => Kind is KIfTrue or KIfFalse;
    }

    /// <summary>Bytes after a check's code byte.</summary>
    public static int ArgLength(int c) => (c & 0xC0) == 0xC0
        ? (c & 0xF8) switch { 0xC0 => 4, 0xC8 or 0xD0 or 0xD8 => 3, _ => 2 }
        : (c & 0x30) == 0 ? 1 : 2;

    public static List<Check> Parse(byte[] b)
    {
        var list = new List<Check>();
        for (int p = 1; p < b.Length && b[p] != 0xFF;)
        {
            int n = 1 + ArgLength(b[p]);
            list.Add(new Check(p, b[p], n));
            p += n;
        }
        return list;
    }

    /// <summary>Where the jump pointers (u16) are in the command's bytes, in order.</summary>
    public static List<int> JumpPositions(byte[] b) => Parse(b).Where(c => c.IsJump).Select(c => c.At + 1).ToList();

    /// <summary>The bytes of a new check of a kind.</summary>
    public static byte[] NewCheck(int kind) => kind switch
    {
        KFlag => new byte[] { 0x00, 0x00 },
        KCounter => new byte[] { 0x10, 0x0F, 0x00 },
        KItems => new byte[] { 0xC2, 0x01, 0x00, 0x01, 0x00 },
        KHp => new byte[] { 0xC8, 0x00, 0x01, 0x00 },
        KMp => new byte[] { 0xD0, 0x00, 0x01, 0x00 },
        KSpell => new byte[] { 0xE0, 0x00, 0x00 },
        KLevel => new byte[] { 0xEA, 0x00, 0x0A },
        KFacing => new byte[] { 0xF0, 0x00, 0x06 },
        KDefeated => new byte[] { 0xF8, 0x51, 0x00 },
        KIfTrue => new byte[] { 0x20, 0x00, 0x00 },
        _ => new byte[] { 0x30, 0x00, 0x00 },
    };

    private static string Op(int c) => L2EventCommands.Compare.FirstOrDefault(o => o.Item1 == (c & 7)).Item2 ?? "is less than";

    public static string Describe(byte[] b, L2EventCommands.Words w)
    {
        var parts = new List<string>();
        var current = new List<string>();
        int jump = 0;
        foreach (var c in Parse(b))
        {
            int a(int k) => b[c.At + k];
            int u(int k) => b[c.At + k] | b[c.At + k + 1] << 8;
            string join = c.Kind == KFlag && current.Count > 0 ? (c.Code & 0xC0) switch { 0x40 => "or ", 0x80 => "and ", _ => "(only) " } : "";
            if (c.IsJump)
            {
                parts.Add((current.Count > 0 ? string.Join(" ", current) : "the same") + (c.Kind == KIfTrue ? ": if so, go to " : ": if not, go to ") + w.Jump(jump++));
                current.Clear();
                continue;
            }
            current.Add(c.Kind switch
            {
                KFlag => $"{join}{w.Flag(a(1))} is {((c.Code & 1) == 0 ? "on" : "off")}",
                KCounter => $"counter {a(1):X2} {Op(c.Code)} {a(2)}",
                KItems => $"the number of {w.Item(u(1))} {Op(c.Code)} {u(3)}",
                KHp => $"{L2EventCommands.HeroName(a(1))}'s HP {Op(c.Code)} {u(2)}",
                KMp => $"{L2EventCommands.HeroName(a(1))}'s MP {Op(c.Code)} {u(2)}",
                KSpell => $"{L2EventCommands.HeroName(a(1))} knows {w.Spell(a(2))}",
                KLevel => $"{L2EventCommands.HeroName(a(1))}'s level {Op(c.Code)} {a(2)}",
                KFacing => $"the party came from the world map facing {L2EventCommands.Facings.FirstOrDefault(f => f.Item1 == a(2)).Item2 ?? "?"}",
                KDefeated => $"{w.Who(a(1))} was beaten",
                _ => "(a broken check)",
            });
        }
        if (current.Count > 0) parts.Add(string.Join(" ", current) + " (nothing goes on it)");
        return string.Join("; then whether ", parts);
    }

    /// <summary>The fields of a 14 command: per check its kind (or "take this out"), then its values; then "add a check".</summary>
    public static L2ParamDef[] Fields(byte[] b)
    {
        var fields = new List<L2ParamDef>();
        var checks = Parse(b);
        int jump = 0;
        for (int i = 0; i < checks.Count; i++)
        {
            var c = checks[i];
            int at = c.At, index = i;
            fields.Add(new L2ParamDef
            {
                Label = $"check {i + 1}", Kind = L2Kind.Choice,
                Options = Kinds.Where(k => k.Item1 != KUnused).Append((KRemove, "(take this check out)")).ToArray(),
                Get = _ => c.Kind, Set = (_, _) => { },
                Rebuild = (old, v) => Replace(old, index, v == KRemove ? Array.Empty<byte>() : NewCheck(v)),
            });
            L2ParamDef Byte(string label, L2Kind kind, int k, int max = 255) => L2ParamDef.Byte(label, kind, at + k, max);
            L2ParamDef Word(string label, L2Kind kind, int k) => L2ParamDef.Word(label, kind, at + k);
            L2ParamDef Cmp() => new()
            {
                Label = "", Kind = L2Kind.Choice, Options = L2EventCommands.Compare,
                Get = x => x[at] & 7, Set = (x, v) => x[at] = (byte)((x[at] & 0xF8) | v),
            };
            switch (c.Kind)
            {
                case KFlag:
                    if (i > 0) fields.Add(new L2ParamDef
                    {
                        Label = "joined", Kind = L2Kind.Choice, Options = new[] { (0x00, "replaces the result"), (0x40, "or"), (0x80, "and") },
                        Get = x => x[at] & 0xC0, Set = (x, v) => x[at] = (byte)((x[at] & 0x3F) | v),
                    });
                    fields.Add(Byte("story flag", L2Kind.Flag, 1));
                    fields.Add(new L2ParamDef
                    {
                        Label = "is", Kind = L2Kind.Choice, Options = new[] { (0, "on"), (1, "off") },
                        Get = x => x[at] & 1, Set = (x, v) => x[at] = (byte)((x[at] & 0xFE) | v),
                    });
                    break;
                case KCounter: fields.Add(Byte("counter", L2Kind.Variable, 1)); fields.Add(Cmp()); fields.Add(Byte("value", L2Kind.Number, 2)); break;
                case KItems: fields.Add(Word("item", L2Kind.Item, 1)); fields.Add(Cmp()); fields.Add(Word("how many", L2Kind.Word, 3)); break;
                case KHp or KMp: fields.Add(Byte("hero", L2Kind.Hero, 1, 6)); fields.Add(Cmp()); fields.Add(Word("value", L2Kind.Word, 2)); break;
                case KSpell: fields.Add(Byte("hero", L2Kind.Hero, 1, 6)); fields.Add(Byte("spell", L2Kind.Spell, 2)); break;
                case KLevel: fields.Add(Byte("hero", L2Kind.Hero, 1, 6)); fields.Add(Cmp()); fields.Add(Byte("level", L2Kind.Number, 2)); break;
                case KFacing: fields.Add(L2ParamDef.Pick("facing", at + 2, L2EventCommands.Facings)); break;
                case KDefeated: fields.Add(Byte("field monster", L2Kind.Who, 1)); break;
                case KIfTrue or KIfFalse: fields.Add(L2ParamDef.Jump("step", jump++)); break;
            }
        }
        fields.Add(new L2ParamDef
        {
            Label = "add", Kind = L2Kind.Choice, Options = Kinds.Where(k => k.Item1 != KUnused).Prepend((-1, "(add a check...)")).ToArray(),
            Get = _ => -1, Set = (_, _) => { },
            Rebuild = (old, v) => v < 0 ? (old, null) : Replace(old, Parse(old).Count, NewCheck(v)),
        });
        return fields.ToArray();
    }

    /// <summary>The bytes with check <paramref name="index"/> replaced by <paramref name="check"/> (empty: taken out; index =
    /// count: added before the FF), and which old jump each new jump keeps.</summary>
    public static (byte[] Bytes, int[]? JumpMap) Replace(byte[] old, int index, byte[] check)
    {
        var checks = Parse(old);
        var n = new List<byte> { old[0] };
        var map = new List<int>();
        int oldJump = 0;
        for (int i = 0; i <= checks.Count; i++)
        {
            if (i == index && check.Length > 0)
            {
                n.AddRange(check);
                if (check[0] is >= 0x20 and < 0x40) map.Add(i < checks.Count && checks[i].IsJump ? oldJump : -1);
            }
            if (i == checks.Count) break;
            var c = checks[i];
            if (i != index) { n.AddRange(old.AsSpan(c.At, c.Length).ToArray()); if (c.IsJump) map.Add(oldJump); }
            if (c.IsJump) oldJump++;
        }
        n.Add(0xFF);
        return (n.ToArray(), map.ToArray());
    }
}
