using LufiaForge.Core.Battle;

namespace LufiaForge.Core.Cave;

public static partial class AncientCave
{
    /// <summary>The Sinistrals of the floor 100 battle, in formation order (Gades, Amon, Erim, Daos).</summary>
    public static readonly int[] Sinistrals = { 0x91, 0x92, 0x93, 0x94 };
    public const int BossFormation = 0xE6;
    public const int ShrunkWidth = 48;

    /// <summary>
    /// Floor 100: all four Sinistrals at once (the project's boss4.asm). The battle code takes at most 3 picture
    /// groups and gives each a palette slot; a 4th group gets the 1st group's slot, because BG palette 7 is used by
    /// battle effects, so Gades and Daos share one palette. Their pictures are shrunk to 48 pixels wide to fit.
    /// <code>
    /// $08:D2CD  A9 04                   up to 4 picture groups (was LDA #$03)
    /// $08:D8B8  22 PaletteSlot EA x4    palette slot of a group (was LDA $DF / SEC / SBC #$02 / EOR #$FF / INC)
    /// </code>
    /// The floor 100 monsters are new copies of the Sinistrals with the shrunken pictures, so the story's own
    /// Sinistral fights look as before; formation E6 (the first, unwinnable Gades fight, which a cave-only game never
    /// reaches) becomes the four of them.
    /// </summary>
    private const string BossSource = """
        PaletteSlot:                ; A = palette slot 0-3 for this picture group
            LDA $DF
            SEC
            SBC.b #$03
            EOR.b #$FF
            INC A
            CMP.b #$03
            BNE +
            LDA.b #$00              ; group 4 shares group 1's slot
        +   RTL
        """;

    private const int GroupsSite = 0x452CD, PaletteSite = 0x458B8;
    private static readonly byte[] GroupsOriginal = { 0xA9, 0x03 };
    private static readonly byte[] PaletteOriginal = { 0xA5, 0xDF, 0x38, 0xE9, 0x02, 0x49, 0xFF, 0x1A };

    /// <summary>Makes the four floor 100 monsters (copies with 48-pixel pictures). Returns their ids.</summary>
    private static int[] MakeBosses(RomBuffer rom)
    {
        var pics = Sinistrals.Select(m => MonsterGraphics.Load(rom, rom.ReadByte(MonsterTable.RecordOffset(rom, m) + 14))).ToArray();
        var shrunk = pics.Select(Shrink).ToArray();
        // Gades (group 1) and Daos (group 4) share a palette slot: one palette for both; Amon and Erim keep theirs
        var shared = SharedPalette(shrunk[0].Pixels.Concat(shrunk[3].Pixels));
        var ids = new int[4];
        for (int i = 0; i < 4; i++)
        {
            uint[] palette = i is 0 or 3 ? shared : MonsterGraphics.Colours(pics[i], 0).Skip(1).ToArray();
            var (w, h, px) = shrunk[i];
            var idx = new byte[w * h];
            for (int k = 0; k < px.Length; k++) idx[k] = (px[k] >> 24) == 0 ? (byte)0 : (byte)(1 + Nearest(palette, px[k]));
            var pal = (byte[])pics[i].Data.Take(0x40).ToArray().Clone();
            for (int c = 0; c < 15; c++)
            {
                ushort v = MonsterGraphics.ToBgr555(c < palette.Length ? palette[c] : 0xFF000000u);
                pal[2 + 2 * c] = (byte)v; pal[3 + 2 * c] = (byte)(v >> 8);
            }
            int graphic = rom.ReadByte(MonsterTable.RecordOffset(rom, Sinistrals[i]) + 14);
            int newGraphic = MonsterTable.AddPicture(rom, graphic);
            var data = MonsterGraphics.Build(pics[i], idx, w, h, pal);
            ResourceWriter.Save(rom, MonsterTable.PictureResource(rom, newGraphic), data, allowExpand: true);
            ids[i] = MonsterTable.Add(rom, Sinistrals[i]);
            rom.WriteByte(MonsterTable.RecordOffset(rom, ids[i]) + 14, (byte)newGraphic);
        }
        return ids;
    }

