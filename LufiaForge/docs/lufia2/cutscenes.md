# Lufia II cutscenes: the Cutscenes page

The Lufia II tab's **Cutscenes** page (in the World group, under Events) makes and edits the scenes of a map the way
Lufia 1's Cutscenes tab does: the map is a stage with its characters on it, a cutscene is a timeline of steps in plain
words, and the stage shows where everyone stands after each step. It is built on the event editor's code
(events.md): a cutscene is an ordinary event of the map, saved the same way (in place, or in the event space).

Code: `Core/Lufia2/L2Cutscene.cs` (steps, the stage, new cutscenes, cutscene characters, the test start),
`Modules/Lufia2/L2CutscenesPage.cs` and `L2CutscenesView.xaml` (the page).

---

## 1. Using the page

1. **Pick a map** in the list on the left. The map is drawn in the middle with its characters (the ones its start-up
   script makes, on their character starts) and the leader where a cutscene would start. The dashed box is what the
   screen shows.
2. **Pick a cutscene** of the map (its scenes, talking to its characters, its step-on areas), or make one with
   **New cutscene** (section 3). The steps show on the right, as a timeline.
3. **Click a step** to see the stage after it: who stands where (a gold frame marks who moved in that step), each
   character drawn facing the way it faces (section 5), the camera's box, the screen dark after a fade, the text box
   of a line, and the story flags on at that point.
4. **Add a step** with the buttons above the timeline; it goes below the picked step. Change it in the box under the
   timeline. Many steps take a place from a click on the map:
   * *Appears* and *Camera*: the clicked tile.
   * *Walks*: click a tile in the same row or column as the character: the direction and the number of tiles follow.
   * *Faces*: click a tile in the direction it should face.
5. **Play** runs the cutscene on the stage, roughly at the game's speed (lines stay up long enough to read): it goes
   the way the game would (section 1.1), and walks are drawn tile by tile in the sprites' walking frames (characters
   walking together move at once). The arrows step one step back or on. Up / down move a step, Delete takes it out,
   Undo takes back the last change.
6. **Save** writes the cutscene into the ROM (Edit > Undo takes it back). Revert drops what isn't saved.
7. **Test in the game** (or start the game here): the ROM is written and opened in BizHawk (the bundled one; the window it opened before is closed first). A new game starts on this map with the party on the point picked
   and the story flags ticked under **Playback starts with** turned on. A scene or a step-on area cutscene plays at
   once (put the party on the area: its walks start from there); for a talk cutscene talk to the character. Export the
   ROM and start a new game. Keep it as a hack's own start, or press **Normal start** for the game's opening again.
   The party, gold and items the new game starts with are on the Characters page.

The **Game start** page (World group) sets the same start in one place: a map (one with preset points), the
cutscene that plays (a scene, a stepping-on area or a talk, or none), the point the party stands on (one on a
stepping-on area is picked for it) and the story flags. The flags the cutscene checks are listed and ticked the way
that plays it furthest (each combination is played back, the fewest flags on among the best); more flags for the story
so far go in a box. **Now** says where a new game starts, which flags are on and which cutscene plays. **The game's
opening** puts the game's own start back.

