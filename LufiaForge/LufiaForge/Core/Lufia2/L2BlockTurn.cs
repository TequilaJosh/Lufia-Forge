namespace LufiaForge.Core.Lufia2;

// turned blocks: a block flipped across or up/down, or turned a quarter, as another block of the map's block set.
// a map cell can't turn its block, so the turned look has to be a block of its own: the four tiles move to their
// turned places and each tile turns too. flips and half turns are just the tile words' own flip bits (no new tiles).
// a quarter turn draws the tile's pixels turned, which is a tile of the set if one looks the same (maybe flipped),
// else a new tile in a tile nothing draws (L2ChestLooks.FreeTiles). a block of the set made the same way gets used
// as is, otherwise we add one at the end (with the block's attribute byte). other maps with the set keep their
// blocks and numbers
public static class L2BlockTurn
{
    // where (x, y) of a w x h grid goes: flipped across first if flip, then quarters quarter turns clockwise
    public static (int X, int Y) Point(int x, int y, int w, int h, int quarters, bool flip)
    {
        if (flip) x = w - 1 - x;
        for (int i = 0; i < (quarters & 3); i++) { (x, y) = (h - 1 - y, x); (w, h) = (h, w); }
        return (x, y);
    }

    // the size after the turn
    public static (int W, int H) Size(int w, int h, int quarters) => (quarters & 1) == 0 ? (w, h) : (h, w);

    public sealed record Prepared(int[] Blocks, int Added, int NewTiles, Action Write);

