# Phase 10 — Graphics Import & New Content

Import self-made graphics and add new content: tilesets for town/dungeon maps, brand-new maps, character sprites
(including new ones) and monsters (including new ones). Asked for by Ok Impala! ("only the graphics importer /
exporter is left").

Branch: `phase-10-graphics` (from `phase-06-cutscenes`, released as 0.4.0-beta.7).

> **Already built (don't redo):**
> - `LufiaCompression` (packer + unpacker), `ResourceWriter` (writes a compressed resource in place or moves it to the
>   added space), `ExpansionSpace` (allocation table at 0x1FF000), `IndexedPng`, `SnesTileDecoder` / `SnesPalette`.
> - Graphics tab: export / import any compressed resource as an indexed PNG (same size only).
> - Map editor: block painting, `TileImport` (PNG → new or replaced 16x16 blocks of a town/dungeon tileset), NPC moves,
>   exits, arrivals, spots.
> - World map encounter **zones**: paint / fill / rectangle tools (Encounters tab and map editor), resource 0xB0.
> - Sprites tab: NPC sprite sheet export / import (same size, in place), battle sheet 0x13D.
> - Monsters: picture import / export (indexed PNG, up to 128x112, size can change), stats, AI, give spells.

---

## 10.1 — Foundations

- [x] **Bigger resource table.** The table at 0x60000 holds ids 0x000–0x13E and has no room after it. Move it into
      the added space with room for new ids (hook the game's resource lookup), keeping old ids where they are.
      Everything below that needs new graphics or new maps depends on this.
- [x] Verify the resource lookup hook in BizHawk (title, town, world map, battle) before building on it.
      Done: all 319 resources identical through the moved table; new game, intro, prologue maps and a battle
      play the same; a map loaded from new id 13F in game. (Core/ResourceIds.cs)
- [x] Palette editor (shared, Game Data > Palettes): edit the 16-colour rows a tileset / sprite / monster uses, with PNG palette import / export. Map palettes (with the maps using
      each), shared map rows 0-1, character sprites (0AF), battle sprites (0x11200), monster pictures. Checked in game.

## 10.2 — Tilesets for town & dungeon maps

- [x] Import a whole tileset sheet (PNG, 4bpp, up to 16 KB of tiles) as well as the existing block-by-block import.
      Block editor > Export / Import tile sheet (indexed PNG keeps colour numbers).
- [x] Block editor: build 16x16 blocks from tiles, set each block's walkability / collision attribute.
      Attribute checked in game: the block walked INTO is tested; 01 = wall on its left, 02 right, 04 top, 08 bottom,
      20 = solid; 10 / 40 don't affect walking. (Map editor > Edit blocks; `Core/Maps/BlockSet.cs`)
- [x] Map palette rows editable (the rows 2–7 a map uses) - Palettes tab (10.1).
- [x] New tilesets: room for 64 (tables at $01:E134 / E140 / E14B move to the added space; `Core/Maps/Tilesets.cs`).
      Checked in game: map 94 on a new tileset 11 (copy of 10, recoloured) while map 93 keeps tileset 10.
- [x] World map tiles and blocks import (resource 0x124 and the world map's own block records) - Block editor on the
      world map (its 240 blocks can be edited, not added).

## 10.3 — New maps

- [x] **New map**: start from an empty map or a copy of an existing one; pick tileset, palette, size.
      Uses free map numbers 46-86 (35 of them); fills the data select ($01:E19B), setup (0x18000), event (0x18200) and
      area-name ($01:E93E) tables. Copies get their own event block; blank maps an empty block and an empty setup
      script. Checked in game: walked from map 93 into a copy and into a blank map ($077F = 46). (`Core/Maps/MapFactory.cs`)
- [x] Resize a map (width / height in blocks).
- [x] Add / remove NPCs and choose their sprite; an NPC without an event gets an empty one to edit.
- [x] Connect it: exits to / from existing maps, arrival points (new maps appear in the exit destination list).
- [x] World map: zones are painted already (encounter groups); exits are edited with the exit tools; world tiles and
      blocks in the Block editor (10.2).

## 10.4 — Character sprites

- [x] Indexed-PNG export / import (keep colour numbers; a full-colour picture is matched to the row), with the
      palette row editable.
- [x] Sizes: the game only uses 16x16, 16x24 and 32x32 (bytes 2/3/5 of a record go with the size), so a new
      sprite takes the size of the sprite it copies instead of a free resize.
- [x] **New sprites**: the pointer table moves to the added space (block "LFSP", 192 entries; only the lookup's
      long operand at $01:AC5D changes; entries past the game's 74 keep their old bytes). Records must be in bank 01
      (the callers read them with data bank 01), so new ones go in bank 01's unused tail ($01:F880-FFFF, 112 records).
      Each new sprite gets its own graphics for all 8 frames in the added space. *Checked in game: two new sprites
      (16x24, 32x32) drawn on map 94; every old sprite still loads through the moved table.*
- [x] Pick an NPC's sprite in the map editor (new sprites included).
- [x] **Found on the way: added characters never appeared.** A map's characters only appear when its setup
      script switches them on (41+n, $01:9AB1); character flags 20 like the game's. Setup scripts could only be in
      banks 03-04 (16-bit offsets), so a hook at $01:9884 reads a table of 24-bit offsets first (block "LFSU"; FFFFFF =
      the original entry) and a map's script can move to the added space. Saving a map now switches its new
      characters on (commands put first, jumps moved) and hidden ones off (40 = no-op). *Checked in game: all 117
      scripts moved -> intro, prologue and maps unchanged; new characters appear.*

## 10.5 — Monsters

- [x] **New monster pictures**: a hook at $08:D959 (where graphic + 0x43 became the resource id) reads a table of
      resource ids (block "LFMP"; graphics 00-6B keep theirs), so new pictures get new resource ids. "New picture for
      this monster" gives a monster its own copy to import over (no more changing every monster that shares it).
- [x] **New monsters**: the record offsets are read only by $01:F67D; it now jumps to a copy reading a moved table
      (block "LFMT", room for 255). The callers read records as $D800+offset with data bank 00, so records must stay
      in bank 00 at $D800 or above: new ones go into the old table's place and the end of bank 0 (room for 22). AI
      scripts are found as (record + offset) & FFFF, so they can be anywhere in bank 00: new monsters' scripts go
      into the unused $D2B0-$D7FF first (jumps are moved with them). Encounters list the new monsters.
- [x] "New monster" in the Monsters tab copies the selected one (name, stats, picture, drops, its own AI copy).
      *Checked in game: a new monster with its own recoloured picture in a random battle - named on the target
      cursor, casting spells from its copied script, battle won normally.*

## 10.6 — Testing & release

- [ ] Every new piece tested in BizHawk (new map walked into through an exit, new sprite on an NPC, new monster
      fought in an encounter, imported tileset rendered in game).
- [ ] UI stress test on the original ROM, a Lufia Restored base and an all-features ROM.
- [ ] Release notes and Discord announcement.
