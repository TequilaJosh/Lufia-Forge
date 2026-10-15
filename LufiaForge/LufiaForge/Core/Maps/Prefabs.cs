namespace LufiaForge.Core.Maps;

/// <summary>
/// Ready-made structures (houses, shops, rooms, towers) cut out of the game's own maps, for stamping.
/// For a tileset, the solid blocks of each map (walls, counters) fall apart into separate structures (cells side by
/// side), and identical structures are merged. Trees and other outdoor blocks never belong to one, nor do rivers,
/// ponds and a list of pieces picked out by hand. Cells of a structure's box that aren't part of it are
/// <see cref="Keep"/>: stamping leaves the ground and trees already painted there.
/// <para>Composite blocks (roof and tree overlays) are numbered from each map's own threshold, so a structure keeps
/// composites as "pair index"; <see cref="Placer"/> fits it to a map by matching block records, so it also works on
/// a tileset copied by Make room.</para>
/// </summary>
public static class Prefabs
{
    /// <summary>A cell left out of a structure (stamping keeps what's there).</summary>
    public const ushort Keep = 0xFFFF;
    /// <summary>Marks a composite cell in <see cref="Prefab.Cells"/>: the low 10 bits are its pair index.</summary>
    public const ushort CompositeFlag = 0x8000;

    public sealed record Prefab(int W, int H, ushort[] Cells, string Source, int Count, int Area, int Tileset = -1)
    {
        public string Category => Area >= 16 ? "Buildings" : "Small objects";
    }

    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<RomBuffer, Dictionary<string, List<Prefab>>> Cache = new();

    /// <summary>The structures for a tileset: its own maps' and those of tilesets with the same graphics (a tileset
    /// copied by Make room, or its original), each with its source tileset (see <see cref="Placer"/>).</summary>
    public static List<Prefab> ForTileset(RomBuffer rom, int tileset, IEnumerable<MapInfo> maps)
    {
        var list = maps as IList<MapInfo> ?? maps.ToList();
        var result = new List<Prefab>();
        foreach (int t in Family(rom, tileset)) result.AddRange(Find(rom, t, list));
        return result;
    }

    /// <summary>The tileset and the others with the same graphics (itself first).</summary>
    public static List<int> Family(RomBuffer rom, int tileset)
    {
        var tiles = TilesOf(rom, tileset);
        var family = new List<int> { tileset };
        for (int t = 0; t < Tilesets.Count(rom); t++)
        {
            if (t == tileset) continue;
            try { if (TilesOf(rom, t).AsSpan().SequenceEqual(tiles)) family.Add(t); } catch { }
        }
        return family;
    }

    private static byte[] TilesOf(RomBuffer rom, int tileset) =>
        LufiaCompression.DecompressResource(rom, Tilesets.TilesResource(rom, tileset), out _);

