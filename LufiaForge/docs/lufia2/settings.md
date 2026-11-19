# Lufia II (US) "Game settings" values (static analysis)

ROM `lufia2.sfc`, LoROM: file = (bank & 0x7F) * 0x8000 + (addr - 0x8000).
All bytes below were read from the ROM. Confidence: H = code read end to end, M = code read but the meaning of a
RAM flag or the game effect is inferred, G = guess.
Helper scripts: `l2research\set_peek.py` (hex dump), `l2research\enc_rate.py` (encounter-rate table).

Joypad RAM (from NMI `$80:8770`): `$46` = held $4218 (A=80 X=40 L=20 R=10), `$47` = held $4219
(B=80 Y=40 Select=20 Start=10, d-pad 0F), `$4A/$4B` = newly pressed (+ auto-repeat).
`$057C` = debug-mode flag: only ever cleared (`$80:80B5`, `$80:810A`); the only code that sets it
(`$8E:B0D4`) is unreached. Several debug features hang off it (below) and can be switched on by patching.

---------------------------------------------------------------------------------------------------
## 1. Random encounters

### 1a. Where random battles happen
* **Only on the world map.** Dungeons/towns have no step encounters: the field monsters are actors.
  `$81:8351` (called from event commands `$80:ABA7`, `$80:BC8E` and `$8E:BB12`) gives visible-monster actor n
  (actor id - $50) its formation: per-map list `$97:B9AC` (u16 offset per map `$05AC`, then 1 group byte per
  monster), or `$7F:F966` on map $F0 (Ancient Cave floors). Then `$81:839F` builds it from group `$97:BE93`. (H)
* World map main loop `$86:924C`..`$86:929F` (entered from `$86:919E`). At each tile centre
  (`$11E8 & 0F == 8` and `$11EA & 0F == 8`) it calls `$86:9E4F`, **`$86:9C71` (encounter check)**, `$86:9ADB`.
* Step counter `$11E3`: `$86:9A35` adds 1 each time the tile coordinate (`$11F2`,`$11F4`) changes (stops at
  $FE). Cleared after a battle (`$86:9DBB`) and on world-map entry (`$86:CC42`). `$11E4` = value of `$11E3`
  at the last check (so each step is rolled once); `$11E4 = FF` forces a battle.

### 1b. The check `$86:9C71` (file 0x31C71)
```
86:9C71 LDA $09EA / ORA $11DE / BNE none     ; vehicle / special modes: no battle
86:9C79 LDA $123F / BNE none                 ; $123F set by $86:9AB8 (standing on a spot) 
86:9C7E LDA $11E4 / INC / BEQ battle         ; forced
86:9C84 LDA $11E3 / CMP #$06 / BCC none      ; minimum 6 steps          operand file 0x31C88 = 06
86:9C8B CMP $11E4 / BEQ none                 ; already rolled this step
86:9C90 CLC / ADC #$D6 / BCC + / LDA #$FF    ; A = min(steps + 214, 255) operand file 0x31C92 = D6
86:9C97 JSL $808299                          ; r = (rand8 * A) >> 8  -> 0..A-1
86:9C9B CMP #$DC / BCS battle                ; battle if r >= 220         operand file 0x31C9C = DC
86:9C9F none: $11E4 = $11E3, RTS
86:9CA6 battle: if $09E1 == 0 -> land ($86:9CF3), else (vehicle, G: ship) see 1d
```
Chance per step s (s >= 6): p = (min(s+214,255) - 220) / min(s+214,255): 0 at s=6, 1/221 at s=7,
rising to 35/255 = 13.7% from s=41 on. **Mean: 25.0 steps per battle.** (H)

### 1c. Patch: scale the rate (same size, 1 byte)
Change the threshold byte **0x31C9C** (now `DC`). Mean steps from `enc_rate.py` (ADC and min steps unchanged):

| byte | mean steps | rate vs. original |
|---|---|---|
| C0 | 11.8 | x2.1 |
| C8 | 13.6 | x1.8 |
| D0 | 16.4 | x1.5 |
| D6 | 19.8 | x1.25 |
| **DC** | **25.0** | **x1 (original)** |
| E2 | 31.4 | x0.8 |
| E8 | 38.2 | x0.65 |
| F0 | 49.4 | x0.5 |
| F6 | 64.2 | x0.4 |
| FA | 89 | x0.28 |
| FC | 124 | x0.2 |
| FE | ~260 | almost none |
| **FF** | never | **0 = no random battles** (r is at most 254) |

For finer control an editor can also change 0x31C88 (minimum steps, `06`) and 0x31C92 (offset, `D6`); keep
D6 + min steps >= the threshold if the first eligible step should still be 0%. Running `mean_steps(m, a, T)`
from `enc_rate.py` gives the mean for any triple. Confidence H.

