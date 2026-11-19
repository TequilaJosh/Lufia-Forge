using System.Globalization;
using System.Text.RegularExpressions;

namespace LufiaForge.Core.Battle;

// assembly text for Asm65816, written like asar so routines from other hackers can be ported mostly as they are.
// one statement per line or several split by " : ", ; comments, Label:, local .labels (inside the last global
// label), anonymous - / + / ++ labels, !name = value defines, db / dw, and .b / .w / .l size suffixes.
// operand sizes follow asar: $12 is direct page, $1234 absolute, $7E1234 long. an immediate is as wide as it's
// written (#$00 8-bit, #$0000 16-bit) unless a suffix says otherwise.
// labels in operands are 16-bit addresses in the block (JSL / JML to a label is 24-bit)
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
        var ops = new Dictionary<(string, Mode), byte>();
        var alu = new[] { ("ORA", 0x00), ("AND", 0x20), ("EOR", 0x40), ("ADC", 0x60), ("STA", 0x80), ("LDA", 0xA0), ("CMP", 0xC0), ("SBC", 0xE0) };
        var aluModes = new[] { (Mode.IndDpX, 0x01), (Mode.Sr, 0x03), (Mode.Dp, 0x05), (Mode.IndLDp, 0x07), (Mode.Imm, 0x09), (Mode.Abs, 0x0D), (Mode.Long, 0x0F),
            (Mode.IndDpY, 0x11), (Mode.IndDp, 0x12), (Mode.SrY, 0x13), (Mode.DpX, 0x15), (Mode.IndLDpY, 0x17), (Mode.AbsY, 0x19), (Mode.AbsX, 0x1D), (Mode.LongX, 0x1F) };
        foreach (var (mnemonic, baseOpcode) in alu)
            foreach (var (mode, modeOffset) in aluModes)
                if (!(mnemonic == "STA" && mode == Mode.Imm)) ops[(mnemonic, mode)] = (byte)(baseOpcode + modeOffset);
        void Add(string mnemonic, params (Mode, int)[] modes) { foreach (var (mode, opcode) in modes) ops[(mnemonic, mode)] = (byte)opcode; }
        foreach (var (mnemonic, baseOpcode) in new[] { ("ASL", 0x00), ("ROL", 0x20), ("LSR", 0x40), ("ROR", 0x60) })
            Add(mnemonic, (Mode.Acc, baseOpcode + 0x0A), (Mode.Dp, baseOpcode + 0x06), (Mode.Abs, baseOpcode + 0x0E), (Mode.DpX, baseOpcode + 0x16), (Mode.AbsX, baseOpcode + 0x1E));
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
        foreach (var (mnemonic, opcode) in new[] { ("BPL", 0x10), ("BMI", 0x30), ("BVC", 0x50), ("BVS", 0x70), ("BRA", 0x80), ("BCC", 0x90), ("BCS", 0xB0), ("BNE", 0xD0), ("BEQ", 0xF0) })
            Add(mnemonic, (Mode.Rel, opcode));
        Add("REP", (Mode.Imm, 0xC2)); Add("SEP", (Mode.Imm, 0xE2)); Add("PEA", (Mode.Abs, 0xF4)); Add("PEI", (Mode.IndDp, 0xD4));
        foreach (var (mnemonic, opcode) in new[] { ("CLC", 0x18), ("SEC", 0x38), ("CLI", 0x58), ("SEI", 0x78), ("CLV", 0xB8), ("CLD", 0xD8), ("SED", 0xF8), ("NOP", 0xEA),
            ("XBA", 0xEB), ("XCE", 0xFB), ("TAX", 0xAA), ("TAY", 0xA8), ("TXA", 0x8A), ("TYA", 0x98), ("TXY", 0x9B), ("TYX", 0xBB), ("TXS", 0x9A), ("TSX", 0xBA),
            ("TCD", 0x5B), ("TDC", 0x7B), ("TCS", 0x1B), ("TSC", 0x3B), ("INX", 0xE8), ("INY", 0xC8), ("DEX", 0xCA), ("DEY", 0x88), ("PHA", 0x48), ("PLA", 0x68),
            ("PHX", 0xDA), ("PLX", 0xFA), ("PHY", 0x5A), ("PLY", 0x7A), ("PHB", 0x8B), ("PLB", 0xAB), ("PHD", 0x0B), ("PLD", 0x2B), ("PHK", 0x4B), ("PHP", 0x08),
            ("PLP", 0x28), ("RTS", 0x60), ("RTL", 0x6B), ("RTI", 0x40), ("WAI", 0xCB) })
            Add(mnemonic, (Mode.Imp, opcode));
        return ops;
    }

    // assembles text into the block (labels can point at ones made with the other methods)
    public Asm65816 Source(string text)
    {
        int lineNo = 0;
        foreach (var rawLine in text.Split('\n'))
        {
            lineNo++;
            string line = rawLine;
            int commentAt = line.IndexOf(';');
            if (commentAt >= 0) line = line[..commentAt];
            foreach (var part in Regex.Split(line, @"\s:\s"))
            {
                try { Statement(part.Trim()); }
                catch (Exception e) when (e is not InvalidOperationException { Data.Count: > 0 })
                {
                    var ex = new InvalidOperationException($"Line {lineNo} \"{rawLine.Trim()}\": {e.Message}", e);
                    ex.Data["line"] = lineNo;
                    throw ex;
                }
            }
        }
        return this;
    }

    private void Statement(string statement)
    {
        if (statement.Length == 0) return;
        var define = Regex.Match(statement, @"^!(\w+)\s*=\s*(.+)$");
        if (define.Success) { _defines[define.Groups[1].Value] = define.Groups[2].Value.Trim(); return; }
        var anon = Regex.Match(statement, @"^([+-]+)(\s+(.*))?$");
        if (anon.Success && (anon.Groups[3].Success || statement.All(c => c is '+' or '-')))
        {
            string signs = anon.Groups[1].Value;
            string label = signs[0] == '+' && _anonFwd.Remove(signs, out var pending) ? pending : $"@anon{_anon++}";
            Label(label);
            if (signs[0] == '-') _anonBack[signs] = label;
            Statement(anon.Groups[3].Value.Trim());
            return;
        }
        var globalLabel = Regex.Match(statement, @"^([A-Za-z_]\w*):(.*)$");
        if (globalLabel.Success) { _scope = globalLabel.Groups[1].Value; Label(_scope); Statement(globalLabel.Groups[2].Value.Trim()); return; }
        if (Regex.IsMatch(statement, @"^\.\w+$")) { Label(_scope + statement); return; }
        var localLabel = Regex.Match(statement, @"^(\.\w+):(.*)$");
        if (localLabel.Success) { Label(_scope + localLabel.Groups[1].Value); Statement(localLabel.Groups[2].Value.Trim()); return; }

        statement = Expand(statement);
        var instruction = Regex.Match(statement, @"^([A-Za-z]{2,3})(\.([bwl]))?\s*(.*)$");
        if (!instruction.Success) throw new FormatException("can't read this");
        string mnemonic = instruction.Groups[1].Value.ToUpperInvariant();
        char size = instruction.Groups[3].Success ? char.ToLowerInvariant(instruction.Groups[3].Value[0]) : ' ';
        string operand = instruction.Groups[4].Value.Trim();

        if (mnemonic is "DB" or "DW" or "DL")
        {
            foreach (var item in Regex.Matches(operand, @"""[^""]*""|[^,]+").Select(x => x.Value.Trim()))
            {
                if (item.StartsWith('"')) { if (mnemonic != "DB") throw new FormatException("text only in db"); B(System.Text.Encoding.ASCII.GetBytes(item.Trim('"'))); continue; }
                if (IsLabelRef(item))
                {
                    if (mnemonic == "DB") throw new FormatException("db of a label");
                    if (mnemonic == "DW") Word(LabelName(item)); else LongWord(LabelName(item));
                    continue;
                }
                int itemValue = Eval(item, out _);
                B((byte)itemValue);
                if (mnemonic != "DB") B((byte)(itemValue >> 8));
                if (mnemonic == "DL") B((byte)(itemValue >> 16));
            }
            return;
        }
        if (mnemonic == "BRL") { Br16(0x82, RefName(operand)); return; }
        // ASL #3, NOP #4 ...: the same one-byte instruction n times
        var repeat = Regex.Match(operand, @"^#(\d+)$");
        if (repeat.Success && !Ops.ContainsKey((mnemonic, Mode.Imm)) && (Ops.ContainsKey((mnemonic, Mode.Imp)) || Ops.ContainsKey((mnemonic, Mode.Acc))))
        {
            byte oneByteOp = Ops.TryGetValue((mnemonic, Mode.Imp), out var impliedOp) ? impliedOp : Ops[(mnemonic, Mode.Acc)];
            for (int times = int.Parse(repeat.Groups[1].Value); times > 0; times--) B(oneByteOp);
            return;
        }

        if (Branches.Contains(mnemonic)) { Br(Ops[(mnemonic, Mode.Rel)], RefName(operand)); return; }

        Mode mode; string expr = operand;
        Match match;
        if (operand.Length == 0) mode = Ops.ContainsKey((mnemonic, Mode.Imp)) ? Mode.Imp : Mode.Acc;
        else if (operand is "A" or "a") mode = Mode.Acc;
        else if (operand.StartsWith('#')) { mode = Mode.Imm; expr = operand[1..]; }
        else if ((match = Regex.Match(operand, @"^\((.+),\s*[sS]\)\s*,\s*[yY]$")).Success) { mode = Mode.SrY; expr = match.Groups[1].Value; }
        else if ((match = Regex.Match(operand, @"^\((.+)\)\s*,\s*[yY]$")).Success) { mode = Mode.IndDpY; expr = match.Groups[1].Value; }
        else if ((match = Regex.Match(operand, @"^\((.+),\s*[xX]\)$")).Success) { mode = mnemonic is "JMP" or "JSR" ? Mode.IndAbsX : Mode.IndDpX; expr = match.Groups[1].Value; }
        else if ((match = Regex.Match(operand, @"^\((.+)\)$")).Success) { mode = mnemonic == "JMP" ? Mode.IndAbs : Mode.IndDp; expr = match.Groups[1].Value; }
        else if ((match = Regex.Match(operand, @"^\[(.+)\]\s*,\s*[yY]$")).Success) { mode = Mode.IndLDpY; expr = match.Groups[1].Value; }
        else if ((match = Regex.Match(operand, @"^\[(.+)\]$")).Success) { mode = mnemonic is "JMP" or "JML" ? Mode.IndLAbs : Mode.IndLDp; expr = match.Groups[1].Value; }
        else if ((match = Regex.Match(operand, @"^(.+),\s*[sS]$")).Success) { mode = Mode.Sr; expr = match.Groups[1].Value; }
        else if ((match = Regex.Match(operand, @"^(.+),\s*([xXyY])$")).Success)
        {
            expr = match.Groups[1].Value;
            bool indexX = match.Groups[2].Value is "x" or "X";
            int width = Width(expr, size, mnemonic);
            mode = width switch { 1 => indexX ? Mode.DpX : Mode.DpY, 2 => indexX ? Mode.AbsX : Mode.AbsY, _ => indexX ? Mode.LongX : throw new FormatException("long,Y doesn't exist") };
        }
        else
        {
            int width = Width(operand, size, mnemonic);
            mode = width switch { 1 => Mode.Dp, 2 => Mode.Abs, _ => Mode.Long };
        }
        if (!Ops.TryGetValue((mnemonic, mode), out byte op)) throw new FormatException($"{mnemonic} has no {mode} mode");
        expr = expr.Trim();
        if (mode is Mode.Imp or Mode.Acc) { B(op); return; }

        var labelPlus = Regex.Match(expr, @"^(\.?[A-Za-z_]\w*)\s*([+-].+)$");
        if (labelPlus.Success && !Regex.IsMatch(labelPlus.Groups[1].Value, "^[0-9]"))
        {
            // label + number
            string target = LabelName(labelPlus.Groups[1].Value) + Eval(labelPlus.Groups[2].Value, out _).ToString("+0;-0");
            if (mode is Mode.Long or Mode.LongX) LongLabel(op, target);
            else if (mode is Mode.Abs or Mode.AbsX or Mode.AbsY or Mode.IndAbs or Mode.IndAbsX) Abs(op, target);
            else throw new FormatException("a label needs an absolute or long operand");
            return;
        }
        if (IsLabelRef(expr))
        {
            string target = LabelName(expr);
            if (mode is Mode.Long or Mode.LongX) LongLabel(op, target);
            else if (mode is Mode.Abs or Mode.AbsX or Mode.AbsY or Mode.IndAbs or Mode.IndAbsX) Abs(op, target);
            else throw new FormatException("a label needs an absolute or long operand");
            return;
        }
        int value = Eval(expr, out int hint);
        int bytes = mode switch
        {
            Mode.Imm => size == 'b' ? 1 : size == 'w' ? 2 : mnemonic is "REP" or "SEP" ? 1 : hint > 0 ? Math.Min(hint, 2) : value > 0xFF ? 2 : 1,
            Mode.Dp or Mode.DpX or Mode.DpY or Mode.IndDp or Mode.IndDpX or Mode.IndDpY or Mode.IndLDp or Mode.IndLDpY or Mode.Sr or Mode.SrY => 1,
            Mode.Long or Mode.LongX => 3,
            _ => 2,
        };
        B(op);
        for (int i = 0; i < bytes; i++) B((byte)(value >> (8 * i)));
    }

    // operand width in bytes: the suffix, else a label = 2 (JSL/JML 3), else how the number is written
    private int Width(string expr, char size, string mnemonic)
    {
        if (size == 'b') return 1;
        if (size == 'w') return 2;
        if (size == 'l') return 3;
        if (IsLabelRef(expr.Trim()) || Regex.IsMatch(expr.Trim(), @"^\.?[A-Za-z_]\w*\s*[+-]")) return mnemonic is "JSL" or "JML" ? 3 : 2;
        Eval(expr, out int hint);
        if (hint == 0) throw new FormatException($"say how wide \"{expr}\" is (.b / .w / .l)");
        return hint;
    }

    private string Expand(string statement)
    {
        foreach (var name in _defines.Keys.OrderByDescending(k => k.Length)) statement = statement.Replace("!" + name, _defines[name]);
        return statement;
    }

    private static bool IsLabelRef(string expr) => Regex.IsMatch(expr, @"^(\.?[A-Za-z_][\w]*|\.\w+|[+-]+)$") && !Regex.IsMatch(expr, @"^[0-9]");

    private string LabelName(string expr) => expr.StartsWith('.') ? _scope + expr : expr[0] is '+' or '-' ? RefName(expr) : expr;

    private string RefName(string expr)
    {
        expr = expr.Trim();
        if (expr.All(c => c == '-')) return _anonBack.TryGetValue(expr, out var backLabel) ? backLabel : throw new FormatException($"no {expr} label before this");
        if (expr.All(c => c == '+'))
        {
            if (!_anonFwd.TryGetValue(expr, out var forwardLabel)) _anonFwd[expr] = forwardLabel = $"@anon{_anon++}";
            return forwardLabel;
        }
        return LabelName(expr);
    }

    // a number expression: $hex, %binary, decimal and 'c' terms with ( ), unary - and ~, * / + - << >> & ^ |
    // (usual precedence, asar sources keep their sums in brackets anyway). hint = bytes the first written number has
    // ($12 = 1, $1234 = 2, $7E1234 = 3, decimal = 0)
    private static int Eval(string expr, out int hint)
    {
        var first = Regex.Match(expr, @"[$%]([0-9A-Fa-f]+)");
        hint = !first.Success ? 0 : expr[first.Index] == '$' ? (first.Groups[1].Length + 1) / 2 : (first.Groups[1].Length + 7) / 8;
        var parser = new ExprParser(expr.Replace(" ", ""));
        long value = parser.Or();
        if (parser.Pos != parser.Text.Length) throw new FormatException($"can't work out \"{expr}\" (labels can't be used in sums)");
        return (int)value;
    }

    private sealed class ExprParser
    {
        public readonly string Text; public int Pos;
        public ExprParser(string text) { Text = text; }
        private bool Eat(string token) { if (string.CompareOrdinal(Text, Pos, token, 0, token.Length) == 0) { Pos += token.Length; return true; } return false; }
        public long Or() { long v = Xor(); while (Eat("|")) v |= Xor(); return v; }
        private long Xor() { long v = And(); while (Eat("^")) v ^= And(); return v; }
        private long And() { long v = Shift(); while (Eat("&")) v &= Shift(); return v; }
        private long Shift() { long v = Add(); while (true) { if (Eat("<<")) v <<= (int)Add(); else if (Eat(">>")) v >>= (int)Add(); else return v; } }
        private long Add() { long v = Mul(); while (true) { if (Eat("+")) v += Mul(); else if (Eat("-")) v -= Mul(); else return v; } }
        private long Mul() { long v = Unary(); while (true) { if (Eat("*")) v *= Unary(); else if (Eat("/")) v /= Unary(); else return v; } }
        private long Unary() => Eat("-") ? -Unary() : Eat("+") ? Unary() : Eat("~") ? ~Unary() : Primary();
        private long Primary()
        {
            if (Eat("(")) { long v = Or(); if (!Eat(")")) throw new FormatException("missing )"); return v; }
            var number = Regex.Match(Text[Pos..], @"^(\$[0-9A-Fa-f]+|%[01]+|\d+|'.')");
            if (!number.Success) throw new FormatException($"can't work out \"{Text}\" (labels can't be used in sums)");
            Pos += number.Length;
            string token = number.Value;
            return token[0] == '$' ? long.Parse(token[1..], NumberStyles.HexNumber) : token[0] == '%' ? Convert.ToInt64(token[1..], 2) : token[0] == '\'' ? token[1] : long.Parse(token);
        }
    }
}
