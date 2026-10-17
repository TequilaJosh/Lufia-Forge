using System.Globalization;
using System.Text.RegularExpressions;

namespace LufiaForge.Core.Battle;

/// <summary>
/// Assembly text for <see cref="Asm65816"/>, in the style of asar (so routines from other hackers can be ported
/// almost as they are): one statement per line or several separated by " : ", <c>; comments</c>, <c>Label:</c>,
/// local <c>.labels</c> (inside the last global label), anonymous <c>-</c> / <c>+</c> / <c>++</c> labels,
/// <c>!name = value</c> defines, <c>db</c> / <c>dw</c>, and <c>.b</c> / <c>.w</c> / <c>.l</c> size suffixes.
/// Operand sizes follow asar: <c>$12</c> is direct page, <c>$1234</c> absolute, <c>$7E1234</c> long; an immediate
/// is as wide as it is written (<c>#$00</c> 8-bit, <c>#$0000</c> 16-bit) unless a suffix says otherwise.
/// Labels in operands are 16-bit addresses in the block (JSL / JML to a label: 24-bit).
/// </summary>
public sealed partial class Asm65816
{
    private enum Mode { Imp, Acc, Imm, Dp, DpX, DpY, Abs, AbsX, AbsY, Long, LongX, IndDp, IndDpX, IndDpY, IndLDp, IndLDpY, Sr, SrY, Rel, IndAbs, IndAbsX, IndLAbs }

    private static readonly Dictionary<(string, Mode), byte> Ops = BuildOps();
    private static readonly HashSet<string> Branches = new() { "BPL", "BMI", "BVC", "BVS", "BRA", "BCC", "BCS", "BNE", "BEQ" };
    private static readonly HashSet<string> IndexImm = new() { "LDX", "LDY", "CPX", "CPY" };

    private readonly Dictionary<string, string> _defines = new();
    private readonly Dictionary<string, string> _anonBack = new(), _anonFwd = new();
    private string _scope = "";
    private int _anon;