How the start is made (map 02 scene 01 point 06 is the game's own, at `$83:ACCD`):

* What the cutscene's characters need is set too (`L2Cutscene.Needs`): the story flags under which the map's start-up
  script makes a character the cutscene names (Dragon Mountain C7 makes the dragon, actor 50, only while flag 59 is on
  and 5A off), and persistent characters (10-4F) the cutscene expects to stand there already, sent to their place on
  the map with `35 id map spot` (the elder of Chaed, 2D, whom the Tower of Truth's event sends to C7's start 2). One
  the cutscene brings in itself (its first step puts it on a point or tile, makes it appear or walks it in) isn't sent:
  the game brings it (Karlloon North Shrine B1's Iris and Idura). A talk cutscene that doesn't speak as "the one talked
  to" (08) plays at once, like a stepping-on one. Checked in the game: C7's talk with the elder plays from a new game
  through to the Fire Dragon battle; the shrine scene plays as before.

* The ticked flags (`1A ff`) and counters (`1D cc nn`) are set by a scene of its own (F0 up), which then sends the
  party to the map again (`16 map scene point`), so the map loads with them: its characters, its "on entering" event
  and the cutscene's checks all see them. Without them the game's own cutscenes check the story so far and skip
  themselves (Karlloon North Shrine B1's trigger, the shrine where Dekar stays behind, starts "if flag 7E is off or
  flag 41 is on, go to the end").
* The scene that shows the leader and fades in (F0 up, `2D 00 59 18 76`) goes on with a copy of a step-on cutscene's
  steps, so it plays as the game starts. A copy, not a jump: the block's scripts move when it is laid out again.
  Pressing the button again writes both scenes over.
* A starting party of more than one hero: the game fills the battle party (`$0A7A`) from the new-game records but sets
  the walking list (`$0926`, the party's map characters) to Maxim alone at `$83:AE56`; only "joins" (`2B`) fills it.
  Without the others on the map a cutscene step that walks one of them waits forever. Lufia Forge puts a JSL at
  `$83:ACE7` (new game only, after that reset) that copies `$0A7B..7E` to `$0926..0929`, whenever the starting party
  has more than one hero (`L2StartParty`).

Checked in the game with a project that starts with Maxim, Selan, Guy and Dekar: the shrine cutscene plays from a new
game through to Karlloon, with Dekar gone from the party.

Steps the page doesn't edit (checks, jumps and the other commands of events.md) show as they are with "(change it on
the Events page)"; they stay where they are and can be moved or deleted.

### 1.1 Checks and jumps in playback

Playback runs the steps the way the game does, from the first one, and follows checks and jumps:

* **Playback starts with** (above the stage) lists the story flags and counters the cutscene checks, with "the
  cutscene has played" on its plays-once flag. Tick the flags that are on as the cutscene starts (they are kept while
  the page is open, for every cutscene that checks them) and type the counters. Steps turn flags on and off (`1A`,
  `1B`) and set counters (`1D`, `1E`, `1F`) as playback goes, and later checks see that.
* A check or jump step says in blue where playback went and why ("story flag EF is off: goes on to step 2", "story flag
  17 is on: goes to step 9"). Steps playback doesn't reach are greyed out and say so; clicking one shows the stage as
  if the steps were read top to bottom.
* What is followed: `15` / `6A` (if a flag is on / off), `1C` (go to), `14` (checks of story flags and counters, joined
  with or / and; checks of items, HP, MP, spells, levels, facing and beaten monsters can't be known on the stage and
  count as "not so"), `12` (a counter picks the next of several steps, and goes up by one), and questions (`10`: the
  answer picked in "Questions take", answer 1 at first). Playback stops at the end of the event, at a jump out of it,
  and after 3000 commands (a loop).
* Under the stage: the story flags on and the counters at the picked step.

### 1.2 Walks that the game stops

A step of a walk (`A3 who 00-03`) is checked against what the stage knows at that point, and a step the game refuses
stops the character there on the stage (later steps start from where it stopped). The step gets a red warning in the
timeline and in the box under it, and the stage marks the tile it hits with a red box ("Character 14 can't walk up
from (33, 28): a wall is in the way, so the game stops the walk there"). What stops a step:

* the map's collision (the upper layer's block flags, as the Maps page's Collision tool paints them): a block that can't
  be walked on (80), a wall on the top edge (04: no step up out of the block or down into it) or on the left edge (08:
  no step left out of it or right into it), and the edge of the map;
* another character standing on the tile (shown at that step), unless it steps away in the same round (two characters
  walking together in a line);
* the character's walk area: a character standing on the map from the start (a character start, map info +10) can't
  step out of its walk area (bytes 3-6). Cutscene characters (hidden as the map is shown) are left out of this check:
  they walked out of a one-tile area in the game. The page gives new characters the whole map as their area.

The stage doesn't know where wandering characters have gone, or where the party members behind the leader stand.

## 2. The steps and the commands they are made of

| Step | Commands | Notes |
|---|---|---|
| A character appears (on a tile, in a flash) | `2D who` (show), `2F who x y` (put), then `A3 who 86` + `89 who` for the flash | Shown first, then put: see section 5. Without a tile only `2D`. |
| A character leaves (in a flash) | `2E who` / `99 who` | |
| Walks n tiles (with a second character) | n times `A3 a d` (`A3 b d`) `89 a` (`89 b`), d = 00 up, 01 down, 02 left, 03 right | Each tile waits for the step to end. Walls, other characters and the walk area stop a walk: the page warns (section 1.2). The game's "walk through anything" actions (94-97) face the wrong way (Jeros' Story), so they aren't used. |
| Faces | `7A who frame` (frame 04 up, 00 down, 02 left, 06 right) | `31` (turn) showed characters facing down in the game, `7A` works. The stage draws the sprite's own picture for the facing (section 5). |
| Acts | `A3 who action` + `89 who` | A list of actions that end (hop in surprise, collapse, hold up an item, step back, hop a tile, gestures, vanish / appear in a flash...). Actions that keep going (tremble, shake) would never end the wait, so they aren't offered. |
| Says | a text step: `61-67` (a hero), `13 who` (a character), `9E 05` (narration), `08` (the person talked to), then the text, `01` | As in the Events page: Enter = new line, `[Name]`, `[Pause 20]`. |
| Camera to a tile | `C4 x y` + `41 00 FF` (scroll and wait), or `3A x y` at once | The camera stays there until "camera back". |
| Camera back to the leader | `41 0n 00` (n = speed) | Waits for the camera. |
| Fade out / in | `58 v` / `59 v` + `76` | v = 13 is smooth. |
| Music | `6F song` | For a scene: the map's own music comes back on the next map. |
| Sound | `4C sound` | Sound 24 goes on forever (Jeros' Story). |
| Wait | `37 frames` | |
| Shake / stop shaking | `5A 00 strength often` / `8A` | |
| Story flag on / off | `1A flag` / `1B flag` | |
| Go to another map | `16 map scene point` | The scene must exist on that map (make it there with "When another cutscene sends the party here"). |
| End | `00` | |

The page reads the game's own scripts the same way: any run of commands that matches a row is shown as that step
(for example the game's `2F` + `2D`, put then show, is "appears"). A step another step jumps to always starts a new
step, so jumps keep working when a step is changed (jumps to a changed step go to its first command).

## 3. How a cutscene starts (New cutscene)

| Choice | What is written |
|---|---|
| **When the party enters the map** (a story flag can decide) | A scene of the map (event list +4, a free number from 20 up) that shows the leader and fades in, and at the top of the map's "on entering" event (+2 id 01): `15 played -> rest` (skip once played), optionally `6A flag -> rest` / `15 flag -> rest` (only while the story flag is on / off), `1A played`, `16 map scene point`. The map is loaded again with the scene: the party stands on the point picked. |
| **When the party steps onto an area** | A trigger area (map info +0C) and its event (+8), as the Events page's "Stepping onto an area". The area's first tile comes from a click on the map. |
| **When the party talks to a character** | A talk event (+6) for one of the map's characters (one that has no talk event yet), or for **a new character on the clicked tile** (the first choice; its sprite is the one picked under Cutscene characters): `68 id sprite` in the start-up script and a character start of its own (standing still, the whole map as its walk area), not hidden, so it stands there from the start. The stage puts the leader below the character. |
| **When another cutscene sends the party here** | A scene of the map (+4) that shows the leader and fades in: "Go to another map" with its number starts it, and so does a new game (Test in the game). |

"Plays only once" (for areas and talking) checks a story flag no event and no game code uses first ("if it is on, go to
the end") and turns it on; "when the party enters the map" always plays once (its flag is turned on before the map is
loaded again). A new cutscene has one line ("...") to start from.

## 4. Cutscene characters

**Add the character** (with a sprite from the list) makes a character only cutscenes use:

* `68 id sprite` at the top of the map's start-up script, with a character start of its own (map info +10: on the
  clicked tile, standing still, the whole map as its walk area). For a cutscene that plays once (an area or talking, with its story flag) the create is
  skipped once the flag is on (`15 flag -> past it`), so after the cutscene has played the character is gone the next
  time the map loads.
* `2E id` (hide) at the top of the map's "when shown" event (+2 id 00), so it stays hidden until an "Appears" step.

Characters of the map (the game's own) can be used in steps too, and so can the heroes (the leader is always there;
the others only while in the party).

## 5. Things found in the game

* **Commands that find a character skip hidden ones.** `2F`, `A3` and most character commands find the actor with
  `$80:BF6F`, which passes over actors whose `$0622` has bit 04 (hidden); when nothing is found the command uses
  whatever slot `$A7` last held, so "put" on a hidden character moved the leader. That's why "appears" shows the
  character first and then puts it (both run in the same frame: no flicker).
* **No character commands in the start-up script after the creates.** The finding routines (`$80:BF6F` and `$80:BF92`)
  leave the slot they found in `$A7`, and `$A7` is also the counter `68` makes characters with: the story characters
  the game adds after the start-up script then took the slots of two characters already made (they were gone). The
  cutscene characters are hidden in the "when shown" event instead.
* **A character's walk area holds its steps.** A character made by `68` with a character start whose walk area was its
  own tile (x, y to x + 1, y + 1, as the page used to write it), standing on the map from the start, did the "hop in
  surprise" action and turned (`7A`) in a talk cutscene, but its steps (`A3 who 00-03`) did nothing (the cutscene went
  on at once); with the whole map as its area it walked. Hiding and showing it, or putting it on its tile (`2F`),
  before the steps didn't help. Cutscene characters hidden as the map is shown walked out of a one-tile area (the
  first test on Elcid). The game's own talk events move the person talked to with paths (`33`), not `A3`.
* **Sprite frames.** Map sprites with six frames (sprite flag 10) hold two each for facing down (0, 1), left (2, 3) and
  up (4, 5); right is left mirrored. The second of each pair is a walking step. Sprites with two frames (flag 08) only
  step in place; others have one picture. The stage draws these; sprites without facings get a small arrow.
* A character made by `68` with no character start of its own worked in the Shrine of Daos (Jeros' Story); the page
  gives each one a start anyway.
* `16` in the map's "on entering" event loads the map again and runs the scene (checked: the party stood on the point,
  the cast showed, the flag was set).

## 6. Checked in the game (BizHawk, BSNES core)

* Elcid (map 03), through the page itself (the real window, run off-screen): a cutscene started by stepping onto the
  tile left of point 4, once, with two cutscene characters (sprites 02 and 03). A new game started there (Test in the
  game), the player stepped left: the first character appeared in a flash, walked 2 tiles up, spoke; Maxim faced down
  and answered; the character hopped; the camera scrolled to (25, 22), waited and came back; the second character
  appeared, walked 2 tiles left and spoke two lines; the music changed; both walked 2 tiles down together and vanished
  in a flash; story flag EF was on.
* The same steps as a scene started by a new game, and as "when the party enters the map" (through the test start's
  own scene, then `16` to the cutscene): both played, flags set.
* Elcid, through the page (off-screen): "When the party talks to a character", a new character (sprite 0C) on (31, 33),
  plays once. Steps: a line, hop in surprise, walk 2 tiles right, face up, a line, face right, walk 8 tiles up (the
  page warned that a wall stops it at (33, 28)). In the game (a new game on point 4) the player walked to (31, 32),
  faced down and pressed A: the lines showed, the character hopped, walked right, turned, and walked up until the
  shop's wall stopped it on (33, 28), where the page said; the cutscene ended and the player could move. Story flag EF
  was on; talking to the character again (from (33, 29)) showed nothing, and the player walked away.
* Before the walk area fix the same character didn't walk at all (section 5).

## 7. Not done yet

* Paths (`33`) aren't shown moving on the stage.
* Story characters (10-4F) other than those a map creates aren't placed on the stage until a step puts them.
* Walk checks don't know where wandering characters have moved or where the party members behind the leader stand, and
  the "walk through anything" and hop actions aren't checked.
* Checks of items, HP, MP, spells, levels and beaten monsters count as "not so" in playback.
