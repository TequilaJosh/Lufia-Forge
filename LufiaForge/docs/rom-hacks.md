# Other ROM hacks

Lufia Forge edits hacked ROMs as well as the clean US games, and keeps what another hack added safe.

## Which game

The cartridge title tells the games apart ("LUFIA" / "Lufia II(Estpolis II)"). A hack that renamed its game is still
found by the game's own code (Core/RomIdentity.cs `ByCode`): the event and setup interpreters of Lufia 1 ($01:98AF,
$01:C2A0) and the event command table and battle script interpreter of Lufia II ($80:CA14, $85:B452), which hacks leave
alone. A copier header (512 bytes) is removed when a ROM is opened.

## What the ROM is

Lufia Forge ships a fingerprint of each clean US ROM (Fingerprints/lufia1.bin, lufia2.bin: the low 16 bits of a CRC32
of every 64 bytes; no game data). Comparing the ROM a project started from with it gives:
- the clean US game;
- a hack Lufia Forge knows: Lufia Restored v3.2 (Lufia 1); Frue, Spekkio, Kureji Lufia and The Wrath of Foomy v8.2
  (Lufia II), from their patches' bytes;
- another hack, and which parts of the game it changed (game code, events, monsters, items, maps and graphics...).

The Lufia II Overview page and ROM Info show it ("Started from: ...").

## Tables another hack moved (Core/TableGuard.cs)

Every table Lufia Forge reads at a fixed place is tied to the places in the game's code that point at it (found by
scanning the clean ROMs for its address; Lufia II: items, item names, spells, IP attacks and their effects, capsule
monsters, names, curves and attacks, hero stats, growth, new game and Ancient Cave records, treasure, staff roll,
battle backgrounds, map sprite colours, map info, packed files, shops, monster groups, map encounters, shared battle
scripts, monster attack names; Lufia 1: the map select table, map records, event scripts, characters, monster groups).
When those bytes differ, another hack changed where the game keeps the table:
- the pages that edit it are read-only, with a banner saying why;
- writing to the table's old place is refused (RomBuffer.Protected), whatever tries it;
- tables Lufia Forge moves itself (spells, IP attacks) or follows from the code (the monster table the fan hacks moved
  to $D0:A000) count as in place.

Checked: the clean ROMs, Restored, Frue, Spekkio and Kureji lock nothing; a test hack that moved Lufia II's item table
to $DE:8000 and renamed the game makes the Items page read-only, keeps Spells editable, and a write to the old table is
refused with the hack's bytes left as they were.

## The Lufia II fan hacks (Frue, Spekkio, Kureji Lufia, The Wrath of Foomy v8.2)

Lufia Forge reads these hacks from where their own code says the data is: the table each routine reads (its operand), the
length the table has (its records start right after it, so the table ends at the first record, or at an entry that
points back into the table), and the 16-bit sums the game uses for offsets (a record's u16 offsets are added in the
record's bank, so $FF79 means 0x87 bytes back and a jump past the bank's end comes round to its start: Core/Lufia2
L2Rom.InBank).

