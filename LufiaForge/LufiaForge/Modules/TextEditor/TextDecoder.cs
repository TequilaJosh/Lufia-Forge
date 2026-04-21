using LufiaForge.Core;

namespace LufiaForge.Modules.TextEditor;

/// <summary>
/// Decodes and re-encodes Lufia 1 dialogue strings.
///
/// Full encoding map (source: AllOriginal.tbl, Digisalt Lufiatools 2.0, flobo 2011;
///                   MTE table locations verified against US ROM via pointer forensics):
///
///   0x00             End of string
///   0x04             Page break (player presses A)
///   0x05             Newline within dialogue box
///   0x07 + 1 byte    Character name (0x00=Hero, 0x01=Lufia, 0x02=Aguro, 0x03=Jerin...)
///   0x09 + 1 byte    Item name      (0x00=Nothing, 0x01=Leather Armor, ...)
///   0x0A + 1 byte    Spell name     (0x00=Flash, 0x01=Spark, 0x02=Blaze, ...)
///   0x0B + 1 byte    Town name      (0x01=Alekia, 0x02=Chatam, 0x03=Sheran...)
///   0x0C + 1 byte    MTE3 word, lowercase   (1-byte index 0x00–0xFF)
///   0x0D + 1 byte    MTE3 word, capitalized (same index, first char uppercased)
///   0x10–0x1F        MTE4: Special single-byte sequences ('s, ed, ing, I'm, Sinistral...)
///   0x20–0x7E        Standard printable ASCII
///   0x80–0xBF        MTE1: Single-byte compressed words, lowercase (the, you, to, it...)
///   0xC0–0xFF        MTE1: Single-byte compressed words, capitalized (The, You, To, It...)
///
/// MTE tables (Multiple Title Entry) — all in ROM bank 0x0A (file 0x50000–0x57FFF):
///   MTE4 ptr table  0x54A50  16 words  codes 0x10–0x1F
///   MTE1 ptr table  0x54AC0  64 words  codes 0x80–0xFF (lower 0x80–0xBF, cap 0xC0–0xFF)
///   MTE3 ptr table  0x54C17  256 words codes 0x0C 00 – 0x0C FF
///   MTE2            (no separate table; decoder capitalizes MTE3 words for 0x0D codes)
///
/// Pointer format: LE 16-bit SNES bank-relative address.  file = ptr + 0x48000
/// Words are contiguous in ROM with NO null-terminators; boundaries come from pointer pairs.
///
/// The expanded-MTE ASM patch (BahaBulle / Hiei-) extends MTE table capacities.
/// Because this decoder reads all tables dynamically from the ROM at runtime, it
/// supports both the original ROM and any patched variant automatically.
/// </summary>
public static class TextDecoder
{
    // -------------------------------------------------------------------------
    // MTE word cache — rebuilt whenever a new ROM is loaded.
    // All reads go through EnsureCache() so stale data is never used.
    // -------------------------------------------------------------------------

    private static RomBuffer? _cachedRom;

    /// <summary>64 lowercase MTE1 words indexed 0–63 (codes 0x80–0xBF).</summary>
    private static string[] _mte1Words = Array.Empty<string>();

    /// <summary>256 lowercase MTE3 words indexed 0–255 (codes 0x0C 00–0x0C FF).</summary>
    private static string[] _mte3Words = Array.Empty<string>();

    /// <summary>
    /// Word-to-encoding table for Encode(), sorted longest-word-first for greedy matching.
    /// Each entry maps a word string to the raw bytes that represent it.
    /// </summary>
    private static (string Word, byte[] Encoding)[] _encodeTable =
        Array.Empty<(string, byte[])>();

    /// <summary>Force-discard the cache. Call after loading a new ROM.</summary>
    public static void InvalidateDictionaryCache() => _cachedRom = null;

    private static void EnsureCache(RomBuffer rom)
    {
        if (ReferenceEquals(rom, _cachedRom)) return;

        _cachedRom  = rom;
        _mte1Words  = ReadMteWordsFromPtrTable(rom,
                          Lufia1Constants.Mte1PtrTableOffset,
                          Lufia1Constants.Mte1EntryCount);
        _mte3Words  = ReadMteWordsFromPtrTable(rom,
                          Lufia1Constants.Mte3PtrTableOffset,
                          Lufia1Constants.Mte3EntryCount);
        _encodeTable = BuildEncodeTable(_mte1Words, _mte3Words);
    }

    // -------------------------------------------------------------------------
    // Decode
    // -------------------------------------------------------------------------

