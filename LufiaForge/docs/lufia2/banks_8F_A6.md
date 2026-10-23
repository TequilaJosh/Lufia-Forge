# Lufia II (US): banks $8F-$A6 (file 0x078000-0x137FFF)

Static analysis only (no emulator). Builds on `rommap.md`. Scratch tools in `l2research\b8f\` (`rom.py`, `ctx.py` = disassembly context by address, `cov.py`/`gaps.py` = coverage registry and gap lister, `render.py` = 2bpp/4bpp tile renderer, `scan24.py` = u24 pointer-run scanner, `lz.py` = packed-file walk, `ent.py` = entropy profile). Renders in `l2research\renders\` (`bank_XX_4bpp.png` for every bank $8F-$A6, plus `9F_8000_2bpp.png`, `99_DE6C_selan_4bpp.png`, `A5_E2FC_*.png`).

Result: after this pass about 0x8DC0 bytes (2.3% of these banks) are not tied to a reader, and almost all of that is the tail of a sprite graphic whose exact length is not stored (bounded by the next known item). Only five blocks (~0x1C00 bytes in all) are truly unidentified (section 4).

Confidence: **high** = reader code read and data shape checked; **med** = reader known, format or extent partly inferred; **low** = shape only.

## 1. Range table

| File offset | SNES | Size | What it is | Evidence | Conf. | Reached by |
|---|---|---|---|---|---|---|
| 0x078000-0x0782D5 | $8F:8000 | 0x2D6 | Per-map pointer table for **map object/actor scripts** (0xF2 x u24: lo16 + $8000, bank = $8F + hi; FFFFFF = none) | 146 maps have a block, all fall in $8F:82D6-$90:BDxx | high | $80:E85D-$80:E897 -> $7FD194/$7FD196 |
| 0x0782D6-~0x083E5F | $8F:82D6 | ~0xBB8A | **Map object scripts** (third script interpreter, l2ref's "puzzles"). Each block starts with a u16 offset list, then bytecode | read by $80:E722/$80:E78D (8 script slots at $7FD18C), opcodes via $80:E58B JSR ($E59E,X) / $80:CC3F JMP ($E5A4,X) | high (extent of last block approx.) | $8F:8000 |
| ~0x083E60-0x083FFF | $90:BE60 | ~0x1A0 | unidentified high-entropy tail (see 4) | | low | none found |
| 0x084000-0x087D36 | $90:C000 | 0x3D37 | **SPC700 sound driver** upload: IPL blocks [u16 len][u16 ARAM dest]: 0x303 bytes -> $0300, 0x3A28 bytes -> $09D2, end marker (len 0, exec $0300) | parsed exactly | high | $80:9383 (`#$90:C000 -> $5D`, JSR $9C0F = IPL handshake $BBAA/$CC) at boot |
| 0x087D37-0x087FFF | $90:FD37 | 0x2C9 | unidentified high-entropy bank tail | | low | none |
| 0x088000-0x088EFF | $91:8000 | ~0xF00 | **Actor animation/motion scripts**, table 1: 0xA0 x u16 (+$8000), bytecode | -> $7FE3EE (per-actor, X = $AB) | med | $83:D3F7 |
| 0x088EC7-0x089AFE | $91:8EC7 | ~0xC38 | Actor animation scripts, table 2: 0x60 x u16 (relative to $8EC7) | -> $7FDEEE; entry 0x32 fixed at $83:CA84 | med | $83:DFFD, $83:CA68 |
| 0x089AFF-0x08A1D3 | $91:9AFF | 0x6D5 | **Staff roll / credits text** (control byte + zero-terminated ASCII: "Scenario composer", ..., "Presented by TAITO CORPORATION") | text checked | high | $8E:B448 (credits renderer, LDA $919AFF,X) |
| 0x08A1D4-0x08A806 | $91:A1D4 | 0x633 | Actor animation scripts, table 3: 0x40 x u16 (relative to $A1D4), indexed by actor sprite id ($070A,X) | -> $7FE506 | med | $83:D416 (16 callers) |
| 0x08A807-0x0A713C | $91:A807 | 0x1C936 | **Map info blocks** ("presets": doors, exits, NPC starts, treasures; u16 size + 21 list offsets). 242 maps via $CF:FCBC; blocks are contiguous except for the small tables below | sizes chain exactly | high | $83:B5D3 via $CF:FCBC |
| 0x08B8B5-0x08BDA7 | $91:B8B5 | 0x4F3 | **Chest lists** (US address, l2ref had none): 0xF2 x u16 offsets (relative to $B8B5), records 3 bytes `id(&1F)|20|40, global chest id, item lo` until FF | last list ends exactly where the next map info block starts | high | $80:E85D (-> $7FD1A0), $80:D5F9, $83:86EA, $8E:B817 |
| 0x08FFCA-0x08FFDB | $91:FFCA | 0x12 | Iris treasure item ids (9 x u16: 0x19C Iris sword ... 0x1A4 Iris tiara) | item names | high | $83:9103 (Ancient Cave), $84:8974 |
| 0x08FFDC-0x08FFFF | $91:FFDC | 0x24 | Ancient Cave small u16 list (9 used) + zero pad | | med | $83:91B8 |
| 0x09D59B-0x09D69A | $93:D59B | 0x100 | Ancient Cave layout: 256-byte neighbour-mask -> tile lookup | | med | $83:962C |
| 0x09D69B-0x09D786 | $93:D69B | 0xEC | Ancient Cave room/piece records (4 bytes: u16 RAM offset, w, h) | | med | $83:9D92 |
| 0x0A4998-0x0A4A88 | $94:C998 | 0xF1 | Per-map chest-list alias byte (maps sharing a chest list) | searched over 0xF1 entries | med | $8E:B7F3 |
| 0x0A52BF / 0x0A554E / 0x0A561F / 0x0A6FE8 | $94:D2BF... | 0xDB/0xD1/0xD1/0x4D | **Ancient Cave floor map-info templates** (same format as map info; not in $CF:FCBC) | u16 sizes chain exactly; pointer list $83:9F73 (4 x u24) | high | $83:9F0E -> $7FE6A5 |
| 0x0A595C-0x0A5A25 | $94:D95C | 0xCA | Ancient Cave monster pool by floor (floor*2 + rand 0..5 -> monster) | | med | $83:9A42 |
| 0x0A5DF6-0x0A5EB5 | $94:DDF6 | 0xC0 | Monster id -> field (map) sprite id, for visible monsters | feeds $CF:F000 lookup | high | $83:9A4A, $80:AB86 |
| 0x0A6EA0-0x0A6EF1 | $94:EEA0 | 0x52 | Ancient Cave item list (0x29 x u16, hi \| 0x40) | | med | $83:91CD, $84:8875, $84:8AFF |
| 0x0A713D-0x0A717D | $94:F13D | 0x41 | Ancient Cave item list (0x1F x u16) | | med | $83:915F |
| 0x0A717E-0x0A7FFF | $94:F17E | 0xE82 | unidentified high-entropy bank tail | | low | none |
| 0x0A8000-0x0A8231 | $95:8000 | 0x232 | **Battle animation (spell/skill effect) script table**, ~0x119 u16 (0 = unused) | | high | $81:899A (`$81:895E`, A = anim id; spell/IP record +2 -> $1262) |
| 0x0A8232-0x0AF1EE | $95:8232 | 0x6FBD | **Battle animation scripts** (bytecode, bank fixed $95 via $C5; interpreter $81:8A95, opcode table $81:8C5C (36 ops), 0x1E threads of 0x2C bytes at $4F8F) | | high (exact end = next table) | $95:8000 |
| 0x0AF1EF-0x0AFA5A | $95:F1EF | 0x86C | IP attack effect (battle) scripts: 0x8D x u16 relative offsets, FF-prefixed records | run by $81:FAC9 with $0A42/$0A44 | high | $81:F46B (from IP record +0, $81:AB50) |
| 0x0AFF16-... | $95:FF16, $95:FFED | | small tables (rommap) | | | $82:C55A, $81:C47D |
| 0x0B7ADD-~0x0B7E3F | $96:FADD | ~0x360 | Shared battle-script subroutines (0x29 x u16 relative to $FADD) | | high | $85:BDA2 (battle opcode $41: call shared script) |
| 0x0B7F3C-0x0B7FEB | $96:FF3C | 0xB0 | small tile block DMA'd to VRAM $1780 | | med | $81:86A5 |
| 0x0BC53D-0x0BCA1C | $97:C53D | 0x4E0 | **Fixed (boss/event) battle formations**: u16 offsets, records (monster list FF-terminated + positions) | | med | $81:82C6 (9 refs) |
| 0x0BCB70-0x0BCCF7 | $97:CB70 | 0x188 | (u8, u8) pairs per monster: graphic id + variant (guess) | shape | low | not found |
| 0x0BEF15-0x0BF63A | $97:EF15 | 0x726 | **Shop item lists** (the 59 shop offsets reach $97:F598) | max offset 0x6F9 | high | $82:E729 |
| 0x0BF63B-~0x0BFC3F | $97:F63B | ~0x600 | Effect scripts, 0x54 x u16 relative (capsule monster attacks? guess) | | med | $81:F476 ($81:AD51) |
| 0x0BFC40-0x0BFD3F | $97:FC40 | 0x100 | palettes (battle/menu) | 15-bit colours | med | $81:B974 ($24 = $97) family |
| 0x0BFD40-0x0BFD9F | $97:FD40 | 0x60 | battle background set records (4 bytes, index $11E1) | | med | $81:B9D5 |
| 0x0BFDA0-0x0BFDE5 | $97:FDA0 | 0x46 | item id list 0x1AC-0x1CD, FFFF end | | med | $82:FB59, $82:FC77 |
| 0x0BFDE6-0x0BFE63 | $97:FDE6 | 0x7E | palettes + byte table $97:FE26 (10 refs) | | med | $81:86C8, $81:CB7E, $81:CC88, $81:C77F, $85:DA64 |
| 0x0BFE64-0x0BFFFF | $97:FE64 | 0x19C | unidentified high-entropy bank tail | | low | none |
| 0x0C0000-0x0C00AA | $98:8000 | 0xAB | **Instrument (BRR) sample table**: 0x39 x u24 (`(w & 7FFF) \| 8000`, bank = $98 + (w>>15 \| b<<1)) | | high | $80:98C0 / $80:98A5 (from song instrument list $7E2052) |
| 0x0C00AB-0x0CDE6B | $98:80AB | 0xDDC1 | **BRR instrument samples** (57): [u16 size][u16 loop][size bytes BRR] | every size is a multiple of 9, last block has the end flag; uploaded through $2140-$2142 | high | $98:8000 |
| 0x0CDE6C-0x0CF86B | $99:DE6C | 0x1A00 | **Selan battle sprite sheet** (4bpp) | render | high | hero sheet table $8E:E5C2 (index 1) |
| 0x0CF86C-0x0CFFFF | $99:F86C | 0x794 | small sprites: shadows/misc + map sprites 4B, F0, object gfx 00 | render, tables | med | $CF:F000, $83:F388 |
| 0x0D0000-0x0D796F | $9A:8000 | 0x7970 | World map 1 big blocks (0xF2E x 8) | rommap | high | world table $86:CE3B |
| 0x0D7970-0x0D7F6F | $9A:F970 | 0x600 | **1bpp 8x16 font** (96 glyphs, ASCII $20-$7F) | glyph bitmaps | high | $80:C7F8, $81:EB48, $81:EB77 (16 bytes per char) |
| 0x0D7F70-0x0D7FFF | $9A:FF70 | 0x90 | map sprite 48 / object gfx 0C | | high | |
| 0x0D8000-0x0DBC07 | $9B:8000 | 0x3C08 | **World map 2 big blocks** (0x781 x 8; max index 0x16E < 0x170 small blocks) | | high | world table $86:CE3B (record 1, files 0x28E/0x28F) |
| 0x0DBC08-0x0DFFFF | $9B:BC08 | 0x43F8 | **Map BG palette sets** (0x57 sets, u16 addresses in $CF:FAFA, index $CF:FA08) | | high | $80:F2DB -> $80:F338 (MVN $00,$9B) |
| 0x0E0000-0x0E33FF | $9C:8000 | 0x3400 | **World-map vehicle sprites** (Excursion ship/submarine/airship = map sprites 71-73, extra frames to $9C:B200) | 6-byte DMA list | high | $86:F083 world-map gfx list |
| 0x0E3400-0x0E67FF | $9C:B400 | 0x3400 | **Dekar and Lexis battle sheets** (0x1A00 each) | | high | $8E:E5C2 (5, 6) |
| 0x0E6800-0x0EDFFF | $9C:E800 | 0x7800 | Map sprites: party members, carry/push/fall poses (00-09, 1D-25, 29-33) | | high | $CF:F000 |
| 0x0EE000-0x0EF7FF | $9D:E000 | 0x1800 | **Maxim battle sheet** | | high | $8E:E5C2 (0) |
| 0x0EF800-0x0EFFFF | $9D:F800 | 0x800 | event object gfx 06 | | med | $83:F388 |
| 0x0F0000-0x0F2FFF | $9E:8000 | 0x3000 | **Artea and Tia battle sheets** (0x1800 each) | | high | $8E:E5C2 (3, 4) |
| 0x0F3000-0x0F737A | $9E:B000 | | world small blocks, item names, description words (rommap/BRIEF) | | high | |
| 0x0F737B-0x0F7FFA | $9E:F37B | 0xC80 | map sprites B6, 4D | | high | $CF:F000 |
| 0x0F8000-0x0F84FF | $9F:8000 | 0x500 | **Palettes** (field/menu/sprite) | 15-bit colours | high | MVN at $86:90CC-$86:9118, $82:C897, $82:D543 |
| 0x0F8500-0x0F94FF | $9F:8500 | 0x1000 | **Main 2bpp font** + "Gold/Item/Coin", arrows, small icons | render | high | DMA to VRAM $1000: $81:867F, $83:B015, $84:A4B2, $86:8EB0 |
| 0x0F9500-0x0FA97F | $9F:9500 | ~0x1480 | **Guy battle sheet** | | high | $8E:E5C2 (2) |
| 0x0FA980-0x0FBA7F | $9F:A980 | 0x1100 | item/treasure icons (map sprites F1-F9, 7E) + object gfx 0A + more small sprites | render | med | $CF:F000, $83:F388 |
| 0x0FBA80-0x0FCA7F | $9F:BA80 | 0x1000 | **Second 2bpp font** (digits, upper/lower case) | render | high | DMA to VRAM $1000: $84:82E7 |
| 0x0FCA80-0x0FDA7F | $9F:CA80 | 0x1000 | event object gfx 17 | | med | $83:F388 |
| 0x0FDA80-0x107FFF | $9F:DA80 | 0xA580 | map sprites B7-B9, BF, C0, C3, C4, D1, D3, EB, 49 (ship), 79-7C; object gfx 07, 08; 0x107880-0x107FFF all zero (padding) | | high | $CF:F000, $86:F083 |
| 0x108000-0x108B7F | $A1:8000 | 0xB80 | event object gfx 03 | | med | $83:F388 |
| 0x108B80-0x1096F1 | $A1:8B80 | 0xB72 | **Map tile/palette animation records** (0x12 sets; byte 2 & 7F = count, then relative u16 offsets; colour entries) | | high | $80:EFE1 (DB = $A1) via $CF:FBA6 -> $CF:FC98 |
| 0x1096F2-0x10A0F1 | $A1:96F2 | 0xA00 | event object gfx 02 | | med | $83:F388 |
| 0x10A0F2-0x10A8F1 | $A1:A0F2 | 0x800 | world-map sprite gfx | | med | $86:F083 |
| 0x10A8F2-0x10B021 | $A1:A8F2 | 0x730 | **World map 2 small blocks** (0x170 x 5, passability 00/30/40) | | high | world table $86:CE3B |
| 0x10B022-0x12D6FF | $A1:B022 | 0x226DE | **Map sprites** (townsfolk, NPCs, Pierre, Daniele, Amon, Gades, Daos, Erim, monsters 82-B5, ...) + object gfx 04, 0D, 11, 1C | | high | $CF:F000, $83:F388 |
| 0x12D700-0x12DEFF | $A5:D700 | 0x800 | Two 0xB6-byte ramp tables ($A5:D700, $A5:DB00; rest zero) -> $7E4AC4 | | low | $81:D3DE, $81:D3F4 |
| 0x12DF00-0x12E2FB | $A5:DF00 | 0x3FC | Capsule monster attack names (0x4E x u16 + strings; ends exactly at 0x12E2FC) | | high | $85:95DA, $82:C3B8 |
| 0x12E2FC-0x12E5FB | $A5:E2FC | 0x300 | Tile block DMA'd to VRAM $1680 next to the font | | med | $83:B03B |
| 0x12E5FC-0x12FFFB | $A5:E5FC | 0x1A00 | Map sprites 80-8C, 99, D5-DB (field monsters, 6 frames) and 46 | | high | $CF:F000 |
| 0x130000-0x132FFF | $A6:8000 | 0x3000 | Map sprites 4E, 57, 59, 60, 63, 88-8C, D9-DB, FA + object gfx 01, 09, 0B, 10, 12-14, 18 | | high | $CF:F000, $83:F388, $86:F083 |
| 0x133000-0x1331FF | $A6:B000 | 0x200 | palettes | | high | $86:8163, $86:816F (MVN) |
| 0x133200-0x133BFF | $A6:B200 | 0xA00 | **World map palettes** (worlds 0/1/2: $A6:B200, $A6:BA00, $A6:B400) | | high | $86:A018, $86:A177, $86:CE3B |
| 0x133C00-0x133DFF | $A6:BC00 | 0x200 | event object gfx 0F, 15 | | med | $83:F388 |
| 0x133E00-0x13401F | $A6:BE00 | 0x220 | tiles + palettes ($A6:BF00 0x100 -> CGRAM buffer; $A6:BFE0 0x20; $A6:C000 0x20 -> $0500) | | med | $83:B077, $80:BCD3, $86:CDDE |
| 0x134020-0x136A1F | $A6:C020 | 0x2A00 | Map sprites (small/one-frame objects: 27, 4A-5E, 61-6B, 8E-9A, C9-CF, DC-E0) + object gfx 05, 1B | | high | $CF:F000 |
| 0x136A20-0x13741F | $A6:EA20 | 0xA00 | **Palettes** (0x100-byte sets for title/intro/ending scenes and world map 2 extra $A6:EB20) | | high | $86:CED1, $84:A538...$84:F3B6 (MVN) |
| 0x137420-0x137517 | $A6:F420 | 0xF8 | byte ramp table (menu effect, guess) | | low | $82:D348 |
| 0x137518-0x13760C | $A6:F518 | 0xF5 | 7-byte menu window/cursor records (guess) | | med | $82:891E (20 callers) |
| 0x13760D-0x137A8C | $A6:F60D | 0x480 | map sprites 5F-7F, object gfx 0E, 16, 19, 1A | | high | |
| 0x137A8D-0x137B0C | $A6:FA8D | 0x80 | 8x8 BG tilemap block (menu window) | | high | $82:C875 |
| 0x137B0D-0x137B2C | $A6:FB0D | 0x20 | palette | | high | $83:B083 (MVN) |
| 0x137B2D-0x137FFF | $A6:FB2D | 0x4D3 | unidentified high-entropy bank tail | | low | none |

