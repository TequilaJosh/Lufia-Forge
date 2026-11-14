using Kind = LufiaForge.Core.Lufia2.L2AiScripts.Kind;
using Step = LufiaForge.Core.Lufia2.L2AiScripts.Step;

namespace LufiaForge.Core.Lufia2;

/// <summary>
/// Element percentages for Lufia II, a table instead of script steps (part of <see cref="L2StatusPatch"/>'s battle code).
/// <para>Each monster, hero and item has a row of 17 signed words: one per element bit (0001 vs beasts ... 8000), then
/// "plain" for an attack with no element. Each is a percent: +22 = 22% more, -42 = 42% less, -100 = nothing, -200 = the
/// hit heals as much as it would hurt. In battle ($85:D1BC, after the target's "when hit" script and before the damage
/// formula) a hit's power is multiplied by (100 + percent) / 100 for each element of the attack, one after the other
/// (fire +50% and light +50% make x2.25), the way the game's own weaknesses double the power before DFP is taken off. A
/// hero's percent is its own row plus the rows of the six items it wears. The capsule monster has none.</para>
/// <para>When the table goes in, the game's weaknesses move into it: every monster's "when hit" script and every armor
/// script is read, the steps that always run and only change the damage by element (the shared scripts 00-10, 14, 17,
/// "counter 2 = bits" + 15 (nothing from those), Lufia Forge's added types) become the row, and they come out of the
/// script (the rest stays: chances, counters, healing hurts, reflecting). Guards that only work some of the time (most
/// shields: "If battle setting is 0") stay in their scripts.</para>
/// </summary>
public static class L2Elements
{
    public const int Plain = 16, Count = 17, RowSize = Count * 2;
    public const int MonsterRows = 0x100, HeroRows = 7, ItemRows = 0x200;
    private const int HeroAt = 0x2200, ItemAt = 0x2300;
    public const int TableSize = ItemAt + ItemRows * RowSize;   // $6700
    public const int Min = -200, Max = 900;

    public enum Who { Monster, Hero, Item }

    /// <summary>The bit of element k (k 16: none, plain).</summary>
    public static int Bit(int k) => k < 16 ? 1 << k : 0;

    /// <summary>Element k's name ("fire", "vs dragons"..., "plain (no element)").</summary>
    public static string Name(int k) => k == Plain ? "plain (no element)" : L2AiScripts.Elements.First(e => e.Bit == 1 << k).Name;

    /// <summary>The order the editor shows them in: the seven elements, plain, then the kinds of attack and the types.</summary>
    public static readonly int[] Order = { 1, 2, 10, 11, 12, 4, 5, Plain, 6, 7, 8, 9, 0, 3, 13, 14, 15 };

    /// <summary>File offset of the table, or -1 (no status patch, or an old one without it).</summary>
    public static int TableAt(RomBuffer rom)
    {
        int at = L2StatusPatch.ElemPtrFile(rom);   // the newest status patch, or the second (upgraded at the next change)
        if (at < 0) return -1;
        int p = (int)rom.ReadUInt24Le(at);
        return (p >> 16) == 0 ? -1 : L2Rom.SnesToFile(p);
    }

    public static bool IsIn(RomBuffer rom) => TableAt(rom) >= 0;

    private static int RowAt(RomBuffer rom, Who who, int index)
    {
        int t = TableAt(rom);
        if (t < 0) return -1;
        return who switch
        {
            Who.Monster when index is >= 0 and < MonsterRows => t + index * RowSize,
            Who.Hero when index is >= 0 and < HeroRows => t + HeroAt + index * RowSize,
            Who.Item when index is >= 0 and < ItemRows => t + ItemAt + index * RowSize,
            _ => -1,
        };
    }

    /// <summary>The row (17 percents). Without the table: what the scripts do now (the row they would become: a monster's
    /// "when hit" script, an item's armor script; a hero has none).</summary>
    public static int[] Row(RomBuffer rom, Who who, int index)
    {
        int at = RowAt(rom, who, index);
        if (at >= 0) return Enumerable.Range(0, Count).Select(k => (int)(short)rom.ReadUInt16Le(at + k * 2)).ToArray();
        if (who == Who.Hero) return new int[Count];
        List<Step> steps;
        try { steps = L2AiScripts.Load(rom, who == Who.Item ? Kind.Armor : Kind.MonsterDefense, index).Steps; } catch (Exception) { return new int[Count]; }
        var (f, _) = TakeElementSteps(steps);
        return f.Select(Percent).ToArray();
    }

