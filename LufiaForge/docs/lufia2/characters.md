# Lufia II (US): character data

Static analysis only (no emulator). File offsets are into lufia2.sfc (LoROM, no header).
Helper scripts: `l2research/growth.py` (stat and EXP formulas, as the code does them) and
`l2research/chars.py` (parses the new-game hero records and the Ancient Cave records). Full output: `l2research/growth_out.txt`.

Confidence: **V** = the code and the data agree (the formulas give the join levels' EXP exactly), **C** = read from code, not seen in game, **G** = guess.

## 0. Hero index (used by every table below)

| id | hero | RAM block ($7E) | growth table | start stats |
|---|---|---|---|---|
| 0 | Maxim | 0BAD | 0xBB62C | 0xBB93C |
| 1 | Selan | 0C6B | 0xBB69C | 0xBB94A |
| 2 | Guy | 0D29 | 0xBB70C | 0xBB958 |
| 3 | Artea ("Arty" in the init record) | 0DE7 | 0xBB77C | 0xBB966 |
| 4 | Tia | 0EA5 | 0xBB7EC | 0xBB974 |
| 5 | Dekar | 0F63 | 0xBB85C | 0xBB982 |
| 6 | Lexis | 1021 | 0xBB8CC | 0xBB990 |

Sources (V):
- New-game init `$81:EC56` writes the records at `$85:B2B0` in this order to blocks 0BAD, 0C6B, ... 1021.
- Block pointer table `$85:9EBA` (file 0x29EBA) holds u16 pointers by id (`$81:F7BD`: X = id*2, LDA $859EBA,X). Entries 0-6 are listed above. Entries 7-12 are 161B, 16D9, 1797, 1855, 1913, 19D1 (same 0xBE stride; probably capsule monsters, G). Entry 13 is 10DF, the slot right after Lexis; new game sets `$0A88` = 10DF (G: a guest or temp slot). Entries 14-15 are F60C and F62A.
- The guy-like heroes check out: Guy (2) and Dekar (5) have MP growth 0 and start with no spells.
- **There is no 8th hero.** The "8th starting-stats record" (0xBB99E, seven u16 = 20) is the **EXP base table**, one u16 per hero (section 2). It ends at 0xBB9AC, where the per-map encounter lists `$97:B9AC` begin.

## 1. Stats per level

### Data
- **Start stats** `$97:B93C` (file 0xBB93C): 7 records x 7 u16 in the order HP, MP, STR, AGL, INT, GUT, MGR. The index is hero*14.

| hero | HP | MP | STR | AGL | INT | GUT | MGR |
|---|---|---|---|---|---|---|---|
| Maxim | 20 | 0 | 12 | 9 | 8 | 100 | 5 |
| Selan | 20 | 0 | 20 | 9 | 7 | 80 | 5 |
| Guy | 20 | 0 | 8 | 9 | 7 | 90 | 5 |
| Artea | 20 | 0 | 8 | 9 | 7 | 60 | 5 |
| Tia | 10 | 0 | 8 | 9 | 7 | 60 | 5 |
| Dekar | 10 | 0 | 8 | 9 | 7 | 100 | 5 |
| Lexis | 10 | 0 | 8 | 9 | 7 | 70 | 5 |

- **Growth tables** `$97:B62C` (file 0xBB62C): 7 tables of 0x70 bytes (hero*0x70). Each table has 14 rows of 8 bytes: `HP MP STR AGL INT GUT MGR EXPg`.
  - Row r is used for the level-up from level k to k+1 where r = k/8 (row 0 covers k=1-7, row 1 covers k=8-15, ..., row 12 covers k=96-98).
  - Row 13 is all zeros (padding).
  - Byte 5 (GUT) is 0 in every row of every hero, so GUT never grows with level.
  - Byte 7 (EXPg) is the same for all heroes: 190, 64, 49, 31, 17, 13, 5, 2, 2, 1, 1, 1, 1.
  - All rows are listed in growth_out.txt. Maxim's rows 0-2 are shown below; the others follow the same layout.

```
Maxim  r0 75 40 36 20 14 00 18 BE   (117 64 54 32 20 0 24 | 190)
       r1 75 44 36 20 14 00 18 40
       r2 7A 48 3A 1E 1A 00 28 31 ...
```

### Code (C)
- `$81:F87F` computes stats for a level. Inputs: `$09FA` = hero id, `$09FE` = level, DB = $97. Output: 7 u16 in `$0A1C..$0A28`.
  ```
  acc[i] = sum over k = 1..L-1 of row[k/8][i]          (8-bit adds into u16)
  stat[i] = start[i] + (acc[i] >> 4)
  ```
  So each level adds row/16 of a point on average. The >>4 is applied to the total, not to each level.
- `$81:F7ED` does the level-up application. It calls F87F for the new level. Then, for each stat i:
  - r = random 0..4 (`$80:8299`: (rand8 * A) >> 8 with A = 5).
  - GUT (i = 5) gets r = 0 (`STZ $0A3D`).
  - If the computed value is 0, r = 0.
  - If calc + r would be below the current value, r becomes cur - calc, so stats never fall. The branch for cur - calc >= 5 lands on a BRK at $81:F87E, but it can't be reached.
  - Writes base[i] = calc[i] + r to block+0x51+2i.
  - The gains go to `$0A38..$0A3E` (u8), probably for the level-up display (G).
  So the stored stat = formula value + 0..4, and the random part is re-rolled every level (it does not build up).
- `$81:F7CA` is the per-hero level-up check (A = hero). It calls `$81:F979`, which compares EXP (block+0x5F, u24) with the needed total (block+0x62, u24). If EXP >= needed and level != 99, it raises the level, recomputes the needed total and returns 1; then F7CA runs F7ED and F4ED. It is called once per level from `$80:DB3B`.
- New-game init, the Ancient Cave reset and the save loader all call F87F and F9E9 to rebuild stats:
  - `$81:EE2F`, `$81:EF2E`.
  - `$85:CBDC` loads a save: base = saved value + formula. `$85:CC69` saves: it stores base - formula, so **saves keep only the random excess**.
- Example values (formula only; the game adds 0..4 at each level-up):
  - Maxim: L1 20/0/12/9/8/100/5, L10 85/36/42/27/19/100/18, L50 394/229/195/100/92/100/128, L99 694/464/364/153/147/100/191.
  - At join level: Selan L16 84/86/59/16/80/80/43, Guy L11 147/0/70/14/9/90/27, Artea L38 277/286/98/139/141/60/127, Tia L7 37/39/14/36/28/60/31, Dekar L20 283/0/171/19/13/100/31, Lexis L28 184/110/86/67/163/70/87.

## 2. EXP table (V)

There is no stored table. `$81:F9E9` builds the total EXP needed for the next level. Inputs: `$09FA` = hero, `$09FE` = level L. Output: u24 in `$0A2A`, which is stored to block+0x62.

```
w = u16 at $97:B99E + 2*hero (file 0xBB99E; all 0x0014). XBA, so v = w<<8 (8.8 fixed point = 20.0)
acc = v
for k = 2..L:   M = row[k/8].EXPg + 1                (row from the hero's growth table, byte 7)
                v = v + (v*M >> 8)                    (32-bit)
                acc += v
needed(L) = (acc >> 8) - 10;    if L >= 98: needed = 9,999,999 ($98967F)
```

Level cap: 99 (`CMP #$63` at $81:F9BF). EXP is cumulative u24.

Because EXPg and the base are the same for every hero, **all heroes share one curve**. The table gives the total EXP needed to reach level n:

```
2:10 3:44 4:105 5:212 6:398 7:722 8:1289 9:2000 10:2891 11:4008 12:5409 13:7166 14:9369 15:12132
16:15595 17:19735 18:24684 19:30599 20:37670 21:46121 22:56224 23:68299 24:82733 25:98971 26:117238
27:137790 28:160910 29:186920 30:216181 31:249100 32:286134 33:325772 34:368197 35:413605 36:462206
37:514224 38:569899 39:629490 40:693270 41:760538 42:831484 43:906311 44:985230 45:1068464
46:1156251 47:1248838 48:1346489 49:1446428 50:1548710 51:1653389 ... 60:2698829 70:4012471
80:5467589 90:7041055 97:8217651 98:8391030 99:9999999
```

(The full list for L = 1..99 is in growth_out.txt; needed(L) is the total for reaching L+1.)

Check: every join record's EXP falls in the right band. Selan L16 has 19000 (between 15595 and 19735). Guy L11 5400, Tia L7 750, Dekar L20 46000, Lexis L28 170000 and Artea L38 570000 all fit.

Editing: change the per-row EXPg bytes or the base word. The base word is byte-swapped (`XBA`): the stored low byte is the integer part and the high byte is the 1/256 fraction, so the stored u16 0x0014 means 20.0.

## 3. New game, heroes joining, Ancient Cave

### New-game data `$85:B2A1` (file 0x2B2A1), read by `$81:EC56` with DB = $85 (V)

| file | bytes | meaning |
|---|---|---|
| 0x2B2A1 | `01` | `$0A7A` party size |
| 0x2B2A2 | `00 FF FF FF` | `$0A7B..7E` party hero ids (Maxim only) |
| 0x2B2A6 | u16 `0000` | `$0A8A` gold (low 16 bits; `$0A8C` is the high byte elsewhere) |
| 0x2B2A8 | u16 `0000` | `$0B55` (unknown) |
| 0x2B2AA | u16 list up to 0000 | `$0A8D` inventory: `02 14` = Potion x10, `A9 03` = item 0x1A9 Arrow x1 |
| 0x2B2B0 | 7 hero records | the format follows |

Hero record (variable length; init routine `$81:ED8E`):

```
+0  char[6]  name                -> block+0x00..05
+6  u16      level               -> block+0x0E (u8 level; +0x0F status = 0)
+8  u16      ?  (all 0)          -> block+0x94
+10 u8[]     spell ids, FF-terminated -> block+0x96..; the rest up to +0xB9 is filled with FF
    u24      EXP                 -> block+0x5F
    u16[7]   equipment           -> block+0x66..0x73
    u8       IP gauge            -> block+0xBC
```

After loading, the init computes the stats for that level into block+0x51.., the next-level EXP, and the derived stats (F4D5). Then it sets HP = max HP and MP = max MP.

| hero | level | EXP | spells (ids) | weapon / armor / shield / helmet / ring / jewel / 7th |
|---|---|---|---|---|
| Maxim | 1 | 0 | none | Small knife 0x39, Cloth 0xA6, -, -, -, -, **Arrow 0x1A9** |
| Selan | 16 | 19000 | Spark 03, Flash 00, Strong 19, Drowsy 0E, Escape 25, Warp 24, Light 27 | Rod 7B, Light dress AE, Bracelet E9, Red beret 117 |
| Guy | 11 | 5400 | none | Short sword 3F, Tough hide AD, Wood shield E8 |
| Artea | 38 | 570000 | Thunder 02, Firebird 05, Dragon 08, Ice Valk 0B, Valor 1E, Champion 1B, Escape 25, Warp 24 | Pounder rod 83, Silver robe C8, Gold gloves FC, Gold band 131 |
| Tia | 7 | 750 | Strong 19, Spark 03, Drowsy 0E | Frypan 37, Frock A9, Small shield E4, Hairband 113 |
| Dekar | 20 | 46000 | none | Bronze sword 48, Ironmail B5, Brone shield ED, Stone helmet 11C |
| Lexis | 28 | 170000 | Stronger 1A, Fireball 04, Vortex 07, Blizzard 0A, Bolt 01, Trick 14, Bravery 16, Rally 1D | Vice pliers 9A, Lab-coat A7, Slash shield F8, Metal cloche 12A |

Notes on the table:
- Slot order (weapon, armor, shield, helmet, ...) is taken from the item kinds (C/G). Slots 5 and 6 (ring, jewel) are empty for everyone.
- The equipment menu at `$81:C146` copies only the first 6 slots.
- The 7th word (block+0x72) is read only for Maxim (`$7E:0C1F`) by field code `$83:C264`. G: Maxim's selected field tool, the one used with the button.
- Spell names come from `$95:FA5B`, 8-character names. Spells are not learned by level: the only spells a hero has are these, plus whatever is bought in shops.
- So **every hero's whole state is set at new game**. Joining does not read a table. It only adds the hero to the party:
  - **Event command `2B xx`** (`$80:A5C8`) calls `$81:F723`. That puts hero xx in the first FF slot of `$0A7B..7E`, increments `$0A7A` and rebuilds `$0A80` (u16 block pointer per party slot, through F7BD). It also adds xx to `$0926..092A` (5 bytes; G: the walking/follower list) and calls `$84:8204`.
  - **Event command `2C xx`** (`$80:A5FF`, `$81:F74F`) removes hero xx.
  - To change a hero's join level, gear or spells, edit the record at 0x2B2B0 (keep the EXP inside the level's band).

