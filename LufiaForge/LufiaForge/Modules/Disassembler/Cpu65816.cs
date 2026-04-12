using LufiaForge.Core;

namespace LufiaForge.Modules.Disassembler;

/// <summary>
/// WDC 65816 instruction decoder.
/// Covers all 256 opcodes with correct mnemonic, addressing mode, and operand-size rules.
/// </summary>
public static class Cpu65816
{
    // -------------------------------------------------------------------------
    // Addressing mode enum
    // -------------------------------------------------------------------------

    private enum AddrMode
    {
        Imp,       // Implied — no operand
        Acc,       // Accumulator — "A"
        Imm_M,     // Immediate — 1 byte (M=1) or 2 bytes (M=0)
        Imm_X,     // Immediate — 1 byte (X=1) or 2 bytes (X=0)
        Imm8,      // Immediate — always 1 byte (REP, SEP, BRK, COP, WDM)
        Imm16,     // Immediate — always 2 bytes (PEA)
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

    // -------------------------------------------------------------------------
    // Opcode table — 256 entries
    // -------------------------------------------------------------------------

    private record struct OpcodeInfo(string Mnemonic, AddrMode Mode);

    private static readonly OpcodeInfo[] Table = BuildTable();

    private static OpcodeInfo[] BuildTable()
    {
        var t = new OpcodeInfo[256];

        // 0x00–0x0F
        t[0x00] = new("BRK", AddrMode.Imm8);
        t[0x01] = new("ORA", AddrMode.Dp_IndX);
        t[0x02] = new("COP", AddrMode.Imm8);
        t[0x03] = new("ORA", AddrMode.Sr);
        t[0x04] = new("TSB", AddrMode.Dp);
        t[0x05] = new("ORA", AddrMode.Dp);
        t[0x06] = new("ASL", AddrMode.Dp);
        t[0x07] = new("ORA", AddrMode.Dp_IndL);
        t[0x08] = new("PHP", AddrMode.Imp);
        t[0x09] = new("ORA", AddrMode.Imm_M);
        t[0x0A] = new("ASL", AddrMode.Acc);
        t[0x0B] = new("PHD", AddrMode.Imp);
        t[0x0C] = new("TSB", AddrMode.Abs);
        t[0x0D] = new("ORA", AddrMode.Abs);
        t[0x0E] = new("ASL", AddrMode.Abs);
        t[0x0F] = new("ORA", AddrMode.Long);

        // 0x10–0x1F
        t[0x10] = new("BPL", AddrMode.Rel);
        t[0x11] = new("ORA", AddrMode.Dp_IndY);
        t[0x12] = new("ORA", AddrMode.Dp_Ind);
        t[0x13] = new("ORA", AddrMode.Sr_IndY);
        t[0x14] = new("TRB", AddrMode.Dp);
        t[0x15] = new("ORA", AddrMode.Dp_X);
        t[0x16] = new("ASL", AddrMode.Dp_X);
        t[0x17] = new("ORA", AddrMode.Dp_IndLY);
        t[0x18] = new("CLC", AddrMode.Imp);
        t[0x19] = new("ORA", AddrMode.Abs_Y);
        t[0x1A] = new("INC", AddrMode.Acc);
        t[0x1B] = new("TCS", AddrMode.Imp);
        t[0x1C] = new("TRB", AddrMode.Abs);
        t[0x1D] = new("ORA", AddrMode.Abs_X);
        t[0x1E] = new("ASL", AddrMode.Abs_X);
        t[0x1F] = new("ORA", AddrMode.Long_X);

        // 0x20–0x2F
        t[0x20] = new("JSR", AddrMode.Abs);
        t[0x21] = new("AND", AddrMode.Dp_IndX);
        t[0x22] = new("JSL", AddrMode.Long);
        t[0x23] = new("AND", AddrMode.Sr);
        t[0x24] = new("BIT", AddrMode.Dp);
        t[0x25] = new("AND", AddrMode.Dp);
        t[0x26] = new("ROL", AddrMode.Dp);
        t[0x27] = new("AND", AddrMode.Dp_IndL);
        t[0x28] = new("PLP", AddrMode.Imp);
        t[0x29] = new("AND", AddrMode.Imm_M);
        t[0x2A] = new("ROL", AddrMode.Acc);
        t[0x2B] = new("PLD", AddrMode.Imp);
        t[0x2C] = new("BIT", AddrMode.Abs);
        t[0x2D] = new("AND", AddrMode.Abs);
        t[0x2E] = new("ROL", AddrMode.Abs);
        t[0x2F] = new("AND", AddrMode.Long);

        // 0x30–0x3F
        t[0x30] = new("BMI", AddrMode.Rel);
        t[0x31] = new("AND", AddrMode.Dp_IndY);
        t[0x32] = new("AND", AddrMode.Dp_Ind);
        t[0x33] = new("AND", AddrMode.Sr_IndY);
        t[0x34] = new("BIT", AddrMode.Dp_X);
        t[0x35] = new("AND", AddrMode.Dp_X);
        t[0x36] = new("ROL", AddrMode.Dp_X);
        t[0x37] = new("AND", AddrMode.Dp_IndLY);
        t[0x38] = new("SEC", AddrMode.Imp);
        t[0x39] = new("AND", AddrMode.Abs_Y);
        t[0x3A] = new("DEC", AddrMode.Acc);
        t[0x3B] = new("TSC", AddrMode.Imp);
        t[0x3C] = new("BIT", AddrMode.Abs_X);
        t[0x3D] = new("AND", AddrMode.Abs_X);
        t[0x3E] = new("ROL", AddrMode.Abs_X);
        t[0x3F] = new("AND", AddrMode.Long_X);

        // 0x40–0x4F
        t[0x40] = new("RTI", AddrMode.Imp);
        t[0x41] = new("EOR", AddrMode.Dp_IndX);
        t[0x42] = new("WDM", AddrMode.Imm8);
        t[0x43] = new("EOR", AddrMode.Sr);
        t[0x44] = new("MVP", AddrMode.Blk);
        t[0x45] = new("EOR", AddrMode.Dp);
        t[0x46] = new("LSR", AddrMode.Dp);
        t[0x47] = new("EOR", AddrMode.Dp_IndL);
        t[0x48] = new("PHA", AddrMode.Imp);
        t[0x49] = new("EOR", AddrMode.Imm_M);
        t[0x4A] = new("LSR", AddrMode.Acc);
        t[0x4B] = new("PHK", AddrMode.Imp);
        t[0x4C] = new("JMP", AddrMode.Abs);
        t[0x4D] = new("EOR", AddrMode.Abs);
        t[0x4E] = new("LSR", AddrMode.Abs);
        t[0x4F] = new("EOR", AddrMode.Long);

        // 0x50–0x5F
        t[0x50] = new("BVC", AddrMode.Rel);
        t[0x51] = new("EOR", AddrMode.Dp_IndY);
        t[0x52] = new("EOR", AddrMode.Dp_Ind);
        t[0x53] = new("EOR", AddrMode.Sr_IndY);
        t[0x54] = new("MVN", AddrMode.Blk);
        t[0x55] = new("EOR", AddrMode.Dp_X);
        t[0x56] = new("LSR", AddrMode.Dp_X);
        t[0x57] = new("EOR", AddrMode.Dp_IndLY);
        t[0x58] = new("CLI", AddrMode.Imp);
        t[0x59] = new("EOR", AddrMode.Abs_Y);
        t[0x5A] = new("PHY", AddrMode.Imp);
        t[0x5B] = new("TCD", AddrMode.Imp);
        t[0x5C] = new("JML", AddrMode.Long);
        t[0x5D] = new("EOR", AddrMode.Abs_X);
        t[0x5E] = new("LSR", AddrMode.Abs_X);
        t[0x5F] = new("EOR", AddrMode.Long_X);

        // 0x60–0x6F
        t[0x60] = new("RTS", AddrMode.Imp);
        t[0x61] = new("ADC", AddrMode.Dp_IndX);
        t[0x62] = new("PER", AddrMode.RelL);
        t[0x63] = new("ADC", AddrMode.Sr);
        t[0x64] = new("STZ", AddrMode.Dp);
        t[0x65] = new("ADC", AddrMode.Dp);
        t[0x66] = new("ROR", AddrMode.Dp);
        t[0x67] = new("ADC", AddrMode.Dp_IndL);
        t[0x68] = new("PLA", AddrMode.Imp);
        t[0x69] = new("ADC", AddrMode.Imm_M);
        t[0x6A] = new("ROR", AddrMode.Acc);
        t[0x6B] = new("RTL", AddrMode.Imp);
        t[0x6C] = new("JMP", AddrMode.Abs_Ind);
        t[0x6D] = new("ADC", AddrMode.Abs);
        t[0x6E] = new("ROR", AddrMode.Abs);
        t[0x6F] = new("ADC", AddrMode.Long);

        // 0x70–0x7F
        t[0x70] = new("BVS", AddrMode.Rel);
        t[0x71] = new("ADC", AddrMode.Dp_IndY);
        t[0x72] = new("ADC", AddrMode.Dp_Ind);
        t[0x73] = new("ADC", AddrMode.Sr_IndY);
        t[0x74] = new("STZ", AddrMode.Dp_X);
        t[0x75] = new("ADC", AddrMode.Dp_X);
        t[0x76] = new("ROR", AddrMode.Dp_X);
        t[0x77] = new("ADC", AddrMode.Dp_IndLY);
        t[0x78] = new("SEI", AddrMode.Imp);
        t[0x79] = new("ADC", AddrMode.Abs_Y);
        t[0x7A] = new("PLY", AddrMode.Imp);
        t[0x7B] = new("TDC", AddrMode.Imp);
        t[0x7C] = new("JMP", AddrMode.Abs_IndX);
        t[0x7D] = new("ADC", AddrMode.Abs_X);
        t[0x7E] = new("ROR", AddrMode.Abs_X);
        t[0x7F] = new("ADC", AddrMode.Long_X);

        // 0x80–0x8F
        t[0x80] = new("BRA", AddrMode.Rel);
        t[0x81] = new("STA", AddrMode.Dp_IndX);
        t[0x82] = new("BRL", AddrMode.RelL);
        t[0x83] = new("STA", AddrMode.Sr);
        t[0x84] = new("STY", AddrMode.Dp);
        t[0x85] = new("STA", AddrMode.Dp);
        t[0x86] = new("STX", AddrMode.Dp);
        t[0x87] = new("STA", AddrMode.Dp_IndL);
        t[0x88] = new("DEY", AddrMode.Imp);
        t[0x89] = new("BIT", AddrMode.Imm_M);
        t[0x8A] = new("TXA", AddrMode.Imp);
        t[0x8B] = new("PHB", AddrMode.Imp);
        t[0x8C] = new("STY", AddrMode.Abs);
        t[0x8D] = new("STA", AddrMode.Abs);
        t[0x8E] = new("STX", AddrMode.Abs);
        t[0x8F] = new("STA", AddrMode.Long);

        // 0x90–0x9F
        t[0x90] = new("BCC", AddrMode.Rel);
        t[0x91] = new("STA", AddrMode.Dp_IndY);
        t[0x92] = new("STA", AddrMode.Dp_Ind);
        t[0x93] = new("STA", AddrMode.Sr_IndY);
        t[0x94] = new("STY", AddrMode.Dp_X);
        t[0x95] = new("STA", AddrMode.Dp_X);
        t[0x96] = new("STX", AddrMode.Dp_Y);
        t[0x97] = new("STA", AddrMode.Dp_IndLY);
        t[0x98] = new("TYA", AddrMode.Imp);
        t[0x99] = new("STA", AddrMode.Abs_Y);
        t[0x9A] = new("TXS", AddrMode.Imp);
        t[0x9B] = new("TXY", AddrMode.Imp);
        t[0x9C] = new("STZ", AddrMode.Abs);
        t[0x9D] = new("STA", AddrMode.Abs_X);
        t[0x9E] = new("STZ", AddrMode.Abs_X);
        t[0x9F] = new("STA", AddrMode.Long_X);

        // 0xA0–0xAF
        t[0xA0] = new("LDY", AddrMode.Imm_X);
        t[0xA1] = new("LDA", AddrMode.Dp_IndX);
        t[0xA2] = new("LDX", AddrMode.Imm_X);
        t[0xA3] = new("LDA", AddrMode.Sr);
        t[0xA4] = new("LDY", AddrMode.Dp);
        t[0xA5] = new("LDA", AddrMode.Dp);
        t[0xA6] = new("LDX", AddrMode.Dp);
        t[0xA7] = new("LDA", AddrMode.Dp_IndL);
        t[0xA8] = new("TAY", AddrMode.Imp);
        t[0xA9] = new("LDA", AddrMode.Imm_M);
        t[0xAA] = new("TAX", AddrMode.Imp);
        t[0xAB] = new("PLB", AddrMode.Imp);
        t[0xAC] = new("LDY", AddrMode.Abs);
        t[0xAD] = new("LDA", AddrMode.Abs);
        t[0xAE] = new("LDX", AddrMode.Abs);
        t[0xAF] = new("LDA", AddrMode.Long);

        // 0xB0–0xBF
        t[0xB0] = new("BCS", AddrMode.Rel);
        t[0xB1] = new("LDA", AddrMode.Dp_IndY);
        t[0xB2] = new("LDA", AddrMode.Dp_Ind);
        t[0xB3] = new("LDA", AddrMode.Sr_IndY);
        t[0xB4] = new("LDY", AddrMode.Dp_X);
        t[0xB5] = new("LDA", AddrMode.Dp_X);
        t[0xB6] = new("LDX", AddrMode.Dp_Y);
        t[0xB7] = new("LDA", AddrMode.Dp_IndLY);
        t[0xB8] = new("CLV", AddrMode.Imp);
        t[0xB9] = new("LDA", AddrMode.Abs_Y);
        t[0xBA] = new("TSX", AddrMode.Imp);
        t[0xBB] = new("TYX", AddrMode.Imp);
        t[0xBC] = new("LDY", AddrMode.Abs_X);
        t[0xBD] = new("LDA", AddrMode.Abs_X);
        t[0xBE] = new("LDX", AddrMode.Abs_Y);
        t[0xBF] = new("LDA", AddrMode.Long_X);

        // 0xC0–0xCF
        t[0xC0] = new("CPY", AddrMode.Imm_X);
        t[0xC1] = new("CMP", AddrMode.Dp_IndX);
        t[0xC2] = new("REP", AddrMode.Imm8);
        t[0xC3] = new("CMP", AddrMode.Sr);
        t[0xC4] = new("CPY", AddrMode.Dp);
        t[0xC5] = new("CMP", AddrMode.Dp);
        t[0xC6] = new("DEC", AddrMode.Dp);
        t[0xC7] = new("CMP", AddrMode.Dp_IndL);
        t[0xC8] = new("INY", AddrMode.Imp);
        t[0xC9] = new("CMP", AddrMode.Imm_M);
        t[0xCA] = new("DEX", AddrMode.Imp);
        t[0xCB] = new("WAI", AddrMode.Imp);
        t[0xCC] = new("CPY", AddrMode.Abs);
        t[0xCD] = new("CMP", AddrMode.Abs);
        t[0xCE] = new("DEC", AddrMode.Abs);
        t[0xCF] = new("CMP", AddrMode.Long);

        // 0xD0–0xDF
        t[0xD0] = new("BNE", AddrMode.Rel);
        t[0xD1] = new("CMP", AddrMode.Dp_IndY);
        t[0xD2] = new("CMP", AddrMode.Dp_Ind);
        t[0xD3] = new("CMP", AddrMode.Sr_IndY);
        t[0xD4] = new("PEI", AddrMode.Dp);
        t[0xD5] = new("CMP", AddrMode.Dp_X);
        t[0xD6] = new("DEC", AddrMode.Dp_X);
        t[0xD7] = new("CMP", AddrMode.Dp_IndLY);
        t[0xD8] = new("CLD", AddrMode.Imp);
        t[0xD9] = new("CMP", AddrMode.Abs_Y);
        t[0xDA] = new("PHX", AddrMode.Imp);
        t[0xDB] = new("STP", AddrMode.Imp);
        t[0xDC] = new("JML", AddrMode.Abs_IndL);
        t[0xDD] = new("CMP", AddrMode.Abs_X);
        t[0xDE] = new("DEC", AddrMode.Abs_X);
        t[0xDF] = new("CMP", AddrMode.Long_X);

        // 0xE0–0xEF
        t[0xE0] = new("CPX", AddrMode.Imm_X);
        t[0xE1] = new("SBC", AddrMode.Dp_IndX);
        t[0xE2] = new("SEP", AddrMode.Imm8);
        t[0xE3] = new("SBC", AddrMode.Sr);
        t[0xE4] = new("CPX", AddrMode.Dp);
        t[0xE5] = new("SBC", AddrMode.Dp);
        t[0xE6] = new("INC", AddrMode.Dp);
        t[0xE7] = new("SBC", AddrMode.Dp_IndL);
        t[0xE8] = new("INX", AddrMode.Imp);
        t[0xE9] = new("SBC", AddrMode.Imm_M);
        t[0xEA] = new("NOP", AddrMode.Imp);
        t[0xEB] = new("XBA", AddrMode.Imp);
        t[0xEC] = new("CPX", AddrMode.Abs);
        t[0xED] = new("SBC", AddrMode.Abs);
        t[0xEE] = new("INC", AddrMode.Abs);
        t[0xEF] = new("SBC", AddrMode.Long);

        // 0xF0–0xFF
        t[0xF0] = new("BEQ", AddrMode.Rel);
        t[0xF1] = new("SBC", AddrMode.Dp_IndY);
        t[0xF2] = new("SBC", AddrMode.Dp_Ind);
        t[0xF3] = new("SBC", AddrMode.Sr_IndY);
        t[0xF4] = new("PEA", AddrMode.Imm16);
        t[0xF5] = new("SBC", AddrMode.Dp_X);
        t[0xF6] = new("INC", AddrMode.Dp_X);
        t[0xF7] = new("SBC", AddrMode.Dp_IndLY);
        t[0xF8] = new("SED", AddrMode.Imp);
        t[0xF9] = new("SBC", AddrMode.Abs_Y);
        t[0xFA] = new("PLX", AddrMode.Imp);
        t[0xFB] = new("XCE", AddrMode.Imp);
        t[0xFC] = new("JSR", AddrMode.Abs_IndX);
        t[0xFD] = new("SBC", AddrMode.Abs_X);
        t[0xFE] = new("INC", AddrMode.Abs_X);
        t[0xFF] = new("SBC", AddrMode.Long_X);

        return t;
    }

