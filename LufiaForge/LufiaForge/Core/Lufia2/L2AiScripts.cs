using System.IO;

namespace LufiaForge.Core.Lufia2;

/// <summary>
/// Editable Lufia II monster and capsule monster battle scripts (AI: what it does on its turn; defense: what happens
/// when it is hit). Found 2026-10-24 by reading the game's code:
/// <list type="bullet">
/// <item>A monster's turn ($81:B1C9): offset = combatant +$4A (tag 07 of the record), base = the record (from the
/// monster id at +$50, $81:FCE2), bank $96; offset 0 runs the built-in plain attack $85:EF20.</item>
/// <item>When hit ($85:D153 and $85:D194): offset +$4C (tag 08), base and bank = the record pointer at +$45/+$47.</item>
/// <item>A capsule monster's turn ($81:B1A3): offset $1129 (record +$25), base and bank $1124/$1126.</item>
/// <item>The script runner $81:FAC9 starts at base + offset (offset 0 = none); jumps count from the base.</item>
/// </list>
/// Scripts had to sit right behind their record (banks $96 and $97 have no room left), so edited scripts go to the
/// added space instead: four small hooks look up (record, kind) in a table there and run the script from there when
/// it has one; everything else runs as before. The game's own bytes are never changed, so scripts shared between
/// monsters stay as they were, a monster without a script can get one, and "back to the game's script" just drops
/// the table entry. A script in the table is stored as [capacity u16][00][script]: its jumps count from the 00
/// (the runner is given offset 1, as offset 0 would mean "no script").
/// </summary>
public static class L2AiScripts
{
    public enum Kind
    {
        MonsterAttack = 0, MonsterDefense = 1, CapsuleAi = 2, CapsuleDefense = 3,
        // effects (item property words 0-3, a spell's word at +$11, an IP attack's effect script)
        ItemMenu = 4, ItemBattle = 5, Weapon = 6, Armor = 7, Spell = 8, IpAttack = 9,
    }

    public static bool IsItem(Kind k) => k is Kind.ItemMenu or Kind.ItemBattle or Kind.Weapon or Kind.Armor;

    /// <summary>Item, spell and IP effects (the hooks at the script runner and the item loader, block version 2).</summary>
    public static bool IsEffect(Kind k) => k >= Kind.ItemMenu;

    public static string KindName(Kind k) => k switch
    {
        Kind.MonsterAttack or Kind.CapsuleAi => "its turn (AI)",
        Kind.ItemMenu => "used from the menu",
        Kind.ItemBattle => "used in battle",
        Kind.Weapon => "as a weapon (its attack)",
        Kind.Armor => "as armor (when its wearer is hit)",
        Kind.Spell => "the spell's effect",
        Kind.IpAttack => "the IP attack's effect",
        _ => "when it is hit (defense)",
    };

    // ── commands ─────────────────────────────────────────────────────────────

    /// <summary>What a command's argument is (for the editor's fields; the size is in <see cref="Size"/>).</summary>
    public enum Arg { Byte, Word, Register, Value, Jump, Chance, Percent, Spell, Monster, CapsuleAttack, Element, Stat, Status, Target, Item, Library, AttackName, Animation, Sound, Restore, Change }

    public static int Size(Arg a) => a is Arg.Word or Arg.Value or Arg.Jump or Arg.Element or Arg.Item or Arg.Library ? 2 : 1;

    public sealed record OpDef(byte Op, string Name, string Group, Arg[] Args, string Help = "")
    {
        public int Length => 1 + Args.Sum(Size);
        public bool IsJump => Args.Contains(Arg.Jump);
        /// <summary>The script stops here (00 end, 4F quit, 03 go to, 43 return).</summary>
        public bool EndsPath => Op is 0x00 or 0x4F or 0x03 or 0x43;
    }

    public static readonly OpDef[] Ops = BuildOps();
    private static readonly Dictionary<byte, OpDef> ByOp = Ops.ToDictionary(o => o.Op);

    public static OpDef? Def(byte op) => ByOp.GetValueOrDefault(op);

    private static OpDef[] BuildOps()
    {
        var l = new List<OpDef>();
        void O(int op, string group, string name, string help, params Arg[] args) => l.Add(new OpDef((byte)op, name, group, args, help));
        // groups, in the order the command list shows them
        const string Act = "Actions", Flow = "Choices and jumps", Eff = "Effects of a special attack", Show = "Show and sound", Reg = "Numbers", Misc = "Rarely used";
        O(0x32, Act, "Pick the target", "Who the action goes to: a random foe, itself... Most turns start with it. 'Everyone on that side' after picking a foe means all of them; after 'itself', all its own side.", Arg.Target);
        O(0x28, Act, "Attack", "A normal physical attack on the target.");
        O(0x2C, Act, "Cast a spell", "Casts the spell on the target. It needs the MP: without enough it attacks instead.", Arg.Spell);
        O(0x54, Act, "Cast a spell for free", "Casts the spell without using MP (the game uses it for IP-like moves).", Arg.Spell);
        O(0x2B, Act, "Use an item", "Uses the item on the target (it doesn't need to have it).", Arg.Item);
        O(0x2D, Act, "Call for help", "Brings in another monster. It fails when there's no room: follow it with 'If that didn't work, go to'.", Arg.Monster);
        O(0x29, Act, "Defend", "Guards this turn.");
        O(0x2A, Act, "Run away", "Leaves the battle (the heroes get nothing for it).");
        O(0x41, Act, "Just show it (no effect)", "An action that does nothing but show the attack name above it: a taunt, a laugh, a warning.");
        O(0x4F, Act, "Do nothing this turn", "Ends the turn without acting.");
        O(0x3E, Act, "Use a capsule monster attack", "", Arg.CapsuleAttack);
        O(0x37, Act, "Weapon attack", "An attack with the wearer's weapon (what weapon effects start with).");
        O(0x00, Act, "End: do the action", "The turn's action happens here and the script stops. Every path through the script ends with one.");
        O(0x01, Act, "Do the action now, then go on", "Does the action picked so far and carries on with the next step: two or more actions in one turn.");
        O(0x05, Flow, "With a chance, go to", "Picks between two actions: the chance is out of 256 (128 = half the time).", Arg.Chance, Arg.Jump);
        O(0x04, Flow, "If that didn't work, go to", "After calling for help (no room) or another action that can fail.", Arg.Jump);
        O(0x03, Flow, "Go to", "Carries on at another step.", Arg.Jump);
        O(0x06, Flow, "If a number is, go to", "Compares one of the battle's numbers (its HP, a counter...) with a value.", Arg.Register, Arg.Value, Arg.Jump);
        O(0x07, Flow, "If a number is not, go to", "", Arg.Register, Arg.Value, Arg.Jump);
        O(0x08, Flow, "If a number is more than, go to", "", Arg.Register, Arg.Value, Arg.Jump);
        O(0x09, Flow, "If a number is less than, go to", "", Arg.Register, Arg.Value, Arg.Jump);
        O(0x0A, Flow, "If a number is at least, go to", "", Arg.Register, Arg.Value, Arg.Jump);
        O(0x0B, Flow, "If a number is at most, go to", "", Arg.Register, Arg.Value, Arg.Jump);
        O(0x3F, Flow, "If it has learned capsule attack slot, go to", "Capsule monsters: slot 1-3 of the attacks they learn.", Arg.Byte, Arg.Jump);
        O(0x46, Flow, "If battle setting is 0, go to", "", Arg.Byte, Arg.Jump);
        O(0x42, Flow, "Run a shared script", "Runs one of the game's shared scripts (the list says what each does; 11 makes it immune to the 5 statuses). Many monsters share each one: to change it for this one alone, copy its steps in here.", Arg.Library);
        O(0x43, Flow, "Return (end of a shared script)", "");
        O(0x1E, Show, "Show an attack name", "The name shown at the top of the screen, from the game's list of monster attacks.", Arg.AttackName);
        O(0x5A, Show, "Play a battle animation", "The animation of a spell, IP attack or monster attack.", Arg.Animation);
        O(0x3D, Show, "Play a sound effect", "", Arg.Sound);
        O(0x56, Show, "Keep the message up", "How long (frames, 60 a second) the attack name stays.", Arg.Value);
        O(0x35, Show, "Show a battle message", "One of the battle's built-in messages and effects (the game's spells use them).", Arg.Byte);
        O(0x36, Show, "If that didn't work: show a battle message", "", Arg.Byte);
        O(0x4A, Show, "Flash and shake the screen", "How many times.", Arg.Byte);
        O(0x4C, Show, "Screen effect", "", Arg.Byte);
        O(0x4B, Show, "Screen effect 4", "");
        O(0x21, Eff, "Physical damage", "A special attack's damage: its element, how much (a number, or one worked out above), and a spread. Blocked by DFP.", Arg.Element, Arg.Value, Arg.Byte);
        O(0x22, Eff, "Magic damage", "Like physical damage, but magic (the spells' kind).", Arg.Element, Arg.Value, Arg.Byte);
        O(0x23, Eff, "Fixed damage", "", Arg.Word);
        O(0x27, Eff, "Cause a status", "Poison, sleep, confusion... with a chance in percent.", Arg.Status, Arg.Percent);
        O(0x26, Eff, "Cure a status", "", Arg.Status, Arg.Percent);
        O(0x24, Eff, "Restore", "Heals HP or MP (or a stat) by an amount, give or take a spread.", Arg.Restore, Arg.Value, Arg.Byte);
        O(0x25, Eff, "Raise or lower a stat", "Changes the target's stat for the battle by a percentage (minus lowers it).", Arg.Restore, Arg.Change);
        O(0x1F, Eff, "Set the attack's element", "", Arg.Element);
        O(0x20, Eff, "Critical hits", "Chance in percent and the damage multiplier (16 = x1).", Arg.Percent, Arg.Byte);
        O(0x3C, Eff, "Add the user's weapon part to the amount", "The game's healing spells add it after Restore.");
        O(0x5C, Eff, "Hide the damage number", "");
        O(0x5B, Eff, "Lose the battle if it dies by this", "");
        O(0x51, Eff, "Reflect magic back (Dark reflector)", "");
        O(0x52, Eff, "Reflect flag 4", "");
        O(0x58, Eff, "Eerie light", "");
        O(0x57, Eff, "Item boosts this spell", "", Arg.Item, Arg.Byte);
        O(0x50, Eff, "Keep only allowed targets", "");
        O(0x4D, Eff, "Drop targets that can't be hit", "");
        O(0x1D, Eff, "Do the effect now (without an action)", "");
        O(0x0C, Reg, "Set a number", "The battle's numbers: the damage, the attack's element, the status chances, its own counters 1-8 (kept from turn to turn)...", Arg.Register, Arg.Value);
        O(0x0D, Reg, "Add to a number", "", Arg.Register, Arg.Value);
        O(0x0E, Reg, "Take from a number", "", Arg.Register, Arg.Value);
        O(0x0F, Reg, "Multiply a number", "", Arg.Register, Arg.Value);
        O(0x10, Reg, "Divide a number", "", Arg.Register, Arg.Value);
        O(0x15, Reg, "A number = its own stat", "", Arg.Register, Arg.Stat);
        O(0x13, Reg, "A number = the target's stat", "Pick the target first.", Arg.Register, Arg.Stat);
        O(0x4E, Reg, "A number = the attacker's stat", "", Arg.Register, Arg.Stat);
        O(0x14, Reg, "Set the target's stat", "", Arg.Register, Arg.Stat);
        O(0x11, Reg, "A random number below it", "", Arg.Register);
        O(0x1A, Reg, "Turn a number negative (or back)", "", Arg.Register);
        O(0x30, Reg, "A number = how many on a side", "", Arg.Register);
        O(0x1C, Reg, "A number = the battle round", "", Arg.Register);
        O(0x55, Reg, "Divide a number (no minus)", "", Arg.Register, Arg.Value);
        O(0x16, Reg, "Keep some bits of a number", "", Arg.Register, Arg.Value);
        O(0x17, Reg, "Add bits to a number", "", Arg.Register, Arg.Value);
        O(0x18, Reg, "Flip bits of a number", "", Arg.Register, Arg.Value);
        O(0x12, Reg, "Ratio of two numbers", "", Arg.Register, Arg.Register);
        O(0x19, Reg, "Make a number positive", "", Arg.Register);
        O(0x1B, Reg, "The sign of a number (-1, 0, 1)", "", Arg.Register);
        O(0x2F, Reg, "A number = fighters flagged 04", "", Arg.Register);
        O(0x40, Reg, "A number = learned capsule attacks", "", Arg.Register);
        O(0x45, Reg, "A number = a battle setting", "", Arg.Register, Arg.Byte);
        O(0x44, Misc, "Set a battle setting", "", Arg.Byte, Arg.Byte);
        O(0x2E, Misc, "Action 0D (not used by the game)", "");
        O(0x53, Misc, "Action 9 (not used by the game)", "", Arg.Byte);
        O(0x59, Misc, "Effect $85:DA9C (not used by the game)", "");
        O(0x48, Misc, "Effect $85:9671 (not used by the game)", "");
        O(0x47, Misc, "Effect timer", "", Arg.Value);
        O(0x49, Misc, "Wait for the effect timer", "");
        return l.ToArray();
    }

