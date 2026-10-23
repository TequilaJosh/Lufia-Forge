# Lufia II (US) event / script commands, from the game's code

Static analysis only (no emulator). ROM: scratchpad\l2\lufia2.sfc. Tools written for this:
`scratchpad\l2research\ev\flow.py` (flow-following disassembler, tracks REP/SEP), `ev\all.txt` (every handler
dumped), `ev\parse.py` (script parser using the table below), `ev\crawl2.py` (parses every map's init script
plus every PH-block script reachable through the +2/+6/+8 lists and through all jump/choice/condition
targets). **Result: 2962 distinct scripts in the 242 maps parse with 0 errors** with the argument counts below,
so the counts of every command that occurs in map scripts are confirmed by the data as well as by the code.
Commands never seen in map scripts are listed at the end of section 6; their counts come from code only.

Confidence column: **H** = argument count read from the code and (if used) confirmed by the crawl, meaning clear
from the code; **M** = count certain, meaning inferred from what RAM it touches; **L** = count certain, meaning
a guess.

---

## 1. Interpreter core

* Script pointer: bank `$09B9`, address `$09B7` (16-bit Y, data bank B = `$09B9`). Runs once per frame from
  `$80:9CCD`: B = `$09B9`, Y = `$09B7`, then the command loop at `$80:9D00`, which first does `STY $09B7`.
* Byte reader `$80:C0B7`: `LDA $0000,Y; INY`; if Y wraps to 0 it increments `$09B9`/B and sets Y = `$8000`
  (so a script may run across a LoROM bank boundary). `$80:C0D0` = two C0B7 calls = **u16 little-endian**.
  `$80:C0EC` = un-read one byte (DEY with the same bank fix-up). `$80:C0DA` = sign-extend A to 16 bit.
* Dispatcher `$80:9D32`: `JSR $C0B7; ASL; TAX; JMP ($CA14,X)`. Table `$80:CA14` (file 0x4A14) has **0xCD
  entries (commands 00..CC)**; the bytes after it ($80:CB7E) are code (`PHB PHP REP #$10 ...`), so CD..FF are not
  commands. Crash handlers (a `BRK`): 02 (`$9DD4`), 11 (`$BF0A`), 5C (`$BF08`), 5D (`$BF09`).
* Handler exits: `BRL $9D00` = continue with the next command in the same frame. `BRL $9DB0` (PLP/PLB/RTL)
  = stop for this frame **without** saving Y, so the same command runs again next frame (this is how all the
  "wait until ..." commands work; `JSR $C0EC` before it only matters for the bank fix-up). Handlers that
  want to continue after a yield store Y first (`STY $09B7`): 01, 07-text, 0C-0F, 18, 19, B4.
* Commands are executed back to back in one frame until one yields; text prints one character per frame.

## 2. Text mode

* `$099B` bit 0 = text mode. Set (`$099B = $81`, then window open via `$80:C652` measure + `$80:C23D`) by
  **08, 10, 13, 61-67, 9E**.
* In text mode (`$80:9D0E`): byte < 0x10 -> dispatched through the same table (so 00-0F are the text control
  codes); 0x10-0x7F -> printed character (`$80:BCE4`); 0x80-0xFF -> dictionary word `(b - 0x80) + 0x200`.
