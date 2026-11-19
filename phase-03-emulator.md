# Phase 3 - Embedded Emulator + Live Memory Viewer

Embed Snes9x inside the WPF app, read live RAM/VRAM/CGRAM/registers in real time, and provide a research workspace for annotating every memory address while playing the game.

**Status: ✅ Complete (2026-11-16).** Built as the Memory Monitor tab (`Modules/MemoryMonitor`). Instead of an embedded Snes9x read with
`ReadProcessMemory`, the app docks BizHawk (`BizHawkHost`) and reads live WRAM and the CPU registers from shared memory (`BizHawkBridge`,
a memory-mapped file) that the Lufia Forge Monitor external tool (`LufiaForge.BizHawkTool`) writes every frame. The same bridge feeds the
Disassembler's live PC tracking, the Events tab's Live Flags and the cutscene timeline's "Follow the game". Only WRAM and the CPU registers
are passed over, so the VRAM, CGRAM and hardware register views were dropped, and address notes became saved labels instead of a separate
notes panel. Differences from the plan are marked *(changed)* or *(dropped)* below.

---

## Overview

The emulator panel hosts the Snes9x process inside a WPF `HwndHost`, captures its window handle, and reparents it into the app. A separate polling thread reads Snes9x's memory via `ReadProcessMemory` and streams values into the live viewer at ~30fps. Every address the user flags gets saved to a research notes file alongside the ROM.

---

## 3.1 - Snes9x Integration

