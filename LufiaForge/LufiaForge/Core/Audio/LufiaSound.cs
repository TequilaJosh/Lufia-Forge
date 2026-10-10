namespace LufiaForge.Core.Audio;

/// <summary>
/// Lufia's own sound driver running on an emulated SNES sound chip. The SNES side of the game is reproduced
/// routine by routine, talking to the driver through the four ports exactly as the game does:
///   $00:8DE2  boot: upload the driver's block list ($02:9400), wait for $CDAB, send $8967
///   $00:8EA7  load the shared samples 00-0F and 30-3F after $6200, ask where free sound RAM starts (command 10)
///   $00:8E13  play song n: stop (03), unpack resource n, upload its instruments (0D / 0F / 0E),
///             send the song to $2800 (01), start it (02)
///   $00:8F54  the sound option (command 08): 1 = mono, sent by the intro's setup
///   $00:8F81  sync point of the playing song (command 06) - what event command 6C waits for
/// The console's boot ROM isn't needed (or included): the driver is placed in sound RAM directly and started
/// at its entry point, the way the boot ROM would after the upload.
/// </summary>
public sealed class LufiaSound : IDisposable
{
    /// <summary>
    /// Output samples per second. The sound chip makes one sample per 32 of its clocks; real consoles (and BizHawk)
    /// run it at about 32040 Hz rather than the nominal 32000, and the songs' tempo in the game follows that: the
    /// driver's state drifted 0.125% from the game's at 32000 and stays in step at 32040.
    /// </summary>
    public const int SampleRate = 32040;
    public const int SongCount = 0x25;   // $00:8E13 ignores higher numbers

    private const int DriverBlocks = 0x029400;   // SNES address of the boot upload's block list
    private const int SampleTable = 0x098000;    // 3-byte pointers to the instrument samples

    private readonly RomBuffer _rom;
    private IntPtr _spc;
    private int _freeAddress;
    private readonly short[] _scratch = new short[64];

    public int Song { get; private set; } = -1;

    private LufiaSound(RomBuffer rom) { _rom = rom; }

    /// <summary>Boots the driver from the ROM, or returns null when the sound DLL isn't available.</summary>
    public static LufiaSound? Create(RomBuffer rom)
    {
        if (!SpcNative.Available) return null;
        var s = new LufiaSound(rom);
        try { s.Boot(); return s; }
        catch { s.Dispose(); return null; }
    }

    public void Dispose()
    {
        if (_spc != IntPtr.Zero) { SpcNative.lfspc_delete(_spc); _spc = IntPtr.Zero; }
    }

    // ── ports ────────────────────────────────────────────────────────────────

    // each access costs the SNES CPU a few instructions: ~2.5 µs of sound chip time
    private int Read(int port) { Run(3); return SpcNative.lfspc_read(_spc, port); }
    private void Write(int port, int value) { Run(3); SpcNative.lfspc_write(_spc, port, value & 0xFF); }
    private void Run(int clocks) { SpcNative.lfspc_wait(_spc, clocks); Clock += clocks; }

    /// <summary>Sound chip clocks (1.024 MHz) that have passed since the driver booted.</summary>
    public long Clock { get; private set; }
    private void Write16(int port, int value) { Write(port, value); Write(port + 1, value >> 8); }

    /// <summary>Lets the sound chip run (silently) until the condition holds, like the game's wait loops.</summary>
    private void WaitUntil(Func<bool> done)
    {
        for (int i = 0; i < 2_000_000; i++)   // up to ~10 s of sound chip time
        {
            if (done()) return;
            Run(2);
        }
        throw new InvalidOperationException($"The sound driver stopped answering (ports {Read(0):X2} {Read(1):X2} {Read(2):X2} {Read(3):X2}).");
    }

    /// <summary>$00:90E0 + $00:90EB: put a command on ports 2/3, wait for $CD/$EF, clear and wait for the echo.</summary>
    private void Command(int cmd)
    {
        Write(2, cmd); Write(3, cmd);
        WaitUntil(() => Read(2) == 0xCD && Read(3) == 0xEF);
        Write(2, 0); Write(3, 0);
        WaitUntil(() => Read(2) == 0 && Read(3) == 0);
    }

    // ── boot ────────────────────────────────────────────────────────────────

