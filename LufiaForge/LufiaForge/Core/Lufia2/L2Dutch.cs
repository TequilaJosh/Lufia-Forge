using System.IO;
namespace LufiaForge.Core.Lufia2;

// the Dutch lufia 2: a translation built on the European release (header title "LUFIA FOR DUTCH", country 07, 3 MB).
// its code and most of its data sit somewhere else than in the US game, so we don't edit it as it is: we put the
// Dutch text into a Lufia II (USA) rom, or a hack built on one (Frue Lufia and the others), which every page then
// edits like normal. found 2026-10-30 on the Dutch rom (SHA1 2CBB1A60...):
// - the map header table (event blocks, start-up scripts, map names, the persistent characters) is at $A7:8000 (file
//   0x138000) instead of $87:8000, and its u24 values count from bank $A7. the events are the US game's commands in
//   the same order (1,557 of 1,564 scripts have the same number of lines, the other 7 split or join lines)
// - the dictionary is at the US address (file 0x76A00) and holds Dutch words
// - the dialogue font is at $A1:B222 and starts at character 10, not 20: 10-1F are A-grave A-acute E-grave E-acute
//   E-umlaut I-umlaut O-grave O-acute O-umlaut U-grave U-acute U-umlaut a-grave a-acute e-grave e-acute, and a few
//   signs are letters ("#" i-umlaut, "~" e-circumflex, "^" N-tilde, "\" n-tilde, "$" o-grave, "&" o-acute, "*" u-grave,
//   "<" a-circumflex, ">" i-grave, "`" upside-down ?, "@" a box). the glyph code that reads it: the US game's
//   "CMP #$10 / BNE / LDA #$20 / SEC / SBC #$20" (10 shows as a space, 11-1F as nothing) became "SEC / SBC #$10", and
//   the LDA $9A:F970,X after it reads the new font
public static class L2Dutch
{
    public const int HeaderTable = 0x138000, Country = 0x07;

    // whether rom is the Dutch lufia 2 (its events where the Dutch rom has them)
    public static bool Is(byte[] rom)
    {
        if (rom.Length < 0x300000 || rom[0x7FD9] != Country || !GameDetect.Title(rom).StartsWith("LUFIA", StringComparison.OrdinalIgnoreCase)) return false;
        try
        {
            var view = View(rom);
            return L2Events.Block(view, 3) >= 0 && L2Events.Block(view, 4) >= 0;
        }
        catch { return false; }
    }

