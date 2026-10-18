namespace LufiaForge.Core.Cave;

public static partial class AncientCave
{
    /// <summary>
    /// The floor counter: the floor number in the bottom right corner on every cave floor (hidden while a script or a
    /// text box runs). Two 8x8 sprites at the front of the OAM buffer show sprite tiles FE/FF (empty in every field
    /// map), which get the floor's digits each vblank from <see cref="FloorTiles"/>; colours 15 / 13 of sprite
    /// palette 0 (the fixed palette nothing in the field uses). Hooks (away from the emote patch's):
    /// <code>
    /// $01:A376  22 HudOam EA            the sprite builder's first free entry: the counter goes first (was STY $0C / JSR $A83A)
    /// $00:8249  22 HudNmi               vblank, after the OAM upload: the digits to tiles FE/FF (was STZ $3E / LDA $A3)
    /// </code>
    /// </summary>
    private const string HudSource = """
        !HUDX = 230
        !HUDY = 206

        HudOam:                     ; M = 8, X/Y = 16, DB = 01, Y = the first free OAM entry ($010A, or $0132)
            PHP
            PHX
            SEP #$20
            LDA $7E077F
            JSR IsCaveMap
            BNE .done
            LDA $7E0094             ; not while a script or a text box runs
            BNE .done
            LDA !AC_FLOOR
            BEQ .done
            REP #$20
            PHY
            LDA.w #!HUDY*256+!HUDX
            STA $0000,y
            LDA.w #$30FE            ; tile FE, sprite palette 0, in front of everything
            STA $0002,y
            LDA.w #!HUDY*256+!HUDX+8
            STA $0004,y
            LDA.w #$30FF
            STA $0006,y
            PLA                     ; their size / X bits small and 0: entry n = (Y - $010A) / 4, 2 bits each at $030A
            SEC
            SBC.w #$010A
            LSR
            LSR
            PHA
            LSR
            LSR
            TAX
            PLA
            AND.w #$0003
            SEP #$20
            BEQ +
            LDA $030A,x             ; entries 2-3 of the byte
            AND.b #$0F
            BRA ++
        +   LDA $030A,x             ; entries 0-1
            AND.b #$F0
        ++  STA $030A,x
            REP #$20
            TYA
            CLC
            ADC.w #8
            TAY
        .done
            PLX
            PLP
            STY $0C                 ; the replaced instructions
            PEA $AF2B               ; JSR $A83A, returning through the RTL at $01:AF2C
            JML $01A83A

        HudNmi:                     ; M = 8, X = 16, DB = 0
            STZ $3E                 ; (replaced)
            LDA $077F
            JSR IsCaveMap
            BNE .done
            LDA !AC_FLOOR
            BEQ .done
            CMP.b #101
            BCS .done
            PHX
            PHY
            REP #$20
            AND.w #$00FF
            DEC A
            ASL #6
            TAX
            LDA.w #$4FE0            ; sprite tile FE (sprites at VRAM word $4000)
            STA $2116
            SEP #$20
            LDA.b #$80
            STA $2115
            REP #$20
            LDY.w #32
        -   LDA.l FloorTiles,x
            STA $2118
            INX
            INX
            DEY
            BNE -
            SEP #$20
            PLY
            PLX
        .done
            LDA $A3                 ; (replaced)
            RTL
        """;

    private const int HudOamSite = 0x0A376, HudNmiSite = 0x00249;   // $01:A376, $00:8249
    private static readonly byte[] HudOamOriginal = { 0x84, 0x0C, 0x20, 0x3A, 0xA8 };
    private static readonly byte[] HudNmiOriginal = { 0x64, 0x3E, 0xA5, 0xA3 };

    // 4x6 digits, one string per row, '#' = ink
    private static readonly string[][] Digits =
    {
        new[] { ".##.", "#..#", "#..#", "#..#", "#..#", ".##." },
        new[] { ".#..", "##..", ".#..", ".#..", ".#..", "###." },
        new[] { ".##.", "#..#", "..#.", ".#..", "#...", "####" },
        new[] { "###.", "...#", ".##.", "...#", "...#", "###." },
        new[] { "#..#", "#..#", "####", "...#", "...#", "...#" },
        new[] { "####", "#...", "###.", "...#", "...#", "###." },
        new[] { ".##.", "#...", "###.", "#..#", "#..#", ".##." },
        new[] { "####", "...#", "..#.", ".#..", ".#..", ".#.." },
        new[] { ".##.", "#..#", ".##.", "#..#", "#..#", ".##." },
        new[] { ".##.", "#..#", "#..#", ".###", "...#", ".##." },
    };

    /// <summary>Floors 1-100: two 4bpp 8x8 tiles each (64 bytes), the number right-aligned, colour 15 with a colour 13
    /// outline.</summary>
    public static byte[] FloorTiles()
    {
        var data = new byte[100 * 64];
        for (int floor = 1; floor <= 100; floor++)
        {
            var px = new int[16, 8];
            string s = floor.ToString();
            for (int d = 0; d < s.Length; d++)
            {
                int x0 = 11 - 5 * (s.Length - 1 - d);
                var g = Digits[s[d] - '0'];
                for (int y = 0; y < 6; y++)
                    for (int x = 0; x < 4; x++)
                        if (g[y][x] == '#') px[x0 + x, 1 + y] = 15;
            }
            for (int y = 0; y < 8; y++)
                for (int x = 0; x < 16; x++)
                {
                    if (px[x, y] == 15) continue;
                    bool near = false;
                    for (int dy = -1; dy <= 1 && !near; dy++)
                        for (int dx = -1; dx <= 1; dx++)
                        {
                            int nx = x + dx, ny = y + dy;
                            if (nx >= 0 && nx < 16 && ny >= 0 && ny < 8 && px[nx, ny] == 15) { near = true; break; }
                        }
                    if (near) px[x, y] = 13;
                }
            int o = (floor - 1) * 64;
            for (int t = 0; t < 2; t++)
                for (int y = 0; y < 8; y++)
                {
                    byte p0 = 0, p1 = 0, p2 = 0, p3 = 0;
                    for (int x = 0; x < 8; x++)
                    {
                        int c = px[t * 8 + x, y], bit = 7 - x;
                        if ((c & 1) != 0) p0 |= (byte)(1 << bit);
                        if ((c & 2) != 0) p1 |= (byte)(1 << bit);
                        if ((c & 4) != 0) p2 |= (byte)(1 << bit);
                        if ((c & 8) != 0) p3 |= (byte)(1 << bit);
                    }
                    int b = o + t * 32;
                    data[b + 2 * y] = p0; data[b + 2 * y + 1] = p1;
                    data[b + 16 + 2 * y] = p2; data[b + 17 + 2 * y] = p3;
                }
        }
        return data;
    }
}
