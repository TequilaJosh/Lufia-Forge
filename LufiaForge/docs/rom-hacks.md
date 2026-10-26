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

## Free space

- Lufia II's added space is used only where it is empty (0xFF), and an event-space bank only when every byte belongs to
  a packed file.
- Lufia 1's added space keeps a table of what is in it; anything else there is "kept safe" and never reused.
- Lufia 1's fixed areas inside the game (bank 0 after the monster scripts, the end of the item records): bytes that
  differ from the clean game in the ROM the project started from are another hack's and are never handed out (checked:
  test code at $00:FE00 counts as taken).
- Patches and hooks check the game's own bytes at the place they change before writing, and say so when another hack
  changed them.
