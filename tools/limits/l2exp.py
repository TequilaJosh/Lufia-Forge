"""The EXP needed for levels 98-255 (u24 each): 98 stays 9,999,999 (what the game gives), then rising to 16.5 million
at 254, and 255 = FFFFFF (never reached). Writes l2limits_full.asm = l2limits.asm + the table."""
def table():
    t = []
    for lv in range(98, 256):
        if lv == 98:
            v = 9_999_999
        elif lv == 255:
            v = 0xFFFFFF
        else:
            v = 9_999_999 + round(6_500_000 * ((lv - 98) / 157) ** 1.6)
        t.append(v)
    return t


if __name__ == '__main__':
    t = table()
    assert all(b > a for a, b in zip(t, t[1:]))
    lines = []
    for k in range(0, len(t), 8):
        lines.append('  dl ' + ', '.join(f'${v:06X}' for v in t[k:k + 8]))
    src = open('l2limits.asm').read() + '\n'.join(lines) + '\n'
    open('l2limits_full.asm', 'w').write(src)
    print(len(t), t[:3], t[-3:])
