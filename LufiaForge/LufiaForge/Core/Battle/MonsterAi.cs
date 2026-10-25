namespace LufiaForge.Core.Battle;

/// <summary>
/// Monster AI scripts, from the battle code at $08:CA97. A monster record's word at +$22 is the offset (from the
/// record's start) of its script; 0 = no script (it just attacks). The script is a list of commands [op][args],
/// ended by 00 or by a command that chooses the action; the battle runs it from the top each turn. Ops 01-13 go
/// through the table at $08:CB0C. Jump offsets are also counted from the record's start.
/// </summary>
public static class MonsterAi
{
    public sealed record Command(int Offset, int Op, byte[] Args, string Text, int? Jump, bool EndsTurn);

    /// <summary>(argument bytes, name) of ops 01-13.</summary>
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
        // Lufia Forge's own (AiCommandsPatch), for every monster
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

    /// <summary>Commands 14 and up are Lufia Forge's own: they need <see cref="AiCommandsPatch"/> in the ROM.</summary>
    public static bool IsNew(int op) => op >= AiCommandsPatch.First && op < AiCommandsPatch.First + AiCommandsPatch.Count;

    /// <summary>Argument bytes of a command (-1 for a number that isn't a command).</summary>
    public static int ArgCount(int op) => op >= 1 && op <= Ops.Length ? Ops[op - 1].Args : op == 0 ? 0 : -1;

    /// <summary>Where a command keeps its "go to" word (an argument index), or -1.</summary>
    public static int JumpArg(int op) => op switch
    {
        0x09 or 0x19 => 0,
        0x04 or 0x0A or 0x10 or 0x14 or 0x15 or 0x16 or 0x1A or 0x1D => 1,
        0x22 => 3,
        0x1E => 5,
        _ => -1,
    };

    /// <summary>Whether the command chooses the monster's action (its turn ends there).</summary>
    public static bool EndsTurn(int op) => op >= 1 && op <= Ops.Length && Ops[op - 1].Ends;

    /// <summary>A command's name.</summary>
    public static string Name(int op) => op == 0 ? "End of the script" : op >= 1 && op <= Ops.Length ? Ops[op - 1].Name : $"Command {op:X2}";

