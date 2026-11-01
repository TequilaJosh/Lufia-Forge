"""65816 opcode table: OPS[opcode] = (mnemonic, mode)."""

ALU = ['ORA', 'AND', 'EOR', 'ADC', 'STA', 'LDA', 'CMP', 'SBC']
ALU_MODES = {0x01: 'idpx', 0x03: 'sr', 0x05: 'dp', 0x07: 'ildp', 0x09: 'immM', 0x0D: 'abs',
             0x0F: 'long', 0x11: 'idpy', 0x12: 'idp', 0x13: 'isry', 0x15: 'dpx', 0x17: 'ildpy',
             0x19: 'absy', 0x1D: 'absx', 0x1F: 'longx'}

OTHER = """
00 BRK imm8|02 COP imm8|04 TSB dp|06 ASL dp|08 PHP imp|0A ASL acc|0B PHD imp|0C TSB abs|0E ASL abs
10 BPL rel|14 TRB dp|16 ASL dpx|18 CLC imp|1A INC acc|1B TCS imp|1C TRB abs|1E ASL absx
20 JSR abs|22 JSL long|24 BIT dp|26 ROL dp|28 PLP imp|2A ROL acc|2B PLD imp|2C BIT abs|2E ROL abs
30 BMI rel|34 BIT dpx|36 ROL dpx|38 SEC imp|3A DEC acc|3B TSC imp|3C BIT absx|3E ROL absx
40 RTI imp|42 WDM imm8|44 MVP mv|46 LSR dp|48 PHA imp|4A LSR acc|4B PHK imp|4C JMP abs|4E LSR abs
50 BVC rel|54 MVN mv|56 LSR dpx|58 CLI imp|5A PHY imp|5B TCD imp|5C JML long|5E LSR absx
60 RTS imp|62 PER rell|64 STZ dp|66 ROR dp|68 PLA imp|6A ROR acc|6B RTL imp|6C JMP iabs|6E ROR abs
70 BVS rel|74 STZ dpx|76 ROR dpx|78 SEI imp|7A PLY imp|7B TDC imp|7C JMP iabsx|7E ROR absx
80 BRA rel|82 BRL rell|84 STY dp|86 STX dp|88 DEY imp|8A TXA imp|8B PHB imp|8C STY abs|8E STX abs
90 BCC rel|94 STY dpx|96 STX dpy|98 TYA imp|9A TXS imp|9B TXY imp|9C STZ abs|9E STZ absx
A0 LDY immX|A2 LDX immX|A4 LDY dp|A6 LDX dp|A8 TAY imp|AA TAX imp|AB PLB imp|AC LDY abs|AE LDX abs
B0 BCS rel|B4 LDY dpx|B6 LDX dpy|B8 CLV imp|BA TSX imp|BB TYX imp|BC LDY absx|BE LDX absy
C0 CPY immX|C2 REP imm8|C4 CPY dp|C6 DEC dp|C8 INY imp|CA DEX imp|CB WAI imp|CC CPY abs|CE DEC abs
D0 BNE rel|D4 PEI pei|D6 DEC dpx|D8 CLD imp|DA PHX imp|DB STP imp|DC JML ilabs|DE DEC absx
E0 CPX immX|E2 SEP imm8|E4 CPX dp|E6 INC dp|E8 INX imp|EA NOP imp|EB XBA imp|EC CPX abs|EE INC abs
F0 BEQ rel|F4 PEA pea|F6 INC dpx|F8 SED imp|FA PLX imp|FB XCE imp|FC JSR iabsx|FE INC absx
"""

OPS = [None] * 256
for i, name in enumerate(ALU):
    for low, mode in ALU_MODES.items():
        OPS[i * 0x20 + low] = (name, mode)
OPS[0x89] = ('BIT', 'immM')
for line in OTHER.strip().splitlines():
    for item in line.split('|'):
        code, name, mode = item.split()
        OPS[int(code, 16)] = (name, mode)
assert all(OPS), [hex(i) for i, o in enumerate(OPS) if not o]

FIXED = {'imp': 1, 'acc': 1, 'imm8': 2, 'dp': 2, 'dpx': 2, 'dpy': 2, 'idp': 2, 'idpx': 2,
         'idpy': 2, 'ildp': 2, 'ildpy': 2, 'abs': 3, 'absx': 3, 'absy': 3, 'long': 4,
         'longx': 4, 'sr': 2, 'isry': 2, 'rel': 2, 'rell': 3, 'iabs': 3, 'iabsx': 3,
         'ilabs': 3, 'mv': 3, 'pea': 3, 'pei': 2, 'per': 3}


def size(op, m8, x8):
    mode = OPS[op][1]
    if mode == 'immM':
        return 2 if m8 else 3
    if mode == 'immX':
        return 2 if x8 else 3
    return FIXED[mode]


BRANCHES = {'BPL', 'BMI', 'BVC', 'BVS', 'BCC', 'BCS', 'BNE', 'BEQ'}
BAD = {'BRK', 'COP', 'STP', 'WDM'}
