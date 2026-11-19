# Lufia II battle animations (2026-10-27)

Found by reading the code (bank $81) and checked in BizHawk (bsnes core). Lufia Forge: Core/Lufia2/L2SpellAnims.cs
(scripts, graphics, colours, timing, new animations), Core/Lufia2/Snes (the player), the Lufia II tab's Battle >
Animations page.

## 1. How the game plays one

* `$81:895E` (A = the animation, 21 callers) plays animation n and returns when it ends. `$1262` holds the one a spell,
  IP attack or item asked for (battlescript.md section 7). `LDA $958000,X` (`$81:899A`) gives the first script's
  address, and `LDA #$95 / STA $C5` (`$81:89A4`) its bank: every script of an animation is in that bank and the
  scripts point at each other with 16-bit addresses. 0 in the table = no animation.
* Free numbers: 37-3F (37-39 are named by spells 25-27, which have no animation), 47-5F, 73-7F, 9C-DB, EA-EF, F2-FE.
* The engine runs 30 tracks (`$7E:4F8F`, 0x2C bytes each) and 64 objects (`$7E:54B7`, 0x2D bytes each, one hardware
  sprite each) once a frame (`$81:8A76`, waiting at `$85:EC81` for the frame counter `$40`). Each runs a script with
  the opcode table `$81:8C5C` (256 entries; `$81:8C5A` = a dead loop, and 24-26, 29-2B, 32, 35, 40-4F, 58, 86, 8A-8D,
  92, 93 are BRK: not usable).
* +0 active, +1 frames left to wait, +2 the step length, +3 the script address, +5 the return address, +7/+0A/+0D/+10
  loop counters with the loop start after each.

## 2. The opcodes Lufia Forge reads (argument bytes in brackets)

