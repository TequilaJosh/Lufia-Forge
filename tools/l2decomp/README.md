# l2decomp: Lufia II (US) as readable C#

Traces every routine in the Lufia II ROM and writes it as structured C#, one file per ROM bank.
The output is a reference for understanding the game (and building editor features). It compiles,
which checks the decompiler's work, but it isn't a runnable port: the hardware is a stub.

The output is the game's own code in another form, so it never goes into this repository or a
release. It's written to `Decomp/` in the repository root, which `.gitignore` excludes. Only the
tool and the names (`symbols.txt`, `symbols/*.txt`) are kept here.

## Running it

```
python tools/l2decomp/run.py <Lufia II (US).sfc> "Decomp/Lufia2"
dotnet build "Decomp/Lufia2"          # optional: checks that the output compiles
```

The ROM must be the US release (2.5 MB, "Lufia II(Estpolis II)" in the header).

## What comes out

| File | What it holds |
|---|---|
| `Bank80.cs` .. `Bank8E.cs` | Every routine of that bank as a C# method, in address order |
| `Tables.cs` | The jump tables (event commands, puzzle commands, battle opcodes and the rest) |
| `Names.cs` | SNES hardware registers and the named RAM / ROM addresses |
| `Runtime.cs` | The CPU model the code is written against (registers, flags, memory) |
| `asm/BankXX.asm` | The 65816 listing each method was made from, to check against |

## Reading the C#

* `A` is the 16-bit accumulator and `AL` its low byte: the game switches between 8-bit and 16-bit
  A, and the code uses `AL` wherever A was 8-bit. `X` and `Y` are the index registers.
* Memory is `m8[address]`, `m16[...]` and `m24[...]` (byte, word, 24-bit), always with a 24-bit
  address: `0x7E05AC` is WRAM $7E:05AC (low RAM is shown in bank $7E), `0x96CF69` is ROM.
  Named addresses read as names: `m16[MapId]`.
* `Ptr16(at)` is a pointer stored at `at` (in the data bank `DB`), `Ptr24(at)` a long pointer.
  `Db(Y + 2)` is "the data bank at Y + 2" (`LDA $0002,Y`, where Y holds a pointer).
* Flags only appear where they're read: `CMP #$10 / BCC` is written `if (AL < 0x10)`.
  `C = ...` or `Z = ...` statements mean a later instruction (or a caller) reads that flag. A
  routine that hands a flag back says so in its remarks ("returns flag C").
* `Adc16`, `Sbc8`, `Rol16`... are add / subtract / rotate with the carry, used when the carry
  matters; plain `A += x` when it doesn't.
* Jumps from one routine into another are calls followed by `return;`. Code a routine reaches with
  other register sizes than its own reading is a separate method (`..._M8X16`), and the call says
  "the same code read with other register sizes".
* Each method's remarks give its address, the register sizes it starts with, the data bank when
  known, and its callers.

## Names

`symbols.txt` and `symbols/*.txt` give routines, tables and RAM their names and one-line
descriptions, and can put notes inside a routine:

```
func  80:8E9D  Decompress     | Unpack a packed file: file index in $54 ...
ram   7E:05AC  2  MapId       | The map being played.
table 80:CA14  EventCommands  | Event commands 00-CC.
note  80:8EA2                 | comment written above that instruction
```

A routine with no name is `Sub_<bank>_<address>`; a jump table slot is named after its table
(`EventCommand_1A`). When two files name the same address, the first file (alphabetically, after
`symbols.txt`) wins.

## How it works

* `analyze.py` traces the code from the reset and interrupt vectors, following calls, branches and
  jump tables (sizes from `docs/lufia2/rommap.md`, plus the event, puzzle and battle tables). It
  keeps track of the A and X/Y sizes (REP/SEP/PHP/PLP), the data bank (PHK/PLB, PEA/PLB) and the
  direct page. Each routine is decoded from its own entry state; a routine called with different
  sizes gets one version per size, and identical versions are merged.
* `lift.py` splits routines into blocks and works out which flags are read where, across calls.
* `gen.py` turns each instruction into C#, keeps flag results as conditions until a branch reads
  them, and rebuilds `if` / `else`, `while` and `do ... while` from the branches. What doesn't fit
  stays a `goto`.
* `emit.py` names everything and writes the files.