    // reads the Dutch rom from a .sfc / .smc file or a .zip holding one (a copier header gets dropped), or throws
    public static byte[] ReadFile(string path)
    {
        byte[] data;
        if (path.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
        {
            using var zip = System.IO.Compression.ZipFile.OpenRead(path);
            var entry = zip.Entries.FirstOrDefault(e => e.Name.EndsWith(".sfc", StringComparison.OrdinalIgnoreCase) || e.Name.EndsWith(".smc", StringComparison.OrdinalIgnoreCase))
                        ?? throw new InvalidOperationException("That zip has no .sfc or .smc file in it.");
            if (entry.Length > 8 << 20) throw new InvalidOperationException("The ROM in that zip is too big to be Lufia II.");
            using var stream = entry.Open();
            using var ms = new MemoryStream();
            stream.CopyTo(ms);
            data = ms.ToArray();
        }
        else data = File.ReadAllBytes(path);
        if (data.Length % 1024 == 512) data = data[512..];
        if (!Is(data)) throw new InvalidOperationException($"That isn't the Dutch Lufia II (its title reads \"{GameDetect.Title(data)}\").");
        return data;
    }

    // a copy of the Dutch rom laid out the way we read lufia 2, for reading only: its map header table copied to
    // $87:8000 with every map's u24 values moved on by 0x20 banks ($A7 + n read as $87 + n + 20), so L2Events,
    // L2EventBlocks and L2Maps.Name read the Dutch events and names unchanged
    public static RomBuffer View(byte[] dutch)
    {
        var view = (byte[])dutch.Clone();
        Array.Copy(dutch, HeaderTable, view, L2Events.HeaderTable, 0x8000);
        for (int m = 0; m < L2NewMaps.GameCount; m++)
            foreach (int field in new[] { 0, 3 })
            {
                int valueAt = L2Events.HeaderTable + 0x10 + m * 8 + field;
                int value = view[valueAt] | view[valueAt + 1] << 8 | view[valueAt + 2] << 16;
                if (value == 0xFFFFFF) continue;
                value += 0x200000;
                view[valueAt] = (byte)value; view[valueAt + 1] = (byte)(value >> 8); view[valueAt + 2] = (byte)(value >> 16);
            }
        return new RomBuffer(view, "Dutch Lufia II (read only)");
    }

    // what an import did
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

    // puts the Dutch translation into target (lufia 2, US layout): both fonts with the Dutch letters, the names (items,
    // spells, monsters, IP attacks, monster and capsule attacks, maps), the descriptions and every event's text. the
    // target's own commands stay, only events whose lines the translation splits or joins differently get copied
    // whole from the Dutch rom
    public static Report Import(RomBuffer target, byte[] dutch, Action<string>? progress = null)
    {
        if (target.Game != GameKind.Lufia2) throw new InvalidOperationException("The text goes into a Lufia II (USA) ROM, or a hack built on one.");
        if (!Is(dutch)) throw new InvalidOperationException("That isn't the Dutch Lufia II.");
        var dutchView = View(dutch);
        var report = new Report();
        progress?.Invoke("Font");
        ImportFont(target, dutch);
        progress?.Invoke("Names");
        ImportNames(target, dutch, ReadNames(dutch), report);
        var blocks = new Dictionary<int, IReadOnlyDictionary<L2EventBlocks.Entry, L2EventScript>>();
        for (int m = 0; m < L2NewMaps.GameCount; m++)
        {
            progress?.Invoke($"Map {m:X2}");
            ImportMap(target, dutchView, m, report, blocks);
        }
        // every block laid out again at once, in the room the game's blocks take (only the overflow goes elsewhere)
        progress?.Invoke("Events");
        var stuck = new List<int>();
        report.MovedBlocks = L2EventBlocks.RepackAll(target, blocks, stuck);
        // a block that can't be laid out again: each line gets written over its old bytes if it fits
        foreach (int block in stuck)
            if (blocks.TryGetValue(block, out var changed))
                foreach (var step in changed.Values.Distinct().Where(x => x.Start >= 0).SelectMany(x => x.Steps).Where(x => x.IsText && x.At >= 0).DistinctBy(x => x.At))
                    if (!InPlace(target, step)) report.EnglishLines++;
        if (stuck.Count > 0) report.Notes.Add($"{stuck.Count} map{(stuck.Count == 1 ? "'s events" : "s' events")} can't be laid out again (a script whose steps overlap), so {(stuck.Count == 1 ? "its" : "their")} lines were written in place: {report.EnglishLines} too long for their place stay as they were.");
        target.FixChecksum();
        return report;
    }

    // the font

    // the US game's glyph code: CMP #$0010 / BNE +3 / LDA #$0020 / SEC / SBC #$0020 / BCC ... / ASL x4 / CLC / TAX /
    // LDY $09B1 / STY $09B5 / SEP #$20 / LDA #$7E / PHA / PLB / LDA #$10 / STA $58 / LDA long,X (the font)
    private static readonly byte[] UsGlyphCode = Hex("C9 10 00 D0 03 A9 20 00 38 E9 20 00 90 30 0A 0A 0A 0A 18 AA AC B1 09 8C B5 09 E2 20 A9 7E 48 AB A9 10 85 58 BF");
    // the same once the font starts at character 10 (the Dutch rom's code, written over the US code with 8 NOPs first)
    private static readonly byte[] DutchGlyphCode = Hex("38 E9 10 00 90 30 0A 0A 0A 0A 18 AA AC B1 09 8C B5 09 E2 20 A9 7E 48 AB A9 10 85 58 BF");
    public const int FontFirst = 0x10, FontGlyphs = 0x70, GlyphSize = 16;

    // file offset of the glyph code's LDA long,X operand in rom and whether the font starts at character 10 there.
    // -1 if the code isn't found
    public static (int Operand, bool From10) FontSite(RomBuffer rom)
    {
        var all = rom.ToArray();
        int usCode = Find(all, UsGlyphCode, 0, 0x10000);
        if (usCode >= 0) return (usCode + UsGlyphCode.Length, false);
        int dutchCode = Find(all, DutchGlyphCode, 0, 0x10000);
        return dutchCode >= 0 ? (dutchCode + DutchGlyphCode.Length, true) : (-1, false);
    }

    private static void ImportFont(RomBuffer target, byte[] dutch)
    {
        var source = new RomBuffer(dutch, "");
        var (dutchOperand, dutchFrom10) = FontSite(source);
        if (dutchOperand < 0 || !dutchFrom10) throw new InvalidOperationException("The Dutch ROM's font code isn't where it was expected.");
        int dutchFont = L2Rom.SnesToFile((int)source.ReadUInt24Le(dutchOperand));
        byte[] font = source.ReadBytes(dutchFont, FontGlyphs * GlyphSize);
        var (operand, from10) = FontSite(target);
        if (operand < 0) throw new InvalidOperationException("This ROM's dialogue font code isn't the US game's, so the Dutch letters can't be added.");
        int fontAt;
        if (from10)
        {
            // imported before: the font is ours already, write it again in its place
            fontAt = L2Rom.SnesToFile((int)target.ReadUInt24Le(operand));
        }
        else
        {
            fontAt = L2Rom.Allocate(target, font.Length);
            int code = operand - UsGlyphCode.Length;
            target.WriteBytes(code, Hex("EA EA EA EA EA EA EA EA 38 E9 10 00"));
        }
        target.WriteBytes(fontAt, font);
        int snes = L2Rom.FileToSnes(fontAt);
        target.WriteBytes(operand, new[] { (byte)snes, (byte)(snes >> 8), (byte)(snes >> 16) });
        // the routine that clears the rest of a window ($82:90FB: LDX #$0010 / STX $09AF / JSL the glyph code) draws
        // character 10, a space in the US font and A-grave in the Dutch one. the Dutch rom draws 20 there
        var all = target.ToArray();
        int fill = Find(all, BlankFill, 0, 0x40000);
        if (fill >= 0) target.WriteByte(fill + 6, 0x20);
    }

    // LDA $02 / BNE / RTS / LDX #$0010 / STX $09AF / JSL: the window blank (US $82:90F6)
    private static readonly byte[] BlankFill = Hex("A5 02 D0 01 60 A2 10 00 8E AF 09 22");

    // the dialogue

    private static void ImportMap(RomBuffer target, RomBuffer dutchView, int map, Report report,
        Dictionary<int, IReadOnlyDictionary<L2EventBlocks.Entry, L2EventScript>> blocks)
    {
        bool any = false;
        // the start-up script
        int targetInit = L2EventBlocks.InitScript(target, map), dutchInit = L2EventBlocks.InitScript(dutchView, map);
        if (targetInit > 0 && targetInit < target.Length && dutchInit > 0 && dutchInit < dutchView.Length)
        {
            var targetScript = L2EventScript.Read(target, targetInit, L2EventBlocks.InitBase(targetInit));
            var dutchScript = L2EventScript.Read(dutchView, dutchInit, L2EventBlocks.InitBase(dutchInit));
            if (targetScript.Problem == null && dutchScript.Problem == null && targetScript.Steps.Any(s => s.IsText))
            {
                var translated = Translate(targetScript, dutchScript, report);
                if (translated != null) { L2EventBlocks.SaveInit(target, map, translated); any = true; }
            }
        }
        // the block's events, paired by list, id and how many came before with the same list and id
        int targetBlock = L2Events.Block(target, map), dutchBlock = L2Events.Block(dutchView, map);
        if (targetBlock >= 0 && dutchBlock >= 0 && !blocks.ContainsKey(targetBlock))   // (maps sharing a block: the first one's)
        {
            var dutchByKey = Keyed(L2EventBlocks.ReadLists(dutchView, dutchBlock)).ToDictionary(x => x.Key, x => x.Entry);
            var dutchScripts = new Dictionary<int, L2EventScript>();
            var done = new Dictionary<int, L2EventScript?>();
            var changed = new Dictionary<L2EventBlocks.Entry, L2EventScript>();
            foreach (var (key, entry) in Keyed(L2EventBlocks.ReadLists(target, targetBlock)))
            {
                if (!entry.IsScript || !dutchByKey.TryGetValue(key, out var dutchEntry)) continue;
                if (!done.TryGetValue(entry.Offset, out var translated))
                {
                    var targetScript = L2EventScript.Read(target, targetBlock + entry.Offset, targetBlock);
                    if (!dutchScripts.TryGetValue(dutchEntry.Offset, out var dutchScript)) dutchScripts[dutchEntry.Offset] = dutchScript = L2EventScript.Read(dutchView, dutchBlock + dutchEntry.Offset, dutchBlock);
                    translated = targetScript.Problem == null && dutchScript.Problem == null && targetScript.Steps.Any(s => s.IsText) ? Translate(targetScript, dutchScript, report) : null;
                    done[entry.Offset] = translated;
                }
                if (translated != null) changed[entry] = translated;
            }
            blocks[targetBlock] = changed;
            if (changed.Count > 0) any = true;
        }
        if (any) report.Maps++;
    }

    // writes a step's (new) text over its old bytes, filled up with pauses of no time (04 80) and a space. false if
    // it's longer than its place
    private static bool InPlace(RomBuffer rom, L2Step step)
    {
        if (!step.TextChanged) return true;
        var text = L2EventText.Encode(rom, step.Text.Replace("\r", "")).ToList();
        if (text.Count > 0 && text[^1] == 0) text.RemoveAt(text.Count - 1);
        int room = step.OriginalLength - step.Bytes.Length - 1;   // the text's bytes without its end code
        if (text.Count > room) return false;
        int fill = room - text.Count;
        if (fill % 2 == 1) { text.Add((byte)' '); fill--; }
        for (; fill > 0; fill -= 2) { text.Add(4); text.Add(0x80); }
        text.Add(step.End);
        // bytes another line repeats (0A, a back-reference) stay: that line (kept in English if it didn't fit) would
        // show the new bytes, garbled. then the step keeps its old text, like one too long for its place
        if (L2Events.CopiedFrom(rom, new[] { (step.At + step.Bytes.Length, text.ToArray()) }) >= 0) return false;
        rom.WriteBytes(step.At + step.Bytes.Length, text.ToArray());
        return true;
    }

    private static IEnumerable<((int, int, int) Key, L2EventBlocks.Entry Entry)> Keyed(List<L2EventBlocks.Entry> list)
    {
        var seen = new Dictionary<(int, int), int>();
        foreach (var entry in list)
        {
            int before = seen.GetValueOrDefault((entry.Head, entry.Id));
            seen[(entry.Head, entry.Id)] = before + 1;
            yield return ((entry.Head, entry.Id, before), entry);
        }
    }

    // the target script with the Dutch lines, or the Dutch script itself (ready to be written into the target) if the
    // two don't have the same number of lines. null if nothing changes
    private static L2EventScript? Translate(L2EventScript target, L2EventScript dutch, Report report)
    {
        var targetLines = target.Steps.Where(s => s.IsText).ToList();
        var dutchLines = dutch.Steps.Where(s => s.IsText).ToList();
        report.Scripts++;
        if (targetLines.Count == dutchLines.Count)
        {
            for (int i = 0; i < targetLines.Count; i++) { targetLines[i].Text = dutchLines[i].Text; targetLines[i].End = dutchLines[i].End; }
            report.Lines += targetLines.Count;
            return target;
        }
        // lines split or joined differently: the whole Dutch script, every line encoded again for the target
        var copies = new Dictionary<L2Step, L2Step>();
        foreach (var step in dutch.Steps)
            copies[step] = new L2Step
            {
                At = -1, Bytes = (byte[])step.Bytes.Clone(), OriginalBytes = step.OriginalBytes, IsText = step.IsText, TextRaw = step.TextRaw,
                Text = step.Text, OriginalText = step.IsText ? "\u0000" : step.OriginalText, End = step.End, OriginalEnd = step.OriginalEnd,
                HasBackReference = true, OriginalLength = step.OriginalLength,
            };
        foreach (var step in dutch.Steps)
            foreach (var jump in step.Jumps)
            {
                if (jump.Step == null) return null;   // a jump out of the script: left as it is
                copies[step].Jumps.Add(new L2Jump { Step = copies[jump.Step] });
            }
        var copy = L2EventScript.New(target.JumpBase);
        copy.Steps.Clear();
        copy.Steps.AddRange(dutch.Steps.Select(s => copies[s]));
        report.Lines += dutchLines.Count;
        report.WholeScripts++;
        return copy;
    }

    // names, descriptions, the menu font

    // the Dutch rom's tables (found 2026-10-30 from the code that reads them): item names 12 letters each, the spell
    // table ($B3:FA5B, records at bank $B3 + u16 + FA5B), the monster table ($B4:85C0, the US layout), the IP attack
    // table ($84:8F20, records in bank $84), monster attack names (u16 | 8000 from $85:8000: an animation byte, the
    // name, 00), capsule attack names (u16 from the table), descriptions ($B5:8000, their word list at $9E:C7E8, where
    // the US game has its item names) and the menu font (the US place, $9F:8500, 10-1F the accented letters)
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
        // descriptions by entry (the game's layout: items 0-1D2, spells 200+, IP attacks 231+)
        public Dictionary<int, string> Descriptions { get; } = new();
    }

