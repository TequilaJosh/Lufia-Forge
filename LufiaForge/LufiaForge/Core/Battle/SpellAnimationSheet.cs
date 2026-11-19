namespace LufiaForge.Core.Battle;

// turns a picture into a spell animation sheet (128 x 64, colour numbers 0-15, 0 = see-through) for
// SpellAnimations.Import, and says what it did:
// - see-through: transparent pixels, or (for a picture without any) the colour of its top-left corner
// - pixel art drawn bigger (every pixel 2 x 2, 3 x 3 ...) gets brought back to its own size
// - frames are the cells of a grid, left to right, top to bottom, empty cells left out. each is kept as drawn (so
//   sparks and pieces stay where they are) and centred in a frame of the cell size rounded up to 16. the grid is the
//   given cell size, or if none is given, the smallest cell size that divides the picture and that no drawing crosses
// - if no grid fits, the frames are the drawings separated by empty space, each cut to what it covers
public static class SpellAnimationSheet
{
    public sealed record Result(byte[] Indices, uint[] Palette, int FrameWidth, int FrameHeight, int Frames, List<string> Notes, List<Frame> FrameImages);

    // one frame as cut from the picture: w x h pixels, 0xAARRGGBB, 0 = see-through
    public sealed record Frame(int W, int H, uint[] Argb);

    // argb: the picture, 0xAARRGGBB per pixel, row by row.
    // gridWidth: the cell size of the frames on the picture (after shrinking pixel art drawn bigger), or 0 to find it.
    // frames: at most this many frames (0 = all)
    public static Result Prepare(uint[] argb, int width, int height, int gridWidth = 0, int gridHeight = 0, int frames = 0)
    {
        var notes = new List<string>();
        // see-through
        bool alpha = argb.Any(c => c >> 24 < 128);
        uint background = argb[0] | 0xFF000000u;
        if (!alpha) notes.Add($"No see-through pixels, so the corner colour #{background & 0xFFFFFF:X6} is see-through.");
        bool Empty(uint c) => alpha ? c >> 24 < 128 : (c | 0xFF000000u) == background;

        // pixel art drawn bigger: the largest scale every run of one colour is a multiple of
        int scale = Scale(argb, width, height);
        if (scale > 1)
        {
            int smallWidth = width / scale, smallHeight = height / scale;
            var small = new uint[smallWidth * smallHeight];
            for (int y = 0; y < smallHeight; y++) for (int x = 0; x < smallWidth; x++) small[y * smallWidth + x] = argb[y * scale * width + x * scale];
            argb = small; width = smallWidth; height = smallHeight;
            notes.Add($"The picture is drawn {scale} times bigger: used at {width} x {height}.");
        }
        var mask = argb.Select(c => !Empty(c)).ToArray();
        if (!mask.Any(m => m)) throw new InvalidOperationException("The picture is empty (everything is see-through).");

        // the frames: cells of a grid, otherwise the drawings
        List<(int X, int Y, int W, int H)> boxes;
        int cellWidth, cellHeight;
        bool manual = gridWidth > 0 && gridHeight > 0;
        if (manual)
        {
            cellWidth = gridWidth; cellHeight = gridHeight;
            if (cellWidth > 80 || cellHeight > 64) throw new InvalidOperationException($"A frame can be at most 80 x 64 pixels (asked for {cellWidth} x {cellHeight}).");
            boxes = Cells(mask, width, height, cellWidth, cellHeight);
            notes.Add($"Cut into {cellWidth} x {cellHeight} cells (the frame size you set): {boxes.Count} with something in them.");
        }
        else if (FindGrid(mask, width, height) is { } grid)
        {
            (cellWidth, cellHeight) = grid;
            boxes = Cells(mask, width, height, cellWidth, cellHeight);
            notes.Add($"Frames on a {cellWidth} x {cellHeight} grid: {boxes.Count} with something in them, kept as drawn.");
        }
        else
        {
            boxes = Drawings(mask, width, height);
            cellWidth = boxes.Max(b => b.W); cellHeight = boxes.Max(b => b.H);
            notes.Add($"No even grid in the picture, so the drawings separated by empty space are the frames: {boxes.Count} of up to {cellWidth} x {cellHeight} pixels.");
        }
        if (boxes.Count == 0) throw new InvalidOperationException("No frame has anything in it.");
        if (frames > 0 && frames < boxes.Count) { boxes = boxes.Take(frames).ToList(); notes.Add($"The first {frames} are used."); }

        if (cellWidth > 80 || cellHeight > 64)
            throw new InvalidOperationException($"The frames are {cellWidth} x {cellHeight} pixels; a frame can be at most 80 x 64. Set a smaller frame size, or draw them smaller.");
        var cut = boxes.Select(box =>
        {
            var pixels = new uint[box.W * box.H];
            for (int y = 0; y < box.H; y++)
                for (int x = 0; x < box.W; x++)
                {
                    int sx = box.X + x, sy = box.Y + y;
                    if (sx < width && sy < height && mask[sy * width + sx]) pixels[y * box.W + x] = argb[sy * width + sx] | 0xFF000000u;
                }
            return new Frame(box.W, box.H, pixels);
        }).ToList();
        return Pack(cut, notes);
    }