| | US game | Frue / Foomy | Spekkio | Kureji |
|---|---|---|---|---|
| Monster table | $96:85C0, 224 entries | $D0:A000, 256 entries | $D0:A000, 256 entries | $D0:A000, 232 entries |
| Monsters | 224 | 224 (E0-FF all point at one blank record, $D0:EA04) | 235: E0 Egg Dragon, E1 La Puella, E2 Queen Spider, E3 Lady Spider, E4 Pet Dragon, E8 Gades, E9 Amon, EA Amon, ED/EE Boxy, EF Bile Jelly (E5-E7, EB, EC, F0-FF point at one placeholder) | 232: E0 Gades, E1 Amon, E2 Gades, E3 Amon, E4 Erim, E5 Daos, E6 Egg Dragon, E7 White Dragon (E8-FF are 0000) |
| Story battles using them ($97:C53D, 27 battles) | | | 16 Egg Dragon, 1C La Puella, 1F Gades, 20 and 25 Amon | 1F-25: E0-E6 |
| Script bytes in a monster's name field | none | none | 60 records | 79 records |
| Script offsets that wrap round bank $D0 | | | 16 | 15 |
| Spell table $95:FA5B | 40 | room for 64, 40 spells; the 24 spare entries hold the tails of Strong, Stronger, Champion, Rally and Valor (their jumps wrap round to $95:FAC9...) | same | same |
| Items, IP attacks, IP effects | 467, 168, 141 | same tables and counts | same | same |
| Descriptions $97:8000 | 729 | same table, 236 texts changed, all in the US space (16 leftover bytes no entry uses) | same | 237 changed |
| Shop table $97:EE9F (any id 00-FF) | shops 01-3E, zeros after | same | 3F, 40, 41 added in the table's zero tail: the table has B0 entries, 62 shops | 3C and 3D repointed, 3F added: A6 entries, 60 shops |
| Maps | | 229 packed files moved to 0x296D27-0x30DAC2 (most maps from the PAL version; the shrine maps stay the US ones), the per-map lists ($CF:F72E...) and the world map unchanged | same | same |

What Lufia Forge does with them:
- Monsters: the list has every entry of the table (L2Battle.MonsterCountIn); placeholder slots show as "(unused slot)", ids
  that share a record as "(= XX)", and the page says when an edit changes several ids. A name may grow only up to the first
  byte a battle script uses (L2Battle.NameRoom; 13 letters once the data is tidied, below), and keeps the hack's 00 ending; stats, drops and scripts are read and
  written in place. Battle scripts are read in the record's bank, following every jump (also back before the record, into
  other records and into the spell table's spare space).
- Shops: the table's real length (L2Shops.CountIn), so the added shops show and edit and nothing past the table is read
  as a shop. Added shops are marked "(added)".
- Spells, IP attacks, IP effects and items: their counts come from the tables (L2Magic.SpellCount / IpCount /
  IpEffectCount, L2Battle.ItemCountIn). Adding spells or IP attacks (moving them, L2Magic) works on the hacks: every spell,
  IP attack, script and description reads the same afterwards.
- New monsters (docs/new-monsters-and-shops.md): Frue and Foomy take them in their placeholder ids, the records in the
  empty end of bank $D0 (about 25); Spekkio and Kureji fill bank $D0, so the first new monster moves the monsters to a
  bank of their own ("Make room for new monsters", below).
- New shops and longer shop lists: the hacks' shop code is the US game's, so their shops move to a bank of Lufia Forge's
  the same way (new ids from 3F in Frue and Foomy, 42 in Spekkio, 40 in Kureji). Checked in the game: Frue's new monster
  E0 in a story battle, Spekkio's new shop 42.
- Descriptions and maps already read the way the game does (the hacks keep both where the US game reads them).

Measured with a console check (every count and entry against what the game's code reads, worked out on its own: monsters
listed and read from the right place, scripts that read, shops, items, spells, IP attacks, descriptions, every map drawn
with its blocks and tiles in range):

| | before | after |
|---|---|---|
| Frue | 0 found, but 5 spell scripts read from the wrong bank | 0 |
| Spekkio | 84 (11 monsters not listed, 12 scripts read from the wrong place, 3 that didn't read, 58 shops that aren't shops) and 24 scripts read from the wrong bank | 0 |
| Kureji | 97 (8 monsters not listed, 11 + 1 scripts, 77 shops that aren't shops) and 32 scripts read from the wrong bank | 0 |
| Foomy | 0 found, but 5 spell scripts read from the wrong bank | 0 |

Checked in the game (BizHawk, the opening changed to start the story battle or open the shop): Spekkio's monster E0 and
Kureji's E6 (both past the US game's 224), edited to the name "FORGE TEST", HP 1, EXP 500 and a turn script that only
defends, fight in their story battles (16 and 25): they never attack, Kureji's shows its new name in the target box, and
Spekkio's falls to one hit and gives Maxim 500 EXP. Spekkio's added shop 41 and Kureji's 3F open with their lists and the
first item changed to Hi-Potion. In the window, a Spekkio project lists 256 monsters (the 21 placeholders as unused
slots) and 62 shops, and an edit of monster E0 on the page reaches the ROM.