    /// <summary>The target modes of 32 ($85:BBB6, read from the code): 01 a random member of the other side, 05 everyone on
    /// the target's side, 06 itself, 07 a random member of its own side, 08 a random hero; 02-04 aren't used by the game.</summary>
    public static readonly string[] Targets =
    {
        "keep the target it has", "a random foe", "a foe picked by a rule (02, not used by the game)",
        "a foe picked by a rule (03, not used by the game)", "04 (not used by the game)",
        "everyone on that side", "itself", "a random one of its own side", "a random hero",
    };
    public static readonly string[] Stats = { "HP", "MP", "max HP", "max MP", "ATP", "DFP", "STR", "AGL", "INT", "GUT", "MGR", "IP", "level",
        "ATP+ (battle)", "DFP+ (battle)", "STR+ (battle)", "AGL+ (battle)", "INT+ (battle)", "GUT+ (battle)", "MGR+ (battle)", "status" };
    public static readonly string[] Statuses = { "poison", "silence", "death", "paralysis", "confusion", "sleep", "status 6", "mirror" };
    public static readonly string[] Restores = { "HP", "MP", "max HP", "max MP", "ATP", "DFP", "STR", "AGL", "INT", "GUT", "MGR" };
    public static readonly (int Bit, string Name)[] Elements =
    {
        // read from the game's own uses (2026-10-25): fire swords and Fireball 0002, Aqua sword and Droplet 0004, Silver
        // sword and Light attack 0010, Deadly sword and Gades blade 0020, maces and rods 0040, boomerangs 0080, Dragon
        // tooth and the dragon IP attacks 0100, Insect crush 0200, Cold rapier and Blizzard 0400, Thunder ax and Bolt
        // 0800, Groundshock 1000; 0001, 0008 and 2000-8000 aren't used by any script
        (0x0002, "fire"), (0x0004, "water"), (0x0400, "ice"), (0x0800, "thunder"), (0x1000, "earth"), (0x0010, "light"),
        (0x0020, "dark"), (0x0040, "blunt (maces, rods)"), (0x0080, "thrown (boomerangs)"), (0x0100, "vs dragons"),
        (0x0200, "vs insects"), (0x0001, "unused 0001"), (0x0008, "unused 0008"), (0x2000, "unused 2000"),
        (0x4000, "unused 4000"), (0x8000, "unused 8000"),
    };

    public static string ElementText(int w) =>
        w == 0 ? "neutral" : string.Join(" + ", Elements.Where(e => (w & e.Bit) != 0).Select(e => e.Name).DefaultIfEmpty($"{w:X4}"));

    /// <summary>A value operand: 80 in the high byte = register (low byte), else a number.</summary>
    public static string ValueText(int v) => (v >> 8) == 0x80 ? RegisterName(v & 0xFF) : $"{(short)v}";

    /// <summary>What a register holds, in words. 00-7F are shared by the whole battle ($7F:F40E), 80-FF belong to the
    /// fighter running the script (its counters). Named from the battle code's handlers and the game's scripts
    /// (shared script 11, "immune to the 5 statuses", zeroes the chances 4E 50 54 56 58; 52, death, is left).</summary>
    public static string RegisterName(int r) => r switch
    {
        0x10 => "the last action failed (0 = no)",
        0x20 => "the attacker",
        0x21 => "the targets",
        0x11 => "a working number (11)",
        0x23 => "the action's kind",
        0x26 => "the special effect's number",
        0x2D => "the special effect's amount",
        0x24 => "the action's number",
        0x25 => "the attack's name",
        0x27 => "the attack's element",
        0x28 => "the critical chance",
        0x29 => "the critical multiplier",
        0x2A => "the damage",
        0x2B => "the damage spread",
        0x4E => "the poison chance",
        0x50 => "the silence chance",
        0x52 => "the death chance",
        0x54 => "the paralysis chance",
        0x56 => "the confusion chance",
        0x58 => "the sleep chance",
        >= 0x80 and <= 0x87 => $"its counter {r - 0x7F}",
        >= 0x88 => $"its value {r:X2}",
        _ => $"shared value {r:X2}",
    };

    public static readonly int[] KnownRegisters = { 0x2A, 0x27, 0x28, 0x29, 0x2B, 0x4E, 0x50, 0x52, 0x54, 0x56, 0x58, 0x10, 0x20, 0x21, 0x23, 0x24, 0x25,
        0x80, 0x81, 0x82, 0x83, 0x84, 0x85, 0x86, 0x87 };

    // ── steps ────────────────────────────────────────────────────────────────

    /// <summary>One command. Jumps point at a step (<see cref="Targets"/>), not at an offset, so steps can be added,
    /// removed and moved; a jump that leaves the script keeps its offset in <see cref="OutsideTargets"/>.</summary>
    public sealed class Step
    {
        public byte Op { get; set; }
        public byte[] Args { get; set; } = Array.Empty<byte>();
        /// <summary>Per jump argument (in order): the step it lands on, or null.</summary>
        public Step?[] Targets { get; set; } = Array.Empty<Step?>();
        /// <summary>Per jump argument: the raw offset when the target isn't a step of this script.</summary>
        public int[] OutsideTargets { get; set; } = Array.Empty<int>();
        public int Offset { get; set; }
        public OpDef? Def => L2AiScripts.Def(Op);
        public int Length => 1 + Args.Length;

        public Step Clone() => new() { Op = Op, Args = (byte[])Args.Clone(), Targets = (Step?[])Targets.Clone(), OutsideTargets = (int[])OutsideTargets.Clone() };
    }

    /// <summary>Byte position of argument <paramref name="index"/> in a command's arguments.</summary>
    public static int ArgAt(OpDef d, int index) => d.Args.Take(index).Sum(Size);

