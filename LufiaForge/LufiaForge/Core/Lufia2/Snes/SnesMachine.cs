namespace LufiaForge.Core.Lufia2.Snes;

/// <summary>
/// The console around <see cref="SnesCpu"/>, enough to run the game's battle animation code and see what it draws:
/// the ROM (LoROM, and the upper 4 MB of an 8 MB ExLoROM), work RAM, cartridge RAM, the picture unit's memories
/// (VRAM, colours, sprites) and registers, DMA and HDMA, the multiply / divide unit. The sound chip only answers back
/// what it was sent (the game's sound commands wait for that echo), so animations play silent.
/// </summary>
public sealed class SnesMachine : ISnesBus
{
    public readonly byte[] Rom;
    public readonly byte[] Wram = new byte[0x20000];
    public readonly byte[] Sram = new byte[0x8000];
    public readonly byte[] Vram = new byte[0x10000];
    public readonly byte[] Cgram = new byte[0x200];
    public readonly byte[] Oam = new byte[0x220];
    public readonly byte[] Regs = new byte[0x100];   // last value written to $21xx
    public readonly SnesCpu Cpu;
    private readonly bool _exLo;

    // PPU state
    public int VramAddr, OamAddr, CgAddr;
    private int _vramLatch, _cgLatch = -1, _oamLatch, _bgLatch, _m7Latch;
    private bool _cgHigh;
    public readonly int[] HScroll = new int[4], VScroll = new int[4];
    private int _wramPort;
    private readonly byte[] _apu = new byte[4];
    // ports 2 and 3: a command (any byte) is answered CD EF, then 00 00 is answered 00 00
    private readonly bool[] _apuAck = { true, true };
    // CPU registers $42xx / DMA $43xx
    public readonly byte[] Io = new byte[0x200];
    private int _mulResult, _divQuot, _divRem;
    private bool _vblankToggle;
    public bool NmiFlag;
    /// <summary>For tests: when set, which work RAM bytes were written, and the ones read before any write.</summary>
    public bool[]? Written;
    public SortedDictionary<int, int>? ReadUnwritten;
    private void Note(int w) { if (Written != null && !Written[w] && ReadUnwritten != null) { ReadUnwritten.TryGetValue(w, out int n); ReadUnwritten[w] = n + 1; } }

    public SnesMachine(byte[] rom)
    {
        Rom = rom;
        _exLo = rom.Length > 0x400000;
        Cpu = new SnesCpu(this);
    }

    private int RomIndex(int addr)
    {
        int bank = addr >> 16, a = addr & 0xFFFF;
        if (_exLo && bank < 0x7E && a >= 0x8000)
        {
            int o = 0x400000 + (bank & 0x7F) * 0x8000 + (a & 0x7FFF);
            return o < Rom.Length ? o : -1;
        }
        int off = (bank & 0x7F) * 0x8000 + (a & 0x7FFF);
        return off < Rom.Length ? off : off % Rom.Length;
    }

    public byte Read(int addr)
    {
        int bank = addr >> 16, a = addr & 0xFFFF;
        if (bank is 0x7E or 0x7F) { Note(addr - 0x7E0000); return Wram[addr - 0x7E0000]; }
        bool sys = (bank & 0x7F) < 0x40;
        if (sys && a < 0x2000) { Note(a); return Wram[a]; }
        if (sys && a < 0x8000)
        {
            if (a >= 0x2100 && a < 0x2200) return ReadPpu(a & 0xFF);
            if (a >= 0x4200 && a < 0x4400) return ReadIo(a);
            if (a >= 0x6000) return 0;
            return 0;
        }
        if (a < 0x8000 && (bank & 0x7F) >= 0x70 && (bank & 0x7F) < 0x7E) return Sram[a & 0x7FFF];
        int o = RomIndex(addr);
        return o >= 0 ? Rom[o] : (byte)0;
    }

