"""Turn analysed 65816 functions into structured C#.

Registers are C# properties (A, AL, X, Y, D, DB, S); flags are bools (N, Z, C, V) but are only
written out where a later instruction reads them: a CMP followed by BCC becomes `if (A < 5)`.
Memory is m8[...] / m16[...] / m24[...] with 24-bit addresses (low RAM shown as $7E:xxxx).
"""
from ops import BRANCHES, BAD

NZ = frozenset('NZ')
ALL = frozenset('NZCV')
EMPTY = frozenset()

SIMPLE_NZ = {'LDA', 'LDX', 'LDY', 'AND', 'ORA', 'EOR', 'INC', 'DEC', 'INX', 'INY', 'DEX', 'DEY',
             'TAX', 'TAY', 'TXA', 'TYA', 'TXY', 'TYX', 'TSX', 'TDC', 'TSC', 'PLA', 'PLX', 'PLY',
             'PLB', 'PLD', 'XBA'}
BRANCH_FLAG = {'BEQ': ('Z', True), 'BNE': ('Z', False), 'BCS': ('C', True), 'BCC': ('C', False),
               'BMI': ('N', True), 'BPL': ('N', False), 'BVS': ('V', True), 'BVC': ('V', False)}
REP_FLAGS = {0x80: 'N', 0x40: 'V', 0x02: 'Z', 0x01: 'C'}


def h(v, digits=0):
    if v < 10 and not digits:
        return str(v)
    return '0x%0*X' % (digits, v)


def flag_effects(ins, ctx):
    """(uses, defs) of N/Z/C/V. PHP/PLP are treated as not touching N/Z/C/V (they save M/X)."""
    mn = ins.mn
    if mn in SIMPLE_NZ:
        return EMPTY, NZ
    if mn in ('ADC', 'SBC'):
        return frozenset('C'), ALL
    if mn in ('CMP', 'CPX', 'CPY', 'ASL', 'LSR'):
        return EMPTY, frozenset('NZC')
    if mn in ('ROL', 'ROR'):
        return frozenset('C'), frozenset('NZC')
    if mn == 'BIT':
        return EMPTY, (frozenset('Z') if ins.mode == 'immM' else frozenset('NZV'))
    if mn in ('TSB', 'TRB'):
        return EMPTY, frozenset('Z')
    if mn in ('CLC', 'SEC'):
        return EMPTY, frozenset('C')
    if mn == 'CLV':
        return EMPTY, frozenset('V')
    if mn in ('REP', 'SEP'):
        return EMPTY, frozenset(f for b, f in REP_FLAGS.items() if ins.val & b)
    if mn == 'XCE':
        return frozenset('C'), frozenset('C')
    if mn == 'RTI':
        return EMPTY, ALL
    if mn in BRANCH_FLAG:
        return frozenset(BRANCH_FLAG[mn][0]), EMPTY
    return EMPTY, EMPTY


class Block:
    def __init__(self, start):
        self.start = start
        self.insns = []
        self.succ = []          # internal successor addresses (taken first for branches)
        self.preds = set()
        self.kind = 'fall'      # fall, branch, jump, return, tail, stop
        self.live_out = EMPTY
        self.live_in = EMPTY


class Ctx:
    """Whole-program facts the lifter needs: names, flag liveness across calls."""

    def __init__(self, an, syms):
        self.an = an
        self.syms = syms
        self.live_in = {}       # func key -> flags read before written
        self.live_out = {}      # func key -> flags callers read after it returns
        self.must_def = {}      # func key -> flags always written before returning
        self.names = {}
        self.tables = {}        # table address -> name

    def key(self, entry, m8, x8):
        f = self.an.funcs.get((entry, m8, x8))
        if f is None:
            for g in self.an.funcs.values():
                if g.entry == entry:
                    f = g
                    break
        if f is None:
            return None
        while f.merged_into is not None:
            f = self.an.funcs[f.merged_into]
        return f.key

    def callee(self, ins, target):
        return self.key(target, ins.m8, ins.x8)


