using LufiaForge.Core.Maps;

namespace LufiaForge.Core.Battle;

/// <summary>
/// "Status effects on equipment" patch.
/// <para>Resist: item property 0D (a = ailment number 1-8, b = chance multiplier in 16ths, FF = immune) only worked
/// on the accessory: $08:F858 copied it to the character ($1788 / $178C) from the last slot only. With the patch every
/// equipped item's 0D is gathered while the equipment is unpacked ($08:F898, once per slot): $1788 holds the
/// ailments as bits and $178C the strongest multiplier; the battle's per-target table ($08:B9CB) gets that multiplier
/// for each of those ailments.</para>
/// <para>On hit: the unused property 12 (a = ailment bits, b = chance out of 255) on a weapon makes a party member's
/// hits inflict the ailment: after the Gloom Pick check ($08:C170 → $08:C17D) the hook re-reads the attacker's weapon,
/// lowers the chance by the target's resistance ($08:BA69), skips targets that can't get it ($08:C966), rolls
/// ($08:B829), applies it ($08:C7A4) and shows the message ($08:C874).</para>
/// </summary>
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

    private static int SnesAddress(int file) => (file / 0x8000 | 0x80) << 16 | (file % 0x8000 | 0x8000);
    private static int FileOffset(int snes) => (snes >> 16 & 0x7F) * 0x8000 + (snes & 0x7FFF);

    public static bool IsOriginal(RomBuffer rom) =>
        rom.ReadBytes(HitSite, 3).SequenceEqual(HitOriginal) && rom.ReadBytes(AccSite, 6).SequenceEqual(AccOriginal) &&
        rom.ReadBytes(CopySite, 12).SequenceEqual(CopyOriginal) && rom.ReadBytes(ResistSite, 13).SequenceEqual(ResistOriginal) &&
        rom.ReadBytes(Stubs, 28).All(b => b == 0);

    private static int Block(RomBuffer rom)
    {
        if (rom.ReadByte(AccSite) != 0x22) return 0;
        int b = FileOffset(rom.ReadByte(AccSite + 1) | rom.ReadByte(AccSite + 2) << 8 | rom.ReadByte(AccSite + 3) << 16) - 8;
        return b > 0 && rom.ReadBytes(b, 4).SequenceEqual(Magic) ? b : 0;
    }
    public static bool IsApplied(RomBuffer rom) => Block(rom) > 0;

    public static void Apply(RomBuffer rom)
    {
        if (IsApplied(rom)) return;
        if (!IsOriginal(rom)) throw new InvalidOperationException("The game's equipment / battle code isn't the original, so status effects on equipment can't be added safely.");
        if (rom.Length < MapWriter.ExpandedSize) rom.Expand(MapWriter.ExpandedSize);
        var space = ExpansionSpace.Load(rom);
        int at = space.AllocateInOneBank(256, ExpansionSpace.Kind.Patch, 19);
        space.Save();
        int s = SnesAddress(at);
        // bank 08 stubs (the routines end with RTS): $FF7F hit, $FF87 BB02, $FF8B BA69, $FF8F C966, $FF93 C7A4, $FF97 C874
        const int sBB02 = 0x08FF87, sBA69 = 0x08FF8B, sC966 = 0x08FF8F, sC7A4 = 0x08FF93, sC874 = 0x08FF97;
        var code = new List<byte>();
        void Op(params int[] b) { foreach (int x in b) code.Add((byte)x); }
        void Jsl(int a) => Op(0x22, a & 0xFF, a >> 8 & 0xFF, a >> 16 & 0xFF);
        void LdaL(int a) => Op(0xAF, a & 0xFF, a >> 8 & 0xFF, a >> 16 & 0xFF);
        void StaL(int a) => Op(0x8F, a & 0xFF, a >> 8 & 0xFF, a >> 16 & 0xFF);
        Op(Magic[0], Magic[1], Magic[2], Magic[3], 0, 0, 0, 0);
        // ── acc: from $08:F898 inside the loop over slots 2-6 (M8 X8; Y counter pushed: 5 = first slot) ──
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
        int j6 = code.Count; Op(0xF0, 0x00);   // BEQ keep (immune already)
        Op(0xC3, 0x01);                   // CMP 1,S
        int j7 = code.Count; Op(0x90, 0x00);   // BCC keep (the old one is stronger)
        int pop = code.Count; Op(0x68);   // pop: PLA
        int j8 = code.Count; Op(0x80, 0x00);   // BRA set
        int keep = code.Count; Op(0x68);  // keep: PLA
        int j9 = code.Count; Op(0x80, 0x00);   // BRA done
        int set = code.Count; Op(0x9D, 0x8C, 0x17);   // set: STA $178C,X
        int done = code.Count;
        Op(0xFA);                         // done: PLX
        Op(0xAC, 0xEA, 0x14, 0xAD, 0xEF, 0x17);   // LDY $14EA; LDA $17EF (the two instructions replaced)
        Op(0x6B);
        void Fix(int at2, int target) => code[at2 + 1] = (byte)(target - (at2 + 2));
        Fix(j1, done); Fix(j2, done); Fix(j3, done); Fix(j4, set); Fix(j5, pop); Fix(j6, keep); Fix(j7, keep); Fix(j8, set); Fix(j9, done);
        // ── resist: from $08:B9CB (M8 X8, DB 7E, X = character): each ailment bit gets the multiplier ──
        int resist = code.Count;
        Op(0xBD, 0x88, 0x17);             // LDA $1788,X
        Op(0xF0, 0x12);                   // BEQ out
        Op(0xA0, 0x00);                   // LDY #0
        int rl = code.Count;
        Op(0x4A);                         // LSR
        Op(0x90, 0x08);                   // BCC next (over the 8 store bytes)
        Op(0x48, 0xBD, 0x8C, 0x17, 0x99, 0x80, 0xDA, 0x68);   // PHA; LDA $178C,X; STA $DA80,Y; PLA
        Op(0xC8);                         // next: INY
        Op(0xC9, 0x00);                   // CMP #0
        Op(0xD0, (byte)(rl - (code.Count + 2)));   // BNE loop
        Op(0x6B);                         // out: RTL
        // ── onHit: from the stub at $08:FF7F after JSR $C17D (DB as the battle has it: long addresses) ──
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
        Jsl(sC966); int h4 = code.Count; Op(0xD0, 0x00);       // can't get it (stone, dead, has it)
        Op(0xA9, 0xFF); Jsl(0x08B829);    // random
        Op(0xC5, 0x17); int h5 = code.Count; Op(0xB0, 0x00);   // CMP $17; BCS out
        Jsl(sC7A4);                       // apply
        Op(0xA5, 0x16, 0xC9, 0x20); int h6 = code.Count; Op(0xF0, 0x00);   // death: no message (as the Gloom Pick)
        Op(0xA9, 0x00); StaL(0x7E14DF); Jsl(sC874);            // the message
        int hout = code.Count;
        Op(0x28, 0x6B);                   // PLP; RTL
        Fix(h1, hout); Fix(h2, hout); Fix(h3, hout); Fix(h4, hout); Fix(h5, hout); Fix(h6, hout);
        int mask = code.Count;
        Op(0x01, 0x02, 0x04, 0x08, 0x10, 0x20, 0x40, 0x80);
        int m = s + mask;
        code[maskFix + 1] = (byte)m; code[maskFix + 2] = (byte)(m >> 8); code[maskFix + 3] = (byte)(m >> 16);
        if (code.Count > 256) throw new InvalidOperationException("Equipment status code too long.");
        rom.WriteBytes(at, code.ToArray());
        int hitA = s + hit;
        rom.WriteBytes(Stubs, new byte[]
        {
            0x20, 0x7D, 0xC1, 0x22, (byte)hitA, (byte)(hitA >> 8), (byte)(hitA >> 16), 0x60,   // JSR $C17D; JSL onHit; RTS
            0x20, 0x02, 0xBB, 0x6B, 0x20, 0x69, 0xBA, 0x6B, 0x20, 0x66, 0xC9, 0x6B,
            0x20, 0xA4, 0xC7, 0x6B, 0x20, 0x74, 0xC8, 0x6B,
        });
        rom.WriteBytes(HitSite, new byte[] { 0x20, 0x7F, 0xFF });
        int a = s + acc, r = s + resist;
        rom.WriteBytes(AccSite, new byte[] { 0x22, (byte)a, (byte)(a >> 8), (byte)(a >> 16), 0xEA, 0xEA });
        rom.WriteBytes(CopySite, Enumerable.Repeat((byte)0xEA, 12).ToArray());
        var rs = Enumerable.Repeat((byte)0xEA, 13).ToArray();
        rs[0] = 0x22; rs[1] = (byte)r; rs[2] = (byte)(r >> 8); rs[3] = (byte)(r >> 16); rs[4] = 0x80; rs[5] = 0x07;   // BRA $B9D8
        rom.WriteBytes(ResistSite, rs);
        rom.FixChecksum();
    }

    public static void Remove(RomBuffer rom)
    {
        int b = Block(rom);
        if (b == 0) return;
        rom.WriteBytes(HitSite, HitOriginal);
        rom.WriteBytes(AccSite, AccOriginal);
        rom.WriteBytes(CopySite, CopyOriginal);
        rom.WriteBytes(ResistSite, ResistOriginal);
        rom.WriteBytes(Stubs, new byte[28]);
        var space = ExpansionSpace.Load(rom);
        space.Free(b);
        space.Save();
        rom.FixChecksum();
    }
}
