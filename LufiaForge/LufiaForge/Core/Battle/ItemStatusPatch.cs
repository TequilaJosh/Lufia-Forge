using LufiaForge.Core.Maps;

namespace LufiaForge.Core.Battle;

// "Status effects on equipment" patch.
// resist: item property 0D (a = ailment number 1-8, b = chance multiplier in 16ths, FF = immune) only worked
// on the accessory, $08:F858 copied it to the character ($1788 / $178C) from the last slot only. with the patch every
// equipped item's 0D gets gathered while the equipment is unpacked ($08:F898, once per slot): $1788 holds the
// ailments as bits and $178C the strongest multiplier, and the battle's per-target table ($08:B9CB) gets that
// multiplier for each of those ailments.
// on hit: the unused property 12 (a = ailment bits, b = chance out of 255) on a weapon makes a party member's
// hits inflict the ailment. after the Gloom Pick check ($08:C170 -> $08:C17D) the hook reads the attacker's weapon
// again, lowers the chance by the target's resistance ($08:BA69), skips targets that can't get it ($08:C966), rolls
// ($08:B829), applies it ($08:C7A4) and shows the message ($08:C874)
public static class ItemStatusPatch
{
    public const int ResistProperty = 0x0D, OnHitProperty = 0x12;
    private const int Stubs = 0x47F7F;                       // $08:FF7F, free (zero) in the game
    private const int HitSite = 0x44170;                     // JSR $C17D
    private const int AccSite = 0x47898, CopySite = 0x478D8, ResistSite = 0x439CB;
    private static readonly byte[] HitOriginal = { 0x20, 0x7D, 0xC1 };
    private static readonly byte[] AccOriginal = Convert.FromHexString("ACEA14ADEF17");
    private static readonly byte[] CopyOriginal = Convert.FromHexString("ADE1179D8817ADE2179D8C17");
    private static readonly byte[] ResistOriginal = Convert.FromHexString("BD8817F0083AA8BD8C179980DA");
    private static readonly byte[] Magic = "LFIS"u8.ToArray();

    private static int SnesAddress(int fileOffset) => (fileOffset / 0x8000 | 0x80) << 16 | (fileOffset % 0x8000 | 0x8000);
    private static int FileOffset(int snesAddress) => (snesAddress >> 16 & 0x7F) * 0x8000 + (snesAddress & 0x7FFF);

    public static bool IsOriginal(RomBuffer rom) =>
        rom.ReadBytes(HitSite, 3).SequenceEqual(HitOriginal) && rom.ReadBytes(AccSite, 6).SequenceEqual(AccOriginal) &&
        rom.ReadBytes(CopySite, 12).SequenceEqual(CopyOriginal) && rom.ReadBytes(ResistSite, 13).SequenceEqual(ResistOriginal) &&
        rom.ReadBytes(Stubs, 28).All(b => b == 0);

    private static int Block(RomBuffer rom)
    {
        if (rom.ReadByte(AccSite) != 0x22) return 0;
        int block = FileOffset(rom.ReadByte(AccSite + 1) | rom.ReadByte(AccSite + 2) << 8 | rom.ReadByte(AccSite + 3) << 16) - 8;
        return block > 0 && rom.ReadBytes(block, 4).SequenceEqual(Magic) ? block : 0;
    }
    public static bool IsApplied(RomBuffer rom) => Block(rom) > 0;

