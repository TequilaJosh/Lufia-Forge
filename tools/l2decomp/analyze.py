"""Recursive-descent analysis of the Lufia II (US) ROM: functions, blocks, M/X, DB and D.

Each function is decoded on its own from its entry state, so a routine called with
8-bit and 16-bit A gets one variant per state (identical variants are merged later).
"""
import sys
from ops import OPS, size, BRANCHES, BAD

sys.setrecursionlimit(20000)

M_DEP = {'ORA', 'AND', 'EOR', 'ADC', 'STA', 'LDA', 'CMP', 'SBC', 'BIT', 'STZ', 'INC', 'DEC',
         'ASL', 'LSR', 'ROL', 'ROR', 'TSB', 'TRB', 'PHA', 'PLA', 'TXA', 'TYA'}
X_DEP = {'LDX', 'LDY', 'STX', 'STY', 'CPX', 'CPY', 'INX', 'INY', 'DEX', 'DEY', 'PHX', 'PHY',
         'PLX', 'PLY', 'TAX', 'TAY', 'TXY', 'TYX', 'TSX', 'MVN', 'MVP'}


class Rom:
    def __init__(self, path):
        self.data = open(path, 'rb').read()

    def offset(self, a):
        bank, lo = a >> 16, a & 0xFFFF
        if lo < 0x8000 or bank in (0x7E, 0x7F):
            return None
        off = ((bank & 0x7F) * 0x8000) + (lo - 0x8000)
        return off if off < len(self.data) else None

    def u8(self, a):
        o = self.offset(a)
        return None if o is None else self.data[o]

    def u16(self, a):
        o = self.offset(a)
        return None if o is None or o + 1 >= len(self.data) else self.data[o] | self.data[o + 1] << 8


class Insn:
    __slots__ = ('addr', 'op', 'mn', 'mode', 'size', 'val', 'm8', 'x8', 'db', 'd', 'target',
                 'table')

    def __init__(self, addr, op, mn, mode, sz, val, m8, x8):
        self.addr, self.op, self.mn, self.mode, self.size, self.val = addr, op, mn, mode, sz, val
        self.m8, self.x8 = m8, x8
        self.db = self.d = None
        self.target = None      # branch / jump / call target (24-bit)
        self.table = None       # list of 24-bit targets for (abs,X) and inline tables

    @property
    def next(self):
        return (self.addr & 0xFF0000) | ((self.addr + self.size) & 0xFFFF)

    def sig(self):
        m = self.m8 if self.mn in M_DEP or self.mode == 'immM' else None
        x = self.x8 if self.mn in X_DEP or self.mode == 'immX' else None
        return (self.addr, self.op, self.val, m, x, self.db if self.mode in ('abs', 'absx', 'absy', 'idp', 'idpx', 'idpy', 'isry') else None)


def decode(rom, addr, m8, x8):
    op = rom.u8(addr)
    if op is None:
        return None
    mn, mode = OPS[op]
    sz = size(op, m8, x8)
    raw = [rom.u8(addr + i) for i in range(1, sz)]
    if any(b is None for b in raw):
        return None
    val = 0
    for i, b in enumerate(raw):
        val |= b << (8 * i)
    ins = Insn(addr, op, mn, mode, sz, val, m8, x8)
    bank = addr & 0xFF0000
    if mode == 'rel':
        ins.target = bank | ((addr + 2 + (val - 256 if val >= 128 else val)) & 0xFFFF)
    elif mode == 'rell':
        ins.target = bank | ((addr + 3 + (val - 65536 if val >= 32768 else val)) & 0xFFFF)
    elif mn in ('JSR', 'JMP') and mode == 'abs':
        ins.target = bank | val
    elif mn in ('JSL', 'JML') and mode == 'long':
        ins.target = val
    return ins


class Function:
    def __init__(self, entry, m8, x8):
        self.entry, self.m8, self.x8 = entry, m8, x8
        self.db = 'unset'           # entry DB: int, None (unknown) or 'unset'
        self.d = 'unset'
        self.insns = {}             # addr -> Insn
        self.exit = None            # (m8, x8) at returns, 'mixed' or None (never returns)
        self.calls = set()          # 24-bit callee entries
        self.tails = set()          # tail-jump targets that are other functions
        self.cross = {}
        self.notes = []
        self.done = False
        self.busy = False
        self.callers = set()
        self.merged_into = None

    @property
    def key(self):
        return (self.entry, self.m8, self.x8)