    // -------------------------------------------------------------------------
    // Public decode entry point
    // -------------------------------------------------------------------------

    /// <summary>
    /// Decode one instruction starting at <paramref name="fileOffset"/> in the ROM.
    /// Returns a data-marker line on out-of-range access.
    /// </summary>
    public static DisassemblyLine DecodeInstruction(RomBuffer rom, int fileOffset, CpuState state)
    {
        if (fileOffset < 0 || fileOffset >= rom.Length)
            return DataLine(fileOffset, Array.Empty<byte>());

        byte opcode = rom.ReadByte(fileOffset);
        var info    = Table[opcode];
        int opSize  = OperandSize(info.Mode, state);

        // Guard: make sure the full instruction fits in the ROM
        if (fileOffset + 1 + opSize > rom.Length)
        {
            byte[] partial = rom.ReadBytes(fileOffset, Math.Min(rom.Length - fileOffset, 1 + opSize));
            return DataLine(fileOffset, partial);
        }

        byte[] operand = opSize > 0
            ? rom.ReadBytes(fileOffset + 1, opSize)
            : Array.Empty<byte>();

        byte[] rawBytes = new byte[1 + opSize];
        rawBytes[0] = opcode;
        Array.Copy(operand, 0, rawBytes, 1, opSize);

        int snesAddr = Lufia1Constants.FileOffsetToSnesAddress(fileOffset);

        string operandStr = FormatOperand(info.Mode, state, operand, snesAddr, opSize);

        // Classify the instruction
        bool isCall      = opcode is 0x20 or 0x22 or 0xFC;
        bool isReturn    = opcode is 0x40 or 0x60 or 0x6B;
        bool isCond      = opcode is 0x10 or 0x30 or 0x50 or 0x70 or 0x90 or 0xB0 or 0xD0 or 0xF0;
        bool isUncondBr  = opcode is 0x80 or 0x82;
        bool isJumpAbs   = opcode is 0x4C or 0x6C or 0x7C;
        bool isJumpLong  = opcode is 0x5C or 0xDC;
        bool isJump      = isCond || isUncondBr || isJumpAbs || isJumpLong;

        // Resolve static jump target
        int? jumpTarget = ResolveTarget(info.Mode, opcode, operand, snesAddr, opSize);

        // Track REP/SEP flag changes
        CpuState? stateAfter = null;
        if (opcode == 0xC2 && opSize >= 1) // REP
        {
            byte mask = operand[0];
            stateAfter = state with
            {
                M = state.M && (mask & 0x20) == 0,
                X = state.X && (mask & 0x10) == 0,
            };
        }
        else if (opcode == 0xE2 && opSize >= 1) // SEP
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
            SnesAddress   = snesAddr,
            RawBytes      = rawBytes,
            Mnemonic      = info.Mnemonic,
            Operand       = operandStr,
            Comment       = comment,
            JumpTarget    = jumpTarget,
            IsJump        = isJump,
            IsCall        = isCall,
            IsReturn      = isReturn,
            IsConditional = isCond,
            StateAfter    = stateAfter,
        };
    }

