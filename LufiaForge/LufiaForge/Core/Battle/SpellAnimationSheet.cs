namespace LufiaForge.Core.Battle;

/// <summary>
/// Turns a picture into a spell animation sheet (128 x 64, colour numbers 0-15, 0 = see-through) for
/// <see cref="SpellAnimations.Import"/>, and says what it did:
/// <list type="bullet">
/// <item>See-through: transparent pixels, or (a picture without any) the colour of its top-left corner.</item>
/// <item>Pixel art drawn bigger (every pixel 2 x 2, 3 x 3 ...) is brought back to its own size.</item>
/// <item>A 128 x 64 picture is a sheet already: frames of the given size, left to right, top to bottom.</item>
/// <item>Any other picture: the frames are the drawings separated by empty space (rows from the top, then left to
/// right); the frame size is the biggest one rounded up to 16 pixels, each drawing centred in its frame.</item>
/// </list>
/// </summary>
public static class SpellAnimationSheet
{
    public sealed record Result(byte[] Indices, uint[] Palette, int FrameWidth, int FrameHeight, int Frames, List<string> Notes);

    /// <param name="argb">The picture, 0xAARRGGBB per pixel, row by row.</param>
    /// <param name="frameWidth">For a ready 128 x 64 sheet: its frame size and count.</param>
    public static Result Prepare(uint[] argb, int width, int height, int frameWidth, int frameHeight, int frames)
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
        // the drawings
        List<(int X, int Y, int W, int H)> boxes;
        int fw, fh;
        if (width == SpellAnimations.SheetWidth && height == SpellAnimations.SheetHeight)
        {
            fw = frameWidth; fh = frameHeight;
            int max = SpellAnimations.MaxFrames(fw, fh);
            if (max == 0) throw new InvalidOperationException("Frames are 16-80 pixels wide and 16-64 high, in steps of 16.");
            int per = 8 / (fw / 16);
            boxes = Enumerable.Range(0, Math.Clamp(frames, 1, max)).Select(f => (f % per * fw, f / per * fh, fw, fh)).ToList();
            notes.Add($"A ready 128 x 64 sheet: {boxes.Count} frames of {fw} x {fh}, left to right, top to bottom.");
        }
        else
        {
            boxes = Drawings(mask, width, height);
            if (boxes.Count == 0) throw new InvalidOperationException("The picture is empty (everything is see-through).");
            int maxW = boxes.Max(b => b.W), maxH = boxes.Max(b => b.H);
            fw = Math.Max(16, (maxW + 15) / 16 * 16); fh = Math.Max(16, (maxH + 15) / 16 * 16);
            if (fw > 80 || fh > 64)
                throw new InvalidOperationException($"The biggest frame is {maxW} x {maxH} pixels; a frame can be at most 80 x 64. Draw the frames smaller.");
            int max = SpellAnimations.MaxFrames(fw, fh);
            if (boxes.Count > max)
                throw new InvalidOperationException($"Found {boxes.Count} frames of up to {maxW} x {maxH} pixels ({fw} x {fh} cells), but only {max} of that size fit on the game's sheet. Use fewer or smaller frames.");
            notes.Add($"Found {boxes.Count} frames of up to {maxW} x {maxH} pixels: each goes in a {fw} x {fh} frame.");
        }

        // colours
        var colours = new List<uint>();
        foreach (var b in boxes)
            for (int y = b.Y; y < b.Y + b.H && y < height; y++)
                for (int x = b.X; x < b.X + b.W && x < width; x++)
                {
                    int i = y * width + x;
                    if (!mask[i]) continue;
                    uint c = argb[i] & 0xF8F8F8u;
                    if (!colours.Contains(c)) colours.Add(c);
                }
        if (colours.Count > 15)
            throw new InvalidOperationException($"The frames use {colours.Count} colours; an animation can use 15 (plus see-through).");

        // the sheet: frame f in cell f, the drawing centred in it
        var idx = new byte[SpellAnimations.SheetWidth * SpellAnimations.SheetHeight];
        int perRow = 8 / (fw / 16);
        for (int f = 0; f < boxes.Count; f++)
        {
            var b = boxes[f];
            int cx = f % perRow * fw, cy = f / perRow * fh;
            int ox = cx + (fw - Math.Min(b.W, fw)) / 2, oy = cy + (fh - Math.Min(b.H, fh)) / 2;
            for (int y = 0; y < Math.Min(b.H, fh); y++)
                for (int x = 0; x < Math.Min(b.W, fw); x++)
                {
                    int sx = b.X + x, sy = b.Y + y;
                    if (sx >= width || sy >= height) continue;
                    int i = sy * width + sx;
                    if (!mask[i]) continue;
                    idx[(oy + y) * SpellAnimations.SheetWidth + ox + x] = (byte)(colours.IndexOf(argb[i] & 0xF8F8F8u) + 1);
                }
        }
        var pal = new uint[16];
        for (int i = 0; i < colours.Count; i++) pal[i + 1] = 0xFF000000u | colours[i];
        return new Result(idx, pal, fw, fh, boxes.Count, notes);
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
