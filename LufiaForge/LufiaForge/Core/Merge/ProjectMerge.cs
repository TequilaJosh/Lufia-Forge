using LufiaForge.Core.Lufia2;
using LufiaForge.Core.Maps;

namespace LufiaForge.Core.Merge;

// a line of a merge item's diff: kept by both, only this project's, only the other project's, or a note
public enum DiffKind { Same, Ours, Theirs, Note, Head }

public sealed record DiffLine(DiffKind Kind, string Text);

// one thing the other project changed that this project doesn't have yet: an event, a list of a map's info, a packed
// file, or a run of other bytes. Take = the other project's version goes in. things only the other project changed
// start ticked, things both changed differently (Conflict) start unticked (this project's stays)
public sealed class MergeItem
{
    public string Group { get; init; } = "";
    public string What { get; init; } = "";
    public bool Conflict { get; init; }
    public bool Take { get; set; }
    public string Why { get; init; } = "";
    // the diff, made when someone looks at the item
    public Func<List<DiffLine>> Details { get; init; } = () => new();
    // sort order in the list (groups in this order, then items)
    public int Order { get; init; }
}

// what gets merged: this project (its rom now and the rom it started from) and the other one. with Ancestor (the
// version both projects were made from) changes are counted from there instead of from the starting roms, so what both
// projects already had before they split doesn't count as a change on both sides
public sealed record MergeInput(
    string OurName, byte[] Ours, byte[] OursBase, IReadOnlyList<string> OurLog, IReadOnlyDictionary<int, string> OurMapNames,
    string TheirName, byte[] Theirs, byte[] TheirsBase, IReadOnlyList<string> TheirLog, IReadOnlyDictionary<int, string> TheirMapNames,
    byte[]? Ancestor = null, string? AncestorName = null);

// merging another project into this one. each byte is compared three ways: the rom the project started from (or the
// version both were made from), this project, and the other one. what only the other project changed gets taken, what
// both changed differently is a conflict the user settles.
// we put new data in free space from a "next free byte" mark, so two projects that split from one both wrote their new
// data at the same addresses and copying bytes would break one of them. so the things we know how to move are merged
// by what's in them, not by bytes (lufia 2: each map's events one at a time and its start-up script, each list of a
// map's info, each packed file = map layouts, music, graphics, see L2MergeUnits). the other bytes are taken where the
// other project put them (its pointers stay right), and then every one of those things is made the way it was chosen:
// if the other project's data landed on one, it gets written again in free room past everything either project used.
// only where both projects put other data (code, tables) in the same place is there no way around it, one side's
// change there has to be made again
public sealed class ProjectMerge
{
    public GameKind Game { get; }
    public string OurName { get; }
    public string TheirName { get; }
    public string? AncestorName { get; }
    public List<MergeItem> Items { get; } = new();
    // events, info lists and packed files both projects changed the same way (nothing to do)
    public int SameCount { get; private set; }
    // other bytes both projects changed the same way
    public int SameBytes { get; private set; }
    public List<string> Warnings { get; } = new();
    // the other project's change log lines this one doesn't have (what it did since they split)
    public List<string> TheirNewLog { get; } = new();
    // map names the other project gave that this one hasn't
    public Dictionary<int, string> TheirNewMapNames { get; } = new();
    public bool TakeLog { get; set; } = true;
    public bool TakeMapNames { get; set; } = true;

    private readonly byte[] _a, _ab, _b, _bb;
    private readonly int _size;
    // per byte: 0 keep this project's, 1 only the other changed it, 2 both changed it, 3 goes with one of the other
    // project's things (taken where this project left the byte alone), 4 a mark or checksum (made afterwards)
    private readonly byte[] _state;
    private readonly List<(int Start, int End, MergeItem Item)> _hunks = new();
    // the free space each side used that its base hadn't (every byte, whatever its value)
    private bool[] _aNew = Array.Empty<bool>(), _bNew = Array.Empty<bool>();
    private readonly L2MergeUnits? _l2;
    private readonly RomBuffer _ra, _rab, _rb, _rbb;

