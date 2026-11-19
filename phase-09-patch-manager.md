# Phase 9 - IPS Patch Manager UI

Full UI wrapper around the existing `IpsHandler.cs` core. Apply patches, preview diffs, export your hack, and manage patch history.

> **Core logic already built:** `IpsHandler.cs` (Phase 1) handles apply and export. This phase adds the UI and enhancements.

**Status: ✅ Complete (2026-11-17).** Built as the Patch Manager tab (`Modules/PatchManager`), which grew into more than an IPS tool:
one-click built-in patches (`BuiltInPatch`, the Lufia II patches and the fan hacks in `Core/CommunityPatches`), the Lufia II font,
the ROM size, and an IPS patches card to load, apply and export. Applying patches the open ROM in memory, so it's one Undo step
in `RomHistory` and the change goes into the project (`.lfproj`) on Save Project. Writing ROM files is File > Export ROM.
Differences from the plan are marked *(changed)*; things that weren't needed are marked *(dropped)*.

---

## 9.1 - Patch Manager UI

- [x] `PatchManagerView.xaml`
  - [x] Two-panel layout: Apply Patch (left) | Export Patch (right) *(changed: one scrolling page of cards: the built-in patch groups, "Look and ROM", then an "IPS patches" card with Load, Apply and Export buttons)*

### Apply Panel
- [x] Browse for `.ips` file button + drag-and-drop support *(changed: a "Load IPS patch..." button with a file dialog; no drag-and-drop)*
- [x] Loaded patch info display:
  - [x] File name, file size
  - [x] Record count, total bytes changed, largest single record *(changed: record count and bytes affected are shown; no largest record)*
  - [x] Warning badge if any record overlaps a critical region (SNES header, dictionary, pointer tables) *(changed: no badge. On apply, a warning (default No) if the patch overwrites or cuts off data Lufia Forge keeps in the expanded space (`ExpansionSpace` for Lufia 1, `L2Rom.Spaces` for Lufia II), and a question if the ROM size changes)*
