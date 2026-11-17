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

    /// <summary>The script's steps; none: what the game does then. <paramref name="write"/>: a script that can't be
    /// read is refused (the stand-in, saved over it, replaced the monster's real script).</summary>
    private static List<Step> Steps(RomBuffer rom, Kind kind, int index, bool write = false)
    {
        List<Step> steps;
        try { steps = L2AiScripts.Load(rom, kind, index).Steps; }
        catch (Exception) when (!write) { steps = new(); }
        catch (Exception ex) { throw new InvalidOperationException("The script can't be read (a command Lufia Forge doesn't know, or a go to into the middle of one), so it isn't changed: a stand-in would replace it. Look at it in its script window.", ex); }
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

    /// <summary>Whether the script has a step <paramref name="op"/> (with first argument <paramref name="arg"/> when not -1):
    /// the game's "cause / cure a status" (27 / 26) for Bounce, Lufia Forge's 61 (focus).</summary>
    public static bool HasStep(RomBuffer rom, Kind kind, int index, byte op, int arg = -1)
    {
        List<Step> steps;
        try { steps = L2AiScripts.Load(rom, kind, index).Steps; } catch (Exception) { return false; }
        return steps.Any(x => x.Op == op && (arg < 0 || x.Args.Length > 0 && x.Args[0] == arg));
    }

    /// <summary>Puts a step at the top of the script (or takes every such step out). False when nothing changed.</summary>
    public static bool SetStep(RomBuffer rom, Kind kind, int index, bool on, byte op, params int[] args)
    {
        int arg = args.Length > 0 ? args[0] : -1;
        if (HasStep(rom, kind, index, op, arg) == on) return false;
        var steps = Steps(rom, kind, index, write: true);
        foreach (var old in steps.Where(x => x.Op == op && (arg < 0 || x.Args.Length > 0 && x.Args[0] == arg)).ToList())
        {
            int i = steps.IndexOf(old);
            steps.RemoveAt(i);
            var next = steps.ElementAtOrDefault(i) ?? steps.LastOrDefault();
            foreach (var o in steps)
                for (int j = 0; j < o.Targets.Length; j++)
                    if (o.Targets[j] == old) o.Targets[j] = next;
        }
        if (on) steps.Insert(0, Op(op, args));
        L2AiScripts.Save(rom, kind, index, steps);
        return true;
    }

    /// <summary>Sets it (0 takes the step out). False when nothing changed.</summary>
    public static bool Set(RomBuffer rom, Kind kind, int index, What what, int status, int value)
    {
        value = Math.Clamp(value, 0, 100);
        if (Get(rom, kind, index, what, status) == value) return false;
        var steps = Steps(rom, kind, index, write: true);
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