    /// <summary>Reads the script at file offset <paramref name="start"/>; jumps count from file offset
    /// <paramref name="baseAt"/>. Stops at 00, 4F, 03 or 43 once no jump goes further. A jump to code outside that
    /// run (some fan hacks share code between scripts) brings that code in too, as more steps after the script (with
    /// a "go to" where it runs into steps already read), so the script still works when it is moved. A jump that
    /// lands inside a command, or on bytes that aren't commands, stays in <see cref="Step.OutsideTargets"/>.</summary>
    public static List<Step> Parse(RomBuffer rom, int start, int baseAt)
    {
        var steps = Parse(rom, start, baseAt, straight: true);
        if (!steps.Any(s => s.OutsideTargets.Any(o => o >= 0))) return steps;
        // a jump into the middle of a command can mean data was read as commands: try following the jumps
        try
        {
            var follow = Parse(rom, start, baseAt, straight: false);
            int Outside(List<Step> l) => l.Sum(s => s.OutsideTargets.Count(o => o >= 0));
            return Outside(follow) < Outside(steps) ? follow : steps;
        }
        catch (InvalidDataException) { return steps; }
    }

    private static List<Step> Parse(RomBuffer rom, int start, int baseAt, bool straight)
    {
        var steps = new List<Step>();
        var byOffset = new Dictionary<int, Step>();
        var jumps = new List<(Step S, int Arg, int To)>();
        var covered = new List<(int Start, int Length)>();
        // Offsets count from the base as the game adds them: a 16-bit sum in the base's bank (L2Rom.InBank), so a
        // script can sit before its record or wrap round the bank (the fan hacks' records in bank $D0 do both).
        int FileAt(int rel)
        {
            int f = L2Rom.InBank(baseAt, rel);
            return f >= 0 && f < rom.Length ? f : throw new InvalidDataException($"Offset {rel & 0xFFFF:X4} from 0x{baseAt:X6} isn't in the ROM.");
        }
        // one straight run of commands from offset p (stops at an end, or where it reaches steps already read)
        // straight: read on to the furthest jump (the game's layout), else stop at each end and follow the jumps
        List<Step> Run(int p, bool first)
        {
            var run = new List<Step>();
            int runStart = p, furthest = 0, done = 0;   // distances from runStart
            var runJumps = new List<(Step, int, int)>();
            for (int guard = 0; guard < 2000; guard++)
            {
                if (!first && byOffset.ContainsKey(p)) break;
                byte op = rom.ReadByte(FileAt(p));
                var d = Def(op) ?? throw new InvalidDataException($"Unknown command {op:X2} at 0x{FileAt(p):X6}.");
                var args = new byte[d.Length - 1];
                for (int k = 0; k < args.Length; k++) args[k] = rom.ReadByte(FileAt(p + 1 + k));
                var s = new Step { Op = op, Args = args, Offset = p };
                int jumpN = d.Args.Count(a => a == Arg.Jump);
                s.Targets = new Step?[jumpN];
                s.OutsideTargets = Enumerable.Repeat(-1, jumpN).ToArray();
                int j = 0;
                for (int i = 0; i < d.Args.Length; i++)
                {
                    if (d.Args[i] != Arg.Jump) continue;
                    int at = ArgAt(d, i), to = s.Args[at] | s.Args[at + 1] << 8;
                    runJumps.Add((s, j++, to));
                    int dist = (to - runStart) & 0xFFFF;
                    if (straight && dist > furthest && dist < done + 0x400) furthest = dist;
                }
                run.Add(s);
                p = (p + d.Length) & 0xFFFF;
                done += d.Length;
                if (d.EndsPath && done > furthest) break;
            }
            // nothing is added unless the whole run reads as commands
            foreach (var s in run) byOffset[s.Offset] = s;
            jumps.AddRange(runJumps);
            covered.Add((runStart, done));
            // a run that ran into steps already read carries on there
            if (!first && run.Count > 0 && run[^1].Def?.EndsPath != true && byOffset.TryGetValue(p, out var next))
            {
                var go = New(0x03);
                go.Targets[0] = next;
                go.Offset = -1;
                run.Add(go);
            }
            return run;
        }
        int startAt = L2Rom.OffsetInBank(baseAt, start);
        try { steps.AddRange(Run(startAt, true)); }
        catch (InvalidDataException) when (straight)
        {
            // (some fan hacks jump ahead over other data: follow the jumps instead of reading straight on)
            return Parse(rom, start, baseAt, straight: false);
        }
        for (int k = 0; k < jumps.Count; k++)
        {
            int to = jumps[k].To;
            if (byOffset.ContainsKey(to) || covered.Any(c => ((to - c.Start) & 0xFFFF) < c.Length)) continue;
            try { steps.AddRange(Run(to, false)); } catch (InvalidDataException) { }
        }
        foreach (var (s, j, to) in jumps)
            if (byOffset.TryGetValue(to, out var t)) s.Targets[j] = t; else s.OutsideTargets[j] = to;
        return steps;
    }

    /// <summary>The script's bytes, laid out from offset <paramref name="first"/> (counted from the jump base).</summary>
    public static byte[] Build(IReadOnlyList<Step> steps, int first)
    {
        var at = new Dictionary<Step, int>();
        int o = first;
        foreach (var s in steps) { at[s] = o; o += s.Length; }
        var bytes = new List<byte>();
        foreach (var s in steps)
        {
            var args = (byte[])s.Args.Clone();
            var d = s.Def;
            if (d != null)
            {
                int j = 0;
                for (int i = 0; i < d.Args.Length; i++)
                {
                    if (d.Args[i] != Arg.Jump) continue;
                    var t = j < s.Targets.Length ? s.Targets[j] : null;
                    if (t != null && !at.ContainsKey(t)) throw new InvalidOperationException("A \"go to\" points at a step that was removed.");
                    int to = t != null ? at[t] : (j < s.OutsideTargets.Length && s.OutsideTargets[j] >= 0 ? s.OutsideTargets[j]
                        : throw new InvalidOperationException("A \"go to\" has no step to go to."));
                    int a = ArgAt(d, i);
                    args[a] = (byte)to; args[a + 1] = (byte)(to >> 8);
                    j++;
                }
            }
            bytes.Add(s.Op);
            bytes.AddRange(args);
        }
        return bytes.ToArray();
    }

    /// <summary>A new command with zero arguments (jumps unset).</summary>
    public static Step New(byte op)
    {
        var d = Def(op) ?? throw new ArgumentException($"Unknown command {op:X2}.");
        int jumps = d.Args.Count(a => a == Arg.Jump);
        return new Step { Op = op, Args = new byte[d.Length - 1], Targets = new Step?[jumps], OutsideTargets = Enumerable.Repeat(-1, jumps).ToArray() };
    }

    // ── monster attack names ($85:F2D7) ───────────────────────────────────────

    /// <summary>The names 1E shows: 1E n (n from 1) reads record n - 1 of the table $85:F2D7 (u16 pointers in bank $85, read
    /// by $85:9532): a byte (the default animation, E0 = none) then the name, ended by 00. The game's names run to 8A.</summary>
    public const int AttackNameTable = 0x2F2D7, AttackNameCount = 0x8A;

    public static string[] MonsterAttackNames(RomBuffer rom)
    {
        var names = new string[AttackNameCount + 1];
        names[0] = "(none)";
        for (int n = 1; n <= AttackNameCount; n++)
        {
            int at = 0x28000 + (rom.ReadUInt16Le(AttackNameTable + (n - 1) * 2) & 0x7FFF) + 1;
            var sb = new System.Text.StringBuilder();
            for (int i = 0; i < 37 && at + i < rom.Length; i++) { byte c = rom.ReadByte(at + i); if (c == 0) break; sb.Append(c is >= 0x20 and < 0x7F ? (char)c : '?'); }
            names[n] = sb.ToString();
        }
        return names;
    }

    // ── the shared library ($96:FADD) ─────────────────────────────────────────

    /// <summary>The shared script table ($96:FADD): u16 offsets from the table, each script based at its own start.</summary>
    public const int LibraryTable = 0xB7ADD, LibraryCount = 0x29;

    public static int LibraryAt(RomBuffer rom, int n) =>
        n >= 0 && n < LibraryCount ? LibraryTable + rom.ReadUInt16Le(LibraryTable + n * 2) : -1;

    /// <summary>Shared script <paramref name="n"/> as steps (its jumps count from its own start).</summary>
    public static List<Step> LoadLibrary(RomBuffer rom, int n)
    {
        int at = LibraryAt(rom, n);
        if (at < 0) throw new ArgumentOutOfRangeException(nameof(n), $"There is no shared script {n:X2}.");
        return Parse(rom, at, at);
    }

    /// <summary>Puts shared script <paramref name="n"/>'s own steps where step <paramref name="index"/> calls it (42), so
    /// this script can change them without changing every monster that shares them. Its returns (43) become a "go to"
    /// the step after the call (the last one simply goes away). Returns how many steps came in.</summary>
    public static int InlineLibrary(RomBuffer rom, List<Step> steps, int index)
    {
        var call = steps[index];
        if (call.Op != 0x42) throw new ArgumentException("That step doesn't call a shared script.");
        var lib = LoadLibrary(rom, ArgValue(call, 0));
        if (lib.Any(s => s.OutsideTargets.Any(o => o >= 0))) throw new InvalidOperationException("That shared script jumps outside itself and can't be copied in.");
        if (lib.Any(s => s.Op == 0x42)) throw new InvalidOperationException("That shared script calls another one (the game allows one level only).");
        var after = index + 1 < steps.Count ? steps[index + 1] : null;
        // a "go to" pointing at the call now points at the first copied step
        var copy = new List<Step>(lib);
        if (copy.Count > 0 && copy[^1].Op == 0x43 && !lib.Any(s => s.Targets.Contains(copy[^1]))) copy.RemoveAt(copy.Count - 1);
        for (int k = 0; k < copy.Count; k++)
        {
            if (copy[k].Op != 0x43) continue;
            if (after == null) { copy[k] = New(0x00); continue; }   // nothing after the call: end
            var go = New(0x03); go.Targets[0] = after;
            foreach (var s in copy) for (int t = 0; t < s.Targets.Length; t++) if (s.Targets[t] == copy[k]) s.Targets[t] = go;
            copy[k] = go;
        }
        if (copy.Count == 0) copy.Add(after != null ? GoTo(after) : New(0x00));
        foreach (var s in steps) for (int t = 0; t < s.Targets.Length; t++) if (s.Targets[t] == call) s.Targets[t] = copy[0];
        steps.RemoveAt(index);
        steps.InsertRange(index, copy);
        return copy.Count;

        static Step GoTo(Step to) { var g = New(0x03); g.Targets[0] = to; return g; }
    }

