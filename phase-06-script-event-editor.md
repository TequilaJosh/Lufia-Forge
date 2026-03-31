# Phase 6 — Script Editor, Event Editor & Cutscene Sequencer

Full visual dialogue tree editor, raw script byte editor, event flag tracker, and cutscene sequencer.

> **Depends on:** Phase 2 (text encoding), Phase 3 (live event flag tracking from RAM), Phase 4 (disassembler to find script engine offsets)

---

## Research Sprint (do before writing any Phase 6 code)

- [ ] Use the disassembler (Phase 4) to find the dialogue display routine
  - Trace from the known `0x0C` dictionary pointer handler back to the script dispatch loop
- [ ] Find the event flag table in RAM using the memory viewer (Phase 3)
  - Event flags are almost certainly a bitfield array in work RAM (`$7E:xxxx`)
  - Watch flag changes during known story triggers (e.g. talking to Lufia for the first time)
- [ ] Document the script opcode table — what opcodes exist beyond text and dictionary refs?
  - Look for: show sprite, play sound, move character, fade screen, set flag, branch on flag
- [ ] Record all findings in Research Notes (Phase 3) and promote confirmed addresses to `Lufia1Constants.cs`

---

## 6.1 — Script Data Model

- [ ] `ScriptEntry.cs` — one complete script block (NPC dialogue, cutscene, etc.)
  - [ ] `int RomOffset`, `int ByteLength`
  - [ ] `List<ScriptNode> Nodes` — parsed into a node graph
  - [ ] `string RawHex` — original bytes for the raw editor
- [ ] `ScriptNode.cs` — one node in the dialogue/event graph
  - [ ] `ScriptNodeType Type` enum: `Dialogue`, `Choice`, `Branch`, `SetFlag`, `ClearFlag`, `PlaySound`, `MoveSprite`, `FadeScreen`, `WaitFrames`, `End`
  - [ ] `List<ScriptNode> Children` — for branching logic
  - [ ] `int? FlagId` — for flag set/clear/branch nodes
  - [ ] `string? DialogueText` — for dialogue nodes
  - [ ] `int? TargetOffset` — for jump/branch nodes
- [ ] `ScriptParser.cs` — parse raw ROM bytes into a `ScriptEntry` node graph
  - [ ] Handles known opcodes; unknown bytes become `RawByte` nodes so nothing is lost
  - [ ] Detects branches and builds the tree structure
- [ ] `ScriptSerializer.cs` — write a modified node graph back to ROM bytes
  - [ ] Must preserve total byte length or warn about size overflow
  - [ ] Recalculates all internal branch offsets after edits

---

## 6.2 — Visual Dialogue Tree Editor

- [ ] `DialogueTreeView.xaml` — node-and-arrow canvas
  - [ ] Each `ScriptNode` renders as a colored card:
    - Dialogue node = purple card with text preview
    - Choice node = gold card with branch count
    - Flag set/clear = blue card with flag ID
    - End node = red rounded cap
  - [ ] Arrows drawn between connected nodes (Bezier curves)
  - [ ] Pan canvas with middle mouse drag
  - [ ] Zoom with scroll wheel
  - [ ] Click a node to select it and open the node editor panel
  - [ ] Drag nodes to reposition them (layout is cosmetic, does not affect ROM order)
  - [ ] Right-click canvas: Add node (with type submenu)
  - [ ] Right-click node: Delete node, Duplicate node, Insert node after
  - [ ] Connect nodes by dragging from an output port to an input port
- [ ] `NodeEditorPanel.xaml` — side panel for editing selected node properties
  - [ ] Dialogue node: text editor (reuses Phase 2 text encoding), character name dropdown, byte count indicator
  - [ ] Choice node: list of choice text strings, each with a connected branch
  - [ ] Flag node: flag ID input with name lookup from research notes
  - [ ] Branch node: condition (flag set / flag clear / always), true target, false target
  - [ ] WaitFrames node: frame count input
- [ ] Auto-layout button — arranges nodes in a left-to-right tree layout automatically
- [ ] Minimap — small overview of the whole graph in the corner

---

## 6.3 — Raw Script Byte Editor

- [ ] `RawScriptView.xaml` — hex + decoded view of script bytes
  - [ ] Left column: raw hex bytes (editable)
  - [ ] Right column: decoded meaning of each byte/sequence (read-only, auto-updates)
  - [ ] Color coding: dialogue bytes = white, control codes = gold, unknown = orange, jump targets = underlined
  - [ ] Sync with dialogue tree — selecting a node in the tree highlights its bytes in the raw editor
  - [ ] Insert / delete bytes with automatic length recalculation warning
  - [ ] Undo / redo (minimum 20 levels)

