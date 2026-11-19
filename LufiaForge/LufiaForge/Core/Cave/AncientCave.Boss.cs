using LufiaForge.Core.Battle;

namespace LufiaForge.Core.Cave;

public static partial class AncientCave
{
    // the Sinistrals of the floor 100 battle, in formation order (Gades, Amon, Erim, Daos)
    public static readonly int[] Sinistrals = { 0x91, 0x92, 0x93, 0x94 };
    public const int BossFormation = 0xFA;   // a copy of F6 that no event uses (the story uses E6, F0, F5)
    public const int ShrunkWidth = 48;

    // floor 100: all four Sinistrals at once (the project's boss4.asm). the battle code takes at most 3 picture
    // groups and gives each a palette slot. a 4th group gets the 1st group's slot, because BG palette 7 is used by
    // battle effects, so Gades and Daos share one palette. their pictures get shrunk to 48 pixels wide to fit.
    //   $08:D2CD  22 GroupCount           up to 4 picture groups in the floor 100 battle, 3 in any other (was LDA #$03 / STA $DF)
    //   $08:D8B8  22 PaletteSlot EA x4    palette slot of a group (was LDA $DF / SEC / SBC #$02 / EOR #$FF / INC)
    //   $08:D1C1  22 CountLoop EA         count the 4th group's monsters too (was INX / CPX #$03 / BNE $D175)
    //   $08:D20A  22 SetupLoop EA         and set it up (was INX / CPX #$03 / BNE $D1CD)
    // the floor 100 monsters are new copies of the Sinistrals with the shrunk pictures, so the story's own
    // Sinistral fights look like before. formation FA (a duplicate of F6 that no event uses) becomes the four of them
    private const string BossSource = """
        GroupCount:                 ; $DF = how many picture groups the battle loads: 4 only for the floor 100 formation
            LDA $7E142D             ; (the battle's formation; with 4 in other battles a stale 4th entry loaded as a
            CMP.b #$FA              ; group and took the first group's palette slot) - $FA = BossFormation
            BEQ +
            LDA.b #$03
            BRA ++
        +   LDA.b #$04
        ++  STA $DF
            RTL

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

        ; The fight's group loops ($08:D175 counts each group's living monsters and how many groups are left, which
        ; ends the fight at 0; $08:D1CD sets up each group) stopped after 3 groups, so in the floor 100 battle the
        ; 4th was never counted: beating the first three ended it. These take their INX / CPX #$03 / BNE: one more
        ; group in that battle. (M = X = 8 bits; A is free: both loops start with an LDA.)
        CountLoop:
            INX
            JSR MoreGroups
            BCC +
            LDA.b #$74              ; go on at $08:D175 (RTL adds 1)
            STA $01,s
            LDA.b #$D1
            STA $02,s
        +   RTL

        SetupLoop:
            INX
            JSR MoreGroups
            BCC +
            LDA.b #$CC              ; go on at $08:D1CD
            STA $01,s
            LDA.b #$D1
            STA $02,s
        +   RTL

        MoreGroups:                 ; C = group X is still to do (X = 0-2, or 3 in the floor 100 battle)
            CPX.b #$03
            BCC ++
            BNE +
            LDA $7E142D
            CMP.b #$FA
            BNE +
            SEC
            RTS
        +   CLC
            RTS
        ++  SEC
            RTS
        """;

    private const int GroupsSite = 0x452CD, PaletteSite = 0x458B8;
    // the fight's two group loops: INX / CPX #$03 / BNE ($08:D1C1 back to $D175, $08:D20A back to $D1CD),
    // each one becomes JSL + NOP
    private const int CountLoopSite = 0x451C1, SetupLoopSite = 0x4520A;
    private static readonly byte[] CountLoopOriginal = { 0xE8, 0xE0, 0x03, 0xD0, 0xAF };
    private static readonly byte[] SetupLoopOriginal = { 0xE8, 0xE0, 0x03, 0xD0, 0xBE };
    private static readonly byte[] GroupsOriginal = { 0xA9, 0x03, 0x85, 0xDF };
    private static readonly byte[] PaletteOriginal = { 0xA5, 0xDF, 0x38, 0xE9, 0x02, 0x49, 0xFF, 0x1A };

