"""Decompile the Lufia II (US) ROM into readable C#.

usage: python run.py <lufia2.sfc> <output folder>
The output is the game's own code in another form: keep it out of the public repository.
"""
import os
import re
import sys
from collections import Counter

from analyze import Rom, Analyzer

HERE = os.path.dirname(os.path.abspath(__file__))
ROMMAP = os.path.join(HERE, '..', '..', 'LufiaForge', 'docs', 'lufia2', 'rommap.md')

# tables whose slots don't follow the "stop at the first handler" rule
KNOWN_TABLES = {
    0x80CA14: (0, 0xCD),    # event commands 00-CC
    0x80E59E: (0, 194),     # puzzle commands (JSR)
    0x80E5A4: (0, 191),     # puzzle commands (JMP)
    0x85B483: (0, 0x5D),    # battle script opcodes 00-5C
    0x80B5BC: (1, 4),       # event command 41's sub-commands (slot 0 unused)
    0x86AF0A: (1, 22),      # event command 5B's world map scenes (slot 0 unused)
    0x83D467: (0, 0x50),    # walking script commands 00-4F (handlers run up to the table's end at $83:D507)
    0x818C5C: (0, 0x100),   # battle animation opcodes 00-FF
    0x83DF17: (0, 16),      # motion script ops (high nibble)
    0x83DF37: (0, 16),      # motion script ops Fx
    0x83DF57: (0, 16),      # motion script ops Ex
    0x83DF77: (0, 8),       # motion script ops Dx
    0x83F328: (0, 16),      # event object script ops
}


# routines whose own exit says nothing about the sizes their callers continue with
KEEP_SIZES = {
    0x849B3E,   # debug monster viewer: leaves with JML into the battle code (16-bit A), never returns in play
}


def rommap_tables():
    out = {}
    if not os.path.exists(ROMMAP):
        return out
    for m in re.finditer(r'^\| \$([0-9A-F]{2}):([0-9A-F]{4}) \| (\d+) \| (JMP \(abs,X\)|JSR \(abs,X\)|inline)',
                         open(ROMMAP, encoding='utf-8').read(), re.M):
        base = int(m.group(1) + m.group(2), 16)
        n = int(m.group(3))
        if n:
            out[base] = (0, n)
    return out


def seeds(rom):
    v = lambda a: 0x800000 | rom.u16(0x80FF00 + a)
    return [
        (v(0xFC), 1, 1, 0x00, 0),       # RESET (emulation)
        (v(0xEA), 1, 1, None, None),    # NMI
        (v(0xEE), 1, 1, None, None),    # IRQ
        (0x8092A4, 1, 0, None, 0),      # NMI hook of the licence screen (only reached through $0067)
    ]


def main():
    rom = Rom(sys.argv[1])
    tables = rommap_tables()
    tables.update(KNOWN_TABLES)
    an = Analyzer(rom, tables)
    an.keep_sizes = KEEP_SIZES
    an.run_all(seeds(rom))
    an.merge_variants()
    live = [f for f in an.funcs.values() if f.merged_into is None]
    addrs = set()
    for f in an.funcs.values():
        for a, i in f.insns.items():
            for k in range(i.size):
                addrs.add(a + k)
    starts = set()
    for f in an.funcs.values():
        starts |= set(f.insns)
    print('functions (variants kept):', len(live), ' entries:', len(an.entries))
    print('instructions:', len(starts), ' code bytes: 0x%X' % len(addrs))
    print('exits:', Counter(str(f.exit) for f in live).most_common())
    print('entry DB:', Counter(str(f.db) for f in live).most_common(8))
    print('entry D:', Counter(str(f.d) for f in live).most_common(8))
    per = Counter(a >> 16 for a in starts)
    print('per bank:', ' '.join('%02X:%d' % kv for kv in sorted(per.items())))
    multi = Counter(f.entry for f in live)
    print('entries with >1 variant:', sum(1 for c in multi.values() if c > 1))
    notes = Counter()
    for f in live:
        for n in f.notes:
            notes[n.split(':')[1].split('$')[0].strip() if ':' in n else n[:20]] += 1
    print('notes:', notes.most_common(10))
    if len(sys.argv) > 2:
        import shutil
        from syms import Symbols
        from emit import Program
        syms = Symbols().load_all(HERE)
        prog = Program(an, syms)
        prog.analyse()
        out = sys.argv[2]
        n = prog.write(out)
        shutil.copy(os.path.join(HERE, 'Runtime.cs'), os.path.join(out, 'Runtime.cs'))
        shutil.copy(os.path.join(HERE, 'Lufia2Decomp.csproj'), os.path.join(out, 'Lufia2Decomp.csproj'))
        shutil.copy(os.path.join(HERE, 'README.md'), os.path.join(out, 'README.md'))
        print('wrote %d lines to %s' % (n, out))
    return an


if __name__ == '__main__':
    main()