    private static Dictionary<(string, Mode), byte> BuildOps()
    {
        var d = new Dictionary<(string, Mode), byte>();
        var alu = new[] { ("ORA", 0x00), ("AND", 0x20), ("EOR", 0x40), ("ADC", 0x60), ("STA", 0x80), ("LDA", 0xA0), ("CMP", 0xC0), ("SBC", 0xE0) };
        var aluModes = new[] { (Mode.IndDpX, 0x01), (Mode.Sr, 0x03), (Mode.Dp, 0x05), (Mode.IndLDp, 0x07), (Mode.Imm, 0x09), (Mode.Abs, 0x0D), (Mode.Long, 0x0F),
            (Mode.IndDpY, 0x11), (Mode.IndDp, 0x12), (Mode.SrY, 0x13), (Mode.DpX, 0x15), (Mode.IndLDpY, 0x17), (Mode.AbsY, 0x19), (Mode.AbsX, 0x1D), (Mode.LongX, 0x1F) };
        foreach (var (mn, b) in alu)
            foreach (var (m, o) in aluModes)
                if (!(mn == "STA" && m == Mode.Imm)) d[(mn, m)] = (byte)(b + o);
        void Add(string mn, params (Mode, int)[] ms) { foreach (var (m, o) in ms) d[(mn, m)] = (byte)o; }
        foreach (var (mn, b) in new[] { ("ASL", 0x00), ("ROL", 0x20), ("LSR", 0x40), ("ROR", 0x60) })
            Add(mn, (Mode.Acc, b + 0x0A), (Mode.Dp, b + 0x06), (Mode.Abs, b + 0x0E), (Mode.DpX, b + 0x16), (Mode.AbsX, b + 0x1E));
        Add("INC", (Mode.Acc, 0x1A), (Mode.Dp, 0xE6), (Mode.Abs, 0xEE), (Mode.DpX, 0xF6), (Mode.AbsX, 0xFE));
        Add("DEC", (Mode.Acc, 0x3A), (Mode.Dp, 0xC6), (Mode.Abs, 0xCE), (Mode.DpX, 0xD6), (Mode.AbsX, 0xDE));
        Add("BIT", (Mode.Imm, 0x89), (Mode.Dp, 0x24), (Mode.Abs, 0x2C), (Mode.DpX, 0x34), (Mode.AbsX, 0x3C));
        Add("TSB", (Mode.Dp, 0x04), (Mode.Abs, 0x0C)); Add("TRB", (Mode.Dp, 0x14), (Mode.Abs, 0x1C));
        Add("STZ", (Mode.Dp, 0x64), (Mode.DpX, 0x74), (Mode.Abs, 0x9C), (Mode.AbsX, 0x9E));
        Add("LDX", (Mode.Imm, 0xA2), (Mode.Dp, 0xA6), (Mode.Abs, 0xAE), (Mode.DpY, 0xB6), (Mode.AbsY, 0xBE));
        Add("LDY", (Mode.Imm, 0xA0), (Mode.Dp, 0xA4), (Mode.Abs, 0xAC), (Mode.DpX, 0xB4), (Mode.AbsX, 0xBC));
        Add("STX", (Mode.Dp, 0x86), (Mode.Abs, 0x8E), (Mode.DpY, 0x96)); Add("STY", (Mode.Dp, 0x84), (Mode.Abs, 0x8C), (Mode.DpX, 0x94));
        Add("CPX", (Mode.Imm, 0xE0), (Mode.Dp, 0xE4), (Mode.Abs, 0xEC)); Add("CPY", (Mode.Imm, 0xC0), (Mode.Dp, 0xC4), (Mode.Abs, 0xCC));
        Add("JMP", (Mode.Abs, 0x4C), (Mode.Long, 0x5C), (Mode.IndAbs, 0x6C), (Mode.IndAbsX, 0x7C), (Mode.IndLAbs, 0xDC));
        Add("JML", (Mode.Long, 0x5C), (Mode.IndLAbs, 0xDC));
        Add("JSR", (Mode.Abs, 0x20), (Mode.IndAbsX, 0xFC)); Add("JSL", (Mode.Long, 0x22));
        foreach (var (mn, o) in new[] { ("BPL", 0x10), ("BMI", 0x30), ("BVC", 0x50), ("BVS", 0x70), ("BRA", 0x80), ("BCC", 0x90), ("BCS", 0xB0), ("BNE", 0xD0), ("BEQ", 0xF0) })
            Add(mn, (Mode.Rel, o));
        Add("REP", (Mode.Imm, 0xC2)); Add("SEP", (Mode.Imm, 0xE2)); Add("PEA", (Mode.Abs, 0xF4)); Add("PEI", (Mode.IndDp, 0xD4));
        foreach (var (mn, o) in new[] { ("CLC", 0x18), ("SEC", 0x38), ("CLI", 0x58), ("SEI", 0x78), ("CLV", 0xB8), ("CLD", 0xD8), ("SED", 0xF8), ("NOP", 0xEA),
            ("XBA", 0xEB), ("XCE", 0xFB), ("TAX", 0xAA), ("TAY", 0xA8), ("TXA", 0x8A), ("TYA", 0x98), ("TXY", 0x9B), ("TYX", 0xBB), ("TXS", 0x9A), ("TSX", 0xBA),
            ("TCD", 0x5B), ("TDC", 0x7B), ("TCS", 0x1B), ("TSC", 0x3B), ("INX", 0xE8), ("INY", 0xC8), ("DEX", 0xCA), ("DEY", 0x88), ("PHA", 0x48), ("PLA", 0x68),
            ("PHX", 0xDA), ("PLX", 0xFA), ("PHY", 0x5A), ("PLY", 0x7A), ("PHB", 0x8B), ("PLB", 0xAB), ("PHD", 0x0B), ("PLD", 0x2B), ("PHK", 0x4B), ("PHP", 0x08),
            ("PLP", 0x28), ("RTS", 0x60), ("RTL", 0x6B), ("RTI", 0x40), ("WAI", 0xCB) })
            Add(mn, (Mode.Imp, o));
        return d;
    }

    /// <summary>Assembles <paramref name="text"/> into the block (labels can refer to ones made with the other methods).</summary>
    public Asm65816 Source(string text)
    {
        int lineNo = 0;
        foreach (var raw in text.Split('\n'))
        {
            lineNo++;
            string line = raw;
            int semi = line.IndexOf(';');
            if (semi >= 0) line = line[..semi];
            foreach (var part in Regex.Split(line, @"\s:\s"))
            {
                try { Statement(part.Trim()); }
                catch (Exception e) when (e is not InvalidOperationException { Data.Count: > 0 })
                {
                    var ex = new InvalidOperationException($"Line {lineNo} \"{raw.Trim()}\": {e.Message}", e);
                    ex.Data["line"] = lineNo;
                    throw ex;
                }
            }
        }
        return this;
    }

