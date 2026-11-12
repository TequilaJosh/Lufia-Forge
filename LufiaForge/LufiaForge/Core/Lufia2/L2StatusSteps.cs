using Kind = LufiaForge.Core.Lufia2.L2AiScripts.Kind;
using Step = LufiaForge.Core.Lufia2.L2AiScripts.Step;

namespace LufiaForge.Core.Lufia2;

/// <summary>
/// The new statuses (<see cref="L2StatusPatch"/>) a battle script gives: its 5D (causes), 5E (cures), 5F (resists) and
/// 60 (wards) steps. They only set chances for the action (or this target), so they go at the top of the script, where
/// every path runs them; a change rewrites that step and saves the script (Lufia Forge's copy; the game's stays).
/// </summary>
public static class L2StatusSteps
{
    public enum What { Cause = 0x5D, Cure = 0x5E, Resist = 0x5F, Ward = 0x60 }

    private static List<Step> Steps(RomBuffer rom, Kind kind, int index)
    {
        List<Step> steps;
        try { steps = L2AiScripts.Load(rom, kind, index).Steps; } catch (Exception) { steps = new(); }
        if (steps.Count > 0) return steps;
        // no script: what the game does then (a weapon attacks, a monster attacks a random hero)
        return kind switch
        {
            Kind.Weapon => new() { L2AiScripts.New(0x37), L2AiScripts.New(0x00) },
            Kind.MonsterAttack => new() { Op(0x32, 1), L2AiScripts.New(0x28), L2AiScripts.New(0x00) },
            _ => new() { L2AiScripts.New(0x00) },
        };
    }

    private static Step Op(byte op, params int[] v)
    {
        var s = L2AiScripts.New(op);
        for (int i = 0; i < v.Length; i++) L2AiScripts.SetArg(s, i, v[i]);
        return s;
    }

    /// <summary>The chance (or for a ward, 100 when warded) the script gives status <paramref name="status"/>; 0 = none.</summary>
    public static int Get(RomBuffer rom, Kind kind, int index, What what, int status)
    {
        List<Step> steps;
        try { steps = L2AiScripts.Load(rom, kind, index).Steps; } catch (Exception) { return 0; }
        var s = steps.FirstOrDefault(x => x.Op == (byte)what && x.Args.Length > 0 && x.Args[0] == status);
        return s == null ? 0 : what == What.Ward ? 100 : s.Args[1];
    }

    /// <summary>All 16 at once (index by status).</summary>
    public static int[] GetAll(RomBuffer rom, Kind kind, int index, What what) =>
        Enumerable.Range(0, 16).Select(st => Get(rom, kind, index, what, st)).ToArray();

    /// <summary>Sets it (0 takes the step out). False when nothing changed.</summary>
    public static bool Set(RomBuffer rom, Kind kind, int index, What what, int status, int value)
    {
        value = Math.Clamp(value, 0, 100);
        if (Get(rom, kind, index, what, status) == value) return false;
        var steps = Steps(rom, kind, index);
        foreach (var old in steps.Where(x => x.Op == (byte)what && x.Args.Length > 0 && x.Args[0] == status).ToList())
        {
            int i = steps.IndexOf(old);
            steps.RemoveAt(i);
            var next = steps.ElementAtOrDefault(i) ?? steps.LastOrDefault();
            foreach (var o in steps)
                for (int j = 0; j < o.Targets.Length; j++)
                    if (o.Targets[j] == old) o.Targets[j] = next;
        }
        if (value > 0) steps.Insert(0, what == What.Ward ? Op((byte)what, status) : Op((byte)what, status, value));
        L2AiScripts.Save(rom, kind, index, steps);
        return true;
    }
}
