# Phase 4 — 65816 Disassembler + Research Tools

Full 65816 disassembler with bookmark system, live PC tracking from the emulator, and a cross-reference builder to map out the entire ROM's call graph.

> **Depends on:** Phase 3 (emulator + memory reader) for live PC tracking

---

## 4.1 — Core Disassembler Engine

- [ ] `Cpu65816.cs` — full WDC 65816 instruction decoder
  - [ ] All 256 opcodes with correct mnemonic, addressing mode, and base cycle count
  - [ ] Variable operand size: immediate operands are 1 byte when M=1 (8-bit acc) or 2 bytes when M=0 (16-bit acc); same for X flag and index registers
  - [ ] `DecodeInstruction(RomBuffer rom, int fileOffset, CpuState state) -> DisassemblyLine`
  - [ ] `CpuState` struct: `bool M, bool X, int DirectPage, int DataBank` — affects operand size and address display
  - [ ] All addressing modes: Implied, Immediate, Direct Page, Absolute, Absolute Long, Relative, Relative Long, Indexed (all variants), Indirect, Indirect Long, Stack-relative
- [ ] `DisassemblyLine.cs` — model for one decoded instruction
  - [ ] `int FileOffset`, `int SnesAddress`, `byte[] RawBytes`
  - [ ] `string Mnemonic`, `string Operand`, `string? Comment`
  - [ ] `int? JumpTarget` — resolved target address for branches and jumps
  - [ ] `bool IsJump`, `bool IsCall`, `bool IsReturn`, `bool IsConditional`
- [ ] `LinearDisassembler.cs` — sweep disassembler that walks forward from a start offset
  - [ ] `Disassemble(RomBuffer rom, int startOffset, int byteCount, CpuState initialState) -> List<DisassemblyLine>`
  - [ ] Stops at `BRK`, `RTI`, `RTS`, `RTL` or when `byteCount` is exhausted
  - [ ] Emits a `[DATA?]` warning line when it hits a byte sequence that doesn't decode cleanly

---

## 4.2 — Disassembler UI

- [ ] `DisassemblerView.xaml` — main disassembler tab
  - [ ] Address input: file offset or SNES address, with LoROM/HiROM toggle
  - [ ] Byte count / end address input
  - [ ] M flag and X flag checkboxes (default both unchecked = 16-bit mode)
  - [ ] Disassembly output: `RichTextBox` with syntax coloring
    - Mnemonic = gold
    - Operand address = light purple
    - Immediate values = white
    - Comments = dim green
    - Jump targets = underlined, clickable
  - [ ] Line numbers shown as SNES addresses in the gutter
  - [ ] Right-click context menu on any line:
    - Add bookmark / label
    - Copy line
    - Copy address
    - Follow jump target
    - Mark as data (removes from disassembly)
- [ ] `DisassemblerViewModel.cs`
  - [ ] `DisassembleCommand` — runs `LinearDisassembler` and populates output
  - [ ] `FollowJumpCommand(DisassemblyLine line)` — re-disassembles from the jump target
  - [ ] Navigation history stack — back/forward buttons like a browser
  - [ ] Export current view as `.asm` text file
- [ ] Live PC tracking (requires Phase 3)
  - [ ] Subscribe to `MemoryPoller.SnapshotReady`
  - [ ] Read CPU registers from snapshot (PC, P flags, D, DB)
  - [ ] Highlight the current instruction line in gold
  - [ ] Auto-scroll to keep current PC visible (with toggle to disable auto-scroll)
  - [ ] "Follow PC" mode — continuously disassembles around the current program counter

---

## 4.3 — Bookmark System

- [ ] `BookmarkStore.cs` — persistent address labels
  - [ ] Load/save from `<romfilename>.lfbookmarks.json`
  - [ ] `Add(int snesAddress, string label, string? comment)`
  - [ ] `GetByAddress(int snesAddress) -> Bookmark?`
  - [ ] `GetAll() -> List<Bookmark>`
  - [ ] Auto-applied to disassembly output — any address with a bookmark shows its label as a comment
- [ ] `Bookmark.cs` model: `SnesAddress`, `Label`, `Comment`, `Color` (for gutter color coding)
- [ ] Bookmark panel: dockable list of all bookmarks, click to jump to address
- [ ] Bookmarks shared with Research Notes from Phase 3 — `Confirmed` notes auto-create bookmarks

---

## 4.4 — Cross-Reference Builder

- [ ] `CrossReferenceBuilder.cs` — scans the entire ROM and builds a call/jump graph
  - [ ] Walk every LoROM bank, attempt to disassemble, collect all `JSR`, `JSL`, `JMP`, `JML`, `BRA`, `BRL` targets
  - [ ] Output: `Dictionary<int, List<int>>` — address -> list of addresses that call/jump to it
  - [ ] Run as a background task with progress reporting (full ROM scan takes a few seconds)
- [ ] `CrossReferenceView.xaml` — cross-ref panel in the disassembler tab
  - [ ] For the currently selected address: "Called from" list + "Calls to" list
  - [ ] Click any entry to jump to that address in the disassembler
- [ ] Integrate with bookmark labels — known addresses show their label instead of raw hex in the xref list

---

## 4.5 — Known Technical Challenges

| Challenge | Notes |
|-----------|-------|
| M/X flag state | A linear disassembler cannot track `REP`/`SEP` instructions that change M/X mid-routine. The UI must let the user set initial state manually. Flag changes within a disassembled block are detected and annotated as warnings. |
| Data regions | Graphics, map data, and text will disassemble as garbage opcodes. The user marks regions as data manually via right-click. Marked data regions are stored in `lfbookmarks.json` and skipped during cross-reference building. |
| Indirect jumps | `JMP ($0000,X)` style indirect jumps cannot be statically resolved. The cross-reference builder logs these as unresolved and the live PC tracker fills them in at runtime. |
| Bank boundaries | `JSR` only jumps within the current bank. `JSL` is the 24-bit form. Display both correctly and never add `0x8000` twice when translating. |