    private const byte Keep = 0, Theirs = 1, Both = 2, Auto = 3, Special = 4;

    private ProjectMerge(MergeInput input)
    {
        OurName = input.OurName; TheirName = input.TheirName; AncestorName = input.AncestorName;
        Game = GameDetect.Detect(input.Ours);
        if (GameDetect.Detect(input.Theirs) != Game) throw new InvalidOperationException("The other project is for another game.");
        var ourBase = input.Ancestor ?? input.OursBase;
        var theirBase = input.Ancestor ?? input.TheirsBase;
        if (input.Ancestor == null && CommunityPatches.Crc32(input.OursBase) != CommunityPatches.Crc32(input.TheirsBase))
            Warnings.Add("The two projects started from different ROMs. Changes are counted from each project's own starting ROM, " +
                         "so where those ROMs differ the merge can't tell which side changed what: check the conflicts closely.");

        // grow every copy to one size, the way the game's rom grows (an 8 MB lufia 2 gets its upper 4 MB)
        _size = new[] { input.Ours.Length, input.Theirs.Length, ourBase.Length, theirBase.Length }.Max();
        _ra = Grown(input.Ours); _rab = Grown(ourBase); _rb = Grown(input.Theirs);
        _rbb = ReferenceEquals(ourBase, theirBase) ? _rab : Grown(theirBase);
        _a = _ra.ToArray(); _ab = _rab.ToArray(); _b = _rb.ToArray(); _bb = _rbb.ToArray();
        if (input.Theirs.Length > input.Ours.Length)
            Warnings.Add($"The other project's ROM is bigger ({input.Theirs.Length / 1024 / 1024.0:0.#} MB against {input.Ours.Length / 1024 / 1024.0:0.#} MB): this project grows to its size.");

        var log = input.OurLog.ToHashSet();
        TheirNewLog.AddRange(input.TheirLog.Where(l => !log.Contains(l)));
        foreach (var (m, name) in input.TheirMapNames)
            if (!input.OurMapNames.ContainsKey(m) && !string.IsNullOrWhiteSpace(name)) TheirNewMapNames[m] = name;

        _state = new byte[_size];
        if (Game == GameKind.Lufia2) _l2 = new L2MergeUnits(_ra, _rab, _rb, _rbb);
        Classify();
        if (_l2 != null)
        {
            Items.AddRange(_l2.Items);
            SameCount += _l2.SameCount;
            Warnings.AddRange(_l2.Warnings);
        }
        Items.Sort((x, y) => x.Order != y.Order ? x.Order.CompareTo(y.Order) : 0);
    }

    // compares the projects. takes a few seconds on big roms, so run it off the window's thread
    public static ProjectMerge Plan(MergeInput input) => new(input);

    private RomBuffer Grown(byte[] data)
    {
        var grown = new RomBuffer((byte[])data.Clone(), "");
        if (grown.Length >= _size) return grown;
        if (Game == GameKind.Lufia2 && _size == L2Rom.ExSize && !L2Rom.IsExLoRom(grown) && L2Rom.CantExpandTo8(grown) == null) L2Rom.ExpandTo8(grown);
        else grown.Expand(_size);
        return grown;
    }

    // bytes

