# Lufia 1 event command reference

Decoded from the handlers at `$01:C78C` (00-6D) and the range table `$01:C868` (80-FF) by static disassembly (session 10, 2026-10-06).
Lengths match `EventScript.CommandLength`; names and fields are in `Core/Maps/EventCommands.cs`. Everything below is from code reading unless an entry says it was seen in game.

**Fade direction (checked against how the game uses them):** `18` fades the screen *in* from black and `19` fades it *to black* (`19` always comes right before warps that keep the screen black, `18` after "Light Magic" in the prologue). The handler notes below had them the other way round.

Corrections made from this work: `51` is 3 bytes and ends the flow; jumps also in `45`-`49`, `4F`, `C0`-`DF`; text codes `01`-`03` close the box like `04`, `08`/`0E` end the script like `00`; `80`-`87` wait 4/8/12/20/40/60/80/100 frames; `0B nn` waits nn*4 frames; `6C` waits for a music sync point, not frames; `58` crashes (never used); `6E`-`7F` have no handler (a decoder that meets one has lost sync).


Source: static disassembly of bank $01 handlers (all addresses $01:xxxx = file 0x0xxxx). "Length" includes the opcode byte.
Conventions used below:
- **actor id**: index into the 40-entry actor table (`$07DE + 0x20*id`, active list `$07B6[id]`). Actor 1.. = party walking chain
  (leader = `$8D`, chain count `$8C`), actor `n+7` = map NPC n (NPC numbering as used by the script, i.e. first NPC = actor 8 when n is 1-based; the engine just adds 7).
- **"NPC operand"** = script byte + 7 → actor id. **"absolute operand"** = script byte is the actor id itself.
- **direction** (actor +0B and all opcodes): 0 = right (+X), 1 = left (−X), 2 = down (+Y), 3 = up (−Y). Movement bitmask form: 1=R, 2=L, 4=D, 8=U. Derived from the walk-to-target code at $01:9D9B and the camera-edge checks at $01:9E81 (confirmed).
- **continue** = handler jumps back to $C66E. **yield** = handler saves the pointer ($C728) and exits via $C6EC; resume happens from the field loop ($01:8348 / $8421) when `$07B4` is cleared / `$0D26` runs out. **inline block** = the handler itself runs a frame loop / busy-wait before continuing.