### 1d. Groups (per-region "rates")
* Land (`$86:9CF3`): region = `$7F:E000[(x>>2) + (y & FC) * 16]` (64x64 grid of 4x4-tile blocks; filled at
  world-map load, source not traced). Region table **`$97:C493` (file 0xBC493)**, 10 bytes per region =
  5 x (group, weight). `$86:9D1B` sums the 5 weights; **sum 0 = no battles in that region**; otherwise
  `$86:9E3B` picks one weighted. 17 records fit before the next table `$97:C53D` (count is G). Region 0 is all
  zero. Weights only choose the group; they do not change the rate. (H for the code, G for the count)
* Vehicle (`$09E1 != 0` and `$09E0 == 0`; G: the ship): after passing the step roll, `STZ $11E3` and a second
  roll `rand(100) < 40` (`CMP #$28`, file **0x31CC0**); group = `$86:9DC7[rand(6) + clamp(Maxim level - 30, 0, 18)/2]`
  (file 0x31DC7, 15 bytes: `38 38 38 38 8C 8E 90 92 94 95 96 97 99 9A 98`). (M)
* Then `$7FF8A4` = group, `$7FF8A3` = 1, battle via `$81:8821`.

### 1e. "Hold a button to avoid battles"
**Option A, 2 bytes (re-enables the developers' debug walk):** file **0x3184C** `F0 28` -> `EA EA`.
This un-gates `$86:9849`, which on every tile centre sets the speed `$1204` (normal value per vehicle mode,
identical to what the game sets anyway) and, **while B is held, does `STZ $11E3` (no battle can happen)
and sets speed $0800 (4x)**. Sub-options on top of it:
* avoid without the speed-up: file 0x31870 `A0 00 08` -> `EA EA EA`;
* speed-up without avoiding: file 0x3186D `9C E3 11` -> `EA EA EA`;
* run at 2x instead of 4x: file 0x31872 `08` -> `04`;
* other button: file 0x31867 `A5 47 89 80` (B) -> e.g. `A5 46 89 10` (R).
Wall-walking (`$86:991B`, `$83:DC54`) and the other debug keys stay off: they test `$057C` separately.
B appears unused while walking on the world map (X = menu `$86:9ADB`, A/L = enter). Confidence H for code, M for "B free".

**Option B, 16 bytes, no speed change (uses dead debug code):** `$86:96BC`-`$86:973B` (file 0x316BC-0x3173B,
0x80 bytes) is the debug key handler, only called from `$86:9261` when `$057C != 0`. Overwrite it:
```
file 0x3125F  F0 03       -> 80 03        ; never call the old debug handler
file 0x31279  20 71 9C    -> 20 BC 96     ; JSR $96BC instead of JSR $9C71
file 0x316BC  A5 47 89 80 F0 04 9C E3 11 60 4C 71 9C
              LDA $47 / BIT #$80 / BEQ +4 / STZ $11E3 / RTS / JMP $9C71   (m=8-bit, x=16-bit there)
```
Button: byte 0x316BF (`80` = B; with 0x316BD `47`). Confidence H (M = test in game).

---------------------------------------------------------------------------------------------------
## 2. Walking speed

### 2a. World map: `$1204` (u16, 8.8 fixed pixels/frame, copied to `$1249` by `$86:9886`; `$86:A417` turns it
into x/y steps with the sine table `$97:B226`)
| where | file | bytes | meaning |
|---|---|---|---|
| `$86:CC48` LDX #$0200 | 0x34C48 | `A2 00 02` | on foot, set at world-map entry (`$86:CC3C`) |
| `$86:A0B9` LDX #$0200 | 0x320B9 | `A2 00 02` | back to walking (G: leaving a vehicle) |
| `$86:A0C6` LDX #$0200 | 0x320C6 | `A2 00 02` | same, other exit path |
| `$86:A034` LDX #$0800 | 0x32034 | `A2 00 08` | vehicle with `$09E1 = FF, $09E0 = 2` (G: ship) |
| `$86:A378` LDX #$0800 | 0x32378 | `A2 00 08` | mode with `$11DE = 1` (G: airship/sub) |
| `$86:9849` debug block | 0x3184E.. | `A0 00 02` x3, `A0 00 08` x2 | see 1e |
Normal walking = **2 px/frame**. Speed must divide 16 px (the tile-centre test `& 0F == 8` must hit
exactly): use $0100, $0200, $0400, $0800 only. **Faster walking: 0x34C4A and 0x320BB and 0x320C8 `02` -> `04`.**
If the 1e option A patch is applied, also 0x31850 `02` -> `04` (its default, rewritten every tile).
Confidence H (vehicle names G).

