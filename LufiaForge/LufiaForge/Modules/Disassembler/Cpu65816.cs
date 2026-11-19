using LufiaForge.Core;

namespace LufiaForge.Modules.Disassembler;

// WDC 65816 instruction decoder. all 256 opcodes, with the right mnemonic, addressing mode and operand size rules
public static class Cpu65816
{
    // addressing modes
    private enum AddrMode
    {
        Imp,       // Implied - no operand
        Acc,       // Accumulator - "A"
        Imm_M,     // Immediate - 1 byte (M=1) or 2 bytes (M=0)
        Imm_X,     // Immediate - 1 byte (X=1) or 2 bytes (X=0)
        Imm8,      // Immediate - always 1 byte (REP, SEP, BRK, COP, WDM)
        Imm16,     // Immediate - always 2 bytes (PEA)
        Dp,        // Direct Page
        Dp_X,      // Direct Page, X
        Dp_Y,      // Direct Page, Y
        Dp_Ind,    // (Direct Page)
        Dp_IndX,   // (Direct Page, X)
        Dp_IndY,   // (Direct Page), Y
        Dp_IndL,   // [Direct Page]
        Dp_IndLY,  // [Direct Page], Y
        Abs,       // Absolute
        Abs_X,     // Absolute, X
        Abs_Y,     // Absolute, Y
        Abs_Ind,   // (Absolute)
        Abs_IndX,  // (Absolute, X)
        Abs_IndL,  // [Absolute]
        Long,      // Absolute Long
        Long_X,    // Absolute Long, X
        Rel,       // PC-relative, 1-byte signed
        RelL,      // PC-relative long, 2-byte signed
        Sr,        // Stack Relative
        Sr_IndY,   // (Stack Relative), Y
        Blk,       // Block Move (2 operand bytes: dst bank, src bank)
    }

    // opcode table, 256 entries
    private record struct OpcodeInfo(string Mnemonic, AddrMode Mode);

    private static readonly OpcodeInfo[] Table = BuildTable();

    // base cycle count of every opcode (WDC 65C816 datasheet, 8-bit registers, direct page aligned)
    // add 1 per extra byte moved with a 16-bit accumulator/index, 1 when the direct page register's low byte
    // isn't 0, 1 when indexing crosses a page, and 1-2 for taken branches
    private static readonly byte[] Cycles =
    {
        //  0  1  2  3  4  5  6  7  8  9  A  B  C  D  E  F
            7, 6, 7, 4, 5, 3, 5, 6, 3, 2, 2, 4, 6, 4, 6, 5,   // 0x
            2, 5, 5, 7, 5, 4, 6, 6, 2, 4, 2, 2, 6, 4, 7, 5,   // 1x
            6, 6, 8, 4, 3, 3, 5, 6, 4, 2, 2, 5, 4, 4, 6, 5,   // 2x
            2, 5, 5, 7, 4, 4, 6, 6, 2, 4, 2, 2, 4, 4, 7, 5,   // 3x
            6, 6, 2, 4, 7, 3, 5, 6, 3, 2, 2, 3, 3, 4, 6, 5,   // 4x
            2, 5, 5, 7, 7, 4, 6, 6, 2, 4, 3, 2, 4, 4, 7, 5,   // 5x
            6, 6, 6, 4, 3, 3, 5, 6, 4, 2, 2, 6, 5, 4, 6, 5,   // 6x
            2, 5, 5, 7, 4, 4, 6, 6, 2, 4, 4, 2, 6, 4, 7, 5,   // 7x
            3, 6, 4, 4, 3, 3, 3, 6, 2, 2, 2, 3, 4, 4, 4, 5,   // 8x
            2, 6, 5, 7, 4, 4, 4, 6, 2, 5, 2, 2, 4, 5, 5, 5,   // 9x
            2, 6, 2, 4, 3, 3, 3, 6, 2, 2, 2, 4, 4, 4, 4, 5,   // Ax
            2, 5, 5, 7, 4, 4, 4, 6, 2, 4, 2, 2, 4, 4, 4, 5,   // Bx
            2, 6, 3, 4, 3, 3, 5, 6, 2, 2, 2, 3, 4, 4, 6, 5,   // Cx
            2, 5, 5, 7, 6, 4, 6, 6, 2, 4, 3, 3, 6, 4, 7, 5,   // Dx
            2, 6, 3, 4, 3, 3, 5, 6, 2, 2, 2, 3, 4, 4, 6, 5,   // Ex
            2, 5, 5, 7, 5, 4, 6, 6, 2, 4, 4, 2, 8, 4, 7, 5,   // Fx
    };

