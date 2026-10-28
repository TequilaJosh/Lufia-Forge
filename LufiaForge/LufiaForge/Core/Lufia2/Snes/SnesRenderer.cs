namespace LufiaForge.Core.Lufia2.Snes;

/// <summary>
/// Draws the picture the console shows from <see cref="SnesMachine"/>'s memories and registers, line by line with HDMA:
/// background modes 0-1 (and 2-6 as 4 / 8 bit layers without offset-per-tile), 8x8 and 16x16 tiles, sprites with their
/// sizes, priorities and flips, the two windows, colour maths (add / subtract, half, fixed colour) and brightness.
/// No mode 7, mosaic or interlace. Output: 256 x 224, 0xAARRGGBB.
/// </summary>
public static class SnesRenderer
{
    public const int Width = 256, Height = 224;

    private static readonly (int W, int H)[,] ObjSizes =
    {
        { (8, 8), (16, 16) }, { (8, 8), (32, 32) }, { (8, 8), (64, 64) }, { (16, 16), (32, 32) },
        { (16, 16), (64, 64) }, { (32, 32), (64, 64) }, { (16, 32), (32, 64) }, { (16, 32), (32, 32) },
    };

    // layer ids: 0-3 BG1-4, 4 OBJ, 5 backdrop
    private struct Px { public int Color; public int Layer; public bool ObjMath; }
    [ThreadStatic] private static uint[]? _under;
    [ThreadStatic] private static bool _hideT1;
    [ThreadStatic] private static int[,]? _bgCol, _bgPri;
    [ThreadStatic] private static int[]? _objCol, _objPri, _objPal;
    [ThreadStatic] private static bool[,]? _win;

    /// <param name="underlay">A picture (0xAARRGGBB, alpha 0 = nothing) shown as a sprite of priority 2 behind the real
    /// ones, e.g. the monster a spell lands on.</param>
    /// <param name="hideTable1">Leave out the sprites from the second name table (the battle's own characters).</param>
    public static uint[] Render(SnesMachine m, uint[]? underlay = null, bool hideTable1 = false)
    {
        _under = underlay; _hideT1 = hideTable1;
        var outPx = new uint[Width * Height];
        m.HdmaInit();
        var main = new Px[Width];
        var sub = new Px[Width];
        for (int y = 0; y < Height; y++)
        {
            m.HdmaLine();
            RenderLine(m, y + 1, main, sub, outPx, y);
        }
        return outPx;
    }

    private static int Bpp(int mode, int bg) => mode switch
    {
        0 => 2,
        1 => bg < 2 ? 4 : bg == 2 ? 2 : 0,
        2 => bg < 2 ? 4 : 0,
        3 => bg == 0 ? 8 : bg == 1 ? 4 : 0,
        4 => bg == 0 ? 8 : bg == 1 ? 2 : 0,
        5 => bg == 0 ? 4 : bg == 1 ? 2 : 0,
        6 => bg == 0 ? 4 : 0,
        _ => 0,
    };

