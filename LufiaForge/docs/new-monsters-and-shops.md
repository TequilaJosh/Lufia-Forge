# New monsters and new shops

Both games can have monsters and shops the game never had. This page says where the game reads them, what Lufia Forge
changes to make room, and what was checked in the game.

## Lufia II: new monsters (Monsters page, "Add a new monster")

A new monster is a copy of the monster shown: its record (name, stats, sprite and palette, drop, gold and EXP) and its
turn and "when hit" battle scripts. It gets the next id (E0 in the US game, up to FE; FF is the "empty" mark of monster
groups and story battles) and is named "New xx". Then change it like any monster, give it a sprite on the Graphics page,
and put it in a monster group or a story battle on the Encounters page.

Where the game reads monsters (found 2026-10-27): the table at $96:85C0 (u16 offsets from the table, the records right
behind it) is read in these places only:
- twelve `LDA $96:85C0+k,X` in $81:FB1A-$81:FC0A (drops, gold, EXP, sprite and palette);
- the record lookup $81:FCE2 (`LDA #$96 / PHA / PLB / LDA $85C0,Y / ADC #$85C0`);
- the battle loader $81:FC0B (the same, and it keeps the record's bank at combatant +$47 for the "when hit" script);
- the turn script's bank, `LDA #$96` at $81:B1E3.

The fan hacks' expansion (Frue, Spekkio, Kureji, Wrath of Foomy) changed exactly these to $D0:A000.

Bank $96 is full (monsters $85C0-$CF68, items after them), so the first new monster moves the monsters to a bank of
Lufia Forge's in the added space (block "LF2MN"):
- table, records and scripts ($96:85C0-$96:CF68) are copied to the new bank at the same addresses, so every record keeps
  its address in the bank: script offsets (they count from the record) and Lufia Forge's edited scripts (looked up by
  record address) stay right;
- the table moves to $xx:8200 with room for 256 ids (ids past the last monster are 0000);
- the places above read the new bank; Lufia Forge's script hook (if it is in) gets the new bank too;
- the added space is in banks $C0 and up, where $0000-$1FFF is ROM, not RAM. The battle loader reads the monster id
  ($09F2) and calls $81:F4D5 while the record's bank is the data bank, so it runs from a copy in the new bank that reads
  $7E:09F2 and sets bank $96 for $81:F4D5, as the game does;
- new records and their scripts go to the bank's free part ($xx:CF69 on, about 12 KB: about 190 more monsters).

A copy keeps its record and scripts laid out as they are around the record, so their jumps stay right.

The fan hacks keep their monsters in bank $D0 already:

| | new monsters |
|---|---|
| Frue, Wrath of Foomy | yes: they take the placeholder ids (E0 on), records in the empty end of bank $D0 ($D0:F7C1-$FFFF, about 25) |
| Spekkio, Kureji | no: their bank $D0 is full; the page says so and the button is off |

Checked in the game (BizHawk, the opening changed to start story battle 04 right after name entry, 0x3C0EE = `53 04 39`):
- US game: a copy of the Goblin as E0, "FORGE TEST", sprite 0C palette 1 (Mega Cyclops), HP 30, EXP 321, gold 77:
  the target box shows FORGE TEST, the Mega Cyclops picture, and after the fight "Gets 321 EXP. Gets 77 GOLD.";
- a copy of the Bat (its turn script calls for help or runs away): the copy runs away, as the Bat's script says;
- Frue Lufia: the same Goblin copy fights in the battle with its name and picture;
- made in the window (Monsters page, Encounters page): "FORGE BAT" placed in story battle 04 at x 120 fights there.

## Lufia II: story battles (Encounters page)

Event command 53 xx starts story battle xx. Table $97:C53D (u16 offsets from the table), each battle 0x1E bytes: six
monster ids (FF = none), each one's place on the screen (x, y), then the battle's settings (left alone). Battles 00-26;
some share a record (the page says which). The page sets each slot's monster and place.

## Lufia II: new shops (Shops page)

"Add a new shop" makes a copy of the shop shown with the next free id (3F in the US game). Any shop's menu can be
changed (Items, Weapons, Armor, Spells, Buys) and its lists can get more or fewer slots (+ and -, up to 40).
An event opens shop xx with command 18 xx ("Open a shop" on the Events page).

The shop code reads the table at $97:EE9F (any id 00-FF) and names bank $97 in three places: $82:E72E (the pointer to the
shop) and $82:E391 / $82:E404 (the item and spell lists, which the game keeps as 16-bit addresses only). Bank $97 is full,
so the first new shop or longer list copies the table and every shop to a bank of Lufia Forge's (block "LF2SH") at the
same addresses ($xx:EE9F on), and those three bytes name that bank. Every shop is read through those long pointers, so
nothing else changes. New shops and longer lists go to the bank's first part ($xx:8010-$xx:EE9E, about 28 KB).

The fan hacks have the same shop code: Frue and Foomy get ids from 3F, Spekkio from 42 and Kureji from 40 (their tables
are shorter: up to B0 and A6 entries).

Checked in the game (0x3C0EE = `18 xx` opens shop xx at the start): the US game's new shop 3F with Items (Potion,
Hi-Potion, Ex-Potion, Magic jar, Hi-Magic, Ex-Magic, Regain, Miracle), Weapons, Spells (Flash, Bolt, Thunder, Spark,
Fireball) and Sell shows each list; Spekkio's new shop 42 the same; a shop made in the window (a copy of shop 01 with a
spell list added, Bolt) shows Items, Weapons, Armor, Spells and Sell.

## Lufia 1: new monsters

Lufia 1 could already add monsters: Game Data, Monsters, "New monster" (a copy of the one shown, with its own copy of the
AI; up to 255 ids), then Encounters.

## Lufia 1: new shops (Game Data, Shops)

Event command 1C xx opens shop xx (the id goes to $1954). The shop builder $00:D1C4 reads a u16 per shop from $01:EC5B
(128 entries; 00-55 are the game's, 56-7F point at single 00 bytes, "nothing"); a shop is a type byte, its items (a byte
each), 00; the builder takes up to 11 items. Bank $01 has no room, so a hook at $00:D1D2 (where the builder reads the
table and sets the pointer's bank) looks the shop up in Lufia Forge's table first (block "LFSH" in the added space, 256
entries) and uses the game's table for the rest. Every other read goes through that pointer, so nothing else changes.

- "Add a new shop": a copy of the shop shown with the next free id from 56 (one no event opens yet), with 11 slots.
- "Give it 11 slots": a game shop gets room for 11 items (Lufia Forge keeps it from then on; events open it as before).
- An event opens it with "Open a shop" (1C xx) on the Events tab.

Checked in the game (test start at the Fortress, the prologue's first event changed to `1C 56 00` after the four heroes
join): shop 56 opens with its 11 items (Knife, Club, Mace, Dagger, Long Knife, Axe, Spear, Broad Rod, Luck Blade, Quilted
Silk, Half Mail) and buying works.
