# Research: Lufia 1 event-script & dialogue engine

Findings from disassembly (Lufia Forge disassembler), two BizHawk trace sessions
(`BizHawk/LufiaForge_ScriptTracer.lua`) and automated test runs of the playthrough tracer
(`BizHawk/LufiaForge_PlaythroughTracer.lua`). Offsets are headerless US ROM file offsets.
Confidence: **confirmed** = seen in code and in traces; *inferred* = consistent with traces, not yet proven in code.

## Where dialogue lives

- Dialogue is **inline inside event scripts**. There is no message table; nothing points at individual lines.
- Scripts are addressed by a 24-bit offset **T relative to file 0x18000** (bank `$03`): file = `0x18000 + T`.
  This is why searching for ordinary SNES pointers to dialogue finds nothing. **(confirmed)**
- The script reader crosses bank boundaries seamlessly (Y wraps to `$8000`, bank+1), so scripts/text run
  continuously across banks `$03`–`$07`+. **(confirmed)**

## Engine routines (bank `$01`)

| Address | Role |
|---|---|
| `$01:C2DF` | "Run script" wrapper. Its caller identifies the trigger (see below). |
| `$01:C63E` | Script start: T from WRAM `$0D12-$0D14`; script block base at `$0D0F-$0D11`. |
| `$01:C66E` | Command loop: read byte; `< $80` → `JMP ($C78C,X)` (128-entry table); `>= $80` → range table `$C868`. |
| `$01:C693` | Text loop: `$00-$0F` control codes via `JSR ($C75C,X)`, `$10-$1F` / `$80-$FF` compressed words, `$20-$7F` characters. |
| `$01:C740` | Read one script byte from `DB:Y`, advancing across banks. |
| `$01:C564` / `$01:84DE` | Look up a sub-entry: index → 16-bit offset table in RAM `$0D3F`, added to block base `$0D0F`. |

Trigger (caller of `$C2DF`, logged as `caller=` by the tracer): **(inferred from traces)**

| Caller | Meaning |
|---|---|
| `$C275` | New script started: talking to an NPC, event tile, cutscene start. |
| `$8429` | Resume a paused script (after a wait / text box), every frame while running. |
| `$8997` | After a battle ends (post-battle script). |
| `$9F76` | Seen right after `$8997` following prologue battles. |
| `$846B` | Return from a shared sub-script (e.g. church menu). |

## Text inside scripts

- A text segment is opened by a command and **ends with control code `$04`**, which returns to script mode. **(confirmed)**
  (The current Text Editor assumes `$00`-terminated strings, which is why it shows junk prefixes.)
- `$05` = new line inside the box. `$07 nn` = character name, `$0C/$0D nn` = dictionary word (see Lufia1Constants).

## Commands identified so far

| Byte(s) | Meaning | Evidence |
|---|---|---|
| `0C` | Narration text follows (no speaker) | opening narration |
| `0D` | Text follows (plain box) | prologue lines |
| `88+N` (N 0-7) | **Actor N speaks**: speaker = actor slot N, box opens by that actor, then text | `$01:CBA4` stores N in `$0D0D` **(confirmed)** |
| `90+N` (N 0-31) | **Actor 8+N speaks** (same as above) | `$01:CB9C` adds 8 **(confirmed)** |
| `80-87` | Short wait: 1, 2, 3, 5, 10, 15, 20, 25 frames | table at `$01:DDB5` **(confirmed)** |
| `6C nn` | Wait nn frames | repeats every frame in traces |
| `3D cc ss` | Character cc learns spell ss | prologue, matches spell list |
| `3E ii qq` | Give item ii, quantity qq | prologue items (Game Data tab) |
| `27 aa xx yy` | Place actor aa at x,y (*inferred*) | prologue party setup |
| `84 xx yy` | Move actor (*inferred*; repeated with coordinates in cutscenes) | Alekia cutscene |
| `04 ff oo oo` / `05 ff oo oo` | *Inferred*: branch on story flag ff by relative offset oooo | NPC scripts start with these; offset lands on the alternate dialogue |
| `00` | End of script (*inferred*) | NPC scripts end with `04 00` |
| `B0-BF`, `C0-CF`, `D0-DF`, `E0-EF`, `F0-FF` | Grouped commands (handlers `$DDBD`, `$DDCB`, `$DDD7`, `$DDDB`, `$DDE1`) | not decoded yet |

## Example NPC scripts (Alekia, map `$0104`?)

