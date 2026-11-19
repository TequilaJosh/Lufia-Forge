namespace LufiaForge.Core.Lufia2;

// chest pictures for maps that have no chest of their own. a chest's look (the map info's list +04, L2Chests)
// copies blocks of the same map onto the chest when it opens ($83:873F / $83:8B6A: the opened picture's source, w x h
// blocks. the layers byte says which layers: layer n when a bit of 0x11 << n is set, both bits = the whole cell),
// so the open chest's blocks have to be somewhere on the map. the game keeps them on blocks the camera never shows
// (most maps in their top-left corner). the closed chest is just painted where the chest stands
//
// GameLooks lists the game's chest looks (the closed blocks on the chest's spot, the open blocks at the look's
// source), one per picture and graphics. Translate gives a look's blocks in another map's block set: the block of
// that set that draws the same (pixels, priority, block attribute), or a block added to the set made of that tile
// set's tiles (any palette row and flip that draws the same colours). a tile the tile set doesn't have gets copied
// into it, in the map's closest colours, on a tile nothing draws (towns have no chest tiles). Stash finds two blocks
// side by side the camera never shows, for the open and closed pictures
public static class L2ChestLooks
{
    // a chest look of map Map: spot id, look id, layers byte, and the closed and open pictures (each layer's block
    // and flags) in that map's block set, tile set and palette set
    public sealed record Look(int Map, int Spot, int LookId, int Layers, (int Block, byte Flags)[] Closed, (int Block, byte Flags)[] Open,
                              int BlockSet, int TileSet, int PaletteSet);

    public const int MaxBlocks = 0x400;

    // the chest looks of the game's maps (1 x 1 chests with an item), one per picture and graphics
    public static List<Look> GameLooks(RomBuffer rom)
    {
        var list = new List<Look>();
        var seen = new HashSet<string>();
        for (int mapId = 2; mapId < L2Maps.Count(rom); mapId++)
        {
            List<(int At, byte[] Bytes)> spots;
            try { spots = L2MapInfo.Records(rom, mapId, 0x26, 4); } catch { continue; }
            if (spots.Count == 0) continue;
            var looks = L2MapInfo.Records(rom, mapId, 0x04, 10).Select(r => r.Bytes).ToList();
            var chests = L2Chests.Records(rom, mapId);
            L2Maps.Map map;
            try { map = L2Maps.Read(rom, mapId); } catch { continue; }
            int blockSet = rom.ReadByte(L2Maps.BlockSetAt(rom, mapId)), tileSet = rom.ReadByte(L2Maps.TileSetAt(rom, mapId)), paletteSet = rom.ReadByte(L2Maps.PaletteSetAt(rom, mapId));
            foreach (var (_, spot) in spots)
            {
                var look = looks.FirstOrDefault(x => x[0] == spot[3]);
                if (look == null || L2MapEdit.IsSelfLook(look) || look[4] != 1 || look[5] != 1 || !chests.Any(c => c.Spot == (spot[0] & 0x1F))) continue;
                if (map.Layers.Any(L => spot[1] >= L.Width || spot[2] >= L.Height || look[2] >= L.Width || look[3] >= L.Height)) continue;
                var closed = map.Layers.Select(L => (L.Blocks[spot[2] * L.Width + spot[1]], L.Flags[spot[2] * L.Width + spot[1]])).ToArray();
                var open = map.Layers.Select(L => (L.Blocks[look[3] * L.Width + look[2]], L.Flags[look[3] * L.Width + look[2]])).ToArray();
                string key = $"{blockSet}/{tileSet}/{paletteSet}/{look[1]}/" + string.Join(",", closed.Concat(open).Select(c => $"{c.Item1:X}.{c.Item2:X}"));
                if (!seen.Add(key)) continue;
                list.Add(new Look(mapId, spot[0], look[0], look[1], closed, open, blockSet, tileSet, paletteSet));
            }
        }
        return list;
    }