    public void Write(int addr, byte v)
    {
        int bank = addr >> 16, a = addr & 0xFFFF;
        if (bank is 0x7E or 0x7F) { Wram[addr - 0x7E0000] = v; if (Written != null) Written[addr - 0x7E0000] = true; return; }
        bool sys = (bank & 0x7F) < 0x40;
        if (sys && a < 0x2000) { Wram[a] = v; if (Written != null) Written[a] = true; return; }
        if (sys && a < 0x8000)
        {
            if (a >= 0x2100 && a < 0x2200) WritePpu(a & 0xFF, v);
            else if (a >= 0x4200 && a < 0x4400) WriteIo(a, v);
            return;
        }
        if (a < 0x8000 && (bank & 0x7F) >= 0x70 && (bank & 0x7F) < 0x7E) Sram[a & 0x7FFF] = v;
    }

    // ── PPU ports ─────────────────────────────────────────────────────────
    private int VramStep => (Regs[0x15] & 3) switch { 0 => 1, 1 => 32, _ => 128 };

    private void WritePpu(int r, byte v)
    {
        switch (r)
        {
            case 0x02: OamAddr = (OamAddr & 0x200) | v << 1; Regs[r] = v; return;
            case 0x03: OamAddr = (OamAddr & 0x1FE) | (v & 1) << 9; Regs[r] = v; return;
            case 0x04:
                if (OamAddr >= 0x200) Oam[0x200 + (OamAddr & 0x1F)] = v;
                else if ((OamAddr & 1) == 0) _oamLatch = v;
                else { Oam[OamAddr - 1] = (byte)_oamLatch; Oam[OamAddr] = v; }
                OamAddr = (OamAddr + 1) & 0x3FF;
                if (OamAddr >= 0x220) OamAddr &= 0x1FF;
                return;
            case 0x0D: case 0x0F: case 0x11: case 0x13:
            {
                int n = (r - 0x0D) / 2;
                HScroll[n] = (v << 8 | (_bgLatch & ~7) | (HScroll[n] >> 8 & 7)) & 0x3FF;
                _bgLatch = v;
                if (r == 0x0D) _m7Latch = v;
                return;
            }
            case 0x0E: case 0x10: case 0x12: case 0x14:
            {
                int n = (r - 0x0E) / 2;
                VScroll[n] = (v << 8 | _bgLatch) & 0x3FF;
                _bgLatch = v;
                return;
            }
            case 0x16: VramAddr = (VramAddr & 0xFF00) | v; _vramLatch = Vram[(VramAddr * 2) & 0xFFFF] | Vram[(VramAddr * 2 + 1) & 0xFFFF] << 8; Regs[r] = v; return;
            case 0x17: VramAddr = (VramAddr & 0xFF) | v << 8; _vramLatch = Vram[(VramAddr * 2) & 0xFFFF] | Vram[(VramAddr * 2 + 1) & 0xFFFF] << 8; Regs[r] = v; return;
            case 0x18:
                Vram[(VramAddr * 2) & 0xFFFF] = v;
                if ((Regs[0x15] & 0x80) == 0) VramAddr = (VramAddr + VramStep) & 0x7FFF;
                return;
            case 0x19:
                Vram[(VramAddr * 2 + 1) & 0xFFFF] = v;
                if ((Regs[0x15] & 0x80) != 0) VramAddr = (VramAddr + VramStep) & 0x7FFF;
                return;
            case 0x21: CgAddr = v; _cgHigh = false; Regs[r] = v; return;
            case 0x22:
                if (!_cgHigh) { _cgLatch = v; _cgHigh = true; }
                else { Cgram[CgAddr * 2] = (byte)_cgLatch; Cgram[CgAddr * 2 + 1] = (byte)(v & 0x7F); CgAddr = (CgAddr + 1) & 0xFF; _cgHigh = false; }
                return;
            case 0x32:
                if ((v & 0x20) != 0) FixedR = v & 31;
                if ((v & 0x40) != 0) FixedG = v & 31;
                if ((v & 0x80) != 0) FixedB = v & 31;
                return;
            case 0x40: case 0x41: _apu[r - 0x40] = v; return;
            case 0x42: case 0x43: _apuAck[r - 0x42] = !(_apuAck[r - 0x42] && v == 0); return;
            case 0x80: Wram[_wramPort] = v; _wramPort = (_wramPort + 1) & 0x1FFFF; return;
            case 0x81: _wramPort = (_wramPort & 0x1FF00) | v; return;
            case 0x82: _wramPort = (_wramPort & 0x100FF) | v << 8; return;
            case 0x83: _wramPort = (_wramPort & 0xFFFF) | (v & 1) << 16; return;
        }
        Regs[r] = v;
    }
    public int FixedR, FixedG, FixedB;