Map sprite and event object graphic sizes: map sprites use LEdit's tile count from the flags; event object graphics ($83:F388) store no length, so each runs to the next known item (capped at 0x1000).

## 2. Tables and code that reach these banks

| Table / code | Entries | Points at |
|---|---|---|
| $80:E85D-$80:E897 | per map | $8F:8000 object scripts (u24) and $91:B8B5 chests (u16) |
| $80:9383 | 1 | $90:C000 SPC driver |
| $83:D3F7, $83:DFFD, $83:D416, $83:CA68 | | $91:8000, $91:8EC7, $91:A1D4 actor scripts |
| $8E:B448 | | $91:9AFF credits |
| $CF:FCBC | 242 x u24 | map info $91:A807-$94:F13C |
| $83:9F73 | 4 x u24 | Ancient Cave map-info templates in $94 |
| $83:90xx-$83:9Dxx (Ancient Cave generator) | | $91:FFCA, $91:FFDC, $93:D59B, $93:D69B, $94:D95C, $94:DDF6, $94:EEA0, $94:F13D |
| $81:899A | anim id | $95:8000 battle animation scripts |
| $81:F46B / $81:F476 / $85:BDA2 | | $95:F1EF, $97:F63B, $96:FADD effect scripts |
| $80:98C0 | 0x39 x u24 | $98:8000 BRR samples |
| $8E:E5C2 | 7 x u24 | hero battle sheets ($81:BB9E, $86:8F88) |
| $CF:F000 | 0xFB records (5 bytes, u24 ptr) | map sprite tiles $99-$A6 |
| $83:F388 | 30 x (u24 ptr + flags) | event object gfx $99-$A6 ($83:EC9C) |
| $86:F083 | 6-byte records (u24 source + 3 parameter bytes) | world-map sprite gfx $9C, $9D, $A0, $A1, $A5, $A6, $7E:E000 |
| $86:CE3B | 3 x 0x12 bytes: tiles file, map file, u16, palette u24, big blocks u24, small blocks u24, extra palette u24 | world 0: 0x28B/0x28C, $A6:B200, $9A:8000, $9E:B000, $A6:BF00; world 1: 0x28E/0x28F, $A6:BA00, $9B:8000, $A1:A8F2, $A6:BF00; world 2: 0x28B/0x28C, $A6:B400, $9A:8000, $9E:B000, $A6:EB20 |
| $CF:FA08 -> $CF:FAFA | per map -> 0x57 x u16 | $9B palette sets |
| $CF:FBA6 -> $CF:FC98 | per map -> 0x12 x u16 | **$A1 tile animation records** (correction: rommap called these packed-file numbers) |
| DMA / MVN with immediate banks | | $9F:8500, $9F:BA80, $A5:E2FC, $96:FF3C (VRAM); $9F:80xx-84xx, $A6:B000-BFE0, $A6:EA20-F320, $A6:FB0D (palettes) |

