using System.Text;

namespace LufiaForge.Core.Lufia2;

// lufia 2 descriptions of items, spells and IP attacks (US rom, found 2026-10-23: the routine at $85:C4F1 takes entry
// $0A11, takes 0xD0 off values from 0x300, and reads a u16 offset from $97:8000, file 0xB8000). the game asks for
// item n as n, spell n as 0x200 + n and IP attack n (counted from 1, like items name them) as 0x300 + n, so the
// table's entry is item n, 0x200 + n spell n, 0x230 + n IP attack n (0x231 the first one)
// a description: 00 ends it, 0A new line, 20-7F letters, 80-FF description word (byte & 7F), 05-09 xx description
// word (code - 5) * 0xFF + xx + 0x80, 10-1F xx dictionary word (code - 0x10) * 0xFF + xx. the 624 description words
// are u16 offsets from $9E:DDCC (file 0xF5DCC), right after the item names. the texts are packed one after another
// from 0xB85B4 to 0xBB0A0, its last 00 (identical ones shared), and the game's own fill that space to the last byte
//
// moved descriptions (Move, 2026-10-25): the table and the texts go to the added space with room to spare and a
// table of 0x400 entries indexed by the game's own number (item n, 0x200 + spell, 0x300 + IP attack counted from 1),
// so spells 28-FE and IP attacks past the game's 168 get descriptions of their own. five places in $85:C4F1 change:
// the "take 0xD0 off" (9 bytes at $85:C4FD) becomes NOPs and its four LDA $978000,X read the new place. the old
// space ($97:8000-$97:B0A0) is left free for the IP attack records (L2Magic)
public static class L2Descriptions
{
    public const int Table = 0xB8000, Count = 0x2D9, SpellBase = 0x200, IpBase = 0x230;
    public const int Words = 0xF5DCC, DataEnd = 0xBB0A1;

    // moved descriptions
    private const string Magic = "LF2DS";
    // "LF2FREE" at the old table: its space is free (the descriptions got moved)
    public const string FreeMark = "LF2FREE";
    private const int MovedCount = 0x400, MovedSize = 0x6000, HeaderSize = 0x10;
    private const int MapSite = 0x2C4FD;
    private static readonly byte[] MapOriginal = { 0xC9, 0x00, 0x03, 0x90, 0x04, 0x38, 0xE9, 0xD0, 0x00 };
    // the four LDA $978000,X (BF 00 80 97) of $85:C4F1: the table, then the text three times
    private static readonly int[] ReadSites = { 0x2C508, 0x2C516, 0x2C53A, 0x2C56F };

    // file offset of the moved block's header, -1 if the descriptions are where the game has them
    public static int Block(RomBuffer rom)
    {
        if (rom.Length <= ReadSites[0] + 4 || rom.ReadByte(ReadSites[0]) != 0xBF) return -1;
        int snes = (int)rom.ReadUInt24Le(ReadSites[0] + 1);
        if (snes == 0x978000) return -1;
        int headerAt = L2Rom.SnesToFile(snes) - HeaderSize;
        return headerAt >= 0 && headerAt + MovedSize <= rom.Length && Encoding.ASCII.GetString(rom.ReadBytes(headerAt, Magic.Length)) == Magic ? headerAt : -1;
    }

    public static bool IsMoved(RomBuffer rom) => Block(rom) >= 0;

    // where the table is (the texts' offsets count from there too)
    private static int Base(RomBuffer rom) => Block(rom) is var block && block >= 0 ? block + HeaderSize : Table;

    // how many entries the table has
    public static int EntryCount(RomBuffer rom) => IsMoved(rom) ? MovedCount : Count;

    private static int End(RomBuffer rom) => Block(rom) is var block && block >= 0 ? block + MovedSize : DataEnd;

    // the entry of spell spell (the same in both layouts. in the game's own one spells from 30 on would share the IP
    // attacks' entries, so spells we add come with moved descriptions)
    public static int SpellEntry(int spell) => SpellBase + spell;

    // the entry of IP attack ip (counted from 0, like our IP attack list)
    public static int IpEntry(RomBuffer rom, int ip) => (IsMoved(rom) ? 0x301 : IpBase + 1) + ip;

