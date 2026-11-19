# Phase 5 - Tile & Sprite Viewer

Render raw SNES graphics from ROM and live VRAM. Browse, inspect, and export tilesets, font tiles, and sprite sheets.

> **Depends on:** Phase 3 (live VRAM feed for real-time tile rendering)

**Status: ✅ Complete (2026-10-09).** Built differently than planned. The Tile Viewer tab came out of the main window when the Cutscenes tab
took its place (2026-10-07); `TileViewerView` / `TileViewerViewModel` are still in the source but not shown. Graphics are viewed and edited in
Game Data instead: the Graphics tab (`GraphicsEditorViewModel`: any packed resource or raw bytes at an offset, 2/4/8bpp, map palettes, export and
import as an indexed PNG through `IndexedPng`), plus the Palettes and Sprites tabs. Lufia II has its own Graphics page (`L2GraphicsPage`, pictures
listed by `L2Gfx`) with a pixel painter. `SnesTileDecoder` and `SnesPalette` are shared by the map tilesets, NPC sprites and spell animations too.
There is no live VRAM view. Differences are marked *(changed)*; parts not built are marked *(dropped)*.

---

## 5.1 - Tile Decoder Core

- [x] `SnesTileDecoder.cs` - decode SNES tile formats to ARGB pixel arrays
  - [x] `DecodeTile2bpp(byte[] data, int offset, Color[] palette) -> Color[64]` *(changed: one `DecodeTile(data, offset, BitDepth)` returns 64 colour numbers and the caller applies the palette; `EncodeTile` does the reverse)*
  - [x] `DecodeTile4bpp(byte[] data, int offset, Color[] palette) -> Color[64]` *(changed: same `DecodeTile`, see above)*
  - [x] `DecodeTile8bpp(byte[] data, int offset, Color[] palette) -> Color[64]` *(changed: same `DecodeTile`, see above)*
  - [x] `RenderTileGrid(byte[] source, int startOffset, int cols, int rows, BppMode mode, Color[] palette) -> WriteableBitmap` *(changed: no separate method; `TileViewerViewModel.Render` and `GraphicsEditorViewModel.Indices` lay out the grid themselves)*
  - [x] `BppMode` enum: `TwoBpp`, `FourBpp`, `EightBpp` *(changed: `BitDepth` enum with `Bpp2`, `Bpp4`, `Bpp8`)*
