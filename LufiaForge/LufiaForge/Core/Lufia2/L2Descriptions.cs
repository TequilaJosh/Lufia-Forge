using System.Text;

namespace LufiaForge.Core.Lufia2;

/// <summary>
/// Lufia II descriptions of items, spells and IP attacks (US ROM, found 2026-10-23: the routine at $85:C4F1 takes
/// entry $0A11, takes 0xD0 off values from 0x300, and reads a u16 offset from $97:8000, file 0xB8000). The game asks for
/// item n as n, spell n as 0x200 + n and IP attack n (counted from 1, as items name them) as 0x300 + n, so the table's
/// entry is item n, 0x200 + n spell n, 0x230 + n IP attack n (0x231 the first one). A description: 00 ends it, 0A new
/// line, 20-7F letters, 80-FF description word (byte &amp; 7F), 05-09 xx description word (code - 5) * 0xFF + xx + 0x80,
/// 10-1F xx dictionary word (code - 0x10) * 0xFF + xx. The 624 description words are u16 offsets from $9E:DDCC (file
/// 0xF5DCC), right after the item names. The texts are packed one after another from 0xB85B4 to 0xBB0A0, its last 00,
/// (identical ones shared); the game's own fill that space to the last byte.
/// <para>
/// Moved descriptions (<see cref="Move"/>, 2026-10-25): the table and the texts go to the added space with room to spare
/// and a table of 0x400 entries indexed by the game's own number (item n, 0x200 + spell, 0x300 + IP attack counted from
/// 1), so spells 28-FE and IP attacks past the game's 168 have descriptions of their own. Five places in $85:C4F1
/// change: the "take 0xD0 off" (9 bytes at $85:C4FD) becomes NOPs and its four LDA $978000,X read the new place. The
/// old space ($97:8000-$97:B0A0) is left free for the IP attack records (<see cref="L2Magic"/>).
/// </para>
/// </summary>
public static class L2Descriptions
{
    public const int Table = 0xB8000, Count = 0x2D9, SpellBase = 0x200, IpBase = 0x230;
    public const int Words = 0xF5DCC, DataEnd = 0xBB0A1;

    // ── moved descriptions ───────────────────────────────────────────────────
    private const string Magic = "LF2DS";
    /// <summary>"LF2FREE" at the old table: its space is free (the descriptions were moved).</summary>
    public const string FreeMark = "LF2FREE";
    private const int MovedCount = 0x400, MovedSize = 0x6000, HeaderSize = 0x10;
    private const int MapSite = 0x2C4FD;
    private static readonly byte[] MapOriginal = { 0xC9, 0x00, 0x03, 0x90, 0x04, 0x38, 0xE9, 0xD0, 0x00 };
    /// <summary>The four LDA $978000,X (BF 00 80 97) of $85:C4F1: the table, then the text three times.</summary>
    private static readonly int[] ReadSites = { 0x2C508, 0x2C516, 0x2C53A, 0x2C56F };

    /// <summary>File offset of the moved block's header, or -1 when the descriptions are where the game has them.</summary>
    public static int Block(RomBuffer rom)
    {
        if (rom.Length <= ReadSites[0] + 4 || rom.ReadByte(ReadSites[0]) != 0xBF) return -1;
        int snes = (int)rom.ReadUInt24Le(ReadSites[0] + 1);
        if (snes == 0x978000) return -1;
        int at = L2Rom.SnesToFile(snes) - HeaderSize;
        return at >= 0 && at + MovedSize <= rom.Length && Encoding.ASCII.GetString(rom.ReadBytes(at, Magic.Length)) == Magic ? at : -1;
    }

    public static bool IsMoved(RomBuffer rom) => Block(rom) >= 0;

    /// <summary>Where the table is (the texts' offsets count from there too).</summary>
    private static int Base(RomBuffer rom) => Block(rom) is var b && b >= 0 ? b + HeaderSize : Table;

    /// <summary>How many entries the table has.</summary>
    public static int EntryCount(RomBuffer rom) => IsMoved(rom) ? MovedCount : Count;

    private static int End(RomBuffer rom) => Block(rom) is var b && b >= 0 ? b + MovedSize : DataEnd;