    private void Boot()
    {
        // the block list the boot ROM would receive: [length][address][data]..., length 0 = start at address
        var ram = new byte[0x10000];
        int at = Lufia1Constants.SnesAddressToFileOffset(DriverBlocks), entry = 0x0400;
        while (true)
        {
            int len = _rom.ReadUInt16Le(at), addr = _rom.ReadUInt16Le(at + 2);
            at += 4;
            if (len == 0) { entry = addr; break; }
            for (int i = 0; i < len; i++) ram[(addr + i) & 0xFFFF] = _rom.ReadByte(at + i);
            at += len;
        }
        _spc = SpcNative.lfspc_new();
        if (_spc == IntPtr.Zero) throw new InvalidOperationException("Sound chip emulator unavailable.");
        var err = SpcNative.lfspc_load(_spc, Snapshot(ram, entry), SnapshotSize);
        if (err != IntPtr.Zero) throw new InvalidOperationException("Sound chip snapshot refused.");

        WaitUntil(() => Read(0) == 0xAB && Read(1) == 0xCD);
        Write(2, 0x67); Write(3, 0x89);
        WaitUntil(() => Read(2) == 0xCD && Read(3) == 0xEF);
        Write(2, 0); Write(3, 0);
        WaitUntil(() => Read(2) == 0 && Read(3) == 0);
        LoadSharedSamples();
        SetSoundMode(1);
    }

    /// <summary>
    /// $00:8F54: command 08 with the sound option (bit 0). The intro's setup ($01:BD38) sends 1, which centres every
    /// voice (mono); the options menu toggles it. 0 is the driver's own default, with the songs' panning.
    /// </summary>
    public void SetSoundMode(int mode)
    {
        Write(0, mode & 1);
        Command(0x08);
    }

    private const int SnapshotSize = 0x10200;

    /// <summary>Sound RAM and registers in the SPC file layout, CPU starting at the driver's entry point.</summary>
    private static byte[] Snapshot(byte[] ram, int pc)
    {
        var f = new byte[SnapshotSize];
        var sig = "SNES-SPC700 Sound File Data v0.30"u8;
        sig.CopyTo(f);
        f[0x21] = 26; f[0x22] = 26; f[0x23] = 27; f[0x24] = 30;
        f[0x25] = (byte)pc; f[0x26] = (byte)(pc >> 8);   // PC
        f[0x2A] = 0x02;                                   // PSW
        f[0x2B] = 0xEF;                                   // SP as the boot ROM leaves it
        ram.CopyTo(f, 0x100);
        f[0x100 + 0xF1] = 0x00;                           // boot ROM unmapped, timers off
        f[0x10100 + 0x6C] = 0xE0;                         // DSP FLG: reset, muted, echo writes off
        return f;
    }

    /// <summary>$00:8EA7.</summary>
    private void LoadSharedSamples()
    {
        Write16(0, 0x6200); Command(0x0D);
        Write(0, 0x00); Command(0x0F);
        for (int i = 0; i < 16; i++) UploadSample(i);
        Write(0, 0x30); Command(0x0F);
        for (int i = 0x30; i < 0x40; i++) UploadSample(i);
        Command(0x10);
        _freeAddress = Read(0) | Read(1) << 8;
    }

    /// <summary>$00:8F0A + $00:8FEC: send one instrument sample (8-byte header, then the stream).</summary>
    private void UploadSample(int id)
    {
        int ptr = Lufia1Constants.SnesAddressToFileOffset(SampleTable) + id * 3;
        int bank = _rom.ReadByte(ptr + 2), addr = _rom.ReadUInt16Le(ptr);
        Command(0x0E);
        var src = new SnesReader(_rom, bank, addr);
        int len = src.Peek(0) | src.Peek(1) << 8;
        for (int x = 8; x >= 1; x--)
        {
            Write(0, src.Next());
            Write(2, x);
            int want = x | 0x80;
            WaitUntil(() => Read(2) == want);
        }
        Write(2, 0xFF);
        Stream(src, len);
    }

    /// <summary>$00:9073: two bytes per handshake, counter on port 2, then $FF on port 3 and the command ack.</summary>
    private void Stream(SnesReader src, int x)
    {
        int counter = 0;
        while (true)
        {
            Write(0, src.Next());
            Write(1, src.Next());
            int c = counter & 0x7F;
            Write(2, c);
            WaitUntil(() => Read(2) == (c | 0x80));
            counter = (c | 0x80) + 2;
            bool more = x >= 3;
            x -= 2;
            if (!more) break;
        }
        Write(3, 0xFF);
        WaitUntil(() => Read(2) == 0xCD && Read(3) == 0xEF);
        Write(2, 0); Write(3, 0);
        WaitUntil(() => Read(2) == 0 && Read(3) == 0);
    }

    // ── music ───────────────────────────────────────────────────────────────

    public const double FramesPerSecond = 60.0988;
    /// <summary>Sound chip clocks per SNES frame.</summary>
    public const double ClocksPerFrame = SampleRate * 32 / FramesPerSecond;