- [x] Diff preview list: *(changed: a "Patch records" list in an expander)*
  - [x] Columns: file offset, SNES address, byte count, old bytes (hex), new bytes (hex) *(changed: offset, size and type (data or RLE fill byte) from `IpsPatchRecord`; no SNES address or old/new bytes)*
  - [x] Color code rows: green = new data, red = zeroed data, yellow = modified data *(dropped: the list doesn't compare with the ROM, so there's nothing to color by)*
  - [x] Sortable by offset or size *(dropped: records show in the patch file's order)*
  - [x] Click a row to jump to that offset in the hex viewer (if implemented) or copy address *(dropped: there's no ROM hex viewer; the list is read-only)*
- [x] Apply to copy button - always creates a new file, never overwrites original *(changed: "Apply it to the ROM" patches the open ROM in memory after a confirm, and Undo takes it back. The starting ROM is never written: the project keeps the change and File > Export ROM writes new files)*
  - [x] SaveFileDialog defaults to `<originalname>_patched.sfc` on the Desktop *(changed: no save dialog on apply; File > Export ROM As... or Export ROM (dated copy) writes the file)*
- [x] Apply and reload button - applies to copy then loads the result as the active ROM in Lufia Forge *(changed: there's one apply, and it always reloads every tab from the patched ROM through `MainViewModel.ReloadModules`)*
- [x] Safe mode toggle - blocks apply if any record overlaps critical regions *(changed: no toggle; the overwrite warning above stops the apply unless you answer Yes)*

### Export Panel
- [x] Session summary: lists all changes made in this session across all modules (text edits, tile edits, map edits, stat edits) *(changed: not in the Patch Manager. Edit > History lists the `RomHistory` steps from every tab, Save Project adds them to the project's dated change list (`LufiaProject.Changes`), and the ROM info panel shows what's in the ROM through `RomEdits.Describe`)*
- [x] Change list columns: module, description, offset, byte count *(changed: History shows each step's description and time; no offset or byte columns)*
- [x] Export as IPS button
  - [x] Diffs current `RomBuffer` against the original file on disk *(it asks for the clean original ROM, drops an SMC header and fixes the checksum the way a save does)*
  - [x] Runs the record merge pass (merges adjacent changed regions into fewer larger records) *(dropped: one record per changed run, see 9.2)*
  - [x] SaveFileDialog defaults to `<romname>_hack_<timestamp>.ips` on the Desktop *(changed: defaults to `Lufia1_patch_<yyyyMMdd_HHmmss>.ips` (or `Lufia2_`) on the Desktop)*
- [x] Export as modified ROM button - saves full ROM file with all changes applied *(changed: File > Export ROM As... and Export ROM (dated copy) in the main menu, `MainViewModel.SaveRomAs` / `SaveRom`)*

---

## 9.2 - IpsHandler Enhancements

- [x] Add record merge pass to `IpsHandler.ExportPatch()` *(dropped: one record per changed run works with every patcher)*
  - [x] Current implementation creates one record per changed byte run
  - [x] Merge pass: combine adjacent records that are within 8 bytes of each other into one larger record (reduces file size and record count) *(dropped: same reason. Bytes past the original's end are written as RLE records for runs of 8 or more and data records for the rest)*
- [x] Add critical region overlap detection *(changed: checks the blocks Lufia Forge stored in the expanded space, see 9.1)*
  - [x] `CheckOverlap(byte[] patchData, out List<string> warnings) -> bool` *(changed: no separate method; the check is in `PatchManagerViewModel.ApplyPatch` and compares the patched bytes with the ROM)*
  - [x] Checks against: SNES header region, dictionary region, any user-bookmarked "do not patch" ranges *(changed: checks the moved maps, scripts, resources, patch routines and the space table in the expanded space; the header checksum is fixed on every save and export anyway)*
- [x] Add BPS patch format support (modern alternative to IPS, supports larger ROMs and includes checksum) *(dropped: IPS covers every ROM size the app writes, with the truncate size after EOF and a workaround for the 0x454F46 offset)*
  - [x] `ExportBps(RomBuffer original, RomBuffer modified, string outputPath)` *(dropped: no BPS)*
  - [x] `ApplyBps(string bpsPath, RomBuffer rom) -> byte[]` *(dropped: no BPS)*

---

## 9.3 - Patch History

- [x] `PatchHistoryStore.cs` - log of all patches applied in this project *(changed: no separate store. An applied IPS patch is a `RomHistory` step and a line `IPS patch "<name>" applied` in the project's change list; built-in patches get "Added: <name>" lines)*
  - [x] Load/save from `<romfilename>.lfpatches.json` *(changed: saved in `project.json` inside the `.lfproj`)*
  - [x] Each entry: patch filename, date applied, record count, bytes changed, MD5 of patch file *(changed: date and time plus the patch's file name; no record count, bytes or MD5)*
- [x] `PatchHistoryPanel.xaml` - list of previously applied patches *(changed: Edit > History, the newest 40 steps; click one to go back to it)*
  - [x] Columns: date, filename, records, bytes *(changed: the step's description and time)*
  - [x] Re-apply button - re-applies the patch from its original file path *(dropped: Redo puts back an undone patch, or load the .ips again)*
  - [x] Remove from history (does not undo changes, just removes the log entry) *(dropped: the change list is the project's record of what was done; Undo takes a patch out)*

---

## 9.4 - Known Technical Challenges

| Challenge | Notes |
|-----------|-------|
| BPS checksum | BPS format includes source and target ROM checksums (CRC32). The exporter must compute these correctly or the patch will be rejected by standard BPS patchers. (No BPS was built, see 9.2.) |
| Pointer table corruption | A patch that moves text blocks without updating pointer tables will corrupt the game. The critical region overlap warning only partially addresses this: warn aggressively and document the limitation. |
| ROM expansion patches | Patches that expand the ROM beyond the original size require the IPS EOF marker to be placed at the correct offset. Confirm that `IpsHandler` handles this correctly when the patched output is larger than the input. (Handled: export writes the bytes past the original's end and the final size after EOF; apply grows the ROM and honours that size.) |
