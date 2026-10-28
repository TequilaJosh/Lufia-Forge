namespace LufiaForge.Core.Lufia2.Snes;

/// <summary>What the CPU reads and writes (24-bit addresses).</summary>
public interface ISnesBus
{
    byte Read(int addr);
    void Write(int addr, byte value);
}

/// <summary>
/// A 65816 interpreter, enough to run the game's own code (the battle animation engine) inside Lufia Forge: every
/// opcode, native and emulation mode, decimal arithmetic. No cycle timing: <see cref="Instructions"/> counts steps.
/// </summary>
public sealed class SnesCpu
{
    public int A, X, Y, S = 0x1FF, D, PC, DB, PB;
    public bool E = true;
    public bool FN, FV, FM = true, FX = true, FD, FI = true, FZ, FC;
    public long Instructions;
    public bool Stopped, Waiting;
    /// <summary>Set when BRK / COP / STP runs (the code went wrong): the address it ran at.</summary>
    public int Crashed = -1;
    private readonly ISnesBus _bus;

    public SnesCpu(ISnesBus bus) { _bus = bus; }

    public int P
    {
        get => (FN ? 0x80 : 0) | (FV ? 0x40 : 0) | (FM ? 0x20 : 0) | (FX ? 0x10 : 0) | (FD ? 8 : 0) | (FI ? 4 : 0) | (FZ ? 2 : 0) | (FC ? 1 : 0);
        set
        {
            FN = (value & 0x80) != 0; FV = (value & 0x40) != 0; FM = (value & 0x20) != 0; FX = (value & 0x10) != 0;
            FD = (value & 8) != 0; FI = (value & 4) != 0; FZ = (value & 2) != 0; FC = (value & 1) != 0;
            if (E) { FM = true; FX = true; }
            if (FX) { X &= 0xFF; Y &= 0xFF; }
        }
    }

    // ── memory ────────────────────────────────────────────────────────────
    private int R8(int a) => _bus.Read(a & 0xFFFFFF);
    private void W8(int a, int v) => _bus.Write(a & 0xFFFFFF, (byte)v);
    private int R16(int a) => R8(a) | R8(a + 1) << 8;
    private int R16Bank0(int a) => R8(a & 0xFFFF) | R8((a + 1) & 0xFFFF) << 8;
    private int Fetch8() { int v = R8(PB << 16 | PC); PC = (PC + 1) & 0xFFFF; return v; }
    private int Fetch16() { int lo = Fetch8(); return lo | Fetch8() << 8; }
    private int Fetch24() { int lo = Fetch16(); return lo | Fetch8() << 16; }
    private void Push8(int v)
    {
        W8(S, v);
        S = E ? 0x100 | ((S - 1) & 0xFF) : (S - 1) & 0xFFFF;
    }
    private int Pull8()
    {
        S = E ? 0x100 | ((S + 1) & 0xFF) : (S + 1) & 0xFFFF;
        return R8(S);
    }
    private void Push16(int v) { Push8(v >> 8); Push8(v); }
    private int Pull16() { int lo = Pull8(); return lo | Pull8() << 8; }

    // ── addressing (24-bit effective addresses) ─────────────────────────────
    private int Dp() => (D + Fetch8()) & 0xFFFF;
    private int DpX() => (D + Fetch8() + X) & 0xFFFF;
    private int DpY() => (D + Fetch8() + Y) & 0xFFFF;
    private int DpInd() => DB << 16 | R16Bank0(Dp());
    private int DpIndX() => DB << 16 | R16Bank0(DpX());
    private int DpIndY() => ((DB << 16 | R16Bank0(Dp())) + Y) & 0xFFFFFF;
    private int DpIndL() { int p = Dp(); return R16Bank0(p) | R8((p + 2) & 0xFFFF) << 16; }
    private int DpIndLY() => (DpIndL() + Y) & 0xFFFFFF;
    private int Abs() => DB << 16 | Fetch16();
    private int AbsX() => ((DB << 16 | Fetch16()) + X) & 0xFFFFFF;
    private int AbsY() => ((DB << 16 | Fetch16()) + Y) & 0xFFFFFF;
    private int Long() => Fetch24();
    private int LongX() => (Fetch24() + X) & 0xFFFFFF;
    private int Sr() => (S + Fetch8()) & 0xFFFF;
    private int SrIndY() => ((DB << 16 | R16Bank0(Sr())) + Y) & 0xFFFFFF;

