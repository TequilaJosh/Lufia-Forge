using LufiaForge.Core;

namespace LufiaForge.Modules.TextEditor;

// decodes Lufia 1 dialogue strings and encodes them back
//
// the whole encoding (from AllOriginal.tbl, Digisalt's Lufiatools 2.0, flobo 2011; the MTE table spots I checked
// against the US ROM by following the pointers):
//
//   0x00             end of string
//   0x04             page break (player presses A)
//   0x05             new line in the dialogue box
//   0x07 + 1 byte    character name (0x00=Hero, 0x01=Lufia, 0x02=Aguro, 0x03=Jerin...)
//   0x09 + 1 byte    item name      (0x00=Nothing, 0x01=Leather Armor, ...)
//   0x0A + 1 byte    spell name     (0x00=Flash, 0x01=Spark, 0x02=Blaze, ...)
//   0x0B + 1 byte    town name      (0x01=Alekia, 0x02=Chatam, 0x03=Sheran...)
//   0x0C + 1 byte    MTE3 word, lowercase   (1-byte index 0x00-0xFF)
//   0x0D + 1 byte    MTE3 word, capitalized (same index, first letter uppercase)
//   0x10-0x1F        MTE4: special one-byte sequences ('s, ed, ing, I'm, Sinistral...)
//   0x20-0x7E        normal printable ASCII
//   0x80-0xBF        MTE1: one-byte compressed words, lowercase (the, you, to, it...)
//   0xC0-0xFF        MTE1: one-byte compressed words, capitalized (The, You, To, It...)
//
// MTE tables (Multiple Title Entry), all in ROM bank 0x0A (file 0x50000-0x57FFF):
//   MTE4 ptr table  0x54A50  16 words  codes 0x10-0x1F
//   MTE1 ptr table  0x54AC0  64 words  codes 0x80-0xFF (lower 0x80-0xBF, cap 0xC0-0xFF)
//   MTE3 ptr table  0x54C17  256 words codes 0x0C 00 - 0x0C FF
//   MTE2            (no table of its own, the decoder capitalizes MTE3 words for 0x0D codes)
//
// pointers are LE 16-bit SNES bank-relative addresses, file = ptr + 0x48000
// the words sit back to back in the ROM with NO null terminators, the pointer pairs give the boundaries
//
// the expanded-MTE ASM patch (BahaBulle / Hiei-) makes the MTE tables bigger. we read every table from the ROM
// itself at runtime, so the original ROM and any patched one both just work
public static class TextDecoder
{
    // mte word cache, rebuilt when a new ROM loads. everything reads through EnsureCache() so it's never stale

    private static RomBuffer? _cachedRom;

    // the 64 lowercase MTE1 words, index 0-63 (codes 0x80-0xBF)
    private static string[] _mte1Words = Array.Empty<string>();

    // the 256 lowercase MTE3 words, index 0-255 (codes 0x0C 00-0x0C FF)
    private static string[] _mte3Words = Array.Empty<string>();

    // word -> bytes table for Encode(), longest word first so the greedy match picks the best one
    private static (string Word, byte[] Encoding)[] _encodeTable =
        Array.Empty<(string, byte[])>();

    // throws the cache away. call it after loading a new ROM
    public static void InvalidateDictionaryCache() => _cachedRom = null;

    private static readonly object CacheLock = new();

    private static void EnsureCache(RomBuffer rom)
    {
        if (ReferenceEquals(rom, _cachedRom)) return;
        lock (CacheLock)
        {
            if (ReferenceEquals(rom, _cachedRom)) return;
            // fill every table first and set the ROM they belong to last. when it was set first, another thread (the
            // dialogue list gets built on one) saw it and used tables that were still empty or from the ROM before
            var mte1 = ReadMteWordsFromPtrTable(rom, Lufia1Constants.Mte1PtrTableOffset, Lufia1Constants.Mte1EntryCount);
            var mte3 = ReadMteWordsFromPtrTable(rom, Lufia1Constants.Mte3PtrTableOffset, Lufia1Constants.Mte3EntryCount);
            var encode = BuildEncodeTable(mte1, mte3);
            var letters = Core.DutchLetters.IsApplied(rom) ? Core.DutchLetters.Map.ToDictionary(m => m.Code, m => m.Letter) : new();
            var codes = letters.ToDictionary(kv => kv.Value, kv => kv.Key);
            _mte1Words = mte1; _mte3Words = mte3; _encodeTable = encode; _letters = letters; _letterCodes = codes;
            System.Threading.Thread.MemoryBarrier();
            _cachedRom = rom;
        }
    }

