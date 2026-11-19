using System.IO;

namespace LufiaForge.Core.Lufia2;

// lufia 2 monster and capsule monster battle scripts we can edit (AI = what it does on its turn, defense = what
// happens when it gets hit). found 2026-10-24 reading the game's code:
// - a monster's turn ($81:B1C9): offset = combatant +$4A (tag 07 of the record), base = the record (from the
//   monster id at +$50, $81:FCE2), bank $96. offset 0 runs the built-in plain attack $85:EF20
// - when hit ($85:D153 and $85:D194): offset +$4C (tag 08), base and bank = the record pointer at +$45/+$47
// - a capsule monster's turn ($81:B1A3): offset $1129 (record +$25), base and bank $1124/$1126
// - the script runner $81:FAC9 starts at base + offset (offset 0 = none), jumps count from the base
// scripts had to sit right behind their record and banks $96 and $97 have no room left, so edited scripts go in
// the added space instead. four small hooks look up (record, kind) in a table there and run our script if there
// is one, everything else runs like before. we never change the game's own bytes, so scripts shared between
// monsters stay how they were, a monster without a script can get one, and "back to the game's script" just drops
// the table entry. a script in the table is stored as [capacity u16][00][script], its jumps count from the 00
// (the runner gets offset 1, since offset 0 would mean "no script")
public static class L2AiScripts
{
    public enum Kind
    {
        MonsterAttack = 0, MonsterDefense = 1, CapsuleAi = 2, CapsuleDefense = 3,
        // effects (item property words 0-3, a spell's word at +$11, an IP attack's effect script)
        ItemMenu = 4, ItemBattle = 5, Weapon = 6, Armor = 7, Spell = 8, IpAttack = 9,
    }

    public static bool IsItem(Kind kind) => kind is Kind.ItemMenu or Kind.ItemBattle or Kind.Weapon or Kind.Armor;

    // item, spell and IP effects (the hooks at the script runner and the item loader, block version 2)
    public static bool IsEffect(Kind kind) => kind >= Kind.ItemMenu;

    public static string KindName(Kind kind) => kind switch
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

    // commands

    // what a command's argument is (for the editor's fields, the size is in Size)
    public enum Arg { Byte, Word, Register, Value, Jump, Chance, Percent, Spell, Monster, CapsuleAttack, Element, Stat, Status, Target, Item, Library, AttackName, Animation, Sound, Restore, Change, Message, NewStatus }

    public static int Size(Arg arg) => arg is Arg.Word or Arg.Value or Arg.Jump or Arg.Element or Arg.Item or Arg.Library ? 2 : 1;

    public sealed record OpDef(byte Op, string Name, string Group, Arg[] Args, string Help = "")
    {
        public int Length => 1 + Args.Sum(Size);
        public bool IsJump => Args.Contains(Arg.Jump);
        // the script stops here (00 end, 4F quit, 03 go to, 43 return)
        public bool EndsPath => Op is 0x00 or 0x4F or 0x03 or 0x43;
    }

    public static readonly OpDef[] Ops = BuildOps();
    private static readonly Dictionary<byte, OpDef> ByOp = Ops.ToDictionary(o => o.Op);

    public static OpDef? Def(byte op) => ByOp.GetValueOrDefault(op);

    private static OpDef[] BuildOps()
    {
        var ops = new List<OpDef>();
        void O(int op, string group, string name, string help, params Arg[] args) => ops.Add(new OpDef((byte)op, name, group, args, help));
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
        O(0x35, Show, "Show a result message", "The message and effect shown after the action, as the game's spells and items use them (HP restored, poisoned, MP absorbed...): each is named after what uses it.", Arg.Message);
        O(0x36, Show, "If that didn't work: show a result message", "", Arg.Message);
        O(0x4A, Show, "Wait", "Waits this many frames (60 a second); the battle screen keeps moving.", Arg.Byte);
        O(0x4C, Show, "Screen effect", "One of the battle's screen effects on the targets (the RAN-RAN step weapon uses 3).", Arg.Byte);
        O(0x4B, Show, "Screen effect 4", "");
        O(0x21, Eff, "Physical damage", "A special attack's damage: its element and its power (a number, or one worked out above). The target's DFP blocks part of it.", Arg.Element, Arg.Value, Arg.Byte);
        O(0x22, Eff, "Magic damage", "Like physical damage, but magic (the spells' kind): the attacker's INT is added to the power, the target's MGR blocks part of it, and hitting several targets shares it out.", Arg.Element, Arg.Value, Arg.Byte);
        O(0x23, Eff, "Damage of this power", "For weapons: the attack's power becomes this (the Bunny sword's is 120), the target's DFP still blocks part of it. In a monster's own attack it doesn't set the kind of action (a bug in the game), so it hits unpredictably: use Physical damage there.", Arg.Word);
        O(0x27, Eff, "Cause a status", "Poison, sleep, confusion... with a chance in percent.", Arg.Status, Arg.Percent);
        O(0x26, Eff, "Cure a status", "", Arg.Status, Arg.Percent);
        O(0x24, Eff, "Restore", "Heals HP or MP (or a stat) by an amount, as the potions and healing spells do.", Arg.Restore, Arg.Value, Arg.Byte);
        O(0x25, Eff, "Raise or lower a stat", "Changes the target's stat for the battle by a percentage (minus lowers it).", Arg.Restore, Arg.Change);
        O(0x1F, Eff, "Set the attack's element", "", Arg.Element);
        // our new statuses (these commands need L2StatusPatch in the rom)
        O(0x5D, Eff, "Cause a new status", "Zombie, Frozen, Burnt, Berserk... with a chance in percent (Lufia Forge's new statuses).", Arg.NewStatus, Arg.Percent);
        O(0x5E, Eff, "Cure a new status", "'Every new status' cures all 16 at once.", Arg.NewStatus, Arg.Percent);
        O(0x5F, Eff, "Resist a new status", "In a 'when hit' or armor script: this target's chance of getting it drops by this percent (100 = immune). 'Every new status' at 100 makes it immune to all 16, as shared script 11 does for the game's own.", Arg.NewStatus, Arg.Percent);
        O(0x61, Eff, "Give focus", "The hero targets get focus for the rest of the battle (as the Tag candy): each weapon attack in a row on the same foe does more (Lufia Forge's focus percents, +10% to +100% to start).");
        O(0x60, Eff, "Ward against a new status", "The targets can't get it for the rest of the battle (a protecting spell). 'Every new status' wards all 16.", Arg.NewStatus);
        O(0x20, Eff, "Critical hits", "The chance of a critical hit and how much it does. A critical hit also goes through the target's DFP.", Arg.Percent, Arg.Byte);
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
        return ops.ToArray();
    }

    // target modes of 32 ($85:BBB6, read from the code): 01 random member of the other side, 05 everyone on the
    // target's side, 06 itself, 07 random member of its own side, 08 random hero. 02-04 the game doesn't use
    public static readonly string[] Targets =
    {
        "keep the target it has", "a random foe", "a foe picked by a rule (02, not used by the game)",
        "a foe picked by a rule (03, not used by the game)", "04 (not used by the game)",
        "everyone on that side", "itself", "a random one of its own side", "a random hero",
    };
    public static readonly string[] Stats = { "HP", "MP", "max HP", "max MP", "ATP", "DFP", "STR", "AGL", "INT", "GUT", "MGR", "IP", "level",
        "ATP+ (battle)", "DFP+ (battle)", "STR+ (battle)", "AGL+ (battle)", "INT+ (battle)", "GUT+ (battle)", "MGR+ (battle)", "status" };
    public static readonly string[] Statuses = { "poison", "silence", "death", "paralysis", "confusion", "sleep", "bounce", "mirror" };
    public static readonly string[] Restores = { "HP", "MP", "max HP", "max MP", "ATP", "DFP", "STR", "AGL", "INT", "GUT", "MGR" };
    public static readonly (int Bit, string Name)[] Elements =
    {
        // read from how the game uses them (2026-10-25): fire swords and Fireball 0002, Aqua sword and Droplet 0004,
        // Silver sword and Light attack 0010, Deadly sword and Gades blade 0020, maces and rods 0040, boomerangs 0080,
        // Dragon tooth and the dragon IP attacks 0100, Insect crush 0200, Cold rapier and Blizzard 0400, Thunder ax and
        // Bolt 0800, Groundshock 1000. no script uses 0001, 0008 or 2000-8000 (our monster types in L2MonsterTypes
        // use 0001, 0008, 2000 and 4000)
        (0x0002, "fire"), (0x0004, "water"), (0x0400, "ice"), (0x0800, "thunder"), (0x1000, "earth"), (0x0010, "light"),
        (0x0020, "dark"), (0x0040, "blunt (maces, rods)"), (0x0080, "thrown (boomerangs)"), (0x0100, "vs dragons"),
        (0x0200, "vs insects"), (0x0001, "vs beasts"), (0x0008, "vs aquatic"), (0x2000, "vs plants"),
        (0x4000, "vs humanoids"), (0x8000, "unused 8000"),
    };

    public static string ElementText(int elementBits) =>
        elementBits == 0 ? "neutral" : string.Join(" + ", Elements.Where(e => (elementBits & e.Bit) != 0).Select(e => e.Name).DefaultIfEmpty($"{elementBits:X4}"));