    /// <summary>What the game's shared scripts do, read from their steps (2026-10-25). They are run when the fighter is hit
    /// (most) or by an attack's effect; "the attack's element" is register 27, "the damage" 2A.</summary>
    private static readonly Dictionary<int, string> LibraryNames = new()
    {
        [0x00] = "takes double damage from fire", [0x01] = "takes double damage from thunder", [0x02] = "takes double damage from water",
        [0x03] = "takes double damage from ice", [0x04] = "double damage from thrown weapons, none from earth",
        [0x05] = "takes double damage from light", [0x06] = "takes half damage from fire", [0x07] = "takes half damage from thunder",
        [0x08] = "takes half damage from water", [0x09] = "takes half damage from ice", [0x0A] = "takes half damage from light",
        [0x0B] = "takes half damage from plain (no element) attacks", [0x0C] = "takes double damage from dragon-slaying attacks",
        [0x0D] = "takes double damage from dark", [0x0E] = "takes half damage from dark", [0x0F] = "takes double damage from blunt weapons",
        [0x10] = "takes double damage from insect-slaying attacks", [0x11] = "immune to poison, silence, paralysis, confusion and sleep",
        [0x12] = "healing hurts it instead, and instant death can't work on it", [0x21] = "works out 1.5 x its own ATP (counter 1)",
        [0x23] = "works out its ATP with its battle bonus (counter 1)",
    };

    /// <summary>A shared script in words: the known ones by name, the others as their first steps.</summary>
    public static string LibraryName(RomBuffer rom, int n, NameLists names) =>
        LibraryNames.TryGetValue(n, out var known) ? known : LibrarySummary(rom, n, names);

    /// <summary>What a shared script does, in a few words, from its steps.</summary>
    public static string LibrarySummary(RomBuffer rom, int n, NameLists names)
    {
        try
        {
            var lib = LoadLibrary(rom, n);
            var lines = lib.Where(s => s.Op != 0x43).Take(4).Select(s => Describe(s, lib, names)).ToList();
            return lines.Count == 0 ? "(empty)" : string.Join("; ", lines) + (lib.Count(s => s.Op != 0x43) > 4 ? "..." : "");
        }
        catch (Exception ex) { return "(can't be read: " + ex.Message + ")"; }
    }

    // ── owners ───────────────────────────────────────────────────────────────

    /// <summary>File offset of the record that owns the script.</summary>
    public static int RecordAt(RomBuffer rom, Kind kind, int index) => kind switch
    {
        Kind.MonsterAttack or Kind.MonsterDefense => L2Battle.ReadMonster(rom, index).Offset,
        Kind.CapsuleAi or Kind.CapsuleDefense => L2Capsules.At(rom, index),
        Kind.Spell => L2Battle.ReadSpell(rom, index).Offset,
        Kind.IpAttack => IpEffectBase(rom, IpEffect(rom, index)),
        _ => L2Battle.ItemOffset(rom, index),
    };

    /// <summary>The item property word (bit) that holds the script of an item kind.</summary>
    public static int ItemBit(Kind k) => 1 << (k - Kind.ItemMenu);

    /// <summary>An IP attack's effect number (the first word of its record; several attacks can share one).</summary>
    public static int IpEffect(RomBuffer rom, int ip) => rom.ReadUInt16Le(L2Battle.ReadIp(rom, ip).Offset);

    /// <summary>IP effect scripts: table $95:F1EF of u16 offsets from the table ($81:F46B: LDA $95F1EF,X / CLC / ADC
    /// #$F1EF, file 0xF46D), bank $95 (LDA #$95 at $81:AB59); the script is run with base = table + offset and offset 1.
    /// Lufia Forge moves them to add effects (<see cref="L2Magic"/>), so the table, the sum and the bank are read from the code.</summary>
    public const int IpEffectTable = 0xAF1EF;
    private const int IpEffectTableRef = 0xF46D, IpEffectBankRef = 0xAB5A;

    public static int IpEffectBase(RomBuffer rom, int effect)
    {
        int table = L2Rom.SnesToFile((int)rom.ReadUInt24Le(IpEffectTableRef + 1));
        int adc = rom.ReadUInt16Le(IpEffectTableRef + 6), bank = rom.ReadByte(IpEffectBankRef);
        return L2Rom.SnesToFile(bank << 16 | (rom.ReadUInt16Le(table + effect * 2) + adc) & 0xFFFF);
    }

    /// <summary>The IP attacks that use an effect.</summary>
    public static List<int> IpAttacksWithEffect(RomBuffer rom, int effect) =>
        Enumerable.Range(0, L2Battle.IpCount(rom)).Where(i => IpEffect(rom, i) == effect).ToList();

    /// <summary>File offset where the game's own script starts, or -1 when it has none.</summary>
    public static int OriginalAt(RomBuffer rom, Kind kind, int index)
    {
        switch (kind)
        {
            case Kind.MonsterAttack: return L2Battle.ReadMonster(rom, index).AttackScript;
            case Kind.MonsterDefense: return L2Battle.ReadMonster(rom, index).DefenseScript;
            case Kind.Spell:
                var sp = L2Battle.ReadSpell(rom, index);
                int w = rom.ReadUInt16Le(sp.Offset + 0x11);
                return w == 0 ? -1 : L2Rom.InBank(sp.Offset, w);
            case Kind.IpAttack: return L2Rom.InBank(RecordAt(rom, kind, index), 1);
            case Kind.ItemMenu or Kind.ItemBattle or Kind.Weapon or Kind.Armor:
                var it = L2Battle.ReadItem(rom, index);
                return it.Values.TryGetValue(ItemBit(kind), out var v) && v != 0 ? L2Rom.InBank(it.Offset, v) : -1;
            default:
                var c = L2Capsules.Read(rom, index);
                int off = kind == Kind.CapsuleAi ? c.AiScript : c.DefenseScript;
                return off == 0 ? -1 : L2Rom.InBank(c.At, off);
        }
    }

    /// <summary>File offsets of every byte the game's own monster scripts (turn and when hit) are read from. The fan hacks
    /// keep scripts in the unused end of a record's name field (and jump between records), so a name or field edit must
    /// keep clear of these.</summary>
    public static HashSet<int> MonsterScriptBytes(RomBuffer rom)
    {
        var used = new HashSet<int>();
        for (int i = 0, n = L2Battle.MonsterCountIn(rom); i < n; i++)
        {
            var m = L2Battle.ReadMonster(rom, i);
            foreach (int at in new[] { m.AttackScript, m.DefenseScript })
            {
                if (at < 0) continue;
                List<Step> steps;
                try { steps = Parse(rom, at, m.Offset); } catch (InvalidDataException) { continue; }
                foreach (var s in steps)
                    if (s.Offset >= 0)
                        for (int k = 0; k < s.Length; k++) used.Add(L2Rom.InBank(m.Offset, s.Offset + k));
            }
        }
        return used;
    }

    /// <summary>The script as it runs now: Lufia Forge's (from the table) or the game's; empty when there is none.</summary>
    public static (List<Step> Steps, bool Edited) Load(RomBuffer rom, Kind kind, int index)
    {
        if (Override(rom, kind, index) is int far) return (Parse(rom, far + 1, far), true);
        int at = OriginalAt(rom, kind, index);
        return at < 0 ? (new List<Step>(), false) : (Parse(rom, at, RecordAt(rom, kind, index)), false);
    }

    /// <summary>The game's own script (empty when there is none).</summary>
    public static List<Step> LoadOriginal(RomBuffer rom, Kind kind, int index)
    {
        int at = OriginalAt(rom, kind, index);
        return at < 0 ? new List<Step>() : Parse(rom, at, RecordAt(rom, kind, index));
    }

    // ── the hooks and the table ─────────────────────────────────────────────

    private const string Magic = "LF2AI";
    private const int CodeAt = 0x10, TableAt = 0x200, BlockSize = 0x1400, EntrySize = 6;
    private const int TableCapacity = (BlockSize - TableAt) / EntrySize - 1;

    private const int AttackSite = 0xB1D4, CapsuleSite = 0xB1A3, DefenseSite1 = 0x2D165, DefenseSite2 = 0x2D1A6;
    // version 2: the script runner $81:FAC9 and the item loader $81:F1C5
    private const int RunnerSite = 0xFAC9, ItemLoaderSite = 0xF1C5;
    private static readonly byte[] RunnerOriginal = Hex("08 8b c2 30 48 da 5a e2 20 4b ab c0 00 00");
    private static readonly byte[] ItemLoaderOriginal = Hex("08 8b e2 20 c2 10 a2 ad 0b");
    private const int CurrentVersion = 2;
    private static readonly byte[] AttackOriginal = Hex("bc 4a 00 f0 0e bd 50 00 22 e2 fc 81 8e 42 0a a9 96 80 08 8c 42 0a a0 20 ef a9 85 8d 44 0a 22 c9 fa 81 6b");
    private static readonly byte[] CapsuleOriginal = Hex("ac 29 11 f0 0e ae 24 11 8e 42 0a ad 26 11 8d 44 0a 80 0e a2 00 00 a0 20 ef 8e 42 0a a9 85 8d 44 0a 22 c9 fa 81 6b");
    private static readonly byte[] DefenseOriginal = Hex("bd 47 00 8d 44 0a bc 4c 00 22 c9 fa 81");
    /// <summary>Where the monster records' bank is in the attack site's code (LDA #$96): the hacks that moved the monster
    /// data (Frue, Kureji, Spekkio, Wrath of Foomy: bank $D0) changed only that byte there.</summary>
    private const int MonsterBankAt = 16;