    private byte ReadPpu(int r)
    {
        switch (r)
        {
            case 0x39:
            {
                int v = _vramLatch & 0xFF;
                if ((Regs[0x15] & 0x80) == 0) { _vramLatch = Vram[(VramAddr * 2) & 0xFFFF] | Vram[(VramAddr * 2 + 1) & 0xFFFF] << 8; VramAddr = (VramAddr + VramStep) & 0x7FFF; }
                return (byte)v;
            }
            case 0x3A:
            {
                int v = _vramLatch >> 8;
                if ((Regs[0x15] & 0x80) != 0) { _vramLatch = Vram[(VramAddr * 2) & 0xFFFF] | Vram[(VramAddr * 2 + 1) & 0xFFFF] << 8; VramAddr = (VramAddr + VramStep) & 0x7FFF; }
                return (byte)v;
            }
            case 0x3B:
            {
                byte v = _cgHigh ? (byte)(Cgram[CgAddr * 2 + 1] & 0x7F) : Cgram[CgAddr * 2];
                if (_cgHigh) CgAddr = (CgAddr + 1) & 0xFF;
                _cgHigh = !_cgHigh;
                return v;
            }
            case 0x38:
            {
                byte v = OamAddr >= 0x200 ? Oam[0x200 + (OamAddr & 0x1F)] : Oam[OamAddr];
                OamAddr = (OamAddr + 1) & 0x3FF; if (OamAddr >= 0x220) OamAddr &= 0x1FF;
                return v;
            }
            // Lufia II waits for its sound driver to say CD EF (ready) on ports 2 and 3, and for echoes on 0 and 1
            case 0x42: return (byte)(_apuAck[0] ? 0xCD : 0);
            case 0x43: return (byte)(_apuAck[1] ? 0xEF : 0);
            case 0x40: case 0x41: return _apu[r - 0x40];
            case 0x80: { byte v = Wram[_wramPort]; _wramPort = (_wramPort + 1) & 0x1FFFF; return v; }
            case 0x3F: return 0x01;
        }
        return 0;
    }

    // ── CPU registers, DMA ─────────────────────────────────────────────────
    private byte ReadIo(int a)
    {
        switch (a)
        {
            case 0x4210: { byte v = (byte)((NmiFlag ? 0x80 : 0) | 2); NmiFlag = false; return v; }
            case 0x4211: return 0;
            case 0x4212: _vblankToggle = !_vblankToggle; return (byte)(_vblankToggle ? 0x80 : 0x00);
            case 0x4214: return (byte)_divQuot;
            case 0x4215: return (byte)(_divQuot >> 8);
            case 0x4216: return (byte)_mulResult;
            case 0x4217: return (byte)(_mulResult >> 8);
        }
        if (a >= 0x4218 && a < 0x4220) return 0;
        if (a >= 0x4300) return Io[a - 0x4200];
        return 0;
    }

    private void WriteIo(int a, byte v)
    {
        Io[a - 0x4200] = v;
        switch (a)
        {
            case 0x4203: _mulResult = Io[2] * v; break;
            case 0x4206:
            {
                int dividend = Io[4] | Io[5] << 8;
                if (v == 0) { _divQuot = 0xFFFF; _mulResult = dividend; }
                else { _divQuot = dividend / v; _mulResult = dividend % v; }
                break;
            }
            case 0x420B: for (int c = 0; c < 8; c++) if ((v & 1 << c) != 0) Dma(c); break;
        }
    }

