"""Names for routines, RAM, tables and SNES hardware registers.

symbols.txt lines (# starts a comment):
    func  80:8E9D  Decompress            | what it does
    ram   7E:05AC  2  MapId              | what it holds
    table 80:CA14  EventCommands         | what the slots are
    note  80:8EA2                        | a comment put above that instruction
"""
import re

HARDWARE = {
    0x2100: 'INIDISP', 0x2101: 'OBSEL', 0x2102: 'OAMADDL', 0x2103: 'OAMADDH', 0x2104: 'OAMDATA',
    0x2105: 'BGMODE', 0x2106: 'MOSAIC', 0x2107: 'BG1SC', 0x2108: 'BG2SC', 0x2109: 'BG3SC',
    0x210A: 'BG4SC', 0x210B: 'BG12NBA', 0x210C: 'BG34NBA', 0x210D: 'BG1HOFS', 0x210E: 'BG1VOFS',
    0x210F: 'BG2HOFS', 0x2110: 'BG2VOFS', 0x2111: 'BG3HOFS', 0x2112: 'BG3VOFS', 0x2113: 'BG4HOFS',
    0x2114: 'BG4VOFS', 0x2115: 'VMAIN', 0x2116: 'VMADDL', 0x2117: 'VMADDH', 0x2118: 'VMDATAL',
    0x2119: 'VMDATAH', 0x211A: 'M7SEL', 0x211B: 'M7A', 0x211C: 'M7B', 0x211D: 'M7C', 0x211E: 'M7D',
    0x211F: 'M7X', 0x2120: 'M7Y', 0x2121: 'CGADD', 0x2122: 'CGDATA', 0x2123: 'W12SEL',
    0x2124: 'W34SEL', 0x2125: 'WOBJSEL', 0x2126: 'WH0', 0x2127: 'WH1', 0x2128: 'WH2', 0x2129: 'WH3',
    0x212A: 'WBGLOG', 0x212B: 'WOBJLOG', 0x212C: 'TM', 0x212D: 'TS', 0x212E: 'TMW', 0x212F: 'TSW',
    0x2130: 'CGWSEL', 0x2131: 'CGADSUB', 0x2132: 'COLDATA', 0x2133: 'SETINI', 0x2134: 'MPYL',
    0x2135: 'MPYM', 0x2136: 'MPYH', 0x2137: 'SLHV', 0x2138: 'RDOAM', 0x2139: 'RDVRAML',
    0x213A: 'RDVRAMH', 0x213B: 'RDCGRAM', 0x213C: 'OPHCT', 0x213D: 'OPVCT', 0x213E: 'STAT77',
    0x213F: 'STAT78', 0x2140: 'APUIO0', 0x2141: 'APUIO1', 0x2142: 'APUIO2', 0x2143: 'APUIO3',
    0x2180: 'WMDATA', 0x2181: 'WMADDL', 0x2182: 'WMADDM', 0x2183: 'WMADDH',
    0x4016: 'JOYSER0', 0x4017: 'JOYSER1', 0x4200: 'NMITIMEN', 0x4201: 'WRIO', 0x4202: 'WRMPYA',
    0x4203: 'WRMPYB', 0x4204: 'WRDIVL', 0x4205: 'WRDIVH', 0x4206: 'WRDIVB', 0x4207: 'HTIMEL',
    0x4208: 'HTIMEH', 0x4209: 'VTIMEL', 0x420A: 'VTIMEH', 0x420B: 'MDMAEN', 0x420C: 'HDMAEN',
    0x420D: 'MEMSEL', 0x4210: 'RDNMI', 0x4211: 'TIMEUP', 0x4212: 'HVBJOY', 0x4213: 'RDIO',
    0x4214: 'RDDIVL', 0x4215: 'RDDIVH', 0x4216: 'RDMPYL', 0x4217: 'RDMPYH', 0x4218: 'JOY1L',
    0x4219: 'JOY1H', 0x421A: 'JOY2L', 0x421B: 'JOY2H', 0x421C: 'JOY3L', 0x421D: 'JOY3H',
    0x421E: 'JOY4L', 0x421F: 'JOY4H',
}
DMA_REGS = {0: 'DMAP', 1: 'BBAD', 2: 'A1TL', 3: 'A1TH', 4: 'A1B', 5: 'DASL', 6: 'DASH', 7: 'DASB',
            8: 'A2AL', 9: 'A2AH', 0xA: 'NLTR'}