    // frames (from one or more pictures) on one sheet: frame f in cell f, each centred in a frame of the biggest
    // size rounded up to 16, at most 15 colours in all
    public static Result Pack(List<Frame> frames, List<string>? notes = null, int minWidth = 0, int minHeight = 0)
    {
        notes ??= new List<string>();
        if (frames.Count == 0) throw new InvalidOperationException("There are no frames.");
        int biggestWidth = Math.Max(minWidth, frames.Max(f => f.W)), biggestHeight = Math.Max(minHeight, frames.Max(f => f.H));
        if (biggestWidth > 80 || biggestHeight > 64)
            throw new InvalidOperationException($"The biggest frame is {biggestWidth} x {biggestHeight} pixels; a frame can be at most 80 x 64.");
        int frameWidth = Math.Max(16, (biggestWidth + 15) / 16 * 16), frameHeight = Math.Max(16, (biggestHeight + 15) / 16 * 16);
        int max = SpellAnimations.MaxFrames(frameWidth, frameHeight);
        if (frames.Count > max)
            throw new InvalidOperationException($"{frames.Count} frames of {frameWidth} x {frameHeight}, but only {max} of that size fit on the game's sheet. Use fewer frames (the frames box), or smaller ones.");
        if ((frameWidth != biggestWidth || frameHeight != biggestHeight) && minWidth == 0) notes.Add($"Each goes in a {frameWidth} x {frameHeight} frame (the game's frames come in steps of 16).");

        var colours = new List<uint>();
        foreach (var frame in frames)
            foreach (uint c in frame.Argb)
            {
                if (c == 0) continue;
                uint key = c & 0xF8F8F8u;
                if (!colours.Contains(key)) colours.Add(key);
            }
        if (colours.Count > 15)
            throw new InvalidOperationException($"The frames use {colours.Count} colours; an animation can use 15 (plus see-through).");

        var indices = new byte[SpellAnimations.SheetWidth * SpellAnimations.SheetHeight];
        int perRow = 8 / (frameWidth / 16);
        for (int index = 0; index < frames.Count; index++)
        {
            var frame = frames[index];
            int cx = index % perRow * frameWidth, cy = index / perRow * frameHeight;
            int ox = cx + (frameWidth - frame.W) / 2, oy = cy + (frameHeight - frame.H) / 2;
            for (int y = 0; y < frame.H; y++)
                for (int x = 0; x < frame.W; x++)
                {
                    uint c = frame.Argb[y * frame.W + x];
                    if (c != 0) indices[(oy + y) * SpellAnimations.SheetWidth + ox + x] = (byte)(colours.IndexOf(c & 0xF8F8F8u) + 1);
                }
        }
        var palette = new uint[16];
        for (int i = 0; i < colours.Count; i++) palette[i + 1] = 0xFF000000u | colours[i];
        return new Result(indices, palette, frameWidth, frameHeight, frames.Count, notes, frames);
    }

    // the cells of a cellWidth x cellHeight grid with something in them, left to right, top to bottom
    private static List<(int X, int Y, int W, int H)> Cells(bool[] mask, int width, int height, int cellWidth, int cellHeight)
    {
        var list = new List<(int, int, int, int)>();
        for (int y0 = 0; y0 < height; y0 += cellHeight)
            for (int x0 = 0; x0 < width; x0 += cellWidth)
            {
                bool any = false;
                for (int y = y0; y < Math.Min(height, y0 + cellHeight) && !any; y++)
                    for (int x = x0; x < Math.Min(width, x0 + cellWidth); x++) if (mask[y * width + x]) { any = true; break; }
                if (any) list.Add((x0, y0, cellWidth, cellHeight));
            }
        return list;
    }

