namespace LufiaForge.Core.Battle;

public static partial class BattleBackgrounds
{
    /// <summary>A picture made ready for the game: its tiles, 32 x 28 layout and colours (BGR555, 16 per palette).</summary>
    public sealed record Converted(byte[] Tiles, ushort[] Layout, ushort[] Colours, int TileCount, int PaletteCount);

    private const int TilesX = 32, TilesY = 28;

    private static ushort Bgr(uint c) => (ushort)(((c >> 3) & 0x1F) << 10 | ((c >> 11) & 0x1F) << 5 | ((c >> 19) & 0x1F));
    private static uint Argb(ushort c)
    {
        uint r = (uint)(c & 0x1F), g = (uint)(c >> 5 & 0x1F), b = (uint)(c >> 10 & 0x1F);
        return 0xFF000000 | (r << 3 | r >> 2) << 16 | (g << 3 | g >> 2) << 8 | (b << 3 | b >> 2);
    }

    /// <summary>
    /// A 256 x 224 picture (ARGB) as it is: each 8 x 8 square gets one of six 15-colour palettes, repeated squares are
    /// stored once (also mirrored ones). Throws with a message the user can act on when it doesn't fit; see
    /// <see cref="Fit"/> for making any picture fit.
    /// </summary>
    public static Converted Convert(uint[] argb, int width, int height)
    {
        if (width != Width || height != Height) throw new InvalidOperationException($"A battle background is {Width} x {Height} (this picture is {width} x {height}).");
        var px = argb.Select(Bgr).ToArray();
        var tileColours = new List<HashSet<ushort>>();
        for (int t = 0; t < TilesX * TilesY; t++)
        {
            var set = TilePixels(px, t).ToHashSet();
            if (set.Count > 15) throw new InvalidOperationException($"The 8 x 8 square at {t % TilesX * 8}, {t / TilesX * 8} has {set.Count} colours; a square can have at most 15.");
            tileColours.Add(set);
        }
        // palettes: biggest colour sets first, each into the palette it grows least
        var palettes = new List<HashSet<ushort>>();
        var palOf = new int[tileColours.Count];
        foreach (int t in Enumerable.Range(0, tileColours.Count).OrderByDescending(i => tileColours[i].Count))
        {
            var set = tileColours[t];
            int best = -1, grow = int.MaxValue;
            for (int p = 0; p < palettes.Count; p++)
            {
                int g = set.Count(c => !palettes[p].Contains(c));
                if (palettes[p].Count + g <= 15 && g < grow) { best = p; grow = g; }
            }
            if (best < 0)
            {
                if (palettes.Count == Palettes) throw new InvalidOperationException($"The picture needs more than {Palettes} palettes of 15 colours.");
                palettes.Add(new HashSet<ushort>());
                best = palettes.Count - 1;
            }
            palettes[best].UnionWith(set);
            palOf[t] = best;
        }
        var built = Build(px, palOf, palettes.Select(p => p.OrderBy(c => c).ToList()).ToList(), merge: false, out _);
        return built;
    }

    /// <summary>
    /// Any picture made into a battle background, and what was done to it: scaled to 256 x 224 (a picture that is a
    /// whole-number size up is shrunk exactly; others are scaled to cover the screen and cut to it from the middle),
    /// its colours brought down to six palettes of 15 when it has more (the squares grouped by colour, each group's
    /// colours picked by median cut), and the most alike squares merged when there are more than 511 different ones.
    /// </summary>
    public static (Converted Picture, List<string> Notes) Fit(uint[] argb, int width, int height)
    {
        var notes = new List<string>();
        var pic = argb;
        if (width != Width || height != Height)
        {
            pic = Scale(argb, width, height, out string how);
            notes.Add(how);
        }
        try
        {
            return (Convert(pic, Width, Height), notes);
        }
        catch (InvalidOperationException) { }
        var px = pic.Select(Bgr).ToArray();
        var (palOf, palettes) = Quantize(px);
        int colours = palettes.Sum(p => p.Count);
        notes.Add($"colours brought down to {colours} in {palettes.Count} palettes");
        var conv = Build(px, palOf, palettes, merge: true, out int merged);
        if (merged > 0) notes.Add($"{merged} squares merged with ones most like them");
        return (conv, notes);
    }

