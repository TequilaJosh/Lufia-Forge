using LufiaForge.Core.Maps;

namespace LufiaForge.Core;

// dutch letters for lufia 1 (Sinrevi asked for them, for the dutch lufia 1 they're writing).
// the 12 letters in Map (e with acute, grave, diaeresis and circumflex, i and o and u with diaeresis, a with acute and
// grave, o and u with acute, and capital E with acute) take the places of # $ % * ; = ^ \ ] ` | _, twelve signs the game
// never shows as text. checked every dialogue box of the us game on 2026-10-30: it uses ! " & ' + , - . : < > ? and ~
// once, in the debug room. item, spell and monster names only use ' and @ (the space inside a name).
// not ~ : the name screen fills the letters still to type with it (checked in bizhawk).
// = is the last sign on the name screen's grid, so a name can have an o with diaeresis.
// lufia 1 has no free character codes (10-1F and 80-FF are compressed words) so a letter has to replace a sign, same as
// the dutch lufia 2 did (" is e with diaeresis there).
// the glyphs are built from the rom's own letters (e, i, o, u, a, E) with an accent on top, in both the text font
// (dialogue, choice windows, the ring menus' lists) and the small font (names, menu titles), so they match whatever font
// the project uses (FontPatch too, apply this after it). the text editors show and take the letters themselves.
// the original glyphs are kept in the added rom space so they can be put back
public static class DutchLetters
{
    // the letters and the sign each one replaces
    public static readonly (char Letter, byte Code)[] Map =
    {
        ('é', 0x23), ('è', 0x24), ('ë', 0x25), ('ê', 0x2A), ('ï', 0x3B), ('ö', 0x3D),
        ('ü', 0x5E), ('á', 0x5C), ('à', 0x5D), ('ó', 0x60), ('ú', 0x7C), ('É', 0x5F),
    };

    private const int BackupId = 96;
    private static readonly byte[] Magic = "LFNL"u8.ToArray();

    private enum Accent { Acute, Grave, Diaeresis, Circumflex }

    private static (char Base, Accent Accent) Parts(char letter) => letter switch
    {
        'é' => ('e', Accent.Acute), 'è' => ('e', Accent.Grave), 'ë' => ('e', Accent.Diaeresis), 'ê' => ('e', Accent.Circumflex),
        'ï' => ('i', Accent.Diaeresis), 'ö' => ('o', Accent.Diaeresis), 'ü' => ('u', Accent.Diaeresis),
        'á' => ('a', Accent.Acute), 'à' => ('a', Accent.Grave), 'ó' => ('o', Accent.Acute), 'ú' => ('u', Accent.Acute),
        'É' => ('E', Accent.Acute),
        _ => throw new ArgumentOutOfRangeException(nameof(letter)),
    };

    private static int BackupOffset(RomBuffer rom)
    {
        if (rom.Length < MapWriter.ExpandedSize) return 0;
        var backup = ExpansionSpace.Load(rom).Entries.FirstOrDefault(x => x.Kind == ExpansionSpace.Kind.Patch && x.Id == BackupId);
        return backup != null && rom.ReadBytes(backup.Start, 4).SequenceEqual(Magic) ? backup.Start : 0;
    }

    public static bool IsApplied(RomBuffer rom) => rom.Game == GameKind.Lufia1 && BackupOffset(rom) > 0;

    // the letter a code shows in rom, or null (it's just the sign)
    public static char? LetterFor(RomBuffer rom, byte code)
    {
        foreach (var (letter, letterCode) in Map) if (letterCode == code) return IsApplied(rom) ? letter : null;
        return null;
    }

