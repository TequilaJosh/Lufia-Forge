using System.IO;
namespace LufiaForge.Core.Lufia2;

/// <summary>
/// The Dutch Lufia II: a translation built on the European release (header title "LUFIA FOR DUTCH", country 07, 3 MB).
/// Its code and most of its data sit elsewhere than in the US game, so Lufia Forge doesn't edit it as it is: it puts
/// the Dutch text into a Lufia II (USA) ROM, or a hack built on one (Frue Lufia and the others), which every page then
/// edits as usual. Found 2026-10-30 on the Dutch ROM (SHA1 2CBB1A60...):
/// <list type="bullet">
/// <item>The map header table (event blocks, start-up scripts, map names, the persistent characters) is at $A7:8000
/// (file 0x138000) instead of $87:8000, and its u24 values count from bank $A7. The events are the US game's commands
/// in the same order (1,557 of 1,564 scripts have the same number of lines; the other 7 split or join lines).</item>
/// <item>The dictionary is at the US address (file 0x76A00) and holds Dutch words.</item>
/// <item>The dialogue font is at $A1:B222 and starts at character 10, not 20: 10-1F are À Á È É Ë Ï Ò Ó Ö Ù Ú Ü à á è
/// é, and a few signs are letters ("#" ï, "~" ê, "^" Ñ, "\" ñ, "$" ò, "&amp;" ó, "*" ù, "&lt;" â, "&gt;" ì, "`" ¿, "@"
/// a box). The glyph code that reads it: the US game's "CMP #$10 / BNE / LDA #$20 / SEC / SBC #$20" (10 shows as a
/// space, 11-1F as nothing) became "SEC / SBC #$10", and the LDA $9A:F970,X after it reads the new font.</item>
/// </list>
/// </summary>
public static class L2Dutch
{
    public const int HeaderTable = 0x138000, Country = 0x07;

    /// <summary>Whether <paramref name="rom"/> is the Dutch Lufia II (its events where the Dutch ROM has them).</summary>
    public static bool Is(byte[] rom)
    {
        if (rom.Length < 0x300000 || rom[0x7FD9] != Country || !GameDetect.Title(rom).StartsWith("LUFIA", StringComparison.OrdinalIgnoreCase)) return false;
        try
        {
            var v = View(rom);
            return L2Events.Block(v, 3) >= 0 && L2Events.Block(v, 4) >= 0;
        }
        catch { return false; }
    }