- [x] Research which Snes9x build exposes a usable window handle for reparenting *(changed: uses the BizHawk bundled in the repo's `BizHawk` folder, found by `MemoryMonitorViewModel.FindBundledBizHawk`; live registers need its BSNES core)*
  - Target: **Snes9x 1.62.3 Win32** - single HWND, no DX fullscreen by default
  - Confirm it launches headlessly with a ROM path as a CLI argument
- [x] Create `Modules/Emulator/` folder structure *(changed: `Modules/MemoryMonitor`)*
- [x] `EmulatorHost.cs` - `HwndHost` subclass that reparents the Snes9x window *(changed: `BizHawkHost` moves the EmuHawk window into a WinForms `Panel` shown in a `WindowsFormsHost`; it can also undock it as an always-on-top window)*
  - [x] `LaunchSnes9x(string romPath, string snes9xExePath)` - start process, wait for main window HWND *(changed: `BizHawkHost.LaunchAsync(emuHawkPath, romPath)`; takes a BizHawk that's already running if there is one)*
  - [x] Override `BuildWindowCore` - call `SetParent()` and `MoveWindow()` to fill the host panel *(changed: `ReparentWindow()` strips the window styles, calls `SetParent` and sizes it with `MoveWindow`)*
  - [x] Override `DestroyWindowCore` - kill process cleanly on tab close or app exit *(changed: `BizHawkHost.Dispose` on app exit closes a BizHawk it started, killing it after 3 s, and hands back one it only attached to)*
  - [x] Handle resize: hook `SizeChanged` and call `MoveWindow` to keep game filling the panel *(changed: the panel's `Resize` event calls `ResizeBizHawk`)*
- [x] `EmulatorHostView.xaml` - WPF panel that contains the `HwndHost` + overlay controls *(changed: `MemoryMonitorView.xaml`, BizHawk on the left, memory tools on the right)*
  - [x] Toolbar: Load ROM, Pause, Resume, Reset, Save State, Load State *(changed: the toolbar has Launch BizHawk, which loads the project's ROM, and Undock / Re-dock; pause, reset and states are BizHawk's own menus in the docked window)*
  - [x] Snes9x path picker with persistent setting saved to `appsettings.json` *(changed: no picker, `FindBundledBizHawk` looks for `BizHawk\EmuHawk.exe` above the app folder)*
  - [x] "Not configured" placeholder shown when Snes9x path is not set *(changed: the empty panel says how to launch BizHawk, and an "undocked" placeholder shows while it floats)*
- [x] `EmulatorHostViewModel.cs` - commands wired to process control methods *(changed: `MemoryMonitorViewModel` with `LaunchBizHawk` and `ToggleUndock`)*
- [x] `AppSettings.cs` - simple JSON settings file (`%AppData%\LufiaForge\settings.json`) *(changed: `Core/AppSettings.cs` uses that file, but only for the Snes9x 1.51 mode setting)*
  - [x] `Snes9xPath`, `LastRomPath`, `MemoryPollRateMs`, `AutoLoadRomOnLaunch` *(dropped: BizHawk is found by itself, the ROM comes from the open project, the poll rate is fixed and launching is always the user's click)*
- [x] Wire `EmulatorHostView` into `MainWindow.xaml` as a new tab: **▶ Emulator** *(changed: the Memory Monitor tab)*
- [x] Update `PropagateRomToModules()` in `MainWindow.xaml.cs` to pass ROM path to emulator VM *(changed: it calls `SetRom`, and `PrepareRom` = `WritePlayRom` writes a fresh copy of the project's ROM when BizHawk launches)*

---

## 3.2 - Memory Reader

- [x] `MemoryReader.cs` - Win32 `ReadProcessMemory` wrapper *(changed: `BizHawkBridge` reads the `LufiaForge_WRAM` memory-mapped file that `LufiaForgeToolForm` writes each frame)*
  - [x] `Attach(Process process)` - open process handle with `PROCESS_VM_READ` *(changed: `TryConnect()` opens the shared memory)*
  - [x] `ReadBytes(long address, int count) -> byte[]` *(changed: `ReadFrame()` copies all 128 KB of WRAM when the frame counter moves, and skips a frame still being written)*
  - [x] `ReadUInt8 / ReadUInt16Le / ReadUInt32Le` convenience methods *(changed: `MemoryMonitorViewModel.ReadTyped` for U8, U16, U32, S8 and S16)*
  - [x] `Detach()` - close handle safely *(changed: `Disconnect()`; a connection with no new frame for 3 s is reopened or dropped)*
- [x] `Snes9xAddressMap.cs` - maps SNES logical addresses to Snes9x process memory offsets *(dropped: the tool reads BizHawk's "WRAM" memory domain directly)*
  - [x] Research: find Snes9x 1.62.3 RAM base pointer in the process (changes per build - use signature scanning) *(dropped: not needed with BizHawk's memory API)*
  - [x] `long GetRamBase()` - signature scan for known byte pattern near RAM start *(dropped: same reason)*
  - [x] Constants: `RamSize = 0x20000` (128KB), `VramSize = 0x10000` (64KB), `CgramSize = 0x200` (512B) *(changed: only `BizHawkBridge.WramSize = 0x20000`; VRAM and CGRAM aren't passed over)*
  - [x] SNES register offsets (PPU, APU, DMA) - document as research progresses *(changed: the shared memory header carries the 65816 registers PC, PB, P, D and DB from BizHawk's `GetRegisters`; no PPU, APU or DMA registers)*
- [x] `MemorySnapshot.cs` - holds a full point-in-time capture of RAM + VRAM + CGRAM + registers *(changed: `BizHawkBridge` keeps `Wram`, `PreviousWram` and `FrameCount`, and sends registers as a `CpuSnapshot`)*
  - [x] `byte[] Ram`, `byte[] Vram`, `byte[] Cgram`, `Dictionary<string, ushort> Registers` *(changed: WRAM only, plus `CpuSnapshot` for the CPU registers)*
  - [x] `DateTime CapturedAt`, `long FrameNumber` *(changed: `FrameCount` from the shared memory header; the tool writes a timestamp too, the app doesn't use it)*
- [x] `MemoryPoller.cs` - background thread that polls at configurable rate *(changed: a `DispatcherTimer` in `MemoryMonitorViewModel`, on the UI thread)*
  - [x] Uses `System.Threading.PeriodicTimer` (not a busy loop) *(changed: `DispatcherTimer`, so no marshaling to the UI thread is needed)*
  - [x] Fires `SnapshotReady(MemorySnapshot snapshot)` event on each tick *(changed: each new frame refreshes the hex view and watchlist, and `BizHawkBridge.CpuSnapshotReady` fires for the Disassembler)*
  - [x] Fires `EmulatorDetached()` if the process exits *(changed: `BizHawkHost.Exited`, plus the bridge's 3 s stale check for a BizHawk that crashed)*
  - [x] Configurable poll rate (default 33ms = ~30fps) *(changed: fixed at 33 ms)*

---

## 3.3 - Live Memory Viewer UI

- [x] `MemoryViewerView.xaml` - side panel shown alongside the emulator *(changed: the "Memory tools" pane on the right of `MemoryMonitorView`, which can pop out through `PopOutHost`)*
  - [x] Tab strip: **RAM** | **VRAM** | **CGRAM** | **Registers** | **Watchlist** *(changed: Hex View | Watchlist | Search)*
  - [x] Each tab has its own virtualized hex grid (address | hex bytes | ASCII preview) *(changed: Hex View is a read-only text dump of 512 bytes at a time with address, hex and ASCII)*
- [x] `HexGridControl.cs` - custom `UserControl` for efficient hex display *(dropped: the text dump only shows 512 bytes, so no custom control was needed)*
  - [x] Virtualized rows - only render visible rows (128KB RAM = 8192 rows of 16 bytes, cannot render all at once) *(dropped: same reason)*
  - [x] Color-coded cell backgrounds: *(dropped: the watchlist shows previous value and delta instead)*
    - Gold highlight = address is on the watchlist
    - Green flash = value changed since last frame
    - Red = value changed and is flagged as unknown/interesting
  - [x] Click a cell to select that address and open the annotation panel *(dropped: no clickable cells and no annotation panel)*
- [x] **RAM tab** *(changed: the Hex View tab)*
  - [x] Address range `$7E0000-$7FFFFF` (full 128KB work RAM)
  - [x] Jump-to-address input box *(changed: an Offset box with the WRAM offset in decimal, and +/- buttons that page 256 bytes)*
  - [x] "Changed this frame" filter toggle - hides all unchanged bytes *(changed: the Search tab narrows addresses with First / Next scans by value)*
  - [x] Freeze button on selected address - writes the current value back every frame (cheat-style) *(dropped: the app only reads the shared memory, it never writes to the game)*
- [x] **VRAM tab** *(dropped: only WRAM is passed over)*
  - [x] Address range `$0000-$FFFF` (64KB VRAM) *(dropped: same reason)*
  - [x] Visual tile preview panel alongside the hex grid - renders 4bpp tiles live from current VRAM *(dropped: same reason)*
- [x] **CGRAM tab** *(dropped: only WRAM is passed over)*
  - [x] 256 colors shown as colored swatches + BGR555 hex value + converted RGB *(dropped: same reason)*
  - [x] Updates live as palettes change during gameplay *(dropped: same reason)*
- [x] **Registers tab** *(changed: no tab; the CPU registers drive the Disassembler's live PC tracking)*
  - [x] Named rows for every SNES hardware register: PPU ($2100-$213F), APU ($2140-$2143), DMA ($4300-$43FF), CPU ($4200-$420D) *(dropped: only the 65816 CPU registers are passed over)*
  - [x] Each register shows: name, address, current value, description tooltip *(dropped: same reason)*
- [x] **Watchlist tab**
  - [x] Table of user-pinned addresses: address, name, current value, previous value, delta, type (u8/u16/u32) *(changed: no type column, the size is picked when adding; S8 and S16 are there too)*
  - [x] Add address button - opens dialog to enter address + name + type *(changed: inline address, label and size boxes with + Add, a search of known addresses from `Lufia1AddressMap` with "+ Category", and the Disassembler's "Add to Watchlist")*
  - [x] Values update live with each poll tick
  - [x] Export watchlist as CSV *(dropped: a watch's Save button keeps it as a reusable label instead)*

---

## 3.4 - Research Notes System

- [x] `ResearchNote.cs` - model for one annotated address *(changed: `KnownAddress` in `Lufia1AddressMap` for RAM, and the Disassembler's `Bookmark` for code)*
  - [x] `int SnesAddress`, `string Label`, `string Description`, `NoteCategory Category`, `DateTime AddedAt` *(changed: `KnownAddress` has address, label, size and category; no description or date. `Bookmark` has a comment)*
  - [x] `NoteCategory` enum: `Unknown`, `GameLogic`, `PlayerData`, `EnemyData`, `MapData`, `EventFlag`, `Confirmed` *(changed: category is a plain string; user labels go in "Custom")*
- [x] `ResearchNotesStore.cs` - load/save notes to `<romfilename>.lfnotes.json` *(changed: `Lufia1AddressMap.LoadCustom` / `SaveCustom` use `%AppData%\LufiaForge\custom_addresses.json`; Disassembler bookmarks go in `<rom>.lfbookmarks.json`)*
  - [x] `Add(ResearchNote note)` *(changed: `Lufia1AddressMap.AddCustom`)*
  - [x] `Update(int address, string label, string description)` *(dropped: labels are added and removed (`RemoveCustom`), not edited)*
  - [x] `GetByAddress(int address) -> ResearchNote?` *(changed: `Lufia1AddressMap.Search` matches label, category or address)*
  - [x] `GetAll() -> List<ResearchNote>` *(changed: `Lufia1AddressMap.All`)*
  - [x] Auto-save on any change (debounced 500ms) *(changed: saves right away on every add)*
- [x] `ResearchNotesView.xaml` - notes panel docked beside the emulator *(dropped: the Watchlist tab's label search does this job)*
  - [x] Split layout: address list on left, note detail editor on right *(dropped: no notes panel)*
  - [x] Address list columns: category icon, address (hex), label, description preview *(changed: the label search list shows address, label and category)*
  - [x] Sortable and filterable by category *(changed: the search also matches the category, and "+ Category" adds every address in one)*
  - [x] Note detail editor: label field, description multiline text area, category dropdown, "Jump to address in memory viewer" button *(dropped: no notes panel)*
  - [x] Search bar filters the list in real time *(changed: the Watchlist tab's Search box)*
  - [x] Export all notes as Markdown table *(dropped: research went into the repo's research `.md` files by hand)*
  - [x] Import notes from CSV (address, label, description, category) *(dropped: no RAM label import; story flag names import from CSV in Live Flags)*
- [x] When user clicks any address in the memory viewer hex grid, auto-open the note for that address (or create a blank one) *(dropped: the hex view is plain text; double-clicking a label search result adds it to the watchlist)*
- [x] Notes with `Confirmed` category are fed back into `Lufia1Constants.cs` - show a "Promote to constants" button that generates a code snippet the user can paste *(dropped: confirmed addresses were added by hand to the `Lufia1AddressMap` list and `Core/RamMap.cs`)*

---

## 3.5 - Integration Wiring

- [x] `MainWindow.xaml` - add **▶ Emulator** tab before Text Editor (make it tab index 0) *(changed: the Memory Monitor tab sits after Patch Manager)*
- [x] When ROM is loaded in the main app, offer to auto-launch it in the emulator *(changed: nothing launches by itself; Launch BizHawk writes the project's ROM and opens it, and a BizHawk that's already running loads it through `LufiaForge_LoadROM.txt`, which the tool checks once a second)*
- [x] Memory viewer panel docks to the right of the emulator view (resizable splitter)
- [x] Research notes panel docks below the memory viewer (collapsible) *(dropped: no notes panel)*
- [x] Emulator tab title shows current game state: **▶ Emulator** when running, **⏸ Paused** when paused *(changed: the top bar shows a red/green connected light, a status line and the frame number)*
- [x] Keyboard shortcut `F5` = pause/resume, `F6` = save state, `F7` = load state (forwarded to Snes9x) *(dropped: the docked BizHawk keeps its own hotkeys)*

---

## 3.6 - Known Technical Challenges

Written for the Snes9x plan. BizHawk's own memory API and the shared memory bridge took the place of the RAM base and VRAM mapping work.

| Challenge | Notes |
|-----------|-------|
| Snes9x RAM base address | Changes between builds and with ASLR. Use `ReadProcessMemory` + signature scan for the byte pattern at the start of the 128KB RAM block. Alternative: use a fixed offset from a known export if Snes9x exposes one. |
| HwndHost input focus | WPF and Win32 fight over keyboard input when hosting a foreign HWND. Must forward `WM_KEYDOWN`/`WM_KEYUP` messages manually via `PostMessage` to the Snes9x HWND. |
| Snes9x window reparenting | Some Snes9x builds use a child window for rendering (OpenGL/DirectDraw), not the top-level HWND. May need to enumerate child windows and reparent the correct one. |
| Poll rate vs UI thread | Never update the hex grid directly from the poll thread. Marshal snapshots to the UI thread via `Dispatcher.InvokeAsync` with a bounded queue - drop frames if the UI can't keep up. |
| VRAM address mapping | Snes9x stores VRAM differently internally than the SNES memory map. The VRAM base pointer in the process must be found separately from the RAM base. |
