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

## 2. What goes in the upper 4 MB

| Banks (file) | What | Why there |
|---|---|---|
| `$01-$3F` (0x408000-0x5FFFFF) | Event space: map event blocks and start-up scripts (`L2EventSpace`) | Scripts run with the data bank set to their own bank and reach RAM `$0000-$1FFF` through it, which banks `$01-$3F` mirror like `$80-$BF`. The map header names the bank as `$87` + a byte, added in 8 bits (`$83:B5BB`, `$80:BEBA`: `ADC #$87`), so it wraps to `$01-$3F`. Empty banks are taken first, so no packed files have to move. About 2 MB. |
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