    // puts the letters in. the rom gets expanded to 2 MB for the backup of the signs they replace
    public static void Apply(RomBuffer rom)
    {
        if (rom.Game != GameKind.Lufia1) throw new InvalidOperationException("The Dutch letters are for Lufia 1.");
        if (rom.Length < MapWriter.ExpandedSize) rom.Expand(MapWriter.ExpandedSize);
        if (!IsApplied(rom))
        {
            var freeSpace = ExpansionSpace.Load(rom);
            int backupOffset = freeSpace.Allocate(4 + Map.Length * 32, ExpansionSpace.Kind.Patch, BackupId);
            rom.WriteBytes(backupOffset, Magic);
            for (int i = 0; i < Map.Length; i++)
            {
                rom.WriteBytes(backupOffset + 4 + i * 32, rom.ReadBytes(FontPatch.Lufia1Font + Map[i].Code * 16, 16));
                rom.WriteBytes(backupOffset + 4 + i * 32 + 16, rom.ReadBytes(FontPatch.Lufia1Small + Map[i].Code * 16, 16));
            }
            freeSpace.Save();
        }
        foreach (var (letter, code) in Map)
        {
            var (baseLetter, accent) = Parts(letter);
            rom.WriteBytes(FontPatch.Lufia1Font + code * 16, TextGlyph(rom.ReadBytes(FontPatch.Lufia1Font + baseLetter * 16, 16), accent, char.IsUpper(baseLetter)));
            var smallTile = rom.ReadBytes(FontPatch.Lufia1Small + baseLetter * 16, 16);
            var rows = Enumerable.Range(0, 8).Select(r => (byte)(smallTile[2 * r] | smallTile[2 * r + 1])).ToArray();
            var glyphRows = SmallGlyph(rows, accent);
            var tile = new byte[16];
            for (int r = 0; r < 8; r++) tile[2 * r] = tile[2 * r + 1] = glyphRows[r];
            rom.WriteBytes(FontPatch.Lufia1Small + code * 16, tile);
        }
        rom.FixChecksum();
        Modules.TextEditor.TextDecoder.InvalidateDictionaryCache();
    }

    // the signs the letters replaced, as the backup keeps them: (code, text font glyph, small font tile).
    // empty when the letters aren't in
    internal static IEnumerable<(int Code, byte[] Text, byte[] Small)> SavedSigns(RomBuffer rom)
    {
        int backupOffset = BackupOffset(rom);
        if (backupOffset == 0) yield break;
        for (int i = 0; i < Map.Length; i++)
            yield return (Map[i].Code, rom.ReadBytes(backupOffset + 4 + i * 32, 16), rom.ReadBytes(backupOffset + 4 + i * 32 + 16, 16));
    }

    // puts the signs back
    public static void Remove(RomBuffer rom)
    {
        int backupOffset = BackupOffset(rom);
        if (backupOffset == 0) return;
        for (int i = 0; i < Map.Length; i++)
        {
            rom.WriteBytes(FontPatch.Lufia1Font + Map[i].Code * 16, rom.ReadBytes(backupOffset + 4 + i * 32, 16));
            rom.WriteBytes(FontPatch.Lufia1Small + Map[i].Code * 16, rom.ReadBytes(backupOffset + 4 + i * 32 + 16, 16));
        }
        var freeSpace = ExpansionSpace.Load(rom);
        freeSpace.Free(backupOffset);
        freeSpace.Save();
        rom.FixChecksum();
        Modules.TextEditor.TextDecoder.InvalidateDictionaryCache();
    }

    // the character a byte shows in names and text (its letter when the letters are in)
    public static char Show(RomBuffer rom, byte b) => LetterFor(rom, b) ?? (char)b;

    // the byte for c (a letter's code when the letters are in), or null
    public static byte? Code(RomBuffer rom, char c)
    {
        if (c > 0x7F)
        {
            if (!IsApplied(rom)) return null;
            foreach (var (letter, code) in Map) if (letter == c) return code;
            return null;
        }
        return (byte)c;
    }

    // why text can't be written to a lufia 1 rom as typed (a letter the fonts don't have), or null.
    // before this, a letter like that got dropped without a word (egoistisch with the i-diaeresis came out egostisch)
    public static string? Unwritable(RomBuffer rom, string text)
    {
        var missing = text.Where(c => c > 0x7E && Code(rom, c) == null).Distinct().ToList();
        if (missing.Count == 0) return null;
        var dutch = missing.Where(c => Map.Any(m => m.Letter == c)).ToList();
        var other = missing.Except(dutch).ToList();
        string Join(List<char> letters) => string.Join(" ", letters);
        if (other.Count == 0)
            return $"{Join(dutch)} need{(dutch.Count == 1 ? "s" : "")} the Dutch letters (Game Data, Game Settings, Dutch)";
        return $"{Join(other)} {(other.Count == 1 ? "isn't" : "aren't")} in Lufia 1's font" +
               (dutch.Count > 0 ? $"; {Join(dutch)} need{(dutch.Count == 1 ? "s" : "")} the Dutch letters (Game Data, Game Settings, Dutch)" : "") +
               (IsApplied(rom) ? "" : $". The Dutch letters are {string.Join(" ", Map.Select(m => m.Letter))}");
    }

