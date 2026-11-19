# Lufia Forge - Build Tracker

ROM hack toolkit for **Lufia & The Fortress of Doom** (SNES, US, Taito 1993) and **Lufia II: Rise of the Sinistrals**
(SNES, US, Taito 1995). Built in C# WPF (.NET 8). Each phase below has its own checklist file.

**Current version: 0.4.0-beta.30.** Get the installer (`LufiaForge-win-Setup.exe`) from
[Lufia-Forge-Releases](https://github.com/TequilaJosh/Lufia-Forge-Releases/releases); installed copies update themselves.
Source releases are on this repo's [Releases](https://github.com/TequilaJosh/Lufia-Forge/releases) page.

---

## Phase Status

| # | Phase | Status |
|---|-------|--------|
| [1](./phase-01-foundation.md) | ROM Loader, Core Infrastructure, IPS Handler | ✅ Complete |
| [2](./phase-02-text-editor.md) | Text Editor + Dictionary Expansion | ✅ Complete: every dialogue box listed by map and speaker |
| [3](./phase-03-emulator.md) | Embedded Emulator + Live Memory Viewer | ✅ Complete: docked BizHawk, live WRAM, watchlist, value search, live flags |
| [4](./phase-04-disassembler.md) | 65816 Disassembler + Research Tools | ✅ Complete |
| [5](./phase-05-tile-viewer.md) | Tile & Sprite Viewer | ✅ Complete: built as the Game Data Graphics, Palettes and Sprites tabs and the Lufia II Graphics page (indexed PNG export / import) |
| [6](./phase-06-script-event-editor.md) | Script Editor, Event Editor, Cutscene Sequencer | ✅ Complete: every command decoded; Lines / Flow / Bytes views, undo/redo, stage, timeline, live flag tracker |
| [7](./phase-07-map-maker.md) | Map Viewer, Map Editor, Map Creator | ✅ Complete: every map incl. the world map; blocks, exits, arrivals, treasure, NPCs, new maps |
| [8](./phase-08-character-editor.md) | Character Editor, Sprite Swapper | ✅ Complete: Game Data editors for characters, growth, spells, items, sprites |
| [9](./phase-09-patch-manager.md) | IPS Patch Manager UI | ✅ Complete: built-in patches, IPS apply / export, project history |
| [10](./phase-10-graphics-and-new-content.md) | Graphics Import & New Content | ✅ Complete: tilesets, new maps, new sprites, new monsters |

---

## Beyond the original plan

All of these are in the app now:

- **Lufia II support**: its own tab with Maps, Exits, Treasure, World, Events, Cutscenes, Text, Heroes, Characters,
  Monsters, Monster attacks, Capsule monsters, Spells, IP attacks, Items, Shops, Encounters, Battle, Animations,
  Graphics, Music, Credits, Game start and Settings. Includes story characters, the flags manager with 128 extra
  story flags, a puzzle editor, the 16-status engine and the Dutch translation import.
- **Projects**: a ROM is opened as a `.lfproj` project with undo / redo history for every edit, dated ROM exports,
  "New project from this one" and File > Merge for combining two projects.
- **Built-in patches** (Patch Manager and the Settings pages): retargeting, level-up heals, hold L to avoid battles, 128 spells, items in the
  added space, status effects on equipment, visible monsters, higher limits (level 255, HP / MP 9999), the 32 KB
  cartridge RAM patch, a test start, fast walking with the Foul Water fix, hold R to turn without walking, and more.
- **Ancient Cave**: the cave-only game mode, built on the same patch system.
- **Fan hacks**: every patch is checked on Spekkio Lufia, Frue, Kureji, Foomy, a randomizer, Lufia Restored, the Dutch
  Lufia and the Ancient Cave demo; a patch whose game code a hack changed refuses and leaves the ROM as it was.
- **Music**: songs decoded and editable for both games, with MIDI import / export and playback.

---

## Architecture Notes

- **RomBuffer.cs**: all ROM reads and writes go through here, never raw file I/O after load
- **Lufia1Constants.cs**: confirmed Lufia 1 offsets, control codes, LoROM address translation
- **IpsHandler.cs**: patch apply and export logic (used by the Patch Manager)
- **ExpansionSpace.cs**: the allocation table for the added ROM space that new content and patches go in
- **HookPatch.cs**: the shared code for patches that hook a game routine and add code in the added space
- **Core/Lufia2/**: everything Lufia II, with verified US addresses (`LufiaForge/docs/lufia2/` has the research)
- Lufia 1 text is **standard ASCII** (0x20-0x7E) + dictionary compression via `0x0C` pointer codes
- Lufia 1 dictionary lives at file offsets `0x054E19-0x0553CC`, base offset `0x48000`
- Lufia 1 dialogue spans ROM banks `$88-$AF` (file `0x40000-0x57FFF`)
- Lufia 1 is **LoROM**, 1MB, CRC32 `5E1AA1A6` (expanded to 2 MB when added content needs room)

---

## Repo Layout

```
LufiaForge/
├── LufiaForge.sln
├── LufiaForge.BizHawkTool/   # the external tool BizHawk loads (shares memory with the app)
├── docs/                     # research notes (Lufia II in docs/lufia2/)
└── LufiaForge/
    ├── Core/                 # RomBuffer, IpsHandler, ExpansionSpace, HookPatch, the patches
    │   ├── Audio/            # songs and sound
    │   ├── Battle/           # monsters, AI, spells, items, battle patches
    │   ├── Cave/             # Ancient Cave
    │   ├── Maps/             # maps, events, cutscenes, tilesets
    │   ├── Merge/            # File > Merge
    │   └── Lufia2/           # all of Lufia II
    ├── Converters/           # WPF value converters
    ├── Themes/               # LufiaTheme.xaml (purple/gold)
    ├── ViewModels/           # MainViewModel
    └── Modules/
        ├── TextEditor/       # Phase 2
        ├── MemoryMonitor/    # Phase 3
        ├── Disassembler/     # Phase 4
        ├── TileViewer/       # Phase 5 (no longer shown, the Graphics tabs took over)
        ├── Events/           # Phase 6
        ├── Cutscenes/        # Phase 6
        ├── MapEditor/        # Phase 7 and 10
        ├── GameData/         # Phase 8 and 10
        ├── PatchManager/     # Phase 9
        ├── Lufia2/           # the Lufia II tab
        └── Common/           # shared windows and controls
tools/                        # generators and research scripts (statuses, flags, limits, emotes, l2decomp, ...)
```

---

## Progress Legend

- ✅ Done
- 🔲 Not started
- 🔄 In progress
- ⚠️ Blocked / needs research

<img width="1264" height="789" alt="image" src="https://github.com/user-attachments/assets/a17aee8c-9aa3-4813-84a4-ae132ccaf8fb" />

<img width="1254" height="791" alt="Screenshot 2026-03-30 202722" src="https://github.com/user-attachments/assets/a6dbbdbc-8975-438f-ab30-795a64e8416b" />
