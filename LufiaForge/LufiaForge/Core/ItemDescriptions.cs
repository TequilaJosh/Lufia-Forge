using System.Text;

namespace LufiaForge.Core;

// item descriptions that the "Lufia & the Fortress of Doom Restored" hack adds (press X on an item).
// its code at $20:8000 (file 0x100000) reads a table of 256 pointers at $20:8200 (file 0x100200, one per item, bank $20)
// to texts from 0x100400: plain ascii, lines split by two 0A bytes, ending in 00. the rest of that bank is zero-filled,
// so edited texts can grow into it. unmodified roms have no descriptions (Find = null)
public sealed class ItemDescriptions
{
    public const int CodeStart = 0x100000;
    public const int Table = 0x100200;
    public const int TextStart = 0x100400;
    public const int Count = 256;
    public const int BankEnd = 0x108000;
    public const int MaxLines = 2;
    // the longest line the hack's own texts use (the window holds this much)
    public const int MaxLineLength = 30;
    // the letters the window shows. measured in bizhawk: its frame is fixed and a 23rd letter sits on the frame
    // (restored's own texts reach 23 with "A three-pronged weapon.")
    public const int VisibleLineLength = 22;

    // a soft warning when a line runs past the window's frame, or ""
    public static string Warning(string text) =>
        text.Replace("\r", "").Split('\n').Any(l => l.TrimEnd().Length > VisibleLineLength)
            ? $"a line is longer than the {VisibleLineLength} letters the window shows, the rest runs over its frame" : "";

    private readonly RomBuffer _rom;
    // end of the room the texts can use (the zero-filled rest of the bank after them)
    public int Limit { get; }
    public int UsedEnd { get; private set; }

    private ItemDescriptions(RomBuffer rom, int usedEnd, int limit) { _rom = rom; UsedEnd = usedEnd; Limit = limit; }

    private static int Pointer(RomBuffer rom, int item) => rom.ReadUInt16Le(Table + item * 2);
    private static int FileOffset(int pointer) => CodeStart + (pointer - 0x8000);

    // the descriptions of a rom that has them (the restored hack), else null
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
            int pointer = Pointer(rom, i);
            if (pointer < 0x8000 + (TextStart - CodeStart) || pointer >= 0x8000 + (BankEnd - CodeStart)) return null;
            int offset = FileOffset(pointer), length = 0;
            while (offset + length < BankEnd && rom.ReadByte(offset + length) != 0) { if (++length > 200) return null; }
            end = Math.Max(end, offset + length + 1);
        }
        // texts no pointer reaches (shared endings) can come after, so go on to the zero padding
        while (end < BankEnd && !Enumerable.Range(0, 16).All(k => end + k >= BankEnd || rom.ReadByte(end + k) == 0)) end++;
        int limit = end;
        while (limit < BankEnd && rom.ReadByte(limit) == 0) limit++;
        return new ItemDescriptions(rom, end, limit);
    }

    // the description of item, lines split by \n
    public string Read(int item)
    {
        int offset = FileOffset(Pointer(_rom, item));
        var sb = new StringBuilder();
        for (int code; (code = _rom.ReadByte(offset)) != 0; offset++) sb.Append(DutchLetters.Show(_rom, (byte)code));
        return sb.ToString().Replace("\n\n", "\n");
    }

    // why a text can't be used, or null. with the dutch letters in (DutchLetters) those can be typed too
    public static string? Problem(string text, RomBuffer? rom = null)
    {
        var lines = text.Replace("\r", "").Split('\n');
        if (lines.Length > MaxLines) return $"at most {MaxLines} lines";
        if (lines.Any(l => l.Length > MaxLineLength)) return $"a line is longer than {MaxLineLength} characters (the window would cut it)";
        string flat = text.Replace("\r", "").Replace("\n", "");
        if (rom != null && DutchLetters.Unwritable(rom, flat) is string bad) return bad;
        if (flat.Any(c => c < 0x20 || c > 0x7E && (rom == null || DutchLetters.Code(rom, c) == null))) return "only plain letters, digits and punctuation";
        return null;
    }

    // writes all 256 texts again (identical texts share their bytes), throws if they don't fit
    public void WriteAll(IReadOnlyList<string> texts)
    {
        if (texts.Count != Count) throw new ArgumentException("256 texts expected.");
        var data = new List<byte>();
        var offsetByText = new Dictionary<string, int>();
        var textOffsets = new int[Count];
        for (int i = 0; i < Count; i++)
        {
            string text = texts[i].Replace("\r", "");
            if (Problem(text, _rom) is string bad) throw new InvalidOperationException($"Item {i:X2}'s description: {bad}.");
            if (!offsetByText.TryGetValue(text, out int offset))
            {
                offset = TextStart + data.Count;
                offsetByText[text] = offset;
                data.AddRange(Bytes(text));
                data.Add(0);
            }
            textOffsets[i] = offset;
        }
        if (TextStart + data.Count > Limit)
            throw new InvalidOperationException($"The descriptions need {data.Count:N0} bytes; there is room for {Limit - TextStart:N0}.");
        var block = new byte[Math.Max(UsedEnd, TextStart + data.Count) - TextStart];
        data.CopyTo(block);
        _rom.WriteBytes(TextStart, block);
        for (int i = 0; i < Count; i++) _rom.WriteUInt16Le(Table + i * 2, (ushort)(0x8000 + textOffsets[i] - CodeStart));
        UsedEnd = Math.Max(UsedEnd, TextStart + data.Count);
    }

    // room left for longer texts, in bytes
    public int Free(IReadOnlyList<string> texts) =>
        Limit - TextStart - texts.Select(t => t.Replace("\r", "")).Distinct().Sum(t => Bytes(t).Length + 1);

    // a text's bytes: a letter each (a dutch letter as its code), a line break as two 0A
    private byte[] Bytes(string text) => text.Replace("\n", "\n\n").Select(c => DutchLetters.Code(_rom, c) ?? (byte)'?').ToArray();
}
