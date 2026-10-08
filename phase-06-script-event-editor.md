# Phase 6 — Script Editor, Event Editor & Cutscene Sequencer

Full visual dialogue tree editor, raw script byte editor, event flag tracker, and cutscene sequencer.

> **Depends on:** Phase 2 (text encoding), Phase 3 (live event flag tracking from RAM), Phase 4 (disassembler to find script engine offsets)

**Status: ✅ Complete (2026-10-07).** Built under different names than planned. `EventScript` / `ScriptOp` / `EventCommands` (Core/Maps) are the model,
parser and serializer. The editor is `EventEditorPanel`, with Lines, Flow and Bytes views, the stage, the timeline and undo/redo. It's used in the
Events and Cutscenes tabs and in the map editor's pop-up. Live tracking reads the game through the BizHawk bridge. Differences are marked *(changed)*;
features the engine doesn't have are marked *(n/a)*.

---

## Research Sprint (do before writing any Phase 6 code)

- [x] Use the disassembler (Phase 4) to find the dialogue display routine
  - Trace from the known `0x0C` dictionary pointer handler back to the script dispatch loop
- [x] Find the event flag table in RAM using the memory viewer (Phase 3)
  - Event flags are almost certainly a bitfield array in work RAM (`$7E:xxxx`)
  - Watch flag changes during known story triggers (e.g. talking to Lufia for the first time)
- [x] Document the script opcode table — what opcodes exist beyond text and dictionary refs?
  - Look for: show sprite, play sound, move character, fade screen, set flag, branch on flag
