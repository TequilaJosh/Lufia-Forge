namespace LufiaForge.Core.Lufia2;

/// <summary>
/// A new game's whole starting party on the map. The new-game records (<see cref="L2Characters"/>) fill the battle party
/// ($0A7A count, $0A7B..7E heroes), but the walking list ($0926..092A) that makes the party's map characters is set to
/// Maxim alone by $83:ADCA (at $83:AE56) on the way into a new game, and only "joins" (2B, $81:F723) fills it. With the
/// game's own start (Maxim alone) that never shows; with a bigger starting party the other heroes have no map character,
/// and a cutscene step that walks one of them waits forever (checked in the game: Karlloon North Shrine B1's trigger
/// with Maxim, Selan, Guy and Dekar).
/// <para>The hook: in the new-game code at $83:ACE7 (LDA #$FF / STA $099D, just before JMP $B18E; the battle party is
/// loaded by then) a JSL to a small block that does those two and copies $0A7B..7E over $0926..0929. It is put in when
/// the starting party has more than one hero and taken out (the game's bytes again) when it has one.</para>
/// </summary>
public static class L2StartParty
{
    public const int Site = 0x1ACE7;
    private static readonly byte[] SiteOriginal = { 0xA9, 0xFF, 0x8D, 0x9D, 0x09 };
    private const string Magic = "LF2PT";
    private const int BlockSize = 0x40, CodeAt = 0x08;

    /// <summary>File offset of the hook's block, or -1 (not in).</summary>
    public static int Block(RomBuffer rom)
    {
        if (rom.Length <= Site + 4 || rom.ReadByte(Site) != 0x22) return -1;
        int at = L2Rom.SnesToFile((int)rom.ReadUInt24Le(Site + 1)) - CodeAt;
        return at >= 0 && at + BlockSize <= rom.Length && System.Text.Encoding.ASCII.GetString(rom.ReadBytes(at, Magic.Length)) == Magic ? at : -1;
    }

    /// <summary>Puts the hook in or takes it out to match the new game's party: call after the party changes.</summary>
    public static void Keep(RomBuffer rom)
    {
        bool need = L2Characters.ReadStart(rom).PartySize > 1;
        int b = Block(rom);
        if (!need)
        {
            if (b >= 0) rom.WriteBytes(Site, SiteOriginal);
            return;
        }
        if (b >= 0) return;
        if (!rom.ReadBytes(Site, SiteOriginal.Length).SequenceEqual(SiteOriginal)) return;   // not the US game's code there
        b = L2Rom.Allocate(rom, BlockSize);
        var c = new byte[]
        {
            0xA9, 0xFF, 0x8F, 0x9D, 0x09, 0x7E,   // LDA #$FF / STA $7E:099D (the game's two)
            0xDA,                                 // PHX
            0xA2, 0x03, 0x00,                     // LDX #3
            0xBF, 0x7B, 0x0A, 0x7E,               // LDA $7E:0A7B,X (battle party)
            0x9F, 0x26, 0x09, 0x7E,               // STA $7E:0926,X (walking list)
            0xCA, 0x10, 0xF5,                     // DEX / BPL
            0xFA,                                 // PLX
            0xA9, 0xFF,                           // LDA #$FF (A as the game leaves it)
            0x6B,                                 // RTL
        };
        rom.WriteBytes(b, new byte[BlockSize]);
        rom.WriteBytes(b, System.Text.Encoding.ASCII.GetBytes(Magic));
        rom.WriteBytes(b + CodeAt, c);
        int code = L2Rom.FileToSnes(b + CodeAt);
        rom.WriteBytes(Site, new byte[] { 0x22, (byte)code, (byte)(code >> 8), (byte)(code >> 16), 0xEA });
    }
}
