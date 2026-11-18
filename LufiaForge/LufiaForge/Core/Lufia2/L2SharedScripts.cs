using Step = LufiaForge.Core.Lufia2.L2AiScripts.Step;

namespace LufiaForge.Core.Lufia2;

// our own shared battle scripts in place of the game's (the library at $96:FADD, run by op 42).
// op 42 ($85:BD77) saves the caller's script pointer and base, then sets both to $96:(FADD + the table's word). bank
// $96 is full, so a changed shared script can't grow there. a hook at $85:BD99 (in place of LDA #$96 / STA $BD /
// STA $0A44 / REP / LDA $96:FADD,X / CLC / ADC #$FADD / STA $0A42 / STA $BB) looks the number up in our table first:
// an entry gives the script's 24-bit address (pointer and base, its jumps count from its start), no entry runs the
// game's. read from the game's code (2026-11-11).
// the block: "LF2SS", version, code at +10, and at +80 a table of 0x29 entries of 4 bytes (address, bank 0 = the game's)
public static class L2SharedScripts
{
    private const string Magic = "LF2SS";
    private const int Version = 1, CodeAt = 0x10, TableAt = 0x80, EntrySize = 4;
    private const int BlockSize = TableAt + L2AiScripts.LibraryCount * EntrySize;
    private const int Site = 0x2BD99, SiteEnd = 0x2BDAF;   // up to the JMP $B45E, which stays
    private static readonly byte[] SiteOriginal =
        { 0xA9, 0x96, 0x85, 0xBD, 0x8D, 0x44, 0x0A, 0xC2, 0x20, 0xBF, 0xDD, 0xFA, 0x96, 0x18, 0x69, 0xDD, 0xFA, 0x8D, 0x42, 0x0A, 0x85, 0xBB };

    // file offset of the block, or -1 if no shared script was replaced yet
    public static int Block(RomBuffer rom)
    {
        if (rom.Length <= SiteEnd || rom.ReadByte(Site) != 0x22) return -1;
        int block = L2Rom.SnesToFile((int)rom.ReadUInt24Le(Site + 1)) - CodeAt;
        return block >= 0 && block + BlockSize <= rom.Length && System.Text.Encoding.ASCII.GetString(rom.ReadBytes(block, Magic.Length)) == Magic ? block : -1;
    }

    // file offset of our version of shared script n, or -1 (the game's runs)
    public static int Override(RomBuffer rom, int n)
    {
        int block = Block(rom);
        if (block < 0 || n < 0 || n >= L2AiScripts.LibraryCount) return -1;
        int entry = block + TableAt + n * EntrySize;
        return rom.ReadByte(entry + 2) == 0 ? -1 : L2Rom.SnesToFile((int)rom.ReadUInt24Le(entry));
    }

    // why shared scripts can't be replaced in this rom, or null if they can
    public static string? CantReplace(RomBuffer rom)
    {
        if (!rom.IsLufia2) return "Only Lufia II has these shared scripts.";
        if (Block(rom) >= 0) return null;
        if (!rom.ReadBytes(Site, SiteOriginal.Length).SequenceEqual(SiteOriginal))
            return "This ROM's code that runs shared scripts isn't the US game's (another patch changed it).";
        return null;
    }

    // runs steps in place of shared script n for everyone who calls it
    public static void Replace(RomBuffer rom, int n, IReadOnlyList<Step> steps)
    {
        if (n < 0 || n >= L2AiScripts.LibraryCount) throw new ArgumentOutOfRangeException(nameof(n));
        if (CantReplace(rom) is { } no) throw new InvalidOperationException(no);
        if (steps.Any(s => s.Op == 0x42)) throw new InvalidOperationException("A shared script can't call another one (the game allows one level only).");
        var body = L2AiScripts.Build(steps, 0);
        int block = Install(rom);
        int scriptAt = Override(rom, n);
        // [capacity u16][script], reused if it fits
        if (scriptAt < 0 || rom.ReadUInt16Le(scriptAt - 2) < body.Length)
        {
            int capacity = Math.Max(body.Length + 8, 24);
            scriptAt = L2Rom.Allocate(rom, capacity + 2) + 2;
            rom.WriteUInt16Le(scriptAt - 2, (ushort)capacity);
        }
        rom.WriteBytes(scriptAt, new byte[rom.ReadUInt16Le(scriptAt - 2)]);
        rom.WriteBytes(scriptAt, body);
        int snesAddress = L2Rom.FileToSnes(scriptAt);
        rom.WriteBytes(block + TableAt + n * EntrySize, new[] { (byte)snesAddress, (byte)(snesAddress >> 8), (byte)(snesAddress >> 16), (byte)0 });
    }

