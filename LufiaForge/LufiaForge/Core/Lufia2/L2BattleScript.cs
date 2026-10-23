using System.Text;

namespace LufiaForge.Core.Lufia2;

/// <summary>
/// Lufia II battle scripts (monster AI, item and spell effects; handlers at $85:B483, opcodes 00-5C, all 828 scripts
/// parse). A command byte, then its arguments; jump targets are u16 offsets from the start of the record that owns
/// the script. 42 w calls shared script w (library at $96:FADD), 43 returns. A script ends at 00 or 4F (43 in a
/// library script) once no jump goes further; 02, 31, 33, 34 and 38-3B aren't commands.
/// </summary>
public static class L2BattleScript
{
    private static readonly Dictionary<int, (int Args, string Name)> Ops = Build();

    private static Dictionary<int, (int, string)> Build()
    {
        var o = new Dictionary<int, (int, string)>();
        void S(int n, string name, params int[] ops) { foreach (var op in ops) o[op] = (n, name); }
        S(0, "end (do the queued action)", 0x00); S(0, "do the action, go on", 0x01); S(2, "go to", 0x03); S(2, "if that failed go to", 0x04);
        S(3, "with chance b/256 go to", 0x05);
        S(5, "if reg == value go to", 0x06); S(5, "if reg != value go to", 0x07); S(5, "if reg > value go to", 0x08);
        S(5, "if reg < value go to", 0x09); S(5, "if reg >= value go to", 0x0A); S(5, "if reg <= value go to", 0x0B);
        S(3, "reg =", 0x0C); S(3, "reg +=", 0x0D); S(3, "reg -=", 0x0E); S(3, "reg *=", 0x0F); S(3, "reg /=", 0x10); S(3, "reg /= (unsigned)", 0x55);
        S(1, "reg = random below reg", 0x11); S(2, "reg = target's stat", 0x13); S(2, "target's stat = reg", 0x14); S(2, "reg = own stat", 0x15);
        S(3, "reg &=", 0x16); S(3, "reg |=", 0x17); S(3, "reg ^=", 0x18); S(1, "reg = -reg", 0x1A);
        S(1, "show attack name", 0x1E); S(2, "element", 0x1F); S(2, "critical chance, multiplier", 0x20);
        S(5, "physical damage", 0x21); S(5, "magic damage", 0x22); S(2, "Bunny sword effect", 0x23); S(4, "restore", 0x24);
        S(2, "stat up/down %", 0x25); S(2, "cure status %", 0x26); S(2, "cause status %", 0x27);
        S(0, "attack", 0x28); S(0, "defend", 0x29); S(0, "flee", 0x2A); S(2, "use item", 0x2B); S(1, "cast spell", 0x2C); S(1, "call monster", 0x2D);
        S(1, "target", 0x32); S(1, "special effect / message", 0x35); S(0, "weapon attack", 0x37); S(0, "wait for the effect", 0x3C);
        S(1, "use capsule attack", 0x3E); S(3, "if attack slot learned go to", 0x3F); S(1, "reg = learned attacks", 0x40); S(0, "check / wait (action 0C)", 0x41); S(2, "call library script", 0x42); S(0, "return", 0x43); S(2, "effect timer, timed effect", 0x47);
        S(0, "quit (do nothing)", 0x4F); S(0, "Dark reflector", 0x51); S(1, "cast spell (IP)", 0x54); S(2, "message timer", 0x56);
        S(3, "item boosts this spell", 0x57); S(0, "Eerie light", 0x58); S(1, "battle animation", 0x5A); S(0, "lose if it kills itself", 0x5B);
        S(0, "hide damage number", 0x5C);
        S(2, "reg = ratio of reg, reg", 0x12); S(1, "reg = abs(reg)", 0x19); S(1, "reg = sign(reg)", 0x1B); S(1, "reg = battle counter", 0x1C);
        S(0, "do the effect step", 0x1D); S(0, "action 0D", 0x2E); S(1, "reg = combatants flagged 04", 0x2F); S(1, "reg = side count", 0x30);
        S(1, "if that failed: special effect", 0x36); S(1, "sound effect", 0x3D); S(2, "battle byte = value", 0x44); S(2, "reg = battle byte", 0x45);
        S(3, "if battle byte is 0 go to", 0x46); S(0, "$85:9671", 0x48); S(0, "wait for the effect timer", 0x49); S(1, "flash / shake times", 0x4A);
        S(0, "screen effect 4", 0x4B); S(1, "screen effect", 0x4C); S(0, "drop targets that can't be hit", 0x4D); S(2, "reg = attacker's stat", 0x4E);
        S(0, "keep only allowed targets", 0x50); S(0, "reflect flag 4", 0x52); S(1, "action 9", 0x53); S(0, "$85:DA9C", 0x59);
        return o;
    }