* Text control codes (all are table commands):
  * `00` end (returns from a dictionary word/name call if one is active, else ends text and the script; same
    handler as 42).
  * `01` end text mode, wait for a button, then continue with commands (stores Y).
  * `03` newline. `0C 0D 0E 0F` = print `?` `!` `,` `.` and newline.
  * `04 xx`: xx < 0x80: text "voice" sound = xx + 0x45 (`$1260`, played per character); xx >= 0x80: pause
    (xx & 0x7F) * 10 frames.
  * `05 xx` / `06 xx`: dictionary word xx / xx + 0x100 (table `$8E:EA00`, u16 offsets from `$8E:EA00`). This is a
    **call**: return address saved in `$1252/$1254`, the word's terminating 00 returns.
  * `07 xx`: character code 0x300 + xx (third glyph page; handler peeks the byte, the print routine consumes it).
  * `08`: (as a command) open text mode with the current speaker.
  * `09`: player name: call into RAM `$00:0BAD` (Maxim's name field), `$099B` bit 4 set while reading it.
  * `0A lo hi`: **back-reference** (text compression): continue reading at (address of the 0A byte + 1 - d),
    d = lo + (hi & 0x0F) * 256, for (hi >> 4) + 2 characters (counter `$125A`, decremented per frame at
    `$80:9CE6`), then return after the 0A command (`$1257/$1259`). Matches the map-name finding "0A = first
    (n + 2) letters of another name". Count is per printed character/frame (M).
  * `0B`: choice cursor (see command 10); ends text mode.
* Speakers: `$09AB` = actor id of the speaker; `$80:C1DF` sets the voice from it. 13 xx = speaker xx;
  61..67 = speaker **0, 1, 2, 3, 5, 4, 6** (note: 65 -> id 5, 66 -> id 4).

## 3. Jump / pointer encoding

* Every script pointer (1C, 15, 6A, 12, 10 choices, 14 condition jumps) is a **u16 offset added to the base
  `$099E` in bank `$09A0`**, then normalised by `$80:C102` (a result below $8000 means it crossed into the next
  bank: +$8000 and bank+1). So the target is the base plus the offset in **ROM-linear (file) terms**.
* For event-block scripts the base is the map's **"PH" block start** (`$83:B5AD`: base = $87:8000 + header u24
  at +0). So `file(target) = file(PH) + offset`. Verified by the crawl for every map.
* For the **map init script** (`$80:BEAF`, header u24 at +3) the code temporarily sets `$099E = $8000`
  (bank `$09A0` unchanged). All init scripts live in bank $87, and the data bank B is not switched by a jump
  until the next frame, so in practice **init-script jumps are relative to $87:8000 = file 0x38000** (e.g. map 2
  init `14 01 7B 30 64 11 FF` jumps to 0x39164; map A2 init `1C 1E 26` -> 0x3A61E). Every init script parses
  cleanly with that base and not with the PH base. (An editor should write init jumps as file - 0x38000.)
* Calls: only the text calls 05/06/09 (one level, `$1252/$1254`) and 0A (`$1257/$1259`). There is no script
  gosub command.
* Ends: 00/42 (end), B8 (end variant), 84 (halt forever on this command), 16/5E/83 (map change; the script is
  replaced, Y is not advanced), A5 (abort to the field main loop), 5B 03 (game reset), 5B 06 (hang).

## 4. State the commands use

* **Event flags**: 256 bits at `$7E:077E-$079D`; flag n = byte `$077E + (n >> 3)`, bit `1 << (n & 7)` (mask
  table `$80:BE45` = 01 02 04 ... 80). Flag **FF** (`$079D` bit 7) doubles as the result of tests 91, 92, AB.
* **Variables**: bytes at `$7E:079E + n` (1D/1E/1F, 12's counter, 14's 1x conditions).
* Gold: u24 `$0A8A` (capped at 9,999,999 = 0x98967F). Inventory: `$0A8D`, 0x60 u16 (item id = low 9 bits,
  count = bits 9-15). Character records: `$0BAD + n * 0xBE` (name +0, level +0x0E, HP +0x11, MP +0x13,
  0x24 item/equipment bytes at +0x96). Party order: `$0926` (5 bytes, FF = empty).
* Actors: 0x28 slots; id `$05FA,X`, flags `$0622,X` (01 script-held, 04 hidden, 08 moving), map sprite
  `$05D2,X`, tile x/y `$06BA/$06E2,X`, pixel x/y `$7FDDAE/$7FDE3E` (16-bit, 16 per tile). `$80:BF92` finds an
  actor by id, `$80:BF6F` the same but skips hidden ones. Ids 0x50 + n = character starts of the map info
  (list +0x10); 0..6 = party members. Ids 0x10-0x4F also have a persistent "hidden" bit at `$081E + (id - 0x10)`
  and a map/spot at `$085E/$089E + (id - 0x10)`.