    // makes the four floor 100 monsters (copies with 48-pixel pictures). returns their ids
    private static int[] MakeBosses(RomBuffer rom)
    {
        var pictures = Sinistrals.Select(m => MonsterGraphics.Load(rom, rom.ReadByte(MonsterTable.RecordOffset(rom, m) + 14))).ToArray();
        var shrunk = pictures.Select(Shrink).ToArray();
        // Gades (group 1) and Daos (group 4) share a palette slot so they get one palette for both, Amon and Erim keep theirs
        var shared = SharedPalette(shrunk[0].Pixels.Concat(shrunk[3].Pixels));
        var ids = new int[4];
        for (int i = 0; i < 4; i++)
        {
            uint[] palette = i is 0 or 3 ? shared : MonsterGraphics.Colours(pictures[i], 0).Skip(1).ToArray();
            var (width, height, pixels) = shrunk[i];
            var indices = new byte[width * height];
            for (int p = 0; p < pixels.Length; p++) indices[p] = (pixels[p] >> 24) == 0 ? (byte)0 : (byte)(1 + Nearest(palette, pixels[p]));
            var paletteBytes = (byte[])pictures[i].Data.Take(0x40).ToArray().Clone();
            for (int c = 0; c < 15; c++)
            {
                ushort bgr = MonsterGraphics.ToBgr555(c < palette.Length ? palette[c] : 0xFF000000u);
                paletteBytes[2 + 2 * c] = (byte)bgr; paletteBytes[3 + 2 * c] = (byte)(bgr >> 8);
            }
            int graphic = rom.ReadByte(MonsterTable.RecordOffset(rom, Sinistrals[i]) + 14);
            int newGraphic = MonsterTable.AddPicture(rom, graphic);
            var data = MonsterGraphics.Build(pictures[i], indices, width, height, paletteBytes);
            ResourceWriter.Save(rom, MonsterTable.PictureResource(rom, newGraphic), data, allowExpand: true);
            ids[i] = MonsterTable.Add(rom, Sinistrals[i]);
            rom.WriteByte(MonsterTable.RecordOffset(rom, ids[i]) + 14, (byte)newGraphic);
        }
        return ids;
    }

    // takes the floor 100 monsters out again (Remove), last first, and then their pictures (and those pictures'
    // resource ids). each one only while it's the last of its kind and nothing else uses it (an encounter, a boss
    // formation, another monster's picture). whatever stays gets found and used again by the next Apply (LeftBosses)
    private static void RemoveBosses(RomBuffer rom, int[] bosses)
    {
        var graphics = new List<int>();
        bool Used(int monster) =>
            Enumerable.Range(1, Encounters.SubgroupCount(rom)).Any(s => Encounters.SubgroupMonsters(rom, s).Contains(monster)) ||
            Enumerable.Range(Encounters.FirstBoss, Encounters.LastBoss - Encounters.FirstBoss + 1).Any(f => Encounters.BossMonsters(rom, f).Contains(monster));
        foreach (int monster in bosses.OrderByDescending(m => m))
        {
            if (monster < MonsterTable.OriginalCount || monster != MonsterTable.Count(rom) - 1 || Used(monster)) break;
            graphics.Add(rom.ReadByte(MonsterTable.RecordOffset(rom, monster) + 14));
            MonsterTable.RemoveLast(rom, monster);
        }
        foreach (int graphic in graphics.OrderByDescending(g => g))
        {
            bool shown = Enumerable.Range(0, MonsterTable.Count(rom)).Any(m => rom.ReadByte(MonsterTable.RecordOffset(rom, m) + 14) == graphic);
            if (shown || !MonsterTable.RemoveLastPicture(rom, graphic)) break;
        }
    }

    // the floor 100 monsters an earlier cave left in the rom when it was taken out (when monsters were added after
    // them): new monsters that are copies of the Sinistrals (everything but the picture and the script's place) with a
    // 48-pixel picture of their own. null unless all four are there
    private static int[]? LeftBosses(RomBuffer rom)
    {
        var ids = new int[4];
        for (int i = 0; i < 4; i++)
        {
            int sinistralRecord = MonsterTable.RecordOffset(rom, Sinistrals[i]);
            var wanted = rom.ReadBytes(sinistralRecord, MonsterTable.RecordLength(rom, sinistralRecord));
            ids[i] = -1;
            for (int monster = MonsterTable.Count(rom) - 1; monster >= MonsterTable.OriginalCount && ids[i] < 0; monster--)
            {
                int recordOffset = MonsterTable.RecordOffset(rom, monster);
                var record = rom.ReadBytes(recordOffset, MonsterTable.RecordLength(rom, recordOffset));
                int graphic = record[14];
                if (record.Length != wanted.Length || graphic < MonsterTable.OriginalPictures || graphic == wanted[14] || ids.Take(i).Contains(monster)) continue;
                if (Enumerable.Range(0, record.Length).Any(k => k is not (14 or 0x22 or 0x23) && record[k] != wanted[k])) continue;
                try { if (MonsterGraphics.Load(rom, graphic).PixelWidth != ShrunkWidth) continue; } catch (Exception) { continue; }
                ids[i] = monster;
            }
            if (ids[i] < 0) return null;
        }
        return ids;
    }

