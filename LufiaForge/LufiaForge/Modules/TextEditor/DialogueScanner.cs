using LufiaForge.Core;

namespace LufiaForge.Modules.TextEditor;

/// <summary>
/// Represents one decoded dialogue entry extracted from the ROM.
/// </summary>
public class DialogueEntry
{
    public int    Index         { get; init; }
    public int    RomOffset     { get; init; }
    public int    RawByteLength { get; init; }
    public string DecodedText   { get; set;  }
    public string EditedText    { get; set;  }
    public bool   IsModified    => EditedText != DecodedText;

    // For display
    public string OffsetHex     => $"0x{RomOffset:X6}";
    public string SnesAddress   => $"${Lufia1Constants.FileOffsetToSnesAddress(RomOffset):X6}";
    public string Preview       => DecodedText.Length > 60
                                    ? DecodedText[..57].Replace('\n', ' ') + "..."
                                    : DecodedText.Replace('\n', ' ');

    public DialogueEntry(int index, int romOffset, string decodedText, int rawByteLength)
    {
        Index         = index;
        RomOffset     = romOffset;
        DecodedText   = decodedText;
        EditedText    = decodedText;
        RawByteLength = rawByteLength;
    }
}

/// <summary>
/// Scans a Lufia 1 ROM for dialogue strings.
///
/// Strategy: Walk through the ROM in the dialogue region (banks $84–$AF,
/// file offsets 0x20000–0x57FFF) looking for runs that look like valid
/// Lufia 1 text: sequences of printable ASCII bytes and known control codes
/// terminated by 0x00, with minimum length to filter out false positives.
///
/// Story/NPC dialogue lives in banks $84–$87 (0x20000–0x3FFFF).
/// Battle messages and menus live in banks $88–$AF (0x40000–0x57FFF).
///
/// This is a heuristic scan — it finds the majority of dialogue and some
/// false positives (item names, NPC name tables, etc.) that can be
/// filtered in the UI.
/// </summary>
public static class DialogueScanner
{
    // Dialogue data spans two ROM regions:
    //   Banks $84–$87 (file 0x20000–0x3FFFF): story/NPC dialogue
    //   Banks $88–$AF (file 0x40000–0x57FFF): battle messages, menus
    private const int ScanStart = Lufia1Constants.DialogueScanStartOffset;
    private const int ScanEnd   = Lufia1Constants.DialogueScanEndOffset;
    private const int MinLength = 4;   // minimum decoded chars to count as dialogue
    private const int MaxLength = 2048; // cap runaway reads

    public static List<DialogueEntry> ScanForDialogue(
        RomBuffer rom,
        IProgress<int>? progress = null,
        CancellationToken cancel  = default)
    {
        var results = new List<DialogueEntry>();
        int pos     = ScanStart;
        int end     = Math.Min(ScanEnd, rom.Length);

        while (pos < end)
        {
            cancel.ThrowIfCancellationRequested();

            // Report progress as percentage
            if (progress != null && (pos - ScanStart) % 0x1000 == 0)
            {
                int pct = (int)(((long)(pos - ScanStart) * 100) / (end - ScanStart));
                progress.Report(pct);
            }

            byte b = rom.ReadByte(pos);

            // A dialogue string can start with a printable ASCII byte or a known
            // control code that makes sense at the start of a string.
            // Covers: ASCII, single-byte words (0x80-0xFF), special sequences (0x10-0x1F),
            // dictionary refs (0x0C/0x0D), character names (0x07), town names (0x0B).
            bool isValidStart = (b >= 0x20 && b <= 0x7E)
                             || b >= 0x80
                             || (b >= 0x10 && b <= 0x1F)
                             || b == Lufia1Constants.CtrlDictionaryRef
                             || b == Lufia1Constants.CtrlDictionaryRefCap
                             || b == Lufia1Constants.CtrlCharName
                             || b == Lufia1Constants.CtrlTownName
                             || b == Lufia1Constants.CtrlPageBreak;

            if (!isValidStart) { pos++; continue; }

            // Try to decode from here
            var decoded = TextDecoder.Decode(rom, pos, expandDictionary: true);

            // Quality filter: must have enough printable content and end with [END]
            int printableCount = decoded.Tokens.Count(t =>
                t.Kind == TextTokenKind.Ascii ||
                t.Kind == TextTokenKind.DictionaryRef ||
                t.Kind == TextTokenKind.CharName);

            bool hasEndToken = decoded.Tokens.Any(t => t.Kind == TextTokenKind.EndString);

            if (printableCount >= MinLength && hasEndToken && decoded.BytesConsumed <= MaxLength)
            {
                results.Add(new DialogueEntry(
                    index:         results.Count,
                    romOffset:     pos,
                    decodedText:   decoded.Text,
                    rawByteLength: decoded.BytesConsumed));

                // Jump past this string
                pos += decoded.BytesConsumed;
            }
            else
            {
                pos++;
            }
        }

        progress?.Report(100);
        return results;
    }

    /// <summary>
    /// Quick scan just within the dictionary region, returning all words.
    /// Used to populate the Dictionary tab.
    /// </summary>
    public static List<(int Offset, string Word)> ScanDictionary(RomBuffer rom) =>
        TextDecoder.ReadAllDictionaryWords(rom);
}