    // back to the game's shared script n. false if it was the game's already
    public static bool Revert(RomBuffer rom, int n)
    {
        if (Override(rom, n) < 0) return false;
        rom.WriteBytes(Block(rom) + TableAt + n * EntrySize, new byte[EntrySize]);
        return true;
    }

    private static int Install(RomBuffer rom)
    {
        int have = Block(rom);
        if (have >= 0) return have;
        int block = L2Rom.Allocate(rom, BlockSize);
        int code = L2Rom.FileToSnes(block + CodeAt), table = L2Rom.FileToSnes(block + TableAt);
        var bytes = Assemble(code, table);
        if (CodeAt + bytes.Length > TableAt) throw new InvalidOperationException("The shared script hook doesn't fit.");
        rom.WriteBytes(block, new byte[BlockSize]);
        rom.WriteBytes(block, System.Text.Encoding.ASCII.GetBytes(Magic));
        rom.WriteByte(block + Magic.Length, Version);
        rom.WriteBytes(block + CodeAt, bytes);
        var site = new byte[SiteEnd - Site];
        Array.Fill(site, (byte)0xEA);
        site[0] = 0x22; site[1] = (byte)code; site[2] = (byte)(code >> 8); site[3] = (byte)(code >> 16);
        rom.WriteBytes(Site, site);
        return block;
    }

    // the hook (JSL from $85:BD99, A 8-bit, X 16-bit = the script number x 2, data bank $85). sets $BB-$BD (the script
    // pointer) and $0A42-$0A44 (its base) to our script if the table has one, otherwise like the game does
    private static byte[] Assemble(int code, int table)
    {
        var asm = new L2AiScripts.Asm(code);
        void Long(byte op, int addr) => asm.B(op, (byte)addr, (byte)(addr >> 8), (byte)(addr >> 16));
        asm.B(0xC2, 0x20, 0x8A, 0x0A, 0xAA);                    // REP #$20 / TXA / ASL / TAX (number x 4)
        Long(0xBF, table + 2); asm.B(0x29, 0xFF, 0x00);         // LDA table+2,X / AND #$00FF (its bank)
        asm.Br(0xF0, "game");                                   // BEQ: none
        asm.B(0xE2, 0x20, 0x85, 0xBD, 0x8D, 0x44, 0x0A);        // SEP #$20 / STA $BD / STA $0A44
        asm.B(0xC2, 0x20); Long(0xBF, table);                   // REP #$20 / LDA table,X
        asm.B(0x48, 0x8A, 0x4A, 0xAA, 0x68);                    // PHA / TXA / LSR / TAX / PLA (X the way the game leaves it)
        asm.B(0x8D, 0x42, 0x0A, 0x85, 0xBB, 0x6B);              // STA $0A42 / STA $BB / RTL
        asm.Label("game");
        asm.B(0x8A, 0x4A, 0xAA);                                // TXA / LSR / TAX (number x 2 again)
        asm.B(0xE2, 0x20, 0xA9, 0x96, 0x85, 0xBD, 0x8D, 0x44, 0x0A);   // like the game: SEP / LDA #$96 / STA $BD / STA $0A44
        asm.B(0xC2, 0x20); Long(0xBF, 0x96FADD);                // REP #$20 / LDA $96:FADD,X
        asm.B(0x18, 0x69, 0xDD, 0xFA, 0x8D, 0x42, 0x0A, 0x85, 0xBB, 0x6B);   // CLC / ADC #$FADD / STA $0A42 / STA $BB / RTL
        return asm.Done();
    }
}