    /// <summary>
    /// Decode one dialogue string starting at <paramref name="offset"/> in the ROM.
    /// Returns the decoded text and the number of raw bytes consumed.
    /// All MTE tables are read dynamically from the ROM; the decoder supports both
    /// the original US ROM and any expanded-MTE patched variant.
    /// </summary>
    public static DecodeResult Decode(RomBuffer rom, int offset, bool expandMte = true)
    {
        EnsureCache(rom);

        var text   = new System.Text.StringBuilder();
        var tokens = new List<TextToken>();
        int pos    = offset;
        int limit  = Math.Min(offset + 4096, rom.Length);

        while (pos < limit)
        {
            byte b = rom.ReadByte(pos);

            // ------------------------------------------------------------------
            // 0x00  End of string
            // ------------------------------------------------------------------
            if (b == Lufia1Constants.CtrlEndString)
            {
                tokens.Add(new TextToken(TextTokenKind.EndString, pos, 1, "[END]"));
                pos++;
                break;
            }

            // ------------------------------------------------------------------
            // 0x04  Page break
            // ------------------------------------------------------------------
            if (b == Lufia1Constants.CtrlPageBreak)
            {
                tokens.Add(new TextToken(TextTokenKind.PageBreak, pos, 1, "\n--- [PAGE] ---\n"));
                text.Append("\n[PAGE]\n");
                pos++;
                continue;
            }

            // ------------------------------------------------------------------
            // 0x05  Newline
            // ------------------------------------------------------------------
            if (b == Lufia1Constants.CtrlNewline)
            {
                tokens.Add(new TextToken(TextTokenKind.Newline, pos, 1, "\n"));
                text.Append('\n');
                pos++;
                continue;
            }

            // ------------------------------------------------------------------
            // 0x07 + 1 byte  Character name
            // ------------------------------------------------------------------
            if (b == Lufia1Constants.CtrlCharName)
            {
                byte charId = pos + 1 < limit ? rom.ReadByte(pos + 1) : (byte)0;
                string label = Lufia1Constants.CharacterNames.TryGetValue(charId, out string? n)
                    ? n : $"[CHAR:0x{charId:X2}]";
                tokens.Add(new TextToken(TextTokenKind.CharName, pos, 2, label, charId));
                text.Append(label);
                pos += 2;
                continue;
            }

            // ------------------------------------------------------------------
            // 0x09 + 1 byte  Item name
            // ------------------------------------------------------------------
            if (b == Lufia1Constants.CtrlItemName)
            {
                byte itemId = pos + 1 < limit ? rom.ReadByte(pos + 1) : (byte)0;
                string item = $"[ITEM:0x{itemId:X2}]";
                tokens.Add(new TextToken(TextTokenKind.Control, pos, 2, item, itemId));
                text.Append(item);
                pos += 2;
                continue;
            }

            // ------------------------------------------------------------------
            // 0x0A + 1 byte  Spell name
            // ------------------------------------------------------------------
            if (b == Lufia1Constants.CtrlSpellName)
            {
                byte spellId = pos + 1 < limit ? rom.ReadByte(pos + 1) : (byte)0;
                string spell = $"[SPELL:0x{spellId:X2}]";
                tokens.Add(new TextToken(TextTokenKind.Control, pos, 2, spell, spellId));
                text.Append(spell);
                pos += 2;
                continue;
            }

            // ------------------------------------------------------------------
            // 0x0B + 1 byte  Town name
            // ------------------------------------------------------------------
            if (b == Lufia1Constants.CtrlTownName)
            {
                byte townId = pos + 1 < limit ? rom.ReadByte(pos + 1) : (byte)0;
                string town = Lufia1Constants.TownNames.TryGetValue(townId, out string? t)
                    ? t : $"[TOWN:0x{townId:X2}]";
                tokens.Add(new TextToken(TextTokenKind.Ascii, pos, 2, town));
                text.Append(town);
                pos += 2;
                continue;
            }

            // ------------------------------------------------------------------
            // 0x0C + 1 byte  MTE3 word (lowercase)
            // 0x0D + 1 byte  MTE3 word (capitalized)
            //
            // FIX: was incorrectly reading 2 bytes after the control (3-byte total).
            //      Correct format is: control + 1-byte index = 2 bytes total.
            // ------------------------------------------------------------------
            if (b == Lufia1Constants.CtrlDictionaryRef ||
                b == Lufia1Constants.CtrlDictionaryRefCap)
            {
                if (pos + 1 >= limit) break;
                byte idx       = rom.ReadByte(pos + 1);
                bool capitalize = (b == Lufia1Constants.CtrlDictionaryRefCap);

                string word;
                if (expandMte && idx < _mte3Words.Length)
                {
                    word = _mte3Words[idx];
                    if (capitalize && word.Length > 0)
                        word = char.ToUpper(word[0]) + word[1..];
                }
                else
                {
                    word = capitalize ? $"[DCAP:{idx:X2}]" : $"[DICT:{idx:X2}]";
                }

                tokens.Add(new TextToken(TextTokenKind.DictionaryRef, pos, 2, word, idx));
                text.Append(word);
                pos += 2;
                continue;
            }

            // ------------------------------------------------------------------
            // 0x10–0x1F  MTE4: Special single-byte compressed sequences
            // ('s, ed, ing, I'm, I'll, I've, Alumina, Sinistral, Dual, Falcon,
            //  Glasdar, Welcome, Raile, Lilah, Reyna, Shaia)
            // ------------------------------------------------------------------
            if (b >= 0x10 && b <= 0x1F)
            {
                string seq = Lufia1Constants.SpecialSequences.TryGetValue(b, out string? s)
                    ? s : $"[SP:0x{b:X2}]";
                tokens.Add(new TextToken(TextTokenKind.Ascii, pos, 1, seq));
                text.Append(seq);
                pos++;
                continue;
            }

            // ------------------------------------------------------------------
            // 0x20–0x7E  Standard printable ASCII
            // ------------------------------------------------------------------
            if (b >= 0x20 && b <= 0x7E)
            {
                char c = (char)b;
                tokens.Add(new TextToken(TextTokenKind.Ascii, pos, 1, c.ToString()));
                text.Append(c);
                pos++;
                continue;
            }

            // ------------------------------------------------------------------
            // 0x80–0xFF  MTE1: Single-byte compressed words
            //   0x80–0xBF  lowercase (the, you, to, it, of, that, is, in...)
            //   0xC0–0xFF  capitalized counterparts (The, You, To, It, Of, That, Is, In...)
            //
            // FIX: now reads word boundaries dynamically from the MTE1 pointer table
            //      (file 0x54AC0) instead of a hardcoded constant table.  Automatically
            //      supports both the original ROM and any expanded-MTE patched variant.
            // ------------------------------------------------------------------
            if (b >= 0x80)
            {
                bool capitalize = b >= 0xC0;
                int  idx        = b - (capitalize ? 0xC0 : 0x80);

                string word;
                if (expandMte && idx < _mte1Words.Length)
                {
                    word = _mte1Words[idx];
                    if (capitalize && word.Length > 0)
                        word = char.ToUpper(word[0]) + word[1..];
                }
                else
                {
                    // Fallback to hardcoded table when ROM is not available or index OOB
                    word = Lufia1Constants.SingleByteWords.TryGetValue(b, out string? w)
                        ? w : $"[W:0x{b:X2}]";
                }

                tokens.Add(new TextToken(TextTokenKind.DictionaryRef, pos, 1, word, b));
                text.Append(word);
                pos++;
                continue;
            }

            // ------------------------------------------------------------------
            // Anything else — unknown control code
            // ------------------------------------------------------------------
            string ctrlLabel = Lufia1Constants.ControlCodeLabels.TryGetValue(b, out string? l)
                ? l : $"[0x{b:X2}]";
            tokens.Add(new TextToken(TextTokenKind.Control, pos, 1, ctrlLabel));
            text.Append(ctrlLabel);
            pos++;
        }

        return new DecodeResult(text.ToString(), tokens, pos - offset);
    }