---

## 6.4 — Event Flag Tracker

- [ ] `EventFlag.cs` — model: `int Id`, `int RamAddress`, `int BitIndex`, `string Name`, `string Description`, `bool CurrentValue`
- [ ] `EventFlagStore.cs` — manages the known flag list
  - [ ] Load/save from `<romfilename>.lfflags.json`
  - [ ] `GetAll() -> List<EventFlag>`
  - [ ] `GetByRamAddress(int address, int bit) -> EventFlag?`
  - [ ] Import flag list from CSV
- [ ] `EventFlagTrackerView.xaml` — live flag panel (requires Phase 3 memory polling)
  - [ ] Table columns: flag ID, name, current value (✅/❌), RAM address, bit index, description
  - [ ] Values update live from `MemoryPoller.SnapshotReady`
  - [ ] Green flash when a flag turns ON, red flash when it turns OFF
  - [ ] Filter: show all / show only changed / show only ON flags
  - [ ] Add flag button — enter RAM address and bit index, auto-reads current value
  - [ ] When a flag changes, log the change with timestamp and frame number to the Event Log panel
- [ ] `EventLogPanel.xaml` — scrolling list of flag change events
  - [ ] Columns: frame, timestamp, flag name, old value, new value
  - [ ] Auto-scroll to latest event (with toggle)
  - [ ] Export event log as CSV
  - [ ] Clear log button

---

## 6.5 — Cutscene Sequencer

- [ ] `CutsceneSequence.cs` — model for one cutscene
  - [ ] `List<CutsceneEvent> Events` — ordered list of timed events
  - [ ] `int RomOffset`, `string Name`
- [ ] `CutsceneEvent.cs` — one event in the sequence
  - [ ] `CutsceneEventType Type` enum: `ShowDialogue`, `MoveSprite`, `PlaySound`, `FadeScreen`, `WaitFrames`, `SetCameraTarget`, `TriggerAnimation`
  - [ ] `int StartFrame` — when this event fires relative to cutscene start
  - [ ] `int DurationFrames` — how long it lasts (for timed events)
  - [ ] `Dictionary<string, object> Parameters` — event-specific data
- [ ] `CutsceneSequencerView.xaml` — timeline editor
  - [ ] Horizontal timeline: X axis = frames, Y axis = tracks (one per sprite/channel)
  - [ ] Each event renders as a colored block on its track, sized by duration
  - [ ] Drag blocks to move them in time
  - [ ] Resize blocks by dragging their right edge
  - [ ] Click a block to edit its parameters in the side panel
  - [ ] Add track button (sprite, sound, camera, dialogue)
  - [ ] Playhead that moves as the emulator runs (requires Phase 3 frame counter)
  - [ ] Play / pause / rewind controls
  - [ ] Zoom in/out on the timeline (frames per pixel)
- [ ] `CutsceneEventPanel.xaml` — parameters editor for selected event
  - [ ] Dynamic form that shows relevant fields based on event type
  - [ ] ShowDialogue: text (links to Phase 2 text editor), speaker, portrait
  - [ ] MoveSprite: sprite ID, target X/Y, movement speed, easing
  - [ ] PlaySound: sound effect ID with name lookup
  - [ ] FadeScreen: direction (in/out), duration frames, color

---

## 6.6 — Known Technical Challenges

| Challenge | Notes |
|-----------|-------|
| Script engine unknown | Lufia 1's script engine opcodes are undocumented. The research sprint above is mandatory before 6.1 can be started. Budget 4–8 hours of disassembler research. |
| Node graph serialization | After editing a dialogue tree, writing bytes back to ROM while preserving all internal jump offsets is non-trivial. Start with fixed-size edits only (replacing text of the same byte length) before attempting tree restructuring. |
| Cutscene format unknown | Cutscene data format depends entirely on what the research sprint finds. The sequencer UI is built generically; event types get filled in as the format is reverse engineered. |
| Flag table location | The event flag bitfield table address in RAM must be found via memory watching during gameplay. This is a Phase 3 research task that feeds into Phase 6. |
| Script vs map events | Some events may be triggered by map collision data rather than the script engine. These will appear as separate event systems. Map-triggered events are addressed in Phase 7. |
