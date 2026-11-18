namespace LufiaForge.Core.Lufia2;

// ready-made pieces for puzzles, taken from the game's own maps: a closed and an open (or whole and blasted) picture
// of the same size, each cell's blocks and flags on both layers, plus where on it the trigger goes
// - doors: a door record (map info +02, 15 bytes: id, x, y, ..., byte 13 its look) and its look (+04, 10 bytes: id,
//   layers, the open picture's x, y, w, h, the closed picture's x, y, w, h. $83:873F opens, $83:8761 closes)
// - bomb walls: a tile change in a bomb spot's puzzle script (06 / 0E / 7A place copy): the blocks at the place and
//   the copy's source (map info +16). the trigger is the bomb spot
// placed on another map they get brought into its block set cell by cell (L2ChestLooks.Translate)
public static class L2PuzzlePresets
{
    public enum Kind { Door, BombWall }

    public sealed record Preset(Kind Kind, string Name, int Map, int W, int H, (int Block, byte Flags)[][] Closed, (int Block, byte Flags)[][] Open,
                                int TriggerX, int TriggerY, int BlockSet, int TileSet, int PaletteSet);

    private static (int Block, byte Flags)[] Cell(L2Maps.Map map, int x, int y) =>
        map.Layers.Select(layer => x < layer.Width && y < layer.Height ? (layer.Blocks[y * layer.Width + x], layer.Flags[y * layer.Width + x]) : (0, (byte)0)).ToArray();

    private static (int Block, byte Flags)[][]? Rect(L2Maps.Map map, int x, int y, int w, int h)
    {
        if (map.Layers.Any(layer => x + w > layer.Width || y + h > layer.Height)) return null;
        var cells = new (int, byte)[w * h][];
        for (int j = 0; j < h; j++) for (int i = 0; i < w; i++) cells[j * w + i] = Cell(map, x + i, y + j);
        return cells;
    }

    // the trigger cell for a door you open by facing it, with the party coming from below. walls are cell edges (flag
    // 04 = a wall on the cell's top edge), so the party stops on the lowest cell with one and faces the cell above it.
    // Secret Skills Cave's 1x3 door is C8 / EC / E8 closed, 48 / 69 / 68 open: the party walks onto the bottom two and
    // faces the top one. the bottom row if no cell has one
    private static (int X, int Y) DoorTrigger((int Block, byte Flags)[][] closed, int w, int h)
    {
        for (int j = h - 1; j >= 0; j--)
            for (int i = 0; i < w; i++)
                if (closed[j * w + i].Any(c => (c.Flags & 0x04) != 0)) return (i, j - 1);
        return (0, h - 1);
    }