    public static int Get(RomBuffer rom, Who who, int index, int k) => Row(rom, who, index)[k];

    /// <summary>Sets one percent (puts the battle code and the table in first). False when nothing changed.</summary>
    public static bool Set(RomBuffer rom, Who who, int index, int k, int percent)
    {
        percent = Math.Clamp(percent, Min, Max);
        if (Get(rom, who, index, k) == percent && IsIn(rom)) return false;
        Install(rom);
        foreach (int i in Sharing(rom, who, index))
            rom.WriteUInt16Le(RowAt(rom, who, i) + k * 2, (ushort)(short)percent);
        return true;
    }

    /// <summary>Monsters that share a record share a script, so their rows are kept the same.</summary>
    private static IEnumerable<int> Sharing(RomBuffer rom, Who who, int index)
    {
        if (who != Who.Monster) return new[] { index };
        int at = L2Battle.MonsterAt(rom, index);
        return Enumerable.Range(0, Math.Min(L2Battle.MonsterCountIn(rom), MonsterRows)).Where(i => i == index || L2Battle.MonsterAt(rom, i) == at).ToList();
    }

    /// <summary>Puts the battle code (<see cref="L2StatusPatch"/>) and the table in, moving the game's weaknesses over.</summary>
    public static void Install(RomBuffer rom)
    {
        if (!L2StatusPatch.IsApplied(rom)) L2StatusPatch.Apply(rom);
    }

    /// <summary>A hit's change for an attack with these element bits against this row, as the battle code works it out
    /// (for the editor and tests): the factor the power is multiplied by.</summary>
    public static double Factor(int[] row, int elements)
    {
        double f = 1;
        if (elements == 0) return (100 + row[Plain]) / 100.0;
        for (int k = 0; k < 16; k++) if ((elements & 1 << k) != 0) f *= (100 + row[k]) / 100.0;
        return f;
    }

    // ── moving the game's weaknesses into the table ───────────────────────────

    /// <summary>What the shared scripts that only change the damage by element do, as (element, factor) pairs.</summary>
    private static (int K, double F)[]? LibraryEffect(int n) => n switch
    {
        0x00 => new[] { (1, 2.0) }, 0x01 => new[] { (11, 2.0) }, 0x02 => new[] { (2, 2.0) }, 0x03 => new[] { (10, 2.0) },
        0x04 => new[] { (7, 2.0), (12, 0.0) }, 0x05 => new[] { (4, 2.0) },
        0x06 => new[] { (1, 0.5) }, 0x07 => new[] { (11, 0.5) }, 0x08 => new[] { (2, 0.5) }, 0x09 => new[] { (10, 0.5) },
        0x0A => new[] { (4, 0.5) }, 0x0B => new[] { (Plain, 0.5) }, 0x0C => new[] { (8, 2.0) }, 0x0D => new[] { (5, 2.0) },
        0x0E => new[] { (5, 0.5) }, 0x0F => new[] { (6, 2.0) }, 0x10 => new[] { (9, 2.0) }, 0x17 => new[] { (6, 0.5) },
        _ => null,
    };

    private static int V(Step s, int i) => L2AiScripts.ArgValue(s, i);
    private static bool IsEnd(Step s) => s.Op is 0x00 or 0x4F or 0x03 or 0x43;
    private static bool IsDamageCopy(Step s) => s.Op == 0x0C && V(s, 0) == 0x81 && V(s, 1) == 0x802A;          // counter 2 = the damage
    private static bool IsDamageCompare(Step s) => s.Op == 0x07 && V(s, 0) == 0x2A && V(s, 1) == 0x8081;     // if the damage is not counter 2