### Ancient Cave reset data `$85:B395` (file 0x2B395 to 0x2B452) (C; G that it is the Cave)
- Read by `$81:ED35` and the routine at `$81:EE94`, called from `$84:8901`.
- It has the same 7 records **without names**, in this layout: u16 level, u16 (+0x94), spells FF-terminated, u24 EXP, **9 u16 written to block+0x74..+0x85** (equipment slots +0x66..0x73 are zeroed instead), u8 IP.
- All seven heroes: level 1, EXP 0, no spells. The bonus words (HPb, MPb, ATP override, DFP override, STR, AGL, INT, GUT, MGR) are:

| hero | HPb | MPb | ATP override | DFP override | STR | AGL | INT | GUT | MGR |
|---|---|---|---|---|---|---|---|---|---|
| Maxim | 13 | 20 | 0 | 0 | 16 | 8 | 6 | 0 | 5 |
| Selan | 8 | 23 | 0 | 0 | 3 | 10 | 15 | 0 | 8 |
| Guy | 18 | 0 | 0 | 0 | 30 | 4 | 1 | 0 | 3 |
| Artea | 12 | 28 | 0 | 0 | 16 | 10 | 15 | 0 | 30 |
| Tia | 13 | 20 | 0 | 0 | 10 | 8 | 15 | 0 | 6 |
| Dekar | 28 | 0 | 0 | 0 | 38 | 4 | 0 | 0 | 0 |
| Lexis | 12 | 38 | 0 | 0 | 7 | 2 | 20 | 0 | 20 |