    private static IEnumerable<ushort> TilePixels(ushort[] px, int t)
    {
        int tx = t % TilesX, ty = t / TilesX;
        for (int y = 0; y < 8; y++) for (int x = 0; x < 8; x++) yield return px[(ty * 8 + y) * Width + tx * 8 + x];
    }

    private static uint[] Scale(uint[] argb, int w, int h, out string how)
    {
        var o = new uint[Width * Height];
        if (w % Width == 0 && h % Height == 0 && w / Width == h / Height)
        {
            int k = w / Width;
            for (int y = 0; y < Height; y++) for (int x = 0; x < Width; x++) o[y * Width + x] = argb[(y * k) * w + x * k];
            how = $"shrunk {k} times ({w} x {h})";
            return o;
        }
        // cover the screen, keep the shape, cut from the middle; each pixel is the average of the area it covers
        double s = Math.Max((double)Width / w, (double)Height / h);
        double offX = (w * s - Width) / 2, offY = (h * s - Height) / 2;
        for (int y = 0; y < Height; y++)
            for (int x = 0; x < Width; x++)
            {
                int x0 = (int)((x + offX) / s), x1 = Math.Max(x0 + 1, (int)((x + 1 + offX) / s));
                int y0 = (int)((y + offY) / s), y1 = Math.Max(y0 + 1, (int)((y + 1 + offY) / s));
                long r = 0, g = 0, b = 0, n = 0;
                for (int yy = y0; yy < Math.Min(h, y1); yy++)
                    for (int xx = x0; xx < Math.Min(w, x1); xx++)
                    {
                        uint c = argb[yy * w + xx];
                        if ((c >> 24) < 0x80) c = 0xFF000000;   // see-through counts as black
                        r += c >> 16 & 0xFF; g += c >> 8 & 0xFF; b += c & 0xFF; n++;
                    }
                if (n == 0) n = 1;
                o[y * Width + x] = 0xFF000000 | (uint)(r / n) << 16 | (uint)(g / n) << 8 | (uint)(b / n);
            }
        how = $"scaled from {w} x {h}" + (Math.Abs((double)w / h - (double)Width / Height) > 0.01 ? " and cut to 256 x 224" : "");
        return o;
    }

    private static (int R, int G, int B) Rgb(ushort c) => (c & 0x1F, c >> 5 & 0x1F, c >> 10 & 0x1F);
    private static int Dist(ushort a, ushort b)
    {
        var (r1, g1, b1) = Rgb(a); var (r2, g2, b2) = Rgb(b);
        int dr = r1 - r2, dg = g1 - g2, db = b1 - b2;
        return 2 * dr * dr + 4 * dg * dg + 3 * db * db;
    }

