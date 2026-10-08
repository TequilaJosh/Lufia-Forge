# Phase 4 — 65816 Disassembler + Research Tools

Full 65816 disassembler with bookmark system, live PC tracking from the emulator, and a cross-reference builder to map out the entire ROM's call graph.

> **Depends on:** Phase 3 (emulator + memory reader) for live PC tracking

**Status: ✅ Complete (closed 2026-10-07).** Built as the Disassembler tab (`Modules/Disassembler`). Live PC tracking uses the
BizHawk bridge (`BizHawkBridge.CpuSnapshotReady`) instead of an embedded emulator. Differences from the original plan are
marked *(changed)* or *(deferred)* below. The disassembler was then used to decode the whole event-script engine
(see `research-script-engine.md` and `research-event-commands.md`).

---

## 4.1 — Core Disassembler Engine

- [x] `Cpu65816.cs` — full WDC 65816 instruction decoder
  - [x] All 256 opcodes with correct mnemonic, addressing mode, and base cycle count (cycles shown in the line tooltip and the `.asm` export)
  - [x] Variable operand size: immediate operands are 1 byte when M=1 (8-bit acc) or 2 bytes when M=0 (16-bit acc); same for X flag and index registers
  - [x] `DecodeInstruction(RomBuffer rom, int fileOffset, CpuState state) -> DisassemblyLine`
  - [x] `CpuState` struct: `bool M, bool X, int DirectPage, int DataBank` — affects operand size and address display
  - [x] All addressing modes: Implied, Immediate, Direct Page, Absolute, Absolute Long, Relative, Relative Long, Indexed (all variants), Indirect, Indirect Long, Stack-relative
- [x] `DisassemblyLine.cs` — model for one decoded instruction
  - [x] `int FileOffset`, `int SnesAddress`, `byte[] RawBytes`
  - [x] `string Mnemonic`, `string Operand`, `string? Comment`
  - [x] `int? JumpTarget` — resolved target address for branches and jumps
  - [x] `bool IsJump`, `bool IsCall`, `bool IsReturn`, `bool IsConditional`
- [x] `LinearDisassembler.cs` — sweep disassembler that walks forward from a start offset
  - [x] `Disassemble(RomBuffer rom, int startOffset, int byteCount, CpuState initialState) -> List<DisassemblyLine>`
  - [x] Stops at `BRK`, `RTI`, `RTS`, `RTL` or when `byteCount` is exhausted
  - [x] Emits a `[DATA?]` warning line when it hits a byte sequence that doesn't decode cleanly

---

## 4.2 — Disassembler UI

- [x] `DisassemblerView.xaml` — main disassembler tab
  - [x] Address input: file offset or SNES address *(changed: LoROM only. Lufia is a LoROM game, so there is no HiROM toggle)*
  - [x] Byte count / end address input
  - [x] M flag and X flag checkboxes (default both unchecked = 16-bit mode)
  - [x] Disassembly output with syntax coloring *(changed: a virtualized list with colored columns instead of a `RichTextBox`; it stays fast on large ranges)*
    - Mnemonic = gold
    - Operand address = light purple
    - Immediate values = white
    - Comments = dim green
    - Jump targets = underlined, clickable
  - [x] Line numbers shown as SNES addresses in the gutter
  - [x] Right-click context menu on any line:
    - Add bookmark / label
    - Copy line
    - Copy address
    - Follow jump target
    - Mark as data (removes from disassembly)
- [x] `DisassemblerViewModel.cs`
  - [x] `DisassembleCommand` — runs `LinearDisassembler` and populates output
  - [x] `FollowJumpCommand(DisassemblyLine line)` — re-disassembles from the jump target
  - [x] Navigation history stack — back/forward buttons like a browser
  - [x] Export current view as `.asm` text file
- [x] Live PC tracking (requires Phase 3)
  - [x] Subscribe to CPU snapshots *(changed: `BizHawkBridge.CpuSnapshotReady`, fed by the BizHawk Lua bridge)*
  - [x] Read CPU registers from snapshot (PC, P flags, D, DB)
  - [x] Highlight the current instruction line in gold
  - [x] Auto-scroll to keep current PC visible (with toggle to disable auto-scroll)
  - [x] "Follow PC" mode — continuously disassembles around the current program counter

---

## 4.3 — Bookmark System

- [x] `BookmarkStore.cs` — persistent address labels
  - [x] Load/save from `<romfilename>.lfbookmarks.json`
  - [x] `Add(int snesAddress, string label, string? comment)`
  - [x] `GetByAddress(int snesAddress) -> Bookmark?`
  - [x] `GetAll() -> List<Bookmark>`
  - [x] Auto-applied to disassembly output — any address with a bookmark shows its label as a comment
- [x] `Bookmark.cs` model: `SnesAddress`, `Label`, `Comment`, `Color` (for gutter color coding)
- [x] Bookmark panel: dockable list of all bookmarks, click to jump to address
- [ ] Bookmarks shared with Research Notes from Phase 3 — `Confirmed` notes auto-create bookmarks *(deferred: Phase 3's Research Notes don't exist yet; research lives in the `research-*.md` files)*

---

## 4.4 — Cross-Reference Builder

- [x] `CrossReferenceBuilder.cs` — scans the entire ROM and builds a call/jump graph
  - [x] Walk every LoROM bank, attempt to disassemble, collect all `JSR`, `JSL`, `JMP`, `JML`, `BRA`, `BRL` targets
  - [x] Output: `Dictionary<int, List<int>>` — address -> list of addresses that call/jump to it
  - [x] Run as a background task with progress reporting (the full ROM scan takes about 0.1 s)
  - [x] Indirect jumps/calls (`JMP (a)`, `JMP (a,X)`, `JML [a]`, `JSR (a,X)`) are listed as unresolved, and the count is shown after a build
- [x] Cross-ref panel in the disassembler tab *(changed: part of `DisassemblerView.xaml`, not a separate view)*
  - [x] For the currently selected address: "Called from" list + "Calls to" list
  - [x] Click any entry to jump to that address in the disassembler
- [x] Integrate with bookmark labels — known addresses show their label instead of raw hex in the xref list

---

## 4.5 — Known Technical Challenges

| Challenge | Notes |
|-----------|-------|
| M/X flag state | A linear disassembler cannot track `REP`/`SEP` instructions that change M/X mid-routine. The UI must let the user set initial state manually. Flag changes within a disassembled block are detected and annotated as warnings. |
| Data regions | Graphics, map data, and text will disassemble as garbage opcodes. The user marks regions as data manually via right-click. Marked data regions are stored in `lfbookmarks.json` and skipped during cross-reference building. |
| Indirect jumps | `JMP ($0000,X)` style indirect jumps cannot be statically resolved. The cross-reference builder logs these as unresolved and the live PC tracker fills them in at runtime. |
| Bank boundaries | `JSR` only jumps within the current bank. `JSL` is the 24-bit form. Display both correctly and never add `0x8000` twice when translating. |