    // the words read last, with the rom and version they came from (one object, see L2Text's cache). keyed on the
    // version too: an Undo put the US word list back while the cache kept the Dutch one
    private sealed record Cache(RomBuffer Rom, int Version, string[] Words);
    private static Cache? _cache;

    public static string[] DescriptionWords(RomBuffer rom)
    {
        if (_cache is { } cache && ReferenceEquals(cache.Rom, rom) && cache.Version == rom.Version) return cache.Words;
        int list = WordsAt(rom);
        int count = rom.ReadUInt16Le(list) / 2;
        var words = new string[count];
        for (int k = 0; k < count; k++) words[k] = new string(rom.ReadUntil(list + rom.ReadUInt16Le(list + k * 2), 0, 40).Select(b => L2Charset.ToChar(rom, b) ?? '?').ToArray());
        _cache = new Cache(rom, rom.Version, words);
        return words;
    }

    // the two LDA $9E:DDCC,X of $85:C4F1 that read the word list (operands)
    private static readonly int[] WordSites = { 0x2C58A, 0x2C598 };

    // where the word list is: read from the code (SetWords moves it), else the game's place
    public static int WordsAt(RomBuffer rom)
    {
        if (rom.Length > WordSites[1] + 3 && WordSites.All(s => rom.ReadByte(s - 1) == 0xBF))
        {
            int snes = (int)rom.ReadUInt24Le(WordSites[0]);
            if (snes == (int)rom.ReadUInt24Le(WordSites[1])) return L2Rom.SnesToFile(snes);
        }
        return Words;
    }

    // puts a new word list in the added space and points the code at it (the caller encodes the descriptions again
    // with it, since what the old codes meant changes)
    public static void SetWords(RomBuffer rom, IReadOnlyList<string> words)
    {
        if (!WordSites.All(s => rom.ReadByte(s - 1) == 0xBF)) throw new InvalidOperationException("This ROM's description code isn't the US game's.");
        var head = new List<byte>(); var body = new List<byte>();
        int count = Math.Min(words.Count, 0x80 + 5 * 0xFF);
        for (int k = 0; k < count; k++)
        {
            head.Add((byte)(count * 2 + body.Count)); head.Add((byte)((count * 2 + body.Count) >> 8));
            body.AddRange(words[k].Select(c => L2Charset.ToByte(rom, c) ?? (byte)'?'));
            body.Add(0);
        }
        var bytes = head.Concat(body).ToArray();
        int listAt = L2Rom.Allocate(rom, bytes.Length);
        rom.WriteBytes(listAt, bytes);
        PointWords(rom, listAt);
    }

    // points the code at the word list at file offset at
    public static void PointWords(RomBuffer rom, int at)
    {
        int snes = L2Rom.FileToSnes(at);
        foreach (int site in WordSites) rom.WriteBytes(site, new[] { (byte)snes, (byte)(snes >> 8), (byte)(snes >> 16) });
        _cache = null;
    }

    public static int At(RomBuffer rom, int entry)
    {
        int tableAt = Base(rom);
        return tableAt + rom.ReadUInt16Le(tableAt + entry * 2);
    }

    // the raw bytes of entry entry, its 00 included
    public static byte[] Raw(RomBuffer rom, int entry) => RawAt(rom, At(rom, entry));

    // the description as text, new lines as '\n'
    public static string Read(RomBuffer rom, int entry)
    {
        if (entry < 0 || entry >= EntryCount(rom)) return "";
        var words = DescriptionWords(rom);
        var dict = L2Text.Words(rom);
        var bytes = Raw(rom, entry);
        var sb = new StringBuilder();
        for (int i = 0; i < bytes.Length && bytes[i] != 0; i++)
        {
            byte c = bytes[i];
            if (c >= 0x80) sb.Append(words[c & 0x7F]);
            else if (c == 0x0A) sb.Append('\n');
            else if (c >= 0x20) sb.Append(L2Charset.ToChar(rom, c) ?? '?');
            else if (c >= 0x10) sb.Append(Lookup(dict, (c - 0x10) * 0xFF + bytes[++i]));
            else if (c >= 5) sb.Append(Lookup(words, (c - 5) * 0xFF + bytes[++i] + 0x80));
            else sb.Append($"[{c:X2}]");
        }
        return sb.ToString();
    }