    // blocks of the map's block set turned (block 0, "nothing here", stays 0), worked out without writing anything.
    // Prepared.Write puts the new tiles and blocks in
    public static Prepared Prepare(RomBuffer rom, int map, int blockSet, int tileSet, IReadOnlyList<int> blocks, int quarters, bool flip)
    {
        quarters &= 3;
        var blockSetFile = L2Rom.ReadFile(rom, L2Maps.BlockSetFileBase + blockSet);
        var tiles = L2Rom.ReadFile(rom, L2Maps.TileSetFileBase + tileSet);
        int count = (short)(blockSetFile[2] | blockSetFile[3] << 8);
        if (blockSetFile.Length < 0x10 + count * 9) throw new InvalidOperationException($"Block set {blockSet} isn't laid out as the game's are, so turned blocks can't be added to it.");
        bool animates = L2ChestLooks.HasTileAnimation(rom, map);
        int Word(int block, int quarter) => blockSetFile[0x10 + block * 8 + quarter * 2] | blockSetFile[0x10 + block * 8 + quarter * 2 + 1] << 8;
        byte Attr(int block) => blockSetFile[0x10 + count * 8 + block];
        var newWords = new List<int[]>();
        var newAttr = new List<byte>();
        List<int>? free = null;
        var taken = new HashSet<int>();
        int newTiles = 0;
        int tileCount() => tiles.Length / 32;

        // one tile's 64 colour numbers drawn with a word's flips
        byte[] Pixels(int word)
        {
            var colors = new byte[64];
            int tileAt = (word & 0x3FF) * 32;
            if (tileAt + 32 > tiles.Length) return colors;
            bool flipX = (word & 0x4000) != 0, flipY = (word & 0x8000) != 0;
            for (int row = 0; row < 8; row++)
            {
                int sourceRow = flipY ? 7 - row : row;
                byte p0 = tiles[tileAt + sourceRow * 2], p1 = tiles[tileAt + sourceRow * 2 + 1], p2 = tiles[tileAt + 0x10 + sourceRow * 2], p3 = tiles[tileAt + 0x11 + sourceRow * 2];
                for (int col = 0; col < 8; col++)
                {
                    int bit = flipX ? col : 7 - col;
                    colors[row * 8 + col] = (byte)((p0 >> bit & 1) | (p1 >> bit & 1) << 1 | (p2 >> bit & 1) << 2 | (p3 >> bit & 1) << 3);
                }
            }
            return colors;
        }
        byte[] Encode(byte[] colors)
        {
            var tile = new byte[32];
            for (int i = 0; i < 64; i++)
            {
                int color = colors[i], row = i / 8, bit = 7 - i % 8;
                if ((color & 1) != 0) tile[row * 2] |= (byte)(1 << bit);
                if ((color & 2) != 0) tile[row * 2 + 1] |= (byte)(1 << bit);
                if ((color & 4) != 0) tile[0x10 + row * 2] |= (byte)(1 << bit);
                if ((color & 8) != 0) tile[0x11 + row * 2] |= (byte)(1 << bit);
            }
            return tile;
        }
        // a tile's pixels turned: a word of the set that draws them (palette row and priority kept)
        var turnedTiles = new Dictionary<int, int>();
        int TurnTile(int word)
        {
            int keep = word & 0x3C00 | word & 0x2000;
            if ((quarters & 1) == 0)
            {
                // flips and half turns are the word's own flip bits
                int flipped = word;
                if (flip) flipped ^= 0x4000;
                if (quarters == 2) flipped ^= 0xC000;
                return flipped;
            }
            int key = word & 0xC3FF;
            if (turnedTiles.TryGetValue(key, out int done)) return done | keep;
            var source = Pixels(word & 0xC3FF);
            var turned = new byte[64];
            for (int y = 0; y < 8; y++)
                for (int x = 0; x < 8; x++)
                {
                    var (nx, ny) = Point(x, y, 8, 8, quarters, flip);
                    turned[ny * 8 + nx] = source[y * 8 + x];
                }
            // a tile of the set (or one we added just now) that draws it, maybe flipped
            for (int tile = 0; tile < tileCount(); tile++)
            {
                if (animates && tile >= 1 && tile <= L2ChestLooks.LastAnimatedTile) continue;
                for (int flips = 0; flips < 4; flips++)
                {
                    int candidate = tile | (flips & 1) << 14 | (flips >> 1) << 15;
                    if (Pixels(candidate).AsSpan().SequenceEqual(turned)) { taken.Add(tile); return (turnedTiles[key] = candidate) | keep; }
                }
            }
            free ??= L2ChestLooks.FreeTiles(rom, tileSet);
            int slot = free.FirstOrDefault(s => !taken.Contains(s), -1);
            if (slot < 0) throw new InvalidOperationException($"Tile set {tileSet} has no free tile for the turned tiles: every tile of the 1024 it can have is drawn by a block (or is one of tiles 1-15, which the tile animation draws over).");
            if ((slot + 1) * 32 > tiles.Length) { var grown = new byte[(slot + 1) * 32]; Array.Copy(tiles, grown, tiles.Length); tiles = grown; }
            Array.Copy(Encode(turned), 0, tiles, slot * 32, 32);
            taken.Add(slot); newTiles++;
            return (turnedTiles[key] = slot) | keep;
        }

        string Look(int[] words) => string.Join("|", words.Select(w => (w & 0x3C00).ToString("X4") + Convert.ToHexString(Pixels(w))));
        var looks = new Dictionary<int, string>();
        string BlockLook(int block) => looks.TryGetValue(block, out var look) ? look : looks[block] = Look(Enumerable.Range(0, 4).Select(q => Word(block, q)).ToArray());
        var turnedBlocks = new Dictionary<int, int>();
        int TurnBlock(int block)
        {
            if (block == 0) return 0;
            if (turnedBlocks.TryGetValue(block, out int done)) return done;
            if (block >= count) throw new InvalidOperationException($"Block {block:X3} isn't in block set {blockSet}.");
            if (quarters == 0 && !flip) return turnedBlocks[block] = block;
            // the quarters (top left, bottom left, top right, bottom right) move to their turned places
            var words = new int[4];
            for (int q = 0; q < 4; q++)
            {
                var (nx, ny) = Point(q >> 1, q & 1, 2, 2, quarters, flip);
                words[nx * 2 + ny] = TurnTile(Word(block, q));
            }
            byte attr = Attr(block);
            // a block of the set that looks the same (same pixels, colour rows and priority, a tile can be another one
            // with the same picture, or flipped where that changes nothing), so turning four times comes back to the block
            string wanted = Look(words);
            for (int k = 0; k < count; k++)
                if (Attr(k) == attr && BlockLook(k) == wanted) return turnedBlocks[block] = k;
            for (int k = 0; k < newWords.Count; k++)
                if (newAttr[k] == attr && Look(newWords[k]) == wanted) return turnedBlocks[block] = count + k;
            if (count + newWords.Count >= 0x400) throw new InvalidOperationException($"Block set {blockSet} is full (1024 blocks), so the turned blocks can't be added.");
            newWords.Add(words); newAttr.Add(attr);
            return turnedBlocks[block] = count + newWords.Count - 1;
        }

        var result = blocks.Select(TurnBlock).ToArray();
        byte[]? blockFile = null;
        if (newWords.Count > 0)
        {
            int newCount = count + newWords.Count;
            var file = new List<byte>(blockSetFile.Take(0x10 + count * 8));
            foreach (var words in newWords) foreach (int word in words) { file.Add((byte)word); file.Add((byte)(word >> 8)); }
            file.AddRange(blockSetFile.Skip(0x10 + count * 8).Take(count));
            file.AddRange(newAttr);
            file[2] = (byte)newCount; file[3] = (byte)(newCount >> 8);
            int size = 0x10 + newCount * 8;
            file[4] = (byte)size; file[5] = (byte)(size >> 8);
            blockFile = file.ToArray();
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

    // a cell's wall flags (04 = wall on the top edge, 08 = on the left edge) after the turn. each wall is an edge
    // between two cells, which turns with the piece. a wall that lands on the bottom or right edge of a cell is the
    // top or left wall of the cell below or right of it. flags are the piece's flags (row by row), the other bits
    // move with their cells. walls that end up outside the piece are left out
    public static byte[] TurnFlags(byte[] flags, int w, int h, int quarters, bool flip)
    {
        var (newWidth, newHeight) = Size(w, h, quarters);
        var turned = new byte[newWidth * newHeight];
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
            {
                var (nx, ny) = Point(x, y, w, h, quarters, flip);
                turned[ny * newWidth + nx] |= (byte)(flags[y * w + x] & ~0x0C);
            }
        // an edge as the two cells it lies between (one can be just outside the piece), turned: the new edge between
        // them. a wall on a bottom or right edge belongs to a cell outside the piece and is left out
        void Edge(int ax, int ay, int bx, int by)
        {
            var (px, py) = Point(ax, ay, w, h, quarters, flip);
            var (qx, qy) = Point(bx, by, w, h, quarters, flip);
            if (px == qx) { int lower = Math.Max(py, qy); if (lower >= 0 && lower < newHeight && px >= 0 && px < newWidth) turned[lower * newWidth + px] |= 0x04; }
            else { int right = Math.Max(px, qx); if (right >= 0 && right < newWidth && py >= 0 && py < newHeight) turned[py * newWidth + right] |= 0x08; }
        }
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
            {
                byte cellFlags = flags[y * w + x];
                if ((cellFlags & 0x04) != 0) Edge(x, y - 1, x, y);
                if ((cellFlags & 0x08) != 0) Edge(x - 1, y, x, y);
            }
        return turned;
    }
}
