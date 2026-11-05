# Higher limits patches (levels to 255, HP and MP to 9999)

The 65816 sources of Lufia Forge's "Higher limits" patches and the small tools that turn them into the C# data of
`Core/LimitsPatch.cs` (Lufia 1) and `Core/Lufia2/L2Limits.cs` (Lufia II). The 3-digit level idea and its menu bytes are
Colmines92's patches.

* `l1limits.asm`, `l2limits.asm`: the patches (asar-like syntax; `HOOK` is where the added code goes).
* `l2exp.py`: appends the EXP table for levels 98-255 to `l2limits.asm` as `l2limits_full.asm`.
* `asm65.py`: a small two-pass assembler for these files (LoROM). `build.py <clean rom> <asm> <out> [HOOK] [MB]` applies
  one to a ROM and prints every change with the bytes it replaces (for test ROMs).
* `gen_cs.py <asm> <clean rom>`: prints the C# `Sites` and `Code` for `HookPatch` (assembled at two addresses to find the
  pointers into the added block). Paste the output over the generated part of the class.
* `dis65.py <rom> <addr> <count> [m8] [x8]`: a linear disassembler (uses `tools/l2decomp/ops.py`).

Lufia 1: `python gen_cs.py l1limits.asm <clean US ROM>`.
Lufia II: `python l2exp.py` then `python gen_cs.py l2limits_full.asm <clean US ROM>`.