    private static void RenderLine(SnesMachine m, int line, Px[] main, Px[] sub, uint[] outPx, int row)
    {
        int inidisp = m.Reg(0x00);
        if ((inidisp & 0x80) != 0 || (inidisp & 15) == 0)
        {
            for (int x = 0; x < Width; x++) outPx[row * Width + x] = 0xFF000000;
            return;
        }
        int mode = m.Reg(0x05) & 7;
        bool bg3Prio = (m.Reg(0x05) & 8) != 0;
        int tm = m.Reg(0x2C), ts = m.Reg(0x2D), tmw = m.Reg(0x2E), tsw = m.Reg(0x2F);
        // per-layer pixel (colour index into CGRAM, 0 = none) and priority
        var bgCol = _bgCol ??= new int[4, Width]; Array.Clear(bgCol);
        var bgPri = _bgPri ??= new int[4, Width]; Array.Clear(bgPri);
        for (int bg = 0; bg < 4; bg++)
        {
            int bpp = Bpp(mode, bg);
            if (bpp == 0 || ((tm | ts) & 1 << bg) == 0) continue;
            DrawBg(m, bg, bpp, mode, line, bgCol, bgPri);
        }
        var objCol = _objCol ??= new int[Width]; Array.Clear(objCol);
        var objPri = _objPri ??= new int[Width]; Array.Clear(objPri);
        var objPal = _objPal ??= new int[Width]; Array.Clear(objPal);
        if (((tm | ts) & 0x10) != 0) DrawObj(m, line, objCol, objPri, objPal);

        // windows
        var win = _win ??= new bool[6, Width]; Array.Clear(win);
        int w1l = m.Reg(0x26), w1r = m.Reg(0x27), w2l = m.Reg(0x28), w2r = m.Reg(0x29);
        for (int layer = 0; layer < 6; layer++)
        {
            int sel = layer switch { 0 => m.Reg(0x23), 1 => m.Reg(0x23) >> 4, 2 => m.Reg(0x24), 3 => m.Reg(0x24) >> 4, 4 => m.Reg(0x25), _ => m.Reg(0x25) >> 4 } & 15;
            int logic = layer < 4 ? m.Reg(0x2A) >> (layer * 2) & 3 : m.Reg(0x2B) >> ((layer - 4) * 2) & 3;
            bool e1 = (sel & 2) != 0, i1 = (sel & 1) != 0, e2 = (sel & 8) != 0, i2 = (sel & 4) != 0;
            if (!e1 && !e2) continue;
            for (int x = 0; x < Width; x++)
            {
                bool a = (x >= w1l && x <= w1r) ^ i1, b = (x >= w2l && x <= w2r) ^ i2;
                win[layer, x] = e1 && e2 ? logic switch { 0 => a || b, 1 => a && b, 2 => a ^ b, _ => !(a ^ b) } : e1 ? a : b;
            }
        }

        for (int x = 0; x < Width; x++)
        {
            int u = -1;
            if (_under != null && _under[row * Width + x] >> 24 >= 128) { uint a = _under[row * Width + x]; u = (int)(a >> 19 & 31) | (int)(a >> 11 & 31) << 5 | (int)(a >> 3 & 31) << 10; }
            main[x] = Pick(mode, bg3Prio, x, tm, tmw, win, bgCol, bgPri, objCol, objPri, objPal, u);
            sub[x] = Pick(mode, bg3Prio, x, ts, tsw, win, bgCol, bgPri, objCol, objPri, objPal, -1);
        }

        int cgwsel = m.Reg(0x30), cgadsub = m.Reg(0x31);
        int bright = inidisp & 15;
        for (int x = 0; x < Width; x++)
        {
            var p = main[x];
            int c = p.Layer == 6 ? p.Color : Colour(m, p.Color);
            bool inColWin = win[5, x];
            bool black = ((cgwsel >> 6) & 3) switch { 0 => false, 1 => !inColWin, 2 => inColWin, _ => true };
            bool noMath = ((cgwsel >> 4) & 3) switch { 0 => false, 1 => !inColWin, 2 => inColWin, _ => true };
            if (black) c = 0;
            bool mathOn = p.Layer == 6 ? false : p.Layer == 4 ? p.ObjMath && (cgadsub & 0x10) != 0 : (cgadsub & 1 << (p.Layer == 5 ? 5 : p.Layer)) != 0;
            if (mathOn && !noMath)
            {
                int other; bool half = (cgadsub & 0x40) != 0;
                if ((cgwsel & 2) != 0)
                {
                    var s = sub[x];
                    if (s.Layer == 5) { other = m.FixedR | m.FixedG << 5 | m.FixedB << 10; half = false; }
                    else other = Colour(m, s.Color);
                }
                else other = m.FixedR | m.FixedG << 5 | m.FixedB << 10;
                if (black) half = false;
                c = Mix(c, other, (cgadsub & 0x80) != 0, half);
            }
            int r = (c & 31) * bright / 15, g = (c >> 5 & 31) * bright / 15, b = (c >> 10 & 31) * bright / 15;
            outPx[row * Width + x] = 0xFF000000u | (uint)(r << 3 | r >> 2) << 16 | (uint)(g << 3 | g >> 2) << 8 | (uint)(b << 3 | b >> 2);
        }
    }

