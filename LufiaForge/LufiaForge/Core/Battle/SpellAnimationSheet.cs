namespace LufiaForge.Core.Battle;

/// <summary>
/// Turns a picture into a spell animation sheet (128 x 64, colour numbers 0-15, 0 = see-through) for
/// <see cref="SpellAnimations.Import"/>, and says what it did:
/// <list type="bullet">
/// <item>See-through: transparent pixels, or (a picture without any) the colour of its top-left corner.</item>
/// <item>Pixel art drawn bigger (every pixel 2 x 2, 3 x 3 ...) is brought back to its own size.</item>
/// <item>Frames are the cells of a grid, left to right, top to bottom, empty cells left out, each kept as drawn (so
/// sparks and pieces stay where they are) and centred in a frame of the cell size rounded up to 16. The grid is the
/// given cell size, or (none given) the smallest cell size that divides the picture and that no drawing crosses.</item>
/// <item>No grid fits: the frames are the drawings separated by empty space, each cut to what it covers.</item>
/// </list>
/// </summary>
public static class SpellAnimationSheet
{
    public sealed record Result(byte[] Indices, uint[] Palette, int FrameWidth, int FrameHeight, int Frames, List<string> Notes, List<Frame> FrameImages);

    /// <summary>One frame as cut from the picture: w x h pixels, 0xAARRGGBB, 0 = see-through.</summary>
    public sealed record Frame(int W, int H, uint[] Argb);

    /// <param name="argb">The picture, 0xAARRGGBB per pixel, row by row.</param>
    /// <param name="gridWidth">The cell size of the frames on the picture (after shrinking pixel art drawn bigger), or 0 to
    /// find it.</param>
    /// <param name="frames">At most this many frames (0 = all).</param>
    public static Result Prepare(uint[] argb, int width, int height, int gridWidth = 0, int gridHeight = 0, int frames = 0)
    {
        var notes = new List<string>();
        // see-through
        bool alpha = argb.Any(c => c >> 24 < 128);
        uint bg = argb[0] | 0xFF000000u;
        if (!alpha) notes.Add($"No see-through pixels, so the corner colour #{bg & 0xFFFFFF:X6} is see-through.");
        bool Empty(uint c) => alpha ? c >> 24 < 128 : (c | 0xFF000000u) == bg;

        // pixel art drawn bigger: the largest scale every run of one colour is a multiple of
        int scale = Scale(argb, width, height);
        if (scale > 1)
        {
            int w2 = width / scale, h2 = height / scale;
            var small = new uint[w2 * h2];
            for (int y = 0; y < h2; y++) for (int x = 0; x < w2; x++) small[y * w2 + x] = argb[y * scale * width + x * scale];
            argb = small; width = w2; height = h2;
            notes.Add($"The picture is drawn {scale} times bigger: used at {width} x {height}.");
        }
        var mask = argb.Select(c => !Empty(c)).ToArray();
        if (!mask.Any(m => m)) throw new InvalidOperationException("The picture is empty (everything is see-through).");

        // the frames: cells of a grid, else the drawings
        List<(int X, int Y, int W, int H)> boxes;
        int cw, ch;
        bool manual = gridWidth > 0 && gridHeight > 0;
        if (manual)
        {
            cw = gridWidth; ch = gridHeight;
            if (cw > 80 || ch > 64) throw new InvalidOperationException($"A frame can be at most 80 x 64 pixels (asked for {cw} x {ch}).");
            boxes = Cells(mask, width, height, cw, ch);
            notes.Add($"Cut into {cw} x {ch} cells (the frame size you set): {boxes.Count} with something in them.");
        }
        else if (FindGrid(mask, width, height) is { } grid)
        {
            (cw, ch) = grid;
            boxes = Cells(mask, width, height, cw, ch);
            notes.Add($"Frames on a {cw} x {ch} grid: {boxes.Count} with something in them, kept as drawn.");
        }
        else
        {
            boxes = Drawings(mask, width, height);
            cw = boxes.Max(b => b.W); ch = boxes.Max(b => b.H);
            notes.Add($"No even grid in the picture, so the drawings separated by empty space are the frames: {boxes.Count} of up to {cw} x {ch} pixels.");
        }
        if (boxes.Count == 0) throw new InvalidOperationException("No frame has anything in it.");
        if (frames > 0 && frames < boxes.Count) { boxes = boxes.Take(frames).ToList(); notes.Add($"The first {frames} are used."); }

        if (cw > 80 || ch > 64)
            throw new InvalidOperationException($"The frames are {cw} x {ch} pixels; a frame can be at most 80 x 64. Set a smaller frame size, or draw them smaller.");
        var cut = boxes.Select(b =>
        {
            var px = new uint[b.W * b.H];
            for (int y = 0; y < b.H; y++)
                for (int x = 0; x < b.W; x++)
                {
                    int sx = b.X + x, sy = b.Y + y;
                    if (sx < width && sy < height && mask[sy * width + sx]) px[y * b.W + x] = argb[sy * width + sx] | 0xFF000000u;
                }
            return new Frame(b.W, b.H, px);
        }).ToList();
        return Pack(cut, notes);
    }

