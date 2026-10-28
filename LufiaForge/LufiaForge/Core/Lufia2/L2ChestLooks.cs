namespace LufiaForge.Core.Lufia2;

/// <summary>
/// Chest pictures for maps that have no chest of their own. A chest's look (the map info's list +04, <see cref="L2Chests"/>)
/// copies blocks of the same map onto the chest when it opens ($83:873F / $83:8B6A: the opened picture's source, w x h
/// blocks; the layers byte says which layers: layer n when a bit of 0x11 &lt;&lt; n is set, both bits = the whole cell),
/// so the open chest's blocks must be somewhere on the map; the game keeps them on blocks the camera never shows (most
/// maps in their top-left corner). The closed chest is just painted where the chest stands.
///
/// <see cref="GameLooks"/> lists the game's chest looks (the closed blocks on the chest's spot, the open blocks at the
/// look's source), one per picture and graphics. <see cref="Translate"/> gives a look's blocks in another map's block
/// set: the block of that set that draws the same (pixels, priority, block attribute), or a block added to the set made
/// of that tile set's tiles (any palette row and flip that draws the same colours); when a tile isn't in the tile set the
/// look can't be used there. <see cref="Stash"/> finds two blocks side by side the camera never shows, for the open and
/// closed pictures.
/// </summary>
public static class L2ChestLooks
{
    /// <summary>A chest look of map <see cref="Map"/>: spot id, look id, layers byte, and the closed and open pictures
    /// (each layer's block and flags) in that map's block set, tile set and palette set.</summary>
    public sealed record Look(int Map, int Spot, int LookId, int Layers, (int Block, byte Flags)[] Closed, (int Block, byte Flags)[] Open,
                              int BlockSet, int TileSet, int PaletteSet);

    public const int MaxBlocks = 0x400;

    /// <summary>The chest looks of the game's maps (1 x 1 chests with an item), one per picture and graphics.</summary>
    public static List<Look> GameLooks(RomBuffer rom)
    {
        var list = new List<Look>();
        var seen = new HashSet<string>();
        for (int m = 2; m < L2Maps.Count; m++)
        {
            List<(int At, byte[] Bytes)> spots;
            try { spots = L2MapInfo.Records(rom, m, 0x26, 4); } catch { continue; }
            if (spots.Count == 0) continue;
            var looks = L2MapInfo.Records(rom, m, 0x04, 10).Select(r => r.Bytes).ToList();
            var chests = L2Chests.Records(rom, m);
            L2Maps.Map map;
            try { map = L2Maps.Read(rom, m); } catch { continue; }
            int bs = rom.ReadByte(L2Maps.BlockSetList + m), ts = rom.ReadByte(L2Maps.TileSetList + m), ps = rom.ReadByte(L2Maps.PaletteSetList + m);
            foreach (var (_, b) in spots)
            {
                var l = looks.FirstOrDefault(x => x[0] == b[3]);
                if (l == null || L2MapEdit.IsSelfLook(l) || l[4] != 1 || l[5] != 1 || !chests.Any(c => c.Spot == (b[0] & 0x1F))) continue;
                if (map.Layers.Any(L => b[1] >= L.Width || b[2] >= L.Height || l[2] >= L.Width || l[3] >= L.Height)) continue;
                var closed = map.Layers.Select(L => (L.Blocks[b[2] * L.Width + b[1]], L.Flags[b[2] * L.Width + b[1]])).ToArray();
                var open = map.Layers.Select(L => (L.Blocks[l[3] * L.Width + l[2]], L.Flags[l[3] * L.Width + l[2]])).ToArray();
                string key = $"{bs}/{ts}/{ps}/{l[1]}/" + string.Join(",", closed.Concat(open).Select(c => $"{c.Item1:X}.{c.Item2:X}"));
                if (!seen.Add(key)) continue;
                list.Add(new Look(m, b[0], l[0], l[1], closed, open, bs, ts, ps));
            }
        }
        return list;
    }

