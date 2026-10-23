# Lufia II (US) ROM map (first pass, static analysis)

ROM: `lufia2.sfc`, 0x280000 bytes, LoROM FastROM, banks $80-$CF. File offset = (bank & 0x7F) * 0x8000 + (addr - 0x8000).
Built by `l2research\l2map.py` (recursive-descent 65816 disassembler) + `l2research\l2report.py` (classification).
Companion files: `l2_disasm.txt` (listing, bank by bank), `l2_refs.txt` (data addresses referenced by code), `l2_ranges.txt` (every byte classified), `l2_files.txt` (packed file extents).

## Method and caveats

* Seeds: native/emulation vectors (RESET $80:8000, NMI $80:862D, IRQ $80:86C0, COP/BRK/ABORT $80:FFAA), every `JMP/JSR (abs,X)` table found while tracing (entries read while they are u16 >= $8000 in the same bank, stop at code, at the first handler, or at a BRK/COP/STP/WDM target), the inline tables after `JSR $82:8028`, the event command table $80:CA14 and the battle script table $85:B485.
* M/X are tracked through REP/SEP/PHP/PLP/XCE; a JSR/JSL callee is entered with the caller's flags and is assumed to return them unchanged (the usual source of error). Each address is traced in at most two M/X states.
* DB is tracked through PHK/PLB, LDA #imm/PHA/PLB, PEA/PLB and PHB/PLB inside one linear trace; elsewhere `abs` operands are listed as `abs?` (bank assumed = code bank). Pointer set-ups `LDA #lo16 / STA dp` + `LDA #bank / STA dp+2` are listed as `ptr-imm`.
* "code?" = speculative: a gap right after an RTS/RTL/JMP/BRA that disassembles cleanly to a terminator without hitting BRK/COP/STP/WDM or overlapping code. Plausible functions reached only through RAM vectors or pointer data, but unproven.
* Packed files are measured exactly by running the decompressor: they tile 0x1387FB-0x27E301 with no gaps or overlaps (the packer does not store the trailing flag byte the decoder would read after the 8th item of the last group).

## Coverage

| Kind | Bytes | Share |
|---|---|---|
| packed files | 0x145B06 (1,334,022) | 50.9% |
| unknown | 0x673FF (422,911) | 16.1% |
| data (referenced) | 0x446D3 (280,275) | 10.7% |
| text/scripts | 0x3B4F8 (242,936) | 9.3% |
| code | 0x2AC31 (175,153) | 6.7% |
| data (known table) | 0x17E9B (97,947) | 3.7% |
| free (FF/00) | 0xDA4D (55,885) | 2.1% |
| code? | 0x2159 (8,537) | 0.3% |
| file table | 0x7FB (2,043) | 0.1% |
| jump table | 0x6C3 (1,731) | 0.1% |

Instructions decoded: 77,739 reached + 3,985 speculative; 2852 reached entry points (JSR/JSL/JMP/table/vector targets), 95 speculative ones; 70 jump tables; 761 data references to 461 distinct ROM addresses; 11 decode conflicts (branch/call into the middle of an instruction: flag mismatch or data).

## Per-bank summary (32 KB banks, bytes in hex)