    // every name and description of the Dutch rom, with the letters themselves (e-acute, not a code)
    public static Names ReadNames(byte[] dutch)
    {
        var dutchRom = new RomBuffer(dutch, "");
        var view = View(dutch);
        var names = new Names();
        for (int i = 0; i < L2Battle.ItemCount; i++) names.Items.Add(L2Rom.Name(dutchRom, NlItemNames + i * L2Battle.ItemNameLength, L2Battle.ItemNameLength));
        for (int i = 0; i < L2Battle.SpellCountOriginal; i++)
            names.Spells.Add(L2Rom.Name(dutchRom, L2Rom.SnesToFile(NlSpellBank << 16 | (dutchRom.ReadUInt16Le(NlSpellTable + i * 2) + NlSpellAdc) & 0xFFFF), L2Battle.SpellNameLength));
        for (int i = 0; i < L2Battle.MonsterCount; i++)
            names.Monsters.Add(L2Rom.Name(dutchRom, L2Rom.InBank(NlMonsterTable, dutchRom.ReadUInt16Le(NlMonsterTable + i * 2)), L2Battle.MonsterNameLength));
        for (int i = 0; i < L2Battle.IpCountOriginal; i++)
            names.IpAttacks.Add(Zero(dutchRom, L2Rom.SnesToFile(0x840000 | dutchRom.ReadUInt16Le(NlIpTable + i * 2)) + 6));
        for (int i = 0; i < L2AiScripts.AttackNameCount; i++)
            names.AttackNames.Add(Zero(dutchRom, 0x28000 + (dutchRom.ReadUInt16Le(NlAttackNames + i * 2) & 0x7FFF) + 1));
        for (int i = 0; i < L2Capsules.AttackNameCount; i++)
            names.CapsuleAttacks.Add(Zero(dutchRom, NlCapsuleAttacks + dutchRom.ReadUInt16Le(NlCapsuleAttacks + i * 2)));
        for (int m = 0; m < L2NewMaps.GameCount; m++) names.MapNames.Add(L2Maps.Name(view, m));
        var words = DescriptionWords(dutchRom);
        var dict = L2Text.Words(dutchRom);
        for (int e = 0; e < L2Descriptions.Count; e++) names.Descriptions[e] = Description(dutchRom, e, words, dict);
        return names;
    }

