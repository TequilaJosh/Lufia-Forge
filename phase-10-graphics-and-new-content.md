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

- [ ] **New map**: start from an empty map or a copy of an existing one; pick tileset, palette, size.
      Needs a free map number (byte ids, 196 select slots, 117 used), a resource id, and entries in the setup-script
      (0x18000) and event-script (0x18200) tables.
- [ ] Resize a map (width / height in blocks).
- [ ] Add / remove NPCs and choose their sprite (section C is only patched in place today).
- [ ] Connect it: exits to / from existing maps, arrival points (tools exist; make sure new maps work with them).
- [ ] World map: zones are painted already (encounter groups); also paint where exits / towns sit and new world tiles.

## 10.4 — Character sprites

- [ ] Indexed-PNG export / import (keep colour numbers), with the palette row editable.
- [ ] Resize a sprite (1–4 x 1–4 tiles) and set its 8 frames; graphics move to the added space when they grow.
- [ ] **New sprites**: the pointer table at 0xE25F (74 sprites) is full — move it (hook $01:AC50) and allow more.
- [ ] Pick an NPC's sprite in the map editor; cutscene / party actors can use the new sprites.

## 10.5 — Monsters

- [ ] **New monster pictures**: picture ids stop at 0x6B (0x6C would be the sprite palette resource) — use new
      resource ids from 10.1 and widen the graphic id lookup.
- [ ] **New monsters**: the pointer table at 0x5800 (165 entries) is full and followed by the records — move it
      (find and hook the code that reads 0x5800), keep records reachable for AI offsets, allow new ids in
      encounters (subgroups, bosses).
- [ ] "New monster" in the Monsters tab: copy an existing one, rename, pick / import its picture, stats, AI, drops.

## 10.6 — Testing & release

- [ ] Every new piece tested in BizHawk (new map walked into through an exit, new sprite on an NPC, new monster
      fought in an encounter, imported tileset rendered in game).
- [ ] UI stress test on the original ROM, a Lufia Restored base and an all-features ROM.
- [ ] Release notes and Discord announcement.
