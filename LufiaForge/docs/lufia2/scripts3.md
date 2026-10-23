# Lufia II (US) third script interpreter ($80:CC35): dungeon puzzle scripts

Static analysis only (no emulator). ROM: scratchpad\l2\lufia2.sfc. Tools (all under l2research\s3\):
`dump.py` (every handler, flow-followed, -> `all.txt`), `count.py` (counts script bytes each handler consumes,
following subroutines), `loopchk.py` (finds loops that read script bytes), `parse3.py` (parser built from the table
below + per-map crawler), `names3.py` (short opcode names), `dumpall.py` -> `all_scripts.txt` (every script listed).

**Result: 146 map blocks, 3007 distinct scripts (22172 commands) parse with 0 errors.** Byte coverage of the whole
data area 0x782D6-0x83E63 (48014 bytes) is 99.3%; the 314 bytes not reached are dead code (93 small gaps:
unreachable `0A xxxx` gotos and `00` after a goto, `27 FF 9C 01` after an endless loop, one 56-byte unused script
at 0x7C666). One script (map BC, 0x81E8B) is a "switch" (`35 A1 00 xxxx` .. `35 A1 0A xxxx`) with no default: it
would fall off into the next map's block, so its last case is assumed always taken; the crawler stops at the block
end.

rommap.md's "$80:E58B, ~194 opcodes" was a misread: `$80:E58B JSR ($E59E,X)` is a helper (see 1.4) and the
real dispatcher is `$80:CC3F JMP ($E5A4,X)`. The table at `$80:E5A4` has **0xBF entries (opcodes 00-BE)**; it ends
exactly where code resumes at `$80:E722`.

## 1. What it runs

### 1.1 Role
It runs the **per-map puzzle / dungeon-object scripts** (LEdit calls the data "Puzzles", l2ref 4.8): switches
and pressure plates, doors that open/close, pushable/movable objects, bomb/sword/arrow/hammer spots, tile
changes (bridges, walls), small object animations, warps out of puzzle rooms. Evidence:
* the scripts are looked up per map (`$05AC` = current map) and triggered by the map-info lists for **bomb spots
  ($1A), arrow spots ($1E), sword spots ($1C), action-button spots ($20), hammer spots ($2A)** (l2ref 4.4 list
  names), plus movable objects (list $28, loaded into the object tables by `$80:EA5B`);
* the commands test the collision map `$7E:4000` bits 01 (character) / 08 (object), open/close doors (map-info
  list $02), copy tile blocks, change map tiles ($91:B8B5 sets), move objects and party members.
They are not cutscene/event scripts (those are the `$80:9D32` event interpreter), battle animations, menus or the
title/ending.

### 1.2 Data layout
* **Map table `$8F:8000`** (file 0x78000): 3 bytes per map, `u16 lo, u8 hi`; block = `$8F:8000 + lo` in bank
  `$8F + hi` (code `$80:E844`: base = lo + $8000, bank = hi + $8F). File offset = `0x78000 + hi*0x8000 + lo`.
  `FFFFFF` = no block. **146 of the 242 maps have a block** (maps 05..E5; list in all_scripts.txt). Entries past
  0xF1 are other data.
* **Block** (all offsets inside are u16 relative to the block start, normalised across the LoROM bank boundary by
  `$80:E8F4`): header of **16 u16 = one per category 00,02..1E**, each pointing to a list of
  `u8 id, u16 offset` records ended by `FF` (`$80:E78D` looks up `(category X, id)`).
  * category **00**: engine events, id = trigger number (see 1.3). Every block has id 00 and id 02.
  * category **02**: action-button spots (map-info list $20), id = record id of the spot the player faces.
  * category **04**: sword spots (list $1C). **06**: arrow spots ($1E). **08**: bomb spots ($1A).
    **0E**: hammer spots ($2A). (Callers `$83:C38E`, `$83:F1EE`, `$83:EF32`, `$83:EC59`, `$80:E817` via
    `$80:E7FA`/`$83:B851`, which find the map-info record at the hit x,y and use its id.)
  * category **0A**: **object groups**, not scripts: the offset points to an FF-terminated byte list of object
    numbers. Operand values 80-9F mean "every member of group n-80" (`$80:E566`).
  * categories 0C, 10-1E: always empty.
  Counts: cat00 352, cat02 61, cat04 40, cat06 74, cat08 152, cat0A 63 groups, cat0E 83.
