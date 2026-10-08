# lufia_spc.dll

The SNES sound chip (SPC-700 + DSP) emulator Lufia Forge uses to play the game's music in the cutscene editor.

- `Snes_Spc.*`, `Spc_Cpu.*`, `Spc_Dsp.*`, `Spc_Filter.*`, `blargg_*.h`: blargg's emulator from
  [Game_Music_Emu](https://github.com/libgme/game-music-emu) (commit f68963b1de0633149b05732113d3c3113c6f63a1),
  unmodified. Licensed under the GNU LGPL 2.1 (`LICENSE-LGPL-2.1.txt`).
- `lufia_spc.cpp`: Lufia Forge's C interface to it (port access with a running clock, rendering, RAM copy for tests).
- `build.cmd`: builds the x64 DLL with the Visual Studio C++ tools and copies it to `LufiaForge/lufia_spc.dll`,
  which the app ships next to its exe.

The console's boot ROM isn't used or included: `Core/Audio/LufiaSound.cs` places the game's sound driver in sound
RAM directly and talks to it through the four ports the way the game's own code does.
