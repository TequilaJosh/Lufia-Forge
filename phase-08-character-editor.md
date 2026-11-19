# Phase 8: Character Editor & Sprite Swapper

Edit character stats, equipment, spells, and growth curves. View and swap sprite sheets with full palette editing and drawing tools.

> **Depends on:** Phase 5 (tile decoder for sprite rendering), Phase 3 (live character stat reading from RAM)

**Status: ✅ Complete (2026-11-17).** Built as tabs of the Game Data module (`Modules/GameData`) instead of one character editor:
Characters, Character Magic, Growth & EXP, Sprites, Items, Spells, Monsters and Palettes. There are no separate model, loader or
serializer classes: each tab's view model reads and writes its ROM records directly, with the offsets in `GameDataOffsets`, `LevelGrowth`,
`NpcSprites` and `MonsterTable`. Lufia 1 sprites are drawn in a paint program and brought back with PNG export/import; the pixel painter
is on the Lufia II Graphics page (`L2GraphicsPage`). Lufia II has its own Characters page (`L2CharactersPage`). Differences are marked
*(changed)*; parts that aren't needed anymore are marked *(dropped)*.

---

## Research Sprint (do before writing any Phase 8 code)

- [x] Locate character stat block offsets in ROM *(changed: taken from L1ME and checked again on the US ROM, not traced with the memory viewer. `GameDataOffsets.CharacterTable` 0x46D25 (8 x 29 bytes); growth table 0x46C69 and EXP thresholds 0x46D11 in `LevelGrowth`, read from the level-up code at $08:E601)*
  - Known from existing community research: some stat data exists but full ROM offsets need confirmation
  - Use memory viewer (Phase 3) to watch character stats during a level-up or item equip, then trace back to ROM
- [x] Locate the character sprite sheet regions in the ROM *(map sprites: record table $01:E25F with a graphics address per sprite, in `NpcSprites`; palettes resource 0xAF; battle sprites resource 0x13D and palettes at 0x11200 in `SpriteEditorViewModel` / `Palettes`)*
  - Use the tile viewer (Phase 5) to scan for regions that look like character sprites
  - Cross-reference with VRAM contents during gameplay to find the upload source offset