    /// <summary>A readable listing of the script at <paramref name="at"/>; <paramref name="record"/> is the start
    /// of the record that owns it (jumps count from there).</summary>
    public static string Disassemble(RomBuffer rom, int at, int record, string[]? spellNames = null, string[]? monsterNames = null, string[]? capsuleAttacks = null)
    {
        var sb = new StringBuilder();
        int p = at, furthest = at;
        for (int guard = 0; guard < 300 && p < rom.Length; guard++)
        {
            byte op = rom.ReadByte(p);
            if (!Ops.TryGetValue(op, out var def)) { sb.AppendLine($"{p - record:X4}  {op:X2}        (command not known: stops here)"); break; }
            var args = rom.ReadBytes(p + 1, def.Args);
            string text = def.Name;
            int target = -1;
            switch (op)
            {
                case 0x03 or 0x04: target = args[0] | args[1] << 8; text += $" {target:X4}"; break;
                case 0x46: target = args[1] | args[2] << 8; text = $"if battle byte {args[0]:X2} is 0 go to {target:X4}"; break;
                case 0x42: text = $"call library script {args[0] | args[1] << 8:X2}"; break;
                case 0x05: target = args[1] | args[2] << 8; text = $"with chance {args[0]}/256 go to {target:X4}"; break;
                case >= 0x06 and <= 0x0B: target = args[3] | args[4] << 8; text = text.Replace("reg", $"reg {args[0]:X2}").Replace("value", Operand(args[1], args[2])) + $" {target:X4}"; break;
                case >= 0x0C and <= 0x10 or >= 0x16 and <= 0x18 or 0x55: text = text.Replace("reg", $"reg {args[0]:X2}") + " " + Operand(args[1], args[2]); break;
                case 0x2C or 0x54 when spellNames != null && args[0] < spellNames.Length: text += $" {spellNames[args[0]]}"; break;
                case 0x3E when capsuleAttacks != null && args[0] < capsuleAttacks.Length: text += $" {capsuleAttacks[args[0]]}"; break;
                case 0x3F: target = args[1] | args[2] << 8; text = $"if attack slot {args[0]} is learned go to {target:X4}"; break;
                case 0x2D when monsterNames != null && args[0] < monsterNames.Length: text += $" {monsterNames[args[0]]}"; break;
                case 0x24:
                    string what = args[0] switch { 0 => "HP", 1 => "MP", 2 => "max HP", 3 => "max MP", 4 => "ATP", 5 => "DFP", 6 => "STR", 7 => "AGL", 8 => "INT", 9 => "GUT", 10 => "MGR", _ => $"stat {args[0]:X2}" };
                    text = $"restore {what} by {args[1] | args[2] << 8} (give or take {args[3]})";
                    break;
                default: if (args.Length > 0) text += " " + string.Join(" ", args.Select(b => b.ToString("X2"))); break;
            }
            sb.AppendLine($"{p - record:X4}  {text}");
            if (target >= 0) furthest = Math.Max(furthest, record + target);
            p += 1 + def.Args;
            if (op is 0x00 or 0x43 or 0x4F && p > furthest) break;
        }
        return sb.ToString().TrimEnd();
    }

    /// <summary>A value operand: 80 in the high byte means register lo, otherwise a number.</summary>
    private static string Operand(byte lo, byte hi) => hi == 0x80 ? $"reg {lo:X2}" : $"{lo | hi << 8}";
}
