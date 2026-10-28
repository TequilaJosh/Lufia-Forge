# Lufia II (US) graphics: packed files and uncompressed graphics

Static analysis only (no emulator). ROM `scratchpad\l2\lufia2.sfc`. Scripts in `scratchpad\l2research\`:
`g.py` (unpacker, tile/tilemap/palette renderers), `montage.py`, `r_bbg.py`, `r_caps.py`, `r_title.py`,
`r_84.py`, `r_font.py`, `unc.py`. All files are unpacked to `l2research\files\NNN.bin`; renders in `l2research\renders\`.

Conventions: tiles are standard SNES planar (2bpp = 16 bytes, planes 0/1 interleaved per row; 4bpp = 32 bytes,
planes 0/1 then 2/3). Tilemap entries are SNES u16 `vhopppcc cccccccc` unless stated. Colours BGR555.
Confidence: **V** = loader code read and render looks right, **R** = render looks right but no loader found,
**G** = guess.

## 1. The decompressor and how files reach the PPU

* `$80:8E9D` is the only unpacker (only code reading `$A7:8000`). In: `$54` = file number (u16), `$60-$61` = WRAM
  destination, `$62` = bank (always `$7E`/`$7F`; the routine ORs `$7E`). Out: `$58` = unpacked size. It never writes
  VRAM/CGRAM itself.
* 55 call sites (48 in reached code + 7 found by byte search: `$81:99EC`, `$81:9A2E`, `$86:B1A8`, `$86:B6BA`,
  `$86:BBF4`, `$86:BFB0`, `$86:C09A`).
* After unpacking, data goes to the PPU in one of these ways:
  * direct DMA channel 0/7 from `$7E:4000`/`$7F:0000` to VRAM (`$80:F05C`, `$80:F28A`, `$84:A57D`, `$86:CE83`);
  * `$82:8044` (queued VRAM DMA: `$5D-$5F` source, `$60` VRAM word address, `$58` size; used by title/menu);
  * the battle DMA queue `$85:ECDB` + `$1A8F,Y` size / `$1A91,Y` WRAM source / `$1A93,Y` VRAM word address
    (helpers `$85:9B67..$85:9CA9`, each a fixed src/dst/size triple);
  * copied as tilemaps into the BG map buffers `$7E:2000`/`$7E:2800` (casino, battle BG).
* Palettes are almost never packed: they are copied from ROM (mostly bank `$A6`, and `$97:CD58`/`$97:D258` for
  battle) into the CGRAM buffer `$0320` (colours 0-127) and `$0420` (colours 128-255) with MVN, then `$73` bit 7
  requests the upload.

## 2. Every packed file

| Files | What | Format | Loader | Conf. |
|---|---|---|---|---|
| 0x000-0x03E | music (songs). Each starts `u16 size, "S2C", tempo?, 13-char Japanese title` ("FIELD", "SHIRO", "BATL 1", "ENDING", "777 PLAY", "ITEM GET1", ...); 0x00C/0x00E/0x030/0x031 are 0x52-byte "SDAMY" dummies | song data for the SPC | `$80:941E`: file A (< 0x64) to `$7E:2020`, checks `"S2"` at `$7E:2022` | V |
| 0x03F | **boot / licence screen** ("NATSUME / Serious Fun / Lufia II Rise of the Sinistrals / (c) TAITO 1996 / Licensed by Nintendo"), NOT map 0's data (map 0 = world map uses 0x28B/0x28C) | header of u16 offsets (`+0` map = 0x10, `+8` palette = 0x19D0, `+C` palette length 0x40); BG1 map 32x16 at +0x10, second map at +0x210 (BG2), 4bpp tiles at +0x710, 2 palettes at +0x19D0 | `$80:91DF` (from RESET): to `$7E:4000`, maps copied to `$7E:2280`/`$7E:2A80`, palette to `$0320`; mode 1, BG1 chr `$4000` | V (`renders\boot\03F_map10_t710.png`) |
| 0x040 | **staff-roll (credits) font**: serif 8x16 capitals/lower case/punctuation (glyph = tile n over tile n+16) and a script "THE END". NOT map 1's data (map 1 = undersea map uses 0x28E/0x28F) | 2bpp, 0x1800 bytes = 384 tiles | `$8E:B2EE` (event special-screen 5 via `$80:BA55` table, `$80:BAC9`): to `$7E:4000`, expanded to 4bpp in WRAM (16 data + 16 zero bytes per tile). Credits text `$91:9AFF` read by `$8E:B448..B510` | V (`renders\fonts\credits_font_040_2bpp.png`) |
| 0x041-0x12F | maps 2..0xF0 (map n = 0x3F + n) | known | `$80:EAE7` | V (known) |
| 0x130-0x14A | map block sets | known | `$80:EAE7` | known |
| 0x14B-0x15F | map tile sets (4bpp) | known | `$80:EF8E` (`$CF:F81E` + 0x14B) | known |
| 0x160-0x162 | map **tile-animation** data (e.g. water). `u16` offset to the frame data, then a list of `u8 tile, u8 frames` pairs ending FF, then tile data | stream | `$80:F23B`: file 0x15F + (byte0 >> 4) of the map's `$CF:F9FE` entry, to `$7E:C000`; pointers kept at `$7F:D0C6/C9` | V |
| 0x163-0x164 | map **parallax-layer** control (scroll/animation lists) | data | `$80:EB69` / `$80:ECD3` path: 0x162 + (byte0 & 0xF), to `$7F:[$2D]` | V (role G) |
| 0x165-0x166 | **parallax background tiles** (sky/clouds: 0x165 sky, 0x166 clouds) | 4bpp, 0x1000/0xF60 | `$80:F276`: 0x164 + (byte1 >> 4), to `$7E:4000`, DMA to VRAM `$1000` (0x2000 bytes) | V |
| 0x167-0x168 | **parallax tilemaps** with "MC" header (`"MC", u16, u16 size, 0, name "TBG.EO0" / "GRDNSKY."`, then u16 entries) | tilemap | `$80:ECD3`: 0x166 + (byte1 & 0xF), to `$7F:[$2D]` | V |
|  |  The map's `$CF:F90E` byte selects an entry of `$CF:F9FE` (2 bytes): 1=10 00, 2=20 00, 3=30 00 (anim 0x160/161/162), 4=01 11 (0x163, 0x165, 0x167), 5=02 22 (0x164, 0x166, 0x168); entry 0x11 (0E 0E) is used only by map 1 and would point at 0x170/0x174 (dummy) | | | |
| 0x169-0x16B | **Ancient Cave room templates** for the 3 floor styles | data, 0x1F8E | `$83:9656`: file = `$7FE699` from `$83:9F59` (style 0/1/2 = 0x169/0x16A/0x16B), to `$7E:4000`. Same style tables give tile set `$83:9F5F` (0x156/0x154/0x14F), block set `$83:9F49` (0x13B/0x139/0x134), battle BG `$80:ABCD` (0x17/0x09/0x01) | V |
| 0x16C-0x178 | **battle background tiles** (13 sets) | 4bpp, tile 0 = BG tile number 0x100 | `$81:B9C7` (see 3.1) to `$7E:C000` | V |
| 0x179-0x18C | **battle background tilemaps** (20) | 32x32, **delta coded**: w[0] stored, then w[i] = w[i-1] - raw[i] (u16 wrap); entries use palettes 2/3, tiles 0x100+ | `$81:B9C7` to `$7E:2000`, decoded in place at `$81:BA4C` | V |
| 0x18D, 0x18E | **battle status bar** tiles (HP/MP/IP gauges, digits, "Hp Mp Ip") two variants | 4bpp | `$81:E872`/`$81:E877` to `$7E:A000`, then names drawn on it with the `$9F:8500` font (`$81:E8EE` ORs glyph rows) | V |
| 0x18F | **battle command icons** (attack/spell/item/defend/... normal and highlighted rows) | 4bpp, 0x800 | `$81:C7E1` to `$7E:DF00`, queued by `$85:9C92` to VRAM `$4000` (OBJ) | V |
| 0x190 | **status / emotion balloons** used in battle (zZ sleep, ..., ?, !, skull) | 4bpp, 0x800 | `$81:8642` (battle init) to `$7E:8800` | V |
| 0x191 | starfield / sparkle tiles | 4bpp, 0x800 | no loader found | R |
| 0x192 | one 16-colour palette (0x20 bytes) | BGR555 | no loader found | R |
| 0x193 | **world-map encounter-zone map**, 64x64 bytes (one per 4x4 world tiles); value = zone into `$97:C494` (10 bytes per zone) | data (not graphics) | `$86:CD2E` to `$7F:E000`, read at `$86:9D0B` | V |
| 0x194-0x1B6 | **capsule monster battle sprites**, 35 = 7 monsters x 5 forms (index = `$154D`) | 4bpp, 0x480 = nine 16x16 blocks (each TL,TR,BL,BR = 0x80 bytes), 3x3 blocks = 48x48 | `$81:BC55` to `$7E:DF00` (`$82:CF9D` for the menu, to `$7E:4000`), blocks placed by `$81:BCCC` into the OBJ buffer `$7E:8000`; palette = 16 colours at `$97:D258 + 0x20 * index` | V (`renders\capsules_all.png`) |
| 0x1B7-0x23C | **monster battle sprites** 1..0x86 (file 0x1B6 + sprite) | byte 0 = palette count, then the palettes (0x20 each), then 4bpp 16x16 squares row by row (each TL, TR, BL, BR); size in tiles at `$97:CA64` (section 8) | `$85:EBA7` (0x1B7 + `$1303,X`) to `$7E:DF00` | V (checked in battle) |
| 0x23D | tilemap 32x24 (0xC00) | tilemap | no loader found | R |
| 0x23E | 0x60 bytes (3 4bpp tiles or 6 2bpp tiles) | ? | no loader found | G |
| 0x23F | **battle message window** tilemap, 32x4 | tilemap | `$81:E792` (from `$85:95FE`) to `$7E:3000` | V |
| 0x240-0x27E | **spell / attack effect graphics and BG tilemaps** (see 3.3) | mixed | battle animation script ops `$81:95C5`, `$81:9605`, `$81:9664`, `$81:99D0`, `$81:9A12` via table `$85:EF53` | V |
| 0x246, 0x247, 0x257, 0x274, 0x275, 0x27F, 0x280 | effect graphics/tilemaps not in `$85:EF53` (0x246/0x247 = 4bpp sweep effects like 0x240-0x245; 0x257 = solid tiles; others tilemaps) | | no loader found (probably unused) | R |
| 0x281 | **menu OBJ graphics**: big digits, cursor hands, gems/orbs, party initials F U L M A X, stat letters, rings | 4bpp, 0x2000 | `$86:8F45` to VRAM `$2000` (size 0x2000) | V |
| 0x282 | **menu window/frame graphics** (window borders, "Item" banner, ring/plates, buttons) | 4bpp, 0x4000 | `$86:8F1B` to VRAM `$4000` (size 0x3200) | V |
| 0x283 | **title screen** BG1/BG2 tiles (night sea, moon, Doom Island) | 4bpp, 1024 tiles | `$86:80DB` (from `$86:8000`, called at `$80:BBB5`): VRAM `$2000` | V |
| 0x284 | title BG3 tiles (tree silhouettes) | 2bpp | `$86:8104`: VRAM `$6000` | V |
| 0x285 | title OBJ tiles (light orbs / sparkles) | 4bpp | `$86:812D`: VRAM `$0000` | V |
| 0x286, 0x287 | title BG1/BG2 tilemaps | header `u16 w=32, u16 h=30`, then entries | `$86:817A` to `$7E:93C0`/`$7E:A3C0` | V |
| 0x288 | title BG3 tilemap | header `w=64, h=32`, then entries | `$86:817A` to `$7E:B3C0` | V |
|  | title palette: 256 colours at `$A6:B000` (`$86:8157`); mode 1, BG1/2 chr `$2000`, BG3 chr `$6000`, maps `$1000/$1400/$1800` (`$86:8033`) | | | |
| 0x289 | **menu labels/numbers**: HP MP, ATP DFP STR AGL INT GUT MGR, LV, digits 00-99, button hints (X:SELECT, L/R:ALL, A:EQUIP, BUY), IP gauge | 2bpp, 0x1000 | `$86:8EB0`: VRAM `$6800` (with the font `$9F:8500` at `$6000`) | V |
| 0x28A | 16 small 2bpp tiles (menu) | 2bpp, 0x100 | `$86:8EF1`: VRAM `$6A00` | V |
| 0x28B, 0x28C | world map tiles / map | known | `$86:CCFC` (table `$86:CE32` entry 0 and 2) | known |
| 0x28D | **"Lufia II Rise of the Sinistrals" logo** | 4bpp OBJ tiles, 0x4000 | `$86:CE68` (from `$86:930D`): to `$7F:0000`, DMA to VRAM `$4000`; palettes `$A6:B800` (BG) and `$A6:EA20` (OBJ, logo = OBJ palette 6) | V (`renders\title\logo_28D_obj_pal6.png`) |
| 0x28E, 0x28F | **undersea world map** tiles / map (map 1, "Map of Seafloor"); same formats as 0x28B/0x28C | | `$86:CCFC` table `$86:CE32` entry 1: big blocks `$9B:8000`, small blocks `$A1:A8F2`, palette `$A6:BA00` | V |
| 0x290-0x294 | **world-map cutscene sprites** (0x290 ships/airship parts, 0x291-0x294 effect sprites) | 4bpp | bank-86 world-map scripts: 0x290 `$86:B6AC`, 0x291 `$86:BBE6`, 0x292 `$86:B19A`, 0x293 `$86:BF99` (to `$7E:E000`, DMA VRAM `$4000`), 0x294 `$86:C08C` (to `$7E:F000`) | V (use G) |
| 0x295 | **casino** shared tiles | 4bpp, 0x1000 | `$84:A529`: VRAM `$3000`; palette `$A6:EC20` | V |
| 0x296, 0x297, 0x29E | casino entrance ("GOOD LUCK / YOUR FORTUNATE / CASINO FOLIE'S 7") tiles + tilemap | 4bpp / map 32x29 | `$84:B580`: 0x296 VRAM `$2000`, 0x297 `$4000`, map 0x29E (`$84:B523`); palette `$A6:ED20` | V |
| 0x298, 0x299, 0x29F, 0x2A0 | casino ball game ("LAST BALLS", tube) | 4bpp / maps 32x29 | `$84:C830`: 0x298 `$2000`, 0x299 `$4000`, maps `$84:D70A` (0x2A0 to `$7E:2000` with priority set, 0x29F to `$7E:2800`); palette `$A6:EE20` | V |
| 0x29A, 0x29B, 0x2A1-0x2A5 | **"Flower Garden" slot machine**: 0x29A BG2 tiles (scenery), 0x29B BG1 tiles; maps 0x2A1 machine, 0x2A2 scenery, 0x2A3/0x2A4 payout tables, 0x2A5 title "B-TYPE" | 4bpp / maps 32x29 (BG1 chr `$4000`, BG2 chr `$2000`) | `$84:DAB1`, maps `$84:EDFA`, `$84:EE73`, `$84:EF4E`; palettes `$A6:F020` (BG) / `$A6:EF20` (OBJ) | V |
| 0x29C, 0x29D, 0x2A6, 0x2A7 | **"Adventure 7" slot machine**: 0x29C BG2 tiles, 0x29D BG1 tiles; 0x2A7 machine map, 0x2A6 frame map | 4bpp / maps 32x29 | `$84:F3A0`, maps `$84:F7D3`; palettes `$A6:F320` / `$A6:F220` | V |
| 0x2A8 | empty (size 0) | | | V |

Casino tilemaps are 0x740 bytes (32 x 29 rows of u16); the code copies 0x800 bytes so the last 3 rows come from
whatever follows in WRAM.

## 3. Details

### 3.1 Battle backgrounds (`renders\battle_bg\`, overview `renders\battle_bg_all.png`)

`$81:B9C7`: id = `$11E1` (0x18 = no background: clears tiles and fills the map with 0x0900). Record table
**`$97:FD40`, 4 bytes per id**: `map, tiles, palette, effect` -> tilemap file 0x179 + map (to `$7E:2000`), tile file
0x16C + tiles (to `$7E:C000`), palette `$97:CD58 + palette * 0x40` (32 colours, loaded as CGRAM palettes 2 and 3 by
`$81:B974`), effect byte -> `$11E5` (jump table `$81:BAD4`; 1 = id 0, 2 = id 15, 3 = ids 18/19).

| id | map/tiles/pal | look |
|---|---|---|
| 00 | 179/16C/00 | desert |
| 01 | 17A/16D/01 | cave (dark, plants) |
| 02 | 17B/16E/02 | water stones, tower exterior |
| 03 | 17C/16F/03 | ship deck at sea |
| 04 | 17D/170/04 | meadow |
| 05 | 17E/171/05 | temple (white stone) |
| 06 | 17F/172/06 | rocky cave with green moss |
| 07 | 180/173/07 | mountain path |
| 08 | 181/174/08 | tower with windows (Daos shrine) |
| 09 | 182/175/09 | dark brick catacomb |
| 0A | 183/176/0A | green tiled hall with window |
| 0B | 184/177/0B | cave (red floor) |
| 0C | 184/177/0C | cave, brown floor |
| 0D | 187/177/0D | cave with water band |
| 0E | 184/177/0E | cave, red-purple floor |
| 0F | 189/177/0F | cave with waterfall pool |
| 10 | 184/177/10 | cave, olive floor |
| 11 | 186/177/10 | cave, olive, more vines |
| 12 | 188/177/11 | lava cave |
| 13 | 187/177/11 | lava cave with rock band |
| 14 | 185/177/12 | cave, dark, vines |
| 15 | 18A/178/13 | brick tower interior |
| 16 | 18B/178/13 | pillared hall |
| 17 | 18C/176/0A | green tiled hall (no window) |

**Who picks the id**: `$7FF8A0` -> `$11E1` at battle start
(`$81:81EA`). Writers: event command **0x74 xx** (`$80:B2A7`, map scripts set the background), world map
(`$86:9D79`: 0 = desert when the terrain value is 0x10, else 4 = meadow; `$86:9CEC`: 3 = ship deck at sea), the
Ancient Cave (`$80:ABC0`, table `$80:ABCD` by floor style: 0x17, 0x09, 0x01), `$83:B311` (0) and `$8E:B9F1`
(from saved data `$7E:3BBA`). Palette area `$97:CD58-$97:D257` (20 x 0x40).

### 3.2 Capsule monsters (`renders\capsules\`, `renders\capsules_all.png`)

35 sprites, file 0x194 + n, n = capsule * 5 + form (order in the file list matches 7 monsters x 5 forms).
Palette `$97:D258 + 0x20 n`. Layout: 9 blocks of 16x16 in row-major 3x3; each block = tiles TL, TR, BL, BR.

### 3.3 Spell / effect graphics (`renders\spells\`)

Table **`$85:EF53`**: 59 u16 file numbers indexed by the battle animation script argument (`[$C3]`):
`270 271 265 264 25E 268 26B 26C 26D 269 26A 25C 25D 26E 26F 260 261 25F 262 263 267 266 276 277 272 273 258 24E
24F 24D 24B 249 248 24A 250 24C 252 253 254 251 25A 240 241 242 243 244 245 278 279 25B 27A 27B 255 256 0 27C 27D 27E 259`.
Script ops: `$81:95C5` unpacks an entry to `$7E:A000` and queues `$85:9C1F` + `$85:9C36` (VRAM `$4000` and `$4800`,
0x1000 each = OBJ tiles), then a second entry to `$7E:DF00` queued by `$85:9C4D` (VRAM `$1800`, 0x1000);
`$81:99D0`/`$81:9A12` unpack to `$7E:DF00` with the VRAM address and size taken from the script; `$81:9664` takes
the WRAM destination from the script. `$83:E4C5` (field) uses a 4-entry table `$83:F400` = 0x24A, 0x253, 0x24C, 0x250
(to `$7E:A000`).

* 4bpp OBJ effect sheets: 0x240-0x245 (0x1800, sweeping slashes/claws), 0x248 (flames), 0x249 (orbs), 0x24A
  (sparkles/stars), 0x24B, 0x24C (rings, rams/sheep), 0x24D (circles), 0x250 (needles/hexagons), 0x251, 0x252/0x253
  (lightning), 0x256 (rocks/explosions), 0x259, 0x25A (damage digits 0-9, MISS, FULL, ATP DFP STR AGL INT GUT MGR IP
  MP), 0x25B (crescents/bubbles), 0x25C (0x90), 0x276 (big cloud/smoke).
* 4bpp BG effect tiles: 0x24E (clouds) with tilemap 0x24F.
* 2bpp BG effect tiles (BG3): 0x25E, 0x25F, 0x263?, 0x265, 0x267, 0x269 (bird), 0x26E (rings), 0x270 (wire frames),
  0x272 (rain), 0x278 (phoenix/dragon summon), 0x27A (goddess summon), 0x27C.
* Tilemaps (32x32 u16, often palette 3/4 + priority): 0x24F, 0x254, 0x255, 0x258, 0x25D, 0x260-0x262, 0x264, 0x266,
  0x268, 0x26A-0x26D, 0x26F, 0x271, 0x273, 0x277, 0x279, 0x27B, 0x27D, 0x27E (0x263 = 0x500 bytes).
  Which tile file each map belongs to follows the script pairs (not traced); 2bpp/4bpp split is by visual check (G for
  the borderline ones). Effect palettes come from the scripts (not traced); renders are grey.

## 4. Fonts

| Font | Where | Format | Loader / user | Conf. |
|---|---|---|---|---|
| **Dialogue font** | **`$9A:F970`** (file 0x0D7970), 0x690 bytes = 0x69 glyphs | **1bpp, 8x16**, 16 bytes per glyph (one byte per row), glyph = char - 0x20, ASCII order (space ! " # ... 0-9 A-Z [ yen ] ^ _ a-z, then a few kana-like symbols); char 0x10 is drawn as space | `$80:C7C8`: expands each row to 2bpp (plane 0 = row, plane 1 = colour mask from `$80:C815`) into the text buffer; `$81:EB34` copies a glyph to `$120F` | V (`renders\fonts\dialogue_font_9AF970_1bpp_8x16.png`) |
| **Menu font** (8x8) | **`$9F:8500-$9F:94FF`**, 256 tiles | **2bpp**, tile = char code (ASCII: 0x30 '0', 0x41 'A'); rows 0x60.. hold arrows, "Gold Item Coin" and item-type icons (sword, armour, ring, potion, ...) | `$86:8EB0`: VRAM `$6000` (menu BG3, with 0x289 at `$6800`); `$81:E8EE`: battle status names | V |
| other 2bpp fonts | `$9F:BA00-$9F:C400` (several digit/letter sets incl. italic and outline styles) | 2bpp | not traced | R |
| Credits font | packed file 0x040 | 2bpp, 8x16 (tile n over n+16) | `$8E:B2EE` | V |

## 5. Uncompressed graphics regions

* **Map (field) sprites** (format, colours and Lufia Forge's new sprites and palettes: section 7): banks `$9C`-`$A6` almost entirely, through the table `$CF:F000` (251 sprites, known; covers
  0x0E0000-0x0E2400, 0x0E6800-0x0EE000, 0x0FDA80-0x0FFE80, 0x100000-0x107800, 0x10B022-0x10FE22, 0x110000-0x11BC00,
  0x11C200-0x126C00, 0x127200-0x129200, 0x129700-0x12D700, 0x12E5FC-0x130F00, 0x132000-0x132A00, 0x134020-0x136020 ...).
  Palettes `$A6:BF00` (file 0x133F00).
* Not covered by the sprite table but clearly 4bpp sprite-style tiles (probably extra frames / event objects; loader
  not traced, R): 0x0E2482-0x0E67ED, 0x0EE00C-0x0F2FF2, 0x0F9528-0x0FA97C, 0x0FBA0A-0x0FDA78 (fonts, then sprites),
  0x107848-0x10A8F1, 0x11BC0A-0x11C1F9, 0x126C0B-0x1271EC, 0x129249-0x1296BF, 0x130F08-0x131F88.
* `$9A:8000` world map big blocks, `$9E:B000` small blocks; `$9B:8000` / `$A1:A8F2` undersea big/small blocks
  (0x0D8000-0x0DFFE8 and 0x10A8F2-...).
* Fonts: `$9A:F970` (dialogue), `$9F:8500` (menu), `$9F:BA00` (misc).
* `$A5:D700` / `$A5:DB00` (0x12D700-0x12DF43): 2bpp tiles (gauge/number pieces) read by `$81:D3DE`/`$81:D3F4` (G).
* Palettes in bank `$A6`: `$A6:B000` title (256 colours), `$A6:B200`/`$A6:B400`/`$A6:BA00` world maps (normal,
  alternate, undersea), `$A6:B800` logo BG, `$A6:BF00` map sprites, `$A6:EA20` logo OBJ, `$A6:EC20`..`$A6:F320`
  casino screens, `$A6:F420` (read by `$82:D348`), `$A6:FA8D` (`$82:C875`). Battle: `$97:CD58` backgrounds,
  `$97:D258` capsule monsters.
* `$90:C000` ... (0x084000-0x08BABA): SPC700 driver/data (pointer at `$80:9392`), not graphics.

## 6. Not found

* **Dialogue face portraits**: none. No packed file or uncompressed region holds face pictures; the dialogue code
  (`$80:C7C8`) draws only the 8x16 text font. Lufia II (US) dialogue appears to have no portraits.
* **Party battle sprites**: none. Lufia II battles are first-person; the party is shown only as the status bar
  (0x18D/0x18E + names from `$9F:8500`). The capsule monster is the only ally sprite (0x194-0x1B6).
* Item icons are 2bpp glyphs inside the menu font `$9F:8500` (row 0x60+), not a separate file.
* Ending: credits font 0x040 + text `$91:9AFF` (names) via `$8E:B2EE`; the ending scenes reuse map graphics.

## 7. Map sprites: format, colours, new sprites and new palettes (2026-10-25)

Read from the code and checked in the game (BizHawk, bsnes core). Lufia Forge: `Core/Lufia2/L2Sprites.cs`,
`L2Gfx.cs`, the Graphics page (`Modules/Lufia2/L2GraphicsPage.cs`).

### 7.1 The sprite table and its readers

* `$CF:F000`: 251 u16 pointers (sprites 00-FA) into bank `$CF`; the 5-byte entries follow at `$CF:F1F6-$CF:F6B4`
  (243 different entries: a few sprites share one, and some share their tiles).
* Entry: **flags** (bits 0-2 to `$7FE216,X`: 01 = 4 tiles high, 02 = 4 tiles wide; bits 3-7 to `$1291,X`: 08 = 2
  frames, 10 = 6 frames, the others animation/attribute bits), **colour row** (0-7, doubled into `$7FE1CE,X`, the
  OAM palette bits), **u24 tiles** (to `$12B9,X`/`$12BB,X`, the DMA source of the actor's frames). A frame's tiles are
  the top halves of its 16-pixel rows, then the bottom halves (`L2Gfx.MapSpriteTile`).
* Only two places read the table: `$83:A9F5` in `$83:A9E5`, which sets an actor up from its sprite (called through
  `$83:A9BA` and `$83:A9D0`: map load `$83:A82E`/`$83:A76D` and every event command that makes or changes a
  character), and `$83:9A55` (Ancient Cave field monsters, flags only, sprites from `$94:DDF6`).
* `$CF:F6B5 + sprite - $80`: the walking script (`$070A`, table `$91:A1D4`) of a sprite from `$80` up, used by
  `$83:A94D` when the character's map info record (+7, `$1212`) names none; below `$80` it is 0. The bytes for FB-FF
  there already belong to the map lists (`01 02 03 04 04`).
* **Sprite numbers are one byte** (`$05D2,X` per actor). FF = no actor (`$83:A776`, `$83:A83B`, `$83:FC46`, ...),
  FE = an event object (`$83:F718` sets it with `$7FE1CE` = 0F, so row 7), FD skips the walking script on the world
  map (`$83:A933`). Nothing else compares a sprite number with FB-FD, so **FB, FC and FD are the only free numbers**:
  3 new sprites on one map. Lufia Forge lets each map say which of its new sprites those three show (7.3), so up to
  128 can be made.
* Who names a sprite: event commands 68 / 7B (create a character: id, sprite) and 34 (change a character's sprite);
  story characters (ids 10-4F) keep theirs in `$08DE + id - 10`, filled at a new game (`$83:ADDF`) from the list at
  `$87:8000` + u16 at `$87:8003` (4 bytes: id, sprite, map, spot; the first is Tia: `10 04 04 08`) and made into
  actors by `$83:B364`.

### 7.2 Colours

* `$83:B062` (map load `$83:8637`, `$83:ACF9`, new game `$83:ADDF`, `$83:B220`) copies the 8 sprite rows at `$A6:BF00`
  (0x100 bytes) to the CGRAM buffer `$0420` (sprite palettes 0-7) and `$A6:FB0D` (0x20) to `$0320`. `$73` bit 7 makes
  the NMI (`$80:8736`) send all of `$0320-$051F` to CGRAM.
* A sprite shows its entry's row. Rows in the game's sprites: 0 (vehicles 71-73 and a few people), 1 (the party and
  most people, 82 sprites), 2 and 3 (people; field monsters from 3), 4 and 5 (field monsters), 6 (the item icons F1-F9
  shown over a chest, 4B, 59, 7E), 7 (4C, 4E and the event objects).

### 7.3 What Lufia Forge adds

Two hooks, put in the first time a new sprite or palette is made (`L2Sprites.Install`; the game's other bytes stay):

| At | Game's bytes | Becomes |
|---|---|---|
| `$83:A9F5` (file 0x1A9F5) | `BF 00 F0 CF` (LDA $CFF000,X) | `5C` JML to the lookup code; it does the rest of `$83:A9E5` and returns with its PLB / RTL |
| `$83:A956` (file 0x1A956) | `BF B5 F6 CF` (LDA $CFF6B5,X) | `22` JSL to the walking code |

The block (0x1C20 bytes in the added space, never across a bank), version 2 (2026-10-27): `"LF2SP"`, 2, the number
of new sprites, the number of new palettes; +0x08 bit masks, +0x10 the row order 5 4 3 2 0 7 6 1, +0x20 the code,
+0x300 a byte per game sprite (0 = the colours of its row, n = new palette n), +0x400 the map table (4 bytes a map,
by `$05AC`: the new sprite + 1 that FB, FC and FD show there; 0 = new sprites 1, 2 and 3), +0x800 the entries of
new sprites 1-128 and one more for FE and FF (8 bytes: the game's 5, the walking script, the new palette, 1 spare;
unused ones hold copies of sprite 00's), +0xC20 new palettes 1-127 (16 colours each, colour 0 see-through).
Version 1 (FB-FD only: palette bytes for every number at +0x200, entries at +0x300, palettes at +0x400) is still
read, and the first change moves it into a new version 2 block (`L2Sprites.Install`; checked: the Jeros' Story ROM's
three sprites and palettes read the same after the move).

* **Lookup**: sprites FB-FD read their entry from the block (any bank) through the map's table: `resolve` takes the
  map number (`$7E:05AC`), its table byte for that slot, and the entry it names; FE and FF read the spare entry. The
  others read `$CF:F000` as before. Lufia Forge numbers new sprites "New 1" up (251 + n) and writes FB, FC or FD in
  a map's scripts: the Maps tab picks a slot the map shows that sprite with, else one no script of the map uses
  (`L2Sprites.ToGame`, `L2MapInfo.NewSpriteBytesUsed`), and says so when a map would need a fourth.
* **New palettes**: after the row is stored, a sprite with a new palette has its colours 1-15 copied into a sprite row
  of `$0420` (and `$73` bit 7 set): a row that already shows exactly those colours; else the first row in the order
  5 4 3 2 0 7 6 1 that no other actor uses (an actor's row = `$7FE1CE,X`; FE objects count as row 7; the field monster
  rows come first because a character made later in a scene is most likely a person, the party's row 1 last); else
  the row its entry names. The row goes into `$7FE1CE,X`. No RAM of its own.
* **Walking**: a new sprite's walking script is its entry's byte 5 (a copy keeps the original's: 0 for people).
* Limits: 128 new sprites, 3 on one map; 127 new palettes. A new palette borrows a row while its sprite is on the map: a character
  made later in the same scene with a game sprite of that row shows the new colours (the row is chosen among the
  rows free when the sprite appears).

### 7.4 The Graphics page

* Every picture except the palette sets can be painted: pencil, fill (on a map sprite within one frame), right click
  picks the colour under the mouse, zoom, a grid (pixels, 8x8 squares, sprite frames in gold), Ctrl+Z / Ctrl+Y for
  strokes. **Write the painting** goes through `L2Gfx.Import` like Import PNG (packed files packed again, moved to the
  added space when they grow) and is one Edit, Undo step. On a battle background or title layer each 8x8 square shows
  one row of colours, so a colour of another row is refused in a square that has colours of its own.
* Map sprites: the Colours list sets the sprite's colours (one of the 8 rows, or a new palette); "New palette for this
  sprite" makes a black one or a copy of the colours shown and gives it to the sprite; "New map sprite" makes the
  next "New n" as a copy of the sprite shown or empty with its size, frames, colours and walking (tiles in the added
  space; on an 8 MB ROM in the upper 4 MB).
* Not done: more battle background palettes. A background's record (`$97:FD40`) names a palette by number, but the
  caller of `$81:B974` computes `$97:CD58 + palette * 0x40` and the 20 used ones end where the capsule colours start
  (`$97:D258`), so more would need a hook there.

### 7.5 Checked in the game

New game through the opening to Elcid's shop (map 04), scratch ROMs built with the harness:
* Sprite FB, a copy of 0E with a painted band on a new green palette, given to map 04's characters 50 and 53 (start-up
  script `68 xx 0E` to `FB`) and to Tia (story list): Tia shows as FB behind the counter in the new colours (row 3,
  `$7FE1CE` = 06); a second new palette on sprite 10 went to row 2; Maxim and the other characters unchanged. Slots
  left from the map before still count as used when the map's characters are made, so the first rows in the order
  can be passed over then; after the menu the same characters took rows 4 and 5.
* Maxim's sprite 00 painted (a white band, written in place): shows in the game, colours unchanged.
* Tia's sprite 04 on a new palette: her own row 1 is Maxim's, so it went to row 5 and both look right.
* A sprite made, coloured and painted only through the Graphics page (FB from 0E, black palette then colours set,
  a cross painted on each frame) shows in the game the same way.
* After opening and closing the menu, and after a battle, the map's characters are set up again and the new palettes
  are back in their rows (`$0420` and `$7FE1CE` read in RAM).
* Version 2 (2026-10-27): 5 new sprites (copies of 10, 20, 30, 40, 50; New 4 and New 5 on new red and green
  palettes), three of them given to new characters next to the start on map 03 (New 4, New 5, New 1: the map
  table made them FD, FC and FB). All three show in the game (Snes9x, BSNESv115+ on an 8 MB ROM; BSNES on a 4 MB
  one), and the Jeros' Story ROM (FB-FD made with version 1) plays its opening the same after the move.

## 8. Monster battle sprites (2026-10-27)

Read from the code and checked in the game (BizHawk, Snes9x core: screenshots and WRAM dumps during a battle). Lufia
Forge: `Core/Lufia2/L2MonsterSprites.cs`, `L2Gfx.cs` (Graphics page group "Monster battle sprites"), the Monsters page.

### 8.1 From a monster record to the picture

* Monster record +0F = **sprite** (1-based), +10 = **palette** (0-based, one of the sprite's own palettes). `$81:FBC6`
  reads both as a word (`LDA $9685CF,X` after the record offset).
* Battle set-up `$85:DF44` keeps up to 3 kinds of monster per battle: `$1300,X` the monster, `$1303,X` sprite - 1,
  `$1306,X` the palette; `$1315,X` / `$1318,X` the size in 16x16 squares, `$1312,X` the square count, `$131B,X` the
  movement flags.
* `$85:EB91` loads them: for each kind, file **0x1B7 + (sprite - 1)** (`ADC #$01B7` at `$85:EBB1`) is unpacked by
  `JSL $808E9D` (at `$85:EBC1`) to **`$7E:DF00`**. `$85:EBE0` copies its squares into the battle sprite buffer
  (`$81:BCCC`; a sprite already loaded for another kind is reused); `$85:EC4D` copies the palette the record names,
  `$7E:DF01 + 0x20 * palette`, to `$7E:4820 + 0x20 * kind` (sprite palettes 1-3; capsule monsters use `$7E:4800`).