    /// <summary>
    /// The command in plain words. <paramref name="step"/> turns a jump offset into a step number.
    /// From the handlers: 0A/10 compare max HP/MP x share / 256 with the current HP/MP (battle record +11/+13 and
    /// +42/+44) and jump while the current value is above it; 12/13 set the current HP/MP.
    /// </summary>
    public static string Plain(Command c, Func<int, string> step, Func<int, string>? spellName = null, Func<int, string>? itemName = null)
    {
        var a = c.Args;
        string Pct(int share) => $"{share * 100 / 256}%";
        int Word(int i) => a[i] | a[i + 1] << 8;
        return c.Op switch
        {
            0x00 => "End of the script.",
            0x01 => "Pick a random party member to target.",
            0x02 => "Pick a party member to target (another way of choosing).",
            0x03 => "Call for help: another monster of its kind joins in an empty spot." + (a[0] != 0 ? $" (Shows battle message {a[0]:X2}.)" : ""),
            0x04 => $"{Pct(a[0])} of the time, skip to {step(Word(1))}. Otherwise carry on.",
            0x05 => "Attack." + (a[0] != 0 ? $" (Shows battle message {a[0]:X2}.)" : ""),
            0x06 => $"Use {itemName?.Invoke(a[0]) ?? $"item {a[0]:X2}"}.",
            0x07 => $"Cast {spellName?.Invoke(a[0]) ?? $"spell {a[0]:X2}"}. If it doesn't have enough MP, carry on to the next step instead.",
            0x08 => $"Special attack {a[0]:X2} with power {a[1]}." + (a[2] != 0 ? $" (Shows battle message {a[2]:X2}.)" : ""),
            0x09 => $"Go to {step(Word(0))}.",
            0x0A => $"If its HP is still above {Pct(a[0])} of its max HP, go to {step(Word(1))}. Otherwise carry on.",
            0x0B => "Guard (defend).",
            0x0C => "Target itself.",
            0x0D => "Target a random monster on its own side.",
            0x0E => "Make its next action silent (no sound).",
            0x0F => $"Show battle message {a[0]:X2}.",
            0x10 => $"If its MP is still above {Pct(a[0])} of its max MP, go to {step(Word(1))}. Otherwise carry on.",
            0x11 => "Run away.",
            0x12 => $"Set its HP to {Word(0)}.",
            0x13 => $"Set its MP to {Word(0)}.",
            0x14 => $"If this is its turn {a[0]} or later, go to {step(Word(1))}.",
            0x15 => a[0] == 0 ? "Every 0 turns: never." : $"Every {a[0]} turns (turn {a[0]}, {a[0] * 2}, {a[0] * 3}...), go to {step(Word(1))}.",
            0x16 => $"If a party member's HP is below {Pct(a[0])} of their max HP, go to {step(Word(1))}.",
            0x17 => "Target the party member with the lowest HP.",
            0x18 => "Target the party member with the highest HP.",
            0x19 => $"If it is the only monster left, go to {step(Word(0))}.",
            0x1A => $"If {a[0]} or fewer party members are still standing, go to {step(Word(1))}.",
            0x1B => $"Switch {a[0] & 7} on (it stays on for the rest of the battle).",
            0x1C => $"Switch {a[0] & 7} off.",
            0x1D => $"If switch {a[0] & 7} is on, go to {step(Word(1))}.",
            0x1E => $"If {Who(a[0])}: {Stat(a[1])} {Cmp(a[2])} {Word(3)}, go to {step(Word(5))}.",
            0x1F => $"Target the standing party member with the {(a[1] == 0 ? "highest" : "lowest")} {Stat(a[0])}.",
            0x20 => $"Number {a[0] & 3} = {a[1]}.",
            0x21 => (sbyte)a[1] >= 0 ? $"Add {a[1]} to number {a[0] & 3} (up to 255)." : $"Take {-(sbyte)a[1]} from number {a[0] & 3} (down to 0).",
            0x22 => $"If number {a[0] & 3} {Cmp(a[1])} {a[2]}, go to {step(Word(3))}.",
            0x23 => a[1] == 0 ? "No bonus damage this turn." : $"This turn its hits do {a[1]}% of the target's {(a[0] == 0 ? "max" : "current")} HP more (up to 9999).",
            0x24 => $"Number {a[0] & 3} = {Stat(a[2])} of {WhoValue(a[1])}.",
            0x25 => a[0] <= 1 ? "One action this turn (as usual)." : $"{Math.Min((int)a[0], 8)} actions this turn: after each one it acts again at once, carrying on from the next line.",
            _ => c.Text,
        } + (c.EndsTurn && c.Op != 0x07 ? "  -> its turn ends here." : "");
    }

    /// <summary>Who a party check (1E / 24) looks at.</summary>
    public static readonly (int Value, string Name)[] WhoChoices =
    {
        (0x00, "Hero (Maxim in the prologue)"), (0x01, "Lufia (Selan)"), (0x02, "Aguro (Guy)"), (0x03, "Jerin (Artea)"),
        (0x10, "any standing party member"), (0x11, "every standing party member"), (0x12, "its current target"), (0x13, "how many are standing"),
    };

    /// <summary>The same for command 24 (10 / 11 give the highest / lowest value).</summary>
    public static readonly (int Value, string Name)[] WhoValueChoices =
    {
        (0x00, "Hero (Maxim in the prologue)"), (0x01, "Lufia (Selan)"), (0x02, "Aguro (Guy)"), (0x03, "Jerin (Artea)"),
        (0x10, "the highest among standing members"), (0x11, "the lowest among standing members"), (0x12, "its current target"), (0x13, "how many are standing"),
    };

    public static readonly string[] StatChoices = { "level", "HP", "max HP", "MP", "max MP", "HP %", "status bits", "standing (1 / 0)" };
    public static readonly string[] CmpChoices = { "=", "is not", "<", "<=", ">", ">=", "has any of the bits", "has none of the bits" };

    private static string Who(int v) => WhoChoices.FirstOrDefault(c => c.Value == v).Name ?? $"who {v:X2}";
    private static string WhoValue(int v) => WhoValueChoices.FirstOrDefault(c => c.Value == v).Name ?? $"who {v:X2}";
    private static string Stat(int v) => v < StatChoices.Length ? StatChoices[v] : $"stat {v}";
    private static string Cmp(int v) => v < CmpChoices.Length ? CmpChoices[v] : $"compare {v}";

