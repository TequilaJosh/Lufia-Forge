using Step = LufiaForge.Core.Lufia2.L2AiScripts.Step;
using Kind = LufiaForge.Core.Lufia2.L2AiScripts.Kind;

namespace LufiaForge.Core.Lufia2;

// lufia 2's named monster attacks, the way the monsters' battle scripts build them. an attack is the run of steps
// around a "show an attack name" (1E) up to the action (00 End or 01): who it targets (32), its element (1F),
// criticals (20), its own damage (21 physical, 22 magic, 23 fixed), effects (27 cause a status, 26 cure, 25 a stat
// up or down, 24 restore...), its animation (5A) and sound (3D), and how it hits. with no damage step it's a normal
// attack under that name (Beak bash), 0C 23 05 makes it only its effects (Chorus, Flame defender), 41 only shows
// the name (taunts). read from the game's 136 named attacks (2026-11-11)
//
// we change steps where they are, so whatever else the script does (counters, chances, shared scripts) stays how
// it was. a step added at the attack's first step takes over the jumps that went there
public static class L2Attacks
{
    // one attack in one script: the monster, which script (its turn or when it gets hit), the attack name and which
    // one of that name in the script (a script can use a name twice)
    public sealed record Use(Kind Kind, int Monster, int Name, int Ordinal);

    // the steps of one attack: From..To (indexes into the script's steps)
    public sealed class Block
    {
        public List<Step> Steps { get; init; } = new();
        public int From { get; set; }
        public int To { get; set; }
        public int NameAt { get; set; }
        public IEnumerable<Step> All => Steps.Skip(From).Take(To - From + 1);
        public Step? Find(byte op) => All.FirstOrDefault(s => s.Op == op);
        public Step NameStep => Steps[NameAt];
        public Step? End => Steps[To].Op is 0x00 or 0x01 ? Steps[To] : null;
    }

    // steps that start another path, an attack never reaches over them
    private static bool Breaks(Step step) => step.Op is 0x00 or 0x01 or 0x03 or 0x04 or 0x05 or >= 0x06 and <= 0x0B or 0x3F or 0x46 or 0x43 or 0x4F;

    private static bool IsTarget(IReadOnlyList<Step> steps, Step step) => steps.Any(other => other.Targets.Contains(step));

    // the attacks (blocks around each 1E) of a script
    public static List<Block> Blocks(List<Step> steps)
    {
        var blocks = new List<Block>();
        for (int i = 0; i < steps.Count; i++)
        {
            if (steps[i].Op != 0x1E) continue;
            int from = i;
            while (from > 0 && !IsTarget(steps, steps[from]) && !Breaks(steps[from - 1])) from--;
            int to = i;
            while (to + 1 < steps.Count && steps[to].Op is not (0x00 or 0x01) && !Breaks(steps[to]) && !IsTarget(steps, steps[to + 1])) to++;
            if (Breaks(steps[to]) && steps[to].Op is not (0x00 or 0x01) && to > i) to--;
            blocks.Add(new Block { Steps = steps, From = from, To = to, NameAt = i });
        }
        return blocks;
    }

    // every named attack in the monsters' scripts (as they run now, edited ones included)
    public static List<Use> Uses(RomBuffer rom)
    {
        var uses = new List<Use>();
        int count = L2Battle.MonsterCountIn(rom);
        for (int monster = 0; monster < count; monster++)
            foreach (var kind in new[] { Kind.MonsterAttack, Kind.MonsterDefense })
            {
                List<Step> steps;
                try { steps = L2AiScripts.Load(rom, kind, monster).Steps; } catch (Exception) { continue; }
                var seen = new Dictionary<int, int>();
                foreach (var block in Blocks(steps))
                {
                    int name = block.NameStep.Args[0];
                    int ordinal = seen.GetValueOrDefault(name);
                    seen[name] = ordinal + 1;
                    uses.Add(new Use(kind, monster, name, ordinal));
                }
            }
        return uses;
    }

    // the script of a use and its attack's steps
    public static Block Open(RomBuffer rom, Use use)
    {
        var steps = L2AiScripts.Load(rom, use.Kind, use.Monster).Steps;
        return Blocks(steps).Where(b => b.NameStep.Args[0] == use.Name).ElementAtOrDefault(use.Ordinal)
               ?? throw new InvalidOperationException("That attack isn't in the monster's script any more.");
    }