* **Data** (unpacked): byte 0 = number of palettes, then the palettes (16 BGR555 colours each, colour 0 see-through),
  then the tiles: **16x16 squares row by row**, 0x80 bytes each = the 8x8 tiles top left, top right, bottom left,
  bottom right (4bpp). The game's 134 files are exactly `1 + 0x20 * palettes + 0x80 * squares` bytes.
* **Size**: `$97:CA64 + (sprite - 1) * 2` (`$81:FB8E`): byte 0 bits 0-4 = 8x8 tiles across, byte 1 bits 0-4 = tiles
  down (squares = (tiles + 1) / 2, every game sprite has an even size); the top 3 bits of both bytes (`$81:FBA2`,
  6 bits: byte 0's are bits 0-2, byte 1's bits 3-5) are the **movement flags** (8.3), copied to `$131B,X` per kind
  and `$13DA` per monster.
* **Buffer**: `$81:BCCC` puts the squares one after the other (slot n at `$7E:6000 + (n / 8) * 0x400 + (n % 8) * 0x40`,
  the lower tiles 0x200 further), from the slot after the kinds loaded before; it is sent to sprite tiles 0x100-0x1FF,
  so **all the kinds in one battle share 64 squares** (the 128 x 128 bosses fill it alone). The OAM builder
  `$81:BD4B` makes 16x16 objects in the same order (row by row, `$02` across, `$03` down), name table bit set.