    /// <summary>Frames (of one or more pictures) on one sheet: frame f in cell f, each centred in a frame of the biggest
    /// size rounded up to 16, at most 15 colours in all.</summary>
    public static Result Pack(List<Frame> frames, List<string>? notes = null, int minWidth = 0, int minHeight = 0)
    {
        notes ??= new List<string>();
        if (frames.Count == 0) throw new InvalidOperationException("There are no frames.");
        int cw = Math.Max(minWidth, frames.Max(f => f.W)), ch = Math.Max(minHeight, frames.Max(f => f.H));
        if (cw > 80 || ch > 64)
            throw new InvalidOperationException($"The biggest frame is {cw} x {ch} pixels; a frame can be at most 80 x 64.");
        int fw = Math.Max(16, (cw + 15) / 16 * 16), fh = Math.Max(16, (ch + 15) / 16 * 16);
        int max = SpellAnimations.MaxFrames(fw, fh);
        if (frames.Count > max)
            throw new InvalidOperationException($"{frames.Count} frames of {fw} x {fh}, but only {max} of that size fit on the game's sheet. Use fewer frames (the frames box), or smaller ones.");
        if ((fw != cw || fh != ch) && minWidth == 0) notes.Add($"Each goes in a {fw} x {fh} frame (the game's frames come in steps of 16).");

        var colours = new List<uint>();
        foreach (var f in frames)
            foreach (uint c in f.Argb)
            {
                if (c == 0) continue;
                uint k = c & 0xF8F8F8u;
                if (!colours.Contains(k)) colours.Add(k);
            }
        if (colours.Count > 15)
            throw new InvalidOperationException($"The frames use {colours.Count} colours; an animation can use 15 (plus see-through).");

        var idx = new byte[SpellAnimations.SheetWidth * SpellAnimations.SheetHeight];
        int perRow = 8 / (fw / 16);
        for (int n = 0; n < frames.Count; n++)
        {
            var f = frames[n];
            int cx = n % perRow * fw, cy = n / perRow * fh;
            int ox = cx + (fw - f.W) / 2, oy = cy + (fh - f.H) / 2;
            for (int y = 0; y < f.H; y++)
                for (int x = 0; x < f.W; x++)
                {
                    uint c = f.Argb[y * f.W + x];
                    if (c != 0) idx[(oy + y) * SpellAnimations.SheetWidth + ox + x] = (byte)(colours.IndexOf(c & 0xF8F8F8u) + 1);
                }
        }
        var pal = new uint[16];
        for (int i = 0; i < colours.Count; i++) pal[i + 1] = 0xFF000000u | colours[i];
        return new Result(idx, pal, fw, fh, frames.Count, notes, frames);
    }

    /// <summary>The cells of a cw x ch grid with something in them, left to right, top to bottom.</summary>
    private static List<(int X, int Y, int W, int H)> Cells(bool[] mask, int w, int h, int cw, int ch)
    {
        var list = new List<(int, int, int, int)>();
        for (int y0 = 0; y0 < h; y0 += ch)
            for (int x0 = 0; x0 < w; x0 += cw)
            {
                bool any = false;
                for (int y = y0; y < Math.Min(h, y0 + ch) && !any; y++)
                    for (int x = x0; x < Math.Min(w, x0 + cw); x++) if (mask[y * w + x]) { any = true; break; }
                if (any) list.Add((x0, y0, cw, ch));
            }
        return list;
    }