    // base cycle count of an opcode (Cycles says what adds to it)
    public static int BaseCycles(byte opcode) => Cycles[opcode];

    private static OpcodeInfo[] BuildTable()
    {
        var table = new OpcodeInfo[256];

        // 0x00-0x0F
        table[0x00] = new("BRK", AddrMode.Imm8);
        table[0x01] = new("ORA", AddrMode.Dp_IndX);
        table[0x02] = new("COP", AddrMode.Imm8);
        table[0x03] = new("ORA", AddrMode.Sr);
        table[0x04] = new("TSB", AddrMode.Dp);
        table[0x05] = new("ORA", AddrMode.Dp);
        table[0x06] = new("ASL", AddrMode.Dp);
        table[0x07] = new("ORA", AddrMode.Dp_IndL);
        table[0x08] = new("PHP", AddrMode.Imp);
        table[0x09] = new("ORA", AddrMode.Imm_M);
        table[0x0A] = new("ASL", AddrMode.Acc);
        table[0x0B] = new("PHD", AddrMode.Imp);
        table[0x0C] = new("TSB", AddrMode.Abs);
        table[0x0D] = new("ORA", AddrMode.Abs);
        table[0x0E] = new("ASL", AddrMode.Abs);
        table[0x0F] = new("ORA", AddrMode.Long);

        // 0x10-0x1F
        table[0x10] = new("BPL", AddrMode.Rel);
        table[0x11] = new("ORA", AddrMode.Dp_IndY);
        table[0x12] = new("ORA", AddrMode.Dp_Ind);
        table[0x13] = new("ORA", AddrMode.Sr_IndY);
        table[0x14] = new("TRB", AddrMode.Dp);
        table[0x15] = new("ORA", AddrMode.Dp_X);
        table[0x16] = new("ASL", AddrMode.Dp_X);
        table[0x17] = new("ORA", AddrMode.Dp_IndLY);
        table[0x18] = new("CLC", AddrMode.Imp);
        table[0x19] = new("ORA", AddrMode.Abs_Y);
        table[0x1A] = new("INC", AddrMode.Acc);
        table[0x1B] = new("TCS", AddrMode.Imp);
        table[0x1C] = new("TRB", AddrMode.Abs);
        table[0x1D] = new("ORA", AddrMode.Abs_X);
        table[0x1E] = new("ASL", AddrMode.Abs_X);
        table[0x1F] = new("ORA", AddrMode.Long_X);

        // 0x20-0x2F
        table[0x20] = new("JSR", AddrMode.Abs);
        table[0x21] = new("AND", AddrMode.Dp_IndX);
        table[0x22] = new("JSL", AddrMode.Long);
        table[0x23] = new("AND", AddrMode.Sr);
        table[0x24] = new("BIT", AddrMode.Dp);
        table[0x25] = new("AND", AddrMode.Dp);
        table[0x26] = new("ROL", AddrMode.Dp);
        table[0x27] = new("AND", AddrMode.Dp_IndL);
        table[0x28] = new("PLP", AddrMode.Imp);
        table[0x29] = new("AND", AddrMode.Imm_M);
        table[0x2A] = new("ROL", AddrMode.Acc);
        table[0x2B] = new("PLD", AddrMode.Imp);
        table[0x2C] = new("BIT", AddrMode.Abs);
        table[0x2D] = new("AND", AddrMode.Abs);
        table[0x2E] = new("ROL", AddrMode.Abs);
        table[0x2F] = new("AND", AddrMode.Long);

        // 0x30-0x3F
        table[0x30] = new("BMI", AddrMode.Rel);
        table[0x31] = new("AND", AddrMode.Dp_IndY);
        table[0x32] = new("AND", AddrMode.Dp_Ind);
        table[0x33] = new("AND", AddrMode.Sr_IndY);
        table[0x34] = new("BIT", AddrMode.Dp_X);
        table[0x35] = new("AND", AddrMode.Dp_X);
        table[0x36] = new("ROL", AddrMode.Dp_X);
        table[0x37] = new("AND", AddrMode.Dp_IndLY);
        table[0x38] = new("SEC", AddrMode.Imp);
        table[0x39] = new("AND", AddrMode.Abs_Y);
        table[0x3A] = new("DEC", AddrMode.Acc);
        table[0x3B] = new("TSC", AddrMode.Imp);
        table[0x3C] = new("BIT", AddrMode.Abs_X);
        table[0x3D] = new("AND", AddrMode.Abs_X);
        table[0x3E] = new("ROL", AddrMode.Abs_X);
        table[0x3F] = new("AND", AddrMode.Long_X);

        // 0x40-0x4F
        table[0x40] = new("RTI", AddrMode.Imp);
        table[0x41] = new("EOR", AddrMode.Dp_IndX);
        table[0x42] = new("WDM", AddrMode.Imm8);
        table[0x43] = new("EOR", AddrMode.Sr);
        table[0x44] = new("MVP", AddrMode.Blk);
        table[0x45] = new("EOR", AddrMode.Dp);
        table[0x46] = new("LSR", AddrMode.Dp);
        table[0x47] = new("EOR", AddrMode.Dp_IndL);
        table[0x48] = new("PHA", AddrMode.Imp);
        table[0x49] = new("EOR", AddrMode.Imm_M);
        table[0x4A] = new("LSR", AddrMode.Acc);
        table[0x4B] = new("PHK", AddrMode.Imp);
        table[0x4C] = new("JMP", AddrMode.Abs);
        table[0x4D] = new("EOR", AddrMode.Abs);
        table[0x4E] = new("LSR", AddrMode.Abs);
        table[0x4F] = new("EOR", AddrMode.Long);

        // 0x50-0x5F
        table[0x50] = new("BVC", AddrMode.Rel);
        table[0x51] = new("EOR", AddrMode.Dp_IndY);
        table[0x52] = new("EOR", AddrMode.Dp_Ind);
        table[0x53] = new("EOR", AddrMode.Sr_IndY);
        table[0x54] = new("MVN", AddrMode.Blk);
        table[0x55] = new("EOR", AddrMode.Dp_X);
        table[0x56] = new("LSR", AddrMode.Dp_X);
        table[0x57] = new("EOR", AddrMode.Dp_IndLY);
        table[0x58] = new("CLI", AddrMode.Imp);
        table[0x59] = new("EOR", AddrMode.Abs_Y);
        table[0x5A] = new("PHY", AddrMode.Imp);
        table[0x5B] = new("TCD", AddrMode.Imp);
        table[0x5C] = new("JML", AddrMode.Long);
        table[0x5D] = new("EOR", AddrMode.Abs_X);
        table[0x5E] = new("LSR", AddrMode.Abs_X);
        table[0x5F] = new("EOR", AddrMode.Long_X);

        // 0x60-0x6F
        table[0x60] = new("RTS", AddrMode.Imp);
        table[0x61] = new("ADC", AddrMode.Dp_IndX);
        table[0x62] = new("PER", AddrMode.RelL);
        table[0x63] = new("ADC", AddrMode.Sr);
        table[0x64] = new("STZ", AddrMode.Dp);
        table[0x65] = new("ADC", AddrMode.Dp);
        table[0x66] = new("ROR", AddrMode.Dp);
        table[0x67] = new("ADC", AddrMode.Dp_IndL);
        table[0x68] = new("PLA", AddrMode.Imp);
        table[0x69] = new("ADC", AddrMode.Imm_M);
        table[0x6A] = new("ROR", AddrMode.Acc);
        table[0x6B] = new("RTL", AddrMode.Imp);
        table[0x6C] = new("JMP", AddrMode.Abs_Ind);
        table[0x6D] = new("ADC", AddrMode.Abs);
        table[0x6E] = new("ROR", AddrMode.Abs);
        table[0x6F] = new("ADC", AddrMode.Long);

        // 0x70-0x7F
        table[0x70] = new("BVS", AddrMode.Rel);
        table[0x71] = new("ADC", AddrMode.Dp_IndY);
        table[0x72] = new("ADC", AddrMode.Dp_Ind);
        table[0x73] = new("ADC", AddrMode.Sr_IndY);
        table[0x74] = new("STZ", AddrMode.Dp_X);
        table[0x75] = new("ADC", AddrMode.Dp_X);
        table[0x76] = new("ROR", AddrMode.Dp_X);
        table[0x77] = new("ADC", AddrMode.Dp_IndLY);
        table[0x78] = new("SEI", AddrMode.Imp);
        table[0x79] = new("ADC", AddrMode.Abs_Y);
        table[0x7A] = new("PLY", AddrMode.Imp);
        table[0x7B] = new("TDC", AddrMode.Imp);
        table[0x7C] = new("JMP", AddrMode.Abs_IndX);
        table[0x7D] = new("ADC", AddrMode.Abs_X);
        table[0x7E] = new("ROR", AddrMode.Abs_X);
        table[0x7F] = new("ADC", AddrMode.Long_X);

        // 0x80-0x8F
        table[0x80] = new("BRA", AddrMode.Rel);
        table[0x81] = new("STA", AddrMode.Dp_IndX);
        table[0x82] = new("BRL", AddrMode.RelL);
        table[0x83] = new("STA", AddrMode.Sr);
        table[0x84] = new("STY", AddrMode.Dp);
        table[0x85] = new("STA", AddrMode.Dp);
        table[0x86] = new("STX", AddrMode.Dp);
        table[0x87] = new("STA", AddrMode.Dp_IndL);
        table[0x88] = new("DEY", AddrMode.Imp);
        table[0x89] = new("BIT", AddrMode.Imm_M);
        table[0x8A] = new("TXA", AddrMode.Imp);
        table[0x8B] = new("PHB", AddrMode.Imp);
        table[0x8C] = new("STY", AddrMode.Abs);
        table[0x8D] = new("STA", AddrMode.Abs);
        table[0x8E] = new("STX", AddrMode.Abs);
        table[0x8F] = new("STA", AddrMode.Long);

        // 0x90-0x9F
        table[0x90] = new("BCC", AddrMode.Rel);
        table[0x91] = new("STA", AddrMode.Dp_IndY);
        table[0x92] = new("STA", AddrMode.Dp_Ind);
        table[0x93] = new("STA", AddrMode.Sr_IndY);
        table[0x94] = new("STY", AddrMode.Dp_X);
        table[0x95] = new("STA", AddrMode.Dp_X);
        table[0x96] = new("STX", AddrMode.Dp_Y);
        table[0x97] = new("STA", AddrMode.Dp_IndLY);
        table[0x98] = new("TYA", AddrMode.Imp);
        table[0x99] = new("STA", AddrMode.Abs_Y);
        table[0x9A] = new("TXS", AddrMode.Imp);
        table[0x9B] = new("TXY", AddrMode.Imp);
        table[0x9C] = new("STZ", AddrMode.Abs);
        table[0x9D] = new("STA", AddrMode.Abs_X);
        table[0x9E] = new("STZ", AddrMode.Abs_X);
        table[0x9F] = new("STA", AddrMode.Long_X);

        // 0xA0-0xAF
        table[0xA0] = new("LDY", AddrMode.Imm_X);
        table[0xA1] = new("LDA", AddrMode.Dp_IndX);
        table[0xA2] = new("LDX", AddrMode.Imm_X);
        table[0xA3] = new("LDA", AddrMode.Sr);
        table[0xA4] = new("LDY", AddrMode.Dp);
        table[0xA5] = new("LDA", AddrMode.Dp);
        table[0xA6] = new("LDX", AddrMode.Dp);
        table[0xA7] = new("LDA", AddrMode.Dp_IndL);
        table[0xA8] = new("TAY", AddrMode.Imp);
        table[0xA9] = new("LDA", AddrMode.Imm_M);
        table[0xAA] = new("TAX", AddrMode.Imp);
        table[0xAB] = new("PLB", AddrMode.Imp);
        table[0xAC] = new("LDY", AddrMode.Abs);
        table[0xAD] = new("LDA", AddrMode.Abs);
        table[0xAE] = new("LDX", AddrMode.Abs);
        table[0xAF] = new("LDA", AddrMode.Long);

        // 0xB0-0xBF
        table[0xB0] = new("BCS", AddrMode.Rel);
        table[0xB1] = new("LDA", AddrMode.Dp_IndY);
        table[0xB2] = new("LDA", AddrMode.Dp_Ind);
        table[0xB3] = new("LDA", AddrMode.Sr_IndY);
        table[0xB4] = new("LDY", AddrMode.Dp_X);
        table[0xB5] = new("LDA", AddrMode.Dp_X);
        table[0xB6] = new("LDX", AddrMode.Dp_Y);
        table[0xB7] = new("LDA", AddrMode.Dp_IndLY);
        table[0xB8] = new("CLV", AddrMode.Imp);
        table[0xB9] = new("LDA", AddrMode.Abs_Y);
        table[0xBA] = new("TSX", AddrMode.Imp);
        table[0xBB] = new("TYX", AddrMode.Imp);
        table[0xBC] = new("LDY", AddrMode.Abs_X);
        table[0xBD] = new("LDA", AddrMode.Abs_X);
        table[0xBE] = new("LDX", AddrMode.Abs_Y);
        table[0xBF] = new("LDA", AddrMode.Long_X);

        // 0xC0-0xCF
        table[0xC0] = new("CPY", AddrMode.Imm_X);
        table[0xC1] = new("CMP", AddrMode.Dp_IndX);
        table[0xC2] = new("REP", AddrMode.Imm8);
        table[0xC3] = new("CMP", AddrMode.Sr);
        table[0xC4] = new("CPY", AddrMode.Dp);
        table[0xC5] = new("CMP", AddrMode.Dp);
        table[0xC6] = new("DEC", AddrMode.Dp);
        table[0xC7] = new("CMP", AddrMode.Dp_IndL);
        table[0xC8] = new("INY", AddrMode.Imp);
        table[0xC9] = new("CMP", AddrMode.Imm_M);
        table[0xCA] = new("DEX", AddrMode.Imp);
        table[0xCB] = new("WAI", AddrMode.Imp);
        table[0xCC] = new("CPY", AddrMode.Abs);
        table[0xCD] = new("CMP", AddrMode.Abs);
        table[0xCE] = new("DEC", AddrMode.Abs);
        table[0xCF] = new("CMP", AddrMode.Long);

        // 0xD0-0xDF
        table[0xD0] = new("BNE", AddrMode.Rel);
        table[0xD1] = new("CMP", AddrMode.Dp_IndY);
        table[0xD2] = new("CMP", AddrMode.Dp_Ind);
        table[0xD3] = new("CMP", AddrMode.Sr_IndY);
        table[0xD4] = new("PEI", AddrMode.Dp);
        table[0xD5] = new("CMP", AddrMode.Dp_X);
        table[0xD6] = new("DEC", AddrMode.Dp_X);
        table[0xD7] = new("CMP", AddrMode.Dp_IndLY);
        table[0xD8] = new("CLD", AddrMode.Imp);
        table[0xD9] = new("CMP", AddrMode.Abs_Y);
        table[0xDA] = new("PHX", AddrMode.Imp);
        table[0xDB] = new("STP", AddrMode.Imp);
        table[0xDC] = new("JML", AddrMode.Abs_IndL);
        table[0xDD] = new("CMP", AddrMode.Abs_X);
        table[0xDE] = new("DEC", AddrMode.Abs_X);
        table[0xDF] = new("CMP", AddrMode.Long_X);

        // 0xE0-0xEF
        table[0xE0] = new("CPX", AddrMode.Imm_X);
        table[0xE1] = new("SBC", AddrMode.Dp_IndX);
        table[0xE2] = new("SEP", AddrMode.Imm8);
        table[0xE3] = new("SBC", AddrMode.Sr);
        table[0xE4] = new("CPX", AddrMode.Dp);
        table[0xE5] = new("SBC", AddrMode.Dp);
        table[0xE6] = new("INC", AddrMode.Dp);
        table[0xE7] = new("SBC", AddrMode.Dp_IndL);
        table[0xE8] = new("INX", AddrMode.Imp);
        table[0xE9] = new("SBC", AddrMode.Imm_M);
        table[0xEA] = new("NOP", AddrMode.Imp);
        table[0xEB] = new("XBA", AddrMode.Imp);
        table[0xEC] = new("CPX", AddrMode.Abs);
        table[0xED] = new("SBC", AddrMode.Abs);
        table[0xEE] = new("INC", AddrMode.Abs);
        table[0xEF] = new("SBC", AddrMode.Long);

        // 0xF0-0xFF
        table[0xF0] = new("BEQ", AddrMode.Rel);
        table[0xF1] = new("SBC", AddrMode.Dp_IndY);
        table[0xF2] = new("SBC", AddrMode.Dp_Ind);
        table[0xF3] = new("SBC", AddrMode.Sr_IndY);
        table[0xF4] = new("PEA", AddrMode.Imm16);
        table[0xF5] = new("SBC", AddrMode.Dp_X);
        table[0xF6] = new("INC", AddrMode.Dp_X);
        table[0xF7] = new("SBC", AddrMode.Dp_IndLY);
        table[0xF8] = new("SED", AddrMode.Imp);
        table[0xF9] = new("SBC", AddrMode.Abs_Y);
        table[0xFA] = new("PLX", AddrMode.Imp);
        table[0xFB] = new("XCE", AddrMode.Imp);
        table[0xFC] = new("JSR", AddrMode.Abs_IndX);
        table[0xFD] = new("SBC", AddrMode.Abs_X);
        table[0xFE] = new("INC", AddrMode.Abs_X);
        table[0xFF] = new("SBC", AddrMode.Long_X);

        return table;
    }

