# Phase 8 — Character Editor & Sprite Swapper

Edit character stats, equipment, spells, and growth curves. View and swap sprite sheets with full palette editing and drawing tools.

> **Depends on:** Phase 5 (tile decoder for sprite rendering), Phase 3 (live character stat reading from RAM)

---

## Research Sprint (do before writing any Phase 8 code)

- [ ] Locate character stat block offsets in ROM
  - Known from existing community research: some stat data exists but full ROM offsets need confirmation
  - Use memory viewer (Phase 3) to watch character stats during a level-up or item equip, then trace back to ROM
- [ ] Locate the character sprite sheet regions in the ROM
  - Use the tile viewer (Phase 5) to scan for regions that look like character sprites
  - Cross-reference with VRAM contents during gameplay to find the upload source offset
- [ ] Locate the spell/magic data table
- [ ] Locate the equipment data table (the Multi Editor utility already documents some of this)
- [ ] Document all confirmed offsets in `Lufia1Constants.cs`

---

## 8.1 — Character Data Model

- [ ] `CharacterData.cs`
  - [ ] `int CharacterIndex` (0=Hero, 1=Lufia, 2=Aguro, 3=Jerin)
  - [ ] `string Name` (up to 6 characters per SRAM map)
  - [ ] `int BaseStrength`, `int BaseAgility`, `int BaseIntelligence`, `int BaseMagicPower`
  - [ ] `int BaseHp`, `int BaseMp`
  - [ ] `int[] LevelUpStatGains` — per-level stat increase table
  - [ ] `List<int> LearnableSpells` — spell IDs learned at each level
  - [ ] `List<int> EquippableItemTypes` — bitmask of what equipment types this character can use
- [ ] `CharacterLoader.cs` — read character data from `RomBuffer`
- [ ] `CharacterSerializer.cs` — write modified character data back to ROM

---

## 8.2 — Character Stats Editor UI

- [ ] `CharacterEditorView.xaml`
  - [ ] Character selector tabs: Hero / Lufia / Aguro / Jerin
  - [ ] Base stats section: editable fields for all base stats with min/max validation
  - [ ] Level-up gains table: grid showing stat gains per level (editable)
    - Columns: Level, STR gain, AGL gain, INT gain, MGR gain, HP gain, MP gain
  - [ ] Spells section:
    - [ ] List of all spells with level learned (editable)
    - [ ] Add/remove spells from the learnable list
    - [ ] Spell stat editor: power, MP cost, target type, element
  - [ ] Equipment section:
    - [ ] Toggle which equipment types the character can use
    - [ ] Starting equipment selector
  - [ ] Live stat preview: shows projected stats at any level using the current growth table
  - [ ] "Read from emulator" button — reads current in-game stats from RAM (requires Phase 3) and populates the editor
  - [ ] Save to ROM button — writes via `CharacterSerializer`

---

## 8.3 — Sprite Sheet Viewer

- [ ] `SpriteSheetView.xaml`
  - [ ] Character selector: Hero / Lufia / Aguro / Jerin / Monster (by index)
  - [ ] Sprite canvas: renders the full sprite sheet for the selected character
    - Uses `SnesTileDecoder` from Phase 5 at 4bpp
  - [ ] Animation frame strip at the bottom: shows individual frames for walk/attack/cast/damage/death cycles
  - [ ] Zoom slider: 1x to 8x
  - [ ] Palette editor panel:
    - [ ] 16 color swatches for the active palette
    - [ ] Click a swatch to open color picker (WPF `ColorDialog`)
    - [ ] BGR555 hex value shown alongside RGB
    - [ ] Changes preview live on the sprite canvas
    - [ ] Save palette to ROM button

---

## 8.4 — Sprite Editor (Drawing Tools)

- [ ] `SpriteEditorView.xaml` — per-tile pixel editor
  - [ ] Tile selector: click any tile in the sprite sheet to open it in the pixel editor
  - [ ] Pixel canvas: 8x8 grid, each pixel rendered large enough to click (minimum 32px per pixel at 1x)
  - [ ] Zoom: 4x / 8x / 16x / 32x
  - [ ] Tools:
    - [ ] Pencil — click/drag to draw with selected color
    - [ ] Eraser — sets pixels to palette index 0 (transparent)
    - [ ] Flood fill — fills contiguous same-color region
    - [ ] Color picker — click a pixel to set the active color to that pixel's palette index
    - [ ] Undo / redo (minimum 30 levels)
  - [ ] Active color selector: shows current palette and selected index
  - [ ] Grid overlay toggle
  - [ ] Mirror horizontal / mirror vertical toggle (applies symmetrically as you draw)
  - [ ] Copy tile / paste tile buttons
  - [ ] Import tile from PNG: paste a 8x8 PNG into the current tile slot (auto-quantizes to the active 16-color palette)
  - [ ] Export tile as PNG
- [ ] `SpriteSwapperPanel.xaml` — swap entire sprite sheets between characters or from external files
  - [ ] Source: current character's sprite sheet
  - [ ] Target: select any character
  - [ ] Preview both side by side before confirming
  - [ ] Import from PNG: load an external sprite sheet PNG, auto-slice into 8x8 tiles, write to ROM
  - [ ] Export to PNG: dump the full sprite sheet as a single PNG image

---

## 8.5 — Monster Editor

- [ ] `MonsterData.cs`
  - [ ] `int MonsterIndex`, `string Name`
  - [ ] `int Hp`, `int Mp`, `int Strength`, `int Agility`, `int Intelligence`, `int MagicPower`
  - [ ] `int ExpReward`, `int GoldReward`
  - [ ] `int DropItemId`, `int DropRate`
  - [ ] `List<int> AttackPattern` — AI script byte sequence
- [ ] `MonsterEditorView.xaml`
  - [ ] Monster list with search
  - [ ] Stat editor (same pattern as character editor)
  - [ ] Sprite viewer showing monster sprite sheet
  - [ ] Drop table editor
  - [ ] AI pattern byte editor (raw bytes + decoded opcode labels)

---

## 8.6 — Known Technical Challenges

| Challenge | Notes |
|-----------|-------|
| Sprite sheet layout | SNES sprites are not stored as contiguous sprite-sheet images. A character's walk animation frames may be scattered across multiple non-contiguous tile regions. The research sprint must map out which ROM offsets correspond to which animation frames before the sprite sheet can be rendered correctly. |
| Palette assignment | Each character uses a specific CGRAM palette slot. The wrong palette index makes sprites look like noise. Confirm palette assignments via the live VRAM viewer (Phase 3) during gameplay. |
| Import PNG quantization | Imported PNGs almost certainly have more than 16 colors. The importer must reduce the image to 15 colors (plus transparent index 0) using a palette quantization algorithm. Consider using a median-cut or k-means approach. |
| Level-up stat table format | Stat growth tables may be stored as deltas (gain per level) or absolute values (stat at level N). The format affects the editor UI and must be confirmed via research before 8.2 is built. |
