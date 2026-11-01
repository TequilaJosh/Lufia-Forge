namespace LufiaForge.Core.Lufia2;

/// <summary>
/// Ready-made pieces for puzzles, taken from the game's own maps: a closed and an open (or whole and blasted) picture of
/// the same size, each cell's blocks and flags on both layers, with where on it the trigger goes.
/// <list type="bullet">
/// <item>Doors: a door record (map info +02, 15 bytes: id, x, y, ..., byte 13 its look) and its look (+04, 10 bytes: id,
/// layers, the open picture's x, y, w, h, the closed picture's x, y, w, h: $83:873F opens, $83:8761 closes).</item>
/// <item>Bomb walls: a tile change of a bomb spot's puzzle script (06 / 0E / 7A place copy): the blocks at the place and
/// the copy's source (map info +16); the trigger is the bomb spot.</item>
/// </list>
/// Placed on another map they are brought into its block set cell by cell (<see cref="L2ChestLooks.Translate"/>).
/// </summary>
public static class L2PuzzlePresets
{
    public enum Kind { Door, BombWall }

    public sealed record Preset(Kind Kind, string Name, int Map, int W, int H, (int Block, byte Flags)[][] Closed, (int Block, byte Flags)[][] Open,
                                int TriggerX, int TriggerY, int BlockSet, int TileSet, int PaletteSet);

    private static (int Block, byte Flags)[] Cell(L2Maps.Map map, int x, int y) =>
        map.Layers.Select(L => x < L.Width && y < L.Height ? (L.Blocks[y * L.Width + x], L.Flags[y * L.Width + x]) : (0, (byte)0)).ToArray();

    private static (int Block, byte Flags)[][]? Rect(L2Maps.Map map, int x, int y, int w, int h)
    {
        if (map.Layers.Any(L => x + w > L.Width || y + h > L.Height)) return null;
        var cells = new (int, byte)[w * h][];
        for (int j = 0; j < h; j++) for (int i = 0; i < w; i++) cells[j * w + i] = Cell(map, x + i, y + j);
        return cells;
    }

    /// <summary>The trigger cell for a door opened by facing it, the party coming from below: walls are cell edges (flag
    /// 04 a wall on the cell's top edge), so the party stops on the lowest cell with one and faces the cell above it
    /// (Secret Skills Cave's 1x3 door: C8 / EC / E8 closed, 48 / 69 / 68 open; the party walks onto the bottom two and
    /// faces the top one). The bottom row when no cell has one.</summary>
    private static (int X, int Y) DoorTrigger((int Block, byte Flags)[][] closed, int w, int h)
    {
        for (int j = h - 1; j >= 0; j--)
            for (int i = 0; i < w; i++)
                if (closed[j * w + i].Any(c => (c.Flags & 0x04) != 0)) return (i, j - 1);
        return (0, h - 1);
    }