    // result messages of 35 ($0A62, shown after the action), named after the spells and items that use each one
    // (read from all of the game's scripts, 2026-11-11)
    public static readonly Dictionary<int, string> Messages = new()
    {
        [0x00] = "none", [0x01] = "travel (as Warp)", [0x02] = "escape (as Escape)", [0x04] = "HP restored (as Potion, Strong)",
        [0x06] = "MP restored (as Magic jar)", [0x08] = "HP and MP restored (as Miracle)", [0x09] = "max HP up (as Life potion)",
        [0x0A] = "max MP up (as Spell potion)", [0x0B] = "ATP changed (as Trick, the ciders)", [0x0C] = "DFP changed (as Dread, Bravery)",
        [0x0D] = "STR up (as Power potion)", [0x0E] = "AGL changed (as Fake, Speed potion)", [0x0F] = "INT up (as Mind potion)",
        [0x10] = "GUT up (as Brave)", [0x11] = "MGR changed (as Shield)", [0x12] = "brought back to life (as Rally, Regain)",
        [0x13] = "poison cured (as Antidote)", [0x14] = "woken up (as Waken)", [0x15] = "confusion (as Release, Confuse ball)",
        [0x17] = "instant death (as Perish, Destroy)", [0x18] = "as the Poison spell", [0x19] = "sleep (as Drowsy, Coma, Sleep ball)",
        [0x1A] = "paralysis (as Freeze ball)", [0x1B] = "confusion (as Confuse)", [0x1C] = "silence (as Deflect)", [0x1E] = "mirror (as Mirror)",
        [0x21] = "MP absorbed (as Absorb, Vampire)", [0x22] = "run from the battle (as Smoke ball)", [0x24] = "as Reset",
        [0x25] = "curses lifted (as Curselifter)", [0x26] = "as Jewel sonar",
    };

    public static string MessageText(int message) => Messages.TryGetValue(message, out var text) ? text : "not used by the game";

    // the action's flags (register 26, read from the game's code): physical damage sets 20, magic 1DC, Absorb 1DE.
    // bit 0 gets set when the action bounced back
    public static readonly (int Bit, string Name)[] ActionFlags =
    {
        (0x0002, "drain: the attacker gets what the targets lose"), (0x0008, "MGR blocks it (not DFP)"), (0x0010, "a mirror bounces it back"),
        (0x0020, "sent back by Bounce"), (0x0040, "back rows don't cut it"), (0x0080, "defending doesn't cut it"),
        (0x0100, "shared out among several targets"), (0x0004, "flag 0004 (magic has it)"), (0x0001, "it bounced back"),
    };

    public static string FlagsText(int flags)
    {
        var setNames = ActionFlags.Where(f => (flags & f.Bit) != 0).Select(f => f.Name).ToList();
        int otherBits = flags & ~ActionFlags.Sum(f => f.Bit);
        if (otherBits != 0) setNames.Add($"other bits {otherBits:X4}");
        return setNames.Count == 0 ? "none" : string.Join(", ", setNames);
    }

    // what a command's number means, for the editor: a label and a line explaining it (null = the plain label of its
    // kind). from the game's code (2026-11-11): damage = (power x (256 + random 0 to spread-1) / 256 - defence / 2) / 2,
    // then cut for back rows and defending. the spread is register 2B, 32 unless something sets it after
    public static (string Label, string Help)? ArgInfo(byte op, int argIndex) => (op, argIndex) switch
    {
        (0x21, 1) => ("power", "How hard it hits. The damage is about (power - the target's DFP / 2) / 2, plus up to 12% at random; a target in the back row, or defending, takes less. Gades's Destructo-wave is 350."),
        (0x22, 1) => ("power", "How hard it hits; the attacker's INT is added. The damage is about (power + INT - the target's MGR / 2) / 2, plus up to 12% at random; against several targets each takes a share (2: 57%, 3: 50%, 4: 45%)."),
        (0x21 or 0x22, 2) => ("(unused)", "The game reads this number and ignores it. The damage always gets up to 12% more at random: to change that, put \"Set a number: the damage spread\" after this step (0 = none, 32 = the game's 12%)."),
        (0x23, 0) => ("power", "As the power of physical damage: about (power - the target's DFP / 2) / 2."),
        (0x24, 1) => ("amount", "How much it heals (the Potion's is 30); healing stops at the maximum. The game's scripts never give it a minus amount (in a monster's attack a minus MP amount took nothing, seen in the game)."),
        (0x24, 2) => ("spread", "HP and MP: up to this many 256ths more at random (the Potion's 11 is about 4%). Other stats don't use it."),
        (0x25, 1) => ("change (%)", "A percentage of the stat (ATP, DFP, STR, AGL, INT, GUT, MGR); minus lowers it, for the battle. On HP it doesn't take a share of the HP (seen in the game: -50% took 1 HP or none): use Restore with a minus amount."),
        (0x26 or 0x27, 1) => ("chance (%)", "Out of 100. The target's own resistance (its shared script) can stop it."),
        (0x20, 0) => ("chance (%)", "Out of 100, each time it hits."),
        (0x20, 1) => ("damage (x/16)", "16 = normal damage, 24 = 1.5 times, 32 = double. A critical hit also ignores the target's DFP."),
        (0x56, 0) => ("frames", "How long the name stays up (60 frames a second)."),
        (0x4A, 0) => ("frames", "60 frames a second."),
        (0x57, 1) => ("boost (x/64)", "With that item worn, the damage is multiplied by this / 64 (96 = 1.5 times)."),
        (0x47, 0) => ("frames", ""),
        _ => null,
    };

    // a value operand: 80 in the high byte = register (low byte), else a number
    // a bit mask, or the register it comes from (8027 = the attack's element)
    private static string Bits(int value) => (value >> 8) == 0x80 ? RegisterName(value & 0xFF) : value.ToString("X4");

    public static string ValueText(int value) => (value >> 8) == 0x80 ? RegisterName(value & 0xFF) : $"{(short)value}";

    // what a register holds, in words. 00-7F are shared by the whole battle ($7F:F40E), 80-FF belong to the fighter
    // running the script (its counters). named from the battle code's handlers and the game's scripts (shared
    // script 11, "immune to the 5 statuses", zeroes the chances 4E 50 54 56 58 but leaves 52, death)
    public static string RegisterName(int register) => register switch
    {
        0x10 => "the last action failed (0 = no)",
        0x20 => "the attacker",
        0x21 => "the targets",
        0x11 => "a working number (11)",
        0x22 => "the damage cut (rows, defending; of 256)",
        0x23 => "the action's kind",
        0x26 => "the action's flags (drain, defence...)",
        0x2C => "the HP change in %",
        0x2D => "the MP change",
        0x2E => "the MP spread",
        0x2F => "the MP change in %",
        0x24 => "the action's number",
        0x25 => "the attack's name",
        0x27 => "the attack's element",
        0x28 => "the critical chance",
        0x29 => "the critical multiplier",
        0x2A => "the damage",
        0x2B => "the damage spread (random, of 256)",
        0x4E => "the poison chance",
        0x50 => "the silence chance",
        0x52 => "the death chance",
        0x54 => "the paralysis chance",
        0x56 => "the confusion chance",
        0x58 => "the sleep chance",
        >= 0x80 and <= 0x87 => $"its counter {register - 0x7F}",
        >= 0x88 => $"its value {register:X2}",
        _ => $"shared value {register:X2}",
    };

    public static readonly int[] KnownRegisters = { 0x2A, 0x27, 0x28, 0x29, 0x2B, 0x26, 0x2D, 0x4E, 0x50, 0x52, 0x54, 0x56, 0x58, 0x10, 0x20, 0x21, 0x23, 0x24, 0x25,
        0x80, 0x81, 0x82, 0x83, 0x84, 0x85, 0x86, 0x87 };

    // steps

    // one command. jumps point at a step (Targets), not an offset, so steps can be added, removed and moved.
    // a jump that leaves the script keeps its offset in OutsideTargets
    public sealed class Step
    {
        public byte Op { get; set; }
        public byte[] Args { get; set; } = Array.Empty<byte>();
        // per jump argument (in order): the step it lands on, or null
        public Step?[] Targets { get; set; } = Array.Empty<Step?>();
        // per jump argument: the raw offset when the target isn't a step of this script
        public int[] OutsideTargets { get; set; } = Array.Empty<int>();
        public int Offset { get; set; }
        public OpDef? Def => L2AiScripts.Def(Op);
        public int Length => 1 + Args.Length;

        public Step Clone() => new() { Op = Op, Args = (byte[])Args.Clone(), Targets = (Step?[])Targets.Clone(), OutsideTargets = (int[])OutsideTargets.Clone() };
    }

    // byte position of argument index in a command's arguments
    public static int ArgAt(OpDef def, int index) => def.Args.Take(index).Sum(Size);

    // reads the script at file offset start, jumps count from file offset baseAt. stops at 00, 4F, 03 or 43 once no
    // jump goes further. a jump to code outside that run (some fan hacks share code between scripts) pulls that code
    // in too, as more steps after the script (with a "go to" where it runs into steps we already read), so the script
    // still works after it's moved. a jump that lands inside a command, or on bytes that aren't commands, stays in
    // Step.OutsideTargets
    public static List<Step> Parse(RomBuffer rom, int start, int baseAt)
    {
        var steps = Parse(rom, start, baseAt, straight: true);
        if (!steps.Any(s => s.OutsideTargets.Any(o => o >= 0))) return steps;
        // a jump into the middle of a command can mean we read data as commands, so try following the jumps
        try
        {
            var followed = Parse(rom, start, baseAt, straight: false);
            int Outside(List<Step> list) => list.Sum(s => s.OutsideTargets.Count(o => o >= 0));
            return Outside(followed) < Outside(steps) ? followed : steps;
        }
        catch (InvalidDataException) { return steps; }
    }