    private static byte[] Hex(string s) => s.Split(' ').Select(b => Convert.ToByte(b, 16)).ToArray();

    /// <summary>File offset of Lufia Forge's script block (hooks and table), or -1 when it isn't in the ROM.</summary>
    public static int Block(RomBuffer rom)
    {
        if (rom.Length <= AttackSite + 4 || rom.ReadByte(AttackSite) != 0x5C) return -1;
        int code = L2Rom.SnesToFile((int)rom.ReadUInt24Le(AttackSite + 1));
        int block = code - CodeAt - HookAOffset;
        return block >= 0 && block + BlockSize <= rom.Length && System.Text.Encoding.ASCII.GetString(rom.ReadBytes(block, Magic.Length)) == Magic ? block : -1;
    }

    public static bool IsInstalled(RomBuffer rom) => Block(rom) >= 0;

    private static int Version(RomBuffer rom, int block) => rom.ReadByte(block + Magic.Length);

    /// <summary>Whether the hooks can go in (the four places still have the game's code).</summary>
    public static bool CanInstall(RomBuffer rom, out string why)
    {
        why = "";
        if (IsInstalled(rom)) return true;
        bool Same(int at, byte[] b) => at + b.Length <= rom.Length && rom.ReadBytes(at, b.Length).SequenceEqual(b);
        bool attack = AttackSite + AttackOriginal.Length <= rom.Length &&
                      Enumerable.Range(0, AttackOriginal.Length).All(i => i == MonsterBankAt || rom.ReadByte(AttackSite + i) == AttackOriginal[i]);
        if (!attack || !Same(CapsuleSite, CapsuleOriginal) || !Same(DefenseSite1, DefenseOriginal) || !Same(DefenseSite2, DefenseOriginal)
            || !Same(RunnerSite, RunnerOriginal) || !Same(ItemLoaderSite, ItemLoaderOriginal))
        {
            why = "This ROM's battle code isn't the US game's where the script hooks go (another patch or hack changed it), so edited scripts can't be added.";
            return false;
        }
        return true;
    }

    // where each routine starts in the code (set by Assemble)
    private static int HookAOffset, HookCOffset, HookDOffset, HookEOffset, HookFOffset;

    static L2AiScripts() => Assemble(0x808000, 0x808000, 0x96);

    /// <summary>The hook code for a block whose code starts at SNES <paramref name="code"/> and whose table is at SNES
    /// <paramref name="table"/>. Routines: Find (the table lookup), A (a monster's turn), C (a capsule monster's turn),
    /// D (being hit). All run with an 8-bit accumulator and 16-bit index registers, as the game's code there.</summary>
    private static byte[] Assemble(int code, int table, int monsterBank)
    {
        var a = new Asm(code);
        void Long(byte op, int addr) => a.B(op, (byte)addr, (byte)(addr >> 8), (byte)(addr >> 16));
        // Find: A = kind, X = record (16-bit). Found: $0A42/$0A44 = the script's base, Y = 1, carry set; else X, Y kept, carry clear.
        a.Label("find");
        a.B(0x5A, 0xDA, 0x48);                 // PHY PHX PHA  (1,S kind, 2,S record)
        a.B(0xA2, 0x00, 0x00);                 // LDX #0
        a.Label("loop");
        Long(0xBF, table + 2);                 // LDA table+2,X (kind)
        a.B(0xC9, 0xFF); a.Br(0xF0, "missing"); // CMP #$FF / BEQ
        a.B(0xC3, 0x01); a.Br(0xD0, "next");    // CMP 1,S / BNE
        a.B(0xC2, 0x20);                       // REP #$20
        Long(0xBF, table);                     // LDA table,X (record)
        a.B(0xC3, 0x02);                       // CMP 2,S
        a.B(0xE2, 0x20); a.Br(0xD0, "next");    // SEP #$20 / BNE
        a.B(0xC2, 0x20);
        Long(0xBF, table + 3);                 // LDA table+3,X (script lo/hi)
        a.B(0x8D, 0x42, 0x0A);                 // STA $0A42
        a.B(0xE2, 0x20);
        Long(0xBF, table + 5);                 // LDA table+5,X (script bank)
        a.B(0x8D, 0x44, 0x0A);                 // STA $0A44
        a.B(0x68, 0xFA, 0x7A);                 // PLA PLX PLY
        a.B(0xA0, 0x01, 0x00, 0x38, 0x60);     // LDY #1 / SEC / RTS
        a.Label("next");
        a.B(0xE8, 0xE8, 0xE8, 0xE8, 0xE8, 0xE8); a.Br(0x80, "loop");
        a.Label("missing");
        a.B(0x68, 0xFA, 0x7A, 0x18, 0x60);     // PLA PLX PLY / CLC / RTS

        // A: a monster's turn (JML from $81:B1D4; X = the combatant)
        a.Label("hookA");
        a.B(0xBC, 0x4A, 0x00);                 // LDY $004A,X (attack script offset)
        a.B(0xBD, 0x50, 0x00);                 // LDA $0050,X (monster id)
        Long(0x22, 0x81FCE2);                  // JSL $81:FCE2 -> X = record
        a.B(0xA9, (byte)Kind.MonsterAttack); a.Jsr("find");
        a.Br(0xB0, "runA");
        a.B(0xC0, 0x00, 0x00); a.Br(0xF0, "plainA");   // CPY #0 / BEQ
        a.B(0x8E, 0x42, 0x0A, 0xA9, (byte)monsterBank, 0x8D, 0x44, 0x0A);   // STX $0A42 / LDA #bank / STA $0A44
        a.Label("runA"); Long(0x5C, 0x81B1F2);  // JML (JSL $81:FAC9 / RTL)
        a.Label("plainA"); Long(0x5C, 0x81B1E7); // JML (the built-in plain attack)

        // C: a capsule monster's turn (JML from $81:B1A3)
        a.Label("hookC");
        a.B(0xAC, 0x29, 0x11, 0xAE, 0x24, 0x11);   // LDY $1129 / LDX $1124
        a.B(0xA9, (byte)Kind.CapsuleAi); a.Jsr("find");
        a.Br(0xB0, "runC");
        a.B(0xC0, 0x00, 0x00); a.Br(0xF0, "plainC");
        a.B(0x8E, 0x42, 0x0A, 0xAD, 0x26, 0x11, 0x8D, 0x44, 0x0A);   // STX $0A42 / LDA $1126 / STA $0A44
        a.Label("runC"); Long(0x5C, 0x81B1C4);
        a.Label("plainC"); Long(0x5C, 0x81B1B6);

        // D: being hit (JSL from $85:D165 and $85:D1A6, in place of LDA $0047,X / STA $0A44 / LDY $004C,X)
        a.Label("hookD");
        a.B(0xBD, 0x47, 0x00, 0x8D, 0x44, 0x0A, 0xBC, 0x4C, 0x00);
        a.B(0xDA, 0x48);                       // PHX / PHA (bank)
        a.B(0xC2, 0x20, 0xBD, 0x45, 0x00, 0xAA, 0xE2, 0x20);   // REP / LDA $0045,X / TAX / SEP
        a.B(0x68, 0xC9, 0x97); a.Br(0xF0, "capD");             // PLA / CMP #$97 / BEQ
        a.B(0xA9, (byte)Kind.MonsterDefense); a.Br(0x80, "findD");
        a.Label("capD"); a.B(0xA9, (byte)Kind.CapsuleDefense);
        a.Label("findD"); a.Jsr("find");
        a.B(0xFA, 0x6B);                       // PLX / RTL

        // (version 2, after the routines above so a version 1 block keeps their places)
        // E: the script runner (JML from $81:FAC9, in place of PHP / PHB / REP #$30). Y = offset, $0A42/$0A44 = base.
        // An item's marker offset ($8000 | key, from F) runs its script from the table (none: offset 0, nothing runs);
        // any other script is looked up by its address (spells, IP attacks).
        a.Label("hookE");
        a.B(0x08, 0x8B, 0xC2, 0x30, 0x48, 0xDA, 0x5A);   // PHP PHB REP #$30 PHA PHX PHY (as the game)
        a.B(0xC0, 0x00, 0x00); a.Br(0xF0, "outE");        // CPY #0 / BEQ
        // a marker has bit 15 set and a base (the game's built-in scripts run with base 0 and offsets like $EF24)
        a.B(0x98); a.Br(0x10, "absE");                    // TYA / BPL
        a.B(0xAD, 0x42, 0x0A); a.Br(0xD0, "markE");       // LDA $0A42 / BNE
        a.Label("absE");
        a.B(0x98, 0x18, 0x6D, 0x42, 0x0A, 0xAA);          // TYA / CLC / ADC $0A42 / TAX (the script's address)
        a.B(0xE2, 0x20, 0xAD, 0x44, 0x0A); a.Jsr("find"); // SEP #$20 / LDA $0A44 (its bank as the kind)
        a.Br(0x80, "outE");
        a.Label("markE");
        a.B(0x98, 0x29, 0xFF, 0x7F, 0xAA, 0xE2, 0x20, 0xA9, ItemMarker); a.Jsr("find");   // TYA / AND #$7FFF / TAX / SEP / LDA #FD
        a.Br(0xB0, "outE");
        a.B(0xA0, 0x00, 0x00);                            // LDY #0 (not in the table: no script)
        a.Label("outE"); Long(0x5C, 0x81FAD0);            // JML back (SEP #$20 / PHK / PLB / CPY #0...)

        // F: the item loader ($81:F1C5, JML in place of PHP / PHB / SEP #$20): runs the game's loader, then gives each
        // property the table has a script for the marker word $8000 | (item | property << 9) at $0B8D + 2 * property.
        a.Label("hookF");
        a.B(0x4B); a.Pea("retF");                         // PHK / PEA retF-1 (the loader's RTL comes back here)
        a.B(0x08, 0x8B, 0xE2, 0x20); Long(0x5C, 0x81F1C9); // PHP PHB SEP #$20 (as the game) / JML on
        a.Label("retF");
        a.B(0x08, 0xC2, 0x30, 0x48, 0xDA, 0x5A);          // PHP / REP #$30 / PHA PHX PHY
        Long(0xAF, 0x7E0A06); a.B(0x29, 0xFF, 0x01, 0x48); // LDA $7E:0A06 / AND #$01FF / PHA (1,S = the item)
        a.B(0xA2, 0x00, 0x00);                            // LDX #0
        a.Label("loopF");
        Long(0xBF, table + 2); a.B(0x29, 0xFF, 0x00);     // LDA table+2,X / AND #$FF (kind)
        a.B(0xC9, 0xFF, 0x00); a.Br(0xF0, "doneF");
        a.B(0xC9, ItemMarker, 0x00); a.Br(0xF0, "isFD");
        a.B(0xC9, ItemWordKind, 0x00); a.Br(0xD0, "nextF");
        // FC: a property word the record doesn't have (key = item | bit << 9, the value in the script field)
        Long(0xBF, table); a.B(0x29, 0xFF, 0x01, 0xC3, 0x01); a.Br(0xD0, "nextF");
        Long(0xBF, table + 3); a.B(0x48);                 // LDA table+3,X / PHA (the value)
        Long(0xBF, table); a.B(0xEB, 0x4A, 0x29, 0x0F, 0x00, 0x0A);   // XBA / LSR / AND #$0F / ASL (2 * bit)
        a.B(0xDA, 0xAA, 0xA3, 0x03);                      // PHX / TAX / LDA 3,S
        Long(0x9F, 0x7E0B8D);                             // STA $7E:0B8D,X
        a.B(0xFA, 0x68); a.Br(0x80, "nextF");             // PLX / PLA
        a.Label("isFD");
        Long(0xBF, table); a.B(0x29, 0xFF, 0x01, 0xC3, 0x01); a.Br(0xD0, "nextF");   // key & $1FF == item?
        Long(0xBF, table); a.B(0x09, 0x00, 0x80, 0x48);   // LDA table,X / ORA #$8000 / PHA (the marker)
        Long(0xBF, table); a.B(0xEB, 0x4A, 0x29, 0x03, 0x00, 0x0A);   // XBA / LSR / AND #3 / ASL (2 * property)
        a.B(0xDA, 0xAA, 0xA3, 0x03);                      // PHX / TAX / LDA 3,S
        Long(0x9F, 0x7E0B8D);                             // STA $7E:0B8D,X
        a.B(0xFA, 0x68);                                  // PLX / PLA
        a.Label("nextF");
        a.B(0x8A, 0x18, 0x69, 0x06, 0x00, 0xAA); a.Br(0x80, "loopF");   // X += 6
        a.Label("doneF");
        a.B(0x68, 0x7A, 0xFA, 0x68, 0x28, 0x6B);          // PLA PLY PLX PLA / PLP / RTL

        var bytes = a.Done();
        HookAOffset = a.At("hookA"); HookCOffset = a.At("hookC"); HookDOffset = a.At("hookD");
        HookEOffset = a.At("hookE"); HookFOffset = a.At("hookF");
        return bytes;
    }

