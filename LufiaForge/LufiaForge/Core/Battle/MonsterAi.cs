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
        (1, "Cast spell", true),                                   // 06 $CCB1
        (1, "Use an ability (if it has the MP)", true),            // 07 $CD01
        (3, "Special attack", true),                               // 08 $CE25
        (2, "Go to", false),                                       // 09 $CC66
        (3, "If HP is below a share, go to", false),               // 0A $CE6A
        (0, "Guard", false),                                       // 0B $CF5B
        (0, "Target itself", false),                               // 0C $CF68
        (0, "Target a random monster on its side", false),         // 0D $CF6E
        (0, "Next action makes no sound", false),                  // 0E $CF8E
        (1, "Show battle message", false),                         // 0F $CF95
        (3, "If MP is below a share, go to", false),               // 10 $CE8B
        (0, "Run away", true),                                     // 11 $CEBB
        (2, "Set the HP check value", false),                      // 12 $CEC8
        (2, "Set the MP check value", false),                      // 13 $CEE6
    };

    public static int ScriptOffset(RomBuffer rom, int record) => rom.ReadUInt16Le(record + 0x22);

    /// <summary>The commands of a monster's script (following jumps to code outside the straight line too).</summary>
    public static List<Command> Decode(RomBuffer rom, int record, Func<int, string>? spellName = null)
    {
        var list = new List<Command>();
        int start = ScriptOffset(rom, record);
        if (start == 0) return list;
        var todo = new Queue<int>(); todo.Enqueue(start);
        var seen = new HashSet<int>();
        while (todo.Count > 0 && list.Count < 200)
        {
            int at = todo.Dequeue();
            while (at < 0x400 && seen.Add(at))
            {
                int op = rom.ReadByte(record + at);
                if (op == 0) { list.Add(new Command(at, 0, Array.Empty<byte>(), "End", null, false)); break; }
                if (op > Ops.Length)
                {
                    // after a last "use ability" the script simply ends (what follows is the next record)
                    if (list.Count == 0 || list[^1].Op != 0x07 || list[^1].Offset + 2 != at)
                        list.Add(new Command(at, op, Array.Empty<byte>(), $"Unknown command {op:X2}", null, false));
                    break;
                }
                var (n, name, ends) = Ops[op - 1];
                var args = Enumerable.Range(1, n).Select(i => rom.ReadByte(record + at + i)).ToArray();
                int? jump = null;
                string text = name;
                switch (op)
                {
                    case 0x04: jump = args[1] | args[2] << 8; text = $"With chance {args[0]}/256 ({args[0] * 100 / 256}%), go to +{jump:X2}"; break;
                    case 0x09: jump = args[0] | args[1] << 8; text = $"Go to +{jump:X2}"; break;
                    case 0x0A: jump = args[1] | args[2] << 8; text = $"If HP is below share {args[0]}, go to +{jump:X2}"; break;
                    case 0x10: jump = args[1] | args[2] << 8; text = $"If MP is below share {args[0]}, go to +{jump:X2}"; break;
                    case 0x05: text = args[0] == 0 ? "Attack" : $"Attack (message {args[0]:X2})"; break;
                    case 0x06: text = $"Cast {spellName?.Invoke(args[0]) ?? $"spell {args[0]:X2}"}"; break;
                    case 0x07: text = $"Use ability {args[0]:X2} (if it has the MP)"; break;
                    case 0x08: text = $"Special attack {args[0]:X2} (power {args[1]:X2})" + (args[2] != 0 ? $", message {args[2]:X2}" : ""); break;
                    case 0x0F: text = $"Show battle message {args[0]:X2}"; break;
                    case 0x03: text = $"Call another monster of its kind (message {args[0]:X2})"; break;
                    case 0x12: case 0x13: text = $"{name}: {args[0] | args[1] << 8}"; break;
                }
                list.Add(new Command(at, op, args, text, jump, ends));
                if (jump is int j && j > 0 && !seen.Contains(j)) todo.Enqueue(j);
                if (op == 0x09) break;   // unconditional jump: the straight line ends here
                if (ends && op != 0x07) break;   // the action is chosen: the script stops (07 carries on without the MP)
                at += 1 + n;
            }
        }
        return list.OrderBy(c => c.Offset).ToList();
    }
}
