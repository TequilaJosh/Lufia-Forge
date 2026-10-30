# Lufia II (US) events: the script commands, the event blocks, and how Lufia Forge edits them

ROM: the US Lufia II (2.5 MB LoROM FastROM). Everything here was read from the game's code and its 2962 map scripts
(all 242 maps parse with 0 errors); the things marked **game** were also tried in the running game (BizHawk, see
section 10). The editor's own copy of this knowledge is `Core/Lufia2/L2EventCommands.cs` (every command in plain words
with its fields), `L2Conditions.cs` (command 14), `L2EventScript.cs` (scripts as steps), `L2EventBlocks.cs` (saving,
new events) and `L2EventSpace.cs` (where moved scripts go).

How sure each meaning is: **game** = tried in the game, **code** = clear from the handler's code, **data** = from how
the game's own scripts use it, **guess** = not confirmed.

---

## 1. Interpreter core

* Script pointer: bank `$09B9`, address `$09B7` (16-bit Y, data bank B = `$09B9`). Runs once per frame from
  `$80:9CCD`: B = `$09B9`, Y = `$09B7`, then the command loop at `$80:9D00`, which first does `STY $09B7`.
* Byte reader `$80:C0B7`: `LDA $0000,Y; INY`; if Y wraps to 0 it increments `$09B9`/B and sets Y = `$8000`
  (so a script may run across a LoROM bank boundary). `$80:C0D0` = two C0B7 calls = **u16 little-endian**.
* Dispatcher `$80:9D32`: `JSR $C0B7; ASL; TAX; JMP ($CA14,X)`. Table `$80:CA14` (file 0x4A14) has **0xCD
  entries (commands 00..CC)**; CD..FF are not commands. Crash handlers (a `BRK`): 02, 11, 5C, 5D.
* Handler exits: `BRL $9D00` = continue with the next command in the same frame. `BRL $9DB0` = stop for this frame
  without saving Y, so the same command runs again next frame (all the "wait until ..." commands).
* **The handlers reach RAM through the data bank** (for example 1A `STA $077E,X`, 2B `LDA $0926,X`), and the data bank
  is the script's own bank. That only works in banks $80-$BF (and $00-$3F), where low RAM is mirrored: **a script
  must sit below file offset 0x200000**. The added space past 2.5 MB ($D0 and up) can't hold scripts (section 8).
* Commands run back to back in one frame until one waits; text prints one letter per frame.

## 2. Text

* `$099B` bit 0 = text mode. Opened by **08** (the one talked to), **13 xx** (character xx), **61-67** (a hero),
  **9E xx** (narration box at row xx, centred) and **10** (a question).
* In text mode: byte < 0x10 = a code (dispatched through the same table), 0x10-0x7F = a letter, 0x80-0xFF =
  dictionary word `(b - 0x80) + 0x200` (dictionary `$8E:EA00`, 0x280 words).
* Codes: `00` end of text and of the event; `01` wait for a button, then back to commands; `0B` the question cursor
  (back to commands); `03` new line; `0C 0D 0E 0F` = `?` `!` `,` `.` and a new line; `04 xx` xx < 80: the letters'
  sound (sound 0x45 + xx), xx >= 80: pause (xx & 7F) * 10 frames (04 80 is a pause of nothing); `05 xx` / `06 xx`
  dictionary word xx / 0x100 + xx; `07 xx` letter 0x300 + xx; `09` the hero's name; `0A lo hi` repeat (hi >> 4) + 2
  letters of the text d = lo + (hi & 0F) * 256 bytes back.
* Speakers: 13 xx = character xx; **61-67 = Maxim, Selan, Guy, Artea, Dekar, Tia, Lexis** (the code maps them to actors
  0, 1, 2, 3, 5, 4, 6; hero 4 is Tia, 5 Dekar: data, Tia speaks with 66 in Alunze Castle, Dekar with 65).
* In the editor the text is plain text: Enter = new line, `[Name]` = the hero's name, `[Pause 20]` = 20 frames,
  `[Voice 3]`, `[Glyph 41]`. Words of the dictionary and repeats are written out; changed text is packed with the
  dictionary again. A box shows **4 lines of 28 letters** (28 measured by Artemis; checked in BizHawk 2026-10-30: a
  36-letter line spills into the lines under it and garbles them, and the 5th and 6th lines of a 6-line text came out
  as copies of the 1st and 2nd). Wider or longer text gets a soft warning on its card and on the Text page; it is still
  written as typed. More than 4 lines needs another text step.
