# Lufia II maps: the Maps tab

The Maps tab edits Lufia II's maps the way Lufia 1's map editor edits Lufia 1's: blocks on both layers, collision,
exits, step-on areas, characters, preset points, treasure spots, the map's graphics and size, the world map and the
sea floor. Code: `Modules/Lufia2/L2MapsPage*.cs` (the tab), `Core/Lufia2/L2MapEdit.cs` (a map being edited and how it
is written), `Modules/Lufia2/L2MapWindows.cs` (pick a place, pieces and presets, the block editor).

## 1. Using it

| Tool (key) | What it does |
|---|---|
| Select (S) | Click an exit, step-on area, character, preset point, treasure spot or door to edit it in the inspector; drag to move it (an exit's or area's bottom-right block resizes it). Clicking the same block again reaches what lies under it. Double-click: an exit goes to its map, a character opens its talk event, an area its event. Del takes it out. |
| Paint (B) | Paints the brush on the ground or the upper layer ("Paint on"). Drag on the block list for a brush of several blocks. Right-click or Ctrl + click picks the block under the mouse. |
| Pick (I) | Takes the block under the mouse as the brush. |
| Area (M) | Drag a rectangle: Copy (Ctrl+C), Cut (Ctrl+X), Paste (Ctrl+V, then click to place; Esc stops), Fill, Delete (Del), Save as preset. Copies take both layers and their walls; they paste on any map with the same graphics. |
| Collision (L) | Paints "can't walk", doorway, wall on the top or left edge, upper level or room border; right-click clears a block. |
| + Exit (E) | Drag where the party leaves; then pick the map and block it leads to (Pick on map...). |
| + Step-on area (T) | Drag an area; its event (empty) is made when the map is written; Event... opens it in Events. |
| + Character (C) | Click where it stands; pick its sprite (the picture shows it) and how it moves; Talk event... gives it something to say. |
| + Point (A) | A preset point: scenes put the party (Go to another map) or characters (Put a character on a point) on it. |

* **Show**: ground, upper layer, collision, characters, exits (labelled with the map they lead to), step-on areas,
  treasure, doors, preset points, and the camera (the 16 x 14 blocks the screen shows with the party on the block under
  the mouse; it stops at the map's edges).
* **Zoom**: the buttons, Fit, or Ctrl + mouse wheel; the middle mouse button drags the map. The status bar says what is
  under the mouse (both layers' blocks, walls, what stands there).
* **Blocks**: the order (block set order, the blocks the game's maps with these graphics use most, or only this map's),
  recent brushes, "Goes with this block" (what the game's maps put to the right, left, below and above the brush),
  Pieces... (copy a part of any map with the same block set, or one of your presets), Edit blocks... (the block editor).
* **Map settings**: the block set, tile set and palette set (drawn at once; block sets and tile sets go in pairs), the
  size, adding a character, and the test start (a new game starts on this map at a preset point; Normal start puts the
  game's opening back).
* **Names**: "Rename this map" keeps your name in the project.
* **Undo**: every change is one step (Ctrl+Z / Ctrl+Y, up to 300). The edits are kept in the tab ("Edited") and written
  by themselves when another map is picked or the project is saved, or with Apply; Revert drops them. Treasure contents
  and the block editor write straight into the ROM (Edit > Undo takes them back).
* **World map and sea floor** (maps 00 and 01): Paint, Pick and Area work on their big blocks (32 x 32 pixels in the
  game, shown at half size). On the world map, Battle zones shows and paints the random-battle zones (the zone typed
  beside it; right-click picks one; 00 = no battles).
* Lufia II keeps its 242 maps in fixed tables, so maps can't be added or deleted (yet): resize one, paint it, place
  pieces of others on it.

## 2. How it is written

* **Blocks**: the map's packed file (0x3F + map) is rebuilt: the 6-byte head, then each layer (u16 head, width, height,
  cells of block-low and flags). A resized map gets its cells laid out again; sizes stay within the game's biggest maps
  (96 wide, 88 high, 7744 blocks in all), since the game's buffers already hold those.
* **Info block** (exits +06, step-on areas +0C, preset points +0E, characters +10, treasure +26; events.md section 5):
  edited as a copy. A list that grows is copied to the end of the block with its new record (or grows in place when it
  is last); a record taken out moves the later ones up. Written over the old bytes when the block kept its size and no
  other map shares it, else into the added space with the map's pointer ($CF:FCBC) moved. Lists the editor doesn't
  change (doors, paths with their programs, areas, puzzle spots) keep their bytes and offsets.
* **Characters**: a new one is `68 id sprite` at the top of the start-up script; a sprite change rewrites every 68 / 7B
  of that id; one taken out loses its 68 / 7B (jumps to it go to the next step) and its talk event (+6).
* **Step-on areas**: a new one gets an empty event in the +8 list; one taken out loses it.
* **Graphics**: the map's bytes in the block set, tile set and palette set lists ($CF:F72E, $CF:F81E, $CF:FA08).
* **Block editor**: the block set file (0x130 + set): 8 bytes per block from +0x10, four SNES words (top left, bottom
  left, top right, bottom right: tile 10 bits, colour row 3 bits, priority, flip across, flip up-down).
* **World map**: file 0x28C (sea floor 0x28F), a 0x40-byte head then 128 x 128 u16 big-block numbers; the zones are file
  0x193 (64 x 64 bytes, one per 2 x 2 big blocks).
* **Test start**: the new-game code at $83:ACCD (map, scene, preset point); the start map gets a scene of its own (F0 up:
  show Maxim, fade in).

## 3. Checked

* The tab in the real window (51 checks): search, painting and Undo / Redo, a 2 x 2 brush, picking, copy / paste / fill,
  collision, adding an exit (pointed at another map in the inspector), a step-on area, a character (Guy, wandering) and
  a point, dragging the character and its walk area, taking an area out, the palette set and its Undo, zoom, the camera,
  Apply and reading everything back (every event and the start-up script of the map still read), Talk event opening
  Events on the new character, a game character taken out of another map written by itself on switching maps (its 68
  and its talk event gone), a resize (and a size past the biggest map refused), a chest's item, the block editor, the
  test start.
* The world map tab (12 checks): painting, Undo, copy and paste of big blocks, zones, Apply and the files read back,
  the sea floor drawn.
* In the game (BizHawk): a new game on the edited Elcid; the new character walks there; an exit added in the editor
  leads to its map (Elcid's house) and another to the world map, where the big blocks painted and pasted in the editor
  show as sea next to Elcid; a resized map (Parcelyte's houses, 46 x 38) loads as the unchanged one does.