    // a picture cropped to what's drawn and shrunk to 48 pixels wide (box filter), bottom-aligned on a
    // height that's a multiple of 16. pixels: ARGB, alpha 0 = see-through
    private static (int W, int H, uint[] Pixels) Shrink(MonsterGraphics.Picture picture)
    {
        var indices = MonsterGraphics.Indices(picture);
        var colours = MonsterGraphics.Colours(picture, 0);
        int sourceWidth = picture.PixelWidth, sourceHeight = picture.PixelHeight;
        int x0 = sourceWidth, x1 = 0, y0 = sourceHeight, y1 = 0;
        for (int y = 0; y < sourceHeight; y++) for (int x = 0; x < sourceWidth; x++)
                if (indices[y * sourceWidth + x] != 0) { x0 = Math.Min(x0, x); x1 = Math.Max(x1, x + 1); y0 = Math.Min(y0, y); y1 = Math.Max(y1, y + 1); }
        int cropWidth = x1 - x0, cropHeight = y1 - y0;
        double scale = (double)ShrunkWidth / cropWidth;
        int targetHeight = Math.Max(1, (int)Math.Round(cropHeight * scale));
        int rows = Math.Min(MonsterGraphics.MaxHeight, (targetHeight + 15) / 16), H = rows * 16;
        targetHeight = Math.Min(targetHeight, H);
        var outPixels = new uint[ShrunkWidth * H];
        for (int ty = 0; ty < targetHeight; ty++)
            for (int tx = 0; tx < ShrunkWidth; tx++)
            {
                double fx0 = x0 + tx / scale, fx1 = x0 + (tx + 1) / scale, fy0 = y0 + ty / scale, fy1 = y0 + (ty + 1) / scale;
                double r = 0, g = 0, b = 0, opaque = 0, total = 0;
                for (int y = (int)fy0; y < Math.Ceiling(fy1) && y < sourceHeight; y++)
                    for (int x = (int)fx0; x < Math.Ceiling(fx1) && x < sourceWidth; x++)
                    {
                        double wx = Math.Min(fx1, x + 1) - Math.Max(fx0, x), wy = Math.Min(fy1, y + 1) - Math.Max(fy0, y), weight = wx * wy;
                        if (weight <= 0) continue;
                        total += weight;
                        int colourIndex = indices[y * sourceWidth + x];
                        if (colourIndex == 0) continue;
                        uint colour = colours[colourIndex];
                        r += (colour >> 16 & 0xFF) * weight; g += (colour >> 8 & 0xFF) * weight; b += (colour & 0xFF) * weight; opaque += weight;
                    }
                if (total <= 0 || opaque / total < 110 / 255.0) continue;
                outPixels[(H - targetHeight + ty) * ShrunkWidth + tx] = 0xFF000000u | (uint)Math.Round(r / opaque) << 16 | (uint)Math.Round(g / opaque) << 8 | (uint)Math.Round(b / opaque);
            }
        return (ShrunkWidth, H, outPixels);
    }

    private static int Dist(uint a, uint b)
    {
        int dr = (int)(a >> 16 & 0xFF) - (int)(b >> 16 & 0xFF), dg = (int)(a >> 8 & 0xFF) - (int)(b >> 8 & 0xFF), db = (int)(a & 0xFF) - (int)(b & 0xFF);
        return dr * dr * 30 + dg * dg * 59 + db * db * 11;
    }

    private static int Nearest(uint[] palette, uint colour)
    {
        int best = 0, bestDistance = int.MaxValue;
        for (int i = 0; i < palette.Length; i++) { int distance = Dist(palette[i], colour); if (distance < bestDistance) { bestDistance = distance; best = i; } }
        return best;
    }

    // 15 colours for two pictures together (k-means from the most common colours, snapped to the SNES's 5 bits)
    private static uint[] SharedPalette(IEnumerable<uint> pixels)
    {
        uint Snap(uint c) => 0xFF000000u | (c & 0xF8F8F8u);
        var points = pixels.Where(p => p >> 24 != 0).Select(Snap).ToArray();
        var centres = points.GroupBy(p => p).OrderByDescending(g => g.Count()).Select(g => g.Key).Take(15).ToList();
        while (centres.Count < 15) centres.Add(centres.Count > 0 ? centres[^1] : 0xFF000000u);
        for (int iter = 0; iter < 12; iter++)
        {
            var sum = new double[15, 3]; var counts = new int[15];
            foreach (var point in points)
            {
                int nearest = Nearest(centres.ToArray(), point);
                sum[nearest, 0] += point >> 16 & 0xFF; sum[nearest, 1] += point >> 8 & 0xFF; sum[nearest, 2] += point & 0xFF; counts[nearest]++;
            }
            for (int k = 0; k < 15; k++)
                if (counts[k] > 0) centres[k] = Snap(0xFF000000u | (uint)(sum[k, 0] / counts[k]) << 16 | (uint)(sum[k, 1] / counts[k]) << 8 | (uint)(sum[k, 2] / counts[k]));
        }
        return centres.ToArray();
    }
}
