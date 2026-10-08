namespace LufiaForge.Core.Maps;

/// <summary>
/// Draws the 256x224 screen of a cutscene the way the console builds it: the map's second layer (BG2, e.g. the
/// intro's clouds) behind the map (BG1, colour 0 transparent), sprites on top, then the palette effects in
/// 5-bit colour: a fade multiplies every component by level/32 and a lightning flash adds white to it. Both only
/// touch the map's colours (palette entries 32-127) and sprites: the text keeps its colours, as in the game.
/// </summary>
public sealed class StageScreen
{
    public const int Width = 256, Height = 224;

    private readonly ushort[] _palette;               // 128 BGR555 colours
    private readonly byte[] _bg1; private readonly int _bg1W, _bg1H;
    private readonly byte[]? _bg2; private readonly int _bg2W, _bg2H;

    /// <summary>Colour index per pixel (pal * 16 + colour, 0 = transparent) of a metatile layer.</summary>
    public static byte[] Indexed(MapTileset ts, IReadOnlyList<ushort> layer, int w, int h)
    {
        var px = new byte[w * 16 * h * 16];
        for (int my = 0; my < h; my++)
            for (int mx = 0; mx < w; mx++)
            {
                var m = ts.MetatileIndices(layer[my * w + mx]);
                for (int y = 0; y < 16; y++) Array.Copy(m, y * 16, px, (my * 16 + y) * w * 16 + mx * 16, 16);
            }
        return px;
    }

    private readonly RomBuffer? _rom;

    public StageScreen(MapTileset ts, LufiaMap map, RomBuffer? rom = null)
    {
        _rom = rom;
        _palette = ts.Palette555;
        _bg1W = map.Width * 16; _bg1H = map.Height * 16;
        _bg1 = Indexed(ts, map.Layer, map.Width, map.Height);
        if (map.Layer2 is { } l2)
        {
            _bg2W = map.Layer2Width * 16; _bg2H = map.Layer2Height * 16;
            _bg2 = Indexed(ts, l2, map.Layer2Width, map.Layer2Height);
        }
    }

    public bool HasLayer2 => _bg2 != null;

    /// <summary>
    /// Map pixel at the screen's top-left corner on a frame, from the camera (tile it centres on) of the frame
    /// before: the console scrolls to the previous frame's camera rounded half down, and shows BG lines one below
    /// their scroll value. Measured against the BG1 scroll registers through the intro.
    /// </summary>
    public static (int Left, int Top) Origin(double prevCamX, double prevCamY) =>
        ((int)Math.Ceiling((prevCamX - 7) * 16 - 0.5), (int)Math.Ceiling((prevCamY - 7) * 16 - 0.5) + 1);