- [x] Record all findings and promote confirmed addresses to `Lufia1Constants.cs` *(changed: findings are in `research-script-engine.md` and `research-event-commands.md`, since Phase 3 Research Notes don't exist yet)*

---

## 6.1 — Script Data Model

- [x] `ScriptEntry.cs` — one complete script block (NPC dialogue, cutscene, etc.) *(changed: `EventScript`; `LoadWhole` also follows jumps into code)*
  - [x] `int RomOffset`, `int ByteLength`
  - [x] `List<ScriptNode> Nodes` — parsed into a node graph
  - [x] `string RawHex` — original bytes for the raw editor
- [x] `ScriptNode.cs` — one node in the dialogue/event graph *(changed: `ScriptOp` per line plus `EventCommands` typed fields; jumps are `JumpRef`s to lines; the graph groups lines into `FlowBlock`s)*
  - [x] `ScriptNodeType Type` enum: `Dialogue`, `Choice`, `Branch`, `SetFlag`, `ClearFlag`, `PlaySound`, `MoveSprite`, `FadeScreen`, `WaitFrames`, `End`
  - [x] `List<ScriptNode> Children` — for branching logic
  - [x] `int? FlagId` — for flag set/clear/branch nodes
  - [x] `string? DialogueText` — for dialogue nodes
  - [x] `int? TargetOffset` — for jump/branch nodes
- [x] `ScriptParser.cs` — parse raw ROM bytes into a `ScriptEntry` node graph
  - [x] Handles known opcodes; unknown bytes become `RawByte` nodes so nothing is lost
  - [x] Detects branches and builds the tree structure
- [x] `ScriptSerializer.cs` — write a modified node graph back to ROM bytes
  - [x] Must preserve total byte length or warn about size overflow *(same size is written in place; a different size moves the event to the expanded ROM, tracked by the space table)*
  - [x] Recalculates all internal branch offsets after edits

---

## 6.2 — Visual Dialogue Tree Editor

- [x] `DialogueTreeView.xaml` — node-and-arrow canvas *(changed: `FlowGraphView`, the ⟲ Flow view; a card is a run of lines)*
  - [x] Each `ScriptNode` renders as a colored card:
    - Dialogue node = purple card with text preview
    - Choice node = gold card with branch count
    - Flag set/clear = blue card with flag ID
    - End node = red rounded cap
  - [x] Arrows drawn between connected nodes (Bezier curves)
  - [x] Pan canvas with middle mouse drag
  - [x] Zoom with scroll wheel *(Ctrl + wheel, as in the other map views)*
  - [x] Click a node to select it and open the node editor panel
  - [x] Drag nodes to reposition them (layout is cosmetic, does not affect ROM order)
  - [x] Right-click canvas: Add node (with type submenu)
  - [x] Right-click node: Delete node, Duplicate node, Insert node after
  - [x] Connect nodes by dragging from an output port to an input port
- [x] `NodeEditorPanel.xaml` — side panel for editing selected node properties *(changed: the picked card's lines are edited under the graph with the same line editor)*
  - [x] Dialogue node: text editor (reuses Phase 2 text encoding), character name dropdown, byte count indicator
  - [x] Choice node: list of choice text strings, each with a connected branch *(the answers are in the next text box; each answer is a jump with its own port)*
  - [x] Flag node: flag ID input with name lookup from research notes
  - [x] Branch node: condition (flag set / flag clear / always), true target, false target
  - [x] WaitFrames node: frame count input
- [x] Auto-layout button — arranges nodes in a left-to-right tree layout automatically
- [x] Minimap — small overview of the whole graph in the corner

---

## 6.3 — Raw Script Byte Editor

- [x] `RawScriptView.xaml` — hex + decoded view of script bytes *(changed: `RawBytesView`, the ⬚ Bytes view; command bytes in the Lines view only appear with "Show bytes")*
  - [x] Left column: raw hex bytes (editable)
  - [x] Right column: decoded meaning of each byte/sequence (read-only, auto-updates)
  - [x] Color coding: dialogue bytes = white, control codes = gold, unknown = orange, jump targets = underlined
  - [x] Sync with dialogue tree — selecting a node in the tree highlights its bytes in the raw editor
  - [x] Insert / delete bytes with automatic length recalculation warning
  - [x] Undo / redo (minimum 20 levels) *(50 levels, for the whole event editor)*

---

## 6.4 — Event Flag Tracker

- [x] `EventFlag.cs` — model: `int Id`, `int RamAddress`, `int BitIndex`, `string Name`, `string Description`, `bool CurrentValue`
- [x] `EventFlagStore.cs` — manages the known flag list
  - [x] Load/save from `<romfilename>.lfflags.json` *(user flags; story flag names stay in the shared flag-name store)*
  - [x] `GetAll() -> List<EventFlag>`
  - [x] `GetByRamAddress(int address, int bit) -> EventFlag?`
  - [x] Import flag list from CSV
- [x] `EventFlagTrackerView.xaml` — live flag panel (requires Phase 3 memory polling)
  - [x] Table columns: flag ID, name, current value (✅/❌), RAM address, bit index, description
  - [x] Values update live *(changed: polls the BizHawk bridge every 100 ms; bit masks come from the engine's table at `$032B`)*
  - [x] Green flash when a flag turns ON, red flash when it turns OFF
  - [x] Filter: show all / show only changed / show only ON flags
  - [x] Add flag button — enter RAM address and bit index, auto-reads current value
  - [x] When a flag changes, log the change with timestamp and frame number to the Event Log panel
- [x] `EventLogPanel.xaml` — scrolling list of flag change events
  - [x] Columns: frame, timestamp, flag name, old value, new value
  - [x] Auto-scroll to latest event (with toggle)
  - [x] Export event log as CSV
  - [x] Clear log button

---

## 6.5 — Cutscene Sequencer

- [x] `CutsceneSequence.cs` — model for one cutscene *(changed: the event script is the cutscene; `CutsceneTimeline.Build` places its lines in time)*
  - [x] `List<CutsceneEvent> Events` — ordered list of timed events
  - [x] `int RomOffset`, `string Name`
- [x] `CutsceneEvent.cs` — one event in the sequence *(changed: `TimelineItem` with line, start, duration, blocking, track and kind; parameters are the command's typed fields)*
  - [x] `CutsceneEventType Type` enum: `ShowDialogue`, `MoveSprite`, `PlaySound`, `FadeScreen`, `WaitFrames`, `SetCameraTarget`, `TriggerAnimation`
  - [x] `int StartFrame` — when this event fires relative to cutscene start
  - [x] `int DurationFrames` — how long it lasts (for timed events)
  - [x] `Dictionary<string, object> Parameters` — event-specific data
- [x] `CutsceneSequencerView.xaml` — timeline editor
  - [x] Horizontal timeline: X axis = frames, Y axis = tracks (one per sprite/channel)
  - [x] Each event renders as a colored block on its track, sized by duration
  - [x] Drag blocks to move them in time
  - [x] Resize blocks by dragging their right edge
  - [x] Click a block to edit its parameters in the side panel
  - [x] Add track button (sprite, sound, camera, dialogue) *(changed: tracks come from the lines; "＋ Add at playhead" adds a line of any kind there)*
  - [x] Playhead that moves as the emulator runs *("Follow the game" jumps to the line BizHawk is running; the stage preview moves it frame by frame)*
  - [x] Play / pause / rewind controls
  - [x] Zoom in/out on the timeline (frames per pixel)
- [x] `CutsceneEventPanel.xaml` — parameters editor for selected event *(changed: clicking a block selects its line in the line editor)*
  - [x] Dynamic form that shows relevant fields based on event type
  - [x] ShowDialogue: text, speaker *(portrait n/a: Lufia 1 text boxes have none)*
  - [x] MoveSprite: who, path steps / target X/Y, movement speed *(easing n/a: the engine moves at a constant speed)*
  - [x] PlaySound: sound effect ID with name lookup
  - [x] FadeScreen: direction (in/out), duration *(colour n/a: fades go to and from black)*

---

## 6.6 — Known Technical Challenges

| Challenge | Notes |
|-----------|-------|
| Script engine unknown | Lufia 1's script engine opcodes are undocumented. The research sprint above is mandatory before 6.1 can be started. Budget 4–8 hours of disassembler research. |
| Node graph serialization | After editing a dialogue tree, writing bytes back to ROM while preserving all internal jump offsets is non-trivial. Start with fixed-size edits only (replacing text of the same byte length) before attempting tree restructuring. |
| Cutscene format unknown | Cutscene data format depends entirely on what the research sprint finds. The sequencer UI is built generically; event types get filled in as the format is reverse engineered. |
| Flag table location | The event flag bitfield table address in RAM must be found via memory watching during gameplay. This is a Phase 3 research task that feeds into Phase 6. |
| Script vs map events | Some events may be triggered by map collision data rather than the script engine. These will appear as separate event systems. Map-triggered events are addressed in Phase 7. |