    // the smallest cell size (16-80 x 16-64, steps of 8) that divides the picture and that no drawing
    // crosses (no two touching pixels on either side of a cell edge), null if there's none
    private static (int, int)? FindGrid(bool[] mask, int width, int height)
    {
        (int, int)? best = null;
        int bestArea = int.MaxValue;
        for (int cellWidth = 16; cellWidth <= 80; cellWidth += 8)
        {
            if (width % cellWidth != 0) continue;
            for (int cellHeight = 16; cellHeight <= 64; cellHeight += 8)
            {
                if (height % cellHeight != 0 || cellWidth * cellHeight >= bestArea) continue;
                if (Crosses(mask, width, height, cellWidth, cellHeight)) continue;
                best = (cellWidth, cellHeight); bestArea = cellWidth * cellHeight;
            }
        }
        return best;
    }

    private static bool Crosses(bool[] mask, int width, int height, int cellWidth, int cellHeight)
    {
        for (int x = cellWidth; x < width; x += cellWidth)
            for (int y = 0; y < height; y++)
                if (mask[y * width + x] && (mask[y * width + x - 1] || y > 0 && mask[(y - 1) * width + x - 1] || y + 1 < height && mask[(y + 1) * width + x - 1])) return true;
        for (int y = cellHeight; y < height; y += cellHeight)
            for (int x = 0; x < width; x++)
                if (mask[y * width + x] && (mask[(y - 1) * width + x] || x > 0 && mask[(y - 1) * width + x - 1] || x + 1 < width && mask[(y - 1) * width + x + 1])) return true;
        return false;
    }

    // the drawings: rows of them separated by empty rows, then in each row the ones separated by empty
    // columns (a gap of 2 or more), each cut to what it covers
    private static List<(int X, int Y, int W, int H)> Drawings(bool[] mask, int width, int height)
    {
        var list = new List<(int, int, int, int)>();
        foreach (var (y0, y1) in Runs(Enumerable.Range(0, height).Select(y => Enumerable.Range(0, width).Any(x => mask[y * width + x])).ToArray()))
            foreach (var (x0, x1) in Runs(Enumerable.Range(0, width).Select(x => Enumerable.Range(y0, y1 - y0).Any(y => mask[y * width + x])).ToArray()))
            {
                int top = Enumerable.Range(y0, y1 - y0).First(y => Enumerable.Range(x0, x1 - x0).Any(x => mask[y * width + x]));
                int bottom = Enumerable.Range(y0, y1 - y0).Last(y => Enumerable.Range(x0, x1 - x0).Any(x => mask[y * width + x]));
                list.Add((x0, top, x1 - x0, bottom - top + 1));
            }
        return list;
    }

    // stretches of "used" separated by at least 2 unused (as start, end exclusive)
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

    // the biggest n where every horizontal and vertical run of one colour (away from the edges) is a multiple
    // of n, and the picture's size too. 1 for ordinary pixel art
    private static int Scale(uint[] pixels, int width, int height)
    {
        int divisor = 0;
        int Gcd(int a, int b) { while (b != 0) (a, b) = (b, a % b); return a; }
        for (int y = 0; y < height && divisor != 1; y++)
            for (int x = 0; x < width;)
            {
                int runStart = x; uint c = pixels[y * width + x];
                while (x < width && pixels[y * width + x] == c) x++;
                if (runStart > 0 && x < width) divisor = Gcd(divisor, x - runStart);
                if (divisor == 1) break;
            }
        for (int x = 0; x < width && divisor != 1; x++)
            for (int y = 0; y < height;)
            {
                int runStart = y; uint c = pixels[y * width + x];
                while (y < height && pixels[y * width + x] == c) y++;
                if (runStart > 0 && y < height) divisor = Gcd(divisor, y - runStart);
                if (divisor == 1) break;
            }
        if (divisor <= 1) return 1;
        // the picture's own size can have a partial border, so only use scales that leave a sensible picture
        return divisor > 16 ? 1 : divisor;
    }
}
