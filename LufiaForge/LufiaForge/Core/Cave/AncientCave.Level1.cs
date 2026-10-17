using LufiaForge.Modules.GameData;

namespace LufiaForge.Core.Cave;

public static partial class AncientCave
{
    /// <summary>
    /// After the game's join routine ($08:EA8F) has set a hero up from their record (level, growth counter, the six
    /// stats twice, EXP, equipment), this puts in their level-1 numbers from <see cref="Level1Data"/>, then lets the
    /// game work out max HP / MP again ($08:E884, as the join does) and fills HP / MP up. DB = $7E.
    /// </summary>
    private const string Level1Source = """
        Level1:                     ; A(8) = character 0-3
            PHP
            REP #$30
            AND.w #$0003
            TAY                     ; Y = character
            ASL #5
            TAX                     ; X = its 32 bytes in Level1Data
            SEP #$20
            LDA.b #$01
            STA $156F,y             ; level 1
            LDA.l Level1Data,x
            STA $1573,y             ; growth counter
            REP #$20
            TYA
            ASL
            TAY                     ; Y = character * 2
            LDA.l Level1Data+2,x : STA $15F1,y : STA $1621,y     ; HP
            LDA.l Level1Data+4,x : STA $15F9,y : STA $1629,y     ; MP
            LDA.l Level1Data+6,x : STA $1601,y : STA $1631,y     ; AGL
            LDA.l Level1Data+8,x : STA $1609,y : STA $1639,y     ; INT
            LDA.l Level1Data+10,x : STA $1611,y : STA $1641,y    ; STR
            LDA.l Level1Data+12,x : STA $1619,y : STA $1649,y    ; MGR
            TYA
            LSR
            TAY                     ; Y = character
            SEP #$20
            LDA.l Level1Data+14,x : STA $1691,y   ; EXP now, next level, increment: 3 bytes each, 4 apart
            LDA.l Level1Data+15,x : STA $1695,y
            LDA.l Level1Data+16,x : STA $1699,y
            LDA.l Level1Data+17,x : STA $169D,y
            LDA.l Level1Data+18,x : STA $16A1,y
            LDA.l Level1Data+19,x : STA $16A5,y
            LDA.l Level1Data+20,x : STA $16A9,y
            LDA.l Level1Data+21,x : STA $16AD,y
            LDA.l Level1Data+22,x : STA $16B1,y
            PHY
            SEP #$30
            PHB
            LDA.b #$08
            PHA
            PLB
            JSL $08E884             ; max HP / MP from the new stats (as the join routine does)
            PLB
            REP #$30
            PLA
            AND.w #$00FF
            ASL
            TAY
            LDA $16E0,y
            STA $157F,y             ; HP and MP full
            LDA $16E8,y
            STA $1587,y
            PLP
            RTS
        """;

    /// <summary>
    /// Level-1 numbers for the four heroes, 32 bytes each: growth counter, the six stats (record order: HP MP AGL INT
    /// STR MGR), EXP now / next level / increment (24-bit). Heroes who join above level 1 are scaled down
    /// (see <see cref="Level1(RomBuffer, int)"/>).
    /// </summary>
    public static byte[] Level1Data(RomBuffer rom)
    {
        var data = new byte[4 * 32];
        for (int c = 0; c < 4; c++)
        {
            var r = Level1(rom, c);
            int o = c * 32;
            data[o] = (byte)r.Counter;
            for (int s = 0; s < 6; s++) { data[o + 2 + 2 * s] = (byte)r.Stats[s]; data[o + 3 + 2 * s] = (byte)(r.Stats[s] >> 8); }
            foreach (var (v, at) in new[] { (0L, 14), (r.Next, 17), (r.Increment, 20) })
            { data[o + at] = (byte)v; data[o + at + 1] = (byte)(v >> 8); data[o + at + 2] = (byte)(v >> 16); }
        }
        return data;
    }

    public sealed record Level1Hero(int Counter, int[] Stats, long Next, long Increment);

    /// <summary>
    /// A hero's level-1 setup (stats in record order HP MP AGL INT STR MGR). Heroes who join above level 1 (Aguro at 5,
    /// Jerin at 14) start their growth counter at 0 when they join, so their own curve says nothing about earlier
    /// levels: they are scaled from a partner who starts at level 1 (Aguro from the Hero, Jerin from Lufia). Each
    /// stat is the joining stat x (partner's level-1 stat / partner's stat at the joining level), and they take the
    /// partner's level-1 EXP numbers; from there they grow with their own growth table.
    /// </summary>
    public static Level1Hero Level1(RomBuffer rom, int c)
    {
        var p = LevelGrowth.Read(rom, c);
        int rec = GameDataOffsets.CharacterTable + c * GameDataOffsets.CharacterStride;
        var start = Enumerable.Range(0, 6).Select(i => (int)rom.ReadUInt16Le(rec + 2 + 2 * i)).ToArray();
        if (p.StartLevel <= 1) return new Level1Hero(p.Counter, start, p.FirstLevelExp, p.Increment);
        int partner = c == 3 ? 1 : 0;
        var q = LevelGrowth.Read(rom, partner);
        if (q.StartLevel > 1) throw new InvalidOperationException("The Hero and Lufia must start at level 1 for the cave's level-1 party.");
        var levels = LevelGrowth.Simulate(q);
        var low = levels[0].Stats;
        var high = levels.First(l => l.Number == Math.Min(99, p.StartLevel)).Stats;
        var stats = new int[6];
        for (int i = 0; i < 6; i++)
        {
            // record order (+2..+12) is HP MP AGL INT STR MGR; LevelGrowth's is HP MP STR INT AGL MGR
            int gs = i switch { 0 => 0, 1 => 1, 2 => 4, 3 => 3, 4 => 2, _ => 5 };
            double ratio = high[gs] > 0 ? (double)low[gs] / high[gs] : 1;
            int floor = i == 1 && start[1] == 0 ? 0 : 1;
            stats[i] = Math.Max(floor, (int)Math.Round(start[i] * ratio));
        }
        return new Level1Hero(p.Counter, stats, q.FirstLevelExp, q.Increment);
    }
}
