# Lufia Forge — Build Tracker

ROM hack toolkit for **Lufia & The Fortress of Doom** (SNES, US, Taito 1993).  
Built in C# WPF (.NET 8). Each phase below has its own checklist file.

---

## Phase Status

| # | Phase | Status |
|---|-------|--------|
| [1](./phase-01-foundation.md) | ROM Loader, Core Infrastructure, IPS Handler | ✅ Complete |
| [2](./phase-02-text-editor.md) | Text Editor + Dictionary Expansion | 🔄 Complete |
| [3](./phase-03-emulator.md) | Embedded Emulator + Live Memory Viewer | ✅ Not Started |
| [4](./phase-04-disassembler.md) | 65816 Disassembler + Research Tools | 🔲 Not Started |
| [5](./phase-05-tile-viewer.md) | Tile & Sprite Viewer | 🔲 Not Started |
| [6](./phase-06-script-event-editor.md) | Script Editor, Event Editor, Cutscene Sequencer | 🔲 Not Started |
| [7](./phase-07-map-maker.md) | Map Viewer, Map Editor, Map Creator | 🔲 Not Started |
| [8](./phase-08-character-editor.md) | Character Editor, Sprite Swapper | 🔲 Not Started |
| [9](./phase-09-patch-manager.md) | IPS Patch Manager UI | 🔲 Not Started |

---

## Architecture Notes

- **RomBuffer.cs** — all ROM reads and writes go through here, never raw file I/O after load
- **Lufia1Constants.cs** — all confirmed offsets, control codes, LoROM address translation
- **IpsHandler.cs** — patch apply and export logic (used by Phase 9 UI)
- Text encoding is **standard ASCII** (0x20–0x7E) + dictionary compression via `0x0C` pointer codes
- Dictionary lives at file offsets `0x054E19–0x0553CC`, base offset `0x48000`
- Dialogue spans ROM banks `$88–$AF` (file `0x40000–0x57FFF`)
- ROM is **LoROM**, 1MB, CRC32 `5E1AA1A6`

---

## Repo Layout

```
LufiaForge/
├── LufiaForge.sln
└── LufiaForge/
    ├── Core/               # RomBuffer, RomLoader, IpsHandler, Lufia1Constants
    ├── Converters/         # WPF value converters
    ├── Themes/             # LufiaTheme.xaml (purple/gold)
    ├── ViewModels/         # MainViewModel
    ├── Modules/
    │   ├── TextEditor/     # 🔄 Phase 2
    │   ├── Emulator/       # Phase 3
    │   ├── Disassembler/   # Phase 4
    │   ├── TileViewer/     # Phase 5
    │   ├── ScriptEditor/   # Phase 6
    │   ├── MapMaker/       # Phase 7
    │   ├── CharacterEditor/# Phase 8
    │   └── PatchManager/   # Phase 9
    └── Dialogs/
```

---

## Progress Legend

- ✅ Done
- 🔲 Not started
- 🔄 In progress
- ⚠️ Blocked / needs research

<img width="1264" height="789" alt="image" src="https://github.com/user-attachments/assets/a17aee8c-9aa3-4813-84a4-ae132ccaf8fb" />

<img width="1254" height="791" alt="Screenshot 2026-03-30 202722" src="https://github.com/user-attachments/assets/a6dbbdbc-8975-438f-ab30-795a64e8416b" />