    /// <summary>The structures of every map with this tileset (cached per ROM and tileset key until the maps change).</summary>
    public static List<Prefab> Find(RomBuffer rom, int tileset, IEnumerable<MapInfo> maps)
    {
        var ids = maps.Where(m => m.MapId != LufiaMap.WorldMap && m.Tileset == tileset).Select(m => m.MapId).ToList();
        string key = tileset + ":" + string.Join(",", ids) + ":" + string.Join(",", ids.Select(i => LufiaCompression.ResourceOffset(rom, LufiaMap.MapDataResource(rom, i))));
        var cache = Cache.GetOrCreateValue(rom);
        if (cache.TryGetValue(key, out var hit)) return hit;

        var loaded = new List<LufiaMap>();
        foreach (int id in ids) { try { loaded.Add(LufiaMap.Load(rom, id)); } catch { } }
        // a cell's identity across maps: plain block b, or composite pair p (| flag)
        ushort Id(LufiaMap m, int c) { int b = c & 0x3FF; return m.CompositeThreshold >= 0 && b >= m.CompositeThreshold ? (ushort)(CompositeFlag | (b - m.CompositeThreshold)) : (ushort)b; }

        // ground: the most used cells, covering about 70% of all cells
        var usage = new Dictionary<ushort, int>();
        long total = 0;
        foreach (var m in loaded) foreach (var c in m.Layer) { var i = Id(m, c); usage[i] = usage.GetValueOrDefault(i) + 1; total++; }
        var ground = new HashSet<ushort>();
        long covered = 0;
        foreach (var kv in usage.OrderByDescending(kv => kv.Value))
        {
            if (covered >= total * 0.70) break;
            ground.Add(kv.Key); covered += kv.Value;
        }

        // per map: which cells are solid (walls, roofs, trees)
        var solids = new Dictionary<LufiaMap, bool[]>();
        foreach (var m in loaded)
        {
            var ts0 = MapTileset.ForMap(rom, m);
            var sol = new bool[m.Layer.Length];
            for (int i = 0; i < sol.Length; i++)
            {
                int b = m.Layer[i] & 0x3FF, a;
                if (ts0.CompositeParts(b) is var (g, o)) a = ts0.Attribute(g) | ts0.Attribute(o); else a = ts0.Attribute(b);
                sol[i] = (a & (BlockSet.AttrBlocked | BlockSet.AttrLeft | BlockSet.AttrRight | BlockSet.AttrUp | BlockSet.AttrDown)) != 0;
            }
            solids[m] = sol;
        }
        // outdoor blocks (trees, tree tops, forest edges): never part of a structure
        var outdoor = new HashSet<ushort>();
        var keys = CellKeys(rom, tileset);
        foreach (var m in loaded)
            foreach (var c in m.Layer)
            {
                var id = Id(m, c);
                if (!outdoor.Contains(id) && OutdoorRecords.Contains(keys(id))) outdoor.Add(id);
            }
        // and the blocks that are nearly always right next to them (bush edges, tree shadows on the grass)
        var nextTo = new Dictionary<ushort, int>();
        foreach (var m in loaded)
            for (int i = 0; i < m.Layer.Length; i++)
            {
                int x = i % m.Width, y = i / m.Width;
                bool by = false;
                for (int dy = -1; dy <= 1 && !by; dy++)
                    for (int dx = -1; dx <= 1 && !by; dx++)
                    {
                        int nx = x + dx, ny = y + dy;
                        if ((dx | dy) != 0 && nx >= 0 && ny >= 0 && nx < m.Width && ny < m.Height && outdoor.Contains(Id(m, m.Layer[ny * m.Width + nx]))) by = true;
                    }
                if (by) { var id = Id(m, m.Layer[i]); nextTo[id] = nextTo.GetValueOrDefault(id) + 1; }
            }
        foreach (var kv in nextTo)
            if (!ground.Contains(kv.Key) && kv.Value >= 4 && kv.Value >= usage[kv.Key] * 0.8) outdoor.Add(kv.Key);

        var found = new Dictionary<string, (Prefab P, int Count, string Source)>();
        var waterCache = new Dictionary<(int, int), bool>();
        foreach (var m in loaded)
        {
            int w = m.Width, h = m.Height;
            var cells = new ushort[w * h];
            for (int i = 0; i < cells.Length; i++) cells[i] = Id(m, m.Layer[i]);
            var solid = solids[m];
            var removed = RemovedCells(m.MapId, w);
            for (int i = 0; i < cells.Length; i++) if (outdoor.Contains(cells[i]) || removed.Contains(i)) solid[i] = false;
            var seen = new bool[w * h];
            for (int start = 0; start < cells.Length; start++)
            {
                if (seen[start] || !solid[start]) continue;
                // the structure: solid cells side by side (blocks touching only at a corner are something else)
                var part = new List<int>();
                var stack = new Stack<int>(); stack.Push(start); seen[start] = true;
                while (stack.Count > 0)
                {
                    int p = stack.Pop(); part.Add(p);
                    int px = p % w, py = p / w;
                    foreach (var (dx, dy) in Sides)
                    {
                        int nx = px + dx, ny = py + dy;
                        if (nx < 0 || ny < 0 || nx >= w || ny >= h) continue;
                        int q = ny * w + nx;
                        if (seen[q] || !solid[q]) continue;
                        seen[q] = true; stack.Push(q);
                    }
                }
                int x1 = part.Min(p => p % w), x2 = part.Max(p => p % w), y1 = part.Min(p => p / w), y2 = part.Max(p => p / w);
                int bw = x2 - x1 + 1, bh = y2 - y1 + 1;
                // something you'd place: at least 2 x 2 and 4 cells, at most 24 x 20, and mostly solid (not a long border)
                if (bw < 2 || bh < 2 || part.Count < 4 || bw > 24 || bh > 20 || part.Count < bw * bh * 0.30) continue;
                // a plain patch of one or two blocks isn't a structure
                if (part.Select(p => cells[p]).Distinct().Count() < 3) continue;
                if (IsRejected(m.MapId, part, w)) continue;
                if (part.Count < 16 && part.Any(p => removed.Contains(p - 1) || removed.Contains(p + 1) || removed.Contains(p - w) || removed.Contains(p + w))) continue;
                // rivers and ponds: painted, not placed
                if (Watery(rom, m, part.Select(p => m.Layer[p]).ToList(), waterCache) * 10 >= part.Count * 3) continue;
                var mine = part.ToHashSet();
                var pc = new ushort[bw * bh];
                for (int y = 0; y < bh; y++)
                    for (int x = 0; x < bw; x++)
                    {
                        int p = (y1 + y) * w + x1 + x;
                        // walkable cells in the box: doors, steps and other rare blocks touching the structure, or enclosed by it
                        // (never outdoor blocks: stamping keeps the ground and trees already painted there)
                        bool near = mine.Contains(p - 1) && x > 0 || mine.Contains(p + 1) && x < bw - 1 || mine.Contains(p - w) || mine.Contains(p + w);
                        bool keep = mine.Contains(p) || !outdoor.Contains(cells[p]) && !removed.Contains(p) &&
                                    (near && !ground.Contains(cells[p]) || Enclosed(cells, mine, ground, w, h, x1 + x, y1 + y, x1, y1, x2, y2));
                        pc[y * bw + x] = keep ? cells[p] : Keep;
                    }
                string k = bw + "x" + bh + ":" + string.Join(",", pc);
                string source = $"{m.MapId:X2}";
                if (found.TryGetValue(k, out var f)) found[k] = (f.P, f.Count + 1, f.Source.Contains(source) ? f.Source : f.Source + ", " + source);
                else found[k] = (new Prefab(bw, bh, pc, "", 1, part.Count, tileset), 1, source);
            }
        }
        var list = found.Values.Select(v => v.P with { Count = v.Count, Source = "maps " + v.Source })
                          .OrderByDescending(p => p.Area).ToList();
        cache[key] = list;
        int pairs = 0;
        foreach (var m in loaded)
            if (m.CompositeThreshold >= 0) pairs = Math.Max(pairs, m.Layer.Max(c => c & 0x3FF) - m.CompositeThreshold + 1);
        PairCounts.GetOrCreateValue(rom)[tileset] = pairs;
        return list;
    }

