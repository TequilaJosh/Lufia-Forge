using LufiaForge.Core.Maps;

namespace LufiaForge.Core.Battle;

/// <summary>
/// Spell memory for up to 64 spells per character, part of the "Up to 128 spells" patch (<see cref="SpellTablePatch"/>).
/// The game kept each character's learned spells as a list in $158F-$15EE (96 bytes, saved with the game): the Hero
/// and Aguro shared 32 entries, Lufia and Jerin had 32 each. Now every character has one bit per spell, 16 bytes at
/// $158F + 16 x slot (spell s: byte s / 8, bit s % 8), and $15EE = B1 marks the new format. Spell memory in a save
/// from before (or a new game) is converted the first time any of the routines below runs. The save file layout is
/// unchanged.
/// <list type="bullet">
/// <item>$08:E829 learn (JML): sets the bit (used by event 3D, level-ups and joining).</item>
/// <item>$08:E65E level-up and $08:EC07 joining: the four learn lists, which live here (up to 64 entries each).</item>
/// <item>$08:EAEC a new member's spells cleared; $01:D2A7 / $01:D2BC event 45 "knows spell" tests the bit.</item>
/// <item>$08:BD07 the battle spell menu and $00:D100 / $00:9C27 the field Magic menu's count and "nth spell": each
/// character's own order, at most 64 (the battle menu's buffers hold 64).</item>
/// <item>Spell order: a list of 64 spell numbers per character in save RAM ($70:1A00 + 64 x slot while playing; save
/// slot + $500 in a save, inside the slot's checksum), copied by saving ($00:920A) and loading ($00:9293). Before a
/// menu shows it, the list is tidied: spells not known (or garbage, as in older saves) dropped, newly learned ones added
/// at the end. The field Magic menu's swap ($00:C21F) swaps two entries, as the game did with its own lists.</item>
/// </list>
/// <item>$00:C21F the field Magic menu's swap: it swapped two entries of the old lists (through the pointers at
/// $00:9CAD; Aguro's pointed at the party names, $14B5), which now scrambled the spell bits and Guy's names. A spell
/// order can't be kept in the new format, so the swap is skipped (BRA to its end: the selection is just dropped).</item>
/// </list>
/// Earlier versions of the Guy / Aguro magic patch (lists per slot, before 0.4.0-beta.7) are recognised: their lists
/// are read, their code is put back, then this is installed.
/// </summary>
public static class SpellMemory
{
    public const int MaxPerCharacter = 64;
    public const int LearnSite = 0x46829, LevelUpSite = 0x4665E, JoinSite = 0x46C07, ClearSite = 0x46AEC, KnowsSite = 0xD2A7,
                     KnowsLoopSite = 0xD2BC, CountSite = 0x5100, NthSite = 0x1C27, MenuSite = 0x43D07;
    /// <summary>
    /// The field Magic menu's swap at $00:C21F (A9 00 EB 18 A5 11 in the game): JSL swap, BRA $C24D. beta.8 skipped it
    /// (80 2C) instead. Saving ($00:920A, AD 53 19 A0 E3 03) and loading ($00:9293, E2 20 A9 00 48 AB) copy the order.
    /// </summary>
    public const int SwapSite = 0x421F, SaveSite = 0x120A, LoadSite = 0x1293;
    private static readonly byte[] SwapOriginal = Convert.FromHexString("A900EB18A511"), SwapSkip = { 0x80, 0x2C },
                                   SaveOriginal = Convert.FromHexString("AD5319A0E303"), LoadOriginal = Convert.FromHexString("E220A90048AB");
    /// <summary>The spell order's save RAM: $70:1A00 + 64 x slot while playing, save slot + $500 in a save.</summary>
    public const int OrderRam = 0x701A00, OrderInSlot = 0x500;
    private static bool Hooked(RomBuffer rom, int site, int entry) =>
        rom.ReadByte(site) == 0x22 && FileOffset(rom.ReadByte(site + 1) | rom.ReadByte(site + 2) << 8 | rom.ReadByte(site + 3) << 16) == BlockOffset(rom) + entry;
    /// <summary>Whether this ROM has the spell order (beta.9 on).</summary>
    public static bool SwapFixed(RomBuffer rom) => Current(rom) && Hooked(rom, SwapSite, Swap) && Hooked(rom, SaveSite, SaveOrder) && Hooked(rom, LoadSite, LoadOrder);
    /// <summary>Brings an installed spell memory up to date (the spell order); see <see cref="Install"/>.</summary>
    public static void FixSwap(RomBuffer rom) { if (Installed(rom) && !SwapFixed(rom)) Upgrade(rom); }

    /// <summary>The battle spell menu skips slot 2 ($08:BD03 CPX #$02): 02, or FF when Aguro has magic.</summary>
    public const int MenuSkip = 0x43D04;
    private static readonly byte[] LearnOriginal = Convert.FromHexString("088BE220");
    private static readonly byte[] LevelUpOriginal = Convert.FromHexString("8AC902F0210AA8B9EBE7A8B90DEEF012C9FFF012DD6F15D009B90EEE85D82229E808C8C880E5");
    private static readonly byte[] JoinOriginal = Convert.FromHexString("C902F01DAA0AA8B9EBE7A8B90DEEC9FFF00F0900D007B90EEE2229E808C8C880EA");
    private static readonly byte[] ClearOriginal = Convert.FromHexString("ACF114C002F00CBE4CE8A0209E8F15E888D0F9");
    private static readonly byte[] KnowsOriginal = Convert.FromHexString("2903C9039002A9020A0A0A0A0AAAA9208522");
    private static readonly byte[] KnowsLoopOriginal = Convert.FromHexString("DD8F15F00AE8C622D0F6");
    private static readonly byte[] CountOriginal = Convert.FromHexString("A24E19860CA20000BCAD9C8409A0FFFFC8B109D0FB98920CA40CC8840CE8E8E00800D0E460");
    private static readonly byte[] NthOriginal = Convert.FromHexString("BEAD9C8609A511186D5519CD5719B004A8B10960");
    private static readonly byte[] MenuOriginal = Convert.FromHexString("BC3FBDA97E48ABA200B98F159D00D8222DFA08AD9D179D40D8AD9A179D80D8AD9C179DC0D8ADA4179D40D9E8C8E020D0D8");
    private const int Ensure = 0, Learn = 151, LearnLevel = 231, LearnInitial = 299, ClearSpells = 355,
                      KnowsSetup = 399, KnowsCheck = 403, CountAll = 756, Nth = 839, BuildMenu = 910,
                      Swap = 1036, SaveOrder = 1141, LoadOrder = 1170,
                      Magic = 1205, Offs = 1209, Blob = 1217;
    /// <summary>The block of the version before the spell order (0.4.0-beta.7 to beta.8, "LFM2"): same start, the lists
    /// elsewhere. Recognised so Update can move its lists over.</summary>
    private const int MagicV2 = 811, OffsV2 = 815, BlobV2 = 823;
    /// <summary>The first spell-order build (a test ROM only; it moved the direct page): "LFM3" at another place.</summary>
    private const int MagicV3a = 1192, OffsV3a = 1196, BlobV3a = 1204;
    /// <summary>Room for the four learn lists: (level, spell) pairs and an FF each.</summary>
    public const int BlobSize = 4 * (MaxPerCharacter * 2 + 1);
    public const int BlockSize = Blob + BlobSize;
    /// <summary>Long operands to fix up when placed: (operand offset, block offset it points at).</summary>
    private static readonly (int At, int Target)[] Fixups =
    {
        (93, 1197), (169, 0), (185, 1197), (248, 1209), (255, 1217), (271, 1218), (287, 151), (309, 1209), (316, 1217), (328, 1218), (342, 151), (366, 0), (411, 0), (427, 1197), (678, 1197), (767, 0), (852, 0), (925, 0),
    };
    /// <summary>JSR operands inside the block (16-bit: the block is in one bank).</summary>
    private static readonly (int At, int Target)[] JsrFixups =
    {
        (559, 666), (566, 712), (573, 641), (592, 666), (599, 712), (606, 641), (624, 641), (776, 479), (931, 479),
    };

