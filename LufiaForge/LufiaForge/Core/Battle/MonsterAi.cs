namespace LufiaForge.Core.Battle;

// monster AI scripts, worked out from the battle code at $08:CA97. a monster record's word at +$22 is the offset
// (from the record's start) of its script, 0 = no script (it just attacks). the script is a list of commands
// [op][args] ended by 00 or by a command that picks the action, and the battle runs it from the top every turn.
// ops 01-13 go through the table at $08:CB0C. jump offsets are counted from the record's start too
public static class MonsterAi
{
    public sealed record Command(int Offset, int Op, byte[] Args, string Text, int? Jump, bool EndsTurn);

    // (argument bytes, name) of every op
    private static readonly (int Args, string Name, bool Ends)[] Ops =
    {
        (3, "Choose a party member to target", false),            // 01 $CBB3
        (3, "Choose a party member to target (from the back)", false), // 02 $CBE6
        (1, "Call another monster of its kind into an empty place", true), // 03 $CC18
        (3, "With a chance, go to", false),                        // 04 $CC58
        (1, "Attack", true),                                       // 05 $CC74
        (1, "Use item", true),                                     // 06 $CCB1 (item data via $08:F999)
        (1, "Cast spell (if it has the MP)", true),                // 07 $CD01 (spell data via $08:FA2D)
        (3, "Special attack", true),                               // 08 $CE25
        (2, "Go to", false),                                       // 09 $CC66
        (3, "If HP is above a share of max HP, go to", false),     // 0A $CE6A
        (0, "Guard", false),                                       // 0B $CF5B
        (0, "Target itself", false),                               // 0C $CF68
        (0, "Target a random monster on its side", false),         // 0D $CF6E
        (0, "Next action makes no sound", false),                  // 0E $CF8E
        (1, "Show battle message", false),                         // 0F $CF95
        (3, "If MP is above a share of max MP, go to", false),     // 10 $CE8B
        (0, "Run away", true),                                     // 11 $CEBB
        (2, "Set its HP", false),                                  // 12 $CEC8
        (2, "Set its MP", false),                                  // 13 $CEE6
        // our own (AiCommandsPatch), for every monster
        (3, "If its turn count is N or more, go to", false),       // 14
        (3, "Every N turns, go to", false),                        // 15
        (3, "If a party member's HP is below a share of max HP, go to", false), // 16
        (0, "Target the party member with the lowest HP", false),  // 17
        (0, "Target the party member with the highest HP", false), // 18
        (2, "If it is the only monster left, go to", false),       // 19
        (3, "If N or fewer party members are standing, go to", false), // 1A
        (1, "Switch on", false),                                   // 1B
        (1, "Switch off", false),                                  // 1C
        (3, "If a switch is on, go to", false),                    // 1D
        // Ok Impala's AI extensions (their 14-1A)
        (7, "If a party member check is true, go to", false),     // 1E who stat cmp val16 to
        (2, "Target the party member with the highest / lowest value", false), // 1F stat mode
        (2, "Set a number", false),                                // 20 n val
        (2, "Add to a number", false),                             // 21 n add
        (5, "If a number compares, go to", false),                 // 22 n cmp val to
        (2, "Bonus damage this turn", false),                      // 23 mode pct
        (3, "Set a number from the party", false),                 // 24 n who stat
        (1, "Take several actions this turn", false),             // 25 n (Ok Impala's 1B)
    };

    // commands 14 and up are ours, they need AiCommandsPatch in the rom
    public static bool IsNew(int op) => op >= AiCommandsPatch.First && op < AiCommandsPatch.First + AiCommandsPatch.Count;

    // argument bytes of a command (-1 for a number that isn't a command)
    public static int ArgCount(int op) => op >= 1 && op <= Ops.Length ? Ops[op - 1].Args : op == 0 ? 0 : -1;

    // where a command keeps its "go to" word (an argument index), or -1
    public static int JumpArg(int op) => op switch
    {
        0x09 or 0x19 => 0,
        0x04 or 0x0A or 0x10 or 0x14 or 0x15 or 0x16 or 0x1A or 0x1D => 1,
        0x22 => 3,
        0x1E => 5,
        _ => -1,
    };

    // does the command pick the monster's action (its turn ends there)
    public static bool EndsTurn(int op) => op >= 1 && op <= Ops.Length && Ops[op - 1].Ends;