    // -------------------------------------------------------------------------
    // Operand size
    // -------------------------------------------------------------------------

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

    // -------------------------------------------------------------------------
    // Operand formatting
    // -------------------------------------------------------------------------

    private static string FormatOperand(AddrMode mode, CpuState state, byte[] op, int snesAddr, int opSize)
    {
        int w16() => op[0] | (op[1] << 8);
        int w24() => op[0] | (op[1] << 8) | (op[2] << 16);

        return mode switch
        {
            AddrMode.Imp      => "",
            AddrMode.Acc      => "A",
            AddrMode.Imm_M    => opSize == 1 ? $"#${op[0]:X2}" : $"#${w16():X4}",
            AddrMode.Imm_X    => opSize == 1 ? $"#${op[0]:X2}" : $"#${w16():X4}",
            AddrMode.Imm8     => $"#${op[0]:X2}",
            AddrMode.Imm16    => $"#${w16():X4}",
            AddrMode.Dp       => $"${op[0]:X2}",
            AddrMode.Dp_X     => $"${op[0]:X2},X",
            AddrMode.Dp_Y     => $"${op[0]:X2},Y",
            AddrMode.Dp_Ind   => $"(${op[0]:X2})",
            AddrMode.Dp_IndX  => $"(${op[0]:X2},X)",
            AddrMode.Dp_IndY  => $"(${op[0]:X2}),Y",
            AddrMode.Dp_IndL  => $"[${op[0]:X2}]",
            AddrMode.Dp_IndLY => $"[${op[0]:X2}],Y",
            AddrMode.Abs      => $"${w16():X4}",
            AddrMode.Abs_X    => $"${w16():X4},X",
            AddrMode.Abs_Y    => $"${w16():X4},Y",
            AddrMode.Abs_Ind  => $"(${w16():X4})",
            AddrMode.Abs_IndX => $"(${w16():X4},X)",
            AddrMode.Abs_IndL => $"[${w16():X4}]",
            AddrMode.Long     => $"${w24():X6}",
            AddrMode.Long_X   => $"${w24():X6},X",
            AddrMode.Rel      => FormatRelative(snesAddr, (sbyte)op[0]),
            AddrMode.RelL     => FormatRelativeLong(snesAddr, (short)w16()),
            AddrMode.Sr       => $"${op[0]:X2},S",
            AddrMode.Sr_IndY  => $"(${op[0]:X2},S),Y",
            // Blk: MVP / MVN — byte[0]=dst bank, byte[1]=src bank; displayed as "src,dst"
            AddrMode.Blk      => $"${op[1]:X2},${op[0]:X2}",
            _                  => "",
        };
    }

