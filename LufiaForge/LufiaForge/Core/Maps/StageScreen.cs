namespace LufiaForge.Core.Maps;

// draws the 256x224 screen of a cutscene the way the console builds it: the map's second layer (BG2, the intro's
// clouds for one) behind the map (BG1, colour 0 transparent), sprites on top, then the palette effects in 5-bit
// colour: a fade multiplies every component by level/32 and a lightning flash adds white to it. both only touch the
// map's colours (palette entries 32-127) and sprites, the text keeps its colours like in the game
public sealed class StageScreen
{
    public const int Width = 256, Height = 224;

    private readonly ushort[] _palette;               // 128 BGR555 colours
    private readonly byte[] _bg1; private readonly int _bg1W, _bg1H;
    private readonly byte[]? _bg2; private readonly int _bg2W, _bg2H;

    // colour index per pixel (pal * 16 + colour, 0 = transparent) of a metatile layer
    public static byte[] Indexed(MapTileset ts, IReadOnlyList<ushort> layer, int w, int h)
    {
        var pixels = new byte[w * 16 * h * 16];
        for (int my = 0; my < h; my++)
            for (int mx = 0; mx < w; mx++)
            {
                var metatile = ts.MetatileIndices(layer[my * w + mx]);
                for (int y = 0; y < 16; y++) Array.Copy(metatile, y * 16, pixels, (my * 16 + y) * w * 16 + mx * 16, 16);
            }
        return pixels;
    }

    private readonly RomBuffer? _rom;

    public StageScreen(MapTileset ts, LufiaMap map, RomBuffer? rom = null)
    {
        _rom = rom;
        _palette = ts.Palette555;
        _bg1W = map.Width * 16; _bg1H = map.Height * 16;
        _bg1 = Indexed(ts, map.Layer, map.Width, map.Height);
        if (map.Layer2 is { } layer2)
        {
            _bg2W = map.Layer2Width * 16; _bg2H = map.Layer2Height * 16;
            _bg2 = Indexed(ts, layer2, map.Layer2Width, map.Layer2Height);
        }
    }

    public bool HasLayer2 => _bg2 != null;

    // the map pixel at the screen's top-left corner on a frame, from the camera (the tile it centres on) of the frame
    // before: the console scrolls to the previous frame's camera rounded half down, and shows BG lines one below
    // their scroll value. measured against the BG1 scroll registers through the intro
    public static (int Left, int Top) Origin(double prevCamX, double prevCamY) =>
        ((int)Math.Ceiling((prevCamX - 7) * 16 - 0.5), (int)Math.Ceiling((prevCamY - 7) * 16 - 0.5) + 1);