    // the chest looks of a map being edited (its chests as they are in the edit)
    public static List<Look> LooksOf(L2MapEdit edit)
    {
        var list = new List<Look>();
        if (edit.IsWorld) return list;
        var seen = new HashSet<string>();
        foreach (var (_, spot) in edit.Records(L2MapEdit.Treasure))
        {
            var look = edit.LookOf(spot);
            if (look == null || L2MapEdit.IsSelfLook(look) || look[4] != 1 || look[5] != 1 || edit.ChestOf(spot[0] & 0x1F) == null) continue;
            if (spot[1] >= edit.Width || spot[2] >= edit.Height || look[2] >= edit.Width || look[3] >= edit.Height) continue;
            var closed = edit.CellAt(spot[1], spot[2]);
            var open = edit.CellAt(look[2], look[3]);
            string key = $"{look[1]}/" + string.Join(",", closed.Concat(open).Select(c => $"{c.Block:X}"));
            if (!seen.Add(key)) continue;
            list.Add(new Look(edit.MapId, spot[0], look[0], look[1], closed, open, edit.BlockSetIndex, edit.TileSetIndex, edit.PaletteSetIndex));
        }
        return list;
    }

    // blocks and tiles

    private sealed class Sets
    {
        public byte[] BlockSet = Array.Empty<byte>(), Tiles = Array.Empty<byte>();
        public uint[][] Palettes = Array.Empty<uint[]>();
        public int Count => (short)(BlockSet[2] | BlockSet[3] << 8);
        public int Word(int block, int quarter) => BlockSet[0x10 + block * 8 + quarter * 2] | BlockSet[0x10 + block * 8 + quarter * 2 + 1] << 8;
        public byte Attribute(int block) => 0x10 + Count * 8 + block < BlockSet.Length ? BlockSet[0x10 + Count * 8 + block] : (byte)0;
        // the map has a tile animation (tiles 1-15 get drawn over in VRAM)
        public bool Animates;
        // the colours (CGRAM 0-127) the map's colour animation changes
        public bool[] Cycling = new bool[128];
    }

    private static Sets Load(RomBuffer rom, int map, int blockSet, int tileSet, int paletteSet) => new()
    {
        BlockSet = L2Rom.ReadFile(rom, L2Maps.BlockSetFileBase + blockSet), Tiles = L2Rom.ReadFile(rom, L2Maps.TileSetFileBase + tileSet),
        Palettes = L2Maps.Palettes(rom, map, paletteSet), Animates = HasTileAnimation(rom, map), Cycling = CyclingColours(rom, map),
    };

    // what the game draws over

    // the last tile a map's tile animation draws over: $83:AF54 copies 0x1E0 bytes (15 tiles) of the frame to VRAM
    // $4010, which is where the tile set's tiles 1-15 are (the tile set goes to VRAM $4000, $80:F05C)
    public const int LastAnimatedTile = 15;

    // the most tiles a tile set can have: the loader unpacks it to $7E:4000 (the tile animation's frames start at
    // $7E:C000) and copies it to VRAM $4000-$7FFF, the 1024 tiles a block's 10-bit tile number reaches
    public const int MaxTiles = 0x400;

    // whether map map has a tile animation (water and so on): its $CF:F90E entry's $CF:F9FE byte 0 high nibble picks
    // file 0x15F + n (0 = none)
    public static bool HasTileAnimation(RomBuffer rom, int map) =>
        rom.ReadByte(L2Maps.AnimData + rom.ReadByte(L2Maps.AnimIndexAt(rom, map)) * 2) >> 4 != 0;

    // the colours (CGRAM numbers 0-127) map map's colour animation changes: $CF:FBA6 + map (1-based, 0 = none) picks
    // a $CF:FC98 pointer to a record in bank $A1 ("PS", count | 80, then count u16 offsets from the record to entries:
    // the colour number, then duration and colour pairs up to a 00). checked in BizHawk on Elcid: colours 97-101
    // change, the five entries of record 1
    public static bool[] CyclingColours(RomBuffer rom, int map)
    {
        var cycling = new bool[128];
        int record = rom.ReadByte(L2Maps.ColourAnimAt(rom, map));
        if (record == 0 || record > 0x12) return cycling;
        int recordAt = L2Rom.SnesToFile(0xA10000 | rom.ReadUInt16Le(0x27FC98 + (record - 1) * 2));
        if (rom.ReadByte(recordAt) != 0x50 || rom.ReadByte(recordAt + 1) != 0x53) return cycling;
        int count = rom.ReadByte(recordAt + 2) & 0x7F;
        for (int i = 0; i < count; i++)
        {
            int colour = rom.ReadByte(recordAt + rom.ReadUInt16Le(recordAt + 3 + i * 2));
            if (colour < 128) cycling[colour] = true;
        }
        return cycling;
    }

