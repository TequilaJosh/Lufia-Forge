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
/// of that tile set's tiles (any palette row and flip that draws the same colours); a tile the tile set doesn't have is
/// copied into it, in the map's closest colours, on a tile nothing draws (towns have no chest tiles).
/// <see cref="Stash"/> finds two blocks side by side the camera never shows, for the open and
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
        /// <summary>The map has a tile animation (tiles 1-15 are drawn over in VRAM).</summary>
        public bool Animates;
        /// <summary>The colours (CGRAM 0-127) the map's colour animation changes.</summary>
        public bool[] Cycling = new bool[128];
    }

    private static Sets Load(RomBuffer rom, int map, int bs, int ts, int ps) => new()
    {
        BlockSet = L2Rom.ReadFile(rom, L2Maps.BlockSetFileBase + bs), Tiles = L2Rom.ReadFile(rom, L2Maps.TileSetFileBase + ts),
        Palettes = L2Maps.Palettes(rom, map, ps), Animates = HasTileAnimation(rom, map), Cycling = CyclingColours(rom, map),
    };

    // ---------------------------------------------------------------- what the game draws over

    /// <summary>The last tile a map's tile animation draws over: $83:AF54 copies 0x1E0 bytes (15 tiles) of the frame to VRAM
    /// $4010, which is where the tile set's tiles 1-15 are (the tile set goes to VRAM $4000, $80:F05C).</summary>
    public const int LastAnimatedTile = 15;

    /// <summary>The most tiles a tile set can have: the loader unpacks it to $7E:4000 (the tile animation's frames start at
    /// $7E:C000) and copies it to VRAM $4000-$7FFF, the 1024 tiles a block's 10-bit tile number reaches.</summary>
    public const int MaxTiles = 0x400;

    /// <summary>Whether map <paramref name="map"/> has a tile animation (water and the like): its $CF:F90E entry's
    /// $CF:F9FE byte 0 high nibble picks file 0x15F + n (0 = none).</summary>
    public static bool HasTileAnimation(RomBuffer rom, int map) =>
        rom.ReadByte(L2Maps.AnimData + rom.ReadByte(L2Maps.AnimIndexList + map) * 2) >> 4 != 0;

    /// <summary>The colours (CGRAM numbers 0-127) map <paramref name="map"/>'s colour animation changes: $CF:FBA6 + map
    /// (1-based, 0 = none) picks a $CF:FC98 pointer to a record in bank $A1 ("PS", count | 80, then count u16 offsets
    /// from the record to entries: the colour number, then duration and colour pairs up to a 00). Checked in BizHawk on
    /// Elcid: colours 97-101 change, the five entries of record 1.</summary>
    public static bool[] CyclingColours(RomBuffer rom, int map)
    {
        var c = new bool[128];
        int n = rom.ReadByte(0x27FBA6 + map);
        if (n == 0 || n > 0x12) return c;
        int at = L2Rom.SnesToFile(0xA10000 | rom.ReadUInt16Le(0x27FC98 + (n - 1) * 2));
        if (rom.ReadByte(at) != 0x50 || rom.ReadByte(at + 1) != 0x53) return c;
        int count = rom.ReadByte(at + 2) & 0x7F;
        for (int i = 0; i < count; i++)
        {
            int col = rom.ReadByte(at + rom.ReadUInt16Le(at + 3 + i * 2));
            if (col < 128) c[col] = true;
        }
        return c;
    }

    /// <summary>Tiles of tile set <paramref name="tileSet"/> that nothing draws: not tile 0, not tiles 1-15 (the tile
    /// animation's), and used by no block of any block set the game's maps (or the Ancient Cave) pair with this tile set,
    /// first those inside the set, then the slots past its end up to <see cref="MaxTiles"/> (the set grows into them).</summary>
    public static List<int> FreeTiles(RomBuffer rom, int tileSet)
    {
        int count = L2Rom.ReadFile(rom, L2Maps.TileSetFileBase + tileSet).Length / 32;
        var used = new bool[MaxTiles];
        foreach (int bs in PairedBlockSets(rom, tileSet))
        {
            var f = L2Rom.ReadFile(rom, L2Maps.BlockSetFileBase + bs);
            int n = f.Length >= 4 ? (short)(f[2] | f[3] << 8) : 0;
            for (int b = 0; b < n && 0x10 + b * 8 + 8 <= f.Length; b++)
                for (int q = 0; q < 4; q++) used[(f[0x10 + b * 8 + q * 2] | f[0x11 + b * 8 + q * 2] << 8) & 0x3FF] = true;
        }
        return Enumerable.Range(LastAnimatedTile + 1, MaxTiles - LastAnimatedTile - 1).Where(i => !used[i]).OrderBy(i => i < count ? 0 : 1).ThenBy(i => i).ToList();
    }

    /// <summary>The block sets drawn with tile set <paramref name="tileSet"/>: those of the maps that use it, and the Ancient
    /// Cave's ($83:9F49 / $83:9F5F: block sets 0B, 09, 04 with tile sets 0B, 09, 04).</summary>
    public static List<int> PairedBlockSets(RomBuffer rom, int tileSet)
    {
        var sets = Enumerable.Range(0, L2Maps.Count).Where(m => rom.ReadByte(L2Maps.TileSetList + m) == tileSet)
            .Select(m => (int)rom.ReadByte(L2Maps.BlockSetList + m)).ToHashSet();
        if (tileSet is 0x0B or 0x09 or 0x04) sets.Add(tileSet);
        return sets.OrderBy(s => s).ToList();
    }

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
        {
            if (t.Animates && tile >= 1 && tile <= LastAnimatedTile) continue;   // the tile animation draws over these
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
                    // a colour the map's colour animation changes would make the chest flicker
                    if (Enumerable.Range(0, 64).Any(i => have[i] != 0 && t.Cycling[pal * 16 + have[i]])) continue;
                    int err = 0;
                    for (int i = 0; i < 64; i++) if (want[i] != 0) err += Distance(row[have[i]], colours[i]);
                    if (err < bestErr) { bestErr = err; best = word | pal << 10; }
                    if (err == 0) return (best, true);
                }
            }
        }
        return best >= 0 && (used == 0 || bestErr <= ColourTolerance * used) ? (best, bestErr == 0) : (-1, false);
    }

    /// <summary>Tiles <see cref="Translate"/> put into a tile set: their numbers, the colour rows (2-7) they are drawn with,
    /// the set's tile count before and after, and how far their colours are from the chest's (red + green + blue, 0-765,
    /// on average per pixel).</summary>
    public sealed record TilesAdded(int TileSet, int[] Tiles, int[] Rows, int CountBefore, int CountAfter, int Off);

    /// <summary>What the last <see cref="Translate"/> added to the map's tile set, or null.</summary>
    public static TilesAdded? LastTiles { get; private set; }

    /// <summary>The look's closed and open pictures in <paramref name="edit"/>'s block set (blocks found there, or added to it
    /// in the ROM: <c>Added</c> of them). A tile of the chest that the map's tile set doesn't have (nor one of its shape in
    /// close colours) is copied into it: recoloured to the map's colour row closest to the chest's colours (one row per
    /// row of the chest; colours the map's colour animation changes are left out) and put in a tile nothing draws
    /// (<see cref="FreeTiles"/>), the set written back (<see cref="LastTiles"/>). Nothing is written when it throws: no
    /// free tile, or the block set is full.</summary>
    public static ((int Block, byte Flags)[] Closed, (int Block, byte Flags)[] Open, int Added) Translate(RomBuffer rom, Look look, L2MapEdit edit)
    {
        var p = Prepare(rom, look, edit);
        p.Write();
        return (p.Closed, p.Open, p.Added);
    }

    /// <summary>A <see cref="Translate"/> worked out but not written yet: <see cref="Write"/> puts the tiles and blocks
    /// into the ROM (nothing when none are added).</summary>
    public sealed record Prepared((int Block, byte Flags)[] Closed, (int Block, byte Flags)[] Open, int Added, Action Write);

    /// <summary><see cref="Translate"/> without writing: the caller writes (<see cref="Prepared.Write"/>) once nothing else
    /// can stop the chest, so a refused chest leaves the ROM as it was.</summary>
    public static Prepared Prepare(RomBuffer rom, Look look, L2MapEdit edit)
    {
        int layers = edit.Map.Layers.Length;
        (int, byte)[] Fit((int Block, byte Flags)[] pic) => Enumerable.Range(0, layers).Select(i => i < pic.Length ? pic[i] : (0, (byte)0)).ToArray();
        LastRecoloured = 0;
        LastTiles = null;
        if (look.BlockSet == edit.BlockSetIndex && look.TileSet == edit.TileSetIndex && look.PaletteSet == edit.PaletteSetIndex)
            return new Prepared(Fit(look.Closed), Fit(look.Open), 0, () => { });
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

        // ---- tiles the map's tile set doesn't have: the chest's own, in this map's colours, put in tiles nothing draws
        int oldTiles = dst.Tiles.Length / 32;
        List<int>? free = null;
        var addedTiles = new List<int>();
        var taken = new HashSet<int>();
        var rowFor = new Dictionary<int, (int Pal, int[] Map)>();
        long offSum = 0, offPixels = 0;
        // the source rows' colours as the chest uses them (pixels per colour number), to pick one row of this map for each
        var chestBlocks = look.Closed.Concat(look.Open).Select((c, i) => (c.Block, Layer: i % Math.Max(1, look.Closed.Length)))
            .Where(c => c.Block != 0 && c.Block < src.Count && L2MapEdit.CopiesLayer(look.Layers, c.Layer)).Select(c => c.Block).Distinct().ToList();
        (int Pal, int[] Map) RowFor(int srcPal)
        {
            if (rowFor.TryGetValue(srcPal, out var r)) return r;
            var counts = new long[16];
            foreach (int b in chestBlocks)
                for (int q = 0; q < 4; q++)
                {
                    int w = src.Word(b, q);
                    if ((w >> 10 & 7) != srcPal) continue;
                    foreach (byte ix in Indices(src.Tiles, w)) counts[ix]++;
                }
            var srcRow = src.Palettes[srcPal >= 2 ? srcPal - 2 : 5];
            (int Pal, int[] Map, long Err) best = (-1, Array.Empty<int>(), long.MaxValue);
            for (int pal = 2; pal < 8; pal++)
            {
                var row = dst.Palettes[pal - 2];
                var map = new int[16];
                long err = 0;
                bool any = true;
                for (int c = 1; c < 16; c++)
                {
                    int bk = -1, bd = int.MaxValue;
                    for (int k = 1; k < 16; k++)
                    {
                        if (dst.Cycling[pal * 16 + k]) continue;   // the colour animation changes it
                        int dd = Distance(row[k], srcRow[c]);
                        if (dd < bd) { bd = dd; bk = k; }
                    }
                    if (bk < 0) { any = false; break; }
                    map[c] = bk;
                    err += counts[c] * bd;
                }
                if (any && err < best.Err) best = (pal, map, err);
            }
            if (best.Pal < 0) throw new InvalidOperationException("This map's colour rows all change with its colour animation, so the chest's tiles have no colours to keep.");
            return rowFor[srcPal] = (best.Pal, best.Map);
        }
        int NewTile(int w)
        {
            var (pal, map) = RowFor(w >> 10 & 7);
            var srcRow = src.Palettes[(w >> 10 & 7) >= 2 ? (w >> 10 & 7) - 2 : 5];
            var ix = Indices(src.Tiles, w & 0x3FF);   // unflipped: the word keeps the chest's flips
            var tile = new byte[32];
            for (int i = 0; i < 64; i++)
            {
                if (ix[i] == 0) continue;
                int v = map[ix[i]], r = i / 8, bit = 7 - i % 8;
                if ((v & 1) != 0) tile[r * 2] |= (byte)(1 << bit);
                if ((v & 2) != 0) tile[r * 2 + 1] |= (byte)(1 << bit);
                if ((v & 4) != 0) tile[0x10 + r * 2] |= (byte)(1 << bit);
                if ((v & 8) != 0) tile[0x11 + r * 2] |= (byte)(1 << bit);
                offSum += Distance(dst.Palettes[pal - 2][v], srcRow[ix[i]]); offPixels++;
            }
            free ??= FreeTiles(rom, edit.TileSetIndex);
            // the same tile already there (one of the set's, or one added for this chest); a free one found this way is
            // kept from being written over
            for (int t = 0; t < dst.Tiles.Length / 32; t++)
            {
                if (t >= 1 && t <= LastAnimatedTile && dst.Animates) continue;
                if (t >= oldTiles && !taken.Contains(t)) continue;
                bool same = true;
                for (int k = 0; k < 32 && same; k++) same = dst.Tiles[t * 32 + k] == tile[k];
                if (!same) continue;
                taken.Add(t);
                return t | pal << 10 | w & 0xC000;
            }
            int slot = free.FirstOrDefault(s => !taken.Contains(s), -1);
            if (slot < 0) throw new InvalidOperationException($"This map's tile set ({edit.TileSetIndex}) doesn't have the tiles of that chest, and it has no free tile to put them in: every tile of the 1024 a tile set can have is drawn by a block, or is one of tiles 1-15, which the tile animation draws over. Pick a chest of a map with the same graphics (they come first in the list).");
            if ((slot + 1) * 32 > dst.Tiles.Length)
            {
                var grown = new byte[(slot + 1) * 32];
                Array.Copy(dst.Tiles, grown, dst.Tiles.Length);
                dst.Tiles = grown;
            }
            Array.Copy(tile, 0, dst.Tiles, slot * 32, 32);
            addedTiles.Add(slot); taken.Add(slot);
            return slot | pal << 10 | w & 0xC000;
        }

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
                if (tw < 0) { tw = NewTile(w); exact = false; }
                else taken.Add(tw & 0x3FF);   // a free tile drawn this way now is no longer free to write over
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
        byte[]? tileFile = null, blockFile = null;
        if (addedTiles.Count > 0)
        {
            tileFile = dst.Tiles;
            var pals = rowFor.Values.Select(r => r.Pal).Distinct().OrderBy(p => p).ToArray();
            LastTiles = new TilesAdded(edit.TileSetIndex, addedTiles.OrderBy(t => t).ToArray(), pals, oldTiles, dst.Tiles.Length / 32,
                                       offPixels == 0 ? 0 : (int)(offSum / offPixels));
        }
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
            blockFile = f.ToArray();
        }
        LastRecoloured = recoloured;
        int ts = edit.TileSetIndex, bs = edit.BlockSetIndex;
        return new Prepared(closed, open, newWords.Count, () =>
        {
            // the tile set first (repacked; moved to the added space when it no longer fits where it was)
            if (tileFile != null) L2Rom.WriteFile(rom, L2Maps.TileSetFileBase + ts, tileFile);
            if (blockFile != null) L2Rom.WriteFile(rom, L2Maps.BlockSetFileBase + bs, blockFile);
        });
    }

    // ---------------------------------------------------------------- where the pictures are kept

    /// <summary>Two blocks side by side (the left one is returned) that the camera never shows: black on both layers, and
    /// out of the 16 x 14 block screen (one more around for scrolling) wherever the party stands on a block that isn't
    /// black. Top-left first, as the game keeps its own. When there are none (towns are drawn edge to edge), any two
    /// blocks out of the screen wherever the party can walk (<see cref="Walkable"/>). When there are none
    /// either, the black pair seen least, else the pair the party can't walk on seen least (Seen = true). (-1, -1) when
    /// there is nothing of these.</summary>
    public static (int X, int Y, bool Seen) Stash(RomBuffer rom, L2MapEdit edit, int notX, int notY)
    {
        int w = edit.Width, h = edit.Height;
        var black = new bool[w * h];
        var pics = L2Maps.AllBlocks(rom, edit.ForDrawing);
        bool Dark(int b) => b == 0 || b >= pics.Length || pics[b].All(c => c == 0 || (c & 0xF8F8F8) == 0);
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
                black[y * w + x] = edit.Map.Layers.All(L => x >= L.Width || y >= L.Height || Dark(L.Blocks[y * L.Width + x]));
        // a block is seen when the screen covers it with the party on a block it can stand on
        int[] Seen(Func<int, int, bool> stands)
        {
            var seen = new int[(w + 1) * (h + 1)];
            for (int y = 0; y < h; y++)
                for (int x = 0; x < w; x++)
                {
                    if (!stands(x, y)) continue;
                    int cx = Math.Clamp(x - 8, 0, Math.Max(0, w - 16)), cy = Math.Clamp(y - 7, 0, Math.Max(0, h - 14));
                    int x0 = Math.Max(0, cx - 1), y0 = Math.Max(0, cy - 1), x1 = Math.Min(w, cx + 17), y1 = Math.Min(h, cy + 15);
                    seen[y0 * (w + 1) + x0]++; seen[y0 * (w + 1) + x1]--; seen[y1 * (w + 1) + x0]--; seen[y1 * (w + 1) + x1]++;
                }
            for (int y = 0; y <= h; y++) for (int x = 1; x <= w; x++) seen[y * (w + 1) + x] += seen[y * (w + 1) + x - 1];
            for (int y = 1; y <= h; y++) for (int x = 0; x <= w; x++) seen[y * (w + 1) + x] += seen[(y - 1) * (w + 1) + x];
            return seen;
        }
        var used = edit.Records(L2MapEdit.Treasure).Select(r => (r.Bytes[1], r.Bytes[2])).ToHashSet();
        foreach (var (_, l) in edit.Records(L2MapEdit.Looks))
            foreach (int o in new[] { 2, 6 })
                for (int dy = 0; dy < Math.Max(1, (int)l[o + 3]); dy++)
                    for (int dx = 0; dx < Math.Max(1, (int)l[o + 2]); dx++) used.Add(((byte)(l[o] + dx), (byte)(l[o + 1] + dy)));
        bool Free(int x, int y) => !(x == notX && y == notY) && !used.Contains(((byte)x, (byte)y));
        (int X, int Y, bool Seen)? Pick(int[] seen, Func<int, int, bool> may, bool least)
        {
            (int X, int Y, int Seen) best = (-1, -1, int.MaxValue);
            for (int y = 0; y < h; y++)
                for (int x = 0; x + 1 < w; x++)
                {
                    if (!may(x, y) || !may(x + 1, y) || !Free(x, y) || !Free(x + 1, y)) continue;
                    int s = seen[y * (w + 1) + x] + seen[y * (w + 1) + x + 1];
                    if (s < best.Seen) best = (x, y, s);
                    if (s == 0) return (x, y, false);
                }
            return least && best.X >= 0 ? (best.X, best.Y, true) : null;
        }
        var blackSeen = Seen((x, y) => !black[y * w + x]);
        if (Pick(blackSeen, (x, y) => black[y * w + x], least: false) is { } p1) return p1;
        // where the party can walk
        var walk = Walkable(edit, notX, notY);
        var walkSeen = walk == null ? null : Seen((x, y) => walk[y * w + x]);
        if (walk != null && Pick(walkSeen!, (x, y) => !walk[y * w + x], least: false) is { } p2) return p2;
        return Pick(blackSeen, (x, y) => black[y * w + x], least: true)
            ?? (walk != null ? Pick(walkSeen!, (x, y) => !walk[y * w + x], least: true) : null)
            ?? (-1, -1, false);
    }

    /// <summary>The blocks the party can reach on the map (null when nothing says where it starts): filled from the preset
    /// points (where scenes put the party) and the blocks around the treasure, crossing a cell's edge unless a wall is
    /// there (cell flag 04 = wall above, 08 = wall on the left, on either layer) and never onto an obstacle (80); an exit's
    /// area (x1, y1 to x2, y2, the end not included) is walked onto but the party leaves there, so the fill stops (towns
    /// are walled only where the party could walk on: the road out leads to the forest beyond the exit). Not from
    /// characters or exits: some characters wait out of reach for their scenes, and an exit's area runs past the road
    /// (Elcid's south exit covers the trees beside it).</summary>
    public static bool[]? Walkable(L2MapEdit edit, int chestX = -1, int chestY = -1)
    {
        int w = edit.Width, h = edit.Height;
        byte F(int x, int y) => (byte)edit.Map.Layers.Aggregate(0, (a, L) => x < L.Width && y < L.Height ? a | L.Flags[y * L.Width + x] : a);
        var seeds = new List<(int, int)>();
        foreach (var (_, p) in edit.Records(L2MapEdit.Points)) seeds.Add((p[1], p[2]));
        var around = edit.Records(L2MapEdit.Treasure).Select(r => ((int)r.Bytes[1], (int)r.Bytes[2])).ToList();
        if (chestX >= 0) around.Add((chestX, chestY));
        // the blocks the party opens it from: beside it with no wall between
        foreach (var (tx, ty) in around)
        {
            if (tx < 0 || ty < 0 || tx >= w || ty >= h) continue;
            if ((F(tx, ty) & 0x08) == 0) seeds.Add((tx - 1, ty));
            if ((F(tx, ty) & 0x04) == 0) seeds.Add((tx, ty - 1));
            if (tx + 1 < w && (F(tx + 1, ty) & 0x08) == 0) seeds.Add((tx + 1, ty));
            if (ty + 1 < h && (F(tx, ty + 1) & 0x04) == 0) seeds.Add((tx, ty + 1));
        }
        // an exit's blocks: the party leaves the map there, so it goes no further (its blocks are walked on, though)
        var leave = new bool[w * h];
        foreach (var (_, x) in edit.Records(L2MapEdit.Exits))
            for (int ey = x[2]; ey < Math.Max(x[2] + 1, (int)x[4]) && ey < h; ey++)
                for (int ex = x[1]; ex < Math.Max(x[1] + 1, (int)x[3]) && ex < w; ex++) leave[ey * w + ex] = true;
        var ok = new bool[w * h];
        var q = new Queue<(int, int)>();
        foreach (var (x, y) in seeds)
            if (x >= 0 && y >= 0 && x < w && y < h && (F(x, y) & 0x80) == 0 && !ok[y * w + x]) { ok[y * w + x] = true; q.Enqueue((x, y)); }
        if (q.Count == 0) return null;
        while (q.Count > 0)
        {
            var (x, y) = q.Dequeue();
            byte here = F(x, y);
            void Go(int nx, int ny, bool wall)
            {
                if (wall || nx < 0 || ny < 0 || nx >= w || ny >= h || ok[ny * w + nx] || (F(nx, ny) & 0x80) != 0) return;
                ok[ny * w + nx] = true;
                if (!leave[ny * w + nx]) q.Enqueue((nx, ny));
            }
            Go(x, y - 1, (here & 0x04) != 0);
            Go(x - 1, y, (here & 0x08) != 0);
            Go(x, y + 1, y + 1 < h && (F(x, y + 1) & 0x04) != 0);
            Go(x + 1, y, x + 1 < w && (F(x + 1, y) & 0x08) != 0);
        }
        return ok;
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
