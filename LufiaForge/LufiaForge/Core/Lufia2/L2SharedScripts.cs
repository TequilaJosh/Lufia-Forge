using Step = LufiaForge.Core.Lufia2.L2AiScripts.Step;

namespace LufiaForge.Core.Lufia2;

/// <summary>
/// Shared battle scripts of Lufia Forge's own in place of the game's (the library at $96:FADD, run by op 42).
/// <para>Op 42 ($85:BD77) saves the caller's script pointer and base, then sets both to $96:(FADD + the table's word):
/// bank $96 is full, so a changed shared script can't grow there. A hook at $85:BD99 (in place of LDA #$96 / STA $BD /
/// STA $0A44 / REP / LDA $96:FADD,X / CLC / ADC #$FADD / STA $0A42 / STA $BB) looks the number up in Lufia Forge's table
/// first: an entry gives the script's 24-bit address (pointer and base, its jumps count from its start), none runs the
/// game's. Read from the game's code (2026-11-11).</para>
/// <para>The block: "LF2SS", version, code at +10, a table of 0x29 entries of 4 bytes (address, bank 0 = the game's)
/// at +80.</para>
/// </summary>
public static class L2SharedScripts
{
    private const string Magic = "LF2SS";
    private const int Version = 1, CodeAt = 0x10, TableAt = 0x80, EntrySize = 4;
    private const int BlockSize = TableAt + L2AiScripts.LibraryCount * EntrySize;
    private const int Site = 0x2BD99, SiteEnd = 0x2BDAF;   // up to the JMP $B45E, which stays
    private static readonly byte[] SiteOriginal =
        { 0xA9, 0x96, 0x85, 0xBD, 0x8D, 0x44, 0x0A, 0xC2, 0x20, 0xBF, 0xDD, 0xFA, 0x96, 0x18, 0x69, 0xDD, 0xFA, 0x8D, 0x42, 0x0A, 0x85, 0xBB };

    /// <summary>File offset of the block, or -1 when no shared script was replaced yet.</summary>
    public static int Block(RomBuffer rom)
    {
        if (rom.Length <= SiteEnd || rom.ReadByte(Site) != 0x22) return -1;
        int block = L2Rom.SnesToFile((int)rom.ReadUInt24Le(Site + 1)) - CodeAt;
        return block >= 0 && block + BlockSize <= rom.Length && System.Text.Encoding.ASCII.GetString(rom.ReadBytes(block, Magic.Length)) == Magic ? block : -1;
    }

    /// <summary>File offset of Lufia Forge's version of shared script <paramref name="n"/>, or -1 (the game's runs).</summary>
    public static int Override(RomBuffer rom, int n)
    {
        int b = Block(rom);
        if (b < 0 || n < 0 || n >= L2AiScripts.LibraryCount) return -1;
        int e = b + TableAt + n * EntrySize;
        return rom.ReadByte(e + 2) == 0 ? -1 : L2Rom.SnesToFile((int)rom.ReadUInt24Le(e));
    }

    /// <summary>Why shared scripts can't be replaced in this ROM, or null when they can.</summary>
    public static string? CantReplace(RomBuffer rom)
    {
        if (!rom.IsLufia2) return "Only Lufia II has these shared scripts.";
        if (Block(rom) >= 0) return null;
        if (!rom.ReadBytes(Site, SiteOriginal.Length).SequenceEqual(SiteOriginal))
            return "This ROM's code that runs shared scripts isn't the US game's (another patch changed it).";
        return null;
    }

    /// <summary>Runs <paramref name="steps"/> in place of shared script <paramref name="n"/> for everyone who calls it.</summary>
    public static void Replace(RomBuffer rom, int n, IReadOnlyList<Step> steps)
    {
        if (n < 0 || n >= L2AiScripts.LibraryCount) throw new ArgumentOutOfRangeException(nameof(n));
        if (CantReplace(rom) is { } no) throw new InvalidOperationException(no);
        if (steps.Any(s => s.Op == 0x42)) throw new InvalidOperationException("A shared script can't call another one (the game allows one level only).");
        var body = L2AiScripts.Build(steps, 0);
        int b = Install(rom);
        int at = Override(rom, n);
        // [capacity u16][script]: reused when it fits
        if (at < 0 || rom.ReadUInt16Le(at - 2) < body.Length)
        {
            int cap = Math.Max(body.Length + 8, 24);
            at = L2Rom.Allocate(rom, cap + 2) + 2;
            rom.WriteUInt16Le(at - 2, (ushort)cap);
        }
        rom.WriteBytes(at, new byte[rom.ReadUInt16Le(at - 2)]);
        rom.WriteBytes(at, body);
        int snes = L2Rom.FileToSnes(at);
        rom.WriteBytes(b + TableAt + n * EntrySize, new[] { (byte)snes, (byte)(snes >> 8), (byte)(snes >> 16), (byte)0 });
    }

    /// <summary>Back to the game's shared script <paramref name="n"/>. False when it was the game's already.</summary>
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

    /// <summary>The hook (JSL from $85:BD99; A 8-bit, X 16-bit = the script number x 2, data bank $85). Sets $BB-$BD
    /// (the script pointer) and $0A42-$0A44 (its base) to Lufia Forge's script when the table has one, else as the game.</summary>
    private static byte[] Assemble(int code, int table)
    {
        var a = new L2AiScripts.Asm(code);
        void Long(byte op, int addr) => a.B(op, (byte)addr, (byte)(addr >> 8), (byte)(addr >> 16));
        a.B(0xC2, 0x20, 0x8A, 0x0A, 0xAA);                    // REP #$20 / TXA / ASL / TAX (number x 4)
        Long(0xBF, table + 2); a.B(0x29, 0xFF, 0x00);         // LDA table+2,X / AND #$00FF (its bank)
        a.Br(0xF0, "game");                                   // BEQ: none
        a.B(0xE2, 0x20, 0x85, 0xBD, 0x8D, 0x44, 0x0A);        // SEP #$20 / STA $BD / STA $0A44
        a.B(0xC2, 0x20); Long(0xBF, table);                   // REP #$20 / LDA table,X
        a.B(0x48, 0x8A, 0x4A, 0xAA, 0x68);                    // PHA / TXA / LSR / TAX / PLA (X as the game leaves it)
        a.B(0x8D, 0x42, 0x0A, 0x85, 0xBB, 0x6B);              // STA $0A42 / STA $BB / RTL
        a.Label("game");
        a.B(0x8A, 0x4A, 0xAA);                                // TXA / LSR / TAX (number x 2 again)
        a.B(0xE2, 0x20, 0xA9, 0x96, 0x85, 0xBD, 0x8D, 0x44, 0x0A);   // as the game: SEP / LDA #$96 / STA $BD / STA $0A44
        a.B(0xC2, 0x20); Long(0xBF, 0x96FADD);                // REP #$20 / LDA $96:FADD,X
        a.B(0x18, 0x69, 0xDD, 0xFA, 0x8D, 0x42, 0x0A, 0x85, 0xBB, 0x6B);   // CLC / ADC #$FADD / STA $0A42 / STA $BB / RTL
        return a.Done();
    }
}
