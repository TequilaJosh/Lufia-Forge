"""Write the decompiled C#: one file per ROM bank, jump tables, names, the runtime stub."""
import os
from collections import defaultdict

from lift import Ctx, ALL, EMPTY
from gen import Gen, h

HEADER = """// Lufia II (US) {what}, decompiled by tools/l2decomp from the game's ROM.
// This is the game's own code written as C#: keep it out of the public repository and releases.
// Generated: change tools/l2decomp/symbols.txt (names, notes) or the tool, not this file.
"""


class Program(Ctx):
    def __init__(self, an, syms):
        Ctx.__init__(self, an, syms)
        self.live = [f for f in an.funcs.values() if f.merged_into is None]
        self.variants = defaultdict(list)
        for f in self.live:
            self.variants[f.entry].append(f)
        self.table_use = {}      # table addr -> (targets, m8, x8)
        self.gens = {}
        self.callers = defaultdict(set)
        self.assign_names()

    # ---- names --------------------------------------------------------------
    def assign_names(self):
        taken = set()
        table_slot = {}
        for base, targets in self.an.table_sites.items():
            tname = self.syms.tables.get(base, (None,))[0]
            if not tname:
                continue
            for n, t in enumerate(targets):
                if t is not None and t not in table_slot:
                    table_slot[t] = '%s_%02X' % (tname.rstrip('s') if tname.endswith('s') else tname, n)
        for entry, fs in sorted(self.variants.items()):
            base = self.syms.funcs.get(entry, (None,))[0] or table_slot.get(entry) \
                or 'Sub_%02X_%04X' % (entry >> 16, entry & 0xFFFF)
            if len(fs) == 1:
                names = {fs[0].key: base}
            else:
                names = {}
                for f in fs:
                    names[f.key] = '%s_M%dX%d' % (base, 8 if f.m8 else 16, 8 if f.x8 else 16)
            for k, n in names.items():
                if n in taken:
                    n = '%s_%02X%04X' % (n, k[0] >> 16, k[0] & 0xFFFF)   # the same name used twice: add the address
                while n in taken:
                    n += '_'
                taken.add(n)
                self.names[k] = n

    def func_name(self, key, target):
        if key is None:
            return 'Far(0x%06X)' % target
        return self.names[key]

    def table_name(self, base, ins):
        if base not in self.table_use:
            self.table_use[base] = (self.an.table_sites.get(base, []), ins.m8, ins.x8)
        name = self.syms.tables.get(base, (None,))[0]
        return name or 'Table_%02X_%04X' % (base >> 16, base & 0xFFFF)

    # ---- whole-program flag facts ---------------------------------------------
    def analyse(self):
        for f in self.live:
            self.gens[f.key] = Gen(self, f)
        for f in self.live:
            self.must_def[f.key] = ALL
        for _ in range(12):
            changed = False
            for f in self.live:
                v = self.gens[f.key].must_define()
                if v != self.must_def[f.key]:
                    self.must_def[f.key] = v
                    changed = True
            if not changed:
                break
        for _ in range(30):
            changed = False
            for f in self.live:
                g = self.gens[f.key]
                before = (self.live_in.get(f.key), dict(self.live_out))
                self.live_in[f.key] = g.liveness()
                g.propagate_live_out()
                if before != (self.live_in.get(f.key), self.live_out):
                    changed = True
            if not changed:
                break
        self.find_direct_uses()
        for f in self.live:
            for k in f.calls | f.tails:
                k2 = self.key(*k)
                if k2:
                    self.callers[k2].add(f.key)

    # ---- writing --------------------------------------------------------------
    def describe(self, f):
        bits = []
        if f.m8 is not None:
            bits.append('A %d-bit, X/Y %d-bit' % (8 if f.m8 else 16, 8 if f.x8 else 16))
        if f.db not in (None, 'unset'):
            bits.append('DB = %s' % h(f.db, 2))
        li = self.reads_own(f)
        if li:
            bits.append('reads flag %s' % '/'.join(sorted(li)))
        lo = self.direct_out.get(f.key, EMPTY) & self.must_def.get(f.key, EMPTY)
        if lo:
            bits.append('returns flag %s' % '/'.join(sorted(lo)))
        return ', '.join(bits)

    def reads_own(self, f):
        """Flags the function itself reads before writing them (not through its callees)."""
        g = self.gens[f.key]
        found = set()
        seen = set()
        work = [(f.entry, frozenset())]
        while work:
            a, defined = work.pop()
            if (a, defined) in seen or a not in g.blocks:
                continue
            seen.add((a, defined))
            b = g.blocks[a]
            for i in b.insns:
                u, d = g.effects(i)
                if i.mn in ('JSR', 'JSL'):
                    u = EMPTY
                found |= set(u) - defined
                defined = defined | d
            for s in b.succ:
                work.append((s, defined))
        return frozenset(found)

    def find_direct_uses(self):
        """Flags a caller reads straight after a call (same block, before anything rewrites them)."""
        self.direct_out = {}
        for f in self.live:
            g = self.gens[f.key]
            for b in g.blocks.values():
                for n, i in enumerate(b.insns):
                    if i.mn not in ('JSR', 'JSL') or i.table is not None:
                        continue
                    k = self.callee(i, i.target) if i.mode in ('abs', 'long') else None
                    if k is None:
                        continue
                    used, done = set(), set()
                    for j in b.insns[n + 1:]:
                        if j.mn in ('JSR', 'JSL'):
                            break
                        u, d = g.effects(j)
                        used |= set(u) - done
                        done |= set(d)
                    self.direct_out[k] = self.direct_out.get(k, EMPTY) | frozenset(used)

    def function_text(self, f):
        g = self.gens[f.key]
        body = g.lift()
        name = self.names[f.key]
        summary = self.syms.funcs.get(f.entry, (None, ''))[1]
        callers = sorted(self.names[k] for k in self.callers.get(f.key, ()) if k in self.names)
        out = []
        out.append('    /// <summary>%s</summary>' % (summary or '(not yet described)'))
        rem = '$%02X:%04X, %s' % (f.entry >> 16, f.entry & 0xFFFF, self.describe(f))
        if callers:
            shown = callers[:8]
            rem += '. Called by %s%s' % (', '.join(shown), ' and %d more' % (len(callers) - 8) if len(callers) > 8 else '')
        out.append('    /// <remarks>%s.</remarks>' % rem.rstrip(', '))
        for n in f.notes:
            out.append('    // analysis: %s' % n)
        out.append('    public static void %s()' % name)
        out.append('    {')
        notes = self.syms.notes
        for ln in body:
            out.append('        ' + ln)
        out.append('    }')
        return out

    def write(self, outdir):
        os.makedirs(outdir, exist_ok=True)
        banks = defaultdict(list)
        for f in self.live:
            banks[f.entry >> 16].append(f)
        lines_total = 0
        for bank, fs in sorted(banks.items()):
            fs.sort(key=lambda g: (g.entry, g.m8, g.x8))
            out = [HEADER.format(what='bank $%02X' % bank), 'namespace Lufia2.Decomp;', '',
                   'public static partial class Game', '{']
            for f in fs:
                out += self.function_text(f)
                out.append('')
            out[-1] = '}'
            path = os.path.join(outdir, 'Bank%02X.cs' % bank)
            open(path, 'w', encoding='utf-8', newline='\n').write('\n'.join(out) + '\n')
            lines_total += len(out)
        self.write_tables(outdir)
        self.write_ram(outdir)
        self.write_listings(outdir, banks)
        return lines_total

    def write_listings(self, outdir, banks):
        from listing import listing
        d = os.path.join(outdir, 'asm')
        os.makedirs(d, exist_ok=True)
        for bank, fs in sorted(banks.items()):
            out = ['; Lufia II (US) bank $%02X: the 65816 code each C# function was made from.' % bank,
                   '; m/M = 8/16-bit A, x/X = 8/16-bit X and Y, db = data bank (-- unknown).', '']
            for f in fs:
                out.append('%s:  ; $%02X:%04X' % (self.names[f.key], f.entry >> 16, f.entry & 0xFFFF))
                out += ['    ' + ln for ln in listing(f)]
                out.append('')
            path = os.path.join(d, 'Bank%02X.asm' % bank)
            open(path, 'w', encoding='utf-8', newline='\n').write('\n'.join(out) + '\n')

    def write_tables(self, outdir):
        out = [HEADER.format(what='jump tables'), 'namespace Lufia2.Decomp;', '', 'using System;', '',
               'public static partial class Game', '{']
        for base, (targets, m8, x8) in sorted(self.table_use.items()):
            name = self.syms.tables.get(base, (None,))[0] or 'Table_%02X_%04X' % (base >> 16, base & 0xFFFF)
            desc = self.syms.tables.get(base, (None, ''))[1]
            out.append('    /// <summary>%s</summary>' % (desc or 'Jump table at $%02X:%04X.' % (base >> 16, base & 0xFFFF)))
            out.append('    public static readonly Action[] %s =' % name)
            out.append('    {')
            for n, t in enumerate(targets):
                if t is None:
                    out.append('        null, // %02X unused' % n)
                    continue
                k = self.key(t, m8, x8)
                out.append('        %s, // %02X' % (self.func_name(k, t) if k else 'null', n))
            out.append('    };')
            out.append('')
        out[-1] = '}'
        open(os.path.join(outdir, 'Tables.cs'), 'w', encoding='utf-8', newline='\n').write('\n'.join(out) + '\n')

    def write_ram(self, outdir):
        from syms import HARDWARE
        out = [HEADER.format(what='RAM and ROM names'), 'namespace Lufia2.Decomp;', '',
               'public static partial class Game', '{', '    // SNES hardware registers']
        for a, n in sorted(HARDWARE.items()):
            out.append('    public const int %s = 0x%04X;' % (n, a))
        out.append('')
        out.append('    // the game\'s RAM and ROM')
        for a, (n, size, text) in sorted(self.syms.ram.items()):
            if text:
                out.append('    /// <summary>%s</summary>' % text)
            out.append('    public const int %s = 0x%06X; // %d byte%s' % (n, a, size, '' if size == 1 else 's'))
        out.append('}')
        open(os.path.join(outdir, 'Names.cs'), 'w', encoding='utf-8', newline='\n').write('\n'.join(out) + '\n')