    private void Classify()
    {
        var special = new bool[_size];
        void Mark(int at, int len) { for (int i = Math.Max(0, at); i < Math.Min(_size, at + len); i++) special[i] = true; }
        Mark(0x7FDC, 4);   // the checksum (made again afterwards)
        if (_size >= RomBuffer.ExLoRomSize) Mark(L2Rom.UpperStart, 0x8000);   // an 8 MB rom's copy of the first bank (made along with the checksum)
        // what each side has put in free space since the base: every byte of it counts as used, whatever its value
        var aNew = _aNew = new bool[_size]; var bNew = _bNew = new bool[_size];
        foreach (var (at, len) in Bookkeeping(_ra).Concat(Bookkeeping(_rb)).Concat(Bookkeeping(_rab)).Concat(Bookkeeping(_rbb))) Mark(at, len);
        foreach (var (s, e) in NewRoom(_ra, _rab)) for (int i = s; i < Math.Min(e, _size); i++) aNew[i] = true;
        foreach (var (s, e) in NewRoom(_rb, _rbb)) for (int i = s; i < Math.Min(e, _size); i++) bNew[i] = true;

        var aData = new bool[_size]; var aPtr = new bool[_size]; var bData = new bool[_size]; var bPtr = new bool[_size];
        var bSlots = new List<(int, int)>();
        _l2?.Own(_ra, aData, aPtr, null);
        _l2?.Own(_rb, bData, bPtr, bSlots);

        for (int offset = 0; offset < _size; offset++)
        {
            if (special[offset]) { _state[offset] = Special; continue; }
            bool theyChanged = _b[offset] != _bb[offset] || bNew[offset];
            if (!theyChanged || _a[offset] == _b[offset]) { _state[offset] = Keep; continue; }
            bool weChanged = _a[offset] != _ab[offset] || aNew[offset];
            if (bData[offset] || bPtr[offset]) { _state[offset] = weChanged ? Keep : Auto; continue; }
            if (!weChanged) { _state[offset] = Theirs; continue; }
            _state[offset] = aData[offset] && !aPtr[offset] ? Theirs : Both;
        }
        // a pointer of the other project's gets taken whole or not at all
        foreach (var (s, len) in bSlots)
        {
            bool allKept = true;
            for (int i = s; i < s + len && i < _size; i++) if (_state[i] == Keep && _a[i] != _b[i]) allKept = false;
            if (!allKept) for (int i = s; i < s + len && i < _size; i++) if (_state[i] == Auto) _state[i] = Keep;
        }
        MakeHunks(aData);
    }

    // bytes that keep track of free space (marks, the space table), made again for the merged rom
    private IEnumerable<(int At, int Length)> Bookkeeping(RomBuffer rom)
    {
        if (Game == GameKind.Lufia2)
        {
            foreach (var (mark, _, _) in L2Rom.Spaces(rom)) yield return (mark, 16);
            foreach (int bank in L2EventSpace.Regions(rom)) yield return (bank + L2EventSpace.Mark, 16);
        }
        else if (Game == GameKind.Lufia1 && rom.Length >= MapWriter.ExpandedSize)
            yield return (ExpansionSpace.TableStart, ExpansionSpace.TableSize);
    }

    // the free space a rom used that its base hadn't. lufia 2: from each space mark's next free byte in the base (or
    // where the space starts) to the rom's. lufia 1: the space table's blocks the base doesn't list
    private IEnumerable<(int Start, int End)> NewRoom(RomBuffer rom, RomBuffer baseRom)
    {
        if (Game == GameKind.Lufia2)
        {
            var baseSpaces = L2Rom.Spaces(baseRom);
            foreach (var (mark, start, next) in L2Rom.Spaces(rom))
            {
                int from = baseSpaces.FirstOrDefault(s => s.Mark == mark) is { Mark: > 0 } baseSpace ? baseSpace.Next : start;
                if (next > from) yield return (from, next);
            }
            var baseRegions = L2EventSpace.Regions(baseRom).ToHashSet();
            foreach (int bank in L2EventSpace.Regions(rom))
            {
                int next = (int)rom.ReadUInt24Le(bank + L2EventSpace.Mark + 4);
                int from = baseRegions.Contains(bank) ? (int)baseRom.ReadUInt24Le(bank + L2EventSpace.Mark + 4) : bank;
                if (next > from) yield return (from, next);
            }
        }
        else if (Game == GameKind.Lufia1 && rom.Length >= MapWriter.ExpandedSize)
        {
            var had = baseRom.Length >= MapWriter.ExpandedSize ? ExpansionSpace.Load(baseRom).Entries.ToHashSet() : new HashSet<ExpansionSpace.Entry>();
            foreach (var entry in ExpansionSpace.Load(rom).Entries)
                if (!had.Contains(entry) && entry.Kind != ExpansionSpace.Kind.Unknown) yield return (entry.Start, entry.End);
        }
    }

