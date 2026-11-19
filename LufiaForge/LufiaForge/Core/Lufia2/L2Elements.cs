using Kind = LufiaForge.Core.Lufia2.L2AiScripts.Kind;
using Step = LufiaForge.Core.Lufia2.L2AiScripts.Step;

namespace LufiaForge.Core.Lufia2;

// element percentages for lufia 2, a table instead of script steps (part of L2StatusPatch's battle code)
// each monster, hero and item has a row of 17 signed words: one per element bit (0001 vs beasts ... 8000), then
// "plain" for an attack with no element. each one is a percent: +22 = 22% more, -42 = 42% less, -100 = nothing,
// -200 = the hit heals as much as it would hurt. in battle ($85:D1BC, after the target's "when hit" script and before
// the damage formula) a hit's power gets multiplied by (100 + percent) / 100 for each element of the attack, one after
// the other (fire +50% and light +50% make x2.25), the same way the game's own weaknesses double the power before DFP
// is taken off. a hero's percent is its own row plus the rows of the six items it wears. the capsule monster has none
//
// when the table goes in, the game's weaknesses move into it: we read every monster's "when hit" script and every
// armor script, and the steps that always run and only change the damage by element (the shared scripts 00-10, 14,
// 17, "counter 2 = bits" + 15 (nothing from those), our added types) become the row and come out of the script (the
// rest stays: chances, counters, healing hurts, reflecting). guards that only work some of the time (most shields:
// "If battle setting is 0") stay in their scripts
public static class L2Elements
{
    public const int Plain = 16, Count = 17, RowSize = Count * 2;
    public const int MonsterRows = 0x100, HeroRows = 7, ItemRows = 0x200;
    private const int HeroAt = 0x2200, ItemAt = 0x2300;
    public const int TableSize = ItemAt + ItemRows * RowSize;   // $6700
    public const int Min = -200, Max = 900;

    public enum Who { Monster, Hero, Item }

    // the bit of element k (k 16 = none, plain)
    public static int Bit(int k) => k < 16 ? 1 << k : 0;

    // element k's name ("fire", "vs dragons"..., "plain (no element)")
    public static string Name(int k) => k == Plain ? "plain (no element)" : L2AiScripts.Elements.First(e => e.Bit == 1 << k).Name;

    // the order the editor shows them in: the seven elements, plain, then the kinds of attack and the types
    public static readonly int[] Order = { 1, 2, 10, 11, 12, 4, 5, Plain, 6, 7, 8, 9, 0, 3, 13, 14, 15 };

    // file offset of the table, or -1 (no status patch, or an old one without it)
    public static int TableAt(RomBuffer rom)
    {
        int pointerAt = L2StatusPatch.ElemPtrFile(rom);   // the newest status patch, or the second (upgraded at the next change)
        if (pointerAt < 0) return -1;
        int pointer = (int)rom.ReadUInt24Le(pointerAt);
        return (pointer >> 16) == 0 ? -1 : L2Rom.SnesToFile(pointer);
    }

    public static bool IsIn(RomBuffer rom) => TableAt(rom) >= 0;

    private static int RowAt(RomBuffer rom, Who who, int index)
    {
        int table = TableAt(rom);
        if (table < 0) return -1;
        return who switch
        {
            Who.Monster when index is >= 0 and < MonsterRows => table + index * RowSize,
            Who.Hero when index is >= 0 and < HeroRows => table + HeroAt + index * RowSize,
            Who.Item when index is >= 0 and < ItemRows => table + ItemAt + index * RowSize,
            _ => -1,
        };
    }