for ch in range(8):
    for r, n in DMA_REGS.items():
        HARDWARE[0x4300 + ch * 0x10 + r] = '%s%d' % (n, ch)

IDENT = re.compile(r'^[A-Za-z_][A-Za-z0-9_]*$')


def parse_addr(s):
    s = s.replace('$', '').replace(':', '')
    return int(s, 16)


class Symbols:
    def __init__(self):
        self.funcs = {}      # addr -> (name, summary)
        self.ram = {}        # addr -> (name, size, summary)
        self.tables = {}     # addr -> (name, summary)
        self.notes = {}      # addr -> [text]
        self.ram_index = {}  # every byte of a named variable -> (base, name)

    def load_all(self, folder):
        """symbols.txt plus every symbols/*.txt (the first name given for an address wins)."""
        import os
        self.load(os.path.join(folder, 'symbols.txt'))
        sub = os.path.join(folder, 'symbols')
        if os.path.isdir(sub):
            for name in sorted(os.listdir(sub)):
                if name.endswith('.txt'):
                    self.load(os.path.join(sub, name))
        self.finish()
        return self

    def load(self, path):
        for raw in open(path, encoding='utf-8'):
            line = raw.rstrip('\n')
            if not line.strip() or line.lstrip().startswith('#'):
                continue
            text = ''
            if '|' in line:
                line, text = line.split('|', 1)
                text = text.strip()
            parts = line.split()
            kind = parts[0]
            if kind == 'func' and len(parts) >= 3:
                self.funcs.setdefault(parse_addr(parts[1]), (parts[2], text))
            elif kind == 'ram' and len(parts) >= 4:
                self.ram.setdefault(parse_addr(parts[1]), (parts[3], int(parts[2]), text))
            elif kind == 'table' and len(parts) >= 3:
                self.tables.setdefault(parse_addr(parts[1]), (parts[2], text))
            elif kind == 'note' and len(parts) >= 2:
                self.notes.setdefault(parse_addr(parts[1]), []).append(text)
        return self

    def finish(self):
        # names must be unique C# identifiers: a repeated name gets the address added
        seen = {}
        other = {n for n, _ in self.funcs.values()} | {n for n, _ in self.tables.values()}
        for a in sorted(self.ram):
            n, size, text = self.ram[a]
            if n in seen or n in HARDWARE.values() or n in other:
                n = '%s_%06X' % (n, a)
                self.ram[a] = (n, size, text)
            seen[n] = a
        # an address with its own name keeps it, even inside a bigger variable (RAM reused by battle and field)
        self.ram_index = {a: (a, n) for a, (n, _, _) in self.ram.items()}
        for a, (n, size, _) in sorted(self.ram.items()):
            for k in range(1, size):
                self.ram_index.setdefault(a + k, (a, n))
        return self

    def addr(self, full, indexed=False):
        """C# expression for a 24-bit address (a name where there is one). An indexed access
        only uses a name of 4 bytes or more (an array): a small variable plus X is usually not it."""
        if full in HARDWARE:
            return HARDWARE[full]
        hit = self.ram_index.get(full)
        if hit is not None and indexed and self.ram[hit[0]][1] < 4:
            hit = None
        if hit is not None:
            base, name = hit
            return name if base == full else '%s + %d' % (name, full - base)
        if full < 0x10000:
            return '0x%04X' % full
        return '0x%06X' % full
