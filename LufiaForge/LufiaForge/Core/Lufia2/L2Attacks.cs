using Step = LufiaForge.Core.Lufia2.L2AiScripts.Step;
using Kind = LufiaForge.Core.Lufia2.L2AiScripts.Kind;

namespace LufiaForge.Core.Lufia2;

/// <summary>
/// Lufia II's named monster attacks, as the monsters' battle scripts make them. An attack is the run of steps around a
/// "show an attack name" (1E) up to the action (00 End or 01): who it targets (32), its element (1F), criticals (20),
/// its own damage (21 physical, 22 magic, 23 fixed), effects (27 cause a status, 26 cure, 25 a stat up or down, 24
/// restore...), its animation (5A) and sound (3D), and how it hits: with no damage step it is a normal attack under
/// that name (Beak bash), 0C 23 05 makes it only its effects (Chorus, Flame defender), 41 only shows the name (taunts).
/// Read from the game's 136 named attacks (2026-11-11).
///
/// Steps are changed where they are, so whatever else the script does (counters, chances, shared scripts) stays as it
/// was; a step added at the attack's first step takes over the jumps that went there.
/// </summary>
public static class L2Attacks
{
    /// <summary>One attack in one script: the monster, which script (its turn or when it is hit), the attack name and
    /// which one of that name in the script (a script can use a name twice).</summary>
    public sealed record Use(Kind Kind, int Monster, int Name, int Ordinal);

    /// <summary>The steps of one attack: <see cref="From"/>..<see cref="To"/> (indices into the script's steps).</summary>
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

    /// <summary>Steps that start another path: an attack never reaches over them.</summary>
    private static bool Breaks(Step s) => s.Op is 0x00 or 0x01 or 0x03 or 0x04 or 0x05 or >= 0x06 and <= 0x0B or 0x3F or 0x46 or 0x43 or 0x4F;

    private static bool IsTarget(IReadOnlyList<Step> steps, Step s) => steps.Any(o => o.Targets.Contains(s));

    /// <summary>The attacks (blocks around each 1E) of a script.</summary>
    public static List<Block> Blocks(List<Step> steps)
    {
        var l = new List<Block>();
        for (int i = 0; i < steps.Count; i++)
        {
            if (steps[i].Op != 0x1E) continue;
            int from = i;
            while (from > 0 && !IsTarget(steps, steps[from]) && !Breaks(steps[from - 1])) from--;
            int to = i;
            while (to + 1 < steps.Count && steps[to].Op is not (0x00 or 0x01) && !Breaks(steps[to]) && !IsTarget(steps, steps[to + 1])) to++;
            if (Breaks(steps[to]) && steps[to].Op is not (0x00 or 0x01) && to > i) to--;
            l.Add(new Block { Steps = steps, From = from, To = to, NameAt = i });
        }
        return l;
    }

    /// <summary>Every named attack in the monsters' scripts (as they run now: edited ones included).</summary>
    public static List<Use> Uses(RomBuffer rom)
    {
        var uses = new List<Use>();
        int count = L2Battle.MonsterCountIn(rom);
        for (int m = 0; m < count; m++)
            foreach (var kind in new[] { Kind.MonsterAttack, Kind.MonsterDefense })
            {
                List<Step> steps;
                try { steps = L2AiScripts.Load(rom, kind, m).Steps; } catch (Exception) { continue; }
                var seen = new Dictionary<int, int>();
                foreach (var b in Blocks(steps))
                {
                    int name = b.NameStep.Args[0];
                    int k = seen.GetValueOrDefault(name);
                    seen[name] = k + 1;
                    uses.Add(new Use(kind, m, name, k));
                }
            }
        return uses;
    }

    /// <summary>The script of a use and its attack's steps.</summary>
    public static Block Open(RomBuffer rom, Use u)
    {
        var steps = L2AiScripts.Load(rom, u.Kind, u.Monster).Steps;
        return Blocks(steps).Where(b => b.NameStep.Args[0] == u.Name).ElementAtOrDefault(u.Ordinal)
               ?? throw new InvalidOperationException("That attack isn't in the monster's script any more.");
    }

    public static void Save(RomBuffer rom, Use u, Block b) => L2AiScripts.Save(rom, u.Kind, u.Monster, b.Steps);

    // ── changing steps where they are ─────────────────────────────────────────