    private void Statement(string s)
    {
        if (s.Length == 0) return;
        var def = Regex.Match(s, @"^!(\w+)\s*=\s*(.+)$");
        if (def.Success) { _defines[def.Groups[1].Value] = def.Groups[2].Value.Trim(); return; }
        var anon = Regex.Match(s, @"^([+-]+)(\s+(.*))?$");
        if (anon.Success && (anon.Groups[3].Success || s.All(c => c is '+' or '-')))
        {
            string n = anon.Groups[1].Value;
            string label = n[0] == '+' && _anonFwd.Remove(n, out var pending) ? pending : $"@anon{_anon++}";
            Label(label);
            if (n[0] == '-') _anonBack[n] = label;
            Statement(anon.Groups[3].Value.Trim());
            return;
        }
        var glob = Regex.Match(s, @"^([A-Za-z_]\w*):(.*)$");
        if (glob.Success) { _scope = glob.Groups[1].Value; Label(_scope); Statement(glob.Groups[2].Value.Trim()); return; }
        if (Regex.IsMatch(s, @"^\.\w+$")) { Label(_scope + s); return; }

        s = Expand(s);
        var m = Regex.Match(s, @"^([A-Za-z]{2,3})(\.([bwl]))?\s*(.*)$");
        if (!m.Success) throw new FormatException("can't read this");
        string mn = m.Groups[1].Value.ToUpperInvariant();
        char size = m.Groups[3].Success ? char.ToLowerInvariant(m.Groups[3].Value[0]) : ' ';
        string arg = m.Groups[4].Value.Trim();

        if (mn is "DB" or "DW")
        {
            foreach (var item in arg.Split(',').Select(x => x.Trim()))
            {
                if (IsLabelRef(item)) { if (mn == "DB") throw new FormatException("db of a label"); Word(LabelName(item)); continue; }
                int iv = Eval(item, out _);
                B((byte)iv); if (mn == "DW") B((byte)(iv >> 8));
            }
            return;
        }

        if (Branches.Contains(mn)) { Br(Ops[(mn, Mode.Rel)], RefName(arg)); return; }

        Mode mode; string e = arg;
        Match g;
        if (arg.Length == 0) mode = Ops.ContainsKey((mn, Mode.Imp)) ? Mode.Imp : Mode.Acc;
        else if (arg is "A" or "a") mode = Mode.Acc;
        else if (arg.StartsWith('#')) { mode = Mode.Imm; e = arg[1..]; }
        else if ((g = Regex.Match(arg, @"^\((.+),\s*[sS]\)\s*,\s*[yY]$")).Success) { mode = Mode.SrY; e = g.Groups[1].Value; }
        else if ((g = Regex.Match(arg, @"^\((.+)\)\s*,\s*[yY]$")).Success) { mode = Mode.IndDpY; e = g.Groups[1].Value; }
        else if ((g = Regex.Match(arg, @"^\((.+),\s*[xX]\)$")).Success) { mode = mn is "JMP" or "JSR" ? Mode.IndAbsX : Mode.IndDpX; e = g.Groups[1].Value; }
        else if ((g = Regex.Match(arg, @"^\((.+)\)$")).Success) { mode = mn == "JMP" ? Mode.IndAbs : Mode.IndDp; e = g.Groups[1].Value; }
        else if ((g = Regex.Match(arg, @"^\[(.+)\]\s*,\s*[yY]$")).Success) { mode = Mode.IndLDpY; e = g.Groups[1].Value; }
        else if ((g = Regex.Match(arg, @"^\[(.+)\]$")).Success) { mode = mn is "JMP" or "JML" ? Mode.IndLAbs : Mode.IndLDp; e = g.Groups[1].Value; }
        else if ((g = Regex.Match(arg, @"^(.+),\s*[sS]$")).Success) { mode = Mode.Sr; e = g.Groups[1].Value; }
        else if ((g = Regex.Match(arg, @"^(.+),\s*([xXyY])$")).Success)
        {
            e = g.Groups[1].Value;
            bool x = g.Groups[2].Value is "x" or "X";
            int w = Width(e, size, mn);
            mode = w switch { 1 => x ? Mode.DpX : Mode.DpY, 2 => x ? Mode.AbsX : Mode.AbsY, _ => x ? Mode.LongX : throw new FormatException("long,Y doesn't exist") };
        }
        else
        {
            int w = Width(arg, size, mn);
            mode = w switch { 1 => Mode.Dp, 2 => Mode.Abs, _ => Mode.Long };
        }
        if (!Ops.TryGetValue((mn, mode), out byte op)) throw new FormatException($"{mn} has no {mode} mode");
        e = e.Trim();
        if (mode is Mode.Imp or Mode.Acc) { B(op); return; }

        if (IsLabelRef(e))
        {
            string l = LabelName(e);
            if (mode is Mode.Long or Mode.LongX) LongLabel(op, l);
            else if (mode is Mode.Abs or Mode.AbsX or Mode.AbsY or Mode.IndAbs or Mode.IndAbsX) Abs(op, l);
            else throw new FormatException("a label needs an absolute or long operand");
            return;
        }
        int v = Eval(e, out int hint);
        int bytes = mode switch
        {
            Mode.Imm => size == 'b' ? 1 : size == 'w' ? 2 : mn is "REP" or "SEP" ? 1 : hint > 0 ? Math.Min(hint, 2) : v > 0xFF ? 2 : 1,
            Mode.Dp or Mode.DpX or Mode.DpY or Mode.IndDp or Mode.IndDpX or Mode.IndDpY or Mode.IndLDp or Mode.IndLDpY or Mode.Sr or Mode.SrY => 1,
            Mode.Long or Mode.LongX => 3,
            _ => 2,
        };
        B(op);
        for (int i = 0; i < bytes; i++) B((byte)(v >> (8 * i)));
    }