    // the Dutch letters in the ROM's font (Core.DutchLetters): code -> letter and back
    private static Dictionary<byte, char> _letters = new();
    private static Dictionary<char, byte> _letterCodes = new();

    // decoding

    // decodes one dialogue string starting at offset in the ROM. returns the text and how many raw bytes it used.
    // all the MTE tables are read from the ROM itself, so it handles the original US ROM and any expanded-MTE one
    public static DecodeResult Decode(RomBuffer rom, int offset, bool expandMte = true, bool stopAtPageBreak = false)
    {
        EnsureCache(rom);

        var text     = new System.Text.StringBuilder();
        var tokens   = new List<TextToken>();
        int position = offset;
        int limit    = Math.Min(offset + 4096, rom.Length);

        while (position < limit)
        {
            byte code = rom.ReadByte(position);

            // 0x00  end of string
            if (code == Lufia1Constants.CtrlEndString)
            {
                tokens.Add(new TextToken(TextTokenKind.EndString, position, 1, "[END]"));
                position++;
                break;
            }

            // 0x04  page break
            if (code == Lufia1Constants.CtrlPageBreak)
            {
                tokens.Add(new TextToken(TextTokenKind.PageBreak, position, 1, "\n--- [PAGE] ---\n"));
                text.Append("\n[PAGE]\n");
                position++;
                if (stopAtPageBreak) break;   // event-script text: 04 ends the box
                continue;
            }

            // 0x05  new line
            if (code == Lufia1Constants.CtrlNewline)
            {
                tokens.Add(new TextToken(TextTokenKind.Newline, position, 1, "\n"));
                text.Append('\n');
                position++;
                continue;
            }

            // 0x07 + 1 byte  character name
            if (code == Lufia1Constants.CtrlCharName)
            {
                byte charId = position + 1 < limit ? rom.ReadByte(position + 1) : (byte)0;
                string label = Lufia1Constants.CharacterNames.TryGetValue(charId, out string? name)
                    ? name : $"[CHAR:0x{charId:X2}]";
                tokens.Add(new TextToken(TextTokenKind.CharName, position, 2, label, charId));
                text.Append(label);
                position += 2;
                continue;
            }

            // 0x06 / 0x0F + 1 byte  control codes with an operand (event-script text)
            if (code == 0x06 || code == 0x0F)
            {
                byte arg = position + 1 < limit ? rom.ReadByte(position + 1) : (byte)0;
                string tag = $"[{code:X2}:{arg:X2}]";
                tokens.Add(new TextToken(TextTokenKind.Control, position, 2, tag, arg));
                text.Append(tag);
                position += 2;
                continue;
            }

            // 0x09 + 1 byte  item name
            if (code == Lufia1Constants.CtrlItemName)
            {
                byte itemId = position + 1 < limit ? rom.ReadByte(position + 1) : (byte)0;
                string item = $"[ITEM:0x{itemId:X2}]";
                tokens.Add(new TextToken(TextTokenKind.Control, position, 2, item, itemId));
                text.Append(item);
                position += 2;
                continue;
            }

            // 0x0A + 1 byte  spell name
            if (code == Lufia1Constants.CtrlSpellName)
            {
                byte spellId = position + 1 < limit ? rom.ReadByte(position + 1) : (byte)0;
                string spell = $"[SPELL:0x{spellId:X2}]";
                tokens.Add(new TextToken(TextTokenKind.Control, position, 2, spell, spellId));
                text.Append(spell);
                position += 2;
                continue;
            }

            // 0x0B + 1 byte  town name
            if (code == Lufia1Constants.CtrlTownName)
            {
                byte townId = position + 1 < limit ? rom.ReadByte(position + 1) : (byte)0;
                string town = Lufia1Constants.TownNames.TryGetValue(townId, out string? townName)
                    ? townName : $"[TOWN:0x{townId:X2}]";
                tokens.Add(new TextToken(TextTokenKind.Ascii, position, 2, town));
                text.Append(town);
                position += 2;
                continue;
            }

            // 0x0C + 1 byte  MTE3 word (lowercase)
            // 0x0D + 1 byte  MTE3 word (capitalized)
            // fixed: it used to read 2 bytes after the control (3 bytes total). it's control + 1-byte index = 2 bytes
            if (code == Lufia1Constants.CtrlDictionaryRef ||
                code == Lufia1Constants.CtrlDictionaryRefCap)
            {
                if (position + 1 >= limit) break;
                byte index      = rom.ReadByte(position + 1);
                bool capitalize = (code == Lufia1Constants.CtrlDictionaryRefCap);

                string word;
                if (expandMte && index < _mte3Words.Length)
                {
                    word = _mte3Words[index];
                    if (capitalize && word.Length > 0)
                        word = char.ToUpper(word[0]) + word[1..];
                }
                else
                {
                    word = capitalize ? $"[DCAP:{index:X2}]" : $"[DICT:{index:X2}]";
                }

                tokens.Add(new TextToken(TextTokenKind.DictionaryRef, position, 2, word, index));
                text.Append(word);
                position += 2;
                continue;
            }

            // 0x10-0x1F  MTE4: special one-byte compressed sequences
            // ('s, ed, ing, I'm, I'll, I've, Alumina, Sinistral, Dual, Falcon, Glasdar, Welcome, Raile, Lilah, Reyna, Shaia)
            if (code >= 0x10 && code <= 0x1F)
            {
                string sequence = Lufia1Constants.SpecialSequences.TryGetValue(code, out string? special)
                    ? special : $"[SP:0x{code:X2}]";
                tokens.Add(new TextToken(TextTokenKind.Ascii, position, 1, sequence));
                text.Append(sequence);
                position++;
                continue;
            }

            // 0x20-0x7E  normal printable ASCII
            if (code >= 0x20 && code <= 0x7E)
            {
                char character = _letters.TryGetValue(code, out char letter) ? letter : (char)code;
                tokens.Add(new TextToken(TextTokenKind.Ascii, position, 1, character.ToString()));
                text.Append(character);
                position++;
                continue;
            }

            // 0x80-0xFF  MTE1: one-byte compressed words
            //   0x80-0xBF  lowercase (the, you, to, it, of, that, is, in...)
            //   0xC0-0xFF  the capitalized ones (The, You, To, It, Of, That, Is, In...)
            // fixed: the word boundaries now come from the MTE1 pointer table (file 0x54AC0) instead of a hardcoded
            // table, so the original ROM and any expanded-MTE one both work
            if (code >= 0x80)
            {
                bool capitalize = code >= 0xC0;
                int  index      = code - (capitalize ? 0xC0 : 0x80);

                string word;
                if (expandMte && index < _mte1Words.Length)
                {
                    word = _mte1Words[index];
                    if (capitalize && word.Length > 0)
                        word = char.ToUpper(word[0]) + word[1..];
                }
                else
                {
                    // fall back to the hardcoded table when there's no ROM table or the index is out of range
                    word = Lufia1Constants.SingleByteWords.TryGetValue(code, out string? knownWord)
                        ? knownWord : $"[W:0x{code:X2}]";
                }

                tokens.Add(new TextToken(TextTokenKind.DictionaryRef, position, 1, word, code));
                text.Append(word);
                position++;
                continue;
            }

            // anything else is a control code we don't know
            string ctrlLabel = Lufia1Constants.ControlCodeLabels.TryGetValue(code, out string? controlName)
                ? controlName : $"[0x{code:X2}]";
            tokens.Add(new TextToken(TextTokenKind.Control, position, 1, ctrlLabel));
            text.Append(ctrlLabel);
            position++;
        }

        return new DecodeResult(text.ToString(), tokens, position - offset);
    }