    /// <summary>Six palettes of 15 for any picture: the squares grouped by their average colour (k-means), each group's
    /// colours picked by median cut, then each square takes the palette that draws it best.</summary>
    private static (int[] PalOf, List<List<ushort>> Palettes) Quantize(ushort[] px)
    {
        int n = TilesX * TilesY;
        var mean = new (double R, double G, double B)[n];
        for (int t = 0; t < n; t++)
        {
            double r = 0, g = 0, b = 0;
            foreach (var c in TilePixels(px, t)) { var (cr, cg, cb) = Rgb(c); r += cr; g += cg; b += cb; }
            mean[t] = (r / 64, g / 64, b / 64);
        }
        int k = Palettes;
        // farthest-point start, then a few k-means rounds
        var centres = new List<(double R, double G, double B)> { mean[0] };
        while (centres.Count < k)
            centres.Add(mean.OrderByDescending(m => centres.Min(c => Sq(m, c))).First());
        var group = new int[n];
        for (int round = 0; round < 12; round++)
        {
            for (int t = 0; t < n; t++) group[t] = Enumerable.Range(0, k).OrderBy(i => Sq(mean[t], centres[i])).First();
            for (int i = 0; i < k; i++)
            {
                var mine = Enumerable.Range(0, n).Where(t => group[t] == i).ToList();
                if (mine.Count > 0) centres[i] = (mine.Average(t => mean[t].R), mine.Average(t => mean[t].G), mine.Average(t => mean[t].B));
            }
        }
        var palettes = new List<List<ushort>>();
        for (int i = 0; i < k; i++)
        {
            var colours = Enumerable.Range(0, n).Where(t => group[t] == i).SelectMany(t => TilePixels(px, t)).ToList();
            if (colours.Count > 0) palettes.Add(MedianCut(colours, 15));
        }
        var palOf = new int[n];
        for (int t = 0; t < n; t++)
        {
            var pixels = TilePixels(px, t).ToList();
            palOf[t] = Enumerable.Range(0, palettes.Count).OrderBy(p => pixels.Sum(c => palettes[p].Min(q => Dist(c, q)))).First();
        }
        // each pixel to the nearest colour of its square's palette
        for (int t = 0; t < n; t++)
        {
            int tx = t % TilesX, ty = t / TilesX;
            var pal = palettes[palOf[t]];
            for (int y = 0; y < 8; y++)
                for (int x = 0; x < 8; x++)
                {
                    int i = (ty * 8 + y) * Width + tx * 8 + x;
                    px[i] = pal.OrderBy(q => Dist(px[i], q)).First();
                }
        }
        // only the colours still used
        for (int p = 0; p < palettes.Count; p++)
        {
            var used = Enumerable.Range(0, n).Where(t => palOf[t] == p).SelectMany(t => TilePixels(px, t)).ToHashSet();
            palettes[p] = palettes[p].Where(used.Contains).Distinct().OrderBy(c => c).ToList();
        }
        return (palOf, palettes);

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
            foreach (var b in boxes)
            {
                if (b.Distinct().Count() < 2) continue;
                for (int a = 0; a < 3; a++)
                {
                    int lo = b.Min(c => Channel(c, a)), hi = b.Max(c => Channel(c, a));
                    if (hi - lo > spread) { spread = hi - lo; widest = b; axis = a; }
                }
            }
            if (widest == null) break;
            var sorted = widest.OrderBy(c => Channel(c, axis)).ToList();
            boxes.Remove(widest);
            boxes.Add(sorted.Take(sorted.Count / 2).ToList());
            boxes.Add(sorted.Skip(sorted.Count / 2).ToList());
        }
        return boxes.Where(b => b.Count > 0).Select(b =>
        {
            int r = (int)Math.Round(b.Average(c => Channel(c, 0))), g = (int)Math.Round(b.Average(c => Channel(c, 1))), bl = (int)Math.Round(b.Average(c => Channel(c, 2)));
            return (ushort)(bl << 10 | g << 5 | r);
        }).Distinct().ToList();

