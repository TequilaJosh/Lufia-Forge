using LufiaForge.Core;

namespace LufiaForge.Modules.TextEditor;

/// <summary>
/// Decodes and encodes Lufia 1 dialogue strings.
///
/// Full encoding map (source: AllOriginal.tbl, Digisalt Lufiatools 2.0, flobo 2011):
///   0x00             End of string
///   0x04             Page break (player presses A)
///   0x05             Newline within dialogue box
///   0x07 + 1 byte    Character name (0x00=Hero, 0x01=Lufia, 0x02=Aguro, 0x03=Jerin...)
///   0x0B + 1 byte    Town name (0x01=Alekia, 0x02=Chatam, 0x03=Sheran...)
///   0x0C + 2 bytes   Dictionary word, lowercase  (2-byte LE ptr + 0x48000 = file offset)
///   0x0D + 2 bytes   Dictionary word, capitalized (same pointer, first char uppercased)
///   0x10–0x1F        Special single-byte sequences ('s, ed, ing, I'm, Sinistral...)
///   0x20–0x7E        Standard printable ASCII
///   0x80–0xBF        Single-byte compressed words, lowercase (the, you, to, it...)
///   0xC0–0xFF        Single-byte compressed words, capitalized (The, You, To, It...)
/// </summary>
public static class TextDecoder
{
    // -------------------------------------------------------------------------
    // Dictionary word cache — rebuilt whenever a new ROM is loaded
    // -------------------------------------------------------------------------
    private static RomBuffer? _cachedDictRom;
    private static string[]   _cachedDictWords = Array.Empty<string>();

    /// <summary>
    /// Force-reload the dictionary word cache for the given ROM.
    /// Call this after loading a new ROM so stale cached words are discarded.
    /// </summary>
    public static void InvalidateDictionaryCache() => _cachedDictRom = null;

    private static string[] GetDictionaryWords(RomBuffer rom)
    {
        if (!ReferenceEquals(rom, _cachedDictRom))
        {
            _cachedDictRom   = rom;
            _cachedDictWords = ReadAllDictionaryWords(rom)
                                   .Select(e => e.Word)
                                   .ToArray();
        }
        return _cachedDictWords;
    }

    // -------------------------------------------------------------------------
    // Decode
    // -------------------------------------------------------------------------