    public static void Save(RomBuffer rom, Use use, Block block) => L2AiScripts.Save(rom, use.Kind, use.Monster, block.Steps);

    // changes several uses (every monster's, "change it for all") and writes each script once. before, two uses in
    // one script were each opened on a copy of the whole script, so the second save put back the first one's old
    // steps. in one script the later uses get changed first (an inserted step doesn't move the earlier ones).
    // everything is changed before anything gets written, and the writes go in all together or not at all
    public static void ChangeAll(RomBuffer rom, IEnumerable<Use> uses, Action<Block> op)
    {
        var scripts = new List<(Use First, List<Step> Steps)>();
        foreach (var script in uses.GroupBy(u => (u.Kind, u.Monster)))
        {
            var steps = L2AiScripts.Load(rom, script.Key.Kind, script.Key.Monster).Steps;
            foreach (var use in script.OrderByDescending(u => u.Ordinal))
                op(Blocks(steps).Where(b => b.NameStep.Args[0] == use.Name).ElementAtOrDefault(use.Ordinal)
                   ?? throw new InvalidOperationException("That attack isn't in the monster's script any more."));
            scripts.Add((script.First(), steps));
        }
        L2Rom.AllOrNothing(rom, () => { foreach (var (use, steps) in scripts) L2AiScripts.Save(rom, use.Kind, use.Monster, steps); return 0; });
    }

    // changing steps where they are

    // puts a new step at index (inside the attack, or right after it). at the attack's first step it takes over the
    // jumps that went there, so every path into the attack runs it
    public static void Insert(Block block, int index, Step step)
    {
        index = Math.Clamp(index, block.From, block.To + 1);
        if (index == block.From && block.From < block.Steps.Count)
        {
            var first = block.Steps[block.From];
            foreach (var other in block.Steps)
                for (int j = 0; j < other.Targets.Length; j++)
                    if (other.Targets[j] == first) other.Targets[j] = step;
        }
        block.Steps.Insert(index, step);
        block.To++;
        if (block.NameAt >= index) block.NameAt++;
    }

    // takes a step out, jumps that went to it go to the step that takes its place
    public static void Remove(Block block, Step step)
    {
        int i = block.Steps.IndexOf(step);
        if (i < 0 || step.Op == 0x1E) return;
        block.Steps.RemoveAt(i);
        var next = block.Steps.ElementAtOrDefault(i) ?? block.Steps.LastOrDefault();
        foreach (var other in block.Steps)
            for (int j = 0; j < other.Targets.Length; j++)
                if (other.Targets[j] == step) other.Targets[j] = next;
        block.To--;
        if (block.NameAt > i) block.NameAt--;
    }

    private static Step Make(byte op, params byte[] args)
    {
        var step = L2AiScripts.New(op);
        Array.Copy(args, step.Args, Math.Min(args.Length, step.Args.Length));
        return step;
    }

    // where an effect or damage step goes: before the name, the way the game writes them
    private static int BeforeName(Block block) => block.NameAt;

    // where the animation, sound and so on go: after the name and the steps that follow it, before the action
    private static int BeforeAction(Block block) => block.End != null ? block.To : block.To + 1;

    // target

    // who the attack goes to, as the target steps (32) the game writes for it. "" = the target its script picked before
    public static readonly (string Key, string Label, byte[] Modes)[] Targets =
    {
        ("", "the target picked before (not set here)", Array.Empty<byte>()),
        ("foe", "one foe (random)", new byte[] { 0x01 }),
        ("foes", "all foes", new byte[] { 0x01, 0x05 }),
        ("self", "itself", new byte[] { 0x06 }),
        ("side", "all of its own side", new byte[] { 0x06, 0x05 }),
        ("ally", "one of its own side (random)", new byte[] { 0x07 }),
        ("hero", "a random hero", new byte[] { 0x08 }),
    };