    /// <summary>The game's doors and bomb walls, one per picture and graphics.</summary>
    public static List<Preset> Game(RomBuffer rom)
    {
        var list = new List<Preset>();
        var seen = new HashSet<string>();
        for (int m = 2; m < L2Maps.Count; m++)
        {
            L2Maps.Map map;
            try { map = L2Maps.Read(rom, m); } catch { continue; }
            int bs = rom.ReadByte(L2Maps.BlockSetList + m), ts = rom.ReadByte(L2Maps.TileSetList + m), ps = rom.ReadByte(L2Maps.PaletteSetList + m);
            string name = L2Maps.Name(rom, m);
            void Add(Kind kind, int w, int h, (int, byte)[][]? closed, (int, byte)[][]? open, int tx, int ty)
            {
                if (closed == null || open == null || w < 1 || h < 1 || w > 8 || h > 8) return;
                string key = $"{kind}/{bs}/{ts}/{ps}/{w}x{h}/" + string.Join(",", closed.Concat(open).SelectMany(c => c).Select(c => $"{c.Item1:X}.{c.Item2:X}"));
                if (!seen.Add(key)) return;
                list.Add(new Preset(kind, $"{name} ({m:X2}), {w}x{h}", m, w, h, closed, open, tx, ty, bs, ts, ps));
            }

            // doors
            var looks = L2MapInfo.Records(rom, m, 0x04, 10).Select(r => r.Bytes).ToList();
            foreach (var (_, d) in L2MapInfo.Records(rom, m, 0x02, 15))
            {
                var l = looks.FirstOrDefault(x => x[0] == d[13]);
                if (l == null || L2MapEdit.IsSelfLook(l) || l[4] != l[8] || l[5] != l[9]) continue;
                int w = l[4], h = l[5];
                var closed = Rect(map, l[6], l[7], w, h);
                if (closed == null) continue;
                var (tx, ty) = DoorTrigger(closed, w, h);
                Add(Kind.Door, w, h, closed, Rect(map, l[2], l[3], w, h), tx, ty);
            }

            // bomb walls: the tile changes of the bomb spots' scripts
            int block = L2Puzzles.Block(rom, m);
            if (block < 0) continue;
            var copies = L2MapInfo.Records(rom, m, 0x16, 10).Select(r => r.Bytes).ToList();
            var spots = L2MapInfo.Records(rom, m, 0x22, 3).Select(r => r.Bytes).ToList();
            var bombs = L2MapInfo.Records(rom, m, 0x1A, 3).Select(r => r.Bytes).ToList();
            int end = L2Puzzles.End(rom, block);
            foreach (var (cat, id, off) in L2Puzzles.Entries(rom, block).Where(e => e.Category == 4))
            {
                var bomb = bombs.FirstOrDefault(b => b[0] == id);
                if (bomb == null) continue;
                var ops = L2Puzzles.Walk(rom, block + off, block, end);
                // the places handed on by "run" / "start a thread" (A9, 26, 27): FC is the first, FD the second...
                var passed = new List<int>();
                foreach (var op in ops.Where(o => rom.ReadByte(o.At) is 0xA9 or 0x26 or 0x27))
                {
                    for (int q = op.At + 1; q < op.At + op.Length - 2 && rom.ReadByte(q) != 0xFF; q++) passed.Add(rom.ReadByte(q));
                    break;
                }
                (int X, int Y)? Where(int p)
                {
                    if (p == 0xFB) return (bomb[1], bomb[2]);   // the spot that set it off
                    if (p is >= 0xFC and <= 0xFE) p = p - 0xFC < passed.Count ? passed[p - 0xFC] : -1;
                    var sp = p >= 0x20 && p < 0xE0 ? spots.FirstOrDefault(s => s[0] == p - 0x20) : null;
                    return sp == null ? null : (sp[1], sp[2]);
                }
                foreach (var op in ops)
                {
                    int c = rom.ReadByte(op.At);
                    if (c is 0x02 or 0xA0)
                    {
                        // a bomb that opens a door: the door's closed and open looks
                        int b = rom.ReadByte(op.At + 1);
                        if (b >= 0xC0 && b < 0xFB) b -= 0xC0;
                        var d = L2MapInfo.Records(rom, m, 0x02, 15).Select(r => r.Bytes).FirstOrDefault(r => r[0] == b);
                        var l = d == null ? null : looks.FirstOrDefault(x => x[0] == d[13]);
                        if (d == null || l == null || L2MapEdit.IsSelfLook(l) || l[4] != l[8] || l[5] != l[9]) continue;
                        Add(Kind.BombWall, l[4], l[5], Rect(map, l[6], l[7], l[4], l[5]), Rect(map, l[2], l[3], l[4], l[5]), bomb[1] - d[1], bomb[2] - d[2]);
                        continue;
                    }
                    if (c is not (0x06 or 0x0E or 0x7A)) continue;
                    int cid = rom.ReadByte(op.At + 2);
                    var at = Where(rom.ReadByte(op.At + 1));
                    var cp = copies.FirstOrDefault(x => x[0] == cid);
                    if (at == null || cp == null) continue;
                    int w = cp[4], h = cp[5];
                    Add(Kind.BombWall, w, h, Rect(map, at.Value.X, at.Value.Y, w, h), Rect(map, cp[2], cp[3], w, h), bomb[1] - at.Value.X, bomb[2] - at.Value.Y);
                }
            }
        }
        return list;
    }

    /// <summary>A cell of a preset as a chest look (one cell, closed and open), for <see cref="L2ChestLooks"/>.</summary>
    private static L2ChestLooks.Look CellLook(Preset p, int i) =>
        new(p.Map, 0, 0, 0x33, p.Closed[i], p.Open[i], p.BlockSet, p.TileSet, p.PaletteSet);

    /// <summary>The preset's closed and open cells in <paramref name="edit"/>'s block set (blocks and tiles added to the
    /// map's sets in the ROM when it lacks them; throws when its graphics can't take them).</summary>
    public static ((int Block, byte Flags)[][] Closed, (int Block, byte Flags)[][] Open) Translate(RomBuffer rom, Preset p, L2MapEdit edit)
    {
        var closed = new (int, byte)[p.Closed.Length][];
        var open = new (int, byte)[p.Open.Length][];
        for (int i = 0; i < p.Closed.Length; i++)
        {
            var (c, o, _) = L2ChestLooks.Translate(rom, CellLook(p, i), edit);
            closed[i] = c; open[i] = o;
        }
        return (closed, open);
    }

    /// <summary>The preset as a picture: the closed piece, a gap, the open piece.</summary>
    public static (int Width, int Height, uint[] Pixels) Picture(RomBuffer rom, Preset p)
    {
        int W = p.W * 16 * 2 + 8, H = p.H * 16;
        var px = new uint[W * H];
        for (int j = 0; j < p.H; j++)
            for (int i = 0; i < p.W; i++)
            {
                var (cw, _, cell) = L2ChestLooks.Picture(rom, CellLook(p, j * p.W + i));
                for (int y = 0; y < 16; y++)
                    for (int x = 0; x < 16; x++)
                    {
                        px[(j * 16 + y) * W + i * 16 + x] = cell[y * cw + x];
                        px[(j * 16 + y) * W + p.W * 16 + 8 + i * 16 + x] = cell[y * cw + 20 + x];
                    }
            }
        return (W, H, px);
    }
}
