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
3. **Click a step** to see the stage after it: who stands where (a gold frame marks who moved in that step, a small
   arrow where they face), the camera's box, the screen dark after a fade, and the text box of a line.
4. **Add a step** with the buttons above the timeline; it goes below the picked step. Change it in the box under the
   timeline. Many steps take a place from a click on the map:
   * *Appears* and *Camera*: the clicked tile.
   * *Walks*: click a tile in the same row or column as the character: the direction and the number of tiles follow.
   * *Faces*: click a tile in the direction it should face.
5. **Play** runs the cutscene on the stage, roughly at the game's speed (lines stay up long enough to read). The arrows
   step one step back or on. Up / down move a step, Delete takes it out, Undo takes back the last change.
6. **Save** writes the cutscene into the ROM (Edit > Undo takes it back). Revert drops what isn't saved.
7. **Test in the game**: a new game starts on this map with the party on the point picked. A scene plays at once;
   for a step-on area walk onto it, for a talk cutscene talk to the character. Export the ROM and start a new game.
   **Normal start** puts the game's own opening back (do this before sharing the game).

Steps the page doesn't edit (checks, jumps and the other commands of events.md) show as they are with "(change it on
the Events page)"; they stay where they are and can be moved or deleted. Playback doesn't follow jumps: it reads the
steps top to bottom.

## 2. The steps and the commands they are made of

| Step | Commands | Notes |
|---|---|---|
| A character appears (on a tile, in a flash) | `2D who` (show), `2F who x y` (put), then `A3 who 86` + `89 who` for the flash | Shown first, then put: see section 5. Without a tile only `2D`. |
| A character leaves (in a flash) | `2E who` / `99 who` | |
| Walks n tiles (with a second character) | n times `A3 a d` (`A3 b d`) `89 a` (`89 b`), d = 00 up, 01 down, 02 left, 03 right | Each tile waits for the step to end. Walls and other characters stop a walk: keep the way clear. The game's "walk through anything" actions (94-97) face the wrong way (Jeros' Story), so they aren't used. |
| Faces | `7A who frame` (frame 04 up, 00 down, 02 left, 06 right) | `31` (turn) showed characters facing down in the game, `7A` works. |
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
| **When the party talks to a character** | A talk event (+6) for one of the map's characters (one that has no talk event yet). |
| **When another cutscene sends the party here** | A scene of the map (+4) that shows the leader and fades in: "Go to another map" with its number starts it, and so does a new game (Test in the game). |

"Plays only once" (for areas and talking) checks a story flag no event and no game code uses first ("if it is on, go to
the end") and turns it on; "when the party enters the map" always plays once (its flag is turned on before the map is
loaded again). A new cutscene has one line ("...") to start from.

## 4. Cutscene characters

**Add the character** (with a sprite from the list) makes a character only cutscenes use:

* `68 id sprite` at the top of the map's start-up script, with a character start of its own (map info +10: on the
  clicked tile, standing still). For a cutscene that plays once (an area or talking, with its story flag) the create is
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

## 7. Not done yet

* Paths (`33`) aren't shown moving on the stage, and playback doesn't follow jumps or checks.
* Story characters (10-4F) other than those a map creates aren't placed on the stage until a step puts them.
* Sprites are drawn facing the screen (frame 0) with an arrow for the facing.
* The page doesn't check that a walk's way is clear in the game.