    public static string Name(int op) => op == 0 ? "End of the script" : op >= 1 && op <= Ops.Length ? Ops[op - 1].Name : $"Command {op:X2}";

    // the command in plain words. step turns a jump offset into a step number.
    // from the handlers: 0A/10 compare max HP/MP x share / 256 with the current HP/MP (battle record +11/+13 and
    // +42/+44) and jump while the current value is above it. 12/13 set the current HP/MP
    public static string Plain(Command command, Func<int, string> step, Func<int, string>? spellName = null, Func<int, string>? itemName = null)
    {
        var args = command.Args;
        string Pct(int share) => $"{share * 100 / 256}%";
        int Word(int i) => args[i] | args[i + 1] << 8;
        return command.Op switch
        {
            0x00 => "End of the script.",
            0x01 => "Pick a random party member to target.",
            0x02 => "Pick a party member to target (another way of choosing).",
            0x03 => "Call for help: another monster of its kind joins in an empty spot." + (args[0] != 0 ? $" (Shows battle message {args[0]:X2}.)" : ""),
            0x04 => $"{Pct(args[0])} of the time, skip to {step(Word(1))}. Otherwise carry on.",
            0x05 => "Attack." + (args[0] != 0 ? $" (Shows battle message {args[0]:X2}.)" : ""),
            0x06 => $"Use {itemName?.Invoke(args[0]) ?? $"item {args[0]:X2}"}.",
            0x07 => $"Cast {spellName?.Invoke(args[0]) ?? $"spell {args[0]:X2}"}. If it doesn't have enough MP, carry on to the next step instead.",
            0x08 => $"Special attack {args[0]:X2} with power {args[1]}." + (args[2] != 0 ? $" (Shows battle message {args[2]:X2}.)" : ""),
            0x09 => $"Go to {step(Word(0))}.",
            0x0A => $"If its HP is still above {Pct(args[0])} of its max HP, go to {step(Word(1))}. Otherwise carry on.",
            0x0B => "Guard (defend).",
            0x0C => "Target itself.",
            0x0D => "Target a random monster on its own side.",
            0x0E => "Make its next action silent (no sound).",
            0x0F => $"Show battle message {args[0]:X2}.",
            0x10 => $"If its MP is still above {Pct(args[0])} of its max MP, go to {step(Word(1))}. Otherwise carry on.",
            0x11 => "Run away.",
            0x12 => $"Set its HP to {Word(0)}.",
            0x13 => $"Set its MP to {Word(0)}.",
            0x14 => $"If this is its turn {args[0]} or later, go to {step(Word(1))}.",
            0x15 => args[0] == 0 ? "Every 0 turns: never." : $"Every {args[0]} turns (turn {args[0]}, {args[0] * 2}, {args[0] * 3}...), go to {step(Word(1))}.",
            0x16 => $"If a party member's HP is below {Pct(args[0])} of their max HP, go to {step(Word(1))}.",
            0x17 => "Target the party member with the lowest HP.",
            0x18 => "Target the party member with the highest HP.",
            0x19 => $"If it is the only monster left, go to {step(Word(0))}.",
            0x1A => $"If {args[0]} or fewer party members are still standing, go to {step(Word(1))}.",
            0x1B => $"Switch {args[0] & 7} on (it stays on for the rest of the battle).",
            0x1C => $"Switch {args[0] & 7} off.",
            0x1D => $"If switch {args[0] & 7} is on, go to {step(Word(1))}.",
            0x1E => $"If {Who(args[0])}: {Stat(args[1])} {Cmp(args[2])} {Word(3)}, go to {step(Word(5))}.",
            0x1F => $"Target the standing party member with the {(args[1] == 0 ? "highest" : "lowest")} {Stat(args[0])}.",
            0x20 => $"Number {args[0] & 3} = {args[1]}.",
            0x21 => (sbyte)args[1] >= 0 ? $"Add {args[1]} to number {args[0] & 3} (up to 255)." : $"Take {-(sbyte)args[1]} from number {args[0] & 3} (down to 0).",
            0x22 => $"If number {args[0] & 3} {Cmp(args[1])} {args[2]}, go to {step(Word(3))}.",
            0x23 => args[1] == 0 ? "No bonus damage this turn." : $"This turn its hits do {args[1]}% of the target's {(args[0] == 0 ? "max" : "current")} HP more (up to 9999).",
            0x24 => $"Number {args[0] & 3} = {Stat(args[2])} of {WhoValue(args[1])}.",
            0x25 => args[0] <= 1 ? "One action this turn (as usual)." : $"{Math.Min((int)args[0], 8)} actions this turn: after each one it acts again at once, carrying on from the next line.",
            _ => command.Text,
        } + (command.EndsTurn && command.Op != 0x07 ? "  -> its turn ends here." : "");
    }