    // the screen with its top-left corner at map pixel (left, top).
    // bg2X = BG2 scroll (pixels). fade = palette fade level, 0 (black) to 32 (full colour).
    // flash = white added to every colour component (0-31). sprites = BGRA sprites (alpha 0 = transparent) with
    // their screen positions. text = narration lines with their screen positions (CutsceneText.Layout), drawn on top
    public uint[] Render(int left, int top, int bg2X, int bg2Y, int fade, int flash,
                         IEnumerable<(uint[] Px, int W, int H, int X, int Y)>? sprites = null,
                         IEnumerable<(string Line, int X, int Y)>? text = null)
    {
        var colours = new ushort[Width * Height];   // BGR555 per pixel
        var takesEffects = new bool[Width * Height];     // takes the fade / flash (map colours 32-127 and sprites, not the text)
        for (int y = 0; y < Height; y++)
        {
            int my = top + y;
            for (int x = 0; x < Width; x++)
            {
                int mx = left + x;
                int index = mx >= 0 && my >= 0 && mx < _bg1W && my < _bg1H ? _bg1[my * _bg1W + mx] : 0;
                if (index == 0 && _bg2 != null)
                {
                    int bx = ((bg2X + x) % _bg2W + _bg2W) % _bg2W, by = ((bg2Y + y) % _bg2H + _bg2H) % _bg2H;
                    index = _bg2[by * _bg2W + bx];
                }
                colours[y * Width + x] = _palette[index];   // index 0 = the backdrop colour
                takesEffects[y * Width + x] = index >= 32;
            }
        }
        if (sprites != null)
            foreach (var (spritePixels, spriteWidth, spriteHeight, sx, sy) in sprites)
                for (int y = 0; y < spriteHeight; y++)
                {
                    int ty = sy + y; if (ty < 0 || ty >= Height) continue;
                    for (int x = 0; x < spriteWidth; x++)
                    {
                        int tx = sx + x; if (tx < 0 || tx >= Width) continue;
                        uint pixel = spritePixels[y * spriteWidth + x];
                        if (pixel >> 24 == 0) continue;
                        colours[ty * Width + tx] = (ushort)(((pixel >> 19) & 31) | (((pixel >> 11) & 31) << 5) | (((pixel >> 3) & 31) << 10));
                        takesEffects[ty * Width + tx] = true;
                    }
                }

        if (text != null && _rom != null) DrawText(colours, takesEffects, text);   // BG3 is in front of the sprites here

        var output = new uint[Width * Height];
        for (int k = 0; k < output.Length; k++)
        {
            int colour = colours[k];
            int r = colour & 31, g = (colour >> 5) & 31, b = (colour >> 10) & 31;
            if (!takesEffects[k]) { output[k] = 0xFF000000u | (uint)(To8(r) << 16) | (uint)(To8(g) << 8) | (uint)To8(b); continue; }
            if (fade < 32) { r = r * fade / 32; g = g * fade / 32; b = b * fade / 32; }
            if (flash > 0) { r = Math.Min(31, r + flash); g = Math.Min(31, g + flash); b = Math.Min(31, b + flash); }
            output[k] = 0xFF000000u | (uint)(To8(r) << 16) | (uint)(To8(g) << 8) | (uint)To8(b);
        }
        return output;
    }

    private static int To8(int c) => (c << 3) | (c >> 2);

    // text colours the game sets in CGRAM (entries 31 and 29 during the intro): white letters, dark blue outline
    public const ushort TextColour = 0x7FFF, TextOutline = 7 << 10;

    // narration the way the game draws it on BG3: each line's glyphs side by side, an outline around the letters
    // (a pixel is outline when a letter pixel is beside it, above it, below it or below-left of it), in BG3
    // palette 7: colour 29 outline, colour 31 letters. BG3 shows its lines one up from the window row (y - 1)
    private void DrawText(ushort[] colours, bool[] takesEffects, IEnumerable<(string Line, int X, int Y)> lines)
    {
        foreach (var (line, x0, y0) in lines)
        {
            int lineWidth = line.Length * 8;
            if (lineWidth == 0) continue;
            var lit = new bool[16, lineWidth];
            for (int k = 0; k < line.Length; k++)
            {
                var glyph = CutsceneText.Glyph(_rom!, line[k]);
                for (int r = 0; r < 16; r++)
                    for (int bit = 0; bit < 8; bit++) lit[r, k * 8 + bit] = ((glyph[r] >> (7 - bit)) & 1) != 0;
            }
            bool IsLit(int r, int x) => r >= 0 && r < 16 && x >= 0 && x < lineWidth && lit[r, x];
            for (int r = 0; r < 16; r++)
                for (int x = 0; x < lineWidth; x++)
                {
                    int screenX = x0 + x, screenY = y0 - 1 + r;
                    if (screenX < 0 || screenX >= Width || screenY < 0 || screenY >= Height) continue;
                    if (lit[r, x]) { colours[screenY * Width + screenX] = TextColour; takesEffects[screenY * Width + screenX] = false; }
                    else if (IsLit(r, x - 1) || IsLit(r, x + 1) || IsLit(r - 1, x - 1) || IsLit(r - 1, x) || IsLit(r - 1, x + 1) || IsLit(r + 1, x) || IsLit(r + 1, x - 1))
                    { colours[screenY * Width + screenX] = TextOutline; takesEffects[screenY * Width + screenX] = false; }
                }
        }
    }
}
