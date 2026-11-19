"""Instruction translation and structuring (ifs, loops, gotos) into C#."""
from lift import Lifter, EMPTY, BRANCH_FLAG, REP_FLAGS
from ops import BRANCHES


def h(v, digits=0):
    if v < 10 and not digits:
        return str(v)
    return '0x%0*X' % (digits, v)


# ---------------------------------------------------------------------------
# conditions

class Cond:
    """A branch condition: lhs op rhs, a flag variable, or a constant."""

    INV = {'==': '!=', '!=': '==', '>=': '<', '<': '>=', '>': '<=', '<=': '>'}

    def __init__(self, lhs, op=None, rhs=None):
        self.lhs, self.op, self.rhs = lhs, op, rhs

    def neg(self):
        if self.op is None:
            if self.lhs in ('true', 'false'):
                return Cond('false' if self.lhs == 'true' else 'true')
            if self.lhs.startswith('!'):
                return Cond(self.lhs[1:])
            return Cond('!' + self.lhs)
        return Cond(self.lhs, self.INV[self.op], self.rhs)

    def __str__(self):
        if self.op is None:
            return self.lhs
        return '%s %s %s' % (self.lhs, self.op, self.rhs)


def lazy_cond(flag, lz):
    kind = lz[0]
    if kind == 'var':
        return Cond(flag)
    if kind == 'const':
        return Cond('true' if lz[1] else 'false')
    if kind == 'nz':
        e, w = lz[1], lz[2]
        if flag == 'Z':
            return Cond(e, '==', '0')
        return Cond(e, '>=', '0x80' if w == 1 else '0x8000')
    if kind == 'cmp':
        l, r, w = lz[1], lz[2], lz[3]
        if flag == 'C':
            return Cond(l, '>=', r)
        if flag == 'Z':
            return Cond(l, '==', r)
        return Cond('((%s - %s) & %s)' % (l, r, '0x80' if w == 1 else '0x8000'), '!=', '0')
    if kind == 'bitz':
        return Cond('(%s & %s)' % (lz[1], lz[2]), '==', '0')
    if kind == 'bitn':
        return Cond('(%s & %s)' % (lz[1], '0x80' if lz[2] == 1 else '0x8000'), '!=', '0')
    if kind == 'bitv':
        return Cond('(%s & %s)' % (lz[1], '0x40' if lz[2] == 1 else '0x4000'), '!=', '0')
    raise ValueError(lz)


def lazy_refs(lz):
    return lz[-1] if lz[0] not in ('var', 'const') else EMPTY


# ---------------------------------------------------------------------------
# instruction translation

REG_WRITES = {
    'LDA': 'A', 'PLA': 'A', 'TXA': 'A', 'TYA': 'A', 'TDC': 'A', 'TSC': 'A', 'XBA': 'A', 'ADC': 'A',
    'SBC': 'A', 'AND': 'A', 'ORA': 'A', 'EOR': 'A', 'LDX': 'X', 'PLX': 'X', 'TAX': 'X', 'TYX': 'X',
    'TSX': 'X', 'INX': 'X', 'DEX': 'X', 'LDY': 'Y', 'PLY': 'Y', 'TAY': 'Y', 'TXY': 'Y', 'INY': 'Y',
    'DEY': 'Y'}
MEM_WRITES = {'STA', 'STX', 'STY', 'STZ', 'TSB', 'TRB'}
RMW = {'INC', 'DEC', 'ASL', 'LSR', 'ROL', 'ROR'}
STACKY = {'TCS', 'TXS', 'PHA', 'PHX', 'PHY', 'PHP', 'PHB', 'PHD', 'PHK', 'PEA', 'PEI', 'PER',
          'PLA', 'PLX', 'PLY', 'PLP', 'PLB', 'PLD'}


def system_bank(b):
    return b is not None and (b & 0x7F) < 0x40