    private static readonly byte[] Routine =
    {
        // ensure:
        0x48,                      //   0 PHA
        0xAD, 0xEE, 0x15,          //   1 LDA $15EE
        0xC9, 0xB1,                //   4 CMP #$B1
        0xD0, 0x02,                //   6 BNE conv
        0x68,                      //   8 PLA
        0x6B,                      //   9 RTL
        // conv:
        0xDA,                      //  10 PHX
        0x5A,                      //  11 PHY
        0xA2, 0x5F, 0x00,          //  12 LDX #$005F
        // P1:
        0xBD, 0x8F, 0x15,          //  15 LDA $158F,X
        0x48,                      //  18 PHA
        0xCA,                      //  19 DEX
        0x10, 0xF9,                //  20 BPL P1
        0xA2, 0x5F, 0x00,          //  22 LDX #$005F
        // Z1:
        0x9E, 0x8F, 0x15,          //  25 STZ $158F,X
        0xCA,                      //  28 DEX
        0x10, 0xFA,                //  29 BPL Z1
        0xBA,                      //  31 TSX
        0xA0, 0x00, 0x00,          //  32 LDY #$0000
        // K1:
        0xE8,                      //  35 INX
        0xBD, 0x00, 0x00,          //  36 LDA $0000,X  (old list byte k)
        0xF0, 0x55,                //  39 BEQ kn
        0x30, 0x53,                //  41 BMI kn
        0xDA,                      //  43 PHX
        0x5A,                      //  44 PHY
        0x48,                      //  45 PHA  (spell)
        0xC0, 0x14, 0x00,          //  46 CPY #$0014
        0x90, 0x0E,                //  49 BCC s0
        0xC0, 0x20, 0x00,          //  51 CPY #$0020
        0x90, 0x0D,                //  54 BCC s2
        0xC0, 0x40, 0x00,          //  56 CPY #$0040
        0x90, 0x0C,                //  59 BCC s1
        0xA9, 0x03,                //  61 LDA #$03
        0x80, 0x0A,                //  63 BRA sg
        // s0:
        0xA9, 0x00,                //  65 LDA #$00
        0x80, 0x06,                //  67 BRA sg
        // s2:
        0xA9, 0x02,                //  69 LDA #$02
        0x80, 0x02,                //  71 BRA sg
        // s1:
        0xA9, 0x01,                //  73 LDA #$01
        // sg:
        0x0A,                      //  75 ASL
        0x0A,                      //  76 ASL
        0x0A,                      //  77 ASL
        0x0A,                      //  78 ASL
        0x48,                      //  79 PHA  (slot x16)
        0xA3, 0x02,                //  80 LDA 2,S  (spell)
        0x29, 0x07,                //  82 AND #$07
        0xC2, 0x20,                //  84 REP #$20
        0x29, 0x07, 0x00,          //  86 AND #$0007
        0xAA,                      //  89 TAX
        0xE2, 0x20,                //  90 SEP #$20
        0xBF, 0x00, 0x00, 0x00,    //  92 LDA mask,X
        0x48,                      //  96 PHA  (mask)
        0xA3, 0x03,                //  97 LDA 3,S  (spell)
        0x4A,                      //  99 LSR
        0x4A,                      // 100 LSR
        0x4A,                      // 101 LSR
        0x18,                      // 102 CLC
        0x63, 0x02,                // 103 ADC 2,S
        0xC2, 0x20,                // 105 REP #$20
        0x29, 0xFF, 0x00,          // 107 AND #$00FF
        0xAA,                      // 110 TAX
        0xE2, 0x20,                // 111 SEP #$20
        0xBD, 0x8F, 0x15,          // 113 LDA $158F,X
        0x03, 0x01,                // 116 ORA 1,S
        0x9D, 0x8F, 0x15,          // 118 STA $158F,X
        0x68,                      // 121 PLA
        0x68,                      // 122 PLA
        0x68,                      // 123 PLA
        0x7A,                      // 124 PLY
        0xFA,                      // 125 PLX
        // kn:
        0xC8,                      // 126 INY
        0xC0, 0x60, 0x00,          // 127 CPY #$0060
        0xD0, 0x9F,                // 130 BNE K1
        0xC2, 0x20,                // 132 REP #$20
        0x3B,                      // 134 TSC
        0x18,                      // 135 CLC
        0x69, 0x60, 0x00,          // 136 ADC #$0060
        0x1B,                      // 139 TCS
        0xE2, 0x20,                // 140 SEP #$20
        0xA9, 0xB1,                // 142 LDA #$B1
        0x8D, 0xEE, 0x15,          // 144 STA $15EE  (marker)
        0x7A,                      // 147 PLY
        0xFA,                      // 148 PLX
        0x68,                      // 149 PLA
        0x6B,                      // 150 RTL
        // learn:
        0x08,                      // 151 PHP
        0x8B,                      // 152 PHB
        0xE2, 0x20,                // 153 SEP #$20
        0xC2, 0x10,                // 155 REP #$10
        0xDA,                      // 157 PHX
        0x5A,                      // 158 PHY
        0x48,                      // 159 PHA  (spell)
        0x8A,                      // 160 TXA
        0x29, 0x03,                // 161 AND #$03
        0x48,                      // 163 PHA  (slot)
        0xA9, 0x7E,                // 164 LDA #$7E
        0x48,                      // 166 PHA
        0xAB,                      // 167 PLB
        0x22, 0x00, 0x00, 0x00,    // 168 JSL ensure
        0xA3, 0x02,                // 172 LDA 2,S  (spell)
        0x29, 0x07,                // 174 AND #$07
        0xC2, 0x20,                // 176 REP #$20
        0x29, 0x07, 0x00,          // 178 AND #$0007
        0xAA,                      // 181 TAX
        0xE2, 0x20,                // 182 SEP #$20
        0xBF, 0x00, 0x00, 0x00,    // 184 LDA mask,X
        0x48,                      // 188 PHA  (mask)
        0xA3, 0x02,                // 189 LDA 2,S  (slot)
        0x0A,                      // 191 ASL
        0x0A,                      // 192 ASL
        0x0A,                      // 193 ASL
        0x0A,                      // 194 ASL
        0x48,                      // 195 PHA  (slot x16)
        0xA3, 0x04,                // 196 LDA 4,S  (spell)
        0x29, 0x7F,                // 198 AND #$7F
        0x4A,                      // 200 LSR
        0x4A,                      // 201 LSR
        0x4A,                      // 202 LSR
        0x18,                      // 203 CLC
        0x63, 0x01,                // 204 ADC 1,S
        0xC2, 0x20,                // 206 REP #$20
        0x29, 0xFF, 0x00,          // 208 AND #$00FF
        0xAA,                      // 211 TAX
        0xE2, 0x20,                // 212 SEP #$20
        0xBD, 0x8F, 0x15,          // 214 LDA $158F,X
        0x03, 0x02,                // 217 ORA 2,S
        0x9D, 0x8F, 0x15,          // 219 STA $158F,X
        0x68,                      // 222 PLA
        0x68,                      // 223 PLA
        0x68,                      // 224 PLA
        0x68,                      // 225 PLA  (A = spell, as before)
        0x7A,                      // 226 PLY
        0xFA,                      // 227 PLX
        0xAB,                      // 228 PLB
        0x28,                      // 229 PLP
        0x6B,                      // 230 RTL
        // learnLevel:
        0x08,                      // 231 PHP
        0xC2, 0x10,                // 232 REP #$10
        0xDA,                      // 234 PHX  (slot)
        0xBD, 0x6F, 0x15,          // 235 LDA $156F,X  (level)
        0x48,                      // 238 PHA
        0xC2, 0x20,                // 239 REP #$20
        0x8A,                      // 241 TXA
        0x29, 0x03, 0x00,          // 242 AND #$0003
        0x0A,                      // 245 ASL
        0xAA,                      // 246 TAX
        0xBF, 0x00, 0x00, 0x00,    // 247 LDA offs,X
        0xAA,                      // 251 TAX
        0xE2, 0x20,                // 252 SEP #$20
        // L1:
        0xBF, 0x00, 0x00, 0x00,    // 254 LDA blob,X
        0xC9, 0xFF,                // 258 CMP #$FF
        0xF0, 0x21,                // 260 BEQ L9
        0xC9, 0x00,                // 262 CMP #$00
        0xF0, 0x19,                // 264 BEQ L2
        0xC3, 0x01,                // 266 CMP 1,S  (this level?)
        0xD0, 0x15,                // 268 BNE L2
        0xBF, 0x00, 0x00, 0x00,    // 270 LDA blob+1,X
        0x85, 0xD8,                // 274 STA $D8
        0xDA,                      // 276 PHX
        0x48,                      // 277 PHA
        0xC2, 0x20,                // 278 REP #$20
        0xA3, 0x05,                // 280 LDA 5,S  (slot)
        0xAA,                      // 282 TAX
        0xE2, 0x20,                // 283 SEP #$20
        0x68,                      // 285 PLA
        0x22, 0x00, 0x00, 0x00,    // 286 JSL learn
        0xFA,                      // 290 PLX
        // L2:
        0xE8,                      // 291 INX
        0xE8,                      // 292 INX
        0x80, 0xD7,                // 293 BRA L1
        // L9:
        0x68,                      // 295 PLA
        0xFA,                      // 296 PLX
        0x28,                      // 297 PLP
        0x6B,                      // 298 RTL
        // learnInitial:
        0x08,                      // 299 PHP
        0xC2, 0x30,                // 300 REP #$30
        0x29, 0x03, 0x00,          // 302 AND #$0003
        0x48,                      // 305 PHA  (slot, 2 bytes)
        0x0A,                      // 306 ASL
        0xAA,                      // 307 TAX
        0xBF, 0x00, 0x00, 0x00,    // 308 LDA offs,X
        0xAA,                      // 312 TAX
        0xE2, 0x20,                // 313 SEP #$20
        // M1:
        0xBF, 0x00, 0x00, 0x00,    // 315 LDA blob,X
        0xC9, 0xFF,                // 319 CMP #$FF
        0xF0, 0x1B,                // 321 BEQ M9
        0xC9, 0x00,                // 323 CMP #$00  (known from the start)
        0xD0, 0x13,                // 325 BNE M2
        0xBF, 0x00, 0x00, 0x00,    // 327 LDA blob+1,X
        0xDA,                      // 331 PHX
        0x48,                      // 332 PHA
        0xC2, 0x20,                // 333 REP #$20
        0xA3, 0x04,                // 335 LDA 4,S  (slot)
        0xAA,                      // 337 TAX
        0xE2, 0x20,                // 338 SEP #$20
        0x68,                      // 340 PLA
        0x22, 0x00, 0x00, 0x00,    // 341 JSL learn
        0xFA,                      // 345 PLX
        // M2:
        0xE8,                      // 346 INX
        0xE8,                      // 347 INX
        0x80, 0xDD,                // 348 BRA M1
        // M9:
        0xC2, 0x20,                // 350 REP #$20
        0x68,                      // 352 PLA
        0x28,                      // 353 PLP
        0x6B,                      // 354 RTL
        // clearSpells:
        0x08,                      // 355 PHP
        0x8B,                      // 356 PHB
        0xE2, 0x20,                // 357 SEP #$20
        0xC2, 0x10,                // 359 REP #$10
        0xA9, 0x7E,                // 361 LDA #$7E
        0x48,                      // 363 PHA
        0xAB,                      // 364 PLB
        0x22, 0x00, 0x00, 0x00,    // 365 JSL ensure
        0xAD, 0xF1, 0x14,          // 369 LDA $14F1
        0x29, 0x03,                // 372 AND #$03
        0x0A,                      // 374 ASL
        0x0A,                      // 375 ASL
        0x0A,                      // 376 ASL
        0x0A,                      // 377 ASL
        0xC2, 0x20,                // 378 REP #$20
        0x29, 0xFF, 0x00,          // 380 AND #$00FF
        0xAA,                      // 383 TAX
        0xE2, 0x20,                // 384 SEP #$20
        0xA0, 0x10, 0x00,          // 386 LDY #$0010
        // C1:
        0x9E, 0x8F, 0x15,          // 389 STZ $158F,X
        0xE8,                      // 392 INX
        0x88,                      // 393 DEY
        0xD0, 0xF9,                // 394 BNE C1
        0xAB,                      // 396 PLB
        0x28,                      // 397 PLP
        0x6B,                      // 398 RTL
        // knowsSetup:
        0x29, 0x03,                // 399 AND #$03
        0xAA,                      // 401 TAX  (slot)
        0x6B,                      // 402 RTL
        // knowsCheck:
        0x8B,                      // 403 PHB
        0xDA,                      // 404 PHX
        0x48,                      // 405 PHA  (spell)
        0xA9, 0x7E,                // 406 LDA #$7E
        0x48,                      // 408 PHA
        0xAB,                      // 409 PLB
        0x22, 0x00, 0x00, 0x00,    // 410 JSL ensure
        0xA3, 0x01,                // 414 LDA 1,S
        0x29, 0x07,                // 416 AND #$07
        0xC2, 0x20,                // 418 REP #$20
        0x29, 0x07, 0x00,          // 420 AND #$0007
        0xAA,                      // 423 TAX
        0xE2, 0x20,                // 424 SEP #$20
        0xBF, 0x00, 0x00, 0x00,    // 426 LDA mask,X
        0x48,                      // 430 PHA  (mask)
        0xA3, 0x03,                // 431 LDA 3,S  (slot)
        0x29, 0x03,                // 433 AND #$03
        0x0A,                      // 435 ASL
        0x0A,                      // 436 ASL
        0x0A,                      // 437 ASL
        0x0A,                      // 438 ASL
        0x48,                      // 439 PHA
        0xA3, 0x03,                // 440 LDA 3,S  (spell)
        0x29, 0x7F,                // 442 AND #$7F
        0x4A,                      // 444 LSR
        0x4A,                      // 445 LSR
        0x4A,                      // 446 LSR
        0x18,                      // 447 CLC
        0x63, 0x01,                // 448 ADC 1,S
        0xC2, 0x20,                // 450 REP #$20
        0x29, 0xFF, 0x00,          // 452 AND #$00FF
        0xAA,                      // 455 TAX
        0xE2, 0x20,                // 456 SEP #$20
        0xBD, 0x8F, 0x15,          // 458 LDA $158F,X
        0x23, 0x02,                // 461 AND 2,S
        0xF0, 0x04,                // 463 BEQ kno
        0xA9, 0x00,                // 465 LDA #$00
        0x80, 0x02,                // 467 BRA kr
        // kno:
        0xA9, 0x01,                // 469 LDA #$01
        // kr:
        0xEB,                      // 471 XBA
        0x68,                      // 472 PLA
        0x68,                      // 473 PLA
        0x68,                      // 474 PLA
        0xFA,                      // 475 PLX
        0xAB,                      // 476 PLB
        0xEB,                      // 477 XBA  (Z from the result)
        0x6B,                      // 478 RTL
        // sync:
        0x08,                      // 479 PHP
        0x8B,                      // 480 PHB
        0xE2, 0x20,                // 481 SEP #$20
        0xC2, 0x10,                // 483 REP #$10
        0x48,                      // 485 PHA
        0xA9, 0x7E,                // 486 LDA #$7E
        0x48,                      // 488 PHA
        0xAB,                      // 489 PLB
        0x68,                      // 490 PLA
        0xC2, 0x20,                // 491 REP #$20
        0x29, 0x03, 0x00,          // 493 AND #$0003
        0xA8,                      // 496 TAY  (slot)
        0x3B,                      // 497 TSC
        0x38,                      // 498 SEC
        0xE9, 0x0E, 0x00,          // 499 SBC #$000E
        0x1B,                      // 502 TCS
        0x98,                      // 503 TYA
        0x83, 0x01,                // 504 STA 1,S  (slot, w = 0)
        0x0A,                      // 506 ASL
        0x0A,                      // 507 ASL
        0x0A,                      // 508 ASL
        0x0A,                      // 509 ASL
        0x83, 0x07,                // 510 STA 7,S  (bits offset)
        0x0A,                      // 512 ASL
        0x0A,                      // 513 ASL
        0x83, 0x05,                // 514 STA 5,S  (list base)
        0xE2, 0x20,                // 516 SEP #$20
        0xA9, 0x00,                // 518 LDA #$00
        0x83, 0x03,                // 520 STA 3,S  (r)
        // YC:
        0xA3, 0x03,                // 522 LDA 3,S
        0xC9, 0x40,                // 524 CMP #$40
        0xB0, 0x31,                // 526 BCS yd
        0xC2, 0x20,                // 528 REP #$20
        0xA3, 0x03,                // 530 LDA 3,S
        0x29, 0xFF, 0x00,          // 532 AND #$00FF
        0x18,                      // 535 CLC
        0x63, 0x05,                // 536 ADC 5,S
        0xAA,                      // 538 TAX
        0xE2, 0x20,                // 539 SEP #$20
        0xBF, 0x00, 0x1A, 0x70,    // 541 LDA order,X
        0x83, 0x04,                // 545 STA 4,S
        0xA3, 0x03,                // 547 LDA 3,S
        0x1A,                      // 549 INC
        0x83, 0x03,                // 550 STA 3,S
        0xA3, 0x04,                // 552 LDA 4,S
        0xF0, 0x15,                // 554 BEQ yd  (0 ends the list)
        0x30, 0xDC,                // 556 BMI YC  (not a spell)
        0x20, 0x00, 0x00,          // 558 JSR known
        0x90, 0xD7,                // 561 BCC YC
        0xA3, 0x04,                // 563 LDA 4,S
        0x20, 0x00, 0x00,          // 565 JSR inList
        0xB0, 0xD0,                // 568 BCS YC
        0xA3, 0x04,                // 570 LDA 4,S
        0x20, 0x00, 0x00,          // 572 JSR put
        0x80, 0xC9,                // 575 BRA YC
        // yd:
        0xA9, 0x01,                // 577 LDA #$01
        0x83, 0x09,                // 579 STA 9,S  (s)
        // YA:
        0xA3, 0x02,                // 581 LDA 2,S
        0xC9, 0x40,                // 583 CMP #$40
        0xB0, 0x1C,                // 585 BCS YF
        0xA3, 0x09,                // 587 LDA 9,S
        0x30, 0x18,                // 589 BMI YF
        0x20, 0x00, 0x00,          // 591 JSR known
        0x90, 0x0C,                // 594 BCC yn
        0xA3, 0x09,                // 596 LDA 9,S
        0x20, 0x00, 0x00,          // 598 JSR inList
        0xB0, 0x05,                // 601 BCS yn
        0xA3, 0x09,                // 603 LDA 9,S
        0x20, 0x00, 0x00,          // 605 JSR put
        // yn:
        0xA3, 0x09,                // 608 LDA 9,S
        0x1A,                      // 610 INC
        0x83, 0x09,                // 611 STA 9,S
        0x80, 0xDE,                // 613 BRA YA
        // YF:
        0xA3, 0x02,                // 615 LDA 2,S
        0xC9, 0x40,                // 617 CMP #$40
        0xB0, 0x07,                // 619 BCS yx
        0xA9, 0x00,                // 621 LDA #$00
        0x20, 0x00, 0x00,          // 623 JSR put
        0x80, 0xF3,                // 626 BRA YF
        // yx:
        0xC2, 0x20,                // 628 REP #$20
        0x3B,                      // 630 TSC
        0x18,                      // 631 CLC
        0x69, 0x0E, 0x00,          // 632 ADC #$000E
        0x1B,                      // 635 TCS
        0xE2, 0x20,                // 636 SEP #$20
        0xAB,                      // 638 PLB
        0x28,                      // 639 PLP
        0x60,                      // 640 RTS
        // put:
        0x48,                      // 641 PHA
        0xC2, 0x20,                // 642 REP #$20
        0xA3, 0x05,                // 644 LDA 5,S  (w)
        0x29, 0xFF, 0x00,          // 646 AND #$00FF
        0x18,                      // 649 CLC
        0x63, 0x08,                // 650 ADC 8,S  (base)
        0xAA,                      // 652 TAX
        0xE2, 0x20,                // 653 SEP #$20
        0x68,                      // 655 PLA
        0x9F, 0x00, 0x1A, 0x70,    // 656 STA order,X
        0xA3, 0x04,                // 660 LDA 4,S  (w)
        0x1A,                      // 662 INC
        0x83, 0x04,                // 663 STA 4,S
        0x60,                      // 665 RTS
        // known:
        0x48,                      // 666 PHA
        0x29, 0x07,                // 667 AND #$07
        0xC2, 0x20,                // 669 REP #$20
        0x29, 0x07, 0x00,          // 671 AND #$0007
        0xAA,                      // 674 TAX
        0xE2, 0x20,                // 675 SEP #$20
        0xBF, 0x00, 0x00, 0x00,    // 677 LDA mask,X
        0x83, 0x0E,                // 681 STA 14,S  (mask)
        0xA3, 0x01,                // 683 LDA 1,S
        0x4A,                      // 685 LSR
        0x4A,                      // 686 LSR
        0x4A,                      // 687 LSR
        0xC2, 0x20,                // 688 REP #$20
        0x29, 0xFF, 0x00,          // 690 AND #$00FF
        0x18,                      // 693 CLC
        0x63, 0x0A,                // 694 ADC 10,S  (bits offset)
        0xAA,                      // 696 TAX
        0xE2, 0x20,                // 697 SEP #$20
        0xBD, 0x8F, 0x15,          // 699 LDA $158F,X
        0x23, 0x0E,                // 702 AND 14,S
        0xF0, 0x03,                // 704 BEQ kn0
        0x68,                      // 706 PLA
        0x38,                      // 707 SEC
        0x60,                      // 708 RTS
        // kn0:
        0x68,                      // 709 PLA
        0x18,                      // 710 CLC
        0x60,                      // 711 RTS
        // inList:
        0x83, 0x10,                // 712 STA 16,S  (value)
        0xA9, 0x00,                // 714 LDA #$00
        0x83, 0x0E,                // 716 STA 14,S  (i)
        // IL:
        0xA3, 0x0E,                // 718 LDA 14,S
        0xC3, 0x04,                // 720 CMP 4,S  (w)
        0xB0, 0x1E,                // 722 BCS ilno
        0xC2, 0x20,                // 724 REP #$20
        0xA3, 0x0E,                // 726 LDA 14,S
        0x29, 0xFF, 0x00,          // 728 AND #$00FF
        0x18,                      // 731 CLC
        0x63, 0x07,                // 732 ADC 7,S  (base)
        0xAA,                      // 734 TAX
        0xE2, 0x20,                // 735 SEP #$20
        0xBF, 0x00, 0x1A, 0x70,    // 737 LDA order,X
        0xC3, 0x10,                // 741 CMP 16,S
        0xF0, 0x07,                // 743 BEQ ilyes
        0xA3, 0x0E,                // 745 LDA 14,S
        0x1A,                      // 747 INC
        0x83, 0x0E,                // 748 STA 14,S
        0x80, 0xDE,                // 750 BRA IL
        // ilyes:
        0x38,                      // 752 SEC
        0x60,                      // 753 RTS
        // ilno:
        0x18,                      // 754 CLC
        0x60,                      // 755 RTS
        // countAll:
        0x08,                      // 756 PHP
        0x8B,                      // 757 PHB
        0xE2, 0x20,                // 758 SEP #$20
        0xC2, 0x10,                // 760 REP #$10
        0xA9, 0x7E,                // 762 LDA #$7E
        0x48,                      // 764 PHA
        0xAB,                      // 765 PLB
        0x22, 0x00, 0x00, 0x00,    // 766 JSL ensure
        0xA9, 0x00,                // 770 LDA #$00
        0x48,                      // 772 PHA  (slot)
        // S1:
        0xA3, 0x01,                // 773 LDA 1,S
        0x20, 0x00, 0x00,          // 775 JSR sync
        0xC2, 0x20,                // 778 REP #$20
        0xA3, 0x01,                // 780 LDA 1,S
        0x29, 0x03, 0x00,          // 782 AND #$0003
        0x0A,                      // 785 ASL
        0x0A,                      // 786 ASL
        0x0A,                      // 787 ASL
        0x0A,                      // 788 ASL
        0x0A,                      // 789 ASL
        0x0A,                      // 790 ASL
        0xAA,                      // 791 TAX
        0xE2, 0x20,                // 792 SEP #$20
        0xA0, 0x00, 0x00,          // 794 LDY #$0000
        // cn:
        0xBF, 0x00, 0x1A, 0x70,    // 797 LDA order,X
        0xF0, 0x07,                // 801 BEQ cd
        0xC8,                      // 803 INY
        0xE8,                      // 804 INX
        0xC0, 0x40, 0x00,          // 805 CPY #$0040
        0xD0, 0xF3,                // 808 BNE cn
        // cd:
        0x98,                      // 810 TYA
        0x48,                      // 811 PHA
        0xC2, 0x20,                // 812 REP #$20
        0xA3, 0x02,                // 814 LDA 2,S  (slot)
        0x29, 0x03, 0x00,          // 816 AND #$0003
        0xA8,                      // 819 TAY
        0xE2, 0x20,                // 820 SEP #$20
        0x68,                      // 822 PLA
        0x99, 0x4E, 0x19,          // 823 STA $194E,Y
        0xA3, 0x01,                // 826 LDA 1,S
        0x1A,                      // 828 INC
        0x83, 0x01,                // 829 STA 1,S
        0xC9, 0x04,                // 831 CMP #$04
        0xD0, 0xC2,                // 833 BNE S1
        0x68,                      // 835 PLA
        0xAB,                      // 836 PLB
        0x28,                      // 837 PLP
        0x6B,                      // 838 RTL
        // nth:
        0x08,                      // 839 PHP
        0x8B,                      // 840 PHB
        0xE2, 0x20,                // 841 SEP #$20
        0xC2, 0x10,                // 843 REP #$10
        0xDA,                      // 845 PHX
        0x5A,                      // 846 PHY
        0xA9, 0x7E,                // 847 LDA #$7E
        0x48,                      // 849 PHA
        0xAB,                      // 850 PLB
        0x22, 0x00, 0x00, 0x00,    // 851 JSL ensure
        0xA5, 0x11,                // 855 LDA $11
        0x18,                      // 857 CLC
        0x6D, 0x55, 0x19,          // 858 ADC $1955
        0xCD, 0x57, 0x19,          // 861 CMP $1957
        0xB0, 0x23,                // 864 BCS none
        0xC9, 0x40,                // 866 CMP #$40
        0xB0, 0x1F,                // 868 BCS none
        0xC2, 0x20,                // 870 REP #$20
        0x29, 0xFF, 0x00,          // 872 AND #$00FF
        0x48,                      // 875 PHA  (n)
        0xA3, 0x05,                // 876 LDA 5,S  (slot)
        0x29, 0x03, 0x00,          // 878 AND #$0003
        0x0A,                      // 881 ASL
        0x0A,                      // 882 ASL
        0x0A,                      // 883 ASL
        0x0A,                      // 884 ASL
        0x0A,                      // 885 ASL
        0x0A,                      // 886 ASL
        0x18,                      // 887 CLC
        0x63, 0x01,                // 888 ADC 1,S
        0xAA,                      // 890 TAX
        0x68,                      // 891 PLA
        0xE2, 0x20,                // 892 SEP #$20
        0xBF, 0x00, 0x1A, 0x70,    // 894 LDA order,X
        0xEB,                      // 898 XBA
        0x80, 0x03,                // 899 BRA nout
        // none:
        0xA9, 0x00,                // 901 LDA #$00
        0xEB,                      // 903 XBA
        // nout:
        0x7A,                      // 904 PLY
        0xFA,                      // 905 PLX
        0xAB,                      // 906 PLB
        0x28,                      // 907 PLP
        0xEB,                      // 908 XBA  (Z from the spell)
        0x6B,                      // 909 RTL
        // buildMenu:
        0x08,                      // 910 PHP
        0x8B,                      // 911 PHB
        0xE2, 0x20,                // 912 SEP #$20
        0xC2, 0x10,                // 914 REP #$10
        0x8A,                      // 916 TXA
        0x29, 0x03,                // 917 AND #$03
        0x48,                      // 919 PHA  (slot)
        0xA9, 0x7E,                // 920 LDA #$7E
        0x48,                      // 922 PHA
        0xAB,                      // 923 PLB
        0x22, 0x00, 0x00, 0x00,    // 924 JSL ensure
        0xA3, 0x01,                // 928 LDA 1,S
        0x20, 0x00, 0x00,          // 930 JSR sync
        0xA9, 0x00,                // 933 LDA #$00
        0x48,                      // 935 PHA  (out)
        0xC2, 0x20,                // 936 REP #$20
        0xA3, 0x02,                // 938 LDA 2,S  (slot)
        0x29, 0x03, 0x00,          // 940 AND #$0003
        0x0A,                      // 943 ASL
        0x0A,                      // 944 ASL
        0x0A,                      // 945 ASL
        0x0A,                      // 946 ASL
        0x0A,                      // 947 ASL
        0x0A,                      // 948 ASL
        0x48,                      // 949 PHA  (base)
        0xE2, 0x20,                // 950 SEP #$20
        0xA0, 0x00, 0x00,          // 952 LDY #$0000  (i)
        // BL:
        0xC2, 0x20,                // 955 REP #$20
        0x98,                      // 957 TYA
        0x18,                      // 958 CLC
        0x63, 0x01,                // 959 ADC 1,S
        0xAA,                      // 961 TAX
        0xE2, 0x20,                // 962 SEP #$20
        0xBF, 0x00, 0x1A, 0x70,    // 964 LDA order,X
        0xF0, 0x38,                // 968 BEQ bz
        0x5A,                      // 970 PHY
        0x48,                      // 971 PHA  (spell)
        0xA3, 0x06,                // 972 LDA 6,S  (out)
        0xC2, 0x20,                // 974 REP #$20
        0x29, 0xFF, 0x00,          // 976 AND #$00FF
        0xAA,                      // 979 TAX
        0xE2, 0x20,                // 980 SEP #$20
        0x68,                      // 982 PLA
        0x9D, 0x00, 0xD8,          // 983 STA $D800,X
        0x22, 0x2D, 0xFA, 0x08,    // 986 JSL $08FA2D  (the spell data)
        0xAD, 0x9D, 0x17,          // 990 LDA $179D
        0x9D, 0x40, 0xD8,          // 993 STA $D840,X
        0xAD, 0x9A, 0x17,          // 996 LDA $179A
        0x9D, 0x80, 0xD8,          // 999 STA $D880,X
        0xAD, 0x9C, 0x17,          // 1002 LDA $179C
        0x9D, 0xC0, 0xD8,          // 1005 STA $D8C0,X
        0xAD, 0xA4, 0x17,          // 1008 LDA $17A4
        0x9D, 0x40, 0xD9,          // 1011 STA $D940,X
        0xA3, 0x05,                // 1014 LDA 5,S  (out)
        0x1A,                      // 1016 INC
        0x83, 0x05,                // 1017 STA 5,S
        0x7A,                      // 1019 PLY
        0xC8,                      // 1020 INY
        0xC0, 0x40, 0x00,          // 1021 CPY #$0040
        0xD0, 0xB9,                // 1024 BNE BL
        // bz:
        0xC2, 0x20,                // 1026 REP #$20
        0x68,                      // 1028 PLA  (base)
        0xE2, 0x20,                // 1029 SEP #$20
        0x68,                      // 1031 PLA  (out)
        0x68,                      // 1032 PLA  (slot)
        0xAB,                      // 1033 PLB
        0x28,                      // 1034 PLP
        0x6B,                      // 1035 RTL
        // swap:
        0x08,                      // 1036 PHP
        0x8B,                      // 1037 PHB
        0xE2, 0x20,                // 1038 SEP #$20
        0xC2, 0x10,                // 1040 REP #$10
        0xA9, 0x7E,                // 1042 LDA #$7E
        0x48,                      // 1044 PHA
        0xAB,                      // 1045 PLB
        0xAD, 0x03, 0x00,          // 1046 LDA $0003
        0xC2, 0x20,                // 1049 REP #$20
        0x29, 0xFF, 0x00,          // 1051 AND #$00FF
        0xAA,                      // 1054 TAX
        0xE2, 0x20,                // 1055 SEP #$20
        0xBD, 0xD2, 0x14,          // 1057 LDA $14D2,X  (slot)
        0xC2, 0x20,                // 1060 REP #$20
        0x29, 0x03, 0x00,          // 1062 AND #$0003
        0x0A,                      // 1065 ASL
        0x0A,                      // 1066 ASL
        0x0A,                      // 1067 ASL
        0x0A,                      // 1068 ASL
        0x0A,                      // 1069 ASL
        0x0A,                      // 1070 ASL
        0x48,                      // 1071 PHA  (base)
        0xE2, 0x20,                // 1072 SEP #$20
        0xA5, 0x11,                // 1074 LDA $11
        0x18,                      // 1076 CLC
        0x6D, 0x55, 0x19,          // 1077 ADC $1955
        0x29, 0x3F,                // 1080 AND #$3F
        0xC2, 0x20,                // 1082 REP #$20
        0x29, 0xFF, 0x00,          // 1084 AND #$00FF
        0x18,                      // 1087 CLC
        0x63, 0x01,                // 1088 ADC 1,S
        0xAA,                      // 1090 TAX
        0xE2, 0x20,                // 1091 SEP #$20
        0xAD, 0x58, 0x19,          // 1093 LDA $1958
        0x18,                      // 1096 CLC
        0x6D, 0x59, 0x19,          // 1097 ADC $1959
        0x29, 0x3F,                // 1100 AND #$3F
        0xC2, 0x20,                // 1102 REP #$20
        0x29, 0xFF, 0x00,          // 1104 AND #$00FF
        0x18,                      // 1107 CLC
        0x63, 0x01,                // 1108 ADC 1,S
        0xA8,                      // 1110 TAY
        0xE2, 0x20,                // 1111 SEP #$20
        0xBF, 0x00, 0x1A, 0x70,    // 1113 LDA order,X
        0xEB,                      // 1117 XBA
        0xDA,                      // 1118 PHX
        0xBB,                      // 1119 TYX
        0xBF, 0x00, 0x1A, 0x70,    // 1120 LDA order,X
        0xFA,                      // 1124 PLX
        0x9F, 0x00, 0x1A, 0x70,    // 1125 STA order,X
        0xEB,                      // 1129 XBA
        0xBB,                      // 1130 TYX
        0x9F, 0x00, 0x1A, 0x70,    // 1131 STA order,X
        0xC2, 0x20,                // 1135 REP #$20
        0x68,                      // 1137 PLA
        0xAB,                      // 1138 PLB
        0x28,                      // 1139 PLP
        0x6B,                      // 1140 RTL
        // saveOrder:
        0x8B,                      // 1141 PHB
        0x08,                      // 1142 PHP
        0xC2, 0x30,                // 1143 REP #$30
        0xA5, 0x1F,                // 1145 LDA $1F
        0x18,                      // 1147 CLC
        0x69, 0x00, 0x05,          // 1148 ADC #$0500
        0xA8,                      // 1151 TAY
        0xA2, 0x00, 0x1A,          // 1152 LDX #$1A00
        0xA9, 0xFF, 0x00,          // 1155 LDA #$00FF
        0x54, 0x70, 0x70,          // 1158 MVN $70,$70
        0x28,                      // 1161 PLP
        0xAB,                      // 1162 PLB
        0xAD, 0x53, 0x19,          // 1163 LDA $1953
        0xA0, 0xE3, 0x03,          // 1166 LDY #$03E3
        0x6B,                      // 1169 RTL
        // loadOrder:
        0x8B,                      // 1170 PHB
        0xC2, 0x30,                // 1171 REP #$30
        0xA5, 0x1F,                // 1173 LDA $1F
        0x18,                      // 1175 CLC
        0x69, 0x00, 0x05,          // 1176 ADC #$0500
        0xAA,                      // 1179 TAX
        0xA0, 0x00, 0x1A,          // 1180 LDY #$1A00
        0xA9, 0xFF, 0x00,          // 1183 LDA #$00FF
        0x54, 0x70, 0x70,          // 1186 MVN $70,$70
        0xAB,                      // 1189 PLB
        0xE2, 0x20,                // 1190 SEP #$20
        0xA9, 0x00,                // 1192 LDA #$00
        0x48,                      // 1194 PHA
        0xAB,                      // 1195 PLB
        0x6B,                      // 1196 RTL
        // mask:
        0x01, 0x02, 0x04, 0x08, 0x10, 0x20, 0x40, 0x80, // 1197 bit masks
        // magic:
        0x4C, 0x46, 0x4D, 0x33,    // 1205 "LFM3"
        // offs:
    };

