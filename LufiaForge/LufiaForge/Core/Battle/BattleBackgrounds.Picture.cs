namespace LufiaForge.Core.Battle;

public static partial class BattleBackgrounds
{
    // a picture made ready for the game: its tiles, 32 x 28 layout and colours (BGR555, 16 per palette)
    public sealed record Converted(byte[] Tiles, ushort[] Layout, ushort[] Colours, int TileCount, int PaletteCount);

    private const int TilesX = 32, TilesY = 28;

    private static ushort Bgr(uint c) => (ushort)(((c >> 3) & 0x1F) << 10 | ((c >> 11) & 0x1F) << 5 | ((c >> 19) & 0x1F));
    private static uint Argb(ushort c)
    {
        uint r = (uint)(c & 0x1F), g = (uint)(c >> 5 & 0x1F), b = (uint)(c >> 10 & 0x1F);
        return 0xFF000000 | (r << 3 | r >> 2) << 16 | (g << 3 | g >> 2) << 8 | (b << 3 | b >> 2);
    }

    // a 256 x 224 picture (ARGB) as it is: each 8 x 8 square gets one of six 15-colour palettes, repeated squares are
    // stored once (mirrored ones too). throws with a message the user can do something with when it doesn't fit,
    // Fit is the one that makes any picture fit
    public static Converted Convert(uint[] argb, int width, int height)
    {
        if (width != Width || height != Height) throw new InvalidOperationException($"A battle background is {Width} x {Height} (this picture is {width} x {height}).");
        var pixels = argb.Select(Bgr).ToArray();
        var tileColours = new List<HashSet<ushort>>();
        for (int t = 0; t < TilesX * TilesY; t++)
        {
            var colourSet = TilePixels(pixels, t).ToHashSet();
            if (colourSet.Count > 15) throw new InvalidOperationException($"The 8 x 8 square at {t % TilesX * 8}, {t / TilesX * 8} has {colourSet.Count} colours; a square can have at most 15.");
            tileColours.Add(colourSet);
        }
        // palettes: biggest colour sets first, each one goes into the palette it grows the least
        var palettes = new List<HashSet<ushort>>();
        var paletteOf = new int[tileColours.Count];
        foreach (int t in Enumerable.Range(0, tileColours.Count).OrderByDescending(i => tileColours[i].Count))
        {
            var colourSet = tileColours[t];
            int best = -1, leastGrowth = int.MaxValue;
            for (int p = 0; p < palettes.Count; p++)
            {
                int growth = colourSet.Count(c => !palettes[p].Contains(c));
                if (palettes[p].Count + growth <= 15 && growth < leastGrowth) { best = p; leastGrowth = growth; }
            }
            if (best < 0)
            {
                if (palettes.Count == Palettes) throw new InvalidOperationException($"The picture needs more than {Palettes} palettes of 15 colours.");
                palettes.Add(new HashSet<ushort>());
                best = palettes.Count - 1;
            }
            palettes[best].UnionWith(colourSet);
            paletteOf[t] = best;
        }
        var built = Build(pixels, paletteOf, palettes.Select(p => p.OrderBy(c => c).ToList()).ToList(), merge: false, out _);
        return built;
    }

    // any picture made into a battle background, plus notes on what was done to it:
    // - scaled to 256 x 224 (a picture that's a whole-number size up gets shrunk exactly, others get scaled to cover
    //   the screen and cut to it from the middle)
    // - colours brought down to six palettes of 15 when it has more (squares grouped by colour, each group's
    //   colours picked by median cut)
    // - the most alike squares merged when there are more than 511 different ones
    public static (Converted Picture, List<string> Notes) Fit(uint[] argb, int width, int height)
    {
        var notes = new List<string>();
        var picture = argb;
        if (width != Width || height != Height)
        {
            picture = Scale(argb, width, height, out string how);
            notes.Add(how);
        }
        try
        {
            return (Convert(picture, Width, Height), notes);
        }
        catch (InvalidOperationException) { }
        var pixels = picture.Select(Bgr).ToArray();
        var (paletteOf, palettes) = Quantize(pixels);
        int colours = palettes.Sum(p => p.Count);
        notes.Add($"colours brought down to {colours} in {palettes.Count} palettes");
        var converted = Build(pixels, paletteOf, palettes, merge: true, out int merged);
        if (merged > 0) notes.Add($"{merged} squares merged with ones most like them");
        return (converted, notes);
    }

    private static IEnumerable<ushort> TilePixels(ushort[] pixels, int t)
    {
        int tx = t % TilesX, ty = t / TilesX;
        for (int y = 0; y < 8; y++) for (int x = 0; x < 8; x++) yield return pixels[(ty * 8 + y) * Width + tx * 8 + x];
    }