## 3. Corrections and notes for rommap.md

* `$CF:FC98` holds $A1 bank pointers to tile animation records, not packed-file numbers (read with DB = $A1 at $80:EFE1).
* "Puzzles" at $8F:8000 (l2ref) are the per-map object scripts of the third interpreter ($80:CC3F/$80:E58B).
* The US chest table is $91:B8B5 (0x8B8B5); its lists end exactly at the next map info block (0x8BDA8).
* The capsule attack names end at 0x12E2FC; 0x12E2FC-0x12E5FB is graphics (DMA source).
* `$9C:9F1A` in l2_refs.txt (`SBC $1C9F1A,X` at $83:DE67) is a decode artefact, not a reference.
* Songs are packed files; this range holds only the driver ($90:C000) and the instrument samples ($98:8000).
* There are two world maps: the second uses tile/map files 0x28E/0x28F, big blocks $9B:8000, small blocks $A1:A8F2 and palettes $A6:BA00.
* Bank $97 also holds the shop item lists (0xBEF15-0xBF63A) and the fixed battle formations ($97:C53D).

## 4. Still unidentified

| File offset | Size | Notes |
|---|---|---|
| ~0x083E60-0x083FFF | ~0x1A0 | after the last object script, before the SPC driver |
| 0x087D37-0x087FFF | 0x2C9 | bank $90 tail after the SPC upload's end marker |
| 0x0A717E-0x0A7FFF | 0xE82 | bank $94 tail |
| 0x0BFE64-0x0BFFFF | 0x19C | bank $97 tail |
| 0x137B2D-0x137FFF | 0x4D3 | bank $A6 tail |

All five are high entropy (6.7-7.2 bits/byte), sit at the end of a bank or block, and have no code reference, no u24 pointer anywhere in the ROM, and no immediate-operand load. They do not walk as packed files and are not copies of other ROM data. Likely build leftovers (low confidence); they are the best candidates for free space but are unproven.
