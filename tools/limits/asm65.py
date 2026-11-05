"""Tiny two-pass 65816 assembler for asar-style patch files (LoROM).

Supports: org $xxxxxx, db $xx[,..], dw $xxxx[,..], labels (name: / _name:), ; comments,
instructions with operands: none / A, #$imm (width = digits: 2 -> 8-bit, 4 -> 16-bit), $dp, $dp,x, $dp,y,
$abs, $abs,x, $abs,y, $long, $long,x, $sr,s, ($dp), ($dp),y, [$dp], [$dp],y, ($sr,s),y, ($abs,x), label
(branches, jsr/jmp/jsl/jml/brl). Number widths come from the digit count, as asar does for hex.
assemble(text) -> list of (snes_addr, bytes).
"""
import re
import sys
import os
sys.path.insert(0, os.path.join(os.path.dirname(os.path.abspath(__file__)), '..', 'l2decomp'))
from ops import OPS

REV = {}
for code, (mn, mode) in enumerate(OPS):
    REV.setdefault((mn, mode), code)


def num(s):
    s = s.strip()
    if s.startswith('$'):
        return int(s[1:], 16), len(s) - 1
    if s.startswith('%'):
        return int(s[1:], 2), (len(s) - 1 + 3) // 4
    return int(s), 0


def parse_operand(mn, op, labels, pc, final):
    """-> (mode, value, size of operand bytes)."""
    op = op.strip()
    if op == '' or op.upper() == 'A':
        return ('acc' if (mn, 'acc') in REV else 'imp'), 0, 0
    if mn in ('BRA', 'BEQ', 'BNE', 'BCC', 'BCS', 'BMI', 'BPL', 'BVC', 'BVS', 'BRL', 'PER'):
        t = value(op, labels, final)
        if mn in ('BRL', 'PER'):
            d = t - ((pc + 3) & 0xFFFF | pc & 0xFF0000) if final else 0
            return 'rell', d & 0xFFFF, 2
        d = t - (pc + 2) if final else 0
        if final and not -128 <= d <= 127:
            raise ValueError(f'branch out of range at {pc:06X}: {op}')
        return 'rel', d & 0xFF, 1
    if mn in ('MVN', 'MVP'):
        a, b = op.split(',')
        return 'mv', (value(b, labels, final) & 0xFF) | ((value(a, labels, final) & 0xFF) << 8), 2
    if op.startswith('#'):
        v, w = num(op[1:]) if op[1] in '$%0123456789' else (value(op[1:], labels, final), 4)
        mode = {'LDX': 'immX', 'LDY': 'immX', 'CPX': 'immX', 'CPY': 'immX', 'REP': 'imm8', 'SEP': 'imm8',
                'BRK': 'imm8', 'COP': 'imm8', 'WDM': 'imm8'}.get(mn, 'immM')
        n = 1 if w <= 2 else 2
        if mode == 'imm8':
            n = 1
        return mode, v, n
    m = re.fullmatch(r'\((.+),\s*s\)\s*,\s*y', op, re.I)
    if m:
        return 'isry', value(m.group(1), labels, final), 1
    m = re.fullmatch(r'\((.+)\)\s*,\s*y', op, re.I)
    if m:
        return 'idpy', value(m.group(1), labels, final), 1
    m = re.fullmatch(r'\[(.+)\]\s*,\s*y', op, re.I)
    if m:
        return 'ildpy', value(m.group(1), labels, final), 1
    m = re.fullmatch(r'\[(.+)\]', op)
    if m:
        v, w = width(m.group(1), labels, final)
        return ('ilabs' if w > 2 else 'ildp'), v, (2 if w > 2 else 1)
    m = re.fullmatch(r'\((.+),\s*x\)', op, re.I)
    if m:
        v, w = width(m.group(1), labels, final)
        return ('iabsx' if w > 2 or mn in ('JMP', 'JSR') else 'idpx'), v, (2 if w > 2 or mn in ('JMP', 'JSR') else 1)
    m = re.fullmatch(r'\((.+)\)', op)
    if m:
        v, w = width(m.group(1), labels, final)
        return ('iabs' if w > 2 or mn == 'JMP' else 'idp'), v, (2 if w > 2 or mn == 'JMP' else 1)
    m = re.fullmatch(r'(.+?)\s*,\s*([xys])', op, re.I)
    idx = ''
    if m:
        op, idx = m.group(1), m.group(2).lower()
    v, w = width(op, labels, final)
    if mn in ('JSL', 'JML'):
        w = 6
    if mn in ('JSR', 'JMP') and w > 4 and op.startswith('$'):
        raise ValueError(f'{mn} needs a 16-bit address: {op}')
    if mn in ('JSR', 'JMP'):
        w = 4
    if idx == 's':
        return 'sr', v, 1
    if w <= 2:
        return {'': 'dp', 'x': 'dpx', 'y': 'dpy'}[idx], v & 0xFF, 1
    if w <= 4:
        return {'': 'abs', 'x': 'absx', 'y': 'absy'}[idx], v & 0xFFFF, 2
    return {'': 'long', 'x': 'longx'}[idx], v & 0xFFFFFF, 3