    private static readonly int[][] Pattern =
    {
        new[] { 0 }, new[] { 0, 1 }, new[] { 0, 0 }, new[] { 0, 0, 1, 1 }, new[] { 0, 1, 2, 3 }, new[] { 0, 1, 0, 1 }, new[] { 0, 0 }, new[] { 0, 0, 1, 1 },
    };

    private void Dma(int c)
    {
        int b = 0x100 + c * 0x10;
        int ctrl = Io[b], bb = Io[b + 1];
        int addr = Io[b + 2] | Io[b + 3] << 8, bank = Io[b + 4];
        int size = Io[b + 5] | Io[b + 6] << 8;
        if (size == 0) size = 0x10000;
        var pat = Pattern[ctrl & 7];
        bool toA = (ctrl & 0x80) != 0, fixedA = (ctrl & 0x08) != 0;
        int step = fixedA ? 0 : (ctrl & 0x10) != 0 ? -1 : 1;
        for (int i = 0; i < size; i++)
        {
            int reg = 0x2100 | ((bb + pat[i % pat.Length]) & 0xFF);
            int src = bank << 16 | addr;
            if (toA) Write(src, Read(reg));
            else Write(reg, Read(src));
            addr = (addr + step) & 0xFFFF;
        }
        Io[b + 2] = (byte)addr; Io[b + 3] = (byte)(addr >> 8);
        Io[b + 5] = 0; Io[b + 6] = 0;
    }

    // ── HDMA (run by the renderer, line by line) ─────────────────────────────
    private readonly int[] _hAddr = new int[8], _hInd = new int[8], _hLine = new int[8];
    private readonly bool[] _hDo = new bool[8], _hOn = new bool[8];

    public void HdmaInit()
    {
        int en = Io[0x0C];
        for (int c = 0; c < 8; c++)
        {
            _hOn[c] = (en & 1 << c) != 0;
            if (!_hOn[c]) continue;
            int b = 0x100 + c * 0x10;
            _hAddr[c] = Io[b + 2] | Io[b + 3] << 8;
            HdmaLoad(c);
        }
    }

    private void HdmaLoad(int c)
    {
        int b = 0x100 + c * 0x10, bank = Io[b + 4];
        int n = Read(bank << 16 | _hAddr[c]);
        _hAddr[c] = (_hAddr[c] + 1) & 0xFFFF;
        _hLine[c] = n;
        if (n == 0) { _hOn[c] = false; return; }
        if ((Io[b] & 0x40) != 0)
        {
            _hInd[c] = Read(bank << 16 | _hAddr[c]) | Read(bank << 16 | ((_hAddr[c] + 1) & 0xFFFF)) << 8;
            _hAddr[c] = (_hAddr[c] + 2) & 0xFFFF;
        }
        _hDo[c] = true;
    }

    public void HdmaLine()
    {
        for (int c = 0; c < 8; c++)
        {
            if (!_hOn[c]) continue;
            int b = 0x100 + c * 0x10;
            int ctrl = Io[b], bb = Io[b + 1];
            if (_hDo[c])
            {
                var pat = Pattern[ctrl & 7];
                bool ind = (ctrl & 0x40) != 0;
                for (int i = 0; i < pat.Length; i++)
                {
                    int src;
                    if (ind) { src = Io[b + 7] << 16 | _hInd[c]; _hInd[c] = (_hInd[c] + 1) & 0xFFFF; }
                    else { src = Io[b + 4] << 16 | _hAddr[c]; _hAddr[c] = (_hAddr[c] + 1) & 0xFFFF; }
                    Write(0x2100 | ((bb + pat[i]) & 0xFF), Read(src));
                }
            }
            int count = (_hLine[c] & 0x7F) - 1;
            if ((_hLine[c] & 0x7F) == 0) count = 0x7F;
            _hLine[c] = (_hLine[c] & 0x80) | (count & 0x7F);
            _hDo[c] = (_hLine[c] & 0x80) != 0;
            if ((_hLine[c] & 0x7F) == 0) HdmaLoad(c);
        }
    }

    /// <summary>The PPU registers as the renderer needs them, read from <see cref="Regs"/>.</summary>
    public byte Reg(int r) => Regs[r & 0xFF];
}