| op | handler | len | operands | semantics | pauses? | RAM touched | conf. | notes |
|---|---|---|---|---|---|---|---|---|
| 00 | CA2B | 1 | – | End script: `$0D14=FF` (no script), exit. | ends | $0D14 | confirmed | |
| 01 | CA63 | 3 | ww = offset from event start | Goto (C700: Y = $0D0F base + ww, bank from base). | continue | Y/DB | confirmed | |
| 02 | CAC4 | 6 | ff, w1, w2 | Map-local flag (ff + `$0D0C`) in `$1336` bitfield: if clear → **set it** and goto w1; if already set → goto w2. | continue | $1336.., $0D0C | confirmed | "first time only" |
| 03 | CAEF | 2+2n | n, n×ww | Store choice jump table: `$0D3E=n-1`, targets → `$0D3F..`, `$0D3C=2` (choice pending; next text box shows the menu). | continue | $0D3C-$0D3F+2n | confirmed | |
| 04 | CA6C | 4 | ff, ww | If story flag ff set → goto ww (else skip word). | continue | $1296.. | confirmed | |
| 05 | CA70 | 4 | ff, ww | If story flag ff clear → goto ww. | continue | $1296.. | confirmed | |
| 06 | CA90 | 2 | ff | Set story flag ff (`$1296 + ff>>3`, bit via $032B table). | continue | $1296 | confirmed | |
| 07 | CAA7 | 2 | ff | Clear story flag ff. | continue | $1296 | confirmed | |
| 08 | CA33 | 1 | – | Save script state, `$0D1F=FF`, run map object setup ($9717/$9817 = re-spawn NPCs per flags), restore state. | continue (setup runs synchronously) | $0D0F-$0D14, $0D1F, actor tables | confirmed | |
| 09 | CECA | 5 | xx xx(word X), yy yy(word Y) | Snap camera: centre BG1 camera ([$7D]) on tile (X,Y): cam+06 = X−7, cam+08 = Y−7 (wrapped by map size cam+02/+04), clear cam+0A/+0B; BG2 camera ([$7F]) same unless `$77`≠0 (then 0,0). Redraws map ($B7D2). | continue | [$7D]+06..0B, [$7F]+06..0B | confirmed | X,Y are tile coords of screen centre |
| 0A | CBEB | 2 | mm | Music: `$92=mm` (map music, restored after battles/inn). mm≠FF → play song mm ($9D0A: skipped if `$93` bit7; `$078E=mm`, JSL $008E13). mm=FF → `$078E=FF`, JSL $008F98 (stop/fade out music). | continue | $92, $078E | confirmed | |
| 0B | CBD8 | 2 | nn | Wait nn×4 frames: `$0D26=nn*4`, `$07B4=FF`, yield. Field loop decrements `$0D26` per frame ($01:8348), clears `$07B4` at 0. | **yield nn×4 frames** | $0D26, $07B4 | confirmed | 0B 3C = 240 frames. Note nn≥$40 overflows (×4 is 8-bit). |
| 0C | CBD5 | 1+text | – | Enter text mode (no window/speaker change) → text until 04 (back to commands) / 00 (end). | text | – | confirmed | |
| 0D | CBC7 | 1+text | – | No speaker: `$0D0D=0`, `$0D09=0`, open window style 1 (`$0D01`, DD52), text mode. | text | $0D0D,$0D09,$0D01 | confirmed | |
| 0E | CBA1 | 2+text | aa (absolute actor) | Actor aa speaks: `$0D0D=aa`, `$0D09` = voice/style from actor+1D&7 via table $01:E2F3, window style 0, text mode. | text | $0D0D,$0D09,$0D01 | confirmed (field meaning of $0D09 inferred) | same code as 31/33/35/37 and 88-8F |
| 0F | CB98 | 2+text | nn (NPC) | As 0E with actor = nn+7 (DEC; +8). | text | same | confirmed | same as 30/32/34/36, 90-AF |
| 10-13 | CD62/66/6A/6E | 3 | nn = tiles, ss = speed idx | **Scroll camera** nn tiles; direction by opcode: 10=right(mask1), 11=left(2), 12=down(4), 13=up(8). Speed = table $01:E237[ss] = {10,02,04,08,10,20,40,80}. Per tile: cam+0E=mask, +0F=FF, +0C/+0D=speed on [$7D]; BG2 [$7F] too if `$77`=0 (if `$77`=5 BG2 gets parallax speed $10). Runs its own frame loop (actors still updated via $9D1F) until cam+0E=0, nn times. | **inline block** until scroll done | $0D22 (mask), $0D23 (count), $0D24 (speed), [$7D]/[$7F]+0C..0F | confirmed | nn=0 → 256 tiles (DEC before test). Speed: 10 likely = 1 px/frame (inferred) |
| 14 | CFAC | 3 | pp = path #, aa (NPC) | **Run movement path** pp (1-based) from the map's path section (bank $7F, ptr `$07AE`) on actor aa+7. See path format below. If path flags bit4 clear: warp actor to path start (X,Y) first (keeps facing). Sets actor+19=record, +13=first step, +15/+18=step count, +16=first step's tile count, +17=flags, clears +1D bit7. If actor == `$0D0A` (NPC being talked to) clear `$0D0A`. | flags bit0=0 → **yield until all pending movement ends** (`$07B4=FF`; resume when `$0D27` underflows → `$0D26=2`). bit0=1 → continue (bit1=0 → `$0D27++`). | actor+00..+1D, $0D0A, $0D27, $07B4 | confirmed | e.g. `14 0F 06` path 15 on NPC 6 |
| 15 | CFA2 | 3 | pp, aa (absolute) | Same as 14 with absolute actor id. | as 14 | as 14 | confirmed | `15 01 01` path 1 on leader |
| 16 | D0AB | 2 | aa (NPC) | aa=0: release **all** actors from path/script control (actor+19=0 for every active actor) and clear `$07B4` (cancels any movement wait). aa≠0: intended "release actor aa", but see bug. | continue | actor+19, $07B4 | confirmed | **Bug**: aa≠0 adds 8 (CMP#0 sets carry, then ADC #7) and the code never loads X from $06, so it zeroes DP $45/$46 instead of the actor. Effectively a no-op except possibly clearing $07B4. All observed uses are `16 00`. |
| 17 | D0A6 | 2 | aa (absolute) | Same as 16 non-zero path with absolute id — same stale-X bug (writes DP $47/$48). | continue | (buggy) | confirmed | only 2 uses (17 02, 17 00) |
| 18 | CDFA | 2 | ss = frames per step | **Fade screen out** (palette to black): rebuild reference palette (CE07: map palette from table $01:E160 by `$7F0009`, $02:9300, decompress → $7E:F600..), clear target `$7E:037F-053E`, start 32-step fade, ss frames per step (`$BD/$BA`), `$077B=4`. | continue (fade runs in NMI/frame task; use 5A to wait) | $7E:F600-F7FF, $7E:033F+, $077B, $BA,$BD,$BE,$B1-B5 | confirmed (direction "out" inferred from target=black) | colors from index $20 up (first 2 BG palettes untouched — inferred) |
| 19 | CE79 | 2 | ss | **Fade screen in**: save current target palette `$7E:037F-053E` → `$7E:F640..`, start 32-step fade back to it, ss frames/step, `$077B=3`. | continue (5A waits) | same | confirmed (direction inferred) | typical: `18 02` … `19 01` |
| 1A | CB11 | 3 | cc = character, ss = spawn source | **Character joins / follower appears**: spawn source actor = 1 (leader) if ss=0 else ss+7 (NPC). Copies its X/Y/facing/layer, creates a new chain actor id `++$8C` with sprite = table $01:E23F[cc*2]; `$0CE3+i`=sprite, `$0CE8+i`=cc. If table high byte E240[cc*2] < $80 → JSL $08EA8F (add party member hi&3: restores level from $1577 or inits). | continue | $8C,$8C+i, $07B6, actor table, $0CE3+, $0CE8+, $156F, $1577, $14D2, $14CE | confirmed (character names inferred) | cc 0-3: Hero/Lufia/Aguro/Jerin (sprites 0-3, slots 0-3); cc 4-7: sprites 27-2A, slots 0-3 (prologue heroes, inferred); cc 8-F: sprite-only followers (0C,10,0C,07,04,1A,1B,08), no party change |
| 1B | CB73 | 2 | cc | **Character leaves**: if E240[cc*2] < $80 → JSL $08EA4A (remove party member hi&3: level saved to $1577, $156F=0, removed from order $14D2.., `$14CE--`). Then deletes the **last** chain actor (`$07B6[$8C]=0`, `$8C--`). | continue | $8C, $07B6, $156F, $1577, $14D2-D5, $14CE | confirmed | removes last follower sprite regardless of cc |
| 1C | CF43 | 2 | nn | Shop nn: clear story flag FF, JSL $00CDB5 (shop UI), if result≠0 set flag FF. | inline block (shop UI) | $1296+$1F | confirmed | flag FF = "bought/sold" result |
| 1D | CF59 | 3 | ww (price) | Inn: subtract ww gold from `$14CF-$14D1` (only if enough; carry result is **ignored**), JSL $08E850 (heal), play song $1E, fade out (`$0540=$78`, `$053F=FF`), wait 60 frames, fade in, restore music `$92`. | inline block (~2-3 s) | $14CF-D1, $0540, $053F, $38 | confirmed | script must check gold itself |
| 1E | CF19 | 1 | – | Church: save return pointer into `$0D19-$0D1E` (CF27), then shared sub-script call at $D96F with `$16=$0A`, `$1A=$63` (same mechanism as 51). | transfers control (sub-script) | $0D19-$0D1E | confirmed (call mechanics), details in 51 analysis | |
| 1F | D185 | 2 | ee = formation | **Scripted battle**: `$142D=ee`, `$0D21=FF`, yield. Field loop ($8920) runs battle (JSL $088000), restores music `$92`, then resumes script ($C2DF) because `$0D21` was set. `$142E` bits0-1 = outcome. | **yield until battle ends** | $142D, $0D21, $142E, $92 | confirmed | examples 1F F0/E6/EF boss formations |
| 20-27 | CC08..CC30 | 4 | aa, xx, yy | **Place actor**: actor = (aa&7F)+7 for even ops (NPC), aa&7F for odd ops (absolute). Facing = (op>>1)&3. X = ((aa>>7)<<8) | xx, Y = yy (tiles). If actor inactive: nothing (except D0FE). Else (leader/NPC): remove from collision map ($AF6E), set +0B, +01/+04, clear +00,+03,+0A, re-add ($AF36). If actor is party leader with followers: snap all followers (not on a path) to same tile, clear trail `$0CDE-$0CE3`. Finally D0FE: actor+19=FFFF ("held by script"), +15,+17,+18=0. | continue | actor+00..+0B,+15..+1A, $0CDE-E3 | confirmed | bit7 of aa = X high bit (maps >255 wide). `24 11 00 00` = park NPC $11 at (0,0) (hide). Followers (actors 2-6) get position only. |
| 28-2F | CCFA..CD1E | 2 | aa | **Face direction**: dir = (op>>1)&3; actor = aa+7 (even ops) or aa (odd). Writes actor+0B, `$0D28=actor`, `$3E=FF` (redraw pending). If the same actor as last face command and `$3E` still pending: rewind 2 bytes, wait 1 frame (`$0D26=1`) and retry. | continue (1-frame yield only on back-to-back face of same actor) | actor+0B, $0D28, $3E, $0D26, $07B4 | confirmed | 28/29 right, 2A/2B left, 2C/2D down, 2E/2F up |

## Family patterns

- **10-13**: low 2 bits → camera direction 0..3 encoded as mask 1<<n (R, L, D, U).
- **14/15, 16/17**: even = NPC operand (+7), odd = absolute actor id. (16/17 non-zero form is buggy.)
- **20-27**: bit0 = 1 → absolute actor id, 0 → NPC (+7). bits1-2 → initial facing (20/21 R, 22/23 L, 24/25 D, 26/27 U).
- **28-2F**: identical encoding to 20-27 (bit0 absolute, bits1-2 facing), 1 operand.
- **0E/0F** (and 30-37, 88-AF): 0E-style = absolute speaker, 0F-style = NPC speaker (+7).

## Shared RAM structures

### Actor table — `$07DE + 0x20*id`, id 0..$27 (computed by $01:AC38 → pointer in DP $06)
| off | meaning |
|---|---|
| +00 | X half-tile/sub-step flag (nonzero = between tiles; tile+1 used for collision) |
| +01-02 | X tile (word) |
| +03 | Y half-tile flag |
| +04-05 | Y tile (word) |
| +08 | layer/priority (path step bit0; copied by 1A) |
| +09 | walk speed (value from $01:E237) |
| +0A | current movement mask (1 R, 2 L, 4 D, 8 U; 0 = idle) |
| +0B | facing 0 R / 1 L / 2 D / 3 U |
| +13-14 | current path step pointer (bank $7F) |
| +15 | steps remaining in path |
| +16 | tiles remaining in current step |
| +17 | path header flags (see below) |
| +18 | path running (nonzero) |
| +19-1A | control: 0 = free (normal NPC behaviour), FFFF = held by script (after 20-27), else pointer to path record |
| +1C | (passed to $C227 on talk; inferred: NPC talk event) |
| +1D | flags: bit7 cleared by path start, bit6 = "don't turn" (step face mode 5), bits0-2 = voice/text-style index → $01:E2F3 |
| +1E-1F | timers used by $8290 routine (inferred) |
- `$07B6[id]` (40 bytes): actor active/sprite slot (0 = absent).
- Party chain: `$8C` = number of chain actors, `$8D..$8C+$8C` = chain actor ids (`$8D` = leader, normally 1); `$0CE3+i` sprite id, `$0CE8+i` character id; follower trail `$0CDE-$0CE3`.

### Camera structs — BG1 at pointer `$7D`, BG2 at pointer `$7F` (`$77` = BG2 mode: 0 = follows BG1, 5 = parallax, else independent)
+02 map width, +04 map height (wrap), +06 scroll X tile (centre−7), +08 scroll Y tile, +0A/+0B cleared on snap, +0C/+0D scroll speed, +0E scroll direction mask (0 = idle), +0F set FF when scrolling.

### Movement path records (map data, bank $7F, base `$07AE`, last map-data section; count probably `$079F`)
Record n (1-based) found by skipping `6 + 3*count` bytes per record.
- Header 6 bytes: [0] flags — bit0 = don't wait (async), bit1 = loop forever (restart at end), bits2-3 = camera follows actor (nonzero: scroll [$7D]/[$7F] with the actor), bit4 = walk to start position instead of warping; [1] step count; [2-3] start X; [4-5] start Y.
- Steps, 3 bytes: [0] bit0 → actor+08 layer, bits2-4 face mode (0 = face move dir, 1-4 = face dir 0-3, 5 = keep facing/no turn), bits5-7 speed index (E237); [1] bits0-1 direction (0 R,1 L,2 D,3 U); [2] tile count.
(confirmed from $01:9D62-$9F41.)

### Script / misc
- `$0D0F-11` event base, `$0D12-14` script ptr (`$0D14=FF` = no script), `$0D19-1E` saved return ptr (1E/51 calls).
- `$07B4` script blocked flag (FF = waiting); `$0D26` wait-frames counter; `$0D27` count of outstanding async paths.
- `$0D28` last actor faced, DP `$3E` face-redraw pending.
- `$0D0A` NPC currently talked to; `$0D0D` speaker actor; `$0D09` speaker voice/style; `$0D01` window style.
- `$0D22/23/24` camera-scroll mask / count / speed.
- `$1296-$12B5` story flags (256), `$1336..` map-local flags (index + `$0D0C`), `$032B` bit-mask table.
- `$0D3C` choice mode, `$0D3E` choice count−1, `$0D3F..` choice targets.
- `$92` map music, `$078E` current song, `$93` bit7 music lock.
- `$142D` battle formation (scripted/random), `$0D21` scripted-battle flag, `$142E` battle result.
- `$077B` fade state (4 = fading out, 3 = fading in, 0 idle — 5A waits on it), `$BA/$BD` fade step timer, `$BE` steps left (32).
- `$14CF-14D1` gold, `$156F+c` level (0 = not in party), `$1577+c` saved level, `$14D2-14D5` party order, `$14CE` party size.

## Length table corrections

None for 00-2F: every length in `EventScript.CommandLength` matches the handlers (03 = 2+2n; 0C/0D = opener 1 + text; 0E/0F = opener 2 + text; 10-15 = 3; 16-19 = 2; 1A = 3; 1B/1C = 2; 1D = 3; 1E = 1; 1F = 2; 20-27 = 4; 28-2F = 2). No handler in this range has conditional/variable operand reads other than 03 (and 02/04/05 always consume both words).

Opstats oddities like `20 0C B6 20`, `21 20 44 65`, `10 20 0C`, `11 00 FF` are text bytes ($20 = space etc.) decoded as commands: the desync comes from somewhere else (another opcode's length, a text control code with operands — text codes 09/0A take 1 byte, others in $C9C9/$C9F4 read bytes/words — or a jump into data), not from 00-2F lengths.

Other editor-relevant notes:
- 1E transfers into a sub-script and 1F/0B/14/15 yield — `IsTerminal`-style flow analysis should treat them as "continue at next byte" (they all resume at the following command).
- 16/17 with a non-zero operand do not actually release the actor (engine bug); only `16 00` is meaningful.


All handlers in bank $01. Engine state on entry: M=8, X=16, Y = script pointer, DB = script bank.
Operand reads: `$C740` = 1 byte, `$C753` = 2 bytes (little-endian). `$C700` = take the word just read as an
offset from the event start ($0D0F) and jump there. "Flag FF" = story flag $FF (bit 7 of $1296+$1F), the
engine's *result flag*: `$CAB0` clears it, `$CA99` sets it; scripts test it afterwards with `04 FF ww` / `05 FF ww`.
"cc" = character index 0 Hero, 1 Lufia, 2 Aguro, 3 Jerin (masked with &3). "Exit/yield" = `JSR $C728` (save Y to
$0D12) + `BRL $C6EC` (leave interpreter; script resumes at the next command when the wait condition clears,
$07B4 = "script suspended" flag).

| op | handler | len | operands | semantics | pauses? | RAM touched | conf. | notes |
|----|---------|-----|----------|-----------|---------|-------------|-------|-------|
| 30,32,34,36 | $CB98 (= 0F) | 2 + text | nn = map NPC index | **Alias of 0F**: text box spoken by actor nn+7 (map NPC nn). Speaker -> $0D0D, text-blip sound $0D09 = $01:E2F3[actor.$1D & 7], window opened ($DD52 A=0), then inline text ($C693) until 04/00. | text (waits for player as any text) | $0D0D, $0D09 | confirmed | Opcode value is discarded (overwritten by the operand read); 30-37 never occur in the shipped scripts. |
| 31,33,35,37 | $CBA1 (= 0E) | 2 + text | aa = actor index | **Alias of 0E**: text spoken by actor aa (0-6 party/vehicle, 7+ = NPC aa-7). Same as above. | text | $0D0D, $0D09 | confirmed | |
| 38 | $D0ED | 2 | nn = map NPC | Freeze actor nn+7: actor.$19 = $FFFF (no AI wander / no move script), $15/$17/$18 = 0 (cancel current step). | no | actor $15,$17,$18,$19 | confirmed (code) / inferred name | The NPC AI routine ($01:9FF1) returns immediately when $19 != 0. |
| 39 | $D0F5 | 2 | aa = actor | Same as 38 for actor aa directly. | no | same | confirmed | ex `39 02`. |
| 3A | $D113 | 2 | ss = sound id | Set the text "blip" sound effect ($0D09) used while text prints. | no | $0D09 | confirmed | $0D09 is passed to the SFX routine $00:8E8F by the text printer. Ex `3A 04`, `3A 29` (both values in the voice table E2F3 = 05 06 29 04 28 05 05 05). Default (map load) = 04. |
| 3B | $D11C | 1 | - | Silence text blips ($0D09 = 0). | no | $0D09 | confirmed | 0D also zeroes it. |
| 3C | $D122 / $D133 | 1 | - | **Gather party**: if party size ($8C) >= 2, every follower gets a 1-step move script ($19=#$0D29, $13=#$0D2F, $17=$10, $18=$16=1) to walk onto the leader's tile ($0D2B/$0D2D = leader X/Y); $0D32 = follower count. | **yes** if >=2 members (yield; resumes when $0D32 counts down to 0 at $01:9F61) | $0D29-$0D32, $07B4, follower actor $13-$19 | confirmed | Used before most cutscenes (82x). With 1 member continues immediately. |
| 3D | $D196 | 3 | cc, ss = spell | Character cc learns spell ss (appended to first free slot of its spell list, $08:E829). | no | $158F/$15AF/$15CF spell lists | confirmed | $08:E84C maps cc 0->$158F, 1->$15AF, 2->$158F(!), 3->$15CF. |
| 3E | $D1A9 | 3 | ii = item, qq = qty | Give item. Clears flag FF; $08:F45A checks room, $08:F49A adds (stacks to 99, 60 slots). **Flag FF set = did not fit / failed.** Items F0-FF are key items stored as bits in $15EF. | no | $14F7 inventory, $15EF, flag FF | confirmed | Ex `3E E6 01`. |
| 3F | $D1CC | 3 | ii, qq | Remove qq of item ii ($08:F518; F0-FF clear the key-item bit). | no | $14F7, $15EF | confirmed | |
| 40 | $D1DD | 3 | wwww = gold | Add gold (24-bit $14CF-$14D1, saturates at $FFFFFF). | no | $14CF | confirmed | unused in game |
| 41 | $D1EB | 3 | wwww = gold | Take gold if affordable ($DE28). **Flag FF set = NOT enough gold** (nothing taken); clear = paid. | no | $14CF, flag FF | confirmed | Ex `41 32 00` (50 GP), `41 0A 00`. |
| 42 | $D201 | 2 | cc or FF | Restore HP to max: $157F[cc] = $16E0[cc] (effective max HP). FF = every party member. Skipped if status bit $20 (dead). | no | $157F, $157B | confirmed | `42 00`, `42 FF`. 42/43/44 = heal family. |
| 43 | $D23B | 2 | cc or FF | Restore MP: $1587[cc] = $16E8[cc]. Skipped if dead. | no | $1587 | confirmed | |
| 44 | $D275 | 2 | cc or FF | Clear all status: $157B[cc] = 0 (also clears the dead bit, HP unchanged). | no | $157B | confirmed | |
| 45 | $D2A1 | 5 | cc, ss, wwww | If spell list of cc contains spell ss -> jump wwww. List = $158F + 32*min(cc&3,2). | no | $158F.. | confirmed | Jump operand at byte 3. Here cc 2 and 3 both use $15CF (Jerin's list) - differs from 3D. |
| 46 | $D2D4 | 5 | ii, nn, wwww | If ii < F0: if total quantity of item ii in inventory >= nn -> jump wwww. If ii >= F0: nn is read and ignored; jump if key-item bit (ii-F0) of $15EF is set. | no | $14F7, $15EF | confirmed | Jump at byte 3. Ex `46 FD 01 8D 01`. |
| 47 | $D322 | 5 | cc, ll, wwww | If level $156F[cc] >= ll -> jump. | no | $156F | confirmed | Jump at byte 3. |
| 48 | $D343 | 6 | cc, hhhh, wwww | If current HP $157F[cc] >= hhhh -> jump. | no | $157F | confirmed | Jump at byte 4. |
| 49 | $D369 | 6 | cc, mmmm, wwww | If current MP $1587[cc] >= mmmm -> jump. | no | $1587 | confirmed | Jump at byte 4. Opstats examples are text misparses. |
| 4A | $D38F | 1 | - | Save-game menu: stores map ($077F->$1345), $75->$1346, leader facing->$1347, scroll pos->$134A/$134C, calls $00:CCF5 (menu). Flag FF set if the menu returned carry (saved/cancelled - unverified which). | yes (menu, synchronous) | $1345-$134C, flag FF | inferred | Load routine $01:C13F reads $1345-$1347 back. |
| 4B | $D3D4 | 6 | vv, xxxx, yyyy | Set vehicle: $0792 = vehicle type, $0794/$0796 = its world-map tile X/Y. Applied to actor 6 on the next map load ($01:92A4 / 91F4). | no | $0792,$0794,$0796 | inferred (strong) | Ex `4B 03 A9 00 B0 00`. |
| 4C | $D3F2 | 3 | mm = map, ee = entrance (1-based) | **Warp/teleport**: $0D4C=mm, $0D20=$0780=ee, $0D4B=ee-1, transition $0D49=0; calls $01:912A (fade out, load map if mm != 0 and != current $077F, place party at entrance record $7F:07A4+6*(ee-1) {flags/facing, X, Y}, fade in, wait). mm = 0 -> reposition on current map. | blocks inside (fade waits); script then continues in the new map | $0D49,$0D4B,$0D4C,$0D20,$0780,$077F,actors | confirmed | Ex `4C 00 08`, `4C 4F 04`. Event base/pointer saved & restored around the call. |
| 4D | $D432 | 2 | nn (1-based) | Set bit nn-1 of $15EF = give key item F0+(nn-1). | no | $15EF | confirmed | |
| 4E | $D459 | 2 | nn | Clear that bit (take key item). | no | $15EF | confirmed | |
| 4F | $D471 | 4 | nn, wwww | If key-item bit nn-1 set -> jump. | no | $15EF | confirmed | Jump at byte 2. Same test as `46 F0+n-1`. |
| 50 | $D48D | 2 | ss = sub-function 00-20 | Call system routine from table $01:D49D (see list below). Y is pushed/pulled, so sub-functions never read operands. | depends (05/06/08/19 wait for fades) | various | confirmed (dispatch) | Ex `50 06`, `50 12`, `50 14`. |
| 51 | $D95D | 3 | mm = map, ee = event | **Chain to another event**: $1A=mm; index = (byte4 of map table $03:8204+5*mm) + ee; $C292 loads map mm's script block (sets $0D0B=mm, $0D0F base, $0D0C local-flag base) and the event pointer; Y = new event start; clears speaker; resets text window ($DD52 A=1); continues there. Never returns. | no | $0D0B,$0D0C,$0D0D,$0D0A,$0D0F-$0D14 | confirmed | Target slot = (LufiaMap.EventBase(mm)+ee)&FF. Always seen as `5B mm xx / 51 mm ee / 00`. Flow ends. |
| 52 | $D988 | 2 | nn (1-based) | Open map object nn-1 (12-byte record at $7F:07AA+12*(nn-1)): if its persistence bit (record[3] + $0787, in $12B6 bitfield) is not yet set, set it and redraw object in "open" state ($9C63) + refresh ($8793). Records with record[3] negative are just redrawn. Already open -> nothing. | no | $12B6, BG map buffer | confirmed | Doors/gates. Ex `52 01`, `52 11`. |
| 53 | $D9B2 | 2 | nn (**0-based**) | Redraw map object nn in "closed" state ($9C19); does not touch the persistence bit. | no | BG map | confirmed | Note index base differs from 52. |
| 54 | $D9C9 | 2 | ss | Play sound effect ss ($00:8E8F, APU cmd 9). | no | APU | confirmed | 148 uses. |
| 55 | $D9D3 | 6 | nn (1-based), xxxx, yyyy | Stamp tile pattern nn-1 (7-byte record at $7F:07B0 + 7*(nn-1): ptr, width, height) into the layer-1 map buffer at tile (x,y). Screen not refreshed until 56. | no | $7F map buffer | confirmed | Map 3C puzzle (112 uses). |
| 56 | $DAB5 | 1 | - | Refresh/redraw the visible tilemap ($B7DA). | no (one frame upload) | VRAM | confirmed | Paired with 55. |
| 57 | $DABD | 2 | nn | Set respawn point after game over: $1349 = nn (0 = last town $1348; else map/entrance from $01:EA6C[nn]). | no | $1349 | confirmed | used at $01:8A12. |
| 58 | $DAC6 | 3 | cc, nn | **BROKEN**: intended "max HP bonus $1651[cc] += nn" + recompute stats, but the code was assembled for 16-bit A (`A9 00 00`, `29 03 00`); with M=8 it executes `BRK`. | crash | ($1651) | confirmed | Unused in game; don't offer in editor. |
| 59 | $DAE7 | 3 | cc, nn | Max MP bonus $1659[cc] += nn, then recompute ($08:E884: $16E8 = $15F9 + $1659). | no | $1659,$16E8 | confirmed | Ex `59 01 0A`. |
| 5A | $CEA0 | 1 | - | Wait until screen fade finished ($077B == 0). If busy: Y-1 (re-run 5A), wait 1 frame ($0D26=1), yield. | **yes** while fading | $077B,$0D26,$07B4 | confirmed | |
| 5B | $D3EB | 3 | mm, ee | Same as 4C with transition $0D49=3: no fade-in at the end (screen stays black) - used right before `51` so the chained event fades in itself. | blocks inside | as 4C | confirmed | Ex `5B 0E 01`. |
| 5C | $DB5C | 3 | nn = NPC, tt = period | Hide/blink actor nn+7: $1E=$1F=tt, $1D bit7 set (hidden). Each frame $1F counts down; at 0 reload from $1E and toggle bit7. tt=0 -> stays hidden. | no | actor $1D,$1E,$1F | confirmed | 5C-5F family. Ex `5C 05 01` (blink every frame), `5C 05 00` (hide). |
| 5D | $DB64 | 3 | aa = actor, tt | Same for actor aa. | no | same | confirmed | |
| 5E | $DB7C | 2 | nn = NPC | Show actor nn+7: $1F=0, clear $1D bit7. | no | same | confirmed | |
| 5F | $DB84 | 2 | aa = actor | Show actor aa. | no | same | confirmed | |

### Families
- **30-37**: pure aliases of 0E/0F (even = NPC-relative like 0F, odd = absolute actor like 0E).
- **38/39, 5C/5D, 5E/5F** (and 0E/0F): the "NPC n" form adds 7 (actor = n+7), the other takes the raw actor index.
- **42/43/44**: heal HP / MP / status; operand FF loops over the party (party slot -> character via $0CE9/$01:E240).
- **45-49, 4F**: conditional jumps "if X >= value goto"; all jump via $C700 (offset from event start).
- **4C/5B**: warp (5B = keep screen black). **4D/4E/4F**: key-item bits = items F0-FF.
- **52/53, 55/56**: map tile edits (objects / stamps + refresh).

### 50 sub-functions (table $01:D49D, 33 entries)
00 open the object the player is interacting with (ptr $127E) · 01 church: list members needing cure (status & $32) -> $127A, flag FF = any · 02 church: next member, sets text vars $1276-$1282 (cost 5/50/level*20) , FF = more · 03 church: pay $127F gold, on success FF + clear status, HP>=1 · 04 reset party to hero only (fade out, clears $134E-$1363, $8C=1, gold-related $1577/$1579 = 0, reload) · 05 fade out + wait · 06 refresh sprites, fade in + wait · 07 $16BA=0 · 08 fade (mode 1 $00:96FB) + wait, refresh · 09 $0D9A=$80 (calls $DF63 after 128 frames) · 0A save text speed $0541 -> $0D9C and set 0 · 0B restore $0541 · 0C $0CEF=0 · 0D $0CEF=1 · 0E refresh actors ($A311) · 0F/10/11 curse removal: list members with cursed equipment ($17B4 bit 2), next (price $17B8/2), pay & unequip · 12 $93=FF (lock music: music-change command 0A ignored) · 13 $93=0 unlock · 14 $DF63 (close a window/overlay layer) · 15 force-equip item $23 on Hero slot 0 (old weapon to bag) · 16 remove item $23 from Hero/inventory · 17 count items E3/E4 -> text vars, FF if any · 18 trade min(E3,E4) pairs for item 7F · 19 special screen/sequence ($CE07,$00:9777,$89E6) · 1A level up every member twice (lv<99) · 1B set SRAM $70:1801=FF, $01:F012 (completion flag) · 1C $0D1E=FF · 1D give item $127F x1, FF if ok · 1E FF = ($0D9E != 0) · 1F switch to tileset/screen $12 · 20 no-op. (all inferred except dispatch)

## Shared RAM structures

**Actor table** `$07DE + $20*n` (address from $01:AC38). n = 0..5 party sprites (party order list at $8D.., size $8C), n = 6 vehicle, n >= 7 map NPC n-7.
| off | meaning |
|---|---|
| +00 / +03 | X / Y fine (sub-tile) offset |
| +01-02 / +04-05 | X / Y tile coordinate (word) |
| +06 (word) | sprite/graphic id ($0302 special) |
| +08 | height/layer (used for Y draw), +09 bitmask from $01:E237 |
| +0A | step counter, +0B facing direction (0-3) |
| +0D / +0E | anim frame count / current frame |
| +13 (word) | movement parameter pointer |
| +15..+18 | movement step state (+17 speed/steps, +18 moving flag) |
| +19 (word) | movement script pointer: 0 = free AI/wander, $FFFF = frozen, else script |
| +1B | AI wander timer |
| +1D | flags: bit7 hidden (not drawn), bit6/5 movement modes, bits0-2 voice -> text blip SFX via $01:E2F3 |
| +1E / +1F | blink period / blink countdown (5C-5F) |
`$07B6` (40 bytes) = actor draw list.

**Script/text state**: $0D0D speaker actor, $0D09 text blip SFX, $0D26 wait-frame counter, $07B4 script-suspended flag, $0D0F-11 event base, $0D12-14 saved script pointer, $0D0B/$0D0C current map / map-local flag base, $0D20/$0D49/$0D4B/$0D4C pending warp (entrance, transition type, entrance-1, map), $077F current map, $077B fade busy (1 out, 2 in).

**Party**: $156F level[4], $157B status[4] ($20 dead, $02 poison?, $10 ?), $157F HP[4w], $1587 MP[4w], spell lists $158F/$15AF/$15CF (32 each), $15EF-$15F0 key items F0-FF (bit = id-F0, LSB first), $15F1 base max HP[4w], $15F9 base max MP[4w], $1601.. base stats, $1651 max-HP bonus[4w], $1659 max-MP bonus[4w], $16C0 equipment (6 slots x 4), $16E0 effective max HP[4w], $16E8 effective max MP[4w]. Gold $14CF (24-bit). Inventory $14F7: 60 x {item, qty<=99}. Story flags $1296 (flag FF = result flag). Map object persistence bits $12B6.

## Length table corrections
- **51: 0 -> 3** (`51 mm ee`). It is an unconditional transfer to another event: add to `EndsFlow`.
- All other lengths in 30-5F in `CommandLength` are correct (30-37 = opener 2 bytes + text, as -2).
- Not lengths but needed for safe relocation: `JumpPositions` is missing event-relative jumps for **45 (byte 3), 46 (3), 47 (3), 48 (4), 49 (4), 4F (2)**.
- 58 is a crashing command (never used) - should not be inserted.
- Outside my range, seen in passing: range op 90-AF calls $CB9C with A = op-$90, does DEC then +8, so 90+N = actor N+7 (NPC N), not "actor 8+N" as `SpeakerName` says.


Method: static disassembly of the handlers (bank $01) and their callees, counting reads through
$01:C740 (1 byte) and $01:C753 (2 bytes). "Continue" = handler BRLs back to $C66E. "Yield" = handler saves the
script pointer (JSR $C728) and exits via $C6EC; the map loop then counts `$0D26` down once per frame and resumes the
script at the saved pointer. A handler that rewinds Y (JSR $CEBB = Y-1, crosses banks) before yielding re-executes
itself next frame (a polling wait).

Direction encoding (confirmed from the joypad code at $01:8B9B-8C03: Right->0, Left->1, Down->2, Up->3):
**0 = right, 1 = left, 2 = down, 3 = up.**

Actor ids: the actor record is `$07DE + id*$20` (`$01:AC38`). 1 = party leader (the player-controlled sprite), 2-6 = party
followers, 7+ = map NPCs (NPC n = actor 7+n). `$8C` = party size, `$8D...` = the actor ids of the party in order.

| opcode | handler | length | operands | semantics | pauses? | RAM touched | confidence | notes |
|---|---|---|---|---|---|---|---|---|
| 60 | $DB97 | 2 | `bb`: low nibble = flash effect index (0 or 1), high nibble = repeat chance | **Start a lightning/screen-flash effect.** `$0D56=(bb&F)+1` picks a record from the pointer table at $01:80F1 (only 2 records: #0 $80F5 = 8-step white flash plus sound effect $10 (thunder), #1 $8107 = 10-step flash with no sound). `$0D57=FF` makes the first flash fire right away. After that, `$01:8097` (run every frame) starts a new flash when `random(0..FF) < $0D58` (`$0D58 = bb>>4`), so with a high nibble of 0 you get a single flash. Each step adds a colour from the record to the palette buffer (`$7E:037F...`, $01:811D) and sets `$0D59` so that NMI DMAs CGRAM | no (continues) | $0D56 effect+1, $0D57 step counter, $0D58 repeat threshold, $0D59 palette-DMA request | confirmed (mechanism); "lightning" is inferred from the white ramp and thunder SFX | effect index >= 2 reads past the 2-entry table (invalid) |
| 61 | $DBB1 | 1 | - | **Stop the flash effect.** It busy-waits frame by frame (polls `$38`, calls $8097 itself) until the current flash ends (`$0D57==0` and `$0D59==0`), then clears `$0D56` | **yes, blocking**: a synchronous loop inside the interpreter, so the map loop does not run meanwhile | $0D56-$0D59 | confirmed | |
| 62 | $DBCC | 6 | `sb rr yy xx gg` | **Start an "orbiting sprites" ring effect** (spinning orbs or a magic circle) in effect slot `sb&7`. `sb>>3`+1 = rotation speed (stored as both current and target). `rr` = radius (current and target). `yy` = centre Y tile (stored as yy*16 px). `xx` = low 8 bits of the centre X tile. `gg` bits 0-3 = number of arms - 1 (arms spread evenly over 360 degrees, each arm a trail of 4 sprites); bits 4-6 = sprite graphic (sprite id $46+n, valid n=0-3); bit 7 = bit 8 of the X tile. 62 sets `$0B=0` (angle increases each frame) | no (continues). It may spin a frame or two waiting for the sprite graphics DMA ($06A2) | slot table $0D5E (+15*slot), active flags $0D5A-$0D5D, sprite VRAM slot list $0553 | confirmed (layout and reads); visual meaning inferred | Only 4 slots exist ($0D5A..$0D5D, loop at $01:A83A); slot 4-7 would corrupt RAM. If the slot is already active, only the parameters are updated and the graphics are not reloaded |
| 63 | $DBD0 | 6 | same as 62 | Same as 62 but the ring turns the other way (`$0B=1`: angle decreases, arm spacing reversed) | no | same | confirmed | |
| 64 | $DC98 | 2 | `ss` | **Stop and remove ring-effect slot `ss`** (`$0D5A[ss]=0`) | no | $0D5A+ss | confirmed | not masked: ss must be 0-3 |
| 65 | $DB06 | 2 | `pp` | **Start horizontal screen shake.** `$0D55=(pp&0F)+1` = amplitude in px. `$0D53=(pp>>3)&7` = frames between toggles (bit 3 is shared with the amplitude). Each frame (00:8401, NMI side) BG1 and BG2 HOFS alternate between +0 and +amplitude. Also runs one $01:8E91 pass with temporary `$62/$72=5, $63/$73=FF` | no (it keeps running until 67) | $0D51=+1, $0D52 phase, $0D53 period, $0D54 counter, $0D55 amplitude | confirmed | |
| 66 | $DB0A | 2 | `pp` | Same as 65 but **vertical** shake (`$0D51=FF`, writes BG1 and BG2 VOFS) | no | same | confirmed | |
| 67 | $DB56 | 1 | - | **Stop screen shake** (`$0D51=0`) | no | $0D51 | confirmed | scroll returns to normal on the next NMI |
| 68 | $DCA7 | 4 | `sb rr dd` | **Change a running ring effect** (62/63) in slot `sb&7`: target speed `(sb>>3)+1` (current speed moves 1 per frame toward it), target radius `rr`, radius step per frame `dd+1` | no | $0D5E slot +7 (target speed), +9 (target radius), +A (radius step) | confirmed | used for grow, shrink and accelerate animations |
| 69 | $DCC8 | 2 | `nn` | **Auto-advance text mode ON.** If it is not already on: saves the message speed `$0541` into `$0D03` (stored +1) and sets the message speed to 1. Then `$0D04=$0D06=nn*8+1`: every text box closes by itself after that many frames, with no button press needed ($01:C533). With nn=0 it also sets `$0D9A=FF` | no | $0D03 saved speed/flag, $0D04 reload, $0D06 counter, $0541 message speed, $0D9A | confirmed (auto-advance); $0D9A role inferred | pair with 6A |
| 6A | $DCF8 | 1 | - | **Auto-advance text OFF**: restores `$0541`, clears `$0D03`, and clears `$0D9A` if it is FF | no | same | confirmed | |
| 6B | $DD11 | 3 | `xx yy` | **Set the dialogue window position**: `$0CFD=xx` (x tile; any value >= $20 = centre horizontally), `$0CFE=yy` (y tile). Used by the window layout code at $01:C449. 0 = automatic placement (above or below the speaker) | no | $0CFD, $0CFE | confirmed (store and use); exact units inferred | reset to 0 at script start ($01:C2E4), so it lasts until the event ends |
| 6C | $DD20 | 2 | `nn` | **Wait for a sound-driver sync point.** Calls 00:8F81 (APU command 6 via $2142/43, returns port0+1). If the result is < nn, it rewinds 2 bytes and yields 1 frame, so it polls until the value is >= nn. Probably "wait until the music reaches marker/position nn" | yes, until the APU value >= nn (1-frame polling) | $0D08 (nn), $0D26=1, $07B4=FF | mechanism confirmed; meaning of the APU value inferred | **not "wait nn frames"** as the current Describe() says |
| 6D | $DD48 | 2 | `nn` | **Set the message-window colour scheme `nn-1`** (JSR $DD52): `$0D01=nn-1`, copies 4 BGR555 colours from $0A:C210+(nn-1)*8 (and the shared colours from $0A:C244) into the palette buffer at $033F..., sets bit 2 of `$3D` (palette upload). 0 = normal blue, 1 = darker blue, 2 = near-black, 3-6 = other tints | no | $0D01, $033F-$037F palette buffer, $3D | confirmed | The text openers call $DD52 too (0D/88-AF -> scheme 0 or 1, 0E/0F/30-37 -> 0). Only 0C does not, so 6D lasts only until the next non-0C opener. Valid nn = 1-7 |
| 6E-7F | (none) | invalid | - | No handler: the 128-word table at $C78C overlaps the range table at $C868 from index 6E. The words read are 6E $E1F0, 6F $E0DD, 70 $DDDB, 71 $D7D0, 72 $C0DD, 73 $DDCB, 74 $BDB0, 75 $90DD, 76 $CB9C, 77 $A488, 78 $80CB, 79 $DDA9, 7A $FE80, 7B $FFA9, 7C $148D, 7D $FA0D, 7E $6182, 7F $A9FE. Most land mid-instruction (crash). 70/73/76/79 happen to land on the E0/C0/90/80 group handlers, but with A = op*2 they act on junk flag, actor or wait-table indices | - | - | confirmed | Running into one of these means a parser desync. No real script uses them |
| 80-87 | $DDA9 | 1 | low 3 bits = n | **Wait.** `$0D26 = table[n]*4` frames with table $01:DDB5 = 1,2,3,5,10,15,20,25, giving **4, 8, 12, 20, 40, 60, 80, 100 frames**. It shares $CBDB with opcode 0B (0B nn = wait nn*4 frames) | yes (yield) | $0D26 wait counter, $07B4=FF | confirmed | The existing note "1,2,3,5,... frames" is off by x4 |
| 88-8F | $CBA4 | 1 + text | low 3 bits = actor 0-7 | **Actor (op-88) speaks.** `$0D0D=actor` (the window is placed near that actor if it exists). `$0D09` = voice blip from $01:E2F3[actor.$1D & 7]. Window colour scheme 0, then text mode $C693 | text: the box waits for a button (or for 69 auto-advance) when the text ends with 04 | $0D0D, $0D09, $0D01 | confirmed | 8F = actor 7 = NPC 0 |
| 90-AF | $CB9C | 1 + text | low 5 bits = n | **Actor (8+n) speaks** = NPC n+1 (actor ids 8-27h). Same as 88-8F | same | same | confirmed | same entry also used by 0F/30/32/34/36 (`aa` -> actor aa-1+8) |
| B0-BF | $DDBD -> $CD27 | 1 | op-B0 = `dddd aa`: bits 2-3 = direction, bits 0-1 = actor-1 | **Party actor faces a direction.** Actor = (op&3)+1 (1 = leader, 2-4 = followers). Direction = (op>>2)&3: B0-B3 right, B4-B7 left, B8-BB down, BC-BF up. Writes actor `$0B` (facing) and sets `$3E=FF`. This is the same code path as 28-2F (28/29 right, 2A/2B left, 2C/2D down, 2E/2F up), which is face-only with no movement | Normally no. If the same actor was already turned this frame (`$0D28==actor` and `$3E` still set because the NMI has not run yet), it rewinds 1 byte and yields 1 frame so every facing gets displayed | actor rec +$0B, $0D28 last turned actor, $3E | confirmed | |
| C0-CF | $DDCB -> $CA77 | 3 | `ww` = jump target (relative to event start, like 01/04/05) | **If flag (F0 + (op&F)) is SET, goto ww**, else continue | no | story flags $1296+ (flags F0-FF = bits of $12B4-$12B5, masks $032B) | confirmed | A compact form of `04 F0+n ww`. **Needs JumpPositions = [1]** |
| D0-DF | $DDD7 -> $CA77 | 3 | `ww` = jump target | **If flag (F0 + (op&F)) is CLEAR, goto ww** | no | same | confirmed | A compact form of `05 F0+n ww`. **Needs JumpPositions = [1]** |
| E0-EF | $DDDB -> $CA93 | 1 | - | **Set flag F0 + (op&F)** (same as `06 F0+n`) | no | $12B4-$12B5 | confirmed | |
| F0-FF | $DDE1 -> $CAAA | 1 | - | **Clear flag F0 + (op&F)** (same as `07 F0+n`) | no | $12B4-$12B5 | confirmed | FF = clear flag FF. Note that FF is used 32 times |

## Shared RAM structures

* **Actor records**: `$07DE + id*$20` (address from $01:AC38). Fields seen here: +$00 sub-pixel/step state, +$01-02 X (16-bit), +$03,
  +$04-05 Y (16-bit), +$0A, **+$0B facing direction (0 R, 1 L, 2 D, 3 U)**, +$0C sprite attributes (bit 6 = may H-flip; right
  uses flipped frames), +$0D frames per direction, +$1D flags (bit 7 set by 5C/5D, bits 0-2 = voice index), +$1E/+$1F.
  `$07B6+id` != 0 means the actor exists. `$8C` = party count, `$8D...` = party actor ids (leader first).
* **Ring-effect slots (62/63/64/68)**: 4 slots. Active flag `$0D5A+s` (FF = active), record `$0D5E + s*15`:
  +$00-01 centre X (pixels x16, 9-bit tile), +$02-03 centre Y (tile x16), +$04 current angle (0-179 = 360 degrees, 2-degree units),
  +$05 arm count, +$06 current angular speed, +$07 target speed, +$08 current radius, +$09 target radius,
  +$0A radius step, +$0B rotation sense (0 for 62, 1 for 63), +$0C palette*2, +$0D-0E OBJ tile base (from $0553 list).
  Processed every frame by $01:A83A, drawn by $A8DE (4 sprites per arm, using a sine table).
* **Flash effect**: `$0D56` effect+1 (0 = off), `$0D57` step (FF = fire now, 0 = idle), `$0D58` random repeat threshold,
  `$0D59` CGRAM DMA request (handled at 00:84F7). Records at $01:80F1: [count][sfx][count+1 BGR555 brightness words].
* **Screen shake**: `$0D51` (0 off, 01 horizontal, FF vertical), `$0D52` phase, `$0D53` period, `$0D54` countdown, `$0D55` amplitude.
  Applied at 00:8401 onto BG1 and BG2 scroll registers (base `$3F/$41/$43/$45`).
* **Text control**: `$0D0D` speaker actor, `$0D09` voice blip, `$0D01` window colour scheme, `$0CFD/$0CFE` forced window
  position, `$0D03/$0D04/$0D06` auto-advance (69/6A), `$0541` message speed (option), `$0D26` script wait counter
  (frames), `$07B4` "script waiting" flag, `$0D28` last turned actor (cleared at script start, $01:C26C).
* **Story flags**: `$1296 + (f>>3)`, bit mask `$032B[f&7]`. The compact opcodes C0-FF always use flags F0-FF
  (`$12B4-$12B5`), probably scratch/temporary flags.

## Length table corrections

All lengths in `EventScript.CommandLength` for 60-6D and 80-FF are **correct**: 60=2, 61=1, 62/63=6, 64-66=2, 67=1, 68=4, 69=2, 6A=1,
6B=3, 6C/6D=2, 80-87=1, 88-AF=1+text, B0-BF=1, C0-DF=3, E0-FF=1, 6E-7F=invalid (0).

Other corrections:
1. **`JumpPositions` is missing C0-DF**: these carry a 16-bit event-relative jump target at byte 1. Without it, inserting or
   deleting lines will not relocate these jumps, and their targets are not marked as labels while decoding (`targets`), so code
   reached only through C0-DF jumps can be cut off after an `00`.
2. `Describe`: 6C is not "Wait nn frames" (it polls the sound driver). 80-87 are waits of 4/8/12/20/40/60/80/100 frames.
   C0-FF should be shown as flag tests, sets and clears of F0-FF. B0-BF and 28-2F are "face direction" commands.
3. The ASCII-looking examples ("63 6B 20 73 63 61", "63 69 6F 75 73 21", "68 6F 74 20", "68 69 72 64", "C9 20 61", "64 79", "D0 20 0C" ...)
   are **not** caused by wrong command lengths in this range. They come from `PlausibleEvents` returning bogus event starts
   that point into dialogue text or pointer tables. For example, 07/22 starts at 0x1EBF3 inside "...awful! Jack scares...",
   04/0 starts inside "hot milk", 16/22 inside "I'm cute or ...!", and 35/20 inside "Delicious!". The stop at 0x22FD6
   ("unknown 76") is a pointer table (`16 00 39 00 72 00 ...`). The 72/6F/6E stops at 0x248FF/0x26D6A/0x1F209/0x206D3/0x21DC8 are
   also inside dialogue. Filter events whose first opcode is 6E-7F, or that decode into ASCII runs.
4. Related to text but outside this range: in the text interpreter ($C693, code table $C75C), codes **01, 02 and 03 also end
   the text and return to command mode** (like 04, with box-close codes 11/12/13). **08 and 0E end the script** like 00.
   `DecodeText` only stops at 00/04, which can desync on real scripts that use 01-03/08/0E. Text code 0F takes
   exactly 1 operand (31-34, then it runs a RAM sub-string at $1276+8n), 06/07/09/0A/0B/0C/0D take 1 operand, and 10-1F are 1 byte.