    /// <summary>Puts a new step at <paramref name="at"/> (inside the attack, or right after it). At the attack's first
    /// step it takes over the jumps that went there, so every path into the attack runs it.</summary>
    public static void Insert(Block b, int at, Step s)
    {
        at = Math.Clamp(at, b.From, b.To + 1);
        if (at == b.From && b.From < b.Steps.Count)
        {
            var first = b.Steps[b.From];
            foreach (var o in b.Steps)
                for (int j = 0; j < o.Targets.Length; j++)
                    if (o.Targets[j] == first) o.Targets[j] = s;
        }
        b.Steps.Insert(at, s);
        b.To++;
        if (b.NameAt >= at) b.NameAt++;
    }

    /// <summary>Takes a step out; jumps that went to it go to the step that takes its place.</summary>
    public static void Remove(Block b, Step s)
    {
        int i = b.Steps.IndexOf(s);
        if (i < 0 || s.Op == 0x1E) return;
        b.Steps.RemoveAt(i);
        var next = b.Steps.ElementAtOrDefault(i) ?? b.Steps.LastOrDefault();
        foreach (var o in b.Steps)
            for (int j = 0; j < o.Targets.Length; j++)
                if (o.Targets[j] == s) o.Targets[j] = next;
        b.To--;
        if (b.NameAt > i) b.NameAt--;
    }

    private static Step Make(byte op, params byte[] args)
    {
        var s = L2AiScripts.New(op);
        Array.Copy(args, s.Args, Math.Min(args.Length, s.Args.Length));
        return s;
    }

    /// <summary>Where a step of an effect or a damage goes: before the name, as the game writes them.</summary>
    private static int BeforeName(Block b) => b.NameAt;

    /// <summary>Where the animation, sound and the like go: after the name and the steps that follow it, before the action.</summary>
    private static int BeforeAction(Block b) => b.End != null ? b.To : b.To + 1;

    // ── target ─────────────────────────────────────────────────────────────────

    /// <summary>Who the attack goes to, as the target steps (32) the game writes for it. "" = the target its script picked before.</summary>
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

    public static string Target(Block b)
    {
        var modes = b.All.Where(s => s.Op == 0x32).Select(s => s.Args[0]).ToArray();
        if (modes.Length == 0) return "";
        if (modes.SequenceEqual(new byte[] { 0x05 })) return "foes";   // Gades: everyone on the side of the foe it attacks
        var t = Targets.FirstOrDefault(t => t.Modes.SequenceEqual(modes));
        return t.Key ?? $"modes {Convert.ToHexString(modes)}";
    }

    public static void SetTarget(Block b, string key)
    {
        var t = Targets.FirstOrDefault(x => x.Key == key);
        if (t.Modes == null) return;
        foreach (var s in b.All.Where(s => s.Op == 0x32).ToList()) Remove(b, s);
        for (int k = t.Modes.Length - 1; k >= 0; k--) Insert(b, b.From, Make(0x32, t.Modes[k]));
    }

    // ── how it hits ───────────────────────────────────────────────────────────

    public enum Hit { Attack, Damage, EffectsOnly, ShowOnly }

    public static readonly (Hit Value, string Label)[] Hits =
    {
        (Hit.Attack, "a normal attack under this name"),
        (Hit.Damage, "its own damage (set below)"),
        (Hit.EffectsOnly, "only its effects (no damage)"),
        (Hit.ShowOnly, "only shows the name (nothing happens)"),
    };

    private static bool IsEffectsOnly(Step s) => s.Op == 0x0C && s.Args.Length >= 3 && s.Args[0] == 0x23 && s.Args[1] == 0x05 && s.Args[2] == 0x00;

    public static Hit HowItHits(Block b)
    {
        if (b.All.Any(s => s.Op == 0x41)) return Hit.ShowOnly;
        if (b.All.Any(s => s.Op is 0x21 or 0x22 or 0x23)) return Hit.Damage;
        if (b.All.Any(IsEffectsOnly)) return Hit.EffectsOnly;
        return Hit.Attack;
    }

    public static void SetHowItHits(Block b, Hit h)
    {
        if (h == HowItHits(b)) return;
        foreach (var s in b.All.Where(s => s.Op == 0x41 || IsEffectsOnly(s)).ToList()) Remove(b, s);
        if (h != Hit.Damage)
        {
            // the damage goes; its element stays (as a step of its own)
            int element = Element(b);
            foreach (var s in b.All.Where(s => s.Op is 0x21 or 0x22 or 0x23).ToList()) Remove(b, s);
            if (element != 0) SetElement(b, element);
        }
        switch (h)
        {
            case Hit.Damage:
                if (!b.All.Any(s => s.Op is 0x21 or 0x22 or 0x23)) Insert(b, BeforeName(b), Make(0x21, 0x00, 0x00, 100, 0x00, 0x00));
                break;
            case Hit.EffectsOnly: Insert(b, b.NameAt + 1, Make(0x0C, 0x23, 0x05, 0x00)); break;
            case Hit.ShowOnly: Insert(b, BeforeAction(b), Make(0x41)); break;
        }
    }