### Tidy the hack's data (Lufia II > Settings > HACK DATA)

The hacks' scripts sit inside other records: monster scripts in the unused end of other monsters' name fields (and a
few inside a record's own bytes), spell scripts running on into the spell table's spare entries. Lufia Forge reads and
edits them there, but a name can't grow over a script and the data is hard to take apart. "Tidy the hack's data..."
moves them to space of their own (Core/Lufia2/L2Tidy.cs):

- Monster scripts: a script's u16 offset is added to its record in the record's bank, so a moved script has to stay in
  bank $D0, and that bank is full but for one empty run: the zeros between the monster attack names (the hacks moved
  them to $D0:8000) and the monster table at $D0:A000 ($D0:8E35-$D0:9FFF, 4,555 bytes, in Spekkio; $D0:8EC6-, 4,410
  bytes, in Kureji). The run has to be at least 4 KB, the rule Lufia Forge's added space follows; 16 bytes are left at
  each end. The monster table and the records stay where they are (the hacks' own code reads them at $D0:A000), so
  only the scripts move. Every script whose bytes (as Lufia Forge reads it) touch a name
  field or a record is laid out again for its own record: only the steps a path reaches, in pieces that end where a
  path ends, a piece without jumps written once for every script that ends with it (it reads the same from any
  record). The record's tag 07 / 08 offset is rewritten (it wraps round the bank, as the hacks' own offsets do). Name
  bytes no script uses any more are set to 00.
- Spell scripts: the spells move to Lufia Forge's spell block with each script right after its spell, the same move
  as adding a spell (L2Magic.Install: the IP attacks, IP effects and descriptions move too). The old spell table is
  no longer read.

Nothing is written before the tidied ROM, built in memory, is proven to read the same:
- every monster: name, stats, drop, the record's bytes (but the rewritten offsets), and both scripts, the old and the
  new walked side by side from their bytes on every path (L2Tidy.SameRun: each pair of steps has the same command and
  arguments and goes on to pairs that do too, a "go to" counted as the step it leads to). A test that changed one
  byte of a moved script at random, 180 times, was caught every time;
- every capsule monster form, item, spell (with its animation), IP attack and IP effect with its scripts, every
  description (by the number the game asks for), every shop and the monster attack names;
- every byte that changed is a moved script's new place, a rewritten offset, a cleared name byte, or (spells) one of
  the code places the spell move changes, the descriptions' old space, or Lufia Forge's added space.

Anything else refuses with what would differ. A script that can't be read, or jumps into the middle of a step, stays
where it is (the page says so). It is one Undo step; the card on the Settings page says what tidying would do before
anything changes, and the confirmation shows the whole preview.

| | Frue | Spekkio | Kureji | Foomy | US game |
|---|---|---|---|---|---|
| Monster scripts moved | 1 (Egg Dragon's when-hit script ran into its own record) | 30 of 23 records | 21 of 20 records | 1 (as Frue) | nothing to tidy |
| Bytes written in that run | 69 | 4,446 of 4,512 free | 4,169 of 4,368 free | 69 | |
| Name bytes cleared | 0 | 321 | 435 | 0 | |
| Monster names that can have 13 letters again | 0 | 60 (were 3 to 9) | 79 (were 3 to 8) | 0 | |
| Spell scripts | Strong, Stronger, Champion, Rally, Valor | same | same | same | |
| Scripts checked step by step | 393 monster (3,658 steps), 70 capsule, 204 item, 40 spell, 141 IP effect | 414 monster (4,919 steps) and the rest | 415 monster (5,290 steps) and the rest | 393 monster and the rest | |

A second tidy of a tidied ROM finds nothing to do. The console check above reads all four tidied ROMs with 0 problems,
and no page is locked by it.

Checked in the window: a Spekkio project shows the card ("30 monster scripts (4446 bytes) sit in name fields... Tidying
lets 60 monster names have 13 letters again"); tidying gives the same bytes as a tidy made outside the window, is one
Undo step (Undo gives back the hack's bytes exactly, Redo tidies again), the button is off afterwards, and on the
Monsters page Amon (EA), whose name could have 4 letters, takes "Amon of Doom!". The same with a Kureji project (79
names; Amon E3 takes "Amon of Doom!").

Checked in the game (BizHawk, the opening changed to start a script battle, Maxim's HP kept full so the monsters get
many turns, the same buttons in both runs, the hack against the tidied hack): the battle script runner is called the
same number of times on the same frames with the same records (the spells' records sit in Lufia Forge's spell block
after tidying, so a spell's base differs, as expected), and every screenshot (every 10 frames, 511 a battle) is
identical:

| | battle | monster turns compared |
|---|---|---|
| Spekkio | 16 Egg Dragon (its 1,885-byte script moved), 1F Gades, 20 Amon, 25 Amon | 19, 18, 15, 17 |
| Spekkio | three Web Spiders, three Red Jellies, three No Cores (battle 16 changed to them, ATP and INT 1 in both ROMs) | 27, 9, 5 |
| Kureji | 22 Amon, 23 Erim, 24 Daos, 25 Egg Dragon (the battle ends after their first action) | 1 each |
| Kureji | three Lizard Men, three Money Spiders, three Snow Cockas | 17, 30, 23 |

With Amon renamed "Amon of Doom!" on the tidied ROM (Spekkio EA, Kureji E3) the battle runs the same and the name shows
in full in the target box.

### Make room for new monsters (Lufia II > Settings > HACK DATA, or the first "Add a new monster")

Spekkio and Kureji fill bank $D0 (Frue and Foomy have 2 KB left at its end), so new monsters need another bank. Read
from the hacks' code (Core/Lufia2/L2NewMonsters.cs, `HackLayout`, every place checked byte for byte before anything
changes): the Expansion patch reads bank $D0 in the same places as the US game reads bank $96 (twelve `LDA $D0:A000+k,X`
in $81:FB1A-$81:FC0A, the record lookup $81:FCE2, the turn script's bank `LDA #$D0` at $81:B1E3) and in its own copy of
the battle loader at $81:FE00 (the game's $81:FC0B is `JML $81:FE00`; the copy reads the monster id at $7E:09F2, as
$D0:09F2 is ROM, and keeps the record's bank at combatant +$47 for the "when hit" script; the hack's $81:F4D5 is `JML
$81:FD04`, which sets bank $96 itself). Bank $D0 also holds the monster attack names ($D0:8000-, read by the hack's
code at $85:F2D7 with bank $D0), so they stay and the monsters move:

- the hack's data is tidied first when it isn't yet (the same as "Tidy the hack's data", with its own checks), so the
  tidied scripts move along and a later tidy finds nothing to do;
- everything a record or a battle script uses (the records to their 00, every step of both scripts) is copied to a
  bank of Lufia Forge's added space at the same addresses ($E4 in all four hacks), so every script offset (the wrapped
  ones too) and every edited script (looked up by record address) stays right;
- the table goes to $E4:8010 (after a 16-byte header "LF2MN" that names the bank it came from): records are read with
  `LDA table+k,X`, so they have to sit after the table; ids past the hack's are 0000;
- every other byte of the new bank is 00 (the attack names' copy, the old table, script bytes no path uses any more);
- the places above read the new bank (49 bytes of code, and Lufia Forge's script hook when it is in). Bank $D0 keeps
  the attack names and its old copy, which nothing reads any more.

New monsters then go to the new bank's free runs (00 bytes no record or script uses, after the table), the smallest one
that fits: the record, then its turn and "when hit" scripts laid out again right after it (only the steps a path
reaches, a script both tags use written once), both checked to run step by step as the original's before anything is
written. Monster FF is never used (it ends monster group and story battle lists).

Nothing is written before the new ROM, built in memory, is proven to read the same as before the move: every monster
at its new place (name, stats, drop, the record's bytes, both scripts walked side by side on every path), every capsule
form, item, spell, IP attack and effect, description and shop; and every byte that changed is in the new bank, one of
the code places, Lufia Forge's script hook or the added space's mark. One Undo step (with the first new monster, when
"Add a new monster" does it; the confirmation shows the whole preview first).

| | Frue | Spekkio | Kureji | Foomy |
|---|---|---|---|---|
| Tidied first | yes (1 script, 5 spells) | yes (30 scripts, 5 spells) | yes (21 scripts, 5 spells) | yes (1 script, 5 spells) |
| Room for new monsters | 2,111 bytes in $D0 -> 13,615 in $E4 | 2 -> 8,240 | 1 -> 7,602 | 2,111 -> 13,606 |
| Bytes of $E4 cleared to 00 (not used by any monster) | 2,192 | 5,914 | 6,391 | 2,271 |
| Ids free for new monsters | 31 (E0-FE) | 20 (E5-E7, EB, EC, F0-FE) | 23 (E8-FE) | 31 |
| Copies made (monsters 00, 01, 02... in turn) until the ids ran out | 31, 2,356 bytes | 20, 1,341 bytes | 23, 1,647 bytes | 31, 2,356 bytes |

So the ids, not the bytes, are the limit: a copy of a monster takes its record (about 45 bytes) and its scripts
(a few dozen bytes for most, 1,885 for Spekkio's Egg Dragon).

Checked with a console check on the four tidied hacks: every monster reads the same after the move (Spekkio 256
monsters, 414 scripts, 4,914 steps; Kureji 232, 415, 5,290), the bytes changed outside $E4 are exactly the code places
and the added space's mark, tidying again finds nothing, "Make room" again refuses (done), no page is locked; every
copy reads like its monster (stats, both scripts step by step) and the old monsters stay the same. On the US game
nothing changes: "Add a new monster" writes the same bytes as before (also on Frue as it is).

Checked in the game (BizHawk, the opening changed to start story battle 16, its monsters three of one with ATP and
INT 1, the same buttons in every run; Spekkio Web Spider 44, Kureji Lizard Man BC):
- the tidied hack against the moved one: the same runner calls on the same frames (the records' bank $E4 instead of
  $D0, same addresses), every screenshot identical (511 of 511, both hacks);
- the moved hack with its old copy in bank $D0 overwritten with FF (every byte the move copied): the same, 511 of 511
  identical, so nothing reads the old copy;
- three new monsters (copies, "FORGE NEW", Spekkio E5 at $E4:E180, Kureji E8 at $E4:DEE2): the same turns on the same
  frames from the new records; only the name in the target box differs (481 of 511 screenshots identical, the 30 others
  show "FORGE NEW");
- one new monster "FORGE TEST" with HP 1 and EXP 500 (Kureji a copy of Lizard Man; Spekkio a copy of Goblin with
  level 1, AGL 1 and DFP 0, as Maxim missed the Web Spider every time there): shown by its name, it falls to Maxim's
  hit and the fight gives "Gets 500 EXP." (and its gold).

Checked in the window (a Spekkio and a Kureji project made from the untouched hack): the Monsters page offers "Add a
new monster" although $D0 is full; adding a copy of Web Spider (Kureji: Lizard Man) makes room and adds E5 (E8) as one
Undo step (the same bytes as made outside the window), Undo gives back the hack's bytes, Redo the same ROM; a second
copy is added without asking, and Undo takes only that one back; the Settings card then says the monsters have their
bank. (The Monsters page no longer writes the monster it showed when its list is read again after an Undo: before, the
new-monster note's change wrote that record back to where it had been.)


## Free space

- Lufia II's added space is used only where it is empty (0xFF), and an event-space bank only when every byte belongs to
  a packed file.
- Lufia 1's added space keeps a table of what is in it; anything else there is "kept safe" and never reused.
- Lufia 1's fixed areas inside the game (bank 0 after the monster scripts, the end of the item records): bytes that
  differ from the clean game in the ROM the project started from are another hack's and are never handed out (checked:
  test code at $00:FE00 counts as taken).
- Patches and hooks check the game's own bytes at the place they change before writing, and say so when another hack
  changed them.
