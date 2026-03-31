# Phase 9 — IPS Patch Manager UI

Full UI wrapper around the existing `IpsHandler.cs` core. Apply patches, preview diffs, export your hack, and manage patch history.

> **Core logic already built:** `IpsHandler.cs` (Phase 1) handles apply and export. This phase adds the UI and enhancements.

---

## 9.1 — Patch Manager UI

- [ ] `PatchManagerView.xaml`
  - [ ] Two-panel layout: Apply Patch (left) | Export Patch (right)

### Apply Panel
- [ ] Browse for `.ips` file button + drag-and-drop support
- [ ] Loaded patch info display:
  - [ ] File name, file size
  - [ ] Record count, total bytes changed, largest single record
  - [ ] Warning badge if any record overlaps a critical region (SNES header, dictionary, pointer tables)
- [ ] Diff preview list:
  - [ ] Columns: file offset, SNES address, byte count, old bytes (hex), new bytes (hex)
  - [ ] Color code rows: green = new data, red = zeroed data, yellow = modified data
  - [ ] Sortable by offset or size
  - [ ] Click a row to jump to that offset in the hex viewer (if implemented) or copy address
- [ ] Apply to copy button — always creates a new file, never overwrites original
  - [ ] SaveFileDialog defaults to `<originalname>_patched.sfc` on the Desktop
- [ ] Apply and reload button — applies to copy then loads the result as the active ROM in Lufia Forge
- [ ] Safe mode toggle — blocks apply if any record overlaps critical regions

### Export Panel
- [ ] Session summary: lists all changes made in this session across all modules (text edits, tile edits, map edits, stat edits)
- [ ] Change list columns: module, description, offset, byte count
- [ ] Export as IPS button
  - [ ] Diffs current `RomBuffer` against the original file on disk
  - [ ] Runs the record merge pass (merges adjacent changed regions into fewer larger records)
  - [ ] SaveFileDialog defaults to `<romname>_hack_<timestamp>.ips` on the Desktop
- [ ] Export as modified ROM button — saves full ROM file with all changes applied

---

## 9.2 — IpsHandler Enhancements

- [ ] Add record merge pass to `IpsHandler.ExportPatch()`
  - [ ] Current implementation creates one record per changed byte run
  - [ ] Merge pass: combine adjacent records that are within 8 bytes of each other into one larger record (reduces file size and record count)
- [ ] Add critical region overlap detection
  - [ ] `CheckOverlap(byte[] patchData, out List<string> warnings) -> bool`
  - [ ] Checks against: SNES header region, dictionary region, any user-bookmarked "do not patch" ranges
- [ ] Add BPS patch format support (modern alternative to IPS, supports larger ROMs and includes checksum)
  - [ ] `ExportBps(RomBuffer original, RomBuffer modified, string outputPath)`
  - [ ] `ApplyBps(string bpsPath, RomBuffer rom) -> byte[]`

---

## 9.3 — Patch History

- [ ] `PatchHistoryStore.cs` — log of all patches applied in this project
  - [ ] Load/save from `<romfilename>.lfpatches.json`
  - [ ] Each entry: patch filename, date applied, record count, bytes changed, MD5 of patch file
- [ ] `PatchHistoryPanel.xaml` — list of previously applied patches
  - [ ] Columns: date, filename, records, bytes
  - [ ] Re-apply button — re-applies the patch from its original file path
  - [ ] Remove from history (does not undo changes, just removes the log entry)

---

## 9.4 — Known Technical Challenges

| Challenge | Notes |
|-----------|-------|
| BPS checksum | BPS format includes source and target ROM checksums (CRC32). The exporter must compute these correctly or the patch will be rejected by standard BPS patchers. |
| Pointer table corruption | A patch that moves text blocks without updating pointer tables will corrupt the game. The critical region overlap warning only partially addresses this — warn aggressively and document the limitation. |
| ROM expansion patches | Patches that expand the ROM beyond the original size require the IPS EOF marker to be placed at the correct offset. Confirm that `IpsHandler` handles this correctly when the patched output is larger than the input. |
