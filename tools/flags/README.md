# Extra story flags (Lufia II)

`extflags.asm` adds story flags 100-17F to the US Lufia II. `gen.py` assembles it with `tools/limits/asm65.py` and writes
`LufiaForge/Core/Lufia2/L2ExtraFlags.Data.cs` (the places it changes, with the game's bytes, and the added block):

```
python tools/flags/gen.py "<clean US Lufia II ROM>"
```

## How it works

* The game keeps 256 story flags at `$7E:077E` and 32 event counters at `$7E:079E`. Its events use counters 01-0E only.
* The extra flags are the bits of counters 10-1F (`$7E:07AE`-`$7E:07BD`): flag 100 + e is bit (e & 7) of byte
  `$07AE + (e >> 3)`. Those bytes are part of what a save keeps and a new game clears, so the extra flags are saved,
  loaded and cleared with the story flags.
* New event commands (the game's command table at `$80:CA14` ends at CC):
  * `CD e`: turn flag 100 + e on
  * `CE e`: turn it off
  * `CF e w`: if it is on, go to w (like 15)
  * `D0 e w`: if it is off, go to w (like 6A)
* Command 14 (check conditions): a story flag check with bit 02 of its code set checks flag 100 + its byte. None of
  the game's 1,197 flag checks sets that bit.
* Hooks: `$80:9D35` (the command dispatch), `$80:A0AF` and `$80:A0C1` (command 14's flag checks). The small trampoline
  sits in bank `$80`'s unused bytes at `$80:9B80`; the rest goes in added space.

In the editor a story flag field holds 000-17F and picks the command by itself (1A or CD, and so on). Saving an event
that uses an extra flag adds the patch when it isn't in yet. The Flags tab (Events page) has the button that adds it.

Checked in BizHawk (2026-11-16): CD from a new-game start, CE, CF, D0, command 14 with bit 02 (also joined with "or"),
and a map's start-up script making a character only while an extra flag is on. Saving and loading a game with extra
flags on was read from the game's code, not played.