    // decodes one instruction starting at fileOffset in the ROM. returns a data line if that's out of range
    public static DisassemblyLine DecodeInstruction(RomBuffer rom, int fileOffset, CpuState state)
    {
        if (fileOffset < 0 || fileOffset >= rom.Length)
            return DataLine(fileOffset, Array.Empty<byte>());

        byte opcode = rom.ReadByte(fileOffset);
        var info    = Table[opcode];
        int operandSize  = OperandSize(info.Mode, state);

        // the whole instruction has to fit in the ROM
        if (fileOffset + 1 + operandSize > rom.Length)
        {
            byte[] partial = rom.ReadBytes(fileOffset, Math.Min(rom.Length - fileOffset, 1 + operandSize));
            return DataLine(fileOffset, partial);
        }

        byte[] operand = operandSize > 0
            ? rom.ReadBytes(fileOffset + 1, operandSize)
            : Array.Empty<byte>();

        byte[] rawBytes = new byte[1 + operandSize];
        rawBytes[0] = opcode;
        Array.Copy(operand, 0, rawBytes, 1, operandSize);

        int snesAddress = Lufia1Constants.FileOffsetToSnesAddress(fileOffset);

        string operandText = FormatOperand(info.Mode, state, operand, snesAddress, operandSize);

        // what kind of instruction it is
        bool isCall      = opcode is 0x20 or 0x22 or 0xFC;
        bool isReturn    = opcode is 0x40 or 0x60 or 0x6B;
        bool isConditional      = opcode is 0x10 or 0x30 or 0x50 or 0x70 or 0x90 or 0xB0 or 0xD0 or 0xF0;
        bool isUnconditionalBranch  = opcode is 0x80 or 0x82;
        bool isJumpAbs   = opcode is 0x4C or 0x6C or 0x7C;
        bool isJumpLong  = opcode is 0x5C or 0xDC;
        bool isJump      = isConditional || isUnconditionalBranch || isJumpAbs || isJumpLong;

        // jump target, if we can tell it from the bytes alone
        int? jumpTarget = ResolveTarget(info.Mode, opcode, operand, snesAddress, operandSize);

        // keep track of REP/SEP flag changes
        CpuState? stateAfter = null;
        if (opcode == 0xC2 && operandSize >= 1) // REP
        {
            byte mask = operand[0];
            stateAfter = state with
            {
                M = state.M && (mask & 0x20) == 0,
                X = state.X && (mask & 0x10) == 0,
            };
        }
        else if (opcode == 0xE2 && operandSize >= 1) // SEP
        {
            byte mask = operand[0];
            stateAfter = state with
            {
                M = state.M || (mask & 0x20) != 0,
                X = state.X || (mask & 0x10) != 0,
            };
        }

        string? comment = stateAfter != null
            ? $"; M={FlagBit(stateAfter.M)} X={FlagBit(stateAfter.X)} after this"
            : null;

        return new DisassemblyLine
        {
            FileOffset    = fileOffset,
            SnesAddress   = snesAddress,
            RawBytes      = rawBytes,
            Mnemonic      = info.Mnemonic,
            Operand       = operandText,
            Comment       = comment,
            JumpTarget    = jumpTarget,
            IsJump        = isJump,
            IsCall        = isCall,
            IsReturn      = isReturn,
            IsConditional = isConditional,
            StateAfter    = stateAfter,
            BaseCycles    = Cycles[opcode],
        };
    }