    private static int FileOffset(int snes) => (snes >> 16 & 0x7F) * 0x8000 + (snes & 0x7FFF);
    private static int SnesAddress(int file) => (file / 0x8000 | 0x80) << 16 | (file % 0x8000 | 0x8000);
    private static bool Same(RomBuffer rom, int at, byte[] b) => at + b.Length <= rom.Length && rom.ReadBytes(at, b.Length).SequenceEqual(b);

    /// <summary>File offset of the installed block, or 0.</summary>
    public static int BlockOffset(RomBuffer rom)
    {
        if (rom.ReadByte(LearnSite) != 0x5C) return 0;
        int r = FileOffset(rom.ReadByte(LearnSite + 1) | rom.ReadByte(LearnSite + 2) << 8 | rom.ReadByte(LearnSite + 3) << 16) - Learn;
        if (r <= 0) return 0;
        if (r + BlockSize <= rom.Length && rom.ReadByte(r + Magic) == 'L' && rom.ReadByte(r + Magic + 3) == '3') return r;
        if (r + BlobV2 + BlobSize <= rom.Length && rom.ReadByte(r + MagicV2) == 'L' && rom.ReadByte(r + MagicV2 + 3) == '2') return r;
        if (r + BlobV3a + BlobSize <= rom.Length && rom.ReadByte(r + MagicV3a) == 'L' && rom.ReadByte(r + MagicV3a + 3) == '3') return r;
        return 0;
    }

