using Kind = LufiaForge.Core.Lufia2.L2AiScripts.Kind;
using Step = LufiaForge.Core.Lufia2.L2AiScripts.Step;

namespace LufiaForge.Core.Lufia2;

/// <summary>
/// Monster types. Lufia II has no type byte (the monster record's +0E is 32 on 222 of the 224 monsters): a monster is a
/// dragon, an insect or undead because its "when hit" script runs the game's shared script for it, and a weapon is
/// strong against them because its attack carries that element (Dragon tooth and Lizard blow "vs dragons", Insect crush
/// "vs insects"). Read from all 224 scripts (2026-11-11):
/// <list type="bullet">
/// <item>Dragon: shared script 0C, double from "vs dragons" (13 monsters: the dragons, T Rex, Brokion, Orky, Current).</item>
/// <item>Insect: 10, double from "vs insects" (spiders, beetles, bees, moths, Sentopez, Tarantula).</item>
/// <item>Undead: 12, healing hurts and instant death can't work, with 05, double from light (Skeleton to Venge Ghost, 21).</item>
/// <item>Flying: 04, double from thrown weapons (boomerangs), earth does nothing (bats, birds, bees, dragons, 39).</item>
/// <item>Hard shell: 0F, double from blunt weapons (maces, rods): crabs, shells, beetles, the gorems (25).</item>
/// <item>Golem: 14, every element does half (the 8 gorems).</item>
/// <item>Shadow: 0B, plain (no element) attacks do half (Fiend, Shadow, Medusa, Deadly Sword, Black Dragon, 12).</item>
/// </list>
/// The game's checks for dragons, insects, flying and hard shells want that element alone (an attack that is also fire
/// misses them): the first change gives them versions that test the bit (<see cref="FixGameChecks"/>). Lufia Forge adds four types of its own on element bits no script uses (0001, 0008, 2000 and 4000):
/// four steps in the "when hit" script test the bit and double the damage, and a weapon or spell adds the bit to its
/// element with a 17 27 step after the step that sets it.
/// </summary>
public static class L2MonsterTypes
{
    /// <param name="Shared">The shared scripts that make it (the first one is what marks it).</param>
    /// <param name="Element">The element that is strong against it (0 = none).</param>
    public sealed record TypeDef(string Name, string Help, int[] Shared, int Element, bool Added = false);

    /// <summary>The register the added types' check works in: shared value 12 ($7F:F432). No script of the game and no
    /// battle code uses it (the monsters' own counters are no good: 12 monsters' turns count with counter 8, and a
    /// "when hit" script runs as the monster).</summary>
    public const int Work = 0x12;

    public static readonly TypeDef[] Types =
    {
        new("Dragon", "Dragon-slaying attacks (the element \"vs dragons\": Dragon tooth, Lizard blow, the dragon IP attacks) do double damage. The game's shared script 0C.", new[] { 0x0C }, 0x0100),
        new("Insect", "Insect-slaying attacks (\"vs insects\": Insect crush) do double damage. Shared script 10.", new[] { 0x10 }, 0x0200),
        new("Undead", "Healing hurts it, instant death can't work on it, and light does double damage. Shared scripts 12 and 05 (unticking it keeps a light weakness the monster has apart from it).", new[] { 0x12, 0x05 }, 0x0010),
        new("Flying", "Thrown weapons (boomerangs) do double damage and earth does nothing. Shared script 04.", new[] { 0x04 }, 0x0080),
        new("Hard shell", "Blunt weapons (maces, rods) do double damage. Shared script 0F.", new[] { 0x0F }, 0x0040),
        new("Golem", "Every element does half damage. Shared script 14.", new[] { 0x14 }, 0),
        new("Shadow", "Plain attacks (no element) do half damage. Shared script 0B.", new[] { 0x0B }, 0),
        new("Beast", "Added by Lufia Forge: attacks with the element \"vs beasts\" do double damage.", Array.Empty<int>(), 0x0001, true),
        new("Aquatic", "Added by Lufia Forge: attacks with the element \"vs aquatic\" do double damage.", Array.Empty<int>(), 0x0008, true),
        new("Plant", "Added by Lufia Forge: attacks with the element \"vs plants\" do double damage.", Array.Empty<int>(), 0x2000, true),
        new("Humanoid", "Added by Lufia Forge: attacks with the element \"vs humanoids\" do double damage.", Array.Empty<int>(), 0x4000, true),
    };