| Script | First bytes | Dialogue |
|---|---|---|
| `0x01CB78` | `04 10 3D 00` `0C` … | "...an argument?" |
| `0x01D1EB` | `05 03 45 00` `0C` … | "...latest dress designs..." |
| `0x01D1AC` | `0C` … | "Roman... tea..." |
| `0x01D5A8` | `04 10 55 00 02 01 0B 00` … | "...crying... cruel, unfeeling..." |
| `0x01D290` | `04 50 06 00` … → shared `0x01A454` | Church: "Revive / Lift curse / Record" |

## Battle messages

- Separate printer (PC `$86B3`), strings in bank `$08` (`0x41290`–`0x47FFF`), `00`-terminated, using
  `0C nn` formatting codes for inserted names/numbers. Examples: `0x04136F` "Gets ... Experience",
  `0x0412B4` "'s level is up!", `0x047ED4` "uses the magic mirror!". **(confirmed by trace)**

## Maps and per-map events (session 3, 2026-10-03 night)

- **Current map number = WRAM `$0D15`** **(confirmed)**: `$97` (151) in the prologue fortress, `$04` in Alekia.
  (`$1A` only holds the map briefly while the script lookup runs; `$0010` is not the map.)
- **Map script table** at file `0x18200`, 5 bytes per map **(confirmed in code at `$01:C292`)**:
  bytes 0-2 = script block offset T (file = `0x18000 + T`), byte 3 → `$0D0C`, byte 4 = ?
- Each map's script block starts with a table of **16-bit offsets, one per event number**; event `N`'s
  script = block + offset[N]. Unused events point at a lone `00` (empty script). **(confirmed)**
- The game copies the event number into `$16` and calls `$01:C2B3`, so **(map, event) identifies every
  NPC conversation**. Verified: Alekia = map 4, events 4/5/9/17/18 are Roman's tea talk, dress designs,
  church, "an argument?", "crying"; prologue boss talks = map 151 events 13/14.
- **Which on-map character uses which event** is stored in the map's own data, which is *compressed*
  (see below). The map header is parsed from RAM via pointer `$52` (`$01:BB10`, field table at `0x0BC4A`).
  The playthrough tracer logs every unpacked resource per map so this can be finished offline.

## Compression and resources **(confirmed: unpacker output matches WRAM byte-for-byte)**

- Resource table: 3-byte SNES pointers at file `0x60000`, index = resource id (WRAM `$16`).
- Unpacker `$00:A47D` → output to `$1E:$1C`. Ported to `LufiaForge/Core/LufiaCompression.cs`.
- Format: u16 length, then control byte + up to 8 items. Bytes `< $80` are literals (no control bit).
  Bytes `>= $80` take the next control bit (MSB first): 0 = literal; 1 = back-reference:
  `b1 b2` (b2 low nibble ≠ 0): copy `(b2&$0F)+2`, distance = `(((b1<<8|b2)>>4)|$F000)` as negative 16-bit;
  `b1 b2 b3` (b2 low nibble = 0): copy `(b3&$3F)+3`, distance = `((((b1<<8|b2)>>4)|$F000)<<2 | b3>>6)` as negative 16-bit.
- Load pipeline seen when entering Alekia (map 4):

| Caller | Resource | Destination | Meaning |
|---|---|---|---|
| `$01:BDA9` | `$B2 + table $01:E19B[$077F]` (Alekia: `$B6`, 17,934 B) | `$7F0000` | area map data (header byte 8 picks the next resource) |
| `$01:BDD8` | `$B1 + table $01:E140[$7F:0008]` (Alekia: `$B4`) | `$7F460E` | second map data block |
| `$01:BE55` | Alekia: `$B3` (16 KB) | `$7E9000` | tile graphics |
| `$00:8E42` via `$00:8E13` | ids `< $25` (Alekia `$03`, fortress `$18`, title `$1C`) | `$7E6000` | *inferred*: music (37 ids) |
| `$01:DEF0` | `$25-$28` | `$7E7000` | battle sprites (the four Sinistrals) |
| `$01:D96E` | `$A3` | `$7E7000` | *inferred*: battle background |

## Map data format **(confirmed by rendering Alekia from ROM data, session 4, 2026-10-05)**

Loading chain for map `m` (all from ROM, no emulator needed):