    /// <summary>
    /// Decode one dialogue string starting at <paramref name="offset"/> in the ROM.
    /// Returns the decoded text and the number of raw bytes consumed.
    /// Dictionary words and compressed sequences are expanded inline.
    /// </summary>
    public static DecodeResult Decode(RomBuffer rom, int offset, bool expandDictionary = true)
    {
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
            // 0x0C + 2 bytes  Dictionary word (lowercase)
            // 0x0D + 2 bytes  Dictionary word (capitalized)
            // ------------------------------------------------------------------
            if (b == Lufia1Constants.CtrlDictionaryRef || b == Lufia1Constants.CtrlDictionaryRefCap)
            {
                if (pos + 2 >= limit) break;
                ushort ptr       = rom.ReadUInt16Le(pos + 1);
                int    wordOffset = ptr + Lufia1Constants.DictionaryBaseOffset;
                bool   capitalize = (b == Lufia1Constants.CtrlDictionaryRefCap);

                string word;
                if (expandDictionary && wordOffset >= 0 && wordOffset < rom.Length)
                {
                    word = ReadDictionaryWord(rom, wordOffset);
                    if (capitalize && word.Length > 0)
                        word = char.ToUpper(word[0]) + word[1..];
                }
                else
                {
                    word = capitalize ? $"[DCAP:0x{ptr:X4}]" : $"[DICT:0x{ptr:X4}]";
                }

                tokens.Add(new TextToken(TextTokenKind.DictionaryRef, pos, 3, word, ptr));
                text.Append(word);
                pos += 3;
                continue;
            }

            // ------------------------------------------------------------------
            // 0x10–0x1F  Special single-byte compressed sequences
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
            // 0x80–0xFF  Single-byte compressed words (the, you, to, The, You, To...)
            // ------------------------------------------------------------------
            if (b >= 0x80)
            {
                string word = Lufia1Constants.SingleByteWords.TryGetValue(b, out string? w)
                    ? w : $"[W:0x{b:X2}]";
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
    // Dictionary word reader
    // -------------------------------------------------------------------------

    /// <summary>
    /// Read a null-terminated ASCII word from the dictionary at the given file offset.
    /// Stops on 0x00 or any byte below 0x20 (control codes).
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

    /// <summary>
    /// Parse all entries in the Lufia 1 word dictionary sequentially.
    /// Returns a list of (file offset, word string) pairs in dictionary order.
    /// </summary>
    public static List<(int Offset, string Word)> ReadAllDictionaryWords(RomBuffer rom)
    {
        var result = new List<(int, string)>();
        int pos    = Lufia1Constants.DictionaryStartOffset;
        int end    = Math.Min(Lufia1Constants.DictionaryEndOffset, rom.Length);

        while (pos < end)
        {
            int    wordStart = pos;
            string word      = ReadDictionaryWord(rom, pos);

            if (word.Length > 0)
            {
                result.Add((wordStart, word));
                pos += word.Length;
                // Skip the terminating byte
                if (pos < end && rom.ReadByte(pos) < 0x20)
                    pos++;
            }
            else
            {
                pos++; // skip control byte at current position
            }
        }

        return result;
    }

    // -------------------------------------------------------------------------
    // Encode (write edited text back to ROM)
    // -------------------------------------------------------------------------

    /// <summary>
    /// Encode a plain-text string back to Lufia 1 raw bytes.
    ///
    /// Control tags supported:
    ///   [PAGE]     → 0x04
    ///   [NL]       → 0x05
    ///   [Hero]     → 0x07 0x00
    ///   [Lufia]    → 0x07 0x01
    ///   [Aguro]    → 0x07 0x02
    ///   [Jerin]    → 0x07 0x03
    ///   [Maxim]    → 0x07 0x04
    ///   [Selan]    → 0x07 0x05
    ///   [Guy]      → 0x07 0x06
    ///   [Artea]    → 0x07 0x07
    ///   [END]      → 0x00 (always appended automatically)
    ///
    /// NOTE: This is a flat encoder. It does NOT re-compress text back into
    /// single-byte words (0x80–0xFF), special sequences (0x10–0x1F), or
    /// dictionary references (0x0C/0x0D). Edited text may be larger than the
    /// original; the editor validates against the original byte length before
    /// committing changes.
    /// </summary>
    public static byte[] Encode(string text)
    {
        var bytes = new List<byte>();
        int i     = 0;

        while (i < text.Length)
        {
            if (text[i] == '[')
            {
                int close = text.IndexOf(']', i);
                if (close > i)
                {
                    string tag = text[(i + 1)..close].ToUpperInvariant();
                    switch (tag)
                    {
                        case "PAGE":   bytes.Add(0x04); break;
                        case "NL":     bytes.Add(0x05); break;
                        case "HERO":   bytes.Add(0x07); bytes.Add(0x00); break;
                        case "LUFIA":  bytes.Add(0x07); bytes.Add(0x01); break;
                        case "AGURO":  bytes.Add(0x07); bytes.Add(0x02); break;
                        case "JERIN":  bytes.Add(0x07); bytes.Add(0x03); break;
                        case "MAXIM":  bytes.Add(0x07); bytes.Add(0x04); break;
                        case "SELAN":  bytes.Add(0x07); bytes.Add(0x05); break;
                        case "GUY":    bytes.Add(0x07); bytes.Add(0x06); break;
                        case "ARTEA":  bytes.Add(0x07); bytes.Add(0x07); break;
                        default:
                            // Unknown tag — write as literal ASCII bytes
                            foreach (char c in text[i..(close + 1)])
                                if (c >= 0x20 && c <= 0x7E)
                                    bytes.Add((byte)c);
                            break;
                    }
                    i = close + 1;
                    continue;
                }
            }

            char ch = text[i];
            if (ch == '\n')
            {
                bytes.Add(Lufia1Constants.CtrlNewline);
            }
            else if (ch >= 0x20 && ch <= 0x7E)
            {
                bytes.Add((byte)ch);
            }
            // else: skip non-printable characters that have no tag representation
            i++;
        }

        bytes.Add(Lufia1Constants.CtrlEndString); // always terminate
        return bytes.ToArray();
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