    private static Step Op(byte op, params int[] v)
    {
        var s = L2AiScripts.New(op);
        for (int i = 0; i < v.Length; i++) L2AiScripts.SetArg(s, i, v[i]);
        return s;
    }

    private static List<Step> Load(RomBuffer rom, Kind kind, int index)
    {
        List<Step> steps;
        try { steps = L2AiScripts.Load(rom, kind, index).Steps; } catch (Exception) { steps = new(); }
        if (steps.Count > 0) return steps;
        return kind == Kind.Weapon ? new() { L2AiScripts.New(0x37), L2AiScripts.New(0x00) } : new() { L2AiScripts.New(0x00) };
    }

    private static bool Calls(Step s, int shared) => s.Op == 0x42 && s.Args.Length > 0 && s.Args[0] == shared;

    /// <summary>Where the added type's four steps start (0C 87 =element / 16 87 bit / 06 87 0 skip / 0F 2A 2), or -1.</summary>
    private static int AddedAt(List<Step> steps, int bit)
    {
        for (int i = 0; i + 3 < steps.Count; i++)
        {
            Step a = steps[i], b = steps[i + 1], c = steps[i + 2], d = steps[i + 3];
            if (a.Op == 0x0C && L2AiScripts.ArgValue(a, 0) == Work && L2AiScripts.ArgValue(a, 1) == 0x8027
                && b.Op == 0x16 && L2AiScripts.ArgValue(b, 0) == Work && L2AiScripts.ArgValue(b, 1) == bit
                && c.Op == 0x06 && L2AiScripts.ArgValue(c, 0) == Work && L2AiScripts.ArgValue(c, 1) == 0
                && d.Op == 0x0F && L2AiScripts.ArgValue(d, 0) == 0x2A)
                return i;
        }
        return -1;
    }

    private static bool Has(List<Step> steps, TypeDef t) =>
        t.Added ? AddedAt(steps, t.Element) >= 0 : steps.Any(s => Calls(s, t.Shared[0]));

    /// <summary>The types a monster is (bit n = <see cref="Types"/>[n]).</summary>
    public static int Of(RomBuffer rom, int monster)
    {
        var steps = Load(rom, Kind.MonsterDefense, monster);
        int bits = 0;
        for (int t = 0; t < Types.Length; t++) if (Has(steps, Types[t])) bits |= 1 << t;
        return bits;
    }

    /// <summary>Takes a step out, sending jumps that landed on it to the step after it.</summary>
    private static void Remove(List<Step> steps, Step old)
    {
        int i = steps.IndexOf(old);
        steps.RemoveAt(i);
        var next = steps.ElementAtOrDefault(i) ?? steps.LastOrDefault();
        foreach (var o in steps)
            for (int j = 0; j < o.Targets.Length; j++)
                if (o.Targets[j] == old) o.Targets[j] = next;
    }