    private static List<Step> Parse(RomBuffer rom, int start, int baseAt, bool straight)
    {
        var steps = new List<Step>();
        var byOffset = new Dictionary<int, Step>();
        var jumps = new List<(Step S, int Arg, int To)>();
        var covered = new List<(int Start, int Length)>();
        // offsets count from the base the way the game adds them: a 16-bit sum in the base's bank (L2Rom.InBank), so
        // a script can sit before its record or wrap round the bank (the fan hacks' records in bank $D0 do both)
        int FileAt(int relative)
        {
            int fileOffset = L2Rom.InBank(baseAt, relative);
            return fileOffset >= 0 && fileOffset < rom.Length ? fileOffset : throw new InvalidDataException($"Offset {relative & 0xFFFF:X4} from 0x{baseAt:X6} isn't in the ROM.");
        }
        // one straight run of commands from offset position (stops at an end, or where it hits steps we already read)
        // straight = read on to the furthest jump (the game's layout), else stop at each end and follow the jumps
        List<Step> Run(int position, bool first)
        {
            var run = new List<Step>();
            int runStart = position, furthest = 0, done = 0;   // distances from runStart
            var runJumps = new List<(Step, int, int)>();
            for (int guard = 0; guard < 2000; guard++)
            {
                if (!first && byOffset.ContainsKey(position)) break;
                byte op = rom.ReadByte(FileAt(position));
                var def = Def(op) ?? throw new InvalidDataException($"Unknown command {op:X2} at 0x{FileAt(position):X6}.");
                var args = new byte[def.Length - 1];
                for (int k = 0; k < args.Length; k++) args[k] = rom.ReadByte(FileAt(position + 1 + k));
                var step = new Step { Op = op, Args = args, Offset = position };
                int jumpCount = def.Args.Count(a => a == Arg.Jump);
                step.Targets = new Step?[jumpCount];
                step.OutsideTargets = Enumerable.Repeat(-1, jumpCount).ToArray();
                int j = 0;
                for (int i = 0; i < def.Args.Length; i++)
                {
                    if (def.Args[i] != Arg.Jump) continue;
                    int argOffset = ArgAt(def, i), to = step.Args[argOffset] | step.Args[argOffset + 1] << 8;
                    runJumps.Add((step, j++, to));
                    int distance = (to - runStart) & 0xFFFF;
                    if (straight && distance > furthest && distance < done + 0x400) furthest = distance;
                }
                run.Add(step);
                position = (position + def.Length) & 0xFFFF;
                done += def.Length;
                if (def.EndsPath && done > furthest) break;
            }
            // nothing gets added unless the whole run reads as commands
            foreach (var s in run) byOffset[s.Offset] = s;
            jumps.AddRange(runJumps);
            covered.Add((runStart, done));
            // a run that ran into steps we already read carries on there
            if (!first && run.Count > 0 && run[^1].Def?.EndsPath != true && byOffset.TryGetValue(position, out var next))
            {
                var goTo = New(0x03);
                goTo.Targets[0] = next;
                goTo.Offset = -1;
                run.Add(goTo);
            }
            return run;
        }
        int startAt = L2Rom.OffsetInBank(baseAt, start);
        try { steps.AddRange(Run(startAt, true)); }
        catch (InvalidDataException) when (straight)
        {
            // some fan hacks jump ahead over other data, so follow the jumps instead of reading straight on
            return Parse(rom, start, baseAt, straight: false);
        }
        for (int k = 0; k < jumps.Count; k++)
        {
            int to = jumps[k].To;
            if (byOffset.ContainsKey(to) || covered.Any(c => ((to - c.Start) & 0xFFFF) < c.Length)) continue;
            try { steps.AddRange(Run(to, false)); } catch (InvalidDataException) { }
        }
        foreach (var (step, j, to) in jumps)
            if (byOffset.TryGetValue(to, out var target)) step.Targets[j] = target; else step.OutsideTargets[j] = to;
        return steps;
    }

    // the script's bytes, laid out from offset first (counted from the jump base)
    public static byte[] Build(IReadOnlyList<Step> steps, int first)
    {
        var stepOffsets = new Dictionary<Step, int>();
        int offset = first;
        foreach (var s in steps) { stepOffsets[s] = offset; offset += s.Length; }
        var bytes = new List<byte>();
        foreach (var step in steps)
        {
            var args = (byte[])step.Args.Clone();
            var def = step.Def;
            if (def != null)
            {
                int j = 0;
                for (int i = 0; i < def.Args.Length; i++)
                {
                    if (def.Args[i] != Arg.Jump) continue;
                    var target = j < step.Targets.Length ? step.Targets[j] : null;
                    if (target != null && !stepOffsets.ContainsKey(target)) throw new InvalidOperationException("A \"go to\" points at a step that was removed.");
                    int to = target != null ? stepOffsets[target] : (j < step.OutsideTargets.Length && step.OutsideTargets[j] >= 0 ? step.OutsideTargets[j]
                        : throw new InvalidOperationException("A \"go to\" has no step to go to."));
                    int argOffset = ArgAt(def, i);
                    args[argOffset] = (byte)to; args[argOffset + 1] = (byte)(to >> 8);
                    j++;
                }
            }
            bytes.Add(step.Op);
            bytes.AddRange(args);
        }
        return bytes.ToArray();
    }

    // a new command with zero arguments (jumps unset)
    public static Step New(byte op)
    {
        var def = Def(op) ?? throw new ArgumentException($"Unknown command {op:X2}.");
        int jumps = def.Args.Count(a => a == Arg.Jump);
        return new Step { Op = op, Args = new byte[def.Length - 1], Targets = new Step?[jumps], OutsideTargets = Enumerable.Repeat(-1, jumps).ToArray() };
    }

    // monster attack names ($85:F2D7)

    // the names 1E shows: 1E n (n from 1) reads record n - 1 of the table $85:F2D7 (u16 pointers in bank $85, read
    // by $85:9532): a byte (the default animation, E0 = none) then the name, ended by 00. the game's names go to 8A
    public const int AttackNameTable = 0x2F2D7, AttackNameCount = 0x8A;

    public static string[] MonsterAttackNames(RomBuffer rom)
    {
        var names = new string[AttackNameCount + 1];
        names[0] = "(none)";
        for (int name = 1; name <= AttackNameCount; name++)
        {
            int textOffset = 0x28000 + (rom.ReadUInt16Le(AttackNameTable + (name - 1) * 2) & 0x7FFF) + 1;
            var sb = new System.Text.StringBuilder();
            for (int i = 0; i < 37 && textOffset + i < rom.Length; i++) { byte c = rom.ReadByte(textOffset + i); if (c == 0) break; sb.Append(L2Charset.ToChar(rom, c) ?? '?'); }
            names[name] = sb.ToString();
        }
        // the names we added (8B and up, L2AttackNames)
        return names.Concat(L2AttackNames.Names(rom)).ToArray();
    }

    // the shared library ($96:FADD)

    // shared script table ($96:FADD): u16 offsets from the table, each script based at its own start
    public const int LibraryTable = 0xB7ADD, LibraryCount = 0x29;

    // where shared script n is: our version if there is one (L2SharedScripts)
    public static int LibraryAt(RomBuffer rom, int n) =>
        n < 0 || n >= LibraryCount ? -1 : L2SharedScripts.Override(rom, n) is var overrideAt && overrideAt >= 0 ? overrideAt : LibraryTable + rom.ReadUInt16Le(LibraryTable + n * 2);

    // shared script n as steps (its jumps count from its own start)
    public static List<Step> LoadLibrary(RomBuffer rom, int n)
    {
        int scriptAt = LibraryAt(rom, n);
        if (scriptAt < 0) throw new ArgumentOutOfRangeException(nameof(n), $"There is no shared script {n:X2}.");
        return Parse(rom, scriptAt, scriptAt);
    }

    // puts shared script n's own steps where step index calls it (42), so this script can change them without
    // changing every monster that shares them. its returns (43) become a "go to" the step after the call (the last
    // one just goes away). returns how many steps came in
    public static int InlineLibrary(RomBuffer rom, List<Step> steps, int index)
    {
        var call = steps[index];
        if (call.Op != 0x42) throw new ArgumentException("That step doesn't call a shared script.");
        var library = LoadLibrary(rom, ArgValue(call, 0));
        if (library.Any(s => s.OutsideTargets.Any(o => o >= 0))) throw new InvalidOperationException("That shared script jumps outside itself and can't be copied in.");
        if (library.Any(s => s.Op == 0x42)) throw new InvalidOperationException("That shared script calls another one (the game allows one level only).");
        var after = index + 1 < steps.Count ? steps[index + 1] : null;
        // a "go to" pointing at the call now points at the first copied step
        var copy = new List<Step>(library);
        if (copy.Count > 0 && copy[^1].Op == 0x43 && !library.Any(s => s.Targets.Contains(copy[^1]))) copy.RemoveAt(copy.Count - 1);
        for (int k = 0; k < copy.Count; k++)
        {
            if (copy[k].Op != 0x43) continue;
            if (after == null)
            {
                // nothing after the call: put an end there, and jumps that went to the return go to it (the shared
                // scripts jump to their 43, and left pointing at it the build failed with "a go to points at a removed step")
                var end = New(0x00);
                foreach (var s in copy) for (int t = 0; t < s.Targets.Length; t++) if (s.Targets[t] == copy[k]) s.Targets[t] = end;
                copy[k] = end;
                continue;
            }
            var goTo = New(0x03); goTo.Targets[0] = after;
            foreach (var s in copy) for (int t = 0; t < s.Targets.Length; t++) if (s.Targets[t] == copy[k]) s.Targets[t] = goTo;
            copy[k] = goTo;
        }
        if (copy.Count == 0) copy.Add(after != null ? GoTo(after) : New(0x00));
        foreach (var s in steps) for (int t = 0; t < s.Targets.Length; t++) if (s.Targets[t] == call) s.Targets[t] = copy[0];
        steps.RemoveAt(index);
        steps.InsertRange(index, copy);
        return copy.Count;

        static Step GoTo(Step to) { var goToStep = New(0x03); goToStep.Targets[0] = to; return goToStep; }
    }