* **Jump targets** (0A, conditional jumps, spawn/call targets): u16 offset from the block start
  (`$80:D2A2`: `JSR $E8AD; ADC $7FD194; JSR $E8F4`).
* A second per-map table `$91:B8B5` (pointer per map in `$7FD1A0`) holds the tile-change sets used by op 8F
  (3-byte records, `id & 1F`, FF end).

### 1.3 Who starts it
* `$80:E722` (A=X category offset, Y = id*2): finds a free thread (8 slots), sets state `$81`, stores the
  script pointer, clears its result/params, records the trigger x,y (`$8F/$91`) as the thread's anchor.
  `$80:CBAE` runs all threads once (called every field frame and right after starting one).
* Category-00 ids and their callers (meaning of most is a guess from context):
  id 00 `$83:8233`, `$83:85A5`, `$83:B4F2`, op 63 (re-check of the room; run from the field loop when
  `$7FD0A3` != 0 and after warps); id 01 `$83:BE21`; id **02 `$80:EADC` = room set-up** (run by `$80:EA5B` when the
  map loads, after the movable objects from list $28 are put in the object tables); id 03 `$80:E7DF` (from
  `$83:D782`, stepping on tile type 9); id 04 `$83:F094`; 05 `$83:EAAD`; 06 `$83:8456`; 07 `$83:C384`;
  08 `$83:EF3C`; 09 `$83:E268`; 0A `$8E:B2A7`; 0B `$83:C2A8`; 0C `$83:EC1C`; 0D `$83:EDB9`; 0E `$83:F7F2`;
  0F `$83:B663`; 10 `$83:852C`; 11 `$8E:B026`. ($83:EAAD/$83:E268 are handlers of another command table at
  `$83:F328`, i.e. event-type commands can fire puzzle events.)

### 1.4 Interpreter core and RAM
* `$80:CBAE`: for each thread X = 0..7 with `$7FD18C,X` bit 7 set: decrement the wait counter (low 7 bits); when
  it reaches 0 load pointer `$7FD134 + 3X` (u16) / bank `$7FD136 + 3X`, `$A7` = X, run `$80:CC35`.
* `$80:CC35`: `JSR $E8B9` (read byte; `$E8B9` = `LDA $0000,Y; INY`, bank+1 and Y = $8000 on wrap), `ASL`,
  `JMP ($E5A4,X)`. Handlers end with `BRL $CC35` (next command, same frame) or `RTS` (stop).
  `$80:E8AD` = read u16; `$80:E8D0` = un-read one byte.
* Yield: op 11 n (and the "wait until" ops 5F/68/6B, which un-read themselves and re-run next frame) store the
  pointer and set `$7FD18C,X = $80 | n`. End ops store 0 in `$7FD18C,X` (thread free).
* `$80:E566` (the `JSR ($E59E,X)` that rommap.md saw): runs one of three helpers (table `$80:E59E` =
  `$E15A`, `$E458`, `$CD88`) for every member of object group n (category 0A) and ORs the results; it reads
  the group list, not the script.