    // -------------------------------------------------------------------------
    // MTE pointer-table reader (shared by MTE1, MTE3; also covers MTE4 if needed)
    // -------------------------------------------------------------------------

    /// <summary>
    /// Read <paramref name="count"/> words from an MTE pointer table.
    ///
    /// The table at <paramref name="ptrTableFileOffset"/> holds (<paramref name="count"/>+1)
    /// consecutive LE 16-bit SNES bank-relative pointers.  Words are contiguous in ROM
    /// with no null terminators; each word spans bytes
    ///   [ptr[i] + MtePtrBase .. ptr[i+1] + MtePtrBase)
    ///
    /// This method works for all four MTE tables because they all use bank 0x0A
    /// (MtePtrBase = 0x48000 = 0x0A*0x8000 - 0x8000).
    /// </summary>
    public static string[] ReadMteWordsFromPtrTable(RomBuffer rom,
                                                     int      ptrTableFileOffset,
                                                     int      count)
    {
        var words  = new string[count];
        int tableEnd = ptrTableFileOffset + (count + 1) * 2;
        if (tableEnd > rom.Length) return words;   // ROM too short — return empty strings

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
            for (int p = fileStart; p < fileEnd; p++)
            {
                byte ch = rom.ReadByte(p);
                // Words are printable ASCII only; stop early if garbled data is hit
                if (ch < 0x20 || ch > 0x7E) break;
                sb.Append((char)ch);
            }
            words[i] = sb.ToString();
        }