    private static string Lookup(string[] list, int k) => k < list.Length ? list[k] : "?";

    // encodes a description (the longest matching word first, description words 0-7F are one byte, the others two),
    // its 00 included
    public static byte[] Encode(RomBuffer rom, string text)
    {
        var words = DescriptionWords(rom);
        var dict = L2Text.Words(rom);
        var candidates = words.Select((w, k) => (w, code: k < 0x80 ? new[] { (byte)(0x80 | k) } : new[] { (byte)(5 + (k - 0x80) / 0xFF), (byte)((k - 0x80) % 0xFF) }))
            .Concat(dict.Select((w, k) => (w, code: new[] { (byte)(0x10 + k / 0xFF), (byte)(k % 0xFF) })))
            .Where(c => c.w.Length > c.code.Length)
            .OrderByDescending(c => c.w.Length).ThenBy(c => c.code.Length).ToList();
        var output = new List<byte>();
        text = text.Replace("\r\n", "\n");
        int i = 0;
        while (i < text.Length)
        {
            char ch = text[i];
            if (ch == '\n') { output.Add(0x0A); i++; continue; }
            var hit = candidates.FirstOrDefault(c => string.CompareOrdinal(text, i, c.w, 0, c.w.Length) == 0);
            if (hit.w != null) { output.AddRange(hit.code); i += hit.w.Length; continue; }
            // 10-1F are dictionary words here, so only the letters at 20 and up (in the Dutch font " is e-umlaut...)
            output.Add(L2Charset.ToByte(rom, ch) is byte letter && letter >= 0x20 ? letter : (byte)'?');
            i++;
        }
        output.Add(0);
        return output.ToArray();
    }

    private static int TextStart(RomBuffer rom) =>
        IsMoved(rom) ? Base(rom) + MovedCount * 2 : Enumerable.Range(0, Count).Min(e => At(rom, e));

    // space the descriptions take and have
    public static (int Used, int Room) Space(RomBuffer rom)
    {
        int count = EntryCount(rom), start = TextStart(rom);
        int used = Enumerable.Range(0, count).Select(e => At(rom, e)).Distinct().Sum(a => RawAt(rom, a).Length);
        return (used, End(rom) - start);
    }

    private static byte[] RawAt(RomBuffer rom, int at)
    {
        int position = at;
        while (position < rom.Length && rom.ReadByte(position) != 0) position += rom.ReadByte(position) is >= 5 and <= 9 or >= 0x10 and < 0x20 ? 2 : 1;
        return rom.ReadBytes(at, position + 1 - at);
    }

    // sets entry entry's text: all descriptions get packed again in their area (the others keep their bytes,
    // identical ones are shared). if they don't fit where the game has them, they get moved to the added space first
    // (Move). returns null, or why it can't be done (too long)
    public static string? Write(RomBuffer rom, int entry, string text)
    {
        if (entry < 0 || entry >= EntryCount(rom)) return "This entry has no description in this ROM.";
        if (Read(rom, entry) == text.Replace("\r\n", "\n")) return null;
        var mine = Encode(rom, text);
        int count = EntryCount(rom);
        // over its own bytes if it isn't longer and no other entry reads them (the others stay where they are)
        int textAt = At(rom, entry), oldLength = RawAt(rom, textAt).Length;
        if (mine.Length <= oldLength && Enumerable.Range(0, count).All(e => e == entry || At(rom, e) is var other && (other < textAt || other >= textAt + oldLength)))
        {
            rom.WriteBytes(textAt, mine);
            // clear what the old text left behind (it read as some other hack's data after the last text)
            if (mine.Length < oldLength) rom.WriteBytes(textAt + mine.Length, new byte[oldLength - mine.Length]);
            return null;
        }
        // the others keep their bytes. if that doesn't fit, they all get encoded again (that saves about 100 bytes)
        if (Pack(rom, Enumerable.Range(0, count).Select(e => e == entry ? mine : Raw(rom, e)).ToList()) == null) return null;
        if (!IsMoved(rom) && CanMove(rom))
        {
            // the game's space is full to the last byte, so move them (this entry's number is the same in both
            // layouts for items and spells, an IP attack's moves from 0x230 + n to 0x300 + n)
            int moved = entry >= IpBase && entry < Count ? entry + 0xD0 : entry;
            Move(rom);
            return Write(rom, moved, text);
        }
        return Pack(rom, Enumerable.Range(0, count).Select(e => e == entry ? mine : Encode(rom, Read(rom, e))).ToList());
    }