    /// <summary>Reads the Dutch ROM from a .sfc / .smc file or a .zip holding one (a copier header is dropped), or throws.</summary>
    public static byte[] ReadFile(string path)
    {
        byte[] d;
        if (path.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
        {
            using var zip = System.IO.Compression.ZipFile.OpenRead(path);
            var entry = zip.Entries.FirstOrDefault(e => e.Name.EndsWith(".sfc", StringComparison.OrdinalIgnoreCase) || e.Name.EndsWith(".smc", StringComparison.OrdinalIgnoreCase))
                        ?? throw new InvalidOperationException("That zip has no .sfc or .smc file in it.");
            if (entry.Length > 8 << 20) throw new InvalidOperationException("The ROM in that zip is too big to be Lufia II.");
            using var st = entry.Open();
            using var ms = new MemoryStream();
            st.CopyTo(ms);
            d = ms.ToArray();
        }
        else d = File.ReadAllBytes(path);
        if (d.Length % 1024 == 512) d = d[512..];
        if (!Is(d)) throw new InvalidOperationException($"That isn't the Dutch Lufia II (its title reads \"{GameDetect.Title(d)}\").");
        return d;
    }

    /// <summary>A copy of the Dutch ROM laid out the way Lufia Forge reads Lufia II, for reading only: its map header
    /// table copied to $87:8000 with every map's u24 values moved on by 0x20 banks ($A7 + n read as $87 + n + 20), so
    /// <see cref="L2Events"/>, <see cref="L2EventBlocks"/> and <see cref="L2Maps.Name"/> read the Dutch events and
    /// names unchanged.</summary>
    public static RomBuffer View(byte[] dutch)
    {
        var v = (byte[])dutch.Clone();
        Array.Copy(dutch, HeaderTable, v, L2Events.HeaderTable, 0x8000);
        for (int m = 0; m < L2Maps.Count; m++)
            foreach (int f in new[] { 0, 3 })
            {
                int e = L2Events.HeaderTable + 0x10 + m * 8 + f;
                int val = v[e] | v[e + 1] << 8 | v[e + 2] << 16;
                if (val == 0xFFFFFF) continue;
                val += 0x200000;
                v[e] = (byte)val; v[e + 1] = (byte)(val >> 8); v[e + 2] = (byte)(val >> 16);
            }
        return new RomBuffer(v, "Dutch Lufia II (read only)");
    }

    /// <summary>What an import did.</summary>
    public sealed class Report
    {
        public int Maps, Scripts, Lines, WholeScripts, Names, Descriptions, MovedBlocks, EnglishLines;
        public List<string> Notes { get; } = new();
        public override string ToString() =>
            $"{Lines:N0} lines of dialogue in {Scripts:N0} events on {Maps} maps" +
            (WholeScripts > 0 ? $" ({WholeScripts} events whose lines the translation splits differently were copied whole)" : "") +
            $", {Names:N0} names and {Descriptions:N0} descriptions, the fonts with the Dutch letters" +
            (Notes.Count > 0 ? ". " + string.Join(" ", Notes) : ".");
    }

    /// <summary>Puts the Dutch translation into <paramref name="target"/> (Lufia II, US layout): both fonts with the Dutch
    /// letters, the names (items, spells, monsters, IP attacks, monster and capsule attacks, maps), the descriptions and
    /// every event's text. The target's own commands stay; only events whose lines the
    /// translation splits or joins differently are copied whole from the Dutch ROM.</summary>
    public static Report Import(RomBuffer target, byte[] dutch, Action<string>? progress = null)
    {
        if (target.Game != GameKind.Lufia2) throw new InvalidOperationException("The text goes into a Lufia II (USA) ROM, or a hack built on one.");
        if (!Is(dutch)) throw new InvalidOperationException("That isn't the Dutch Lufia II.");
        var nl = View(dutch);
        var report = new Report();
        progress?.Invoke("Font");
        ImportFont(target, dutch);
        progress?.Invoke("Names");
        ImportNames(target, dutch, ReadNames(dutch), report);
        var blocks = new Dictionary<int, IReadOnlyDictionary<L2EventBlocks.Entry, L2EventScript>>();
        for (int m = 0; m < L2Maps.Count; m++)
        {
            progress?.Invoke($"Map {m:X2}");
            ImportMap(target, nl, m, report, blocks);
        }
        // every block laid out again at once, in the room the game's blocks take (only the overflow goes elsewhere)
        progress?.Invoke("Events");
        var stuck = new List<int>();
        report.MovedBlocks = L2EventBlocks.RepackAll(target, blocks, stuck);
        // a block that can't be laid out again: each line written over its old bytes when it fits
        foreach (int b in stuck)
            if (blocks.TryGetValue(b, out var ch))
                foreach (var st in ch.Values.Distinct().Where(x => x.Start >= 0).SelectMany(x => x.Steps).Where(x => x.IsText && x.At >= 0).DistinctBy(x => x.At))
                    if (!InPlace(target, st)) report.EnglishLines++;
        if (stuck.Count > 0) report.Notes.Add($"{stuck.Count} map{(stuck.Count == 1 ? "'s events" : "s' events")} can't be laid out again (a script whose steps overlap), so {(stuck.Count == 1 ? "its" : "their")} lines were written in place: {report.EnglishLines} too long for their place stay as they were.");
        target.FixChecksum();
        return report;
    }

    // ── the font ──

    /// <summary>The US game's glyph code: CMP #$0010 / BNE +3 / LDA #$0020 / SEC / SBC #$0020 / BCC ... / ASL x4 / CLC /
    /// TAX / LDY $09B1 / STY $09B5 / SEP #$20 / LDA #$7E / PHA / PLB / LDA #$10 / STA $58 / LDA long,X (the font).</summary>
    private static readonly byte[] UsGlyphCode = Hex("C9 10 00 D0 03 A9 20 00 38 E9 20 00 90 30 0A 0A 0A 0A 18 AA AC B1 09 8C B5 09 E2 20 A9 7E 48 AB A9 10 85 58 BF");
    /// <summary>The same once the font starts at character 10 (the Dutch ROM's code; written over the US code with 8 NOPs first).</summary>
    private static readonly byte[] DutchGlyphCode = Hex("38 E9 10 00 90 30 0A 0A 0A 0A 18 AA AC B1 09 8C B5 09 E2 20 A9 7E 48 AB A9 10 85 58 BF");
    public const int FontFirst = 0x10, FontGlyphs = 0x70, GlyphSize = 16;

    /// <summary>File offset of the glyph code's LDA long,X operand in <paramref name="rom"/> and whether the font starts
    /// at character 10 there; -1 when the code isn't found.</summary>
    public static (int Operand, bool From10) FontSite(RomBuffer rom)
    {
        var all = rom.ToArray();
        int us = Find(all, UsGlyphCode, 0, 0x10000);
        if (us >= 0) return (us + UsGlyphCode.Length, false);
        int nl = Find(all, DutchGlyphCode, 0, 0x10000);
        return nl >= 0 ? (nl + DutchGlyphCode.Length, true) : (-1, false);
    }

    private static void ImportFont(RomBuffer target, byte[] dutch)
    {
        var src = new RomBuffer(dutch, "");
        var (nlOp, nlFrom10) = FontSite(src);
        if (nlOp < 0 || !nlFrom10) throw new InvalidOperationException("The Dutch ROM's font code isn't where it was expected.");
        int nlFont = L2Rom.SnesToFile((int)src.ReadUInt24Le(nlOp));
        byte[] font = src.ReadBytes(nlFont, FontGlyphs * GlyphSize);
        var (op, from10) = FontSite(target);
        if (op < 0) throw new InvalidOperationException("This ROM's dialogue font code isn't the US game's, so the Dutch letters can't be added.");
        int at;
        if (from10)
        {
            // imported before: the font is ours already, written again in its place
            at = L2Rom.SnesToFile((int)target.ReadUInt24Le(op));
        }
        else
        {
            at = L2Rom.Allocate(target, font.Length);
            int code = op - UsGlyphCode.Length;
            target.WriteBytes(code, Hex("EA EA EA EA EA EA EA EA 38 E9 10 00"));
        }
        target.WriteBytes(at, font);
        int snes = L2Rom.FileToSnes(at);
        target.WriteBytes(op, new[] { (byte)snes, (byte)(snes >> 8), (byte)(snes >> 16) });
        // the routine that clears a window's rest ($82:90FB: LDX #$0010 / STX $09AF / JSL the glyph code) draws
        // character 10, a space in the US font and À in the Dutch one: the Dutch ROM draws 20 there
        var all = target.ToArray();
        int fill = Find(all, BlankFill, 0, 0x40000);
        if (fill >= 0) target.WriteByte(fill + 6, 0x20);
    }

    /// <summary>LDA $02 / BNE / RTS / LDX #$0010 / STX $09AF / JSL: the window blank (US $82:90F6).</summary>
    private static readonly byte[] BlankFill = Hex("A5 02 D0 01 60 A2 10 00 8E AF 09 22");

    // ── the dialogue ──

    private static void ImportMap(RomBuffer target, RomBuffer nl, int map, Report report,
        Dictionary<int, IReadOnlyDictionary<L2EventBlocks.Entry, L2EventScript>> blocks)
    {
        bool any = false;
        // the start-up script
        int ti = L2EventBlocks.InitScript(target, map), ni = L2EventBlocks.InitScript(nl, map);
        if (ti > 0 && ti < target.Length && ni > 0 && ni < nl.Length)
        {
            var t = L2EventScript.Read(target, ti, L2EventBlocks.InitBase(ti));
            var n = L2EventScript.Read(nl, ni, L2EventBlocks.InitBase(ni));
            if (t.Problem == null && n.Problem == null && t.Steps.Any(s => s.IsText))
            {
                var use = Translate(t, n, report);
                if (use != null) { L2EventBlocks.SaveInit(target, map, use); any = true; }
            }
        }
        // the block's events, paired by list, id and how many came before with the same list and id
        int tb = L2Events.Block(target, map), nb = L2Events.Block(nl, map);
        if (tb >= 0 && nb >= 0 && !blocks.ContainsKey(tb))   // (maps sharing a block: the first one's)
        {
            var nlByKey = Keyed(L2EventBlocks.ReadLists(nl, nb)).ToDictionary(x => x.Key, x => x.Entry);
            var nScripts = new Dictionary<int, L2EventScript>();
            var done = new Dictionary<int, L2EventScript?>();
            var changed = new Dictionary<L2EventBlocks.Entry, L2EventScript>();
            foreach (var (key, e) in Keyed(L2EventBlocks.ReadLists(target, tb)))
            {
                if (!e.IsScript || !nlByKey.TryGetValue(key, out var ne)) continue;
                if (!done.TryGetValue(e.Offset, out var use))
                {
                    var t = L2EventScript.Read(target, tb + e.Offset, tb);
                    if (!nScripts.TryGetValue(ne.Offset, out var n)) nScripts[ne.Offset] = n = L2EventScript.Read(nl, nb + ne.Offset, nb);
                    use = t.Problem == null && n.Problem == null && t.Steps.Any(s => s.IsText) ? Translate(t, n, report) : null;
                    done[e.Offset] = use;
                }
                if (use != null) changed[e] = use;
            }
            blocks[tb] = changed;
            if (changed.Count > 0) any = true;
        }
        if (any) report.Maps++;
    }

    /// <summary>Writes a step's (new) text over its old bytes, filled up with pauses of no time (04 80) and a space; false
    /// when it is longer than its place.</summary>
    private static bool InPlace(RomBuffer rom, L2Step st)
    {
        if (!st.TextChanged) return true;
        var text = L2EventText.Encode(rom, st.Text.Replace("\r", "")).ToList();
        if (text.Count > 0 && text[^1] == 0) text.RemoveAt(text.Count - 1);
        int room = st.OriginalLength - st.Bytes.Length - 1;   // the text's bytes without its end code
        if (text.Count > room) return false;
        int fill = room - text.Count;
        if (fill % 2 == 1) { text.Add((byte)' '); fill--; }
        for (; fill > 0; fill -= 2) { text.Add(4); text.Add(0x80); }
        text.Add(st.End);
        rom.WriteBytes(st.At + st.Bytes.Length, text.ToArray());
        return true;
    }

    private static IEnumerable<((int, int, int) Key, L2EventBlocks.Entry Entry)> Keyed(List<L2EventBlocks.Entry> list)
    {
        var seen = new Dictionary<(int, int), int>();
        foreach (var e in list)
        {
            int k = seen.GetValueOrDefault((e.Head, e.Id));
            seen[(e.Head, e.Id)] = k + 1;
            yield return ((e.Head, e.Id, k), e);
        }
    }

    /// <summary>The target script with the Dutch lines, or the Dutch script itself (ready to be written into the target)
    /// when the two don't have the same number of lines; null when nothing changes.</summary>
    private static L2EventScript? Translate(L2EventScript target, L2EventScript dutch, Report report)
    {
        var tt = target.Steps.Where(s => s.IsText).ToList();
        var nt = dutch.Steps.Where(s => s.IsText).ToList();
        report.Scripts++;
        if (tt.Count == nt.Count)
        {
            for (int i = 0; i < tt.Count; i++) { tt[i].Text = nt[i].Text; tt[i].End = nt[i].End; }
            report.Lines += tt.Count;
            return target;
        }
        // lines split or joined differently: the Dutch script whole, every line encoded again for the target
        var map = new Dictionary<L2Step, L2Step>();
        foreach (var s in dutch.Steps)
            map[s] = new L2Step
            {
                At = -1, Bytes = (byte[])s.Bytes.Clone(), OriginalBytes = s.OriginalBytes, IsText = s.IsText, TextRaw = s.TextRaw,
                Text = s.Text, OriginalText = s.IsText ? "\u0000" : s.OriginalText, End = s.End, OriginalEnd = s.OriginalEnd,
                HasBackReference = true, OriginalLength = s.OriginalLength,
            };
        foreach (var s in dutch.Steps)
            foreach (var j in s.Jumps)
            {
                if (j.Step == null) return null;   // a jump out of the script: left as it is
                map[s].Jumps.Add(new L2Jump { Step = map[j.Step] });
            }
        var copy = L2EventScript.New(target.JumpBase);
        copy.Steps.Clear();
        copy.Steps.AddRange(dutch.Steps.Select(s => map[s]));
        report.Lines += nt.Count;
        report.WholeScripts++;
        return copy;
    }

    // ── names, descriptions, the menu font ──

    /// <summary>The Dutch ROM's tables (found 2026-10-30 by the code that reads them): item names 12 letters each, the
    /// spell table ($B3:FA5B, records at bank $B3 + u16 + FA5B), the monster table ($B4:85C0, the US layout), the IP
    /// attack table ($84:8F20, records in bank $84), monster attack names (u16 | 8000 from $85:8000: an animation byte, the name, 00),
    /// capsule attack names (u16 from the table), descriptions ($B5:8000; their word list at $9E:C7E8, where the US game
    /// has its item names) and the menu font (the US place, $9F:8500, 10-1F the accented letters).</summary>
    private const int NlItemNames = 0xF5ED9, NlSpellTable = 0x19FA5B, NlSpellAdc = 0xFA5B, NlSpellBank = 0xB3, NlMonsterTable = 0x1A05C0,
        NlIpTable = 0x20F20, NlAttackNames = 0x2F2C7, NlCapsuleAttacks = 0x12E100, NlDescTable = 0x1A8000, NlDescWords = 0xF47E8, SmallFont = 0xF8500;

    public sealed class Names
    {
        public List<string> Items { get; } = new();
        public List<string> Spells { get; } = new();
        public List<string> Monsters { get; } = new();
        public List<string> IpAttacks { get; } = new();
        public List<string> AttackNames { get; } = new();
        public List<string> CapsuleAttacks { get; } = new();
        public List<string> MapNames { get; } = new();
        /// <summary>Descriptions by entry (the game's layout: items 0-1D2, spells 200+, IP attacks 231+).</summary>
        public Dictionary<int, string> Descriptions { get; } = new();
    }

    /// <summary>Every name and description of the Dutch ROM, with the letters themselves (é, not a code).</summary>
    public static Names ReadNames(byte[] dutch)
    {
        var nl = new RomBuffer(dutch, "");
        var view = View(dutch);
        var n = new Names();
        for (int i = 0; i < L2Battle.ItemCount; i++) n.Items.Add(L2Rom.Name(nl, NlItemNames + i * L2Battle.ItemNameLength, L2Battle.ItemNameLength));
        for (int i = 0; i < L2Battle.SpellCountOriginal; i++)
            n.Spells.Add(L2Rom.Name(nl, L2Rom.SnesToFile(NlSpellBank << 16 | (nl.ReadUInt16Le(NlSpellTable + i * 2) + NlSpellAdc) & 0xFFFF), L2Battle.SpellNameLength));
        for (int i = 0; i < L2Battle.MonsterCount; i++)
            n.Monsters.Add(L2Rom.Name(nl, L2Rom.InBank(NlMonsterTable, nl.ReadUInt16Le(NlMonsterTable + i * 2)), L2Battle.MonsterNameLength));
        for (int i = 0; i < L2Battle.IpCountOriginal; i++)
            n.IpAttacks.Add(Zero(nl, L2Rom.SnesToFile(0x840000 | nl.ReadUInt16Le(NlIpTable + i * 2)) + 6));
        for (int i = 0; i < L2AiScripts.AttackNameCount; i++)
            n.AttackNames.Add(Zero(nl, 0x28000 + (nl.ReadUInt16Le(NlAttackNames + i * 2) & 0x7FFF) + 1));
        for (int i = 0; i < L2Capsules.AttackNameCount; i++)
            n.CapsuleAttacks.Add(Zero(nl, NlCapsuleAttacks + nl.ReadUInt16Le(NlCapsuleAttacks + i * 2)));
        for (int m = 0; m < L2Maps.Count; m++) n.MapNames.Add(L2Maps.Name(view, m));
        var words = DescriptionWords(nl);
        var dict = L2Text.Words(nl);
        for (int e = 0; e < L2Descriptions.Count; e++) n.Descriptions[e] = Description(nl, e, words, dict);
        return n;
    }

    private static string Zero(RomBuffer rom, int at) => new(rom.ReadUntil(at, 0, 40).Select(b => L2Charset.ToChar(rom, b) ?? '?').ToArray());

    private static string[] DescriptionWords(RomBuffer nl)
    {
        // the first offset is the table's size, as in the US list
        int n = nl.ReadUInt16Le(NlDescWords) / 2;
        return Enumerable.Range(0, n).Select(k => Zero(nl, NlDescWords + nl.ReadUInt16Le(NlDescWords + k * 2))).ToArray();
    }

    /// <summary>A Dutch description: like the US game's, except that the main dictionary's codes are 0D-1F and the second
    /// byte of a two-byte code counts from 1 ($85:C4F1 in the Dutch ROM: SBC #$0D or SBC #$05, then DEC on the next byte).</summary>
    private static string Description(RomBuffer nl, int entry, string[] words, string[] dict)
    {
        int at = NlDescTable + nl.ReadUInt16Le(NlDescTable + entry * 2);
        var sb = new System.Text.StringBuilder();
        string Get(string[] list, int k) => k >= 0 && k < list.Length ? list[k] : "?";
        for (int p = at, guard = 0; guard < 400; guard++)
        {
            byte c = nl.ReadByte(p++);
            if (c == 0) break;
            if (c >= 0x80) sb.Append(Get(words, c & 0x7F));
            else if (c >= 0x20) sb.Append(L2Charset.ToChar(nl, c) ?? '?');
            else if (c == 0x0A) sb.Append('\n');
            else if (c > 0x0A) sb.Append(Get(dict, (c - 0x0D) * 0xFF + nl.ReadByte(p++) - 1));
            else if (c >= 5) sb.Append(Get(words, (c - 5) * 0xFF + nl.ReadByte(p++) - 1 + 0x80));
        }
        return sb.ToString();
    }

    /// <summary>Writes the Dutch names, descriptions and menu font into <paramref name="target"/>.</summary>
    private static void ImportNames(RomBuffer target, byte[] dutch, Names n, Report report)
    {
        // the menu font: the accented letters at 10-1F and the signs that are letters
        target.WriteBytes(SmallFont + 0x100, dutch.AsSpan(SmallFont + 0x100, 0x700).ToArray());
        int items = Math.Min(n.Items.Count, L2Battle.ItemCountIn(target));
        for (int i = 0; i < items; i++) L2Rom.WriteName(target, L2Battle.ItemNames + i * L2Battle.ItemNameLength, L2Battle.ItemNameLength, n.Items[i]);
        int spells = Math.Min(n.Spells.Count, L2Battle.SpellCount(target));
        for (int i = 0; i < spells; i++) { var s = L2Battle.ReadSpell(target, i); s.Name = n.Spells[i]; L2Battle.WriteSpell(target, s); }
        for (int i = 0; i < Math.Min(n.Monsters.Count, L2Battle.MonsterCount); i++)
        {
            var m = L2Battle.ReadMonster(target, i);
            if (m.Name == n.Monsters[i]) continue;
            m.Name = n.Monsters[i];
            L2Battle.WriteMonster(target, m);
        }
        int ips = Math.Min(n.IpAttacks.Count, L2Battle.IpCount(target));
        for (int i = 0; i < ips; i++) if (n.IpAttacks[i].Length > 0) L2Magic.SetIpName(target, i, n.IpAttacks[i]);
        report.Names = items + spells + n.Monsters.Count + ips;
        if (RepackAttackNames(target, n.AttackNames)) report.Names += n.AttackNames.Count;
        else report.Notes.Add("Some monster attack names didn't fit where this ROM has them, so those stay as they were.");
        if (RepackCapsuleAttacks(target, n.CapsuleAttacks)) report.Names += n.CapsuleAttacks.Count;
        else report.Notes.Add("Some capsule monster attack names didn't fit, so those stay as they were.");
        if (WriteMapNames(target, n.MapNames)) report.Names += n.MapNames.Count(x => x.Length > 0);
        else report.Notes.Add("Some map names didn't fit where this ROM has them, so those stay as they were.");
        // descriptions: the IP attacks' entries follow the target's layout; they are packed with the Dutch word list
        // (with the US one they don't fit), so every entry is written again, the ones the Dutch ROM lacks as they read now
        var desc = new Dictionary<int, string>();
        foreach (var (e, text) in n.Descriptions)
        {
            if (text.Length == 0) continue;
            int t = e >= L2Descriptions.IpBase + 1 && e < L2Descriptions.Count ? L2Descriptions.IpEntry(target, e - L2Descriptions.IpBase - 1) : e;
            desc[t] = text;
        }
        int dutchCount = desc.Count;
        for (int e = 0; e < L2Descriptions.EntryCount(target); e++)
            if (!desc.ContainsKey(e)) desc[e] = L2Descriptions.Read(target, e);
        int oldWords = L2Descriptions.WordsAt(target);
        L2Descriptions.SetWords(target, DescriptionWords(new RomBuffer(dutch, "")));
        if (L2Descriptions.WriteMany(target, desc) is { } why)
        {
            L2Descriptions.PointWords(target, oldWords);   // nothing was packed: the old texts need the old words
            report.Notes.Add("Descriptions: " + why);
        }
        else report.Descriptions = dutchCount;
    }

    /// <summary>Monster attack names (a byte for the default animation, the name, 00; at $85:8000 + (u16 &amp; 7FFF)): packed
    /// again in their room when they sit there one after another, else each written in its own place when it fits.</summary>
    private static bool RepackAttackNames(RomBuffer rom, List<string> names)
    {
        int table = L2AiScripts.AttackNameTable, n = L2AiScripts.AttackNameCount;
        var starts = Enumerable.Range(0, n).Select(i => 0x28000 + (rom.ReadUInt16Le(table + i * 2) & 0x7FFF)).ToList();
        if (starts.Any(a => a < 0x28000 || a >= 0x30000)) return false;
        var old = L2AiScripts.MonsterAttackNames(rom);
        var recs = Enumerable.Range(0, n).Select(i =>
        {
            var b = new List<byte> { rom.ReadByte(starts[i]) };   // the attack's default animation (E0: none)
            b.AddRange((i < names.Count ? names[i] : old[i + 1]).Select(c => L2Charset.ToByte(rom, c) ?? (byte)' '));
            b.Add(0);
            return b.ToArray();
        }).ToList();
        return Rewrite(rom, starts, a => 1 + rom.ReadUntil(a + 1, 0, 64).Length + 1, recs,
            (i, at) => rom.WriteUInt16Le(table + i * 2, (ushort)(0x8000 | at - 0x28000)));
    }

    /// <summary>Capsule monster attack names (u16 from the table to "name 00"), as <see cref="RepackAttackNames"/>.</summary>
    private static bool RepackCapsuleAttacks(RomBuffer rom, List<string> names)
    {
        int table = L2Capsules.AttackNames, n = L2Capsules.AttackNameCount;
        var starts = Enumerable.Range(0, n).Select(i => table + rom.ReadUInt16Le(table + i * 2)).ToList();
        if (starts.Min() < table + n * 2 || starts.Max() >= table / 0x8000 * 0x8000 + 0x8000) return false;
        var old = L2Capsules.AttackNameList(rom);
        var recs = Enumerable.Range(0, n).Select(i =>
            (i < names.Count ? names[i] : old[i]).Select(c => L2Charset.ToByte(rom, c) ?? (byte)' ').Append((byte)0).ToArray()).ToList();
        return Rewrite(rom, starts, a => rom.ReadUntil(a, 0, 64).Length + 1, recs, (i, at) => rom.WriteUInt16Le(table + i * 2, (ushort)(at - table)));
    }

    /// <summary>Map names (zero-terminated at the map header table + u16, packed with the dictionary), as
    /// <see cref="RepackAttackNames"/>; identical names share their bytes.</summary>
    private static bool WriteMapNames(RomBuffer rom, List<string> names)
    {
        int h = L2Events.HeaderTable;
        var starts = Enumerable.Range(0, L2Maps.Count).Select(m => h + rom.ReadUInt16Le(h + 0x10 + m * 8 + 6)).ToList();
        if (starts.Min() <= h + 0x10 + L2Maps.Count * 8 || starts.Max() >= h + 0x8000) return false;
        var recs = Enumerable.Range(0, L2Maps.Count).Select(m =>
        {
            string name = m < names.Count && names[m].Length > 0 ? names[m] : L2Maps.Name(rom, m);
            var enc = L2Text.Encode(rom, name).ToList();
            if (enc.Count > 0 && enc[^1] == 0) enc.RemoveAt(enc.Count - 1);
            return enc.Where(b => b != 0x0A).Append((byte)0).ToArray();
        }).ToList();
        return Rewrite(rom, starts, a => MapNameLength(rom, a), recs, (m, at) => rom.WriteUInt16Le(h + 0x10 + m * 8 + 6, (ushort)(at - h)));
    }

    /// <summary>A map name's bytes, its 00 included (05 / 06 take a byte, 0A two).</summary>
    private static int MapNameLength(RomBuffer rom, int at)
    {
        int p = at;
        for (int guard = 0; guard < 64; guard++)
        {
            byte b = rom.ReadByte(p++);
            if (b == 0) break;
            if (b is 5 or 6) p++;
            else if (b == 0x0A) p += 2;
        }
        return p - at;
    }

    /// <summary>Writes new records for a list whose entries point at <paramref name="starts"/>. When the old records sit one
    /// after another (nothing but 00 between them) they are all packed again in that room (identical ones shared).
    /// Otherwise (a hack moved some) each new record goes where its old one is, when it isn't longer; the others stay.
    /// Returns false when not every record could be written.</summary>
    private static bool Rewrite(RomBuffer rom, List<int> starts, Func<int, int> length, List<byte[]> recs, Action<int, int> point)
    {
        var spans = starts.Distinct().OrderBy(a => a).Select(a => (Start: a, End: a + length(a))).ToList();
        bool together = true;
        for (int k = 1; k < spans.Count && together; k++)
        {
            if (spans[k].Start < spans[k - 1].End) { together = false; break; }   // records inside others
            for (int p = spans[k - 1].End; p < spans[k].Start; p++) if (rom.ReadByte(p) != 0) { together = false; break; }
        }
        if (together)
        {
            int from = spans[0].Start, to = spans[^1].End;
            var blob = new List<byte>();
            var placed = new Dictionary<string, int>();
            var at = new int[recs.Count];
            for (int i = 0; i < recs.Count; i++)
            {
                string key = Convert.ToHexString(recs[i]);
                if (!placed.TryGetValue(key, out int a)) { a = from + blob.Count; placed[key] = a; blob.AddRange(recs[i]); }
                at[i] = a;
            }
            if (from + blob.Count <= to)
            {
                rom.WriteBytes(from, blob.ToArray());
                for (int p = from + blob.Count; p < to; p++) rom.WriteByte(p, 0);
                for (int i = 0; i < recs.Count; i++) point(i, at[i]);
                return true;
            }
        }
        // in place, each record no longer than the old one (a shared record only when every sharer gets the same)
        bool all = true;
        foreach (var g in Enumerable.Range(0, recs.Count).GroupBy(i => starts[i]))
        {
            var ids = g.ToList();
            var rec = recs[ids[0]];
            if (ids.Any(i => !recs[i].SequenceEqual(rec)) || rec.Length > length(g.Key)) { all = false; continue; }
            rom.WriteBytes(g.Key, rec);
        }
        return all;
    }

    // ── helpers ──

    private static byte[] Hex(string s) => s.Split(' ').Select(x => Convert.ToByte(x, 16)).ToArray();

    private static int Find(byte[] d, byte[] pat, int from, int to)
    {
        to = Math.Min(to, d.Length - pat.Length);
        for (int i = from; i <= to; i++)
        {
            int k = 0;
            while (k < pat.Length && d[i + k] == pat[k]) k++;
            if (k == pat.Length) return i;
        }
        return -1;
    }
}
