# Lufia II (US) capsule monsters: static analysis

ROM: scratchpad\l2\lufia2.sfc (US, LoROM, no header). Everything below is from static reading of ROM data and
65816 code; the feeding part of section 6 was checked in the game (2026-10-27), the rest was not. "Guess" marks
inferences.
Helper: `l2research\tools\capsules.py table|attacks|scripts|levels` regenerates the tables below.

## 0. Summary

* **7 monsters x 5 forms = 35 records**, table `$97:DCB8` (file **0xBDCB8**), `u16 offset[35]` relative to the
  table start (first = 0x46 = 35*2, data right after the table at 0xBDCFE). Variable length (header 0x2B bytes
  + AI script + defense script). Record index = `monster*5 + form - 1` (code `$82:C38B`).
* **Default (player) names** are NOT in the records: 7 x char[5] at `$8E:D558` (file **0x75558**):
  `JELZE FLASH GUSTO ZEPPY DARBI SULLY BLAZE`, copied to `$7F:F180` at new game (`$82:C2E7`).
  Record names ("Foomy S", "Gold Fox"...) are the species/form names shown on the status page.
* **Attack names**: table `$A5:DF00` (file **0x12DF00**; 0x12DF44 is inside its offset array),
  `u16 offset[0x4E]` relative to the table, 0-terminated ASCII, 78 names (0 "Nothing" ... 0x4D "Inferno").
* **Attack effects**: table `$97:F63B` (file **0xBF63B**), `u16 offset[0x54]` relative to the table (first 0xA8),
  84 attacks: `u8 name id, u8 animation, L2BASM script`. Battle-script opcode **3E b** = "use capsule attack b"
  (b indexes this effect table, not the name table).
* Stats: the record holds level-1 base stats + one **growth-curve id** per stat; curves at `$A6:F420` (file
  **0x137420**, 0x1E curves x 8 bytes). Stats are recomputed from level and form (`$82:D283`).
* Forms 1-4 are reached by feeding (meter fills to 0x20, then evolution). Form 5 (Gold Fox, Unicorn, Sky Dragon,
  Fish Head, Red Dragon, Centaur, Firebird) is entered by feeding a specific fruit at a specific form, and left
  with the monster's own fruit (`$82:CC58`).

Monster order: 0 Jelze (Foomy, neutral), 1 Flash (Shaggy, light), 2 Gusto (Hard Hat, wind), 3 Zeppy (Red Fish,
water), 4 Darbi (Myconido, dark), 5 Sully (Raddisher, soil), 6 Blaze (Armor Dog, fire).

## 1. Record layout (`$97:DCB8` table)

| Off | Size | Field | Evidence |
|---|---|---|---|
| 0x00 | char[12] | form name, space padded (no terminator) | data |
| 0x0C | u8 | always 00 | data (all 35) |
| 0x0D | u8 | form number 1-5 | status page prints 0x0E and 0x0D (`$82:D0C0`, text `$8E:C85E`); evolution increments `$11A4` |
| 0x0E | u8 | type bit: 00 neutral, 01 light, 02 wind, 04 water, 08 dark, 10 soil, 20 fire | data (constant per monster); names = guess from the monsters' elements |
| 0x0F | u8[3] | attack slots 0-2: **base** attack name ids (0x12DF00 table; 0 = empty) | `$82:C4BC`: `LDY #$000F` |
| 0x12 | u8[3] | attack slots 0-2: **learned/upgraded** name ids, shown instead of 0x0F+s once slot s is learned | `$82:C4BC`: `LDY #$0012` if the learned bit is set; `$82:CD41` learns only if [0x12+s] != 0 |
| 0x15 | u8 | base max HP (level 1) | `$82:D2A8` -> block +0x51 (`$1130`) |
| 0x16 | u8 | ATP (flat, no growth) | `$82:D290` -> block +0x86 (`$1165`), the equipment-bonus ATP word |
| 0x17 | u8 | DFP (flat, no growth) | `$82:D299` -> block +0x88 (`$1167`), equipment-bonus DFP word |
| 0x18 | u8 | base STR | -> +0x55 (`$1134`) |
| 0x19 | u8 | base AGL | -> +0x57 (`$1136`) |
| 0x1A | u8 | base INT | -> +0x59 (`$1138`) |
| 0x1B | u8 | base GUT | -> +0x5B (`$113A`) |
| 0x1C | u8 | base MGR | -> +0x5D (`$113C`) |
| 0x1D | u8[6] | growth-curve ids for HP, STR, AGL, INT, GUT, MGR (same for all forms of a monster, except form 5 sometimes differs in one) | `$82:D2A2..D302`: `LDA $1D..$22,Y / JSR $D31C` |
| 0x23 | u16 | script offset, always 0 (unused slot) | copied to `$1127` by `$82:C261` |
| 0x25 | u16 | **AI (attack) script** offset relative to the record start (always 0x2B) | copied to `$1129` |
| 0x27 | u16 | **defense script** offset (right after the AI script) | copied to `$112B` |
| 0x29 | u16 | script offset, always 0 (unused slot) | copied to `$112D` |
| 0x2B | | AI script, then defense script (L2BASM) | |

The base-stat order (+0x51 HP, +0x53 MP (never written), +0x55 STR, AGL, INT, GUT, MGR) is the same as the heroes'
starting stats (`$97:B93C`). +0x86..+0x92 is the 7-word equipment-bonus block (ATP DFP STR AGL INT GUT MGR),
cleared by `$82:F6A4`; the record's ATP/DFP go into its first two words. Final stats: `JSL $81:F4D5` (not traced).

### Stat growth (`$82:D31C`)

```
stat(level) = base + ( sum over L = 1 .. level-1 of g(L) ) >> 4
g(L) = curve[id][L >> 4]          if (L >> 4) <  form
     = curve[id][L >> 4] >> 1     if (L >> 4) == form
     = curve[id][L >> 4] >> 2     if (L >> 4) >  form
curves: $A6:F420 (file 0x137420), 8 bytes per curve (one per 16-level band), ids 0x00-0x1D
```
A higher form keeps full growth for more level bands; evolving recomputes all stats with the new record.
Examples: Foomy S L1 HP 19, L10 77, L99 243; Gold Fox L99 HP 520, GUT 385.
Some curves: 01 `80 78 70 68 60 58 50 00`, 02 `68 60 58 50 48 40 38 00`, 0F `08 04 04 02 02 01 01 00`,
1D `16 14 12 0E 0C 0A 08 00`; 0x1E is the last real one (`02 02 01 01 00 00 00 00`, unused).

### EXP to next level (`$82:CE52` / `$82:CEAB`)

Not in the record. 8.8 fixed accumulator starts at 20.0 (`$09D5..$09D8` = 00 14 00 00); for each level
`next += int(acc)`, then `acc += acc * (E4CB[(lvl+1) >> 3] + 1) / 256`; cap 9,999,999; finally `next -= 10`.
Multipliers `$8E:E4CB` (file 0x764CB): `BE 40 31 1F 11 0D 05 02 02 01 01 01 01`. Totals: L2 at 10, then 44, 104,
210, 395, 719, 1285, 1995, 2886, 4003 ... Max level 99 (`$82:CD86 CMP #$63`).

## 2. All 35 records

Stats are the raw level-1 bytes (decimal); attack slots by name.