    /// <summary>A tiny 65816 assembler: bytes, labels, 8-bit branches and JSRs within the block.</summary>
    private sealed class Asm(int origin)
    {
        private readonly List<byte> _b = new();
        private readonly Dictionary<string, int> _labels = new();
        // fix kinds: 0 = 8-bit branch, 1 = 16-bit address, 2 = 16-bit address - 1 (PEA for an RTL/RTS)
        private readonly List<(int At, string Label, int Fix)> _fix = new();
        public void B(params byte[] b) => _b.AddRange(b);
        public void Label(string l) => _labels[l] = _b.Count;
        public void Br(byte op, string l) { _b.Add(op); _fix.Add((_b.Count, l, 0)); _b.Add(0); }
        public void Jsr(string l) { _b.Add(0x20); _fix.Add((_b.Count, l, 1)); _b.Add(0); _b.Add(0); }
        public void Pea(string l) { _b.Add(0xF4); _fix.Add((_b.Count, l, 2)); _b.Add(0); _b.Add(0); }
        public int At(string l) => _labels[l];
        public byte[] Done()
        {
            foreach (var (at, l, fix) in _fix)
            {
                int to = _labels[l];
                if (fix > 0) { int addr = (origin + to - (fix == 2 ? 1 : 0)) & 0xFFFF; _b[at] = (byte)addr; _b[at + 1] = (byte)(addr >> 8); continue; }
                int rel = to - (at + 1);
                if (rel is < -128 or > 127) throw new InvalidOperationException($"Branch to {l} too far.");
                _b[at] = (byte)rel;
            }
            return _b.ToArray();
        }
    }

    /// <summary>Puts the hooks and the empty table in (once).</summary>
    public static int Install(RomBuffer rom)
    {
        int have = Block(rom);
        if (have >= 0) return Version(rom, have) >= CurrentVersion ? have : Upgrade(rom, have);
        if (!CanInstall(rom, out var why)) throw new InvalidOperationException(why);
        int block = L2Rom.Allocate(rom, BlockSize);
        int code = L2Rom.FileToSnes(block + CodeAt), table = L2Rom.FileToSnes(block + TableAt);
        int bank = rom.ReadByte(AttackSite + MonsterBankAt);
        var bytes = Assemble(code, table, bank);
        if (CodeAt + bytes.Length > TableAt) throw new InvalidOperationException("The hook code doesn't fit.");
        rom.WriteBytes(block, new byte[BlockSize]);
        rom.WriteBytes(block, System.Text.Encoding.ASCII.GetBytes(Magic));
        rom.WriteByte(block + Magic.Length, CurrentVersion);
        rom.WriteByte(block + Magic.Length + 1, (byte)bank);   // the monster records' bank (the hook overwrites where it was)
        rom.WriteBytes(block + CodeAt, bytes);
        rom.WriteBytes(block + TableAt, new byte[] { 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF });
        byte[] Jump(byte op, int addr) => new[] { op, (byte)addr, (byte)(addr >> 8), (byte)(addr >> 16) };
        rom.WriteBytes(AttackSite, Jump(0x5C, code + HookAOffset));
        rom.WriteBytes(CapsuleSite, Jump(0x5C, code + HookCOffset));
        var d = Jump(0x22, code + HookDOffset).Concat(Enumerable.Repeat((byte)0xEA, 5)).ToArray();
        rom.WriteBytes(DefenseSite1, d);
        rom.WriteBytes(DefenseSite2, d);
        rom.WriteBytes(RunnerSite, Jump(0x5C, code + HookEOffset));
        rom.WriteBytes(ItemLoaderSite, Jump(0x5C, code + HookFOffset));
        return block;
    }

    /// <summary>The monster records moved to bank <paramref name="bank"/> (<see cref="L2NewMonsters"/>): the turn hook's
    /// LDA #bank follows (the code is assembled again; its routines keep their places).</summary>
    internal static void SetMonsterBank(RomBuffer rom, int bank)
    {
        int block = Block(rom);
        if (block < 0) return;
        rom.WriteByte(block + Magic.Length + 1, (byte)bank);
        int code = L2Rom.FileToSnes(block + CodeAt), table = L2Rom.FileToSnes(block + TableAt);
        var bytes = Assemble(code, table, bank);
        if (Version(rom, block) < CurrentVersion) bytes = bytes.Take(HookEOffset).ToArray();   // (a version 1 block has only A-D)
        rom.WriteBytes(block + CodeAt, bytes);
    }

    /// <summary>A version 1 block (monster and capsule scripts only) gets the effect hooks: the code is assembled again
    /// (the first routines keep their places) and the runner and item loader jump to it.</summary>
    private static int Upgrade(RomBuffer rom, int block)
    {
        bool Same(int at, byte[] b) => at + b.Length <= rom.Length && rom.ReadBytes(at, b.Length).SequenceEqual(b);
        if (!Same(RunnerSite, RunnerOriginal) || !Same(ItemLoaderSite, ItemLoaderOriginal))
            throw new InvalidOperationException("This ROM's battle code isn't the US game's where the effect hooks go (another patch changed it), so item, spell and IP effects can't be edited.");
        int code = L2Rom.FileToSnes(block + CodeAt), table = L2Rom.FileToSnes(block + TableAt);
        var bytes = Assemble(code, table, rom.ReadByte(block + Magic.Length + 1));
        if (CodeAt + bytes.Length > TableAt) throw new InvalidOperationException("The hook code doesn't fit.");
        rom.WriteBytes(block + CodeAt, bytes);
        byte[] Jump(byte op, int addr) => new[] { op, (byte)addr, (byte)(addr >> 8), (byte)(addr >> 16) };
        rom.WriteBytes(RunnerSite, Jump(0x5C, code + HookEOffset));
        rom.WriteBytes(ItemLoaderSite, Jump(0x5C, code + HookFOffset));
        rom.WriteByte(block + Magic.Length, CurrentVersion);
        return block;
    }