| Piece | Resource id | Notes |
|---|---|---|
| Map data | `$B2 + byte[0xE19B + m]` | per map; unpacked to `$7F0000` (verified for all 10 maps visited) |
| Tileset number | map data byte `+0x08` | |
| Tile graphics | `$B1 + byte[0xE134 + tileset]` | 16 KB, 4bpp, loaded to VRAM byte `0x2000` = BG character base |
| Metatile set | `$B1 + byte[0xE140 + tileset]` | 9-byte records: 4 SNES tilemap words (TL, BL, TR, BR) + 1 byte (collision?) |
| Music | id `< $25` via `$00:8E13` | *inferred* |

Map data layout (towns/dungeons; the world map, map 01, has a different header):

| Offset | Content |
|---|---|
| `0x00` | ASCII map number in decimal + leftover dev-tool text ("004     ..w file") |
| `0x08` | tileset number |
| `0x12` / `0x14` | width / height in 16x16 metatiles (Alekia 112x72) |
| `0x24` | u32 total length |
| `0x28`-`0x38` | u32 section offsets: layer starts at `0x40`; objects start at `[0x30]` |
| `0x3E` | marker `76 33` |
| `0x40` | layer: width*height u16 metatile numbers (low 10 bits = metatile) |
| `[0x30]` | object block: 8 count bytes, 8 u16 offsets (relative), then sections |

Object block sections (counts from the first 8 bytes; offsets relative to the block):

| Section | Record | Meaning |
|---|---|---|
| A (count 0) | 12 bytes: id, kind, ?, x1, y1, x2, y2 (u16) | **exits / doors** (edge strips and door tiles) |
| B (count 1) | 6 bytes: ?, x, y (u16) | *inferred*: arrival points for doors |
| C (count 2) | 14 bytes: sprite, flags (facing/movement), x, y, box x1, y1, x2, y2 | **NPCs** (positions match the live actor table at `$0800`) |
| D, E (counts 3, 4) | 12 bytes | not decoded yet |
| F (count 5) | — | empty in the maps seen |
| G (count 6) | variable length | not decoded yet (likely event triggers) |
| first ~163 bytes | — | not decoded yet |

Live actor table: WRAM `$0800`, 32 bytes per actor; party in slots 0-3; tile x/y at +0F/+11 (u16).

## Scripted map capture and NPC → dialogue link (session 5, 2026-10-05)

- **117 maps** exist (catalogue: `tools/map_research/map_catalog.json`; a map is valid when its data starts
  with its own number in decimal). All 116 non-world maps render from ROM data
  (`tools/map_research/renders/`, `_contact_sheet.png`, objects in `maps_objects.json`).
- **Visiting any map without playing**: `tools/map_research/mapcapture.lua` loads the save-slot state, presses A,
  and redirects the Continue load: write the target to `$077F` right after `$01:BD75` (exec `$01:BD78`) and to
  `$0D15` right after `$01:9998` (exec `$01:999B`). Setting CPU registers from Lua does not work on BSNES.
  Maps 05/1B/5C/7D immediately exit to the world map (arrival spot is on an exit); 8C turns into 8D.
- Palettes per map captured from CGRAM: `tools/map_research/palettes/mapXX_cgram.bin` (ROM palette source not found yet).
- **Actor slots**: slot `n + 7` in the actor table (`$0800 + 32*slot`) = NPC record `n` of the map; +1A = map, +1B = facing.
- **NPC n runs event n** **(confirmed)**: talking goes through `$01:C228`, which sets `$16 = n` and the speaker slot
  `$0D0A/$0D0D = n + 7`, then runs event `$16` of the map's script block. Verified on 36 conversations.
  Other events (doors, triggers) go through `$01:C278`, which adds a per-map base (`byte 4` of the map script entry, table `0x18204 + map*5`).
- Exits change maps without a script command, so exit destinations come from native exit handling (still to find).

## Exits, palettes and saving maps (session 6, 2026-10-05)

- **Exit record (section A, 12 bytes)** **(confirmed in code at `$01:94FD` and against traced map changes)**:
  `[0]` arrival point number in the destination, `[1]` high nibble = transition type (→ `$0D49`),
  `[2]` destination map (0 = same map), `[3]` ?, then u16 x1, y1, x2, y2 (x2/y2 exclusive).
  The player's tile position for the check is WRAM `$12`/`$14`. On a hit: `$0D4B` = arrival, `$0D4C` = map.
- **Arrival point (section B, 6 bytes)**: u16 flags, u16 x, u16 y.
- **World map (map 01)**: 320x256, different layout; object block at `0xD46C` uses the same exit format
  (110 exits, one per town/dungeon entrance).