    /// <summary>A picture cropped to what's drawn and shrunk to 48 pixels wide (box filter), bottom-aligned on a
    /// height that's a multiple of 16. Pixels: ARGB, alpha 0 = see-through.</summary>
    private static (int W, int H, uint[] Pixels) Shrink(MonsterGraphics.Picture p)
    {
        var idx = MonsterGraphics.Indices(p);
        var col = MonsterGraphics.Colours(p, 0);
        int sw = p.PixelWidth, sh = p.PixelHeight;
        int x0 = sw, x1 = 0, y0 = sh, y1 = 0;
        for (int y = 0; y < sh; y++) for (int x = 0; x < sw; x++)
                if (idx[y * sw + x] != 0) { x0 = Math.Min(x0, x); x1 = Math.Max(x1, x + 1); y0 = Math.Min(y0, y); y1 = Math.Max(y1, y + 1); }
        int cw = x1 - x0, ch = y1 - y0;
        double s = (double)ShrunkWidth / cw;
        int th = Math.Max(1, (int)Math.Round(ch * s));
        int rows = Math.Min(MonsterGraphics.MaxHeight, (th + 15) / 16), H = rows * 16;
        th = Math.Min(th, H);
        var outPx = new uint[ShrunkWidth * H];
        for (int ty = 0; ty < th; ty++)
            for (int tx = 0; tx < ShrunkWidth; tx++)
            {
                double fx0 = x0 + tx / s, fx1 = x0 + (tx + 1) / s, fy0 = y0 + ty / s, fy1 = y0 + (ty + 1) / s;
                double r = 0, g = 0, b = 0, opaque = 0, total = 0;
                for (int y = (int)fy0; y < Math.Ceiling(fy1) && y < sh; y++)
                    for (int x = (int)fx0; x < Math.Ceiling(fx1) && x < sw; x++)
                    {
                        double wx = Math.Min(fx1, x + 1) - Math.Max(fx0, x), wy = Math.Min(fy1, y + 1) - Math.Max(fy0, y), wgt = wx * wy;
                        if (wgt <= 0) continue;
                        total += wgt;
                        int c = idx[y * sw + x];
                        if (c == 0) continue;
                        uint v = col[c];
                        r += (v >> 16 & 0xFF) * wgt; g += (v >> 8 & 0xFF) * wgt; b += (v & 0xFF) * wgt; opaque += wgt;
                    }
                if (total <= 0 || opaque / total < 110 / 255.0) continue;
                outPx[(H - th + ty) * ShrunkWidth + tx] = 0xFF000000u | (uint)Math.Round(r / opaque) << 16 | (uint)Math.Round(g / opaque) << 8 | (uint)Math.Round(b / opaque);
            }
        return (ShrunkWidth, H, outPx);
    }

    private static int Dist(uint a, uint b)
    {
        int dr = (int)(a >> 16 & 0xFF) - (int)(b >> 16 & 0xFF), dg = (int)(a >> 8 & 0xFF) - (int)(b >> 8 & 0xFF), db = (int)(a & 0xFF) - (int)(b & 0xFF);
        return dr * dr * 30 + dg * dg * 59 + db * db * 11;
    }

    private static int Nearest(uint[] palette, uint c)
    {
        int best = 0, bd = int.MaxValue;
        for (int i = 0; i < palette.Length; i++) { int d = Dist(palette[i], c); if (d < bd) { bd = d; best = i; } }
        return best;
    }

    /// <summary>15 colours for two pictures together (k-means from the most common colours, snapped to the SNES's 5 bits).</summary>
    private static uint[] SharedPalette(IEnumerable<uint> pixels)
    {
        uint Snap(uint c) => 0xFF000000u | (c & 0xF8F8F8u);
        var pts = pixels.Where(p => p >> 24 != 0).Select(Snap).ToArray();
        var centres = pts.GroupBy(p => p).OrderByDescending(g => g.Count()).Select(g => g.Key).Take(15).ToList();
        while (centres.Count < 15) centres.Add(centres.Count > 0 ? centres[^1] : 0xFF000000u);
        for (int iter = 0; iter < 12; iter++)
        {
            var sum = new double[15, 3]; var n = new int[15];
            foreach (var p in pts)
            {
                int k = Nearest(centres.ToArray(), p);
                sum[k, 0] += p >> 16 & 0xFF; sum[k, 1] += p >> 8 & 0xFF; sum[k, 2] += p & 0xFF; n[k]++;
            }
            for (int k = 0; k < 15; k++)
                if (n[k] > 0) centres[k] = Snap(0xFF000000u | (uint)(sum[k, 0] / n[k]) << 16 | (uint)(sum[k, 1] / n[k]) << 8 | (uint)(sum[k, 2] / n[k]));
        }
        return centres.ToArray();
    }
}
