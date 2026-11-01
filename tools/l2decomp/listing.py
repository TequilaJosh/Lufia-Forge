"""Plain 65816 listing of analysed functions (to check the C# against, and for readers)."""


def fmt(ins):
    m, v = ins.mode, ins.val
    t = ins.target
    s = {
        'imp': '', 'acc': 'A', 'imm8': '#$%02X' % v,
        'immM': ('#$%02X' if ins.m8 else '#$%04X') % v, 'immX': ('#$%02X' if ins.x8 else '#$%04X') % v,
        'dp': '$%02X' % v, 'dpx': '$%02X,X' % v, 'dpy': '$%02X,Y' % v, 'idp': '($%02X)' % v,
        'idpx': '($%02X,X)' % v, 'idpy': '($%02X),Y' % v, 'ildp': '[$%02X]' % v, 'ildpy': '[$%02X],Y' % v,
        'abs': '$%04X' % v, 'absx': '$%04X,X' % v, 'absy': '$%04X,Y' % v, 'long': '$%06X' % v,
        'longx': '$%06X,X' % v, 'sr': '$%02X,S' % v, 'isry': '($%02X,S),Y' % v,
        'rel': '$%06X' % (t or 0), 'rell': '$%06X' % (t or 0), 'iabs': '($%04X)' % v,
        'iabsx': '($%04X,X)' % v, 'ilabs': '[$%04X]' % v, 'mv': '$%02X,$%02X' % (v & 0xFF, v >> 8),
        'pea': '#$%04X' % v, 'pei': '($%02X)' % v, 'per': '$%04X' % v,
    }[m]
    if ins.mn in ('JSR', 'JMP') and m == 'abs':
        s = '$%06X' % t
    return ('%s %s' % (ins.mn, s)).strip()


def listing(f, names=None):
    out = []
    for a in sorted(f.insns):
        i = f.insns[a]
        flags = '%s%s' % ('m' if i.m8 else 'M', 'x' if i.x8 else 'X')
        db = '--' if i.db is None else '%02X' % i.db
        out.append('%02X:%04X  %s db=%s  %s' % (a >> 16, a & 0xFFFF, flags, db, fmt(i)))
    return out