    /// <summary>The block is this version's (with the spell order), not beta.7/8's.</summary>
    public static bool Current(RomBuffer rom) { int b = BlockOffset(rom); return b > 0 && b + BlockSize <= rom.Length && rom.ReadByte(b + Magic) == 'L' && rom.ReadByte(b + Magic + 1) == 'F' && rom.ReadByte(b + Magic + 3) == '3'; }
    private static (int Offs, int Blob) Layout(RomBuffer rom)
    {
        if (Current(rom)) return (Offs, Blob);
        int b = BlockOffset(rom);
        return b > 0 && rom.ReadByte(b + MagicV3a) == 'L' && rom.ReadByte(b + MagicV3a + 3) == '3' ? (OffsV3a, BlobV3a) : (OffsV2, BlobV2);
    }

    /// <summary>A JSL from <paramref name="site"/> into the installed block (any version's routine).</summary>
    private static bool HookedIntoBlock(RomBuffer rom, int site)
    {
        int b = BlockOffset(rom);
        if (b <= 0 || rom.ReadByte(site) != 0x22) return false;
        int t = FileOffset(rom.ReadByte(site + 1) | rom.ReadByte(site + 2) << 8 | rom.ReadByte(site + 3) << 16);
        return t >= b && t < b + BlockSize;
    }