    /// <summary>The entry of spell <paramref name="spell"/> (the same in both layouts; in the game's own one spells from 30
    /// on would share the IP attacks' entries, so spells added by Lufia Forge come with moved descriptions).</summary>
    public static int SpellEntry(int spell) => SpellBase + spell;

    /// <summary>The entry of IP attack <paramref name="ip"/> (counted from 0, as Lufia Forge's IP attack list).</summary>
    public static int IpEntry(RomBuffer rom, int ip) => (IsMoved(rom) ? 0x301 : IpBase + 1) + ip;

    private static string[]? _words;
    private static RomBuffer? _wordsRom;

    public static string[] DescriptionWords(RomBuffer rom)
    {
        if (_words != null && ReferenceEquals(_wordsRom, rom)) return _words;
        int list = WordsAt(rom);
        int n = rom.ReadUInt16Le(list) / 2;
        var w = new string[n];
        for (int k = 0; k < n; k++) w[k] = new string(rom.ReadUntil(list + rom.ReadUInt16Le(list + k * 2), 0, 40).Select(b => L2Charset.ToChar(rom, b) ?? '?').ToArray());
        _wordsRom = rom; _words = w;
        return w;
    }

    /// <summary>The two LDA $9E:DDCC,X of $85:C4F1 that read the word list (operands).</summary>
    private static readonly int[] WordSites = { 0x2C58A, 0x2C598 };

    /// <summary>Where the word list is: read from the code (<see cref="SetWords"/> moves it), else the game's place.</summary>
    public static int WordsAt(RomBuffer rom)
    {
        if (rom.Length > WordSites[1] + 3 && WordSites.All(s => rom.ReadByte(s - 1) == 0xBF))
        {
            int snes = (int)rom.ReadUInt24Le(WordSites[0]);
            if (snes == (int)rom.ReadUInt24Le(WordSites[1])) return L2Rom.SnesToFile(snes);
        }
        return Words;
    }

    /// <summary>Puts a new word list in the added space and points the code at it (the descriptions are encoded again
    /// with it by the caller: what the old codes meant changes).</summary>
    public static void SetWords(RomBuffer rom, IReadOnlyList<string> words)
    {
        if (!WordSites.All(s => rom.ReadByte(s - 1) == 0xBF)) throw new InvalidOperationException("This ROM's description code isn't the US game's.");
        var head = new List<byte>(); var body = new List<byte>();
        int n = Math.Min(words.Count, 0x80 + 5 * 0xFF);
        for (int k = 0; k < n; k++)
        {
            head.Add((byte)(n * 2 + body.Count)); head.Add((byte)((n * 2 + body.Count) >> 8));
            body.AddRange(words[k].Select(c => L2Charset.ToByte(rom, c) ?? (byte)'?'));
            body.Add(0);
        }
        var bytes = head.Concat(body).ToArray();
        int at = L2Rom.Allocate(rom, bytes.Length);
        rom.WriteBytes(at, bytes);
        PointWords(rom, at);
    }

    /// <summary>Points the code at the word list at file offset <paramref name="at"/>.</summary>
    public static void PointWords(RomBuffer rom, int at)
    {
        int snes = L2Rom.FileToSnes(at);
        foreach (int s in WordSites) rom.WriteBytes(s, new[] { (byte)snes, (byte)(snes >> 8), (byte)(snes >> 16) });
        _words = null; _wordsRom = null;
    }

    public static int At(RomBuffer rom, int entry)
    {
        int b = Base(rom);
        return b + rom.ReadUInt16Le(b + entry * 2);
    }

    /// <summary>The raw bytes of entry <paramref name="entry"/>, its 00 included.</summary>
    public static byte[] Raw(RomBuffer rom, int entry) => RawAt(rom, At(rom, entry));

