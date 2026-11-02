# Lufia II: an 8 MB ROM

Lufia II is a 2.5 MB LoROM. Lufia Forge grows it to 3 and then 4 MB by itself as its added data needs room
(`L2Rom.Allocate`). 4 MB is the most a plain LoROM cartridge addresses; **Lufia II > Settings > Make the ROM 8 MB**
(`L2Rom.ExpandTo8`) adds 4 MB more.

## 1. How it works

* An 8 MB LoROM is wired as "ExLoROM" (bsnes `boards.bml`: `EXLOROM-RAM`): banks `$80-$FF` (addresses `$8000-$FFFF`)
  read the first 4 MB as before, banks `$00-$7D` read the upper 4 MB (file 0x400000 + bank x 0x8000).
* The game runs in `$80-$FF` (FastROM), so it doesn't move. Only the console's start and its interrupts go through
  bank `$00` (reset `$00:8000`, NMI `$00:862D`), so file 0x400000-0x407FFF holds a copy of the first bank, header
  included. `RomBuffer.FixChecksum` (run on every save) renews the copy, so changes to the first bank follow; the copy
  is also what marks such a ROM (`RomBuffer.IsExLoRomCopy`).
* The header's size byte says 8 MB (`0D`); the map mode stays `30`.
* Measured in BizHawk (a read hook on banks `$00-$7D`, boot, opening, walking, menus): the game reads nothing there but
  bank `$00` (its vectors and interrupt code), and 24 reads of `$60:CF7E` in the opening that land in empty space on
  any size of ROM.
* **Battle window frame (found 2026-11-01).** A battle read bank `$01` too: the frame helpers `$81:E542` and `$81:E570`
  read the frame pieces (`$81:87B7-`) with `LDA $01:0000,X`, `$01:0002,X` and `$01:0004,X` (`$81:E548`, `E54F`, `E55D`,
  `E570`, `E580`). On 4 MB that is bank `$81`; on 8 MB it is the upper 4 MB, empty or holding events, so the hero's
  status window in battle lost its blue border (or showed garbage when events were there). Lufia Forge points the five
  reads at bank `$81` (`L2Rom.FixLowBankReads`): when it makes a ROM 8 MB, and when it opens an 8 MB ROM made before
  (one Edit, Undo step). Every long address in the decompiled code was checked: these are the only reads of banks
  `$01-$7D` (the others are SRAM, `$70:xxxx`). With the fix, a battle on 8 MB (the opening's script battle, menus
  opened) reads nothing from the upper 4 MB and every screenshot matches the 2.5 MB game's (BSNESv115+).

## 2. What goes in the upper 4 MB

