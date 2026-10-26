# Lufia II (US) battle-script interpreter ($85:B460, table $85:B483): every opcode

Static analysis only. Tools under l2research\bs\: `dump85.py` -> `all85.txt` (every handler), `count85.py`
(script bytes consumed per handler, following subroutines), `loop85.py` (no handler has a loop that reads script
bytes), `bparse.py` (parser + crawler over all owners).

**Result: 828 entry points (items, spells, monsters, capsules, capsule attacks, the shared library) -> 1551 parsed
script paths, 0 errors.** Byte coverage between records: spells 100%, library 100%, items all but 8 single bytes
(a stray `00` after a script), monsters all but 4 known original-data bugs (Bolt Fish, Copper Dragon, Ninja,
Erim: script bytes with no 07/08 entry or skipped by a wrong offset, the same four LEdit's Monster.txt names);
those orphan bytes also decode cleanly.

## 1. Core
* `$85:B452` (JSL entry) -> `$85:B45E`: `JSR $C4DE`, `LDA [$BB]`, `INC $BB`, `JMP ($B483,X)` with X = op*2.
  Script pointer = 24-bit `[$BB]`; jump base (record start) = `$0A42` (bank `$0A44`).
* Table `$85:B483`, **0x5D opcodes (00-5C)**; the next word (`$8F20`) is not a handler. (l2ref's `$B485` is wrong.)
* Readers: `$85:BFBF` byte, `$85:BFCA` u16, `$85:BFD8` **value operand** u16: if bit 15 is set the low byte is a
  register number (`$85:BFED`), else the number itself.
* Registers (`$85:BFED`/`$C023`): r 00-7F = shared word `$7F:F40E + 2r`; r 80-FF = per-combatant word at
  combatant base (`$BE`, or `$C1` when `$0A52` != 0) + 0x15 + 2(r & 7F). Named shared regs used by the handlers:
  10 (`F42E`) failure flag, 20 (`F44E`) attacker, 21 (`F450`) target flags, 23 (`F454`) action type,
  24 (`F456`) action argument, 25 (`F458`) attack name, 26 (`F45A`), 27 (`F45C`) element, 28/29 (`F45E/F460`)
  critical rate / multiplier, 2A (`F462`) damage, 2B (`F464`) damage spread.
* Every handler ends with `JMP $B45E` (next opcode), except **00** (`$B473`: `$0A5B` = 0, return) and **4F**
  (`$B476`: `$0A5B` = FF, return). So only 00 and 4F end a script; 2A (flee) and 41 just set the action and go on
  (the data always follows them with 00). 03 (goto) and 43 (return) end a straight path.
* Jumps: `$85:B551` = `$BB = $0A42 + u16` (offset from the owning record).
* **42 / 43 are call / return into a shared script library**: 42 w saves `$BB/$BD` in `$0A48/$0A4A` and the base
  in `$0A45/$0A47`, then runs library script w: table **`$96:FADD` (file 0xB7ADD), u16 offsets from the table,
  0x29 scripts**, base = the script itself; 43 restores. One level only. The defence scripts' `42 11 00` is "call
  library script 11" (status immunities), not a "weakness" setting.

## 2. Opcode table

Args are the bytes after the opcode (b byte, r register, w u16, v value operand u16 = number or register).
Uses = occurrences in the parsed data. Conf: H = length from code and seen in the data; C = code only (never in
the data); X = handler is `BRK` (crash: opcode unusable).

| Op | Handler | Args | Meaning | Uses | Conf | Lufia Forge now |
|---|---|---|---|---|---|---|
| 00 | B473 | 0 | end (`$0A5B` = 0) | 1300 | H | ok |
| 01 | B53D | 0 | perform the queued action now, continue (`$C28F`, `$81:A832`, `$85:CCCE`, `$C2C9`) | 54 | H | ok |
| 02 | B550 | - | BRK | 0 | X | 0 args (wrong but harmless) |
| 03 | B551 | w | goto w | 159 | H | ok |
| 04 | B560 | w | if reg 10 (failure) != 0 goto w | 162 | H | ok |
| 05 | B582 | b w | goto w with chance b/255 (random 0..FE < b) | 492 | H | ok |
| 06-0B | B598.. | r v w | if reg r (==, !=, >, <, >=, <=) v goto w (06 == read in the code; the order of the others is LEdit's) | 401 | H | ok |
| 0C | B670 | r v | reg r = v | 359 | H | ok |
| 0D | B67C | r v | reg r += v | 7 | H | ok |
| 0E | B698 | r v | reg r -= v | 22 | H | ok |
| 0F | B6B7 | r v | reg r *= v | 55 | H | ok |
| 10 | B6D2 | r v | reg r /= v (signed) | 28 | H | ok |
| 11 | B73B | r | reg r = random below reg r (`$85:DCEA`) | 2 | H | ok |
| 12 | B753 | r r2 | reg r = f(reg r, reg r2) (`$85:DB6D`, result byte `$64`; probably a percentage or ratio) | 0 | C | ok (2) |
| 13 | B77B | r s | reg r = target's stat s (`$C1C7` with reg 21) | 11 | H | ok |
| 14 | B7A8 | r s | target's stat s = reg r (first byte is the register) | 1 | H | ok (2) |
| 15 | B7CF | r s | reg r = own stat s (attacker or target side by `$0A52`) | 62 | H | ok |
| 16-18 | B849.. | r v | reg r &=, \|=, ^= v | 30 | H | ok |
| 19 | B89A | r | reg r = abs(reg r) | 0 | C | 1, unnamed |
| 1A | B8B4 | r | reg r = -reg r | 11 | H | ok |
| 1B | B8CC | r | reg r = sign(reg r) (-1, 0, 1) | 0 | C | 1, unnamed |
| 1C | B8EB | r | reg r = `$11E8` (battle counter, e.g. turn number; guess) | 0 | C | 1, unnamed |
| 1D | B904 | 0 | `$85:CCCE` (the effect step of 01 without the action part) | 2 | H | ok |
| 1E | B90B | b | show attack name b (reg 25), message timer `$1264` = 120 | 213 | H | ok |
| 1F | B91F | w | element = w (reg 27) | 158 | H | ok |
| 20 | B92C | b b | critical rate / multiplier (regs 28, 29) | 67 | H | ok |
| 21 | B93D | w v | physical damage: element w, damage = -v (reg 2A), spread 0x20, action 5 | 173 | H | ok (5) |
| 22 | B96E | w v | magic damage: same layout, action 0A | 49 | H | ok (5) |
| 23 | B9B8 | w | fixed damage w (reg 2A = -w, spread 0x20) (Bunny sword) | 1 | H | ok |
| 24 | B9DA | t v b | restore/modify: stat class t (`$85:9F04` table), amount v, spread b | 47 | H | ok |
| 25 | BA02 | t b | stat change % (`$85:9F04` + 4) | 23 | H | ok |
| 26 | BA28 | e b | cure status e, chance b (`$85:9F0F` table) | 26 | H | ok |
| 27 | BA47 | e b | cause status e, chance b | 117 | H | ok |
| 28 | BA64 | 0 | physical attack (action 1) | 256 | H | ok |
| 29 | BA81 | 0 | defend (action 4) | 30 | H | ok |
| 2A | BA8A | 0 | flee (action 6); does NOT end the script | 35 | H | ok (but treated as an end) |
| 2B | BA93 | w | use item w (action 3) | 0 | C | ok |
| 2C | BAA6 | b | cast spell b (MP check, `$81:F3F4`) | 118 | H | ok |
| 2D | BB15 | b | call monster b (action 7; failure flag if no room) | 59 | H | ok |
| 2E | BB46 | 0 | action 0D | 0 | C | 0, unnamed |
| 2F | BB4F | r | reg r = number of combatants with flag 04 (0 if target bit 7) | 0 | C | 1, unnamed |
| 30 | BB7E | r | reg r = side-dependent count (`$15FE` / `$153C` / `$0A7A`) | 2 | H | 1, unnamed |
| 31 | BBA1 | - | BRK | 0 | X | missing |
| 32 | BBA2 | b | choose target, mode b (sub-table `$85:BBB6`, 9 modes); clears reg 10 | 613 | H | ok |
| 33 34 | BCCD BCCE | - | BRK | 0 | X | missing |
| 35 | BCCF | b | `$0A62` = b (special effect / message) | 66 | H | ok |
| 36 | BCD8 | b | if reg 10 != 0: `$0A62` = b | 0 | C | **missing** |
| 37 | BCED | 0 | weapon attack (action 1, damage from ATP etc.) | 117 | H | ok |
| 38-3B | BD2B | - | BRK | 0 | X | missing |
| 3C | BD2C | 0 | add weapon part (`$C05F` 8) to the damage in reg 2A | 3 | H | ok |
| 3D | BD54 | b | `$81:895E` b (sound effect, guess) | 86 | H | 1, unnamed |
| 3E | BD5E | b | capsule attack b (action 0B) | 103 | H | ok |
| 3F | B652 | b w | if capsule slot b learned goto w | 35 | H | ok |
| 40 | B8F7 | r | reg r = learned mask (`$C1A1`) | 0 | C | ok |
| 41 | BD6E | 0 | action 0C ("wait" / check); does not end | 11 | H | ok (treated as end) |
| 42 | BD77 | w | **call library script w** (`$96:FADD`) | 812 | H | 2, named "weakness" |
| 43 | BDB2 | 0 | **return** from 42 | 71 | H | **missing** |
| 44 | BDCB | i b | battle byte i (`$14E6[$C0BE(i)]`) = b | 0 | C | 2, unnamed |
| 45 | BDDA | r i | reg r = battle byte i | 0 | C | **missing** |
| 46 | BDF3 | i w | if battle byte i == 0 goto w (else skip w) | 25 | H | 3, not followed as a jump |
| 47 | BE06 | v | `$1264` = v, `$85:95FE` (timed effect) | 0 | C | ok (2) |
| 48 | BE13 | 0 | `$85:9671` | 0 | C | **missing** |
| 49 | BE1A | 0 | wait until `$1264` == 0 (busy loop) | 0 | C | **missing** |
| 4A | BE2B | b | repeat b times `$85:8A2F` + `$85:EC81` (flash/shake; guess) | 0 | C | **missing** |
| 4B | BE43 | 0 | `$81:C35F` with A = 4 | 0 | C | **1 (wrong: code reads none)** |
| 4C | BE4C | b | `$81:C35F` with A = b | 1 | H | ok |
| 4D | BE56 | 0 | reg 21 &= `$C117`(reg 21) (drop invalid targets) | 0 | C | **missing** |
| 4E | B81C | r s | reg r = attacker's stat s (reg 20) | 1 | H | **missing** |
| 4F | B476 | 0 | end (`$0A5B` = FF: action cancelled) | 11 | H | ok |
| 50 | BE68 | 0 | reg 21 &= (`$0A5D` | EF) unless `$0A5D` bit 7 | 4 | H | 0, unnamed |
| 51 | BE7D | 0 | if reg 26 bit 0 clear: reg 61 \|= 2 (Dark reflector) | 5 | H | ok |
| 52 | BE96 | 0 | reg 61 \|= 4 | 0 | C | **missing** |
| 53 | BEBF | b | action 9, argument b; clears reg 10 | 0 | C | **missing** |
| 54 | BEDA | b | action 0x19 (IP), argument b; clears reg 10 | 0 | C | ok |
| 55 | B71D | r v | reg r /= v (unsigned, `$85:DC6F`) | 0 | C | ok |
| 56 | BE22 | v | message/effect timer `$1264` = v | 8 | H | ok (2) |
| 57 | BEF5 | w b | if the attacker's equipped item == w (`$C168`, word & 1FF): boost b applies (`$09FA`) | 26 | H | ok |
| 58 | BF97 | 0 | `$85:DA71` (Eerie light) | 0 | C | ok |
| 59 | BF9E | 0 | `$85:DA9C` | 0 | C | **missing** |
| 5A | BFA5 | b | battle animation b (`$1262`) | 128 | H | ok |
| 5B | BFAE | 0 | `$7FF8A5` = 0 (lose if it kills itself) | 1 | H | ok |
| 5C | BFB6 | 0 | `$7FFAB6` = FF (hide damage number) | 1 | H | ok |

## 3. What to change in L2BattleScript.cs
1. Add 36 (1), 43 (0), 45 (2), 48 (0), 49 (0), 4A (1), 4D (0), 4E (2), 52 (0), 53 (1), 59 (0). Mark 02, 31, 33,
   34, 38-3B as invalid (BRK) instead of "0 args".
2. **4B takes 0 args**, not 1 (unused in the data, so nothing breaks today).
3. 46 is a conditional jump (`46 i w`); include it in the jump-target handling.
4. 42 is "call shared script w" (library `$96:FADD`, 0x29 entries, offsets from the table; each script is based
   at its own start) and 43 "return"; the editor can show the library scripts as a separate list.
5. End rule: only 00 and 4F end the interpreter. 2A and 41 continue; since the data always puts 00 after them the
   current rule works, but "stop at 00/4F/03/43 once past the furthest jump" is the exact one.
6. 14's first byte is the register and the second the stat (l2ref says the reverse).

## 4. Owners parsed (bparse.py)
| Owner | Entry points | Script offset |
|---|---|---|
| shared library `$96:FADD` | 41 | own start |
| items (0xB4F69, 0x1D3) | menu 27, battle 37, weapon 73, armor 65 | property words at +0x0D, relative to record |
| spells (0xAFA5B, 0x28) | 40 | +0x11 |
| monsters (0xB05C0, 0xE0) | attack 204, defence 187 | extra list `07`/`08` |
| capsule forms (0xBDCB8, 35) | AI 35, defence 35 | +0x25 / +0x27 |
| capsule attacks (0xBF63B, 0x54) | 84 | +2 |
Opcodes used in the data: 00 01 03-11 13-17 1A 1D-2A 2C 2D 30 32 35 37 3C-3F 41-43 46 4C 4E 4F 50 51 56 57 5A-5C.

Not covered: IP attacks (`$84:8F10` records start with a u16 "effect" = 0x2C for Repeat attack; it is not a
script offset; the IP scripts are probably reached through op 54/action 0x19, not traced). Built-in scripts in
bank $85 (e.g. 0x2EF20 `32 01 28 00`, 0x2EF24 `37 00`, 0x2EF2B `15 11 0B 0C 62 11 80 ...` = LEdit's "Maxim's
script" at 0x2EF2A) decode cleanly with this table but their pointer tables were not traced.

## 5. Where monster and capsule scripts start, and Lufia Forge's script hooks (2026-10-24)

Found by reading the code, checked in battles in BizHawk (bsnes core).

* **Combatant builder** `$81:FC0B`: copies the monster record into a WRAM combatant struct at [$B2] (bank $7E). +$45/+$47 = the
  record's address (24-bit, bank $96), +$50 = the monster id, and every record tag >= 06 is stored at +$48 + (tag - 6) * 2:
  **tag 07 (attack script offset) at +$4A, tag 08 (defense) at +$4C**. Record addresses come from `$81:FCE2` (A = id ->
  X = table + offset, bank $96).
* **A monster's turn** `$81:B1C9`: Y = +$4A; 0 runs the built-in plain attack `$85:EF20` (`32 01 28 00`); else base = record
  (`$81:FCE2`), bank **`LDA #$96` at `$81:B1E3`** (the hacks that moved the monster data change only this byte, to `D0`).
* **When hit** `$85:D153` and `$85:D194` (two copies): base/bank = +$45/+$47, offset +$4C.
* **A capsule monster's turn** `$81:B1A3`: offset `$1129` (record +$25), base/bank `$1124`/`$1126` (bank $97).
* **Script runner** `$81:FAC9`: Y = offset (0 = none), `$BB` = `$0A42` + Y, bank `$0A44`, then `JSL $85:B452`.
* `$81:F1C5` ($0B8D-$0B91) is the **item** script loader (menu/battle/weapon/armor property words), not monsters.
* Banks $85 and $96 have no free run of 32+ bytes; $97 has about 700 bytes in four pieces.

**Lufia Forge's hooks** (Core/Lufia2/L2AiScripts.cs): a block in the added space (`LF2AI`, code at +$10, table at +$200 of
6-byte entries: record lo/hi, kind 0-3 (monster turn, monster hit, capsule turn, capsule hit), script 24-bit; FF ends) and
four patches: `JML` at `$81:B1D4` and `$81:B1A3`, `JSL` + 5 NOP at `$85:D165` and `$85:D1A6`. Each looks up (record, kind);
found: `$0A42`/`$0A44` = the script's base, Y = 1 (the script is stored as `[capacity u16][00][script]`, jumps counted from
the 00); not found: the game's own path. The game's bytes stay, so shared scripts are safe, monsters without a script can
get one, and taking the entry out restores the game's. All 461 game scripts (monsters and capsules) parse and build back
byte for byte; checked in battles: a monster with no script given "Defend" (attack and defense hooks each round), the
capsule hook, and Frue Lufia v8.2 (monster data in bank $D0).

## 6. Item, spell and IP attack effects (2026-10-25)

Every effect script is started through the runner `$81:FAC9` with `$0A42/$0A44` = the base and Y = the offset:

| Owner | Where | Base | Offset |
|---|---|---|---|
| item (menu, battle, weapon, armor) | `$81:AA57`, `$81:A8EC`/`$81:A94D` (weapon), `$81:B203`/`$85:DA4C` (armor), `$81:EFB0`, `$81:F004` | the item record (`$0A09`, bank $96) | its property word, copied by the item loader `$81:F1C5` to `$0B8D` + 2 x bit (bits 0-3 = menu, battle, weapon, armor) |
| spell | `$81:A9C4`, `$81:AFAD`, `$81:F313`, `$81:F389` | the spell record (`$0A0D`, bank $95) | the record's word at +$11 (`$0B89`) |
| IP attack | `$81:AB4D` | `$95:F1EF` + table[the record's first word] (`$81:F46B`) | 1 |
| capsule attack | `$81:AD4B` | `$97:F63B` + table[id] (`$81:F476`) | |

The game's built-in scripts run with base 0 and offsets like `$EF20`/`$EF24` (bank $85).

The item loader clears `$0B77-$0BAC`, copies the 9-byte head, the 4 bytes of property bits (`$09FC-$09FF`), then for each of
the 32 bits stores the next word at `$0B84` + 9 + 2 x bit when the bit is set (so absent words are 0).

**Lufia Forge's effect hooks** (version 2 of the `LF2AI` block, Core/Lufia2/L2AiScripts.cs; a version 1 block is upgraded
in place, its first routines keep their addresses):

* `JML` at `$81:FAC9` (in place of `PHP PHB REP #$30`): an offset with bit 15 set and a base other than 0 is an item marker
  `$8000 | key`; any other script is looked up by its address (bank, base + offset). Found: base = Lufia Forge's script,
  Y = 1. A marker that isn't in the table becomes offset 0 (no script).
* `JML` at `$81:F1C5`: runs the game's loader (`PHK / PEA` return), then for every table entry of the item (`$0A06` & $1FF):
  kind `FD`, key = item | property << 9: the marker `$8000 | key` goes to `$0B8D` + 2 x property, so an item without that
  script gets one; kind `FC`, key = item | bit << 9: the value (the entry's script field) goes to `$0B8D` + 2 x bit, so an
  item gets a stat bonus, IP attack or battle animation its record doesn't have.
* Spells and IP attacks: entries keyed by the script's address (kind byte = its bank, $95). IP attacks that share an
  effect script share the edit (effects 79 and 92 are used by 17 and 9 attacks).

Checked in battles (BizHawk, bsnes core, a Goblin made weak and passive at the start of a new game):
* weapon: the Small knife (no weapon script) given `37 00` (weapon attack, end): the battle is won as without hooks;
  given `29 00` or `2A 00`: every attack misses (weapon scripts are what deal the damage);
* spell: a monster casting the game's Flash takes Maxim from 20 HP to 0; with Flash edited to `00` the cast shows and
  Maxim keeps 20 HP;
* IP attack: Thunder blast deals 16; edited to `00` it misses;
* added word: the Small knife given IP attack 07 (not in its record): Maxim uses it (IP 255, 191, 127, 63); without it
  he never does.

**Elements** (the word of 1F/21/22), read from the game's uses: 0002 fire, 0004 water, 0010 light, 0020 dark, 0040 blunt
(maces, rods), 0080 thrown (boomerangs), 0100 vs dragons, 0200 vs insects, 0400 ice, 0800 thunder, 1000 earth; 0001, 0008
and 2000-8000 are used by no script. (The first names in this file's table and in LEdit were wrong.)

**Registers with a meaning**: 10 the last action failed, 20 the attacker, 21 the targets, 23 the action's kind, 24 its
number, 25 the attack's name, 27 the attack's element, 28/29 critical chance and multiplier, 2A the damage (negative for
damage worked out by a script), 2B the spread, 4E/50/52/54/56/58 the chance of poison, silence, death, paralysis,
confusion, sleep working on the fighter (shared script 11 zeroes all but death); 80-87 the fighter's own counters.

**Shared scripts** (the library): 00-05 double damage from fire, thunder, water, ice, thrown (none from earth), light;
06-0A half damage from fire, thunder, water, ice, light; 0B half from plain attacks; 0C double from dragon-slaying; 0D/0E
double/half from dark; 0F double from blunt; 10 double from insect-slaying; 11 immune to poison, silence, paralysis,
confusion and sleep; 12 healing hurts it, no instant death; 21 counter 1 = 1.5 x own ATP; 23 counter 1 = ATP with the
battle bonus. The editor can copy a shared script's steps into a script (its returns become "go to" the step after the
call) so one monster can change them.