    /// <summary>How many of these cells look like water (mostly blue).</summary>
    private static int Watery(RomBuffer rom, LufiaMap m, List<ushort> cells, Dictionary<(int, int), bool> cache)
    {
        MapTileset? ts = null;
        int n = 0;
        foreach (var c in cells)
        {
            int b = c & 0x3FF;
            if (!cache.TryGetValue((m.MapId, b), out bool blue))
            {
                ts ??= MapTileset.ForMap(rom, m);
                int bluePx = 0;
                foreach (uint px in ts.MetatilePixels(b))
                {
                    int rr = (int)(px >> 16) & 0xFF, gg = (int)(px >> 8) & 0xFF, bb = (int)px & 0xFF;
                    if (bb > rr + 80 && bb > gg + 60) bluePx++;   // deep blue (water), not the light blue sky
                }
                cache[(m.MapId, b)] = blue = bluePx >= 128;
            }
            if (blue) n++;
        }
        return n;
    }

    private static readonly (int dx, int dy)[] Sides = { (1, 0), (-1, 0), (0, 1), (0, -1) };

    /// <summary>A block's identity across tilesets: its record and a checksum of its tiles' graphics.</summary>
    public static string BlockKey(byte[] blocks, byte[] tiles, int b)
    {
        if (b * BlockSet.Size + BlockSet.Size > blocks.Length) return "";
        uint hash = 2166136261;
        for (int q = 0; q < 4; q++)
        {
            int t = BlockSet.TileOf((ushort)(blocks[b * BlockSet.Size + q * 2] | blocks[b * BlockSet.Size + q * 2 + 1] << 8)) * 32;
            for (int i = t; i < t + 32 && i < tiles.Length; i++) hash = (hash ^ tiles[i]) * 16777619;
        }
        return Convert.ToHexString(blocks, b * BlockSet.Size, BlockSet.Size) + "-" + hash.ToString("X8");
    }

    /// <summary>Keys (<see cref="BlockKey"/>) of a tileset's cells: plain blocks, or "ground/overlay" for composites.</summary>
    public static Func<ushort, string> CellKeys(RomBuffer rom, int tileset)
    {
        var blocks = LufiaCompression.DecompressResource(rom, Tilesets.BlocksResource(rom, tileset), out _);
        var tiles = TilesOf(rom, tileset);
        int pairs = Tilesets.CompositePairs(rom, tileset);
        var memo = new Dictionary<ushort, string>();
        return id =>
        {
            if (memo.TryGetValue(id, out var k)) return k;
            if ((id & CompositeFlag) == 0) k = BlockKey(blocks, tiles, id);
            else
            {
                int e = pairs + (id & 0x3FF) * 4;
                k = e + 4 > rom.Length ? "" : BlockKey(blocks, tiles, rom.ReadUInt16Le(e) & 0x3FF) + "/" + BlockKey(blocks, tiles, rom.ReadUInt16Le(e + 2) & 0x3FF);
            }
            return memo[id] = k;
        };
    }

    /// <summary>Cells of a map taken out of its structures by hand (stray bits of wall, ledges, grass).</summary>
    private static HashSet<int> RemovedCells(int mapId, int w)
    {
        var set = new HashSet<int>();
        foreach (var c in Removed) if (c.Map == mapId) set.Add(c.Y * w + c.X);
        return set;
    }

    /// <summary>Structures left out on purpose (mostly inside one of these rectangles of a game map).</summary>
    private static bool IsRejected(int mapId, List<int> part, int w)
    {
        foreach (var r in Rejected)
            if (r.Map == mapId && part.Count(p => p % w >= r.X1 && p % w <= r.X2 && p / w >= r.Y1 && p / w <= r.Y2) * 2 > part.Count) return true;
        return false;
    }