    /// <summary>The description as text, new lines as '\n'.</summary>
    public static string Read(RomBuffer rom, int entry)
    {
        if (entry < 0 || entry >= EntryCount(rom)) return "";
        var words = DescriptionWords(rom);
        var dict = L2Text.Words(rom);
        var b = Raw(rom, entry);
        var sb = new StringBuilder();
        for (int i = 0; i < b.Length && b[i] != 0; i++)
        {
            byte c = b[i];
            if (c >= 0x80) sb.Append(words[c & 0x7F]);
            else if (c == 0x0A) sb.Append('\n');
            else if (c >= 0x20) sb.Append(L2Charset.ToChar(rom, c) ?? '?');
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
            // 10-1F are dictionary words here, so only the letters at 20 and up (in the Dutch font " is ë...)
            outp.Add(L2Charset.ToByte(rom, ch) is byte cb && cb >= 0x20 ? cb : (byte)'?');
            i++;
        }
        outp.Add(0);
        return outp.ToArray();
    }

    private static int TextStart(RomBuffer rom) =>
        IsMoved(rom) ? Base(rom) + MovedCount * 2 : Enumerable.Range(0, Count).Min(e => At(rom, e));

    /// <summary>Space the descriptions take and have.</summary>
    public static (int Used, int Room) Space(RomBuffer rom)
    {
        int n = EntryCount(rom), start = TextStart(rom);
        int used = Enumerable.Range(0, n).Select(e => At(rom, e)).Distinct().Sum(a => RawAt(rom, a).Length);
        return (used, End(rom) - start);
    }

    private static byte[] RawAt(RomBuffer rom, int at)
    {
        int p = at;
        while (p < rom.Length && rom.ReadByte(p) != 0) p += rom.ReadByte(p) is >= 5 and <= 9 or >= 0x10 and < 0x20 ? 2 : 1;
        return rom.ReadBytes(at, p + 1 - at);
    }

    /// <summary>Sets entry <paramref name="entry"/>'s text: all descriptions are packed again in their area (the
    /// others keep their bytes; identical ones are shared). When they don't fit where the game has them, they are moved
    /// to the added space first (<see cref="Move"/>). Returns null, or why it can't be done (too long).</summary>
    public static string? Write(RomBuffer rom, int entry, string text)
    {
        if (entry < 0 || entry >= EntryCount(rom)) return "This entry has no description in this ROM.";
        // the others keep their bytes; if that doesn't fit, they're all encoded again (that saves about 100 bytes)
        var mine = Encode(rom, text);
        int n = EntryCount(rom);
        if (Pack(rom, Enumerable.Range(0, n).Select(e => e == entry ? mine : Raw(rom, e)).ToList()) == null) return null;
        if (!IsMoved(rom) && CanMove(rom))
        {
            // the game's space is full to the last byte: move them (this entry's number is the same in both layouts
            // for items and spells; an IP attack's moves from 0x230 + n to 0x300 + n)
            int moved = entry >= IpBase && entry < Count ? entry + 0xD0 : entry;
            Move(rom);
            return Write(rom, moved, text);
        }
        return Pack(rom, Enumerable.Range(0, n).Select(e => e == entry ? mine : Encode(rom, Read(rom, e))).ToList());
    }

    /// <summary>Sets many entries at once (numbers as in the ROM's layout now), packed once; moved to the added space
    /// when they don't fit where the game has them. Returns null, or why they don't fit.</summary>
    public static string? WriteMany(RomBuffer rom, IReadOnlyDictionary<int, string> texts)
    {
        int n = EntryCount(rom);
        var mine = texts.Where(kv => kv.Key >= 0 && kv.Key < n).ToDictionary(kv => kv.Key, kv => Encode(rom, kv.Value));
        if (Pack(rom, Enumerable.Range(0, n).Select(e => mine.TryGetValue(e, out var b) ? b : Raw(rom, e)).ToList()) == null) return null;
        if (!IsMoved(rom) && CanMove(rom))
        {
            Move(rom);
            return WriteMany(rom, texts.ToDictionary(kv => kv.Key >= IpBase && kv.Key < Count ? kv.Key + 0xD0 : kv.Key, kv => kv.Value));
        }
        return Pack(rom, Enumerable.Range(0, n).Select(e => mine.TryGetValue(e, out var b) ? b : Encode(rom, Read(rom, e))).ToList());
    }