    // MTE pointer table reader (MTE1 and MTE3 use it, MTE4 could too)

    // reads count words from an MTE pointer table
    //
    // the table at ptrTableFileOffset holds count+1 LE 16-bit SNES bank-relative pointers in a row. the words sit
    // back to back in the ROM with no null terminators, each word is the bytes
    //   [ptr[i] + MtePtrBase .. ptr[i+1] + MtePtrBase)
    //
    // works for all four MTE tables since they're all in bank 0x0A (MtePtrBase = 0x48000 = 0x0A*0x8000 - 0x8000)
    public static string[] ReadMteWordsFromPtrTable(RomBuffer rom,
                                                     int      ptrTableFileOffset,
                                                     int      count)
    {
        var words  = new string[count];
        int tableEnd = ptrTableFileOffset + (count + 1) * 2;
        if (tableEnd > rom.Length) return words;   // ROM too short, so empty strings

        for (int i = 0; i < count; i++)
        {
            ushort startPtr = rom.ReadUInt16Le(ptrTableFileOffset + i * 2);
            ushort endPtr   = rom.ReadUInt16Le(ptrTableFileOffset + i * 2 + 2);

            int fileStart = startPtr + Lufia1Constants.MtePtrBase;
            int fileEnd   = endPtr   + Lufia1Constants.MtePtrBase;

            if (fileStart >= fileEnd || fileEnd > rom.Length)
            {
                words[i] = string.Empty;
                continue;
            }

            var sb = new System.Text.StringBuilder(fileEnd - fileStart);
            for (int at = fileStart; at < fileEnd; at++)
            {
                byte wordByte = rom.ReadByte(at);
                // words are printable ASCII only, stop early if we hit garbage
                if (wordByte < 0x20 || wordByte > 0x7E) break;
                sb.Append((char)wordByte);
            }
            words[i] = sb.ToString();
        }

        return words;
    }