    // ── damage ─────────────────────────────────────────────────────────────────

    public enum DamageKind { Physical = 0x21, Magic = 0x22, Fixed = 0x23 }

    public static Step? DamageStep(Block b) => b.All.FirstOrDefault(s => s.Op is 0x21 or 0x22 or 0x23);

    /// <summary>The damage: its kind, how much (null when the script works it out in a counter), the spread.</summary>
    public static (DamageKind Kind, int? Power, int Spread)? Damage(Block b)
    {
        var s = DamageStep(b);
        if (s == null) return null;
        if (s.Op == 0x23) return (DamageKind.Fixed, s.Args[0] | s.Args[1] << 8, 0);
        int v = s.Args[2] | s.Args[3] << 8;
        return ((DamageKind)s.Op, (v >> 8) == 0x80 ? null : (short)v, s.Args[4]);
    }

    public static void SetDamageKind(Block b, DamageKind k)
    {
        var s = DamageStep(b);
        if (s == null) { SetHowItHits(b, Hit.Damage); s = DamageStep(b)!; }
        if ((int)k == s.Op) return;
        int power = Damage(b)?.Power ?? 100, spread = Damage(b)?.Spread ?? 0, element = s.Op is 0x21 or 0x22 ? s.Args[0] | s.Args[1] << 8 : 0;
        var n = k == DamageKind.Fixed
            ? Make(0x23, (byte)power, (byte)(power >> 8))
            : Make((byte)k, (byte)element, (byte)(element >> 8), (byte)power, (byte)(power >> 8), (byte)spread);
        int at = b.Steps.IndexOf(s);
        Remove(b, s);
        Insert(b, at, n);
    }

    public static void SetPower(Block b, int power)
    {
        var s = DamageStep(b);
        if (s == null) { SetHowItHits(b, Hit.Damage); s = DamageStep(b)!; }
        power = Math.Clamp(power, 0, 0x7FFF);
        if (s.Op == 0x23) { s.Args[0] = (byte)power; s.Args[1] = (byte)(power >> 8); }
        else { s.Args[2] = (byte)power; s.Args[3] = (byte)(power >> 8); }
    }

    public static void SetSpread(Block b, int spread)
    {
        var s = DamageStep(b);
        if (s == null || s.Op == 0x23) return;
        s.Args[4] = (byte)Math.Clamp(spread, 0, 255);
    }

    // ── element ───────────────────────────────────────────────────────────────

    /// <summary>The attack's element bits: its "set the element" step and its damage's element together.</summary>
    public static int Element(Block b)
    {
        int w = 0;
        if (b.Find(0x1F) is { } e) w |= e.Args[0] | e.Args[1] << 8;
        if (DamageStep(b) is { Op: 0x21 or 0x22 } d) w |= d.Args[0] | d.Args[1] << 8;
        return w;
    }

    public static void SetElement(Block b, int w)
    {
        var e = b.Find(0x1F);
        var d = DamageStep(b) is { Op: 0x21 or 0x22 } x ? x : null;
        bool dHas = d != null && (d.Args[0] | d.Args[1]) != 0;
        if (e != null && w == 0 && d == null) { Remove(b, e); return; }
        if (e != null) { e.Args[0] = (byte)w; e.Args[1] = (byte)(w >> 8); }
        if (d != null && (dHas || e == null)) { d.Args[0] = (byte)w; d.Args[1] = (byte)(w >> 8); }
        if (e == null && d == null && w != 0) Insert(b, b.From, Make(0x1F, (byte)w, (byte)(w >> 8)));
    }

    // ── criticals ─────────────────────────────────────────────────────────────

    public static (int Chance, int Multiplier)? Critical(Block b) => b.Find(0x20) is { } s ? (s.Args[0], s.Args[1]) : null;

    public static void SetCritical(Block b, (int Chance, int Multiplier)? c)
    {
        var s = b.Find(0x20);
        if (c == null) { if (s != null) Remove(b, s); return; }
        if (s == null) Insert(b, b.From, s = Make(0x20));
        s.Args[0] = (byte)Math.Clamp(c.Value.Chance, 0, 100);
        s.Args[1] = (byte)Math.Clamp(c.Value.Multiplier, 0, 255);
    }