    /// <summary>The chest looks of a map being edited (its chests as they are in the edit).</summary>
    public static List<Look> LooksOf(L2MapEdit e)
    {
        var list = new List<Look>();
        if (e.IsWorld) return list;
        var seen = new HashSet<string>();
        foreach (var (_, b) in e.Records(L2MapEdit.Treasure))
        {
            var l = e.LookOf(b);
            if (l == null || L2MapEdit.IsSelfLook(l) || l[4] != 1 || l[5] != 1 || e.ChestOf(b[0] & 0x1F) == null) continue;
            if (b[1] >= e.Width || b[2] >= e.Height || l[2] >= e.Width || l[3] >= e.Height) continue;
            var closed = e.CellAt(b[1], b[2]);
            var open = e.CellAt(l[2], l[3]);
            string key = $"{l[1]}/" + string.Join(",", closed.Concat(open).Select(c => $"{c.Block:X}"));
            if (!seen.Add(key)) continue;
            list.Add(new Look(e.MapId, b[0], l[0], l[1], closed, open, e.BlockSetIndex, e.TileSetIndex, e.PaletteSetIndex));
        }
        return list;
    }

    // ---------------------------------------------------------------- blocks and tiles

    private sealed class Sets
    {
        public byte[] BlockSet = Array.Empty<byte>(), Tiles = Array.Empty<byte>();
        public uint[][] Palettes = Array.Empty<uint[]>();
        public int Count => (short)(BlockSet[2] | BlockSet[3] << 8);
        public int Word(int b, int q) => BlockSet[0x10 + b * 8 + q * 2] | BlockSet[0x10 + b * 8 + q * 2 + 1] << 8;
        public byte Attribute(int b) => 0x10 + Count * 8 + b < BlockSet.Length ? BlockSet[0x10 + Count * 8 + b] : (byte)0;
    }

    private static Sets Load(RomBuffer rom, int map, int bs, int ts, int ps) => new()
    {
        BlockSet = L2Rom.ReadFile(rom, L2Maps.BlockSetFileBase + bs), Tiles = L2Rom.ReadFile(rom, L2Maps.TileSetFileBase + ts),
        Palettes = L2Maps.Palettes(rom, map, ps),
    };

    /// <summary>A tile drawn with a block word (tile, palette row, flips) as 64 ARGB pixels (0 = see-through).</summary>
    private static uint[] Quarter(Sets s, int word)
    {
        var px = new uint[64];
        int tile = word & 0x3FF, pal = word >> 10 & 7;
        bool hf = (word & 0x4000) != 0, vf = (word & 0x8000) != 0;
        int row = pal >= 2 ? pal - 2 : 5, at = tile * 32;
        if (at + 32 > s.Tiles.Length) return px;
        for (int r = 0; r < 8; r++)
        {
            int sr = vf ? 7 - r : r;
            byte p0 = s.Tiles[at + sr * 2], p1 = s.Tiles[at + sr * 2 + 1], p2 = s.Tiles[at + 0x10 + sr * 2], p3 = s.Tiles[at + 0x11 + sr * 2];
            for (int c = 0; c < 8; c++)
            {
                int bit = hf ? c : 7 - c;
                int ci = (p0 >> bit & 1) | (p1 >> bit & 1) << 1 | (p2 >> bit & 1) << 2 | (p3 >> bit & 1) << 3;
                px[r * 8 + c] = ci == 0 ? 0 : s.Palettes[row][ci];
            }
        }
        return px;
    }

    private static string Key(Sets s, int b) =>
        string.Join("|", Enumerable.Range(0, 4).Select(q => s.Word(b, q) & 0x2000 | 0)) + "/" + s.Attribute(b) + "/" +
        string.Join(",", Enumerable.Range(0, 4).SelectMany(q => Quarter(s, s.Word(b, q))));