    /// <summary>Effective address of the ALU group's addressing column (op &amp; 0x1F).</summary>
    private int AluEa(int col) => col switch
    {
        0x01 => DpIndX(), 0x03 => Sr(), 0x05 => Dp(), 0x07 => DpIndL(), 0x0D => Abs(), 0x0F => Long(),
        0x11 => DpIndY(), 0x12 => DpInd(), 0x13 => SrIndY(), 0x15 => DpX(), 0x17 => DpIndLY(), 0x19 => AbsY(),
        0x1D => AbsX(), 0x1F => LongX(), _ => throw new InvalidOperationException()
    };
    private static bool IsBank0Mode(int col) => col is 0x03 or 0x05 or 0x15;

    private int ReadM(int ea, bool bank0)
    {
        if (FM) return R8(ea);
        return bank0 ? R16Bank0(ea) : R16(ea);
    }
    private void WriteM(int ea, int v, bool bank0)
    {
        W8(ea, v);
        if (!FM) W8(bank0 ? (ea + 1) & 0xFFFF : ea + 1, v >> 8);
    }
    private int ReadX(int ea, bool bank0) => FX ? R8(ea) : bank0 ? R16Bank0(ea) : R16(ea);
    private void WriteX(int ea, int v, bool bank0)
    {
        W8(ea, v);
        if (!FX) W8(bank0 ? (ea + 1) & 0xFFFF : ea + 1, v >> 8);
    }

    private void SetNZ(int v, bool eight)
    {
        if (eight) { FZ = (v & 0xFF) == 0; FN = (v & 0x80) != 0; }
        else { FZ = (v & 0xFFFF) == 0; FN = (v & 0x8000) != 0; }
    }
    private int Acc { get => FM ? A & 0xFF : A; set => A = FM ? (A & 0xFF00) | (value & 0xFF) : value & 0xFFFF; }

    // ── ALU ───────────────────────────────────────────────────────────────
    private void Adc(int v)
    {
        if (FM)
        {
            int a = A & 0xFF, r;
            if (FD)
            {
                r = (a & 0x0F) + (v & 0x0F) + (FC ? 1 : 0);
                if (r > 9) r += 6;
                r = (a & 0xF0) + (v & 0xF0) + (r > 0x0F ? 0x10 : 0) + (r & 0x0F);
                FV = (~(a ^ v) & (a ^ r) & 0x80) != 0;
                if (r > 0x9F) r += 0x60;
                FC = r > 0xFF;
            }
            else
            {
                r = a + v + (FC ? 1 : 0);
                FV = (~(a ^ v) & (a ^ r) & 0x80) != 0;
                FC = r > 0xFF;
            }
            Acc = r; SetNZ(r, true);
        }
        else
        {
            int a = A, r;
            if (FD)
            {
                r = (a & 0x0F) + (v & 0x0F) + (FC ? 1 : 0);
                if (r > 0x09) r += 0x06;
                r = (a & 0xF0) + (v & 0xF0) + (r > 0x0F ? 0x10 : 0) + (r & 0x0F);
                if (r > 0x9F) r += 0x60;
                r = (a & 0xF00) + (v & 0xF00) + (r > 0xFF ? 0x100 : 0) + (r & 0xFF);
                if (r > 0x9FF) r += 0x600;
                r = (a & 0xF000) + (v & 0xF000) + (r > 0xFFF ? 0x1000 : 0) + (r & 0xFFF);
                FV = (~(a ^ v) & (a ^ r) & 0x8000) != 0;
                if (r > 0x9FFF) r += 0x6000;
                FC = r > 0xFFFF;
            }
            else
            {
                r = a + v + (FC ? 1 : 0);
                FV = (~(a ^ v) & (a ^ r) & 0x8000) != 0;
                FC = r > 0xFFFF;
            }
            A = r & 0xFFFF; SetNZ(r, false);
        }
    }
    private void Sbc(int v)
    {
        if (FM)
        {
            v = ~v & 0xFF;
            int a = A & 0xFF, r;
            if (FD)
            {
                r = (a & 0x0F) + (v & 0x0F) + (FC ? 1 : 0);
                if (r <= 0x0F) r -= 6;
                r = (a & 0xF0) + (v & 0xF0) + (r > 0x0F ? 0x10 : 0) + (r & 0x0F);
                FV = (~(a ^ v) & (a ^ r) & 0x80) != 0;
                if (r <= 0xFF) r -= 0x60;
                FC = r > 0xFF;
            }
            else
            {
                r = a + v + (FC ? 1 : 0);
                FV = (~(a ^ v) & (a ^ r) & 0x80) != 0;
                FC = r > 0xFF;
            }
            Acc = r; SetNZ(r, true);
        }
        else
        {
            v = ~v & 0xFFFF;
            int a = A, r;
            if (FD)
            {
                r = (a & 0x0F) + (v & 0x0F) + (FC ? 1 : 0);
                if (r <= 0x0F) r -= 0x06;
                r = (a & 0xF0) + (v & 0xF0) + (r > 0x0F ? 0x10 : 0) + (r & 0x0F);
                if (r <= 0xFF) r -= 0x60;
                r = (a & 0xF00) + (v & 0xF00) + (r > 0xFF ? 0x100 : 0) + (r & 0xFF);
                if (r <= 0xFFF) r -= 0x600;
                r = (a & 0xF000) + (v & 0xF000) + (r > 0xFFF ? 0x1000 : 0) + (r & 0xFFF);
                FV = (~(a ^ v) & (a ^ r) & 0x8000) != 0;
                if (r <= 0xFFFF) r -= 0x6000;
                FC = r > 0xFFFF;
            }
            else
            {
                r = a + v + (FC ? 1 : 0);
                FV = (~(a ^ v) & (a ^ r) & 0x8000) != 0;
                FC = r > 0xFFFF;
            }
            A = r & 0xFFFF; SetNZ(r, false);
        }
    }
    private void Compare(int reg, int v, bool eight)
    {
        int r = eight ? (reg & 0xFF) - (v & 0xFF) : (reg & 0xFFFF) - (v & 0xFFFF);
        FC = r >= 0; SetNZ(r, eight);
    }
    private void Alu(int group, int v)
    {
        switch (group)
        {
            case 0: Acc = Acc | v; SetNZ(Acc, FM); break;
            case 1: Acc = Acc & v; SetNZ(Acc, FM); break;
            case 2: Acc = Acc ^ v; SetNZ(Acc, FM); break;
            case 3: Adc(v); break;
            case 5: Acc = v; SetNZ(v, FM); break;
            case 6: Compare(A, v, FM); break;
            case 7: Sbc(v); break;
        }
    }