    public static string Target(Block block)
    {
        var modes = block.All.Where(s => s.Op == 0x32).Select(s => s.Args[0]).ToArray();
        if (modes.Length == 0) return "";
        if (modes.SequenceEqual(new byte[] { 0x05 })) return "foes";   // Gades: everyone on the side of the foe it attacks
        var target = Targets.FirstOrDefault(t => t.Modes.SequenceEqual(modes));
        return target.Key ?? $"modes {Convert.ToHexString(modes)}";
    }

    public static void SetTarget(Block block, string key)
    {
        var target = Targets.FirstOrDefault(x => x.Key == key);
        if (target.Modes == null) return;
        foreach (var s in block.All.Where(s => s.Op == 0x32).ToList()) Remove(block, s);
        for (int k = target.Modes.Length - 1; k >= 0; k--) Insert(block, block.From, Make(0x32, target.Modes[k]));
    }

    // how it hits

    public enum Hit { Attack, Damage, EffectsOnly, ShowOnly }

    public static readonly (Hit Value, string Label)[] Hits =
    {
        (Hit.Attack, "a normal attack under this name"),
        (Hit.Damage, "its own damage (set below)"),
        (Hit.EffectsOnly, "only its effects (no damage)"),
        (Hit.ShowOnly, "only shows the name (nothing happens)"),
    };

    private static bool IsEffectsOnly(Step step) => step.Op == 0x0C && step.Args.Length >= 3 && step.Args[0] == 0x23 && step.Args[1] == 0x05 && step.Args[2] == 0x00;

    public static Hit HowItHits(Block block)
    {
        if (block.All.Any(s => s.Op == 0x41)) return Hit.ShowOnly;
        if (block.All.Any(s => s.Op is 0x21 or 0x22 or 0x23)) return Hit.Damage;
        if (block.All.Any(IsEffectsOnly)) return Hit.EffectsOnly;
        return Hit.Attack;
    }

    public static void SetHowItHits(Block block, Hit hit)
    {
        if (hit == HowItHits(block)) return;
        foreach (var s in block.All.Where(s => s.Op == 0x41 || IsEffectsOnly(s)).ToList()) Remove(block, s);
        if (hit != Hit.Damage)
        {
            // the damage goes but its element stays (as a step of its own)
            int element = Element(block);
            foreach (var s in block.All.Where(s => s.Op is 0x21 or 0x22 or 0x23 || IsSpreadStep(s)).ToList()) Remove(block, s);
            if (element != 0) SetElement(block, element);
        }
        switch (hit)
        {
            case Hit.Damage:
                if (!block.All.Any(s => s.Op is 0x21 or 0x22 or 0x23)) Insert(block, BeforeName(block), Make(0x21, 0x00, 0x00, 100, 0x00, 0x00));
                break;
            case Hit.EffectsOnly: Insert(block, block.NameAt + 1, Make(0x0C, 0x23, 0x05, 0x00)); break;
            case Hit.ShowOnly: Insert(block, BeforeAction(block), Make(0x41)); break;
        }
    }

    // damage

    public enum DamageKind { Physical = 0x21, Magic = 0x22, Fixed = 0x23 }

    public static Step? DamageStep(Block block) => block.All.FirstOrDefault(s => s.Op is 0x21 or 0x22 or 0x23);

    // the game's spread: every damage step sets register 2B to 32 (up to 12% more at random). the byte in 21 and 22
    // gets read and ignored ($85:B93D)
    public const int GameSpread = 32;

    private static bool IsSpreadStep(Step step) => step.Op == 0x0C && step.Args.Length >= 3 && step.Args[0] == 0x2B && step.Args[2] == 0x00;

    // the "set the damage spread" step after the damage, if the attack has one
    public static Step? SpreadStep(Block block) => DamageStep(block) is { } damage ? block.All.SkipWhile(s => s != damage).FirstOrDefault(IsSpreadStep) : null;

    // the damage: its kind, how much (null when the script works it out in a counter), the spread (of 256,
    // GameSpread unless a step after the damage sets it)
    public static (DamageKind Kind, int? Power, int Spread)? Damage(Block block)
    {
        var step = DamageStep(block);
        if (step == null) return null;
        int spread = SpreadStep(block)?.Args[1] ?? GameSpread;
        if (step.Op == 0x23) return (DamageKind.Fixed, step.Args[0] | step.Args[1] << 8, spread);
        int power = step.Args[2] | step.Args[3] << 8;
        return ((DamageKind)step.Op, (power >> 8) == 0x80 ? null : (short)power, spread);
    }