* **The Text page writes any length.** A line that fits its place is written there; a longer one is written the way
  the event editor writes: every event of the map that shows the line gets the new text and is laid out again in the
  map's added part, the block moving to the event space the first time (`L2Events.WriteText`,
  `L2EventBlocks.SaveAll`). Checked in BizHawk: the new game's first line made six lines long plays in Tia's shop and
  the scene goes on as before; every other line of every map reads back unchanged.

## 3. Jumps

* Every script pointer (1C, 15, 6A, 12, 10 answers, 14 checks) is a **u16 offset added to `$099E` in bank
  `$09A0`**, then `$80:C102` moves it into the next bank when the 16-bit sum wrapped. For event-block scripts the base
  is the map's **"PH" block** (`$83:B5AD`). A target reaches at most into the bank after the PH's; a jump from that next
  bank back into the PH's bank is broken (the data bank isn't reloaded), so Lufia Forge keeps a moved block inside one
  bank.
* The **start-up script** (map header second u24, `$80:BEAF`) runs with `$099E` = $8000: its jumps are offsets from the
  start of the bank it runs in (bank $87 for the game's own, file 0x38000).
* Ends: 00 / 42 (end), B8 (message left up), 84 (stop forever), 16 / 5E / 83 (map change), A5 (game over),
  5B 03 (reset), 5B 06 (hang). 18, 19, 81 and BC end the event too (`$80:A368`).

## 4. State

* **Story flags**: 256 bits at `$7E:077E-$079D`; flag n = byte `$077E + (n >> 3)`, bit `1 << (n & 7)`.
  Flags the game's own code uses (the editor never hands them out as free):

  | Flags | Used by |
  |---|---|
  | F0-FF | cleared on every map change that loads a map ($83:B2A8) |
  | FF | the answer of 91 / 92 / AB, and "the party did not win" after a story battle (53) |
  | FE | set after some story battles |
  | C8-D0 | Iris treasures taken out of the Ancient Cave |
  | 95 | where the ship can be parked |
  | 41, 50, 5F, 62 | change the world map when set |

* **Counters** (event variables): bytes at `$7E:079E + n` (1D/1E/1F, 12, checks 1x of 14). New game zeroes 00-1F.
* Gold u24 `$0A8A`; inventory `$0A8D`; hero records `$0BAD + n * 0xBE`; walking party `$0926`.
* **Actors**: 0x28 slots; id `$05FA,X`, flags `$0622,X` (01 held by the event, 02 walks through things, 04 hidden,
  08 walking a path, 80 an action runs), sprite `$05D2,X`, tile `$06BA/$06E2,X`, sprite frame `$066A`, facing `$0692`
  (0 down, 2 left, 4 up, 6 right), drawing `$7FE316` (80 = not drawn).
  Ids: **0-6 the heroes** (Maxim, Selan, Guy, Artea, Tia, Dekar, Lexis), **10-4F story characters** (they keep a map,
  a start and a hidden bit across maps: `$085E`, `$089E`, `$081E` + id - 0x10; in Elcid 10 is Tia), **50 + n map
  character n** (made by 68 / 7B in the start-up script and placed by character start n + 1).

## 5. Event blocks and map info

**The event block** ("PH", the map header's first u24: bank $87 + hi, address lo + $8000): "PH", then six u16 offsets
(+2..+C) to lists of (u8 id, u16 script offset) ended by FF. The lists are searched by `$80:C12E` (A = id, X = list).

| List | Started by | Ids |
|---|---|---|
| +2 | the game | 00 when the map is shown (`$83:B40A`, also the world maps); 01 on entering the map or one of its rooms (`$83:B420`, `$8E:B29D`); 02 before the map loads (`$83:B28D`, may change the arrival: 83, 85); **03 is not a script**: the locked-door key table (door + 1, u16 key item; ends with 00) |
| +4 | "Go to another map" (16) with that scene number, and puzzle op BE | the scene number (`$05B8`); the game starts on map 02 scene 01 |
| +6 | talking to a character | the character's actor id |
| +8 | the leader stepping onto a tile of a trigger area | the area's id (map info list +0C) |
| +A, +C | nothing | always empty |

The +2 scripts and the start-up script run in one go: anything that waits (text, 37...) ends them. A trigger area fires
**every time the leader finishes a step onto one of its tiles** (`$05B5` bit 4, `$83:B711`), never on loading the map,
and the first area of the list that contains the leader wins: use a story flag for "only once". The game never checks
that a looked-up script exists.

**Map info** (pointer table `$CF:FCBC`, u24 per map; copied to `$7E:F000` with its first u16 as the size): u16 size,
then 21 u16 list offsets. Any bank works (the copy uses MVN with the pointer's bank), but the block must stay inside one
bank and under 4 KB (`$7E:F000-$FFFF`; the biggest in the game is 0x5EB).

| List | Records | Bytes |
|---|---|---|
| +02 | doors | 15: id, x, y, ..., +0D key / lock |
| +06 | exits | 9: id, x1, y1, x2, y2, arrival (facing / transition), x, y, map |
| +0C | trigger areas | 5: id, x1, y1, x2, y2 (x1 <= x < x2, y1 <= y < y2) |
| +0E | preset points | 3: id, x, y (30, 3B, 41 7n, 73, 98, 16's arrival point) |
| +10 | character starts | 8: n (actor 0x4F + n), x, y, walk area x1, y1, x2, y2 (x2 / y2 not included), movement (32's values; 0 = the sprite's default) |
| +12 | paths | 6: id, x, y (copy of the first target), flags (40 clear: the camera follows; 10: the event doesn't wait; 20: walk in the background), u16 program offset |
| +14 | areas | 5: id, x1, y1, x2, y2 (43, 87, B4, 9C) |

**Making a map character**: the start-up script's `68 id sprite` (or `7B` for a field monster) fills the next actor
slot from 8 on (at most 32), and the character start with n = id - 0x4F gives its place, walk area and movement. 68
must come before any other command that uses characters in the start-up script.

## 6. Command 14: check conditions

**14 c ... FF** (`$80:A074`): checks in order; each sets the result R (story flag checks can combine with it); jump checks
go somewhere on R; when no jump is taken the event carries on after the FF.

| c | Bytes after | Meaning (op = c & 7: 0 is, 1 is not, 2 at least, 3 at most, 4 more than, 5-7 less than) |
|---|---|---|
| 00-0F, 40-4F, 80-8F | flag | story flag (bit 0 of c = 0: on, 1: off); 0x replaces R, 4x R or it, 8x R and it |
| 1x (5x, 9x) | counter, value | counter op value |
| 2x (6x, Ax) | u16 | if R: go to |
| 3x (7x, Bx) | u16 | if not R: go to |
| C0-C7 | u16 item, u16 n | number of the item op n |
| C8-CF / D0-D7 | hero, u16 n | the hero's HP / MP op n (hero 0 Maxim .. 6 Lexis) |
| D8-DF | 3 | broken, unused |
| E0-E7 | hero, spell | **the hero knows the spell** |
| E8-EF | hero, n | the hero's level op n |
| F0-F7 | (ignored), v | the party came from the world map facing v (0 down, 2 left, 4 up, 6 right) |
| F8-FE | actor, (ignored) | **the field monster was beaten** (`$0736` bit 20); never sets R false |

The editor shows it as "Check whether story flag 1A is off: if not, go to step 24", with one row of fields per check
and "add a check" / "take this check out".

## 7. The commands

Fields are what the editor shows; "rare" commands sit in the "Rarely used" menu.

| Cmd | Bytes after | In the editor | Fields | What it does | Sure |
|---|---|---|---|---|---|
| 00 | 0 | End the event |  | Closes any text box, brings the camera back and ends. | game |
| 01 | 0 | (text code) | | wait for a button, then back to commands | code |
| 02 | - | Crash (rare) |  | Not a real command: the game stops here. | code |
| 03-07, 09-0F | | (text codes) | | section 2 | code |
| 08 | 0 | Text: the person talked to speaks |  | Opens the text box for the character the player talked to. | game |
| 10 | 1+2n | Ask a question | answers, answer n goes to | Shows the text with a cursor on the answers (the last lines of the text, one per answer); each answer goes to its step. B closes the box and the event carries on with the next step. | code |
| 11 | - | Crash (rare) |  | | code |
| 12 | 2+2n | Say something different each time | counter, choices, time n goes to | Goes to the first step the first time, the second the next time, and so on (the last repeats); the counter is an event variable. | code |
| 13 | 1 | Text: a character speaks | speaker | Opens the text box for this character. | code |
| 14 | list to FF | Check conditions | per check | Section 6. | code |
| 15 | 3 | If a story flag is on, go to a step | story flag, go to |  | game |
| 16 | 3 | Go to another map | map, scene, arrival point | The scene (an event of that map's +4 list) runs on arrival; it must exist. The party faces down. Not for the world maps. | data |
| 17 | 1 | Rest at the inn | price | Music, fade, everyone alive is healed, then the gold is paid (nothing when the party has less). Waits until the rest is over. | code |
| 18 | 1 | Open a shop | shop | The event ends when the shop closes. | game |
| 19 | 0 | Open the church |  | Save, cure and so on; the event ends there. | code |
| 1A | 1 | Turn a story flag on | story flag |  | game |
| 1B | 1 | Turn a story flag off | story flag |  | code |
| 1C | 2 | Go to a step | go to |  | game |
| 1D / 1E / 1F | 2 | Set / add to / take from a counter | counter, value | 8-bit, wraps; the game uses counters 01-0E. | code |
| 20 / 21 | 2 | Give an item | item, how many | 21 = items 100-1FF. Added one at a time (a full bag shows its screen). No message by itself. Item 2B (Dragon egg) also counts in `$0B61`. | code |
| 22 | 2 | Give gold | gold | Capped at 9,999,999. | code |
| 23 | 2 | A hero learns a spell | hero, spell | Only when the spell can be learned by that hero; no message. | code |
| 24 / 25 | 2 | Take an item | item, how many | Only if the party has it; a key item is switched (taking one the party doesn't have gives it). | code |
| 26 | 2 | Take gold | gold | Nothing is taken when the party has less. | code |
| 27, 2A, 39, 4F, 5F | 2, 3, 0, 1, 1 | Nothing (rare) |  | Do nothing. | code |
| 28 | 0 | Fully restore the party |  | HP, MP and status of everyone (revives); no fade, unlike the inn. | code |
| 29 | 3 | Raise a hero's stat | hero, stat, amount | Permanent bonus of base x amount / 256 (stats HP MP STR AGL INT GUT MGR). | code |
| 2B | 1 | A hero joins the party | hero | Follow it with "Line up the party". | code |
| 2C | 1 | A hero leaves the party | hero | The last member never leaves. | code |
| 2D / 2E | 1 | Show / hide a character | who | Story characters keep the state. Nothing happens when the character isn't on the map. | code |
| 2F | 3 | Put a character on a tile (rare) | who, x, y | At once; holds it still. | code |
| 30 | 2 | Put a character on a point | who, point | At once; holds it still. | code |
| 31 | 2 | A character turns | who, to face | 1 up, 2 down, 3 left, 4 right (0 is garbage). | code |
| 32 | 2 | Let a character move on its own | who (FF everyone), moves | Movement modes below. | code |
| 33 | 2 | A character walks a path | path, who | Note the order: path first. The event waits unless the path's flag 10 is set. | code |
| 34 | 2 | Change a character's sprite | who, sprite |  | code |
| 35 | 3 | Send a story character to another map | story character, map, start | From now on it stands on that map at that character start; hidden here at once. | code |
| 36 | 1 | Wait for a character to stop walking (rare) | who | | code |
| 37 / 38 | 1 | Wait (frames / seconds) | frames / seconds | 60 frames = 1 second. | code |
| 3A / 3B | 2 / 1 | Camera jumps to a tile / point | x, y / point | At once (the map is drawn again). | code |
| 3C | 0 | The party gathers on the leader |  | Waits until they are there. | code |
| 3D | 1 | Line up the party |  | Rebuilds the party's sprites behind the leader (after joins and leaves); the byte is unused. | data |
| 3E / 3F / 40 | 0 | Screen off / on / draw the map again (rare) | | | code |
| 41 | 2 or 3 | Camera | form, then its values | Forms below. | code |
| 42 | 0 | End the event (rare) | | Same as 00. | code |
| 43 | 2 | Copy map tiles from one area to another | onto area, from area | Both layers; updates where the party can walk. | code |
| 44 / 45 / 46 | 3 | Add to / take from / set a palette colour (rare) | colour, BGR15 value | `$0320` is the palette buffer. | code |
| 47 | 1 | Text box style | style | 00 normal, 10 thought box (bubble tail, no voice). Stays until changed. | game |
| 48 | 2 | Text box position | column, row | For the next box only; also turns its voice off. | game |
| 49 | 1 | Text turns pages by itself | frames | 0 = wait for a button again. | code |
| 4A | 1 | Text background (rare) | background | 01 = no box behind the letters (the game's opening). | game |
| 4B | 1 | Set the map's music | song (00-3E) | Nothing changes when it already plays; it comes back after battles. | code |
| 4C | 1 | Play a sound | sound (00-8E) | Skipped while sounds are off (AE). | code |
| 4D | 1 | Wait for a music cue (rare) | cue | | code |
| 4E | 0 | Wait for the sound to end (rare) | | | guess |
| 50 / 51 / 52 | 0 / 0 / 1 | Text voice off / from the speaker / a sound (rare) | | | code |
| 53 | 1 | Start a story battle | battle (00-26) | Afterwards flag FF is on when the party did not win; losing is game over except battles 0B and 25. | game |
| 54 / 55 | 1 | Open / close a door | door | | data |
| 56 | 0 | Stop all screen effects |  | Shaking, fades, colours and the camera hold. | code |
| 57 | 0 | Wait for every screen effect (rare) |  | Never ends during a quake or while the event holds the camera. | code |
| 58 / 59 | 1 | Fade the screen out / in | step (high nibble), frames per step (low) | Fade in: use step 1, 3, 5 or 15. Follow with 76. | code |
| 5A | 3 | Shake the screen | (unused), strength, how often (of 255) | Until 8A or 56. | game |
| 5B | 1 or 2 | Special scene (rare) | what, value | 00 y: darken (y > 0) / brighten the screen bars; 01 00: **the title sequence** (game); 02 y: cinema bars on / off; 03: reset; 04 y: ferry crossing (02 Aleyn to Narcysus, 03 back, 04 Aleyn to Gruberik, 05 back, 06 Narcysus to Gruberik, 07 back); 05: back from the staff roll (game, by an earlier test); 06: hang; 07 / 08: big / normal font. | code |
| 5E | 1 | Leave through an exit | exit | As if the party walked through it. | code |
| 60 | 2 | Write a screen register (rare) | register, value | | code |
| 61-67 | 0 | Text: a hero speaks | hero | Maxim, Selan, Guy, Artea, Dekar, Tia, Lexis. | game |
| 68 | 2 | Add a map character (start-up script) | who, sprite | Section 5. | game |
| 69 | 1 | Map settings | settings | Bits: 01 dungeon controls, 02 special palette, 04 unknown, 08 no Escape, 10 no Warp, 20 can step down slopes, 40 no field tools, 80 no Reset spell. Towns 08 / 98, dungeons 01 / 11. | code |
| 6A | 3 | If a story flag is off, go to a step | story flag, go to |  | code |
| 6B / 88 | 0 | Update where the party can walk |  | After changing map tiles. | code |
| 6C | 3 | Draw a character shifted | who, x, y (signed pixels) | Only the picture moves. | code |
| 6D / 6E | 2 + text + 00 | Credits text (rare) | | u16 screen offset, then letters. | code |
| 6F | 1 | Play a song now | song | For a scene; the map's music comes back later. | code |
| 70 / 71 / 72 | 0 | Fade the music out / wait for it / stop the music | | | code |
| 73 | 2 | Play an animation on a point | animation (00-5F), point | | code |
| 74 | 1 | Battle background (rare) | background | Start-up script. | code |
| 75 | 0 | Camera points at the leader | | | code |
| 76 | 0 | Wait for the fade | | | code |
| 77 / BE | 1 | Hold a character still | who | | code |
| 78 | 0 | Everyone stands still | | 32 FF lets them move again. | data |
| 79 | 0 | Wait for everyone to stop walking | | | code |
| 7A | 2 | Show a sprite frame (rare) | who, frame | 00/01 down, 02/03 left, 04/05 up, 06/07 right; poses from 08. | game |
| 7B | 2 | Add a field monster (start-up script) | who, sprite | Also makes it a field monster (formation from `$97:B9AC`). | code |
| 7C-80 | 1 | Ship: abilities, parking point, state, party aboard, facing (rare) | | 7C 0 sail, 1 dive, 2 fly (guess); 7E FF none, 0 sea, 1 under the sea, 2 air. | code / guess |
| 81 | 1 | A capsule monster joins | capsule monster | 0 Jelze, 1 Flash, 2 Gusto, 3 Zeppy, 4 Darbi, 5 Sully, 6 Blaze. Ends the event. | game |
| 82 | 0 | Refresh around the hero (rare) | | | guess |
| 83 / 85 | 1 / 2 | Send the player elsewhere / choose the arrival (rare) | exit / point, facing | Only in the +2 id 02 event. | code |
| 84 | 0 | Stop here forever (rare) | | | code |
| 86 | 2 | A character acts toward the leader's facing | who, action group | The group's up / down / left / right action by the leader's facing. | code |
| 87 | 2 | Copy map tiles (quick, for animations) | onto area, from area | Follow with 6B / 88. | data |
| 89 | 1 | Wait for the action | who | Then holds the character still. | code |
| 8A | 0 | Stop the shaking | | | game |
| 8B / C0 | 3 | Nudge a character (and settle it on the tile) | who, x, y (signed pixels) | | code |
| 8C | 3 | Action settings | who, first, second | Before "hold up an item" (86 xx 40). | data |
| 8D, A0, C2, C3, C8, C9 | 6 / 7 | Ring of sparkles around a point / character (rare) | ring, place, count, size, spin, other | | data |
| 8E, 8F, 90, 9F, 9A, A1, A2 | | Change, tilt, remove, wait for a ring (rare) | | | code |
| 91 | 2 | Check casino coins | coins | Answer in flag FF. | data |
| 92 | 1 | Check whether a door is open | door | Answer in flag FF. | code |
| 93, 9B | 3, 5 | Colour fades (rare) | | | code |
| 94 / 95 / 96 | 3 | Darken / flash / darken half with a colour | colour, start, end strength, frames | Colour bits E0 white, 20 red, 40 green, 80 blue... 97 ends it. | code |
| 97 | 0 | End the colour | | | code |
| 98 / 99 / 9D | 2 / 1 / 1 | A character appears in a flash / vanishes / flickers away | who (, point) | | code |
| 9C | 3 | Scatter animations over an area | area, count, animation | | code |
| 9E | 1 | Text: narration | row | Centred box; voice off. | code |
| A3 | 2 | A character does an action | who, action | Actions below; follow with 89. | code |
| A4 | 0 | Fade in quickly | | | code |
| A5 | 0 | Game over (rare) | | Back to the title screen. | code |
| A6 | 0 | Place the cave floor's monsters (rare) | | | guess |
| A7 | 1 | Followers stop / resume (rare) | | | code |
| A8 | 1 | Enter / leave the Ancient Cave (rare) | | | data |
| A9 | 2 | Where Escape leads (rare) | map, point | | data |
| AA | 0 | Clear the puzzle movement flags (rare) | | | code |
| AB | 1 | Check Dragon eggs | eggs | Answer in flag FF. | code |
| AC | 2 | Where the party appears when the map reloads (rare) | point, facing | | code |
| AD | 0 | Rebuild a treasure list (rare) | | | guess |
| AE | 1 | Sounds on (FF) or off | | | code |
| AF / B0 | 2 / 1 | A character blinks / stops blinking | who, rate | | code |
| B1 / B2 / B3 | 2 / 2 / 1 | Face another / face each other / the party looks at (rare) | | | code |
| B4 | 3 | Copy map tiles on chosen layers (rare) | | | code |
| B5, B7, BD | | Story character 4F, bring back a field monster, effect character (rare) | | | guess |
| B6 | 2 | Character drawing | who, setting | 20 normal, A0 not drawn (still blocks the way). | game |
| B8 | 0 | Next message stays up while the player walks on | | Signs. | code |
| B9 | 1 | Fill a hero's IP gauge | hero | | code |
| BA | 1 | Take off a hero's equipment | hero | When a hero leaves for good. | data |
| BB | 0 | Show the report screen (rare) | | "LUFIA 2 REPORT" (defeats...), then the event ends. | game |
| BC | 0 | Draw the map again and end (rare) | | | code |
| BF | 3 | A character rides along with another | action, carried by, rider | | code |
| C1 | 2 | Effect settings (rare) | | Unknown; always before a ring. | guess |
| C4 | 2 | Camera scrolls to a tile | x, y | | code |
| C5 | 0 | Keep the colour as it is (rare) | | | guess |
| C6 | 2 | Play an animation on a character | animation, who | | code |
| C7 | 0 | Draw the map again while dark (rare) | | | data |
| CA | 3 | Layer scrolling (rare) | layer, mode, setting | | code |
| CB | 1 | Message speed (rare) | | | data |
| CC | 0 | Remember the last story battle (rare) | | | guess |

**Camera (41)**: `xx FF` wait until the camera stops; `0n 00` back to the leader at speed n, waits; `dn tt` move tt
tiles (d: 1 up, 2 down, 3 left, 4 right) at speed n; `6n x y` scroll so tile (x + 1, y) is the top left; `7n pp` scroll
to preset point pp. Speed n = pixels a frame, 0 = at once. Moves don't wait.

**Movement modes (32, character start byte +7)**: 00 wander (inside the walk area), 02 wander more often, 04 stand
still, 05 / 06 / 07 / 08 stand facing down / up / right / left (turn back after talking), 09 stand keeping the facing,
0A follow the party. 0B-39 are monster and special behaviours (chase, vanish, walk squares...); 01, 03 and 3A+ are
broken.

**Actions (A3, 86, BF; table `$91:8000`)**: groups of four go up, down, left, right: 00-03 step one tile, 04-07 walk in
place, 18-1B face, 40-43 hold up an item, 64 hop in surprise, 68-6B hop a tile, 79-7C collapse, 81-84 step back, 85 / 86
vanish / appear in a flash, 94-97 walk through anything... The editor lists 116 of them by name
(`L2EventCommands.Actions`).

**Paths (33; map info +12)**: a small program from the block: 00-03 step up / down / left / right, 08 d face, 0C s
speed, 12 x y walk to the tile, 18 x y jump to it, 1B n wait, 1C s sprite, 1D n sound, 29 offset loop, 4A-4D step
back, 4F a action, 0F end.

## 8. How Lufia Forge saves events

The Events tab shows a map's events (the start-up script, scenes, talking to each character, the trigger areas, the
+2 events) as steps in plain words, in the same editor the Cutscenes page uses and the Maps page pops up (cutscenes.md
section 1): each step is a card edited in place (text with its speaker, a command with its fields), the map is a stage
that shows where everyone stands after the picked step and takes walks, appearances and turns from clicks, and a
timeline places the steps in time. Every step reachable from the event's start is shown, in ROM order, with its
jumps as "go to step N"; code another event shares is marked "shared with ...". The edits are written when the event
is left (or with Save).

1. **Same size: written over the old bytes.** When every step keeps its place and size (text may get shorter: it is
   filled up with `04 80` pauses of nothing and a space) and nothing another event shares changes, only the changed
   bytes are written.
2. **Otherwise the block moves into the event space**, once per map. The event space is a bank of $A8-$BF taken from
   the packed files: the packed map files of that bank (the map editor moves those files too) are copied as they are to
   the space added past 2.5 MB, the file table points at the copies, and the bank is cleared. A bank of map files is
   taken first, then any bank whose bytes all belong to packed files. The bank's last 16 bytes hold `"LF2V"` and the
   next free offset (u24). On the US ROM banks $A8-$BF hold nothing but packed files (18 banks were used when every map was moved).
   On an 8 MB ROM (romsize.md) empty banks $01-$3F of the upper 4 MB are taken first (they see the same RAM, and the
   header's bank, $87 + a byte added in 8 bits, wraps to them), so nothing moves.
3. A moved block is `"LF2B"`, u16 room (from the PH), u16 size of the copied part, then the copy of the game's block
   (header, lists, key table, every script the lists reach: offsets don't change), then the **added part**, laid out
   again on every save: the six lists, then each script that lives there (a changed or new event, written whole with
   the code it shares with others, so it depends on nothing else). The map header's u24 points at the copy
   (bank $87 + hi, address lo + $8000), for every map that used the block. When the added part outgrows its room the
   chunk grows in place (when it was the last thing put in its bank) or moves to a bigger one.
4. A start-up script that grows moves on its own (`"LF2I"`, u16 room, then the script), its jumps counted from the
   start of its new bank; only this map's header changes.
5. Text that moves is packed again (repeats `0A` are written out).

**New events** (the "New event" button):

* *Talking to a character*: a +6 record with the character's id.
* *Talking to a new character*: a character start (map info +10: the clicked tile, a walk area 2 tiles around, the
  movement picked) and `68 id sprite` at the top of the start-up script, then a +6 record.
* *Stepping onto an area*: a trigger area (map info +0C, the next free id) and a +8 record.
* *Right after another event*: that event's ends go on to the new steps (an End at the very bottom is just removed;
  others become "go to").
* *Plays only once*: a story flag no event and no game code uses (from EF down) is checked first ("if it is on, go to
  the end") and turned on right after.
* The map info block grows into the added space (any bank works for it): the old block is copied with the changed list
  rewritten at its end, and the map's pointer at `$CF:FCBC` changes.

Edit > Undo takes back each save. In the tab, Undo / Redo work on the steps before saving; Revert drops them.

## 9. Checked with the editor's own tests (not in the game)

* All 1322 events of the 242 maps read with no problem; every event whose steps sit back to back is rebuilt byte for
  byte the same.
* Every event of every map grown by one step and saved, one after the other on one ROM (253 block moves, 18 event
  space banks): every event reads back the same, the packed files unpack the same.
* A new character with a talk event and a trigger area event added to every map (half the talk events removed again):
  all 1679 events, 239 start-up scripts and every map info list read back as expected.
* Same-size edits (a wait in each of 153 events) change exactly one byte; 745 of 784 shortened texts stay in place.

## 10. Checked in the game (BizHawk, new game, the shop scene in Elcid, map 04)

* The opening scene (map 04 scene 1) with a changed line and an added sound step: the map's events moved to $B5:8008
  and the changed line shows. The game's intro (map 02 scene 1) moved the same way also showed its changed line.
* A shortened line written in place ("Hi, [Name]! (in place)").
* "Right after another event": the opening scene went on with a new line before control came back.
* A new character (sprite 0C, standing facing down) on a clicked tile in Tia's shop, with "plays once": talking to it
  showed its line and turned story flag EF on; the second time nothing was said.
* A new trigger area under the player: stepping on it showed its line and turned flag EE on; stepping off and back on
  again (still inside the area) showed nothing.
* Added to the opening scene: B6 00 A0 (Maxim not drawn), 7A 00 10 (a pose frame), 5A / 8A (the screen shakes, then
  stops), 47 10 (thought box), 48 01 14 (the box at column 1, row 20), BB (the "LUFIA 2 REPORT" screen).
* The opening map's first event step 5B 01 00 plays the title sequence (night forest, title screen).

## 11. Still uncertain

4E, 82, A6, AD, B5, B7, BD, C1, C5, CC, 7C and 80 (ship values), the 0B-39 movement modes, the meaning of the ring
parameters (8D family), 95's colours (a test flash wasn't clearly visible), 69 bit 04, the animation numbers of 73 / 9C
/ C6, and which door values open or close beyond the game's own uses.

## 12. Differences from LEdit (l2ref.md 4.5 / 4.6)

* **2E and 2D are 1 byte** (show / hide a character). 14 is a check list ending in FF, not 6 bytes. 19 church: 0
  bytes. 28: 0. 3D: 1. 83: 1. 25: 2 (items 100-1FF). 60: 2. 41: 2 or 3. 5B: 2 except 5B 03 / 5B 06. 6D / 6E: u16 +
  text + 00. C8 / C9: 7.
* 0A is a text repeat, 12 a "different each time" counter, 1D-1F counters, 07 a letter of the third page, 17's byte is
  the inn price, 5E a walk through exit xx, 84 stops forever, E0 (in 14) is "knows a spell".
* 61-67 speak as Maxim, Selan, Guy, Artea, **Dekar, Tia**, Lexis (LEdit has Tia = 65, Dekar = 66).
* E0 is not a command (the table ends at CC).