    // the game's doors and bomb walls, one per picture and graphics
    public static List<Preset> Game(RomBuffer rom)
    {
        var list = new List<Preset>();
        var seen = new HashSet<string>();
        for (int m = 2; m < L2Maps.Count(rom); m++)
        {
            L2Maps.Map map;
            try { map = L2Maps.Read(rom, m); } catch { continue; }
            int blockSet = rom.ReadByte(L2Maps.BlockSetAt(rom, m)), tileSet = rom.ReadByte(L2Maps.TileSetAt(rom, m)), paletteSet = rom.ReadByte(L2Maps.PaletteSetAt(rom, m));
            string name = L2Maps.Name(rom, m);
            void Add(Kind kind, int w, int h, (int, byte)[][]? closed, (int, byte)[][]? open, int triggerX, int triggerY)
            {
                if (closed == null || open == null || w < 1 || h < 1 || w > 8 || h > 8) return;
                string key = $"{kind}/{blockSet}/{tileSet}/{paletteSet}/{w}x{h}/" + string.Join(",", closed.Concat(open).SelectMany(c => c).Select(c => $"{c.Item1:X}.{c.Item2:X}"));
                if (!seen.Add(key)) return;
                list.Add(new Preset(kind, $"{name} ({m:X2}), {w}x{h}", m, w, h, closed, open, triggerX, triggerY, blockSet, tileSet, paletteSet));
            }

            // doors
            var looks = L2MapInfo.Records(rom, m, 0x04, 10).Select(r => r.Bytes).ToList();
            foreach (var (_, door) in L2MapInfo.Records(rom, m, 0x02, 15))
            {
                var look = looks.FirstOrDefault(x => x[0] == door[13]);
                if (look == null || L2MapEdit.IsSelfLook(look) || look[4] != look[8] || look[5] != look[9]) continue;
                int w = look[4], h = look[5];
                var closed = Rect(map, look[6], look[7], w, h);
                if (closed == null) continue;
                var (triggerX, triggerY) = DoorTrigger(closed, w, h);
                Add(Kind.Door, w, h, closed, Rect(map, look[2], look[3], w, h), triggerX, triggerY);
            }

            // bomb walls: the tile changes in the bomb spots' scripts
            int block = L2Puzzles.Block(rom, m);
            if (block < 0) continue;
            var copies = L2MapInfo.Records(rom, m, 0x16, 10).Select(r => r.Bytes).ToList();
            var spots = L2MapInfo.Records(rom, m, 0x22, 3).Select(r => r.Bytes).ToList();
            var bombs = L2MapInfo.Records(rom, m, 0x1A, 3).Select(r => r.Bytes).ToList();
            int end = L2Puzzles.End(rom, block);
            foreach (var (category, id, offset) in L2Puzzles.Entries(rom, block).Where(e => e.Category == 4))
            {
                var bomb = bombs.FirstOrDefault(b => b[0] == id);
                if (bomb == null) continue;
                var ops = L2Puzzles.Walk(rom, block + offset, block, end);
                // the places handed on by "run" / "start a thread" (A9, 26, 27): FC is the first, FD the second...
                var passed = new List<int>();
                foreach (var op in ops.Where(o => rom.ReadByte(o.At) is 0xA9 or 0x26 or 0x27))
                {
                    for (int q = op.At + 1; q < op.At + op.Length - 2 && rom.ReadByte(q) != 0xFF; q++) passed.Add(rom.ReadByte(q));
                    break;
                }
                (int X, int Y)? Where(int place)
                {
                    if (place == 0xFB) return (bomb[1], bomb[2]);   // the spot that set it off
                    if (place is >= 0xFC and <= 0xFE) place = place - 0xFC < passed.Count ? passed[place - 0xFC] : -1;
                    var spot = place >= 0x20 && place < 0xE0 ? spots.FirstOrDefault(s => s[0] == place - 0x20) : null;
                    return spot == null ? null : (spot[1], spot[2]);
                }
                foreach (var op in ops)
                {
                    int code = rom.ReadByte(op.At);
                    if (code is 0x02 or 0xA0)
                    {
                        // a bomb that opens a door: the door's closed and open looks
                        int doorId = rom.ReadByte(op.At + 1);
                        if (doorId >= 0xC0 && doorId < 0xFB) doorId -= 0xC0;
                        var door = L2MapInfo.Records(rom, m, 0x02, 15).Select(r => r.Bytes).FirstOrDefault(r => r[0] == doorId);
                        var look = door == null ? null : looks.FirstOrDefault(x => x[0] == door[13]);
                        if (door == null || look == null || L2MapEdit.IsSelfLook(look) || look[4] != look[8] || look[5] != look[9]) continue;
                        Add(Kind.BombWall, look[4], look[5], Rect(map, look[6], look[7], look[4], look[5]), Rect(map, look[2], look[3], look[4], look[5]), bomb[1] - door[1], bomb[2] - door[2]);
                        continue;
                    }
                    if (code is not (0x06 or 0x0E or 0x7A)) continue;
                    int copyId = rom.ReadByte(op.At + 2);
                    var at = Where(rom.ReadByte(op.At + 1));
                    var copy = copies.FirstOrDefault(x => x[0] == copyId);
                    if (at == null || copy == null) continue;
                    int w = copy[4], h = copy[5];
                    Add(Kind.BombWall, w, h, Rect(map, at.Value.X, at.Value.Y, w, h), Rect(map, copy[2], copy[3], w, h), bomb[1] - at.Value.X, bomb[2] - at.Value.Y);
                }
            }
        }
        return list;
    }

    // a cell of a preset as a chest look (one cell, closed and open), for L2ChestLooks
    private static L2ChestLooks.Look CellLook(Preset preset, int i) =>
        new(preset.Map, 0, 0, 0x33, preset.Closed[i], preset.Open[i], preset.BlockSet, preset.TileSet, preset.PaletteSet);

    // the preset's closed and open cells in edit's block set. blocks and tiles get added to the map's sets in the rom
    // if it doesn't have them, and it throws if its graphics can't take them
    public static ((int Block, byte Flags)[][] Closed, (int Block, byte Flags)[][] Open) Translate(RomBuffer rom, Preset p, L2MapEdit edit)
    {
        var closed = new (int, byte)[p.Closed.Length][];
        var open = new (int, byte)[p.Open.Length][];
        for (int i = 0; i < p.Closed.Length; i++)
        {
            var (closedCell, openCell, _) = L2ChestLooks.Translate(rom, CellLook(p, i), edit);
            closed[i] = closedCell; open[i] = openCell;
        }
        return (closed, open);
    }

    // the preset as a picture: the closed piece, a gap, the open piece
    public static (int Width, int Height, uint[] Pixels) Picture(RomBuffer rom, Preset p)
    {
        int width = p.W * 16 * 2 + 8, height = p.H * 16;
        var pixels = new uint[width * height];
        for (int j = 0; j < p.H; j++)
            for (int i = 0; i < p.W; i++)
            {
                var (cellWidth, _, cell) = L2ChestLooks.Picture(rom, CellLook(p, j * p.W + i));
                for (int y = 0; y < 16; y++)
                    for (int x = 0; x < 16; x++)
                    {
                        pixels[(j * 16 + y) * width + i * 16 + x] = cell[y * cellWidth + x];
                        pixels[(j * 16 + y) * width + p.W * 16 + 8 + i * 16 + x] = cell[y * cellWidth + 20 + x];
                    }
            }
        return (width, height, pixels);
    }
}