    // operand size
    private static int OperandSize(AddrMode mode, CpuState state) => mode switch
    {
        AddrMode.Imp      => 0,
        AddrMode.Acc      => 0,
        AddrMode.Imm_M    => state.M ? 1 : 2,
        AddrMode.Imm_X    => state.X ? 1 : 2,
        AddrMode.Imm8     => 1,
        AddrMode.Imm16    => 2,
        AddrMode.Dp       => 1,
        AddrMode.Dp_X     => 1,
        AddrMode.Dp_Y     => 1,
        AddrMode.Dp_Ind   => 1,
        AddrMode.Dp_IndX  => 1,
        AddrMode.Dp_IndY  => 1,
        AddrMode.Dp_IndL  => 1,
        AddrMode.Dp_IndLY => 1,
        AddrMode.Rel      => 1,
        AddrMode.Sr       => 1,
        AddrMode.Sr_IndY  => 1,
        AddrMode.Abs      => 2,
        AddrMode.Abs_X    => 2,
        AddrMode.Abs_Y    => 2,
        AddrMode.Abs_Ind  => 2,
        AddrMode.Abs_IndX => 2,
        AddrMode.Abs_IndL => 2,
        AddrMode.RelL     => 2,
        AddrMode.Blk      => 2,
        AddrMode.Long     => 3,
        AddrMode.Long_X   => 3,
        _                  => 0,
    };