    /// <summary>
    /// Measured on the intro's song 1C: the game spends 0.42 frames unpacking the song, and its upload loop runs
    /// 1.2245 times longer than the sound chip time the upload takes here (the SNES CPU's own loop time), so the
    /// song starts 12.65 frames after the load: entry on frame 224, command 02 on frame 237, scanline 154.
    /// </summary>
    private const double UnpackFrames = 0.42, UploadSlowdown = 1.2245;

    /// <summary>
    /// $00:8E13: load song n and start it. Returns how many frames after the load the song starts playing in the
    /// game (the driver is silent until then).
    /// </summary>
    public double PlaySong(int song)
    {
        if (song < 0 || song >= SongCount) return 0;
        double frames = PlaySongData(LufiaCompression.DecompressResource(_rom, song, out _));
        Song = song;
        return frames;
    }

    /// <summary>Loads and starts song data that isn't in the ROM (unpacked, with its length word): the note editor's preview.</summary>
    public double PlaySongData(byte[] data)
    {
        long start = Clock;
        Command(0x03);
        Write16(0, _freeAddress); Command(0x0D);
        for (int i = 0; i < 8 && 0x32 + i < data.Length; i++)
        {
            int id = data[0x32 + i];
            if (id == 0) break;
            if (id >= 0x38) continue;
            Write(0, id); Command(0x0F);
            UploadSample(id);
        }
        // $00:9040: destination $2800, length from the data's first word
        Write16(0, 0x2800);
        int len = data[0] | data[1] << 8;
        Command(0x01);
        Write(2, 0xFF);
        Stream(new SnesReader(data, 2), len);
        Command(0x02);
        Song = -1;
        return UnpackFrames + (Clock - start) / ClocksPerFrame * UploadSlowdown;
    }

    /// <summary>Event 0A FF ($00:8F98 → command 04).</summary>
    public void StopMusic()
    {
        Command(0x0B);
        if (Read(1) >= 0xFF) { Write(0, 1); Command(0x04); }
        Song = -1;
    }

    /// <summary>$00:8E8F: sound effect n (command 09) - event command 54, and the thunder of a lightning flash.</summary>
    public void PlaySoundEffect(int id)
    {
        Write(0, id);
        Command(0x09);
    }

    /// <summary>
    /// The sound effect a lightning flash (event command 60) plays: the flash routine ($01:8098) takes record
    /// (low nibble + 1) from the table at $01:80F1, whose byte 1 is the effect (0 = none) played on its first frame.
    /// </summary>
    public static int FlashSoundEffect(RomBuffer rom, int param)
    {
        int table = Lufia1Constants.SnesAddressToFileOffset(0x0180F1) + (param & 0x0F) * 2;
        int rec = Lufia1Constants.SnesAddressToFileOffset(0x01, rom.ReadUInt16Le(table));
        return rom.ReadByte(rec + 1);
    }

    /// <summary>$00:8F81: the sync point the song has reached (what event command 6C compares with).</summary>
    public int SyncPoint()
    {
        Command(0x06);
        return (Read(0) + 1) & 0xFF;
    }

    /// <summary>Runs the sound chip for <paramref name="count"/> samples (2 per stereo pair) into the buffer.</summary>
    public void Render(short[] buffer, int count)
    {
        SpcNative.lfspc_render(_spc, buffer, count & ~1);
        Clock += (count & ~1) / 2 * 32;
    }

    /// <summary>The 64 KB of sound RAM, for tests.</summary>
    public byte[] Ram()
    {
        var r = new byte[0x10000];
        SpcNative.lfspc_ram(_spc, r);
        return r;
    }

    /// <summary>Reads bytes in order, from ROM (crossing LoROM bank ends like $00:9073) or from a buffer.</summary>
    private sealed class SnesReader
    {
        private readonly RomBuffer? _rom; private readonly byte[]? _buf;
        private int _bank, _addr, _pos;
        public SnesReader(RomBuffer rom, int bank, int addr) { _rom = rom; _bank = bank; _addr = addr; }
        public SnesReader(byte[] buf, int pos) { _buf = buf; _pos = pos; }

        public int Peek(int k) => _buf != null ? Get(_pos + k) : _rom!.ReadByte(Lufia1Constants.SnesAddressToFileOffset(_bank, _addr + k));

        public int Next()
        {
            if (_buf != null) return Get(_pos++);
            int v = _rom!.ReadByte(Lufia1Constants.SnesAddressToFileOffset(_bank, _addr));
            _addr++;
            if (_addr > 0xFFFF) { _addr = 0x8000; _bank++; }
            return v;
        }

        private int Get(int i) => i < _buf!.Length ? _buf[i] : 0;
    }
}