    private static string FormatRelative(int snesAddr, sbyte offset)
    {
        int target = (snesAddr + 2 + offset) & 0xFFFFFF;
        return $"${target:X6}";
    }

    private static string FormatRelativeLong(int snesAddr, short offset)
    {
        int target = (snesAddr + 3 + offset) & 0xFFFFFF;
        return $"${target:X6}";
    }

    // -------------------------------------------------------------------------
    // Jump target resolution
    // -------------------------------------------------------------------------

    private static int? ResolveTarget(AddrMode mode, byte opcode, byte[] op, int snesAddr, int opSize)
    {
        // Branches — always resolvable (relative to PC)
        if (mode == AddrMode.Rel && opSize >= 1)
            return (snesAddr + 2 + (sbyte)op[0]) & 0xFFFFFF;

        if (mode == AddrMode.RelL && opSize >= 2)
            return (snesAddr + 3 + (short)(op[0] | (op[1] << 8))) & 0xFFFFFF;

        // Absolute JMP/JSR — target is in same bank as the instruction
        if (mode == AddrMode.Abs && opSize >= 2 && opcode is 0x4C or 0x20)
        {
            int bank   = snesAddr & 0xFF0000;
            int offset = op[0] | (op[1] << 8);
            return bank | offset;
        }

        // JSL / JML long — full 24-bit address
        if (mode == AddrMode.Long && opSize >= 3 && opcode is 0x22 or 0x5C)
            return op[0] | (op[1] << 8) | (op[2] << 16);

        // JSR (abs,X) — indirect, cannot resolve statically
        // JMP (abs), JML [abs] — indirect, cannot resolve statically

        return null;
    }

    // -------------------------------------------------------------------------
    // Helpers
    // -------------------------------------------------------------------------

    private static DisassemblyLine DataLine(int fileOffset, byte[] raw)
    {
        int snesAddr = fileOffset >= 0 && fileOffset < int.MaxValue
            ? Lufia1Constants.FileOffsetToSnesAddress(fileOffset)
            : 0;
        return new DisassemblyLine
        {
            FileOffset  = fileOffset,
            SnesAddress = snesAddr,
            RawBytes    = raw,
            Mnemonic    = raw.Length > 0 ? $".db ${raw[0]:X2}" : ".db",
            Operand     = "",
            Comment     = "; [DATA?]",
            IsData      = true,
        };
    }

    private static string FlagBit(bool v) => v ? "1" : "0";
}