    public static bool Installed(RomBuffer rom) => BlockOffset(rom) > 0;

    /// <summary>SNES address of the routine that converts old spell memory (A8 X16; DB with work RAM; keeps A X Y).</summary>
    public static int EnsureAddress(RomBuffer rom) => SnesAddress(BlockOffset(rom) + Ensure);

    /// <summary>The game's own code at every site (nothing else changed it).</summary>
    public static bool SitesOriginal(RomBuffer rom) =>
        Same(rom, LearnSite, LearnOriginal) && Same(rom, LevelUpSite, LevelUpOriginal) && Same(rom, JoinSite, JoinOriginal) &&
        Same(rom, ClearSite, ClearOriginal) && Same(rom, KnowsSite, KnowsOriginal) && Same(rom, KnowsLoopSite, KnowsLoopOriginal) &&
        Same(rom, CountSite, CountOriginal) && Same(rom, NthSite, NthOriginal) && Same(rom, MenuSite, MenuOriginal);

    // ------------------------------------------------------------------ earlier Guy / Aguro patch (lists per slot)

    /// <summary>File offset of an earlier Guy / Aguro magic block, or 0 (its level-up hook leads to PHX / LDA $156F,X).</summary>
    public static int OldBlock(RomBuffer rom)
    {
        if (rom.ReadByte(LevelUpSite) != 0x22) return 0;
        int r = FileOffset(rom.ReadByte(LevelUpSite + 1) | rom.ReadByte(LevelUpSite + 2) << 8 | rom.ReadByte(LevelUpSite + 3) << 16);
        return r > 0 && r + 400 <= rom.Length && rom.ReadByte(r) == 0xDA && rom.ReadByte(r + 1) == 0xBD ? r : 0;
    }
    /// <summary>The earlier block's layout: with its battle menu routine (offs 191, lists 203) or without (132, 144).</summary>
    private static (int Offs, int Blob, bool Menu) OldLayout(RomBuffer rom, int r) => rom.ReadByte(r + 132) == 0xBF ? (191, 203, true) : (132, 144, false);