    /// <summary>The smallest cell size (16-80 x 16-64, steps of 8) that divides the picture and that no drawing
    /// crosses (no two touching pixels on either side of a cell edge); null if none.</summary>
    private static (int, int)? FindGrid(bool[] mask, int w, int h)
    {
        (int, int)? best = null;
        int bestArea = int.MaxValue;
        for (int cw = 16; cw <= 80; cw += 8)
        {
            if (w % cw != 0) continue;
            for (int ch = 16; ch <= 64; ch += 8)
            {
                if (h % ch != 0 || cw * ch >= bestArea) continue;
                if (Crosses(mask, w, h, cw, ch)) continue;
                best = (cw, ch); bestArea = cw * ch;
            }
        }
        return best;
    }

    private static bool Crosses(bool[] mask, int w, int h, int cw, int ch)
    {
        for (int x = cw; x < w; x += cw)
            for (int y = 0; y < h; y++)
                if (mask[y * w + x] && (mask[y * w + x - 1] || y > 0 && mask[(y - 1) * w + x - 1] || y + 1 < h && mask[(y + 1) * w + x - 1])) return true;
        for (int y = ch; y < h; y += ch)
            for (int x = 0; x < w; x++)
                if (mask[y * w + x] && (mask[(y - 1) * w + x] || x > 0 && mask[(y - 1) * w + x - 1] || x + 1 < w && mask[(y - 1) * w + x + 1])) return true;
        return false;
    }

    /// <summary>The drawings: rows of them separated by empty rows, then in each row the ones separated by empty
    /// columns (a gap of 2 or more), each cut to what it covers.</summary>
    private static List<(int X, int Y, int W, int H)> Drawings(bool[] mask, int w, int h)
    {
        var list = new List<(int, int, int, int)>();
        foreach (var (y0, y1) in Runs(Enumerable.Range(0, h).Select(y => Enumerable.Range(0, w).Any(x => mask[y * w + x])).ToArray()))
            foreach (var (x0, x1) in Runs(Enumerable.Range(0, w).Select(x => Enumerable.Range(y0, y1 - y0).Any(y => mask[y * w + x])).ToArray()))
            {
                int top = Enumerable.Range(y0, y1 - y0).First(y => Enumerable.Range(x0, x1 - x0).Any(x => mask[y * w + x]));
                int bottom = Enumerable.Range(y0, y1 - y0).Last(y => Enumerable.Range(x0, x1 - x0).Any(x => mask[y * w + x]));
                list.Add((x0, top, x1 - x0, bottom - top + 1));
            }
        return list;
    }

    /// <summary>Stretches of "used" separated by at least 2 unused (as start, end exclusive).</summary>
    private static List<(int, int)> Runs(bool[] used)
    {
        var runs = new List<(int, int)>();
        int i = 0;
        while (i < used.Length)
        {
            if (!used[i]) { i++; continue; }
            int start = i, end = i;
            while (i < used.Length)
            {
                if (used[i]) { end = i + 1; i++; continue; }
                int gap = i;
                while (gap < used.Length && !used[gap]) gap++;
                if (gap - i >= 2 || gap >= used.Length) break;
                i = gap;
            }
            runs.Add((start, end));
            i = Math.Max(i, end);
        }
        return runs;
    }

    /// <summary>The biggest n where every horizontal and vertical run of one colour (away from the edges) is a multiple
    /// of n, and the picture's size too; 1 for ordinary pixel art.</summary>
    private static int Scale(uint[] px, int w, int h)
    {
        int g = 0;
        int Gcd(int a, int b) { while (b != 0) (a, b) = (b, a % b); return a; }
        for (int y = 0; y < h && g != 1; y++)
            for (int x = 0; x < w;)
            {
                int s = x; uint c = px[y * w + x];
                while (x < w && px[y * w + x] == c) x++;
                if (s > 0 && x < w) g = Gcd(g, x - s);
                if (g == 1) break;
            }
        for (int x = 0; x < w && g != 1; x++)
            for (int y = 0; y < h;)
            {
                int s = y; uint c = px[y * w + x];
                while (y < h && px[y * w + x] == c) y++;
                if (s > 0 && y < h) g = Gcd(g, y - s);
                if (g == 1) break;
            }
        if (g <= 1) return 1;
        // the picture's own size may have a partial border: only use scales that leave a sensible picture
        return g > 16 ? 1 : g;
    }
}