    /// <summary>Which steps run every time the script runs (no earlier end, no earlier jump past them; "if the damage
    /// changed" jumps, the game's way of letting one weakness skip the rest, don't count: the table works them all out).</summary>
    private static bool[] AlwaysRun(List<Step> steps)
    {
        var always = new bool[steps.Count];
        int reach = 0;   // the furthest step an earlier jump can land on
        bool ended = false;
        for (int i = 0; i < steps.Count; i++)
        {
            always[i] = !ended && reach <= i;
            var s = steps[i];
            if (!IsDamageCompare(s))
                foreach (var t in s.Targets)
                    if (t != null) reach = Math.Max(reach, steps.IndexOf(t));
            if (IsEnd(s)) ended = true;
        }
        return always;
    }

    /// <summary>"counter 2 = bits" at <paramref name="i"/>, any "counter 2: add bits" after it, then shared script 15, all
    /// run every time: the bits and the last step's index, else null.</summary>
    private static (int Bits, int Last)? ImmuneRun(List<Step> steps, int i, bool[] always)
    {
        int bits = V(steps[i], 1), j = i + 1;
        while (j < steps.Count && steps[j].Op == 0x17 && V(steps[j], 0) == 0x81 && (V(steps[j], 1) >> 8) != 0x80) { bits |= V(steps[j], 1); j++; }
        return j < steps.Count && always[j] && steps[j].Op == 0x42 && steps[j].Args[0] == 0x15 ? (bits, j) : null;
    }

    /// <summary>The game's elemental shields: a chance (the first step) to take nothing from an element ("counter 2 = bit",
    /// shared script 19, end), else half from it (the step at <paramref name="i"/>). Moving the half into the table works
    /// the same: nothing halved is still nothing.</summary>
    private static bool ShieldHalf(List<Step> steps, int i)
    {
        if (i != 4 || steps.Count < 5) return false;
        Step gate = steps[0], set = steps[1], nineteen = steps[2], end = steps[3], half = steps[4];
        if (!gate.Targets.Contains(half) || set.Op != 0x0C || V(set, 0) != 0x81 || nineteen.Op != 0x42 || nineteen.Args[0] != 0x19 || end.Op != 0x00) return false;
        if (half.Op != 0x42 || LibraryEffect(half.Args[0]) is not { Length: 1 } fx || fx[0].F != 0.5) return false;
        return V(set, 1) == Bit(fx[0].K) && steps.Skip(5).All(st => st.Targets.All(t => t == null || steps.IndexOf(t) > 4));
    }

    /// <summary>Takes the element steps out of <paramref name="steps"/>: returns the factors (per element, 1 = none) and
    /// whether anything came out.</summary>
    public static (double[] Factors, bool Changed) TakeElementSteps(List<Step> steps)
    {
        var f = Enumerable.Repeat(1.0, Count).ToArray();
        var set = new bool[Count];
        var drop = new HashSet<Step>();
        bool all = false;   // shared script 14: every element without its own entry does half
        var always = AlwaysRun(steps);
        for (int i = 0; i < steps.Count; i++)
        {
            var s = steps[i];
            if (!always[i] && !ShieldHalf(steps, i)) continue;
            if (s.Op == 0x42 && LibraryEffect(s.Args[0]) is { } fx)
            {
                foreach (var (k, x) in fx) { f[k] *= x; set[k] = true; }
                drop.Add(s);
            }
            else if (s.Op == 0x42 && s.Args[0] == 0x14) { all = true; drop.Add(s); }
            else if (s.Op == 0x0C && V(s, 0) == 0x81 && (V(s, 1) >> 8) != 0x80 && V(s, 1) != 0 && ImmuneRun(steps, i, always) is { } run)
            {
                // counter 2 = bits (more added with 17), then 15: those elements do nothing
                int bits = run.Bits;
                for (int k = 0; k < 16; k++) if ((bits & 1 << k) != 0) { f[k] = 0; set[k] = true; }
                for (int j = i; j <= run.Last; j++) drop.Add(steps[j]);
                i = run.Last;
            }
            else if (L2MonsterTypes.AddedTypeAt(steps, i) is int bit and > 0)
            {
                int k = System.Numerics.BitOperations.TrailingZeroCount(bit);
                f[k] *= 2; set[k] = true;
                for (int j = 0; j < 4; j++) drop.Add(steps[i + j]);
                i += 3;
            }
        }
        if (all) for (int k = 0; k < 16; k++) if (!set[k]) f[k] *= 0.5;
        if (drop.Count == 0) return (f, false);
        foreach (var s in drop) Remove(steps, s);
        // a "counter 2 = the damage" with its "if the damage changed" right after it has nothing left to compare
        for (int i = 0; i + 1 < steps.Count; i++)
            if (IsDamageCopy(steps[i]) && IsDamageCompare(steps[i + 1])) { Remove(steps, steps[i + 1]); Remove(steps, steps[i]); i--; }
        return (f, true);
    }