    /// <summary>A tile's colour numbers (0-15) with a block word's flips, 64 of them.</summary>
    private static byte[] Indices(byte[] tiles, int word)
    {
        var ix = new byte[64];
        int at = (word & 0x3FF) * 32;
        bool hf = (word & 0x4000) != 0, vf = (word & 0x8000) != 0;
        if (at + 32 > tiles.Length) return ix;
        for (int r = 0; r < 8; r++)
        {
            int sr = vf ? 7 - r : r;
            byte p0 = tiles[at + sr * 2], p1 = tiles[at + sr * 2 + 1], p2 = tiles[at + 0x10 + sr * 2], p3 = tiles[at + 0x11 + sr * 2];
            for (int c = 0; c < 8; c++)
            {
                int bit = hf ? c : 7 - c;
                ix[r * 8 + c] = (byte)((p0 >> bit & 1) | (p1 >> bit & 1) << 1 | (p2 >> bit & 1) << 2 | (p3 >> bit & 1) << 3);
            }
        }
        return ix;
    }

    private static int Distance(uint a, uint b) =>
        Math.Abs((int)(a >> 16 & 0xFF) - (int)(b >> 16 & 0xFF)) + Math.Abs((int)(a >> 8 & 0xFF) - (int)(b >> 8 & 0xFF)) + Math.Abs((int)(a & 0xFF) - (int)(b & 0xFF));

    /// <summary>Most a colour may differ (red + green + blue, 0-765, on average over the tile's pixels) for a tile of this
    /// map's tile set drawn with one of its palette rows to stand for the chest's tile.</summary>
    public const int ColourTolerance = 60;

    /// <summary>How many 8 x 8 tiles the last <see cref="Translate"/> drew with the map's own colours (close to, not the
    /// same as, the chest's).</summary>
    public static int LastRecoloured { get; private set; }

    /// <summary>A word of <paramref name="t"/>'s tile set and palettes that draws source word <paramref name="w"/> of
    /// <paramref name="s"/>: the same shape (the same pixels share a colour; the colour numbers may differ) with the
    /// palette row closest in colour; -1 when no tile has that shape or the colours are too far
    /// (<see cref="ColourTolerance"/>). <c>Exact</c>: the colours are the same.</summary>
    private static (int Word, bool Exact) FindTile(Sets t, Sets s, int w)
    {
        var want = Indices(s.Tiles, w);
        var colours = Quarter(s, w);
        int used = want.Count(i => i != 0);
        int n = t.Tiles.Length / 32, best = -1, bestErr = int.MaxValue;
        var map = new int[16]; var back = new int[16];
        for (int tile = 0; tile < n; tile++)
            for (int flip = 0; flip < 4; flip++)
            {
                int word = tile | (flip & 1) << 14 | (flip >> 1) << 15;
                var have = Indices(t.Tiles, word);
                Array.Fill(map, -1); Array.Fill(back, -1);
                bool ok = true;
                for (int i = 0; i < 64 && ok; i++)
                {
                    int a = want[i], b = have[i];
                    if (a == 0 || b == 0) { ok = a == b; continue; }
                    if (map[a] < 0 && back[b] < 0) { map[a] = b; back[b] = a; }
                    else ok = map[a] == b && back[b] == a;
                }
                if (!ok) continue;
                for (int pal = 2; pal < 8; pal++)
                {
                    var row = t.Palettes[pal - 2];
                    int err = 0;
                    for (int i = 0; i < 64; i++) if (want[i] != 0) err += Distance(row[have[i]], colours[i]);
                    if (err < bestErr) { bestErr = err; best = word | pal << 10; }
                    if (err == 0) return (best, true);
                }
            }
        return best >= 0 && (used == 0 || bestErr <= ColourTolerance * used) ? (best, bestErr == 0) : (-1, false);
    }