    public static int ScriptOffset(RomBuffer rom, int record) => rom.ReadUInt16Le(record + 0x22);

    /// <summary>The commands of a monster's script (following jumps to code outside the straight line too).</summary>
    public const int PointerTable = 0x5800, MonsterCount = 165;
    public static int RecordOffset(RomBuffer rom, int id) => MonsterTable.RecordOffset(rom, id);

    /// <summary>File offset of record-relative offset <paramref name="rel"/>: the game adds them as 16-bit numbers in
    /// bank 00, so a script can be anywhere in the bank (also before its record).</summary>
    public static int At(int record, int rel) => (record + rel) & 0xFFFF;

    /// <summary>Where a monster's script bytes are: in bank 0 (offsets from its record) or in Lufia Forge's script
    /// space (<see cref="AiFarScripts"/>, offsets from the 00 in front of the script, which starts at 1).</summary>
    public readonly record struct Home(int Base, int Start, bool Far)
    {
        public int At(int rel) => Far ? Base + rel : MonsterAi.At(Base, rel);
        public bool Inside(int rel) => Far ? rel >= 1 && rel < AiFarScripts.BufferSize : MonsterAi.At(Base, rel) < 0x8000;
    }

    /// <summary>The monster id whose record is at <paramref name="record"/>, or -1.</summary>
    public static int IdOf(RomBuffer rom, int record)
    {
        for (int m = 0, n = MonsterTable.Count(rom); m < n; m++) if (RecordOffset(rom, m) == record) return m;
        return -1;
    }

    public static Home HomeOf(RomBuffer rom, int record) =>
        AiFarScripts.Count(rom) > 0 && IdOf(rom, record) is var id && id >= 0 ? HomeOf(rom, record, id) : new Home(record, ScriptOffset(rom, record), false);

    /// <summary><see cref="HomeOf(RomBuffer, int)"/> when the monster's id is known.</summary>
    public static Home HomeOf(RomBuffer rom, int record, int id) =>
        AiFarScripts.Base(rom, id) is int b ? new Home(b, 1, true) : new Home(record, ScriptOffset(rom, record), false);

    /// <summary>File offset of byte <paramref name="rel"/> of the monster's script (offsets as <see cref="Decode"/> gives them).</summary>
    public static int ByteAt(RomBuffer rom, int record, int rel) => HomeOf(rom, record).At(rel);

    /// <summary>Every monster's script commands, with the record they belong to.</summary>
    public static IEnumerable<(int Record, Command Cmd)> AllCommands(RomBuffer rom)
    {
        for (int m = 0, n = MonsterTable.Count(rom); m < n; m++)
        {
            int r = RecordOffset(rom, m);
            foreach (var c in Decode(rom, r)) yield return (r, c);
        }
    }

    /// <summary>Unused filler at the end of bank 0, after the last monster record: room for new or longer scripts
    /// (a script must lie after its record, in the same bank, as offsets count from the record).</summary>
    public const int FreeStart = 0x7DD0, FreeEnd = 0x7FB0;

    /// <summary>(start, length) of a monster's script, offsets from its record; (0, 0) when it has none.</summary>
    public static (int Start, int Length) ScriptRange(RomBuffer rom, int record)
    {
        var cmds = Decode(rom, record);
        if (cmds.Count == 0) return (0, 0);
        int start = cmds.Min(c => c.Offset);
        return (start, cmds.Max(c => c.Offset + 1 + c.Args.Length) - start);
    }

    /// <summary>
    /// A script that casts spells: each (percent, spell) is the share of turns that try that spell (a monster without
    /// enough MP attacks instead); the rest of the turns it attacks. Built for a script starting at record offset
    /// <paramref name="at"/>: chance steps (04), then attack (05), then cast steps (07, each followed by an attack).
    /// </summary>
    public static byte[] BuildSpellScript(IReadOnlyList<(int Percent, int Spell)> spells, int at)
    {
        var b = new List<byte>();
        int k = spells.Count, casts = at + k * 4 + 2;
        double left = 100;
        for (int i = 0; i < k; i++)
        {
            double p = Math.Clamp(spells[i].Percent, 0, 100);
            int chance = left <= 0 ? 0 : (int)Math.Round(Math.Min(1, p / left) * 256);
            left -= p;
            int target = casts + i * 4;
            b.AddRange(new[] { (byte)0x04, (byte)Math.Clamp(chance, 0, 255), (byte)target, (byte)(target >> 8) });
        }
        b.AddRange(new byte[] { 0x05, 0x00 });
        foreach (var (_, sp) in spells) b.AddRange(new byte[] { 0x07, (byte)sp, 0x05, 0x00 });
        return b.ToArray();
    }