    // ReadAllDictionaryWords, the MTE3 word list (the Dictionary tab and export use it)

    // returns all 256 MTE3 words as (file offset, word) pairs in dictionary index order. the word boundaries come
    // from the MTE3 pointer table (file 0x54C17)
    public static List<(int Offset, string Word)> ReadAllDictionaryWords(RomBuffer rom)
    {
        var result = new List<(int, string)>(Lufia1Constants.Mte3EntryCount);
        int tableOffset = Lufia1Constants.Mte3PtrTableOffset;
        int count    = Lufia1Constants.Mte3EntryCount;
        int tableEnd = tableOffset + (count + 1) * 2;
        if (tableEnd > rom.Length) return result;

        for (int i = 0; i < count; i++)
        {
            ushort startPtr = rom.ReadUInt16Le(tableOffset + i * 2);
            ushort endPtr   = rom.ReadUInt16Le(tableOffset + i * 2 + 2);

            int fileStart = startPtr + Lufia1Constants.MtePtrBase;
            int fileEnd   = endPtr   + Lufia1Constants.MtePtrBase;

            if (fileStart >= fileEnd || fileEnd > rom.Length) continue;

            var sb = new System.Text.StringBuilder(fileEnd - fileStart);
            for (int at = fileStart; at < fileEnd; at++)
            {
                byte wordByte = rom.ReadByte(at);
                if (wordByte < 0x20 || wordByte > 0x7E) break;
                sb.Append((char)wordByte);
            }
            if (sb.Length > 0)
                result.Add((fileStart, sb.ToString()));
        }

        return result;
    }

    // ReadDictionaryWord, kept so old callers still work (reads until a null)

