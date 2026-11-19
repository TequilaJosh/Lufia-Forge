# Phase 7 - Map Viewer, Map Editor & Map Creator

Read-only map viewing, full tile-level editing of existing maps, and creation of brand new maps from scratch.

> **Depends on:** Phase 3 (live VRAM for accurate tile rendering), Phase 4 (disassembler to find map format), Phase 5 (tile decoder for rendering)

**Status: ✅ Complete (2026-11-16).** Built as one tab per game that views and edits at once, not a separate viewer and editor. Lufia 1:
the Map Editor tab (`Modules/MapEditor`), with `LufiaMap` (Core/Maps) as the model and loader and `MapWriter` as the writer; maps are
packed resources, unpacked and packed with `LufiaCompression`. Lufia II: the Maps page (`L2MapsView` / `L2MapsPage`, model `L2Maps` and
`L2MapEdit` in Core/Lufia2). The format research is in `research-script-engine.md` (Lufia 1) and `LufiaForge/docs/lufia2/maps.md`
(Lufia II). New maps, resizing and new tilesets were finished in Phase 10 (`phase-10-graphics-and-new-content.md`). Differences are
marked *(changed)*; parts not built are marked *(dropped)*.

---

## Research Sprint (do before writing any Phase 7 code)

- [x] Use the disassembler to trace the map tile upload routine *(changed: found through the resource unpacker calls instead of the VRAM registers: map data `$01:BDA9` to `$7F0000`, blocks `$01:BDD8`, tile graphics `$01:BE55` to VRAM byte `0x2000`; `research-script-engine.md`, "Compression and resources" and "Map data format")*
  - Find writes to VRAM upload registers (`$2116`/`$2118`) and trace backward to the map data pointer
- [x] Find where the current map index is stored in RAM using the memory viewer *(found: WRAM `$0D15`, and `$077F` while a map loads; `research-script-engine.md` "Maps and per-map events", `Lufia1Constants.RamCurrentMap`)*
  - Watch for a value that changes when you walk between areas
