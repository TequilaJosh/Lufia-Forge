# Phase 5 — Tile & Sprite Viewer

Render raw SNES graphics from ROM and live VRAM. Browse, inspect, and export tilesets, font tiles, and sprite sheets.

> **Depends on:** Phase 3 (live VRAM feed for real-time tile rendering)

---

## 5.1 — Tile Decoder Core

- [ ] `SnesTileDecoder.cs` — decode SNES tile formats to ARGB pixel arrays
  - [ ] `DecodeTile2bpp(byte[] data, int offset, Color[] palette) -> Color[64]`
  - [ ] `DecodeTile4bpp(byte[] data, int offset, Color[] palette) -> Color[64]`
  - [ ] `DecodeTile8bpp(byte[] data, int offset, Color[] palette) -> Color[64]`
  - [ ] `RenderTileGrid(byte[] source, int startOffset, int cols, int rows, BppMode mode, Color[] palette) -> WriteableBitmap`
  - [ ] `BppMode` enum: `TwoBpp`, `FourBpp`, `EightBpp`
- [ ] `SnesPalette.cs` — SNES BGR555 color format handling
  - [ ] `Color ParseBgr555(ushort raw)` — convert SNES 15-bit color to WPF `Color`
  - [ ] `Color[] LoadPaletteFromRom(RomBuffer rom, int fileOffset, int colorCount)`
  - [ ] `Color[] LoadPaletteFromCgram(byte[] cgram, int paletteIndex)` — read from live CGRAM snapshot

---

## 5.2 — Tile Viewer UI

- [ ] `TileViewerView.xaml`
  - [ ] Offset input (file offset or SNES address) with live jump
  - [ ] BPP mode selector: 2bpp / 4bpp / 8bpp
  - [ ] Palette selector: 0–15 (16 palettes), with color swatch preview for each
  - [ ] Source toggle: **ROM** (static) vs **Live VRAM** (from emulator snapshot)
  - [ ] Zoom slider: 1x / 2x / 4x / 8x
  - [ ] Grid overlay toggle (draws 8x8 tile boundaries)
  - [ ] Columns input: how many tiles wide to render (default 16)
  - [ ] Tile canvas: `Image` backed by `WriteableBitmap`, updates on any input change
  - [ ] Hover tooltip on tile: tile index, file offset, SNES address, raw 32 bytes as hex
  - [ ] Click a tile to select it and show full detail in the inspector panel
  - [ ] Export PNG button — saves visible tile grid at current zoom
  - [ ] Shortcut buttons: "Font tiles", "Sprite sheet", "UI tiles" — jumps to known offsets once discovered
- [ ] `TileViewerViewModel.cs`
  - [ ] Properties: `CurrentOffset`, `BppMode`, `PaletteIndex`, `ZoomLevel`, `ShowGrid`, `Columns`, `UseVram`
  - [ ] `RenderCommand` — triggers `SnesTileDecoder.RenderTileGrid`, writes to `WriteableBitmap`
  - [ ] Debounced re-render on input changes (don't re-render on every keystroke)
  - [ ] Subscribe to `MemoryPoller.SnapshotReady` when `UseVram = true` — update CGRAM palettes live
- [ ] `TileInspectorPanel.xaml` — side panel for selected tile
  - [ ] Shows: tile index, file offset, SNES address, BPP mode, byte count
  - [ ] Raw hex dump of the tile bytes
  - [ ] 8x8 zoomed pixel grid showing individual pixel palette indices

---

## 5.3 — Font Research Workflow

- [ ] Add "Find Font" workflow to help locate the font tile offset
  - [ ] Scan ROM for sequences of tiles that look like ASCII glyphs (heuristic: check for tile patterns consistent with letter shapes)
  - [ ] Present candidates as a list — user clicks each to preview it in the tile viewer
  - [ ] Once confirmed, save the font offset to `Lufia1Constants.cs` via the "Promote to constants" button
- [ ] Once font offset is confirmed, update `TextDecoder.cs` to render a font preview in the text editor

---

## 5.4 — Known Technical Challenges

| Challenge | Notes |
|-----------|-------|
| Palette location | SNES CGRAM is uploaded at runtime, not stored at a fixed ROM offset. Use the live VRAM feed from Phase 3 for accurate colors. For static ROM view, default to a greyscale palette until the user locates the correct palette data. |
| BPP mode detection | No header indicates which BPP mode a region uses. Wrong mode shows visual garbage. Default to 4bpp; let the user switch. |
| Render performance | Rendering 256+ tiles on every scroll/input change must stay under 16ms. Only decode tiles in the visible viewport. Use a `WriteableBitmap` and write pixel data directly — never use WPF drawing primitives per-tile. |
| Font offset unknown | Must be found empirically using the font research workflow above or via the disassembler tracing the text rendering routine. |