    // reads an ASCII word at fileOffset that ends in a null or a control code
    // MTE3 words have NO null at the end, so for words by index use ReadAllDictionaryWords or
    // ReadMteWordsFromPtrTable, they go through the pointer table
    public static string ReadDictionaryWord(RomBuffer rom, int fileOffset)
    {
        var sb  = new System.Text.StringBuilder();
        int position = fileOffset;
        int end = Math.Min(fileOffset + 64, rom.Length);

        while (position < end)
        {
            byte wordByte = rom.ReadByte(position);
            if (wordByte == 0x00 || wordByte < 0x20) break;
            if (wordByte <= 0x7E) sb.Append((char)wordByte);
            else break;
            position++;
        }
        return sb.ToString();
    }

    // encoding (edited text back to ROM bytes)

    // turns plain text back into Lufia 1 bytes, compressing with the MTE words again
    //
    // with a rom, the encoder reads the MTE word tables from that ROM and does a greedy longest-match so the edited
    // dialogue fits in its old byte budget. words from all four MTE tables get tried, longest first. with rom = null
    // it only has the hardcoded MTE4 / SpecialSequences table, so no MTE1 or MTE3 compression
    //
    // control tags you can use in the text:
    //   [PAGE]        -> 0x04
    //   \n  or [NL]   -> 0x05
    //   [Hero]        -> 0x07 0x00     [Lufia]  -> 0x07 0x01
    //   [Aguro]       -> 0x07 0x02     [Jerin]  -> 0x07 0x03
    //   [Maxim]       -> 0x07 0x04     [Selan]  -> 0x07 0x05
    //   [Guy]         -> 0x07 0x06     [Artea]  -> 0x07 0x07
    //   [END]         -> 0x00 (always added at the end anyway, a typed one is skipped)
    public static byte[] Encode(string text, RomBuffer? rom = null)
    {
        if (rom != null) EnsureCache(rom);

        // the compression table for this call
        (string Word, byte[] Encoding)[] compressTable = (rom != null)
            ? _encodeTable
            : BuildEncodeTable(Array.Empty<string>(), Array.Empty<string>());  // MTE4 only

        var bytes = new List<byte>();
        int i     = 0;

        while (i < text.Length)
        {
            // control tags: [TAG] or [TAG:ARG]
            if (text[i] == '[')
            {
                int close = text.IndexOf(']', i);
                if (close > i)
                {
                    string inner = text[(i + 1)..close];
                    string tag   = inner.ToUpperInvariant();

                    bool handled = true;
                    switch (tag)
                    {
                        case "PAGE":   bytes.Add(0x04); break;
                        case "NL":     bytes.Add(0x05); break;
                        case "END":    /* the end byte, added at the very end */  break;
                        case "HERO":   bytes.Add(0x07); bytes.Add(0x00); break;
                        case "LUFIA":  bytes.Add(0x07); bytes.Add(0x01); break;
                        case "AGURO":  bytes.Add(0x07); bytes.Add(0x02); break;
                        case "JERIN":  bytes.Add(0x07); bytes.Add(0x03); break;
                        case "MAXIM":  bytes.Add(0x07); bytes.Add(0x04); break;
                        case "SELAN":  bytes.Add(0x07); bytes.Add(0x05); break;
                        case "GUY":    bytes.Add(0x07); bytes.Add(0x06); break;
                        case "ARTEA":  bytes.Add(0x07); bytes.Add(0x07); break;
                        default:       handled = TryEncodeRawTag(tag, bytes); break;
                    }

                    if (handled)
                    {
                        i = close + 1;
                        continue;
                    }

                    // a tag we don't know: fall through, write the '[' as it is and let the rest of the loop handle
                    // the characters after it
                }
            }

            // new line
            if (text[i] == '\n')
            {
                bytes.Add(Lufia1Constants.CtrlNewline);
                i++;
                continue;
            }

            // try greedy MTE compression (longest match first)
            bool compressed = false;
            foreach (var (word, encoding) in compressTable)
            {
                if (word.Length == 0) continue;
                if (i + word.Length > text.Length) continue;
                if (string.Compare(text, i, word, 0, word.Length,
                                   StringComparison.Ordinal) == 0)
                {
                    bytes.AddRange(encoding);
                    i += word.Length;
                    compressed = true;
                    break;
                }
            }

            if (compressed) continue;

            // plain printable ASCII
            char character = text[i];
            if (rom != null && _letterCodes.TryGetValue(character, out byte letterCode))
                bytes.Add(letterCode);
            else if (character >= 0x20 && character <= 0x7E)
                bytes.Add((byte)character);
            // anything else that isn't printable or a tag just gets dropped
            i++;
        }

        bytes.Add(Lufia1Constants.CtrlEndString); // always end it
        return bytes.ToArray();
    }