    // who a party check (1E / 24) looks at
    public static readonly (int Value, string Name)[] WhoChoices =
    {
        (0x00, "Hero (Maxim in the prologue)"), (0x01, "Lufia (Selan)"), (0x02, "Aguro (Guy)"), (0x03, "Jerin (Artea)"),
        (0x10, "any standing party member"), (0x11, "every standing party member"), (0x12, "its current target"), (0x13, "how many are standing"),
    };

    // the same for command 24 (10 / 11 give the highest / lowest value)
    public static readonly (int Value, string Name)[] WhoValueChoices =
    {
        (0x00, "Hero (Maxim in the prologue)"), (0x01, "Lufia (Selan)"), (0x02, "Aguro (Guy)"), (0x03, "Jerin (Artea)"),
        (0x10, "the highest among standing members"), (0x11, "the lowest among standing members"), (0x12, "its current target"), (0x13, "how many are standing"),
    };

    public static readonly string[] StatChoices = { "level", "HP", "max HP", "MP", "max MP", "HP %", "status bits", "standing (1 / 0)" };
    public static readonly string[] CmpChoices = { "=", "is not", "<", "<=", ">", ">=", "has any of the bits", "has none of the bits" };

    private static string Who(int value) => WhoChoices.FirstOrDefault(c => c.Value == value).Name ?? $"who {value:X2}";
    private static string WhoValue(int value) => WhoValueChoices.FirstOrDefault(c => c.Value == value).Name ?? $"who {value:X2}";
    private static string Stat(int value) => value < StatChoices.Length ? StatChoices[value] : $"stat {value}";
    private static string Cmp(int value) => value < CmpChoices.Length ? CmpChoices[value] : $"compare {value}";

    public static int ScriptOffset(RomBuffer rom, int record) => rom.ReadUInt16Le(record + 0x22);

    // the commands of a monster's script (following jumps to code outside the straight line too)
    public const int PointerTable = 0x5800, MonsterCount = 165;
    public static int RecordOffset(RomBuffer rom, int id) => MonsterTable.RecordOffset(rom, id);

    // file offset of record-relative offset rel. the game adds them as 16-bit numbers in
    // bank 00, so a script can be anywhere in the bank (even before its record)
    public static int At(int record, int rel) => (record + rel) & 0xFFFF;

    // where a monster's script bytes are: in bank 0 (offsets from its record) or in Lufia Forge's script
    // space (AiFarScripts, offsets from the 00 in front of the script, which starts at 1)
    public readonly record struct Home(int Base, int Start, bool Far)
    {
        public int At(int rel) => Far ? Base + rel : MonsterAi.At(Base, rel);
        public bool Inside(int rel) => Far ? rel >= 1 && rel < AiFarScripts.BufferSize : MonsterAi.At(Base, rel) < 0x8000;
    }

    // the monster id whose record is at record, or -1
    public static int IdOf(RomBuffer rom, int record)
    {
        for (int id = 0, count = MonsterTable.Count(rom); id < count; id++) if (RecordOffset(rom, id) == record) return id;
        return -1;
    }

    public static Home HomeOf(RomBuffer rom, int record) =>
        AiFarScripts.Count(rom) > 0 && IdOf(rom, record) is var id && id >= 0 ? HomeOf(rom, record, id) : new Home(record, ScriptOffset(rom, record), false);

    // HomeOf(RomBuffer, int) for when the monster's id is known
    public static Home HomeOf(RomBuffer rom, int record, int id) =>
        AiFarScripts.Base(rom, id) is int farBase ? new Home(farBase, 1, true) : new Home(record, ScriptOffset(rom, record), false);