    private static string Zero(RomBuffer rom, int at) => new(rom.ReadUntil(at, 0, 40).Select(b => L2Charset.ToChar(rom, b) ?? '?').ToArray());

    private static string[] DescriptionWords(RomBuffer dutchRom)
    {
        // the first offset is the table's size, same as in the US list
        int count = dutchRom.ReadUInt16Le(NlDescWords) / 2;
        return Enumerable.Range(0, count).Select(k => Zero(dutchRom, NlDescWords + dutchRom.ReadUInt16Le(NlDescWords + k * 2))).ToArray();
    }

    // a Dutch description: like the US game's, except the main dictionary's codes are 0D-1F and the second byte of a
    // two-byte code counts from 1 ($85:C4F1 in the Dutch rom: SBC #$0D or SBC #$05, then DEC on the next byte)
    private static string Description(RomBuffer dutchRom, int entry, string[] words, string[] dict)
    {
        int textAt = NlDescTable + dutchRom.ReadUInt16Le(NlDescTable + entry * 2);
        var sb = new System.Text.StringBuilder();
        string Get(string[] list, int k) => k >= 0 && k < list.Length ? list[k] : "?";
        for (int position = textAt, guard = 0; guard < 400; guard++)
        {
            byte c = dutchRom.ReadByte(position++);
            if (c == 0) break;
            if (c >= 0x80) sb.Append(Get(words, c & 0x7F));
            else if (c >= 0x20) sb.Append(L2Charset.ToChar(dutchRom, c) ?? '?');
            else if (c == 0x0A) sb.Append('\n');
            else if (c > 0x0A) sb.Append(Get(dict, (c - 0x0D) * 0xFF + dutchRom.ReadByte(position++) - 1));
            else if (c >= 5) sb.Append(Get(words, (c - 5) * 0xFF + dutchRom.ReadByte(position++) - 1 + 0x80));
        }
        return sb.ToString();
    }