class Lifter:
    def __init__(self, ctx, f):
        self.ctx, self.f = ctx, f
        self.an = ctx.an
        self.build_blocks()

    # ---- blocks -----------------------------------------------------------
    def is_tail_jump(self, ins):
        jump = (ins.mn in ('JMP', 'JML') and ins.mode in ('abs', 'long')) or ins.mn in ('BRA', 'BRL')
        return (jump and ins.target != self.f.entry and ins.target in self.an.entries
                and ins.target not in self.f.insns)

    def build_blocks(self):
        insns = self.f.insns
        leaders = {self.f.entry}
        for i in insns.values():
            if i.mn in BRANCHES:
                leaders.add(i.target)
                leaders.add(i.next)
            elif i.mn in ('BRA', 'BRL') or (i.mn in ('JMP', 'JML') and i.mode in ('abs', 'long')):
                if i.target in insns:
                    leaders.add(i.target)
                leaders.add(i.next)
            elif i.mn in ('RTS', 'RTL', 'RTI') or i.mn in BAD or i.mode in ('iabs', 'ilabs') or (i.mode == 'iabsx' and i.mn == 'JMP') \
                    or (i.mn == 'JSR' and i.table is not None and i.mode == 'abs'):
                leaders.add(i.next)
        self.blocks = {}
        for start in sorted(a for a in leaders if a in insns):
            b = Block(start)
            a = start
            while True:
                i = insns[a]
                b.insns.append(i)
                mn = i.mn
                if mn in BRANCHES:
                    b.kind = 'branch'
                    b.succ = [i.target, i.next]
                    break
                if mn in ('BRA', 'BRL') or (mn in ('JMP', 'JML') and i.mode in ('abs', 'long')):
                    if self.is_tail_jump(i):
                        b.kind = 'tail'
                    else:
                        b.kind, b.succ = 'jump', [i.target]
                    break
                if mn in ('RTS', 'RTL', 'RTI'):
                    b.kind = 'return'
                    break
                if mn in BAD or i.mode in ('iabs', 'ilabs') or (i.mode == 'iabsx' and mn == 'JMP') or mn == 'STP':
                    b.kind = 'stop' if mn in BAD or mn == 'STP' else 'tail'
                    break
                if mn == 'JSR' and i.table is not None and i.mode == 'abs':
                    b.kind = 'tail'   # inline dispatcher: the handler returns for us
                    break
                n = i.next
                if n not in insns:
                    b.kind = 'stop'
                    break
                if n in leaders:
                    b.succ = [n]
                    break
                a = n
            self.blocks[start] = b
        # edges into code this function reads in another M/X state go to that state's function
        self.ext = {}               # fake successor id -> function key
        cross = getattr(self.f, 'cross', {})
        for b in self.blocks.values():
            last = b.insns[-1].addr
            succ = []
            for s in b.succ:
                k = cross.get((last, s))
                if k is not None:
                    fake = 0x1000000 + len(self.ext)
                    self.ext[fake] = self.ctx.key(*k) or k
                    succ.append(fake)
                elif s in self.blocks:
                    succ.append(s)
            b.succ = succ
            for s in b.succ:
                if s in self.blocks:
                    self.blocks[s].preds.add(b.start)
        # the entry block comes first (code below the entry is reached by jumps)
        e = self.f.entry
        self.order = sorted(a for a in self.blocks if a >= e) + sorted(a for a in self.blocks if a < e)

    # ---- per-instruction flag effects with calls ---------------------------
    def effects(self, ins):
        mn = ins.mn
        if mn in ('JSR', 'JSL') and ins.mode in ('abs', 'long') and ins.table is None:
            k = self.ctx.callee(ins, ins.target)
            if k is None:
                return EMPTY, EMPTY
            return self.ctx.live_in.get(k, EMPTY), self.ctx.must_def.get(k, ALL)
        if mn == 'JSR' and ins.mode == 'iabsx':
            uses, defs = EMPTY, ALL
            for t in ins.table or []:
                if t is None:
                    continue
                k = self.ctx.callee(ins, t)
                if k:
                    uses |= self.ctx.live_in.get(k, EMPTY)
                    defs &= self.ctx.must_def.get(k, ALL)
            return uses, defs
        return flag_effects(ins, self.ctx)

    def tail_keys(self, b):
        i = b.insns[-1]
        if i.table is not None:
            return [self.ctx.callee(i, t) for t in i.table if t is not None]
        if (i.mn in ('JMP', 'JML') and i.mode in ('abs', 'long')) or i.mn in ('BRA', 'BRL'):
            return [self.ctx.callee(i, i.target)]
        return []

    def exit_live(self, b):
        """Flags live at the end of a block that leaves the function."""
        if b.kind == 'return':
            return self.ctx.live_out.get(self.f.key, EMPTY)
        if b.kind == 'tail':
            out = EMPTY
            for k in self.tail_keys(b):
                if k:
                    out |= self.ctx.live_in.get(k, EMPTY)
            return out
        return EMPTY

    def liveness(self):
        changed = True
        while changed:
            changed = False
            for a in reversed(self.order):
                b = self.blocks[a]
                out = self.exit_live(b)
                for s in b.succ:
                    if s in self.ext:
                        out |= self.ctx.live_in.get(self.ext[s], EMPTY) | self.ctx.live_out.get(self.f.key, EMPTY)
                    else:
                        out |= self.blocks[s].live_in
                live = out
                for i in reversed(b.insns):
                    u, d = self.effects(i)
                    live = (live - d) | u
                if out != b.live_out or live != b.live_in:
                    b.live_out, b.live_in = out, live
                    changed = True
        return self.blocks[self.f.entry].live_in

    def must_define(self):
        """Flags written on every path from the entry to a return."""
        inn = {a: ALL for a in self.order}
        inn[self.f.entry] = EMPTY
        result = ALL
        changed = True
        while changed:
            changed = False
            result = ALL
            for a in self.order:
                b = self.blocks[a]
                cur = inn[a]
                for i in b.insns:
                    cur = cur | self.effects(i)[1]
                if b.kind == 'return':
                    result &= cur
                elif b.kind == 'tail':
                    for k in self.tail_keys(b):
                        result &= cur | (self.ctx.must_def.get(k, ALL) if k else EMPTY)
                for s in b.succ:
                    if s in self.ext:
                        result &= cur | self.ctx.must_def.get(self.ext[s], ALL)
                        continue
                    n = inn[s] & cur if s != self.f.entry else EMPTY
                    if n != inn[s]:
                        inn[s] = n
                        changed = True
        return result

    def propagate_live_out(self):
        """Tell callees which flags this function reads right after they return."""
        for a in self.order:
            b = self.blocks[a]
            live = b.live_out
            for i in reversed(b.insns):
                u, d = self.effects(i)
                if i.mn in ('JSR', 'JSL'):
                    targets = []
                    if i.mode == 'iabsx' and i.table:
                        targets = [t for t in i.table if t is not None]
                    elif i.mode in ('abs', 'long') and i.table is None:
                        targets = [i.target]
                    for t in targets:
                        k = self.ctx.callee(i, t)
                        if k:
                            self.ctx.live_out[k] = self.ctx.live_out.get(k, EMPTY) | live
                live = (live - d) | u
            mine = self.ctx.live_out.get(self.f.key, EMPTY)
            for s in b.succ:
                if s in self.ext:
                    k = self.ext[s]
                    self.ctx.live_out[k] = self.ctx.live_out.get(k, EMPTY) | mine
            if b.kind == 'tail':
                for k in self.tail_keys(b):
                    if k:
                        self.ctx.live_out[k] = self.ctx.live_out.get(k, EMPTY) | mine