    private void MakeHunks(bool[] aData)
    {
        var areas = new MergeAreas(Game, _rb, _ra, _size);
        int offset = 0;
        while (offset < _size)
        {
            if (_state[offset] is not (Theirs or Both)) { offset++; continue; }
            byte kind = _state[offset];
            int area = areas.At(offset);
            int start = offset, end = offset + 1, gap = 0;
            bool displaced = false;
            for (int q = offset; q < _size; q++)
            {
                if (_state[q] == kind && areas.At(q) == area) { end = q + 1; gap = 0; if (aData[q]) displaced = true; continue; }
                if (_state[q] is Theirs or Both || ++gap > 8 || end - start >= 0x1000) break;
            }
            AddHunk(start, end, kind == Both, areas.Name(area), areas.InSpace(area), displaced);
            offset = end;
        }
        // bytes both changed the same way
        for (int q = 0; q < _size; q++)
            if (_state[q] == Keep && _b[q] != _bb[q] && _a[q] == _b[q]) SameBytes++;
    }

    private void AddHunk(int start, int end, bool conflict, string area, bool inSpace, bool displaced)
    {
        int count = 0;
        for (int i = start; i < end; i++) if (_state[i] is Theirs or Both) count++;
        string where = Game == GameKind.Lufia2 ? $"${L2Rom.FileToSnes(start):X6}" : $"0x{start:X6}";
        string why = conflict
            ? "Both projects changed these bytes, differently." + (inSpace
                ? " Both put new data at the same place in the free space: whichever side is kept, the other side's change there is lost and has to be made again after the merge."
                : " Tick to use the other project's bytes.")
            : "Only the other project changed these bytes." + (displaced ? " This project's data that was there (events, a map's info or a packed file) is written again in free room." : "");
        var item = new MergeItem
        {
            Group = "Other data: " + area,
            What = $"{where}, {count} byte{(count == 1 ? "" : "s")}" + (conflict ? " (both changed)" : ""),
            Conflict = conflict, Take = !conflict, Why = why, Order = 900000 + _hunks.Count,
            Details = () => HexDetails(start, end),
        };
        _hunks.Add((start, end, item));
        Items.Add(item);
    }

    private List<DiffLine> HexDetails(int start, int end)
    {
        var lines = new List<DiffLine>
        {
            new(DiffKind.Head, $"File offset 0x{start:X6} to 0x{end - 1:X6}" + (Game == GameKind.Lufia2 ? $" (SNES ${L2Rom.FileToSnes(start):X6})" : "")),
            new(DiffKind.Note, "Each row: the starting ROM, then this project, then the other project. [xx] marks the bytes the other project changed."),
        };
        int shown = 0;
        for (int row = start & ~15; row < end && shown < 64; row += 16, shown++)
        {
            string Line(byte[] rom, bool mark)
            {
                var sb = new System.Text.StringBuilder();
                for (int i = row; i < row + 16 && i < _size; i++)
                {
                    bool marked = mark && i >= start && i < end && _state[i] is Theirs or Both;
                    sb.Append(marked ? $"[{rom[i]:X2}]" : $" {rom[i]:X2} ");
                }
                return sb.ToString();
            }
            lines.Add(new(DiffKind.Same, $"{row:X6} start  {Line(_ab, false)}"));
            lines.Add(new(DiffKind.Ours, $"{row:X6} this   {Line(_a, false)}"));
            lines.Add(new(DiffKind.Theirs, $"{row:X6} other  {Line(_b, true)}"));
        }
        if (shown == 64) lines.Add(new(DiffKind.Note, "(the first 1 KB is shown)"));
        return lines;
    }