## 4. Names

- The only character-name table is the 6-byte ASCII name field inside each new-game record (section 3). The names start at file 0x2B2B0, 0x2B2CD, 0x2B2F1, 0x2B30E, 0x2B333, 0x2B353, 0x2B370; the stride varies.
- Record 3's name reads **"Arty"**. The dialogue dictionary has "Artea" (0x775E3, a dictionary word). Whether the US menus show "Arty" or rename him later is open.
- Order: 0 Maxim, 1 Selan, 2 Guy, 3 Artea, 4 Tia, 5 Dekar, 6 Lexis. There are 7 heroes; the 8th "stats" record is the EXP base (section 2).

## 5. RAM block of a party member (stride 0xBE, Maxim at $7E:0BAD)

The stride comes from the pointer table `$85:9EBA` and the init list (0BAD, 0C6B, 0D29, ... with a step of 0xBE). Offsets are relative to the block start. Code-verified fields are marked C; the rest are from l2ref.

| off | Maxim | type | meaning | source |
|---|---|---|---|---|
| 00 | 0BAD | char[6] | name (0-padded) | C (init) |
| 06-0D | | ? | unknown | |
| 0E | 0BBB | u8 | level | C |
| 0F | 0BBC | u8 | status (bit 2 = dead; checked at $81:F5C8) | C + ref |
| 11 | 0BBE | u16 | HP | C |
| 13 | 0BC0 | u16 | MP | C |
| 25 | 0BD2 | u16 | max HP = base HP + [74] | C |
| 27 | 0BD4 | u16 | max MP = base MP + [76] (ref's 0BD6 is wrong) | C |
| 29 | 0BD6 | u16 | ATP = STR' + [86]; if [78] != 0 then ATP = [78] | C |
| 2B | 0BD8 | u16 | DFP = (STR' + AGL') / 2 + [88]; if [7A] != 0 then DFP = [7A] | C |
| 2D 2F 31 33 35 | 0BDA-0BE2 | u16 | STR' AGL' INT' GUT' MGR' = base + [7C+2i] + [8A+2i]; GUT' capped at 199 | C ($81:F4ED) |
| 37-50 | | ? | not mapped (ref's INT/GUT/MGR at 0BF0-0BF4 fall here: unverified) | |
| 51-5D | 0BFE-0C0A | u16 x7 | base HP MP STR AGL INT GUT MGR (formula + level-up random) | C |
| 5F | 0C0C | u24 | EXP | C |
| 62 | 0C0F | u24 | total EXP needed for the next level | C |
| 66-70 | 0C13-0C1D | u16 x6 | equipment: weapon, armor, shield, helmet, ring, jewel (ref says 0C14: off by one) | C |
| 72 | 0C1F | u16 | 7th equip word (Maxim: Arrow; G: field tool) | C/G |
| 74-84 | | u16 x9 | bonus set A: HP, MP, ATP override, DFP override, STR, AGL, INT, GUT, MGR (G: from equipment) | C |
| 86 88 | | u16 | ATP bonus and DFP bonus (G: weapon AP / armor DP) | C |
| 8A-92 | | u16 x5 | bonus set B: STR AGL INT GUT MGR (G: from spells or items) | C |
| 94 | | u16 | from the record (0 for all); unknown | C |
| 96-B9 | 0C43 | u8[36] | spell ids, FF = none | C |
| BC | 0C69 | u8 | IP gauge (item IP costs are taken from it at $81:AB36) | C |
| BA BB BD | | | unknown | |

Party lists:
- `$0A7A` party count.
- `$0A7B..7E` hero ids (FF = empty).
- `$0A80..87` u16 block pointers for the party slots.

## Open questions

1. The stat order HP MP STR AGL INT GUT MGR comes from the brief, and only HP and STR were checked in game. GUT has zero growth and is capped at 199. Worth confirming in game that column 5 really is GUT (and column 3 AGL, column 4 INT).
2. Is the "Arty" name really shown in the US menus?
3. What are block+0x94 (u16), +0x06..0D, +0x37..0x50, and `$0B55`?
4. Is the 7th equipment word really the field tool? Is item 0x1A9 "Arrow" the right reading of Maxim's starting tool?
5. Are pointer entries 7-13 capsule monsters, and what is block 10DF?
6. Is `$81:ED35` / `$85:B395` really the Ancient Cave reset? The data fits: everyone at level 1, gear and spells cleared.