- [x] `SnesPalette.cs` - SNES BGR555 color format handling
  - [x] `Color ParseBgr555(ushort raw)` - convert SNES 15-bit color to WPF `Color` *(changed: `Bgr555ToArgb32` returns a BGRA32 `uint` for bitmaps; the Palettes tab uses `PaletteSwatch.ToColor`/`FromRgb` for WPF colours)*
  - [x] `Color[] LoadPaletteFromRom(RomBuffer rom, int fileOffset, int colorCount)` *(changed: `ReadFromRom` returns `uint[]`; `Grayscale` gives the grey ramp)*
  - [x] `Color[] LoadPaletteFromCgram(byte[] cgram, int paletteIndex)` - read from live CGRAM snapshot *(dropped: no CGRAM feed; the game's palettes are read straight from the ROM)*

---

## 5.2 - Tile Viewer UI

- [x] `TileViewerView.xaml` *(changed: still in the source but no longer in the main window; the Game Data Graphics tab (`GraphicsEditorView`) does its job)*
  - [x] Offset input (file offset or SNES address) with live jump *(changed: hex file offset only, in "Raw bytes at an offset"; the Graphics tab redraws as you type)*
  - [x] BPP mode selector: 2bpp / 4bpp / 8bpp
  - [x] Palette selector: 0-15 (16 palettes), with color swatch preview for each *(changed: the Graphics tab picks grey, a map palette and row 0-7, or colours at a ROM offset; swatches are on the Palettes tab and the Lufia II Graphics page)*
  - [x] Source toggle: **ROM** (static) vs **Live VRAM** (from emulator snapshot) *(dropped: no VRAM feed; graphics are unpacked from the ROM's resources with `LufiaCompression`)*
  - [x] Zoom slider: 1x / 2x / 4x / 8x *(changed: the Graphics tab shows a fixed 2x; the Lufia II Graphics page zooms x1 to x24 with buttons or Ctrl + mouse wheel)*
  - [x] Grid overlay toggle (draws 8x8 tile boundaries) *(changed: only the Lufia II Graphics page has it, the "Grid" checkbox on `L2PixelCanvas`)*
  - [x] Columns input: how many tiles wide to render (default 16)
  - [x] Tile canvas: `Image` backed by `WriteableBitmap`, updates on any input change *(changed: the Graphics tab's `Image` shows a `BitmapSource` that is redrawn on every setting change)*
  - [x] Hover tooltip on tile: tile index, file offset, SNES address, raw 32 bytes as hex *(changed: only the Lufia II Graphics page shows the pixel position and colour number under the mouse; no offsets or hex)*
  - [x] Click a tile to select it and show full detail in the inspector panel *(dropped: no inspector; on the Lufia II Graphics page a click paints)*
  - [x] Export PNG button - saves visible tile grid at current zoom *(changed: "Export picture..." saves an indexed PNG at 1x and "Import picture..." writes it back; the Lufia II page has Export PNG / Import PNG)*
  - [x] Shortcut buttons: "Font tiles", "Sprite sheet", "UI tiles" - jumps to known offsets once discovered *(changed: the Graphics tab lists packed resources by name (title screen, world map tiles, battle sprites, tilesets); the Lufia II page lists named pictures from `L2Gfx`, fonts and menu graphics included)*
- [x] `TileViewerViewModel.cs` *(changed: still in the source with the unused view; `GraphicsEditorViewModel` is what the app uses)*
  - [x] Properties: `CurrentOffset`, `BppMode`, `PaletteIndex`, `ZoomLevel`, `ShowGrid`, `Columns`, `UseVram` *(changed: `OffsetText`, `SelectedBitDepthIndex`, `UseRomPalette`, `PaletteOffsetText`, `ZoomLevel`, `TilesPerRow`, `TileCount`; no grid or VRAM)*
  - [x] `RenderCommand` - triggers `SnesTileDecoder.RenderTileGrid`, writes to `WriteableBitmap` *(changed: decodes each tile with `DecodeTile` and writes the `WriteableBitmap` itself)*
  - [x] Debounced re-render on input changes (don't re-render on every keystroke) *(changed: the Tile Viewer only renders on the Render button; the Graphics tab redraws right away on each change)*
  - [x] Subscribe to `MemoryPoller.SnapshotReady` when `UseVram = true` - update CGRAM palettes live *(dropped: no VRAM or CGRAM feed)*
- [x] `TileInspectorPanel.xaml` - side panel for selected tile *(dropped: not built; graphics are edited by exporting to PNG or painting on the Lufia II page)*
  - [x] Shows: tile index, file offset, SNES address, BPP mode, byte count *(dropped: no inspector; the Graphics tab's info line shows tile count, size and resource)*
  - [x] Raw hex dump of the tile bytes *(dropped: no inspector)*
  - [x] 8x8 zoomed pixel grid showing individual pixel palette indices *(changed: the Lufia II Graphics painter (`L2PixelCanvas`) zooms up to x24 with a grid and shows the colour number under the mouse)*

---

## 5.3 - Font Research Workflow

- [x] Add "Find Font" workflow to help locate the font tile offset *(dropped: the font was found by tracing the draw routine at $01:E001 with the disassembler)*
  - [x] Scan ROM for sequences of tiles that look like ASCII glyphs (heuristic: check for tile patterns consistent with letter shapes) *(dropped: not needed once the font was found)*
  - [x] Present candidates as a list - user clicks each to preview it in the tile viewer *(dropped: not needed once the font was found)*
  - [x] Once confirmed, save the font offset to `Lufia1Constants.cs` via the "Promote to constants" button *(changed: the offsets are in `FontPatch.cs` as `Lufia1Font` (0x54250) and `Lufia1Small` (0x10000); no Promote button)*
- [x] Once font offset is confirmed, update `TextDecoder.cs` to render a font preview in the text editor *(dropped: the text editor shows plain text and the speaker's sprite; the font is used by the Lufia II font patch and the Dutch letters instead)*

---

## 5.4 - Known Technical Challenges

| Challenge | Notes |
|-----------|-------|
| Palette location | SNES CGRAM is uploaded at runtime, not stored at a fixed ROM offset. Use the live VRAM feed from Phase 3 for accurate colors. For static ROM view, default to a greyscale palette until the user locates the correct palette data. |
| BPP mode detection | No header indicates which BPP mode a region uses. Wrong mode shows visual garbage. Default to 4bpp; let the user switch. |
| Render performance | Rendering 256+ tiles on every scroll/input change must stay under 16ms. Only decode tiles in the visible viewport. Use a `WriteableBitmap` and write pixel data directly - never use WPF drawing primitives per-tile. |
| Font offset unknown | Must be found empirically using the font research workflow above or via the disassembler tracing the text rendering routine. Found with the disassembler: text font at file 0x54250, small font at 0x10000 (`FontPatch.cs`). |
