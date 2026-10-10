using System.Text;

namespace LufiaForge.Core;

/// <summary>
/// Item descriptions added by the "Lufia &amp; the Fortress of Doom Restored" hack (press X on an item). Its code at
/// $20:8000 (file 0x100000) reads a table of 256 pointers at $20:8200 (file 0x100200, one per item, bank $20) to
/// texts from 0x100400: plain ASCII, lines separated by two 0A bytes, ending with 00. The rest of that bank is
/// zero-filled, so edited texts can grow into it. Unmodified ROMs have no descriptions (<see cref="Find"/> = null).
/// </summary>
public sealed class ItemDescriptions
{
    public const int CodeStart = 0x100000;
    public const int Table = 0x100200;
    public const int TextStart = 0x100400;
    public const int Count = 256;
    public const int BankEnd = 0x108000;
    public const int MaxLines = 2;
    /// <summary>The longest line the hack's own texts use (the window holds this much).</summary>
    public const int MaxLineLength = 30;

    private readonly RomBuffer _rom;
    /// <summary>End of the room the texts may use (the zero-filled rest of the bank after them).</summary>
    public int Limit { get; }
    public int UsedEnd { get; private set; }

    private ItemDescriptions(RomBuffer rom, int usedEnd, int limit) { _rom = rom; UsedEnd = usedEnd; Limit = limit; }

    private static int Pointer(RomBuffer rom, int i) => rom.ReadUInt16Le(Table + i * 2);
    private static int FileOffset(int ptr) => CodeStart + (ptr - 0x8000);

    /// <summary>The descriptions of a ROM that has them (the Restored hack), else null.</summary>
    public static ItemDescriptions? Find(RomBuffer rom)
    {
        if (rom.Length < BankEnd) return null;
        // the hack's code reads the table with LDA $8200,X (BD 00 82)
        var code = rom.ReadBytes(CodeStart, Table - CodeStart);
        bool reads = false;
        for (int i = 0; i + 2 < code.Length; i++) if (code[i] == 0xBD && code[i + 1] == 0x00 && code[i + 2] == 0x82) { reads = true; break; }
        if (!reads) return null;
        int end = TextStart;
        for (int i = 0; i < Count; i++)
        {
            int p = Pointer(rom, i);
            if (p < 0x8000 + (TextStart - CodeStart) || p >= 0x8000 + (BankEnd - CodeStart)) return null;
            int o = FileOffset(p), n = 0;
            while (o + n < BankEnd && rom.ReadByte(o + n) != 0) { if (++n > 200) return null; }
            end = Math.Max(end, o + n + 1);
        }
        // texts not reached by a pointer (shared endings) may follow: go on to the zero padding
        while (end < BankEnd && !Enumerable.Range(0, 16).All(k => end + k >= BankEnd || rom.ReadByte(end + k) == 0)) end++;
        int limit = end;
        while (limit < BankEnd && rom.ReadByte(limit) == 0) limit++;
        return new ItemDescriptions(rom, end, limit);
    }

    /// <summary>The description of item <paramref name="item"/>, lines separated by \n.</summary>
    public string Read(int item)
    {
        int o = FileOffset(Pointer(_rom, item));
        var sb = new StringBuilder();
        for (int b; (b = _rom.ReadByte(o)) != 0; o++) sb.Append((char)b);
        return sb.ToString().Replace("\n\n", "\n");
    }

    /// <summary>Why a text can't be used, or null.</summary>
    public static string? Problem(string text)
    {
        var lines = text.Replace("\r", "").Split('\n');
        if (lines.Length > MaxLines) return $"at most {MaxLines} lines";
        if (lines.Any(l => l.Length > MaxLineLength)) return $"a line is longer than {MaxLineLength} characters (the window would cut it)";
        if (text.Replace("\r", "").Replace("\n", "").Any(c => c < 0x20 || c > 0x7E)) return "only plain letters, digits and punctuation";
        return null;
    }

    /// <summary>Writes all 256 texts again (identical texts share their bytes); throws when they don't fit.</summary>
    public void WriteAll(IReadOnlyList<string> texts)
    {
        if (texts.Count != Count) throw new ArgumentException("256 texts expected.");
        var data = new List<byte>();
        var at = new Dictionary<string, int>();
        var ptr = new int[Count];
        for (int i = 0; i < Count; i++)
        {
            string t = texts[i].Replace("\r", "");
            if (Problem(t) is string bad) throw new InvalidOperationException($"Item {i:X2}'s description: {bad}.");
            if (!at.TryGetValue(t, out int pos))
            {
                pos = TextStart + data.Count;
                at[t] = pos;
                data.AddRange(Encoding.ASCII.GetBytes(t.Replace("\n", "\n\n")));
                data.Add(0);
            }
            ptr[i] = pos;
        }
        if (TextStart + data.Count > Limit)
            throw new InvalidOperationException($"The descriptions need {data.Count:N0} bytes; there is room for {Limit - TextStart:N0}.");
        var block = new byte[Math.Max(UsedEnd, TextStart + data.Count) - TextStart];
        data.CopyTo(block);
        _rom.WriteBytes(TextStart, block);
        for (int i = 0; i < Count; i++) _rom.WriteUInt16Le(Table + i * 2, (ushort)(0x8000 + ptr[i] - CodeStart));
        UsedEnd = Math.Max(UsedEnd, TextStart + data.Count);
    }

    /// <summary>Room left for longer texts, in bytes.</summary>
    public int Free(IReadOnlyList<string> texts) =>
        Limit - TextStart - texts.Select(t => t.Replace("\r", "")).Distinct().Sum(t => Encoding.ASCII.GetByteCount(t.Replace("\n", "\n\n")) + 1);
}