* RAM (all bank $7F):
  | Address | Use |
  |---|---|
  | `D18C+t` | thread state (bit7 active, low bits wait frames) |
  | `D134+3t` / `D136+3t` | thread script pointer / bank |
  | `D14C+t` | thread result: bit7 = last test true, bit0 = "changed" (test differs from the stored flag) |
  | `D15C+t +8k` | thread variables FB..FE (FB = last tested operand; FC-FE = parameters from spawn/call lists) |
  | `D17C+t` / `D184+t` | thread anchor x / y (FB as a position = the trigger's own spot) |
  | `D100..D133` | puzzle flags (bit n = byte `D100 + n>>3`, mask `$80:BE45`); `D10B.7` set by op 9F, `D133` by A7 |
  | `D074+n` | byte variables (ops 2F-40, 5E, 9C, 9D) |
  | `D1A3/D1E3/D223/D263 + r` | 32 rectangle registers E0-FF: x1, y1, x2, y2 |
  | `D194/D196` | current block base (u16 + bank); `D197/D199` current pointer while looking up |
  | `D466..` | call stack for A9/AA (10-byte frames: tag, return address, bank, saved FB-FE, result) |
  | `D4E6+t` | call depth per thread |
  | `D69C/D6CC/D6FC/D75C + i` | movable objects: x, y, tile, flag 80 = present (from list $28) |
* **Operand kinds** (resolved by `$80:E9BC` / `$E9ED` / `$EA09` / `$E92A`):
  * `f` flag number: FB-FF = thread variable, else literal (bit `D100 + f>>3`).
  * `p` place: **00-5F** = position record from map-info list $22 (id, x, y) (`$80:E912`); **60-7F** = area record
    from list $24 (id - 60: x1,y1,x2,y2) (`$80:E92A`); **80-9F** = object group n-80 (category 0A);
    **A0-BF** = the value of variable `D074+n` (then resolved again); **E0-FF** = rectangle register n-E0;
    **FB** = the thread's anchor spot; FC-FE = thread parameters.
  * `v` variable number `D074+v`; `b` plain byte; `w` u16 block offset; `[..FF]` FF-terminated byte list.

## 2. Opcode table

Args = bytes after the opcode. "goto w" = jump to block + w if the condition holds, else skip w (handlers
`$80:D2A2` / `$80:D2B2`). Conf: **H** length from code and confirmed by the 3007-script parse; **C** length from code
only (opcode never used in the data); meaning confidence in brackets where it is a guess. Uses = count in data.

| Op | Handler | Args | Meaning | Uses | Conf |
|---|---|---|---|---|---|
| 00 | CC42 | 0 | end thread (also 07 2C 2D 2E 56 62 93 9B AC AD, same handler) | 1882 | H |
| 01 | CC4A | f w | if flag f set goto w | 619 | H |
| 02 | CC78 | b | door b: open (animated; `$83:8AC9` state, `$83:8AD5` or `$83:F559` with 00) [M] | 178 | H |
| 03 | CCA7 | b | door b: close (animated, `$83:8AE5`/`$83:F559` with 40) [M] | 45 | H |
| 04 | CD23 | p m w | test collision bits (m + $10) at p (groups: OR of members); if any set goto w | 15 | H |
| 05 | CD1D | p m | same test, result + "changed" into the thread result (see E3D9 below) | 15 | H |
| 06 | D03D | p b | tile-block operation at p with argument b (`$80:D0AC`/`$D0BA`: `$83:8B40` load block b, redraw) [L] | 2452 | H |
| 08 | D274 | f | set flag f | 906 | H |
| 09 | D28A | f | clear flag f | 429 | H |
| 0A | D2A2 | w | goto w | 932 | H |
| 0B | D2BB | b | sound effect b (`$84:8775` -> `$80:953B` APU) | 951 | H |
| 0C | CC61 | f w | if flag f clear goto w | 892 | H |
| 0D | D2C5 | b w | if `$0692` == b goto w (`$0692` = map/area id) [M] | 153 | H |
| 0E | CDD2 | p b | like 06 with `$AE` = FF [L] | 168 | H |
| 0F | D2E1 | p w | `$83:FB51` at p; if bit0 goto w [L] | 56 | H |
| 10 | D30D | 0 | `$83:8E66`, `$1273` |= 3 (refresh map) [L] | 6 | H |
| 11 | D319 | n | wait n frames (yield) | 960 | H |
| 12 | E0CD | p w | occupancy test of p (`$80:E132`/`E147`/`E153` by kind); if false goto w | 207 | H |
| 13 | E105 | p | same test -> result/changed | 383 | H |
| 14 | E3A0 | p w | if flag p set goto w; else test char/object (mask 09) on p, if none goto w | 25 | H |
| 15 | E344 | p | test char or object (`$7E:4000` & 09) on p -> result/changed | 178 | H |
| 16 | E34E | p w | same test; if false goto w | 140 | H |
| 17 | E177 | p | test object (& 08) on p -> result/changed | 128 | H |
| 18 | E181 | p w | same; if false goto w | 30 | H |
| 19 | E4EB | w | if result false goto w | 68 | H |
| 1A | E50B | b | run the map-info entry b of list $06 (warp/exit) via `$80:BFAA`, `$83:B76E` [M] | 131 | H |
| 1B | E52A | a b | write b to PPU register `$2100 + a` | 0 | C |
| 1C | E538 | a b | screen effect: `$1261` |= 4, `$7FD07E` = 5, `$7FD07F` = a, `$7FD080` = b [L] | 76 | H |
| 1D | E554 | 0 | `JSL $80:B97E` [L] | 84 | H |
| 1E | E4D8 | wA wB | if not changed goto wA; else if result false goto wB (two jump targets) | 516 | H |
| 1F | D087 | x y b | tile-block op at literal x, y [L] | 0 | C |
| 20 | E09D | [ids FF] w | result = FF; for each party/actor id (+4F, `$80:BF92`): if `$0736` bit 20 clear -> result 0; then if result false goto w [M] | 20 | H |
| 21 | D336 | x y | tile op at literal x, y (`$80:D357`) [L] | 0 | C |
| 22 | D346 | p | tile op at p (`$80:D357`) [L] | 138 | H |
| 23 | D439 | b | player actor: action (`$83:C1A5[$0692]` + b) via `$83:D350`, sets walk flags [L] | 13 | H |
| 24 | D4E0 | x y o | place object o at literal x, y (`$83:DF87`, `$83:E018`) [L] | 0 | C |
| 25 | D4EC | p o | place object o at p [L] | 102 | H |
| 26 | D6CA | [params FF] w | if result bit0 (changed) spawn a thread at w, else skip | 582 | H |
| 27 | D6E9 | [params FF] w | **spawn**: new thread at w, params -> its vars FC.. (FB-FF in the list = this thread's vars); the new thread runs at once | 562 | H |
| 28 | E4A1 | b | door b: open if result true, else close (jumps into 02/03) | 33 | H |
| 29 | E421 | 0 | store result bit7 into flag (thread var FB = last tested operand) | 509 | H |
| 2A | D3D6 | p b | add movable object at p with tile b (`D69C..`), draw it (`$83:8B40`, `$83:F422`, `$83:F86B`) [M] | 79 | H |
| 2B | E4F9 | w | if result is not (changed and false) goto w | 2 | H |
| 2F | D924 | v x | var v = value x (x resolved: A0-BF = another var) | 59 | H |
| 30 | D8D1 | v b | var v += b | 79 | H |
| 31 | D8E0 | v b | var v -= b | 6 | H |
| 32 | D8F2 | v | var v += 1 | 232 | H |
| 33 | D906 | v | var v -= 1 | 276 | H |
| 34 | D91A | v b | var v = b | 569 | H |
| 35-3A | D93B.. | v b w | if var v ? literal b goto w: 35 ==, 36 !=, 37 >, 38 <, 39 >=, 3A <= | 969 | H |
| 3B-40 | D98E.. | v x w | same six compares (3B ==, 3C !=, 3D >, 3E <, 3F >=, 40 <=) against a resolved value x | 9 | H(3C,3E)/C |
| 41-54 | D9FC | r [b] | rectangle register r (E0-FF): 41-45 x1, 46-4A y1, 4B-4F x2, 50-54 y2; sub-op (op-41)%5: 0 `= b`, 1 `+= b`, 2 `-= b` (2 args), 3 `++`, 4 `--` (1 arg) | 445 | H |
| 55 | DA82 | r p | rect r = p as raw 4 bytes (`$80:E92A`) | 0 | C |
| 57 | DB1E | 0 | nop | 0 | C |
| 58 | DB21 | s dx dy a b | per-layer scroll/camera move: `$7FD0CE/D0D6 + 2s` += dx/dy, speeds a/b, `$1261` |= 40 [M] | 32 | H |
| 59 | DC0D | 0 | finish/reset that scroll (copies `$121E..` back) [M] | 20 | H |
| 5A-5D | DCAC.. | o b | object o (`$7FD0BE`) face/turn: direction 04/00/02/06 (`$80:DCDA`) [L] | 15 | H |
| 5E | E078 | v p | var v = number of the movable object at p, FF if none (`$83:FB9B`) | 99 | H |
| 5F | DD8F | o | wait until movable object o has stopped (re-runs itself) | 13 | H |
| 60 | DDB5 | a | actor a (+4F) action `$8E:BB17` [L] | 35 | H |
| 61 | DDCC | a | actor a (+4F) action `$8E:BAF6` [L] | 34 | H |
| 63 | DDE3 | 0 | start category-00 id-00 script as a new thread, end this one | 17 | H |
| 64-67 | DDF2.. | p a s | actor a moves to p, direction 0/1/2/3 (`$80:E004`, `$83:D350` cmd 4C+d), speed s [M] | 564 | H |
| 68 | DD86 | o | wait until actor o has stopped | 269 | H |
| 69 | DA98 | r p | rect r = p (x1,y1,x2,y2) | 595 | H |
| 6A | D47E | b | player (actor 0) action b (`$83:D350`) [L] | 203 | H |
| 6B | D4CA | 0 | wait until the player has stopped | 0 | C |
| 6C | D2EE | p w | `$83:FB51` at p; if bit0 clear goto w [L] | 21 | H |
| 6D | E0D3 | p w | occupancy test of p; if true goto w | 309 | H |
| 6E | E358 | p w | if char or object on p goto w | 22 | H |
| 6F | E18B | p w | if object on p goto w | 69 | H |
| 70 | CD32 | p m w | collision bits test; if none goto w | 22 | H |
| 71 | D2D3 | b w | if `$0692` != b goto w | 15 | H |
| 72 | E198 | p | test character (& 01) on p -> result/changed | 0 | C |
| 73 | E1A2 | p w | same; if false goto w | 0 | C |
| 74 | E1AC | p w | same; if true goto w | 5 | H |
| 75 | E1B9 | b | result = (`$7FD0A1` bit0 and `$7FD0F4` == b): last tool/attack hit id == b [L] | 0 | C |
| 76 | E1BF | b w | same test; if false goto w | 0 | C |
| 77 | E1C5 | b w | same test; if true goto w | 2 | H |
| 78 | D516 | p b x | `$80:D533` (move/slide something from p, b, value x) [L] | 314 | H |
| 79 | D5A5 | b | `$7FE316` = b (object speed) [L] | 55 | H |
| 7A | CDD9 | p b | tile-block op (load block b at p, redraw) [L] | 374 | H |
| 7B | D5AF | 0 | `JSL $8E:BD77` [L] | 12 | H |
| 7C-7F | DED3.. | p a s t | as 64-67 plus `$7FE316,a` = t | 78 | H |
| 80 | DEEF | p a s t | move with no direction (cmd 5C) | 6 | H |
| 81 | DE11 | p a s | move, direction 3, cmd 5C | 0 | C |
| 82 | E038 | r o | rect r = 1x1 cell of movable object o (if present) | 22 | H |
| 83 | DAC9 | r b | rect r.x2 = r.x1 + b | 62 | H |
| 84 | DAD7 | r b | rect r.y2 = r.y1 + b | 32 | H |
| 85 | DAF2 | p b | warp destination: `$05BD/$05BE` = p, `$05BF` = b, `$7FD0BF` = FF [M] | 8 | H |
| 86 | DB11 | 0 | start that warp (`$05B5` |= 80, `$05B3` |= 08) [M] | 18 | H |
| 87 | E1EE | m p p | compare the tiles at two places (mask m of BG layers, `$7F:0000` & 3FF) -> result | 0 | C |
| 88 | E1F4 | m p p w | same; if false goto w | 4 | H |
| 89 | E1FA | m p p w | same; if true goto w | 0 | C |
| 8A | DE32 | o | actor o: action 5D, `$83:F7D4` [L] | 0 | C |
| 8B | DE52 | o | actor o: stop/hide (`$0622` |= 4, `$05D2` = FF, bit 7 off) [M] | 42 | H |
| 8C | D5BC | 0 | redraw the player sprite (`$83:BB93`, `$83:A21A`) [L] | 4 | H |
| 8D | CE1B | mode p r | copy a tile rectangle (mode -> `$7FD05F`, src p, rect r) and redraw [M] | 4 | H |
| 8E | CE33 | mode p p w h | same with explicit width/height | 9 | H |
| 8F | D5F2 | b | apply tile-change set b from `$91:B8B5` (two states by a flag in `$093B`) [M] | 65 | H |
| 90 | D01D | r | tile op on rectangle r [L] | 0 | C |
| 91 | D051 | p b | variant of 06 (`$80:D112`) [L] | 0 | C |
| 92 | CE27 | mode p r | variant of 8D (`$80:D112`) [L] | 0 | C |
| 94 | E277 | p b | tile compare 2 (`$80:E28C`) -> result [L] | 0 | C |
| 95 | E27D | p b w | same; if false goto w | 38 | H |
| 96 | E283 | p b w | same; if true goto w | 45 | H |
| 97-9A | CED9.. | a | actor a (+4F): one step down/left/up/right (`$7FE5A6/E5CE`, `$83:D416`) [M] | 2 | H(97)/C |
| 9C | CF3A | v p | var v = tile/attribute at p (`$83:F984`) [M] | 27 | H |
| 9D | CFD8 | v r k | var v = byte k of rect register r (k = 0/40/80/C0 for x1/y1/x2/y2) | 28 | H |
| 9E | CFEA | v r k | that byte = var v | 0 | C |
| 9F | CF63 | p p | `D10B` bit 7 = (the two places are the same cell) | 2 | H |
| A0 | CF9D | b | door b open, no animation (`$83:8AD5`) [M] | 3 | H |
| A1 | CFAA | b | door b close, no animation (`$83:8AE5`) [M] | 6 | H |
| A2 | CFB7 | 0 | `$05B5` |= 20 [L] | 1 | H |
| A3-A6 | DEAA.. | p o s t | move object o to p (direction 0-3), speed s, `$7FE316` = t | 46 | H |
| A7 | CFBF | p | `$7FD133` = `$83:FB71` at p (tile type) [L] | 6 | H |
| A8 | D065 | p x | tile-block op, b resolved [L] | 1 | H |
| A9 | D79B | [params FF] w | **call** w (pushes return address, saves FB-FE and result; params -> FC..) | 688 | H |
| AA | D849 | 0 | **return** from A9 (restores vars/result) | 175 | H |
| AB | D8C8 | 0 | `$7FD0BF` = FF [L] | 2 | H |
| AE | D4B0 | b | actor `$7FD0A2` action b [L] | 1 | H |
| AF-B2 | DF69.. | p o s | actor o walks (direction 0-3) to p, speed s (cmd 74+d, `$0736` |= 10) [M] | 120 | H |
| B3 | DF40 | p o s | actor o to p, `$83:F5EA` (jump/teleport?) [L] | 26 | H |
| B4 | DB6A | dx dy b | camera target = player + (dx, dy) ($7FD08B/8D), `$05A8` = b, `$1261` |= 8 [M] | 21 | H |
| B5 | DBCA | 0 | camera release (`$1261` &= ~8) [M] | 15 | H |
| B6 | DBDD | 0 | player sprite flag 02 on (`$83:FA12`) [L] | 4 | H |
| B7 | DBF5 | 0 | player sprite flag 02 off (`$83:FA3F`) [L] | 2 | H |
| B8 | D5E4 | 0 | `$72` |= 1 [L] | 4 | H |
| B9 | D5EB | 0 | `$72` &= ~1, then as 8C | 4 | H |
| BA | D686 | t p | move map-info treasure record t (list $26) to p | 8 | H |
| BB | D6B4 | b | `$099C` &= ~80, start event/text b (`$83:B727`, X = 4) [M] | 53 | H |
| BC | CEA0 | a p | actor a (+4F) goes to p (`$80:A74D`) [M] | 2 | H |
| BD | CECF | b | `JSL $83:E033` with b [L] | 0 | C |
| BE | DB9D | dx dy | scroll the map view by dx, dy from the player (`$83:8D42`) [L] | 1 | H |

Notes:
* Compare ops 35-40 (`$80:D9F0` reads `var, value`): code is `CMP var` with BEQ/BNE (35/36), BCC/BCS on
  the value (37 jump if var > b, 3A if var <= b) or on value-1 (38 var < b, 39 var >= b). A value of 0 in 38/39
  wraps to FF (unsigned compare). Length H, comparison direction read from the code (M until tested).
* "result/changed" (`$80:E3D9`): result bit 7 = test true; bit 0 = the test differs from the stored flag whose
  number is the tested operand. 29 then writes bit 7 into that flag. Typical switch pattern:
  `17 p` (object on plate?) `1E wA wB` (unchanged -> done; became false -> close) `28 door` `29` `00`.
* Triplets: tests come as `X` (store result), `X+1` (goto if false), `X+2` (goto if true): 72/73/74, 75/76/77,
  87/88/89, 94/95/96; 13/12/6D, 15/16/6E, 17/18/6F.
* Opcodes 41-54: one handler, `X = op*2`; `(op-41) / 5` picks the register field (table `$80:DA4B` = D1A3, D1E3,
  D223, D263), `(op-41) % 5` the operation (table `$80:DA53`).
* 5F, 68, 6B un-read their bytes (`$80:E8D0`) and wait 1 frame while the object/actor still moves.
* Never used in the data (lengths from code only, C): 07 1B 1F 21 24 2C 2D 2E 3B 3D 3F 40 41 46 4B 4C 4D 50 51
  52 55 56 57 62 6B 72 73 75 76 81 87 89 8A 90 91 92 93 94 98 99 9A 9B 9E AC AD BD.

## 3. Parse result
* Entry points: 352 + 61 + 40 + 74 + 152 + 83 list records (cat 0A = 63 groups parsed as byte lists), plus every
  jump, spawn (26/27) and call (A9) target. 3007 distinct scripts, 22172 commands, **0 errors** (bad opcode,
  runaway list, or > 16 KB script); the one fall-through (map BC 0x81E8B) is described at the top.
* Group members are all 00-27 (movable-object or area numbers).
* Full listing: `l2research\s3\all_scripts.txt`. Parser + crawler: `python -I s3\parse3.py` (prints the summary,
  writes `s3\parsed.pkl`); `python -I s3\dumpall.py` writes the listing.

## 4. Confidence and open points
* Argument lengths: high (all 191 handlers traced with subroutines; fetch loops only in 20, 26, 27, A9 (FF lists);
  confirmed by the data for every opcode that occurs).
* Category meanings 02/04/06/08/0E: high for the mechanism (map-info list offsets $20/$1C/$1E/$1A/$2A), list names
  from LEdit. Category-00 trigger ids other than 02: mostly unknown.
* Meanings marked [L] are from the RAM/routine touched only; nothing was checked in the game. Worth a trace:
  06/0E/7A/A8 (tile-block ops, the most used command), 64-67/7C-7F/A3-A6/AF-B2 (which arg is the actor),
  78, 8D/8E, 95/96.