* Limits: a sprite's data must fit `$7E:DF00-$7E:FFFF` (0x2100 bytes: 64 squares and up to 7 palettes; the
  biggest game sprite is 0x2021); sizes up to 31 tiles (5 bits); the sprite byte goes to 0xFF.

### 8.2 What Lufia Forge adds

* Every sprite is a picture on the Graphics page (Colours = the sprite's palettes, named by the monsters that use
  them): paint, Export / Import PNG, colours changed one by one (written into the packed data, which is packed again
  and moves to the added space when it grows).
* **New palettes** go into the sprite's own data (the count byte + 1); a monster picks one with its palette field.
  No hook is needed.
* **New sprites** (0x87 up to 0xFF: 121 more), put in the first time one is made (`L2MonsterSprites.Install`):

| At | Game's bytes | Becomes |
|---|---|---|
| `$81:FB98` (file 0xFB98) | `BF 64 CA 97` (LDA $97CA64,X) | LDA from Lufia Forge's size table |
| `$81:FBAE` (file 0xFBAE) | `BF 64 CA 97` | the same table |
| `$81:FBB9` (file 0xFBB9) | `BF 65 CA 97` | the table + 1 |
| `$85:EBC1` (file 0x2EBC1) | `22 9D 8E 80` (JSL $808E9D) | `22` JSL to the load code |

  The block (0x400 bytes in the added space): `"LF2MS"`, version 1, the number of new sprites; +0x10 the load code;
  +0x80 the size table (a copy of the game's 0x200 bytes, so every number reads what it read before; new sprites'
  entries written in); +0x280 a u24 pointer per new sprite to its packed data (the game's format, packed like the
  files). The load code: for file number 0x23D + i (sprite 0x87 + i) with a pointer, it pushes P and DB as the
  unpacker does, sets `[$5D],Y` to the pointer and jumps into `$80:8EBD` (the unpacker after its file table lookup),
  which unpacks and returns to `$85:EBC5`; other numbers go to `$80:8E9D` as before.