    private static uint[] Scale(uint[] argb, int sourceWidth, int sourceHeight, out string how)
    {
        var output = new uint[Width * Height];
        if (sourceWidth % Width == 0 && sourceHeight % Height == 0 && sourceWidth / Width == sourceHeight / Height)
        {
            int factor = sourceWidth / Width;
            for (int y = 0; y < Height; y++) for (int x = 0; x < Width; x++) output[y * Width + x] = argb[(y * factor) * sourceWidth + x * factor];
            how = $"shrunk {factor} times ({sourceWidth} x {sourceHeight})";
            return output;
        }
        // cover the screen, keep the shape, cut from the middle. each pixel is the average of the area it covers
        double scale = Math.Max((double)Width / sourceWidth, (double)Height / sourceHeight);
        double offX = (sourceWidth * scale - Width) / 2, offY = (sourceHeight * scale - Height) / 2;
        for (int y = 0; y < Height; y++)
            for (int x = 0; x < Width; x++)
            {
                int x0 = (int)((x + offX) / scale), x1 = Math.Max(x0 + 1, (int)((x + 1 + offX) / scale));
                int y0 = (int)((y + offY) / scale), y1 = Math.Max(y0 + 1, (int)((y + 1 + offY) / scale));
                long r = 0, g = 0, b = 0, count = 0;
                for (int yy = y0; yy < Math.Min(sourceHeight, y1); yy++)
                    for (int xx = x0; xx < Math.Min(sourceWidth, x1); xx++)
                    {
                        uint c = argb[yy * sourceWidth + xx];
                        if ((c >> 24) < 0x80) c = 0xFF000000;   // see-through counts as black
                        r += c >> 16 & 0xFF; g += c >> 8 & 0xFF; b += c & 0xFF; count++;
                    }
                if (count == 0) count = 1;
                output[y * Width + x] = 0xFF000000 | (uint)(r / count) << 16 | (uint)(g / count) << 8 | (uint)(b / count);
            }
        how = $"scaled from {sourceWidth} x {sourceHeight}" + (Math.Abs((double)sourceWidth / sourceHeight - (double)Width / Height) > 0.01 ? " and cut to 256 x 224" : "");
        return output;
    }

    private static (int R, int G, int B) Rgb(ushort c) => (c & 0x1F, c >> 5 & 0x1F, c >> 10 & 0x1F);
    private static int Dist(ushort a, ushort b)
    {
        var (r1, g1, b1) = Rgb(a); var (r2, g2, b2) = Rgb(b);
        int dr = r1 - r2, dg = g1 - g2, db = b1 - b2;
        return 2 * dr * dr + 4 * dg * dg + 3 * db * db;
    }

    // six palettes of 15 for any picture: squares grouped by their average colour (k-means), each group's
    // colours picked by median cut, then each square takes the palette that draws it best
    private static (int[] PalOf, List<List<ushort>> Palettes) Quantize(ushort[] pixels)
    {
        int tileCount = TilesX * TilesY;
        var mean = new (double R, double G, double B)[tileCount];
        for (int t = 0; t < tileCount; t++)
        {
            double r = 0, g = 0, b = 0;
            foreach (var c in TilePixels(pixels, t)) { var (cr, cg, cb) = Rgb(c); r += cr; g += cg; b += cb; }
            mean[t] = (r / 64, g / 64, b / 64);
        }
        int groupCount = Palettes;
        // start from the farthest points, then a few k-means rounds
        var centres = new List<(double R, double G, double B)> { mean[0] };
        while (centres.Count < groupCount)
            centres.Add(mean.OrderByDescending(m => centres.Min(c => Sq(m, c))).First());
        var group = new int[tileCount];
        for (int round = 0; round < 12; round++)
        {
            for (int t = 0; t < tileCount; t++) group[t] = Enumerable.Range(0, groupCount).OrderBy(i => Sq(mean[t], centres[i])).First();
            for (int i = 0; i < groupCount; i++)
            {
                var members = Enumerable.Range(0, tileCount).Where(t => group[t] == i).ToList();
                if (members.Count > 0) centres[i] = (members.Average(t => mean[t].R), members.Average(t => mean[t].G), members.Average(t => mean[t].B));
            }
        }
        var palettes = new List<List<ushort>>();
        for (int i = 0; i < groupCount; i++)
        {
            var colours = Enumerable.Range(0, tileCount).Where(t => group[t] == i).SelectMany(t => TilePixels(pixels, t)).ToList();
            if (colours.Count > 0) palettes.Add(MedianCut(colours, 15));
        }
        var paletteOf = new int[tileCount];
        for (int t = 0; t < tileCount; t++)
        {
            var squarePixels = TilePixels(pixels, t).ToList();
            paletteOf[t] = Enumerable.Range(0, palettes.Count).OrderBy(p => squarePixels.Sum(c => palettes[p].Min(q => Dist(c, q)))).First();
        }
        // each pixel becomes the nearest colour of its square's palette
        for (int t = 0; t < tileCount; t++)
        {
            int tx = t % TilesX, ty = t / TilesX;
            var palette = palettes[paletteOf[t]];
            for (int y = 0; y < 8; y++)
                for (int x = 0; x < 8; x++)
                {
                    int i = (ty * 8 + y) * Width + tx * 8 + x;
                    pixels[i] = palette.OrderBy(q => Dist(pixels[i], q)).First();
                }
        }
        // keep only the colours still used
        for (int p = 0; p < palettes.Count; p++)
        {
            var used = Enumerable.Range(0, tileCount).Where(t => paletteOf[t] == p).SelectMany(t => TilePixels(pixels, t)).ToHashSet();
            palettes[p] = palettes[p].Where(used.Contains).Distinct().OrderBy(c => c).ToList();
        }
        return (paletteOf, palettes);

        static double Sq((double R, double G, double B) a, (double R, double G, double B) b) =>
            (a.R - b.R) * (a.R - b.R) + (a.G - b.G) * (a.G - b.G) + (a.B - b.B) * (a.B - b.B);
    }

