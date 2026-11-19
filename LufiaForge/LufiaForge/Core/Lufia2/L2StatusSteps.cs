using Kind = LufiaForge.Core.Lufia2.L2AiScripts.Kind;
using Step = LufiaForge.Core.Lufia2.L2AiScripts.Step;

namespace LufiaForge.Core.Lufia2;

// the new statuses (L2StatusPatch) a battle script gives: its 5D (causes), 5E (cures), 5F (resists) and 60 (wards)
// steps. they only set chances for the action (or this target), so they go at the top of the script where every path
// runs them. a change rewrites that step and saves the script (our copy, the game's stays)
public static class L2StatusSteps
{
    public enum What { Cause = 0x5D, Cure = 0x5E, Resist = 0x5F, Ward = 0x60 }

    // the script's steps (none = whatever the game does then). with write, a script that can't be read is refused
    // (the stand-in, saved over it, replaced the monster's real script)
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

    private static Step Op(byte op, params int[] args)
    {
        var step = L2AiScripts.New(op);
        for (int i = 0; i < args.Length; i++) L2AiScripts.SetArg(step, i, args[i]);
        return step;
    }

    // the chance (or for a ward, 100 if warded) the script gives that status, 0 = none
    public static int Get(RomBuffer rom, Kind kind, int index, What what, int status)
    {
        List<Step> steps;
        try { steps = L2AiScripts.Load(rom, kind, index).Steps; } catch (Exception) { return 0; }
        var step = steps.FirstOrDefault(x => x.Op == (byte)what && x.Args.Length > 0 && x.Args[0] == status);
        return step == null ? 0 : what == What.Ward ? 100 : step.Args[1];
    }

    // all 16 at once (indexed by status)
    public static int[] GetAll(RomBuffer rom, Kind kind, int index, What what) =>
        Enumerable.Range(0, 16).Select(st => Get(rom, kind, index, what, st)).ToArray();

    // true if the script has a step op (with first argument arg when it isn't -1): the game's "cause / cure a status"
    // (27 / 26) for Bounce, our 61 (focus)
    public static bool HasStep(RomBuffer rom, Kind kind, int index, byte op, int arg = -1)
    {
        List<Step> steps;
        try { steps = L2AiScripts.Load(rom, kind, index).Steps; } catch (Exception) { return false; }
        return steps.Any(x => x.Op == op && (arg < 0 || x.Args.Length > 0 && x.Args[0] == arg));
    }

    // puts a step at the top of the script (or takes every step like it out). false if nothing changed
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
            foreach (var other in steps)
                for (int j = 0; j < other.Targets.Length; j++)
                    if (other.Targets[j] == old) other.Targets[j] = next;
        }
        if (on) steps.Insert(0, Op(op, args));
        L2AiScripts.Save(rom, kind, index, steps);
        return true;
    }

    // sets it (0 takes the step out). false if nothing changed
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
            foreach (var other in steps)
                for (int j = 0; j < other.Targets.Length; j++)
                    if (other.Targets[j] == old) other.Targets[j] = next;
        }
        if (value > 0) steps.Insert(0, what == What.Ward ? Op((byte)what, status) : Op((byte)what, status, value));
        L2AiScripts.Save(rom, kind, index, steps);
        return true;
    }
}
