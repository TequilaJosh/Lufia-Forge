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
| + Chest (K) | Click a block: a chest with the picture picked in "Chest picture" under the map (this map's own chest, or any chest of the game, closed and open; see below), its own opened flag, and an item or gold picked in the inspector. Pressing A facing it gives it once. |
| + Hidden item (H) | Click a block: nothing shows there; pressing A facing it finds the item, once. The block can't be walked onto (as a chest), so put it on furniture, a wall or a dead end. |

* **Show**: ground, upper layer, collision, characters, exits (labelled with the map they lead to), step-on areas,
  treasure, doors, preset points, and the camera (the 16 x 14 blocks the screen shows with the party on the block under
  the mouse; it stops at the map's edges).
* **Story**: a map's characters change with the story. Its start-up script makes some only while story flags are on or
  off (Elcid: characters 1-7 while flag 0B is off, Tia (character 10, sprite 04) and character 11 while it is on). The
  list offers each story point the script tells apart; picked, the map shows only the characters the game makes then,
  with the sprite they have then, and clicks skip the others. The inspector says when the game makes a character, or
  that the start-up script never makes it (an event does, or the game never shows it).
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
  or delete it. Moving a chest (dragging it, or its X / Y) moves its picture: the closed chest goes where it goes and the
  block it stood on gets the floor it had (when Lufia Forge put the chest there) or the block the 4 blocks around it have
  most; it is one Undo step, and dragging over other blocks leaves them as they were. Taking a chest out gives its block
  the floor back the same way.
* **Gold**: tick Gold in the inspector (or pick Gold on the Treasure page) and type the amount, 1 to 9,999,999. The game
  says "Found 1234 GOLD" and adds it to the party's gold, once, for chests and hidden items. Up to 32 different amounts in
  the whole game (treasure with the same amount shares one).
* **Chest picture** (the + Chest tool, under the map): this map's own chest comes first, then every chest of the game,
  those with this map's graphics first ("same graphics", then "same tiles"), each shown closed and open. A chest of
  another map is drawn with this map's block set: each of its blocks is a block of the set that draws the same, or a new
  block added to the block set (straight into the ROM, as the block editor does; Edit > Undo takes it out) made of this
  map's tiles in the palette row closest in colour. When the map's tile set has no tiles of that shape (towns have no
  chest tiles), the chest's own tiles are copied into the tile set, in the colour row of this map closest to the
  chest's colours, on tiles nothing draws; the status says how many, which tiles, which colour row and whether the
  colours are close ("8 chest tiles added to tile set 2 (tiles 363, ..., which grows from 867 to 875 tiles), drawn with
  colour row 4, the closest to the chest's colours"). The tiles and blocks are one Edit > Undo step. The chest is
  refused, saying why, when the block set is full (1024 blocks) or the tile set has no free tile left (1024 tiles). The
  open and closed chest are also kept on two blocks the camera never shows (as the game keeps its own, mostly in a
  corner): black ones first, else (towns are drawn edge to edge) blocks out of the screen wherever the party can walk;
  the status says where. A map with no chest gets its first chest this way, towns too.
* **Treasure page**: it edits the same treasure as the Maps tab. Showing it writes the Maps tab's changes first, and a
  change there is written by spot over what the ROM holds then; back on Maps, the map's edit takes the Treasure page's
  changes (for the treasure it didn't change itself, also in its Undo steps), and Apply writes only what the map's edit
  changed. Neither page writes over the other's changes, in either order.
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
  + New zone adds a zone past the game's 17 (up to 256) and picks it to paint; give it groups with Monsters... (or + New
  zone on the Encounters page, which adds one there). A zone that doesn't exist can't be painted.
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
* **Characters**: their sprites are read from the start-up script's own steps (`L2MapInfo.Stages`: each `68` / `7B` with
  the flag checks that jump over it). Before 2026-10-29 the editor scanned 1 KB of bytes from the script's start, past
  its end into the next maps' scripts, so 155 characters on 73 maps that no create makes were drawn with another map's
  sprite (Dankirk North Cave B5's character 1 as a soldier, Dragon Mountain's character 2 as a monster). A new one is
  `68 id sprite` at the top of the start-up script; a sprite change rewrites every 68 / 7B of that id; one taken out loses its 68 / 7B (jumps to it go to the next step) and its talk event (+6).
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
* **More zones** (`L2Encounters.AddZone`): the zone byte can be 0-255 and the offset is the SNES's 8 x 8 multiply
  (zone * 10, up to 2550), read by three long loads: $86:9D22 and $86:9D3E (`LDA $97C494,X`) and $86:9D59
  (`LDA $97C493,X`); nothing else reads the table, and the next table starts at $97:C53D. The first + New zone copies the
  17 zones into the added space (room for 256: "LF2ZN", the count - 1, then the table 16 bytes on) and points those three
  loads at it; later zones only raise the count. Another hack's changed loads stop it (the Encounters page is read-only
  then too).
* **Gold** (`L2Gold`): opening treasure ($8E:C153) puts the item in $7F:D4EF / $7F:D4F0 (bit 0 = item + 0x100) and calls
  $8E:C1DE, which opens the chest ($83:F559), writes "Found " ($85:8000) and the item's name ($0B77, 12 letters) into the
  message ($8E:B5FB), picks the sound, shows it and gives the item; with bit 7 of $7F:D4F0 set it gives nothing (the
  caller still sets the opened flag). The JSL $8E:C1DE at $8E:C195 (towns and dungeons; the other caller, $8E:C0F5, is taken when bit 0 of $05B6 is set and reads its chests from a list in RAM, the Ancient Cave's by the look of it, and is left alone)
  goes to Lufia Forge's block ("LF2GD"): items 0x1E0-0x1FF (past the game's 0x1D3) are gold, slot n = an amount (u24) and
  its text ("1234 GOLD"). For those the block adds the amount to $0A8A / $0A8C (kept at 9,999,999), puts the text where
  the name goes, sets bit 7, does the start of $8E:C1DE itself and goes on at $8E:C26E with the item sound. Other items go
  to $8E:C1DE as before. The editors keep gold as 0x10000 + amount; a slot is found (or taken) when the list is written.
  Refused when $8E:C195 isn't the game's JSL or the ROM has more than 0x1E0 items.
* **Chest pictures** (`L2ChestLooks`): a look's layers byte copies layer n when a bit of 0x11 << n is set ($83:8B6A: both
  bits = the whole cell but its 10 / 20 flags, the low bit = the block only, the high bit = the 04 / 08 walls only). A
  block set file is "MC", the count (u16), 0x10 + count * 8 (u16), then 8 bytes per block from +0x10 and one attribute
  byte per block after them; a block added for a chest goes at the end of both, with the source block's attribute. A
  tile matches when its pixels have the same shape (colour numbers may differ) and a palette row of the map is within 60
  (red + green + blue) per pixel on average. A chest from another map keeps this map's flags (walls) on its blocks. Its
  look is a new +04 record (the source's layers byte; opened source = the kept open chest, closed source = the kept
  closed one), unless a look of the map already opens to the same blocks. The kept pair: two blocks side by side that are
  black on both layers and outside the camera (16 x 14 blocks, one more around, stopped at the map's edges) from every
  block that isn't black; top-left first. When there is none, any two blocks outside the camera from every block the
  party can walk on: filled from the preset points and the blocks beside the treasure (no wall between), crossing a
  cell's edge unless a wall is there (cell flag 04 = wall above, 08 = wall on the left, either layer), never onto an
  obstacle (80), and stopping at exits (x1, y1 to x2, y2, the end not included: the party leaves there, and towns are
  walled only where the party walks, so the road out leads on into the forest). Characters and exits are no starting
  points (some characters wait out of reach for scenes; Elcid's south exit covers the trees beside the road). Else the
  pair seen least, with a warning in the status. Nothing is written when the chest is refused (the tiles and blocks are
  worked out first and written only after the kept pair is found).
* **Chest tiles** (`L2ChestLooks.Translate` / `FreeTiles`): a tile set (file 0x14B + set) is plain 4bpp tiles; the loader
  ($80:F03B) unpacks it to $7E:4000 and copies all of it to VRAM $4000, where block words' 10-bit tile numbers point
  (1024 tiles at most; the tile animation's frames start at $7E:C000). Measured in BizHawk on Elcid: VRAM past the 867
  tiles of set 2 stays empty in the field, during a message and after the menu (the menu draws over $4000 and the map's
  tile set is loaded again, whole, when it closes). A map's tile animation (its $CF:F9FE byte 0 high nibble: file 0x15F +
  n) copies 0x1E0 bytes of a frame to VRAM $4010 ($83:AF54), so tiles 1-15 are drawn over on those maps. Free tiles:
  not 0-15, and used by no block of any block set the game pairs with that tile set (the maps' $CF:F72E / $CF:F81E,
  and the Ancient Cave's 0B / 09 / 04 pairs), those inside the set first, then past its end (the set grows; tile set 2
  has only one unused tile, 00F, which the water animation draws over). A tile of the chest the set doesn't have is
  recoloured: per colour row of the chest, the map's colour row (2-7) whose nearest colours are closest over the
  chest's pixels (red + green + blue), leaving out the colours the map's colour animation changes; a tile the same as
  one already in the set is used again. Colour animation: $CF:FBA6 + map (1-based, 0 = none) picks a $CF:FC98 pointer
  to a record in bank $A1: "PS", count (& 7F), then count u16 offsets from the record to entries of a CGRAM colour
  number, then (duration, BGR555) pairs up to 00 (record 1, Elcid's: colours 97-101, the water's, as measured). The
  tile set is written back packed (`L2Rom.WriteFile`: where it was when it fits, else in the added space with the file
  table pointing there), then the block set; the window makes it one Edit > Undo step. Matching tiles of the set
  (as before) also skip tiles 1-15 on maps with a tile animation and colour rows with animated colours.
* **Treasure** (`L2Chests`): the spot is the info block's list +26 (4 bytes: id, x, y, look); the look is a record of list
  +04 (10 bytes: id, layers, the opened picture's source x, y, w, h, the closed one's x, y, w, h, in blocks of the same
  map); the contents are the chest list at $91:B8B5 (a u16 per map, then 3 bytes per treasure: spot | 40 item + 0x100 | 80
  flag + 0x100, flag, item; FF ends). On loading a map the game marks every spot's block ($80:EE36); pressing A facing one
  ($83:BEB0 -> $8E:C05F) gives the item and sets the opened flag (a bit at $7E:093B, 512 of them; the game uses 0-0xAA)
  when it is clear, and copies the opened picture onto the spot. A new chest takes the picked look (see Chest pictures); a hidden
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

## 3c. Checked: gold, chest pictures, moving chests, more zones, Treasure and Maps together (2026-10-27)

* In the real window (a project made from the clean US ROM; 31 checks, `UiGaps`): dragging 2F's chest moves its picture
  (the closed chest's blocks go with it, the block it stood on gets the floor around it, the block it was dragged over
  stays), one Undo step puts all back; the inspector's X moves it with its picture; Gold 777 in the inspector, Apply
  writes it (the hook goes in); a second chest; a Maps change not written yet is written when the Treasure page shows,
  the Treasure page gives a chest 999 gold, back on Maps the inspector shows it; a Treasure page change then a Maps move
  and Apply keeps both; an edit read before a Treasure change keeps it on Apply; on 1F (no chests) the chest list puts
  the towers' chests first, a cave chest adds one block to block set 11 and keeps its pictures at (34,0) / (35,0), 321
  gold, Apply; on Elcid the cave chest is refused ("This map's tile set (2) doesn't have the tiles of that chest...");
  + New zone adds zone 11 (18 zones) and picks it, painting it, a zone 30 refused, Apply writes it, Monsters... shows
  zone 11 on Encounters (18 zones listed), group 10 added there, + New zone there adds zone 12, the game's zones
  unchanged in the moved table; the Treasure page shows 1F's chest with 321 gold.
* In the game (BizHawk): on 2F a chest with 1234 gold says "Found 1234 GOLD" and the party's gold goes 0 -> 1234; again:
  nothing; a hidden item with 50 gold: "Found 50 GOLD", 1284; again: nothing (flags AB and AC set); the old chest still
  gives Holy wings. On 1F (a map with no chest; ROM written by the window): the new chest with the cave chest's picture
  says "Found 321 GOLD", its picture changes to the open chest and stays open, a second A gives nothing. A new zone 11
  (past the game's 17, the table moved to $D1:8F5F) painted around (40,220) gives its battle: `$7F:F8A4` = 10, two
  Red Bats.

## 3d. Checked: chests in towns, tiles added to the tile set (2026-10-28)

* Core (the clean US ROM): on Elcid each of the game's 126 chest pictures of other graphics fits (4 to 14 tiles added
  to tile set 2, colour rows 4, 2 to 7, about 26 to 52 of 765 off per pixel). A chest next to the first preset point of
  every map with one (86 added, 50 of them with tiles added; 8 refused: block set 12 full, or no room for the kept
  pictures on 02, 38, D6, E6), and after each every map with the same tile set or block set was drawn pixel for pixel
  as before. On Elcid the cave chest of 06 adds tiles 363-36A (867 -> 875; the set still fits where it was) and 3 blocks,
  keeps its pictures at (44,0) / (45,0); after Apply only those two blocks and the chest's look different. Tanbel
  Southeast Tower 1F (3c) still adds 1 block and keeps its pictures at (34,0) / (35,0).
* In the real window (a project made from the clean US ROM; 14 checks, `UiTown`): a chest refused on map 38 changes
  nothing (ROM, Edit history, map); + Chest on Elcid with the cave chest says "8 chest tiles added to tile set 2 ...
  colour row 4"; Edit > Undo is one step ("chest tiles added to tile set 2 and blocks to block set 2") and puts the ROM
  back byte for byte; Redo, then the chest again finds its tiles and blocks (none added again); Apply; the 13 maps of
  tile set 2 / block set 2 drawn as before; a second chest adds nothing. (Elcid is no longer refused as in 3c.)
* In the game (BizHawk, the ROM written by that window): a new game on Elcid, the chest beside Maxim shows closed,
  "Found Hi-Potion", then open; a second A gives nothing; it stays open after the menu (the tile set loaded again) and
  the Hi-Potion is in the items. VRAM at the start, during the message and after the menu holds all 875 tiles of the new
  set (only tiles 1-15 differ: the water animation). From the walkable block nearest the kept pictures (35,8) they
  are off screen.

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