| Banks (file) | What | Why there |
|---|---|---|
| `$01-$3F` (0x408000-0x5FFFFF) | Event space: map event blocks and start-up scripts (`L2EventSpace`), puzzle blocks (`L2PuzzleBlock`, the table's bank is `$8F` + a byte, added in 8 bits at `$80:E891`) | Scripts run with the data bank set to their own bank and reach RAM `$0000-$1FFF` through it, which banks `$01-$3F` mirror like `$80-$BF`. The map header names the bank as `$87` + a byte, added in 8 bits (`$83:B5BB`, `$80:BEBA`: `ADC #$87`), so it wraps to `$01-$3F`. Empty banks are taken first, so no packed files have to move. About 2 MB. |
| `$40-$7D` (0x600000-0x7EFFFF) | Data read through a full 24-bit address: new map sprites' tiles (`L2Sprites`), map info blocks (`L2MapInfo`, `L2MapEdit`) | No RAM there, so no scripts or code. About 2 MB; the last 16 bytes hold "LF2U" and the next free offset. |

Everything else Lufia Forge adds (hook code, packed files, tables the game reads with its own bank arithmetic) stays
in the first 4 MB. The Settings page shows what is free in each part.

`L2Rom.SnesToFile` reads banks `$01-$7D` (upper halves) as the upper 4 MB, and `L2Rom.FileToSnes` gives upper 4 MB
offsets their `$01-$7D` address.

## 3. Where it runs

* Snes9x (BizHawk's Snes9x core), bsnes / higan and BizHawk's "BSNESv115+" core: yes.
* BizHawk's older "BSNES" core (the one in Config > Preferred Cores > SNES named just BSNES): no. It maps banks
  `$00-$7D` like a 4 MB ROM and reads `$C0-$FF` as nothing, and the game stops at the Natsume logo. 4 MB ROMs are fine
  there.
* Real cartridges and flash carts: not tried.

## 4. Checked in the game (2026-10-27)

An 8 MB ROM made from the clean US ROM (`ExpandTo8`), with 5 new map sprites (tiles at `$40:8000`), three of them
given to new characters next to the start on map 03 (the map's info block moved to `$40:9E00`, its start-up script
and event block to `$01:8008` / `$01:8097`), a talk event on one of them, and a new game started there. In Snes9x and
BSNESv115+: the three new sprites show (map table: FB = New 1, FC = New 5, FD = New 4), and talking to the character
shows the new text. The same steps on a 4 MB ROM (everything in the first 4 MB, events in bank `$B5`) work in
BizHawk's BSNES core.

## 5. Everything checked for 4 and 8 MB (2026-11-01)

What can break when the ROM grows: the game reading the new space by accident, data Lufia Forge puts there in a bank
the game can't use it from, and the tools around the ROM.

* **The game's code** (the decompiled listings, `tools/l2decomp`): every long address, `MVN` / `MVP` bank, data bank
  set by `PLB`, bank byte stored for a `[dp]` pointer and DMA source bank. Only the battle window frame (section 1)
  reads banks `$01-$7D`. Banks the game works out by adding (packed files `$A7` + n, samples `$98` + n, event blocks
  `$87` + n, puzzle blocks `$8F` + n) are 8-bit sums: Lufia Forge's linear pointers give the right bank on every size,
  `$01-$3F` on 8 MB included.
* **Data banks.** Code that sets the data bank to where its data is and then reaches RAM or registers through it only
  works in banks `$80-$BF` (and `$01-$3F` on 8 MB): event scripts (handled by the event space) and **puzzle blocks**
  (found now: `$80:CBEB` runs every puzzle command with the block's bank, and the commands read `$0583`, `$0692`,
  `$1273`, `$06BA` and set up the multiplier `$4202` / `$4216` through it). Lufia Forge wrote puzzle blocks past
  2.5 MB until now (`$D0+`, where those addresses hold nothing); they go into the event space now and are moved there
  when a project is opened (`L2PuzzleBlock.MoveIntoReach`, one Edit, Undo step). The song upload, battle palettes,
  world map exits and the walking / object scripts switch banks too but only touch RAM with long addresses or the
  direct page, so their data works anywhere.
* **In the game** (BizHawk BSNESv115+, a read hook for every CPU read outside the 2.5 MB game and every DMA source
  bank): 94 maps started one after another (every map with a preset point and an event block, each with a start scene
  written by Lufia Forge, so on 8 MB their event blocks sit in `$01-$08`) on a 2.5, a 4 and an 8 MB ROM. All 188
  screenshot pairs are the same to the pixel; the only reads past 2.5 MB are Lufia Forge's own data (packed files it
  moved, the 8 MB event blocks). With the 142 game puzzle blocks written again past 2.5 MB, the puzzle reads above show
  up on 24 maps; written into the event space they are gone.
* **Save RAM** reads and writes the same on 2.5, 4 and 8 MB (`$70:0000-1FFF`, mirrored to `$71-$7D` and `$F0-$FF`).
* **Saving.** Every ROM write fixes the checksum and renews the bank `$00` copy (`RomBuffer.WriteSafely`); IPS export
  does too now. **IPS export** of an 8 MB ROM: a record can't start at 0x454F46, whose offset bytes read "EOF" (every
  patcher stops there and the rest of the ROM is lost); the record starts a byte earlier instead.