    public static int SpellScriptLength(int spells) => spells * 8 + 2;

    /// <summary>
    /// Gives monster <paramref name="id"/> a spell-casting script (see <see cref="BuildSpellScript"/>): in place when
    /// it fits in its old script, otherwise in the free space at the end of bank 0, and when that is used up in
    /// Lufia Forge's script space (<see cref="AiFarScripts"/>). Returns where it went.
    /// </summary>
    public static int WriteSpellScript(RomBuffer rom, int id, IReadOnlyList<(int Percent, int Spell)> spells)
    {
        int record = RecordOffset(rom, id);
        if (HomeOf(rom, record).Far) return AiFarScripts.Save(rom, id, record, BuildSpellScript(spells, 1));
        var (oldStart, oldLen) = ScriptRange(rom, record);
        int len = SpellScriptLength(spells.Count);
        int at;
        if (oldLen >= len) at = At(record, oldStart);
        else
        {
            // free space not used by any other monster's record or script
            at = MonsterTable.FindSpace(rom, len, MonsterTable.ScriptAreas, skipScriptOf: id);
            if (at < 0)
            {
                if (!AiFarScripts.CanInstall(rom)) throw new InvalidOperationException("No room left for a longer monster script (the free space in bank 0 is used up). Use fewer spells.");
                return AiFarScripts.Save(rom, id, record, BuildSpellScript(spells, 1));
            }
        }
        int rel = (at - record) & 0xFFFF;
        var script = BuildSpellScript(spells, rel);
        rom.WriteBytes(at, script);
        if (oldLen > len && at == At(record, oldStart)) rom.WriteBytes(at + len, Enumerable.Repeat((byte)0x00, oldLen - len).ToArray());
        rom.WriteUInt16Le(record + 0x22, (ushort)rel);
        return at;
    }

    public static List<Command> Decode(RomBuffer rom, int record, Func<int, string>? spellName = null, Func<int, string>? itemName = null)
    {
        var list = new List<Command>();
        var home = HomeOf(rom, record);
        int start = home.Start;
        if (start == 0) return list;
        var todo = new Queue<int>(); todo.Enqueue(start);
        var seen = new HashSet<int>();
        bool multi = false;   // a command 25 (several actions a turn): the script goes on after an action
        while (todo.Count > 0 && list.Count < 200)
        {
            int at = todo.Dequeue();
            while (home.Inside(at) && seen.Add(at))
            {
                int op = rom.ReadByte(home.At(at));
                if (op == 0) { list.Add(new Command(at, 0, Array.Empty<byte>(), "End", null, false)); break; }
                if (op > Ops.Length)
                {
                    // after a last action the script simply ends (what follows is the next record)
                    if (list.Count == 0 || !list[^1].EndsTurn || list[^1].Offset + 1 + list[^1].Args.Length != at)
                        list.Add(new Command(at, op, Array.Empty<byte>(), $"Unknown command {op:X2}", null, false));
                    break;
                }
                var (n, name, ends) = Ops[op - 1];
                var args = Enumerable.Range(1, n).Select(i => rom.ReadByte(home.At(at + i))).ToArray();
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
                        if (JumpArg(op) is int ja and >= 0) { jump = args[ja] | args[ja + 1] << 8; text = $"{name} +{jump:X2}"; }
                        break;
                }
                list.Add(new Command(at, op, args, text, jump, ends));
                if (jump is int j && j > 0 && !seen.Contains(j)) todo.Enqueue(j);
                if (op == 0x09) break;   // unconditional jump: the straight line ends here
                if (op == 0x25) multi = true;
                if (ends && op != 0x07 && !multi) break;   // the action is chosen: the script stops (07 carries on without the MP)
                at = (at + 1 + n) & 0xFFFF;
            }
        }
        return list.OrderBy(c => c.Offset).ToList();
    }
}