- **Palette** (no emulator needed): rows 0-1 = `0x11300` (shared), rows 2-7 = `0x17040 + (header byte 9 + 1) * 0xC0`.
  Verified pixel-identical against the game for Alekia; animated colours (water/lava) differ slightly at runtime.
- **Compressor**: `LufiaCompression.Compress` (optimal parse). All 347 resources round-trip; output is 6.4% smaller
  than the original packing overall. Verified in the emulator: a recompressed Alekia loads and shows the edit.
- **Saving**: `MapWriter` writes in place when the new stream fits; otherwise it expands the ROM to 2 MB, writes
  above 1 MB and repoints the resource (`0x60000 + id*3`, bank `$A0+`). Also verified in the emulator.
- Lufia Forge: **Map Editor tab** (`Modules/MapEditor`, `Core/Maps`) — browse all maps, characters with dialogue,
  exits with destinations; paint blocks, move characters, change exit destinations, Apply to ROM.

## Composite blocks / second layer (session 7, 2026-10-05) **(confirmed from WRAM after load)**

- On load, `$01:BEDF` splits the layer into two: every metatile `m >= threshold` (u16 at header `0x1E`)
  is replaced in the base layer (BG2) by a **ground** block and written to a second layer (BG1, offset
  stored into header `0x2C` in WRAM) as an **overlay** block. Other cells get overlay = flags only.
- Pair table: LoROM pointer at `$01:E14B + tileset*3` (tileset 0 → `$02:B400`, file `0x13400`);
  entry `(m - threshold)*4` = u16 ground, u16 overlay. Flags (`0xFC00`) are kept on both.
- Only done when header `0x10` bit 1 set, `0x11` bit 7 clear and `0x23 == 0`.
- Example map 0D (threshold `0x18D`): tree top `0x1A1` → ground `0x0D` + overlay `0xED`.
  Drawing raw records for these (e.g. `0x19E` = tile `0x127` ×4) gave the "red squares" on trees.
- Editors draw ground then overlay (colour 0 transparent); the editor still stores the composite number.

## Treasure, map setup scripts and exit links (session 7, 2026-10-06) **(confirmed in code; chest boxes line up with chest graphics)**

- **Map setup script**: pointer table at file `0x18000 + map*2` (u16, file = `0x18000 + value`), run by the
  interpreter at `$01:98AF` when a map loads. Starts with a u16 (→ `$0787`), then opcodes:
  `00` end · `01`-`03`, `05`-`0A`, `0C`, `0D` + 1 byte (`05`-`0A` test story flags into `$96`) · `04 ww` jump if
  the test failed · `0B` + 2 bytes · `21+n bb ww` arrival n · `41+n` character n · `81+n bb` section D record n ·
  **`C1+n lo hi ff`** = object section **E** record n.
- **Section E** (12 bytes: 4 runtime bytes + x1, y1, x2, y2) = object spots, filled by `C1+n` (`$01:9B2E`):
  `hi = FC` treasure chest with item `lo` (lo 0 = empty; drawn open/closed by flag `ff` via `$01:9CDC`),
  `FD` hidden item `lo` (search spot), `FE` door (graphic `lo & 0F`), `FF` trigger, anything else = chest
  with `hi:lo` gold. `ff` = per-map "taken" flag. A spot can be assigned twice behind a story check
  (e.g. map 07 spot 3: Sweet Water, later Dragon Egg).
- Section G = character walking routes (variable length).
- **Section D = step-on trigger areas** (12 bytes: 4 runtime bytes + rectangle). Setup opcode `81+n bb` enables
  area n: byte 2 = current map, byte 3 = n+1, byte 1 = lead word + bb (`$01:9ABC`). Stepping in (`$01:9494`)
  runs event **event base + byte 3** (`$01:94DE` → `$16`). Example: world map area D0 → event 24+1 = 25
  ("Lufia joins party"), confirmed against the playthrough trace.
- **Exit ↔ arrival**: exit byte 0 = arrival number in the destination (byte 2; 0 = same map). The world map's
  object block uses the same exit records, so every arrival can be traced back to the exits (or world-map
  entrances) that lead to it.
- Chest/hidden-item contents are edited by rewriting the `C1+n` value word in place (same length).

## World map format (session 8, 2026-10-06) **(rendered from ROM and matched to the game; edits verified in game)**