    // the row (17 percents). without the table: what the scripts do now (the row they would become: a monster's
    // "when hit" script, an item's armor script, a hero has none)
    public static int[] Row(RomBuffer rom, Who who, int index)
    {
        int rowAt = RowAt(rom, who, index);
        if (rowAt >= 0) return Enumerable.Range(0, Count).Select(k => (int)(short)rom.ReadUInt16Le(rowAt + k * 2)).ToArray();
        if (who == Who.Hero) return new int[Count];
        List<Step> steps;
        try { steps = L2AiScripts.Load(rom, who == Who.Item ? Kind.Armor : Kind.MonsterDefense, index).Steps; } catch (Exception) { return new int[Count]; }
        var (factors, _) = TakeElementSteps(steps);
        return factors.Select(Percent).ToArray();
    }

    public static int Get(RomBuffer rom, Who who, int index, int k) => Row(rom, who, index)[k];

    // sets one percent (puts the battle code and the table in first). false if nothing changed
    public static bool Set(RomBuffer rom, Who who, int index, int k, int percent)
    {
        percent = Math.Clamp(percent, Min, Max);
        if (Get(rom, who, index, k) == percent && IsIn(rom)) return false;
        Install(rom);
        foreach (int i in Sharing(rom, who, index))
            rom.WriteUInt16Le(RowAt(rom, who, i) + k * 2, (ushort)(short)percent);
        return true;
    }

    // monsters that share a record share a script, so their rows are kept the same
    private static IEnumerable<int> Sharing(RomBuffer rom, Who who, int index)
    {
        if (who != Who.Monster) return new[] { index };
        int recordAt = L2Battle.MonsterAt(rom, index);
        return Enumerable.Range(0, Math.Min(L2Battle.MonsterCountIn(rom), MonsterRows)).Where(i => i == index || L2Battle.MonsterAt(rom, i) == recordAt).ToList();
    }

    // puts the battle code (L2StatusPatch) and the table in, moving the game's weaknesses over
    public static void Install(RomBuffer rom)
    {
        if (!L2StatusPatch.IsApplied(rom)) L2StatusPatch.Apply(rom);
    }

    // a hit's change for an attack with these element bits against this row, worked out like the battle code does
    // (for the editor and tests): the factor the power gets multiplied by
    public static double Factor(int[] row, int elements)
    {
        double factor = 1;
        if (elements == 0) return (100 + row[Plain]) / 100.0;
        for (int k = 0; k < 16; k++) if ((elements & 1 << k) != 0) factor *= (100 + row[k]) / 100.0;
        return factor;
    }

    // moving the game's weaknesses into the table

    // what the shared scripts that only change the damage by element do, as (element, factor) pairs
    private static (int K, double F)[]? LibraryEffect(int n) => n switch
    {
        0x00 => new[] { (1, 2.0) }, 0x01 => new[] { (11, 2.0) }, 0x02 => new[] { (2, 2.0) }, 0x03 => new[] { (10, 2.0) },
        0x04 => new[] { (7, 2.0), (12, 0.0) }, 0x05 => new[] { (4, 2.0) },
        0x06 => new[] { (1, 0.5) }, 0x07 => new[] { (11, 0.5) }, 0x08 => new[] { (2, 0.5) }, 0x09 => new[] { (10, 0.5) },
        0x0A => new[] { (4, 0.5) }, 0x0B => new[] { (Plain, 0.5) }, 0x0C => new[] { (8, 2.0) }, 0x0D => new[] { (5, 2.0) },
        0x0E => new[] { (5, 0.5) }, 0x0F => new[] { (6, 2.0) }, 0x10 => new[] { (9, 2.0) }, 0x17 => new[] { (6, 0.5) },
        _ => null,
    };

    private static int V(Step step, int i) => L2AiScripts.ArgValue(step, i);
    private static bool IsEnd(Step step) => step.Op is 0x00 or 0x4F or 0x03 or 0x43;
    private static bool IsDamageCopy(Step step) => step.Op == 0x0C && V(step, 0) == 0x81 && V(step, 1) == 0x802A;          // counter 2 = the damage
    private static bool IsDamageCompare(Step step) => step.Op == 0x07 && V(step, 0) == 0x2A && V(step, 1) == 0x8081;     // if the damage is not counter 2