### 2b. Field (towns/dungeons): `$7FE4DE,X` per actor (quarter-pixels per frame)
* Motion op `$83:DC45`: progress `$7FE48E,X += $7FE4DE,X`; pixels moved = growth of (progress & FC)/4,
  summed in `$7FE4B6,X`; the step ends when that is exactly 16; walk frames change at 4 and 12.
* Player, every step: **`$83:BD96` `LDA #$08 / STA $7FE4DE`, file 0x1BD96 `A9 08`** -> 2 px/frame.
  Followers copy it (`$83:BDDE`). Also `$83:C19E` `A9 08` (file 0x1C19E, reset after a special move),
  `$83:A700` (8, default for new actors), `$83:A7A2` (4, NPC default), `$83:C041` (4, G: pushing).
* Safe values: 04, 08, 10 (16 must be hit exactly; 20 works but skips the walk-frame changes at 4/12).
  Faster walking: changing 0x1BD97 `08` -> `10` (and 0x1C19F) was tried in the game and does nothing: the continuing-walk path ($83:C232) keeps the old speed. What works (checked in the game): turn the run code on for every step, 0x1FC6C `F0 1C` -> `EA EA` and 0x1FC72 `F0 08` -> `EA EA` (8 tiles in 32 frames instead of 4).
* Run button (debug code `$83:FC69`, called after `$83:BD96`): if `$057C` and B held: speed $10, else a $10
  speed is put back to 08. Enable: file **0x1FC6C `F0 1C` -> `EA EA`**. B on the field opens a window
  when pressed (`$83:BC99` -> `$83:C375`), so move it to R: file **0x1FC6E `A9 80 25 47` -> `A9 10 25 46`**
  (R is not read by the field input code at `$83:BC2C`-`$83:BCB8`). Confidence H for code, M for "R free".
* No run button exists in the normal game.

---------------------------------------------------------------------------------------------------
## 3. Other global numbers

### Level cap 99 (H)
* `$81:F9BC` LDA level / **`CMP #$63` (file 0xF9C0)** / BEQ: level-up routine (EXP vs `$0A2A` next-level EXP).
* `$85:C94A` same test when counting level-ups after battle: **`CMP #$63` (file 0x2C94E)**.
* Patch both bytes to the new cap (01-63). Above 99 is unsafe (2-digit displays; the next-level EXP
  `$81:FA2B..` is capped at 9,999,999 by `$81:FABB`).

### Max gold 9,999,999 = $98967F (H)
Gold = `$0A8A` (u16) + `$0A8C` (u8). Every clamp writes the 3 constants; change all for another cap:
| routine | file offsets of the constant bytes (current) |
|---|---|
| `$80:BF12` event "add gold" | 0x3F27 `98`, 0x3F30 `7F 96`, 0x3F35 `7F 96`, 0x3F3D `98` |
| `$81:DD11` battle gold | 0xDD30 `98`, 0xDD39 `96`, 0xDD42 `7F`, 0xDD46 `98`, 0xDD4B `96`, 0xDD50 `7F` |
| `$81:DA4C` gold x4 bonus (when `$0B51 & 3`) | 0xDA63 `98`, 0xDA6C `96`, 0xDA75 `7F`, 0xDA79 `98`, 0xDA7E `96`, 0xDA83 `7F` |
| `$82:9808` shop sell | 0x11826 `7F 96` (SBC), 0x1182E `98`, 0x11832 `7F 96`, 0x11838 `98` |
EXP uses the same 9,999,999 clamps at `$81:DA11` (x4 bonus), `$81:DAEC`, `$81:DC0C`, `$81:FABB`.
Note `$80:BF26` only clamps when the high byte is >= $98 (fine for the real cap). Display is 7 digits.

### Item stack 99 (H)
Inventory `$0A8D`: 0x60 u16 slots, bits 0-8 item, bits 9-15 count (7 bits, so 127 is the hard limit).
| where | file | bytes | role |
|---|---|---|---|
| `$81:F0E3` add item (`$81:F085`) | 0xF0E4 / 0xF0E8 / 0xF0ED | `64` / `63` / `63` | CMP #100, SBC #99 (overflow), LDA #99 |
| `$82:FAA0` add count | 0x17ABD, 0x17AC2 | `63 00`, `63 00` | CMP/LDA #99 |
| `$82:FAF4` "stack full?" | 0x17AFC | `63 00` | CMP #99 |
| `$82:D7DD` shop: how many can be bought | 0x157DE | `63` | 99 - owned |
For a new max N (<= 127): 0xF0E4 = N+1, 0xF0E8 = N, 0xF0ED = N, the others = N.