    public static void SetDamageKind(Block block, DamageKind kind)
    {
        var step = DamageStep(block);
        if (step == null) { SetHowItHits(block, Hit.Damage); step = DamageStep(block)!; }
        if ((int)kind == step.Op) return;
        int power = Damage(block)?.Power ?? 100, element = step.Op is 0x21 or 0x22 ? step.Args[0] | step.Args[1] << 8 : 0;
        var newStep = kind == DamageKind.Fixed
            ? Make(0x23, (byte)power, (byte)(power >> 8))
            : Make((byte)kind, (byte)element, (byte)(element >> 8), (byte)power, (byte)(power >> 8), 0);
        int index = block.Steps.IndexOf(step);
        Remove(block, step);
        Insert(block, index, newStep);
    }

    public static void SetPower(Block block, int power)
    {
        var step = DamageStep(block);
        if (step == null) { SetHowItHits(block, Hit.Damage); step = DamageStep(block)!; }
        power = Math.Clamp(power, 0, 0x7FFF);
        if (step.Op == 0x23) { step.Args[0] = (byte)power; step.Args[1] = (byte)(power >> 8); }
        else { step.Args[2] = (byte)power; step.Args[3] = (byte)(power >> 8); }
    }

    // the spread (of 256): the game's 32 takes the step out, any other value sets register 2B after the damage
    public static void SetSpread(Block block, int spread)
    {
        var damage = DamageStep(block);
        if (damage == null) return;
        spread = Math.Clamp(spread, 0, 255);
        var spreadStep = SpreadStep(block);
        if (spread == GameSpread) { if (spreadStep != null) Remove(block, spreadStep); return; }
        if (spreadStep == null) Insert(block, block.Steps.IndexOf(damage) + 1, spreadStep = Make(0x0C, 0x2B, 0, 0));
        spreadStep.Args[1] = (byte)spread;
    }

    // element

    // the attack's element bits: its "set the element" step and its damage's element together
    public static int Element(Block block)
    {
        int elementBits = 0;
        if (block.Find(0x1F) is { } elementStep) elementBits |= elementStep.Args[0] | elementStep.Args[1] << 8;
        if (DamageStep(block) is { Op: 0x21 or 0x22 } damage) elementBits |= damage.Args[0] | damage.Args[1] << 8;
        return elementBits;
    }

    public static void SetElement(Block block, int elementBits)
    {
        var elementStep = block.Find(0x1F);
        var damage = DamageStep(block) is { Op: 0x21 or 0x22 } x ? x : null;
        bool damageHasElement = damage != null && (damage.Args[0] | damage.Args[1]) != 0;
        if (elementStep != null && elementBits == 0 && damage == null) { Remove(block, elementStep); return; }
        if (elementStep != null) { elementStep.Args[0] = (byte)elementBits; elementStep.Args[1] = (byte)(elementBits >> 8); }
        if (damage != null && (damageHasElement || elementStep == null)) { damage.Args[0] = (byte)elementBits; damage.Args[1] = (byte)(elementBits >> 8); }
        if (elementStep == null && damage == null && elementBits != 0) Insert(block, block.From, Make(0x1F, (byte)elementBits, (byte)(elementBits >> 8)));
    }

    // criticals

    public static (int Chance, int Multiplier)? Critical(Block block) => block.Find(0x20) is { } step ? (step.Args[0], step.Args[1]) : null;

    public static void SetCritical(Block block, (int Chance, int Multiplier)? critical)
    {
        var step = block.Find(0x20);
        if (critical == null) { if (step != null) Remove(block, step); return; }
        if (step == null) Insert(block, block.From, step = Make(0x20));
        step.Args[0] = (byte)Math.Clamp(critical.Value.Chance, 0, 100);
        step.Args[1] = (byte)Math.Clamp(critical.Value.Multiplier, 0, 255);
    }

    // animation and sound

    // the animation it plays (5A), null if it has none of its own (then the name's own animation plays)
    public static int? Animation(Block block) => block.Find(0x5A)?.Args[0];