    // applying

    // makes the merged rom (this project's rom isn't touched): the ticked bytes, then the space marks, then every event,
    // map info list and packed file as chosen, then the checksum, and checks the result. returns the rom and what was done
    public (byte[] Rom, List<string> Report) Apply()
    {
        var report = new List<string>();
        var merged = (byte[])_a.Clone();
        int taken = 0, conflicts = 0;
        foreach (var (start, end, item) in _hunks)
        {
            if (!item.Take) continue;
            for (int i = start; i < end; i++) if (_state[i] is Theirs or Both) merged[i] = _b[i];
            if (item.Conflict) conflicts++; else taken++;
        }
        for (int i = 0; i < _size; i++) if (_state[i] == Auto) merged[i] = _b[i];
        var rom = new RomBuffer(merged, "merge");
        MergeBookkeeping(rom, report);
        if (_l2 != null) _l2.Converge(rom, report);
        // a project made before 2026-11-14 can bring data in banks $40-$7D, which old snes9x versions can't read
        if (Game == GameKind.Lufia2 && L2HighBanks.Needed(rom))
            report.Add("Moved out of banks $40-$7D (old Snes9x versions can't read them): " + string.Join(", ", L2HighBanks.Move(rom)) + ".");
        // snes9x 1.51 mode: nothing of the other project's gets written in the top 2 MB (what it had there got moved
        // above), the merged rom keeps this project's bytes there
        if (Game == GameKind.Lufia2 && L2Rom.Snes9x151Mode && _size > L2Rom.UpperDataStart && L2Rom.IsExLoRom(rom))
        {
            int changed = 0;
            for (int i = L2Rom.UpperDataStart; i < _size; i++)
                if (rom.ReadByte(i) != _a[i]) { rom.WriteByte(i, _a[i]); changed++; }
            if (changed > 0 && L2HighBanks.Found(rom).Count > 0)
                throw new InvalidOperationException("Something of the merge still reads from the top 2 MB, which Snes9x 1.51 mode keeps out of: " + string.Join(", ", L2HighBanks.Found(rom)));
        }
        rom.FixChecksum();
        if (taken + conflicts > 0) report.Insert(0, $"{taken + conflicts} run{(taken + conflicts == 1 ? "" : "s")} of other bytes taken from \"{TheirName}\"" + (conflicts > 0 ? $" ({conflicts} where both projects had changed them)." : "."));
        if (_l2 != null) _l2.Verify(rom, report);
        return (rom.ToArray(), report);
    }