    // what the game's shared scripts do, read from their steps (2026-10-25). they run when the fighter gets hit
    // (most of them) or from an attack's effect. "the attack's element" is register 27, "the damage" 2A
    private static readonly Dictionary<int, string> LibraryNames = new()
    {
        [0x00] = "takes double damage from fire", [0x01] = "takes double damage from thunder", [0x02] = "takes double damage from water",
        [0x03] = "takes double damage from ice", [0x04] = "double damage from thrown weapons, none from earth",
        [0x05] = "takes double damage from light", [0x06] = "takes half damage from fire", [0x07] = "takes half damage from thunder",
        [0x08] = "takes half damage from water", [0x09] = "takes half damage from ice", [0x0A] = "takes half damage from light",
        [0x0B] = "takes half damage from plain (no element) attacks", [0x0C] = "takes double damage from dragon-slaying attacks",
        [0x0D] = "takes double damage from dark", [0x0E] = "takes half damage from dark", [0x0F] = "takes double damage from blunt weapons",
        [0x10] = "takes double damage from insect-slaying attacks", [0x11] = "immune to poison, silence, paralysis, confusion and sleep",
        [0x12] = "healing hurts it instead, and instant death can't work on it", [0x14] = "takes half damage from every element",
        [0x17] = "takes half damage from blunt weapons", [0x21] = "works out 1.5 x its own ATP (counter 1)",
        [0x23] = "works out its ATP with its battle bonus (counter 1)",
    };

    // a shared script in words: the known ones by name, the others as their first steps
    public static string LibraryName(RomBuffer rom, int n, NameLists names) =>
        LibraryNames.TryGetValue(n, out var known) ? known : LibrarySummary(rom, n, names);

    // what a shared script does in a few words, from its steps
    public static string LibrarySummary(RomBuffer rom, int n, NameLists names)
    {
        try
        {
            var library = LoadLibrary(rom, n);
            var lines = library.Where(s => s.Op != 0x43).Take(4).Select(s => Describe(s, library, names)).ToList();
            return lines.Count == 0 ? "(empty)" : string.Join("; ", lines) + (library.Count(s => s.Op != 0x43) > 4 ? "..." : "");
        }
        catch (Exception ex) { return "(can't be read: " + ex.Message + ")"; }
    }

    // owners

    // file offset of the record that owns the script
    public static int RecordAt(RomBuffer rom, Kind kind, int index) => kind switch
    {
        Kind.MonsterAttack or Kind.MonsterDefense => L2Battle.ReadMonster(rom, index).Offset,
        Kind.CapsuleAi or Kind.CapsuleDefense => L2Capsules.At(rom, index),
        Kind.Spell => L2Battle.ReadSpell(rom, index).Offset,
        Kind.IpAttack => IpEffectBase(rom, IpEffect(rom, index)),
        _ => L2Battle.ItemOffset(rom, index),
    };

    // the item property word (bit) that holds the script of an item kind
    public static int ItemBit(Kind kind) => 1 << (kind - Kind.ItemMenu);

    // an IP attack's effect number (first word of its record, several attacks can share one)
    public static int IpEffect(RomBuffer rom, int ip) => rom.ReadUInt16Le(L2Battle.ReadIp(rom, ip).Offset);

    // IP effect scripts: table $95:F1EF of u16 offsets from the table ($81:F46B: LDA $95F1EF,X / CLC / ADC #$F1EF,
    // file 0xF46D), bank $95 (LDA #$95 at $81:AB59). the script runs with base = table + offset and offset 1.
    // we move them to add effects (L2Magic), so the table, the sum and the bank get read from the code
    public const int IpEffectTable = 0xAF1EF;
    private const int IpEffectTableRef = 0xF46D, IpEffectBankRef = 0xAB5A;

    public static int IpEffectBase(RomBuffer rom, int effect)
    {
        int table = L2Rom.SnesToFile((int)rom.ReadUInt24Le(IpEffectTableRef + 1));
        int adc = rom.ReadUInt16Le(IpEffectTableRef + 6), bank = rom.ReadByte(IpEffectBankRef);
        return L2Rom.SnesToFile(bank << 16 | (rom.ReadUInt16Le(table + effect * 2) + adc) & 0xFFFF);
    }

    // the IP attacks that use an effect
    public static List<int> IpAttacksWithEffect(RomBuffer rom, int effect) =>
        Enumerable.Range(0, L2Battle.IpCount(rom)).Where(i => IpEffect(rom, i) == effect).ToList();

    // file offset where the game's own script starts, -1 if it has none
    public static int OriginalAt(RomBuffer rom, Kind kind, int index)
    {
        switch (kind)
        {
            case Kind.MonsterAttack: return L2Battle.ReadMonster(rom, index).AttackScript;
            case Kind.MonsterDefense: return L2Battle.ReadMonster(rom, index).DefenseScript;
            case Kind.Spell:
                var spell = L2Battle.ReadSpell(rom, index);
                int scriptWord = rom.ReadUInt16Le(spell.Offset + 0x11);
                return scriptWord == 0 ? -1 : L2Rom.InBank(spell.Offset, scriptWord);
            case Kind.IpAttack: return L2Rom.InBank(RecordAt(rom, kind, index), 1);
            case Kind.ItemMenu or Kind.ItemBattle or Kind.Weapon or Kind.Armor:
                var item = L2Battle.ReadItem(rom, index);
                return item.Values.TryGetValue(ItemBit(kind), out var scriptOffset) && scriptOffset != 0 ? L2Rom.InBank(item.Offset, scriptOffset) : -1;
            default:
                var capsule = L2Capsules.Read(rom, index);
                int offset = kind == Kind.CapsuleAi ? capsule.AiScript : capsule.DefenseScript;
                return offset == 0 ? -1 : L2Rom.InBank(capsule.At, offset);
        }
    }

    // file offsets of every byte the game's own monster scripts (turn and when hit) get read from. the fan hacks
    // keep scripts in the unused end of a record's name field (and jump between records), so a name or field edit
    // has to stay clear of these
    public static HashSet<int> MonsterScriptBytes(RomBuffer rom)
    {
        var used = new HashSet<int>();
        for (int i = 0, count = L2Battle.MonsterCountIn(rom); i < count; i++)
        {
            var monster = L2Battle.ReadMonster(rom, i);
            foreach (int scriptAt in new[] { monster.AttackScript, monster.DefenseScript })
            {
                if (scriptAt < 0) continue;
                List<Step> steps;
                try { steps = Parse(rom, scriptAt, monster.Offset); } catch (InvalidDataException) { continue; }
                foreach (var s in steps)
                    if (s.Offset >= 0)
                        for (int k = 0; k < s.Length; k++) used.Add(L2Rom.InBank(monster.Offset, s.Offset + k));
            }
        }
        return used;
    }

    // the script as it runs now: ours (from the table) or the game's, empty if there's none
    public static (List<Step> Steps, bool Edited) Load(RomBuffer rom, Kind kind, int index)
    {
        if (Override(rom, kind, index) is int editedBase) return (Parse(rom, editedBase + 1, editedBase), true);
        int scriptAt = OriginalAt(rom, kind, index);
        return scriptAt < 0 ? (new List<Step>(), false) : (Parse(rom, scriptAt, RecordAt(rom, kind, index)), false);
    }

    // the game's own script (empty if there's none)
    public static List<Step> LoadOriginal(RomBuffer rom, Kind kind, int index)
    {
        int scriptAt = OriginalAt(rom, kind, index);
        return scriptAt < 0 ? new List<Step>() : Parse(rom, scriptAt, RecordAt(rom, kind, index));
    }

    // the hooks and the table

    private const string Magic = "LF2AI";
    private const int CodeAt = 0x10, TableAt = 0x200, BlockSize = 0x1400, EntrySize = 6;
    private const int TableCapacity = (BlockSize - TableAt) / EntrySize - 1;

    private const int AttackSite = 0xB1D4, CapsuleSite = 0xB1A3, DefenseSite1 = 0x2D165, DefenseSite2 = 0x2D1A6;
    // version 2: the script runner $81:FAC9 and the item loader $81:F1C5
    private const int RunnerSite = 0xFAC9, ItemLoaderSite = 0xF1C5;
    private static readonly byte[] RunnerOriginal = Hex("08 8b c2 30 48 da 5a e2 20 4b ab c0 00 00");
    private static readonly byte[] ItemLoaderOriginal = Hex("08 8b e2 20 c2 10 a2 ad 0b");
    // version 3 (2026-11-01): the runner hook E tells item markers from offsets that wrap round the bank (see Assemble)
    private const int CurrentVersion = 3;
    private static readonly byte[] AttackOriginal = Hex("bc 4a 00 f0 0e bd 50 00 22 e2 fc 81 8e 42 0a a9 96 80 08 8c 42 0a a0 20 ef a9 85 8d 44 0a 22 c9 fa 81 6b");
    private static readonly byte[] CapsuleOriginal = Hex("ac 29 11 f0 0e ae 24 11 8e 42 0a ad 26 11 8d 44 0a 80 0e a2 00 00 a0 20 ef 8e 42 0a a9 85 8d 44 0a 22 c9 fa 81 6b");
    private static readonly byte[] DefenseOriginal = Hex("bd 47 00 8d 44 0a bc 4c 00 22 c9 fa 81");
    // where the monster records' bank sits in the attack site's code (LDA #$96). the hacks that moved the monster
    // data (Frue, Kureji, Spekkio, Wrath of Foomy: bank $D0) only changed that byte there
    private const int MonsterBankAt = 16;

    private static byte[] Hex(string hex) => hex.Split(' ').Select(b => Convert.ToByte(b, 16)).ToArray();

    // file offset of our script block (hooks and table), -1 if it's not in the rom
    public static int Block(RomBuffer rom)
    {
        if (rom.Length <= AttackSite + 4 || rom.ReadByte(AttackSite) != 0x5C) return -1;
        int code = L2Rom.SnesToFile((int)rom.ReadUInt24Le(AttackSite + 1));
        int block = code - CodeAt - HookAOffset;
        return block >= 0 && block + BlockSize <= rom.Length && System.Text.Encoding.ASCII.GetString(rom.ReadBytes(block, Magic.Length)) == Magic ? block : -1;
    }

