# Phase 7 — Map Viewer, Map Editor & Map Creator

Read-only map viewing, full tile-level editing of existing maps, and creation of brand new maps from scratch.

> **Depends on:** Phase 3 (live VRAM for accurate tile rendering), Phase 4 (disassembler to find map format), Phase 5 (tile decoder for rendering)

---

## Research Sprint (do before writing any Phase 7 code)

- [ ] Use the disassembler to trace the map tile upload routine
  - Find writes to VRAM upload registers (`$2116`/`$2118`) and trace backward to the map data pointer
- [ ] Find where the current map index is stored in RAM using the memory viewer
  - Watch for a value that changes when you walk between areas
- [ ] Find the map pointer table in ROM — a table of 24-bit pointers, one per map
- [ ] Characterize the map tile data format:
  - [ ] Is map data compressed? (try RLE, LZ77)
  - [ ] How many bytes per tile entry? (1 byte = 256 tile types, 2 bytes = tileset + flags)
  - [ ] Are metatiles used? (2x2 or 4x4 groups of base tiles)
  - [ ] Does the map header include width, height, tileset ID?
- [ ] Find NPC placement data — probably a separate table per map
- [ ] Find collision/passability data — may be embedded in tile entries or a separate layer
- [ ] Document all findings in Research Notes and promote to `Lufia1Constants.cs`

---

## 7.1 — Map Data Model

- [ ] `MapData.cs` — in-memory map representation
  - [ ] `int MapIndex`, `string Name`, `int RomOffset`
  - [ ] `int Width`, `int Height`
  - [ ] `int TilesetId`
  - [ ] `ushort[,] Tiles` — 2D array of tile entries (width x height)
  - [ ] `List<NpcEntry> Npcs`
  - [ ] `List<CollisionEntry> Collision` (if stored as a separate layer)
  - [ ] `bool IsDirty` — true if unsaved changes exist
- [ ] `NpcEntry.cs` — `int NpcId`, `int X`, `int Y`, `int FacingDirection`, `int ScriptOffset`
- [ ] `MapLoader.cs` — read map data from ROM into `MapData`
  - [ ] `LoadMap(RomBuffer rom, int mapIndex) -> MapData`
  - [ ] `GetMapCount(RomBuffer rom) -> int` — reads the pointer table length
  - [ ] Handle decompression if needed (implement after format is confirmed)
- [ ] `MapSerializer.cs` — write a modified `MapData` back to ROM bytes
  - [ ] Must not exceed original byte length without triggering ROM expansion workflow
  - [ ] Recalculates map pointer if map offset moves
  - [ ] Compresses output if original data was compressed

---

## 7.2 — Map Viewer UI (Read-Only)

- [ ] `MapViewerView.xaml`
  - [ ] Map list: dropdown sorted by map index, with names once confirmed
  - [ ] Map canvas: `Image` backed by `WriteableBitmap`
  - [ ] Pan: click and drag
  - [ ] Zoom: scroll wheel, 0.25x to 8x
  - [ ] Layer toggles: Tile Layer, Collision Layer, NPC Layer, Event Trigger Layer
  - [ ] Coordinate display: map X/Y and ROM file offset of the hovered tile
  - [ ] Tile inspector: click a tile to show tile index, tileset, collision flag, NPC ID, event ID
  - [ ] Export PNG: saves current view at current zoom
  - [ ] "Sync with emulator" toggle: auto-switches to the current map as the player walks around (requires Phase 3 map index tracking)
- [ ] `MapRenderer.cs` — renders a `MapData` to a `WriteableBitmap`
  - [ ] Uses `SnesTileDecoder` from Phase 5
  - [ ] Renders tile layer first, then overlays collision grid (semi-transparent red), then NPC sprites
  - [ ] Only renders tiles in the visible viewport (virtual rendering for large maps)

---

## 7.3 — Map Editor (Edit Existing Maps)