    private static List<ushort> MedianCut(List<ushort> colours, int count)
    {
        var boxes = new List<List<ushort>> { colours };
        while (boxes.Count < count)
        {
            // split the box with the widest spread
            List<ushort>? widest = null; int axis = 0, spread = 0;
            foreach (var box in boxes)
            {
                if (box.Distinct().Count() < 2) continue;
                for (int channel = 0; channel < 3; channel++)
                {
                    int lo = box.Min(c => Channel(c, channel)), hi = box.Max(c => Channel(c, channel));
                    if (hi - lo > spread) { spread = hi - lo; widest = box; axis = channel; }
                }
            }
            if (widest == null) break;
            var sorted = widest.OrderBy(c => Channel(c, axis)).ToList();
            boxes.Remove(widest);
            boxes.Add(sorted.Take(sorted.Count / 2).ToList());
            boxes.Add(sorted.Skip(sorted.Count / 2).ToList());
        }
        return boxes.Where(box => box.Count > 0).Select(box =>
        {
            int r = (int)Math.Round(box.Average(c => Channel(c, 0))), g = (int)Math.Round(box.Average(c => Channel(c, 1))), blue = (int)Math.Round(box.Average(c => Channel(c, 2)));
            return (ushort)(blue << 10 | g << 5 | r);
        }).Distinct().ToList();

        static int Channel(ushort c, int a) => a == 0 ? c & 0x1F : a == 1 ? c >> 5 & 0x1F : c >> 10 & 0x1F;
    }