    // the animation that comes with attack name name (the byte before the name, E0 = none)
    public static int NameAnimation(RomBuffer rom, int name) =>
        name >= L2AttackNames.First ? L2AttackNames.Animation(rom, name)
        : name < 1 || name > L2AiScripts.AttackNameCount ? 0xE0 : rom.ReadByte(0x28000 + (rom.ReadUInt16Le(L2AiScripts.AttackNameTable + (name - 1) * 2) & 0x7FFF));

    public static void SetAnimation(Block block, int? animation)
    {
        var step = block.Find(0x5A);
        if (animation == null) { if (step != null) Remove(block, step); return; }
        if (step == null) Insert(block, block.NameAt + 1, step = Make(0x5A));
        step.Args[0] = (byte)animation.Value;
    }

    public static int? Sound(Block block) => block.Find(0x3D)?.Args[0];

    public static void SetSound(Block block, int? id)
    {
        var step = block.Find(0x3D);
        if (id == null) { if (step != null) Remove(block, step); return; }
        if (step == null) Insert(block, BeforeAction(block), step = Make(0x3D));
        step.Args[0] = (byte)id.Value;
    }

    // effects

    // a kind of effect: a command, or (keys from 0x10000) one of the action's flags added with 17 26 (register 26,
    // read from the game's code: the damage steps set it to 20 or 1DC, so these go after them)
    public sealed record EffectKind(int Key, string Label, string Help);

    public const int FlagKey = 0x10000;

    public static readonly EffectKind[] EffectKinds =
    {
        new(0x27, "Cause a status", "With a chance; the target's own resistance can stop it."),
        new(0x26, "Cure a status", ""),
        new(0x5D, "Cause a new status", "One of Lufia Forge's 16 new statuses (Zombie, Frozen, Burnt, Berserk...), with a chance."),
        new(0x5E, "Cure a new status", ""),
        new(0x60, "Ward against a new status", "Its targets can't get it for the rest of the battle."),
        new(0x25, "Raise or lower a stat", "By a percentage of the stat, for the battle (minus lowers it)."),
        new(0x24, "Restore HP, MP or a stat", "By an amount (the Potion's is 30)."),
        new(FlagKey | 0x0002, "Drain: the attacker gets what it takes", "The HP (or MP) the targets lose goes to the attacker, as Absorb and Vampire do."),
        new(FlagKey | 0x00C0, "Back rows and defending don't cut it", "Every target takes the full damage, as with magic."),
        new(FlagKey | 0x0008, "The target's MGR blocks it (not DFP)", "As magic: the target's magic defence counts instead of its DFP."),
        new(0x5C, "Hide the damage number", ""),
        new(0x35, "Show a result message", "The message the game shows after an action of that kind (HP restored, MP absorbed, poisoned...)."),
        new(0x4C, "Screen effect", "One of the battle's screen effects on the targets."),
        new(0x4A, "Wait", "A pause of some frames (60 a second)."),
        new(0x51, "Reflect magic back (Dark reflector)", "For a script run when the monster is hit: the magic goes back to the caster."),
        new(0x58, "Eerie light", ""),
        new(0x5B, "Lose the battle if it dies by this", "As the Master's fight: the heroes lose when this kills one of them."),
    };