    // file offset of byte rel of the monster's script (offsets the way Decode gives them)
    public static int ByteAt(RomBuffer rom, int record, int rel) => HomeOf(rom, record).At(rel);

    // every monster's script commands, with the record they belong to
    public static IEnumerable<(int Record, Command Cmd)> AllCommands(RomBuffer rom)
    {
        for (int id = 0, count = MonsterTable.Count(rom); id < count; id++)
        {
            int record = RecordOffset(rom, id);
            foreach (var command in Decode(rom, record)) yield return (record, command);
        }
    }

    // unused filler at the end of bank 0 after the last monster record: room for new or longer scripts
    // (a script has to sit after its record in the same bank, since offsets count from the record)
    public const int FreeStart = 0x7DD0, FreeEnd = 0x7FB0;

    // (start, length) of a monster's script, offsets from its record. (0, 0) when it has none
    public static (int Start, int Length) ScriptRange(RomBuffer rom, int record)
    {
        var commands = Decode(rom, record);
        if (commands.Count == 0) return (0, 0);
        int start = commands.Min(c => c.Offset);
        return (start, commands.Max(c => c.Offset + 1 + c.Args.Length) - start);
    }

    // a script that casts spells. each (percent, spell) is the share of turns that try that spell (a monster without
    // enough MP attacks instead), the rest of the turns it attacks. built for a script starting at record offset
    // start: chance steps (04), then attack (05), then cast steps (07, each followed by an attack)
    public static byte[] BuildSpellScript(IReadOnlyList<(int Percent, int Spell)> spells, int start)
    {
        var bytes = new List<byte>();
        int count = spells.Count, castsStart = start + count * 4 + 2;
        double percentLeft = 100;
        for (int i = 0; i < count; i++)
        {
            double percent = Math.Clamp(spells[i].Percent, 0, 100);
            int chance = percentLeft <= 0 ? 0 : (int)Math.Round(Math.Min(1, percent / percentLeft) * 256);
            percentLeft -= percent;
            int target = castsStart + i * 4;
            bytes.AddRange(new[] { (byte)0x04, (byte)Math.Clamp(chance, 0, 255), (byte)target, (byte)(target >> 8) });
        }
        bytes.AddRange(new byte[] { 0x05, 0x00 });
        foreach (var (_, spell) in spells) bytes.AddRange(new byte[] { 0x07, (byte)spell, 0x05, 0x00 });
        return bytes.ToArray();
    }

    public static int SpellScriptLength(int spells) => spells * 8 + 2;

    // gives monster id a spell casting script (see BuildSpellScript): in place when
    // it fits in its old script, otherwise in the free space at the end of bank 0, and when that's used up in
    // Lufia Forge's script space (AiFarScripts). returns where it went
    public static int WriteSpellScript(RomBuffer rom, int id, IReadOnlyList<(int Percent, int Spell)> spells)
    {
        int record = RecordOffset(rom, id);
        if (HomeOf(rom, record).Far) return AiFarScripts.Save(rom, id, record, BuildSpellScript(spells, 1));
        var (oldStart, oldLen) = ScriptRange(rom, record);
        int length = SpellScriptLength(spells.Count);
        int scriptOffset;
        // in place when it fits and no other monster uses the same bytes (same as AiScript.Write: a hack's monsters
        // can share a script, and writing here used to change the other one's too)
        if (oldLen >= length && !MonsterTable.Overlaps(rom, At(record, oldStart), oldLen, id)) scriptOffset = At(record, oldStart);
        else
        {
            // free space no other monster's record or script uses
            scriptOffset = MonsterTable.FindSpace(rom, length, MonsterTable.ScriptAreas, skipScriptOf: id);
            if (scriptOffset < 0)
            {
                if (!AiFarScripts.CanInstall(rom)) throw new InvalidOperationException("No room left for a longer monster script (the free space in bank 0 is used up). Use fewer spells.");
                return AiFarScripts.Save(rom, id, record, BuildSpellScript(spells, 1));
            }
        }
        int relativeStart = (scriptOffset - record) & 0xFFFF;
        var script = BuildSpellScript(spells, relativeStart);
        rom.WriteBytes(scriptOffset, script);
        if (oldLen > length && scriptOffset == At(record, oldStart)) rom.WriteBytes(scriptOffset + length, Enumerable.Repeat((byte)0x00, oldLen - length).ToArray());
        rom.WriteUInt16Le(record + 0x22, (ushort)relativeStart);
        return scriptOffset;
    }

