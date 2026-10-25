# Start from a clean slate (Edit menu)

Takes the story out of a ROM so a new one can be built on the same maps. Edit > Start from a Clean Slate...
asks where a new game starts (a map and one of its arrival points); Edit > Undo takes the whole clear back.

Removed: every event and cutscene, every character placed on a map (shopkeepers, innkeepers and priests
included, so shops, inns and churches go with them), step-on areas, and all treasure (chests, gold, hidden items).
Kept: the maps as built (layout, tiles, exits, doors, arrival points), graphics, monsters, encounters, items,
spells and music.

## Lufia 1 (Core/Maps/StoryClear.cs)

- **Map data:** sections C (characters), G (walking paths) and D (step-on areas) are emptied on every map except
  4F, the title stage, whose character and areas are what start the opening.
- **Setup scripts** ($01:98AF) are rebuilt along the path a new game takes. No story flag is set in a new game,
  so 05/07/09 count as true and 06/08/0A as false ($01:DDE7 sets carry when a flag is clear), and 04 jumps when the
  test is true. Kept: 01-03 and 0B-0D (map, song, palette settings), 21+n arrival points, and C1+n spots whose
  high byte is FE (doors). Left out: 41+n characters, 81+n areas, and the other spots (FC chest, FD hidden item,
  FF trigger, anything else gold).
- **Events:** every map's script table entry (0x18200 + map * 5: u24 block offset from 0x18000, a byte, the
  event base) points at one shared table of 256 FFFF entries ("no event"), base 0. Old blocks in the added space
  are freed.
- **The opening:** map 4F keeps its event base (2) and gets a table whose entry 8 (the opening the game starts
  after "TAITO PRESENTS") is:

  | bytes | what it does |
  |---|---|
  | `19 02 5A` | fade out; the file select and name entry run during this wait |
  | `1B 04` `1B 05` `1B 06` `1B 07` | the prologue's four heroes leave |
  | `1A 00 00` | the Hero joins (a new game's party list $14D2 is otherwise empty) |
  | `06 80` | story flag 80, "prologue done" |
  | `5F 01` | show the leader (the opening hides the party) |
  | `5B map arrival` | warp, the screen staying black |
  | `0A song` | the start map's song (its setup's `02 song`); after 5B the music is silent until a script plays one |
  | `19 01 5A 50 06 18 02 5A` | fade in as the game's own scenes after a warp; also brings the sprites' colours back |

- **Chests stay drawn.** A chest is part of the map's tiles; the setup spot only makes it open. After a clear
  the chest picture is scenery that does nothing; paint it over in the Map Editor if it shouldn't be there.

Checked in BizHawk (US ROM): name entry, then the Hero alone (party list `00 FF FF FF`) on Alekia arrival 1 and on
map 14 arrival 2, in colour, with the town empty; the start map's song is loaded into the sound RAM.