        return words;
    }

    // -------------------------------------------------------------------------
    // ReadAllDictionaryWords — MTE3 word list (used by Dictionary tab / export)
    // -------------------------------------------------------------------------

    /// <summary>
    /// Return all 256 MTE3 words as (file offset, word) pairs, in dictionary index order.
    /// Reads word boundaries from the MTE3 pointer table (file 0x54C17).
    /// </summary>
    public static List<(int Offset, string Word)> ReadAllDictionaryWords(RomBuffer rom)
    {
        var result = new List<(int, string)>(Lufia1Constants.Mte3EntryCount);
        int tableOff = Lufia1Constants.Mte3PtrTableOffset;
        int count    = Lufia1Constants.Mte3EntryCount;
        int tableEnd = tableOff + (count + 1) * 2;
        if (tableEnd > rom.Length) return result;

        for (int i = 0; i < count; i++)
        {
            ushort startPtr = rom.ReadUInt16Le(tableOff + i * 2);
            ushort endPtr   = rom.ReadUInt16Le(tableOff + i * 2 + 2);

            int fileStart = startPtr + Lufia1Constants.MtePtrBase;
            int fileEnd   = endPtr   + Lufia1Constants.MtePtrBase;

            if (fileStart >= fileEnd || fileEnd > rom.Length) continue;

            var sb = new System.Text.StringBuilder(fileEnd - fileStart);
            for (int p = fileStart; p < fileEnd; p++)
            {
                byte ch = rom.ReadByte(p);
                if (ch < 0x20 || ch > 0x7E) break;
                sb.Append((char)ch);
            }
            if (sb.Length > 0)
                result.Add((fileStart, sb.ToString()));
        }

        return result;
    }

    // -------------------------------------------------------------------------
    // ReadDictionaryWord — kept for backward compatibility (reads null-terminated)
    // -------------------------------------------------------------------------

    /// <summary>
    /// Read a null-terminated or control-terminated ASCII word from <paramref name="fileOffset"/>.
    /// NOTE: MTE3 words are NOT null-terminated; use <see cref="ReadAllDictionaryWords"/> or
    /// <see cref="ReadMteWordsFromPtrTable"/> for index-based access via the pointer table.
    /// </summary>
    public static string ReadDictionaryWord(RomBuffer rom, int fileOffset)
    {
        var sb  = new System.Text.StringBuilder();
        int pos = fileOffset;
        int end = Math.Min(fileOffset + 64, rom.Length);

        while (pos < end)
        {
            byte b = rom.ReadByte(pos);
            if (b == 0x00 || b < 0x20) break;
            if (b <= 0x7E) sb.Append((char)b);
            else break;
            pos++;
        }
        return sb.ToString();
    }

    // -------------------------------------------------------------------------
    // Encode (write edited text back to ROM bytes)
    // -------------------------------------------------------------------------

    /// <summary>
    /// Encode a plain-text string back to Lufia 1 raw bytes, with MTE re-compression.
    ///
    /// When <paramref name="rom"/> is provided the encoder reads the live MTE word tables
    /// from the ROM and uses greedy longest-match compression to fit edited dialogue within
    /// its original byte budget.  Words from all four MTE tables are tried, longest first.
    /// If <paramref name="rom"/> is null the encoder falls back to the hardcoded MTE4 /
    /// SpecialSequences table only; MTE1 and MTE3 compression is skipped.
    ///
    /// Control tags supported in the input text:
    ///   [PAGE]        → 0x04
    ///   \n  or [NL]   → 0x05
    ///   [Hero]        → 0x07 0x00     [Lufia]  → 0x07 0x01
    ///   [Aguro]       → 0x07 0x02     [Jerin]  → 0x07 0x03
    ///   [Maxim]       → 0x07 0x04     [Selan]  → 0x07 0x05
    ///   [Guy]         → 0x07 0x06     [Artea]  → 0x07 0x07
    ///   [END]         → 0x00 (always appended automatically; skip if already present)
    /// </summary>
    public static byte[] Encode(string text, RomBuffer? rom = null)
    {
        if (rom != null) EnsureCache(rom);

        // Build the compression table to use for this call
        (string Word, byte[] Encoding)[] compressTable = (rom != null)
            ? _encodeTable
            : BuildEncodeTable(Array.Empty<string>(), Array.Empty<string>());  // MTE4 only

        var bytes = new List<byte>();
        int i     = 0;

        while (i < text.Length)
        {
            // ----------------------------------------------------------------
            // Handle control tags: [TAG] or [TAG:ARG]
            // ----------------------------------------------------------------
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
                        case "END":    /* terminator — added at the very end */  break;
                        case "HERO":   bytes.Add(0x07); bytes.Add(0x00); break;
                        case "LUFIA":  bytes.Add(0x07); bytes.Add(0x01); break;
                        case "AGURO":  bytes.Add(0x07); bytes.Add(0x02); break;
                        case "JERIN":  bytes.Add(0x07); bytes.Add(0x03); break;
                        case "MAXIM":  bytes.Add(0x07); bytes.Add(0x04); break;
                        case "SELAN":  bytes.Add(0x07); bytes.Add(0x05); break;
                        case "GUY":    bytes.Add(0x07); bytes.Add(0x06); break;
                        case "ARTEA":  bytes.Add(0x07); bytes.Add(0x07); break;
                        default:       handled = false; break;
                    }

                    if (handled)
                    {
                        i = close + 1;
                        continue;
                    }

                    // Unknown tag — fall through to write literal '[' then let
                    // the rest of the loop handle subsequent characters.
                }
            }

            // ----------------------------------------------------------------
            // Newline character
            // ----------------------------------------------------------------
            if (text[i] == '\n')
            {
                bytes.Add(Lufia1Constants.CtrlNewline);
                i++;
                continue;
            }

            // ----------------------------------------------------------------
            // Try greedy MTE compression (longest match first)
            // ----------------------------------------------------------------
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

            // ----------------------------------------------------------------
            // Plain printable ASCII
            // ----------------------------------------------------------------
            char ch = text[i];
            if (ch >= 0x20 && ch <= 0x7E)
                bytes.Add((byte)ch);
            // Non-printable, non-tag chars are silently dropped.
            i++;
        }

        bytes.Add(Lufia1Constants.CtrlEndString); // always terminate
        return bytes.ToArray();
    }

    // -------------------------------------------------------------------------
    // Private: build the encode compression lookup table
    // -------------------------------------------------------------------------

    /// <summary>
    /// Build the sorted (longest-word-first) word-to-encoding lookup used by Encode().
    /// Includes MTE4 (always), MTE1 (if mte1Words provided), MTE3 (if mte3Words provided).
    /// </summary>
    private static (string Word, byte[] Encoding)[] BuildEncodeTable(
        string[] mte1Words, string[] mte3Words)
    {
        var entries = new List<(string Word, byte[] Encoding)>();

        // --- MTE4 (codes 0x10–0x1F, always available from SpecialSequences) ---
        foreach (var (code, word) in Lufia1Constants.SpecialSequences)
            if (!string.IsNullOrEmpty(word))
                entries.Add((word, new[] { code }));

        // --- MTE1 lowercase (0x80–0xBF) and capitalized (0xC0–0xFF) ---
        for (int idx = 0; idx < mte1Words.Length; idx++)
        {
            string lower = mte1Words[idx];
            if (string.IsNullOrEmpty(lower)) continue;

            entries.Add((lower, new[] { (byte)(0x80 + idx) }));

            // Capitalized variant
            string cap = char.ToUpper(lower[0]) + lower[1..];
            entries.Add((cap, new[] { (byte)(0xC0 + idx) }));
        }

        // --- MTE3 lowercase (0x0C XX) and capitalized (0x0D XX) ---
        for (int idx = 0; idx < mte3Words.Length; idx++)
        {
            string lower = mte3Words[idx];
            if (string.IsNullOrEmpty(lower)) continue;

            entries.Add((lower, new byte[] { 0x0C, (byte)idx }));

            string cap = char.ToUpper(lower[0]) + lower[1..];
            entries.Add((cap, new byte[] { 0x0D, (byte)idx }));
        }

        // Sort longest-word-first so greedy matching always prefers the most
        // compressed encoding when multiple words share a prefix.
        entries.Sort((a, b) => b.Word.Length.CompareTo(a.Word.Length));

        return entries.ToArray();
    }
}

// -------------------------------------------------------------------------
// Supporting types
// -------------------------------------------------------------------------

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
