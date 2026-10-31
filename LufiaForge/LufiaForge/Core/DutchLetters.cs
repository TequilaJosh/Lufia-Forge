using LufiaForge.Core.Maps;

namespace LufiaForge.Core;

/// <summary>
/// Dutch letters for Lufia 1 (asked for by Sinrevi, who writes a Dutch Lufia 1): é è ë ê ï ö ü á à ó ú É take the
/// places of # $ % * ; = ^ \ ] ` | _, twelve signs the game never shows as text (every dialogue box of the US game,
/// 2026-10-30: it uses ! " &amp; ' + , - . : &lt; &gt; ? and ~ once, in the debug room; item, spell and monster names use
/// only ' and @, the space inside a name). Not ~ : the name screen fills the letters still to type with it (checked in
/// BizHawk). = is the last sign of the name screen's grid, so a name can have an ö. Lufia 1 has no free character codes:
/// 10-1F and 80-FF are compressed words, so a letter has to replace a sign, as the Dutch Lufia II did (" is ë there).
/// <para>The glyphs are made from the ROM's own letters (e, i, o, u, a, E) with an accent above, in both the text font
/// (dialogue, choice windows, the ring menus' lists) and the small font (names, menu titles), so they match whatever
/// font the project uses (<see cref="FontPatch"/> included: apply this after it). The text editors show and take the
/// letters themselves; the original glyphs are kept in the added ROM space so they can be put back.</para>
/// </summary>
public static class DutchLetters
{
    /// <summary>The letters and the sign each one replaces.</summary>
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
        var e = ExpansionSpace.Load(rom).Entries.FirstOrDefault(x => x.Kind == ExpansionSpace.Kind.Patch && x.Id == BackupId);
        return e != null && rom.ReadBytes(e.Start, 4).SequenceEqual(Magic) ? e.Start : 0;
    }

    public static bool IsApplied(RomBuffer rom) => rom.Game == GameKind.Lufia1 && BackupOffset(rom) > 0;

    /// <summary>The letter a code shows in <paramref name="rom"/>, or null (the sign itself).</summary>
    public static char? LetterFor(RomBuffer rom, byte code)
    {
        foreach (var (l, c) in Map) if (c == code) return IsApplied(rom) ? l : null;
        return null;
    }

    /// <summary>Puts the letters in (the ROM is expanded to 2 MB for the backup of the signs they replace).</summary>
    public static void Apply(RomBuffer rom)
    {
        if (rom.Game != GameKind.Lufia1) throw new InvalidOperationException("The Dutch letters are for Lufia 1.");
        if (rom.Length < MapWriter.ExpandedSize) rom.Expand(MapWriter.ExpandedSize);
        if (!IsApplied(rom))
        {
            var space = ExpansionSpace.Load(rom);
            int at = space.Allocate(4 + Map.Length * 32, ExpansionSpace.Kind.Patch, BackupId);
            rom.WriteBytes(at, Magic);
            for (int i = 0; i < Map.Length; i++)
            {
                rom.WriteBytes(at + 4 + i * 32, rom.ReadBytes(FontPatch.Lufia1Font + Map[i].Code * 16, 16));
                rom.WriteBytes(at + 4 + i * 32 + 16, rom.ReadBytes(FontPatch.Lufia1Small + Map[i].Code * 16, 16));
            }
            space.Save();
        }
        foreach (var (letter, code) in Map)
        {
            var (b, accent) = Parts(letter);
            rom.WriteBytes(FontPatch.Lufia1Font + code * 16, TextGlyph(rom.ReadBytes(FontPatch.Lufia1Font + b * 16, 16), accent, char.IsUpper(b)));
            var small = rom.ReadBytes(FontPatch.Lufia1Small + b * 16, 16);
            var rows = Enumerable.Range(0, 8).Select(r => (byte)(small[2 * r] | small[2 * r + 1])).ToArray();
            var made = SmallGlyph(rows, accent);
            var tile = new byte[16];
            for (int r = 0; r < 8; r++) tile[2 * r] = tile[2 * r + 1] = made[r];
            rom.WriteBytes(FontPatch.Lufia1Small + code * 16, tile);
        }
        rom.FixChecksum();
        Modules.TextEditor.TextDecoder.InvalidateDictionaryCache();
    }

    /// <summary>Puts the signs back.</summary>
    public static void Remove(RomBuffer rom)
    {
        int b = BackupOffset(rom);
        if (b == 0) return;
        for (int i = 0; i < Map.Length; i++)
        {
            rom.WriteBytes(FontPatch.Lufia1Font + Map[i].Code * 16, rom.ReadBytes(b + 4 + i * 32, 16));
            rom.WriteBytes(FontPatch.Lufia1Small + Map[i].Code * 16, rom.ReadBytes(b + 4 + i * 32 + 16, 16));
        }
        var space = ExpansionSpace.Load(rom);
        space.Free(b);
        space.Save();
        rom.FixChecksum();
        Modules.TextEditor.TextDecoder.InvalidateDictionaryCache();
    }

    /// <summary>The character byte <paramref name="b"/> shows in names and text (its letter when the letters are in).</summary>
    public static char Show(RomBuffer rom, byte b) => LetterFor(rom, b) ?? (char)b;

    /// <summary>The byte for <paramref name="c"/> (a letter's code when the letters are in), or null.</summary>
    public static byte? Code(RomBuffer rom, char c)
    {
        if (c > 0x7F)
        {
            if (!IsApplied(rom)) return null;
            foreach (var (l, code) in Map) if (l == c) return code;
            return null;
        }
        return (byte)c;
    }

    /// <summary>Text with the letters written as the codes they have in the game (when the letters are in).</summary>
    public static string ToCodes(string text)
    {
        foreach (var (l, c) in Map) text = text.Replace(l, (char)c);
        return text;
    }

    // ── Dutch names from the Dutch Lufia II ──

    /// <summary>What <see cref="ApplyLufia2Names"/> did.</summary>
    public sealed record NamesResult(int Items, int Spells, int Monsters, int Kept, int TooLong)
    {
        public override string ToString() =>
            $"{Items} item, {Spells} spell and {Monsters} monster names are Dutch now" +
            (Kept > 0 ? $"; {Kept} the project had renamed itself were left as they are" : "") +
            (TooLong > 0 ? $"; {TooLong} too long for Lufia 1 kept their English name" : "") + ".";
    }

    private sealed record Entry(int id, string en, string nl);
    private sealed record NameList(List<Entry> items, List<Entry> spells, List<Entry> monsters);

    /// <summary>The Dutch names Lufia Forge keeps for Lufia 1 (Translations/Lufia1_Dutch.json): the items, spells and
    /// monsters Lufia 1 shares with Lufia II (the same English name), named as the Dutch translation of Lufia II names
    /// them. Kept in Lufia Forge so no Dutch ROM is needed.</summary>
    private static NameList LoadNames()
    {
        using var st = typeof(DutchLetters).Assembly.GetManifestResourceStream("Translations.Lufia1_Dutch.json")
            ?? throw new InvalidOperationException("The Dutch name list is missing from this build of Lufia Forge.");
        return System.Text.Json.JsonSerializer.Deserialize<NameList>(st) ?? throw new InvalidOperationException("The Dutch name list can't be read.");
    }

    /// <summary>Gives the items, spells and monsters Lufia 1 shares with Lufia II their Dutch names. A name the project
    /// changed itself (it no longer reads as the US game's) is left alone; a name longer than Lufia 1 allows keeps its
    /// English one. Names with Dutch letters bring the letters in (<see cref="Apply"/>).</summary>
    public static NamesResult ApplyLufia2Names(RomBuffer rom)
    {
        if (rom.Game != GameKind.Lufia1) throw new InvalidOperationException("These names are for Lufia 1.");
        var list = LoadNames();
        if (list.items.Concat(list.spells).Concat(list.monsters).Any(e => e.nl.Any(c => c > 0x7F)) && !IsApplied(rom)) Apply(rom);
        int kept = 0, tooLong = 0;
        string Read(int at, int len) => new string(rom.ReadBytes(at, len).Select(b => Show(rom, b)).ToArray()).TrimEnd(' ', '\0').Replace('@', ' ');
        int Do(List<Entry> entries, Func<int, int> at, int len, int count)
        {
            int done = 0;
            foreach (var e in entries)
            {
                if (e.id >= count) continue;
                int o = at(e.id);
                if (!string.Equals(Read(o, len), e.en, StringComparison.OrdinalIgnoreCase)) { kept++; continue; }
                if (e.nl.Length > len) { tooLong++; continue; }
                var bytes = new byte[len];
                for (int i = 0; i < len; i++)
                    bytes[i] = i < e.nl.Length ? (e.nl[i] == ' ' ? (byte)'@' : Code(rom, e.nl[i]) ?? (byte)'@') : (byte)' ';
                rom.WriteBytes(o, bytes);
                done++;
            }
            return done;
        }
        int items = Do(list.items, id => Battle.ItemTable.RecordOffset(rom, id), 12, 256);
        int spells = Do(list.spells, id => Battle.SpellTablePatch.SpellOffset(rom, id), 8, Battle.SpellTablePatch.SpellCount(rom));
        int monsters = Do(list.monsters, id => Battle.MonsterTable.RecordOffset(rom, id), 10, Battle.MonsterTable.Count(rom));
        rom.FixChecksum();
        return new NamesResult(items, spells, monsters, kept, tooLong);
    }

    // ── glyphs ──

    /// <summary>An 8x16 glyph: the letter's body (the lowest run of inked rows, so an i loses its dot) with the accent
    /// ending one row above it; a capital's accent touches the letter (rows 1-2), as the intro's narration shows only rows
    /// 2-13.</summary>
    private static byte[] TextGlyph(byte[] letter, Accent accent, bool capital) => Compose(letter, accent, rowsAbove: capital ? 1 : 2);

    private static byte[] SmallGlyph(byte[] rows, Accent accent) => Compose(rows, accent, rowsAbove: 2);

    private static byte[] Compose(byte[] g, Accent accent, int rowsAbove)
    {
        var o = (byte[])g.Clone();
        int bottom = Array.FindLastIndex(o, r => r != 0);
        if (bottom < 0) return o;
        int top = bottom;
        while (top > 0 && o[top - 1] != 0) top--;
        for (int r = 0; r < top; r++) o[r] = 0;   // an i's dot goes
        // the column the accent sits over: the middle of the body's ink
        int ink = 0;
        for (int r = top; r <= bottom; r++) ink |= o[r];
        int left = 0, right = 7;
        while (left < 7 && (ink & 0x80 >> left) == 0) left++;
        while (right > 0 && (ink & 0x80 >> right) == 0) right--;
        int cx = (left + right + 1) / 2;
        int a = top - rowsAbove;   // the accent's lower row
        void Dot(int row, int col) { if (row >= 0 && row < o.Length && col is >= 0 and < 8) o[row] |= (byte)(0x80 >> col); }
        if (a < 1)
        {
            // no room above (a capital in the small font): one dot on the top row
            Dot(0, accent switch { Accent.Acute => cx + 1, Accent.Grave => cx - 1, _ => cx });
            return o;
        }
        switch (accent)
        {
            case Accent.Acute: Dot(a, cx); Dot(a - 1, cx + 1); break;
            case Accent.Grave: Dot(a, cx); Dot(a - 1, cx - 1); break;
            case Accent.Diaeresis: Dot(a, cx - 1); Dot(a, cx + 1); break;
            case Accent.Circumflex: Dot(a, cx - 1); Dot(a, cx + 1); Dot(a - 1, cx); break;
        }
        return o;
    }
}
