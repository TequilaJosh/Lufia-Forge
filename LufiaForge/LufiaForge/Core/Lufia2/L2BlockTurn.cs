namespace LufiaForge.Core.Lufia2;

/// <summary>
/// Turned blocks: a block flipped across or up/down, or turned a quarter, as another block of the map's block set. A map
/// cell can't turn its block, so the turned look is a block of its own: the four tiles move to their turned places and
/// each tile turns too. Flips and half turns are the tile words' own flip bits (no new tiles); a quarter turn draws the
/// tile's pixels turned, which is a tile of the set when one looks the same (maybe flipped), else a new tile in a tile
/// nothing draws (<see cref="L2ChestLooks.FreeTiles"/>). A block of the set made the same way is used as it is; otherwise
/// one is added at the end (with the block's attribute byte). Other maps with the set keep their blocks and numbers.
/// </summary>
public static class L2BlockTurn
{
    /// <summary>Where (x, y) of a <paramref name="w"/> x <paramref name="h"/> grid goes: flipped across first when
    /// <paramref name="flip"/>, then <paramref name="quarters"/> quarter turns clockwise.</summary>
    public static (int X, int Y) Point(int x, int y, int w, int h, int quarters, bool flip)
    {
        if (flip) x = w - 1 - x;
        for (int i = 0; i < (quarters & 3); i++) { (x, y) = (h - 1 - y, x); (w, h) = (h, w); }
        return (x, y);
    }

    /// <summary>The size after the turn.</summary>
    public static (int W, int H) Size(int w, int h, int quarters) => (quarters & 1) == 0 ? (w, h) : (h, w);

    public sealed record Prepared(int[] Blocks, int Added, int NewTiles, Action Write);

