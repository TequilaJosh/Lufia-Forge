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
/// With Lufia Forge's element table (<see cref="L2Elements"/>) a type is a set of element percents in the monster's
/// row (the game's shared scripts move there when the table goes in); Undead also keeps shared script 12 in the script
/// (healing hurts, no instant death). Lufia Forge adds four types of its own on element bits no script uses (0001 vs
/// beasts, 0008 vs aquatic, 2000 vs plants, 4000 vs humanoids); a weapon or spell is strong against a type when its
/// attack has the type's element (a 17 27 step after the step that sets it). The game's checks for dragons, insects,
/// flying and hard shells wanted that element alone; scripts that still run them get versions that test the bit
/// (<see cref="FixGameChecks"/>).
/// </summary>
public static class L2MonsterTypes
{
    /// <param name="Values">The element percents that make it (element index as <see cref="L2Elements"/>, percent).</param>
    /// <param name="Element">The element that is strong against it (0 = none).</param>
    /// <param name="Undead">Also healing hurts it and instant death can't work (shared script 12 in its "when hit" script).</param>
    public sealed record TypeDef(string Name, string Help, (int K, int Pct)[] Values, int Element, bool Undead = false);

    /// <summary>The register the added types' check worked in before the table: shared value 12 ($7F:F432). No script of
    /// the game and no battle code uses it; the bit-test versions of the game's checks use it too.</summary>
    public const int Work = 0x12;

    private static readonly (int, int)[] AllHalf = { (1, -50), (2, -50), (10, -50), (11, -50), (12, -50), (4, -50), (5, -50) };

    public static readonly TypeDef[] Types =
    {
        new("Dragon", "Dragon-slaying attacks (the element \"vs dragons\": Dragon tooth, Lizard blow, the dragon IP attacks) do double damage: vs dragons +100%.", new[] { (8, 100) }, 0x0100),
        new("Insect", "Insect-slaying attacks (\"vs insects\": Insect crush) do double damage: vs insects +100%.", new[] { (9, 100) }, 0x0200),
        new("Undead", "Healing hurts it and instant death can't work on it (shared script 12 in its 'when hit' script), and light does double damage: light +100%.", new[] { (4, 100) }, 0x0010, true),
        new("Flying", "Thrown weapons (boomerangs) do double damage and earth does nothing: thrown +100%, earth -100%.", new[] { (7, 100), (12, -100) }, 0x0080),
        new("Hard shell", "Blunt weapons (maces, rods) do double damage: blunt +100%.", new[] { (6, 100) }, 0x0040),
        new("Golem", "Every element does half damage: fire, water, ice, thunder, earth, light and dark -50%.", AllHalf, 0),
        new("Shadow", "Plain attacks (no element) do half damage: plain -50%.", new[] { (L2Elements.Plain, -50) }, 0),
        new("Beast", "Added by Lufia Forge: attacks with the element \"vs beasts\" do double damage: vs beasts +100%.", new[] { (0, 100) }, 0x0001),
        new("Aquatic", "Added by Lufia Forge: attacks with the element \"vs aquatic\" do double damage: vs aquatic +100%.", new[] { (3, 100) }, 0x0008),
        new("Plant", "Added by Lufia Forge: attacks with the element \"vs plants\" do double damage: vs plants +100%.", new[] { (13, 100) }, 0x2000),
        new("Humanoid", "Added by Lufia Forge: attacks with the element \"vs humanoids\" do double damage: vs humanoids +100%.", new[] { (14, 100) }, 0x4000),
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

    /// <summary>The element bit of the 4-step check Lufia Forge's types used before the table (0C 12 =element / 16 12 bit
    /// / 06 12 0 skip / 0F 2A 2) when it starts at step <paramref name="i"/>, else -1 (the table takes them over).</summary>
    public static int AddedTypeAt(List<Step> steps, int i)
    {
        if (i < 0 || i + 3 >= steps.Count) return -1;
        Step a = steps[i], b = steps[i + 1], c = steps[i + 2], d = steps[i + 3];
        if (a.Op == 0x0C && L2AiScripts.ArgValue(a, 0) == Work && L2AiScripts.ArgValue(a, 1) == 0x8027
            && b.Op == 0x16 && L2AiScripts.ArgValue(b, 0) == Work
            && c.Op == 0x06 && L2AiScripts.ArgValue(c, 0) == Work && L2AiScripts.ArgValue(c, 1) == 0 && c.Targets[0] == steps.ElementAtOrDefault(i + 4)
            && d.Op == 0x0F && L2AiScripts.ArgValue(d, 0) == 0x2A && L2AiScripts.ArgValue(d, 1) == 2)
            return L2AiScripts.ArgValue(b, 1);
        return -1;
    }

    /// <summary>Whether monster <paramref name="monster"/> is that type: its element row has the type's percents (and for
    /// Undead its "when hit" script runs shared script 12).</summary>
    private static bool Has(RomBuffer rom, int monster, TypeDef t)
    {
        var row = L2Elements.Row(rom, L2Elements.Who.Monster, monster);
        if (t.Values.Any(v => row[v.K] != v.Pct)) return false;
        return !t.Undead || Load(rom, Kind.MonsterDefense, monster).Any(s => Calls(s, 0x12));
    }

    /// <summary>The types a monster is (bit n = <see cref="Types"/>[n]).</summary>
    public static int Of(RomBuffer rom, int monster)
    {
        int bits = 0;
        for (int t = 0; t < Types.Length; t++) if (Has(rom, monster, Types[t])) bits |= 1 << t;
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

    /// <summary>Makes the monster that type or not: on writes the type's percents into its element row, off sets them back
    /// to 0 (Undead also adds or takes out shared script 12 in its "when hit" script). Puts the battle code and the table
    /// in first. False when nothing changed.</summary>
    public static bool Set(RomBuffer rom, int monster, int type, bool on)
    {
        var t = Types[type];
        if (Has(rom, monster, t) == on) return false;
        L2Elements.Install(rom);
        FixGameChecks(rom);
        foreach (var (k, pct) in t.Values) L2Elements.Set(rom, L2Elements.Who.Monster, monster, k, on ? pct : 0);
        if (t.Undead)
        {
            var steps = Load(rom, Kind.MonsterDefense, monster);
            if (on && !steps.Any(s => Calls(s, 0x12))) steps.Insert(0, Op(0x42, 0x12));
            if (!on) foreach (var s in steps.Where(s => Calls(s, 0x12)).ToList()) Remove(steps, s);
            Save(rom, Kind.MonsterDefense, monster, steps);
        }
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