    // writes the Dutch names, descriptions and menu font into target
    private static void ImportNames(RomBuffer target, byte[] dutch, Names names, Report report)
    {
        // the menu font: the accented letters at 10-1F and the signs that are letters
        target.WriteBytes(SmallFont + 0x100, dutch.AsSpan(SmallFont + 0x100, 0x700).ToArray());
        int items = Math.Min(names.Items.Count, L2Battle.ItemCountIn(target));
        for (int i = 0; i < items; i++) L2Rom.WriteName(target, L2Battle.ItemNames + i * L2Battle.ItemNameLength, L2Battle.ItemNameLength, names.Items[i]);
        int spells = Math.Min(names.Spells.Count, L2Battle.SpellCount(target));
        for (int i = 0; i < spells; i++) { var spell = L2Battle.ReadSpell(target, i); spell.Name = names.Spells[i]; L2Battle.WriteSpell(target, spell); }
        for (int i = 0; i < Math.Min(names.Monsters.Count, L2Battle.MonsterCount); i++)
        {
            var monster = L2Battle.ReadMonster(target, i);
            if (monster.Name == names.Monsters[i]) continue;
            monster.Name = names.Monsters[i];
            L2Battle.WriteMonster(target, monster);
        }
        int ips = Math.Min(names.IpAttacks.Count, L2Battle.IpCount(target));
        for (int i = 0; i < ips; i++) if (names.IpAttacks[i].Length > 0) L2Magic.SetIpName(target, i, names.IpAttacks[i]);
        report.Names = items + spells + names.Monsters.Count + ips;
        if (RepackAttackNames(target, names.AttackNames)) report.Names += names.AttackNames.Count;
        else report.Notes.Add("Some monster attack names didn't fit where this ROM has them, so those stay as they were.");
        if (RepackCapsuleAttacks(target, names.CapsuleAttacks)) report.Names += names.CapsuleAttacks.Count;
        else report.Notes.Add("Some capsule monster attack names didn't fit, so those stay as they were.");
        if (WriteMapNames(target, names.MapNames)) report.Names += names.MapNames.Count(x => x.Length > 0);
        else report.Notes.Add("Some map names didn't fit where this ROM has them, so those stay as they were.");
        // descriptions: the IP attacks' entries follow the target's layout. they get packed with the Dutch word list
        // (with the US one they don't fit), so every entry is written again, the ones the Dutch rom lacks as they read now
        var descriptions = new Dictionary<int, string>();
        foreach (var (e, text) in names.Descriptions)
        {
            if (text.Length == 0) continue;
            int targetEntry = e >= L2Descriptions.IpBase + 1 && e < L2Descriptions.Count ? L2Descriptions.IpEntry(target, e - L2Descriptions.IpBase - 1) : e;
            descriptions[targetEntry] = text;
        }
        int dutchCount = descriptions.Count;
        for (int e = 0; e < L2Descriptions.EntryCount(target); e++)
            if (!descriptions.ContainsKey(e)) descriptions[e] = L2Descriptions.Read(target, e);
        int oldWords = L2Descriptions.WordsAt(target);
        L2Descriptions.SetWords(target, DescriptionWords(new RomBuffer(dutch, "")));
        if (L2Descriptions.WriteMany(target, descriptions) is { } why)
        {
            L2Descriptions.PointWords(target, oldWords);   // nothing got packed, so the old texts need the old words
            report.Notes.Add("Descriptions: " + why);
        }
        else report.Descriptions = dutchCount;
    }

