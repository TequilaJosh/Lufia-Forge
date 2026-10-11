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
/// <item>$08:BD07 the battle spell menu and $00:D100 / $00:9C27 the field Magic menu's count and "nth spell": spells in
/// spell number order, at most 64 (the battle menu's buffers hold 64).</item>
/// </list>
/// Earlier versions of the Guy / Aguro magic patch (lists per slot, before 0.4.0-beta.7) are recognised: their lists
/// are read, their code is put back, then this is installed.
/// </summary>
public static class SpellMemory
{
    public const int MaxPerCharacter = 64;
    public const int LearnSite = 0x46829, LevelUpSite = 0x4665E, JoinSite = 0x46C07, ClearSite = 0x46AEC, KnowsSite = 0xD2A7,
                     KnowsLoopSite = 0xD2BC, CountSite = 0x5100, NthSite = 0x1C27, MenuSite = 0x43D07;
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
                      KnowsSetup = 399, KnowsCheck = 403, CountAll = 479, Nth = 564, BuildMenu = 665,
                      Magic = 811, Offs = 815, Blob = 823;
    /// <summary>Room for the four learn lists: (level, spell) pairs and an FF each.</summary>
    public const int BlobSize = 4 * (MaxPerCharacter * 2 + 1);
    public const int BlockSize = Blob + BlobSize;
    /// <summary>Long operands to fix up when placed: (operand offset, block offset it points at).</summary>
    private static readonly (int At, int Target)[] Fixups =
    {
        (93, 803), (169, 0), (185, 803), (248, 815), (255, 823), (271, 824), (287, 151), (309, 815), (316, 823), (328, 824), (342, 151), (366, 0), (411, 0), (427, 803), (490, 0), (577, 0), (680, 0),
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
        // countAll:
        0x08,                      // 479 PHP
        0x8B,                      // 480 PHB
        0xE2, 0x20,                // 481 SEP #$20
        0xC2, 0x10,                // 483 REP #$10
        0xA9, 0x7E,                // 485 LDA #$7E
        0x48,                      // 487 PHA
        0xAB,                      // 488 PLB
        0x22, 0x00, 0x00, 0x00,    // 489 JSL ensure
        0xA2, 0x00, 0x00,          // 493 LDX #$0000
        0xA9, 0x00,                // 496 LDA #$00
        0x48,                      // 498 PHA  (slot)
        // S1:
        0xA0, 0x00, 0x00,          // 499 LDY #$0000  (count)
        0xA9, 0x10,                // 502 LDA #$10
        0x48,                      // 504 PHA  (bytes left)
        // B1:
        0xBD, 0x8F, 0x15,          // 505 LDA $158F,X
        // b2:
        0xC9, 0x00,                // 508 CMP #$00
        0xF0, 0x06,                // 510 BEQ bn
        0x4A,                      // 512 LSR
        0x90, 0xF9,                // 513 BCC b2
        0xC8,                      // 515 INY
        0x80, 0xF6,                // 516 BRA b2
        // bn:
        0xE8,                      // 518 INX
        0xA3, 0x01,                // 519 LDA 1,S
        0x3A,                      // 521 DEC
        0x83, 0x01,                // 522 STA 1,S
        0xD0, 0xEB,                // 524 BNE B1
        0x68,                      // 526 PLA
        0xC0, 0x40, 0x00,          // 527 CPY #$0040
        0x90, 0x03,                // 530 BCC c64
        0xA0, 0x40, 0x00,          // 532 LDY #$0040
        // c64:
        0x98,                      // 535 TYA
        0x48,                      // 536 PHA
        0xC2, 0x20,                // 537 REP #$20
        0xA3, 0x02,                // 539 LDA 2,S  (slot)
        0x29, 0x03, 0x00,          // 541 AND #$0003
        0xA8,                      // 544 TAY
        0xE2, 0x20,                // 545 SEP #$20
        0x68,                      // 547 PLA
        0x99, 0x4E, 0x19,          // 548 STA $194E,Y
        0xA3, 0x01,                // 551 LDA 1,S
        0x1A,                      // 553 INC
        0x83, 0x01,                // 554 STA 1,S
        0xC9, 0x04,                // 556 CMP #$04
        0xD0, 0xC3,                // 558 BNE S1
        0x68,                      // 560 PLA
        0xAB,                      // 561 PLB
        0x28,                      // 562 PLP
        0x6B,                      // 563 RTL
        // nth:
        0x08,                      // 564 PHP
        0x8B,                      // 565 PHB
        0xE2, 0x20,                // 566 SEP #$20
        0xC2, 0x10,                // 568 REP #$10
        0xDA,                      // 570 PHX
        0x5A,                      // 571 PHY
        0xA9, 0x7E,                // 572 LDA #$7E
        0x48,                      // 574 PHA
        0xAB,                      // 575 PLB
        0x22, 0x00, 0x00, 0x00,    // 576 JSL ensure
        0xA5, 0x11,                // 580 LDA $11
        0x18,                      // 582 CLC
        0x6D, 0x55, 0x19,          // 583 ADC $1955
        0xCD, 0x57, 0x19,          // 586 CMP $1957
        0xB0, 0x41,                // 589 BCS none
        0x48,                      // 591 PHA  (n)
        0xC2, 0x20,                // 592 REP #$20
        0xA3, 0x04,                // 594 LDA 4,S  (slot)
        0x29, 0x03, 0x00,          // 596 AND #$0003
        0x0A,                      // 599 ASL
        0x0A,                      // 600 ASL
        0x0A,                      // 601 ASL
        0x0A,                      // 602 ASL
        0xAA,                      // 603 TAX
        0xE2, 0x20,                // 604 SEP #$20
        0xA0, 0x00, 0x00,          // 606 LDY #$0000  (spell)
        // NB:
        0xBD, 0x8F, 0x15,          // 609 LDA $158F,X
        0x48,                      // 612 PHA  (bits)
        0xA9, 0x08,                // 613 LDA #$08
        0x48,                      // 615 PHA  (left)
        // NI:
        0xA3, 0x02,                // 616 LDA 2,S
        0x4A,                      // 618 LSR
        0x83, 0x02,                // 619 STA 2,S
        0x90, 0x07,                // 621 BCC nsk
        0xA3, 0x03,                // 623 LDA 3,S  (n)
        0xF0, 0x16,                // 625 BEQ nf
        0x3A,                      // 627 DEC
        0x83, 0x03,                // 628 STA 3,S
        // nsk:
        0xC8,                      // 630 INY
        0xA3, 0x01,                // 631 LDA 1,S
        0x3A,                      // 633 DEC
        0x83, 0x01,                // 634 STA 1,S
        0xD0, 0xEA,                // 636 BNE NI
        0x68,                      // 638 PLA
        0x68,                      // 639 PLA
        0xE8,                      // 640 INX
        0xC0, 0x80, 0x00,          // 641 CPY #$0080
        0xD0, 0xDB,                // 644 BNE NB
        0x68,                      // 646 PLA  (n)
        0x80, 0x07,                // 647 BRA none
        // nf:
        0x98,                      // 649 TYA
        0xEB,                      // 650 XBA
        0x68,                      // 651 PLA
        0x68,                      // 652 PLA
        0x68,                      // 653 PLA
        0x80, 0x03,                // 654 BRA nout
        // none:
        0xA9, 0x00,                // 656 LDA #$00
        0xEB,                      // 658 XBA
        // nout:
        0x7A,                      // 659 PLY
        0xFA,                      // 660 PLX
        0xAB,                      // 661 PLB
        0x28,                      // 662 PLP
        0xEB,                      // 663 XBA  (Z from the spell)
        0x6B,                      // 664 RTL
        // buildMenu:
        0x08,                      // 665 PHP
        0x8B,                      // 666 PHB
        0xE2, 0x20,                // 667 SEP #$20
        0xC2, 0x10,                // 669 REP #$10
        0x8A,                      // 671 TXA
        0x29, 0x03,                // 672 AND #$03
        0x48,                      // 674 PHA  (slot)
        0xA9, 0x7E,                // 675 LDA #$7E
        0x48,                      // 677 PHA
        0xAB,                      // 678 PLB
        0x22, 0x00, 0x00, 0x00,    // 679 JSL ensure
        0xC2, 0x20,                // 683 REP #$20
        0xA3, 0x01,                // 685 LDA 1,S
        0x29, 0x03, 0x00,          // 687 AND #$0003
        0x0A,                      // 690 ASL
        0x0A,                      // 691 ASL
        0x0A,                      // 692 ASL
        0x0A,                      // 693 ASL
        0xAA,                      // 694 TAX
        0xE2, 0x20,                // 695 SEP #$20
        0xA9, 0x00,                // 697 LDA #$00
        0x48,                      // 699 PHA  (out)
        0xA0, 0x00, 0x00,          // 700 LDY #$0000  (spell)
        // BB:
        0xBD, 0x8F, 0x15,          // 703 LDA $158F,X
        0x48,                      // 706 PHA  (bits)
        0xA9, 0x08,                // 707 LDA #$08
        0x48,                      // 709 PHA  (left)
        // BI:
        0xA3, 0x02,                // 710 LDA 2,S
        0x4A,                      // 712 LSR
        0x83, 0x02,                // 713 STA 2,S
        0x90, 0x3D,                // 715 BCC bsk
        0x98,                      // 717 TYA
        0xF0, 0x3A,                // 718 BEQ bsk
        0xDA,                      // 720 PHX
        0x5A,                      // 721 PHY
        0x48,                      // 722 PHA  (spell)
        0xA3, 0x08,                // 723 LDA 8,S  (out)
        0xC2, 0x20,                // 725 REP #$20
        0x29, 0xFF, 0x00,          // 727 AND #$00FF
        0xAA,                      // 730 TAX
        0xE2, 0x20,                // 731 SEP #$20
        0x68,                      // 733 PLA
        0x9D, 0x00, 0xD8,          // 734 STA $D800,X
        0x22, 0x2D, 0xFA, 0x08,    // 737 JSL $08FA2D  (the spell's data)
        0xAD, 0x9D, 0x17,          // 741 LDA $179D
        0x9D, 0x40, 0xD8,          // 744 STA $D840,X
        0xAD, 0x9A, 0x17,          // 747 LDA $179A
        0x9D, 0x80, 0xD8,          // 750 STA $D880,X
        0xAD, 0x9C, 0x17,          // 753 LDA $179C
        0x9D, 0xC0, 0xD8,          // 756 STA $D8C0,X
        0xAD, 0xA4, 0x17,          // 759 LDA $17A4
        0x9D, 0x40, 0xD9,          // 762 STA $D940,X
        0xA3, 0x07,                // 765 LDA 7,S  (out)
        0x1A,                      // 767 INC
        0x83, 0x07,                // 768 STA 7,S
        0x7A,                      // 770 PLY
        0xFA,                      // 771 PLX
        0xA3, 0x03,                // 772 LDA 3,S  (out)
        0xC9, 0x40,                // 774 CMP #$40  (full?)
        0xB0, 0x12,                // 776 BCS bdone
        // bsk:
        0xC8,                      // 778 INY
        0xA3, 0x01,                // 779 LDA 1,S
        0x3A,                      // 781 DEC
        0x83, 0x01,                // 782 STA 1,S
        0xD0, 0xB4,                // 784 BNE BI
        0x68,                      // 786 PLA
        0x68,                      // 787 PLA
        0xE8,                      // 788 INX
        0xC0, 0x80, 0x00,          // 789 CPY #$0080
        0xD0, 0xA5,                // 792 BNE BB
        0x80, 0x02,                // 794 BRA bend
        // bdone:
        0x68,                      // 796 PLA
        0x68,                      // 797 PLA
        // bend:
        0x68,                      // 798 PLA  (out)
        0x68,                      // 799 PLA  (slot)
        0xAB,                      // 800 PLB
        0x28,                      // 801 PLP
        0x6B,                      // 802 RTL
        // mask:
        0x01, 0x02, 0x04, 0x08, 0x10, 0x20, 0x40, 0x80, // 803 bit masks
        // magic:
        0x4C, 0x46, 0x4D, 0x32,    // 811 "LFM2"
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
        return r > 0 && r + BlockSize <= rom.Length && rom.ReadByte(r + Magic) == 'L' && rom.ReadByte(r + Magic + 3) == '2' ? r : 0;
    }

    public static bool Installed(RomBuffer rom) => BlockOffset(rom) > 0;

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
            if (block > 0) p = block + Blob + rom.ReadUInt16Le(block + Offs + 2 * s);
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
        if (Installed(rom)) return rom.ReadByte(MenuSkip) == 0xFF;
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
        rom.WriteByte(MenuSkip, 0x02);
        var space = ExpansionSpace.Load(rom);
        space.Free(b);
        space.Save();
    }
}