    // the free space marks of the merged rom, compared three ways like the bytes are. one only the other project changed
    // gets taken (a mark it moved on, or wiped when its run filled up or its bank stopped being event space), one both
    // changed goes to the side that got further. then every mark gets moved past whatever either project put in its
    // space, so nothing written later lands on it. lufia 1: the space table lists both projects' blocks
    private void MergeBookkeeping(RomBuffer rom, List<string> report)
    {
        if (Game == GameKind.Lufia2)
        {
            bool Same(byte[] x, byte[] y, int q) => x.AsSpan(q, 16).SequenceEqual(y.AsSpan(q, 16));
            void Take(int q) => rom.WriteBytes(q, _rb.ReadBytes(q, 16));
            // the added space (the first 4 MB's, the old run of banks $40-$7D). a run that filled up has its mark wiped and
            // the next run's mark at its end, so both sides' marks go together
            var dataA = L2Rom.DataBanks(_ra).ToHashSet(); var dataB = L2Rom.DataBanks(_rb).ToHashSet();
            foreach (bool upper in new[] { false, true })
            {
                bool Run((int Mark, int Start, int Next) s) => upper ? s.Mark == L2Rom.UpperMark : s.Mark < L2Rom.UpperStart;
                var markA = L2Rom.Spaces(_ra).Where(Run).Select(s => ((int, int)?)(s.Mark, s.Next)).FirstOrDefault();
                var markB = L2Rom.Spaces(_rb).Where(Run).Select(s => ((int, int)?)(s.Mark, s.Next)).FirstOrDefault();
                var markOffsets = new[] { markA, markB }.Where(x => x != null).Select(x => x!.Value.Item1).Distinct().ToList();
                if (markOffsets.Count == 0 || markOffsets.All(q => Same(_b, _bb, q))) continue;
                bool further = markB is var (bMark, bNext) && (markA is not var (aMark, aNext) || bMark > aMark || bMark == aMark && bNext > aNext);
                if (markOffsets.All(q => Same(_a, _ab, q)) || further) foreach (int q in markOffsets) Take(q);
            }
            // banks with a mark of their own: event space ("LF2V") and data banks ("LF2D"), both at the bank's last 16 bytes
            var aHas = L2EventSpace.Regions(_ra).Concat(dataA).ToHashSet(); var bHas = L2EventSpace.Regions(_rb).Concat(dataB).ToHashSet();
            foreach (int bank in aHas.Union(bHas))
            {
                int q = bank + L2EventSpace.Mark;
                if (Same(_b, _bb, q)) continue;
                if (Same(_a, _ab, q)) { Take(q); continue; }
                bool sameKind = dataA.Contains(bank) == dataB.Contains(bank);
                if (aHas.Contains(bank) && bHas.Contains(bank) && sameKind) { if (_rb.ReadUInt24Le(q + 4) > _ra.ReadUInt24Le(q + 4)) Take(q); }
                else if (bHas.Contains(bank))
                    report.Add($"The other project made bank ${L2Rom.FileToSnes(bank) >> 16:X2} {(dataB.Contains(bank) ? "a data bank" : "event space")}, but this project put {(dataA.Contains(bank) ? "other data" : "something")} there too: it stays this project's.");
            }
            // every mark past what either project put in its space (data banks are among the spaces)
            var spaces = L2Rom.Spaces(rom).Select(s => (s.Mark, s.Start)).Concat(L2EventSpace.Regions(rom).Select(b => (Mark: b + L2EventSpace.Mark, Start: b)));
            foreach (var (mark, start) in spaces)
            {
                int next = (int)rom.ReadUInt24Le(mark + 4), last = -1;
                for (int offset = Math.Min(mark, _size) - 1; offset >= Math.Max(start, next); offset--)
                    if (_aNew[offset] || _bNew[offset]) { last = offset; break; }
                if (last < 0) continue;
                rom.WriteBytes(mark + 4, new[] { (byte)(last + 1), (byte)(last + 1 >> 8), (byte)(last + 1 >> 16) });
            }
        }
        else if (Game == GameKind.Lufia1 && _size >= MapWriter.ExpandedSize)
        {
            var space = ExpansionSpace.Load(rom);
            var had = ExpansionSpace.Load(_rbb).Entries.ToHashSet();
            foreach (var entry in ExpansionSpace.Load(_rb).Entries)
                if (!had.Contains(entry)) space.Keep(entry);
            space.Save();
        }
    }
}

// names for where a run of bytes is (the game's parts and tables, our spaces and blocks)
internal sealed class MergeAreas
{
    private readonly List<(string Name, bool Space)> _names = new() { ("other", false) };
    private readonly ushort[] _paint;