### Party size 4 (H, structural)
`$0A7A` count, `$0A7B..$0A7E` ids, `$0A80..$0A87` block pointers; `$81:F723` adds into the first FF slot.
Not a single constant; do not change.

### Sell price = half (H)
`$82:9918` (called for every shop price): if `$1544 == 0` (sell list, set `$82:E413`) -> X = price/2
rounded up: **`$82:9946` `4A 69 00 00` (file 0x11946)** = LSR / ADC #0. Buy modes 1-3 pay full price,
except when Selan (hero 1) is in the party and her `$0CD9` (ring slot) holds item $167 (G: a bargain ring):
then buy prices are halved by the same code. Price = item record +5 (u16, `$0B89`).
* sell for full price: 0x11946 `4A 69 00 00` -> `EA EA EA EA` (also removes the Selan discount);
* sell for 1/4: -> `4A 4A EA EA` (rounds down).

### Inn price (H)
Event command `17 xx` (`$80:A2E1`): fade/music, full heal (`$81:F5BD`), then `JSR $BF43` with A = xx:
subtract xx gold (u8, 0-255; refused silently if not enough). Prices are per inn in the scripts.
Free inns everywhere: file **0x2325 `20 43 BF` -> `EA EA EA`** (any gold check done by the script before the
command is unaffected).

### New game start (H for map, M for the other bytes)
`$83:AD7A` (no save loaded, `$0B51` bit 1 clear): **map `$05AC` = 3 (file 0x1ADAC)**, `$05B2` = 0,
`$05B8` = 2 (file 0x1ADB4; arrival script index run by `$83:B45A`), `$05B0` = 3 (file 0x1ADB9; entrance/spot
index for `$80:C05C`, since `$05B3` = 1 (file 0x1ADBE) selects "spot" mode), then `$83:B18E`.
With `$0B51` bit 1 set: map $68 (file 0x1AD93), spot 3, script 2, capsule slot set (G: a retry/new-game+ mode).
Changing the start map needs a valid spot and script index on that map.
New-game party/gold/inventory: `$85:B2A1` (file 0x2B2A1): `01` party size, `00 FF FF FF` ids,
**gold u16 at 0x2B2A6 (`00 00`)**, inventory list from 0x2B2AA (`02 14` Potion x10, `A9 03` Arrow).

### IP gauge (H)
u8 at hero block +0xBC (Maxim `$0C69`); full = 255. Event command B9 sets FF. Gain on taking damage
`$85:D56F`: g = min(GUT', 199); t = g * $80 / 100 (`$85:D586` `A9 80`, file 0x2D587); gain = (t * `$4E`) >> 8
+ **12 (`$85:D5B7` ADC #$000C, file 0x2D5B8 `0C 00`)**, capped at 255. Raise 0x2D587 or 0x2D5B8 to fill
faster. The max (255) is the byte size; IP costs subtract from it (`$81:AB36`).

### Caps on displayed numbers (M)
999 for HP/MP and stats in displays and the status check (`$82:91B1`, `$81:E2D4`, `$85:D8FF..$85:D923`),
GUT' 199 (`$81:F4ED`, `$85:D91A`), 9999 at `$81:C513` (a display/division helper), casino coins 60000
(`$84:B966`) and 9999 (`$84:B990`). **No damage cap constant was found** in the battle bank $85 (no
CMP #9999/#999 on a damage value); left open.

---------------------------------------------------------------------------------------------------
## 4. ROM header (file 0x7FC0, LoROM) (H)
| file | bytes | field |
|---|---|---|
| 0x7FB0 | `45 39` | maker code "E9" |
| 0x7FB2 | `41 4E 49 45` | game code "ANIE" |
| 0x7FC0 | `Lufia II(Estpolis II)` (21 bytes, ASCII) | title |
| 0x7FD5 | `30` | LoROM + FastROM |
| 0x7FD6 | `02` | ROM + RAM + battery |
| 0x7FD7 | `0C` | ROM size 4 MB class (file is 2.5 MB) |
| 0x7FD8 | `03` | SRAM 8 KB |
| 0x7FD9 | `01` | region: North America |
| 0x7FDA | `33` | old maker byte = 33 (use extended header) |
| 0x7FDB | `00` | version 1.0 |
| 0x7FDC | `97 45` | complement 0x4597 |
| 0x7FDE | `68 BA` | checksum 0xBA68 |
Checksum = (sum of the first 2 MB + 4 x sum of the last 0.5 MB) & FFFF (the 0.5 MB tail is mirrored to fill
4 MB); complement = checksum ^ FFFF. Any patch must recompute both. Title can be changed in place (pad
with spaces to 21 bytes).