    // ── animation and sound ───────────────────────────────────────────────────

    /// <summary>The animation it plays (5A), or null when it has none of its own (the name's own animation plays).</summary>
    public static int? Animation(Block b) => b.Find(0x5A)?.Args[0];

    /// <summary>The animation that comes with attack name <paramref name="name"/> (the byte before the name; E0 = none).</summary>
    public static int NameAnimation(RomBuffer rom, int name) =>
        name < 1 || name > L2AiScripts.AttackNameCount ? 0xE0 : rom.ReadByte(0x28000 + (rom.ReadUInt16Le(L2AiScripts.AttackNameTable + (name - 1) * 2) & 0x7FFF));

    public static void SetAnimation(Block b, int? a)
    {
        var s = b.Find(0x5A);
        if (a == null) { if (s != null) Remove(b, s); return; }
        if (s == null) Insert(b, b.NameAt + 1, s = Make(0x5A));
        s.Args[0] = (byte)a.Value;
    }

    public static int? Sound(Block b) => b.Find(0x3D)?.Args[0];

    public static void SetSound(Block b, int? id)
    {
        var s = b.Find(0x3D);
        if (id == null) { if (s != null) Remove(b, s); return; }
        if (s == null) Insert(b, BeforeAction(b), s = Make(0x3D));
        s.Args[0] = (byte)id.Value;
    }

    // ── effects ───────────────────────────────────────────────────────────────

    /// <summary>The effects an attack can have, in the order the add menu offers them.</summary>
    public static readonly (byte Op, string Label)[] EffectKinds =
    {
        (0x27, "Cause a status"), (0x26, "Cure a status"), (0x25, "Raise or lower a stat"), (0x24, "Restore HP, MP or a stat"),
        (0x5C, "Hide the damage number"), (0x4A, "Flash and shake the screen"), (0x4C, "Screen effect"), (0x35, "Show a battle message"),
        (0x51, "Reflect magic back (Dark reflector)"), (0x58, "Eerie light"), (0x5B, "Lose the battle if it dies by this"),
    };

    public static bool IsEffect(Step s) => EffectKinds.Any(k => k.Op == s.Op);

    public static List<Step> Effects(Block b) => b.All.Where(IsEffect).ToList();

    public static Step AddEffect(Block b, byte op)
    {
        var s = op switch
        {
            0x27 or 0x26 => Make(op, 0x00, 50),        // poison, 50%
            0x25 => Make(op, 0x04, 25),                // ATP +25%
            0x24 => Make(op, 0x00, 100, 0x00, 0x00),   // HP by 100
            0x4A => Make(op, 1),
            _ => Make(op),
        };
        // shown effects go with the animation, the rest before the name as the game writes them
        Insert(b, op is 0x5C or 0x4A or 0x4C or 0x35 ? BeforeAction(b) : BeforeName(b), s);
        // with no damage and no action of its own, the effects need the attack to be "only its effects"
        if (op is 0x27 or 0x26 or 0x25 or 0x24 && HowItHits(b) == Hit.Attack) SetHowItHits(b, Hit.EffectsOnly);
        return s;
    }

    /// <summary>The <paramref name="ordinal"/>-th effect step of kind <paramref name="op"/> (to change the same effect in
    /// every monster that has the attack).</summary>
    public static Step? EffectStep(Block b, byte op, int ordinal) => b.All.Where(s => s.Op == op).ElementAtOrDefault(ordinal);

    // ── names ─────────────────────────────────────────────────────────────────

    /// <summary>The longest name the game has (its battle window shows it in one line).</summary>
    public const int NameMax = 26;

    /// <summary>Renames attack <paramref name="name"/>; null when done, else why not.</summary>
    public static string? Rename(RomBuffer rom, int name, string text)
    {
        text = text.Trim();
        if (name < 1 || name > L2AiScripts.AttackNameCount) return "There's no such attack name.";
        if (text.Length == 0) return "A name can't be empty.";
        if (text.Length > NameMax) return $"Up to {NameMax} letters.";
        foreach (char c in text) if (L2Charset.ToByte(rom, c) == null) return $"The game's letters have no '{c}'.";
        var all = L2AiScripts.MonsterAttackNames(rom).Skip(1).ToList();
        all[name - 1] = text;
        return L2Dutch.RepackAttackNames(rom, all) ? null : "The names have no more room (shorten this one or another).";
    }
}