    public MergeAreas(GameKind game, RomBuffer theirs, RomBuffer ours, int romSize)
    {
        _paint = new ushort[romSize];
        void Paint(int s, int e, string name, bool space = false)
        {
            int id = _names.FindIndex(x => x.Name == name);
            if (id < 0) { id = _names.Count; _names.Add((name, space)); }
            for (int i = Math.Max(0, s); i < Math.Min(romSize, e); i++) _paint[i] = (ushort)id;
        }
        foreach (var (s, e, name) in RomIdentity.Parts(game)) Paint(s, e, name);
        if (game == GameKind.Lufia2)
        {
            int size = Math.Max(theirs.Length, ours.Length);
            Paint(L2Rom.OriginalSize, Math.Min(size, L2Rom.MaxSize), "Lufia Forge's added space", true);
            if (size >= L2Rom.ExSize) Paint(L2Rom.UpperStart + 0x8000, size, "the upper 4 MB", true);
            foreach (var rom in new[] { ours, theirs })
            {
                foreach (int bank in L2EventSpace.Regions(rom))
                    Paint(bank, bank + 0x8000, $"event space (bank ${L2Rom.FileToSnes(bank) >> 16:X2})", true);
                foreach (int bank in L2Rom.DataBanks(rom))
                    Paint(bank, bank + 0x8000, $"Lufia Forge's data (bank ${L2Rom.FileToSnes(bank) >> 16:X2})", true);
            }
            foreach (var table in TableGuard.Lufia2) if (table.Old.Length > 0) Paint(table.Old.At, table.Old.At + table.Old.Length, table.Name);
            // our own blocks: from where each one starts to the next one (or the end of its bank)
            var marks = new List<(int At, string Name)>();
            foreach (var rom in new[] { ours, theirs })
                foreach (var (name, find) in Blocks)
                {
                    int blockAt;
                    try { blockAt = find(rom); } catch (Exception) { continue; }
                    if (blockAt > 0 && blockAt < romSize) marks.Add((blockAt, name));
                }
            marks = marks.Distinct().OrderBy(m => m.At).ToList();
            for (int i = 0; i < marks.Count; i++)
            {
                int end = Math.Min(i + 1 < marks.Count ? marks[i + 1].At : romSize, (marks[i].At / 0x8000 + 1) * 0x8000);
                Paint(marks[i].At, end, $"Lufia Forge's {marks[i].Name}", true);
            }
        }
        else if (game == GameKind.Lufia1 && romSize >= MapWriter.ExpandedSize)
        {
            Paint(MapWriter.ExpansionStart, romSize, "Lufia Forge's added space", true);
            foreach (var rom in new[] { ours, theirs })
            {
                if (rom.Length < MapWriter.ExpandedSize) continue;
                foreach (var e in ExpansionSpace.Load(rom).Entries)
                    Paint(e.Start, e.End, e.Kind switch
                    {
                        ExpansionSpace.Kind.MapData => "Lufia Forge's added space: map data",
                        ExpansionSpace.Kind.ScriptBlock => "Lufia Forge's added space: event scripts",
                        ExpansionSpace.Kind.Resource => "Lufia Forge's added space: graphics or music",
                        ExpansionSpace.Kind.Patch => "Lufia Forge's added space: a patch",
                        _ => "Lufia Forge's added space",
                    }, true);
            }
        }
    }

    // lufia 2 blocks we put in, by the patch or page that makes them
    private static readonly (string Name, Func<RomBuffer, int> Find)[] Blocks =
    {
        ("new statuses patch", L2StatusPatch.BlockAt), ("battle scripts block", L2AiScripts.Block), ("item break chances", L2ItemBreak.Block),
        ("shared battle scripts", L2SharedScripts.Block), ("monster attack names", L2AttackNames.Block), ("capsule monster wishes", L2CapsuleWishes.Block),
        ("descriptions", L2Descriptions.Block), ("element percentages", L2Elements.TableAt), ("gold patch", L2Gold.Block),
        ("monster sprites", L2MonsterSprites.Block), ("new maps tables", L2NewMaps.Block), ("new monsters", L2NewMonsters.Block),
        ("shops", L2Shops.Block), ("spell animations", L2SpellAnims.Block), ("new sprites", L2Sprites.Block),
        ("starting party", L2StartParty.Block), ("starting spells", L2StartSpells.Block), ("extra story flags patch", L2ExtraFlags.BlockAt),
    };

    public int At(int offset) => _paint[offset];
    public string Name(int id) => _names[id].Name;
    public bool InSpace(int id) => _names[id].Space;
}
