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
| 0F | 0 | go to a script picked by the weapon (a table of words follows): only the weapon swings 00-11 |
| 10-13 / 18-1B | 1 / 0 | repeat n times / end of that loop |
| 20 | 5 | b, w1, w2: graphics. w1 → file (`$85:EF53`, 0x40 entries) unpacked to `$7E:A000`, sent to VRAM $4000 (the sprites' 256 tiles, name table 0); w2 → `$7E:DF00`, VRAM $1800 (a background map). Index 36 names no file |
| 27 | 4 | w, a: file of index w unpacked to `$7E:a` |
| DD / DE / DF | 2 | 16 colours from `$96:8000` + 32 w into colours $10 / $90 (sprite palette 1) / $A0 (sprite palette 2), and their copy at `$7F:F1DB` |
| 2C | 5 | row, column, count, w: colours from `$96:8000` + 32 w |
| 72 | 1 | sound effect |
| 89 | 4 | the object's sprite: attributes, tile, priority, size |

The others (21-23, 27-34, 50-6B, 70, 71, 80-9D, C0-C2, DA-DC, EB-F1) move, scroll, fade, tint and shake; their
argument counts are in L2SpellAnims.cs. Following every pointer above, all 125 animations parse with no unknown
opcode (9615 script bytes); the weapon swings 00-11 use 0F.

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
(runs of consecutive steps kept together, every script address moved with them), and the graphics and colour rows
it loads are copied too (new graphics indexes, rows from $200), so changing it changes no other animation. Spells get
it on this page or the Spells page (L2Magic's animation byte per spell), IP attacks on this page or the IP attacks
page (the record's animation byte), items as word 0x2000 on the Items page.

## 5. Checked in the game

New game, battle 53 04 39 (a Goblin made passive), Maxim casting Flash from his spell list (L2StartSpells, 0 MP),
BizHawk screenshots every frame from `$81:895E`:

* a copy of Flash (37) with its sprite colours turned red and its tiles mirrored: Maxim's Flash shows the red,
  mirrored bolt and sparks, 32 damage as before;
* the same made through the page in the window (Make a copy "Red flash" = 3A, colours set one by one, graphics
  exported to PNG and imported back, a few pixels painted, Give to this spell): Flash shows animation 3A in the game;
* every spell animation 12-36 copied plays frame for frame like the game's in the player; a copy of Dragon with its
  step lengths doubled plays 878 frames instead of 456;
* the fan hacks Frue, Kureji, Spekkio Lufia and The Wrath of Foomy v8.2 keep the game's code there: a copy of
  Firebird made in each plays its 304 frames.