    // Picked out by hand from the game's maps: blocks that belong outdoors (trees, tree tops, forest edges), and
    // structures that aren't worth placing (cave walls, ruins, lakes, plain floors).
    private static readonly HashSet<string> OutdoorRecords = new(StringComparer.Ordinal)
    {
        "071D171D081D181D00-A71DA4F9", "201020102010201000-2E197EE5", "201020102010201000-2E197EE5/00007C106D107D1000-FD47B918", "201020102010201000-2E197EE5/00207C306D307D3000-FD47B918",
        "201020102010201000-2E197EE5/6C10BC108D109D1000-1BD8F927", "201020102010201000-2E197EE5/6C30BC308D309D3000-1BD8F927", "201020102010201000-2E197EE5/6E107E1000007F1000-246FBDA1", "201020102010201000-2E197EE5/6E307E3000207F3000-246FBDA1",
        "201020102010201000-2E197EE5/8E309E306F30BF3000-C7F9B141", "301020103010201000-6450FAB1", "301030103010301000-5DF1A8C5", "301030103010301000-5DF1A8C5/6C30BC308D309D3000-1BD8F927",
        "301030103010301000-5DF1A8C5/8E309E306F30BF3000-C7F9B141", "620D720D630D730D0F-6347C454", "640D740D650D750D0F-AB5EA346", "640D740D650D750D0F-AB5EA346/F02C102D0020112D00-83D43108",
        "820C920C830C930C00-068E8A95/00381E2D0F2D1F2D00-64B6A7B5", "820C9A0C8B0C9B0C00-F03061F4", "840C8B0C850C950C00-65D621B6", "840C940C850C950C00-D4BF4F74",
        "840C940C850C950C00-D4BF4F74/00181B2D0C2D1C2D00-64B6A7B5", "840C940C850C950C00-D4BF4F74/00381B2D0C2D1C2D00-64B6A7B5", "840C940C850C950C00-D4BF4F74/00381E2D0F2D1F2D00-64B6A7B5", "840C940C850C950C00-D4BF4F74/0B2D2B2D00182C2D00-83D43108",
        "840C940C850C950C00-D4BF4F74/0B2D2B2D00202C2D00-83D43108", "840C940C850C950C00-D4BF4F74/0B2D2B2D00382C2D00-83D43108", "840C940C850C950C00-D4BF4F74/0C1400180C14001800-FABAD405", "840C940C850C950C00-D4BF4F74/0E2D2E2D00382F2D00-83D43108",
        "840C940C980C980C00-0E43B997", "840C940CC40CD40C00-311F8744", "840C990C850C990C00-B082A62D", "840C990C850C990C00-D75D57AD",
        "840C990C980C8A0C00-E1E51066", "840CC40CE00CC91C00-25B39F7B", "840CC40CE00CE71C00-D61C67CB", "840CE00C850CE10C00-31BE46ED",
        "860C940C850C950C00-48E7F5FF", "860C940C870C950C00-FAD3865C", "860C960C850C950C00-1003EFA2", "860C960C850C950C00-1003EFA2/00381E2D0F2D1F2D00-64B6A7B5",
        "860C960C870C970C00-C16D0BE0/00381E2D0F2D1F2D00-64B6A7B5", "860C960C870C970C00-C16D0BE0/0E2D2E2D00382F2D00-83D43108", "880C880C850C950C00-F527457A", "880C880C850C950C00-F574EBBA",
        "880C880C850C950C00-F574EBBA/00381E2D0F2D1F2D00-64B6A7B5", "880C8B0C850C990C00-8C79ACFA", "880C980C890C990C00-D4BF4F74", "880C980C890C990C00-D4BF4F74/863896388738973800-436E65AB",
        "880C980C890C990C00-D4BF4F74/F02C102D0020112D00-83D43108", "880CB80C890CB90C00-361DEA1C", "890C940C9A0C980C00-0CCC6F5B", "8A0C9A0C8B0C9B0C00-8E9C863D",
        "8C109C108D109D100F-44AB72A7", "8C109C108D109D100F-44AB72A7/6E107E1000007F1000-246FBDA1", "8E089E088F089F0800-D4F6960C/00381E2D0F2D1F2D00-64B6A7B5", "8E089E088F089F0800-D4F6960C/0E2D2E2D00382F2D00-83D43108",
        "8E109E108F109F100F-142A6D83", "8E109E108F109F100F-142A6D83/00007C106D107D1000-FD47B918", "980C940C830C9A0C00-3B875575", "9A0C940C830C9A0C00-A158EAF5/0E2D2E2D00382F2D00-83D43108",
        "9B0C880C890C950C00-6BBF9590", "9B0C940C850C950C00-D9D6A747", "A00CB00CA10CB10C00-E0EE5F49", "A20CB20CA30CB30C00-E8B715C9",
        "A20CB20CA30CB30C0F-E8B715C9", "A40CB40CA50CB10C0F-6347C454", "A40CB40CA50CB50C0F-6347C454", "A40CB40CA50CB50C0F-6347C454/00381B2D0C2D1C2D00-64B6A7B5",
        "A60CB60CA70CB70C0F-AB5EA346", "A60CB60CA70CB70C0F-AB5EA346/0B2D2B2D00382C2D00-83D43108", "A60CB60CA70CB70C0F-AB5EA346/0E2D2E2D00382F2D00-83D43108", "A80CB80CA90CB90C0F-C16D0BE0/B618C618B718C7180F-30E52F6A",
        "AC103010AD10BD100F-2B4E8B64", "AC103010AD10BD100F-2B4E8B64/00007C106D107D1000-FD47B918", "AC103010AD10BD100F-2B4E8B64/00207C306D307D3000-FD47B918", "AC103010AD10BD100F-2B4E8B64/8E109E106F10BF1000-C7F9B141",
        "AC103010AD10BD100F-2B4E8B64/8E309E306F30BF3000-C7F9B141", "AE10BE10AF1030100F-BA7C877B", "AE10BE10AF1030100F-BA7C877B/6C10BC108D109D1000-1BD8F927", "AE10BE10AF1030100F-BA7C877B/6C30BC308D309D3000-1BD8F927",
        "AE10BE10AF1030100F-BA7C877B/6E107E1000007F1000-246FBDA1", "AE10BE10AF1030100F-BA7C877B/6E307E3000207F3000-246FBDA1", "B214B214B214B21401-0C4E495D", "B618B618B618B61800-336AC6BD",
        "C00CD00CC10CD10C0F-9B55D3FA", "C00CD00CC10CD10C0F-9B55D3FA/0E2D2E2D00382F2D00-83D43108", "C00CD00CE10CF10C00-08F0852A", "C20CD20CC30CD30C0F-88C9B66D",
        "C20CD20CC30CD30C0F-88C9B66D/00381B2D0C2D1C2D00-64B6A7B5", "C20CD20CC30CD30C0F-88C9B66D/00381E2D0F2D1F2D00-64B6A7B5", "C214C214C214C21401-4566453D", "C214C214C214C21402-4566453D",
        "C214C214C214C21408-4566453D", "C40CD40CC50CD50C0F-ACDD6296/00007A2CA12CB12C0F-29E90079", "C50CD50CC60CD60C00-77376A60", "C70CD70C850C950C00-78DD199C",
        "C91CD91CCA1CDA1C00-477799FB", "CE10DE10CF10DF100F-F1D9D468", "CE10DE10CF10DF100F-F1D9D468/00207C306D307D3000-FD47B918", "E00CF00CE50CF50C00-6A2FC95E",
        "E10C081D870CC70C00-CFACC3C8", "E10C081DE00C071D00-25910082", "E10CCA1C870CC70C00-5304BFEA", "E10CCA1CE00CC91C00-AB99F974",
        "E10CE81C870CC70C00-66334742", "E40CF40CE50CF50C0F-51AE969D/A02CB02CC12CD12C0F-D1E9D345", "E60CF60CE70CF70C00-09EA2653/00207C306D307D3000-FD47B918", "E60CF60CE70CF70C00-09EA2653/6E307E3000207F3000-246FBDA1",
        "EC0DFC0DED0DFD0D0F-9B55D3FA", "EC0DFC0DED0DFD0D0F-9B55D3FA/F02C102D0020112D00-83D43108", "EE0DFE0DEF0DFF0D0F-88C9B66D", "EE0DFE0DEF0DFF0D0F-88C9B66D/0038002DF12C012D00-64B6A7B5",
        "EE10FE10EF10FF100F-79F79E0D", "EE10FE10EF10FF100F-79F79E0D/6E107E1000007F1000-246FBDA1", "EE10FE10EF10FF100F-79F79E0D/6E307E3000207F3000-246FBDA1", "F00C940CF10C950C00-3B9C982A",
        "F00C940CF10CD40C00-00E1FF3A", "F211E211E311E21100-DDA1B1CF", "F81CF10CD70C950C00-684C83C5",
    };