    private delegate int Shift(int v);
    private int Asl(int v) { bool e = FM; FC = (v & (e ? 0x80 : 0x8000)) != 0; v = (v << 1) & (e ? 0xFF : 0xFFFF); SetNZ(v, e); return v; }
    private int Lsr(int v) { bool e = FM; FC = (v & 1) != 0; v >>= 1; SetNZ(v, e); return v; }
    private int Rol(int v) { bool e = FM; bool c = FC; FC = (v & (e ? 0x80 : 0x8000)) != 0; v = ((v << 1) | (c ? 1 : 0)) & (e ? 0xFF : 0xFFFF); SetNZ(v, e); return v; }
    private int Ror(int v) { bool e = FM; bool c = FC; FC = (v & 1) != 0; v = (v >> 1) | (c ? (e ? 0x80 : 0x8000) : 0); SetNZ(v, e); return v; }
    private int Inc(int v) { v = (v + 1) & (FM ? 0xFF : 0xFFFF); SetNZ(v, FM); return v; }
    private int Dec(int v) { v = (v - 1) & (FM ? 0xFF : 0xFFFF); SetNZ(v, FM); return v; }
    private void Rmw(int ea, bool bank0, Shift f) => WriteM(ea, f(ReadM(ea, bank0)), bank0);

    private void Branch(bool cond)
    {
        int off = (sbyte)Fetch8();
        if (cond) PC = (PC + off) & 0xFFFF;
    }

    /// <summary>Takes an interrupt (NMI when <paramref name="nmi"/>), as the console does between two instructions.</summary>
    public void Interrupt(bool nmi)
    {
        Waiting = false;
        if (!E) Push8(PB);
        Push16(PC);
        Push8(E ? P & ~0x10 : P);
        FI = true; FD = false; PB = 0;
        PC = R16(E ? (nmi ? 0xFFFA : 0xFFFE) : (nmi ? 0xFFEA : 0xFFEE));
    }