| Bank | File | code | code? | jtab | files | text | known tbl | ref data | unknown | free | main content |
|---|---|---|---|---|---|---|---|---|---|---|---|
| $80 | 0x000000 | 6C06 | 2CE | 375 | 0 | 0 | 40 | 365 | 983 | 8F | code, unknown |
| $81 | 0x008000 | 6377 | 40A | A8 | 0 | 0 | 0 | 60 | 1777 | 0 | code, unknown, code (speculative) |
| $82 | 0x010000 | 7945 | 414 | B8 | 0 | 0 | 0 | 10D | E2 | 0 | code, code (speculative) |
| $83 | 0x018000 | 61C5 | 577 | 106 | 0 | 0 | 0 | 3DF | 13BE | 21 | code, unknown, code (speculative) |
| $84 | 0x020000 | 63E2 | C6 | 0 | 0 | 0 | C2E | DD6 | 154 | 0 | code, referenced by code, IP attack records (follow the IP pointer table; names inline) |
| $85 | 0x028000 | 5266 | 746 | 8C | 0 | 0 | 7DF | C83 | 1166 | 0 | code, unknown, referenced by code |
| $86 | 0x030000 | 388F | 67D | 52 | 0 | 0 | 0 | 1E64 | 223E | 0 | code, unknown, referenced by code |
| $87 | 0x038000 | 0 | 0 | 0 | 0 | 76E1 | 810 | 0 | 97 | 78 | map event/dialog blocks ("PH"), map start-up scripts (242 maps), map names |
| $88 | 0x040000 | 0 | 0 | 0 | 0 | 8000 | 0 | 0 | 0 | 0 | map event/dialog blocks ("PH") |
| $89 | 0x048000 | 0 | 0 | 0 | 0 | 8000 | 0 | 0 | 0 | 0 | map event/dialog blocks ("PH") |
| $8A | 0x050000 | 0 | 0 | 0 | 0 | 8000 | 0 | 0 | 0 | 0 | map event/dialog blocks ("PH") |
| $8B | 0x058000 | 0 | 0 | 0 | 0 | 8000 | 0 | 0 | 0 | 0 | map event/dialog blocks ("PH") |
| $8C | 0x060000 | 0 | 0 | 0 | 0 | 8000 | 0 | 0 | 0 | 0 | map event/dialog blocks ("PH") |
| $8D | 0x068000 | 0 | 0 | 0 | 0 | 8000 | 0 | 0 | 0 | 0 | map event/dialog blocks ("PH") |
| $8E | 0x070000 | 12D3 | 26D | A | 0 | 3E17 | 500 | 1ABA | CE5 | 0 | map event/dialog blocks ("PH"), referenced by code, code |
| $8F | 0x078000 | 0 | 0 | 0 | 0 | 0 | 2B2 | 0 | 7D2A | 24 | unknown |
| $90 | 0x080000 | 0 | 0 | 0 | 0 | 0 | 0 | 4000 | 4000 | 0 | unknown, referenced by code |
| $91 | 0x088000 | 0 | 0 | 0 | 0 | 0 | 0 | 3ACF | 450F | 22 | unknown, referenced by code |
| $92 | 0x090000 | 0 | 0 | 0 | 0 | 0 | 0 | 8000 | 0 | 0 | referenced by code |
| $93 | 0x098000 | 0 | 0 | 0 | 0 | 0 | 0 | 8000 | 0 | 0 | referenced by code |
| $94 | 0x0A0000 | 0 | 0 | 0 | 0 | 0 | 0 | 8000 | 0 | 0 | referenced by code |
| $95 | 0x0A8000 | 0 | 0 | 0 | 0 | 0 | 4BB | 9E4 | 70AF | B2 | unknown, referenced by code, spell records (follow the spell offset table) |
| $96 | 0x0B0000 | 0 | 0 | 0 | 0 | 0 | 751D | AE3 | 0 | 0 | monster records ("Goblin" first; follow the monster offset table), item records (follow the item offset table), referenced by code |
| $97 | 0x0B8000 | 0 | 0 | 0 | 0 | 0 | 1B8B | 50A2 | 1107 | 2CC | referenced by code, capsule monster records ("Foomy S" first, BRIEF), unknown |
| $98 | 0x0C0000 | 0 | 0 | 0 | 0 | 0 | 0 | 8000 | 0 | 0 | referenced by code |
| $99 | 0x0C8000 | 0 | 0 | 0 | 0 | 0 | 0 | 605B | 1CF4 | 2B1 | referenced by code, unknown |
| $9A | 0x0D0000 | 0 | 0 | 0 | 0 | 0 | 7970 | 690 | 0 | 0 | world map big blocks (l2ref 9: same as German), referenced by code |
| $9B | 0x0D8000 | 0 | 0 | 0 | 0 | 0 | 0 | 7FE8 | 0 | 18 | referenced by code |
| $9C | 0x0E0000 | 0 | 0 | 0 | 0 | 0 | 0 | 65 | 5C3B | 2360 | unknown, run of 00 |
| $9D | 0x0E8000 | 0 | 0 | 0 | 0 | 0 | 0 | 0 | 6C3A | 13C6 | unknown, run of 00 |
| $9E | 0x0F0000 | 0 | 0 | 0 | 0 | 0 | 2DCC | 15AE | 2FEF | C97 | unknown, world map small blocks (l2ref 9), item names (0x1D3 x 12) |
| $9F | 0x0F8000 | 0 | 0 | 0 | 0 | 0 | 0 | 0 | 62A7 | 1D59 | unknown, run of 00 |
| $A0 | 0x100000 | 0 | 0 | 0 | 0 | 0 | 0 | 0 | 69A6 | 165A | unknown, run of 00 |
| $A1 | 0x108000 | 0 | 0 | 0 | 0 | 0 | 0 | 0 | 67C6 | 183A | unknown, run of 00 |
| $A2 | 0x110000 | 0 | 0 | 0 | 0 | 0 | 0 | 0 | 6EA7 | 1159 | unknown, run of 00 |
| $A3 | 0x118000 | 0 | 0 | 0 | 0 | 0 | 0 | 0 | 75CC | A34 | unknown, run of 00 |
| $A4 | 0x120000 | 0 | 0 | 0 | 0 | 0 | 0 | 0 | 79E0 | 620 | unknown, run of 00 |
| $A5 | 0x128000 | 0 | 0 | 0 | 0 | 0 | 578 | 1B0 | 687D | 105B | unknown, run of 00, capsule monster attack names (BRIEF, "near") |
| $A6 | 0x130000 | 0 | 0 | 0 | 0 | 0 | 8E3 | BDD | 5C02 | F3E | unknown, run of 00, referenced by code |
| $A7 | 0x138000 | 0 | 0 | 0 | 8000 | 0 | 0 | 0 | 0 | 0 | packed files 0x000..0x01C |
| $A8 | 0x140000 | 0 | 0 | 0 | 8000 | 0 | 0 | 0 | 0 | 0 | packed files 0x01C..0x044 |
| $A9 | 0x148000 | 0 | 0 | 0 | 8000 | 0 | 0 | 0 | 0 | 0 | packed files 0x044..0x052 |
| $AA | 0x150000 | 0 | 0 | 0 | 8000 | 0 | 0 | 0 | 0 | 0 | packed files 0x052..0x061 |
| $AB | 0x158000 | 0 | 0 | 0 | 8000 | 0 | 0 | 0 | 0 | 0 | packed files 0x061..0x072 |
| $AC | 0x160000 | 0 | 0 | 0 | 8000 | 0 | 0 | 0 | 0 | 0 | packed files 0x072..0x085 |
| $AD | 0x168000 | 0 | 0 | 0 | 8000 | 0 | 0 | 0 | 0 | 0 | packed files 0x085..0x097 |
| $AE | 0x170000 | 0 | 0 | 0 | 8000 | 0 | 0 | 0 | 0 | 0 | packed files 0x097..0x0A9 |
| $AF | 0x178000 | 0 | 0 | 0 | 8000 | 0 | 0 | 0 | 0 | 0 | packed files 0x0A9..0x0BB |
| $B0 | 0x180000 | 0 | 0 | 0 | 8000 | 0 | 0 | 0 | 0 | 0 | packed files 0x0BB..0x0CD |
| $B1 | 0x188000 | 0 | 0 | 0 | 8000 | 0 | 0 | 0 | 0 | 0 | packed files 0x0CD..0x0E1 |
| $B2 | 0x190000 | 0 | 0 | 0 | 8000 | 0 | 0 | 0 | 0 | 0 | packed files 0x0E1..0x0F2 |
| $B3 | 0x198000 | 0 | 0 | 0 | 8000 | 0 | 0 | 0 | 0 | 0 | packed files 0x0F2..0x102 |
| $B4 | 0x1A0000 | 0 | 0 | 0 | 8000 | 0 | 0 | 0 | 0 | 0 | packed files 0x102..0x118 |
| $B5 | 0x1A8000 | 0 | 0 | 0 | 8000 | 0 | 0 | 0 | 0 | 0 | packed files 0x118..0x12B |
| $B6 | 0x1B0000 | 0 | 0 | 0 | 8000 | 0 | 0 | 0 | 0 | 0 | packed files 0x12B..0x139 |
| $B7 | 0x1B8000 | 0 | 0 | 0 | 8000 | 0 | 0 | 0 | 0 | 0 | packed files 0x139..0x141 |
| $B8 | 0x1C0000 | 0 | 0 | 0 | 8000 | 0 | 0 | 0 | 0 | 0 | packed files 0x141..0x14C |
| $B9 | 0x1C8000 | 0 | 0 | 0 | 8000 | 0 | 0 | 0 | 0 | 0 | packed files 0x14C..0x14E |
| $BA | 0x1D0000 | 0 | 0 | 0 | 8000 | 0 | 0 | 0 | 0 | 0 | packed files 0x14E..0x14F |
| $BB | 0x1D8000 | 0 | 0 | 0 | 8000 | 0 | 0 | 0 | 0 | 0 | packed files 0x14F..0x152 |
| $BC | 0x1E0000 | 0 | 0 | 0 | 8000 | 0 | 0 | 0 | 0 | 0 | packed files 0x152..0x154 |
| $BD | 0x1E8000 | 0 | 0 | 0 | 8000 | 0 | 0 | 0 | 0 | 0 | packed files 0x154..0x156 |
| $BE | 0x1F0000 | 0 | 0 | 0 | 8000 | 0 | 0 | 0 | 0 | 0 | packed files 0x156..0x158 |
| $BF | 0x1F8000 | 0 | 0 | 0 | 8000 | 0 | 0 | 0 | 0 | 0 | packed files 0x158..0x15B |
| $C0 | 0x200000 | 0 | 0 | 0 | 8000 | 0 | 0 | 0 | 0 | 0 | packed files 0x15B..0x15D |
| $C1 | 0x208000 | 0 | 0 | 0 | 8000 | 0 | 0 | 0 | 0 | 0 | packed files 0x15D..0x15F |
| $C2 | 0x210000 | 0 | 0 | 0 | 8000 | 0 | 0 | 0 | 0 | 0 | packed files 0x15F..0x16E |
| $C3 | 0x218000 | 0 | 0 | 0 | 8000 | 0 | 0 | 0 | 0 | 0 | packed files 0x16E..0x176 |
| $C4 | 0x220000 | 0 | 0 | 0 | 8000 | 0 | 0 | 0 | 0 | 0 | packed files 0x176..0x1A3 |
| $C5 | 0x228000 | 0 | 0 | 0 | 8000 | 0 | 0 | 0 | 0 | 0 | packed files 0x1A3..0x1D0 |
| $C6 | 0x230000 | 0 | 0 | 0 | 8000 | 0 | 0 | 0 | 0 | 0 | packed files 0x1D0..0x1FF |
| $C7 | 0x238000 | 0 | 0 | 0 | 8000 | 0 | 0 | 0 | 0 | 0 | packed files 0x1FF..0x221 |
| $C8 | 0x240000 | 0 | 0 | 0 | 8000 | 0 | 0 | 0 | 0 | 0 | packed files 0x221..0x238 |
| $C9 | 0x248000 | 0 | 0 | 0 | 8000 | 0 | 0 | 0 | 0 | 0 | packed files 0x238..0x24E |
| $CA | 0x250000 | 0 | 0 | 0 | 8000 | 0 | 0 | 0 | 0 | 0 | packed files 0x24E..0x26E |
| $CB | 0x258000 | 0 | 0 | 0 | 8000 | 0 | 0 | 0 | 0 | 0 | packed files 0x26E..0x283 |
| $CC | 0x260000 | 0 | 0 | 0 | 8000 | 0 | 0 | 0 | 0 | 0 | packed files 0x283..0x28B |
| $CD | 0x268000 | 0 | 0 | 0 | 8000 | 0 | 0 | 0 | 0 | 0 | packed files 0x28B..0x28F |
| $CE | 0x270000 | 0 | 0 | 0 | 8000 | 0 | 0 | 0 | 0 | 0 | packed files 0x28F..0x29A |
| $CF | 0x278000 | 0 | 0 | 0 | 6301 | 0 | F92 | 0 | BC5 | 1A8 | packed files 0x29A..0x2A8 |

