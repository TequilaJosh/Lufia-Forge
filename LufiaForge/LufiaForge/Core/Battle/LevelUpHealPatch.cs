using LufiaForge.Core.Maps;

namespace LufiaForge.Core.Battle;

/// <summary>
/// HP and MP gained at a level-up are added to the character's current HP and MP too (the game only raises the
/// maximums). The level-up routine ($08:E601) raises the six stats, then works out max HP / MP again for the whole
/// party at $08:E884; that call is hooked: the routine keeps the levelling character's ($14DC) max HP ($16E0) and
/// MP ($16E8) from before, calls $08:E884, and adds what they went up by to current HP ($157F) and MP ($1587), at
/// most the new maximum. In a fight ($0548 = 3) the fight's copies ($7E:DA48 HP, $7E:DA50 MP; FFFF = no one) get it
/// too.
/// <code>
/// $08:E64C  22 Hook       (was JSL $08E884)
/// </code>
/// </summary>
public static class LevelUpHealPatch
{
    public const int Site = 0x4664C;   // $08:E64C
    private static readonly byte[] Original = { 0x22, 0x84, 0xE8, 0x08 };
    private const string Magic = "LFLH";

    private static int FileOffset(int snes) => (snes >> 16 & 0x7F) * 0x8000 + (snes & 0x7FFF);
    private static int SnesAddress(int file) => (file / 0x8000 | 0x80) << 16 | (file % 0x8000 | 0x8000);

    private const string Source = """
        Hook:                       ; M = X = 8, $14DC = the character levelling up
            PHP
            REP #$30
            LDA $14DC
            AND.w #$0003
            ASL
            TAX
            LDA $16E0,x             ; max HP and MP before
            PHA
            LDA $16E8,x
            PHA
            JSL $08E884             ; (replaced) max HP / MP worked out again
            REP #$30
            LDA $14DC
            AND.w #$0003
            ASL
            TAX
            LDA $16E8,x             ; what they went up by: MP at $01,s, HP at $03,s
            SEC
            SBC $01,s
            BPL +
            LDA.w #$0000
        +   STA $01,s
            LDA $16E0,x
            SEC
            SBC $03,s
            BPL +
            LDA.w #$0000
        +   STA $03,s
            LDA $03,s               ; current HP
            CLC
            ADC $157F,x
            CMP $16E0,x
            BCC +
            LDA $16E0,x
        +   STA $157F,x
            LDA $01,s               ; current MP
            CLC
            ADC $1587,x
            CMP $16E8,x
            BCC +
            LDA $16E8,x
        +   STA $1587,x
            SEP #$20
            LDA $0548
            CMP.b #$03
            REP #$20
            BNE .done
            LDA $7EDA48,x           ; in a fight: the fight's copies too
            CMP.w #$FFFF
            BEQ .done
            CLC
            ADC $03,s
            CMP $16E0,x
            BCC +
            LDA $16E0,x
        +   STA $7EDA48,x
            LDA $7EDA50,x
            CLC
            ADC $01,s
            CMP $16E8,x
            BCC +
            LDA $16E8,x
        +   STA $7EDA50,x
        .done
            PLA
            PLA
            PLP
            RTL
        """;

    public static bool IsOriginal(RomBuffer rom) => rom.ReadBytes(Site, Original.Length).SequenceEqual(Original);

    private static int Routine(RomBuffer rom)
    {
        if (rom.ReadByte(Site) != 0x22) return -1;
        int r = FileOffset(rom.ReadByte(Site + 1) | rom.ReadByte(Site + 2) << 8 | rom.ReadByte(Site + 3) << 16);
        return r >= 4 && r < rom.Length && rom.ReadBytes(r - 4, 4).SequenceEqual(Magic.Select(c => (byte)c)) ? r : -1;
    }

    public static bool IsApplied(RomBuffer rom) => Routine(rom) > 0;

    public static void Apply(RomBuffer rom)
    {
        if (IsApplied(rom)) return;
        if (!IsOriginal(rom)) throw new InvalidOperationException("The level-up code at $08:E64C isn't the original, so this can't be added safely.");
        if (rom.Length < MapWriter.ExpandedSize) rom.Expand(MapWriter.ExpandedSize);
        var a = new Asm65816();
        a.Source(Source);
        var space = ExpansionSpace.Load(rom);
        var probe = a.Build(0x808000);
        int at = space.AllocateInOneBank(probe.Length + 4, ExpansionSpace.Kind.Patch, 0);
        space.Save();
        int snes = SnesAddress(at + 4);
        var b = new Asm65816();
        b.Source(Source);
        var code = b.Build(snes);
        rom.WriteBytes(at, Magic.Select(c => (byte)c).ToArray());
        rom.WriteBytes(at + 4, code);
        rom.WriteBytes(Site, new byte[] { 0x22, (byte)snes, (byte)(snes >> 8), (byte)(snes >> 16) });
        rom.FixChecksum();
    }

    public static void Remove(RomBuffer rom)
    {
        int r = Routine(rom);
        if (r < 0) return;
        rom.WriteBytes(Site, Original);
        var space = ExpansionSpace.Load(rom);
        space.Free(r - 4);
        space.Save();
        rom.FixChecksum();
    }
}
