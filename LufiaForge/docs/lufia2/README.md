# Lufia II (US) ROM map

What Lufia Forge knows about *Lufia II: Rise of the Sinistrals* (US, 2.5 MB LoROM FastROM, no copier header).
File offset = (bank & 0x7F) * 0x8000 + (address - 0x8000). Everything here was read from the ROM and its code;
the items marked as checked were also tested in the running game (BizHawk). The Lufia Forge source
(`LufiaForge/Core/Lufia2`) uses these addresses and says the same in its comments.

| File | What it covers |
|---|---|
| [rommap.md](rommap.md) | Whole-ROM classification: code, tables, packed files, text; code entry points and jump tables (first pass, see the corrections below) |
| [banks_8F_A6.md](banks_8F_A6.md) | The data banks $8F-$A6 range by range |
| [events.md](events.md) | The event script interpreter: all 205 commands, text codes, jumps, flags and variables |
| [scripts3.md](scripts3.md) | The dungeon puzzle interpreter: 191 opcodes, the per-map puzzle blocks |
| [battlescript.md](battlescript.md) | The battle script interpreter (monster AI, item, spell and capsule effects) |
| [characters.md](characters.md) | Heroes: starting stats, growth, the EXP curve, new-game records, RAM layout |
| [capsules.md](capsules.md) | Capsule monsters: records, names, growth, attacks, evolution |
| [music.md](music.md) | The sound driver, song format, samples, sound effects |
| [graphics.md](graphics.md) | Every packed file classified; battle backgrounds, title, fonts, sprites; map sprites, painting, new sprites and palettes |
| [settings.md](settings.md) | Game-wide values: random battle rate, walking speed, run button, sell price, level cap, gold cap, inns |

## The ROM at a glance

| File offsets | Banks | Contents |
|---|---|---|
| 0x000000-0x037FFF | $80-$86 | Code (about 74% of these banks is reached code), plus tables: event command table $80:CA14, battle script handlers $85:B483, new-game data $85:B2A1 |
| 0x038000-0x077FFF | $87-$8E | Map headers $87:8000, event blocks ("PH") and start-up scripts, the map names, menu and battle text, the dictionary $8E:EA00, capsule names $8E:D558 |
| 0x078000-0x083E63 | $8F-$90 | Puzzle scripts (map table $8F:8000) |
| 0x084000-0x087D36 | $90 | The sound driver (uploaded to sound RAM) |
| 0x088000-0x0BFFFF | $91-$97 | Map info blocks, actor animation scripts, credits text, battle animation scripts, spells $95:FA5B, monsters $96:85C0, items $96:CF69, descriptions $97:8000, growth $97:B62C, starting stats $97:B93C, encounters $97:B9AC / $97:BE93, script battles $97:C53D, capsule monsters $97:DCB8, shops $97:EE9F, capsule attacks $97:F63B, battle backgrounds $97:FD40 |
| 0x0C0000-0x0CDE6B | $98-$99 | Instrument samples (BRR, table $98:8000) |
| 0x0CDE6C-0x137FFF | $99-$A6 | Fonts ($9A:F970, $9F:8500), world-map blocks, palettes, field sprite graphics, item names $9E:C7E8, description words $9E:DDCC, capsule attack names $A5:DF00, capsule growth curves $A6:F420 |
| 0x138000-0x27E300 | $A7-$CF | Packed files (table $A7:8000, 0x2A9 files, the same LZ packer as Lufia 1): songs 00-3E, maps 41+, graphics |
| 0x27F000-0x27FFFF | $CF | Map sprite table $CF:F000, map lists $CF:F72E.., map info pointers $CF:FCBC |

The ROM is full: only about 0x14D bytes are unused FF runs, so data that grows goes into added space after
0x280000. Lufia Forge grows the ROM to 3 MB, then 4 MB, and uses the largest empty run there (the fan hacks Frue,
Spekkio and Kureji Lufia v8.2 fill 0x280000-0x30DAC2 and 0x3FFC00-0x3FFDD5 and move the monster table to $D0:A000).

## Checked in the game

- Map drawing (against video RAM), map and text edits.
- Shops: a changed item.
- Item descriptions: the X button shows an edited one.
- The new game: gold, level, EXP, and stats from the growth tables.
- A battle against an edited monster: its name, sprite, HP, EXP and gold ("Gets 500 EXP. Gets 777 GOLD.").
- A battle background redrawn through the Graphics page (mirrored, new sky colour).
- The staff roll with an edited heading (event command 5B 05 runs it).
- A song imported from MIDI (it replaced Elcid's music, and the game loaded it into sound RAM byte for byte).
- Walking in towns: the run button on R, and always running (8 tiles in 32 frames instead of 4).
- The Patch Manager's Lufia II patches: fast world-map walking (114 steps in 480 frames instead of 57), hold B to avoid battles
  (the step counter stays at 1 while B is held, and no battle can start before 6), free inns (500 gold stays 500 instead of 400),
  faster IP gauge (255 instead of 206 after the same hit).
- A ROM grown to 4 MB with a map moved past 0x300000, and Frue Lufia with a map moved into its free space: both draw the map.
- A capsule monster (event command 81 00 makes it join, at level 8): its HP, ATP and DFP follow the edited record and growth exactly.

## Corrections to the first-pass files

- rommap.md says the third interpreter is at $80:E58B with about 194 opcodes. It is at $80:CC35 (table $80:E5A4),
  with 191 opcodes; $80:E58B is a helper (scripts3.md).
- $CF:FC98 holds pointers to tile-animation records in bank $A1, not packed-file numbers (banks_8F_A6.md).
- Packed files 0x03F and 0x040 are not maps: 0x03F is the licence screen, 0x040 the credits font. Maps 0 and 1 are
  the world maps, drawn from files 0x28B/0x28C and 0x28E/0x28F (graphics.md).
- Battle script opcode 42 is a call into the shared script library at $96:FADD, and 43 returns (battlescript.md).
- LEdit's German addresses are often off for the US ROM; the files here are for the US ROM only.

Shop menu bit 0 (shop 25) is not "several shops": the game builds that list itself ($82:E433) from every item with
a count in the 4-bit table at $7F:F080, probably items sold earlier. The shop record has no data for it.

## Still open

- A few event, puzzle and battle commands are only named from what they touch (marked L / guess in the files).
- Five high-entropy blocks at bank ends in $8F-$A6 (about 0x1C00 bytes) have no reader yet.
- Which spell-effect tilemap goes with which tile file.
- What the capsule monsters' default names ($8E:D558) are used for: the naming screen starts empty.