- Map 01 data (unpacked to `$7F:0000`): header w/h = 320×256 blocks (16 px); header `0x1E` = block count (240).
  Block records (9 bytes, same as towns) at `0x40`; **chunk table** right after (`0x40 + count*9` = `0x8B0`),
  4 block bytes per chunk in order TL TR BL BR; **chunk grid** at header `0x28` (`0x336C`), 160×128 u16
  (`& 0x0FFF` = chunk). `0xD36C` (header `0x2C`): 256 bytes of extra chunk numbers (story changes, untouched).
  Object block at header `0x30` (`0xD46C`), same format as towns. Reader: `$01:B047` (`$4F` = chunk table,
  `$4D` = blocks, both relative to `$7F:0000`).
- Graphics are not in the header: tiles = resource `0x124` (VRAM byte `0x2000`, as towns), palette rows 2-7 =
  map palette 17 (`0x17DC0`; 85/96 colours match the game, the rest are animated), rows 0-1 = `0x11300`. BG2.
- Editing: new block combinations get new chunks appended to the table (later sections and header offsets
  `0x28`/`0x2C`/`0x30` shift). Up to 4096 chunks.

## Object block layout (all maps) **(rebuilt with added exits/arrivals; verified in game)**

- 8 counts, 8 u16 offsets (relative to the block), an unknown prefix (163 bytes on towns), then sections
  A exits (12) · B arrivals (6) · C characters (14) · D (12) · E spots (12) · F (9) back to back, then G routes
  and H (variable) to the end of the data. Town header `0x24` = total length.
- Adding exits/arrivals at the end of A/B works in game (tested: a new Alekia exit to Treck).
- **A savestate holds the map already loaded in RAM**: map edits show only after the map is loaded again
  (leave and re-enter, or start from an in-game save).

## Character sprites (session 9, 2026-10-06) **(all 74 sprites rendered from the table)**

- Character spawn (`$01:976E`) reads the NPC record's sprite byte → `$01:AD32` (loaded-sprite list at `$0553`,
  max 0x18) → `$01:AC50`: record pointer = word at **`$01:E25F + sprite*2`** (bank 01).
- Record (17 bytes): `[0]` width, `[1]` height in tiles, `[4]` OAM attribute (low 3 bits = palette row),
  `[6..8]` LoROM address of the 4bpp graphics (uncompressed, e.g. bank 07), `[9..16]` first tile of 8 frames
  (frame = w*h tiles, row order). Sprite palettes: resource `0xAF` (8 rows × 16). Sprites 00–49 are valid.
- More event commands: `1C nn` open shop nn, `1D ww` stay at the inn (ww GP), `1E` church menu.

## Event script grammar (session 7, 2026-10-06) **(confirmed: 14,520 texts round-trip; relocated event shown in game)**

- **Jump operands are relative to the event's own start**: at event start the engine sets `$0D0F` (base) =
  `$0D12` (T) = block + table[event] (`$01:C292`), and `$C700` adds offsets to `$0D0F`. Address = bank
  `3 + (T >> 15)`, `(T & $7FFF) | $8000`, so scripts may live in the expanded area (banks `$20+`).
- Lengths (00-6D from `$01:C78C` handlers, checked against traces): `00` end · `01 ww` goto · `02 ff w1 w2`
  first-time branch · `03 n w…` choice targets (continues) · `04/05 ff ww` jump if flag set/clear · `06/07 ff`
  set/clear flag · `08` refresh map objects (1 byte) · `0C`/`0D` text · `0E`/`0F`/`30`-`37` + 1 byte then text ·
  `1E` church menu (1) · `3D cc ss` learn spell · `3E ii qq` give item · `6C nn` wait · `80`-`87` wait (1) ·
  `88`-`AF` actor speaks + text · `B0`-`BF` (1) · `C0`-`DF` (3) · `E0`-`FF` (1). `51` and `6E`-`7F` unknown.
  Full table in `Core/Maps/EventScript.cs`.
- Text control codes (`$01:C75C`): `00` end script, `04` back to commands, `05` new line, `01`-`03` (1 byte),
  `06`, `07` name, `09` item, `0A` spell, `0B` town, `0C`/`0D` dictionary word, `0F` (+1 byte each).
- Saving a longer/shorter text: copy the map's script block to the expanded area, append the rewritten event,
  repoint table entry + `0x18200` map entry. Old code stays, so other events/jumps keep working.
- **Savestates made before a ROM expansion zero the expanded area when loaded** (the state carries the ROM
  image). Test expanded ROMs by booting from SaveRAM, or rewrite CARTROM after loading.