    private static readonly (int Map, int X1, int Y1, int X2, int Y2)[] Rejected =
    {
        (0x16, 24, 10, 28, 14),   // tileset 0 #67
        (0x1E, 17, 12, 22, 15),   // tileset 0 #72
        (0x1C, 5, 23, 13, 30),   // tileset 1 #21
        (0x08, 60, 29, 65, 31),   // tileset 1 #31
        (0x7F, 20, 27, 33, 35),   // tileset 2 #3
        (0x07, 59, 10, 61, 11),   // tileset 3 #14
        (0x07, 5, 62, 6, 63),   // tileset 3 #15
        (0x45, 0, 28, 16, 30),   // tileset 4 #2
        (0x81, 61, 10, 64, 11),   // tileset 4 #18
        (0x81, 62, 16, 64, 17),   // tileset 4 #27
        (0x77, 71, 21, 72, 22),   // tileset 5 #20
        (0x33, 48, 10, 64, 18),   // tileset 6 #1
        (0x24, 47, 11, 50, 17),   // tileset 6 #2
        (0x8B, 14, 25, 18, 30),   // tileset 6 #3
        (0x39, 44, 32, 47, 35),   // tileset 6 #4
        (0x39, 42, 39, 48, 42),   // tileset 6 #5
        (0x79, 14, 26, 18, 28),   // tileset 6 #6
        (0x82, 59, 6, 60, 8),   // tileset 6 #7
        (0x33, 50, 6, 51, 8),   // tileset 6 #8
        (0x33, 66, 8, 67, 10),   // tileset 6 #9
        (0x33, 58, 17, 60, 20),   // tileset 6 #10
        (0x7E, 81, 49, 85, 55),   // tileset 7 #25
        (0x3D, 4, 11, 13, 28),   // tileset 9 #1
        (0x92, 36, 34, 46, 39),   // tileset 9 #2
        (0x20, 51, 55, 61, 58),   // tileset 9 #3
        (0x35, 47, 0, 58, 11),   // tileset 0 #11
        (0x35, 49, 21, 62, 28),   // tileset 0 #32
        (0x35, 64, 21, 73, 29),   // tileset 0 #53
        (0x04, 73, 42, 81, 50),   // tileset 0 #57
        (0x3F, 35, 6, 47, 11),   // tileset 0 #82
        (0x0C, 39, 14, 47, 20),   // tileset 0 #85
        (0x0D, 3, 17, 10, 23),   // tileset 0 #88
        (0x15, 9, 8, 15, 13),   // tileset 0 #92
        (0x16, 25, 35, 26, 45),   // tileset 0 #94
        (0x41, 28, 13, 32, 18),   // tileset 0 #95
        (0x16, 28, 35, 30, 41),   // tileset 0 #97
        (0x35, 49, 30, 55, 31),   // tileset 0 #98
        (0x3F, 19, 12, 20, 18),   // tileset 0 #99
        (0x3F, 23, 12, 24, 18),   // tileset 0 #100
        (0x04, 76, 46, 78, 50),   // tileset 0 #101
        (0x04, 14, 37, 16, 39),   // tileset 0 #102
        (0x15, 16, 42, 18, 44),   // tileset 0 #102
        (0x0C, 42, 18, 44, 20),   // tileset 0 #103
        (0x0C, 14, 32, 17, 33),   // tileset 0 #104
        (0x11, 16, 32, 18, 34),   // tileset 0 #105
        (0x0D, 6, 21, 7, 23),   // tileset 0 #106
        (0x41, 6, 40, 7, 42),   // tileset 0 #106
        (0x1E, 32, 9, 33, 11),   // tileset 0 #107
        (0x1E, 42, 12, 44, 13),   // tileset 0 #108
        (0x41, 10, 40, 11, 42),   // tileset 0 #109
        (0x15, 0, 15, 23, 31),   // tileset 0 #110
        (0x35, 31, 25, 47, 38),   // tileset 0 #111
        (0x04, 28, 52, 32, 71),   // tileset 0 #112
        (0x0D, 10, 44, 16, 48),   // tileset 0 #113
        (0x0D, 20, 23, 23, 28),   // tileset 0 #114
        (0x6F, 19, 38, 41, 42),   // tileset 1 #11
        (0x1C, 1, 15, 4, 30),   // tileset 1 #42
        (0x08, 38, 37, 54, 38),   // tileset 1 #45
        (0x0B, 71, 34, 73, 43),   // tileset 1 #46
        (0x0B, 76, 34, 78, 43),   // tileset 1 #47
        (0x0B, 30, 23, 31, 30),   // tileset 1 #48
        (0x0B, 50, 25, 53, 31),   // tileset 1 #49
        (0x0B, 56, 28, 58, 32),   // tileset 1 #50
        (0x0B, 60, 24, 62, 29),   // tileset 1 #52
        (0x08, 23, 32, 25, 37),   // tileset 1 #53
        (0x3E, 5, 21, 7, 26),   // tileset 1 #54
        (0x08, 29, 37, 34, 38),   // tileset 1 #55
        (0x29, 25, 11, 30, 12),   // tileset 1 #55
        (0x0B, 21, 24, 23, 27),   // tileset 1 #56
        (0x0B, 63, 58, 68, 60),   // tileset 1 #57
        (0x29, 39, 29, 41, 34),   // tileset 1 #58
        (0x1C, 19, 26, 23, 28),   // tileset 1 #59
        (0x1C, 15, 35, 20, 36),   // tileset 1 #60
        (0x1C, 9, 33, 11, 35),   // tileset 1 #61
        (0x29, 26, 11, 29, 12),   // tileset 1 #63
        (0x29, 25, 41, 28, 42),   // tileset 1 #63
        (0x30, 46, 26, 47, 29),   // tileset 1 #64
        (0x08, 36, 14, 38, 16),   // tileset 1 #65
        (0x0B, 61, 42, 63, 44),   // tileset 1 #66
        (0x29, 39, 18, 41, 20),   // tileset 1 #67
        (0x29, 31, 46, 32, 49),   // tileset 1 #68
        (0x2D, 23, 38, 26, 39),   // tileset 1 #69
        (0x6F, 22, 15, 24, 16),   // tileset 1 #70
        (0x6F, 29, 20, 30, 21),   // tileset 1 #71
        (0x30, 17, 16, 31, 23),   // tileset 1 #72
        (0x05, 25, 0, 33, 17),   // tileset 2 #20
        (0x2F, 26, 17, 31, 33),   // tileset 2 #21
        (0x05, 32, 19, 38, 33),   // tileset 2 #22
        (0x2F, 22, 0, 27, 15),   // tileset 2 #23
        (0x75, 21, 48, 22, 59),   // tileset 3 #16
        (0x07, 18, 46, 19, 55),   // tileset 3 #17
        (0x2B, 2, 3, 21, 5),   // tileset 5 #9
        (0x18, 16, 10, 26, 11),   // tileset 5 #10
        (0x18, 14, 24, 20, 25),   // tileset 5 #13
        (0x18, 22, 24, 28, 25),   // tileset 5 #14
        (0x2B, 10, 13, 13, 14),   // tileset 5 #15
        (0x2B, 6, 17, 9, 18),   // tileset 5 #15
        (0x2B, 14, 17, 17, 18),   // tileset 5 #15
        (0x18, 13, 12, 15, 13),   // tileset 5 #16
        (0x18, 27, 12, 29, 13),   // tileset 5 #17
        (0x39, 50, 38, 53, 40),   // tileset 6 #3
        (0x39, 49, 33, 52, 36),   // tileset 6 #4
        (0x39, 41, 36, 43, 38),   // tileset 6 #5
        (0x39, 40, 32, 42, 34),   // tileset 6 #6
        (0x1B, 48, 52, 52, 56),   // tileset 7 #26
        (0x1B, 39, 61, 42, 64),   // tileset 7 #28
        (0x1B, 58, 61, 61, 64),   // tileset 7 #29
        (0x1B, 42, 51, 45, 54),   // tileset 7 #30
        (0x1B, 54, 59, 57, 61),   // tileset 7 #31
        (0x1B, 42, 51, 44, 53),   // tileset 7 #33
        (0x1B, 56, 51, 58, 53),   // tileset 7 #33
        (0x1B, 49, 54, 51, 56),   // tileset 7 #33
        (0x1B, 44, 59, 46, 61),   // tileset 7 #33
        (0x1B, 54, 59, 56, 61),   // tileset 7 #33
        (0x3C, 38, 46, 40, 49),   // tileset 7 #34
        (0x3C, 42, 46, 44, 49),   // tileset 7 #35
        (0x7E, 15, 12, 16, 16),   // tileset 7 #36
        (0x7E, 18, 12, 19, 16),   // tileset 7 #37
        (0x40, 5, 11, 13, 28),   // tileset 9 #1
    };