    // which steps run every time the script runs (no earlier end, no earlier jump past them). "if the damage changed"
    // jumps, the game's way of letting one weakness skip the rest, don't count: the table works them all out
    private static bool[] AlwaysRun(List<Step> steps)
    {
        var always = new bool[steps.Count];
        int reach = 0;   // the furthest step an earlier jump can land on
        bool ended = false;
        for (int i = 0; i < steps.Count; i++)
        {
            always[i] = !ended && reach <= i;
            var step = steps[i];
            if (!IsDamageCompare(step))
                foreach (var target in step.Targets)
                    if (target != null) reach = Math.Max(reach, steps.IndexOf(target));
            if (IsEnd(step)) ended = true;
        }
        return always;
    }

    // "counter 2 = bits" at i, any "counter 2: add bits" after it, then shared script 15, all running every time:
    // the bits and the last step's index, else null
    private static (int Bits, int Last)? ImmuneRun(List<Step> steps, int i, bool[] always)
    {
        int bits = V(steps[i], 1), j = i + 1;
        while (j < steps.Count && steps[j].Op == 0x17 && V(steps[j], 0) == 0x81 && (V(steps[j], 1) >> 8) != 0x80) { bits |= V(steps[j], 1); j++; }
        return j < steps.Count && always[j] && steps[j].Op == 0x42 && steps[j].Args[0] == 0x15 ? (bits, j) : null;
    }

    // the game's elemental shields: a chance (the first step) to take nothing from an element ("counter 2 = bit",
    // shared script 19, end), else half from it (the step at i). moving the half into the table works the same:
    // nothing halved is still nothing
    private static bool ShieldHalf(List<Step> steps, int i)
    {
        if (i != 4 || steps.Count < 5) return false;
        Step gate = steps[0], set = steps[1], nineteen = steps[2], end = steps[3], half = steps[4];
        if (!gate.Targets.Contains(half) || set.Op != 0x0C || V(set, 0) != 0x81 || nineteen.Op != 0x42 || nineteen.Args[0] != 0x19 || end.Op != 0x00) return false;
        if (half.Op != 0x42 || LibraryEffect(half.Args[0]) is not { Length: 1 } effect || effect[0].F != 0.5) return false;
        return V(set, 1) == Bit(effect[0].K) && steps.Skip(5).All(st => st.Targets.All(t => t == null || steps.IndexOf(t) > 4));
    }

    // takes the element steps out of steps: returns the factors (per element, 1 = none) and whether anything came out
    public static (double[] Factors, bool Changed) TakeElementSteps(List<Step> steps)
    {
        var factors = Enumerable.Repeat(1.0, Count).ToArray();
        var set = new bool[Count];
        var drop = new HashSet<Step>();
        bool all = false;   // shared script 14: every element without its own entry does half
        var always = AlwaysRun(steps);
        for (int i = 0; i < steps.Count; i++)
        {
            var step = steps[i];
            if (!always[i] && !ShieldHalf(steps, i)) continue;
            if (step.Op == 0x42 && LibraryEffect(step.Args[0]) is { } effect)
            {
                foreach (var (k, factor) in effect) { factors[k] *= factor; set[k] = true; }
                drop.Add(step);
            }
            else if (step.Op == 0x42 && step.Args[0] == 0x14) { all = true; drop.Add(step); }
            else if (step.Op == 0x0C && V(step, 0) == 0x81 && (V(step, 1) >> 8) != 0x80 && V(step, 1) != 0 && ImmuneRun(steps, i, always) is { } run)
            {
                // counter 2 = bits (more added with 17), then 15: those elements do nothing
                int bits = run.Bits;
                for (int k = 0; k < 16; k++) if ((bits & 1 << k) != 0) { factors[k] = 0; set[k] = true; }
                for (int j = i; j <= run.Last; j++) drop.Add(steps[j]);
                i = run.Last;
            }
            else if (L2MonsterTypes.AddedTypeAt(steps, i) is int bit and > 0)
            {
                int k = System.Numerics.BitOperations.TrailingZeroCount(bit);
                factors[k] *= 2; set[k] = true;
                for (int j = 0; j < 4; j++) drop.Add(steps[i + j]);
                i += 3;
            }
        }
        if (all) for (int k = 0; k < 16; k++) if (!set[k]) factors[k] *= 0.5;
        if (drop.Count == 0) return (factors, false);
        foreach (var step in drop) Remove(steps, step);
        // a "counter 2 = the damage" with its "if the damage changed" right after it has nothing left to compare
        for (int i = 0; i + 1 < steps.Count; i++)
            if (IsDamageCopy(steps[i]) && IsDamageCompare(steps[i + 1])) { Remove(steps, steps[i + 1]); Remove(steps, steps[i]); i--; }
        return (factors, true);
    }