    /// <summary>Puts the game's code back where the earlier version changed it (it gave Aguro magic: true).</summary>
    private static bool RemoveOld(RomBuffer rom)
    {
        int r = OldBlock(rom);
        if (r == 0) return false;
        bool aguro = rom.ReadByte(MenuSkip) == 0xFF;
        rom.WriteBytes(LevelUpSite, LevelUpOriginal);
        rom.WriteBytes(JoinSite, JoinOriginal);
        rom.WriteBytes(ClearSite, ClearOriginal);
        rom.WriteBytes(KnowsSite, KnowsOriginal);
        if (OldLayout(rom, r).Menu) rom.WriteBytes(MenuSite, MenuOriginal);
        rom.WriteByte(0x4684C + 2, 0x00);           // learn offsets ($08:E84C)
        rom.WriteByte(0x43D3F + 2, 0x00);           // menu offsets ($08:BD3F)
        rom.WriteUInt16Le(0x1CAD + 4, 0x14B5);      // the field menu's slot-2 pointer
        rom.WriteByte(MenuSkip, 0x02);
        var space = ExpansionSpace.Load(rom);
        if (space.At(r) is { } e && e.Start == r) { space.Free(r); space.Save(); }
        return aguro;
    }

    // ------------------------------------------------------------------ learn lists