    // the add menu: each entry is an effect kind with the values it starts with
    public static readonly (string Label, string Help, int Key, Func<Step> Make)[] AddMenu =
    {
        ("Cause a status", "Poison, sleep, confusion... with a chance.", 0x27, () => Make(0x27, 0x00, 50)),
        ("Cure a status", "", 0x26, () => Make(0x26, 0x00, 100)),
        ("Cause a new status", "Zombie, Frozen, Decay, Burnt, Berserk, Spooked, Amnesia, Piety, Regen, Doom, Blind, Stone, Bleed, Curse, Stop, Mana leak (Lufia Forge's new battle code goes in with it).", 0x5D, () => Make(0x5D, 0x03, 50)),
        ("Cure a new status", "", 0x5E, () => Make(0x5E, 0x03, 100)),
        ("Ward against a new status", "", 0x60, () => Make(0x60, 0x03)),
        ("Raise or lower a stat", "For the battle, by a percentage.", 0x25, () => Make(0x25, 0x04, 25)),
        ("Restore HP, MP or a stat", "", 0x24, () => Make(0x24, 0x00, 100, 0x00, 0x00)),
        ("Drain: the attacker gets what it takes", "With its own damage: the attacker gets back the HP the targets lose (checked in the game).", FlagKey | 0x0002, () => Make(0x17, 0x26, 0x02, 0x00)),
        ("Back rows and defending don't cut it", "", FlagKey | 0x00C0, () => Make(0x17, 0x26, 0xC0, 0x00)),
        ("The target's MGR blocks it (not DFP)", "", FlagKey | 0x0008, () => Make(0x17, 0x26, 0x08, 0x00)),
        ("Ignore the target's DFP", "Makes every hit a critical one of normal damage (critical hits 100%, x1): a critical hit goes through DFP.", -1, () => Make(0x20, 100, 16)),
        ("Hide the damage number", "", 0x5C, () => Make(0x5C)),
        ("Show a result message", "", 0x35, () => Make(0x35, 0x21)),
        ("Screen effect", "", 0x4C, () => Make(0x4C, 0x03)),
        ("Wait", "", 0x4A, () => Make(0x4A, 30)),
        ("Reflect magic back (Dark reflector)", "", 0x51, () => Make(0x51)),
        ("Eerie light", "", 0x58, () => Make(0x58)),
        ("Lose the battle if it dies by this", "", 0x5B, () => Make(0x5B)),
    };

    // the effect kind of a step, -1 if it isn't one of the effects
    public static int KeyOf(Step step)
    {
        if (step.Op == 0x17 && step.Args.Length >= 3 && step.Args[0] == 0x26)
        {
            int key = FlagKey | step.Args[1] | step.Args[2] << 8;
            return EffectKinds.Any(k => k.Key == key) ? key : -1;
        }
        return EffectKinds.Any(k => k.Key == step.Op) ? step.Op : -1;
    }

    public static bool IsEffect(Step step) => KeyOf(step) >= 0;

    public static List<Step> Effects(Block block) => block.All.Where(IsEffect).ToList();

    // adds the effect of add menu entry entry, returns its step
    public static Step AddEffect(Block block, int entry)
    {
        var step = AddMenu[entry].Make();
        if (step.Op == 0x20)
        {
            // ignore DFP: every hit a critical one, x1 (the criticals step)
            SetCritical(block, (100, 16));
            return block.Find(0x20)!;
        }
        // shown effects and the action's flags go with the animation (after the damage step, which sets the flags),
        // the rest before the name like the game writes them
        Insert(block, step.Op is 0x5C or 0x4A or 0x4C or 0x35 or 0x17 ? BeforeAction(block) : BeforeName(block), step);
        // with no damage and no action of its own, the effects need the attack to be "only its effects"
        if (step.Op is 0x27 or 0x26 or 0x25 or 0x24 or 0x5D or 0x5E or 0x60 or 0x61 && HowItHits(block) == Hit.Attack) SetHowItHits(block, Hit.EffectsOnly);
        return step;
    }

    // adds an effect of kind key (the first add menu entry of that kind)
    public static Step AddEffectOfKind(Block block, int key) => AddEffect(block, Array.FindIndex(AddMenu, m => m.Key == key));

    // the ordinal-th effect step of kind key (to change the same effect in every monster that has the attack)
    public static Step? EffectStep(Block block, int key, int ordinal) => block.All.Where(s => KeyOf(s) == key).ElementAtOrDefault(ordinal);

    // names

    // the longest name the game has (its battle window shows it on one line)
    public const int NameMax = 26;

    // renames attack name, null when done, else why not
    public static string? Rename(RomBuffer rom, int name, string text)
    {
        text = text.Trim();
        if (name >= L2AttackNames.First) return L2AttackNames.Rename(rom, name, text);
        if (name < 1 || name > L2AiScripts.AttackNameCount) return "There's no such attack name.";
        if (L2AttackNames.Check(rom, text) is { } problem) return problem;
        var all = L2AiScripts.MonsterAttackNames(rom).Skip(1).Take(L2AiScripts.AttackNameCount).ToList();
        all[name - 1] = text;
        return L2Dutch.RepackAttackNames(rom, all) ? null : "The names have no more room (shorten this one or another).";
    }
}