* Map info (copied to `$7E:F000`, l2ref 4.4) lists looked up by id with `$80:BFAA`: **preset points** list +0x0E
  (`$80:C05C`, 3-byte records), **areas** list +0x14 (`$80:C093`), **paths** list +0x12 (`$80:BFD9`),
  **exits/warps** list +0x06 (5E/83).
* Script battles (53): table **`$97:C53D` (file 0xBC53D)**, u16 offsets relative to the table, 39 entries
  (0x00-0x26), records 0x1E bytes: 6 monster ids (FF = empty) then per-slot data (`$81:82BD`).

## 5. Command 14 (conditions) and 10 (choices)

**10 n p1..pn**: n u16 pointers follow; then text (text mode on) with the options; the text's `0B` runs the
cursor: up/down, confirm -> jump to pointer [cursor], cancel (B) -> leave text mode and continue after the 0B.

**12 v n p1..pn**: "say something different each time": i = var[v]; var[v]++; i = min(i, n - 1); jump to
p[i] (`$80:A041`).

**14 c ... FF** (`$80:A074`): a list of condition items, each starting with a byte c, ended by FF. A result
register R (`$1267`) is computed; the list continues after FF if no jump was taken.

| c | Bytes after c | Meaning |
|---|---|---|
| 00-0F, 40-4F, 80-8F | 1 (flag) | flag test; c bit 0 = 0: true if set, 1: true if clear. 0x: R = t; 4x: R = R or t; 8x: R = R and t |
| 10-17 (also 50-57, 90-97) | 2 (var, value) | compare var[v] with value; op = c & 7: 0 ==, 1 !=, 2 >=, 3 <=, 4 >, 5-7 < (R = result) |
| 2x (also 6x, Ax) | 2 (u16 ptr) | if R true: jump (ends 14) |
| 3x (also 7x, Bx) | 2 (u16 ptr) | if R false: jump (ends 14) |
| C0-C7 | 4 (u16 item, u16 value) | count of item (`$82:FB1F`) op value |
| C8-CF | 3 (char, u16) | party member HP (+0x11) op value |
| D0-D7 | 3 (char, u16) | MP (+0x13) op value |
| D8-DF | 3 | reads 1 byte then u16, compares FF with it (broken/unused) |
| E0-E7 | 2 (char, item) | R = member has item in his 0x24-byte list (+0x96) |
| E8-EF | 2 (char, value) | level (+0x0E) op value |
| F0-F7 | 2 (ignored, value) | world-map vehicle: (map $05AE < 2 ? $05B2 >> 4 : FF) op value |
| F8-FF (not FF) | 2 (actor, ignored) | R = true if actor's `$0736` bit 0x20 is set (else unchanged) |
| FF | 0 | end of 14 |

---

## 6. Command table

Args = bytes after the command byte. "p" = u16 pointer (section 3). Handler = `$80:xxxx`.