    /// <summary>The four learn lists (Hero, Lufia, Aguro, Jerin): this patch's, an earlier version's, or the game's
    /// own (Aguro's empty).</summary>
    public static List<(int Level, int Spell)>[] ReadLists(RomBuffer rom)
    {
        var lists = new List<(int, int)>[4];
        int block = BlockOffset(rom), old = block == 0 ? OldBlock(rom) : 0;
        var layout = old > 0 ? OldLayout(rom, old) : default;
        for (int s = 0; s < 4; s++)
        {
            lists[s] = new List<(int, int)>();
            int p;
            if (block > 0) { var (o, bl) = Layout(rom); p = block + bl + rom.ReadUInt16Le(block + o + 2 * s); }
            else if (old > 0) p = old + layout.Blob + rom.ReadByte(old + layout.Offs + s);
            else if (s == 2) continue;
            else p = 0x46E0D + rom.ReadByte(0x467EB + s * 2);
            for (int n = 0; n < MaxPerCharacter && p + 1 < rom.Length && rom.ReadByte(p) != 0xFF; n++, p += 2) lists[s].Add((rom.ReadByte(p), rom.ReadByte(p + 1)));
        }
        return lists;
    }

    /// <summary>Why the lists can't be stored, or null.</summary>
    public static string? Problem(IReadOnlyList<IReadOnlyList<(int Level, int Spell)>> lists)
    {
        string[] names = { "The Hero", "Lufia", "Aguro", "Jerin" };
        for (int s = 0; s < 4; s++)
            if (lists[s].Count > MaxPerCharacter) return $"{names[s]} can learn at most {MaxPerCharacter} spells.";
        return null;
    }

