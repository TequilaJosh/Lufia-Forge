"""dis.py rom addr count [m8 x8] : linear 65816 disassembly (LoROM), tracking REP/SEP."""
import sys
import os
sys.path.insert(0, os.path.join(os.path.dirname(os.path.abspath(__file__)), '..', 'l2decomp'))
from analyze import Rom, decode


def fmt(i):
    m = i.mode
    v = i.val
    w = (i.size - 1) * 2
    if m in ('imp', 'acc'):
        a = ''
    elif m.startswith('imm'):
        a = f'#${v:0{w}X}'
    elif m in ('rel', 'rell'):
        a = f'${i.target:06X}'
    else:
        a = f'${v:0{w}X} ({m})'
    return f'{i.mn} {a}'


def dis(path, addr, n, m8=True, x8=True, out=print):
    rom = Rom(path)
    a = addr
    for _ in range(n):
        i = decode(rom, a, m8, x8)
        if i is None:
            break
        raw = ' '.join(f'{rom.u8(a + k):02X}' for k in range(i.size))
        out(f'{a >> 16:02X}:{a & 0xFFFF:04X}  {"m" if m8 else "M"}{"x" if x8 else "X"}  {raw:<12} {fmt(i)}')
        if i.mn == 'REP':
            if v := i.val & 0x20: m8 = False
            if i.val & 0x10: x8 = False
        elif i.mn == 'SEP':
            if i.val & 0x20: m8 = True
            if i.val & 0x10: x8 = True
        a = i.next


if __name__ == '__main__':
    p, a, n = sys.argv[1], int(sys.argv[2], 16), int(sys.argv[3])
    m8 = sys.argv[4] == '1' if len(sys.argv) > 4 else True
    x8 = sys.argv[5] == '1' if len(sys.argv) > 5 else True
    dis(p, a, n, m8, x8)