    private static void Remove(List<Step> steps, Step old)
    {
        int i = steps.IndexOf(old);
        if (i < 0) return;
        steps.RemoveAt(i);
        var next = steps.ElementAtOrDefault(i) ?? steps.LastOrDefault();
        foreach (var other in steps)
            for (int j = 0; j < other.Targets.Length; j++)
                if (other.Targets[j] == old) other.Targets[j] = next;
    }

    // a factor as the row's percent (x2 = +100, x0.5 = -50, x0 = -100)
    public static int Percent(double factor) => (int)Math.Round(factor * 100) - 100;

    // moves every monster's and item's element steps into the table (once, when the table goes in). returns how many
    // scripts changed
    internal static int MoveAll(RomBuffer rom)
    {
        int changed = 0;
        var seen = new HashSet<int>();
        int monsters = Math.Min(L2Battle.MonsterCountIn(rom), MonsterRows);
        for (int m = 0; m < monsters; m++)
        {
            int recordAt = L2Battle.MonsterAt(rom, m);
            if (recordAt < 0 || !seen.Add(recordAt)) continue;
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
        // a script that can't be saved again (a go to into the middle of a command, some fan hacks) keeps its element
        // steps and its row stays the same. writing the row first and then failing the save counted the element twice
        if (steps.Any(st => st.OutsideTargets.Any(o => o >= 0))) return false;
        var (factors, changed) = TakeElementSteps(steps);
        if (!changed) return false;
        WriteFactors(rom, who, index, factors);
        if (steps.Count == 0) steps.Add(L2AiScripts.New(0x00));
        L2AiScripts.Save(rom, kind, index, steps);
        return true;
    }

    // multiplies the row by the factors (a script step that came out)
    internal static void WriteFactors(RomBuffer rom, Who who, int index, double[] factors)
    {
        foreach (int i in Sharing(rom, who, index))
        {
            int rowAt = RowAt(rom, who, i);
            for (int k = 0; k < Count; k++)
            {
                if (factors[k] == 1) continue;
                double now = (100 + (short)rom.ReadUInt16Le(rowAt + k * 2)) / 100.0;
                rom.WriteUInt16Le(rowAt + k * 2, (ushort)(short)Math.Clamp(Percent(now * factors[k]), Min, Max));
            }
        }
    }

    // a "when hit" or armor script about to be saved right after the table went in: its element steps go to the row
    // too, so they don't count twice
    internal static List<Step> MoveIncoming(RomBuffer rom, Kind kind, int index, IReadOnlyList<Step> steps)
    {
        if (kind is not (Kind.MonsterDefense or Kind.Armor)) return steps.ToList();
        var copy = steps.ToList();
        var (factors, changed) = TakeElementSteps(copy);
        if (!changed) return copy;
        // the row was just made from the script in the rom, this one replaces it
        var who = kind == Kind.Armor ? Who.Item : Who.Monster;
        foreach (int i in Sharing(rom, who, index)) rom.WriteBytes(RowAt(rom, who, i), new byte[RowSize]);
        WriteFactors(rom, who, index, factors);
        if (copy.Count == 0) copy.Add(L2AiScripts.New(0x00));
        return copy;
    }
}
