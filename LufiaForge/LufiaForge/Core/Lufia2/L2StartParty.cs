namespace LufiaForge.Core.Lufia2;

// a new game's whole starting party on the map. the new-game records (L2Characters) fill the battle party ($0A7A
// count, $0A7B..7E heroes), but the walking list ($0926..092A) that makes the party's map characters gets set to Maxim
// alone by $83:ADCA (at $83:AE56) on the way into a new game, and only "joins" (2B, $81:F723) fills it. with the game's
// own start (Maxim alone) that never shows. with a bigger starting party the other heroes have no map character, and
// a cutscene step that walks one of them waits forever (checked in the game: Karlloon North Shrine B1's trigger with
// Maxim, Selan, Guy and Dekar).
// the hook: in the new-game code at $83:ACE7 (LDA #$FF / STA $099D, just before JMP $B18E, the battle party is loaded
// by then) a JSL to a small block that does those two and copies $0A7B..7E over $0926..0929. it goes in when the
// starting party has more than one hero and comes out (the game's bytes again) when it has one
public static class L2StartParty
{
    public const int Site = 0x1ACE7;
    private static readonly byte[] SiteOriginal = { 0xA9, 0xFF, 0x8D, 0x9D, 0x09 };
    private const string Magic = "LF2PT";
    private const int BlockSize = 0x40, CodeAt = 0x08;

    // file offset of the hook's block, or -1 (not in)
    public static int Block(RomBuffer rom)
    {
        if (rom.Length <= Site + 4 || rom.ReadByte(Site) != 0x22) return -1;
        int blockAt = L2Rom.SnesToFile((int)rom.ReadUInt24Le(Site + 1)) - CodeAt;
        return blockAt >= 0 && blockAt + BlockSize <= rom.Length && System.Text.Encoding.ASCII.GetString(rom.ReadBytes(blockAt, Magic.Length)) == Magic ? blockAt : -1;
    }

    // puts the hook in or takes it out to match the new game's party. call this after the party changes
    public static void Keep(RomBuffer rom)
    {
        bool need = L2Characters.ReadStart(rom).PartySize > 1;
        int block = Block(rom);
        if (!need)
        {
            if (block >= 0) rom.WriteBytes(Site, SiteOriginal);
            return;
        }
        if (block >= 0) return;
        if (!rom.ReadBytes(Site, SiteOriginal.Length).SequenceEqual(SiteOriginal)) return;   // not the US game's code there
        block = L2Rom.Allocate(rom, BlockSize);
        var hookCode = new byte[]
        {
            0xA9, 0xFF, 0x8F, 0x9D, 0x09, 0x7E,   // LDA #$FF / STA $7E:099D (the game's two)
            0xDA,                                 // PHX
            0xA2, 0x03, 0x00,                     // LDX #3
            0xBF, 0x7B, 0x0A, 0x7E,               // LDA $7E:0A7B,X (battle party)
            0x9F, 0x26, 0x09, 0x7E,               // STA $7E:0926,X (walking list)
            0xCA, 0x10, 0xF5,                     // DEX / BPL
            0xFA,                                 // PLX
            0xA9, 0xFF,                           // LDA #$FF (A the way the game leaves it)
            0x6B,                                 // RTL
        };
        rom.WriteBytes(block, new byte[BlockSize]);
        rom.WriteBytes(block, System.Text.Encoding.ASCII.GetBytes(Magic));
        rom.WriteBytes(block + CodeAt, hookCode);
        int code = L2Rom.FileToSnes(block + CodeAt);
        rom.WriteBytes(Site, new byte[] { 0x22, (byte)code, (byte)(code >> 8), (byte)(code >> 16), 0xEA });
    }
}
