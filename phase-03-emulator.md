# Phase 3 — Embedded Emulator + Live Memory Viewer

Embed Snes9x inside the WPF app, read live RAM/VRAM/CGRAM/registers in real time, and provide a research workspace for annotating every memory address while playing the game.

---

## Overview

The emulator panel hosts the Snes9x process inside a WPF `HwndHost`, captures its window handle, and reparents it into the app. A separate polling thread reads Snes9x's memory via `ReadProcessMemory` and streams values into the live viewer at ~30fps. Every address the user flags gets saved to a research notes file alongside the ROM.

---

## 3.1 — Snes9x Integration

- [ ] Research which Snes9x build exposes a usable window handle for reparenting
  - Target: **Snes9x 1.62.3 Win32** — single HWND, no DX fullscreen by default
  - Confirm it launches headlessly with a ROM path as a CLI argument
- [ ] Create `Modules/Emulator/` folder structure
- [ ] `EmulatorHost.cs` — `HwndHost` subclass that reparents the Snes9x window
  - [ ] `LaunchSnes9x(string romPath, string snes9xExePath)` — start process, wait for main window HWND
  - [ ] Override `BuildWindowCore` — call `SetParent()` and `MoveWindow()` to fill the host panel
  - [ ] Override `DestroyWindowCore` — kill process cleanly on tab close or app exit
  - [ ] Handle resize: hook `SizeChanged` and call `MoveWindow` to keep game filling the panel
- [ ] `EmulatorHostView.xaml` — WPF panel that contains the `HwndHost` + overlay controls
  - [ ] Toolbar: Load ROM, Pause, Resume, Reset, Save State, Load State
  - [ ] Snes9x path picker with persistent setting saved to `appsettings.json`
  - [ ] "Not configured" placeholder shown when Snes9x path is not set
- [ ] `EmulatorHostViewModel.cs` — commands wired to process control methods
- [ ] `AppSettings.cs` — simple JSON settings file (`%AppData%\LufiaForge\settings.json`)
  - [ ] `Snes9xPath`, `LastRomPath`, `MemoryPollRateMs`, `AutoLoadRomOnLaunch`
- [ ] Wire `EmulatorHostView` into `MainWindow.xaml` as a new tab: **▶ Emulator**
- [ ] Update `PropagateRomToModules()` in `MainWindow.xaml.cs` to pass ROM path to emulator VM

---

## 3.2 — Memory Reader

- [ ] `MemoryReader.cs` — Win32 `ReadProcessMemory` wrapper
  - [ ] `Attach(Process process)` — open process handle with `PROCESS_VM_READ`
  - [ ] `ReadBytes(long address, int count) -> byte[]`
  - [ ] `ReadUInt8 / ReadUInt16Le / ReadUInt32Le` convenience methods
  - [ ] `Detach()` — close handle safely
- [ ] `Snes9xAddressMap.cs` — maps SNES logical addresses to Snes9x process memory offsets
  - [ ] Research: find Snes9x 1.62.3 RAM base pointer in the process (changes per build — use signature scanning)
  - [ ] `long GetRamBase()` — signature scan for known byte pattern near RAM start
  - [ ] Constants: `RamSize = 0x20000` (128KB), `VramSize = 0x10000` (64KB), `CgramSize = 0x200` (512B)
  - [ ] SNES register offsets (PPU, APU, DMA) — document as research progresses
- [ ] `MemorySnapshot.cs` — holds a full point-in-time capture of RAM + VRAM + CGRAM + registers
  - [ ] `byte[] Ram`, `byte[] Vram`, `byte[] Cgram`, `Dictionary<string, ushort> Registers`
  - [ ] `DateTime CapturedAt`, `long FrameNumber`
- [ ] `MemoryPoller.cs` — background thread that polls at configurable rate
  - [ ] Uses `System.Threading.PeriodicTimer` (not a busy loop)
  - [ ] Fires `SnapshotReady(MemorySnapshot snapshot)` event on each tick
  - [ ] Fires `EmulatorDetached()` if the process exits
  - [ ] Configurable poll rate (default 33ms = ~30fps)

---

## 3.3 — Live Memory Viewer UI

- [ ] `MemoryViewerView.xaml` — side panel shown alongside the emulator
  - [ ] Tab strip: **RAM** | **VRAM** | **CGRAM** | **Registers** | **Watchlist**
  - [ ] Each tab has its own virtualized hex grid (address | hex bytes | ASCII preview)
- [ ] `HexGridControl.cs` — custom `UserControl` for efficient hex display
  - [ ] Virtualized rows — only render visible rows (128KB RAM = 8192 rows of 16 bytes, cannot render all at once)
  - [ ] Color-coded cell backgrounds:
    - Gold highlight = address is on the watchlist
    - Green flash = value changed since last frame
    - Red = value changed and is flagged as unknown/interesting
  - [ ] Click a cell to select that address and open the annotation panel