    private static readonly (int Map, int X, int Y)[] Removed =
    {
        (0x3F, 17, 26), (0x3F, 17, 27), (0x3F, 17, 28), (0x3F, 17, 29), (0x3F, 17, 30), (0x3F, 16, 31), (0x3F, 17, 31), (0x3F, 3, 32), (0x3F, 16, 32), (0x3F, 17, 32), (0x3F, 2, 33), (0x3F, 3, 33), (0x3F, 17, 33), (0x3F, 3, 34), (0x3F, 16, 34), (0x3F, 17, 34), (0x3F, 16, 35),   // tileset 0 #3
        (0x41, 34, 15), (0x41, 34, 17), (0x41, 34, 19),   // tileset 0 #21
        (0x29, 30, 15), (0x29, 30, 16), (0x29, 31, 16), (0x29, 32, 16), (0x29, 32, 17), (0x29, 32, 18), (0x29, 32, 19), (0x29, 32, 20), (0x29, 32, 21), (0x29, 32, 22), (0x29, 32, 23), (0x29, 32, 24), (0x29, 26, 25), (0x29, 27, 25), (0x29, 28, 25), (0x29, 29, 25), (0x29, 30, 25), (0x29, 31, 25), (0x29, 32, 25), (0x29, 25, 26), (0x29, 26, 26), (0x29, 27, 26), (0x29, 28, 26), (0x29, 29, 26), (0x29, 30, 26), (0x29, 31, 26), (0x29, 32, 26), (0x29, 24, 27), (0x29, 25, 27), (0x29, 26, 27), (0x29, 27, 27), (0x29, 28, 27), (0x29, 29, 27), (0x29, 30, 27), (0x29, 31, 27), (0x29, 23, 28), (0x29, 24, 28), (0x29, 25, 28), (0x29, 26, 28), (0x29, 27, 28), (0x29, 23, 29), (0x29, 24, 29), (0x29, 23, 30),   // tileset 1 #1
        (0x30, 57, 21), (0x30, 57, 24),   // tileset 1 #24
        (0x05, 15, 8),   // tileset 2 #5
        (0x05, 53, 8), (0x05, 53, 9), (0x05, 54, 9), (0x05, 53, 11), (0x05, 54, 11), (0x05, 53, 13), (0x05, 54, 13),   // tileset 2 #12
        (0x2F, 10, 6), (0x2F, 12, 6), (0x2F, 14, 6), (0x2F, 16, 6),   // tileset 2 #18
        (0x45, 24, 7), (0x45, 24, 8), (0x45, 24, 9), (0x45, 24, 10), (0x45, 25, 10), (0x45, 24, 11), (0x45, 25, 11), (0x45, 24, 12), (0x45, 24, 13), (0x45, 24, 14), (0x45, 24, 15), (0x45, 24, 16), (0x45, 24, 17), (0x45, 24, 18), (0x45, 24, 19), (0x45, 25, 19), (0x45, 15, 20), (0x45, 17, 20), (0x45, 19, 20), (0x45, 25, 20), (0x45, 26, 20), (0x45, 26, 21), (0x45, 26, 22),   // tileset 4 #3
        (0x45, 37, 5), (0x45, 37, 8), (0x45, 37, 9), (0x45, 37, 10), (0x45, 37, 11), (0x45, 37, 13), (0x45, 38, 13), (0x45, 26, 14), (0x45, 37, 14), (0x45, 38, 14), (0x45, 37, 16),   // tileset 4 #6
        (0x25, 37, 30), (0x25, 38, 30),   // tileset 4 #7
        (0x33, 24, 15), (0x33, 25, 15), (0x33, 26, 15), (0x33, 27, 15), (0x33, 28, 15), (0x33, 29, 15), (0x33, 30, 15), (0x33, 23, 16), (0x33, 31, 16), (0x33, 22, 17), (0x33, 32, 17), (0x33, 22, 18), (0x33, 32, 18), (0x33, 22, 19), (0x33, 32, 19),   // tileset 6 #2
        (0x17, 54, 44),   // tileset 7 #7
        (0x2A, 86, 12),   // tileset 7 #10
        (0x2A, 104, 12),   // tileset 7 #11
        (0x2A, 97, 22), (0x2A, 88, 23), (0x2A, 89, 23), (0x2A, 90, 23), (0x2A, 101, 23), (0x2A, 102, 23),   // tileset 7 #12
        (0x2A, 49, 12),   // tileset 7 #14
        (0x2A, 67, 12),   // tileset 7 #15
        (0x7E, 86, 21), (0x7E, 86, 22), (0x7E, 90, 22), (0x7E, 86, 23), (0x7E, 90, 23), (0x7E, 87, 24), (0x7E, 90, 24), (0x7E, 90, 25), (0x7E, 90, 29), (0x7E, 90, 30),   // tileset 7 #19
        (0x7E, 49, 21), (0x7E, 50, 21), (0x7E, 53, 22), (0x7E, 53, 23), (0x7E, 53, 24),   // tileset 7 #25
    };