    /// <summary>Operand width in bytes: the suffix, else a label = 2 (JSL/JML 3), else how the number is written.</summary>
    private int Width(string e, char size, string mn)
    {
        if (size == 'b') return 1;
        if (size == 'w') return 2;
        if (size == 'l') return 3;
        if (IsLabelRef(e.Trim())) return mn is "JSL" or "JML" ? 3 : 2;
        Eval(e, out int hint);
        if (hint == 0) throw new FormatException($"say how wide \"{e}\" is (.b / .w / .l)");
        return hint;
    }

    private string Expand(string s)
    {
        foreach (var k in _defines.Keys.OrderByDescending(k => k.Length)) s = s.Replace("!" + k, _defines[k]);
        return s;
    }

    private static bool IsLabelRef(string e) => Regex.IsMatch(e, @"^(\.?[A-Za-z_][\w]*|\.\w+|[+-]+)$") && !Regex.IsMatch(e, @"^[0-9]");

    private string LabelName(string e) => e.StartsWith('.') ? _scope + e : e[0] is '+' or '-' ? RefName(e) : e;

    private string RefName(string e)
    {
        e = e.Trim();
        if (e.All(c => c == '-')) return _anonBack.TryGetValue(e, out var b) ? b : throw new FormatException($"no {e} label before this");
        if (e.All(c => c == '+'))
        {
            if (!_anonFwd.TryGetValue(e, out var f)) _anonFwd[e] = f = $"@anon{_anon++}";
            return f;
        }
        return LabelName(e);
    }

    /// <summary>A number expression: terms ($hex, decimal, %binary) joined by + and -; hint = bytes the first term is written with.</summary>
    private static int Eval(string e, out int hint)
    {
        hint = 0;
        int total = 0, sign = 1; bool first = true;
        foreach (Match t in Regex.Matches(e.Replace(" ", ""), @"([+-]?)([^+-]+)"))
        {
            if (t.Groups[1].Value == "-") sign = -1; else sign = 1;
            string term = t.Groups[2].Value;
            int v; int h;
            if (term.StartsWith('$')) { v = int.Parse(term[1..], NumberStyles.HexNumber); h = (term.Length - 1 + 1) / 2; }
            else if (term.StartsWith('%')) { v = Convert.ToInt32(term[1..], 2); h = (term.Length - 1 + 7) / 8; }
            else if (Regex.IsMatch(term, @"^\d+$")) { v = int.Parse(term); h = 0; }
            else throw new FormatException($"can't work out \"{term}\" (labels can't be used in sums)");
            if (first) { hint = h; first = false; }
            total += sign * v;
        }
        return total;
    }
}