    // sets many entries at once (numbers as in the rom's layout now), packed once. moved to the added space if they
    // don't fit where the game has them. returns null, or why they don't fit
    public static string? WriteMany(RomBuffer rom, IReadOnlyDictionary<int, string> texts)
    {
        int count = EntryCount(rom);
        var mine = texts.Where(kv => kv.Key >= 0 && kv.Key < count).ToDictionary(kv => kv.Key, kv => Encode(rom, kv.Value));
        if (Pack(rom, Enumerable.Range(0, count).Select(e => mine.TryGetValue(e, out var encoded) ? encoded : Raw(rom, e)).ToList()) == null) return null;
        if (!IsMoved(rom) && CanMove(rom))
        {
            Move(rom);
            return WriteMany(rom, texts.ToDictionary(kv => kv.Key >= IpBase && kv.Key < Count ? kv.Key + 0xD0 : kv.Key, kv => kv.Value));
        }
        return Pack(rom, Enumerable.Range(0, count).Select(e => mine.TryGetValue(e, out var encoded) ? encoded : Encode(rom, Read(rom, e))).ToList());
    }

    // writes the descriptions packed (identical ones shared), or returns why they don't fit
    private static string? Pack(RomBuffer rom, List<byte[]> all)
    {
        // (packing rewrites the whole space, so not over another hack's data in it)
        if (HasOtherData(rom))
            return "Another hack keeps data of its own in the descriptions' space ($97:8000-$97:B0A0), so they can't be packed again safely. A description no longer than before can still be changed.";
        int count = all.Count, start = TextStart(rom), end = End(rom), tableAt = Base(rom);
        var placed = new Dictionary<string, int>();
        var blob = new List<byte>();
        var offsets = new int[count];
        // in the order they sit now, so the untouched ones keep their places up to the edited one
        foreach (int e in Enumerable.Range(0, count).OrderBy(e => At(rom, e)).ThenBy(e => e))
        {
            string key = Convert.ToHexString(all[e]);
            if (!placed.TryGetValue(key, out int textAt)) { textAt = start + blob.Count; placed[key] = textAt; blob.AddRange(all[e]); }
            offsets[e] = textAt;
        }
        if (start + blob.Count > end) return $"{start + blob.Count - end} bytes too long for the descriptions' space (shorten this or another description).";
        rom.WriteBytes(start, blob.ToArray());
        for (int position = start + blob.Count; position < end; position++) rom.WriteByte(position, 0);
        for (int e = 0; e < count; e++) rom.WriteUInt16Le(tableAt + e * 2, (ushort)(offsets[e] - tableAt));
        return null;
    }

    // the bytes of the game's code Move changes (file offset, length)
    internal static IEnumerable<(int At, int Length)> CodeSites() =>
        new[] { (MapSite, MapOriginal.Length) }.Concat(ReadSites.Select(s => (s + 1, 3)));

    // whether the descriptions can be moved (the reading code is still the game's, and nothing but descriptions
    // lives in their space, which moving them clears)
    public static bool CanMove(RomBuffer rom) => WhyCantMove(rom) == null;

