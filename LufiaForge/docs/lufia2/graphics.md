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
| 0x1B7-0x23C | monster battle sprites 1..0x86 | known (`L2Graphics.cs`) | `$85:EBA7` (0x1B7 + `$1303,X`) | known |
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
  at most 3 new sprites.
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

The block (0x1400 bytes in the added space, never across a bank): `"LF2SP"`, version 1, the number of new sprites,
the number of new palettes; +0x08 bit masks, +0x10 the row order 5 4 3 2 0 7 6 1, +0x20 the code, +0x200 a byte per
sprite number (0 = the colours of its row, n = new palette n), +0x300 the entries of FB-FF (8 bytes: the game's 5,
the walking script, 2 spare; FE and FF hold copies of sprite 00's), +0x400 new palettes 1-127 (16 colours each,
colour 0 see-through).

* **Lookup**: sprites FB-FD read their entry from the block (any bank), the others from `$CF:F000` as before.
* **New palettes**: after the row is stored, a sprite with a new palette has its colours 1-15 copied into a sprite row
  of `$0420` (and `$73` bit 7 set): a row that already shows exactly those colours; else the first row in the order
  5 4 3 2 0 7 6 1 that no other actor uses (an actor's row = `$7FE1CE,X`; FE objects count as row 7; the field monster
  rows come first because a character made later in a scene is most likely a person, the party's row 1 last); else
  the row its entry names. The row goes into `$7FE1CE,X`. No RAM of its own.
* **Walking**: a new sprite's walking script is its entry's byte 5 (a copy keeps the original's: 0 for people).
* Limits: 3 new sprites, 127 new palettes. A new palette borrows a row while its sprite is on the map: a character
  made later in the same scene with a game sprite of that row shows the new colours (the row is chosen among the
  rows free when the sprite appears).

### 7.4 The Graphics page

* Every picture except the palette sets can be painted: pencil, fill (on a map sprite within one frame), right click
  picks the colour under the mouse, zoom, a grid (pixels, 8x8 squares, sprite frames in gold), Ctrl+Z / Ctrl+Y for
  strokes. **Write the painting** goes through `L2Gfx.Import` like Import PNG (packed files packed again, moved to the
  added space when they grow) and is one Edit, Undo step. On a battle background or title layer each 8x8 square shows
  one row of colours, so a colour of another row is refused in a square that has colours of its own.
* Map sprites: the Colours list sets the sprite's colours (one of the 8 rows, or a new palette); "New palette for this
  sprite" makes a black one or a copy of the colours shown and gives it to the sprite; "New map sprite" makes FB, FC,
  FD as a copy of the sprite shown or empty with its size, frames, colours and walking (tiles in the added space).
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
