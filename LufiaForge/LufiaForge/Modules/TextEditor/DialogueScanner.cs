using LufiaForge.Core;

namespace LufiaForge.Modules.TextEditor;

// one decoded dialogue entry pulled out of the ROM
public class DialogueEntry
{
    public int    Index         { get; init; }
    public int    RomOffset     { get; init; }
    public int    RawByteLength { get; init; }
    public string DecodedText   { get; set;  }
    public string EditedText    { get; set;  }
    public bool   IsModified    => EditedText != DecodedText;

    // which scene it's in (filled in after the scan)
    public int    SceneIndex    { get; set; }
    public string SceneName     { get; set; } = "Battle / Menu";

    // for the list
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

// scans a Lufia 1 ROM for dialogue strings
//
// how: walk the dialogue part of the ROM (banks $84-$AF, file offsets 0x20000-0x57FFF) looking for runs that look
// like real Lufia 1 text, printable ASCII bytes and known control codes ending in 0x00, with a minimum length so we
// skip junk that just happens to look like text
//
// story/NPC dialogue is in banks $84-$87 (0x20000-0x3FFFF)
// battle messages and menus are in banks $88-$AF (0x40000-0x57FFF)
//
// it's a guess-based scan. it finds most of the dialogue plus some things that aren't (item names, NPC name tables
// and so on), and those can be filtered out in the UI
public static class DialogueScanner
{
    // the dialogue sits in two parts of the ROM:
    //   banks $84-$87 (file 0x20000-0x3FFFF): story/NPC dialogue
    //   banks $88-$AF (file 0x40000-0x57FFF): battle messages, menus
    private const int ScanStart = Lufia1Constants.DialogueScanStartOffset;
    private const int ScanEnd   = Lufia1Constants.DialogueScanEndOffset;
    private const int MinLength = 4;   // fewest decoded chars that count as dialogue
    private const int MaxLength = 2048; // stop reads that run away

    public static List<DialogueEntry> ScanForDialogue(
        RomBuffer rom,
        IProgress<int>? progress = null,
        CancellationToken cancel  = default)
    {
        var results = new List<DialogueEntry>();
        int offset  = ScanStart;
        int end     = Math.Min(ScanEnd, rom.Length);

        while (offset < end)
        {
            cancel.ThrowIfCancellationRequested();

            // progress in percent
            if (progress != null && (offset - ScanStart) % 0x1000 == 0)
            {
                int percent = (int)(((long)(offset - ScanStart) * 100) / (end - ScanStart));
                progress.Report(percent);
            }

            byte firstByte = rom.ReadByte(offset);

            // a dialogue string can start with a printable ASCII byte or a control code that makes sense at the start
            // of a string: ASCII, one-byte words (0x80-0xFF), special sequences (0x10-0x1F), dictionary refs
            // (0x0C/0x0D), character names (0x07), town names (0x0B)
            bool isValidStart = (firstByte >= 0x20 && firstByte <= 0x7E)
                             || firstByte >= 0x80
                             || (firstByte >= 0x10 && firstByte <= 0x1F)
                             || firstByte == Lufia1Constants.CtrlDictionaryRef
                             || firstByte == Lufia1Constants.CtrlDictionaryRefCap
                             || firstByte == Lufia1Constants.CtrlCharName
                             || firstByte == Lufia1Constants.CtrlTownName
                             || firstByte == Lufia1Constants.CtrlPageBreak;

            if (!isValidStart) { offset++; continue; }

            // try decoding from here
            var decoded = TextDecoder.Decode(rom, offset, expandMte: true);

            // it has to have enough printable stuff and end with [END]
            int printableCount = decoded.Tokens.Count(t =>
                t.Kind == TextTokenKind.Ascii ||
                t.Kind == TextTokenKind.DictionaryRef ||
                t.Kind == TextTokenKind.CharName);

            bool hasEndToken = decoded.Tokens.Any(t => t.Kind == TextTokenKind.EndString);

            if (printableCount >= MinLength && hasEndToken && decoded.BytesConsumed <= MaxLength)
            {
                results.Add(new DialogueEntry(
                    index:         results.Count,
                    romOffset:     offset,
                    decodedText:   decoded.Text,
                    rawByteLength: decoded.BytesConsumed));

                // skip past this string
                offset += decoded.BytesConsumed;
            }
            else
            {
                offset++;
            }
        }

        progress?.Report(100);

        // give every entry its scene name, by where it sits in the ROM
        foreach (var entry in results)
        {
            var scene        = LufiaSceneData.GetScene(entry.RomOffset);
            entry.SceneIndex = scene.SceneIndex;
            entry.SceneName  = scene.SceneName;
        }

        return results;
    }

    // quick scan of just the dictionary part, returns every word. fills the Dictionary tab
    public static List<(int Offset, string Word)> ScanDictionary(RomBuffer rom) =>
        TextDecoder.ReadAllDictionaryWords(rom);
}