| Cmd | Args | Meaning | Handler | Conf |
|---|---|---|---|---|
| 00 | 0 | end (return from 05/06/09 call if active; else close text, restore camera, end script) | 9D4C | H |
| 01 | 0 | end text mode, wait for button, continue | 9DB3 | H |
| 02 | - | crash (BRK) | 9DD4 | H |
| 03 | 0 | newline | 9DD5 | H |
| 04 | 1 | text voice sound (xx<80) or pause (xx&7F)*10 frames | 9DFA | H |
| 05 | 1 | dictionary word xx (call) | 9E45 | H |
| 06 | 1 | dictionary word xx+0x100 (call) | 9E4E | H |
| 07 | 1 | glyph 0x300+xx | 9EC8 | M |
| 08 | 0 | open text mode, current speaker (the character talked to) | 9D3E | H |
| 09 | 0 | player name (text) | 9ED6 | H |
| 0A | 2 | text back-reference (section 2) | 9E83 | H |
| 0B | 0 | choice cursor (text) | 9F37 | H |
| 0C-0F | 0 | `?` `!` `,` `.` + newline | 9EF0.. | H |
| 10 | 1+2n | choice: n, n pointers, then text | 9F10 | H |
| 11 | - | crash | BF0A | H |
| 12 | 2+2n | var v, n, n pointers: jump to p[min(var[v]++, n-1)] | A041 | H |
| 13 | 1 | open text mode, speaker = actor xx | A06E | H |
| 14 | var | condition list until FF (section 5) | A074 | H |
| 15 | 3 | flag, p: jump if flag set | A288 | H |
| 16 | 3 | change map: map ($05AE), $05B8, $05B0 (script replaced) | A2BA | M |
| 17 | 1 | inn: music/fade/rest sequence, then pay xx gold | A2E1 | H |
| 18 | 1 | shop xx | A32B | H |
| 19 | 0 | church menu (menu 2 via $83:AFEA) | A372 | H |
| 1A | 1 | set flag | A392 | H |
| 1B | 1 | clear flag | A3AC | H |
| 1C | 2 | jump p | A3C6 | H |
| 1D | 2 | var[v] = value | A3DF | H |
| 1E | 2 | var[v] += value | A3ED | H |
| 1F | 2 | var[v] -= value | A3FF | H |
| 20 | 2 | give item xx (0x000-0x0FF), count yy (with "got" message per item) | A414 | H |
| 21 | 2 | give item xx+0x100, count yy | A41F | H |
| 22 | 2 | add gold u16 | A480 | H |
| 23 | 2 | party member xx learns spell yy ($82:FD3D) | A489 | M |
| 24 | 2 | remove item xx, count yy ($82:FA57) | A49D | H |
| 25 | 2 | remove item xx+0x100, count yy | A4A6 | H |
| 26 | 2 | remove gold u16 | A4CB | H |
| 27 | 2 | no-op (reads 2 bytes) | A4D4 | H |
| 28 | 0 | $81:F629/F5ED/F60B (unknown) | A4DD | L |
| 29 | 3 | member xx: raise stat yy by zz (tables $85:9EBA, $80:A596/A5A4) | A54A | M |
| 2A | 3 | no-op | A5BC | H |
| 2B | 1 | party member xx joins ($0926) | A5C8 | H |
| 2C | 1 | party member xx leaves | A5FF | H |
| 2D | 1 | show actor xx (clear hidden bit; ids 10-4F also persistent) | A63A | H |
| 2E | 1 | hide actor xx (set hidden bit; ids 10-4F persistent) | A679 | H |
| 2F | 3 | place actor xx at tile x, y ($80:A74D) | A71B | M |
| 30 | 2 | place actor xx at preset point yy | A72E | M |
| 31 | 2 | actor xx turns to direction yy (table $80:A7B6) | A794 | H |
| 32 | 2 | release actor xx (FF = all) with movement mode yy ($070A) | A7BB | M |
| 33 | 2 | path xx (list +0x12), actor yy walks it (waits for a free walker) | A80F | H |
| 34 | 2 | actor xx map sprite yy | A8F8 | H |
| 35 | 3 | persistent NPC xx (10-4F) moves to map yy, spot zz (hidden now if on this map) | B2B1 | M |
| 36 | 1 | wait until actor xx stops moving | B005 | H |
| 37 | 1 | wait xx frames | B2EB | H |
| 38 | 1 | wait xx * 60 frames | B30C | H |
| 39 | 0 | no-op | B33E | H |
| 3A | 2 | scroll screen to tile x, y ($80:B374) | B341 | M |
| 3B | 1 | scroll screen to preset point xx | B34F | M |
| 3C | 0 | party gathers behind the leader (waits until done) | B397 | M |
| 3D | 1 | party gathers instantly (byte unused) | B46B | M |
| 3E | 0 | $0583 = 80 | B484 | L |
| 3F | 0 | $0583 = 0F | B48C | L |
| 40 | 0 | $83:8E4C | B494 | L |
| 41 | 2 or 3 | camera: `0n 00` back to party (speed n), `dn yy` d=1-4 move yy tiles (direction table $80:B5BE), `6n x y` to tile, `7n pp` to preset, `xx FF` wait for camera | B4C4 | H |
| 42 | 0 | = 00 | 9D4C | H |
| 43 | 2 | effect between areas xx and yy (list +0x14), $83:8B6A | B72D | L |
| 44 | 3 | word[$0320 + 2xx] += u16 | B7A6 | M |
| 45 | 3 | word[$0320 + 2xx] -= u16 | B7B4 | M |
| 46 | 3 | word[$0320 + 2xx] = u16 | B7C2 | M |
| 47 | 1 | text box style ($1255; 10 = thought, no voice) | B7E6 | M |
| 48 | 2 | text window position: column xx, row yy ($125D) | B7EF | M |
| 49 | 1 | text auto-advance after xx frames (0 = wait for button) | B837 | M |
| 4A | 1 | $09AD (text appearance?) | B840 | L |
| 4B | 1 | play music xx (restarts) | B849 | H |
| 4C | 1 | play sound xx ($84:8775) | B8BD | H |
| 4D | 1 | wait until APU port 0 >= xx (music sync) | B8C7 | M |
| 4E | 0 | wait while APU status bit 1 | B8DC | L |
| 4F | 1 | no-op | B8ED | H |
| 50 | 0 | text voice off ($1260 = 0) | B8F3 | M |
| 51 | 0 | text voice from speaker | B8F9 | M |
| 52 | 1 | text voice = xx | B900 | M |
| 53 | 1 | script battle xx (table $97:C53D) | B909 | H |
| 54 | 1 | door xx ($83:F559, A = xx<<8) | B92C | L |
| 55 | 1 | door xx, variant 0x40 | B93D | L |
| 56 | 0 | stop earthquake ($80:B97E) | B94E | M |
| 57 | 0 | wait until screen effects done ($1261 = 0) | B98F | M |
| 58 | 1 | fade, target 0F; low nibble -> $7FD08F, high -> $7FD090 | B9B2 | M |
| 59 | 1 | fade, target 00; same nibbles | B9DA | M |
| 5A | 3 | earthquake (3 parameters -> $7FD07E-80) | BA1C | M |
| 5B | 1-2 | sub xx (table $80:BA55): 00 y, 01 y (bank $86 sequence), 02 y, 03 = reset (JML $80:8000, 1 arg), 04 y, 05 y, 06 = hang, 07 y, 08 y | BA39 | M |
| 5C 5D | - | crash | BF08/9 | H |
| 5E | 1 | use exit/warp xx of the map (list +0x06) | BBEB | M |
| 5F | 1 | no-op | BC05 | H |
| 60 | 2 | write PPU register $2100 + xx = yy | BC0B | H |
| 61-67 | 0 | speaker 0,1,2,3,5,4,6 and open text mode | BC19.. | H |
| 68 | 2 | create actor: id xx, map sprite yy (skipped if $05B3 bit 4) | BC3D | H |
| 69 | 1 | map mode ($09A7) | BCBC | H |
| 6A | 3 | flag, p: jump if flag clear | A29C | H |
| 6B | 0 | refresh leader/camera ($80:ED9C) | A2B0 | L |
| 6C | 3 | actor xx pixel offset x, y (signed; y - 16) | B078 | H |
| 6D | 2+n+1 | credits text: u16 tilemap offset, bytes until 00 (table $80:B13E) | B128 | H |
| 6E | 2+n+1 | same, 2-row font (table $80:B1D7) | B1BE | H |
| 6F | 1 | change music xx | B887 | M |
| 70 | 0 | fade music out (60 frames) | B871 | M |
| 71 | 0 | wait for music fade | B89D | M |
| 72 | 0 | stop music | B8B1 | M |
| 73 | 2 | animation xx at preset point yy | B257 | H |
| 74 | 1 | battle background | B2A7 | H |
| 75 | 0 | scroll screen to the leader | B49B | M |
| 76 | 0 | wait for fade/screen effect | B99D | M |
| 77 | 1 | hold actor xx (flag 01) | A6AE | M |
| 78 | 0 | reset NPCs 5-27 (mode 9) | A6C2 | L |
| 79 | 0 | wait until no visible actor moves | A6E7 | M |
| 7A | 2 | actor xx sprite frame yy ($066A) | A709 | H |
| 7B | 2 | create actor xx, sprite yy (variant) | BC55 | H |
| 7C-80 | 1 | ship: mode ($09DF), position mode ($09EE), on map ($09E0), party in ship ($09E1), sprite ($09E2) | BD6C.. | M |
| 81 | 1 | capsule monster xx joins (message $30 = 3) | BD99 | M |
| 82 | 0 | $80:F821 + $83:8E4C | BDBB | L |
| 83 | 1 | use exit xx (variant, sets $05B3 bit 5) | BDCA | M |
| 84 | 0 | halt (never advances) | 9DB0 | H |
| 85 | 2 | world position = preset xx, vehicle/facing nibble yy ($05B0-$05B2) | BDF1 | M |
| 86 | 2 | actor xx action yy relative to facing ($83:D350) | A936 | M |
| 87 | 2 | effect between areas xx, yy (variant of 43) | B62B | L |
| 88 | 0 | = 6B | B721 | L |
| 89 | 1 | hold actor xx when it stops (waits) | AFE7 | M |
| 8A | 0 | stop earthquake immediately | B962 | M |
| 8B | 3 | move actor xx by signed dx, dy pixels | B0BD | H |
| 8C | 3 | actor xx movement params ($7FE5CE, $7FE5A6) | A99D | L |
| 8D | 6 | object/projectile: actor, preset, 4 bytes, speed 3C | A9BE | L |
| 8E | 3 | effect object ($83:C6AA, type 0600) | AA43 | L |
| 8F | 3 | effect object (type 06) | AA83 | L |
| 90 | 3 | effect object (type 30) | AAA1 | L |
| 91 | 2 | flag FF = (u24 $0B55 >= u16) | AE03 | M |
| 92 | 1 | flag FF = $83:8AC9(xx) | AE24 | L |
| 93 | 3 | colour fade (signed step) | AE42 | L |
| 94/95/96 | 3 | screen tint (colour math $2131 = 93/53/D3) | AF5B.. | M |
| 97 | 0 | end tint | AF6B | M |
| 98 | 2 | actor xx to preset yy (action 62) | B021 | M |
| 99 | 1 | actor action 61 | B054 | L |
| 9A | 1 | remove effect objects with id xx | AABF | L |
| 9B | 5 | colour fade (two BGR15 colours + 1) | AEF2 | L |
| 9C | 3 | area xx + 2 bytes ($80:D533) | AFC0 | L |
| 9D | 1 | actor action 63 | B066 | L |
| 9E | 1 | open text mode, window at row xx, centred on the text width | B7FE | H |
| 9F | 3 | effect object (as 8E, flag 04) | AA49 | L |
| A0 | 6 | as 8D, actor-to-actor | A9B7 | L |
| A1 | 2 | wait for effect object xx, field yy | AAE6 | M |
| A2 | 1 | wait until effect object xx is gone | AB1D | M |
| A3 | 2 | actor xx action yy ($83:D350) | A94E | M |
| A4 | 0 | pixelate screen | BA00 | M |
| A5 | 0 | reset stack, JML $83:ACEF (back to the field loop) | AB45 | M |
| A6 | 0 | rebuild actors from $7EF600 (if $05B6 bit 0) | AB51 | L |
| A7 | 1 | 00: set $09A6 bit 0, else clear | ADEC | L |
| A8 | 1 | 01: $84:890B, else $84:878A | ABD0 | L |
| A9 | 2 | xx < 2: $09E6 = yy; else $05C0/$05C1 = xx, yy | ABF9 | L |
| AA | 0 | clear $7FD100-$7FD103 | AC18 | L |
| AB | 1 | flag FF = (Dragon egg count >= xx) (item 0x2B) | AC28 | H |
| AC | 2 | preset xx, yy -> $05BD-$05BF | AC48 | L |
| AD | 0 | $8E:BCAD | AC6A | L |
| AE | 1 | FF: clear $05B6 bit 1, else set | AC73 | L |
| AF | 2 | actor xx $7FE35E = yy, $0736 bit 7 set | ACBD | L |
| B0 | 1 | actor xx clear that bit | ACE2 | L |
| B1 | 2 | $80:AD57 on actors xx, yy | AD02 | L |
| B2 | 2 | same, both orders | AD12 | L |
| B3 | 1 | $80:AD39 | AD2F | L |
| B4 | 3 | as 43 with mode zz (stores Y, yields) | B6F4 | L |
| B5 | 0 | $089D = 0, $085D = FF | ADD1 | L |
| B6 | 2 | actor xx $7FE316 = yy | AC8A | L |
| B7 | 1 | actor xx $8E:BAF6/BAE7 | ACA7 | L |
| B8 | 0 | end script (variant of 00) | ADDC | M |
| B9 | 1 | party member xx: $BC = FF unless status bit 2 | A4F0 | L |
| BA | 1 | party member xx $82:FD15 | A50B | L |
| BB | 0 | menu/message 7 (report) | A51D | M |
| BC | 0 | $83:8C8A (menu), then continue | B646 | L |
| BD | 2 | create effect actor (id - 0x48) xx, target yy | B6B3 | L |
| BE | 1 | = 77 | A6AE | M |
| BF | 3 | actor zz action xx toward actor yy | A95C | L |
| C0 | 3 | as 8B, then snap tile position | B0C3 | M |
| C1 | 2 | $7FD4F3/F4 | B117 | L |
| C2 | 6 | as 8D, speed 4F | A9CC | L |
| C3 | 6 | as A0, speed 4F | A9C5 | L |
| C4 | 2 | scroll to tile x, y (mode 2) | B4AD | M |
| C5 | 0 | clear tint/effect bits | B955 | L |
| C6 | 2 | animation xx at actor yy | B269 | M |
| C7 | 0 | screen refresh with NMI off | B659 | L |
| C8 | 7 | as 8D with speed byte | A9D3 | L |
| C9 | 7 | as A0 with speed byte | A9DB | L |
| CA | 3 | $7FD020 + xx = yy, zz | B67D | L |
| CB | 1 | $0B52 | B696 | L |
| CC | 0 | $0B62 = max | B69F | L |