    // the tiles, layout and colours from 15-bit pixels, each square's palette and the palettes. repeated squares
    // (mirrored too) get stored once. with merge the most alike squares share a tile until at most 511
    // are left (tile 0 stays empty, the upper layer gets cleared to it)
    private static Converted Build(ushort[] pixels, int[] paletteOf, List<List<ushort>> palettes, bool merge, out int merged)
    {
        merged = 0;
        int tileCount = TilesX * TilesY;
        var colours = new ushort[Palettes * 16];
        for (int p = 0; p < palettes.Count; p++) for (int i = 0; i < palettes[p].Count; i++) colours[p * 16 + 1 + i] = palettes[p][i];
        var indices = new int[tileCount][];
        for (int t = 0; t < tileCount; t++)
        {
            var palette = palettes[paletteOf[t]];
            indices[t] = TilePixels(pixels, t).Select(c => palette.IndexOf(c) + 1).ToArray();
        }
        // unique squares (as colours, so squares in different palettes that look the same also count as one)
        var uniqueOf = new int[tileCount];
        var uniques = new List<int>();          // first square of each
        var keyOf = new Dictionary<string, int>();
        for (int t = 0; t < tileCount; t++)
        {
            string key = string.Join(",", TilePixels(pixels, t)) + "|" + paletteOf[t];
            if (!keyOf.TryGetValue(key, out int unique)) { unique = uniques.Count; uniques.Add(t); keyOf[key] = unique; }
            uniqueOf[t] = unique;
        }
        int limit = MaxTiles - 1;
        if (uniques.Count > limit)
        {
            if (!merge) throw new InvalidOperationException($"The picture has {uniques.Count} different 8 x 8 squares; at most {limit} fit.");
            // merge the closest pairs (same palette) until few enough are left
            var uniquePixels = uniques.Select(t => TilePixels(pixels, t).ToArray()).ToList();
            var pairs = new List<(int D, int A, int B)>();
            for (int a = 0; a < uniques.Count; a++)
                for (int b = a + 1; b < uniques.Count; b++)
                {
                    if (paletteOf[uniques[a]] != paletteOf[uniques[b]]) continue;
                    int distance = 0;
                    for (int i = 0; i < 64 && distance < 1 << 20; i++) distance += Dist(uniquePixels[a][i], uniquePixels[b][i]);
                    pairs.Add((distance, a, b));
                }
            pairs.Sort((x, y) => x.D.CompareTo(y.D));
            var parent = Enumerable.Range(0, uniques.Count).ToArray();
            int Root(int x) { while (parent[x] != x) x = parent[x] = parent[parent[x]]; return x; }
            int remaining = uniques.Count;
            foreach (var (_, a, b) in pairs)
            {
                if (remaining <= limit) break;
                int rootA = Root(a), rootB = Root(b);
                if (rootA == rootB) continue;
                parent[rootB] = rootA; remaining--; merged++;
            }
            for (int t = 0; t < tileCount; t++) uniqueOf[t] = Root(uniqueOf[t]);
            if (remaining > limit) throw new InvalidOperationException($"The picture still has {remaining} different squares after merging; at most {limit} fit.");
        }
        var tiles = new List<byte[]> { new byte[32] };
        var known = new Dictionary<string, int> { [System.Convert.ToBase64String(new byte[32])] = 0 };
        var layout = new ushort[tileCount];
        for (int t = 0; t < tileCount; t++)
        {
            int sourceSquare = uniques[uniqueOf[t]];
            var squareIndices = indices[sourceSquare];
            int found = -1, flips = 0;
            for (int flip = 0; flip < 4 && found < 0; flip++)
                if (known.TryGetValue(System.Convert.ToBase64String(Planar(squareIndices, (flip & 1) != 0, (flip & 2) != 0)), out int knownTile)) { found = knownTile; flips = flip; }
            if (found < 0)
            {
                var tileBytes = Planar(squareIndices, false, false);
                found = tiles.Count;
                tiles.Add(tileBytes);
                known[System.Convert.ToBase64String(tileBytes)] = found;
            }
            layout[t] = (ushort)(found | (FirstPalette + paletteOf[sourceSquare]) << 10 | ((flips & 1) != 0 ? 0x4000 : 0) | ((flips & 2) != 0 ? 0x8000 : 0));
        }
        if (tiles.Count > MaxTiles) throw new InvalidOperationException($"The picture has {tiles.Count - 1} different squares; at most {limit} fit.");
        return new Converted(tiles.SelectMany(b => b).ToArray(), layout, colours, tiles.Count, palettes.Count);
    }

    // an 8 x 8 tile in the console's 4bpp layout, flipped the way the layout word would show it
    private static byte[] Planar(int[] indices, bool hflip, bool vflip)
    {
        var tileBytes = new byte[32];
        for (int y = 0; y < 8; y++)
            for (int x = 0; x < 8; x++)
            {
                int colourIndex = indices[(vflip ? 7 - y : y) * 8 + (hflip ? 7 - x : x)];
                for (int plane = 0; plane < 4; plane++)
                    if ((colourIndex >> plane & 1) != 0) tileBytes[(plane >> 1) * 16 + y * 2 + (plane & 1)] |= (byte)(0x80 >> x);
            }
        return tileBytes;
    }

    // the background the way the game draws it (ARGB, 256 x 224)
    public static uint[] Render(Converted picture)
    {
        var output = new uint[Width * Height];
        for (int t = 0; t < TilesX * TilesY; t++)
        {
            ushort entry = picture.Layout[t];
            int tile = entry & 0x3FF, palette = (entry >> 10 & 7) - FirstPalette;
            bool hflip = (entry & 0x4000) != 0, vflip = (entry & 0x8000) != 0;
            int tx = t % TilesX, ty = t / TilesX;
            for (int y = 0; y < 8; y++)
                for (int x = 0; x < 8; x++)
                {
                    int sy = vflip ? 7 - y : y, sx = hflip ? 7 - x : x;
                    int tileAt = tile * 32;
                    int colourIndex = 0;
                    if (tileAt + 31 < picture.Tiles.Length)
                        for (int plane = 0; plane < 4; plane++)
                            if ((picture.Tiles[tileAt + (plane >> 1) * 16 + sy * 2 + (plane & 1)] & (0x80 >> sx)) != 0) colourIndex |= 1 << plane;
                    int colourAt = Math.Clamp(palette, 0, Palettes - 1) * 16 + colourIndex;
                    output[(ty * 8 + y) * Width + tx * 8 + x] = colourIndex == 0 ? 0xFF000000 : Argb(picture.Colours[colourAt]);
                }
        }
        return output;
    }
}