        static int Channel(ushort c, int a) => a == 0 ? c & 0x1F : a == 1 ? c >> 5 & 0x1F : c >> 10 & 0x1F;
    }

    /// <summary>
    /// The tiles, layout and colours from 15-bit pixels, each square's palette and the palettes. Repeated squares (also
    /// mirrored) are stored once; with <paramref name="merge"/> the most alike squares share a tile until at most 511
    /// are left (tile 0 stays empty: the upper layer is cleared to it).
    /// </summary>
    private static Converted Build(ushort[] px, int[] palOf, List<List<ushort>> palettes, bool merge, out int merged)
    {
        merged = 0;
        int n = TilesX * TilesY;
        var colours = new ushort[Palettes * 16];
        for (int p = 0; p < palettes.Count; p++) for (int i = 0; i < palettes[p].Count; i++) colours[p * 16 + 1 + i] = palettes[p][i];
        var idx = new int[n][];
        for (int t = 0; t < n; t++)
        {
            var pal = palettes[palOf[t]];
            idx[t] = TilePixels(px, t).Select(c => pal.IndexOf(c) + 1).ToArray();
        }
        // unique squares (as colours, so squares in different palettes that look the same also count as one)
        var uniqueOf = new int[n];
        var uniques = new List<int>();          // first square of each
        var keyOf = new Dictionary<string, int>();
        for (int t = 0; t < n; t++)
        {
            string key = string.Join(",", TilePixels(px, t)) + "|" + palOf[t];
            if (!keyOf.TryGetValue(key, out int u)) { u = uniques.Count; uniques.Add(t); keyOf[key] = u; }
            uniqueOf[t] = u;
        }
        int limit = MaxTiles - 1;
        if (uniques.Count > limit)
        {
            if (!merge) throw new InvalidOperationException($"The picture has {uniques.Count} different 8 x 8 squares; at most {limit} fit.");
            // merge the closest pairs (same palette) until few enough are left
            var pix = uniques.Select(t => TilePixels(px, t).ToArray()).ToList();
            var pairs = new List<(int D, int A, int B)>();
            for (int a = 0; a < uniques.Count; a++)
                for (int b = a + 1; b < uniques.Count; b++)
                {
                    if (palOf[uniques[a]] != palOf[uniques[b]]) continue;
                    int d = 0;
                    for (int i = 0; i < 64 && d < 1 << 20; i++) d += Dist(pix[a][i], pix[b][i]);
                    pairs.Add((d, a, b));
                }
            pairs.Sort((x, y) => x.D.CompareTo(y.D));
            var parent = Enumerable.Range(0, uniques.Count).ToArray();
            int Root(int x) { while (parent[x] != x) x = parent[x] = parent[parent[x]]; return x; }
            int left = uniques.Count;
            foreach (var (_, a, b) in pairs)
            {
                if (left <= limit) break;
                int ra = Root(a), rb = Root(b);
                if (ra == rb) continue;
                parent[rb] = ra; left--; merged++;
            }
            for (int t = 0; t < n; t++) uniqueOf[t] = Root(uniqueOf[t]);
            if (left > limit) throw new InvalidOperationException($"The picture still has {left} different squares after merging; at most {limit} fit.");
        }
        var tiles = new List<byte[]> { new byte[32] };
        var known = new Dictionary<string, int> { [System.Convert.ToBase64String(new byte[32])] = 0 };
        var layout = new ushort[n];
        for (int t = 0; t < n; t++)
        {
            int src = uniques[uniqueOf[t]];
            var ix = idx[src];
            int found = -1, flips = 0;
            for (int f = 0; f < 4 && found < 0; f++)
                if (known.TryGetValue(System.Convert.ToBase64String(Planar(ix, (f & 1) != 0, (f & 2) != 0)), out int kk)) { found = kk; flips = f; }
            if (found < 0)
            {
                var b = Planar(ix, false, false);
                found = tiles.Count;
                tiles.Add(b);
                known[System.Convert.ToBase64String(b)] = found;
            }
            layout[t] = (ushort)(found | (FirstPalette + palOf[src]) << 10 | ((flips & 1) != 0 ? 0x4000 : 0) | ((flips & 2) != 0 ? 0x8000 : 0));
        }
        if (tiles.Count > MaxTiles) throw new InvalidOperationException($"The picture has {tiles.Count - 1} different squares; at most {limit} fit.");
        return new Converted(tiles.SelectMany(b => b).ToArray(), layout, colours, tiles.Count, palettes.Count);
    }

    /// <summary>An 8 x 8 tile in the console's 4bpp layout, flipped as the layout word would show it.</summary>
    private static byte[] Planar(int[] idx, bool hflip, bool vflip)
    {
        var b = new byte[32];
        for (int y = 0; y < 8; y++)
            for (int x = 0; x < 8; x++)
            {
                int v = idx[(vflip ? 7 - y : y) * 8 + (hflip ? 7 - x : x)];
                for (int p = 0; p < 4; p++)
                    if ((v >> p & 1) != 0) b[(p >> 1) * 16 + y * 2 + (p & 1)] |= (byte)(0x80 >> x);
            }
        return b;
    }

    /// <summary>The background as the game draws it (ARGB, 256 x 224).</summary>
    public static uint[] Render(Converted c)
    {
        var o = new uint[Width * Height];
        for (int t = 0; t < TilesX * TilesY; t++)
        {
            ushort e = c.Layout[t];
            int tile = e & 0x3FF, pal = (e >> 10 & 7) - FirstPalette;
            bool h = (e & 0x4000) != 0, v = (e & 0x8000) != 0;
            int tx = t % TilesX, ty = t / TilesX;
            for (int y = 0; y < 8; y++)
                for (int x = 0; x < 8; x++)
                {
                    int sy = v ? 7 - y : y, sx = h ? 7 - x : x;
                    int at = tile * 32;
                    int val = 0;
                    if (at + 31 < c.Tiles.Length)
                        for (int p = 0; p < 4; p++)
                            if ((c.Tiles[at + (p >> 1) * 16 + sy * 2 + (p & 1)] & (0x80 >> sx)) != 0) val |= 1 << p;
                    int ci = Math.Clamp(pal, 0, Palettes - 1) * 16 + val;
                    o[(ty * 8 + y) * Width + tx * 8 + x] = val == 0 ? 0xFF000000 : Argb(c.Colours[ci]);
                }
        }
        return o;
    }
}