Not seen in any map script (counts from code only): 02 07 11 1F 24 26 27 2A 2F 36 39 3A 3E 3F 40 42 44 45 46
4A 4D 4E 4F 50 51 52 57 5C 5D 5F 60 6D 6E 77 7A 80 84 93 96 9B AC B1 B2 B3 B5 B7 BB C8 CB.

## 7. Differences from LEdit (l2ref.md 4.5 / 4.6)

* **2E is 1 arg** (hide actor); the opening's `2E 59 00` is `2E 59` + `00` (end). 2D likewise.
* 14: variable length (condition list ending FF), not 6. 15 (map-command table) is 3, 1C is 2 (as the
  Dialogs.txt list already says).
* 19 church: **0** args (LEdit 1). 28: **0** (LEdit 1). 3D: **1** (LEdit 0). 83: **1** (LEdit 0).
  25: **2** = remove item xx + 0x100 (LEdit 1). 60: **2** = PPU register write (LEdit 1).
* 41: 2 **or 3** (3 when the first byte is 6x). 5B: 2 except 5B 03 / 5B 06 (1). 6D/6E: u16 + bytes until 00.
  C8/C9: 7.
* 0A is a text back-reference, 12 a "next line each time" counter, 1D-1F variable set/add/sub, 07 a glyph
  page. 17's byte is the inn price. 5E is a warp through exit xx, not "end". 84 halts.
* 61-67: the code gives speaker ids 0,1,2,3,**5,4**,6 (65 -> 5, 66 -> 4); LEdit has Tia = 65, Dekar = 66. If
  actor 4 = Tia (sprite order) then 65 = Dekar, 66 = Tia. Unverified in game.
* E0 is not a script command (table ends at CC).
* PH list +2 records with id 03 do not point at scripts (8 such entries, e.g. map 30 0x4D59C, map D2 0x6D340
  `01 BD 01 00`); only ids other than 03 are scripts.
* Init-script jumps use base $87:8000 (section 3), not the PH block.