- [ ] Enable edit mode toggle in `MapViewerView.xaml`
- [ ] `TilePalettePanel.xaml` — docked panel showing all tiles in the current tileset
  - [ ] Same `WriteableBitmap` rendering as Phase 5 tile viewer
  - [ ] Click a tile to select it as the active brush
  - [ ] Shows selected tile highlighted in gold
- [ ] Map canvas edit tools toolbar:
  - [ ] Pencil tool — left-click to paint one tile, drag to paint continuously
  - [ ] Flood fill tool — fills a contiguous region of same-tile-type with the selected tile
  - [ ] Eraser tool — replaces with tile index 0 (typically empty/void)
  - [ ] Rectangle tool — draws a filled or outlined rectangle of the selected tile
  - [ ] Selection tool — drag to select a rectangular region
    - [ ] Cut, Copy, Paste selection
    - [ ] Move selection by dragging
- [ ] NPC editor mode:
  - [ ] Click an NPC on the map to select it — shows NPC inspector panel
  - [ ] Drag NPC to move it
  - [ ] Right-click NPC: Delete NPC, Edit Script (links to Phase 6 script editor)
  - [ ] Add NPC button: click anywhere on map to place a new NPC at that position
- [ ] Collision editor mode:
  - [ ] Toggle per-tile passability by clicking
  - [ ] Shows collision overlay while active
- [ ] Undo/redo stack — minimum 50 levels, stored as delta operations (not full map copies)
- [ ] Save map changes to `RomBuffer` — writes modified tiles back via `MapSerializer`

---

## 7.4 — Map Creator (New Maps from Scratch)

- [ ] New Map dialog:
  - [ ] Map name input
  - [ ] Width and height inputs (in tiles)
  - [ ] Tileset selector
  - [ ] Fill tile selector (what to fill with initially)
  - [ ] Creates a new `MapData` with a new map index appended to the pointer table
- [ ] ROM expansion workflow (required for new maps):
  - [ ] New maps need space in the ROM — existing 1MB may not have room
  - [ ] `RomExpander.cs` — expand ROM to 2MB by appending a zero-padded bank
    - [ ] Update SNES header size byte and recalculate checksum
    - [ ] Warn user that expanded ROMs require a patched emulator or flash cart that supports >1MB LoROM
  - [ ] New map data written to the expanded bank
  - [ ] Map pointer table updated with new entry
- [ ] Connection editor — define which map exits lead where
  - [ ] Each map edge (N/S/E/W) or specific tile can be set as a transition
  - [ ] Set: target map index, target X/Y entry point
  - [ ] Visual: arrows drawn from exit tiles to their destination maps in a world graph view
- [ ] World map overview panel — shows all maps as boxes with connections drawn between them
  - [ ] Click a map box to open it in the editor
  - [ ] Drag map boxes to visually organize (cosmetic only, does not affect ROM layout)

---

## 7.5 — Known Technical Challenges

| Challenge | Notes |
|-----------|-------|
| Format completely unknown | No prior documentation. The research sprint is mandatory and may take significant time. Do not start implementation until at least one map is successfully loaded and rendered correctly. |
| Compression | If map data is compressed, a decompressor must be written and tested against every map before the editor can safely write data back. Writing back uncompressed data into a compressed slot will corrupt the ROM. |
| Metatile system | If Lufia 1 uses metatiles, the editor must edit at the metatile level for most operations, with raw tile editing only available as an advanced mode. The research sprint must confirm this before 7.3 is started. |
| ROM expansion | Adding new maps may require expanding beyond 1MB. The LoROM mapping mode supports up to 4MB in theory but real SNES hardware and most emulators have limits. Test expanded ROMs in Snes9x and bsnes before shipping this feature. |
| NPC script linkage | Moving or deleting NPCs must not break script pointer references. The script editor (Phase 6) and map editor must share the same NPC data model. |
