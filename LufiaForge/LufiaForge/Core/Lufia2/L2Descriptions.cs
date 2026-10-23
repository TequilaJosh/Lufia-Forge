using System.Text;

namespace LufiaForge.Core.Lufia2;

/// <summary>
/// Lufia II descriptions of items, spells and IP attacks (US ROM, found 2026-10-23: the routine at $85:C4F1 takes
/// entry $0A11, takes 0xD0 off values from 0x300, and reads a u16 offset from $97:8000, file 0xB8000). Entry n is
/// item n, 0x200 + n spell n, 0x230 + n IP attack n. A description: 00 ends it, 0A new line, 20-7F letters, 80-FF
/// description word (byte &amp; 7F), 05-09 xx description word (code - 5) * 0xFF + xx + 0x80, 10-1F xx dictionary word
/// (code - 0x10) * 0xFF + xx. The 624 description words are u16 offsets from $9E:DDCC (file 0xF5DCC), right after the
/// item names. The texts are packed one after another from 0xB85B4 to 0xBB0A0, its last 00, (identical ones shared).
/// </summary>
public static class L2Descriptions
{
    public const int Table = 0xB8000, Count = 0x2D9, SpellBase = 0x200, IpBase = 0x230;
    public const int Words = 0xF5DCC, DataEnd = 0xBB0A1;

    private static string[]? _words;
    private static RomBuffer? _wordsRom;

    public static string[] DescriptionWords(RomBuffer rom)
    {
        if (_words != null && ReferenceEquals(_wordsRom, rom)) return _words;
        int n = rom.ReadUInt16Le(Words) / 2;
        var w = new string[n];
        for (int k = 0; k < n; k++) w[k] = Encoding.Latin1.GetString(rom.ReadUntil(Words + rom.ReadUInt16Le(Words + k * 2), 0, 40));
        _wordsRom = rom; _words = w;
        return w;
    }

    public static int At(RomBuffer rom, int entry) => Table + rom.ReadUInt16Le(Table + entry * 2);

    /// <summary>The raw bytes of entry <paramref name="entry"/>, its 00 included.</summary>
    public static byte[] Raw(RomBuffer rom, int entry)
    {
        int at = At(rom, entry), p = at;
        while (p < rom.Length)
        {
            byte c = rom.ReadByte(p);
            if (c == 0) break;
            p += c is >= 5 and <= 9 or >= 0x10 and < 0x20 ? 2 : 1;
        }
        return rom.ReadBytes(at, p + 1 - at);
    }

    /// <summary>The description as text, new lines as '\n'.</summary>
    public static string Read(RomBuffer rom, int entry)
    {
        var words = DescriptionWords(rom);
        var dict = L2Text.Words(rom);
        var b = Raw(rom, entry);
        var sb = new StringBuilder();
        for (int i = 0; i < b.Length && b[i] != 0; i++)
        {
            byte c = b[i];
            if (c >= 0x80) sb.Append(words[c & 0x7F]);
            else if (c == 0x0A) sb.Append('\n');
            else if (c >= 0x20) sb.Append((char)c);
            else if (c >= 0x10) sb.Append(Lookup(dict, (c - 0x10) * 0xFF + b[++i]));
            else if (c >= 5) sb.Append(Lookup(words, (c - 5) * 0xFF + b[++i] + 0x80));
            else sb.Append($"[{c:X2}]");
        }
        return sb.ToString();
    }

    private static string Lookup(string[] list, int k) => k < list.Length ? list[k] : "?";

    /// <summary>Encodes a description (the longest matching word first; description words 0-7F are one byte, the
    /// others two), its 00 included.</summary>
    public static byte[] Encode(RomBuffer rom, string text)
    {
        var words = DescriptionWords(rom);
        var dict = L2Text.Words(rom);
        var cands = words.Select((w, k) => (w, code: k < 0x80 ? new[] { (byte)(0x80 | k) } : new[] { (byte)(5 + (k - 0x80) / 0xFF), (byte)((k - 0x80) % 0xFF) }))
            .Concat(dict.Select((w, k) => (w, code: new[] { (byte)(0x10 + k / 0xFF), (byte)(k % 0xFF) })))
            .Where(c => c.w.Length > c.code.Length)
            .OrderByDescending(c => c.w.Length).ThenBy(c => c.code.Length).ToList();
        var outp = new List<byte>();
        text = text.Replace("\r\n", "\n");
        int i = 0;
        while (i < text.Length)
        {
            char ch = text[i];
            if (ch == '\n') { outp.Add(0x0A); i++; continue; }
            var hit = cands.FirstOrDefault(c => string.CompareOrdinal(text, i, c.w, 0, c.w.Length) == 0);
            if (hit.w != null) { outp.AddRange(hit.code); i += hit.w.Length; continue; }
            outp.Add(ch is >= ' ' and < (char)0x7F ? (byte)ch : (byte)'?');
            i++;
        }
        outp.Add(0);
        return outp.ToArray();
    }

    /// <summary>Space the descriptions take and have.</summary>
    public static (int Used, int Room) Space(RomBuffer rom)
    {
        int start = Enumerable.Range(0, Count).Min(e => At(rom, e));
        int used = Enumerable.Range(0, Count).Select(e => At(rom, e)).Distinct().Sum(a => RawAt(rom, a).Length);
        return (used, DataEnd - start);
    }

    private static byte[] RawAt(RomBuffer rom, int at)
    {
        int p = at;
        while (rom.ReadByte(p) != 0) p += rom.ReadByte(p) is >= 5 and <= 9 or >= 0x10 and < 0x20 ? 2 : 1;
        return rom.ReadBytes(at, p + 1 - at);
    }

    /// <summary>Sets entry <paramref name="entry"/>'s text: all descriptions are packed again in their area (the
    /// others keep their bytes; identical ones are shared). Returns null, or why it can't be done (too long).</summary>
    public static string? Write(RomBuffer rom, int entry, string text)
    {
        // the others keep their bytes; if that doesn't fit, they're all encoded again (that saves about 100 bytes)
        var mine = Encode(rom, text);
        return Pack(rom, Enumerable.Range(0, Count).Select(e => e == entry ? mine : Raw(rom, e)).ToList()) == null ? null
             : Pack(rom, Enumerable.Range(0, Count).Select(e => e == entry ? mine : Encode(rom, Read(rom, e))).ToList());
    }

    /// <summary>Writes the descriptions packed (identical ones shared), or returns why they don't fit.</summary>
    private static string? Pack(RomBuffer rom, List<byte[]> all)
    {
        int start = Enumerable.Range(0, Count).Min(e => At(rom, e));
        var placed = new Dictionary<string, int>();
        var blob = new List<byte>();
        var offsets = new int[Count];
        // in the order they sit now, so the untouched ones keep their places up to the edited one
        foreach (int e in Enumerable.Range(0, Count).OrderBy(e => At(rom, e)).ThenBy(e => e))
        {
            string key = Convert.ToHexString(all[e]);
            if (!placed.TryGetValue(key, out int at)) { at = start + blob.Count; placed[key] = at; blob.AddRange(all[e]); }
            offsets[e] = at;
        }
        if (start + blob.Count > DataEnd) return $"{start + blob.Count - DataEnd} bytes too long for the descriptions' space (shorten this or another description).";
        rom.WriteBytes(start, blob.ToArray());
        for (int p = start + blob.Count; p < DataEnd; p++) rom.WriteByte(p, 0);
        for (int e = 0; e < Count; e++) rom.WriteUInt16Le(Table + e * 2, (ushort)(offsets[e] - Table));
        return null;
    }
}