    public static List<Command> Decode(RomBuffer rom, int record, Func<int, string>? spellName = null, Func<int, string>? itemName = null)
    {
        // a script with a command 25 (several actions a turn) keeps going after an action, so read all of it that way.
        // found only during the walk, a step right after an action seen before the 25 got left out, and saving the
        // script dropped it
        var list = Walk(rom, record, spellName, itemName, false);
        return list.Any(c => c.Op == 0x25) ? Walk(rom, record, spellName, itemName, true) : list;
    }

    private static List<Command> Walk(RomBuffer rom, int record, Func<int, string>? spellName, Func<int, string>? itemName, bool multiAll)
    {
        var list = new List<Command>();
        var home = HomeOf(rom, record);
        int start = home.Start;
        if (start == 0) return list;
        var todo = new Queue<int>(); todo.Enqueue(start);
        var seen = new HashSet<int>();
        bool multi = multiAll;   // a command 25 (several actions a turn): the script goes on after an action
        while (todo.Count > 0 && list.Count < 200)
        {
            int position = todo.Dequeue();
            while (home.Inside(position) && seen.Add(position))
            {
                int op = rom.ReadByte(home.At(position));
                if (op == 0) { list.Add(new Command(position, 0, Array.Empty<byte>(), "End", null, false)); break; }
                if (op > Ops.Length)
                {
                    // after a last action the script just ends (what follows is the next record)
                    if (list.Count == 0 || !list[^1].EndsTurn || list[^1].Offset + 1 + list[^1].Args.Length != position)
                        list.Add(new Command(position, op, Array.Empty<byte>(), $"Unknown command {op:X2}", null, false));
                    break;
                }
                var (argCount, name, ends) = Ops[op - 1];
                var args = Enumerable.Range(1, argCount).Select(i => rom.ReadByte(home.At(position + i))).ToArray();
                int? jump = null;
                string text = name;
                switch (op)
                {
                    case 0x04: jump = args[1] | args[2] << 8; text = $"With chance {args[0]}/256 ({args[0] * 100 / 256}%), go to +{jump:X2}"; break;
                    case 0x09: jump = args[0] | args[1] << 8; text = $"Go to +{jump:X2}"; break;
                    case 0x0A: jump = args[1] | args[2] << 8; text = $"If HP > max HP x {args[0]}/256, go to +{jump:X2}"; break;
                    case 0x10: jump = args[1] | args[2] << 8; text = $"If MP > max MP x {args[0]}/256, go to +{jump:X2}"; break;
                    case 0x05: text = args[0] == 0 ? "Attack" : $"Attack (message {args[0]:X2})"; break;
                    case 0x06: text = $"Use {itemName?.Invoke(args[0]) ?? $"item {args[0]:X2}"}"; break;
                    case 0x07: text = $"Cast {spellName?.Invoke(args[0]) ?? $"spell {args[0]:X2}"} (if it has the MP)"; break;
                    case 0x08: text = $"Special attack {args[0]:X2} (power {args[1]:X2})" + (args[2] != 0 ? $", message {args[2]:X2}" : ""); break;
                    case 0x0F: text = $"Show battle message {args[0]:X2}"; break;
                    case 0x03: text = $"Call another monster of its kind (message {args[0]:X2})"; break;
                    case 0x12: case 0x13: text = $"{name}: {args[0] | args[1] << 8}"; break;
                    default:
                        if (JumpArg(op) is int jumpArg and >= 0) { jump = args[jumpArg] | args[jumpArg + 1] << 8; text = $"{name} +{jump:X2}"; }
                        break;
                }
                list.Add(new Command(position, op, args, text, jump, ends));
                if (jump is int target && target > 0 && !seen.Contains(target)) todo.Enqueue(target);
                if (op == 0x09) break;   // unconditional jump, the straight line ends here
                if (op == 0x25) multi = true;
                if (ends && op != 0x07 && !multi) break;   // the action is picked so the script stops (07 carries on without the MP)
                position = (position + 1 + argCount) & 0xFFFF;
            }
        }
        return list.OrderBy(c => c.Offset).ToList();
    }
}