    /// <summary>
    /// The screen with its top-left corner at map pixel (<paramref name="left"/>, <paramref name="top"/>).
    /// </summary>
    /// <param name="bg2X">BG2 scroll (pixels).</param>
    /// <param name="fade">Palette fade level, 0 (black) to 32 (full colour).</param>
    /// <param name="flash">White added to every colour component (0-31).</param>
    /// <param name="sprites">BGRA sprites (alpha 0 = transparent) with their screen positions.</param>
    /// <param name="text">Narration lines with their screen positions (<see cref="CutsceneText.Layout"/>), drawn on top.</param>
    public uint[] Render(int left, int top, int bg2X, int bg2Y, int fade, int flash,
                         IEnumerable<(uint[] Px, int W, int H, int X, int Y)>? sprites = null,
                         IEnumerable<(string Line, int X, int Y)>? text = null)
    {
        var c5 = new ushort[Width * Height];   // BGR555 per pixel
        var fx = new bool[Width * Height];     // takes the fade / flash (map colours 32-127 and sprites, not the text)
        for (int y = 0; y < Height; y++)
        {
            int my = top + y;
            for (int x = 0; x < Width; x++)
            {
                int mx = left + x;
                int i = mx >= 0 && my >= 0 && mx < _bg1W && my < _bg1H ? _bg1[my * _bg1W + mx] : 0;
                if (i == 0 && _bg2 != null)
                {
                    int bx = ((bg2X + x) % _bg2W + _bg2W) % _bg2W, by = ((bg2Y + y) % _bg2H + _bg2H) % _bg2H;
                    i = _bg2[by * _bg2W + bx];
                }
                c5[y * Width + x] = _palette[i];   // index 0 = the backdrop colour
                fx[y * Width + x] = i >= 32;
            }
        }
        if (sprites != null)
            foreach (var (px, w, h, sx, sy) in sprites)
                for (int y = 0; y < h; y++)
                {
                    int ty = sy + y; if (ty < 0 || ty >= Height) continue;
                    for (int x = 0; x < w; x++)
                    {
                        int tx = sx + x; if (tx < 0 || tx >= Width) continue;
                        uint p = px[y * w + x];
                        if (p >> 24 == 0) continue;
                        c5[ty * Width + tx] = (ushort)(((p >> 19) & 31) | (((p >> 11) & 31) << 5) | (((p >> 3) & 31) << 10));
                        fx[ty * Width + tx] = true;
                    }
                }

        if (text != null && _rom != null) DrawText(c5, fx, text);   // BG3 is in front of the sprites here

        var out_ = new uint[Width * Height];
        for (int k = 0; k < out_.Length; k++)
        {
            int c = c5[k];
            int r = c & 31, g = (c >> 5) & 31, b = (c >> 10) & 31;
            if (!fx[k]) { out_[k] = 0xFF000000u | (uint)(To8(r) << 16) | (uint)(To8(g) << 8) | (uint)To8(b); continue; }
            if (fade < 32) { r = r * fade / 32; g = g * fade / 32; b = b * fade / 32; }
            if (flash > 0) { r = Math.Min(31, r + flash); g = Math.Min(31, g + flash); b = Math.Min(31, b + flash); }
            out_[k] = 0xFF000000u | (uint)(To8(r) << 16) | (uint)(To8(g) << 8) | (uint)To8(b);
        }
        return out_;
    }

    private static int To8(int c) => (c << 3) | (c >> 2);

    /// <summary>Text colours the game sets in CGRAM (entries 31 and 29 during the intro): white letters, dark blue outline.</summary>
    public const ushort TextColour = 0x7FFF, TextOutline = 7 << 10;

    /// <summary>
    /// Narration as the game draws it on BG3: each line's glyphs side by side, an outline around the letters
    /// (a pixel is outline when a letter pixel is beside it, above it, or below it or below-left of it), in BG3
    /// palette 7: colour 29 outline, colour 31 letters. BG3 shows its lines one up from the window row (y - 1).
    /// </summary>
    private void DrawText(ushort[] c5, bool[] fx, IEnumerable<(string Line, int X, int Y)> lines)
    {
        foreach (var (line, x0, y0) in lines)
        {
            int w = line.Length * 8;
            if (w == 0) continue;
            var on = new bool[16, w];
            for (int k = 0; k < line.Length; k++)
            {
                var g = CutsceneText.Glyph(_rom!, line[k]);
                for (int r = 0; r < 16; r++)
                    for (int b = 0; b < 8; b++) on[r, k * 8 + b] = ((g[r] >> (7 - b)) & 1) != 0;
            }
            bool P(int r, int x) => r >= 0 && r < 16 && x >= 0 && x < w && on[r, x];
            for (int r = 0; r < 16; r++)
                for (int x = 0; x < w; x++)
                {
                    int sx = x0 + x, sy = y0 - 1 + r;
                    if (sx < 0 || sx >= Width || sy < 0 || sy >= Height) continue;
                    if (on[r, x]) { c5[sy * Width + sx] = TextColour; fx[sy * Width + sx] = false; }
                    else if (P(r, x - 1) || P(r, x + 1) || P(r - 1, x - 1) || P(r - 1, x) || P(r - 1, x + 1) || P(r + 1, x) || P(r + 1, x - 1))
                    { c5[sy * Width + sx] = TextOutline; fx[sy * Width + sx] = false; }
                }
        }
    }
}