    // raw tags the event editor writes: [ITEM:xx], [SPELL:xx], [TOWN:xx], [CHAR:xx], [xx], [xx:yy] (hex)
    private static bool TryEncodeRawTag(string tag, List<byte> bytes)
    {
        static bool Hex(string s, out byte v) =>
            byte.TryParse(s, System.Globalization.NumberStyles.HexNumber, null, out v) && s.Length is 1 or 2;
        var parts = tag.Split(':');
        if (parts.Length == 2 && Hex(parts[1], out byte arg))
        {
            byte? code = parts[0] switch { "ITEM" => 0x09, "SPELL" => 0x0A, "TOWN" => 0x0B, "CHAR" => 0x07, _ => null };
            if (code == null && Hex(parts[0], out byte rawCode)) code = rawCode;
            if (code == null) return false;
            bytes.Add(code.Value); bytes.Add(arg);
            return true;
        }
        if (parts.Length == 1 && Hex(parts[0], out byte single)) { bytes.Add(single); return true; }
        return false;
    }

    // building the Encode() lookup table

    // the word -> bytes lookup Encode() uses, sorted longest word first. MTE4 is always in, MTE1 if mte1Words has
    // words, MTE3 if mte3Words has words
    private static (string Word, byte[] Encoding)[] BuildEncodeTable(
        string[] mte1Words, string[] mte3Words)
    {
        var entries = new List<(string Word, byte[] Encoding)>();

        // MTE4 (codes 0x10-0x1F, always there from SpecialSequences)
        foreach (var (code, word) in Lufia1Constants.SpecialSequences)
            if (!string.IsNullOrEmpty(word))
                entries.Add((word, new[] { code }));

        // MTE1 lowercase (0x80-0xBF) and capitalized (0xC0-0xFF)
        for (int index = 0; index < mte1Words.Length; index++)
        {
            string lower = mte1Words[index];
            if (string.IsNullOrEmpty(lower)) continue;

            entries.Add((lower, new[] { (byte)(0x80 + index) }));

            string capitalized = char.ToUpper(lower[0]) + lower[1..];
            entries.Add((capitalized, new[] { (byte)(0xC0 + index) }));
        }

        // MTE3 lowercase (0x0C XX) and capitalized (0x0D XX)
        for (int index = 0; index < mte3Words.Length; index++)
        {
            string lower = mte3Words[index];
            if (string.IsNullOrEmpty(lower)) continue;

            entries.Add((lower, new byte[] { 0x0C, (byte)index }));

            string capitalized = char.ToUpper(lower[0]) + lower[1..];
            entries.Add((capitalized, new byte[] { 0x0D, (byte)index }));
        }

        // longest word first, so the greedy match always takes the best compression when words share a start
        entries.Sort((a, b) => b.Word.Length.CompareTo(a.Word.Length));

        return entries.ToArray();
    }
}

// the types that go with it

public record DecodeResult(string Text, List<TextToken> Tokens, int BytesConsumed);

public enum TextTokenKind
{
    Ascii, DictionaryRef, PageBreak, Newline, CharName, Control, EndString
}

public record TextToken(
    TextTokenKind Kind,
    int           RomOffset,
    int           ByteLength,
    string        Display,
    object?       Metadata = null);