    /// <summary>Runs one instruction.</summary>
    public void Step()
    {
        if (Stopped || Waiting) return;
        Instructions++;
        int pcAt = PB << 16 | PC;
        int op = Fetch8();
        int col = op & 0x1F, grp = op >> 5;
        // the regular ALU group (ORA AND EOR ADC STA LDA CMP SBC)
        if (((col & 1) == 1 && col != 0x0B && col != 0x1B) || col == 0x12)
        {
            if (col == 0x09)
            {
                if (grp == 4) { int v = FM ? Fetch8() : Fetch16(); FZ = (Acc & v) == 0; return; }   // BIT #
                Alu(grp, FM ? Fetch8() : Fetch16());
                return;
            }
            int ea = AluEa(col);
            bool b0 = IsBank0Mode(col);
            if (grp == 4) WriteM(ea, Acc, b0);
            else Alu(grp, ReadM(ea, b0));
            return;
        }
        int t;
        switch (op)
        {
            case 0x00: Crashed = pcAt; Stopped = true; return;                       // BRK
            case 0x02: Crashed = pcAt; Stopped = true; return;                       // COP
            case 0xDB: Crashed = pcAt; Stopped = true; return;                       // STP
            case 0xCB: Waiting = true; return;                                       // WAI
            case 0x42: Fetch8(); return;                                             // WDM
            case 0xEA: return;                                                       // NOP

            // flags
            case 0x18: FC = false; return;
            case 0x38: FC = true; return;
            case 0x58: FI = false; return;
            case 0x78: FI = true; return;
            case 0xB8: FV = false; return;
            case 0xD8: FD = false; return;
            case 0xF8: FD = true; return;
            case 0xC2: P = P & ~Fetch8(); return;                                    // REP
            case 0xE2: P = P | Fetch8(); return;                                     // SEP
            case 0xFB:                                                               // XCE
            {
                bool c = FC; FC = E; E = c;
                if (E) { FM = true; FX = true; X &= 0xFF; Y &= 0xFF; S = 0x100 | (S & 0xFF); }
                return;
            }

            // transfers
            case 0xAA: X = FX ? A & 0xFF : A; SetNZ(X, FX); return;                  // TAX
            case 0xA8: Y = FX ? A & 0xFF : A; SetNZ(Y, FX); return;                  // TAY
            case 0x8A: Acc = X; SetNZ(Acc, FM); return;                              // TXA
            case 0x98: Acc = Y; SetNZ(Acc, FM); return;                              // TYA
            case 0x9B: Y = X; SetNZ(Y, FX); return;                                  // TXY
            case 0xBB: X = Y; SetNZ(X, FX); return;                                  // TYX
            case 0xBA: X = FX ? S & 0xFF : S; SetNZ(X, FX); return;                  // TSX
            case 0x9A: S = E ? 0x100 | (X & 0xFF) : X; return;                       // TXS
            case 0x5B: D = A; SetNZ(D, false); return;                               // TCD
            case 0x7B: A = D; SetNZ(A, false); return;                               // TDC
            case 0x1B: S = E ? 0x100 | (A & 0xFF) : A; return;                       // TCS
            case 0x3B: A = S; SetNZ(A, false); return;                               // TSC
            case 0xEB: A = (A >> 8 | A << 8) & 0xFFFF; SetNZ(A & 0xFF, true); return; // XBA

            // stack
            case 0x48: if (FM) Push8(A); else Push16(A); return;                     // PHA
            case 0xDA: if (FX) Push8(X); else Push16(X); return;                     // PHX
            case 0x5A: if (FX) Push8(Y); else Push16(Y); return;                     // PHY
            case 0x68: if (FM) { Acc = Pull8(); } else A = Pull16(); SetNZ(Acc, FM); return;
            case 0xFA: X = FX ? Pull8() : Pull16(); SetNZ(X, FX); return;
            case 0x7A: Y = FX ? Pull8() : Pull16(); SetNZ(Y, FX); return;
            case 0x08: Push8(P); return;                                             // PHP
            case 0x28: P = Pull8(); return;                                          // PLP
            case 0x8B: Push8(DB); return;                                            // PHB
            case 0xAB: DB = Pull8(); SetNZ(DB, true); return;                        // PLB
            case 0x4B: Push8(PB); return;                                            // PHK
            case 0x0B: Push16(D); return;                                            // PHD
            case 0x2B: D = Pull16(); SetNZ(D, false); return;                        // PLD
            case 0xF4: Push16(Fetch16()); return;                                    // PEA
            case 0xD4: Push16(R16Bank0(Dp())); return;                               // PEI
            case 0x62: { int o = Fetch16(); Push16((PC + o) & 0xFFFF); return; }    // PER

            // jumps
            case 0x4C: PC = Fetch16(); return;
            case 0x5C: { int a = Fetch24(); PC = a & 0xFFFF; PB = a >> 16; return; }
            case 0x6C: PC = R16Bank0(Fetch16()); return;
            case 0x7C: { int o = Fetch16(); PC = R16(PB << 16 | ((o + X) & 0xFFFF)); return; }
            case 0xDC: { int o = Fetch16(); int a = R16Bank0(o) | R8((o + 2) & 0xFFFF) << 16; PC = a & 0xFFFF; PB = a >> 16; return; }
            case 0x20: { int a = Fetch16(); Push16((PC - 1) & 0xFFFF); PC = a; return; }
            case 0xFC: { int o = Fetch16(); Push16((PC - 1) & 0xFFFF); PC = R16(PB << 16 | ((o + X) & 0xFFFF)); return; }
            case 0x22: { int a = Fetch24(); Push8(PB); Push16((PC - 1) & 0xFFFF); PC = a & 0xFFFF; PB = a >> 16; return; }
            case 0x60: PC = (Pull16() + 1) & 0xFFFF; return;
            case 0x6B: PC = (Pull16() + 1) & 0xFFFF; PB = Pull8(); return;
            case 0x40: P = Pull8(); PC = Pull16(); if (!E) PB = Pull8(); return;    // RTI
            case 0x80: Branch(true); return;
            case 0x82: { int o = (short)Fetch16(); PC = (PC + o) & 0xFFFF; return; }
            case 0x10: Branch(!FN); return;
            case 0x30: Branch(FN); return;
            case 0x50: Branch(!FV); return;
            case 0x70: Branch(FV); return;
            case 0x90: Branch(!FC); return;
            case 0xB0: Branch(FC); return;
            case 0xD0: Branch(!FZ); return;
            case 0xF0: Branch(FZ); return;

            // loads / stores of X and Y, STZ
            case 0xA2: X = FX ? Fetch8() : Fetch16(); SetNZ(X, FX); return;
            case 0xA0: Y = FX ? Fetch8() : Fetch16(); SetNZ(Y, FX); return;
            case 0xA6: X = ReadX(Dp(), true); SetNZ(X, FX); return;
            case 0xB6: X = ReadX(DpY(), true); SetNZ(X, FX); return;
            case 0xAE: X = ReadX(Abs(), false); SetNZ(X, FX); return;
            case 0xBE: X = ReadX(AbsY(), false); SetNZ(X, FX); return;
            case 0xA4: Y = ReadX(Dp(), true); SetNZ(Y, FX); return;
            case 0xB4: Y = ReadX(DpX(), true); SetNZ(Y, FX); return;
            case 0xAC: Y = ReadX(Abs(), false); SetNZ(Y, FX); return;
            case 0xBC: Y = ReadX(AbsX(), false); SetNZ(Y, FX); return;
            case 0x86: WriteX(Dp(), X, true); return;
            case 0x96: WriteX(DpY(), X, true); return;
            case 0x8E: WriteX(Abs(), X, false); return;
            case 0x84: WriteX(Dp(), Y, true); return;
            case 0x94: WriteX(DpX(), Y, true); return;
            case 0x8C: WriteX(Abs(), Y, false); return;
            case 0x64: WriteM(Dp(), 0, true); return;
            case 0x74: WriteM(DpX(), 0, true); return;
            case 0x9C: WriteM(Abs(), 0, false); return;
            case 0x9E: WriteM(AbsX(), 0, false); return;

            // compares of X and Y
            case 0xE0: Compare(X, FX ? Fetch8() : Fetch16(), FX); return;
            case 0xE4: Compare(X, ReadX(Dp(), true), FX); return;
            case 0xEC: Compare(X, ReadX(Abs(), false), FX); return;
            case 0xC0: Compare(Y, FX ? Fetch8() : Fetch16(), FX); return;
            case 0xC4: Compare(Y, ReadX(Dp(), true), FX); return;
            case 0xCC: Compare(Y, ReadX(Abs(), false), FX); return;

            // increments
            case 0xE8: X = (X + 1) & (FX ? 0xFF : 0xFFFF); SetNZ(X, FX); return;
            case 0xC8: Y = (Y + 1) & (FX ? 0xFF : 0xFFFF); SetNZ(Y, FX); return;
            case 0xCA: X = (X - 1) & (FX ? 0xFF : 0xFFFF); SetNZ(X, FX); return;
            case 0x88: Y = (Y - 1) & (FX ? 0xFF : 0xFFFF); SetNZ(Y, FX); return;
            case 0x1A: Acc = Inc(Acc); return;
            case 0x3A: Acc = Dec(Acc); return;
            case 0xE6: Rmw(Dp(), true, Inc); return;
            case 0xF6: Rmw(DpX(), true, Inc); return;
            case 0xEE: Rmw(Abs(), false, Inc); return;
            case 0xFE: Rmw(AbsX(), false, Inc); return;
            case 0xC6: Rmw(Dp(), true, Dec); return;
            case 0xD6: Rmw(DpX(), true, Dec); return;
            case 0xCE: Rmw(Abs(), false, Dec); return;
            case 0xDE: Rmw(AbsX(), false, Dec); return;

            // shifts
            case 0x0A: Acc = Asl(Acc); return;
            case 0x4A: Acc = Lsr(Acc); return;
            case 0x2A: Acc = Rol(Acc); return;
            case 0x6A: Acc = Ror(Acc); return;
            case 0x06: Rmw(Dp(), true, Asl); return;
            case 0x16: Rmw(DpX(), true, Asl); return;
            case 0x0E: Rmw(Abs(), false, Asl); return;
            case 0x1E: Rmw(AbsX(), false, Asl); return;
            case 0x46: Rmw(Dp(), true, Lsr); return;
            case 0x56: Rmw(DpX(), true, Lsr); return;
            case 0x4E: Rmw(Abs(), false, Lsr); return;
            case 0x5E: Rmw(AbsX(), false, Lsr); return;
            case 0x26: Rmw(Dp(), true, Rol); return;
            case 0x36: Rmw(DpX(), true, Rol); return;
            case 0x2E: Rmw(Abs(), false, Rol); return;
            case 0x3E: Rmw(AbsX(), false, Rol); return;
            case 0x66: Rmw(Dp(), true, Ror); return;
            case 0x76: Rmw(DpX(), true, Ror); return;
            case 0x6E: Rmw(Abs(), false, Ror); return;
            case 0x7E: Rmw(AbsX(), false, Ror); return;

            // BIT, TSB, TRB
            case 0x24: BitMem(Dp(), true); return;
            case 0x34: BitMem(DpX(), true); return;
            case 0x2C: BitMem(Abs(), false); return;
            case 0x3C: BitMem(AbsX(), false); return;
            case 0x04: { int ea = Dp(); t = ReadM(ea, true); FZ = (t & Acc) == 0; WriteM(ea, t | Acc, true); return; }
            case 0x0C: { int ea = Abs(); t = ReadM(ea, false); FZ = (t & Acc) == 0; WriteM(ea, t | Acc, false); return; }
            case 0x14: { int ea = Dp(); t = ReadM(ea, true); FZ = (t & Acc) == 0; WriteM(ea, t & ~Acc, true); return; }
            case 0x1C: { int ea = Abs(); t = ReadM(ea, false); FZ = (t & Acc) == 0; WriteM(ea, t & ~Acc, false); return; }

            // block moves
            case 0x54:                                                               // MVN dest, src
            case 0x44:                                                               // MVP
            {
                int dst = Fetch8(), src = Fetch8();
                DB = dst;
                W8(dst << 16 | Y, R8(src << 16 | X));
                int step = op == 0x54 ? 1 : -1;
                int m = FX ? 0xFF : 0xFFFF;
                X = (X + step) & m; Y = (Y + step) & m;
                A = (A - 1) & 0xFFFF;
                if (A != 0xFFFF) PC = (PC - 3) & 0xFFFF;
                return;
            }
        }
        Crashed = pcAt; Stopped = true;
    }

    private void BitMem(int ea, bool b0)
    {
        int v = ReadM(ea, b0);
        FZ = (v & Acc) == 0;
        if (FM) { FN = (v & 0x80) != 0; FV = (v & 0x40) != 0; }
        else { FN = (v & 0x8000) != 0; FV = (v & 0x4000) != 0; }
    }
}