    // tiles of tile set tileSet that nothing draws: not tile 0, not tiles 1-15 (the tile animation's), and used by no
    // block of any block set the game's maps (or the Ancient Cave) pair with this tile set. first the ones inside the
    // set, then the slots past its end up to MaxTiles (the set grows into them)
    public static List<int> FreeTiles(RomBuffer rom, int tileSet)
    {
        int count = L2Rom.ReadFile(rom, L2Maps.TileSetFileBase + tileSet).Length / 32;
        var used = new bool[MaxTiles];
        foreach (int blockSet in PairedBlockSets(rom, tileSet))
        {
            var file = L2Rom.ReadFile(rom, L2Maps.BlockSetFileBase + blockSet);
            int blockCount = file.Length >= 4 ? (short)(file[2] | file[3] << 8) : 0;
            for (int b = 0; b < blockCount && 0x10 + b * 8 + 8 <= file.Length; b++)
                for (int q = 0; q < 4; q++) used[(file[0x10 + b * 8 + q * 2] | file[0x11 + b * 8 + q * 2] << 8) & 0x3FF] = true;
        }
        return Enumerable.Range(LastAnimatedTile + 1, MaxTiles - LastAnimatedTile - 1).Where(i => !used[i]).OrderBy(i => i < count ? 0 : 1).ThenBy(i => i).ToList();
    }

    // the block sets drawn with tile set tileSet: those of the maps that use it, and the Ancient Cave's
    // ($83:9F49 / $83:9F5F: block sets 0B, 09, 04 with tile sets 0B, 09, 04)
    public static List<int> PairedBlockSets(RomBuffer rom, int tileSet)
    {
        var sets = Enumerable.Range(0, L2Maps.Count(rom)).Where(m => rom.ReadByte(L2Maps.TileSetAt(rom, m)) == tileSet)
            .Select(m => (int)rom.ReadByte(L2Maps.BlockSetAt(rom, m))).ToHashSet();
        if (tileSet is 0x0B or 0x09 or 0x04) sets.Add(tileSet);
        return sets.OrderBy(s => s).ToList();
    }

    // a tile drawn with a block word (tile, palette row, flips) as 64 ARGB pixels (0 = see-through)
    private static uint[] Quarter(Sets sets, int word)
    {
        var pixels = new uint[64];
        int tile = word & 0x3FF, palette = word >> 10 & 7;
        bool flipX = (word & 0x4000) != 0, flipY = (word & 0x8000) != 0;
        int paletteRow = palette >= 2 ? palette - 2 : 5, tileAt = tile * 32;
        if (tileAt + 32 > sets.Tiles.Length) return pixels;
        for (int row = 0; row < 8; row++)
        {
            int sourceRow = flipY ? 7 - row : row;
            byte p0 = sets.Tiles[tileAt + sourceRow * 2], p1 = sets.Tiles[tileAt + sourceRow * 2 + 1], p2 = sets.Tiles[tileAt + 0x10 + sourceRow * 2], p3 = sets.Tiles[tileAt + 0x11 + sourceRow * 2];
            for (int col = 0; col < 8; col++)
            {
                int bit = flipX ? col : 7 - col;
                int colorIndex = (p0 >> bit & 1) | (p1 >> bit & 1) << 1 | (p2 >> bit & 1) << 2 | (p3 >> bit & 1) << 3;
                pixels[row * 8 + col] = colorIndex == 0 ? 0 : sets.Palettes[paletteRow][colorIndex];
            }
        }
        return pixels;
    }

    private static string Key(Sets sets, int block) =>
        string.Join("|", Enumerable.Range(0, 4).Select(q => sets.Word(block, q) & 0x2000 | 0)) + "/" + sets.Attribute(block) + "/" +
        string.Join(",", Enumerable.Range(0, 4).SelectMany(q => Quarter(sets, sets.Word(block, q))));