- [x] Locate the spell/magic data table *(`GameDataOffsets.SpellOffsets` from 0xFE354, plus the spell lists at `HeroSpellList` / `LufiaSpellList` / `JerinSpellList`)*
- [x] Locate the equipment data table (the Multi Editor utility already documents some of this) *(`GameDataOffsets.ItemPointerTable` 0x55800, 256 items)*
- [x] Document all confirmed offsets in `Lufia1Constants.cs` *(changed: they're in `GameDataOffsets.cs`, with the growth, sprite and monster ones in `LevelGrowth`, `NpcSprites` and `MonsterTable`)*

---

## 8.1 Character Data Model

- [x] `CharacterData.cs` *(changed: no model class; `CharacterEditorViewModel` reads the 29 byte record at `GameDataOffsets.CharacterTable`)*
  - [x] `int CharacterIndex` (0=Hero, 1=Lufia, 2=Aguro, 3=Jerin) *(changed: 0-7, Maxim, Selan, Guy and Artea too)*
  - [x] `string Name` (up to 6 characters per SRAM map) *(changed: 5 letters at `GameDataOffsets.CharacterNames`; the Hero has no name in the ROM, the player names him)*
  - [x] `int BaseStrength`, `int BaseAgility`, `int BaseIntelligence`, `int BaseMagicPower`
  - [x] `int BaseHp`, `int BaseMp`
  - [x] `int[] LevelUpStatGains`: per-level stat increase table *(changed: gains per band of 16 level-ups, 6 stats x 7 bands, in `LevelGrowth.Setup`)*
  - [x] `List<int> LearnableSpells`: spell IDs learned at each level *((level, spell) pairs, edited by `CharacterMagicViewModel`)*
  - [x] `List<int> EquippableItemTypes`: bitmask of what equipment types this character can use *(changed: set per item, the "who can equip" flags of the item record)*
- [x] `CharacterLoader.cs`: read character data from `RomBuffer` *(changed: `CharacterEditorViewModel.LoadRecord` and `LevelGrowth.Read`)*
- [x] `CharacterSerializer.cs`: write modified character data back to ROM *(changed: `CharacterEditorViewModel.Apply` and `LevelGrowth.Write`)*

---

## 8.2 Character Stats Editor UI

- [x] `CharacterEditorView.xaml` *(Characters tab: starting level, stats, EXP, equipment and name)*
  - [x] Character selector tabs: Hero / Lufia / Aguro / Jerin *(changed: a list of all 8 characters)*
  - [x] Base stats section: editable fields for all base stats with min/max validation *(changed: values are clamped to their size on Apply)*
  - [x] Level-up gains table: grid showing stat gains per level (editable) *(changed: Growth & EXP tab, `GrowthEditorViewModel`: a 6 stats x 7 bands grid plus the EXP curve and thresholds)*
    - Columns: Level, STR gain, AGL gain, INT gain, MGR gain, HP gain, MP gain
  - [x] Spells section: *(changed: its own Character Magic tab)*
    - [x] List of all spells with level learned (editable) *(`CharacterMagicViewModel`, one list per caster)*
    - [x] Add/remove spells from the learnable list *(changed: only with the "Up to 128 spells, 64 per character" patch (`SpellMemory`); without it the lists keep their length)*
    - [x] Spell stat editor: power, MP cost, target type, element *(changed: the Spells tab, `SpellEditorViewModel`; element is the sub-type of element spells, and the effect type is read-only)*
  - [x] Equipment section:
    - [x] Toggle which equipment types the character can use *(changed: Hero / Lufia / Aguro / Jerin checkboxes on each item in the Items tab)*
    - [x] Starting equipment selector *(weapon, armor, shield, helmet, shoes and ring)*
  - [x] Live stat preview: shows projected stats at any level using the current growth table *(the Growth & EXP tab's level by level table up to 99, worked out like the game's level-up code, without the random -2..+2)*
  - [x] "Read from emulator" button: reads current in-game stats from RAM (requires Phase 3) and populates the editor *(dropped: the editor holds starting values, not in-game ones; live party stats are watched in the Memory Monitor (`Lufia1AddressMap` Hero / Lufia / Aguro / Jerin entries))*
  - [x] Save to ROM button: writes via `CharacterSerializer` *(changed: Apply, through `GameDataEditorBase.Commit`, one undo step)*

---

## 8.3 Sprite Sheet Viewer

- [x] `SpriteSheetView.xaml` *(changed: the Sprites tab, `SpriteEditorView` / `SpriteEditorViewModel`)*
  - [x] Character selector: Hero / Lufia / Aguro / Jerin / Monster (by index) *(changed: a list of every map sprite by number, party and NPCs; monster pictures are on the Monsters tab (`MonsterPicture`))*
  - [x] Sprite canvas: renders the full sprite sheet for the selected character
    - Uses `SnesTileDecoder` from Phase 5 at 4bpp
  - [x] Animation frame strip at the bottom: shows individual frames for walk/attack/cast/damage/death cycles *(changed: the sheet shows the 8 map frames side by side; the party's battle poses are on the battle sprite sheet (resource 0x13D) below it)*
  - [x] Zoom slider: 1x to 8x *(changed: fixed 3x on the Sprites tab; the Lufia II Graphics page zooms 1x to 24x)*
  - [x] Palette editor panel: *(changed: the Palettes tab, `PaletteEditorViewModel`, with "Character sprites" and "Battle sprites" rows; the Sprites tab picks the row a sprite uses)*
    - [x] 16 color swatches for the active palette
    - [x] Click a swatch to open color picker (WPF `ColorDialog`) *(changed: Red / Green / Blue fields (0-31) and a web colour box)*
    - [x] BGR555 hex value shown alongside RGB *(changed: the SNES value is in the swatch tooltip, the web hex in the editor)*
    - [x] Changes preview live on the sprite canvas *(changed: a preview box shows the edited colour; the sprite shows it after Apply)*
    - [x] Save palette to ROM button *(Apply; palettes also export to and import from PNG)*

---

## 8.4 Sprite Editor (Drawing Tools)

- [x] `SpriteEditorView.xaml`: per-tile pixel editor *(changed: no Lufia 1 painter, sprites go out and back in as PNG; the Lufia II Graphics page (`L2GraphicsPage` + `L2PixelCanvas`) paints whole pictures pixel by pixel, map sprites included)*
  - [x] Tile selector: click any tile in the sprite sheet to open it in the pixel editor *(changed: the whole picture is painted, no tile picking)*
  - [x] Pixel canvas: 8x8 grid, each pixel rendered large enough to click (minimum 32px per pixel at 1x) *(changed: `L2PixelCanvas` shows the whole picture at the chosen zoom)*
  - [x] Zoom: 4x / 8x / 16x / 32x *(changed: 1x to 24x, Ctrl + mouse wheel)*
  - [x] Tools:
    - [x] Pencil: click/drag to draw with selected color
    - [x] Eraser: sets pixels to palette index 0 (transparent) *(changed: no eraser tool; pick colour 0 and paint with it)*
    - [x] Flood fill: fills contiguous same-color region
    - [x] Color picker: click a pixel to set the active color to that pixel's palette index *(right click)*
    - [x] Undo / redo (minimum 30 levels) *(200 strokes, Ctrl+Z / Ctrl+Y)*
  - [x] Active color selector: shows current palette and selected index
  - [x] Grid overlay toggle
  - [x] Mirror horizontal / mirror vertical toggle (applies symmetrically as you draw) *(dropped: detailed drawing is done in a paint program through PNG export/import)*
  - [x] Copy tile / paste tile buttons *(changed: whole sprites are copied instead: "New sprite (copy of this one)" on the Sprites tab, "Copy of this sprite" on the Lufia II Graphics page)*
  - [x] Import tile from PNG: paste a 8x8 PNG into the current tile slot (auto-quantizes to the active 16-color palette) *(changed: the whole sheet is imported; an indexed PNG keeps its colour numbers, others get the nearest colours of the sprite's palette row (`NpcSprites.ImportSheet`))*
  - [x] Export tile as PNG *(changed: the whole sheet is exported as an indexed PNG)*
- [x] `SpriteSwapperPanel.xaml`: swap entire sprite sheets between characters or from external files *(changed: no swap panel; export one sprite's sheet and import it into another, or give characters a different sprite in the Map Editor)*
  - [x] Source: current character's sprite sheet *(Export sheet)*
  - [x] Target: select any character *(pick the sprite in the list, then Import sheet)*
  - [x] Preview both side by side before confirming *(dropped: there's no swap step to confirm; the list shows every sprite's picture)*
  - [x] Import from PNG: load an external sprite sheet PNG, auto-slice into 8x8 tiles, write to ROM
  - [x] Export to PNG: dump the full sprite sheet as a single PNG image

---

## 8.5 Monster Editor

- [x] `MonsterData.cs` *(changed: no model class; `MonsterEditorViewModel` reads the record, layout in `GameDataOffsets` and `MonsterTable`)*
  - [x] `int MonsterIndex`, `string Name`
  - [x] `int Hp`, `int Mp`, `int Strength`, `int Agility`, `int Intelligence`, `int MagicPower` *(changed: HP, MP, ATP, DFP, M.DEF, AGL and MGR, the stats a monster record has)*
  - [x] `int ExpReward`, `int GoldReward`
  - [x] `int DropItemId`, `int DropRate`
  - [x] `List<int> AttackPattern`: AI script byte sequence *(changed: `AiScript` in Core/Battle)*
- [x] `MonsterEditorView.xaml` *(Monsters tab)*
  - [x] Monster list with search *(the "Find..." box above the list)*
  - [x] Stat editor (same pattern as character editor)
  - [x] Sprite viewer showing monster sprite sheet *(the monster picture, with PNG export and import)*
  - [x] Drop table editor *(drop item and drop rate fields)*
  - [x] AI pattern byte editor (raw bytes + decoded opcode labels) *(raw hex on the tab; "Edit AI script..." opens `AiScriptWindow` with the steps decoded)*

---

## 8.6 Known Technical Challenges

| Challenge | Notes |
|-----------|-------|
| Sprite sheet layout | SNES sprites are not stored as contiguous sprite-sheet images. A character's walk animation frames may be scattered across multiple non-contiguous tile regions. The research sprint must map out which ROM offsets correspond to which animation frames before the sprite sheet can be rendered correctly. |
| Palette assignment | Each character uses a specific CGRAM palette slot. The wrong palette index makes sprites look like noise. Confirm palette assignments via the live VRAM viewer (Phase 3) during gameplay. |
| Import PNG quantization | Imported PNGs almost certainly have more than 16 colors. The importer must reduce the image to 15 colors (plus transparent index 0) using a palette quantization algorithm. Consider using a median-cut or k-means approach. |
| Level-up stat table format | Stat growth tables may be stored as deltas (gain per level) or absolute values (stat at level N). The format affects the editor UI and must be confirmed via research before 8.2 is built. |