    // monster attack names (a byte for the default animation, the name, 00, at $85:8000 + (u16 & 7FFF)): packed again
    // in their room if they sit there one after another, else each one written in its own place if it fits
    internal static bool RepackAttackNames(RomBuffer rom, List<string> names)
    {
        int table = L2AiScripts.AttackNameTable, count = L2AiScripts.AttackNameCount;
        var starts = Enumerable.Range(0, count).Select(i => 0x28000 + (rom.ReadUInt16Le(table + i * 2) & 0x7FFF)).ToList();
        if (starts.Any(a => a < 0x28000 || a >= 0x30000)) return false;
        var old = L2AiScripts.MonsterAttackNames(rom);
        var records = Enumerable.Range(0, count).Select(i =>
        {
            var record = new List<byte> { rom.ReadByte(starts[i]) };   // the attack's default animation (E0 = none)
            record.AddRange((i < names.Count ? names[i] : old[i + 1]).Select(c => L2Charset.ToByte(rom, c) ?? (byte)' '));
            record.Add(0);
            return record.ToArray();
        }).ToList();
        return Rewrite(rom, starts, a => 1 + rom.ReadUntil(a + 1, 0, 64).Length + 1, records,
            (i, at) => rom.WriteUInt16Le(table + i * 2, (ushort)(0x8000 | at - 0x28000)));
    }