    // text with the letters swapped for the codes they have in the game (when the letters are in)
    public static string ToCodes(string text)
    {
        foreach (var (letter, code) in Map) text = text.Replace(letter, (char)code);
        return text;
    }

    // dutch names from the dutch lufia 2

    // what ApplyLufia2Names did
    public sealed record NamesResult(int Items, int Spells, int Monsters, int Kept, int TooLong)
    {
        public override string ToString() =>
            $"{Items} item, {Spells} spell and {Monsters} monster names are Dutch now" +
            (Kept > 0 ? $"; {Kept} the project had renamed itself were left as they are" : "") +
            (TooLong > 0 ? $"; {TooLong} too long for Lufia 1 kept their English name" : "") + ".";
    }

    private sealed record Entry(int id, string en, string nl);
    private sealed record NameList(List<Entry> items, List<Entry> spells, List<Entry> monsters);

    // the dutch names we keep for lufia 1 (Translations/Lufia1_Dutch.json): the items, spells and monsters lufia 1
    // shares with lufia 2 (same english name), named the way the dutch translation of lufia 2 names them.
    // kept in lufia forge so nobody needs a dutch rom
    private static NameList LoadNames()
    {
        using var stream = typeof(DutchLetters).Assembly.GetManifestResourceStream("Translations.Lufia1_Dutch.json")
            ?? throw new InvalidOperationException("The Dutch name list is missing from this build of Lufia Forge.");
        return System.Text.Json.JsonSerializer.Deserialize<NameList>(stream) ?? throw new InvalidOperationException("The Dutch name list can't be read.");
    }

    // gives the items, spells and monsters lufia 1 shares with lufia 2 their dutch names.
    // a name the project changed itself (doesn't read as the us game's anymore) is left alone, and a name longer than
    // lufia 1 allows keeps its english one. names with dutch letters bring the letters in (Apply)
    public static NamesResult ApplyLufia2Names(RomBuffer rom)
    {
        if (rom.Game != GameKind.Lufia1) throw new InvalidOperationException("These names are for Lufia 1.");
        var list = LoadNames();
        if (list.items.Concat(list.spells).Concat(list.monsters).Any(e => e.nl.Any(c => c > 0x7F)) && !IsApplied(rom)) Apply(rom);
        int kept = 0, tooLong = 0;
        string ReadName(int offset, int length) => new string(rom.ReadBytes(offset, length).Select(b => Show(rom, b)).ToArray()).TrimEnd(' ', '\0').Replace('@', ' ');
        int WriteNames(List<Entry> entries, Func<int, int> offsetOf, int length, int count)
        {
            int done = 0;
            foreach (var entry in entries)
            {
                if (entry.id >= count) continue;
                int offset = offsetOf(entry.id);
                if (!string.Equals(ReadName(offset, length), entry.en, StringComparison.OrdinalIgnoreCase)) { kept++; continue; }
                if (entry.nl.Length > length) { tooLong++; continue; }
                var bytes = new byte[length];
                for (int i = 0; i < length; i++)
                    bytes[i] = i < entry.nl.Length ? (entry.nl[i] == ' ' ? (byte)'@' : Code(rom, entry.nl[i]) ?? (byte)'@') : (byte)' ';
                rom.WriteBytes(offset, bytes);
                done++;
            }
            return done;
        }
        int items = WriteNames(list.items, id => Battle.ItemTable.RecordOffset(rom, id), 12, 256);
        int spells = WriteNames(list.spells, id => Battle.SpellTablePatch.SpellOffset(rom, id), 8, Battle.SpellTablePatch.SpellCount(rom));
        int monsters = WriteNames(list.monsters, id => Battle.MonsterTable.RecordOffset(rom, id), 10, Battle.MonsterTable.Count(rom));
        rom.FixChecksum();
        return new NamesResult(items, spells, monsters, kept, tooLong);
    }

    // glyphs

    // an 8x16 glyph: the letter's body (the lowest run of inked rows, so an i loses its dot) with the accent ending one
    // row above it. a capital's accent touches the letter (rows 1-2) because the intro's narration only shows rows 2-13
    private static byte[] TextGlyph(byte[] letter, Accent accent, bool capital) => Compose(letter, accent, rowsAbove: capital ? 1 : 2);