def width(s, labels, final):
    s = s.strip()
    if s.startswith('$') or s[0].isdigit():
        return num(s)
    v = value(s, labels, final)
    return v, 6 if v > 0xFFFF else 4


def value(s, labels, final):
    s = s.strip()
    if s.startswith('$') or s.startswith('%') or s[0].isdigit():
        return num(s)[0]
    m = re.fullmatch(r'(\w+)\s*([+-])\s*(.+)', s)
    if m:
        b = value(m.group(1), labels, final)
        o = value(m.group(3), labels, final)
        return b + o if m.group(2) == '+' else b - o
    if s in labels:
        return labels[s]
    if final:
        raise KeyError(f'unknown label {s}')
    return 0x808000


def assemble(text, defines=None):
    lines = []
    for raw in text.splitlines():
        line = raw.split(';')[0].strip()
        if line:
            lines.append(line)
    labels = dict(defines or {})
    for final in (False, True):
        out, pc, cur = [], None, None
        for line in lines:
            m = re.match(r'^([A-Za-z_.][\w.]*):\s*(.*)$', line)
            if m:
                labels[m.group(1)] = pc
                line = m.group(2)
                if not line:
                    continue
            parts = line.split(None, 1)
            mn = parts[0].upper()
            op = parts[1] if len(parts) > 1 else ''
            if mn == 'ORG':
                pc = value(op, labels, True)
                cur = [pc, bytearray()]
                out.append(cur)
                continue
            if mn in ('DB', 'DW', 'DL'):
                n = {'DB': 1, 'DW': 2, 'DL': 3}[mn]
                for item in op.split(','):
                    v = value(item, labels, final)
                    cur[1] += (v & ((1 << (8 * n)) - 1)).to_bytes(n, 'little')
                    pc += n
                continue
            mode, v, n = parse_operand(mn, op, labels, pc, final)
            if mn == 'JMP' and mode == 'long':
                mn = 'JML'
            key = (mn, mode)
            if key not in REV:
                raise ValueError(f'no opcode for {mn} {mode} ({line})')
            cur[1].append(REV[key])
            cur[1] += (v & ((1 << (8 * n)) - 1)).to_bytes(n, 'little') if n else b''
            pc += 1 + n
    return [(a, bytes(b)) for a, b in out], labels


def lorom(a):
    return ((a >> 16) & 0x7F) * 0x8000 + (a & 0xFFFF) - 0x8000


if __name__ == '__main__':
    recs, _ = assemble(open(sys.argv[1]).read())
    for a, b in recs:
        print(f'{a:06X} ({lorom(a):06X}) {len(b):3d}: {b.hex(" ").upper()}')
