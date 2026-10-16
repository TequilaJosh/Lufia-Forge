namespace LufiaForge.Core.Battle;

/// <summary>
/// A very small 65816 assembler for Lufia Forge's own routines: bytes are written as they are, with labels for
/// branches (8-bit relative), JSR / JMP / table words inside the block (16-bit, from the block's SNES address) and
/// long addresses. Mistakes (an unknown label, a branch too far) throw when the block is built.
/// </summary>
public sealed class Asm65816
{
    private readonly List<byte> _code = new();
    private readonly Dictionary<string, int> _labels = new();
    private readonly List<(int At, string Label, char Kind)> _fixups = new();

    public int Position => _code.Count;

    /// <summary>Raw bytes (opcodes and operands).</summary>
    public Asm65816 B(params byte[] bytes) { _code.AddRange(bytes); return this; }

    public Asm65816 Label(string name)
    {
        if (_labels.ContainsKey(name)) throw new InvalidOperationException($"Label {name} is defined twice.");
        _labels[name] = _code.Count;
        return this;
    }

    /// <summary>A branch (BRA BEQ BNE BCC BCS BPL BMI BVC BVS) to a label.</summary>
    public Asm65816 Br(byte opcode, string label)
    {
        _code.Add(opcode);
        _fixups.Add((_code.Count, label, 'r'));
        _code.Add(0);
        return this;
    }

    /// <summary>An instruction with a 16-bit address of a label in this block (JSR, JMP, JSR (abs,X)...).</summary>
    public Asm65816 Abs(byte opcode, string label)
    {
        _code.Add(opcode);
        _fixups.Add((_code.Count, label, 'w'));
        _code.Add(0); _code.Add(0);
        return this;
    }

    /// <summary>A 16-bit table word holding a label's address in this block.</summary>
    public Asm65816 Word(string label)
    {
        _fixups.Add((_code.Count, label, 'w'));
        _code.Add(0); _code.Add(0);
        return this;
    }

    /// <summary>An instruction with a 24-bit address (JSL, LDA long, STA long...).</summary>
    public Asm65816 Long(byte opcode, int address)
    {
        _code.Add(opcode);
        _code.Add((byte)address); _code.Add((byte)(address >> 8)); _code.Add((byte)(address >> 16));
        return this;
    }

    /// <summary>The bytes, for a block that starts at <paramref name="snesBase"/> (bank in the top byte).</summary>
    public byte[] Build(int snesBase)
    {
        var bytes = _code.ToArray();
        foreach (var (at, label, kind) in _fixups)
        {
            if (!_labels.TryGetValue(label, out int target)) throw new InvalidOperationException($"Unknown label {label}.");
            if (kind == 'r')
            {
                int rel = target - (at + 1);
                if (rel < -128 || rel > 127) throw new InvalidOperationException($"Branch to {label} is too far ({rel}).");
                bytes[at] = (byte)(sbyte)rel;
            }
            else
            {
                int address = (snesBase & 0xFFFF) + target;
                if ((snesBase & 0xFFFF) + bytes.Length > 0x10000) throw new InvalidOperationException("The block crosses a bank.");
                bytes[at] = (byte)address; bytes[at + 1] = (byte)(address >> 8);
            }
        }
        return bytes;
    }

    /// <summary>Where a label is in the block (an offset).</summary>
    public int Offset(string label) => _labels[label];
}