class Gen(Lifter):
    def __init__(self, ctx, f):
        Lifter.__init__(self, ctx, f)

    # ---- names ----------------------------------------------------------------
    def addr_name(self, full, indexed=False):
        return self.ctx.syms.addr(full, indexed)

    def dp_addr(self, ins, v, indexed=False):
        d = ins.d
        if d is None:
            return 'Dp(%s)' % h(v, 2)
        a = (d + v) & 0xFFFF
        return self.addr_name(0x7E0000 | a if a < 0x2000 else a, indexed)

    def abs_addr(self, ins, v, indexed=False):
        db = ins.db
        if v < 0x2000:
            return self.addr_name((0x7F if db == 0x7F else 0x7E) << 16 | v, indexed)
        if v < 0x8000:
            if db is None or system_bank(db):
                return self.addr_name(v, indexed)
            return self.addr_name(db << 16 | v, indexed)
        if db is None:
            return 'Db(%s)' % h(v, 4)
        return self.addr_name(db << 16 | v, indexed)

    def operand(self, ins, width):
        """(address expression or None, value expression, refs)."""
        mode, v = ins.mode, ins.val
        mt = {1: 'm8', 2: 'm16', 3: 'm24'}[width]
        refs = {'M'}
        if mode in ('immM', 'immX', 'imm8'):
            return None, h(v), EMPTY
        if mode == 'dp':
            a = self.dp_addr(ins, v)
        elif mode == 'dpx':
            a, refs = '%s + X' % self.dp_addr(ins, v, True), {'M', 'X'}
        elif mode == 'dpy':
            a, refs = '%s + Y' % self.dp_addr(ins, v, True), {'M', 'Y'}
        elif mode == 'abs':
            a = self.abs_addr(ins, v)
        elif mode in ('absx', 'absy'):
            r = 'X' if mode == 'absx' else 'Y'
            refs = {'M', r}
            if v < 0x100 and ins.db is None:
                # LDA $0002,Y: the register holds a pointer into the data bank
                a = 'Db(%s)' % (r if v == 0 else '%s + %s' % (r, h(v)))
            else:
                a = '%s + %s' % (self.abs_addr(ins, v, True), r)
        elif mode == 'long':
            a = self.addr_name(v)
        elif mode == 'longx':
            a, refs = '%s + X' % self.addr_name(v, True), {'M', 'X'}
        elif mode == 'idp':
            a = 'Ptr16(%s)' % self.dp_addr(ins, v)
        elif mode == 'idpx':
            a, refs = 'Ptr16(%s + X)' % self.dp_addr(ins, v), {'M', 'X'}
        elif mode == 'idpy':
            a, refs = 'Ptr16(%s) + Y' % self.dp_addr(ins, v), {'M', 'Y'}
        elif mode == 'ildp':
            a = 'Ptr24(%s)' % self.dp_addr(ins, v)
        elif mode == 'ildpy':
            a, refs = 'Ptr24(%s) + Y' % self.dp_addr(ins, v), {'M', 'Y'}
        elif mode == 'sr':
            a, refs = 'S + %s' % h(v), {'M', 'S'}
        elif mode == 'isry':
            a, refs = 'Ptr16(S + %s) + Y' % h(v), {'M', 'S', 'Y'}
        else:
            raise ValueError(mode)
        return a, '%s[%s]' % (mt, a), frozenset(refs)

    def reg(self, ins, r):
        if r == 'A':
            return 'AL' if ins.m8 else 'A'
        return r

    # ---- flags ----------------------------------------------------------------
    def materialize(self, out, flags):
        for fl in 'NZCV':
            if fl in flags and self.lazy[fl][0] != 'var':
                out.append('%s = %s;' % (fl, lazy_cond(fl, self.lazy[fl])))
                self.lazy[fl] = ('var',)

    def set_nz(self, expr, width, refs):
        lz = ('nz', expr, width, frozenset(refs))
        self.lazy['N'] = self.lazy['Z'] = lz

    def set_vars(self, flags):
        for fl in flags:
            self.lazy[fl] = ('var',)

    # ---- one block ------------------------------------------------------------
    def gen_block(self, b):
        """Statements of a block (without its branch/jump), leaving self.lazy for the branch."""
        out = []
        self.lazy = {fl: ('var',) for fl in 'NZCV'}
        live_after = []
        live = b.live_out | self.term_live(b)
        for i in reversed(b.insns):
            live_after.append(live)
            u, d = self.effects(i)
            live = (live - d) | u
        live_after.reverse()
        n = 0
        while n < len(b.insns):
            used = self.peephole(b.insns, n, out)
            if used:
                n += used
                continue
            for text in self.ctx.syms.notes.get(b.insns[n].addr, ()):
                out.append('// ' + text)
            self.gen_insn(b.insns[n], live_after[n], out)
            n += 1
        # flags read after this block that are still pending
        branch_flag = BRANCH_FLAG.get(b.insns[-1].mn, (None,))[0]
        self.materialize(out, (b.live_out | self.term_live(b)) - {branch_flag})
        return out

    def term_live(self, b):
        if b.kind == 'return':
            return self.exit_live(b)
        if b.kind == 'tail':
            return self.exit_live(b) | self.ctx.live_out.get(self.f.key, EMPTY)
        return EMPTY

    def peephole(self, insns, n, out):
        """Collapse DB set-ups: PHK/PLB, PHA/PLB, PEA/PLB/PLB. Returns instructions consumed."""
        i = insns[n]
        nx = insns[n + 1] if n + 1 < len(insns) else None
        nx2 = insns[n + 2] if n + 2 < len(insns) else None
        if i.mn == 'PHK' and nx and nx.mn == 'PLB':
            out.append('DB = %s;' % h(i.addr >> 16))
            return 2
        if i.mn == 'PHA' and i.m8 and nx and nx.mn == 'PLB':
            out.append('DB = AL;')
            return 2
        if i.mn == 'PEA' and nx and nx.mn == 'PLB' and nx2 and nx2.mn == 'PLB':
            hi, lo = i.val >> 8, i.val & 0xFF
            out.append('DB = %s;' % h(hi) if hi == lo else 'DB = %s; // after %s' % (h(hi), h(lo)))
            return 3
        return 0

    def gen_insn(self, ins, live_after, out):
        mn, mode = ins.mn, ins.mode
        u, d = self.effects(ins)
        writes = set()
        r = REG_WRITES.get(mn)
        if r:
            writes.add(r)
        if mn in MEM_WRITES or (mn in RMW and mode != 'acc') or mn in ('MVN', 'MVP'):
            writes.add('M')
        if mn in RMW and mode == 'acc':
            writes.add('A')
        if mn in ('MVN', 'MVP'):
            writes |= {'A', 'X', 'Y'}
        if mn in ('REP', 'SEP'):
            if ins.val & 0x20:
                writes.add('A')
            if ins.val & 0x10:
                writes |= {'X', 'Y'}
        if mn in STACKY:
            writes.add('S')
        if mn == 'PLB':
            writes.add('DB')
        call = (mn in ('JSR', 'JSL') and ins.table is None) or (mn == 'JSR' and mode == 'iabsx')
        if call:
            self.materialize(out, (live_after - d) | u)
        else:
            need = set()
            for fl in 'NZCV':
                lz = self.lazy[fl]
                if lazy_refs(lz) & writes and ((fl in live_after and fl not in d) or fl in u):
                    need.add(fl)
            for fl in u:
                if mn in BRANCH_FLAG:
                    continue
                if fl == 'C' and mn in ('ADC', 'SBC', 'XCE') and self.lazy['C'][0] == 'const':
                    continue
                need.add(fl)
            self.materialize(out, need)
        getattr(self, 'op_' + mn, self.op_default)(ins, live_after, out)

    # ---- instructions ---------------------------------------------------------
    def width(self, ins, xdep=False):
        return 1 if (ins.x8 if xdep else ins.m8) else 2

    def load(self, ins, r, xdep, out):
        w = self.width(ins, xdep)
        _, val, refs = self.operand(ins, w)
        name = self.reg(ins, r)
        out.append('%s = %s;' % (name, val))
        self.set_nz(name, w, {r})

    def op_LDA(self, ins, la, out): self.load(ins, 'A', False, out)
    def op_LDX(self, ins, la, out): self.load(ins, 'X', True, out)
    def op_LDY(self, ins, la, out): self.load(ins, 'Y', True, out)

    def store(self, ins, src, xdep, out):
        _, dst, _ = self.operand(ins, self.width(ins, xdep))
        out.append('%s = %s;' % (dst, src))

    def op_STA(self, ins, la, out): self.store(ins, self.reg(ins, 'A'), False, out)
    def op_STX(self, ins, la, out): self.store(ins, 'X', True, out)
    def op_STY(self, ins, la, out): self.store(ins, 'Y', True, out)
    def op_STZ(self, ins, la, out): self.store(ins, '0', False, out)

    def logic(self, ins, op, out):
        w = self.width(ins)
        _, val, _ = self.operand(ins, w)
        a = self.reg(ins, 'A')
        out.append('%s %s= %s;' % (a, op, val))
        self.set_nz(a, w, {'A'})

    def op_AND(self, ins, la, out): self.logic(ins, '&', out)
    def op_ORA(self, ins, la, out): self.logic(ins, '|', out)
    def op_EOR(self, ins, la, out): self.logic(ins, '^', out)

    def addsub(self, ins, la, out, add):
        w = self.width(ins)
        _, val, _ = self.operand(ins, w)
        a = self.reg(ins, 'A')
        c = self.lazy['C']
        sign = '+' if add else '-'
        if c[0] == 'const' and 'C' not in la and 'V' not in la:
            plain = c[1] == (not add)    # CLC before ADC, SEC before SBC
            out.append('%s %s= %s;' % (a, sign, val) if plain else '%s %s= %s + 1;' % (a, sign, val))
        else:
            self.materialize(out, {'C'})
            out.append('%s = %s%d(%s, %s);' % (a, 'Adc' if add else 'Sbc', 8 * w, a, val))
        self.set_nz(a, w, {'A'})
        self.set_vars('CV')

    def op_ADC(self, ins, la, out): self.addsub(ins, la, out, True)
    def op_SBC(self, ins, la, out): self.addsub(ins, la, out, False)

    def compare(self, ins, r, xdep):
        w = self.width(ins, xdep)
        _, val, refs = self.operand(ins, w)
        lz = ('cmp', self.reg(ins, r), val, w, frozenset(refs) | {r})
        self.lazy['N'] = self.lazy['Z'] = self.lazy['C'] = lz

    def op_CMP(self, ins, la, out): self.compare(ins, 'A', False)
    def op_CPX(self, ins, la, out): self.compare(ins, 'X', True)
    def op_CPY(self, ins, la, out): self.compare(ins, 'Y', True)

    def op_BIT(self, ins, la, out):
        w = self.width(ins)
        _, val, refs = self.operand(ins, w)
        self.lazy['Z'] = ('bitz', self.reg(ins, 'A'), val, frozenset(refs) | {'A'})
        if ins.mode != 'immM':
            self.lazy['N'] = ('bitn', val, w, frozenset(refs))
            self.lazy['V'] = ('bitv', val, w, frozenset(refs))

    def target(self, ins):
        w = self.width(ins)
        if ins.mode == 'acc':
            return self.reg(ins, 'A'), {'A'}, w
        _, val, refs = self.operand(ins, w)
        return val, refs, w

    def op_INC(self, ins, la, out):
        t, refs, w = self.target(ins)
        out.append('%s++;' % t)
        self.set_nz(t, w, refs)

    def op_DEC(self, ins, la, out):
        t, refs, w = self.target(ins)
        out.append('%s--;' % t)
        self.set_nz(t, w, refs)

    def incdec(self, ins, r, op, out):
        out.append('%s%s;' % (r, op))
        self.set_nz(r, self.width(ins, True), {r})

    def op_INX(self, ins, la, out): self.incdec(ins, 'X', '++', out)
    def op_INY(self, ins, la, out): self.incdec(ins, 'Y', '++', out)
    def op_DEX(self, ins, la, out): self.incdec(ins, 'X', '--', out)
    def op_DEY(self, ins, la, out): self.incdec(ins, 'Y', '--', out)

    def shift(self, ins, la, out, left):
        t, refs, w = self.target(ins)
        if 'C' in la:
            bit = ('0x80' if w == 1 else '0x8000') if left else '1'
            out.append('C = (%s & %s) != 0;' % (t, bit))
        out.append('%s %s= 1;' % (t, '<<' if left else '>>'))
        self.set_nz(t, w, refs)
        self.set_vars('C')

    def op_ASL(self, ins, la, out): self.shift(ins, la, out, True)
    def op_LSR(self, ins, la, out): self.shift(ins, la, out, False)

    def rotate(self, ins, out, name):
        t, refs, w = self.target(ins)
        out.append('%s = %s%d(%s);' % (t, name, 8 * w, t))
        self.set_nz(t, w, refs)
        self.set_vars('C')

    def op_ROL(self, ins, la, out): self.rotate(ins, out, 'Rol')
    def op_ROR(self, ins, la, out): self.rotate(ins, out, 'Ror')

    def testbits(self, ins, la, out, setbits):
        t, refs, w = self.target(ins)
        a = self.reg(ins, 'A')
        if 'Z' in la:
            out.append('Z = (%s & %s) == 0;' % (t, a))
        out.append(('%s |= %s;' if setbits else '%s &= ~%s;') % (t, a))
        self.set_vars('Z')

    def op_TSB(self, ins, la, out): self.testbits(ins, la, out, True)
    def op_TRB(self, ins, la, out): self.testbits(ins, la, out, False)

    def transfer(self, ins, dst, src, out, narrow):
        dname = ('AL' if ins.m8 else 'A') if dst == 'A' else dst
        sname = ('AL' if ins.x8 else 'A') if src == 'A' else src
        out.append('%s = %s;' % (dname, sname))
        self.set_nz(dname, 1 if narrow else 2, {dst})

    def op_TAX(self, ins, la, out): self.transfer(ins, 'X', 'A', out, ins.x8)
    def op_TAY(self, ins, la, out): self.transfer(ins, 'Y', 'A', out, ins.x8)
    def op_TXA(self, ins, la, out): self.transfer(ins, 'A', 'X', out, ins.m8)
    def op_TYA(self, ins, la, out): self.transfer(ins, 'A', 'Y', out, ins.m8)
    def op_TXY(self, ins, la, out): self.transfer(ins, 'Y', 'X', out, ins.x8)
    def op_TYX(self, ins, la, out): self.transfer(ins, 'X', 'Y', out, ins.x8)
    def op_TSX(self, ins, la, out): self.transfer(ins, 'X', 'S', out, ins.x8)
    def op_TXS(self, ins, la, out): out.append('S = X;')
    def op_TCS(self, ins, la, out): out.append('S = A;')
    def op_TCD(self, ins, la, out): out.append('D = A;')

    def op_TDC(self, ins, la, out):
        out.append('A = 0;' if ins.d == 0 else 'A = D;')
        self.set_nz('A', 2, {'A'})

    def op_TSC(self, ins, la, out):
        out.append('A = S;')
        self.set_nz('A', 2, {'A'})

    def op_XBA(self, ins, la, out):
        out.append('A = Swap(A);')
        self.set_nz('AL', 1, {'A'})

    def op_PHA(self, ins, la, out): out.append('Push%d(%s);' % (8 * self.width(ins), self.reg(ins, 'A')))
    def op_PHX(self, ins, la, out): out.append('Push%d(X);' % (8 * self.width(ins, True)))
    def op_PHY(self, ins, la, out): out.append('Push%d(Y);' % (8 * self.width(ins, True)))
    def op_PHB(self, ins, la, out): out.append('Push8(DB);')
    def op_PHD(self, ins, la, out): out.append('Push16(D);')
    def op_PHK(self, ins, la, out): out.append('Push8(%s);' % h(ins.addr >> 16))
    def op_PHP(self, ins, la, out): out.append('PushFlags();')
    def op_PLP(self, ins, la, out): out.append('PullFlags();')
    def op_PEA(self, ins, la, out): out.append('Push16(%s);' % h(ins.val, 4))

    def op_PER(self, ins, la, out):
        t = (ins.addr + 3 + (ins.val - 65536 if ins.val >= 32768 else ins.val)) & 0xFFFF
        out.append('Push16(%s);' % h(t, 4))

    def op_PEI(self, ins, la, out):
        out.append('Push16(m16[%s]);' % self.dp_addr(ins, ins.val))

    def pull(self, ins, r, w, out):
        name = self.reg(ins, r)
        out.append('%s = Pop%d();' % (name, 8 * w))
        self.set_nz(name, w, {r})

    def op_PLA(self, ins, la, out): self.pull(ins, 'A', self.width(ins), out)
    def op_PLX(self, ins, la, out): self.pull(ins, 'X', self.width(ins, True), out)
    def op_PLY(self, ins, la, out): self.pull(ins, 'Y', self.width(ins, True), out)

    def op_PLB(self, ins, la, out):
        nxt = self.f.insns.get(ins.next)
        known = nxt.db if nxt is not None else None
        out.append('DB = Pop8();' + (' // %s' % h(known) if known is not None else ''))
        self.set_nz('DB', 1, {'DB'})

    def op_PLD(self, ins, la, out):
        out.append('D = Pop16();')
        self.set_nz('D', 2, {'D'})

    def op_CLC(self, ins, la, out): self.lazy['C'] = ('const', False)
    def op_SEC(self, ins, la, out): self.lazy['C'] = ('const', True)
    def op_CLV(self, ins, la, out): self.lazy['V'] = ('const', False)

    def flagbits(self, ins, value):
        for b, fl in REP_FLAGS.items():
            if ins.val & b:
                self.lazy[fl] = ('const', value)

    def op_REP(self, ins, la, out): self.flagbits(ins, False)
    def op_SEP(self, ins, la, out): self.flagbits(ins, True)
    def op_SEI(self, ins, la, out): out.append('DisableInterrupts();')
    def op_CLI(self, ins, la, out): out.append('EnableInterrupts();')
    def op_SED(self, ins, la, out): out.append('DecimalMode = true;')
    def op_CLD(self, ins, la, out): out.append('DecimalMode = false;')
    def op_NOP(self, ins, la, out): pass
    def op_WAI(self, ins, la, out): out.append('WaitForInterrupt();')
    def op_STP(self, ins, la, out): out.append('Stop();')
    def op_BRK(self, ins, la, out): out.append('Brk(%s);' % h(ins.val, 2))
    def op_COP(self, ins, la, out): out.append('Cop(%s);' % h(ins.val, 2))
    def op_WDM(self, ins, la, out): out.append('Wdm(%s);' % h(ins.val, 2))

    def op_XCE(self, ins, la, out):
        c = self.lazy['C']
        if c[0] == 'const':
            out.append('EmulationMode = %s;' % ('true' if c[1] else 'false'))
        else:
            out.append('ExchangeCarryAndEmulation();')
        self.set_vars('C')

    def op_MVN(self, ins, la, out):
        out.append('MoveBlockUp(fromBank: %s, toBank: %s); // A + 1 bytes from X to Y'
                   % (h(ins.val >> 8, 2), h(ins.val & 0xFF, 2)))

    def op_MVP(self, ins, la, out):
        out.append('MoveBlockDown(fromBank: %s, toBank: %s); // A + 1 bytes ending at X and Y'
                   % (h(ins.val >> 8, 2), h(ins.val & 0xFF, 2)))

    def op_JSR(self, ins, la, out):
        if ins.mode == 'iabsx':
            out.append('%s[X / 2]();' % self.ctx.table_name((ins.addr & 0xFF0000) | ins.val, ins))
            self.set_vars('NZCV')
            return
        if ins.table is not None:
            return   # inline dispatcher: written as the block's terminator
        out.append('%s();' % self.ctx.func_name(self.ctx.callee(ins, ins.target), ins.target))
        self.set_vars('NZCV')

    op_JSL = op_JSR

    def op_default(self, ins, la, out):
        pass   # branches, jumps and returns are written by the structurer

    # ---- terminators ----------------------------------------------------------
    def term(self, b):
        """Statements that end a block leaving the function, or None if it doesn't."""
        i = b.insns[-1]
        if b.kind == 'return':
            return ['return;' + (' // from the interrupt' if i.mn == 'RTI' else '')]
        if b.kind == 'stop':
            return ['return; // (no way on)']
        if b.kind == 'tail':
            if i.mn == 'JSR' and i.table is not None:
                return ['%s[AL]();' % self.ctx.table_name(i.next, i), 'return;']
            if i.mode == 'iabsx':
                return ['%s[X / 2]();' % self.ctx.table_name((i.addr & 0xFF0000) | i.val, i), 'return;']
            if i.mode == 'iabs':
                return ['JumpIndirect(m16[%s]);' % self.abs_addr(i, i.val), 'return;']
            if i.mode == 'ilabs':
                return ['JumpIndirectLong(m24[%s]);' % self.addr_name(0x7E0000 | i.val), 'return;']
            return ['%s();' % self.ctx.func_name(self.ctx.callee(i, i.target), i.target), 'return;']
        return None

    # ---- structuring ----------------------------------------------------------
    def lift(self):
        """C# body lines (without the outer braces)."""
        self.code = {}
        self.cond = {}
        for a in self.order:
            b = self.blocks[a]
            stmts = self.gen_block(b)
            if b.kind == 'branch':
                fl, taken = BRANCH_FLAG[b.insns[-1].mn]
                c = lazy_cond(fl, self.lazy[fl])
                self.cond[a] = c if taken else c.neg()
            self.code[a] = stmts
        self.pos = {a: n for n, a in enumerate(self.order)}
        self.gotos = set()
        lines = self.seq(0, len(self.order), None, [])
        # drop a trailing "return;" at the top level
        while lines and lines[-1] == 'return;':
            lines.pop()
        out = []
        for ln in lines:
            out.append(ln)
        return self.place_labels(out)

    def at(self, p):
        return self.order[p] if p < len(self.order) else None

    def closed(self, lo, hi, entry_preds):
        """Every block in [lo, hi) is only entered from inside, except the first (from entry_preds)."""
        for p in range(lo, hi):
            b = self.blocks[self.order[p]]
            for q in b.preds:
                if lo <= self.pos[q] < hi:
                    continue
                if p == lo and q in entry_preds:
                    continue
                return False
        return True

    def seq(self, lo, hi, follow, loops, header=None):
        """Lines for blocks [lo, hi); `follow` is the block that runs after the region.
        `header` is the loop header whose loop is being written (no label, no new loop)."""
        lines = []
        p = lo
        while p < hi:
            a = self.order[p]
            b = self.blocks[a]
            loop = None if a == header else self.loop_at(p, hi)
            if loop is not None:
                k = loop
                exit_a = self.at(k + 1) if k + 1 < hi else follow
                last = self.blocks[self.order[k]]
                lines.append('@label %06X' % a)
                if last.kind == 'branch' and last.succ and last.succ[0] == a:
                    inner = loops + [(a, exit_a, 'do')]
                    lk = self.order[k]
                    if k == p:
                        body = list(self.code[a])
                    else:
                        body = self.seq(p, k, lk, inner, a) + ['@label %06X' % lk] + self.code[lk]
                    lines.append('do')
                    lines += self.braced(body)
                    lines[-1] = '} while (%s);' % self.cond[self.order[k]]
                    fall = last.succ[1] if len(last.succ) > 1 else None
                    if fall is not None:
                        lines += self.jump(fall, exit_a, loops)
                else:
                    body = self.seq(p, k + 1, a, loops + [(a, exit_a, 'while')], a)
                    if not [x for x in body if not x.lstrip().startswith('@label')]:
                        lines.append('while (true) { } // the game stops here for good')
                    else:
                        lines.append('while (true)')
                        lines += self.braced(body)
                p = k + 1
                continue
            natural = self.at(p + 1) if p + 1 < hi else follow
            if a != header:
                lines.append('@label %06X' % a)
            lines += self.code[a]
            t = self.term(b)
            if t is not None:
                lines += t
                p += 1
                continue
            if b.kind == 'branch':
                taken, fall = b.succ[0], (b.succ[1] if len(b.succ) > 1 else None)
                c = self.cond[a]
                tp = self.pos.get(taken, 1 << 30)
                if fall is not None and self.pos.get(fall) == p + 1 and p + 1 < tp <= hi \
                        and (tp < hi or self.at(tp) == follow):
                    # if (!cond) { then } [else { ... }]
                    then_lo, then_hi = p + 1, tp
                    if self.closed(then_lo, then_hi, {a}):
                        lastb = self.blocks[self.order[then_hi - 1]]
                        join = self.at(tp) if tp < hi else follow
                        u = lastb.succ[0] if lastb.kind == 'jump' and lastb.succ else None
                        if u is not None and u in self.pos and tp < hi and self.pos[u] > tp and (self.pos[u] < hi or self.at(self.pos[u]) == follow) \
                                and self.closed(tp, self.pos[u], {a}):
                            up = self.pos[u]
                            ujoin = self.at(up) if up < hi else follow
                            then = self.seq(then_lo, then_hi, ujoin, loops)
                            els = self.seq(tp, up, ujoin, loops)
                            if not then:
                                lines.append('if (%s)' % c)
                                lines += self.braced(els)
                            else:
                                lines.append('if (%s)' % c.neg())
                                lines += self.braced(then)
                                if els:
                                    lines.append('else')
                                    lines += self.braced(els)
                            p = up
                            continue
                        then = self.seq(then_lo, then_hi, join, loops)
                        lines.append('if (%s)' % c.neg())
                        lines += self.braced(then)
                        p = tp
                        continue
                if tp == p + 1 and fall is not None and self.pos.get(fall) == p + 1:
                    p += 1
                    continue
                j = self.jump(taken, None, loops)
                if len(j) == 1:
                    lines.append('if (%s) %s' % (c, j[0]))
                else:
                    lines.append('if (%s)' % c)
                    lines += self.braced(j)
                if fall is not None:
                    lines += self.jump(fall, natural, loops)
                p += 1
                continue
            # fall or jump
            if b.succ:
                lines += self.jump(b.succ[0], natural, loops)
            p += 1
        return lines

    def braced(self, body):
        return ['{'] + ['    ' + x for x in body] + ['}']

    def loop_at(self, p, hi):
        """Last block of a loop headed by block p, or None."""
        a = self.order[p]
        b = self.blocks[a]
        backs = sorted((self.pos[q] for q in b.preds if self.pos[q] >= p), reverse=True)
        for k in backs:
            if k >= hi:
                continue
            ok = True
            for r in range(p + 1, k + 1):
                for q in self.blocks[self.order[r]].preds:
                    if not (p <= self.pos[q] <= k):
                        ok = False
                        break
                if not ok:
                    break
            if ok:
                return k
        return None

    def jump(self, target, natural, loops):
        """Statements that continue at `target`."""
        if target in self.ext:
            k = self.ext[target]
            return ['%s(); // the same code read with other register sizes' % self.ctx.names[k], 'return;']
        if target == natural:
            return []
        if loops:
            head, exit_a, kind = loops[-1]
            if target == head and kind == 'while':
                return ['continue;']
            if target == exit_a:
                return ['break;']
        tb = self.blocks[target]
        t = self.term(tb)
        if t is not None and len(tb.insns) == 1 and not self.code[target]:
            return list(t)
        self.gotos.add(target)
        return ['goto L_%06X;' % target]

    def place_labels(self, lines):
        out = []
        for n, ln in enumerate(lines):
            s = ln.lstrip()
            if s.startswith('@label '):
                a = int(s[7:], 16)
                if a in self.gotos:
                    ind = ln[:len(ln) - len(s)]
                    nxt = lines[n + 1].strip() if n + 1 < len(lines) else '}'
                    out.append('%sL_%06X:%s' % (ind, a, ' ;' if nxt == '}' or nxt.startswith('@label') and False else ''))
                continue
            out.append(ln)
        # a label must be followed by a statement
        fixed = []
        for n, ln in enumerate(out):
            fixed.append(ln)
            s = ln.strip()
            if s.startswith('L_') and s.endswith(':'):
                nxt = out[n + 1].strip() if n + 1 < len(out) else '}'
                if nxt == '}' or nxt.startswith('L_') or nxt == '':
                    fixed[-1] = ln + ' ;'
        return fixed