class Analyzer:
    INLINE_DISPATCH = 0x828028

    def __init__(self, rom, tables=None):
        self.rom = rom
        self.funcs = {}             # key -> Function
        self.entries = set()        # every 24-bit address that starts a function
        self.tables = tables or {}  # table addr -> entry count (known sizes)
        self.table_sites = {}       # table addr -> list of targets
        self.final = False
        self.keep_sizes = set()     # calls that leave the register sizes as they were (see run.py)

    # ---- functions -------------------------------------------------------
    def func(self, entry, m8, x8, db, d, caller=None):
        key = (entry, m8, x8)
        f = self.funcs.get(key)
        if f is None:
            f = self.funcs[key] = Function(entry, m8, x8)
            self.entries.add(entry)
        if caller is not None:
            f.callers.add(caller)
        changed = False
        if f.db == 'unset':
            f.db = db
        elif f.db != db and f.db is not None:
            f.db, changed = None, True
        if f.d == 'unset':
            f.d = d
        elif f.d != d and f.d is not None:
            f.d, changed = None, True
        if changed and f.done and not f.busy:
            f.done = False
        if not f.done and not f.busy:
            self.run(f)
        return f

    def exit_of(self, f, m8, x8):
        if f.entry in self.keep_sizes or f.exit in (None, 'mixed') or f.busy:
            return m8, x8
        return f.exit

    # ---- tables ----------------------------------------------------------
    def read_table(self, base, bank, known_code):
        """Targets of a u16 jump table (None for unused slots)."""
        known = self.tables.get(base)
        if known is not None:
            first, count = known
            out = [None] * first
            for i in range(first, first + count):
                v = self.rom.u16(base + 2 * i)
                t = bank | v if v is not None and v >= 0x8000 else None
                if t is not None and OPS[self.rom.u8(t)][0] in BAD:
                    t = None
                out.append(t)
            return out
        count = None
        out = []
        pos = base
        lowest = 0x10000
        while True:
            if count is not None and len(out) >= count:
                break
            if count is None and (pos & 0xFFFF) >= lowest:
                break
            if count is None and pos != base and pos in known_code:
                break
            v = self.rom.u16(pos)
            if v is None or v < 0x8000:
                break
            t = bank | v
            op = self.rom.u8(t)
            if count is None and (op is None or OPS[op][0] in BAD):
                break
            out.append(t)
            if t > base:
                lowest = min(lowest, v)
            pos += 2
            if len(out) > 512:
                break
        return out

    # ---- decoding one function -------------------------------------------
    def run(self, f):
        f.busy = True
        f.insns = {}
        f.calls, f.tails, f.notes = set(), set(), []
        f.cross = {}                # (from addr, to addr) -> function key: code reached in another M/X
        exits = set()
        seen = {}
        db0 = None if f.db == 'unset' else f.db
        d0 = None if f.d == 'unset' else f.d
        work = [(f.entry, f.m8, f.x8, db0, d0, (), None)]
        while work:
            addr, m8, x8, db, d, stack, src = work.pop()
            while True:
                prev = seen.get(addr)
                if prev is not None:
                    pm, px, pdb, pd = prev
                    if (pm, px) != (m8, x8):
                        # REP/SEP that sets every differing size: both paths continue the same way
                        op = self.rom.u8(addr)
                        mask = self.rom.u8(addr + 1) or 0
                        diff = (0x20 if pm != m8 else 0) | (0x10 if px != x8 else 0)
                        if op in (0xC2, 0xE2) and mask & diff == diff:
                            break
                        f.notes.append('$%06X reached with M/X %d%d and %d%d' % (addr, pm, px, m8, x8))
                        if src is not None:
                            g = self.func(addr, m8, x8, db, d, caller=f.entry)
                            f.cross[(src, addr)] = g.key
                            f.tails.add(g.key)
                            if g.exit not in (None, 'mixed') and not g.busy:
                                exits.add(g.exit)
                        break
                    ndb = db if pdb == db else None
                    nd = d if pd == d else None
                    if (ndb, nd) == (pdb, pd):
                        break
                    db, d = ndb, nd
                seen[addr] = (m8, x8, db, d)
                ins = decode(self.rom, addr, m8, x8)
                if ins is None:
                    f.notes.append('$%06X: not in ROM' % addr)
                    break
                ins.db, ins.d = db, d
                f.insns[addr] = ins
                mn, mode, val = ins.mn, ins.mode, ins.val
                nxt = ins.next
                if mn in BAD:
                    break
                # flags and registers
                if mn == 'REP':
                    if val & 0x20: m8 = 0
                    if val & 0x10: x8 = 0
                elif mn == 'SEP':
                    if val & 0x20: m8 = 1
                    if val & 0x10: x8 = 1
                elif mn == 'PHP':
                    stack = (stack + (('p', m8, x8),))[-24:]
                elif mn == 'PLP':
                    if stack and stack[-1] and stack[-1][0] == 'p':
                        m8, x8 = stack[-1][1], stack[-1][2]
                    stack = stack[:-1]
                elif mn == 'PHK':
                    stack = (stack + (('c', addr >> 16),))[-24:]
                elif mn == 'PHB':
                    stack = (stack + (('c', db) if db is not None else None,))[-24:]
                elif mn == 'PEA':
                    stack = (stack + (('c', val >> 8), ('c', val & 0xFF)))[-24:]
                elif mn == 'PLB':
                    db = stack[-1][1] if stack and stack[-1] and stack[-1][0] == 'c' else None
                    stack = stack[:-1]
                elif mn == 'PHD':
                    stack = (stack + (('c', d >> 8) if d is not None else None,
                                      ('c', d & 0xFF) if d is not None else None))[-24:]
                elif mn == 'PLD':
                    if len(stack) >= 2 and stack[-1] and stack[-2] and stack[-1][0] == 'c' == stack[-2][0]:
                        d = stack[-1][1] | stack[-2][1] << 8
                    else:
                        d = None
                    stack = stack[:-2]
                elif mn == 'TCD':
                    d = None
                    p = f.insns.get(self.prev_addr(f, addr))
                    if p and p.mn == 'LDA' and p.mode == 'immM' and not p.m8:
                        d = p.val
                elif mn in ('PHA', 'PHX', 'PHY'):
                    wide = not (m8 if mn == 'PHA' else x8)
                    item = None
                    p = f.insns.get(self.prev_addr(f, addr))
                    if mn == 'PHA' and p and p.mn == 'LDA' and p.mode == 'immM':
                        item = p.val
                    if wide:
                        stack = (stack + (('c', item >> 8) if item is not None else None,
                                          ('c', item & 0xFF) if item is not None else None))[-24:]
                    else:
                        stack = (stack + (('c', item & 0xFF) if item is not None else None,))[-24:]
                elif mn in ('PLA', 'PLX', 'PLY'):
                    wide = not (m8 if mn == 'PLA' else x8)
                    stack = stack[:-2] if wide else stack[:-1]
                elif mn in ('PEI', 'PER'):
                    stack = (stack + (None, None))[-24:]
                elif mn == 'XCE':
                    p = f.insns.get(self.prev_addr(f, addr))
                    if p and p.mn == 'SEC':
                        m8 = x8 = 1
                # control flow
                if mn in BRANCHES:
                    work.append((ins.target, m8, x8, db, d, stack, addr))
                    src, addr = addr, nxt
                    continue
                if mn in ('RTS', 'RTL', 'RTI'):
                    exits.add((m8, x8))
                    break
                if (mn in ('JMP', 'JML') and mode in ('abs', 'long')) or mn in ('BRA', 'BRL'):
                    t = ins.target
                    if t != f.entry and t in self.entries and self.final:
                        g = self.func(t, m8, x8, db, d, caller=f.entry)
                        f.tails.add(g.key)
                        e = g.exit
                        if e == 'mixed':
                            exits.add('mixed')
                        elif e is not None and not g.busy:
                            exits.add(e)
                        break
                    src, addr = addr, t
                    continue
                if mn == 'JMP' and mode == 'iabsx':
                    base = (addr & 0xFF0000) | val
                    ins.table = self.read_table(base, addr & 0xFF0000, f.insns)
                    self.table_sites[base] = ins.table
                    for t in ins.table:
                        if t is None:
                            continue
                        g = self.func(t, m8, x8, db, d, caller=f.entry)
                        f.tails.add(g.key)
                        if g.exit not in (None, 'mixed') and not g.busy:
                            exits.add(g.exit)
                    break
                if mode in ('iabs', 'ilabs'):
                    f.notes.append('$%06X: indirect jump %s not followed' % (addr, mn))
                    exits.add('mixed')
                    break
                if mn == 'JSR' and mode == 'iabsx':
                    base = (addr & 0xFF0000) | val
                    ins.table = self.read_table(base, addr & 0xFF0000, f.insns)
                    self.table_sites[base] = ins.table
                    states = set()
                    for t in ins.table:
                        if t is None:
                            continue
                        g = self.func(t, m8, x8, db, d, caller=f.entry)
                        f.calls.add(g.key)
                        states.add(self.exit_of(g, m8, x8))
                    if len(states) == 1:
                        m8, x8 = states.pop()
                    src, addr = addr, nxt
                    continue
                if mn == 'JSR' and ins.target == self.INLINE_DISPATCH:
                    base = nxt
                    ins.table = self.read_table(base, addr & 0xFF0000, f.insns)
                    self.table_sites[base] = ins.table
                    for t in ins.table:
                        if t is None:
                            continue
                        g = self.func(t, m8, x8, db, d, caller=f.entry)
                        f.tails.add(g.key)
                        if g.exit not in (None, 'mixed') and not g.busy:
                            exits.add(g.exit)
                    break
                if mn in ('JSR', 'JSL'):
                    t = ins.target
                    if self.rom.offset(t) is None:
                        f.notes.append('$%06X: call into RAM $%06X' % (addr, t))
                        src, addr = addr, nxt
                        continue
                    g = self.func(t, m8, x8, db, d, caller=f.entry)
                    f.calls.add(g.key)
                    if g.exit is None and g.done:
                        f.notes.append('$%06X: callee $%06X never returns' % (addr, t))
                    m8, x8 = self.exit_of(g, m8, x8)
                    src, addr = addr, nxt
                    continue
                if mn == 'STP' or mn == 'WAI' and False:
                    break
                src, addr = addr, nxt
        if not exits:
            f.exit = None
        elif len(exits) == 1 and 'mixed' not in exits:
            f.exit = next(iter(exits))
        else:
            f.exit = 'mixed'
        f.busy = False
        f.done = True

    def prev_addr(self, f, addr):
        # the instruction that ends right before addr, if decoded in f
        for back in (1, 2, 3, 4):
            p = f.insns.get(addr - back)
            if p is not None and p.size == back:
                return addr - back
        return None

    def run_all(self, seeds):
        for entry, m8, x8, db, d in seeds:
            self.func(entry, m8, x8, db, d)
        # code that several functions jump into (shared tails, the script loops) gets its own function
        jumpers = {}
        for f in list(self.funcs.values()):
            for i in f.insns.values():
                if (i.mn in ('JMP', 'JML') and i.mode in ('abs', 'long')) or i.mn in ('BRA', 'BRL'):
                    if i.target != f.entry and i.target not in self.entries:
                        jumpers.setdefault(i.target, {})[f.entry] = (i.m8, i.x8, i.db, i.d)
        promoted = [(t, st) for t, fs in jumpers.items() if len(fs) >= 2 for st in [next(iter(fs.values()))]]
        for t, (m8, x8, db, d) in promoted:
            self.entries.add(t)
        for t, (m8, x8, db, d) in promoted:
            self.func(t, m8, x8, db, d)
        self.promoted = {t for t, _ in promoted}
        # second pass: every entry is known now, so jumps into other functions become tail calls
        self.final = True
        for f in list(self.funcs.values()):
            f.done = False
        for f in list(self.funcs.values()):
            if not f.done:
                self.run(f)
        # keep going until no new functions appear
        while True:
            pending = [f for f in self.funcs.values() if not f.done]
            if not pending:
                break
            for f in pending:
                self.run(f)

    def merge_variants(self):
        by_entry = {}
        for f in self.funcs.values():
            by_entry.setdefault(f.entry, []).append(f)
        for entry, fs in by_entry.items():
            fs.sort(key=lambda g: (g.m8, g.x8))
            kept = []
            for g in fs:
                s = tuple(sorted(i.sig() for i in g.insns.values()))
                for k, ks in kept:
                    if ks == s:
                        g.merged_into = k.key
                        k.callers |= g.callers
                        break
                else:
                    kept.append((g, s))