    // a tile's colour numbers (0-15) with a block word's flips, 64 of them
    private static byte[] Indices(byte[] tiles, int word)
    {
        var colors = new byte[64];
        int tileAt = (word & 0x3FF) * 32;
        bool flipX = (word & 0x4000) != 0, flipY = (word & 0x8000) != 0;
        if (tileAt + 32 > tiles.Length) return colors;
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

    private static int Distance(uint a, uint b) =>
        Math.Abs((int)(a >> 16 & 0xFF) - (int)(b >> 16 & 0xFF)) + Math.Abs((int)(a >> 8 & 0xFF) - (int)(b >> 8 & 0xFF)) + Math.Abs((int)(a & 0xFF) - (int)(b & 0xFF));

    // how much a colour can differ (red + green + blue, 0-765, on average over the tile's pixels) for a tile of this
    // map's tile set drawn with one of its palette rows to stand in for the chest's tile
    public const int ColourTolerance = 60;

    // how many 8 x 8 tiles the last Translate drew with the map's own colours (close to, not the same as, the chest's)
    public static int LastRecoloured { get; private set; }

    // a word of target's tile set and palettes that draws source word sourceWord of source: the same shape (the same
    // pixels share a colour, the colour numbers can differ) with the palette row closest in colour. -1 if no tile has
    // that shape or the colours are too far off (ColourTolerance). Exact = the colours are the same
    private static (int Word, bool Exact) FindTile(Sets target, Sets source, int sourceWord)
    {
        var wanted = Indices(source.Tiles, sourceWord);
        var colours = Quarter(source, sourceWord);
        int used = wanted.Count(i => i != 0);
        int tileCount = target.Tiles.Length / 32, best = -1, bestError = int.MaxValue;
        var forward = new int[16]; var back = new int[16];
        for (int tile = 0; tile < tileCount; tile++)
        {
            if (target.Animates && tile >= 1 && tile <= LastAnimatedTile) continue;   // the tile animation draws over these
            for (int flip = 0; flip < 4; flip++)
            {
                int word = tile | (flip & 1) << 14 | (flip >> 1) << 15;
                var have = Indices(target.Tiles, word);
                Array.Fill(forward, -1); Array.Fill(back, -1);
                bool ok = true;
                for (int i = 0; i < 64 && ok; i++)
                {
                    int a = wanted[i], b = have[i];
                    if (a == 0 || b == 0) { ok = a == b; continue; }
                    if (forward[a] < 0 && back[b] < 0) { forward[a] = b; back[b] = a; }
                    else ok = forward[a] == b && back[b] == a;
                }
                if (!ok) continue;
                for (int palette = 2; palette < 8; palette++)
                {
                    var row = target.Palettes[palette - 2];
                    // a colour the map's colour animation changes would make the chest flicker
                    if (Enumerable.Range(0, 64).Any(i => have[i] != 0 && target.Cycling[palette * 16 + have[i]])) continue;
                    int error = 0;
                    for (int i = 0; i < 64; i++) if (wanted[i] != 0) error += Distance(row[have[i]], colours[i]);
                    if (error < bestError) { bestError = error; best = word | palette << 10; }
                    if (error == 0) return (best, true);
                }
            }
        }
        return best >= 0 && (used == 0 || bestError <= ColourTolerance * used) ? (best, bestError == 0) : (-1, false);
    }

    // tiles Translate put into a tile set: their numbers, the colour rows (2-7) they're drawn with, the set's tile
    // count before and after, and how far their colours are from the chest's (red + green + blue, 0-765, on average
    // per pixel)
    public sealed record TilesAdded(int TileSet, int[] Tiles, int[] Rows, int CountBefore, int CountAfter, int Off);

    // what the last Translate added to the map's tile set, or null
    public static TilesAdded? LastTiles { get; private set; }

    // the look's closed and open pictures in edit's block set (blocks found there, or added to it in the rom, Added
    // of them). a tile of the chest that the map's tile set doesn't have (or one of its shape in close colours) gets
    // copied into it: recoloured to the map's colour row closest to the chest's colours (one row per row of the
    // chest, colours the map's colour animation changes are left out) and put in a tile nothing draws (FreeTiles),
    // the set written back (LastTiles). nothing gets written when it throws: no free tile, or the block set is full
    public static ((int Block, byte Flags)[] Closed, (int Block, byte Flags)[] Open, int Added) Translate(RomBuffer rom, Look look, L2MapEdit edit)
    {
        var prepared = Prepare(rom, look, edit);
        prepared.Write();
        return (prepared.Closed, prepared.Open, prepared.Added);
    }

    // a Translate worked out but not written yet: Write puts the tiles and blocks into the rom (nothing if none were added)
    public sealed record Prepared((int Block, byte Flags)[] Closed, (int Block, byte Flags)[] Open, int Added, Action Write);

    // Translate without writing: the caller writes (Prepared.Write) once nothing else can stop the chest, so a
    // refused chest leaves the rom how it was
    public static Prepared Prepare(RomBuffer rom, Look look, L2MapEdit edit)
    {
        // Layers -1: a list of blocks (a piece from other graphics), every entry of Closed, whatever the layer
        bool many = look.Layers < 0;
        int layers = many ? look.Closed.Length : edit.Map.Layers.Length;
        bool Copies(int i) => many || L2MapEdit.CopiesLayer(look.Layers, i);
        (int, byte)[] Fit((int Block, byte Flags)[] pic) => Enumerable.Range(0, layers).Select(i => i < pic.Length ? pic[i] : (0, (byte)0)).ToArray();
        LastRecoloured = 0;
        LastTiles = null;
        if (look.BlockSet == edit.BlockSetIndex && look.TileSet == edit.TileSetIndex && look.PaletteSet == edit.PaletteSetIndex)
            return new Prepared(Fit(look.Closed), Fit(look.Open), 0, () => { });
        var source = Load(rom, look.Map, look.BlockSet, look.TileSet, look.PaletteSet);
        var target = Load(rom, edit.MapId, edit.BlockSetIndex, edit.TileSetIndex, edit.PaletteSetIndex);
        int count = target.Count;
        if (target.BlockSet.Length != 0x10 + count * 9) throw new InvalidOperationException($"Block set {edit.BlockSetIndex} isn't laid out as the game's are, so blocks can't be added to it.");
        var have = new Dictionary<string, int>();
        for (int b = count - 1; b >= 0; b--) have[Key(target, b)] = b;
        var newWords = new List<int[]>();
        var newAttr = new List<byte>();
        var done = new Dictionary<int, int>();
        int recoloured = 0;

        // tiles the map's tile set doesn't have: the chest's own, in this map's colours, put in tiles nothing draws
        int oldTiles = target.Tiles.Length / 32;
        List<int>? free = null;
        var addedTiles = new List<int>();
        var taken = new HashSet<int>();
        var rowFor = new Dictionary<int, (int Pal, int[] Map)>();
        long offSum = 0, offPixels = 0;
        // the source rows' colours as the chest uses them (pixels per colour number), to pick one row of this map for each
        var chestBlocks = look.Closed.Concat(look.Open).Select((c, i) => (c.Block, Layer: i % Math.Max(1, look.Closed.Length)))
            .Where(c => c.Block != 0 && c.Block < source.Count && Copies(c.Layer)).Select(c => c.Block).Distinct().ToList();
        (int Pal, int[] Map) RowFor(int sourcePalette)
        {
            if (rowFor.TryGetValue(sourcePalette, out var known)) return known;
            var counts = new long[16];
            foreach (int block in chestBlocks)
                for (int q = 0; q < 4; q++)
                {
                    int word = source.Word(block, q);
                    if ((word >> 10 & 7) != sourcePalette) continue;
                    foreach (byte colorIndex in Indices(source.Tiles, word)) counts[colorIndex]++;
                }
            var sourceRow = source.Palettes[sourcePalette >= 2 ? sourcePalette - 2 : 5];
            (int Pal, int[] Map, long Err) best = (-1, Array.Empty<int>(), long.MaxValue);
            for (int palette = 2; palette < 8; palette++)
            {
                var row = target.Palettes[palette - 2];
                var colorMap = new int[16];
                long error = 0;
                bool any = true;
                for (int c = 1; c < 16; c++)
                {
                    int bestColor = -1, bestDistance = int.MaxValue;
                    for (int k = 1; k < 16; k++)
                    {
                        if (target.Cycling[palette * 16 + k]) continue;   // the colour animation changes it
                        int distance = Distance(row[k], sourceRow[c]);
                        if (distance < bestDistance) { bestDistance = distance; bestColor = k; }
                    }
                    if (bestColor < 0) { any = false; break; }
                    colorMap[c] = bestColor;
                    error += counts[c] * bestDistance;
                }
                if (any && error < best.Err) best = (palette, colorMap, error);
            }
            if (best.Pal < 0) throw new InvalidOperationException("This map's colour rows all change with its colour animation, so the chest's tiles have no colours to keep.");
            return rowFor[sourcePalette] = (best.Pal, best.Map);
        }
        int NewTile(int word)
        {
            var (palette, colorMap) = RowFor(word >> 10 & 7);
            var sourceRow = source.Palettes[(word >> 10 & 7) >= 2 ? (word >> 10 & 7) - 2 : 5];
            var colors = Indices(source.Tiles, word & 0x3FF);   // unflipped: the word keeps the chest's flips
            var tile = new byte[32];
            for (int i = 0; i < 64; i++)
            {
                if (colors[i] == 0) continue;
                int color = colorMap[colors[i]], row = i / 8, bit = 7 - i % 8;
                if ((color & 1) != 0) tile[row * 2] |= (byte)(1 << bit);
                if ((color & 2) != 0) tile[row * 2 + 1] |= (byte)(1 << bit);
                if ((color & 4) != 0) tile[0x10 + row * 2] |= (byte)(1 << bit);
                if ((color & 8) != 0) tile[0x11 + row * 2] |= (byte)(1 << bit);
                offSum += Distance(target.Palettes[palette - 2][color], sourceRow[colors[i]]); offPixels++;
            }
            free ??= FreeTiles(rom, edit.TileSetIndex);
            // the same tile already there (one of the set's, or one added for this chest). a free one found this way
            // is kept from being written over
            for (int t = 0; t < target.Tiles.Length / 32; t++)
            {
                if (t >= 1 && t <= LastAnimatedTile && target.Animates) continue;
                if (t >= oldTiles && !taken.Contains(t)) continue;
                bool same = true;
                for (int k = 0; k < 32 && same; k++) same = target.Tiles[t * 32 + k] == tile[k];
                if (!same) continue;
                taken.Add(t);
                return t | palette << 10 | word & 0xC000;
            }
            int slot = free.FirstOrDefault(s => !taken.Contains(s), -1);
            if (slot < 0) throw new InvalidOperationException($"This map's tile set ({edit.TileSetIndex}) doesn't have the tiles of that chest, and it has no free tile to put them in: every tile of the 1024 a tile set can have is drawn by a block, or is one of tiles 1-15, which the tile animation draws over. Pick a chest of a map with the same graphics (they come first in the list).");
            if ((slot + 1) * 32 > target.Tiles.Length)
            {
                var grown = new byte[(slot + 1) * 32];
                Array.Copy(target.Tiles, grown, target.Tiles.Length);
                target.Tiles = grown;
            }
            Array.Copy(tile, 0, target.Tiles, slot * 32, 32);
            addedTiles.Add(slot); taken.Add(slot);
            return slot | palette << 10 | word & 0xC000;
        }

        int MapBlock(int block)
        {
            if (block == 0) return 0;   // nothing there
            if (done.TryGetValue(block, out int mapped)) return mapped;
            if (block >= source.Count) throw new InvalidOperationException($"That chest's block {block:X3} isn't in its own block set.");
            string key = Key(source, block);
            if (have.TryGetValue(key, out int found)) return done[block] = found;
            // a new block of this map's tiles
            var words = new int[4];
            for (int q = 0; q < 4; q++)
            {
                int word = source.Word(block, q);
                var (targetWord, exact) = FindTile(target, source, word);
                if (targetWord < 0) { targetWord = NewTile(word); exact = false; }
                else taken.Add(targetWord & 0x3FF);   // a free tile drawn this way now isn't free to write over any more
                if (!exact) recoloured++;
                words[q] = targetWord | word & 0x2000;
            }
            // a block of the set already made the same way
            for (int b = 0; b < count; b++)
                if (target.Attribute(b) == source.Attribute(block) && Enumerable.Range(0, 4).All(q => target.Word(b, q) == words[q])) { have[key] = b; return done[block] = b; }
            if (count + newWords.Count >= MaxBlocks) throw new InvalidOperationException($"Block set {edit.BlockSetIndex} is full ({MaxBlocks} blocks), so the chest's blocks can't be added.");
            int newBlock = count + newWords.Count;
            newWords.Add(words); newAttr.Add(source.Attribute(block));
            have[key] = newBlock;
            return done[block] = newBlock;
        }
        (int, byte)[] Pic((int Block, byte Flags)[] pic) => Enumerable.Range(0, layers).Select(i =>
            i < pic.Length && Copies(i) ? (MapBlock(pic[i].Block), pic[i].Flags) : (0, (byte)0)).ToArray();
        var closed = Pic(look.Closed);
        var open = Pic(look.Open);
        byte[]? tileFile = null, blockFile = null;
        if (addedTiles.Count > 0)
        {
            tileFile = target.Tiles;
            var palettes = rowFor.Values.Select(r => r.Pal).Distinct().OrderBy(p => p).ToArray();
            LastTiles = new TilesAdded(edit.TileSetIndex, addedTiles.OrderBy(t => t).ToArray(), palettes, oldTiles, target.Tiles.Length / 32,
                                       offPixels == 0 ? 0 : (int)(offSum / offPixels));
        }
        if (newWords.Count > 0)
        {
            int newCount = count + newWords.Count;
            var file = new List<byte>(target.BlockSet.Take(0x10 + count * 8));
            foreach (var words in newWords) foreach (int word in words) { file.Add((byte)word); file.Add((byte)(word >> 8)); }
            file.AddRange(target.BlockSet.Skip(0x10 + count * 8).Take(count));
            file.AddRange(newAttr);
            file[2] = (byte)newCount; file[3] = (byte)(newCount >> 8);
            int size = 0x10 + newCount * 8;
            file[4] = (byte)size; file[5] = (byte)(size >> 8);
            blockFile = file.ToArray();
        }
        LastRecoloured = recoloured;
        int tileSet = edit.TileSetIndex, blockSet = edit.BlockSetIndex;
        return new Prepared(closed, open, newWords.Count, () =>
        {
            // the tile set and the block set (repacked, moved to the added space if they don't fit where they were any more)
            var write = new List<(int, byte[])>();
            if (tileFile != null) write.Add((L2Maps.TileSetFileBase + tileSet, tileFile));
            if (blockFile != null) write.Add((L2Maps.BlockSetFileBase + blockSet, blockFile));
            L2Rom.WriteFiles(rom, write.ToArray());
        });
    }

    // where the pictures are kept

    // two blocks side by side (the left one is returned) that the camera never shows: black on both layers, and out
    // of the 16 x 14 block screen (one more around for scrolling) wherever the party stands on a block that isn't
    // black. top-left first, like the game keeps its own. if there are none (towns are drawn edge to edge), any two
    // blocks out of the screen wherever the party can walk (Walkable). if there are none of those either, the black
    // pair seen least, else the pair the party can't walk on seen least (Seen = true). (-1, -1) if there's nothing
    public static (int X, int Y, bool Seen) Stash(RomBuffer rom, L2MapEdit edit, int notX, int notY)
    {
        int w = edit.Width, h = edit.Height;
        var black = new bool[w * h];
        var pics = L2Maps.AllBlocks(rom, edit.ForDrawing);
        bool Dark(int block) => block == 0 || block >= pics.Length || pics[block].All(c => c == 0 || (c & 0xF8F8F8) == 0);
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
        foreach (var (_, look) in edit.Records(L2MapEdit.Looks))
            foreach (int pictureAt in new[] { 2, 6 })
                for (int dy = 0; dy < Math.Max(1, (int)look[pictureAt + 3]); dy++)
                    for (int dx = 0; dx < Math.Max(1, (int)look[pictureAt + 2]); dx++) used.Add(((byte)(look[pictureAt] + dx), (byte)(look[pictureAt + 1] + dy)));
        bool Free(int x, int y) => !(x == notX && y == notY) && !used.Contains(((byte)x, (byte)y));
        (int X, int Y, bool Seen)? Pick(int[] seen, Func<int, int, bool> may, bool least)
        {
            (int X, int Y, int Seen) best = (-1, -1, int.MaxValue);
            for (int y = 0; y < h; y++)
                for (int x = 0; x + 1 < w; x++)
                {
                    if (!may(x, y) || !may(x + 1, y) || !Free(x, y) || !Free(x + 1, y)) continue;
                    int seenCount = seen[y * (w + 1) + x] + seen[y * (w + 1) + x + 1];
                    if (seenCount < best.Seen) best = (x, y, seenCount);
                    if (seenCount == 0) return (x, y, false);
                }
            return least && best.X >= 0 ? (best.X, best.Y, true) : null;
        }
        var blackSeen = Seen((x, y) => !black[y * w + x]);
        if (Pick(blackSeen, (x, y) => black[y * w + x], least: false) is { } blackPair) return blackPair;
        // where the party can walk
        var walk = Walkable(edit, notX, notY);
        var walkSeen = walk == null ? null : Seen((x, y) => walk[y * w + x]);
        if (walk != null && Pick(walkSeen!, (x, y) => !walk[y * w + x], least: false) is { } offPathPair) return offPathPair;
        return Pick(blackSeen, (x, y) => black[y * w + x], least: true)
            ?? (walk != null ? Pick(walkSeen!, (x, y) => !walk[y * w + x], least: true) : null)
            ?? (-1, -1, false);
    }

    // the blocks the party can reach on the map (null if nothing says where it starts). filled from the preset points
    // (where scenes put the party) and the blocks around the treasure, crossing a cell's edge unless there's a wall
    // (cell flag 04 = wall above, 08 = wall on the left, on either layer) and never onto an obstacle (80). an exit's
    // area (x1, y1 to x2, y2, the end not included) gets walked onto but the party leaves there, so the fill stops
    // (towns are only walled where the party could walk: the road out leads to the forest past the exit). not filled
    // from characters or exits: some characters wait out of reach for their scenes, and an exit's area runs past the
    // road (Elcid's south exit covers the trees beside it)
    public static bool[]? Walkable(L2MapEdit edit, int chestX = -1, int chestY = -1)
    {
        int w = edit.Width, h = edit.Height;
        byte F(int x, int y) => (byte)edit.Map.Layers.Aggregate(0, (a, L) => x < L.Width && y < L.Height ? a | L.Flags[y * L.Width + x] : a);
        var seeds = new List<(int, int)>();
        foreach (var (_, point) in edit.Records(L2MapEdit.Points)) seeds.Add((point[1], point[2]));
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
        foreach (var (_, exit) in edit.Records(L2MapEdit.Exits))
            for (int ey = exit[2]; ey < Math.Max(exit[2] + 1, (int)exit[4]) && ey < h; ey++)
                for (int ex = exit[1]; ex < Math.Max(exit[1] + 1, (int)exit[3]) && ex < w; ex++) leave[ey * w + ex] = true;
        var reached = new bool[w * h];
        var queue = new Queue<(int, int)>();
        foreach (var (x, y) in seeds)
            if (x >= 0 && y >= 0 && x < w && y < h && (F(x, y) & 0x80) == 0 && !reached[y * w + x]) { reached[y * w + x] = true; queue.Enqueue((x, y)); }
        if (queue.Count == 0) return null;
        while (queue.Count > 0)
        {
            var (x, y) = queue.Dequeue();
            byte here = F(x, y);
            void Go(int nx, int ny, bool wall)
            {
                if (wall || nx < 0 || ny < 0 || nx >= w || ny >= h || reached[ny * w + nx] || (F(nx, ny) & 0x80) != 0) return;
                reached[ny * w + nx] = true;
                if (!leave[ny * w + nx]) queue.Enqueue((nx, ny));
            }
            Go(x, y - 1, (here & 0x04) != 0);
            Go(x - 1, y, (here & 0x08) != 0);
            Go(x, y + 1, y + 1 < h && (F(x, y + 1) & 0x04) != 0);
            Go(x + 1, y, x + 1 < w && (F(x + 1, y) & 0x08) != 0);
        }
        return reached;
    }

    // pictures for the list

    // the look as a 36 x 16 picture: the closed chest, then the open one (both layers, as on its map)
    public static (int Width, int Height, uint[] Pixels) Picture(RomBuffer rom, Look look)
    {
        var sets = Load(rom, look.Map, look.BlockSet, look.TileSet, look.PaletteSet);
        var pixels = new uint[36 * 16];
        void Draw((int Block, byte Flags)[] pic, int x0)
        {
            foreach (var (block, _) in pic)
            {
                if (block == 0 || block >= sets.Count) continue;
                for (int q = 0; q < 4; q++)
                {
                    var tilePixels = Quarter(sets, sets.Word(block, q));
                    int qx = q >= 2 ? 8 : 0, qy = (q & 1) * 8;
                    for (int i = 0; i < 64; i++) if (tilePixels[i] != 0) pixels[(qy + i / 8) * 36 + x0 + qx + i % 8] = tilePixels[i];
                }
            }
        }
        Draw(look.Closed, 0);
        var open = look.Open.Select((o, i) => L2MapEdit.CopiesLayer(look.Layers, i) ? o : i < look.Closed.Length ? look.Closed[i] : o).ToArray();
        Draw(open, 20);
        return (36, 16, pixels);
    }
}