    /// <summary>A ground cell inside the box with structure cells on both sides, across and up/down (kept: courtyards).</summary>
    private static bool Enclosed(ushort[] cells, HashSet<int> mine, HashSet<ushort> ground, int w, int h, int x, int y, int x1, int y1, int x2, int y2)
    {
        bool Hit(int dx, int dy)
        {
            for (int cx = x + dx, cy = y + dy; cx >= x1 && cx <= x2 && cy >= y1 && cy <= y2; cx += dx, cy += dy)
                if (mine.Contains(cy * w + cx)) return true;
            return false;
        }
        return Hit(-1, 0) && Hit(1, 0) && (Hit(0, -1) || Hit(0, 1));
    }

    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<RomBuffer, Dictionary<int, int>> PairCounts = new();

    /// <summary>How many (ground, overlay) pairs a tileset's maps use (the part of its pair table that's in use).</summary>
    public static int PairCount(RomBuffer rom, int tileset, IEnumerable<MapInfo> maps)
    {
        Find(rom, tileset, maps);
        return PairCounts.GetOrCreateValue(rom).TryGetValue(tileset, out int n) ? n : 0;
    }

    /// <summary>A structure ready for a map: its cells as the map's block numbers (<see cref="Keep"/> where the map's
    /// blocks stay), and the composite threshold the map needs for them (higher than its own when the structure uses
    /// blocks the map shows as composites: <see cref="LufiaMap.RaiseThreshold"/>).</summary>
    public sealed record Placement(Prefab Prefab, ushort[] Cells, int Threshold);

    /// <summary>Fits structures (from the map's own tileset or one with the same graphics) to a map: blocks are matched
    /// by their records, composites by the records of their ground and overlay.</summary>
    public sealed class Placer
    {
        private readonly RomBuffer _rom;
        private readonly LufiaMap _map;
        private readonly byte[] _blocks;
        private readonly int _count;
        private readonly Dictionary<string, List<int>> _byRecord = new();
        private readonly Dictionary<string, int> _byPair = new();
        private readonly Dictionary<int, (byte[] Blocks, int Pairs)> _sources = new();