    private static int Colour(SnesMachine m, int index) => m.Cgram[index * 2] | m.Cgram[index * 2 + 1] << 8;

    private static int Mix(int a, int b, bool sub, bool half)
    {
        int r = a & 31, g = a >> 5 & 31, bl = a >> 10 & 31;
        int r2 = b & 31, g2 = b >> 5 & 31, b2 = b >> 10 & 31;
        if (sub) { r -= r2; g -= g2; bl -= b2; } else { r += r2; g += g2; bl += b2; }
        if (half) { r >>= 1; g >>= 1; bl >>= 1; }
        r = Math.Clamp(r, 0, 31); g = Math.Clamp(g, 0, 31); bl = Math.Clamp(bl, 0, 31);
        return r | g << 5 | bl << 10;
    }

    private static Px Pick(int mode, bool bg3Prio, int x, int enable, int winEnable, bool[,] win,
        int[,] bgCol, int[,] bgPri, int[] objCol, int[] objPri, int[] objPal, int under)
    {
        bool U() => under >= 0 && (enable & 0x10) != 0;
        Px Un() => new() { Color = under, Layer = 6 };
        bool On(int layer) => (enable & 1 << layer) != 0 && !((winEnable & 1 << layer) != 0 && win[layer, x]);
        bool Bg(int bg, int pri) => On(bg) && bgCol[bg, x] != 0 && bgPri[bg, x] == pri;
        bool Obj(int pri) => On(4) && objCol[x] != 0 && objPri[x] == pri;
        Px B(int bg) => new() { Color = bgCol[bg, x], Layer = bg };
        Px O() => new() { Color = objCol[x], Layer = 4, ObjMath = objPal[x] >= 4 };
        if (mode == 0)
        {
            if (Obj(3)) return O();
            if (Bg(0, 1)) return B(0); if (Bg(1, 1)) return B(1);
            if (Obj(2)) return O();
            if (U()) return Un();
            if (Bg(0, 0)) return B(0); if (Bg(1, 0)) return B(1);
            if (Obj(1)) return O();
            if (Bg(2, 1)) return B(2); if (Bg(3, 1)) return B(3);
            if (Obj(0)) return O();
            if (Bg(2, 0)) return B(2); if (Bg(3, 0)) return B(3);
        }
        else if (mode == 1)
        {
            if (bg3Prio && Bg(2, 1)) return B(2);
            if (Obj(3)) return O();
            if (Bg(0, 1)) return B(0); if (Bg(1, 1)) return B(1);
            if (Obj(2)) return O();
            if (U()) return Un();
            if (Bg(0, 0)) return B(0); if (Bg(1, 0)) return B(1);
            if (Obj(1)) return O();
            if (!bg3Prio && Bg(2, 1)) return B(2);
            if (Obj(0)) return O();
            if (Bg(2, 0)) return B(2);
        }
        else
        {
            if (Obj(3)) return O();
            if (Bg(0, 1)) return B(0);
            if (Obj(2)) return O();
            if (U()) return Un();
            if (Bg(1, 1)) return B(1);
            if (Obj(1)) return O();
            if (Bg(0, 0)) return B(0);
            if (Obj(0)) return O();
            if (Bg(1, 0)) return B(1);
        }
        return new Px { Color = 0, Layer = 5 };
    }