    /// <summary>Blocks <paramref name="blocks"/> of the map's block set turned (block 0, "nothing here", stays 0), worked out
    /// without writing; <see cref="Prepared.Write"/> puts the new tiles and blocks in.</summary>
    public static Prepared Prepare(RomBuffer rom, int map, int blockSet, int tileSet, IReadOnlyList<int> blocks, int quarters, bool flip)
    {
        quarters &= 3;
        var bsFile = L2Rom.ReadFile(rom, L2Maps.BlockSetFileBase + blockSet);
        var tiles = L2Rom.ReadFile(rom, L2Maps.TileSetFileBase + tileSet);
        int count = (short)(bsFile[2] | bsFile[3] << 8);
        if (bsFile.Length < 0x10 + count * 9) throw new InvalidOperationException($"Block set {blockSet} isn't laid out as the game's are, so turned blocks can't be added to it.");
        bool animates = L2ChestLooks.HasTileAnimation(rom, map);
        int Word(int b, int q) => bsFile[0x10 + b * 8 + q * 2] | bsFile[0x10 + b * 8 + q * 2 + 1] << 8;
        byte Attr(int b) => bsFile[0x10 + count * 8 + b];
        var newWords = new List<int[]>();
        var newAttr = new List<byte>();
        List<int>? free = null;
        var taken = new HashSet<int>();
        int newTiles = 0;
        int tileCount() => tiles.Length / 32;

        // one tile's 64 colour numbers drawn with a word's flips
        byte[] Pixels(int word)
        {
            var ix = new byte[64];
            int at = (word & 0x3FF) * 32;
            if (at + 32 > tiles.Length) return ix;
            bool hf = (word & 0x4000) != 0, vf = (word & 0x8000) != 0;
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
        byte[] Encode(byte[] ix)
        {
            var t = new byte[32];
            for (int i = 0; i < 64; i++)
            {
                int v = ix[i], r = i / 8, bit = 7 - i % 8;
                if ((v & 1) != 0) t[r * 2] |= (byte)(1 << bit);
                if ((v & 2) != 0) t[r * 2 + 1] |= (byte)(1 << bit);
                if ((v & 4) != 0) t[0x10 + r * 2] |= (byte)(1 << bit);
                if ((v & 8) != 0) t[0x11 + r * 2] |= (byte)(1 << bit);
            }
            return t;
        }
        // a tile's pixels turned: a word of the set that draws them (palette row and priority kept)
        var turnedTiles = new Dictionary<int, int>();
        int TurnTile(int word)
        {
            int keep = word & 0x3C00 | word & 0x2000;
            if ((quarters & 1) == 0)
            {
                // flips and half turns are the word's own flip bits
                int w = word;
                if (flip) w ^= 0x4000;
                if (quarters == 2) w ^= 0xC000;
                return w;
            }
            int key = word & 0xC3FF;
            if (turnedTiles.TryGetValue(key, out int done)) return done | keep;
            var src = Pixels(word & 0xC3FF);
            var dst = new byte[64];
            for (int y = 0; y < 8; y++)
                for (int x = 0; x < 8; x++)
                {
                    var (nx, ny) = Point(x, y, 8, 8, quarters, flip);
                    dst[ny * 8 + nx] = src[y * 8 + x];
                }
            // a tile of the set (or one added now) that draws it, maybe flipped
            for (int t = 0; t < tileCount(); t++)
            {
                if (animates && t >= 1 && t <= L2ChestLooks.LastAnimatedTile) continue;
                for (int f = 0; f < 4; f++)
                {
                    int w = t | (f & 1) << 14 | (f >> 1) << 15;
                    if (Pixels(w).AsSpan().SequenceEqual(dst)) { taken.Add(t); return (turnedTiles[key] = w) | keep; }
                }
            }
            free ??= L2ChestLooks.FreeTiles(rom, tileSet);
            int slot = free.FirstOrDefault(s => !taken.Contains(s), -1);
            if (slot < 0) throw new InvalidOperationException($"Tile set {tileSet} has no free tile for the turned tiles: every tile of the 1024 it can have is drawn by a block (or is one of tiles 1-15, which the tile animation draws over).");
            if ((slot + 1) * 32 > tiles.Length) { var grown = new byte[(slot + 1) * 32]; Array.Copy(tiles, grown, tiles.Length); tiles = grown; }
            Array.Copy(Encode(dst), 0, tiles, slot * 32, 32);
            taken.Add(slot); newTiles++;
            return (turnedTiles[key] = slot) | keep;
        }

        string Look(int[] words) => string.Join("|", words.Select(w => (w & 0x3C00).ToString("X4") + Convert.ToHexString(Pixels(w))));
        var looks = new Dictionary<int, string>();
        string BlockLook(int k) => looks.TryGetValue(k, out var l) ? l : looks[k] = Look(Enumerable.Range(0, 4).Select(q => Word(k, q)).ToArray());
        var turned = new Dictionary<int, int>();
        int TurnBlock(int b)
        {
            if (b == 0) return 0;
            if (turned.TryGetValue(b, out int d)) return d;
            if (b >= count) throw new InvalidOperationException($"Block {b:X3} isn't in block set {blockSet}.");
            if (quarters == 0 && !flip) return turned[b] = b;
            // the quarters (top left, bottom left, top right, bottom right) move to their turned places
            var words = new int[4];
            for (int q = 0; q < 4; q++)
            {
                var (nx, ny) = Point(q >> 1, q & 1, 2, 2, quarters, flip);
                words[nx * 2 + ny] = TurnTile(Word(b, q));
            }
            byte attr = Attr(b);
            // a block of the set that looks the same (the same pixels, colour rows and priority; a tile may be another
            // with the same picture, or flipped where that changes nothing), so turning four times comes back to the block
            string want = Look(words);
            for (int k = 0; k < count; k++)
                if (Attr(k) == attr && BlockLook(k) == want) return turned[b] = k;
            for (int k = 0; k < newWords.Count; k++)
                if (newAttr[k] == attr && Look(newWords[k]) == want) return turned[b] = count + k;
            if (count + newWords.Count >= 0x400) throw new InvalidOperationException($"Block set {blockSet} is full (1024 blocks), so the turned blocks can't be added.");
            newWords.Add(words); newAttr.Add(attr);
            return turned[b] = count + newWords.Count - 1;
        }

        var result = blocks.Select(TurnBlock).ToArray();
        byte[]? blockFile = null;
        if (newWords.Count > 0)
        {
            int n = count + newWords.Count;
            var f = new List<byte>(bsFile.Take(0x10 + count * 8));
            foreach (var w in newWords) foreach (int x in w) { f.Add((byte)x); f.Add((byte)(x >> 8)); }
            f.AddRange(bsFile.Skip(0x10 + count * 8).Take(count));
            f.AddRange(newAttr);
            f[2] = (byte)n; f[3] = (byte)(n >> 8);
            int size = 0x10 + n * 8;
            f[4] = (byte)size; f[5] = (byte)(size >> 8);
            blockFile = f.ToArray();
        }
        var tileFile = newTiles > 0 ? tiles : null;
        return new Prepared(result, newWords.Count, newTiles, () =>
        {
            var write = new List<(int, byte[])>();
            if (tileFile != null) write.Add((L2Maps.TileSetFileBase + tileSet, tileFile));
            if (blockFile != null) write.Add((L2Maps.BlockSetFileBase + blockSet, blockFile));
            L2Rom.WriteFiles(rom, write.ToArray());
        });
    }

    /// <summary>A cell's wall flags (04 a wall on the top edge, 08 on the left edge) after the turn: each wall is an edge
    /// between two cells, which turns with the piece; a wall that lands on the bottom or right edge of a cell is the top or
    /// left wall of the cell below or right of it. <paramref name="flags"/> are the piece's flags (row by row); the other
    /// bits move with their cells. Walls that end up outside the piece are left out.</summary>
    public static byte[] TurnFlags(byte[] flags, int w, int h, int quarters, bool flip)
    {
        var (nw, nh) = Size(w, h, quarters);
        var o = new byte[nw * nh];
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
            {
                var (nx, ny) = Point(x, y, w, h, quarters, flip);
                o[ny * nw + nx] |= (byte)(flags[y * w + x] & ~0x0C);
            }
        // an edge as the two cells it lies between (one may be just outside the piece), turned: the new edge between
        // them; a wall on a bottom or right edge belongs to a cell outside the piece and is left out
        void Edge(int ax, int ay, int bx, int by)
        {
            var (px, py) = Point(ax, ay, w, h, quarters, flip);
            var (qx, qy) = Point(bx, by, w, h, quarters, flip);
            if (px == qx) { int lo = Math.Max(py, qy); if (lo >= 0 && lo < nh && px >= 0 && px < nw) o[lo * nw + px] |= 0x04; }
            else { int ri = Math.Max(px, qx); if (ri >= 0 && ri < nw && py >= 0 && py < nh) o[py * nw + ri] |= 0x08; }
        }
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
            {
                byte f = flags[y * w + x];
                if ((f & 0x04) != 0) Edge(x, y - 1, x, y);
                if ((f & 0x08) != 0) Edge(x - 1, y, x, y);
            }
        return o;
    }
}