| # | file | name | form | type | slots 0x0F-0x11 (base) | slots 0x12-0x14 (learned) | HP | ATP | DFP | STR | AGL | INT | GUT | MGR | curves HP,STR,AGL,INT,GUT,MGR | 0x23 | AI 0x25 | def 0x27 | 0x29 |
|---|---|---|---|---|---|---|---|---|---|---|---|---|---|---|---|---|---|---|---|
| 0 | 0xBDCFE | Foomy S | 1 | 00 Neutral | Foomy punch, -, - | -, -, - | 19 | 10 | 12 | 3 | 10 | 8 | 21 | 8 | 02 08 0D 12 15 1C | 0 | 2B | 4F | 0 |
| 1 | 0xBDD51 | Foomy M | 2 | 00 Neutral | Foomy punch, -, - | -, Head Butt, - | 37 | 23 | 18 | 11 | 18 | 10 | 33 | 16 | 02 08 0D 12 15 1C | 0 | 2B | 78 | 0 |
| 2 | 0xBDDCD | Foomy L | 3 | 00 Neutral | Foomy punch, Head Butt, - | -, -, Tackle | 62 | 51 | 32 | 19 | 26 | 12 | 45 | 24 | 02 08 0D 12 15 1C | 0 | 2B | 85 | 0 |
| 3 | 0xBDE56 | Foomy H | 4 | 00 Neutral | Foomy punch, Head Butt, Tackle | Mega punch, -, - | 96 | 98 | 64 | 27 | 34 | 14 | 57 | 32 | 02 08 0D 12 15 1C | 0 | 2B | 89 | 0 |
| 4 | 0xBDEE3 | Gold Fox | 5 | 00 Neutral | Fang attack, Bubble blow, - | -, -, Tail smash | 52 | 100 | 80 | 30 | 41 | 16 | 49 | 37 | 02 08 0D 12 15 1C | 0 | 2B | 76 | 0 |
| 5 | 0xBDF5D | Shaggy | 1 | 01 Light | Spear revive, -, - | -, -, - | 5 | 10 | 12 | 2 | 11 | 16 | 23 | 11 | 04 0A 0C 10 15 1B | 0 | 2B | 4F | 0 |
| 6 | 0xBDFB6 | Sprite | 2 | 01 Light | Healing kiss, -, - | -, Wink, - | 14 | 18 | 16 | 3 | 21 | 28 | 36 | 21 | 04 0A 0C 10 15 1B | 0 | 2B | 78 | 0 |
| 7 | 0xBE038 | Cupid | 3 | 01 Light | Sacred song, Restoration, - | -, -, Thunderbolt | 31 | 26 | 24 | 4 | 31 | 40 | 48 | 31 | 04 0A 0C 10 15 1B | 0 | 2B | 85 | 0 |
| 8 | 0xBE0C7 | Twinkle | 4 | 01 Light | Sacred song, Restoration, Thunderbolt | Holy energy, Purification, Bolt attack | 57 | 24 | 32 | 6 | 41 | 86 | 61 | 41 | 04 0A 0C 10 15 1B | 0 | 2B | 9F | 0 |
| 9 | 0xBE170 | Unicorn | 5 | 01 Light | Dash, Antler stab, - | -, Holy thunder, Healing aura | 38 | 64 | 32 | 28 | 53 | 80 | 44 | 30 | 04 06 0C 10 15 1B | 0 | 2B | 81 | 0 |
| 10 | 0xBE1FB | Hard Hat | 1 | 02 Wind | Fang attack, -, - | -, -, - | 6 | 14 | 6 | 4 | 17 | 12 | 5 | 3 | 03 09 0B 11 18 1C | 0 | 2B | 49 | 0 |
| 11 | 0xBE24E | Bluebird | 2 | 02 Wind | Beak bash, Iron claw, - | -, -, Spiral wave | 19 | 27 | 8 | 10 | 29 | 22 | 11 | 11 | 03 09 0B 11 18 1C | 0 | 2B | 7F | 0 |
| 12 | 0xBE2D7 | Winged Horse | 3 | 02 Wind | Electro bolt, Finale, - | -, -, Air attack | 41 | 55 | 12 | 16 | 41 | 32 | 17 | 19 | 03 09 0B 11 18 1C | 0 | 2B | 7F | 0 |
| 13 | 0xBE360 | Green Titan | 4 | 02 Wind | Iron fist, Twister, - | -, -, Sonic blast | 70 | 102 | 20 | 22 | 53 | 42 | 23 | 27 | 03 09 0B 11 18 1C | 0 | 2B | 7F | 0 |
| 14 | 0xBE3E9 | Sky Dragon | 5 | 02 Wind | Tornado blow, Dragon fang, Electro bolt | Cyclone wave, -, - | 60 | 100 | 8 | 32 | 72 | 84 | 20 | 30 | 03 07 0B 11 18 1C | 0 | 2B | 74 | 0 |
| 15 | 0xBE467 | Red Fish | 1 | 04 Water | Fish kick, -, - | -, Bubble blast, - | 15 | 16 | 18 | 8 | 6 | 10 | 9 | 18 | 02 08 0E 11 17 1A | 0 | 2B | 72 | 0 |
| 16 | 0xBE4E3 | Toadie | 2 | 04 Water | Frog song, Frog kick, Iron trident | -, -, Mega trident | 33 | 29 | 28 | 16 | 12 | 20 | 17 | 30 | 02 08 0E 11 17 1A | 0 | 2B | 83 | 0 |
| 17 | 0xBE570 | Sea Giant | 3 | 04 Water | Energy wave, Hail attack, Iron trident | -, -, Mega trident | 58 | 57 | 48 | 24 | 18 | 30 | 25 | 42 | 02 08 0E 11 17 1A | 0 | 2B | 83 | 0 |
| 18 | 0xBE5FD | Blue Titan | 4 | 04 Water | Thunderblast, Thunder fist, - | -, Power fist, - | 92 | 104 | 88 | 32 | 24 | 40 | 33 | 54 | 02 08 0E 11 17 1A | 0 | 2B | 76 | 0 |
| 19 | 0xBE680 | Fish Head | 5 | 04 Water | Fish kick, Dive attack, - | Hyper kick, Diving claw, Magic freeze | 100 | 110 | 88 | 34 | 86 | 56 | 34 | 60 | 02 07 0E 11 17 1A | 0 | 2B | 86 | 0 |
| 20 | 0xBE710 | Myconido | 1 | 08 Dark | Trancemaker, -, - | -, Shriek, - | 5 | 18 | 12 | 9 | 5 | 20 | 3 | 1 | 05 07 0D 10 19 1E | 0 | 2B | 72 | 0 |
| 21 | 0xBE793 | Wolfman | 2 | 08 Dark | Wolf fang, Wolf punch, - | -, Sizzle smash, - | 16 | 35 | 30 | 19 | 13 | 32 | 7 | 5 | 05 07 0D 10 19 1E | 0 | 2B | 76 | 0 |
| 22 | 0xBE81A | Mini Imp | 3 | 08 Dark | Destruction, Mega trident, Dark flame | -, -, Terminate | 36 | 71 | 54 | 29 | 21 | 44 | 11 | 9 | 05 07 0D 10 19 1E | 0 | 2B | 83 | 0 |
| 23 | 0xBE8AE | Big Imp | 4 | 08 Dark | Sizzle smash, Terminate, Dark thunder | -, -, Evil aura | 63 | 134 | 68 | 39 | 29 | 56 | 15 | 13 | 05 07 0D 10 19 1E | 0 | 2B | 83 | 0 |
| 24 | 0xBE942 | Red Dragon | 5 | 08 Dark | Dash, Dragon blast, - | -, -, Eliminator | 100 | 160 | 68 | 40 | 39 | 44 | 14 | 16 | 05 07 0D 10 19 1E | 0 | 2B | 70 | 0 |
| 25 | 0xBE9C3 | Raddisher | 1 | 10 Soil | Radish kick, -, - | -, -, - | 16 | 30 | 25 | 7 | 2 | 6 | 11 | 0 | 01 06 0F 13 16 1E | 0 | 2B | 4F | 0 |
| 26 | 0xBEA1C | Red Cap | 2 | 10 Soil | Perish punch, -, - | -, Glow knuckle, - | 35 | 47 | 40 | 19 | 4 | 8 | 21 | 1 | 01 06 0F 13 16 1E | 0 | 2B | 78 | 0 |
| 27 | 0xBEA9E | Giant | 3 | 10 Soil | Fist, Heel drop, Shakedown | -, -, Battle anger | 63 | 83 | 70 | 31 | 8 | 10 | 31 | 2 | 01 06 0F 13 16 1E | 0 | 2B | 89 | 0 |
| 28 | 0xBEB31 | Stonehead | 4 | 10 Soil | Battle anger, Head Butt, - | -, -, Stone crush | 98 | 146 | 130 | 43 | 10 | 12 | 41 | 4 | 01 06 0F 13 16 1E | 0 | 2B | 85 | 0 |
| 29 | 0xBEBC0 | Centaur | 5 | 10 Soil | Battle bow, Hammer punch, - | -, -, Tackle | 56 | 200 | 100 | 45 | 24 | 14 | 40 | 8 | 02 06 0F 13 16 1E | 0 | 2B | 76 | 0 |
| 30 | 0xBEC40 | Armor Dog | 1 | 20 Fire | Tail, -, - | -, -, - | 11 | 8 | 14 | 8 | 9 | 14 | 6 | 9 | 03 07 0C 11 18 1B | 0 | 2B | 49 | 0 |
| 31 | 0xBEC93 | Winged Lion | 2 | 20 Fire | Tail, -, - | -, Burning fang, - | 26 | 23 | 20 | 18 | 19 | 24 | 12 | 19 | 03 07 0C 11 18 1B | 0 | 2B | 72 | 0 |
| 32 | 0xBED0F | Wing Lizard | 3 | 20 Fire | Fang attack, Iron claw, - | -, -, Flame breath | 50 | 55 | 40 | 28 | 29 | 34 | 20 | 29 | 03 07 0C 11 18 1B | 0 | 2B | 7F | 0 |
| 33 | 0xBED98 | Blaze Dragon | 4 | 20 Fire | Flame punch, Burning fang, Flame breath | -, -, Terminate | 81 | 110 | 80 | 38 | 39 | 44 | 32 | 39 | 03 07 0C 11 18 1B | 0 | 2B | 83 | 0 |
| 34 | 0xBEE25 | Firebird | 5 | 20 Fire | Burning rain, Tailspin, - | -, -, Inferno | 38 | 100 | 68 | 42 | 40 | 80 | 40 | 30 | 03 07 0C 10 18 1B | 0 | 2B | 70 | 0 |


## 3. Attack names (`$A5:DF00`, file 0x12DF00)