    private static void DrawBg(SnesMachine m, int bg, int bpp, int mode, int line, int[,] col, int[,] pri)
    {
        int sc = m.Reg(0x07 + bg);
        int mapBase = (sc & 0xFC) << 9;   // bytes
        bool wide = (sc & 1) != 0, tall = (sc & 2) != 0;
        int nba = bg < 2 ? m.Reg(0x0B) >> (bg * 4) & 15 : m.Reg(0x0C) >> ((bg - 2) * 4) & 15;
        int charBase = nba << 13;          // bytes
        bool big = (m.Reg(0x05) & 0x10 << bg) != 0;
        int ts = big ? 16 : 8;
        int hofs = m.HScroll[bg], vofs = m.VScroll[bg];
        int yy = (line + vofs) & 0x3FF;
        int palBase = mode == 0 ? bg * 32 : 0;
        int tileBytes = bpp * 8;
        for (int x = 0; x < Width; x++)
        {
            int xx = (x + hofs) & 0x3FF;
            int tx = xx / ts, ty = yy / ts;
            int scx = (tx >> 5) & (wide ? 1 : 0), scy = (ty >> 5) & (tall ? 1 : 0);
            int screen = scx + scy * (wide ? 2 : 1);
            int entryAddr = mapBase + screen * 0x800 + ((ty & 31) * 32 + (tx & 31)) * 2;
            int e = m.Vram[entryAddr & 0xFFFF] | m.Vram[(entryAddr + 1) & 0xFFFF] << 8;
            int tile = e & 0x3FF, pal = e >> 10 & 7, p = e >> 13 & 1;
            bool hf = (e & 0x4000) != 0, vf = (e & 0x8000) != 0;
            int px = xx % ts, py = yy % ts;
            if (hf) px = ts - 1 - px;
            if (vf) py = ts - 1 - py;
            if (big) { tile += (px >> 3) + (py >> 3) * 16; px &= 7; py &= 7; }
            int ci = TilePixel(m.Vram, (charBase + (tile & 0x3FF) * tileBytes) & 0xFFFF, bpp, px, py);
            if (ci == 0) continue;
            col[bg, x] = bpp == 8 ? ci : palBase + pal * (1 << bpp) + ci;
            pri[bg, x] = p;
        }
    }

    private static int TilePixel(byte[] vram, int at, int bpp, int x, int y)
    {
        int bit = 7 - x, v = 0;
        for (int plane = 0; plane < bpp; plane += 2)
        {
            int o = (at + plane * 8 + y * 2) & 0xFFFF;
            v |= ((vram[o] >> bit) & 1) << plane;
            v |= ((vram[(o + 1) & 0xFFFF] >> bit) & 1) << (plane + 1);
        }
        return v;
    }

    private static void DrawObj(SnesMachine m, int line, int[] col, int[] pri, int[] palOut)
    {
        int obsel = m.Reg(0x01);
        int nameBase = (obsel & 7) << 14, nameGap = ((obsel >> 3) & 3) + 1 << 13;
        int sizeSel = obsel >> 5 & 7;
        // lower numbers are in front: draw from 127 down
        for (int i = 127; i >= 0; i--)
        {
            int o = i * 4;
            int hi = m.Oam[0x200 + i / 4] >> ((i & 3) * 2) & 3;
            int x = m.Oam[o] | (hi & 1) << 8;
            if (x >= 256) x -= 512;
            int y = m.Oam[o + 1];
            int tile = m.Oam[o + 2] | (m.Oam[o + 3] & 1) << 8;
            if (_hideT1 && tile >= 0x100) continue;
            int attr = m.Oam[o + 3];
            var (w, h) = ObjSizes[sizeSel, (hi >> 1) & 1];
            int dy = (line - y) & 0xFF;
            if (dy >= h) continue;
            bool hf = (attr & 0x40) != 0, vf = (attr & 0x80) != 0;
            int p = attr >> 4 & 3, pal = attr >> 1 & 7;
            int sy = vf ? h - 1 - dy : dy;
            for (int dx = 0; dx < w; dx++)
            {
                int sx = x + dx;
                if (sx < 0 || sx >= Width) continue;
                int tx = hf ? w - 1 - dx : dx;
                int t = tile & 0xFF;
                int row = ((t >> 4) + (sy >> 3)) & 15, column = ((t & 15) + (tx >> 3)) & 15;
                int addr = nameBase + ((tile & 0x100) != 0 ? nameGap : 0) + (row * 16 + column) * 32;
                int ci = TilePixel(m.Vram, addr & 0xFFFF, 4, tx & 7, sy & 7);
                if (ci == 0) continue;
                col[sx] = 128 + pal * 16 + ci;
                pri[sx] = p;
                palOut[sx] = pal;
            }
        }
    }
}