    // why the descriptions can't be moved, or null
    public static string? WhyCantMove(RomBuffer rom)
    {
        if (IsMoved(rom)) return null;
        if (rom.Length < MapSite + MapOriginal.Length || !rom.ReadBytes(MapSite, MapOriginal.Length).SequenceEqual(MapOriginal) ||
            !ReadSites.All(s => rom.ReadByte(s) == 0xBF && rom.ReadUInt24Le(s + 1) == 0x978000))
            return "This ROM's description code isn't the US game's (another patch changed it), so the descriptions can't be moved.";
        if (HasOtherData(rom))
            return "Another hack keeps data of its own in the descriptions' space ($97:8000-$97:B0A0), so the descriptions can't be moved or packed again safely.";
        return null;
    }

    // how many bytes after the last description, besides 00 / FF fill, before they count as another hack's data.
    // (the fan hacks fill the space to its last byte or one before, our own packing leaves zeros)
    private const int OtherDataLimit = 64;

    // something besides the descriptions lives in their space in the game's layout ($97:8000-$97:B0A0): more than
    // OtherDataLimit bytes that aren't fill after the last description, where a hack that made them shorter would put
    // its own data. moving them clears the space and packing them rewrites it, so that data would get lost. (checked on
    // Frue, Spekkio, Kureji, Foomy and a randomizer: all theirs is descriptions. bytes left between texts by a shorter
    // description written over a longer one don't count, those are old text)
    public static bool HasOtherData(RomBuffer rom)
    {
        if (IsMoved(rom) || rom.Length < DataEnd) return false;
        int last = Table + Count * 2;
        for (int e = 0; e < Count; e++)
        {
            int textAt = At(rom, e);
            if (textAt < Table || textAt >= DataEnd) continue;
            last = Math.Max(last, textAt + RawAt(rom, textAt).Length);
        }
        int other = 0;
        for (int position = last; position < DataEnd; position++)
            if (rom.ReadByte(position) is not (0x00 or 0xFF) && ++other > OtherDataLimit) return true;
        return false;
    }

    // moves the table and the texts to the added space (once): a table of 0x400 entries indexed the way the game asks
    // (item n, 0x200 + spell n, 0x300 + IP attack n counted from 1) and room for about 12 KB more text. the old space
    // gets cleared and marked free (FreeMark)
    public static void Move(RomBuffer rom)
    {
        if (IsMoved(rom)) return;
        if (WhyCantMove(rom) is { } why) throw new InvalidOperationException(why);
        // the game's entry for each new one: items and spells as they are, 0x300 + n is the game's 0x230 + n
        var texts = new byte[MovedCount][];
        for (int e = 0; e < MovedCount; e++)
        {
            int source = e < IpBase ? e : e >= 0x300 && e - 0xD0 < Count ? e - 0xD0 : -1;
            texts[e] = source >= 0 && source < Count ? Raw(rom, source) : new byte[] { 0 };
        }
        int block = L2Rom.Allocate(rom, MovedSize);
        rom.WriteBytes(block, new byte[MovedSize]);
        rom.WriteBytes(block, Encoding.ASCII.GetBytes(Magic));
        int tableAt = block + HeaderSize, start = tableAt + MovedCount * 2;
        var placed = new Dictionary<string, int>();
        var blob = new List<byte>();
        for (int e = 0; e < MovedCount; e++)
        {
            string key = Convert.ToHexString(texts[e]);
            if (!placed.TryGetValue(key, out int textAt)) { textAt = start + blob.Count; placed[key] = textAt; blob.AddRange(texts[e]); }
            rom.WriteUInt16Le(tableAt + e * 2, (ushort)(textAt - tableAt));
        }
        if (start + blob.Count > block + MovedSize) throw new InvalidOperationException("The descriptions don't fit their new block.");
        rom.WriteBytes(start, blob.ToArray());
        // the code: no "take 0xD0 off", and the four reads at the new place
        rom.WriteBytes(MapSite, Enumerable.Repeat((byte)0xEA, MapOriginal.Length).ToArray());
        int snes = L2Rom.FileToSnes(tableAt);
        foreach (int site in ReadSites) rom.WriteBytes(site + 1, new[] { (byte)snes, (byte)(snes >> 8), (byte)(snes >> 16) });
        // the old space is free now
        rom.WriteBytes(Table, new byte[DataEnd - Table]);
        rom.WriteBytes(Table, Encoding.ASCII.GetBytes(FreeMark));
    }
}
