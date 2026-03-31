# Phase 1 — Foundation ✅ Complete

ROM loader, core infrastructure, IPS patch handler, WPF shell.

---

## Completed Tasks

- [x] Create `LufiaForge.sln` and `.csproj` targeting .NET 8 WPF
- [x] Add NuGet packages: `CommunityToolkit.Mvvm`, `Microsoft.Xaml.Behaviors.Wpf`
- [x] `Lufia1Constants.cs` — SNES header offsets, LoROM address translator, control codes
- [x] `RomBuffer.cs` — in-memory ROM with safe read/write helpers and pattern search
- [x] `RomLoader.cs` — file load, SMC header detection and strip, checksum validation, game identity check
- [x] `IpsHandler.cs` — full IPS patch apply and export (record merge, RLE support)
- [x] `LufiaTheme.xaml` — purple/gold WPF resource dictionary (all control styles)
- [x] `ValueConverters.cs` — `BoolToVisibilityConverter`, `InverseBoolToVisibilityConverter`
- [x] `MainViewModel.cs` — Open/Save/SaveAs commands, ROM load state, dirty title indicator
- [x] `MainWindow.xaml` — toolbar, 6-tab shell, welcome splash, gold status bar
- [x] `MainWindow.xaml.cs` — ROM info tab population, `PropagateRomToModules()`

---

# Phase 2 — Text Editor ✅ Complete

ASCII + dictionary-compressed dialogue extraction, editing, and re-encoding.

---

## Completed Tasks

- [x] Update `Lufia1Constants.cs` with confirmed encoding data (Vegetaman 2010 research)
  - [x] Dictionary offsets `0x054E19–0x0553CC`, base `0x48000`
  - [x] Control codes: `0x00` END, `0x04` PAGE, `0x05` NL, `0x0B` NAME, `0x0C` DICT
- [x] `TextDecoder.cs` — decode strings (ASCII + dictionary expansion), encode edits back to ROM bytes
- [x] `DialogueScanner.cs` — async heuristic scan of banks `$88–$AF` for valid dialogue strings
- [x] `TextEditorViewModel.cs` — scan, search/filter, commit with byte-length guard, revert, export
- [x] `TextEditorView.xaml` — split original/edit pane, dictionary tab, hex token view
- [x] `TextEditorView.xaml.cs` — hex token view wired to selection
- [x] Wire `TextEditorView` into `MainWindow.xaml` tab
- [x] `MainWindow.xaml.cs` — `PropagateRomToModules()` passes `RomBuffer` on load