    /// <summary>Makes the monster that type or not (the steps go at the top of its "when hit" script). False when nothing changed.</summary>
    public static bool Set(RomBuffer rom, int monster, int type, bool on)
    {
        var t = Types[type];
        var steps = Load(rom, Kind.MonsterDefense, monster);
        if (Has(steps, t) == on) return false;
        FixGameChecks(rom);
        if (on)
        {
            if (t.Added)
            {
                var mul = Op(0x0F, 0x2A, 2);
                var skip = Op(0x06, Work, 0);
                skip.Targets[0] = steps[0];
                steps.InsertRange(0, new[] { Op(0x0C, Work, 0x8027), Op(0x16, Work, t.Element), skip, mul });
            }
            else
            {
                // the ones that come with it, then the type's script (a monster that has one of them already keeps its own)
                var add = t.Shared.Skip(1).Where(sh => !steps.Any(s => Calls(s, sh))).Append(t.Shared[0]).Select(sh => Op(0x42, sh));
                steps.InsertRange(0, add.ToList());
            }
        }
        else if (t.Added)
        {
            int at = AddedAt(steps, t.Element);
            foreach (var s in steps.GetRange(at, 4).ToList()) Remove(steps, s);
        }
        else
        {
            // the type's own script goes; the ones that come with it (Undead's light weakness) only right before it, where a
            // tick puts them, so a weakness the monster had of its own stays
            foreach (var s in steps.Where(s => Calls(s, t.Shared[0])).ToList())
            {
                for (int k = t.Shared.Length - 1; k >= 1; k--)
                {
                    int i = steps.IndexOf(s);
                    if (i > 0 && Calls(steps[i - 1], t.Shared[k])) Remove(steps, steps[i - 1]);
                }
                Remove(steps, s);
            }
        }
        Save(rom, Kind.MonsterDefense, monster, steps);
        return true;
    }

    // ── the game's checks that want the element alone ──────────────────────────

    /// <summary>The game's shared scripts that test the element with "is" (==): 04 thrown x2 / earth 0, 0C vs dragons x2,
    /// 0F blunt x2, 10 vs insects x2, 17 blunt half. An attack with a second element (a fire sword that is also vs
    /// dragons) misses them. <see cref="FixGameChecks"/> gives each a version that tests the bit.</summary>
    public static readonly int[] ExactChecks = { 0x04, 0x0C, 0x0F, 0x10, 0x17 };

    /// <summary>Shared script <paramref name="n"/> testing bits: counter 1 = the element (as the game leaves it), the test in
    /// register 12.</summary>
    public static List<Step> BitCheck(int n)
    {
        var ret = Op(0x43);
        var steps = new List<Step> { Op(0x0C, 0x80, 0x8027) };
        void Test(int bit, Step act, Step after)
        {
            var skip = Op(0x06, Work, 0);
            skip.Targets[0] = after;
            steps.AddRange(new[] { Op(0x0C, Work, 0x8027), Op(0x16, Work, bit), skip, act });
        }
        switch (n)
        {
            case 0x04:
                var earth = Op(0x0C, Work, 0x8027);
                Test(0x0080, Op(0x0F, 0x2A, 2), earth);
                var skip = Op(0x06, Work, 0); skip.Targets[0] = ret;
                steps.AddRange(new[] { earth, Op(0x16, Work, 0x1000), skip, Op(0x0C, 0x2A, 0) });
                break;
            case 0x0C: Test(0x0100, Op(0x0F, 0x2A, 2), ret); break;
            case 0x0F: Test(0x0040, Op(0x0F, 0x2A, 2), ret); break;
            case 0x10: Test(0x0200, Op(0x0F, 0x2A, 2), ret); break;
            case 0x17: Test(0x0040, Op(0x10, 0x2A, 2), ret); break;
            default: throw new ArgumentOutOfRangeException(nameof(n));
        }
        steps.Add(ret);
        return steps;
    }

    /// <summary>Whether the game's exact checks were given bit tests.</summary>
    public static bool GameChecksFixed(RomBuffer rom) => ExactChecks.All(n => L2SharedScripts.Override(rom, n) >= 0);

    /// <summary>Gives the game's exact checks bit tests (once), so a dragon, an insect, a flier or a hard shell counts an attack
    /// that has other elements too. False when they were already, or this ROM's code is not the US game's.</summary>
    public static bool FixGameChecks(RomBuffer rom)
    {
        if (GameChecksFixed(rom) || L2SharedScripts.CantReplace(rom) != null) return false;
        foreach (int n in ExactChecks)
            if (L2SharedScripts.Override(rom, n) < 0) L2SharedScripts.Replace(rom, n, BitCheck(n));
        return true;
    }

