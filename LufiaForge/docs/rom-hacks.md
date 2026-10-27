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
  byte a battle script uses (L2Battle.NameRoom), and keeps the hack's 00 ending; stats, drops and scripts are read and
  written in place. Battle scripts are read in the record's bank, following every jump (also back before the record, into
  other records and into the spell table's spare space).
- Shops: the table's real length (L2Shops.CountIn), so the added shops show and edit and nothing past the table is read
  as a shop. Added shops are marked "(added)".
- Spells, IP attacks, IP effects and items: their counts come from the tables (L2Magic.SpellCount / IpCount /
  IpEffectCount, L2Battle.ItemCountIn). Adding spells or IP attacks (moving them, L2Magic) works on the hacks: every spell,
  IP attack, script and description reads the same afterwards.
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

Not done: a "tidy the hack's data" action. The hacks' scripts sit inside other records (name fields, the spell table's
spare entries) and are shared between monsters, so packing them elsewhere would mean changing the game's own offsets in
many records; it isn't needed to read or edit them, so it was left out.

## Free space

- Lufia II's added space is used only where it is empty (0xFF), and an event-space bank only when every byte belongs to
  a packed file.
- Lufia 1's added space keeps a table of what is in it; anything else there is "kept safe" and never reused.
- Lufia 1's fixed areas inside the game (bank 0 after the monster scripts, the end of the item records): bytes that
  differ from the clean game in the ROM the project started from are another hack's and are never handed out (checked:
  test code at $00:FE00 counts as taken).
- Patches and hooks check the game's own bytes at the place they change before writing, and say so when another hack
  changed them.