    // an 8x8 glyph for the small font (item lists, the restored descriptions). its lower case letters start on row 2,
    // so the accent takes rows 0-1 and touches the letter, like the dutch lufia 2's menu font draws them.
    // a capital loses two of its repeated rows to make room (capital E-acute is a shorter E there too).
    // before, every accent in this font was one dot on row 0 (all four e's looked the same, i-diaeresis like an i)
    private static byte[] SmallGlyph(byte[] letterRows, Accent accent)
    {
        var rows = letterRows.ToList();
        int bottom = rows.FindLastIndex(r => r != 0);
        if (bottom < 0) return letterRows;
        int top = bottom;
        while (top > 0 && rows[top - 1] != 0) top--;
        for (int r = 0; r < top; r++) rows[r] = 0;   // an i's dot goes
        // a capital: drop rows that repeat the one above until the letter starts on row 2
        for (int r = top + 1; top < 2 && r < bottom; )
            if (rows[r] == rows[r - 1]) { rows.RemoveAt(r); rows.Insert(0, 0); top++; r++; }
            else r++;
        var glyph = rows.ToArray();
        int ink = 0;
        for (int r = top; r <= bottom; r++) ink |= glyph[r];
        int left = 0, right = 7;
        while (left < 7 && (ink & 0x80 >> left) == 0) left++;
        while (right > 0 && (ink & 0x80 >> right) == 0) right--;
        int centerColumn = (left + right + 1) / 2;
        int low = top - 1, high = top - 2;
        void Dot(int row, int col) { if (row >= 0 && row < glyph.Length && col is >= 0 and < 8) glyph[row] |= (byte)(0x80 >> col); }
        switch (accent)
        {
            case Accent.Acute: Dot(low, centerColumn); Dot(high, centerColumn + 1); break;
            case Accent.Grave: Dot(low, centerColumn); Dot(high, centerColumn - 1); break;
            case Accent.Diaeresis: Dot(high, centerColumn - 1); Dot(high, centerColumn + 1); break;
            case Accent.Circumflex: Dot(high, centerColumn); Dot(low, centerColumn - 1); Dot(low, centerColumn + 1); break;
        }
        return glyph;
    }

    private static byte[] Compose(byte[] letterGlyph, Accent accent, int rowsAbove)
    {
        var glyph = (byte[])letterGlyph.Clone();
        int bottom = Array.FindLastIndex(glyph, r => r != 0);
        if (bottom < 0) return glyph;
        int top = bottom;
        while (top > 0 && glyph[top - 1] != 0) top--;
        for (int r = 0; r < top; r++) glyph[r] = 0;   // an i's dot goes
        // the column the accent sits over = the middle of the body's ink
        int ink = 0;
        for (int r = top; r <= bottom; r++) ink |= glyph[r];
        int left = 0, right = 7;
        while (left < 7 && (ink & 0x80 >> left) == 0) left++;
        while (right > 0 && (ink & 0x80 >> right) == 0) right--;
        int centerColumn = (left + right + 1) / 2;
        int accentRow = top - rowsAbove;   // the accent's lower row
        void Dot(int row, int col) { if (row >= 0 && row < glyph.Length && col is >= 0 and < 8) glyph[row] |= (byte)(0x80 >> col); }
        if (accentRow < 1)
        {
            // no room above (a capital in the small font), so one dot on the top row
            Dot(0, accent switch { Accent.Acute => centerColumn + 1, Accent.Grave => centerColumn - 1, _ => centerColumn });
            return glyph;
        }
        switch (accent)
        {
            case Accent.Acute: Dot(accentRow, centerColumn); Dot(accentRow - 1, centerColumn + 1); break;
            case Accent.Grave: Dot(accentRow, centerColumn); Dot(accentRow - 1, centerColumn - 1); break;
            case Accent.Diaeresis: Dot(accentRow, centerColumn - 1); Dot(accentRow, centerColumn + 1); break;
            case Accent.Circumflex: Dot(accentRow, centerColumn - 1); Dot(accentRow, centerColumn + 1); Dot(accentRow - 1, centerColumn); break;
        }
        return glyph;
    }
}