    // operand formatting
    private static string FormatOperand(AddrMode mode, CpuState state, byte[] operand, int snesAddress, int operandSize)
    {
        int Word16() => operand[0] | (operand[1] << 8);
        int Word24() => operand[0] | (operand[1] << 8) | (operand[2] << 16);

        return mode switch
        {
            AddrMode.Imp      => "",
            AddrMode.Acc      => "A",
            AddrMode.Imm_M    => operandSize == 1 ? $"#${operand[0]:X2}" : $"#${Word16():X4}",
            AddrMode.Imm_X    => operandSize == 1 ? $"#${operand[0]:X2}" : $"#${Word16():X4}",
            AddrMode.Imm8     => $"#${operand[0]:X2}",
            AddrMode.Imm16    => $"#${Word16():X4}",
            AddrMode.Dp       => $"${operand[0]:X2}",
            AddrMode.Dp_X     => $"${operand[0]:X2},X",
            AddrMode.Dp_Y     => $"${operand[0]:X2},Y",
            AddrMode.Dp_Ind   => $"(${operand[0]:X2})",
            AddrMode.Dp_IndX  => $"(${operand[0]:X2},X)",
            AddrMode.Dp_IndY  => $"(${operand[0]:X2}),Y",
            AddrMode.Dp_IndL  => $"[${operand[0]:X2}]",
            AddrMode.Dp_IndLY => $"[${operand[0]:X2}],Y",
            AddrMode.Abs      => $"${Word16():X4}",
            AddrMode.Abs_X    => $"${Word16():X4},X",
            AddrMode.Abs_Y    => $"${Word16():X4},Y",
            AddrMode.Abs_Ind  => $"(${Word16():X4})",
            AddrMode.Abs_IndX => $"(${Word16():X4},X)",
            AddrMode.Abs_IndL => $"[${Word16():X4}]",
            AddrMode.Long     => $"${Word24():X6}",
            AddrMode.Long_X   => $"${Word24():X6},X",
            AddrMode.Rel      => FormatRelative(snesAddress, (sbyte)operand[0]),
            AddrMode.RelL     => FormatRelativeLong(snesAddress, (short)Word16()),
            AddrMode.Sr       => $"${operand[0]:X2},S",
            AddrMode.Sr_IndY  => $"(${operand[0]:X2},S),Y",
            // MVP / MVN: byte 0 = dest bank, byte 1 = source bank, shown as "src,dst"
            AddrMode.Blk      => $"${operand[1]:X2},${operand[0]:X2}",
            _                  => "",
        };
    }