- [x] Find the map pointer table in ROM - a table of 24-bit pointers, one per map *(changed: there isn't one; map data is resource `0xB2 + byte[$01:E19B + map]` and the resource table at `0x60000` holds the 24-bit pointers (`LufiaMap.MapDataSelectTable`). Lufia II: file `0x3F + map`, `L2Maps`)*
- [x] Characterize the map tile data format:
  - [x] Is map data compressed? (try RLE, LZ77) *(found: yes, the game's own LZ packing, ported as `LufiaCompression`; Lufia II map files are packed too)*
  - [x] How many bytes per tile entry? (1 byte = 256 tile types, 2 bytes = tileset + flags) *(found: a u16 per cell, low 10 bits = block number; Lufia II: a block byte and a flag byte per cell)*
  - [x] Are metatiles used? (2x2 or 4x4 groups of base tiles) *(found: yes, 16x16 blocks of four 8x8 tiles, 9-byte records ending in an attribute byte; `MapTileset`, `BlockSet`)*
  - [x] Does the map header include width, height, tileset ID? *(found: tileset `0x08`, palette `0x09`, width `0x12`, height `0x14`; `research-script-engine.md` "Map data format")*
- [x] Find NPC placement data - probably a separate table per map *(found: section C of the map's object block, 14 bytes each (`MapNpc`); Lufia II: the map's info block (`L2MapEdit`))*
- [x] Find collision/passability data - may be embedded in tile entries or a separate layer *(found: the attribute byte of each block, walls by side and 20 = solid, checked in game in Phase 10; Lufia II keeps a flag byte per cell, `L2Maps.Layer.Flags`)*
- [x] Document all findings in Research Notes and promote to `Lufia1Constants.cs` *(changed: written up in `research-script-engine.md` (map data, object block, world map, composite blocks) and `LufiaForge/docs/lufia2/maps.md`; RAM addresses are in `Lufia1Constants`, ROM tables are constants in `LufiaMap` and `MapFactory`)*

---

## 7.1 - Map Data Model

- [x] `MapData.cs` - in-memory map representation *(changed: `LufiaMap` (Core/Maps); Lufia II: `L2Maps.Map`, with `L2MapEdit` as the working copy)*
  - [x] `int MapIndex`, `string Name`, `int RomOffset` *(changed: `MapId`, `ResourceId`, `CompressedOffset`; names come from `MapNames` through `MapInfo`)*
  - [x] `int Width`, `int Height`
  - [x] `int TilesetId` *(changed: `Tileset` plus `PaletteIndex`)*
  - [x] `ushort[,] Tiles` - 2D array of tile entries (width x height) *(changed: a flat `ushort[] Layer`, Width x Height, plus a read-only `Layer2` for maps with a second layer)*
  - [x] `List<NpcEntry> Npcs` *(changed: `List<MapNpc> Npcs`, next to `Exits`, `Arrivals`, `Spots`, `Triggers` and `Paths`)*
  - [x] `List<CollisionEntry> Collision` (if stored as a separate layer) *(changed: not a map layer in Lufia 1, it's each block's attribute byte in the tileset (`BlockSet`); Lufia II keeps it per cell in `L2Maps.Layer.Flags`)*
  - [x] `bool IsDirty` - true if unsaved changes exist *(changed: `HasUnappliedChanges` on the editor, shown as "Edited")*
- [x] `NpcEntry.cs` - `int NpcId`, `int X`, `int Y`, `int FacingDirection`, `int ScriptOffset` *(changed: `MapNpc`: `Sprite`, `Flags` (facing and movement), `X`, `Y` and a walking box; no script offset, character n runs event n + 1 of the map's event block)*
- [x] `MapLoader.cs` - read map data from ROM into `MapData` *(changed: `LufiaMap.Load` and `MapCatalog`)*
  - [x] `LoadMap(RomBuffer rom, int mapIndex) -> MapData` *(changed: `LufiaMap.Load(RomBuffer rom, int mapId)`)*
  - [x] `GetMapCount(RomBuffer rom) -> int` - reads the pointer table length *(changed: `MapCatalog.Scan` tries every number and keeps the maps whose data starts with their own number, 117 in the original game)*
  - [x] Handle decompression if needed (implement after format is confirmed) (`LufiaCompression.Decompress`)
- [x] `MapSerializer.cs` - write a modified `MapData` back to ROM bytes *(changed: `MapWriter.Save`, with `LufiaMap.BuildData` rebuilding the unpacked data)*
  - [x] Must not exceed original byte length without triggering ROM expansion workflow (a map that doesn't fit moves to the added space; the app asks first when the ROM has to grow)
  - [x] Recalculates map pointer if map offset moves (the resource pointer at `0x60000 + id*3`)
  - [x] Compresses output if original data was compressed (always packed with `LufiaCompression.Compress`, then unpacked again to check)

---

## 7.2 - Map Viewer UI (Read-Only)

- [x] `MapViewerView.xaml` *(changed: no separate viewer; `MapEditorView.xaml` (Lufia 1) and `L2MapsView.xaml` (Lufia II) view and edit)*
  - [x] Map list: dropdown sorted by map index, with names once confirmed *(changed: a searchable list by number and name; names are the game's town names, worked out from exits, or typed by the user (`MapNames`))*
  - [x] Map canvas: `Image` backed by `WriteableBitmap`
  - [x] Pan: click and drag *(changed: the middle mouse button drags, the left button runs the tools)*
  - [x] Zoom: scroll wheel, 0.25x to 8x *(changed: Ctrl + mouse wheel, 0.25x to 6x, plus Fit and 100%)*
  - [x] Layer toggles: Tile Layer, Collision Layer, NPC Layer, Event Trigger Layer *(changed: Show chips for characters, exits, arrival points, treasure, step-on areas, the game camera and world map battle zones; Lufia II adds ground, upper layer and collision)*
  - [x] Coordinate display: map X/Y and ROM file offset of the hovered tile *(changed: the bar under the map shows block X/Y, the block number and its attribute; no ROM offset, since map data is packed)*
  - [x] Tile inspector: click a tile to show tile index, tileset, collision flag, NPC ID, event ID *(changed: hovering shows the block and what's on it; the Select tool opens characters, exits, arrival points, treasure and step-on areas in the inspector, with their events)*
  - [x] Export PNG: saves current view at current zoom *(dropped: not in the app; full map renders for research came from `tools/map_research/render_all.py`, and the Block editor exports the tile sheet)*
  - [x] "Sync with emulator" toggle: auto-switches to the current map as the player walks around (requires Phase 3 map index tracking) *(dropped: the editor doesn't follow the game; Events > Live flags shows the current map, and Test start begins a new game on the map being edited)*
- [x] `MapRenderer.cs` - renders a `MapData` to a `WriteableBitmap` *(changed: `MapTileset.RenderMap` (Lufia 1) and `L2MapsPage.DrawMap` (Lufia II))*
  - [x] Uses `SnesTileDecoder` from Phase 5
  - [x] Renders tile layer first, then overlays collision grid (semi-transparent red), then NPC sprites *(changed: blocks first (composite blocks as ground, then overlay), then characters, exits and other objects as coloured boxes on top; Lufia II draws collision marks in red, blue and orange)*
  - [x] Only renders tiles in the visible viewport (virtual rendering for large maps) *(changed: the whole map is drawn into one bitmap; Lufia 1 redraws only the changed blocks after an edit)*

---

## 7.3 - Map Editor (Edit Existing Maps)

- [x] Enable edit mode toggle in `MapViewerView.xaml` *(dropped: the map is always editable; the Select tool is for looking)*
- [x] `TilePalettePanel.xaml` - docked panel showing all tiles in the current tileset *(changed: the Blocks panel on the right of the map editor, in tileset order, most used first or only this map's blocks, plus blocks from other graphics through From)*
  - [x] Same `WriteableBitmap` rendering as Phase 5 tile viewer
  - [x] Click a tile to select it as the active brush (drag for a brush of several blocks)
  - [x] Shows selected tile highlighted in gold
- [x] Map canvas edit tools toolbar:
  - [x] Pencil tool - left-click to paint one tile, drag to paint continuously *(changed: the Paint tool (B), brush 1, 3, 5 or 7 blocks; Ctrl + click picks a block)*
  - [x] Flood fill tool - fills a contiguous region of same-tile-type with the selected tile (Fill (G); Shift + click fills the whole map)
  - [x] Eraser tool - replaces with tile index 0 (typically empty/void) *(changed: no eraser tool. Lufia 1 has no empty block, so Del fills the area with the brush block; on Lufia II, Delete clears the layer to block 000, which draws nothing)*
  - [x] Rectangle tool - draws a filled or outlined rectangle of the selected tile *(changed: the Area tool (M) plus Fill fills a rectangle with the brush; no outline mode)*
  - [x] Selection tool - drag to select a rectangular region *(changed: the Area tool (M))*
    - [x] Cut, Copy, Paste selection (Ctrl+X, Ctrl+C, Ctrl+V; Save as preset keeps a piece for later)
    - [x] Move selection by dragging *(changed: Cut, then Paste stamps it at the new place)*
- [x] NPC editor mode: *(changed: no separate mode, the Select tool does it)*
  - [x] Click an NPC on the map to select it - shows NPC inspector panel
  - [x] Drag NPC to move it
  - [x] Right-click NPC: Delete NPC, Edit Script (links to Phase 6 script editor) *(changed: no right-click menu; the inspector has Event / dialogue... (opens the event editor) and Hide / remove, and clicking a character without dragging opens its event)*
  - [x] Add NPC button: click anywhere on map to place a new NPC at that position *(changed: + Add character puts one near the first arrival point to drag into place; Lufia II's + Character tool places it where you click)*
- [x] Collision editor mode: *(changed: Lufia 1 walls belong to blocks and are set by side in the Block editor (`BlockEditorWindow`, Phase 10); Lufia II has a Collision tool (L))*
  - [x] Toggle per-tile passability by clicking *(changed: Lufia II paints can't walk, doorway, walls and wall lines per cell, right-click clears; Lufia 1 sets walls per block)*
  - [x] Shows collision overlay while active *(changed: Lufia II only, the Collision chip; Lufia 1 shows the attribute of the block under the mouse)*
- [x] Undo/redo stack - minimum 50 levels, stored as delta operations (not full map copies) *(changed: Lufia 1 keeps up to 300 steps per map as whole-map snapshots (`LufiaMap.State`), Lufia II keeps `L2MapEdit` states; after Apply, Edit > Undo takes the write back)*
- [x] Save map changes to `RomBuffer` - writes modified tiles back via `MapSerializer` *(changed: Apply, picking another map or saving the project writes the map with `MapWriter.Save`; Lufia II through `L2MapEdit`)*

---

## 7.4 - Map Creator (New Maps from Scratch)

- [x] New Map dialog: *(changed: no dialog; Map settings has "Copy of this map" and "Blank (brush block)", `MapFactory`, Phase 10 section 10.3)*
  - [x] Map name input *(changed: typed afterwards in "Rename or delete this map")*
  - [x] Width and height inputs (in tiles) *(changed: a new map starts at the current map's size; Resize map changes it)*
  - [x] Tileset selector *(changed: it takes the current map's graphics; the Graphics box and + New tileset change them)*
  - [x] Fill tile selector (what to fill with initially) *(changed: Blank fills with the brush block)*
  - [x] Creates a new `MapData` with a new map index appended to the pointer table *(changed: takes a map number the game left free (02-A3) and fills the data select, setup, event and area-name tables; Lufia II new maps take F2-FF, `L2NewMaps`)*
- [x] ROM expansion workflow (required for new maps):
  - [x] New maps need space in the ROM - existing 1MB may not have room
  - [x] `RomExpander.cs` - expand ROM to 2MB by appending a zero-padded bank *(changed: `RomBuffer.Expand`, called from `MapWriter` when a map doesn't fit; the new space is filled with FF)*
    - [x] Update SNES header size byte and recalculate checksum (`RomBuffer.Expand`, `RomBuffer.FixChecksum`)
    - [x] Warn user that expanded ROMs require a patched emulator or flash cart that supports >1MB LoROM *(changed: the app asks before expanding and says expanded ROMs work in BizHawk, Snes9x and bsnes)*
  - [x] New map data written to the expanded bank *(changed: into the added space, given out by `ExpansionSpace`)*
  - [x] Map pointer table updated with new entry *(changed: the map's resource pointer at `0x60000` and its byte in `$01:E19B`; new resource ids from `ResourceIds`, Phase 10)*
- [x] Connection editor - define which map exits lead where *(changed: exits and arrival points are edited on the map; `DestinationPickerWindow` picks the target)*
  - [x] Each map edge (N/S/E/W) or specific tile can be set as a transition *(changed: an exit is any rectangle of blocks, edge strips included; + Exit drags one)*
  - [x] Set: target map index, target X/Y entry point *(changed: target map plus an arrival point number there, the arrival point holds X/Y; Pick on map... clicks an arrival point or a new spot)*
  - [x] Visual: arrows drawn from exit tiles to their destination maps in a world graph view *(changed: exits are labelled with their destination, a dotted line goes to the arrival point when it's on the same map, double-click goes to the destination)*
- [x] World map overview panel - shows all maps as boxes with connections drawn between them *(dropped: maps are picked from the list and exits are followed by double-clicking; the game's own world map is edited like any other map)*
  - [x] Click a map box to open it in the editor *(dropped: no overview panel)*
  - [x] Drag map boxes to visually organize (cosmetic only, does not affect ROM layout) *(dropped: no overview panel)*

---

## 7.5 - Known Technical Challenges

| Challenge | Notes |
|-----------|-------|
| Format completely unknown | No prior documentation. The research sprint is mandatory and may take significant time. Do not start implementation until at least one map is successfully loaded and rendered correctly. |
| Compression | If map data is compressed, a decompressor must be written and tested against every map before the editor can safely write data back. Writing back uncompressed data into a compressed slot will corrupt the ROM. |
| Metatile system | If Lufia 1 uses metatiles, the editor must edit at the metatile level for most operations, with raw tile editing only available as an advanced mode. The research sprint must confirm this before 7.3 is started. |
| ROM expansion | Adding new maps may require expanding beyond 1MB. The LoROM mapping mode supports up to 4MB in theory but real SNES hardware and most emulators have limits. Test expanded ROMs in Snes9x and bsnes before shipping this feature. |
| NPC script linkage | Moving or deleting NPCs must not break script pointer references. The script editor (Phase 6) and map editor must share the same NPC data model. |