    /// <summary>Writes the descriptions packed (identical ones shared), or returns why they don't fit.</summary>
    private static string? Pack(RomBuffer rom, List<byte[]> all)
    {
        int n = all.Count, start = TextStart(rom), end = End(rom), b = Base(rom);
        var placed = new Dictionary<string, int>();
        var blob = new List<byte>();
        var offsets = new int[n];
        // in the order they sit now, so the untouched ones keep their places up to the edited one
        foreach (int e in Enumerable.Range(0, n).OrderBy(e => At(rom, e)).ThenBy(e => e))
        {
            string key = Convert.ToHexString(all[e]);
            if (!placed.TryGetValue(key, out int at)) { at = start + blob.Count; placed[key] = at; blob.AddRange(all[e]); }
            offsets[e] = at;
        }
        if (start + blob.Count > end) return $"{start + blob.Count - end} bytes too long for the descriptions' space (shorten this or another description).";
        rom.WriteBytes(start, blob.ToArray());
        for (int p = start + blob.Count; p < end; p++) rom.WriteByte(p, 0);
        for (int e = 0; e < n; e++) rom.WriteUInt16Le(b + e * 2, (ushort)(offsets[e] - b));
        return null;
    }

    /// <summary>The bytes of the game's code <see cref="Move"/> changes (file offset, length).</summary>
    internal static IEnumerable<(int At, int Length)> CodeSites() =>
        new[] { (MapSite, MapOriginal.Length) }.Concat(ReadSites.Select(s => (s + 1, 3)));

    /// <summary>Whether the descriptions can be moved (the reading code is still the game's).</summary>
    public static bool CanMove(RomBuffer rom)
    {
        if (IsMoved(rom)) return true;
        if (rom.Length < MapSite + MapOriginal.Length || !rom.ReadBytes(MapSite, MapOriginal.Length).SequenceEqual(MapOriginal)) return false;
        return ReadSites.All(s => rom.ReadByte(s) == 0xBF && rom.ReadUInt24Le(s + 1) == 0x978000);
    }

    /// <summary>Moves the table and the texts to the added space (once): a table of 0x400 entries indexed as the game asks
    /// (item n, 0x200 + spell n, 0x300 + IP attack n counted from 1) and room for about 12 KB more text. The old space is
    /// cleared and marked free (<see cref="FreeMark"/>).</summary>
    public static void Move(RomBuffer rom)
    {
        if (IsMoved(rom)) return;
        if (!CanMove(rom)) throw new InvalidOperationException("This ROM's description code isn't the US game's (another patch changed it), so the descriptions can't be moved.");
        // the game's entry for each new one: items and spells as they are; 0x300 + n is the game's 0x230 + n
        var texts = new byte[MovedCount][];
        for (int e = 0; e < MovedCount; e++)
        {
            int src = e < IpBase ? e : e >= 0x300 && e - 0xD0 < Count ? e - 0xD0 : -1;
            texts[e] = src >= 0 && src < Count ? Raw(rom, src) : new byte[] { 0 };
        }
        int block = L2Rom.Allocate(rom, MovedSize);
        rom.WriteBytes(block, new byte[MovedSize]);
        rom.WriteBytes(block, Encoding.ASCII.GetBytes(Magic));
        int b = block + HeaderSize, start = b + MovedCount * 2;
        var placed = new Dictionary<string, int>();
        var blob = new List<byte>();
        for (int e = 0; e < MovedCount; e++)
        {
            string key = Convert.ToHexString(texts[e]);
            if (!placed.TryGetValue(key, out int at)) { at = start + blob.Count; placed[key] = at; blob.AddRange(texts[e]); }
            rom.WriteUInt16Le(b + e * 2, (ushort)(at - b));
        }
        if (start + blob.Count > block + MovedSize) throw new InvalidOperationException("The descriptions don't fit their new block.");
        rom.WriteBytes(start, blob.ToArray());
        // the code: no "take 0xD0 off", and the four reads at the new place
        rom.WriteBytes(MapSite, Enumerable.Repeat((byte)0xEA, MapOriginal.Length).ToArray());
        int snes = L2Rom.FileToSnes(b);
        foreach (int s in ReadSites) rom.WriteBytes(s + 1, new[] { (byte)snes, (byte)(snes >> 8), (byte)(snes >> 16) });
        // the old space is free now
        rom.WriteBytes(Table, new byte[DataEnd - Table]);
        rom.WriteBytes(Table, Encoding.ASCII.GetBytes(FreeMark));
    }
}