    public static void WriteLists(RomBuffer rom, IReadOnlyList<IReadOnlyList<(int Level, int Spell)>> lists)
    {
        int block = BlockOffset(rom);
        if (block == 0) throw new InvalidOperationException("The \"Up to 128 spells\" patch isn't in this ROM.");
        if (!Current(rom)) { Upgrade(rom); block = BlockOffset(rom); }
        if (Problem(lists) is string bad) throw new InvalidOperationException(bad);
        var blob = new List<byte>();
        for (int s = 0; s < 4; s++)
        {
            rom.WriteUInt16Le(block + Offs + 2 * s, (ushort)blob.Count);
            foreach (var (lv, sp) in lists[s]) { blob.Add((byte)Math.Clamp(lv, 0, 0xFE)); blob.Add((byte)sp); }
            blob.Add(0xFF);
        }
        while (blob.Count < BlobSize) blob.Add(0xFF);
        rom.WriteBytes(block + Blob, blob.ToArray());
    }

    // ------------------------------------------------------------------ install / remove

    /// <summary>Installs the spell memory (taking over an earlier Guy / Aguro version's lists). Returns whether that
    /// earlier version had given Aguro magic.</summary>
    public static bool Install(RomBuffer rom)
    {
        if (Installed(rom)) { if (!SwapFixed(rom)) Upgrade(rom); return rom.ReadByte(MenuSkip) == 0xFF; }
        var lists = ReadLists(rom);
        bool aguro = RemoveOld(rom);
        if (!SitesOriginal(rom)) throw new InvalidOperationException("The game's spell memory code isn't the original (another patch changed it), so spells can't be expanded safely.");
        if (rom.Length < MapWriter.ExpandedSize) rom.Expand(MapWriter.ExpandedSize);
        var space = ExpansionSpace.Load(rom);
        int at = space.AllocateInOneBank(BlockSize, ExpansionSpace.Kind.Patch, 10);
        space.Save();
        WriteAt(rom, at, lists);
        if (aguro) rom.WriteByte(MenuSkip, 0xFF);
        return aguro;
    }

    /// <summary>
    /// Moves an installed spell memory to this version (beta.7/8 had no spell order): its learn lists are kept, the
    /// old block freed, a new one written and every hook pointed at it.
    /// </summary>
    private static void Upgrade(RomBuffer rom)
    {
        var lists = ReadLists(rom);
        int old = BlockOffset(rom);
        if (!Current(rom) || !SwapFixed(rom))
        {
            if (!(Same(rom, SwapSite, SwapOriginal) || Same(rom, SwapSite, SwapSkip) || HookedIntoBlock(rom, SwapSite)) ||
                !(Same(rom, SaveSite, SaveOriginal) || HookedIntoBlock(rom, SaveSite)) || !(Same(rom, LoadSite, LoadOriginal) || HookedIntoBlock(rom, LoadSite)))
                throw new InvalidOperationException("The game's save or field Magic menu code isn't the original, so the spell order can't be added safely.");
            var space = ExpansionSpace.Load(rom);
            int at = space.AllocateInOneBank(BlockSize, ExpansionSpace.Kind.Patch, 10);
            if (!Current(rom) && space.At(old) is { } e && e.Start == old) space.Free(old);
            space.Save();
            WriteAt(rom, at, lists);
            rom.FixChecksum();
        }
    }

    /// <summary>Writes the routines and lists at <paramref name="at"/> and hooks the game's code (also for test copies).</summary>
    public static void WriteAt(RomBuffer rom, int at, IReadOnlyList<IReadOnlyList<(int Level, int Spell)>> lists)
    {
        var code = new byte[BlockSize];
        Routine.CopyTo(code, 0);
        int baseSnes = SnesAddress(at);
        foreach (var (o, t) in Fixups)
        {
            int a = baseSnes + t;
            code[o] = (byte)a; code[o + 1] = (byte)(a >> 8); code[o + 2] = (byte)(a >> 16);
        }
        foreach (var (o, t) in JsrFixups)
        {
            int a = baseSnes + t;
            code[o] = (byte)a; code[o + 1] = (byte)(a >> 8);
        }
        rom.WriteBytes(at, code);
        void Hook(int site, int length, byte op, int entry, params byte[] after)
        {
            var b = Enumerable.Repeat((byte)0xEA, length).ToArray();
            int r = baseSnes + entry;
            b[0] = op; b[1] = (byte)r; b[2] = (byte)(r >> 8); b[3] = (byte)(r >> 16);
            after.CopyTo(b, 4);
            rom.WriteBytes(site, b);
        }
        Hook(LearnSite, LearnOriginal.Length, 0x5C, Learn);                                   // JML: the routine returns with RTL
        Hook(LevelUpSite, LevelUpOriginal.Length, 0x22, LearnLevel, 0x80, 0x20);              // then BRA $E684
        Hook(JoinSite, JoinOriginal.Length, 0x22, LearnInitial, 0x80, 0x1B);                  // then BRA $EC28
        Hook(ClearSite, ClearOriginal.Length, 0x22, ClearSpells);
        Hook(KnowsSite, KnowsOriginal.Length, 0x22, KnowsSetup);
        Hook(KnowsLoopSite, KnowsLoopOriginal.Length, 0x22, KnowsCheck, 0xF0, 0x09, 0x80, 0x02);   // known: $D2CB, not: $D2C6
        Hook(CountSite, CountOriginal.Length, 0x22, CountAll, 0x60);                         // then RTS
        Hook(NthSite, NthOriginal.Length, 0x22, Nth, 0x60);                                  // then RTS
        Hook(MenuSite, MenuOriginal.Length, 0x22, BuildMenu, 0x80, 0x2B);                    // then BRA $BD38
        Hook(SwapSite, SwapOriginal.Length, 0x22, Swap, 0x80, 0x28);                         // then BRA $C24D
        Hook(SaveSite, SaveOriginal.Length, 0x22, SaveOrder);
        Hook(LoadSite, LoadOriginal.Length, 0x22, LoadOrder);
        WriteLists(rom, lists);
    }

    public static void Remove(RomBuffer rom)
    {
        int b = BlockOffset(rom);
        if (b == 0) return;
        rom.WriteBytes(LearnSite, LearnOriginal);
        rom.WriteBytes(LevelUpSite, LevelUpOriginal);
        rom.WriteBytes(JoinSite, JoinOriginal);
        rom.WriteBytes(ClearSite, ClearOriginal);
        rom.WriteBytes(KnowsSite, KnowsOriginal);
        rom.WriteBytes(KnowsLoopSite, KnowsLoopOriginal);
        rom.WriteBytes(CountSite, CountOriginal);
        rom.WriteBytes(NthSite, NthOriginal);
        rom.WriteBytes(MenuSite, MenuOriginal);
        if (!Same(rom, SwapSite, SwapOriginal)) rom.WriteBytes(SwapSite, SwapOriginal);
        if (!Same(rom, SaveSite, SaveOriginal)) rom.WriteBytes(SaveSite, SaveOriginal);
        if (!Same(rom, LoadSite, LoadOriginal)) rom.WriteBytes(LoadSite, LoadOriginal);
        rom.WriteByte(MenuSkip, 0x02);
        var space = ExpansionSpace.Load(rom);
        space.Free(b);
        space.Save();
    }
}