    // capsule monster attack names (u16 from the table to "name 00"), same as RepackAttackNames
    private static bool RepackCapsuleAttacks(RomBuffer rom, List<string> names)
    {
        int table = L2Capsules.AttackNames, count = L2Capsules.AttackNameCount;
        var starts = Enumerable.Range(0, count).Select(i => table + rom.ReadUInt16Le(table + i * 2)).ToList();
        if (starts.Min() < table + count * 2 || starts.Max() >= table / 0x8000 * 0x8000 + 0x8000) return false;
        var old = L2Capsules.AttackNameList(rom);
        var records = Enumerable.Range(0, count).Select(i =>
            (i < names.Count ? names[i] : old[i]).Select(c => L2Charset.ToByte(rom, c) ?? (byte)' ').Append((byte)0).ToArray()).ToList();
        return Rewrite(rom, starts, a => rom.ReadUntil(a, 0, 64).Length + 1, records, (i, at) => rom.WriteUInt16Le(table + i * 2, (ushort)(at - table)));
    }

    // map names (zero-terminated at the map header table + u16, packed with the dictionary), same as
    // RepackAttackNames. identical names share their bytes
    private static bool WriteMapNames(RomBuffer rom, List<string> names)
    {
        if (L2MapNames.IsMoved(rom))
        {
            // the names got moved out of bank $87 (named maps), so each one is written there
            for (int m = 0; m < Math.Min(names.Count, L2Maps.Count(rom)); m++)
                if (names[m].Length > 0 && L2Maps.Name(rom, m) != names[m]) try { L2MapNames.Write(rom, m, names[m]); } catch (InvalidOperationException) { }
            return true;
        }
        int headerTable = L2Events.HeaderTable;
        var starts = Enumerable.Range(0, L2Maps.Count(rom)).Select(m => headerTable + rom.ReadUInt16Le(L2Events.HeaderAt(rom, m) + 6)).ToList();
        if (starts.Min() <= headerTable + 0x10 + L2NewMaps.GameCount * 8 || starts.Max() >= headerTable + 0x8000) return false;
        var records = Enumerable.Range(0, L2Maps.Count(rom)).Select(m =>
        {
            // one byte per letter (L2MapNames.Encode): the box shown when entering a map prints every byte as a letter,
            // so dictionary words (what L2Text.Encode makes) came out as other letters. a name that can't be written
            // that way keeps the bytes it has
            string name = m < names.Count && names[m].Length > 0 ? names[m] : L2Maps.Name(rom, m);
            var (encoded, _) = L2MapNames.Encode(rom, name);
            return encoded ?? rom.ReadBytes(starts[m], MapNameLength(rom, starts[m]));
        }).ToList();
        return Rewrite(rom, starts, a => MapNameLength(rom, a), records, (m, at) => rom.WriteUInt16Le(L2Events.HeaderAt(rom, m) + 6, (ushort)(at - headerTable)));
    }

