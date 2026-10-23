using System.Text;

namespace LufiaForge.Core.Lufia2;

/// <summary>
/// Lufia II battle scripts (monster AI, item and spell effects): LEdit's L2BattleScript / L2_Effects.txt. A command
/// byte, then its arguments; jump targets are u16 offsets from the start of the record that owns the script. A script
/// ends at 00, 2A, 41 or 4F once no jump goes further.
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
        S(1, "target", 0x32); S(1, "special effect", 0x35); S(0, "weapon attack", 0x37); S(0, "wait for the effect", 0x3C);
        S(1, "use capsule attack", 0x3E); S(3, "if attack slot learned go to", 0x3F); S(1, "reg = learned attacks", 0x40); S(0, "wait", 0x41); S(2, "weakness / protection", 0x42); S(2, "IP: cast without MP", 0x47);
        S(0, "quit (do nothing)", 0x4F); S(0, "Dark reflector", 0x51); S(1, "cast spell (IP)", 0x54); S(2, "name shown for", 0x56);
        S(3, "item boosts this spell", 0x57); S(0, "Eerie light", 0x58); S(1, "battle animation", 0x5A); S(0, "lose if it kills itself", 0x5B);
        S(0, "hide damage number", 0x5C);
        // documented lengths, meaning unknown
        S(0, "?", 0x02, 0x1D, 0x2E, 0x50); S(1, "?", 0x19, 0x1B, 0x1C, 0x2F, 0x30, 0x3D, 0x4B, 0x4C);
        S(2, "?", 0x12, 0x44); S(3, "?", 0x46);
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
            if (op is 0x00 or 0x2A or 0x41 or 0x4F && p > furthest) break;
        }
        return sb.ToString().TrimEnd();
    }

    /// <summary>A value operand: 80 in the high byte means register lo, otherwise a number.</summary>
    private static string Operand(byte lo, byte hi) => hi == 0x80 ? $"reg {lo:X2}" : $"{lo | hi << 8}";
}