    private static void Remove(List<Step> steps, Step old)
    {
        int i = steps.IndexOf(old);
        if (i < 0) return;
        steps.RemoveAt(i);
        var next = steps.ElementAtOrDefault(i) ?? steps.LastOrDefault();
        foreach (var o in steps)
            for (int j = 0; j < o.Targets.Length; j++)
                if (o.Targets[j] == old) o.Targets[j] = next;
    }

    /// <summary>A factor as the row's percent (x2 = +100, x0.5 = -50, x0 = -100).</summary>
    public static int Percent(double factor) => (int)Math.Round(factor * 100) - 100;

    /// <summary>Moves every monster's and item's element steps into the table (once, when the table goes in). Returns
    /// how many scripts changed.</summary>
    internal static int MoveAll(RomBuffer rom)
    {
        int changed = 0;
        var seen = new HashSet<int>();
        int monsters = Math.Min(L2Battle.MonsterCountIn(rom), MonsterRows);
        for (int m = 0; m < monsters; m++)
        {
            int rec = L2Battle.MonsterAt(rom, m);
            if (rec < 0 || !seen.Add(rec)) continue;
            if (Move(rom, Kind.MonsterDefense, Who.Monster, m)) changed++;
        }
        int items = Math.Min(L2Battle.ItemCountIn(rom), ItemRows);
        for (int i = 0; i < items; i++)
            if (Move(rom, Kind.Armor, Who.Item, i)) changed++;
        return changed;
    }

    private static bool Move(RomBuffer rom, Kind kind, Who who, int index)
    {
        List<Step> steps;
        try { steps = L2AiScripts.Load(rom, kind, index).Steps; } catch (Exception) { return false; }
        if (steps.Count == 0) return false;
        var (f, changed) = TakeElementSteps(steps);
        if (!changed) return false;
        WriteFactors(rom, who, index, f);
        if (steps.Count == 0) steps.Add(L2AiScripts.New(0x00));
        L2AiScripts.Save(rom, kind, index, steps);
        return true;
    }

    /// <summary>Multiplies the row by the factors (a script step that came out).</summary>
    internal static void WriteFactors(RomBuffer rom, Who who, int index, double[] f)
    {
        foreach (int i in Sharing(rom, who, index))
        {
            int at = RowAt(rom, who, i);
            for (int k = 0; k < Count; k++)
            {
                if (f[k] == 1) continue;
                double now = (100 + (short)rom.ReadUInt16Le(at + k * 2)) / 100.0;
                rom.WriteUInt16Le(at + k * 2, (ushort)(short)Math.Clamp(Percent(now * f[k]), Min, Max));
            }
        }
    }

    /// <summary>A "when hit" or armor script about to be saved when the table has just gone in: its element steps go to the
    /// row too, so they don't count twice.</summary>
    internal static List<Step> MoveIncoming(RomBuffer rom, Kind kind, int index, IReadOnlyList<Step> steps)
    {
        if (kind is not (Kind.MonsterDefense or Kind.Armor)) return steps.ToList();
        var copy = steps.ToList();
        var (f, changed) = TakeElementSteps(copy);
        if (!changed) return copy;
        // the row was just made from the script in the ROM; this one replaces it
        var who = kind == Kind.Armor ? Who.Item : Who.Monster;
        foreach (int i in Sharing(rom, who, index)) rom.WriteBytes(RowAt(rom, who, i), new byte[RowSize]);
        WriteFactors(rom, who, index, f);
        if (copy.Count == 0) copy.Add(L2AiScripts.New(0x00));
        return copy;
    }
}