    /// <summary>The table entry's key: (record, kind 0-3) for monsters and capsules; (item | property << 9, FD) for items,
    /// whose loader is given a marker word $8000 | key; (script address, bank) for spells and IP attacks, the script the
    /// game would run.</summary>
    private static (int Key, byte Kind) TableKey(RomBuffer rom, Kind kind, int index)
    {
        if (IsItem(kind)) return (index | (kind - Kind.ItemMenu) << 9, ItemMarker);
        if (kind is Kind.Spell or Kind.IpAttack)
        {
            int snes = L2Rom.FileToSnes(OriginalAt(rom, kind, index));
            return (snes & 0xFFFF, (byte)(snes >> 16));
        }
        return (L2Rom.FileToSnes(RecordAt(rom, kind, index)) & 0xFFFF, (byte)kind);
    }

    private const byte ItemMarker = 0xFD, ItemWordKind = 0xFC;

    // ── property words an item record doesn't have (stat bonuses, IP attack, battle animation) ───────────────

    /// <summary>The words Lufia Forge gives an item on top of its record (bit -> value): the item loader hook puts them
    /// where the game keeps the record's own ($0B8D + 2 * bit), so the game reads them like any other.</summary>
    public static Dictionary<int, int> ItemAddedWords(RomBuffer rom, int item)
    {
        var d = new Dictionary<int, int>();
        int block = Block(rom);
        if (block < 0 || Version(rom, block) < 2) return d;
        foreach (var e in Entries(rom, block))
            if (e.Kind == ItemWordKind && (e.Record & 0x1FF) == item)
                d[1 << ((e.Record >> 9) & 0xF)] = rom.ReadUInt16Le(e.At + 3);
        return d;
    }

    /// <summary>Gives an item a property word its record doesn't have (null takes it away again).</summary>
    public static void SetItemAddedWord(RomBuffer rom, int item, int bit, int? value)
    {
        int b = System.Numerics.BitOperations.Log2((uint)bit);
        if (bit <= 0 || (bit & (bit - 1)) != 0 || b < 4) throw new ArgumentException("Scripts go through Save; this is for the other words.");
        int key = item | b << 9;
        int block = value == null ? Block(rom) : Install(rom);
        if (block < 0) return;
        var all = Entries(rom, block).ToList();
        int i = all.FindIndex(e => e.Kind == ItemWordKind && e.Record == key);
        if (value == null)
        {
            if (i < 0) return;
            var last = all[^1];
            if (i != all.Count - 1) rom.WriteBytes(all[i].At, rom.ReadBytes(last.At, EntrySize));
            rom.WriteBytes(last.At, new byte[] { 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF });
            return;
        }
        int at = i >= 0 ? all[i].At : block + TableAt + all.Count * EntrySize;
        if (i < 0)
        {
            if (all.Count >= TableCapacity) throw new InvalidOperationException("Lufia Forge's script table is full.");
            rom.WriteBytes(at + EntrySize, new byte[] { 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF });
        }
        int v = value.Value & 0xFFFF;
        rom.WriteBytes(at, new[] { (byte)key, (byte)(key >> 8), ItemWordKind, (byte)v, (byte)(v >> 8), (byte)0 });
    }

    private static IEnumerable<(int At, int Record, byte Kind, int Script)> Entries(RomBuffer rom, int block)
    {
        for (int i = 0; i < TableCapacity; i++)
        {
            int e = block + TableAt + i * EntrySize;
            byte k = rom.ReadByte(e + 2);
            if (k == 0xFF) yield break;
            yield return (e, rom.ReadUInt16Le(e), k, L2Rom.SnesToFile((int)rom.ReadUInt24Le(e + 3)));
        }
    }

    /// <summary>File offset of the script's base (its 00) when Lufia Forge's table has one for it, else null.</summary>
    public static int? Override(RomBuffer rom, Kind kind, int index)
    {
        int block = Block(rom);
        if (block < 0 || IsEffect(kind) && Version(rom, block) < 2) return null;
        var (key, k) = TableKey(rom, kind, index);
        foreach (var e in Entries(rom, block))
            if (e.Record == key && e.Kind == k) return e.Script;
        return null;
    }

    /// <summary>Writes the script for (kind, index) to the added space and points the table at it (the game's bytes
    /// stay as they are). Returns the file offset of the script.</summary>
    public static int Save(RomBuffer rom, Kind kind, int index, IReadOnlyList<Step> steps)
    {
        if (steps.Count == 0) throw new InvalidOperationException("The script is empty: it needs at least an End.");
        if (steps.Any(st => st.OutsideTargets.Any(o => o >= 0)))
            throw new InvalidOperationException("A \"go to\" jumps into the middle of a command or onto data, so the script can't be moved: point it at a step.");
        var body = Build(steps, 1);
        if (body.Length > 0x7F00) throw new InvalidOperationException("The script is too long.");
        if (kind is Kind.Spell or Kind.IpAttack && OriginalAt(rom, kind, index) < 0)
            throw new InvalidOperationException("It has no effect script for an edited one to take the place of.");
        int block = Install(rom);
        var (key, k) = TableKey(rom, kind, index);
        var entry = Entries(rom, block).FirstOrDefault(e => e.Record == key && e.Kind == k);
        if (entry.At <= 0 && Entries(rom, block).Count() >= TableCapacity) throw new InvalidOperationException("Lufia Forge's script table is full.");
        // [00][script][00]: a last step that doesn't end the script still stops at the 00 after it
        int need = body.Length + 2;
        int baseAt;
        if (entry.At > 0 && rom.ReadUInt16Le(entry.Script - 2) >= need)
            baseAt = entry.Script;   // fits where it was
        else
        {
            int cap = Math.Max(need + 16, 32);
            int at = L2Rom.Allocate(rom, cap + 2);
            rom.WriteUInt16Le(at, (ushort)cap);
            baseAt = at + 2;
        }
        int capacity = rom.ReadUInt16Le(baseAt - 2);
        rom.WriteBytes(baseAt, new byte[capacity]);
        rom.WriteByte(baseAt, 0x00);
        rom.WriteBytes(baseAt + 1, body);
        int snes = L2Rom.FileToSnes(baseAt);
        int e = entry.At > 0 ? entry.At : block + TableAt + Entries(rom, block).Count() * EntrySize;
        if (entry.At <= 0)
        {
            if ((e - block - TableAt) / EntrySize >= TableCapacity) throw new InvalidOperationException("Lufia Forge's script table is full.");
            rom.WriteBytes(e + EntrySize, new byte[] { 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF });
        }
        rom.WriteBytes(e, new[] { (byte)key, (byte)(key >> 8), k, (byte)snes, (byte)(snes >> 8), (byte)(snes >> 16) });
        return baseAt;
    }

    /// <summary>Back to the game's own script (or none): drops the table entry (its space is left as it is).</summary>
    public static bool Revert(RomBuffer rom, Kind kind, int index)
    {
        int block = Block(rom);
        if (block < 0) return false;
        var (key, k) = TableKey(rom, kind, index);
        var all = Entries(rom, block).ToList();
        int i = all.FindIndex(e => e.Record == key && e.Kind == k);
        if (i < 0) return false;
        // move the last entry into its place, end the table one entry earlier
        var last = all[^1];
        if (i != all.Count - 1) rom.WriteBytes(all[i].At, rom.ReadBytes(last.At, EntrySize));
        rom.WriteBytes(last.At, new byte[] { 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF });
        return true;
    }

    /// <summary>Spell and IP effect scripts are keyed by their address: when Lufia Forge moves those scripts
    /// (<see cref="L2Magic"/>), the entries follow them. <paramref name="map"/> gives a script's new file offset from its
    /// old one (null: it didn't move).</summary>
    internal static void MoveEffectKeys(RomBuffer rom, Func<int, int?> map)
    {
        int block = Block(rom);
        if (block < 0) return;
        foreach (var e in Entries(rom, block).ToList())
        {
            if (e.Kind < 0x80 || e.Kind is ItemMarker or ItemWordKind or 0xFF) continue;
            int old = L2Rom.SnesToFile(e.Kind << 16 | e.Record);
            if (map(old) is not int moved) continue;
            int snes = L2Rom.FileToSnes(moved);
            rom.WriteBytes(e.At, new[] { (byte)snes, (byte)(snes >> 8), (byte)(snes >> 16) });
        }
    }

    /// <summary>A script's bytes for a copy laid out from <paramref name="first"/> (counted from its jump base), or why it
    /// can't be copied.</summary>
    public static byte[] BuildCopy(IReadOnlyList<Step> steps, int first)
    {
        if (steps.Any(st => st.OutsideTargets.Any(o => o >= 0)))
            throw new InvalidOperationException("A \"go to\" of this script jumps into the middle of a command or onto data, so it can't be copied: point it at a step first.");
        return steps.Count == 0 ? new byte[] { 0 } : Build(steps, first);
    }

    /// <summary>Every (kind, index) with an edited script.</summary>
    public static int EditedCount(RomBuffer rom) => Block(rom) is var b && b >= 0 ? Entries(rom, b).Count() : 0;

    // ── text ─────────────────────────────────────────────────────────────────

    /// <summary>Names used to show arguments (spells, monsters, items, capsule attacks).</summary>
    public sealed class NameLists
    {
        public string[] Spells { get; init; } = Array.Empty<string>();
        public string[] Monsters { get; init; } = Array.Empty<string>();
        public string[] Items { get; init; } = Array.Empty<string>();
        public string[] CapsuleAttacks { get; init; } = Array.Empty<string>();
        /// <summary>What each shared script does (<see cref="LibraryName"/>).</summary>
        public string[] Library { get; set; } = Array.Empty<string>();
        /// <summary>The monster attack names shown by 1E (index 0 = none).</summary>
        public string[] AttackNames { get; init; } = Array.Empty<string>();
        /// <summary>Battle animations 00-FF named by what uses them (<see cref="L2Magic.Animations"/>).</summary>
        public string[] Animations { get; set; } = Array.Empty<string>();