    /// <summary>The look's closed and open pictures in <paramref name="edit"/>'s block set (blocks found there, or added to it
    /// in the ROM: <c>Added</c> of them). Throws when a tile of the chest isn't in the map's tile set, or the set is full.</summary>
    public static ((int Block, byte Flags)[] Closed, (int Block, byte Flags)[] Open, int Added) Translate(RomBuffer rom, Look look, L2MapEdit edit)
    {
        int layers = edit.Map.Layers.Length;
        (int, byte)[] Fit((int Block, byte Flags)[] pic) => Enumerable.Range(0, layers).Select(i => i < pic.Length ? pic[i] : (0, (byte)0)).ToArray();
        LastRecoloured = 0;
        if (look.BlockSet == edit.BlockSetIndex && look.TileSet == edit.TileSetIndex && look.PaletteSet == edit.PaletteSetIndex)
            return (Fit(look.Closed), Fit(look.Open), 0);
        var src = Load(rom, look.Map, look.BlockSet, look.TileSet, look.PaletteSet);
        var dst = Load(rom, edit.MapId, edit.BlockSetIndex, edit.TileSetIndex, edit.PaletteSetIndex);
        int count = dst.Count;
        if (dst.BlockSet.Length != 0x10 + count * 9) throw new InvalidOperationException($"Block set {edit.BlockSetIndex} isn't laid out as the game's are, so blocks can't be added to it.");
        var have = new Dictionary<string, int>();
        for (int b = count - 1; b >= 0; b--) have[Key(dst, b)] = b;
        var newWords = new List<int[]>();
        var newAttr = new List<byte>();
        var done = new Dictionary<int, int>();
        int recoloured = 0;
        int Map1(int block)
        {
            if (block == 0) return 0;   // nothing there
            if (done.TryGetValue(block, out int d)) return d;
            if (block >= src.Count) throw new InvalidOperationException($"That chest's block {block:X3} isn't in its own block set.");
            string key = Key(src, block);
            if (have.TryGetValue(key, out int found)) return done[block] = found;
            // a new block of this map's tiles
            var words = new int[4];
            for (int q = 0; q < 4; q++)
            {
                int w = src.Word(block, q);
                var (tw, exact) = FindTile(dst, src, w);
                if (tw < 0) throw new InvalidOperationException($"This map's tile set ({edit.TileSetIndex}) doesn't have the tiles of that chest (nor tiles of its shape in colours close to it), so its picture can't be drawn here: pick a chest of a map with the same graphics (they come first in the list).");
                if (!exact) recoloured++;
                words[q] = tw | w & 0x2000;
            }
            // a block of the set made the same way already
            for (int b = 0; b < count; b++)
                if (dst.Attribute(b) == src.Attribute(block) && Enumerable.Range(0, 4).All(q => dst.Word(b, q) == words[q])) { have[key] = b; return done[block] = b; }
            if (count + newWords.Count >= MaxBlocks) throw new InvalidOperationException($"Block set {edit.BlockSetIndex} is full ({MaxBlocks} blocks), so the chest's blocks can't be added.");
            int nb = count + newWords.Count;
            newWords.Add(words); newAttr.Add(src.Attribute(block));
            have[key] = nb;
            return done[block] = nb;
        }
        (int, byte)[] Pic((int Block, byte Flags)[] pic) => Enumerable.Range(0, layers).Select(i =>
            i < pic.Length && L2MapEdit.CopiesLayer(look.Layers, i) ? (Map1(pic[i].Block), pic[i].Flags) : (0, (byte)0)).ToArray();
        var closed = Pic(look.Closed);
        var open = Pic(look.Open);
        if (newWords.Count > 0)
        {
            int n = count + newWords.Count;
            var f = new List<byte>(dst.BlockSet.Take(0x10 + count * 8));
            foreach (var w in newWords) foreach (int x in w) { f.Add((byte)x); f.Add((byte)(x >> 8)); }
            f.AddRange(dst.BlockSet.Skip(0x10 + count * 8).Take(count));
            f.AddRange(newAttr);
            f[2] = (byte)n; f[3] = (byte)(n >> 8);
            int size = 0x10 + n * 8;
            f[4] = (byte)size; f[5] = (byte)(size >> 8);
            L2Rom.WriteFile(rom, L2Maps.BlockSetFileBase + edit.BlockSetIndex, f.ToArray());
        }
        LastRecoloured = recoloured;
        return (closed, open, newWords.Count);
    }

    // ---------------------------------------------------------------- where the pictures are kept