    // ── what an attack is strong against ────────────────────────────────────────

    /// <summary>The element a step sets (1F w, 21 w.., 22 w.., 0C 27 w) or adds (17 27 w), and which argument holds it.</summary>
    private static int ElementArg(Step s)
    {
        if (s.Op is 0x1F or 0x21 or 0x22) return 0;
        if (s.Op is 0x0C or 0x17 && L2AiScripts.ArgValue(s, 0) == 0x27 && (L2AiScripts.ArgValue(s, 1) >> 8) != 0x80) return 1;
        return -1;
    }

    /// <summary>The element bits a weapon, spell or attack script gives its attack (every step that sets or adds them).</summary>
    public static int ElementOf(RomBuffer rom, Kind kind, int index)
    {
        int bits = 0;
        foreach (var s in Load(rom, kind, index))
            if (ElementArg(s) is var a && a >= 0) bits |= L2AiScripts.ArgValue(s, a);
        return bits;
    }

    /// <summary>The types an attack is strong against (bit n = <see cref="Types"/>[n]).</summary>
    public static int StrongAgainst(RomBuffer rom, Kind kind, int index)
    {
        int el = ElementOf(rom, kind, index), bits = 0;
        for (int t = 0; t < Types.Length; t++) if (Types[t].Element != 0 && (el & Types[t].Element) != 0) bits |= 1 << t;
        return bits;
    }

    /// <summary>Adds or takes out a type's element on a weapon, spell or attack script: on, a 17 27 step right after the last
    /// step that sets the element (the first step when none does); off, the bit is cleared everywhere (a 17 left with no
    /// bits goes). False when nothing changed.</summary>
    public static bool SetStrong(RomBuffer rom, Kind kind, int index, int type, bool on)
    {
        int bit = Types[type].Element;
        if (bit == 0) return false;
        var steps = Load(rom, kind, index);
        if (((ElementOf(rom, kind, index) & bit) != 0) == on) return false;
        FixGameChecks(rom);
        if (on)
        {
            int last = steps.FindLastIndex(s => ElementArg(s) >= 0 && s.Op != 0x17);
            if (last < 0) last = steps.FindLastIndex(s => s.Op == 0x37);
            steps.Insert(last + 1, Op(0x17, 0x27, bit));
        }
        else
            foreach (var s in steps.ToList())
            {
                int a = ElementArg(s);
                if (a < 0) continue;
                int v = L2AiScripts.ArgValue(s, a);
                if ((v & bit) == 0) continue;
                if (s.Op == 0x17 && (v & ~bit) == 0) Remove(steps, s);
                else L2AiScripts.SetArg(s, a, v & ~bit);
            }
        Save(rom, kind, index, steps);
        return true;
    }

    /// <summary>Saves the script, or goes back to the game's own when it is the same again (a type ticked and unticked
    /// leaves no edited copy; a monster without a script is the same as one that only does the action).</summary>
    private static void Save(RomBuffer rom, Kind kind, int index, List<Step> steps)
    {
        var orig = L2AiScripts.LoadOriginal(rom, kind, index);
        bool same = orig.Count == 0
            ? steps.Count == 1 && steps[0].Op == 0x00 || kind == Kind.Weapon && steps.Count == 2 && steps[0].Op == 0x37 && steps[1].Op == 0x00
            : L2AiScripts.Build(orig, 0).AsSpan().SequenceEqual(L2AiScripts.Build(steps, 0));
        if (same && L2AiScripts.Override(rom, kind, index) != null) L2AiScripts.Revert(rom, kind, index);
        else if (!same) L2AiScripts.Save(rom, kind, index, steps);
    }
}