- Lufia Forge Map Editor: gold/pink boxes for chests/hidden items, hover tooltip with item names, exit labels
  `E2→A1`, arrival labels `A1←E2`, linked ends outlined in green on selection.

## Event command reference (session 10, 2026-10-06)

Every command (00-6D, 80-FF) is decoded in [research-event-commands.md](research-event-commands.md): operands, what it does,
whether the event waits, RAM it uses, plus the actor table (`$07DE`, $20 per actor: 1 = leader, 2-5 followers, 6 vehicle,
7+n = map character n), camera structs and the movement path format (map data section G, `$7F` base `$07AE`).
The intro is map `4F` event 8 (jumps to the narration at `0x034F9D`); the prologue is maps `93`-`96`.
Event lists skip table entries that point into the pointer table, into another event's dialogue, or that decode into
6E-7F or jump outside the map's script block (1029 real events remain).

## Other routines found

| Address | Role |
|---|---|
| `$00:86B1` | General text printer for battle and menu text: `LDA [$1F],Y` (strings in bank `$00` menus, bank `$08` battle). |
| `$88:FAD0` | Monster record lookup, A = monster id (pointer table `0x5800`). Also used by the title demo. |
| `$01:AC60` | Reads a 68-entry pointer table at `0x0E25F` → 17-byte records (`0x0E2FB`…), *inferred*: sprite/actor definitions. |

## Editor requirements vs. what the playthrough tracer records

Requirements are taken from README phases 5–9. "Tracer" = `BizHawk/LufiaForge_PlaythroughTracer.lua`.

| Editor need (phase) | What the tracer records | Still needs |
|---|---|---|
| Dialogue by map & character (6) | `START` (map, event, script offset, trigger), `TEXT` (exact offset, speaker slot) | decoding operand sizes of all commands |
| Event commands / cutscenes (6) | every `CMD`/`HCMD` with operands, per map | naming each command (from trace patterns + code) |
| Story flags (6) | `RAM` changes in `$0100-$1DFF` (rarely-changing bytes) tied to map/frame | matching flags to `04`/`05` branch commands |
| Map list, map ↔ data (7) | `MAP` changes (`$0D15`), `RES` unpacks per map, ROM read bursts on first visit | parsing the map blob format (header, layers, NPC list, exits) |
| Exits / warps (7) | `MAP` + `BEFORE`/after RAM snapshots on every map change | locating position fields in the snapshots |
| NPC placement & which event they run (7) | map blob via `RES`; `START` events per map | NPC record format inside the map blob |
| Tilesets & graphics (5, 7) | `DMA` source → VRAM address, `RES` graphics unpacks | tile/metatile arrangement format |
| Palettes (5) | `DMA` → CGRAM with source | mapping palette sources to maps |
| Battle text (2) | `PRINT` with offsets | — |
| Menu text (2) | `PRINT` with offsets (status, options, save screens) | — |
| Monsters per area / encounters (8) | `MONSTER` per map, battle bursts (formation/AI reads) | encounter table format |
| Level-up growth (8) | level-up burst (ROM reads right after "level is up!") | growth table format |
| Shops, inns, chests (6) | event bursts (first time each), `RAM` inventory changes | — |
| Music per map (9/other) | `RES` ids `< $25`, `LOAD8E13`, `APU` port writes | confirming those ids are songs |
| Sprite sheets (8) | `DMA` to VRAM/OAM, `RES` battle sprite unpacks | field sprite format |

## Playthrough checklist (to get the most out of the trace)

- Talk to **every** NPC at least once; talk to some twice, and again after story events (flag branches).
- Open every chest, enter every building, use every staircase/door and the world map exits.
- Visit each shop and inn at least once; buy and sell something; rest once.
- Fight a range of battles in each area (the first battle per map records encounter data).
- Level up, learn spells, use items in and out of battle, change equipment once.
- Ride the ship and airship when available; open the menus (status, options, save).
- Savestates are fine; every BizHawk launch starts new log files (named with date/time).

## Open questions (next steps)

1. World map rendering (different layout) and editing.
2. Confirm `04`/`05` as flag branches and find the flag storage in WRAM (the `RAM` lines will show it).
3. Operand lengths for every command `$00-$7F` (needed to parse scripts without running them).
4. Palettes in ROM (rendering currently uses CGRAM from a savestate), roof layer, collision byte meaning,
   map exit destinations, world map format, and an LZ compressor for writing maps back.