    /// <summary>Two blocks side by side (the left one is returned) that the camera never shows: black on both layers, and
    /// out of the 16 x 14 block screen (one more around for scrolling) wherever the party stands on a block that isn't
    /// black. Top-left first, as the game keeps its own. When there are none, the black pair seen least (Seen = true).
    /// (-1, -1) when the map has no black pair at all.</summary>
    public static (int X, int Y, bool Seen) Stash(RomBuffer rom, L2MapEdit edit, int notX, int notY)
    {
        int w = edit.Width, h = edit.Height;
        var black = new bool[w * h];
        var pics = L2Maps.AllBlocks(rom, edit.ForDrawing);
        bool Dark(int b) => b == 0 || b >= pics.Length || pics[b].All(c => c == 0 || (c & 0xF8F8F8) == 0);
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
                black[y * w + x] = edit.Map.Layers.All(L => x >= L.Width || y >= L.Height || Dark(L.Blocks[y * L.Width + x]));
        // a block is seen when some not-black block's screen covers it
        var seen = new int[(w + 1) * (h + 1)];
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
            {
                if (black[y * w + x]) continue;
                int cx = Math.Clamp(x - 8, 0, Math.Max(0, w - 16)), cy = Math.Clamp(y - 7, 0, Math.Max(0, h - 14));
                int x0 = Math.Max(0, cx - 1), y0 = Math.Max(0, cy - 1), x1 = Math.Min(w, cx + 17), y1 = Math.Min(h, cy + 15);
                seen[y0 * (w + 1) + x0]++; seen[y0 * (w + 1) + x1]--; seen[y1 * (w + 1) + x0]--; seen[y1 * (w + 1) + x1]++;
            }
        for (int y = 0; y <= h; y++) for (int x = 1; x <= w; x++) seen[y * (w + 1) + x] += seen[y * (w + 1) + x - 1];
        for (int y = 1; y <= h; y++) for (int x = 0; x <= w; x++) seen[y * (w + 1) + x] += seen[(y - 1) * (w + 1) + x];
        var used = edit.Records(L2MapEdit.Treasure).Select(r => (r.Bytes[1], r.Bytes[2])).ToHashSet();
        foreach (var (_, l) in edit.Records(L2MapEdit.Looks)) { used.Add((l[2], l[3])); used.Add((l[6], l[7])); }
        (int X, int Y, int Seen) best = (-1, -1, int.MaxValue);
        for (int y = 0; y < h; y++)
            for (int x = 0; x + 1 < w; x++)
            {
                if (!black[y * w + x] || !black[y * w + x + 1] || x == notX && y == notY || x + 1 == notX && y == notY) continue;
                if (used.Contains(((byte)x, (byte)y)) || used.Contains(((byte)(x + 1), (byte)y))) continue;
                int s = seen[y * (w + 1) + x] + seen[y * (w + 1) + x + 1];
                if (s < best.Seen) best = (x, y, s);
                if (s == 0) return (x, y, false);
            }
        return (best.X, best.Y, best.X >= 0);
    }

    // ---------------------------------------------------------------- pictures for the list

    /// <summary>The look as a 36 x 16 picture: the closed chest, then the open one (both layers, as on its map).</summary>
    public static (int Width, int Height, uint[] Pixels) Picture(RomBuffer rom, Look look)
    {
        var s = Load(rom, look.Map, look.BlockSet, look.TileSet, look.PaletteSet);
        var px = new uint[36 * 16];
        void Draw((int Block, byte Flags)[] pic, int x0)
        {
            foreach (var (b, _) in pic)
            {
                if (b == 0 || b >= s.Count) continue;
                for (int q = 0; q < 4; q++)
                {
                    var t = Quarter(s, s.Word(b, q));
                    int qx = q >= 2 ? 8 : 0, qy = (q & 1) * 8;
                    for (int i = 0; i < 64; i++) if (t[i] != 0) px[(qy + i / 8) * 36 + x0 + qx + i % 8] = t[i];
                }
            }
        }
        Draw(look.Closed, 0);
        var open = look.Open.Select((o, i) => L2MapEdit.CopiesLayer(look.Layers, i) ? o : i < look.Closed.Length ? look.Closed[i] : o).ToArray();
        Draw(open, 20);
        return (36, 16, px);
    }
}