    private static string FormatRelative(int snesAddress, sbyte offset)
    {
        int target = (snesAddress + 2 + offset) & 0xFFFFFF;
        return $"${target:X6}";
    }

    private static string FormatRelativeLong(int snesAddress, short offset)
    {
        int target = (snesAddress + 3 + offset) & 0xFFFFFF;
        return $"${target:X6}";
    }

    // jump targets
    private static int? ResolveTarget(AddrMode mode, byte opcode, byte[] operand, int snesAddress, int operandSize)
    {
        // branches we can always work out (relative to PC)
        if (mode == AddrMode.Rel && operandSize >= 1)
            return (snesAddress + 2 + (sbyte)operand[0]) & 0xFFFFFF;

        if (mode == AddrMode.RelL && operandSize >= 2)
            return (snesAddress + 3 + (short)(operand[0] | (operand[1] << 8))) & 0xFFFFFF;

        // absolute JMP/JSR: the target is in the same bank as the instruction
        if (mode == AddrMode.Abs && operandSize >= 2 && opcode is 0x4C or 0x20)
        {
            int bank   = snesAddress & 0xFF0000;
            int offset = operand[0] | (operand[1] << 8);
            return bank | offset;
        }

        // JSL / JML long: full 24-bit address
        if (mode == AddrMode.Long && operandSize >= 3 && opcode is 0x22 or 0x5C)
            return operand[0] | (operand[1] << 8) | (operand[2] << 16);

        // JSR (abs,X), JMP (abs), JML [abs] are indirect, no way to know the target from the bytes

        return null;
    }

    private static DisassemblyLine DataLine(int fileOffset, byte[] rawBytes)
    {
        int snesAddress = fileOffset >= 0 && fileOffset < int.MaxValue
            ? Lufia1Constants.FileOffsetToSnesAddress(fileOffset)
            : 0;
        return new DisassemblyLine
        {
            FileOffset  = fileOffset,
            SnesAddress = snesAddress,
            RawBytes    = rawBytes,
            Mnemonic    = rawBytes.Length > 0 ? $".db ${rawBytes[0]:X2}" : ".db",
            Operand     = "",
            Comment     = "; [DATA?]",
            IsData      = true,
        };
    }

    private static string FlagBit(bool isSet) => isSet ? "1" : "0";
}