        public Placer(RomBuffer rom, LufiaMap map, IEnumerable<MapInfo> maps)
        {
            _rom = rom; _map = map;
            _blocks = LufiaCompression.DecompressResource(rom, Tilesets.BlocksResource(rom, map.Tileset), out _);
            _count = _blocks.Length / BlockSet.Size;
            for (int b = 0; b < _count; b++)
            {
                string k = Record(_blocks, b);
                if (!_byRecord.TryGetValue(k, out var l)) _byRecord[k] = l = new List<int>();
                l.Add(b);
            }
            if (map.CompositeThreshold >= 0)
            {
                int table = Tilesets.CompositePairs(rom, map.Tileset);
                int own = map.Layer.Max(c => c & 0x3FF) - map.CompositeThreshold + 1;
                // a copy made by Make room holds the pairs its original's maps use
                int pairs = Math.Max(own, Family(rom, map.Tileset).Max(t => PairCount(rom, t, maps)));
                for (int q = pairs - 1; q >= 0; q--)
                {
                    if (table + q * 4 + 4 > rom.Length) continue;
                    var (g, o) = PairAt(rom, table, q);
                    if (g < _count && o < _count) _byPair[Record(_blocks, g) + "/" + Record(_blocks, o)] = q;
                }
            }
        }

        private static string Record(byte[] blocks, int b) =>
            b * BlockSet.Size + BlockSet.Size <= blocks.Length ? Convert.ToHexString(blocks, b * BlockSet.Size, BlockSet.Size) : "";

        private static (int G, int O) PairAt(RomBuffer rom, int table, int q) =>
            (rom.ReadUInt16Le(table + q * 4) & 0x3FF, rom.ReadUInt16Le(table + q * 4 + 2) & 0x3FF);

        /// <summary>The structure for this map, or null when some of its blocks aren't in the map's tileset.</summary>
        public Placement? Place(Prefab p)
        {
            if (p.Tileset < 0 || _map.IsWorld) return null;
            if (!_sources.TryGetValue(p.Tileset, out var src))
                _sources[p.Tileset] = src = (p.Tileset == _map.Tileset ? _blocks : LufiaCompression.DecompressResource(_rom, Tilesets.BlocksResource(_rom, p.Tileset), out _),
                                             Tilesets.CompositePairs(_rom, p.Tileset));
            var keys = new string[p.Cells.Length];
            var same = new int[p.Cells.Length];
            for (int i = 0; i < keys.Length; i++)
            {
                ushort c = p.Cells[i];
                same[i] = -1;
                if (c == Keep) { keys[i] = ""; continue; }
                if ((c & CompositeFlag) != 0)
                {
                    var (g, o) = PairAt(_rom, src.Pairs, c & 0x3FF);
                    keys[i] = Record(src.Blocks, g) + "/" + Record(src.Blocks, o);
                }
                else
                {
                    keys[i] = Record(src.Blocks, c);
                    if (p.Tileset == _map.Tileset) same[i] = c;   // the same block number when it is the same block
                }
            }
            return PlaceKeys(p, keys, same);
        }

        /// <summary>A preset the user made (cells kept as block records), fitted to this map; null when some of its
        /// blocks aren't in the map's tileset.</summary>
        public Placement? Place(CustomPresets.Preset preset)
        {
            if (_map.IsWorld) return null;
            var shell = new Prefab(preset.W, preset.H, Array.Empty<ushort>(), preset.Name, 1, preset.Count, _map.Tileset);
            return PlaceKeys(shell, preset.Cells, Enumerable.Repeat(-1, preset.Cells.Length).ToArray());
        }

        /// <summary>Cells given by their block records ("" = left out, "ground/overlay" = a composite) as this map's
        /// block numbers. <paramref name="same"/>: a block number to keep when it still is that block (or -1).</summary>
        private Placement? PlaceKeys(Prefab p, string[] keys, int[] same)
        {
            int t = _map.CompositeThreshold;
            var plain = new int[keys.Length];
            var pair = new int[keys.Length];
            int need = t;
            for (int i = 0; i < keys.Length; i++)
            {
                string k = keys[i];
                plain[i] = pair[i] = -1;
                if (k.Length == 0) continue;
                if (k.Contains('/'))
                {
                    if (t < 0 || !_byPair.TryGetValue(k, out int q)) return null;
                    pair[i] = q;
                }
                else
                {
                    if (!_byRecord.TryGetValue(k, out var list)) return null;
                    // the same block number when it is the same block, else the first copy the map shows as a block
                    int b = same[i] >= 0 && (t < 0 || same[i] < t) ? same[i] : list.FirstOrDefault(x => t < 0 || x < t, list[0]);
                    plain[i] = b;
                    if (t >= 0 && b >= t) need = Math.Max(need, b + 1);
                }
            }
            if (t >= 0 && need > t)
            {
                // the map's own composites must still fit below 400 after raising the threshold
                int ownMax = _map.Layer.Max(c => c & 0x3FF);
                if (ownMax >= t && ownMax + need - t > 0x3FF) return null;
            }
            var cells = new ushort[keys.Length];
            for (int i = 0; i < cells.Length; i++)
            {
                if (plain[i] >= 0) cells[i] = (ushort)plain[i];
                else if (pair[i] >= 0)
                {
                    int b = need + pair[i];
                    if (b > 0x3FF) return null;
                    cells[i] = (ushort)b;
                }
                else cells[i] = Keep;
            }
            return new Placement(p, cells, need);
        }

        /// <summary>Blocks copied from a map (any blocks) as a structure, so <see cref="Place"/> can fit them here.</summary>
        public static Prefab FromMap(LufiaMap from, int w, int h, ushort[] blocks, string source) =>
            new(w, h, blocks.Select(b => b == Keep ? Keep : from.CompositeThreshold >= 0 && (b & 0x3FF) >= from.CompositeThreshold
                ? (ushort)(CompositeFlag | ((b & 0x3FF) - from.CompositeThreshold)) : (ushort)(b & 0x3FF)).ToArray(),
                source, 1, w * h, from.Tileset);
    }
}