    // a map name's bytes, its 00 included (05 / 06 take a byte, 0A two)
    private static int MapNameLength(RomBuffer rom, int at)
    {
        int position = at;
        for (int guard = 0; guard < 64; guard++)
        {
            byte b = rom.ReadByte(position++);
            if (b == 0) break;
            if (b is 5 or 6) position++;
            else if (b == 0x0A) position += 2;
        }
        return position - at;
    }

    // writes new records for a list whose entries point at starts. if the old records sit one after another (nothing
    // but 00 between them) they all get packed again in that room (identical ones shared). otherwise (a hack moved
    // some) each new record goes where its old one is, if it isn't longer, and the others stay. returns false if not
    // every record could be written
    private static bool Rewrite(RomBuffer rom, List<int> starts, Func<int, int> length, List<byte[]> recs, Action<int, int> point)
    {
        var spans = starts.Distinct().OrderBy(a => a).Select(a => (Start: a, End: a + length(a))).ToList();
        bool together = true;
        for (int k = 1; k < spans.Count && together; k++)
        {
            if (spans[k].Start < spans[k - 1].End) { together = false; break; }   // records inside others
            for (int position = spans[k - 1].End; position < spans[k].Start; position++) if (rom.ReadByte(position) != 0) { together = false; break; }
        }
        if (together)
        {
            int from = spans[0].Start, to = spans[^1].End;
            var blob = new List<byte>();
            var placed = new Dictionary<string, int>();
            var newStarts = new int[recs.Count];
            for (int i = 0; i < recs.Count; i++)
            {
                string key = Convert.ToHexString(recs[i]);
                if (!placed.TryGetValue(key, out int recordAt)) { recordAt = from + blob.Count; placed[key] = recordAt; blob.AddRange(recs[i]); }
                newStarts[i] = recordAt;
            }
            if (from + blob.Count <= to)
            {
                rom.WriteBytes(from, blob.ToArray());
                for (int position = from + blob.Count; position < to; position++) rom.WriteByte(position, 0);
                for (int i = 0; i < recs.Count; i++) point(i, newStarts[i]);
                return true;
            }
        }
        // in place, each record no longer than the old one (a shared record only if every sharer gets the same)
        bool all = true;
        foreach (var group in Enumerable.Range(0, recs.Count).GroupBy(i => starts[i]))
        {
            var ids = group.ToList();
            var record = recs[ids[0]];
            if (ids.Any(i => !recs[i].SequenceEqual(record)) || record.Length > length(group.Key)) { all = false; continue; }
            rom.WriteBytes(group.Key, record);
        }
        return all;
    }

    // helpers

    private static byte[] Hex(string hex) => hex.Split(' ').Select(x => Convert.ToByte(x, 16)).ToArray();

    private static int Find(byte[] data, byte[] pattern, int from, int to)
    {
        to = Math.Min(to, data.Length - pattern.Length);
        for (int i = from; i <= to; i++)
        {
            int k = 0;
            while (k < pattern.Length && data[i + k] == pattern[k]) k++;
            if (k == pattern.Length) return i;
        }
        return -1;
    }
}