## Range table (coarse; ranges under 0x400 bytes are folded into their neighbours, see `l2_ranges.txt` for every range)

| File offset | SNES | Size | Kind | What | How known |
|---|---|---|---|---|---|
| 0x000000-0x009689 | $80:8000 | 0x968A | code | code [mixed: code 84.4%, unknown 7.7%, jump table 2.5%, data (referenced) 2.5%] | recursive descent from vectors/tables |
| 0x00968A-0x00B443 | $81:968A | 0x1DBA | unknown | unknown [mixed: unknown 57.4%, code 35.6%, code? 5.7%, jump table 1.3%] | not identified |
| 0x00B444-0x01CAB8 | $81:B444 | 0x11675 | code | code [mixed: code 93.0%, unknown 2.7%, code? 2.3%, data (referenced) 1.6%] | recursive descent from vectors/tables |
| 0x01CAB9-0x01F40F | $83:CAB9 | 0x2957 | unknown | unknown [mixed: code 51.7%, unknown 39.1%, code? 5.9%, jump table 2.0%] | not identified |
| 0x01F410-0x02105F | $83:F410 | 0x1C50 | code | code [mixed: code 81.1%, code? 8.3%, unknown 4.8%, data (known table) 4.6%] | recursive descent from vectors/tables |
| 0x021060-0x021F19 | $84:9060 | 0xEBA | data (known table) | IP attack records (follow the IP pointer table; names inline) [mixed: data (known table) 73.8%, code 19.3%, data (referenced) 5.4%, unknown 1.5%] | start known; extent = to next classified byte (guess) |
| 0x021F1A-0x0278D1 | $84:9F1A | 0x59B8 | code | code [mixed: code 92.0%, data (referenced) 6.2%, unknown 1.0%, code? 0.7%] | recursive descent from vectors/tables |
| 0x0278D2-0x02884F | $84:F8D2 | 0xF7E | data (referenced) | referenced by code [mixed: data (referenced) 48.1%, code 29.1%, unknown 20.6%, code? 2.2%] | code reference; extent = to next classified byte (guess) |
| 0x028850-0x029E46 | $85:8850 | 0x15F7 | code | code [mixed: code 90.2%, code? 9.2%, unknown 0.4%, jump table 0.2%] | recursive descent from vectors/tables |
| 0x029E47-0x02ACDE | $85:9E47 | 0xE98 | data (referenced) | referenced by code [mixed: data (referenced) 59.8%, code 26.8%, code? 12.6%, unknown 0.8%] | code reference; extent = to next classified byte (guess) |
| 0x02ACDF-0x02B581 | $85:ACDF | 0x8A3 | unknown | unknown [mixed: unknown 86.4%, jump table 5.1%, code 4.3%, code? 4.3%] | not identified |
| 0x02B582-0x02F3EA | $85:B582 | 0x3E69 | code | code [mixed: code 86.2%, data (referenced) 5.6%, code? 4.3%, unknown 2.0%] | recursive descent from vectors/tables |
| 0x02F3EB-0x02FAB5 | $85:F3EB | 0x6CB | data (known table) | enemy attack names | pointer table (exact end) |
| 0x02FAB6-0x0301CF | $85:FAB6 | 0x71A | unknown | unknown [mixed: unknown 74.5%, code 23.4%, code? 2.1%] | not identified |
| 0x0301D0-0x032F2F | $86:81D0 | 0x2D60 | code | code [mixed: code 93.1%, data (referenced) 3.1%, code? 2.0%, unknown 1.3%] | recursive descent from vectors/tables |
| 0x032F30-0x0358B5 | $86:AF30 | 0x2986 | unknown | unknown [mixed: unknown 79.3%, code? 10.0%, code 9.4%, data (referenced) 1.4%] | not identified |
| 0x0358B6-0x037FFF | $86:D8B6 | 0x274A | data (referenced) | referenced by code [mixed: data (referenced) 72.3%, code 22.4%, code? 3.3%, unknown 1.9%] | code reference; extent = to next classified byte (guess) |
| 0x038000-0x03880F | $87:8000 | 0x810 | data (known table) | map header table $87:8000 (0x10 head + 0x100 x 8) | verified (BRIEF) |
| 0x038810-0x03914D | $87:8810 | 0x93E | text/scripts | map names [mixed: text/scripts 99.4%, unknown 0.6%] | map header name offsets (exact end) |
| 0x03914E-0x03AF4D | $87:914E | 0x1E00 | text/scripts | map start-up scripts (242 maps) [mixed: text/scripts 96.7%, unknown 1.8%, free (FF/00) 1.6%] | map header pointers; end = last script start + first 00 (lower bound) |
| 0x03AF4E-0x0735FA | $87:AF4E | 0x386AD | text/scripts | map event/dialog blocks ("PH") [mixed: text/scripts 99.3%, code 0.4%, code? 0.2%, data (referenced) 0.0%] | map header pointers; each block runs to the next; last one to the next code ref (guess) |
| 0x0735FB-0x074384 | $8E:B5FB | 0xD8A | code | code [mixed: code 96.6%, code? 2.4%, unknown 0.4%, jump table 0.3%] | recursive descent from vectors/tables |
| 0x074385-0x074B9E | $8E:C385 | 0x81A | unknown | unknown | not identified |
| 0x074B9F-0x0769FF | $8E:CB9F | 0x1E61 | data (referenced) | referenced by code [mixed: data (referenced) 86.6%, unknown 6.2%, code 6.0%, code? 1.2%] | code reference; extent = to next classified byte (guess) |
| 0x076A00-0x076EFF | $8E:EA00 | 0x500 | data (known table) | dictionary pointer table $8E:EA00 (0x280 x u16) | verified (BRIEF) |
| 0x076F00-0x0782D5 | $8E:EF00 | 0x13D6 | text/scripts | dictionary strings [mixed: text/scripts 71.5%, unknown 14.2%, data (known table) 13.6%, free (FF/00) 0.7%] | pointer table (exact end) |
| 0x0782D6-0x083FFF | $8F:82D6 | 0xBD2A | unknown | unknown | not identified |
| 0x084000-0x08BABA | $90:C000 | 0x7ABB | data (referenced) | referenced by code [mixed: data (referenced) 99.9%, free (FF/00) 0.1%] | code reference; extent = to next classified byte (guess) |
| 0x08BABB-0x08FFC9 | $91:BABB | 0x450F | unknown | unknown | not identified |
| 0x08FFCA-0x0A81B7 | $91:FFCA | 0x181EE | data (referenced) | referenced by code [mixed: data (referenced) 99.7%, free (FF/00) 0.2%, unknown 0.1%] | code reference; extent = to next classified byte (guess) |
| 0x0A81B8-0x0AF1EE | $95:81B8 | 0x7037 | unknown | unknown | not identified |
| 0x0AF1EF-0x0AFAAA | $95:F1EF | 0x8BC | data (referenced) | referenced by code [mixed: data (referenced) 96.4%, data (known table) 3.6%] | code reference; extent = to next classified byte (guess) |
| 0x0AFAAB-0x0AFF15 | $95:FAAB | 0x46B | data (known table) | spell records (follow the spell offset table) | start known; extent = to next classified byte (guess) |
| 0x0AFF16-0x0B077F | $95:FF16 | 0x86A | data (referenced) | referenced by code [mixed: data (referenced) 79.2%, data (known table) 20.8%] | code reference; extent = to next classified byte (guess) |
| 0x0B0780-0x0B530E | $96:8780 | 0x4B8F | data (known table) | monster records ("Goblin" first; follow the monster offset table) | start known; extent = to next classified byte (guess) |
| 0x0B530F-0x0B7ADC | $96:D30F | 0x27CE | data (known table) | item records (follow the item offset table) | start known; extent = to next classified byte (guess) |
| 0x0B7ADD-0x0BBE92 | $96:FADD | 0x43B6 | data (referenced) | referenced by code [mixed: data (referenced) 89.9%, unknown 7.3%, data (known table) 2.0%, free (FF/00) 0.8%] | code reference; extent = to next classified byte (guess) |
| 0x0BBE93-0x0BC552 | $97:BE93 | 0x6C0 | data (known table) | encounter groups (0xD8 x 8) | verified (BRIEF) |
| 0x0BC553-0x0BCCF7 | $97:C553 | 0x7A5 | unknown | unknown [mixed: unknown 82.7%, data (known table) 13.7%, data (referenced) 3.6%] | not identified |
| 0x0BCCF8-0x0BDCFD | $97:CCF8 | 0x1006 | data (referenced) | referenced by code [mixed: data (referenced) 92.8%, free (FF/00) 4.7%, data (known table) 1.7%, unknown 0.8%] | code reference; extent = to next classified byte (guess) |
| 0x0BDCFE-0x0BF09E | $97:DCFE | 0x13A1 | data (known table) | capsule monster records ("Foomy S" first, BRIEF) [mixed: data (known table) 92.2%, free (FF/00) 7.7%, unknown 0.2%] | start known; extent = to next classified byte (guess) |
| 0x0BF09F-0x0BF63A | $97:F09F | 0x59C | unknown | unknown | not identified |
| 0x0BF63B-0x0CF86F | $97:F63B | 0x10235 | data (referenced) | referenced by code [mixed: data (referenced) 90.7%, unknown 8.3%, free (FF/00) 1.0%] | code reference; extent = to next classified byte (guess) |
| 0x0CF870-0x0CFFFF | $99:F870 | 0x790 | unknown | unknown | not identified |
| 0x0D0000-0x0D796F | $9A:8000 | 0x7970 | data (known table) | world map big blocks (l2ref 9: same as German) | start known; extent = to next classified byte (guess) |
| 0x0D7970-0x0E339F | $9A:F970 | 0xBA30 | data (referenced) | referenced by code [mixed: data (referenced) 72.4%, unknown 15.3%, free (FF/00) 12.2%] | code reference; extent = to next classified byte (guess) |
| 0x0E33A0-0x0F2FFF | $9C:B3A0 | 0xFC60 | unknown | unknown [mixed: unknown 83.3%, free (FF/00) 16.7%] | not identified |
| 0x0F3000-0x0F47E7 | $9E:B000 | 0x17E8 | data (known table) | world map small blocks (l2ref 9) | start known; extent = to next classified byte (guess) |
| 0x0F47E8-0x0F5DCB | $9E:C7E8 | 0x15E4 | data (known table) | item names (0x1D3 x 12) | verified (BRIEF) |
| 0x0F5DCC-0x0F7FBC | $9E:DDCC | 0x21F1 | data (referenced) | referenced by code [mixed: data (referenced) 63.9%, unknown 27.6%, free (FF/00) 8.6%] | code reference; extent = to next classified byte (guess) |
| 0x0F7FBD-0x0F8D3D | $9E:FFBD | 0xD81 | unknown | unknown [mixed: unknown 80.7%, free (FF/00) 19.3%] | not identified |
| 0x0F8D3E-0x0FA87F | $9F:8D3E | 0x1B42 | free (FF/00) | run of 00 [mixed: unknown 64.4%, free (FF/00) 35.6%] | byte scan (>= 32 bytes) |
| 0x0FA880-0x12DF43 | $9F:A880 | 0x336C4 | unknown | unknown [mixed: unknown 86.1%, free (FF/00) 13.7%, data (referenced) 0.2%] | not identified |
| 0x12DF44-0x12E5FB | $A5:DF44 | 0x6B8 | data (known table) | capsule monster attack names (BRIEF, "near") [mixed: data (known table) 81.4%, unknown 9.9%, free (FF/00) 8.7%] | start known; extent = to next classified byte (guess) |
| 0x12E5FC-0x1333FF | $A5:E5FC | 0x4E04 | unknown | unknown [mixed: unknown 86.3%, free (FF/00) 11.1%, data (referenced) 2.6%] | not identified |
| 0x133400-0x13475F | $A6:B400 | 0x1360 | data (known table) | world map palettes (l2ref 9) [mixed: data (known table) 45.9%, unknown 44.4%, free (FF/00) 9.8%] | start known; extent = to next classified byte (guess) |
| 0x134760-0x13741F | $A6:C760 | 0x2CC0 | unknown | unknown [mixed: unknown 89.3%, free (FF/00) 10.7%] | not identified |
| 0x137420-0x137FFF | $A6:F420 | 0xBE0 | data (referenced) | referenced by code [mixed: data (referenced) 83.1%, unknown 12.6%, free (FF/00) 4.3%] | code reference; extent = to next classified byte (guess) |
| 0x138000-0x1387FA | $A7:8000 | 0x7FB | file table | file table $A7:8000 (0x2A9 x u24) | verified (BRIEF) |
| 0x1387FB-0x27E300 | $A7:87FB | 0x145B06 | packed files | packed files 0x000..0x2A8 (681 files) | file table + decompressor walk (exact) |
| 0x27E301-0x27EFFF | $CF:E301 | 0xCFF | unknown | unknown [mixed: unknown 87.3%, free (FF/00) 12.7%] | not identified |
| 0x27F000-0x27FFFF | $CF:F000 | 0x1000 | data (known table) | map sprite table $CF:F000 [mixed: data (known table) 97.3%, unknown 2.7%] | l2ref 9 (US-verified) |