`u16 offset[0x4E]` relative to 0x12DF00 (first = 0x9C), strings 0-terminated ASCII. Read by `$82:C3B1`
(`LDA $A5DF00,X / ADC #$DF00`, X = id*2). Ids:
00 Nothing, 01 Foomy punch, 02 Mega punch, 03 Fang attack, 04 Tail smash, 05 Head Butt, 06 Tackle,
07 Bubble blow, 08 Antler stab, 09 Dash, 0A Spear revive, 0B Healing kiss, 0C Sacred song, 0D Holy energy,
0E Healing aura, 0F Wink, 10 Restoration, 11 Purification, 12 Holy thunder, 13 Thunderbolt, 14 Bolt attack,
15 Beak bash, 16 Finale, 17 Dragon fang, 18 Iron claw, 19 Iron fist, 1A Air attack, 1B Twister, 1C Spiral wave,
1D Tornado blow, 1E Electro bolt, 1F Cyclone wave, 20 Sonic blast, 21 Fish kick, 22 Hyper kick, 23 Frog kick,
24 Dive attack, 25 Diving claw, 26 Iron trident, 27 Thunder fist, 28 Power fist, 29 Mega trident, 2A Bubble blast,
2B Hail attack, 2C Thunderblast, 2D Energy wave, 2E Magic freeze, 2F Frog song, 30 Wolf fang, 31 Wolf punch,
32 Sizzle smash, 33 Shriek, 34 Trancemaker, 35 Destruction, 36 Eliminator, 37 Dark flame, 38 Dark thunder,
39 Terminate, 3A Evil aura, 3B Dragon blast, 3C Fist, 3D Radish kick, 3E Battle bow, 3F Perish punch,
40 Glow knuckle, 41 Heel drop, 42 Hammer punch, 43 Stone crush, 44 Tackle, 45 Shakedown, 46 Battle anger,
47 Tail, 48 Flame punch, 49 Burning fang, 4A Tailspin, 4B Flame breath, 4C Burning rain, 4D Inferno.
(l2ref's 0x12E100 from old/ROMMap.txt lands inside this string block: German or approximate.)

How records use them: record bytes 0x0F-0x14 hold **name ids** (status page and "learned a new attack" message:
`$82:C4B3` -> `$82:C3B1`). Battle scripts instead use opcode **3E b** with an **attack id** into the effect
table below, whose byte 0 is again a name id (mostly attack id + 1, but not always: e.g. attack 0x14 and 0x4B
reuse name 03, attack 0x33 uses name 0x29).

## 4. Attack effects (`$97:F63B`, file 0xBF63B)

`u16 offset[0x54]` relative to 0xBF63B (first = 0xA8). Record: `+0 u8 name id`, `+1 u8 battle animation`
(00 becomes FF in `$1262`; F0 = the standard hit, guess), `+2` L2BASM script (jumps relative to the record
start). Code path: op 3E (`$85:BD5E`) sets reg $23 = 0x0B, reg $24 = b; battle action 0x0B (`$81:A867` table ->
`$81:AD3B`, only if the actor `$7F:F44E` == 0x10 = the capsule) calls `$81:F476` (`LDA $97F63B,X / ADC #$F63B`),
passes byte 0 to `JSL $85:95C6` (guess: name display), stores the animation in `$1262`, runs the script at +2
(`JSL $81:FAC9` with Y = 2).
Elements in these scripts (21/22 xx xx, 1F xx xx): 0000 neutral, 0001 wind, 0002 fire, 0004 water, 0008 soil,
0010 light, 0020 dark (by which monster uses them; this differs from l2ref's 1F list, which may be for
monsters/items or German).

| id | file | name (byte 0) | anim (byte 1) | script (+2) |
|---|---|---|---|---|
| 00 | 0xBF6E3 | 01 Foomy punch | F0 | 37 32 01 15 80 04 0F 80 02 00 21 00 00 80 80 00 00 |
| 01 | 0xBF6F6 | 02 Mega punch | F0 | 56 00 00 37 32 01 15 80 04 0F 80 02 00 21 00 00 80 80 00 01 37 32 01 15 80 04 0F 80 02 00 21 00 00 80 80 00 00 |
| 02 | 0xBF71D | 03 Fang attack | F0 | 37 32 01 15 80 04 0F 80 03 00 21 00 00 80 80 00 00 |
| 03 | 0xBF730 | 04 Tail smash | F0 | 37 32 01 15 80 04 0F 80 09 00 21 00 00 80 80 00 00 |
| 04 | 0xBF743 | 05 Head Butt | F0 | 37 1F 00 00 32 01 20 64 30 00 |
| 05 | 0xBF74F | 06 Tackle | F0 | 37 1F 00 00 32 01 32 05 20 64 20 00 |
| 06 | 0xBF75D | 07 Bubble blow | 92 | 32 01 32 05 25 05 B0 00 |
| 07 | 0xBF767 | 08 Antler stab | F0 | 37 32 01 15 80 04 0F 80 02 00 21 10 00 80 80 00 00 |
| 08 | 0xBF77A | 09 Dash | F0 | 37 1F 10 00 32 01 32 05 20 64 20 00 |
| 09 | 0xBF788 | 0A Spear revive | 2B | 32 08 24 00 14 00 15 00 |
| 0A | 0xBF792 | 0B Healing kiss | 2B | 32 08 24 00 3C 00 29 00 |
| 0B | 0xBF79C | 0C Sacred song | 2C | 32 08 24 00 8C 00 29 00 |
| 0C | 0xBF7A6 | 0D Holy energy | 2D | 32 06 32 05 24 00 8C 00 29 00 |
| 0D | 0xBF7B2 | 0E Healing aura | 2D | 32 06 32 05 24 00 B4 00 29 00 |
| 0E | 0xBF7BE | 0F Wink | 26 | 32 08 25 04 50 00 |
| 0F | 0xBF7C6 | 10 Restoration | 2E | 32 08 26 00 64 26 01 64 26 03 64 26 04 64 26 05 64 00 |
| 10 | 0xBF7DA | 11 Purification | 35 | 32 06 32 05 26 00 64 26 01 64 26 03 64 26 04 64 26 05 64 00 |
| 11 | 0xBF7F0 | 12 Holy thunder | 12 | 15 80 08 0F 80 04 00 32 01 22 10 00 80 80 00 00 |
| 12 | 0xBF802 | 13 Thunderbolt | 12 | 15 80 08 0F 80 02 00 32 01 22 10 00 80 80 00 00 |
| 13 | 0xBF814 | 14 Bolt attack | 13 | 15 80 08 0F 80 04 00 32 01 32 05 22 10 00 80 80 00 00 |
| 14 | 0xBF828 | 03 Fang attack | F0 | 37 32 01 15 80 04 0F 80 02 00 21 01 00 80 80 00 00 |
| 15 | 0xBF83B | 15 Beak bash | F0 | 37 32 01 15 80 04 0F 80 02 00 21 01 00 80 80 00 00 |
| 16 | 0xBF84E | 16 Finale | F0 | 37 32 01 15 80 04 0F 80 02 00 21 01 00 80 80 00 00 |
| 17 | 0xBF861 | 17 Dragon fang | F0 | 37 1F 01 00 32 01 20 64 38 00 |
| 18 | 0xBF86D | 18 Iron claw | F0 | 37 1F 01 00 32 01 20 64 30 00 |
| 19 | 0xBF879 | 19 Iron fist | F0 | 37 1F 01 00 32 01 20 64 30 00 |
| 1A | 0xBF885 | 1A Air attack | F0 | 37 1F 01 00 32 01 32 05 20 64 20 00 |
| 1B | 0xBF893 | 1B Twister | F0 | 37 1F 01 00 32 01 32 05 20 64 20 00 |
| 1C | 0xBF8A1 | 1C Spiral wave | 1B | 15 80 08 0F 80 04 00 32 01 32 05 22 01 00 80 80 00 00 |
| 1D | 0xBF8B5 | 1D Tornado blow | 1B | 15 80 08 0F 80 02 00 32 01 32 05 22 01 00 80 80 00 00 |
| 1E | 0xBF8C9 | 1E Electro bolt | 12 | 15 80 08 0F 80 02 00 32 01 32 05 22 01 00 80 80 00 00 |
| 1F | 0xBF8DD | 1F Cyclone wave | 1C | 15 80 08 0F 80 04 00 32 01 32 05 22 01 00 80 80 00 00 |
| 20 | 0xBF8F1 | 20 Sonic blast | 85 | 15 80 08 0F 80 08 00 32 01 32 05 22 01 00 80 80 00 00 |
| 21 | 0xBF905 | 21 Fish kick | F0 | 37 32 01 15 80 04 0F 80 03 00 21 04 00 80 80 00 00 |
| 22 | 0xBF918 | 22 Hyper kick | F0 | 37 32 01 15 80 04 0F 80 05 00 21 04 00 80 80 00 00 |
| 23 | 0xBF92B | 23 Frog kick | F0 | 37 32 01 15 80 04 0F 80 02 00 21 04 00 80 80 00 00 |
| 24 | 0xBF93E | 26 Iron trident | F0 | 37 1F 04 00 32 01 20 64 20 00 |
| 25 | 0xBF94A | 27 Thunder fist | F0 | 37 1F 04 00 32 01 20 64 20 00 |
| 26 | 0xBF956 | 28 Power fist | F0 | 56 00 00 37 1F 04 00 32 01 20 64 20 01 37 1F 04 00 32 01 20 64 20 00 |
| 27 | 0xBF96F | 29 Mega trident | F0 | 37 1F 04 00 32 01 32 05 20 64 20 00 |
| 28 | 0xBF97D | 24 Dive attack | F0 | 37 1F 04 00 32 01 32 05 20 64 20 00 |
| 29 | 0xBF98B | 25 Diving claw | F0 | 37 1F 04 00 32 01 32 05 20 64 30 00 |
| 2A | 0xBF999 | 2A Bubble blast | 19 | 15 80 08 0F 80 02 00 32 01 32 05 22 04 00 80 80 00 00 |
| 2B | 0xBF9AD | 2B Hail attack | 91 | 15 80 08 0F 80 04 00 32 01 32 05 22 04 00 80 80 00 00 |
| 2C | 0xBF9C1 | 2C Thunderblast | 13 | 15 80 08 0F 80 04 00 32 01 32 05 22 04 00 80 80 00 00 |
| 2D | 0xBF9D5 | 2D Energy wave | 26 | 32 06 32 05 25 04 50 00 |
| 2E | 0xBF9DF | 2E Magic freeze | 23 | 32 01 27 01 64 00 |
| 2F | 0xBF9E7 | 2F Frog song | 8D | 32 01 32 05 25 05 B0 00 |
| 30 | 0xBF9F1 | 30 Wolf fang | F0 | 37 32 01 15 80 04 0F 80 02 00 21 20 00 80 80 00 27 00 24 00 |
| 31 | 0xBFA07 | 31 Wolf punch | F0 | 37 1F 20 00 32 01 20 64 20 00 |
| 32 | 0xBFA13 | 32 Sizzle smash | F0 | 37 1F 20 00 32 01 20 64 30 00 |
| 33 | 0xBFA1F | 29 Mega trident | F0 | 37 1F 20 00 32 01 32 05 20 64 20 00 |
| 34 | 0xBFA2D | 09 Dash | F0 | 37 1F 20 00 32 01 32 05 20 64 30 00 |
| 35 | 0xBFA3B | 33 Shriek | 65 | 32 06 32 05 26 04 64 00 |
| 36 | 0xBFA45 | 34 Trancemaker | 6D | 32 01 32 05 27 04 64 00 |
| 37 | 0xBFA4F | 35 Destruction | 1E | 32 01 27 02 5A 00 |
| 38 | 0xBFA57 | 36 Eliminator | 1F | 32 01 32 05 27 02 5A 00 |
| 39 | 0xBFA61 | 37 Dark flame | 8A | 15 80 08 0F 80 02 00 32 01 32 05 22 20 00 80 80 00 00 |
| 3A | 0xBFA75 | 38 Dark thunder | 89 | 15 80 08 0F 80 02 00 32 01 32 05 22 20 00 80 80 00 00 |
| 3B | 0xBFA89 | 39 Terminate | 95 | 15 80 08 0F 80 04 00 32 01 32 05 22 20 00 80 80 00 00 |
| 3C | 0xBFA9D | 3A Evil aura | 32 | 15 80 08 0F 80 06 00 32 01 32 05 22 20 00 80 80 00 00 |
| 3D | 0xBFAB1 | 3B Dragon blast | 16 | 15 80 08 0F 80 04 00 32 01 32 05 22 20 00 80 80 00 00 |
| 3E | 0xBFAC5 | 3C Fist | F0 | 37 32 01 15 80 04 0F 80 02 00 21 08 00 80 80 00 27 05 24 00 |
| 3F | 0xBFADB | 3D Radish kick | F0 | 37 32 01 15 80 04 0F 80 02 00 21 08 00 80 80 00 00 |
| 40 | 0xBFAEE | 3E Battle bow | 94 | 37 32 01 32 05 15 80 04 0F 80 02 00 21 08 00 80 80 00 00 |
| 41 | 0xBFB03 | 3F Perish punch | F0 | 37 1F 08 00 32 01 20 64 18 27 02 3C 00 |
| 42 | 0xBFB12 | 40 Glow knuckle | F0 | 37 1F 08 00 32 01 20 64 30 00 |
| 43 | 0xBFB1E | 41 Heel drop | F0 | 37 1F 08 00 32 01 20 64 28 00 |
| 44 | 0xBFB2A | 42 Hammer punch | F0 | 37 1F 08 00 32 01 20 64 20 27 03 3C 00 |
| 45 | 0xBFB39 | 05 Head Butt | F0 | 37 1F 08 00 32 01 20 64 38 00 |
| 46 | 0xBFB45 | 43 Stone crush | F0 | 37 1F 08 00 32 01 32 05 20 64 30 00 |
| 47 | 0xBFB53 | 44 Tackle | F0 | 37 1F 08 00 32 01 32 05 20 64 30 27 03 3C 00 |
| 48 | 0xBFB64 | 45 Shakedown | 80 | 15 80 04 15 81 08 0D 80 81 80 0F 80 02 00 32 01 32 05 22 08 00 80 80 00 00 |
| 49 | 0xBFB7F | 46 Battle anger | 80 | 15 80 04 15 81 08 0D 80 81 80 0F 80 04 00 32 01 32 05 22 08 00 80 80 00 00 |
| 4A | 0xBFB9A | 47 Tail | F0 | 37 32 01 15 80 04 0F 80 02 00 21 02 00 80 80 00 00 |
| 4B | 0xBFBAD | 03 Fang attack | F0 | 37 32 01 15 80 04 0F 80 02 00 21 02 00 80 80 00 00 |
| 4C | 0xBFBC0 | 48 Flame punch | F0 | 37 1F 02 00 32 01 20 64 18 00 |
| 4D | 0xBFBCC | 49 Burning fang | F0 | 37 1F 02 00 32 01 20 64 30 00 |
| 4E | 0xBFBD8 | 18 Iron claw | F0 | 37 1F 02 00 32 01 20 64 20 00 |
| 4F | 0xBFBE4 | 4A Tailspin | F0 | 37 1F 02 00 32 01 20 64 38 00 |
| 50 | 0xBFBF0 | 4B Flame breath | 16 | 15 80 08 0F 80 02 00 32 01 32 05 22 02 00 80 80 00 00 |
| 51 | 0xBFC04 | 4C Burning rain | 15 | 15 80 08 0F 80 02 00 32 01 32 05 22 02 00 80 80 00 00 |
| 52 | 0xBFC18 | 39 Terminate | 16 | 15 80 08 0F 80 04 00 32 01 32 05 22 02 00 80 80 00 00 |
| 53 | 0xBFC2C | 4D Inferno | 82 | 15 80 08 0F 80 06 00 32 01 32 05 22 02 00 80 80 00 00 |


## 5. Battle-script opcodes used by capsules (corrections to l2ref 3.5)

* **The L2BASM handler table base is `$85:B483`** (file 0x2B483): `$85:B470 JMP ($B483,X)`, X = op*2.
  l2ref's `$85:B485` is off by 2 (German?); with B485 every handler is the wrong one.
* **3E b** (1 arg): capsule attack b (reg $23 = 0x0B, reg $24 = b), `$85:BD5E`.
* **3F b w** (b + word): GOTO w if attack slot b-1 is learned in the current form, else continue
  (`$85:B652`: `1 << (b-1)` AND `$85:C1A1()`, jump via `$85:B551` = op 03 GOTO). Matches the data: Foomy M's
  Head Butt is learned slot 1 and gated by `3F 02`.
* **40 r** (1 arg): register r = learned mask of the current form (`$85:B8F7`, via the store helper `$85:C023`).
* `$85:C1A1` = `($11CA[2*monster] >> 3*(form-1)) & 7`.
* Forms 1-4 AI scripts start `0C 82 vv 00` (reg $82 = 0x11 / 0x23 / 0x32 / 0x3C for forms 1-4), `42 25 00`,
  `0A 80 81 80 ww 00` (IF reg80 >= reg81 GOTO ww, which is `2A` flee for most); then `42 26 00` + tests reg81 ==
  1/2/3, `42 27 00` + test reg80 == 0. In this context `42 25/26/27 00` seem to load regs $80/$81 rather than
  set weaknesses (open). Form-5 scripts have no flee branch. Physical attack = `32 01 28 00`, defend = `29 00`.
* Defense scripts all start `42 11 00` (immune to the 5 statuses); Darbi forms add `0C 52 00 00`; then
  per-monster `42 kk 00` pairs (Flash 0D 0A, Gusto 01 28, Zeppy 00 08 (+07 Blue Titan), Darbi 05 0E 28,
  Sully 07 17, Blaze 02 06).

### All 35 scripts (decoded with l2ref arg counts plus 3E = 1 arg, 3F = 3 bytes; every script decodes cleanly)

### 0 Foomy S (0xBDCFE)
AI: 2B: 0c 82 11 00 | 2F: 42 25 00 | 32: 0a 80 81 80 4d 00 | 38: 05 51 47 00 | 3C: 05 28 4b 00 | 40: 05 66 47 00 | 44: 3e 00 | 46: 00 | 47: 32 01 | 49: 28 | 4A: 00 | 4B: 29 | 4C: 00 | 4D: 2a | 4E: 00
Def: 4F: 42 11 00 | 52: 00

### 1 Foomy M (0xBDD51)
AI: 2B: 0c 82 23 00 | 2F: 42 25 00 | 32: 0a 80 81 80 76 00 | 38: 05 66 70 00 | 3C: 05 28 74 00 | 40: 42 26 00 | 43: 06 81 01 00 5b 00 | 49: 06 81 02 00 62 00 | 4F: 42 27 00 | 52: 06 80 00 00 70 00 | 58: 03 43 00 | 5B: 05 66 4f 00 | 5F: 3e 00 | 61: 00 | 62: 3f 02 69 00 | 66: 03 4f 00 | 69: 05 a3 4f 00 | 6D: 3e 04 | 6F: 00 | 70: 32 01 | 72: 28 | 73: 00 | 74: 29 | 75: 00 | 76: 2a | 77: 00
Def: 78: 42 11 00 | 7B: 00

### 2 Foomy L (0xBDDCD)
AI: 2B: 0c 82 32 00 | 2F: 42 25 00 | 32: 0a 80 81 80 83 00 | 38: 05 7a 7d 00 | 3C: 05 28 81 00 | 40: 42 26 00 | 43: 06 81 01 00 61 00 | 49: 06 81 02 00 68 00 | 4F: 06 81 03 00 6f 00 | 55: 42 27 00 | 58: 06 80 00 00 7d 00 | 5E: 03 43 00 | 61: 05 66 55 00 | 65: 3e 00 | 67: 00 | 68: 05 99 55 00 | 6C: 3e 04 | 6E: 00 | 6F: 3f 03 76 00 | 73: 03 55 00 | 76: 05 a3 55 00 | 7A: 3e 05 | 7C: 00 | 7D: 32 01 | 7F: 28 | 80: 00 | 81: 29 | 82: 00 | 83: 2a | 84: 00
Def: 85: 42 11 00 | 88: 00

### 3 Foomy H (0xBDE56)
AI: 2B: 0c 82 3c 00 | 2F: 42 25 00 | 32: 0a 80 81 80 87 00 | 38: 05 8f 81 00 | 3C: 05 28 85 00 | 40: 42 26 00 | 43: 06 81 01 00 61 00 | 49: 06 81 02 00 73 00 | 4F: 06 81 03 00 7a 00 | 55: 42 27 00 | 58: 06 80 00 00 81 00 | 5E: 03 43 00 | 61: 3f 01 6c 00 | 65: 05 66 55 00 | 69: 3e 00 | 6B: 00 | 6C: 05 66 55 00 | 70: 3e 01 | 72: 00 | 73: 05 99 55 00 | 77: 3e 04 | 79: 00 | 7A: 05 99 55 00 | 7E: 3e 05 | 80: 00 | 81: 32 01 | 83: 28 | 84: 00 | 85: 29 | 86: 00 | 87: 2a | 88: 00
Def: 89: 42 11 00 | 8C: 00

### 4 Gold Fox (0xBDEE3)
AI: 2B: 05 a3 70 00 | 2F: 05 28 74 00 | 33: 42 26 00 | 36: 06 81 01 00 54 00 | 3C: 06 81 02 00 5b 00 | 42: 06 81 03 00 62 00 | 48: 42 27 00 | 4B: 06 80 00 00 70 00 | 51: 03 36 00 | 54: 05 66 48 00 | 58: 3e 02 | 5A: 00 | 5B: 05 99 48 00 | 5F: 3e 06 | 61: 00 | 62: 3f 03 69 00 | 66: 03 48 00 | 69: 05 a3 48 00 | 6D: 3e 03 | 6F: 00 | 70: 32 01 | 72: 28 | 73: 00 | 74: 29 | 75: 00
Def: 76: 42 11 00 | 79: 00

### 5 Shaggy (0xBDF5D)
AI: 2B: 0c 82 11 00 | 2F: 42 25 00 | 32: 0a 80 81 80 4d 00 | 38: 05 51 47 00 | 3C: 05 14 4b 00 | 40: 05 66 47 00 | 44: 3e 09 | 46: 00 | 47: 32 01 | 49: 28 | 4A: 00 | 4B: 29 | 4C: 00 | 4D: 2a | 4E: 00
Def: 4F: 42 11 00 | 52: 42 0d 00 | 55: 42 0a 00 | 58: 00

### 6 Sprite (0xBDFB6)
AI: 2B: 0c 82 23 00 | 2F: 42 25 00 | 32: 0a 80 81 80 76 00 | 38: 05 51 70 00 | 3C: 05 14 74 00 | 40: 42 26 00 | 43: 06 81 01 00 5b 00 | 49: 06 81 02 00 62 00 | 4F: 42 27 00 | 52: 06 80 00 00 70 00 | 58: 03 43 00 | 5B: 05 85 4f 00 | 5F: 3e 0a | 61: 00 | 62: 3f 02 69 00 | 66: 03 4f 00 | 69: 05 99 4f 00 | 6D: 3e 0e | 6F: 00 | 70: 32 01 | 72: 28 | 73: 00 | 74: 29 | 75: 00 | 76: 2a | 77: 00
Def: 78: 42 11 00 | 7B: 42 0d 00 | 7E: 42 0a 00 | 81: 00

### 7 Cupid (0xBE038)
AI: 2B: 0c 82 32 00 | 2F: 42 25 00 | 32: 0a 80 81 80 83 00 | 38: 05 51 7d 00 | 3C: 05 14 81 00 | 40: 42 26 00 | 43: 06 81 01 00 61 00 | 49: 06 81 02 00 68 00 | 4F: 06 81 03 00 6f 00 | 55: 42 27 00 | 58: 06 80 00 00 7d 00 | 5E: 03 43 00 | 61: 05 99 55 00 | 65: 3e 0b | 67: 00 | 68: 05 99 55 00 | 6C: 3e 0f | 6E: 00 | 6F: 3f 03 76 00 | 73: 03 55 00 | 76: 05 a3 55 00 | 7A: 3e 12 | 7C: 00 | 7D: 32 01 | 7F: 28 | 80: 00 | 81: 29 | 82: 00 | 83: 2a | 84: 00
Def: 85: 42 11 00 | 88: 42 0d 00 | 8B: 42 0a 00 | 8E: 00

### 8 Twinkle (0xBE0C7)
AI: 2B: 0c 82 3c 00 | 2F: 42 25 00 | 32: 0a 80 81 80 9d 00 | 38: 05 51 97 00 | 3C: 05 14 9b 00 | 40: 42 26 00 | 43: 06 81 01 00 61 00 | 49: 06 81 02 00 73 00 | 4F: 06 81 03 00 85 00 | 55: 42 27 00 | 58: 06 80 00 00 97 00 | 5E: 03 43 00 | 61: 3f 01 6c 00 | 65: 05 99 55 00 | 69: 3e 0b | 6B: 00 | 6C: 05 a3 55 00 | 70: 3e 0c | 72: 00 | 73: 3f 02 7e 00 | 77: 05 99 55 00 | 7B: 3e 0f | 7D: 00 | 7E: 05 a3 55 00 | 82: 3e 10 | 84: 00 | 85: 3f 03 90 00 | 89: 05 99 55 00 | 8D: 3e 12 | 8F: 00 | 90: 05 a3 55 00 | 94: 3e 13 | 96: 00 | 97: 32 01 | 99: 28 | 9A: 00 | 9B: 29 | 9C: 00 | 9D: 2a | 9E: 00
Def: 9F: 42 11 00 | A2: 42 0d 00 | A5: 42 0a 00 | A8: 00

### 9 Unicorn (0xBE170)
AI: 2B: 05 7a 7b 00 | 2F: 05 14 7f 00 | 33: 42 26 00 | 36: 06 81 01 00 54 00 | 3C: 06 81 02 00 5b 00 | 42: 06 81 03 00 6d 00 | 48: 42 27 00 | 4B: 06 80 00 00 7b 00 | 51: 03 36 00 | 54: 05 99 48 00 | 58: 3e 08 | 5A: 00 | 5B: 3f 02 66 00 | 5F: 05 66 48 00 | 63: 3e 07 | 65: 00 | 66: 05 99 48 00 | 6A: 3e 11 | 6C: 00 | 6D: 3f 03 74 00 | 71: 03 48 00 | 74: 05 a3 48 00 | 78: 3e 0d | 7A: 00 | 7B: 32 01 | 7D: 28 | 7E: 00 | 7F: 29 | 80: 00
Def: 81: 42 11 00 | 84: 42 0d 00 | 87: 42 0a 00 | 8A: 00

### 10 Hard Hat (0xBE1FB)
AI: 2B: 0c 82 11 00 | 2F: 42 25 00 | 32: 0a 80 81 80 47 00 | 38: 05 51 43 00 | 3C: 05 66 43 00 | 40: 3e 14 | 42: 00 | 43: 32 01 | 45: 28 | 46: 00 | 47: 2a | 48: 00
Def: 49: 42 11 00 | 4C: 42 01 00 | 4F: 42 28 00 | 52: 00

### 11 Bluebird (0xBE24E)
AI: 2B: 0c 82 23 00 | 2F: 42 25 00 | 32: 0a 80 81 80 7d 00 | 38: 05 66 79 00 | 3C: 42 26 00 | 3F: 06 81 01 00 5d 00 | 45: 06 81 02 00 64 00 | 4B: 06 81 03 00 6b 00 | 51: 42 27 00 | 54: 06 80 00 00 79 00 | 5A: 03 3f 00 | 5D: 05 66 51 00 | 61: 3e 15 | 63: 00 | 64: 05 99 51 00 | 68: 3e 18 | 6A: 00 | 6B: 3f 03 72 00 | 6F: 03 51 00 | 72: 05 a3 51 00 | 76: 3e 1c | 78: 00 | 79: 32 01 | 7B: 28 | 7C: 00 | 7D: 2a | 7E: 00
Def: 7F: 42 11 00 | 82: 42 01 00 | 85: 42 28 00 | 88: 00

### 12 Winged Horse (0xBE2D7)
AI: 2B: 0c 82 32 00 | 2F: 42 25 00 | 32: 0a 80 81 80 7d 00 | 38: 05 7a 79 00 | 3C: 42 26 00 | 3F: 06 81 01 00 5d 00 | 45: 06 81 02 00 64 00 | 4B: 06 81 03 00 6b 00 | 51: 42 27 00 | 54: 06 80 00 00 79 00 | 5A: 03 3f 00 | 5D: 05 66 51 00 | 61: 3e 1a | 63: 00 | 64: 05 66 51 00 | 68: 3e 16 | 6A: 00 | 6B: 3f 03 72 00 | 6F: 03 51 00 | 72: 05 99 51 00 | 76: 3e 1e | 78: 00 | 79: 32 01 | 7B: 28 | 7C: 00 | 7D: 2a | 7E: 00
Def: 7F: 42 11 00 | 82: 42 01 00 | 85: 42 28 00 | 88: 00

### 13 Green Titan (0xBE360)
AI: 2B: 0c 82 3c 00 | 2F: 42 25 00 | 32: 0a 80 81 80 7d 00 | 38: 05 8f 79 00 | 3C: 42 26 00 | 3F: 06 81 01 00 5d 00 | 45: 06 81 02 00 64 00 | 4B: 06 81 03 00 6b 00 | 51: 42 27 00 | 54: 06 80 00 00 79 00 | 5A: 03 3f 00 | 5D: 05 99 51 00 | 61: 3e 19 | 63: 00 | 64: 05 99 51 00 | 68: 3e 1b | 6A: 00 | 6B: 3f 03 72 00 | 6F: 03 51 00 | 72: 05 a3 51 00 | 76: 3e 20 | 78: 00 | 79: 32 01 | 7B: 28 | 7C: 00 | 7D: 2a | 7E: 00
Def: 7F: 42 11 00 | 82: 42 01 00 | 85: 42 28 00 | 88: 00

### 14 Sky Dragon (0xBE3E9)
AI: 2B: 05 a3 70 00 | 2F: 42 26 00 | 32: 06 81 01 00 50 00 | 38: 06 81 02 00 62 00 | 3E: 06 81 03 00 69 00 | 44: 42 27 00 | 47: 06 80 00 00 70 00 | 4D: 03 32 00 | 50: 3f 01 5b 00 | 54: 05 99 44 00 | 58: 3e 1d | 5A: 00 | 5B: 05 a3 44 00 | 5F: 3e 1f | 61: 00 | 62: 05 99 44 00 | 66: 3e 17 | 68: 00 | 69: 05 99 44 00 | 6D: 3e 1e | 6F: 00 | 70: 32 01 | 72: 28 | 73: 00
Def: 74: 42 11 00 | 77: 42 01 00 | 7A: 42 28 00 | 7D: 00

### 15 Red Fish (0xBE467)
AI: 2B: 0c 82 11 00 | 2F: 42 25 00 | 32: 0a 80 81 80 70 00 | 38: 05 51 6c 00 | 3C: 42 26 00 | 3F: 06 81 01 00 57 00 | 45: 06 81 02 00 5e 00 | 4B: 42 27 00 | 4E: 06 80 00 00 6c 00 | 54: 03 3f 00 | 57: 05 66 4b 00 | 5B: 3e 21 | 5D: 00 | 5E: 3f 02 65 00 | 62: 03 4b 00 | 65: 05 a3 4b 00 | 69: 3e 2a | 6B: 00 | 6C: 32 01 | 6E: 28 | 6F: 00 | 70: 2a | 71: 00
Def: 72: 42 11 00 | 75: 42 00 00 | 78: 42 08 00 | 7B: 00

### 16 Toadie (0xBE4E3)
AI: 2B: 0c 82 23 00 | 2F: 42 25 00 | 32: 0a 80 81 80 81 00 | 38: 05 66 7d 00 | 3C: 42 26 00 | 3F: 06 81 01 00 5d 00 | 45: 06 81 02 00 64 00 | 4B: 06 81 03 00 6b 00 | 51: 42 27 00 | 54: 06 80 00 00 7d 00 | 5A: 03 3f 00 | 5D: 05 99 51 00 | 61: 3e 2f | 63: 00 | 64: 05 66 51 00 | 68: 3e 23 | 6A: 00 | 6B: 3f 03 76 00 | 6F: 05 99 51 00 | 73: 3e 24 | 75: 00 | 76: 05 99 51 00 | 7A: 3e 27 | 7C: 00 | 7D: 32 01 | 7F: 28 | 80: 00 | 81: 2a | 82: 00
Def: 83: 42 11 00 | 86: 42 00 00 | 89: 42 08 00 | 8C: 00

### 17 Sea Giant (0xBE570)
AI: 2B: 0c 82 32 00 | 2F: 42 25 00 | 32: 0a 80 81 80 81 00 | 38: 05 7a 7d 00 | 3C: 42 26 00 | 3F: 06 81 01 00 5d 00 | 45: 06 81 02 00 64 00 | 4B: 06 81 03 00 6b 00 | 51: 42 27 00 | 54: 06 80 00 00 7d 00 | 5A: 03 3f 00 | 5D: 05 66 51 00 | 61: 3e 2d | 63: 00 | 64: 05 99 51 00 | 68: 3e 2b | 6A: 00 | 6B: 3f 03 76 00 | 6F: 05 99 51 00 | 73: 3e 24 | 75: 00 | 76: 05 99 51 00 | 7A: 3e 27 | 7C: 00 | 7D: 32 01 | 7F: 28 | 80: 00 | 81: 2a | 82: 00
Def: 83: 42 11 00 | 86: 42 00 00 | 89: 42 08 00 | 8C: 00

### 18 Blue Titan (0xBE5FD)
AI: 2B: 0c 82 3c 00 | 2F: 42 25 00 | 32: 0a 80 81 80 74 00 | 38: 05 8f 70 00 | 3C: 42 26 00 | 3F: 06 81 01 00 57 00 | 45: 06 81 02 00 5e 00 | 4B: 42 27 00 | 4E: 06 80 00 00 70 00 | 54: 03 3f 00 | 57: 05 99 4b 00 | 5B: 3e 2c | 5D: 00 | 5E: 3f 02 69 00 | 62: 05 99 4b 00 | 66: 3e 25 | 68: 00 | 69: 05 99 4b 00 | 6D: 3e 26 | 6F: 00 | 70: 32 01 | 72: 28 | 73: 00 | 74: 2a | 75: 00
Def: 76: 42 11 00 | 79: 42 00 00 | 7C: 42 08 00 | 7F: 42 07 00 | 82: 00

### 19 Fish Head (0xBE680)
AI: 2B: 05 8f 82 00 | 2F: 42 26 00 | 32: 06 81 01 00 50 00 | 38: 06 81 02 00 62 00 | 3E: 06 81 03 00 74 00 | 44: 42 27 00 | 47: 06 80 00 00 82 00 | 4D: 03 32 00 | 50: 3f 01 5b 00 | 54: 05 66 44 00 | 58: 3e 21 | 5A: 00 | 5B: 05 66 44 00 | 5F: 3e 22 | 61: 00 | 62: 3f 02 6d 00 | 66: 05 99 44 00 | 6A: 3e 28 | 6C: 00 | 6D: 05 99 44 00 | 71: 3e 29 | 73: 00 | 74: 3f 03 7b 00 | 78: 03 44 00 | 7B: 05 66 44 00 | 7F: 3e 2e | 81: 00 | 82: 32 01 | 84: 28 | 85: 00
Def: 86: 42 11 00 | 89: 42 00 00 | 8C: 42 08 00 | 8F: 00

### 20 Myconido (0xBE710)
AI: 2B: 0c 82 11 00 | 2F: 42 25 00 | 32: 0a 80 81 80 70 00 | 38: 05 51 6c 00 | 3C: 42 26 00 | 3F: 06 81 01 00 57 00 | 45: 06 81 02 00 5e 00 | 4B: 42 27 00 | 4E: 06 80 00 00 6c 00 | 54: 03 3f 00 | 57: 05 66 4b 00 | 5B: 3e 36 | 5D: 00 | 5E: 3f 02 65 00 | 62: 03 4b 00 | 65: 05 99 4b 00 | 69: 3e 35 | 6B: 00 | 6C: 32 01 | 6E: 28 | 6F: 00 | 70: 2a | 71: 00
Def: 72: 0c 52 00 00 | 76: 42 11 00 | 79: 42 05 00 | 7C: 42 0e 00 | 7F: 42 28 00 | 82: 00

### 21 Wolfman (0xBE793)
AI: 2B: 0c 82 23 00 | 2F: 42 25 00 | 32: 0a 80 81 80 74 00 | 38: 05 33 70 00 | 3C: 42 26 00 | 3F: 06 81 01 00 57 00 | 45: 06 81 02 00 5e 00 | 4B: 42 27 00 | 4E: 06 80 00 00 70 00 | 54: 03 3f 00 | 57: 05 66 4b 00 | 5B: 3e 30 | 5D: 00 | 5E: 3f 02 69 00 | 62: 05 99 4b 00 | 66: 3e 31 | 68: 00 | 69: 05 99 4b 00 | 6D: 3e 32 | 6F: 00 | 70: 32 01 | 72: 28 | 73: 00 | 74: 2a | 75: 00
Def: 76: 0c 52 00 00 | 7A: 42 11 00 | 7D: 42 05 00 | 80: 42 0e 00 | 83: 42 28 00 | 86: 00

### 22 Mini Imp (0xBE81A)
AI: 2B: 0c 82 32 00 | 2F: 42 25 00 | 32: 0a 80 81 80 81 00 | 38: 05 33 7d 00 | 3C: 42 26 00 | 3F: 06 81 01 00 5d 00 | 45: 06 81 02 00 64 00 | 4B: 06 81 03 00 6b 00 | 51: 42 27 00 | 54: 06 80 00 00 7d 00 | 5A: 03 3f 00 | 5D: 05 99 51 00 | 61: 3e 37 | 63: 00 | 64: 05 99 51 00 | 68: 3e 33 | 6A: 00 | 6B: 3f 03 76 00 | 6F: 05 99 51 00 | 73: 3e 39 | 75: 00 | 76: 05 99 51 00 | 7A: 3e 3b | 7C: 00 | 7D: 32 01 | 7F: 28 | 80: 00 | 81: 2a | 82: 00
Def: 83: 0c 52 00 00 | 87: 42 11 00 | 8A: 42 05 00 | 8D: 42 0e 00 | 90: 42 28 00 | 93: 00

### 23 Big Imp (0xBE8AE)
AI: 2B: 0c 82 3c 00 | 2F: 42 25 00 | 32: 0a 80 81 80 81 00 | 38: 05 33 7d 00 | 3C: 42 26 00 | 3F: 06 81 01 00 5d 00 | 45: 06 81 02 00 64 00 | 4B: 06 81 03 00 6b 00 | 51: 42 27 00 | 54: 06 80 00 00 7d 00 | 5A: 03 3f 00 | 5D: 05 99 51 00 | 61: 3e 32 | 63: 00 | 64: 05 99 51 00 | 68: 3e 3b | 6A: 00 | 6B: 3f 03 76 00 | 6F: 05 99 51 00 | 73: 3e 3a | 75: 00 | 76: 05 99 51 00 | 7A: 3e 3c | 7C: 00 | 7D: 32 01 | 7F: 28 | 80: 00 | 81: 2a | 82: 00
Def: 83: 0c 52 00 00 | 87: 42 11 00 | 8A: 42 05 00 | 8D: 42 0e 00 | 90: 42 28 00 | 93: 00

### 24 Red Dragon (0xBE942)
AI: 2B: 05 a3 6c 00 | 2F: 42 26 00 | 32: 06 81 01 00 50 00 | 38: 06 81 02 00 57 00 | 3E: 06 81 03 00 5e 00 | 44: 42 27 00 | 47: 06 80 00 00 6c 00 | 4D: 03 32 00 | 50: 05 99 44 00 | 54: 3e 34 | 56: 00 | 57: 05 99 44 00 | 5B: 3e 3d | 5D: 00 | 5E: 3f 03 65 00 | 62: 03 44 00 | 65: 05 a3 44 00 | 69: 3e 38 | 6B: 00 | 6C: 32 01 | 6E: 28 | 6F: 00
Def: 70: 0c 52 00 00 | 74: 42 11 00 | 77: 42 05 00 | 7A: 42 0e 00 | 7D: 42 28 00 | 80: 00

### 25 Raddisher (0xBE9C3)
AI: 2B: 0c 82 11 00 | 2F: 42 25 00 | 32: 0a 80 81 80 4d 00 | 38: 05 3d 4b 00 | 3C: 05 80 47 00 | 40: 05 66 47 00 | 44: 3e 3f | 46: 00 | 47: 32 01 | 49: 28 | 4A: 00 | 4B: 29 | 4C: 00 | 4D: 2a | 4E: 00
Def: 4F: 42 11 00 | 52: 42 07 00 | 55: 42 17 00 | 58: 00

### 26 Red Cap (0xBEA1C)
AI: 2B: 0c 82 23 00 | 2F: 42 25 00 | 32: 0a 80 81 80 76 00 | 38: 05 51 74 00 | 3C: 05 7a 70 00 | 40: 42 26 00 | 43: 06 81 01 00 5b 00 | 49: 06 81 02 00 62 00 | 4F: 42 27 00 | 52: 06 80 00 00 70 00 | 58: 03 43 00 | 5B: 05 99 4f 00 | 5F: 3e 41 | 61: 00 | 62: 3f 02 69 00 | 66: 03 4f 00 | 69: 05 a3 4f 00 | 6D: 3e 42 | 6F: 00 | 70: 32 01 | 72: 28 | 73: 00 | 74: 29 | 75: 00 | 76: 2a | 77: 00
Def: 78: 42 11 00 | 7B: 42 07 00 | 7E: 42 17 00 | 81: 00

### 27 Giant (0xBEA9E)
AI: 2B: 0c 82 32 00 | 2F: 42 25 00 | 32: 0a 80 81 80 87 00 | 38: 05 51 85 00 | 3C: 05 8f 81 00 | 40: 42 26 00 | 43: 06 81 01 00 61 00 | 49: 06 81 02 00 68 00 | 4F: 06 81 03 00 6f 00 | 55: 42 27 00 | 58: 06 80 00 00 81 00 | 5E: 03 43 00 | 61: 05 66 55 00 | 65: 3e 3e | 67: 00 | 68: 05 99 55 00 | 6C: 3e 43 | 6E: 00 | 6F: 3f 03 7a 00 | 73: 05 99 55 00 | 77: 3e 48 | 79: 00 | 7A: 05 99 55 00 | 7E: 3e 49 | 80: 00 | 81: 32 01 | 83: 28 | 84: 00 | 85: 29 | 86: 00 | 87: 2a | 88: 00
Def: 89: 42 11 00 | 8C: 42 07 00 | 8F: 42 17 00 | 92: 00

### 28 Stonehead (0xBEB31)
AI: 2B: 0c 82 3c 00 | 2F: 42 25 00 | 32: 0a 80 81 80 83 00 | 38: 05 51 81 00 | 3C: 05 8f 7d 00 | 40: 42 26 00 | 43: 06 81 01 00 61 00 | 49: 06 81 02 00 68 00 | 4F: 06 81 03 00 6f 00 | 55: 42 27 00 | 58: 06 80 00 00 7d 00 | 5E: 03 43 00 | 61: 05 99 55 00 | 65: 3e 49 | 67: 00 | 68: 05 99 55 00 | 6C: 3e 45 | 6E: 00 | 6F: 3f 03 76 00 | 73: 03 55 00 | 76: 05 a3 55 00 | 7A: 3e 46 | 7C: 00 | 7D: 32 01 | 7F: 28 | 80: 00 | 81: 29 | 82: 00 | 83: 2a | 84: 00
Def: 85: 42 11 00 | 88: 42 07 00 | 8B: 42 17 00 | 8E: 00

### 29 Centaur (0xBEBC0)
AI: 2B: 05 51 74 00 | 2F: 05 8f 70 00 | 33: 42 26 00 | 36: 06 81 01 00 54 00 | 3C: 06 81 02 00 5b 00 | 42: 06 81 03 00 62 00 | 48: 42 27 00 | 4B: 06 80 00 00 70 00 | 51: 03 36 00 | 54: 05 66 48 00 | 58: 3e 40 | 5A: 00 | 5B: 05 99 48 00 | 5F: 3e 44 | 61: 00 | 62: 3f 03 69 00 | 66: 03 48 00 | 69: 05 a3 48 00 | 6D: 3e 47 | 6F: 00 | 70: 32 01 | 72: 28 | 73: 00 | 74: 29 | 75: 00
Def: 76: 42 11 00 | 79: 42 07 00 | 7C: 42 17 00 | 7F: 00

### 30 Armor Dog (0xBEC40)
AI: 2B: 0c 82 11 00 | 2F: 42 25 00 | 32: 0a 80 81 80 47 00 | 38: 05 51 43 00 | 3C: 05 66 43 00 | 40: 3e 4a | 42: 00 | 43: 32 01 | 45: 28 | 46: 00 | 47: 2a | 48: 00
Def: 49: 42 11 00 | 4C: 42 02 00 | 4F: 42 06 00 | 52: 00

### 31 Winged Lion (0xBEC93)
AI: 2B: 0c 82 23 00 | 2F: 42 25 00 | 32: 0a 80 81 80 70 00 | 38: 05 66 6c 00 | 3C: 42 26 00 | 3F: 06 81 01 00 57 00 | 45: 06 81 02 00 5e 00 | 4B: 42 27 00 | 4E: 06 80 00 00 6c 00 | 54: 03 3f 00 | 57: 05 66 4b 00 | 5B: 3e 4a | 5D: 00 | 5E: 3f 02 65 00 | 62: 03 4b 00 | 65: 05 a3 4b 00 | 69: 3e 4d | 6B: 00 | 6C: 32 01 | 6E: 28 | 6F: 00 | 70: 2a | 71: 00
Def: 72: 42 11 00 | 75: 42 02 00 | 78: 42 06 00 | 7B: 00

### 32 Wing Lizard (0xBED0F)
AI: 2B: 0c 82 32 00 | 2F: 42 25 00 | 32: 0a 80 81 80 7d 00 | 38: 05 7a 79 00 | 3C: 42 26 00 | 3F: 06 81 01 00 5d 00 | 45: 06 81 02 00 64 00 | 4B: 06 81 03 00 6b 00 | 51: 42 27 00 | 54: 06 80 00 00 79 00 | 5A: 03 3f 00 | 5D: 05 66 51 00 | 61: 3e 4b | 63: 00 | 64: 05 99 51 00 | 68: 3e 4e | 6A: 00 | 6B: 3f 03 72 00 | 6F: 03 51 00 | 72: 05 a3 51 00 | 76: 3e 50 | 78: 00 | 79: 32 01 | 7B: 28 | 7C: 00 | 7D: 2a | 7E: 00
Def: 7F: 42 11 00 | 82: 42 02 00 | 85: 42 06 00 | 88: 00

### 33 Blaze Dragon (0xBED98)
AI: 2B: 0c 82 3c 00 | 2F: 42 25 00 | 32: 0a 80 81 80 81 00 | 38: 05 8f 7d 00 | 3C: 42 26 00 | 3F: 06 81 01 00 5d 00 | 45: 06 81 02 00 64 00 | 4B: 06 81 03 00 6b 00 | 51: 42 27 00 | 54: 06 80 00 00 7d 00 | 5A: 03 3f 00 | 5D: 05 99 51 00 | 61: 3e 4c | 63: 00 | 64: 05 99 51 00 | 68: 3e 4d | 6A: 00 | 6B: 3f 03 76 00 | 6F: 05 99 51 00 | 73: 3e 50 | 75: 00 | 76: 05 99 51 00 | 7A: 3e 52 | 7C: 00 | 7D: 32 01 | 7F: 28 | 80: 00 | 81: 2a | 82: 00
Def: 83: 42 11 00 | 86: 42 02 00 | 89: 42 06 00 | 8C: 00

### 34 Firebird (0xBEE25)
AI: 2B: 05 a3 6c 00 | 2F: 42 26 00 | 32: 06 81 01 00 50 00 | 38: 06 81 02 00 57 00 | 3E: 06 81 03 00 5e 00 | 44: 42 27 00 | 47: 06 80 00 00 44 00 | 4D: 03 32 00 | 50: 05 99 44 00 | 54: 3e 51 | 56: 00 | 57: 05 99 44 00 | 5B: 3e 4f | 5D: 00 | 5E: 3f 03 65 00 | 62: 03 44 00 | 65: 05 a3 44 00 | 69: 3e 53 | 6B: 00 | 6C: 32 01 | 6E: 28 | 6F: 00
Def: 70: 42 11 00 | 73: 42 02 00


## 6. Evolution, feeding, fruits (capsule menu code, bank $82 ~C1B5-D5D0)

Tables in bank $8E (file = 0x70000 + addr - 0x8000):

| SNES | File | Content |
|---|---|---|
| `$8E:D558` | 0x75558 | default names, 7 x char[5]: JELZE FLASH GUSTO ZEPPY DARBI SULLY BLAZE, then 00 |
| `$8E:D8BB` | 0x758BB | per-monster bit 01 02 04 08 10 20 40 |
| `$8E:D8C3` | 0x758C3 | slot bits u16 0001 0002 0004 (shifted by 3*(form-1) into `$11CA`) |
| `$8E:E466` | 0x76466 | u16 price thresholds 499 599 699 999 1999 2999 3999 4999 5999 7999 11999 19999 24999 28999 31999 (tier = first index whose threshold >= value; only values >= 200 count) |
| `$8E:E485` | 0x76485 | initial pickiness by highest form 0-4: FF 00 04 08 00 |
| `$8E:E48A` | 0x7648A | meter gain [(form-1)*4 + tier/4]: form1 01 02 04 08, form2 00 01 02 04, form3 00 00 01 02, form4 00 00 00 00 |
| `$8E:E49A` | 0x7649A | u16 per monster, fruit that gives form 5: Jelze 2E Secret, Flash 32 Dark, Gusto 33 Earth, Zeppy 34 Flame, Darbi 2F Holy, Sully 30 Breeze, Blaze 31 Charm |
| `$8E:E4A8` | 0x764A8 | u16 per monster, own fruit: Jelze 2E Secret, Flash 2F Holy, Gusto 30 Breeze, Zeppy 31 Charm, Darbi 32 Dark, Sully 33 Earth, Blaze 34 Flame (0x35 Magic fruit counts for everyone) |
| `$8E:E4B6` | 0x764B6 | u16 per monster, form needed for the form-5 fruit (and returned to by the own fruit): 4 2 4 3 2 4 3 |
| `$8E:E4C4` | 0x764C4 | level when the capsule joins: 8 21 26 42 36 32 15 (`$82:E7C8`) |
| `$8E:E4CB` | 0x764CB | EXP multipliers (section 1) |
| `$95:FF16` | 0xAFF16 | per price tier: u16 list offset, u16 count; a random craved item is drawn from it (`$82:C554`; each capsule can have its own, see below) |

### Feeding (checked in the game, 2026-10-27)

Capsule > Feed does not give EXP (the level only comes from battles). It fills the "GROW" meter `$11C2[n]`, 32
points; when it is full the monster moves up a form. Lufia Forge: `Core/Lufia2/L2CapsuleFeed.cs`, edited on the
Capsule monsters page (cards Feeding, Items they ask for, <name>'s fruits, What an item gives).

Code: `$82:C683` feed item (item id in `$0A06`; `$81:F1C5` loads the item record to `$0B84`, so `$0B89` is the
record's +5, the **price**; items with use bit 0x20 "can't be dropped" can't be fed, `$82:C68E`).
* Own fruit `E4A8[n]`: pickiness reset to `E485[highest form]` (`$82:C53A`), +1 point (operand file 0x14BDF,
  message 09). Magic fruit (item id at file 0x1464A, 0x35): +8 points (operand 0x14BEB, message 0A).
* Any other item: price < 200 (operand of `CMP #$00C8`, file 0x146D9) or tier < pickiness `$11A6[n]`: refused.
  The item is still used up; the pickiness drops by 1 unless `E48A[(form-1)*4 + newpick/4]` is 0 (then it stays),
  and when it drops a new wish is drawn. Otherwise eaten: tier = first index i of the 15 thresholds `E466` with
  threshold >= price (15 when above them all); points = `E48A[(form-1)*4 + tier/4]` (form = highest form, 1-3);
  pickiness := tier.
* The wish (`$82:C577`, when the feed screen opens and after each feeding): if bit n of `$11A5` is clear, set it and
  draw `$11AD[n]` = random item of the list of tier `$11A6[n]` (`$82:C554`: `LDA $95FF16,X` offset, `LDA $95FF18,X`
  count, `JSL $80:8299` random 0..count-1 (count 0 gives 0, so the first item of the next list), `LDA $95FF16,X`
  item; the three long addresses are at file 0x1455B, 0x14560, 0x14573). Eating the wished item multiplies the
  points by 3 (`ASL / CLC / ADC $154F` at `$82:CC24`, file 0x14C24; message 0B "Thank you!!"), and every eaten
  item clears bit n, so a new wish is drawn.
* The points go into the meter during the sparkle animation (`$82:CB10`): 4 x points + 4 steps, at most 0x20
  (`CMP #$20` / `LDA #$20`, file 0x14B1E / 0x14B22); +1 point every second step, so **one feeding gives at most 16
  points** (a wished tier-12 item in form 1, 8 x 3, gives 16). Each step puts a sparkle in one of 32 sprite slots
  (`$1388,X` etc., X = 0x10..0x2F); with 0x40 steps the game crashed (writes past the slots), so 16 can only be
  lowered.
* At 32 points `$82:C78E` evolves: form + 1, `$11BB[n]` + 1, meter 0, pickiness := `E485[new form]`, new sprite and
  palette (`$82:C818`).
* Normal feeding only when current form == highest form, highest < 4, not in form 5 (`$82:CC3C`).

The game's wish lists (by tier; hand-picked, not matched to the tier's prices; tier 15 is empty and reads past the
table, Lufia Forge points an empty list at the nearest list with items when it writes them):

| Tier | Prices | Items |
|---|---|---|
| 0 | 200-499 | Long knife |
| 1 | 500-599 | Flying blow, Thunder ax, Toga, Chain armor, Stone plate, Long robe, Iron plate, Metal mail |
| 2 | 600-699 | (empty: asks for Magic scale) |
| 3 | 700-999 | Magic scale, Holy robe, Ghostclothes, Royal dress, Full mail |
| 4 | 1000-1999 | Buster sword |
| 5, 6 | 2000-3999 | (empty: Deadly sword) |
| 7 | 4000-4999 | Deadly sword |
| 8 | 5000-5999 | War rapier |
| 9 | 6000-7999 | Secret fruit, Knife, Insect crush, Kukri, Bronze sword, Long sword, Beserk blade, Multi sword, Rockbreaker, Lucky blade, Super sword, Old sword, Sizzle sword, Blaze sword, Egg sword, Staff |
| 10 | 8000-11999 | Gladius, Block shield, Cloth helmet |
| 11 | 12000-19999 | Deadly sword, Fire dagger, Mega ax, Crystal wand |
| 12 | 20000-24999 | 33 items: Flame fruit, Magic fruit, Frypan, Battle knife, Short sword, Light knife, Cold rapier, Estok, Silvo rapier, Crazy blade, Red saber, Lucky blade, Sky sword, Snow sword, Fry sword, Bronze ax, Flying ax, Mace, Trident, Figgoru, Coat, Light armor, Thick cloth, Bright armor, Bright cloth, Pearl brace, Gold gloves, Gold shield, Plati gloves, Rune gloves, Zirco shield, Water gaunt, Cap |
| 13 | 25000-28999 | (empty: Green tea) |
| 14 | 29000-31999 | Green tea, Escape, Jet helm |
| 15 | 32000- | (empty) |

The table is 0xD8 bytes (room for 76 items). Longer lists go to the added space: "LFWL", u16 room, then the table
(with room for 64 more items), the three long addresses pointing there.

Checked in BizHawk (agent test kit `scratchpad\agent_capsule`: a new game on map 03 where Jelze joins with event
`81 00`, Dagger x5 and Frock x5 in the new-game items; RAM `$11C2` meter, `$11A6` pickiness, `$11AD` wish):
* Clean game: Jelze asks for Long knife; Dagger (400 G) and Frock (400 G) give 1 point each.
* Tier 0 list = Dagger, form 1 tiers 0-3 = 3 points, wish bonus x2 (made on the page): it asks for Dagger, Dagger
  gives 6 ("Thank you!!"), Frock 3. Same with the lists moved to the added space.
* Long knife (800 G, wished) gives 3, pickiness 3, the wish becomes Magic scale (tier 3); then Dagger is refused,
  used up, pickiness 2.
* Form 1 tiers 0-3 = 16, x1: two Daggers give 16 + 16, Jelze becomes Foomy M (form 2), pickiness 4, asks for Buster
  sword. With points 32 and the sparkle limit raised to 32 the game crashed (black screen, then reset).
* Form 5 (`$82:CC58`): fruit E49A[n] while form == E4B6[n] sets bit 08 of `$11BB[n]`, form := 5; in form 5, own
  fruit E4A8[n] clears bit 08 and form := E4B6[n]. `$82:CC58` only runs when `$82:CC3C` says the monster can't be
  fed for points (highest form 4, a form below the highest picked in the capsule menu, or in form 5); where it can be
  fed, the 5th-form fruit is eaten as an ordinary item (seen in the game: with the fruit set to Dagger and the form to
  1, Jelze in form 1 ate it for 1 point).
* Learning (`$82:CD1F`, RTL, battle side): 1/8 chance, random slot 0-2; if record[0x12+slot] != 0 and not yet
  learned, set the bit in `$11CA[n]` and show the name (`$82:C4B3`).
* Form choice: the capsule menu (`$82:C1B5..C243`) lets the player pick any form up to the highest reached.

More settings checked in BizHawk (2026-10-27, test kit `scratchpad\agent_gaps_cap`, same new game with Jelze):
* Own fruit and Magic fruit: Jelze's own fruit set to Dagger with 5 points, the Magic fruit set to Frock with 12
  points: Dagger gives 5 (meter 5, pickiness stays 0), then Frock 12 (meter 17). Fruits leave the wish as it is.
* Price threshold: tiers 0-3 set to 250 / 300 / 350 / 390, so Dagger and Frock (400 G) are tier 4: each gives the
  points of tiers 4-7 in form 1 (2), pickiness 4, the wish is drawn from tier 4 (Buster sword).
* 5th-form fruit: own fruit Dagger with 16 points, forms 1-3 tiers 0-3 = 16 and starting pickiness 0, the 5th-form
  fruit Frock in form 4: D D (form 2), D D (form 3), F F (form 4, "I'm not hungry" for points), then Frock: Gold Fox
  (form 5, `$11BB` = 0C), then Dagger (own fruit): Foomy H again (form 4, `$11BB` = 04).

### Each capsule's own wish lists (Lufia Forge hook)

In the game all 7 monsters draw the asked-for item from the 16 shared lists. The draw is the routine at `$82:C554`
(A = tier; only caller `$82:C593`, the wish of the active capsule `$11A3` in its form `$11A4`):

```
C554 AND #$00FF / ASL / ASL / TAX     <- hook: JSL block+8 / NOP / NOP (file 0x14554)
C55A LDA $95FF16,X / PHA / LDA $95FF18,X / JSL $808299 / AND #$00FF / ASL / STA $54 / PLA / CLC / ADC $54 / TAX
C572 LDA $95FF16,X
C576 RTS
```

Lufia Forge (`Core/Lufia2/L2CapsuleWishes.cs`) replaces the first 6 bytes with a JSL into a block in the added space:
"LFCW", version, u16 room; code at +8; at +0xC0 a directory of 7 monsters x 8 u16 (entry 0 = all its forms, 1-5 = that
form, 0 = none; addresses in the block's bank); then the sets, each laid out like the game's table (16 x u16 offset from
the set, u16 count, then the item ids). The code tries the set of the monster's form, then its all-forms set; the first
whose list for the tier has items gives a random item of it (`JSL $80:8299` as the game does) and the code returns to
the RTS at `$82:C576` (it changes the JSL's return address to `$82:C575`). With neither it does the 6 bytes it replaced
and returns to `$82:C558`, so the game draws from the shared lists. It uses DP `$54` as the game does. The game's other
bytes stay, so the shared lists are edited as before; the hook goes in only when a capsule first gets its own lists, and
is refused when the 6 bytes or the RTS aren't the US game's.

Capsule monsters page, Items they ask for: "All capsules (the game's lists)" or a capsule, then "All its forms" or form
1-3 (forms 4 and 5 are never fed for points, so never ask). A tier left empty uses the all-forms list, then the game's.
Copy the game's lists starts a set as a copy; Use the game's lists removes it.

Checked in BizHawk (2026-10-27): Jelze all forms tier 0 = Frock, Jelze form 2 tier 4 = Dagger, Flash all forms tier 0 =
Dagger (the game's lists unchanged: tier 0 Long knife, tier 4 Buster sword), made in the Lufia Forge window:
* Jelze joins: it asks for Frock; two Daggers (16 points each) take it to form 2, pickiness 4, and it asks for Dagger
  (its form 2 list).
* Same ROM, Flash joins instead: it asks for Dagger; in form 2 (no list of its own for tier 4) it asks for Buster
  sword (the game's list).

Graphics: battle sprite = packed file **0x194 + record index** (`$82:CFA7`: `ADC #$0194`, `JSL $80:8E9D`),
files 0x194-0x1B6 (monster sprites follow at 0x1B6 + 1-based id). Palette: 32 bytes at **`$97:D258` + 0x20 *
record index** (file 0xBD258..0xBD6B7), copied by `$82:CF72`.

## 7. RAM

| Address | Type | Meaning | Evidence |
|---|---|---|---|
| `$7E:11A3` | u8 | active capsule 0-6 | `$82:C38B` |
| `$7E:11A4` | u8 | active form 1-5 | `$82:C38B`, `$82:C7F5` |
| `$7E:11A5` | u8 | bitmask: craved item chosen for monster n | `$82:C577`, `$82:C784` |
| `$7E:11A6+n` | u8[7] | pickiness (minimum price tier) | `$82:C4F4`, `$82:C6F4` |
| `$7E:11AD+2n` | u16[7] | craved item id | `$82:C504`, `$82:CC16` |
| `$7E:11BB+n` | u8[7] | 0 = not owned; bits 0-2 highest form reached (1-4); bit 3 = in form 5 | `$82:C482`, `$82:CC3C`, `$82:E7C5` |
| `$7E:11C2+n` | u8[7] | feeding meter 0..0x20 | `$82:C492`, `$82:CBBD`, `$82:D177` |
| `$7E:11CA+2n` | u16[7] | learned slots, bit 3*(form-1)+slot | `$82:C4A2`, `$85:C1A1` |
| `$7F:F180+5n` | char[5] x7 | capsule names | `$82:C2E7`, `$82:C42B` |
| `$7F:F1A3+n` | u8[7] | levels | `$82:C40A`, `$82:E7CC` |
| `$7F:F1AA+3n` | u24[7] | EXP | `$82:C416` |
| `$7E:10DF` | block | active capsule as a party member: +0x00 name, +0x0E level, +0x25 max HP (`$1104`), +0x2D..+0x35 the five other stats shown on level up, +0x45 record ptr, +0x47 bank 97, +0x48..+0x4E four script offsets, +0x51.. base stats, +0x5F EXP u24, +0x62 next EXP u24, +0x65 record index, +0x86/+0x88 ATP/DFP | `$82:C3F8`, `$82:C443`, `$82:C261`, `$82:D283` |
| `$7E:0A7F` | u8 | 7 when the capsule slot is in use, FF at new game (guess) | `$82:C263`, `$82:C2F7` |

`$82:C3F8` loads the save arrays into the block, `$82:C443` stores them back, `$82:C2AE` is the new-game init.
Level up: `$82:CD83` (RTL; stat gains in `$0A38..$0A3E`).

## 8. Code index

| Address | Role (my names) |
|---|---|
| `$82:C38B` | GetCapsuleRecord: X = `$97:DCB8` record for (`$11A3`, `$11A4`), also stored in `$09C4` |
| `$82:C3B1` | GetCapsuleAttackName: X = string in bank $A5 for name id A |
| `$82:C3C4` | A = record index (monster*5 + form - 1) |
| `$82:C3D3` | save-array indices (`$09D9` n, `$09DB` 3n, `$09DD` 5n) |
| `$82:C3F8` / `$82:C443` | load / store the active block from/to `$7F:F180..` |
| `$82:C261` | (RTL) copy record script pointers into the block |
| `$82:C2AE` | new-game init |
| `$82:C4B3` | name id of attack slot A (base or learned) |
| `$82:C554` | draw a wished item of tier A from `$95:FF16` (Lufia Forge hook for own lists) |
| `$82:C647` | fruit check (CLC with A=0 own fruit, A=1 Magic fruit) |
| `$82:C683`.. | feed item |
| `$82:C78E` | evolve |
| `$82:CBD2` | feeding gain |
| `$82:CC3C` | can be fed normally? |
| `$82:CC58` | form-5 fruit handling |
| `$82:CD1F` | (RTL) battle: maybe learn an attack |
| `$82:CD83` | (RTL) level-up check |
| `$82:CE52` | next-level EXP |
| `$82:CF9D` | load sprite (file 0x194+idx) and palette |
| `$82:D098` | status page (names, form/type, 3 attacks) |
| `$82:D283` / `$82:D31C` | stats from record / growth sum |
| `$82:E7B0` | capsule joins (`$11BB[n]` = 1, level from E4C4) |
| `$81:F476` | (RTL) attack id -> record in `$97:F63B` |
| `$81:AD3B` | battle action 0x0B (capsule attack) |
| `$85:B652` / `$85:B8F7` / `$85:BD5E` | L2BASM ops 3F / 40 / 3E |

## 9. Open questions

* Meaning of `42 25/26/27 00` and regs $80/$81/$82 in the AI scripts (obedience / mood / flee test?).
* Firebird's AI has `06 80 00 00 44 00` (back to 0x44) where Red Dragon has `... 6C 00` (physical attack):
  possible original data bug.
* Record byte 0x0C and words 0x23 / 0x29 are always 0.
* `$0B89` is the fed item's price (record +5, loaded by `$81:F1C5`; checked in the game).
* `JSL $85:95C6` assumed to show the attack name; animation byte semantics unverified.
* `$0A7F == 7`; the text at `$8E:C85E` that prints form/type is not decoded (form letters S/M/L/H/? not seen).
* Only feeding (section 6) was checked in the running game.
