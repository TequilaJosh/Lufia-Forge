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
| + Exit (E) | Drag where the party leaves; then pick the map and block it leads to (Pick on map...). On the world map this adds an entrance to a town or dungeon (see below). |
| + Step-on area (T) | Drag an area; its event (empty) is made when the map is written; Event... opens it in Events. |
| + Character (C) | Click where it stands; pick its sprite (the picture shows it) and how it moves; Talk event... gives it something to say. |
| + Point (A) | A preset point: scenes put the party (Go to another map) or characters (Put a character on a point) on it. |
| + Chest (K) | Click a block: a chest with the picture of a chest of this map (both layers' block copied onto it), its own opened flag, and an item picked in the inspector. Pressing A facing it gives the item once. |
| + Hidden item (H) | Click a block: nothing shows there; pressing A facing it finds the item, once. The block can't be walked onto (as a chest), so put it on furniture, a wall or a dead end. |

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
  by themselves when another map is picked or the project is saved, or with Apply; Revert drops them. The block editor
  writes straight into the ROM (Edit > Undo takes it back).
* **Treasure**: Select a chest or hidden item (pink, labelled with what it holds; ? = hidden) to change its item, move it
  or delete it. A chest's picture is part of the map: when you move one, paint it where it goes. Gold isn't possible: the
  game's treasure records hold an item.
* **World map and sea floor** (maps 00 and 01): shown at full size (one game pixel per screen pixel at 100%, sharp at any
  zoom). Paint, Pick and Area work on their big blocks (32 x 32 pixels).
* **Entrances** (world map): the green boxes are the world map's exits, named after the map they lead to. + Exit, then
  click (or drag over) the blocks the party walks onto, then Pick on map... in the inspector: pick the town or dungeon and
  click where the party arrives. Select moves them, Del takes one out, double-click goes into its map. Leaving a town
  works the other way: a town exit that leads to map 00 picks its world map spot in Pick on map... (the world map is in
  its list, with the towns labelled); such an exit gets the world map arrival style F0 by itself.
* **Battle zones** (world map): the toggle colours each zone and lists them on the right with each zone's groups, their
  share of the zone's battles and their monsters. Paint picks a zone, then click or drag on the map (2 x 2 big blocks per
  square; right-click picks the zone under the mouse; 00 = no battles). Monsters... opens the zone on the Encounters page.
* **Encounters page**: World zones (where the zone is on a small world map, which towns it is near, its up to 5 groups
  with their chance and their monsters' pictures; add or take out groups), Dungeons (each field monster of a map and the
  group it fights) and All groups. The group on the right shows its monsters with their pictures, picked from a list with
  pictures; Remove and Add change them. How a group is read: a battle starts with the monster in place 1 or 2 (picked at
  random) and adds monsters of up to 2 more kinds from the list; how many come depends on the party's size. A group with
  place 1 empty and place 2 filled is a fixed battle (the other places are layout data): only place 2 can change.
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
* **Entrances**: map 00's info block, list +06 (9 bytes as a town's exits; places in 16-pixel blocks, 256 x 256), the
  same code as a town's exits. Arrival style 40 is what most entrances use. Its preset points (+0E) are where scenes put
  the party; a new game can't start on the world map (the party would stand in the sea), so the test start refuses it.
* **Battle zones** ($86:9CF3): the zone under the party is `$7F:E000[(x / 4) + (y / 4) * 64]`; zone z is 5 pairs (group,
  weight) at $97:C493 + z * 10 (file 0xBC493), 17 zones. The game adds the weights (0 = no battles there) and picks a group
  by weight. How often battles come is the same everywhere (Settings).
* **Treasure** (`L2Chests`): the spot is the info block's list +26 (4 bytes: id, x, y, look); the look is a record of list
  +04 (10 bytes: id, layers, the opened picture's source x, y, w, h, the closed one's x, y, w, h, in blocks of the same
  map); the contents are the chest list at $91:B8B5 (a u16 per map, then 3 bytes per treasure: spot | 40 item + 0x100 | 80
  flag + 0x100, flag, item; FF ends). On loading a map the game marks every spot's block ($80:EE36); pressing A facing one
  ($83:BEB0 -> $8E:C05F) gives the item and sets the opened flag (a bit at $7E:093B, 512 of them; the game uses 0-0xAA)
  when it is clear, and copies the opened picture onto the spot. A new chest takes the look of a chest of the map; a hidden
  item gets a look of its own that copies the spot onto itself (so nothing changes). New treasure gets the lowest flag no
  treasure of any map uses. The chest lists fill the table up to the next info block, so a list that grows goes to
  $92:8000 on (the table's offsets are 24-bit sums from $91:B8B5, so 0xC74B on reaches bank $92's ROM): that is map 00's
  info block, which is moved to the added space the first time (any bank works for an info block).
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

## 3b. Checked: world map, entrances, zones, treasure (2026-10-27)

* In the real window (a project made from the clean US ROM; 30 checks): the world map is 4096 x 4096 pixels and every
  pixel checked is the game's; Pick and Paint hit the right big block; Undo; + Exit on the world map adds an entrance,
  labelled with its map after pointing it at 2F (20,48), moved by dragging, written by Apply; Battle zones lists the 17
  zones with their monsters, Paint and painting a square of zone 0F; Monsters... opens zone 0F on Encounters, its groups
  cut to group 10 alone, group 10's monsters changed, taken out and added (pictures shown); an Elcid exit pointed at the
  world map (40,220) gets style F0; + Chest and + Hidden item on 2F, their items, Undo / Redo, Apply (each its own new
  flag); the world map's entrances still all read after its info block moved for the treasure list; a town drawn after
  the world map.
* In the game (BizHawk), on ROMs written by that window: walking out of Elcid through the new exit puts Maxim on the
  world map at (40,220); two steps right, the new entrance takes him into Parcelyte Castle 2F; with the battle rate at
  most, zone 0F there gives group 10 (Red Bats; `$7F:F8A4` = 10); on 2F the new chest gives a Hi-Potion and the hidden
  item a Miracle, each once (a second A gives nothing; flags AB and AC set), and the old chest still gives Holy wings.

## 4. Shrine maps CF and F1: fixed as in the European release

In the US game maps CF and F1 (shrines) are garbage: their map files ask for blocks that block set 17 and tile set 11
don't have. The European release (*Lufia*, 1996; its packed files are numbered one higher from 0x40) has the same two
map files but a bigger block set 17 (0x30D blocks instead of 0x2CF) and tile set 11 (819 tiles instead of 802), and
maps E7-EB, which share those sets, renumbered to them.

When a project opens, Lufia Forge fixes this (`L2ShrineFix`, one Edit, Undo step) while both sets are still the US
game's: it puts the European sets in (`Fixes/Lufia2_ShrineSets.bin`: the block set, and the tile set as tiles of the US
tile set 11 by number plus the 17 it lacks) and gives E7-EB, block by block, the block of the new set that looks the
same (pixels and priority; every block they use has one). None of these maps has a puzzle script or tile animation,
and events only copy tiles within a map, so nothing else names their block numbers. The fan hacks already carry the
European block set 17 and their own E7-EB, so they are left alone.

Checked: after the fix CF and F1 draw exactly as in the European game and E7-EB exactly as before (every pixel); a new
game started on CF shows the shrine in the game (BizHawk), where the US ROM shows scattered pieces.