## Important code entry points

Roles marked (read) were checked by reading the code; (auto) = summary of the I/O registers and ROM tables the routine touches; (guess) = inferred.

| Address | Callers | Role |
|---|---|---|
| $80:8000 | 0 | (read) RESET (emulation vector) |
| $80:862D | 0 | (read) NMI handler (native and emulation vectors) |
| $80:86C0 | 0 | (read) IRQ handler (native IRQ, emulation IRQ/BRK) |
| $80:8E9D | 48 | (read) DECOMPRESS packed file: file index in $54, table $A7:8000, destination $60-$62 (US address; LEdit's German one is $80:8EAF, 0x12 later) |
| $80:9D00 | 0 | (read) event/text script loop: byte < $10 = command, $10-$7F = character, >= $80 = dictionary word |
| $80:9D32 | 0 | (read) event command dispatch: JSR $C0B7 then JMP ($CA14,X); 0xCD commands 00..CC (02, 11, 5C, 5D point at BRK/padding) |
| $80:9E4E | 0 | (read) event command 06: dictionary word (LDA $8EEA00,X) |
| $80:BEAF | 3 | (read) run map start-up script: map header +3 -> $09B7/$09B9, same pointer rule |
| $80:C0B7 | 259 | (read) read next script byte (LDA $0000,Y / INY with DB = script bank) |
| $80:C652 | 3 | (read) text output helper that expands dictionary words ($8E:EA00) |
| $80:C9C0 | 3 | (read) copy a map name from $87:8000+ to RAM at Y, expanding 0A back-reference codes |
| $80:CC3F | 0 | (read) another script interpreter: JMP ($E5A4,X) / JSR ($E59E,X) at $80:E58B, ~194 opcodes (guess: map sprite / cutscene movement scripts) |
| $80:EAE7 | 1 | (read) MAP LOADER: A = map; clears $7F:D000.., unpacks file 0x3F + map to $7F:0000, then reads block set ($CF:F72E) and anim lists ($CF:F90E/$CF:F9FE) |
| $80:EF8E | 1 | (read) map graphics loader for map $05AC: tile set list $CF:F81E, anim lists, palette set $CF:FA08/$CF:FAFA, extra file list $CF:FBA6 -> $CF:FC98 |
| $80:FFAA | 0 | (read) COP / BRK / ABORT vector target |
| $81:8351 | 3 | (guess) random encounter set-up (per-map lists $97:B9AC) |
| $81:F291 | 4 | (guess) item table lookup ($96:CF69) |
| $81:F2A9 | 1 | (guess) item name lookup ($9E:C7E8) |
| $81:F446 | 4 | (guess) spell table lookup ($95:FA5B) |
| $81:F45E | 3 | (guess) IP attack lookup ($84:8F10) |
| $81:FB1A | 1 | (guess) monster record reader ($96:85C0) |
| $81:FB8E | 12 | (guess) monster sprite size lookup ($97:CA64, 12 callers) |
| $81:FBC6 | 12 | (guess) monster table lookup helper ($96:85C0, 12 callers) |
| $82:8000 | 14 | (read) 16x16 multiply, $1570 x $1572 -> $1574/$1576 (shift and add) |
| $82:8028 | 20 | (read) inline jump-table dispatcher: "JSR $8028" is followed by a u16 handler table, A = index (only indirect JMP in the code) |
| $82:E729 | 1 | (guess) shop menu (pointer #$97:EE9F) |
| $83:A82E | 4 | (guess) place map sprites/NPCs (reads $CF:F000 sprite table) |
| $83:AD23 | 0 | (read) map entry sequence (JML target): event block ptr ($83:B5AD), map info ($83:B5D3), map sprites ($83:A82E), start-up script ($80:BEAF), map load ($83:B53B) |
| $83:B53B | 4 | (read) load current map ($05AC) via $80:EAE7, then copies rows with MVN (uses $4202 multiply) |
| $83:B5AD | 4 | (read) map header +0: event block pointer -> $099E/$09A0 (addr = lo16 + $8000, bank = $87 + hi; carry dropped) |
| $83:B5D3 | 6 | (guess) map info reader ($CF:FCBC u24 pointers) |
| $85:9532 | 6 | (guess) enemy attack name lookup ($85:F2D7) |
| $85:B460 | 0 | (read) battle script interpreter: opcode = [$BB], JMP ($B483,X) (table used from $B485 = opcode 1; 0x5C opcodes) |
| $85:C516 | 0 | (read) battle text: dictionary word expansion ($8E:EA00) |

### Most-called routines

| Address | Callers | Size (instr.) | Auto summary |
|---|---|---|---|
| $80:C0B7 | 259 | 15 | (read) read next script byte (LDA $0000,Y / INY with DB = script bank) |
| $80:8878 | 151 | 11 |  |
| $80:E8B9 | 146 | 12 |  |
| $80:8299 | 128 | 27 | mul/div x3 |
| $85:EC81 | 104 | 42 |  |
| $80:953B | 102 | 13 | APU x1 |
| $86:8B55 | 82 | 15 |  |
| $84:A5A8 | 75 | 9 |  |
| $85:BFBF | 72 | 7 |  |
| $82:895B | 60 | 16 | reads $82:897D |
| $83:AB4F | 58 | 10 |  |
| $82:810E | 55 | 113 |  |
| $84:A607 | 51 | 2 |  |
| $80:8E9D | 48 | 232 | (read) DECOMPRESS packed file: file index in $54, table $A7:8000, destination $60-$62 (US address; LEdit's German one is $80:8EAF, 0x12 later) |
| $80:E9ED | 46 | 16 |  |
| $85:8A2F | 44 | 6 |  |
| $85:ECDB | 40 | 13 |  |
| $80:9A0A | 37 | 22 | APU x6 |
| $80:99FD | 33 | 6 | APU x2 |
| $83:D350 | 33 | 36 | reads $83:C1B0 |
| $86:8B32 | 33 | 10 |  |
| $81:F1C5 | 31 | 93 |  |
| $80:C0EC | 29 | 14 |  |
| $85:95FE | 28 | 48 |  |
| $82:8B08 | 28 | 33 |  |
| $82:838F | 26 | 20 |  |
| $84:A2B3 | 26 | 82 |  |
| $86:8CF5 | 25 | 38 |  |
| $82:83EB | 25 | 30 |  |
| $86:974F | 25 | 6 |  |
| $84:CBCD | 25 | 12 |  |
| $81:DDE7 | 24 | 48 |  |
| $82:93F6 | 24 | 26 | reads $8E:E57E, $8E:E5A0 |
| $80:BFAA | 23 | 31 |  |
| $83:F9AD | 23 | 5 |  |
| $80:BF92 | 23 | 12 |  |
| $85:C023 | 23 | 34 |  |
| $80:EA09 | 23 | 29 |  |
| $80:E9BC | 23 | 25 | mul/div x3 |
| $83:C0EF | 22 | 5 |  |
| $83:FB71 | 22 | 3 |  |
| $81:DE9E | 22 | 5 |  |
| $86:973E | 22 | 5 |  |
| $85:BFED | 22 | 28 |  |
| $84:E9B1 | 22 | 26 |  |

### Routines touching hardware (auto)

| Address | I/O use |
|---|---|
| $86:CEF6 | DMA x65, PPU x32, VRAM x7, CGRAM x6 |
| $85:8DC5 | PPU x21, DMA x7, VRAM x1 |
| $84:A455 | PPU x15, DMA x6, VRAM x1 |
| $86:CE68 | DMA x17, NMITIMEN x2, WRAM port x2, VRAM x1 |
| $80:8117 | DMA x16, VRAM x2, OAM x2, CGRAM x1 |
| $84:8BC7 | PPU x12, DMA x8 |
| $86:CD91 | DMA x14, WRAM port x3 |
| $82:8D44 | DMA x11, PPU x4, VRAM x1 |
| $80:8703 | DMA x12, OAM x1, CGRAM x1, joypad x1 |
| $81:851E | DMA x12, VRAM x2, PPU x1 |
| $80:EF8E | DMA x12, VRAM x2 |
| $83:B007 | DMA x12, VRAM x2 |
| $83:B062 | PPU x13, DMA x1 |
| $86:92DF | PPU x8, DMA x6 |
| $80:BAF1 | DMA x9, PPU x4 |
| $84:8193 | DMA x11, VRAM x2 |
| $86:AD82 | DMA x7, NMITIMEN x2, PPU x2, VRAM x1 |
| $80:C56E | DMA x10 |
| $86:CBBF | DMA x7, NMITIMEN x1, CGRAM x1 |
| $80:9C0F | APU x8 |
| $86:87D3 | DMA x6, PPU x1, VRAM x1 |
| $86:CD67 | DMA x6, PPU x1, VRAM x1 |
| $80:882E | DMA x6, VRAM x1 |
| $80:97E5 | APU x7 |
| $80:9945 | APU x7 |
| $84:A569 | DMA x6, VRAM x1 |
| $8E:B557 | DMA x6, VRAM x1 |
| $80:9A0A | APU x6 |
| $83:B18E | NMITIMEN x3, PPU x2, DMA x1 |
| $84:F12F | DMA x6 |
| $80:9383 | APU x4, joypad x1 |
| $80:941A | APU x5 |
| $80:BD38 | DMA x5 |
| $80:C23D | DMA x5 |
| $80:C8D5 | DMA x5 |
| $82:8044 | DMA x5 |
| $82:CFD4 | DMA x5 |
| $82:CFFD | DMA x5 |
| $83:AF54 | DMA x5 |
| $84:82E7 | DMA x5 |

## Jump tables found

| Table | Entries | Kind | Dispatch site |
|---|---|---|---|
| $80:8A81 | 12 | JMP (abs,X) | $80:8A7E |
| $80:8C11 | 4 | JMP (abs,X) | $80:8C0E |
| $80:8E7C | 16 | JMP (abs,X) | $80:88C5 |
| $80:B5BC | 0 | JSR (abs,X) | $80:B579 |
| $80:BA55 | 9 | JSR (abs,X) | $80:BA42 |
| $80:CA14 | 205 | JMP (abs,X) | $80:9D3B |
| $80:DA53 | 5 | JSR (abs,X) | $80:DA43 |
| $80:E59E | 194 | JSR (abs,X) | $80:E58B |
| $80:E5A4 | 191 | JMP (abs,X) | $80:CC3F |
| $81:8C5C | 36 | JSR (abs,X) | $81:8AA4 |
| $81:8C5C | 36 | JSR (abs,X) | $81:8B15 |
| $81:A867 | 32 | JMP (abs,X) | $81:A864 |
| $81:AFD9 | 16 | JMP (abs,X) | $81:AFD5 |
| $82:978E | 5 | JSR (abs,X) | $82:9736 |
| $82:A0D4 | 3 | inline after JSR $8028 | $82:A0D1 |
| $82:A322 | 5 | inline after JSR $8028 | $82:A31F |
| $82:A4C1 | 8 | inline after JSR $8028 | $82:A4BE |
| $82:B14E | 5 | inline after JSR $8028 | $82:B14B |
| $82:B80E | 5 | inline after JSR $8028 | $82:B80B |
| $82:BF52 | 3 | inline after JSR $8028 | $82:BF4F |
| $82:BF5E | 4 | inline after JSR $8028 | $82:BF5B |
| $82:C047 | 3 | inline after JSR $8028 | $82:C044 |
| $82:C107 | 3 | inline after JSR $8028 | $82:C104 |
| $82:DBCC | 4 | inline after JSR $8028 | $82:DBC9 |
| $82:DC16 | 4 | inline after JSR $8028 | $82:DC13 |
| $82:DC4C | 4 | inline after JSR $8028 | $82:DC49 |
| $82:DCA6 | 4 | inline after JSR $8028 | $82:DCA3 |
| $82:DD2A | 4 | inline after JSR $8028 | $82:DD27 |
| $82:DDC3 | 4 | inline after JSR $8028 | $82:DDC0 |
| $82:DFA8 | 3 | inline after JSR $8028 | $82:DFA5 |
| $82:E293 | 2 | inline after JSR $8028 | $82:E290 |
| $82:E74B | 9 | inline after JSR $8028 | $82:E748 |
| $82:E8CE | 8 | inline after JSR $8028 | $82:E8CB |
| $82:FDF2 | 2 | inline after JSR $8028 | $82:FDEF |
| $83:A48A | 4 | JSR (abs,X) | $83:A461 |
| $83:A589 | 4 | JSR (abs,X) | $83:A553 |
| $83:BA54 | 4 | JSR (abs,X) | $83:BA1C |
| $83:BE33 | 4 | JSR (abs,X) | $83:BE16 |
| $83:D3AF | 4 | JSR (abs,X) | $83:D370 |
| $83:D467 | 4 | JMP (abs,X) | $83:C864 |
| $83:D8A3 | 4 | JSR (abs,X) | $83:D89F |
| $83:D92A | 4 | JMP (abs,X) | $83:D927 |
| $83:DD18 | 4 | JSR (abs,X) | $83:DCCD |
| $83:DF17 | 56 | JMP (abs,X) | $83:D5D1 |
| $83:DF37 | 40 | JMP (abs,X) | $83:D5DD |
| $83:DF57 | 24 | JMP (abs,X) | $83:D5E9 |
| $83:DF77 | 8 | JMP (abs,X) | $83:D5F5 |
| $83:F2C8 | 9 | JMP (abs,X) | $83:E119 |
| $83:F328 | 21 | JMP (abs,X) | $83:ED54 |
| $83:F348 | 5 | JMP (abs,X) | $83:ED4E |
| $83:F368 | 1 | JMP (abs,X) | $83:ED48 |
| $83:FB1A | 4 | JMP (abs,X) | $83:FB17 |
| $83:FBC6 | 4 | JSR (abs,X) | $83:FBC2 |
| $85:9422 | 5 | JMP (abs,X) | $85:941F |
| $85:B485 | 92 | JMP (abs,X) | $85:B470 |
| $85:BBB6 | 9 | JMP (abs,X) | $85:BBB3 |
| $86:98A6 | 16 | JSR (abs,X) | $86:9881 |
| $86:A367 | 7 | JSR (abs,X) | $86:CCE1 |
| $86:A46B | 4 | JSR (abs,X) | $86:A45E |
| $86:A9A8 | 4 | JSR (abs,X) | $86:A8E7 |
| $86:A9A8 | 4 | JSR (abs,X) | $86:A900 |
| $86:AF0A | 0 | JSR (abs,X) | $86:AEF6 |
| $86:E281 | 3 | JSR (abs,X) | $86:E26B |
| $86:E3DF | 3 | JSR (abs,X) | $86:E3DB |
| $86:E5DA | 4 | JSR (abs,X) | $86:E4DB |
| $86:E5DA | 4 | JSR (abs,X) | $86:E549 |
| $86:E5DA | 4 | JSR (abs,X) | $86:E5CE |
| $86:E5DA | 4 | JSR (abs,X) | $86:EA18 |
| $86:E5DA | 4 | JSR (abs,X) | $86:EB45 |
| $8E:BE6E | 5 | JSR (abs,X) | $8E:BDD7 |

Table slots that point at BRK/COP/STP/WDM (unused opcodes, not traced): $80:CA14[02] -> $80:9DD4, $80:CA14[11] -> $80:BF0A, $80:CA14[5C] -> $80:BF08, $80:CA14[5D] -> $80:BF09, $85:B485[01] -> $85:B550, $85:B485[30] -> $85:BBA1, $85:B485[32] -> $85:BCCD, $85:B485[33] -> $85:BCCE, $85:B485[37] -> $85:BD2B, $85:B485[38] -> $85:BD2B, $85:B485[39] -> $85:BD2B, $85:B485[3A] -> $85:BD2B.

Indirect jumps not followed: $82:8041 `JMP ($0060)`, $81:92E4 `JML [$129B]`, $82:BADE `JMP ($0008)`, $85:8529 `JMP ($6520)` (in speculative code). $82:8041 is the inline-table dispatcher (its tables are followed); `JML [$129B]` jumps through a RAM vector, so its targets are only found if they are also called directly.

Decode conflicts (a branch or call landing inside an already decoded instruction; usually a wrong M/X guess after a JSR): $80:C431 (branch), $83:B846 (JSL), $83:C0DA (branch), $83:C0E4 (branch), $83:D5CB (branch), $83:E116 (branch), $83:FAFA (JSR), $85:82FB (branch).

## Biggest unknown regions (not code, not files, not text, not referenced)

| File offset | SNES | Size | First bytes | Note |
|---|---|---|---|---|
| 0x0782D6-0x083FFF | $8F:82D6 | 0xBD2A | `20 00 2d 00 31 00 32 00 36 00 37 00` |  |
| 0x0A81B8-0x0AF1EE | $95:81B8 | 0x7037 | `35 87 56 87 74 87 88 8b 0e 8b 87 8b` |  |
| 0x08BABB-0x08FFC9 | $91:BABB | 0x450F | `00 00 29 01 01 0a 02 02 11 ff 00 03` |  |
| 0x11FD40-0x123CFC | $A3:FD40 | 0x3FBD | `b1 7f 8d 7f b5 5f 49 3f 61 1f 55 2f` |  |
| 0x12970C-0x12BAEC | $A5:970C | 0x23E1 | `07 00 19 07 00 00 00 00 00 00 00 00` |  |
| 0x119740-0x11B6FB | $A3:9740 | 0x1FBC | `8d 2d 89 29 db 69 b3 7b f2 6b 86 6d` |  |
| 0x0EAF40-0x0ECEFB | $9D:AF40 | 0x1FBC | `47 2f 42 2b 62 0b 70 25 d4 6b 5b 07` |  |
| 0x032F30-0x034BBE | $86:AF30 | 0x1C8F | `02 c0 0f c0 41 c0 4e c0 60 60 a2 55` |  |
| 0x10A8F7-0x10C21B | $A1:A8F7 | 0x1925 | `7e 7e 7e 7e 00 7e 7e 7e 6a 00 7e 6b` |  |
| 0x12E5FC-0x12FDF9 | $A5:E5FC | 0x17FE | `03 00 0d 03 17 09 17 09 33 1c 20 1f` |  |
| 0x116540-0x1178FB | $A2:E540 | 0x13BC | `2b 00 53 00 53 00 39 00 59 00 ad 00` |  |
| 0x102AA0-0x103BEE | $A0:AAA0 | 0x114F | `78 20 7f 28 7f 08 0b 00 07 00 05 00` |  |
| 0x00968A-0x00A799 | $81:968A | 0x1110 | `a9 7e 48 ab a7 c3 0a 0a 0a 0a 85 00` |  |
| 0x0FA880-0x0FB964 | $9F:A880 | 0x10E5 | `02 01 07 03 0f 07 1f 0f 3f 1f 7f 3f` |  |
| 0x12FF7C-0x130FE2 | $A5:FF7C | 0x1067 | `0f 00 01 00 00 00 00 00 00 00 00 00` |  |
| 0x114540-0x1153F2 | $A2:C540 | 0xEB3 | `36 0a 0d 02 03 01 02 00 03 01 01 00` |  |
| 0x1271C8-0x127FF8 | $A4:F1C8 | 0xE31 | `02 02 01 01 2a 2a 15 15 00 00 00 00` |  |
| 0x100A80-0x10185E | $A0:8A80 | 0xDDF | `3d 03 6a 3d a7 78 ce 7d fe 7d d6 7d` |  |
| 0x124140-0x124EFD | $A4:C140 | 0xDBE | `88 3b 85 1d 46 1f 42 0f 20 07 38 00` |  |
| 0x11E540-0x11F2FD | $A3:E540 | 0xDBE | `bb 7b 9f 7f df 3f 47 3f 49 3f 41 1b` |  |
| 0x134760-0x13551B | $A6:C760 | 0xDBC | `50 39 69 1b d2 06 68 07 74 00 6e 10` |  |
| 0x111040-0x111DF9 | $A2:9040 | 0xDBA | `4d 00 23 00 34 00 3b 00 5a 01 77 08` |  |
| 0x11C140-0x11CDFE | $A3:C140 | 0xCBF | `6f ff 6f ff 67 ff 37 ff 33 7f 1b 7f` |  |
| 0x125340-0x125FFB | $A4:D340 | 0xCBC | `6f 3f 6d 3f 55 3f 54 3f 5d 3f e9 7f` |  |
| 0x128540-0x1291F8 | $A5:8540 | 0xCB9 | `6f 03 bc 6f f7 6f ff 6f ab 7f eb 1f` |  |
| 0x108B0A-0x1096F0 | $A1:8B0A | 0xBE7 | `03 00 06 03 0a 07 00 00 00 00 00 00` |  |
| 0x136960-0x13741F | $A6:E960 | 0xAC0 | `37 03 63 30 cd 70 da 60 b6 42 d6 00` |  |
| 0x113240-0x113BF2 | $A2:B240 | 0x9B3 | `ee 47 4f 01 5f 08 5f 08 3f 00 3f 00` |  |
| 0x01CAB9-0x01D34F | $83:CAB9 | 0x897 | `a6 a7 b9 01 00 9f c6 e3 7f 20 93 ca` |  |
| 0x102220-0x102A7C | $A0:A220 | 0x85D | `07 00 0f 01 17 03 2f 00 23 00 5b 18` |  |

## Free space (runs of FF or 00, at least 32 bytes, outside code/files/known data)

Total 0xDA4D bytes in 822 runs; runs of 00 inside data banks may be part of tables (padding or zero rows), so only FF runs and runs at bank ends are safe bets.

| File offset | SNES | Size | Byte |
|---|---|---|---|
| 0x0F8D3E | $9F:8D3E | 0x4E2 | 00 |
| 0x12D7B6 | $A5:D7B6 | 0x34A | 00 |
| 0x12DBB6 | $A5:DBB6 | 0x34A | 00 |
| 0x0F84FF | $9F:84FF | 0x211 | 00 |
| 0x131F88 | $A6:9F88 | 0x1FE | 00 |
| 0x0BEF1D | $97:EF1D | 0x182 | 00 |
| 0x109828 | $A1:9828 | 0x14F | 00 |
| 0x0FC95D | $9F:C95D | 0x143 | 00 |
| 0x0FBA78 | $9F:BA78 | 0x140 | 00 |
| 0x0F9400 | $9F:9400 | 0x128 | 00 |
| 0x109E2C | $A1:9E2C | 0xF4 | 00 |
| 0x109992 | $A1:9992 | 0xE0 | 00 |
| 0x107BEF | $A0:FBEF | 0xD9 | 00 |
| 0x109AAC | $A1:9AAC | 0xD4 | 00 |
| 0x10A070 | $A1:A070 | 0xCC | 00 |
| 0x0FBBBB | $9F:BBBB | 0xC9 | 00 |
| 0x0FCBDE | $9F:CBDE | 0xC2 | 00 |
| 0x0E31F6 | $9C:B1F6 | 0xB8 | 00 |
| 0x0E2FF6 | $9C:AFF6 | 0xB4 | 00 |
| 0x0FC4CD | $9F:C4CD | 0xB3 | 00 |
| 0x0E2DF4 | $9C:ADF4 | 0xAC | 00 |
| 0x0E23D7 | $9C:A3D7 | 0xAB | 00 |
| 0x0E2BF7 | $9C:ABF7 | 0xA9 | 00 |
| 0x0F98FE | $9F:98FE | 0xA2 | 00 |
| 0x109F52 | $A1:9F52 | 0xA2 | 00 |
| 0x0E3300 | $9C:B300 | 0xA0 | 00 |
| 0x12BAED | $A5:BAED | 0x9D | 00 |
| 0x0FDA78 | $9F:DA78 | 0x96 | 00 |
| 0x0E2EEF | $9C:AEEF | 0x95 | 00 |
| 0x0E30EF | $9C:B0EF | 0x95 | 00 |

End of the file area: 0x27E301-0x27EFFF (0xCFF bytes) before the map lists at $CF:F000: `23 e7 18 df 20 ff 00 ff 00 ff 00 fe 01 fe 01 00` ...

## Findings and notes

* Code lives only in banks $80-$86 (about 74% of those 0x38000 bytes reached) plus part of $8E ($8E:B5FB-$8E:C384 and around $8E:E6xx). No reached code anywhere in $87-$CF; the 65816 never executes from the file area.
* The decompressor is **$80:8E9D** in the US ROM (file index in $54, destination pointer $60-$62), 48 JSL callers. Map n is unpacked by $80:EAE7 as file $3F + n to $7F:0000 (matches the BRIEF).
* Map header pointers are resolved by the game as addr = lo16 + $8000 (16-bit), bank = $87 + hi byte, with the carry of the 16-bit add dropped ($83:B5AD event block -> $099E/$09A0, $80:BEAF start-up script -> $09B7/$09B9). This is why every US offset has lo16 < $8000.
* Packed files tile 0x1387FB-0x27E300 exactly (681 entries, file 0x2A8 is empty: size 0). The only bytes between the file area and the map lists are 0x27E301-0x27EFFF, which hold non-FF data (not free).
* The ROM is essentially full: FF runs (>= 32 bytes) total only 0x14D bytes (0x1B80, 0x3AED6, 0x782B2, 0x8BA99). The 00 runs listed as free are almost all inside the unreferenced banks $9C-$A6 and are probably blank tiles / zero rows of uncompressed graphics or palettes, not usable space.
* Banks $8F-$A6 have no code and few direct references: the code reaches them through pointer tables held in data (e.g. palette sets via $CF:FAFA -> bank $9B), so "data (referenced)" extents there are guesses (a referenced address claims everything up to the next known byte) and "unknown" mostly means "reached indirectly", not "unused".
* Event/dialog ("PH") blocks fill $87:AF4E-$8E:A5FA+ (about 225 KB). The last block (map at $8E:A5FA) is over-extended here: its scripts seem to end near 0x72930, followed by about 0xCC0 bytes that look like a bit table, then code at $8E:B5FB.
* Three script interpreters: event/text ($80:9D32, table $80:CA14, 0xCD commands), battle ($85:B460, table $85:B485, opcodes 1..$5C), and a third one at $80:E58B/$80:CC3F (tables $80:E59E/$80:E5A4, about 194 opcodes, many slots = $CC42 no-op; role not yet identified).
* Bank $82 code dispatches with "JSR $8028" followed by an inline u16 table (20 such tables decoded).
* New per-map tables found: $CF:FBA6 (byte per map, 1-based index) -> $CF:FC98 (u16 packed-file numbers, 0x12 entries), read by $80:EF8E.