* A new sprite is a copy of another (size, movement, palettes, tiles), or made from a picture: a multiple of 16 pixels
  across and down, at most 240 x 240 and 64 squares; a palette PNG of up to 16 colours keeps its colours, another
  picture is brought down to 15 colours (median cut) with see-through pixels as colour 0; its movement flags are the
  sprite shown's. "Give it" (Graphics) or the Monsters page's sprite and palette lists give it to a monster.
* **Movement** (8.3): the Graphics page's Monster battle sprite card has a box for each of the 6 flags (Floats,
  Shakes, Twitches and the three that do nothing), written into the size table the game reads (the game's own at
  `$97:CA64`, or Lufia Forge's copy once new sprites are in), one Undo step per box. The Monsters page says how the
  picked monster's sprite moves.
* **Room in a battle** (8.4): the Encounters page shows, for the group shown, the most squares one of its battles can
  need and with which monsters, in orange past 64 (the group list marks such a group with "(! n squares)"); for a
  story battle the squares its kinds need, and a warning past 64 or past 3 kinds. The Monsters page shows, when a
  sprite is picked, the squares it takes and which groups and story battles with this monster would go past 64 with it.
* Changes made on the Graphics page (a new sprite, a new palette, painting, a colour, the movement, "Give it") are
  shown at once on the Monsters page (the sprite list and the monster shown are read again) and the Encounters page
  (pictures and squares); a monster's sprite, palette or name changed on the Monsters page updates the Encounters
  page and the Graphics page's "Give it to" list.
* `CanInstall` refuses when another hack changed one of those places. The Frue, Spekkio, Kureji and Wrath of Foomy
  hacks (v8.2) leave them as the US game: the hook goes in there too (block after their data at 0x30DAC2).

### 8.3 Movement flags (read from the code 2026-10-27, checked in the game)

The 6 bits (byte 0's top 3 = bits 0-2, byte 1's top 3 = bits 3-5) reach a monster this way: `$85:DF44` (battle
set-up) `JSL $81FBA2` -> `$131B,X` per kind; `$85:DEDC` copies it to `$13DA + 0x0F * n` for each monster on screen;
`$85:8905` (called from `$81:C758`) sets it again for every monster: a status byte (+0F of its battler record) with
08 gives 02, with 20 gives 01, else the sprite's flags. Every frame while `$129A` bit 7 is set, `$85:894A` moves each
monster with `$13DB` bit 7 set by its x offset `$13E3` and y offset `$13E5` (16-bit, added to the place `$13DF` /
`$13E1` when the objects are built at `$85:8CC5`):

| Bit | Name in Lufia Forge | What `$85:894A` does |
|---|---|---|
| 01 | Floats | counter `$152A,X` from 0x3F down; y offset = word `(counter >> 3)` of `$85:9E37` = 0, -1, -2, -1, 0, 1, 2, 1: up and down by 2 pixels, 8 frames a step, 64 frames a wave. Checked first: wins over the others. |
| 02 | Shakes | every 2 frames the x offset is turned over (`EOR #$FFFF`): 0, -1, 0, -1: a 1-pixel tremble. Wins over 20. |
| 20 | Twitches | every 12 frames the x and y offsets are each set to 0 or -1 at random (`$85:8F4A`, the random bits at `$1230`): a restless 1-pixel twitch. |
| 04, 08, 10 | no effect | nothing reads them (`$13DA` is read only at `$85:895E`, `$131B` only at `$85:DF15`). |

The game's 134 sprites use 0x30 (107: Twitches) or 0x10 (27: still). Checked in BizHawk (BSNES), the battle after the
name entry (Goblin CB, sprite 01) with the flags set to 30 (the game's), 10, 11, 12: logged `$13DA`, `$13E3`, `$13E5`
every frame from frame 1100 and screenshots every 4 frames (every frame for 12): with 10 the offsets stay 0,0 and the
Goblin stands still; 11 runs the y offset -2 -1 0 1 2 1 0 -1 (8 frames each) and the Goblin bobs up and down by up to
2 pixels on screen; 12 flips the x offset 0 / -1 every 2 frames and the Goblin moves 1 pixel left and back every 2
frames on screen; 30 gives 0,0 / -1,0 / 0,-1 / -1,-1 changing every 12 frames.

### 8.4 How many squares one battle needs (read from the code 2026-10-27)

* **A monster group's battle** (`$81:839F`): the group's 8 bytes go to `$22-$29`. Place 1 empty and place 2 filled is
  a fixed battle (`$81:84CF`, the other bytes copied as they are; not counted by Lufia Forge). Both filled: places 1
  and 2 are swapped at random. Then places 2-8 are shuffled (36 random swaps of neighbours among `$23-$29`), and the
  first 3 filled places become the **kinds** (`$13`, `$15`, `$17`): the place 1 or 2 monster and any 2 of the others.
  Monsters are then dealt to the kinds (`$81:84FC`): each adds its width in 8x8 tiles + 1 to `$11` and is only taken
  while the sum stays under 25 (0x19), so a kind too wide to stand beside the others never comes with them. Lufia
  Forge counts, for each possible first kind and each 2 others (and every part of that set that fits the width), the
  squares of the different sprites, and shows the most.
* **A story battle**: every different monster of its six is a kind. `$85:DF44` keeps 3 (`$1300-$1302`); a 4th runs
  into a `BRK` at `$85:DF73`. Checked: story battle 04 with monsters 01, 02, 03, 04 goes back to the title screen
  instead of the battle.
* **The buffer**: `$85:EB91` loads each kind's sprite one after the other into `$7E:6000` (64 squares, sent to sprite
  tiles 0x100-0x1FF); a sprite another kind already loaded is not loaded again (`$85:EBE0` checks the sprites loaded
  so far), so two kinds with one sprite count once. Past 64 squares the copy runs on past the buffer and the objects
  ask for tile numbers past 0x1FF, so the pictures come out wrong (read from the code, not tried in the game).
* In the US game only group D7 (Blue Core, Goblin, Gades, ... : 68 squares with Blue Core and Gades) goes past 64; no
  zone or map uses it. The story battles with a 128 x 128 boss use exactly 64.

### 8.5 Checked in the game

The battle right after the name entry (`53 04 39` at 0x3C0EE), its monster CB given the sprite; at frame 1080 of the
test the WRAM buffer `$7E:6000` and the colours `$7E:4820` were dumped and compared with Lufia Forge's decode:
* a new sprite 0x87 from a full-colour 64 x 64 picture (15 colours): buffer and colours identical, shown right;
* the game's sprite 01 (Goblin) painted (a band and a line) and colour 1 changed: identical, shown right;
* a copy of the Cyclops (0C) as 0x87 with a third palette added (colours turned around), CB on palette 2: identical;
* made only through the window (Graphics page): the Cyclops painted with a stripe and colour 2 of palette 1 changed,
  a palette 2 added, a new sprite 0x87 from a 64 x 48 PNG given to CB with "Give it", the Monsters page's lists: the
  new sprite and the painted Cyclops (CB on palette 1) show in the battle as in the app;
* the Frue hack (v8.2) with a new 64 x 48 sprite on every monster: buffer identical, shown right. In that hack the
  battle command menu draws over the sprite tiles of the second row of squares in the buffer (squares 8-15) while it
  is open, and the game's own Cyclops shows the same, so it is the hack's menu, not the new sprite;
* the movement flags (8.3) and a story battle with 4 kinds (8.4), as written there;
* in the window (17 checks): the Monsters page shows the Goblin's movement and that its battles fit; on the Graphics
  page the 6 boxes are ticked as in the ROM, Floats on and Twitches off write 11 and the Monsters page (still on
  CB, not picked again) says it floats; Undo takes it back; a copy of the Cyclops made as sprite 0x87 is in the
  Monsters page's list at once, and "Give it" to CB shows CB with it there; the Beetle given the 128 x 128 sprite 81
  warns for groups 0A and 1C (68 squares), the Encounters page marks group 0A in its list and in orange, group 00
  fits; story battle 04 given 4 different monsters warns about the 4th kind.