- [ ] **RAM tab**
  - [ ] Address range `$7E0000–$7FFFFF` (full 128KB work RAM)
  - [ ] Jump-to-address input box
  - [ ] "Changed this frame" filter toggle — hides all unchanged bytes
  - [ ] Freeze button on selected address — writes the current value back every frame (cheat-style)
- [ ] **VRAM tab**
  - [ ] Address range `$0000–$FFFF` (64KB VRAM)
  - [ ] Visual tile preview panel alongside the hex grid — renders 4bpp tiles live from current VRAM
- [ ] **CGRAM tab**
  - [ ] 256 colors shown as colored swatches + BGR555 hex value + converted RGB
  - [ ] Updates live as palettes change during gameplay
- [ ] **Registers tab**
  - [ ] Named rows for every SNES hardware register: PPU ($2100–$213F), APU ($2140–$2143), DMA ($4300–$43FF), CPU ($4200–$420D)
  - [ ] Each register shows: name, address, current value, description tooltip
- [ ] **Watchlist tab**
  - [ ] Table of user-pinned addresses: address, name, current value, previous value, delta, type (u8/u16/u32)
  - [ ] Add address button — opens dialog to enter address + name + type
  - [ ] Values update live with each poll tick
  - [ ] Export watchlist as CSV

---

## 3.4 — Research Notes System

- [ ] `ResearchNote.cs` — model for one annotated address
  - [ ] `int SnesAddress`, `string Label`, `string Description`, `NoteCategory Category`, `DateTime AddedAt`
  - [ ] `NoteCategory` enum: `Unknown`, `GameLogic`, `PlayerData`, `EnemyData`, `MapData`, `EventFlag`, `Confirmed`
- [ ] `ResearchNotesStore.cs` — load/save notes to `<romfilename>.lfnotes.json`
  - [ ] `Add(ResearchNote note)`
  - [ ] `Update(int address, string label, string description)`
  - [ ] `GetByAddress(int address) -> ResearchNote?`
  - [ ] `GetAll() -> List<ResearchNote>`
  - [ ] Auto-save on any change (debounced 500ms)
- [ ] `ResearchNotesView.xaml` — notes panel docked beside the emulator
  - [ ] Split layout: address list on left, note detail editor on right
  - [ ] Address list columns: category icon, address (hex), label, description preview
  - [ ] Sortable and filterable by category
  - [ ] Note detail editor: label field, description multiline text area, category dropdown, "Jump to address in memory viewer" button
  - [ ] Search bar filters the list in real time
  - [ ] Export all notes as Markdown table
  - [ ] Import notes from CSV (address, label, description, category)
- [ ] When user clicks any address in the memory viewer hex grid, auto-open the note for that address (or create a blank one)
- [ ] Notes with `Confirmed` category are fed back into `Lufia1Constants.cs` — show a "Promote to constants" button that generates a code snippet the user can paste

---

## 3.5 — Integration Wiring

- [ ] `MainWindow.xaml` — add **▶ Emulator** tab before Text Editor (make it tab index 0)
- [ ] When ROM is loaded in the main app, offer to auto-launch it in the emulator
- [ ] Memory viewer panel docks to the right of the emulator view (resizable splitter)
- [ ] Research notes panel docks below the memory viewer (collapsible)
- [ ] Emulator tab title shows current game state: **▶ Emulator** when running, **⏸ Paused** when paused
- [ ] Keyboard shortcut `F5` = pause/resume, `F6` = save state, `F7` = load state (forwarded to Snes9x)

---

## 3.6 — Known Technical Challenges

| Challenge | Notes |
|-----------|-------|
| Snes9x RAM base address | Changes between builds and with ASLR. Use `ReadProcessMemory` + signature scan for the byte pattern at the start of the 128KB RAM block. Alternative: use a fixed offset from a known export if Snes9x exposes one. |
| HwndHost input focus | WPF and Win32 fight over keyboard input when hosting a foreign HWND. Must forward `WM_KEYDOWN`/`WM_KEYUP` messages manually via `PostMessage` to the Snes9x HWND. |
| Snes9x window reparenting | Some Snes9x builds use a child window for rendering (OpenGL/DirectDraw), not the top-level HWND. May need to enumerate child windows and reparent the correct one. |
| Poll rate vs UI thread | Never update the hex grid directly from the poll thread. Marshal snapshots to the UI thread via `Dispatcher.InvokeAsync` with a bounded queue — drop frames if the UI can't keep up. |
| VRAM address mapping | Snes9x stores VRAM differently internally than the SNES memory map. The VRAM base pointer in the process must be found separately from the RAM base. |