| Op | Args | What it does |
|---|---|---|
| 00 | 0 | end (the track or object stops) |
| 01 | 0 | wait one step (+2 frames) |
| 02 | 1 | step length n frames |
| E0-E8 | 0 | step length 1, 2, 3, 4, 5, 6, 8, 10, 20 |
| 03 / 04 / 05 | 2 / 2 / 0 | go to w / call w (one level, return address at +5) / return |
| 06 | 4 | if the byte at +b of the track is v: go to w (w b v) |
| 08, 09, 0C, 0D, 0E, 56, E9, EA | 4, 2, 2, 2, 2, 2, 2, 3 | start a track at w (where: the target, every target, the attacker...) |
| 0A, 8E, 96, D0-D9 | 5, 5, 6, 2-3 | start an object at w |
| 0B | 1 | call a routine of the table `$81:8E5C` |
| 0F | 14 | go to the attacking hero's script: 7 words follow, one per character (section 2a); only the weapon swings 00-11 |
| 10-13 / 18-1B | 1 / 0 | repeat n times / end of that loop |
| 20 | 5 | b, w1, w2: graphics. w1 → file (`$85:EF53`, 0x40 entries) unpacked to `$7E:A000`, sent to VRAM $4000 (the sprites' 256 tiles, name table 0); w2 → `$7E:DF00`, VRAM $1800 (a background map). Index 36 names no file |
| 27 | 4 | w, a: file of index w unpacked to `$7E:a` |
| DD / DE / DF | 2 | 16 colours from `$96:8000` + 32 w into colours $10 / $90 (sprite palette 1) / $A0 (sprite palette 2), and their copy at `$7F:F1DB` |
| 2C | 5 | row, column, count, w: colours from `$96:8000` + 32 w |
| 72 | 1 | sound effect |
| 89 | 4 | the object's sprite: attributes, tile, priority, size |

The others (21-23, 27-34, 50-6B, 70, 71, 80-9D, C0-C2, DA-DC, EB-F1) move, scroll, fade, tint and shake; their
argument counts are in L2SpellAnims.cs. Following every pointer above (and every word of 0F's table), all 125
animations parse with no unknown opcode; the weapon swings 00-11 use 0F.

### 2a. Weapon swings: op 0F (2026-10-28)

`$81:94CA` (A 8-bit, Y = the track): `LDA $7E:0025,Y` is the track's attacker, a party slot (0-3; a monster's has
bit 7 set, 4 is the capsule monster: both go to a BRK, so the game stops). `LDA $153D,X` is that slot's character,
copied from the party list `$0A7B` when the battle starts (`$81:807A`): Maxim 0, Selan 1, Guy 2, Artea 3, Tia 4,
Dekar 5, Lexis 6. The script address becomes the word at (the address after 0F) + 2 x character. So the table is
picked by **the hero who attacks**, not by the weapon: the weapon picks the animation number (the item's word 0x2000 =
animation + 1, `$81:A8B7`; none = 0E, a punch), and the animation's table picks that hero's script. The table always
has 7 words: in the game the next byte is the next animation's first step.

Every swing n is `0C` (start a track at the next byte), `00`, then `0F` and its 7 words. Each hero has a block of
eight entries (Maxim $88CD, Selan $891E, Guy $896F, Artea $89C0, Tia $8A11, Dekar $8A62, Lexis $8AB3, 0x51 bytes
apart), each entry `20 01 gg 00 01 00` (graphics gg) and `03` to that hero's swing script, then `00`. Swing 2k points
at entry k; the odd swings, 0F, 10 and 11 point at the `00` after an entry, so they show nothing:

| Swing | Graphics | Weapons in the US game |
|---|---|---|
| 00 | 29 | swords, knives, rapiers |
| 02 | 2A | rods, staffs, maces, flails, Frypan |
| 04 | 2B | bows, Launcher |
| 06 | 2C | spears, picks, Superdriver |
| 08 | 2D | whips |
| 0A | 3C | Vice pliers, Coma hit, Figgoru, Stun gun, Battledriver |
| 0C | 3D | axes |
| 0E | 2E | a punch (no weapon) |

A visible swing reaches 929 steps (1769 bytes: the seven heroes' scripts and what they start); its colour rows
$200-$206 in a copy (one per hero, colours 1 and 2 differ) come from the game's.

## 3. Playing it in Lufia Forge

The page runs the game's own code: Core/Lufia2/Snes has a 65816 interpreter (every opcode, decimal mode), the
console's memories (work RAM, VRAM, colours, sprites), DMA and HDMA, the multiply / divide unit, and a picture unit
drawing modes 0-1 with sprites, windows and colour maths. The sound chip only answers the game's handshake (ports 2
and 3 say CD EF to a command, 00 00 to 00 00), so animations play silent.

A battle is set up as the game has it when it starts an animation: the work RAM of a real battle ($0000-$1FFF,
$2800-$2EFF, $4F00-$4FFF, measured in BizHawk: Maxim against one monster), the battle's screen registers
(`$97:B5D3`, as `$81:8633` writes them), a battle background on BG1 (from its record, as on the Graphics page) and
the monster the spell lands on (drawn by Lufia Forge behind the animation's sprites; the battle's own character
sprites, name table 1, are left out). Then `$81:895E` runs, and every time the game waits for a frame the frame
interrupt (`$80:862D`, the battle's `$85:8DC5` through `$0067`) runs and the picture is drawn.

Checked: Flash and Firebird played from the work RAM, VRAM and colours BizHawk saved at `$81:895E` look like the
game's screenshots frame for frame (the Firebird's red sky, white flash, the bird and its fire). From the built-in
battle all 242 animation numbers play to their end.

## 4. What Lufia Forge changes (only when the first new animation is made)

Every site is checked to hold the game's bytes first; otherwise the page says another hack changed the code and
makes nothing.

| Site | Game's bytes | Becomes | Why |
|---|---|---|---|
| `$81:899A` | BF 00 80 95 8F 92 4F 7E | JSL AnimStart, 4 NOP | an animation's first script and its bank from Lufia Forge's table (bank 0 there = the game's table) |
| `$81:89A4` | A9 95 85 C5 | 4 NOP | (AnimStart sets `$C5`) |
| `$81:95CF`, `$81:960D`, `$81:966E` | BF 53 EF 85 | JSL GfxIdx | graphics indexes from $40 become `$8000` + index |
| `$81:95E6`, `$81:9624`, `$81:9684` | 22 9D 8E 80 | JSL GfxLoad | `$8000` + index: packed data of Lufia Forge's, unpacked by the game's unpacker from `$80:8EBD` |
| `$81:913E` | A9 96 85 0A | JSL PalHook | colour rows from $200 (addresses from $C000) come from Lufia Forge's bank |

The bank ("LF2AN", 32 KB of the added space): header (+6 new animations, +8 next free byte, +0A graphics count,
+0C colour rows), the hooks at +10, 256 script addresses at +100, 256 banks at +300, the graphics pointers (u24, index
$40 up) at +500, a 16-letter name per animation at +740, scripts from +1800, colour rows from +4000 ($C000).

**A new animation** is a copy of another: every step reachable from its first script is laid out again in that bank
(runs of consecutive steps kept together, every script address moved with them, all 7 words of a 0F table too; table
words naming the same script share one copy), and the graphics and colour rows it loads are copied too (new graphics
indexes, rows from $200), so changing it changes no other animation. A copy that would have two steps sharing bytes
(a jump into the middle of a step) is refused; no game animation does that. Spells get it on this page or the
Spells page (L2Magic's animation byte per spell), IP attacks on this page or the IP attacks page (the record's
animation byte), weapons and items on this page ("Give to this weapon or item") or the Items page, as word 0x2000
(the record's own, or a word Lufia Forge adds when the record has none).

The scripts' space is 10 KB (+1800 to +4000): five copies of a visible swing (1769 bytes each), or a swing and many
spells. A copied swing only works for a hero: given to a spell a monster casts, its 0F stops the game as the game's
own would.

The preview plays the seed battle's attacker, Maxim in slot 0; the page's Hero list sets `$153D` to another
character, so a swing shows that hero's script (Maxim 39 frames, Selan 41, Guy 39, Artea 46, Tia 42, Dekar 43,
Lexis 40 for swing 00). In the opcode handlers ($81:8C5A-$A770) only 0F reads `$153D`.

## 5. Checked in the game

New game, battle 53 04 39 (a Goblin made passive), Maxim casting Flash from his spell list (L2StartSpells, 0 MP),
BizHawk screenshots every frame from `$81:895E`:

* a copy of Flash (37) with its sprite colours turned red and its tiles mirrored: Maxim's Flash shows the red,
  mirrored bolt and sparks, 32 damage as before;
* the same made through the page in the window (Make a copy "Red flash" = 3A, colours set one by one, graphics
  exported to PNG and imported back, a few pixels painted, Give to this spell): Flash shows animation 3A in the game;
* every spell animation 12-36 copied plays frame for frame like the game's in the player; a copy of Dragon with its
  step lengths doubled plays 878 frames instead of 456;
* weapon swings (2026-10-28): a copy of the sword swing 00 (3A) with its seven colour rows turned red, given to the
  Small knife (item 039, Maxim's weapon in the new game: `$0A09` = $96:D8D5): Maxim's attack plays animation 3A
  (`$1262` = 3A, `$0BA7` = 3B) with a red slash where the game's own (the same battle, nothing changed) is gold, same
  frames; every swing 00-11 copied has the same steps as its source (every hero's branch), plays frame for frame like
  it in the player for all seven heroes, and leaves the other 125 animations as they were; 15 copies fit one ROM
  (all the swings but three of the big ones), a copy of a copy is the same too;
* the fan hacks Frue, Kureji, Spekkio Lufia and The Wrath of Foomy v8.2 keep the game's code there: a copy of
  Firebird made in each plays its 304 frames.