    public static bool IsInstalled(RomBuffer rom) => Block(rom) >= 0;

    // the script block's bytes (file offset, length), null if it's not in the rom
    internal static (int At, int Length)? BlockRange(RomBuffer rom) => Block(rom) is var block && block >= 0 ? (block, BlockSize) : null;

    private static int Version(RomBuffer rom, int block) => rom.ReadByte(block + Magic.Length);

    // whether the hooks can go in (the four places still have the game's code)
    public static bool CanInstall(RomBuffer rom, out string why)
    {
        why = "";
        if (IsInstalled(rom)) return true;
        bool Same(int offset, byte[] expected) => offset + expected.Length <= rom.Length && rom.ReadBytes(offset, expected.Length).SequenceEqual(expected);
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

    // the hook code for a block whose code starts at SNES code and whose table is at SNES table. routines: Find (the
    // table lookup), A (a monster's turn), C (a capsule monster's turn), D (being hit). all run with 8-bit A and
    // 16-bit index registers, same as the game's code there
    private static byte[] Assemble(int code, int table, int monsterBank)
    {
        var asm = new Asm(code);
        void Long(byte op, int addr) => asm.B(op, (byte)addr, (byte)(addr >> 8), (byte)(addr >> 16));
        // Find: A = kind, X = record (16-bit). found: $0A42/$0A44 = the script's base, Y = 1, carry set. else X, Y kept, carry clear
        asm.Label("find");
        asm.B(0x5A, 0xDA, 0x48);                 // PHY PHX PHA  (1,S kind, 2,S record)
        asm.B(0xA2, 0x00, 0x00);                 // LDX #0
        asm.Label("loop");
        Long(0xBF, table + 2);                 // LDA table+2,X (kind)
        asm.B(0xC9, 0xFF); asm.Br(0xF0, "missing"); // CMP #$FF / BEQ
        asm.B(0xC3, 0x01); asm.Br(0xD0, "next");    // CMP 1,S / BNE
        asm.B(0xC2, 0x20);                       // REP #$20
        Long(0xBF, table);                     // LDA table,X (record)
        asm.B(0xC3, 0x02);                       // CMP 2,S
        asm.B(0xE2, 0x20); asm.Br(0xD0, "next");    // SEP #$20 / BNE
        asm.B(0xC2, 0x20);
        Long(0xBF, table + 3);                 // LDA table+3,X (script lo/hi)
        asm.B(0x8D, 0x42, 0x0A);                 // STA $0A42
        asm.B(0xE2, 0x20);
        Long(0xBF, table + 5);                 // LDA table+5,X (script bank)
        asm.B(0x8D, 0x44, 0x0A);                 // STA $0A44
        asm.B(0x68, 0xFA, 0x7A);                 // PLA PLX PLY
        asm.B(0xA0, 0x01, 0x00, 0x38, 0x60);     // LDY #1 / SEC / RTS
        asm.Label("next");
        asm.B(0xE8, 0xE8, 0xE8, 0xE8, 0xE8, 0xE8); asm.Br(0x80, "loop");
        asm.Label("missing");
        asm.B(0x68, 0xFA, 0x7A, 0x18, 0x60);     // PLA PLX PLY / CLC / RTS

        // A: a monster's turn (JML from $81:B1D4; X = the combatant)
        asm.Label("hookA");
        asm.B(0xBC, 0x4A, 0x00);                 // LDY $004A,X (attack script offset)
        asm.B(0xBD, 0x50, 0x00);                 // LDA $0050,X (monster id)
        Long(0x22, 0x81FCE2);                  // JSL $81:FCE2 -> X = record
        asm.B(0xA9, (byte)Kind.MonsterAttack); asm.Jsr("find");
        asm.Br(0xB0, "runA");
        asm.B(0xC0, 0x00, 0x00); asm.Br(0xF0, "plainA");   // CPY #0 / BEQ
        asm.B(0x8E, 0x42, 0x0A, 0xA9, (byte)monsterBank, 0x8D, 0x44, 0x0A);   // STX $0A42 / LDA #bank / STA $0A44
        asm.Label("runA"); Long(0x5C, 0x81B1F2);  // JML (JSL $81:FAC9 / RTL)
        asm.Label("plainA"); Long(0x5C, 0x81B1E7); // JML (the built-in plain attack)

        // C: a capsule monster's turn (JML from $81:B1A3)
        asm.Label("hookC");
        asm.B(0xAC, 0x29, 0x11, 0xAE, 0x24, 0x11);   // LDY $1129 / LDX $1124
        asm.B(0xA9, (byte)Kind.CapsuleAi); asm.Jsr("find");
        asm.Br(0xB0, "runC");
        asm.B(0xC0, 0x00, 0x00); asm.Br(0xF0, "plainC");
        asm.B(0x8E, 0x42, 0x0A, 0xAD, 0x26, 0x11, 0x8D, 0x44, 0x0A);   // STX $0A42 / LDA $1126 / STA $0A44
        asm.Label("runC"); Long(0x5C, 0x81B1C4);
        asm.Label("plainC"); Long(0x5C, 0x81B1B6);

        // D: being hit (JSL from $85:D165 and $85:D1A6, in place of LDA $0047,X / STA $0A44 / LDY $004C,X)
        asm.Label("hookD");
        asm.B(0xBD, 0x47, 0x00, 0x8D, 0x44, 0x0A, 0xBC, 0x4C, 0x00);
        asm.B(0xDA, 0x48);                       // PHX / PHA (bank)
        asm.B(0xC2, 0x20, 0xBD, 0x45, 0x00, 0xAA, 0xE2, 0x20);   // REP / LDA $0045,X / TAX / SEP
        asm.B(0x68, 0xC9, 0x97); asm.Br(0xF0, "capD");             // PLA / CMP #$97 / BEQ
        asm.B(0xA9, (byte)Kind.MonsterDefense); asm.Br(0x80, "findD");
        asm.Label("capD"); asm.B(0xA9, (byte)Kind.CapsuleDefense);
        asm.Label("findD"); asm.Jsr("find");
        asm.B(0xFA, 0x6B);                       // PLX / RTL

        // (version 2, after the routines above so a version 1 block keeps their places)
        // E: the script runner (JML from $81:FAC9, in place of PHP / PHB / REP #$30). Y = offset, $0A42/$0A44 = base.
        // an item's marker offset ($8000 | key, from F) runs its script from the table. any other script gets looked
        // up by its address (spells, IP attacks) and otherwise runs the way the game has it
        asm.Label("hookE");
        asm.B(0x08, 0x8B, 0xC2, 0x30, 0x48, 0xDA, 0x5A);   // PHP PHB REP #$30 PHA PHX PHY (same as the game)
        asm.B(0xC0, 0x00, 0x00); asm.Br(0xF0, "outE");        // CPY #0 / BEQ
        // a marker has bit 15 set, a base (the game's built-in scripts run with base 0 and offsets like $EF24), the item
        // records' bank (every item path in the game sets $0A44 = $96: $81:A8F2, A953, AA5D, EFBB, F009, $85:BD99) and a
        // table entry. anything else is a real offset: the fan hacks' monster records in bank $D0 use offsets like $F780
        // that wrap round the bank (version 2 took those for markers, so those scripts didn't run)
        asm.B(0x98); asm.Br(0x10, "absE");                    // TYA / BPL
        asm.B(0xAD, 0x42, 0x0A); asm.Br(0xF0, "absE");        // LDA $0A42 / BEQ
        asm.B(0xAD, 0x44, 0x0A, 0x29, 0xFF, 0x00, 0xC9, ItemBank, 0x00); asm.Br(0xD0, "absE");   // LDA $0A44 / AND #$FF / CMP #$96 / BNE
        asm.B(0x98, 0x29, 0xFF, 0x7F, 0xAA, 0xE2, 0x20, 0xA9, ItemMarker); asm.Jsr("find");   // TYA / AND #$7FFF / TAX / SEP / LDA #FD
        asm.Br(0xB0, "outE");                               // (found: Y = 1, base and bank set)
        asm.B(0xC2, 0x20);                                  // REP #$20 (not a marker: Y is the offset again)
        asm.Label("absE");
        asm.B(0x98, 0x18, 0x6D, 0x42, 0x0A, 0xAA);          // TYA / CLC / ADC $0A42 / TAX (the script's address)
        asm.B(0xE2, 0x20, 0xAD, 0x44, 0x0A); asm.Jsr("find"); // SEP #$20 / LDA $0A44 (its bank as the kind)
        asm.Label("outE"); Long(0x5C, 0x81FAD0);            // JML back (SEP #$20 / PHK / PLB / CPY #0...)

        // F: the item loader ($81:F1C5, JML in place of PHP / PHB / SEP #$20): runs the game's loader, then each
        // property the table has a script for gets the marker word $8000 | (item | property << 9) at $0B8D + 2 * property
        asm.Label("hookF");
        asm.B(0x4B); asm.Pea("retF");                         // PHK / PEA retF-1 (the loader's RTL comes back here)
        asm.B(0x08, 0x8B, 0xE2, 0x20); Long(0x5C, 0x81F1C9); // PHP PHB SEP #$20 (same as the game) / JML on
        asm.Label("retF");
        asm.B(0x08, 0xC2, 0x30, 0x48, 0xDA, 0x5A);          // PHP / REP #$30 / PHA PHX PHY
        Long(0xAF, 0x7E0A06); asm.B(0x29, 0xFF, 0x01, 0x48); // LDA $7E:0A06 / AND #$01FF / PHA (1,S = the item)
        asm.B(0xA2, 0x00, 0x00);                            // LDX #0
        asm.Label("loopF");
        Long(0xBF, table + 2); asm.B(0x29, 0xFF, 0x00);     // LDA table+2,X / AND #$FF (kind)
        asm.B(0xC9, 0xFF, 0x00); asm.Br(0xF0, "doneF");
        asm.B(0xC9, ItemMarker, 0x00); asm.Br(0xF0, "isFD");
        asm.B(0xC9, ItemWordKind, 0x00); asm.Br(0xD0, "nextF");
        // FC: a property word the record doesn't have (key = item | bit << 9, the value in the script field)
        Long(0xBF, table); asm.B(0x29, 0xFF, 0x01, 0xC3, 0x01); asm.Br(0xD0, "nextF");
        Long(0xBF, table + 3); asm.B(0x48);                 // LDA table+3,X / PHA (the value)
        Long(0xBF, table); asm.B(0xEB, 0x4A, 0x29, 0x0F, 0x00, 0x0A);   // XBA / LSR / AND #$0F / ASL (2 * bit)
        asm.B(0xDA, 0xAA, 0xA3, 0x03);                      // PHX / TAX / LDA 3,S
        Long(0x9F, 0x7E0B8D);                             // STA $7E:0B8D,X
        asm.B(0xFA, 0x68); asm.Br(0x80, "nextF");             // PLX / PLA
        asm.Label("isFD");
        Long(0xBF, table); asm.B(0x29, 0xFF, 0x01, 0xC3, 0x01); asm.Br(0xD0, "nextF");   // key & $1FF == item?
        Long(0xBF, table); asm.B(0x09, 0x00, 0x80, 0x48);   // LDA table,X / ORA #$8000 / PHA (the marker)
        Long(0xBF, table); asm.B(0xEB, 0x4A, 0x29, 0x03, 0x00, 0x0A);   // XBA / LSR / AND #3 / ASL (2 * property)
        asm.B(0xDA, 0xAA, 0xA3, 0x03);                      // PHX / TAX / LDA 3,S
        Long(0x9F, 0x7E0B8D);                             // STA $7E:0B8D,X
        asm.B(0xFA, 0x68);                                  // PLX / PLA
        asm.Label("nextF");
        asm.B(0x8A, 0x18, 0x69, 0x06, 0x00, 0xAA); asm.Br(0x80, "loopF");   // X += 6
        asm.Label("doneF");
        asm.B(0x68, 0x7A, 0xFA, 0x68, 0x28, 0x6B);          // PLA PLY PLX PLA / PLP / RTL

        var bytes = asm.Done();
        HookAOffset = asm.At("hookA"); HookCOffset = asm.At("hookC"); HookDOffset = asm.At("hookD");
        HookEOffset = asm.At("hookE"); HookFOffset = asm.At("hookF");
        return bytes;
    }

    // a tiny 65816 assembler: bytes, labels, 8-bit branches and JSRs inside the block
    internal sealed class Asm(int origin)
    {
        private readonly List<byte> _bytes = new();
        private readonly Dictionary<string, int> _labels = new();
        // fix kinds: 0 = 8-bit branch, 1 = 16-bit address, 2 = 16-bit address - 1 (PEA for an RTL/RTS)
        private readonly List<(int At, string Label, int Fix)> _fixups = new();
        public void B(params byte[] b) => _bytes.AddRange(b);
        public void Label(string label) => _labels[label] = _bytes.Count;
        public void Br(byte op, string label) { _bytes.Add(op); _fixups.Add((_bytes.Count, label, 0)); _bytes.Add(0); }
        public void Jsr(string label) { _bytes.Add(0x20); _fixups.Add((_bytes.Count, label, 1)); _bytes.Add(0); _bytes.Add(0); }
        public void Pea(string label) { _bytes.Add(0xF4); _fixups.Add((_bytes.Count, label, 2)); _bytes.Add(0); _bytes.Add(0); }
        // a label's 16-bit address (the operand of an absolute or long instruction written before it)
        public void Abs(string label) { _fixups.Add((_bytes.Count, label, 1)); _bytes.Add(0); _bytes.Add(0); }
        // where the next byte goes (from the origin)
        public int Here => _bytes.Count;
        public int At(string label) => _labels[label];
        public byte[] Done()
        {
            foreach (var (fixAt, label, fix) in _fixups)
            {
                int to = _labels[label];
                if (fix > 0) { int addr = (origin + to - (fix == 2 ? 1 : 0)) & 0xFFFF; _bytes[fixAt] = (byte)addr; _bytes[fixAt + 1] = (byte)(addr >> 8); continue; }
                int branchOffset = to - (fixAt + 1);
                if (branchOffset is < -128 or > 127) throw new InvalidOperationException($"Branch to {label} too far.");
                _bytes[fixAt] = (byte)branchOffset;
            }
            return _bytes.ToArray();
        }
    }

    // puts the hooks and the empty table in (once)
    public static int Install(RomBuffer rom)
    {
        int existing = Block(rom);
        if (existing >= 0) return Version(rom, existing) >= CurrentVersion ? existing : Upgrade(rom, existing);
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
        var defenseJump = Jump(0x22, code + HookDOffset).Concat(Enumerable.Repeat((byte)0xEA, 5)).ToArray();
        rom.WriteBytes(DefenseSite1, defenseJump);
        rom.WriteBytes(DefenseSite2, defenseJump);
        rom.WriteBytes(RunnerSite, Jump(0x5C, code + HookEOffset));
        rom.WriteBytes(ItemLoaderSite, Jump(0x5C, code + HookFOffset));
        return block;
    }

    // the monster records moved to bank bank (L2NewMonsters), so the turn hook's LDA #bank follows (we assemble the
    // code again, its routines keep their places)
    internal static void SetMonsterBank(RomBuffer rom, int bank)
    {
        int block = Block(rom);
        if (block < 0) return;
        rom.WriteByte(block + Magic.Length + 1, (byte)bank);
        if (Version(rom, block) >= 2) { Upgrade(rom, block); return; }   // (the code again, the newest version's)
        int code = L2Rom.FileToSnes(block + CodeAt), table = L2Rom.FileToSnes(block + TableAt);
        var bytes = Assemble(code, table, bank).Take(HookEOffset).ToArray();   // (a version 1 block only has A-D)
        rom.WriteBytes(block + CodeAt, bytes);
    }

    // true if the rom has a version 2 block, whose runner hook doesn't run the fan hacks' monster scripts with
    // offsets that wrap round their bank (UpdateHooks)
    public static bool NeedsHookUpdate(RomBuffer rom) => Block(rom) is var block && block >= 0 && Version(rom, block) == 2 && OwnSites(rom, block);

    // puts the newest hook code in a version 2 block (NeedsHookUpdate). true if it changed
    public static bool UpdateHooks(RomBuffer rom)
    {
        if (!NeedsHookUpdate(rom)) return false;
        Upgrade(rom, Block(rom));
        return true;
    }

    // whether the runner and the item loader jump into the block (a version 2 block's hooks)
    private static bool OwnSites(RomBuffer rom, int block)
    {
        bool Ours(int site) => rom.ReadByte(site) == 0x5C && L2Rom.SnesToFile((int)rom.ReadUInt24Le(site + 1)) is var to && to >= block + CodeAt && to < block + TableAt;
        return Ours(RunnerSite) && Ours(ItemLoaderSite);
    }

    // the bytes of the game's code an update of the block's hooks can change (the runner's and the item loader's jumps)
    internal static IEnumerable<int> HookSiteBytes() => Enumerable.Range(0, 4).SelectMany(k => new[] { RunnerSite + k, ItemLoaderSite + k });

    // an older block gets the newest code: a version 1 block (monster and capsule scripts only) gets the effect
    // hooks, a version 2 block the fixed runner hook. we assemble the code again (the first routines keep their
    // places) and point the runner and item loader at it
    private static int Upgrade(RomBuffer rom, int block)
    {
        bool Same(int offset, byte[] expected) => offset + expected.Length <= rom.Length && rom.ReadBytes(offset, expected.Length).SequenceEqual(expected);
        if (Version(rom, block) < 2 ? !Same(RunnerSite, RunnerOriginal) || !Same(ItemLoaderSite, ItemLoaderOriginal) : !OwnSites(rom, block))
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

    // the table entry's key:
    // - (record, kind 0-3) for monsters and capsules
    // - (item | property << 9, FD) for items, whose loader gets a marker word $8000 | key
    // - (script address, bank) for spells and IP attacks, the script the game would run
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
    // the bank the game gives every item script ($0A44 = $96, the item records' bank)
    private const byte ItemBank = 0x96;

    // property words an item record doesn't have (stat bonuses, IP attack, battle animation)

    // the words we give an item on top of its record (bit -> value). the item loader hook puts them where the game
    // keeps the record's own ($0B8D + 2 * bit), so the game reads them like any other
    public static Dictionary<int, int> ItemAddedWords(RomBuffer rom, int item)
    {
        var words = new Dictionary<int, int>();
        int block = Block(rom);
        if (block < 0 || Version(rom, block) < 2) return words;
        foreach (var entry in Entries(rom, block))
            if (entry.Kind == ItemWordKind && (entry.Record & 0x1FF) == item)
                words[1 << ((entry.Record >> 9) & 0xF)] = rom.ReadUInt16Le(entry.At + 3);
        return words;
    }

    // gives an item a property word its record doesn't have (null takes it away again)
    public static void SetItemAddedWord(RomBuffer rom, int item, int bit, int? value)
    {
        int bitNumber = System.Numerics.BitOperations.Log2((uint)bit);
        if (bit <= 0 || (bit & (bit - 1)) != 0 || bitNumber < 4) throw new ArgumentException("Scripts go through Save; this is for the other words.");
        int key = item | bitNumber << 9;
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
        int entryAt = i >= 0 ? all[i].At : block + TableAt + all.Count * EntrySize;
        if (i < 0)
        {
            if (all.Count >= TableCapacity) throw new InvalidOperationException("Lufia Forge's script table is full.");
            rom.WriteBytes(entryAt + EntrySize, new byte[] { 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF });
        }
        int word = value.Value & 0xFFFF;
        rom.WriteBytes(entryAt, new[] { (byte)key, (byte)(key >> 8), ItemWordKind, (byte)word, (byte)(word >> 8), (byte)0 });
    }

    private static IEnumerable<(int At, int Record, byte Kind, int Script)> Entries(RomBuffer rom, int block)
    {
        for (int i = 0; i < TableCapacity; i++)
        {
            int entryAt = block + TableAt + i * EntrySize;
            byte kind = rom.ReadByte(entryAt + 2);
            if (kind == 0xFF) yield break;
            yield return (entryAt, rom.ReadUInt16Le(entryAt), kind, L2Rom.SnesToFile((int)rom.ReadUInt24Le(entryAt + 3)));
        }
    }

    // file offset of the script's base (its 00) if our table has one for it, else null
    public static int? Override(RomBuffer rom, Kind kind, int index)
    {
        int block = Block(rom);
        if (block < 0 || IsEffect(kind) && Version(rom, block) < 2) return null;
        var (key, keyKind) = TableKey(rom, kind, index);
        foreach (var entry in Entries(rom, block))
            if (entry.Record == key && entry.Kind == keyKind) return entry.Script;
        return null;
    }

    // writes the script for (kind, index) to the added space and points the table at it (the game's bytes stay how
    // they are). returns the script's file offset
    public static int Save(RomBuffer rom, Kind kind, int index, IReadOnlyList<Step> steps) =>
        // all or nothing: the status patch, the element rows and the game check fixes go in before the script gets
        // written, and if something fails after them (a spell with no script, the table full) they can't stay behind
        L2Rom.AllOrNothing(rom, () => SaveCore(rom, kind, index, steps));

    private static int SaveCore(RomBuffer rom, Kind kind, int index, IReadOnlyList<Step> steps)
    {
        if (steps.Count == 0) throw new InvalidOperationException("The script is empty: it needs at least an End.");
        if (steps.Any(st => st.OutsideTargets.Any(o => o >= 0)))
            throw new InvalidOperationException("A \"go to\" jumps into the middle of a command or onto data, so the script can't be moved: point it at a step.");
        var body = Build(steps, 1);
        if (body.Length > 0x7F00) throw new InvalidOperationException("The script is too long.");
        // the new statuses' commands need their code in the game
        if (steps.Any(st => st.Op is >= 0x5D and <= 0x61) && !L2StatusPatch.IsApplied(rom))
        {
            // the battle code goes in, and with it the element table. the game's weaknesses move there, this script's too
            bool fresh = !L2Elements.IsIn(rom);   // no element table yet, so this Apply makes it from the scripts
            L2StatusPatch.Apply(rom);
            // its element steps go to the row too (only if the row was just made from the script, an upgrade keeps
            // the table how it was), and we build the script again without them, the old bytes would count them twice
            if (fresh) steps = L2Elements.MoveIncoming(rom, kind, index, steps);
            body = Build(steps, 1);
        }
        // an attack with a second element on top of a type's (a fire sword that's also vs dragons): the game's dragon,
        // insect, flying and hard shell checks want the element alone, so they get bit tests
        if (steps.Any(MixedTypeElement)) L2MonsterTypes.FixGameChecks(rom);
        if (kind is Kind.Spell or Kind.IpAttack && OriginalAt(rom, kind, index) < 0)
            throw new InvalidOperationException("It has no effect script for an edited one to take the place of.");
        int block = Install(rom);
        var (key, keyKind) = TableKey(rom, kind, index);
        var entry = Entries(rom, block).FirstOrDefault(e => e.Record == key && e.Kind == keyKind);
        if (entry.At <= 0 && Entries(rom, block).Count() >= TableCapacity) throw new InvalidOperationException("Lufia Forge's script table is full.");
        // [00][script][00]: a last step that doesn't end the script still stops at the 00 after it
        int needed = body.Length + 2;
        int baseAt;
        if (entry.At > 0 && rom.ReadUInt16Le(entry.Script - 2) >= needed)
            baseAt = entry.Script;   // fits where it was
        else
        {
            int newCapacity = Math.Max(needed + 16, 32);
            int spaceAt = L2Rom.Allocate(rom, newCapacity + 2);
            rom.WriteUInt16Le(spaceAt, (ushort)newCapacity);
            baseAt = spaceAt + 2;
        }
        int capacity = rom.ReadUInt16Le(baseAt - 2);
        rom.WriteBytes(baseAt, new byte[capacity]);
        rom.WriteByte(baseAt, 0x00);
        rom.WriteBytes(baseAt + 1, body);
        int snes = L2Rom.FileToSnes(baseAt);
        int entryAt = entry.At > 0 ? entry.At : block + TableAt + Entries(rom, block).Count() * EntrySize;
        if (entry.At <= 0)
        {
            if ((entryAt - block - TableAt) / EntrySize >= TableCapacity) throw new InvalidOperationException("Lufia Forge's script table is full.");
            rom.WriteBytes(entryAt + EntrySize, new byte[] { 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF });
        }
        rom.WriteBytes(entryAt, new[] { (byte)key, (byte)(key >> 8), keyKind, (byte)snes, (byte)(snes >> 8), (byte)(snes >> 16) });
        // the STATUS screen's resistance pages read the armor scripts' table
        if (kind == Kind.Armor) L2StatusPatch.WriteResistTable(rom);
        return baseAt;
    }

    // a step that gives an attack the element of a type the game tests with "is" (thrown, blunt, vs dragons, vs
    // insects, earth) along with another one: 1F, 21, 22 or 0C 27 with two bits or more, or 17 27 (bits added)
    private static bool MixedTypeElement(Step step)
    {
        const int Exact = 0x0040 | 0x0080 | 0x0100 | 0x0200 | 0x1000;
        int elementBits;
        if (step.Op is 0x1F or 0x21 or 0x22) elementBits = ArgValue(step, 0);
        else if (step.Op is 0x0C or 0x17 && ArgValue(step, 0) == 0x27 && (ArgValue(step, 1) >> 8) != 0x80) elementBits = ArgValue(step, 1);
        else return false;
        return (elementBits & Exact) != 0 && (step.Op == 0x17 || System.Numerics.BitOperations.PopCount((uint)elementBits) > 1);
    }

    // back to the game's own script (or none): drops the table entry (its space is left as is)
    public static bool Revert(RomBuffer rom, Kind kind, int index)
    {
        int block = Block(rom);
        if (block < 0) return false;
        var (key, keyKind) = TableKey(rom, kind, index);
        var all = Entries(rom, block).ToList();
        int i = all.FindIndex(e => e.Record == key && e.Kind == keyKind);
        if (i < 0) return false;
        // move the last entry into its place, end the table one entry earlier
        var last = all[^1];
        if (i != all.Count - 1) rom.WriteBytes(all[i].At, rom.ReadBytes(last.At, EntrySize));
        rom.WriteBytes(last.At, new byte[] { 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF });
        if (kind == Kind.Armor) L2StatusPatch.WriteResistTable(rom);
        return true;
    }

    // spell and IP effect scripts are keyed by their address, so when we move those scripts (L2Magic) the entries
    // follow them. map gives a script's new file offset from its old one (null = it didn't move)
    internal static void MoveEffectKeys(RomBuffer rom, Func<int, int?> map)
    {
        int block = Block(rom);
        if (block < 0) return;
        foreach (var entry in Entries(rom, block).ToList())
        {
            if (entry.Kind < 0x80 || entry.Kind is ItemMarker or ItemWordKind or 0xFF) continue;
            int oldOffset = L2Rom.SnesToFile(entry.Kind << 16 | entry.Record);
            if (map(oldOffset) is not int moved) continue;
            int snes = L2Rom.FileToSnes(moved);
            rom.WriteBytes(entry.At, new[] { (byte)snes, (byte)(snes >> 8), (byte)(snes >> 16) });
        }
    }

    // a script's bytes for a copy laid out from first (counted from its jump base), or throws why it can't be copied
    public static byte[] BuildCopy(IReadOnlyList<Step> steps, int first)
    {
        if (steps.Any(st => st.OutsideTargets.Any(o => o >= 0)))
            throw new InvalidOperationException("A \"go to\" of this script jumps into the middle of a command or onto data, so it can't be copied: point it at a step first.");
        return steps.Count == 0 ? new byte[] { 0 } : Build(steps, first);
    }

    // how many (kind, index) have an edited script
    public static int EditedCount(RomBuffer rom) => Block(rom) is var block && block >= 0 ? Entries(rom, block).Count() : 0;

    // text

    // names used to show arguments (spells, monsters, items, capsule attacks)
    public sealed class NameLists
    {
        public string[] Spells { get; init; } = Array.Empty<string>();
        public string[] Monsters { get; init; } = Array.Empty<string>();
        public string[] Items { get; init; } = Array.Empty<string>();
        public string[] CapsuleAttacks { get; init; } = Array.Empty<string>();
        // what each shared script does (LibraryName)
        public string[] Library { get; set; } = Array.Empty<string>();
        // the monster attack names 1E shows (index 0 = none)
        public string[] AttackNames { get; set; } = Array.Empty<string>();
        // battle animations 00-FF named by what uses them (L2Magic.Animations)
        public string[] Animations { get; set; } = Array.Empty<string>();

        public static NameLists From(RomBuffer rom)
        {
            var lists = new NameLists
            {
                Spells = L2Battle.Spells(rom).Select(s => s.Name).ToArray(),
                Monsters = L2Battle.Monsters(rom).Select(m => m.Unused ? "(unused slot)" : m.Name).ToArray(),
                Items = L2Battle.Items(rom).Select(i => i.Name).ToArray(),
                CapsuleAttacks = L2Capsules.AttackList(rom),
                AttackNames = MonsterAttackNames(rom),
            };
            lists.Library = Enumerable.Range(0, LibraryCount).Select(i => LibraryName(rom, i, lists)).ToArray();
            var animationNames = L2Magic.Animations(rom).ToDictionary(a => a.Value, a => a.Name);
            lists.Animations = Enumerable.Range(0, 0x100).Select(k => animationNames.TryGetValue(k, out var name) ? name : k == 0xFF ? "none" : "(no animation here)").ToArray();
            return lists;
        }
    }

    private static string Pick(string[] names, int i) => i >= 0 && i < names.Length ? $"{i:X2} {names[i]}" : $"{i:X2}";

    // an argument as text. step numbers a jump's target step (1-based), or -1
    public static string ArgText(Arg arg, int value, NameLists names, int step = -1) => arg switch
    {
        Arg.Jump => step > 0 ? $"step {step}" : $"offset {value:X4}",
        Arg.Chance => $"{value}/256 ({value * 100 / 256}%)",
        Arg.Percent => $"{value}%",
        Arg.Register => RegisterName(value),
        Arg.Value => ValueText(value),
        Arg.Spell => Pick(names.Spells, value),
        Arg.Monster => Pick(names.Monsters, value),
        Arg.Item => Pick(names.Items, value & 0x1FF),
        Arg.CapsuleAttack => Pick(names.CapsuleAttacks, value),
        Arg.Element => ElementText(value),
        Arg.Stat => value < Stats.Length ? Stats[value] : $"stat {value:X2}",
        Arg.Status => value < Statuses.Length ? Statuses[value] : $"status {value:X2}",
        Arg.Target => value < Targets.Length ? Targets[value] : $"{value:X2}",
        Arg.Restore => value < Restores.Length ? Restores[value] : $"{value:X2}",
        Arg.Library => value < names.Library.Length ? $"shared script {value:X2} ({names.Library[value]})" : $"shared script {value:X2}",
        Arg.AttackName => value > 0 && value < names.AttackNames.Length ? $"\"{names.AttackNames[value]}\"" : value == 0 ? "(none)" : $"attack name {value:X2}",
        Arg.Change => $"{(sbyte)value:+0;-0;0}%",
        Arg.Animation => value < names.Animations.Length ? $"animation {value:X2} ({names.Animations[value]})" : $"animation {value:X2}",
        Arg.Sound => $"sound {value:X2}",
        Arg.Message => $"{value:X2} {MessageText(value)}",
        Arg.NewStatus => value < L2StatusPatch.Names.Length ? L2StatusPatch.Names[value] : value == L2StatusPatch.Every ? L2StatusPatch.EveryName.ToLowerInvariant() : $"new status {value}",
        Arg.Word => $"{value}",
        _ => $"{value}",
    };

    // the value of argument i of a step
    public static int ArgValue(Step s, int i)
    {
        var def = s.Def!;
        int argOffset = ArgAt(def, i);
        return Size(def.Args[i]) == 2 ? s.Args[argOffset] | s.Args[argOffset + 1] << 8 : s.Args[argOffset];
    }

    public static void SetArg(Step s, int i, int value)
    {
        var def = s.Def!;
        int argOffset = ArgAt(def, i);
        s.Args[argOffset] = (byte)value;
        if (Size(def.Args[i]) == 2) s.Args[argOffset + 1] = (byte)(value >> 8);
    }

    // one line for a step, like "Cast a spell: 03 Fire" or "If the damage is more than 100, go to step 7"
    public static string Describe(Step step, IReadOnlyList<Step> all, NameLists names)
    {
        var def = step.Def;
        if (def == null) return $"command {step.Op:X2}";
        if (Sentence(step, all, names) is { } sentence) return sentence;
        var parts = new List<string>();
        int j = 0;
        for (int i = 0; i < def.Args.Length; i++)
        {
            int stepNumber = -1;
            if (def.Args[i] == Arg.Jump) { var target = j < step.Targets.Length ? step.Targets[j] : null; stepNumber = target == null ? -1 : IndexOf(all, target) + 1; j++; }
            parts.Add(ArgText(def.Args[i], ArgValue(step, i), names, stepNumber));
        }
        return parts.Count == 0 ? def.Name : $"{def.Name}: {string.Join(", ", parts)}";
    }

    // number and effect commands as a sentence ("Set the damage to 0", "Add 5 to its counter 1"). null for the
    // others, they read fine as "name: values"
    private static string? Sentence(Step step, IReadOnlyList<Step> all, NameLists names)
    {
        string A(int i) => ArgText(step.Def!.Args[i], ArgValue(step, i), names);
        string Go() { var target = step.Targets.Length > 0 ? step.Targets[0] : null; int index = target == null ? -1 : IndexOf(all, target); return index < 0 ? "(no step)" : $"step {index + 1}"; }
        string Cap(string text) => text.Length == 0 ? text : char.ToUpperInvariant(text[0]) + text[1..];
        string Pct(int chance) => $"{chance * 100 / 256}%";
        return step.Op switch
        {
            0x32 => $"Target: {A(0)}",
            0x28 => "Attack",
            0x2C => $"Cast {A(0)}",
            0x54 => $"Cast {A(0)} for free",
            0x2B => $"Use the item {A(0)}",
            0x2D => $"Call for help: {A(0)}",
            0x1E => $"Show the name {A(0)}",
            0x5A => $"Play {A(0)}",
            0x3D => $"Play sound effect {ArgValue(step, 0):X2}",
            0x05 => $"With a {Pct(ArgValue(step, 0))} chance, go to {Go()}",
            0x04 => $"If that didn't work, go to {Go()}",
            0x03 => $"Go to {Go()}",
            0x3F => $"If it has learned capsule attack {ArgValue(step, 0)}, go to {Go()}",
            0x56 => $"Keep the message up for {ValueText(ArgValue(step, 0))} frames",
            0x25 => $"{((sbyte)ArgValue(step, 1) < 0 ? "Lower" : "Raise")} the target's {A(0)} by {Math.Abs((sbyte)ArgValue(step, 1))}%",
            0x0C when ArgValue(step, 0) == 0x23 && ArgValue(step, 1) == 5 => "Make it a special attack (the effects above happen)",
            0x0C when ArgValue(step, 0) == 0x23 && ArgValue(step, 1) == 0x0A => "Make it a magic attack (the effects above happen)",
            0x0C when ArgValue(step, 0) == 0x26 && (ArgValue(step, 1) >> 8) != 0x80 => $"Set the action's flags to {ArgValue(step, 1):X} ({FlagsText(ArgValue(step, 1))})",
            0x17 when ArgValue(step, 0) == 0x26 => $"The action: {FlagsText(ArgValue(step, 1))}",
            0x17 when ArgValue(step, 0) == 0x27 && (ArgValue(step, 1) >> 8) != 0x80 => $"The attack is also: {ElementText(ArgValue(step, 1))}",
            0x16 when ArgValue(step, 0) == 0x26 => $"The action: only {FlagsText(ArgValue(step, 1))}",
            0x35 => $"Result message: {MessageText(ArgValue(step, 0))}",
            0x4A => $"Wait {ArgValue(step, 0)} frames",
            0x23 => $"Damage of power {ArgValue(step, 0)}",
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
            0x16 => $"{Cap(A(0))}: keep only the bits {Bits(ArgValue(step, 1))}",
            0x17 => $"{Cap(A(0))}: add the bits {Bits(ArgValue(step, 1))}",
            0x18 => $"{Cap(A(0))}: flip the bits {Bits(ArgValue(step, 1))}",
            0x11 => $"{Cap(A(0))} becomes a random number below it",
            0x19 => $"Make {A(0)} positive",
            0x1A => $"Turn {A(0)} negative (or back)",
            0x13 => $"{Cap(A(0))} = the target's {A(1)}",
            0x15 => $"{Cap(A(0))} = its own {A(1)}",
            0x4E => $"{Cap(A(0))} = the attacker's {A(1)}",
            0x14 => $"Set the target's {A(1)} to {A(0)}",
            0x21 => $"Physical damage ({A(0)}): power {DamageText(ArgValue(step, 1))}",
            0x22 => $"Magic damage ({A(0)}): power {DamageText(ArgValue(step, 1))} + INT",
            0x24 when (ArgValue(step, 1) >> 8) != 0x80 && (short)ArgValue(step, 1) < 0 => $"Take {-(short)ArgValue(step, 1)} {A(0)}" + (ArgValue(step, 2) > 0 ? $" (up to {ArgValue(step, 2) * 100 / 256}% more)" : ""),
            0x24 => $"Restore {A(0)} by {A(1)}" + (ArgValue(step, 2) > 0 ? $" (up to {ArgValue(step, 2) * 100 / 256}% more)" : ""),
            0x26 => $"Cure {A(0)} ({A(1)} chance)",
            0x5D => $"Cause {A(0)} ({A(1)} chance)",
            0x5E => $"Cure {A(0)} ({A(1)} chance)",
            0x5F => $"Resist {A(0)} by {A(1)}",
            0x60 => $"Ward the targets against {A(0)}",
            0x61 => "Give the targets focus",
            0x27 => $"Cause {A(0)} ({A(1)} chance)",
            0x1F => $"The attack's element: {A(0)}",
            0x20 => $"Critical hits: {A(0)} chance, {ArgValue(step, 1) / 16.0:0.##}x damage, through DFP",
            _ => null,
        };
    }

    // a damage amount: the power as a number (Flash 65), or a number worked out above
    private static string DamageText(int power) => ValueText(power);

    private static int IndexOf(IReadOnlyList<Step> all, Step target) { for (int i = 0; i < all.Count; i++) if (ReferenceEquals(all[i], target)) return i; return -1; }

    // the whole script as numbered lines
    public static string Listing(IReadOnlyList<Step> steps, NameLists names) =>
        steps.Count == 0 ? "(none)" : string.Join(Environment.NewLine, steps.Select((step, i) => $"{i + 1,3}  {Describe(step, steps, names)}"));
}