        public static NameLists From(RomBuffer rom)
        {
            var n = new NameLists
            {
                Spells = L2Battle.Spells(rom).Select(s => s.Name).ToArray(),
                Monsters = L2Battle.Monsters(rom).Select(m => m.Unused ? "(unused slot)" : m.Name).ToArray(),
                Items = L2Battle.Items(rom).Select(i => i.Name).ToArray(),
                CapsuleAttacks = L2Capsules.AttackList(rom),
                AttackNames = MonsterAttackNames(rom),
            };
            n.Library = Enumerable.Range(0, LibraryCount).Select(i => LibraryName(rom, i, n)).ToArray();
            var anims = L2Magic.Animations(rom).ToDictionary(a => a.Value, a => a.Name);
            n.Animations = Enumerable.Range(0, 0x100).Select(k => anims.TryGetValue(k, out var a) ? a : k == 0xFF ? "none" : "(no animation here)").ToArray();
            return n;
        }
    }

    private static string Pick(string[] names, int i) => i >= 0 && i < names.Length ? $"{i:X2} {names[i]}" : $"{i:X2}";

    /// <summary>An argument as text; <paramref name="step"/> numbers a jump's target step (1-based), or -1.</summary>
    public static string ArgText(Arg a, int v, NameLists n, int step = -1) => a switch
    {
        Arg.Jump => step > 0 ? $"step {step}" : $"offset {v:X4}",
        Arg.Chance => $"{v}/256 ({v * 100 / 256}%)",
        Arg.Percent => $"{v}%",
        Arg.Register => RegisterName(v),
        Arg.Value => ValueText(v),
        Arg.Spell => Pick(n.Spells, v),
        Arg.Monster => Pick(n.Monsters, v),
        Arg.Item => Pick(n.Items, v & 0x1FF),
        Arg.CapsuleAttack => Pick(n.CapsuleAttacks, v),
        Arg.Element => ElementText(v),
        Arg.Stat => v < Stats.Length ? Stats[v] : $"stat {v:X2}",
        Arg.Status => v < Statuses.Length ? Statuses[v] : $"status {v:X2}",
        Arg.Target => v < Targets.Length ? Targets[v] : $"{v:X2}",
        Arg.Restore => v < Restores.Length ? Restores[v] : $"{v:X2}",
        Arg.Library => v < n.Library.Length ? $"shared script {v:X2} ({n.Library[v]})" : $"shared script {v:X2}",
        Arg.AttackName => v > 0 && v < n.AttackNames.Length ? $"\"{n.AttackNames[v]}\"" : v == 0 ? "(none)" : $"attack name {v:X2}",
        Arg.Change => $"{(sbyte)v:+0;-0;0}%",
        Arg.Animation => v < n.Animations.Length ? $"animation {v:X2} ({n.Animations[v]})" : $"animation {v:X2}",
        Arg.Sound => $"sound {v:X2}",
        Arg.Word => $"{v}",
        _ => $"{v}",
    };

    /// <summary>The value of argument <paramref name="i"/> of a step.</summary>
    public static int ArgValue(Step s, int i)
    {
        var d = s.Def!;
        int at = ArgAt(d, i);
        return Size(d.Args[i]) == 2 ? s.Args[at] | s.Args[at + 1] << 8 : s.Args[at];
    }

    public static void SetArg(Step s, int i, int v)
    {
        var d = s.Def!;
        int at = ArgAt(d, i);
        s.Args[at] = (byte)v;
        if (Size(d.Args[i]) == 2) s.Args[at + 1] = (byte)(v >> 8);
    }

    /// <summary>One line for a step, e.g. "Cast a spell: 03 Fire", "If the damage is more than 100, go to step 7".</summary>
    public static string Describe(Step s, IReadOnlyList<Step> all, NameLists n)
    {
        var d = s.Def;
        if (d == null) return $"command {s.Op:X2}";
        if (Sentence(s, all, n) is { } said) return said;
        var parts = new List<string>();
        int j = 0;
        for (int i = 0; i < d.Args.Length; i++)
        {
            int step = -1;
            if (d.Args[i] == Arg.Jump) { var t = j < s.Targets.Length ? s.Targets[j] : null; step = t == null ? -1 : IndexOf(all, t) + 1; j++; }
            parts.Add(ArgText(d.Args[i], ArgValue(s, i), n, step));
        }
        return parts.Count == 0 ? d.Name : $"{d.Name}: {string.Join(", ", parts)}";
    }

    /// <summary>Number and effect commands as a sentence ("Set the damage to 0", "Add 5 to its counter 1"); null for the
    /// others, which read fine as "name: values".</summary>
    private static string? Sentence(Step s, IReadOnlyList<Step> all, NameLists n)
    {
        string A(int i) => ArgText(s.Def!.Args[i], ArgValue(s, i), n);
        string Go() { var t = s.Targets.Length > 0 ? s.Targets[0] : null; int k = t == null ? -1 : IndexOf(all, t); return k < 0 ? "(no step)" : $"step {k + 1}"; }
        string Cap(string x) => x.Length == 0 ? x : char.ToUpperInvariant(x[0]) + x[1..];
        string Pct(int v) => $"{v * 100 / 256}%";
        return s.Op switch
        {
            0x32 => $"Target: {A(0)}",
            0x28 => "Attack",
            0x2C => $"Cast {A(0)}",
            0x54 => $"Cast {A(0)} for free",
            0x2B => $"Use the item {A(0)}",
            0x2D => $"Call for help: {A(0)}",
            0x1E => $"Show the name {A(0)}",
            0x5A => $"Play {A(0)}",
            0x3D => $"Play sound effect {ArgValue(s, 0):X2}",
            0x05 => $"With a {Pct(ArgValue(s, 0))} chance, go to {Go()}",
            0x04 => $"If that didn't work, go to {Go()}",
            0x03 => $"Go to {Go()}",
            0x3F => $"If it has learned capsule attack {ArgValue(s, 0)}, go to {Go()}",
            0x56 => $"Keep the message up for {ValueText(ArgValue(s, 0))} frames",
            0x25 => $"{((sbyte)ArgValue(s, 1) < 0 ? "Lower" : "Raise")} the target's {A(0)} by {Math.Abs((sbyte)ArgValue(s, 1))}%",
            0x0C when ArgValue(s, 0) == 0x23 && ArgValue(s, 1) == 5 => "Make it a special attack (the effects above happen)",
            0x0C when ArgValue(s, 0) == 0x23 && ArgValue(s, 1) == 0x0A => "Make it a magic attack (the effects above happen)",
            0x06 => $"If {A(0)} is {A(1)}, go to {Go()}",
            0x07 => $"If {A(0)} is not {A(1)}, go to {Go()}",
            0x08 => $"If {A(0)} is more than {A(1)}, go to {Go()}",
            0x09 => $"If {A(0)} is less than {A(1)}, go to {Go()}",
            0x0A => $"If {A(0)} is at least {A(1)}, go to {Go()}",
            0x0B => $"If {A(0)} is at most {A(1)}, go to {Go()}",
            0x0C => $"Set {A(0)} to {A(1)}",
            0x0D => $"Add {A(1)} to {A(0)}",
            0x0E => $"Take {A(1)} from {A(0)}",
            0x0F => $"Multiply {A(0)} by {A(1)}",
            0x10 or 0x55 => $"Divide {A(0)} by {A(1)}",
            0x16 => $"{Cap(A(0))}: keep only the bits {ArgValue(s, 1):X4}",
            0x17 => $"{Cap(A(0))}: add the bits {ArgValue(s, 1):X4}",
            0x18 => $"{Cap(A(0))}: flip the bits {ArgValue(s, 1):X4}",
            0x11 => $"{Cap(A(0))} becomes a random number below it",
            0x19 => $"Make {A(0)} positive",
            0x1A => $"Turn {A(0)} negative (or back)",
            0x13 => $"{Cap(A(0))} = the target's {A(1)}",
            0x15 => $"{Cap(A(0))} = its own {A(1)}",
            0x4E => $"{Cap(A(0))} = the attacker's {A(1)}",
            0x14 => $"Set the target's {A(1)} to {A(0)}",
            0x21 => $"Physical damage ({A(0)}): {DamageText(ArgValue(s, 1))}, spread {ArgValue(s, 2)}",
            0x22 => $"Magic damage ({A(0)}): {DamageText(ArgValue(s, 1))}, spread {ArgValue(s, 2)}",
            0x24 => $"Restore {A(0)} by {A(1)} (give or take {ArgValue(s, 2)})",
            0x26 => $"Cure {A(0)} ({A(1)} chance)",
            0x27 => $"Cause {A(0)} ({A(1)} chance)",
            0x1F => $"The attack's element: {A(0)}",
            0x20 => $"Critical hits: {A(0)} chance, damage x{ArgValue(s, 1)}/16",
            _ => null,
        };
    }

    /// <summary>A damage amount: the power as a number (Flash 65), or a number worked out above.</summary>
    private static string DamageText(int v) => ValueText(v);

    private static int IndexOf(IReadOnlyList<Step> all, Step t) { for (int i = 0; i < all.Count; i++) if (ReferenceEquals(all[i], t)) return i; return -1; }

    /// <summary>The whole script as numbered lines.</summary>
    public static string Listing(IReadOnlyList<Step> steps, NameLists n) =>
        steps.Count == 0 ? "(none)" : string.Join(Environment.NewLine, steps.Select((s, i) => $"{i + 1,3}  {Describe(s, steps, n)}"));
}