    public static void Apply(RomBuffer rom)
    {
        if (IsApplied(rom)) return;
        if (!IsOriginal(rom)) throw new InvalidOperationException("The game's equipment / battle code isn't the original, so status effects on equipment can't be added safely.");
        if (rom.Length < MapWriter.ExpandedSize) rom.Expand(MapWriter.ExpandedSize);
        var freeSpace = ExpansionSpace.Load(rom);
        int codeOffset = freeSpace.AllocateInOneBank(256, ExpansionSpace.Kind.Patch, 19);
        freeSpace.Save();
        int codeSnes = SnesAddress(codeOffset);
        // bank 08 stubs (the routines end with RTS): $FF7F hit, $FF87 BB02, $FF8B BA69, $FF8F C966, $FF93 C7A4, $FF97 C874
        const int sBB02 = 0x08FF87, sBA69 = 0x08FF8B, sC966 = 0x08FF8F, sC7A4 = 0x08FF93, sC874 = 0x08FF97;
        var code = new List<byte>();
        void Op(params int[] bytes) { foreach (int x in bytes) code.Add((byte)x); }
        void Jsl(int address) => Op(0x22, address & 0xFF, address >> 8 & 0xFF, address >> 16 & 0xFF);
        void LdaL(int address) => Op(0xAF, address & 0xFF, address >> 8 & 0xFF, address >> 16 & 0xFF);
        void StaL(int address) => Op(0x8F, address & 0xFF, address >> 8 & 0xFF, address >> 16 & 0xFF);
        Op(Magic[0], Magic[1], Magic[2], Magic[3], 0, 0, 0, 0);
        // acc: from $08:F898 inside the loop over slots 2-6 (M8 X8, Y counter pushed: 5 = first slot)
        int acc = code.Count;
        Op(0xDA);                         // PHX
        Op(0xA3, 0x05);                   // LDA 5,S (loop counter)
        Op(0xC9, 0x05, 0xD0, 0x09);       // CMP #5; BNE +9
        Op(0xAE, 0xEA, 0x14);             // LDX $14EA
        Op(0x9E, 0x88, 0x17);             // STZ $1788,X
        Op(0x9E, 0x8C, 0x17);             // STZ $178C,X
        Op(0xAD, 0xE1, 0x17);             // LDA $17E1 (this item's 0D a)
        int j1 = code.Count; Op(0xF0, 0x00);   // BEQ done
        Op(0xC9, 0x09);                   // CMP #9
        int j2 = code.Count; Op(0xB0, 0x00);   // BCS done
        Op(0x3A, 0xAA);                   // DEC A; TAX
        int maskFix = code.Count; Op(0xBF, 0, 0, 0);   // LDA mask,X
        Op(0xAE, 0xEA, 0x14);             // LDX $14EA
        Op(0x1D, 0x88, 0x17, 0x9D, 0x88, 0x17);   // ORA $1788,X; STA $1788,X
        Op(0xAD, 0xE2, 0x17);             // LDA $17E2 (b)
        int j3 = code.Count; Op(0xF0, 0x00);   // BEQ done
        Op(0xC9, 0xFF);                   // CMP #$FF
        int j4 = code.Count; Op(0xF0, 0x00);   // BEQ set
        Op(0x48);                         // PHA
        Op(0xBD, 0x8C, 0x17);             // LDA $178C,X
        int j5 = code.Count; Op(0xF0, 0x00);   // BEQ pop (none yet)
        Op(0xC9, 0xFF);                   // CMP #$FF
        int j6 = code.Count; Op(0xF0, 0x00);   // BEQ keep (already immune)
        Op(0xC3, 0x01);                   // CMP 1,S
        int j7 = code.Count; Op(0x90, 0x00);   // BCC keep (the old one is stronger)
        int pop = code.Count; Op(0x68);   // pop: PLA
        int j8 = code.Count; Op(0x80, 0x00);   // BRA set
        int keep = code.Count; Op(0x68);  // keep: PLA
        int j9 = code.Count; Op(0x80, 0x00);   // BRA done
        int set = code.Count; Op(0x9D, 0x8C, 0x17);   // set: STA $178C,X
        int done = code.Count;
        Op(0xFA);                         // done: PLX
        Op(0xAC, 0xEA, 0x14, 0xAD, 0xEF, 0x17);   // LDY $14EA; LDA $17EF (the two instructions we replaced)
        Op(0x6B);
        void Fix(int branchAt, int target) => code[branchAt + 1] = (byte)(target - (branchAt + 2));
        Fix(j1, done); Fix(j2, done); Fix(j3, done); Fix(j4, set); Fix(j5, pop); Fix(j6, keep); Fix(j7, keep); Fix(j8, set); Fix(j9, done);
        // resist: from $08:B9CB (M8 X8, DB 7E, X = character), each ailment bit gets the multiplier
        int resist = code.Count;
        Op(0xBD, 0x88, 0x17);             // LDA $1788,X
        Op(0xF0, 0x12);                   // BEQ out
        Op(0xA0, 0x00);                   // LDY #0
        int loopStart = code.Count;
        Op(0x4A);                         // LSR
        Op(0x90, 0x08);                   // BCC next (over the 8 store bytes)
        Op(0x48, 0xBD, 0x8C, 0x17, 0x99, 0x80, 0xDA, 0x68);   // PHA; LDA $178C,X; STA $DA80,Y; PLA
        Op(0xC8);                         // next: INY
        Op(0xC9, 0x00);                   // CMP #0
        Op(0xD0, (byte)(loopStart - (code.Count + 2)));   // BNE loop
        Op(0x6B);                         // out: RTL
        // onHit: from the stub at $08:FF7F after JSR $C17D (DB is whatever the battle has, so long addresses)
        int hit = code.Count;
        Op(0x08, 0xE2, 0x30);             // PHP; SEP #$30
        LdaL(0x7E1440); int h1 = code.Count; Op(0x10, 0x00);   // party attacker only (bit 7)
        LdaL(0x7EDA04); int h2 = code.Count; Op(0xD0, 0x00);   // missed / Gloom Pick handled it
        LdaL(0x7E1440); Jsl(sBB02); Op(0xAA);                  // TAX (party index)
        Op(0xBF, 0xC0, 0x16, 0x7E);       // LDA $7E16C0,X (weapon)
        Jsl(0x08F999);                    // unpack its properties
        LdaL(0x7E17EB); int h3 = code.Count; Op(0xF0, 0x00);   // property 12 a (ailment bits)
        Op(0x85, 0x16);                   // STA $16
        LdaL(0x7E17EC); Op(0x85, 0x17);   // chance
        LdaL(0x7E1451); Jsl(sBB02); StaL(0x7E14DC);            // the target
        Jsl(sBA69);                       // chance x target's resistance
        Jsl(sC966); int h4 = code.Count; Op(0xD0, 0x00);       // can't get it (stone, dead, already has it)
        Op(0xA9, 0xFF); Jsl(0x08B829);    // random
        Op(0xC5, 0x17); int h5 = code.Count; Op(0xB0, 0x00);   // CMP $17; BCS out
        Jsl(sC7A4);                       // apply
        Op(0xA5, 0x16, 0xC9, 0x20); int h6 = code.Count; Op(0xF0, 0x00);   // death: no message (same as the Gloom Pick)
        Op(0xA9, 0x00); StaL(0x7E14DF); Jsl(sC874);            // the message
        int hout = code.Count;
        Op(0x28, 0x6B);                   // PLP; RTL
        Fix(h1, hout); Fix(h2, hout); Fix(h3, hout); Fix(h4, hout); Fix(h5, hout); Fix(h6, hout);
        int mask = code.Count;
        Op(0x01, 0x02, 0x04, 0x08, 0x10, 0x20, 0x40, 0x80);
        int maskAddress = codeSnes + mask;
        code[maskFix + 1] = (byte)maskAddress; code[maskFix + 2] = (byte)(maskAddress >> 8); code[maskFix + 3] = (byte)(maskAddress >> 16);
        if (code.Count > 256) throw new InvalidOperationException("Equipment status code too long.");
        rom.WriteBytes(codeOffset, code.ToArray());
        int hitAddress = codeSnes + hit;
        rom.WriteBytes(Stubs, new byte[]
        {
            0x20, 0x7D, 0xC1, 0x22, (byte)hitAddress, (byte)(hitAddress >> 8), (byte)(hitAddress >> 16), 0x60,   // JSR $C17D; JSL onHit; RTS
            0x20, 0x02, 0xBB, 0x6B, 0x20, 0x69, 0xBA, 0x6B, 0x20, 0x66, 0xC9, 0x6B,
            0x20, 0xA4, 0xC7, 0x6B, 0x20, 0x74, 0xC8, 0x6B,
        });
        rom.WriteBytes(HitSite, new byte[] { 0x20, 0x7F, 0xFF });
        int accAddress = codeSnes + acc, resistAddress = codeSnes + resist;
        rom.WriteBytes(AccSite, new byte[] { 0x22, (byte)accAddress, (byte)(accAddress >> 8), (byte)(accAddress >> 16), 0xEA, 0xEA });
        rom.WriteBytes(CopySite, Enumerable.Repeat((byte)0xEA, 12).ToArray());
        var resistBytes = Enumerable.Repeat((byte)0xEA, 13).ToArray();
        resistBytes[0] = 0x22; resistBytes[1] = (byte)resistAddress; resistBytes[2] = (byte)(resistAddress >> 8); resistBytes[3] = (byte)(resistAddress >> 16); resistBytes[4] = 0x80; resistBytes[5] = 0x07;   // BRA $B9D8
        rom.WriteBytes(ResistSite, resistBytes);
        rom.FixChecksum();
    }

    public static void Remove(RomBuffer rom)
    {
        int block = Block(rom);
        if (block == 0) return;
        rom.WriteBytes(HitSite, HitOriginal);
        rom.WriteBytes(AccSite, AccOriginal);
        rom.WriteBytes(CopySite, CopyOriginal);
        rom.WriteBytes(ResistSite, ResistOriginal);
        rom.WriteBytes(Stubs, new byte[28]);
        var freeSpace = ExpansionSpace.Load(rom);
        freeSpace.Free(block);
        freeSpace.Save();
        rom.FixChecksum();
    }
}
