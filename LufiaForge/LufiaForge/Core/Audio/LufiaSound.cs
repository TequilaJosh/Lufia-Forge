namespace LufiaForge.Core.Audio;

// Lufia's own sound driver running on an emulated SNES sound chip. we redo the SNES side of the game routine by
// routine, talking to the driver through the four ports exactly like the game does:
//   $00:8DE2  boot: upload the driver's block list ($02:9400), wait for $CDAB, send $8967
//   $00:8EA7  load the shared samples 00-0F and 30-3F after $6200, ask where free sound RAM starts (command 10)
//   $00:8E13  play song n: stop (03), unpack resource n, upload its instruments (0D / 0F / 0E),
//             send the song to $2800 (01), start it (02)
//   $00:8F54  the sound option (command 08): 1 = mono, the intro's setup sends it
//   $00:8F81  sync point of the playing song (command 06), what event command 6C waits for
// the console's boot ROM isn't needed (or included): the driver goes straight into sound RAM and starts at its entry
// point, the way the boot ROM would after the upload
public sealed class LufiaSound : IDisposable
{
    // output samples per second. the sound chip makes one sample per 32 of its clocks. real consoles (and BizHawk) run
    // it at about 32040 Hz instead of the nominal 32000, and the songs' tempo in the game follows that: the driver's
    // state drifted 0.125% from the game's at 32000 and stays in step at 32040
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

    // boots the driver from the rom, or returns null when the sound DLL isn't there
    public static LufiaSound? Create(RomBuffer rom)
    {
        if (!SpcNative.Available) return null;
        var sound = new LufiaSound(rom);
        try { sound.Boot(); return sound; }
        catch { sound.Dispose(); return null; }
    }

    public void Dispose()
    {
        if (_spc != IntPtr.Zero) { SpcNative.lfspc_delete(_spc); _spc = IntPtr.Zero; }
    }

    // ports

    // each access costs the SNES CPU a few instructions, about 2.5 microseconds of sound chip time
    private int Read(int port) { Run(3); return SpcNative.lfspc_read(_spc, port); }
    private void Write(int port, int value) { Run(3); SpcNative.lfspc_write(_spc, port, value & 0xFF); }
    private void Run(int clocks) { SpcNative.lfspc_wait(_spc, clocks); Clock += clocks; }

    // sound chip clocks (1.024 MHz) that have passed since the driver booted
    public long Clock { get; private set; }
    private void Write16(int port, int value) { Write(port, value); Write(port + 1, value >> 8); }

    // lets the sound chip run (silently) until the condition holds, like the game's wait loops
    private void WaitUntil(Func<bool> done)
    {
        for (int i = 0; i < 2_000_000; i++)   // up to ~10 s of sound chip time
        {
            if (done()) return;
            Run(2);
        }
        throw new InvalidOperationException($"The sound driver stopped answering (ports {Read(0):X2} {Read(1):X2} {Read(2):X2} {Read(3):X2}).");
    }

    // $00:90E0 + $00:90EB: put a command on ports 2/3, wait for $CD/$EF, clear and wait for the echo
    private void Command(int cmd)
    {
        Write(2, cmd); Write(3, cmd);
        WaitUntil(() => Read(2) == 0xCD && Read(3) == 0xEF);
        Write(2, 0); Write(3, 0);
        WaitUntil(() => Read(2) == 0 && Read(3) == 0);
    }

    // boot

    private void Boot()
    {
        // the block list the boot ROM would get: [length][address][data]..., length 0 = start at address
        var ram = new byte[0x10000];
        int pos = Lufia1Constants.SnesAddressToFileOffset(DriverBlocks), entry = 0x0400;
        while (true)
        {
            int length = _rom.ReadUInt16Le(pos), address = _rom.ReadUInt16Le(pos + 2);
            pos += 4;
            if (length == 0) { entry = address; break; }
            for (int i = 0; i < length; i++) ram[(address + i) & 0xFFFF] = _rom.ReadByte(pos + i);
            pos += length;
        }
        _spc = SpcNative.lfspc_new();
        if (_spc == IntPtr.Zero) throw new InvalidOperationException("Sound chip emulator unavailable.");
        var error = SpcNative.lfspc_load(_spc, Snapshot(ram, entry), SnapshotSize);
        if (error != IntPtr.Zero) throw new InvalidOperationException("Sound chip snapshot refused.");

        WaitUntil(() => Read(0) == 0xAB && Read(1) == 0xCD);
        Write(2, 0x67); Write(3, 0x89);
        WaitUntil(() => Read(2) == 0xCD && Read(3) == 0xEF);
        Write(2, 0); Write(3, 0);
        WaitUntil(() => Read(2) == 0 && Read(3) == 0);
        LoadSharedSamples();
        SetSoundMode(1);
    }

    // $00:8F54: command 08 with the sound option (bit 0). the intro's setup ($01:BD38) sends 1, which centres every
    // voice (mono), and the options menu toggles it. 0 is the driver's own default, with the songs' panning
    public void SetSoundMode(int mode)
    {
        Write(0, mode & 1);
        Command(0x08);
    }

    private const int SnapshotSize = 0x10200;

    // sound RAM and registers in the SPC file layout, with the CPU starting at the driver's entry point
    private static byte[] Snapshot(byte[] ram, int pc)
    {
        var file = new byte[SnapshotSize];
        var signature = "SNES-SPC700 Sound File Data v0.30"u8;
        signature.CopyTo(file);
        file[0x21] = 26; file[0x22] = 26; file[0x23] = 27; file[0x24] = 30;
        file[0x25] = (byte)pc; file[0x26] = (byte)(pc >> 8);   // PC
        file[0x2A] = 0x02;                                   // PSW
        file[0x2B] = 0xEF;                                   // SP the way the boot ROM leaves it
        ram.CopyTo(file, 0x100);
        file[0x100 + 0xF1] = 0x00;                           // boot ROM unmapped, timers off
        file[0x10100 + 0x6C] = 0xE0;                         // DSP FLG: reset, muted, echo writes off
        return file;
    }

    // $00:8EA7
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

    // $00:8F0A + $00:8FEC: send one instrument sample (8-byte header, then the stream)
    private void UploadSample(int id)
    {
        int pointer = Lufia1Constants.SnesAddressToFileOffset(SampleTable) + id * 3;
        int bank = _rom.ReadByte(pointer + 2), address = _rom.ReadUInt16Le(pointer);
        Command(0x0E);
        var source = new SnesReader(_rom, bank, address);
        int length = source.Peek(0) | source.Peek(1) << 8;
        for (int x = 8; x >= 1; x--)
        {
            Write(0, source.Next());
            Write(2, x);
            int want = x | 0x80;
            WaitUntil(() => Read(2) == want);
        }
        Write(2, 0xFF);
        Stream(source, length);
    }

    // $00:9073: two bytes per handshake, counter on port 2, then $FF on port 3 and the command ack
    private void Stream(SnesReader source, int x)
    {
        int counter = 0;
        while (true)
        {
            Write(0, source.Next());
            Write(1, source.Next());
            int count = counter & 0x7F;
            Write(2, count);
            WaitUntil(() => Read(2) == (count | 0x80));
            counter = (count | 0x80) + 2;
            bool more = x >= 3;
            x -= 2;
            if (!more) break;
        }
        Write(3, 0xFF);
        WaitUntil(() => Read(2) == 0xCD && Read(3) == 0xEF);
        Write(2, 0); Write(3, 0);
        WaitUntil(() => Read(2) == 0 && Read(3) == 0);
    }

    // music

    public const double FramesPerSecond = 60.0988;
    // sound chip clocks per SNES frame
    public const double ClocksPerFrame = SampleRate * 32 / FramesPerSecond;

    // measured on the intro's song 1C: the game spends 0.42 frames unpacking the song, and its upload loop runs
    // 1.2245 times longer than the sound chip time the upload takes here (the SNES CPU's own loop time), so the song
    // starts 12.65 frames after the load: entry on frame 224, command 02 on frame 237, scanline 154
    private const double UnpackFrames = 0.42, UploadSlowdown = 1.2245;

    // $00:8E13: load song n and start it. returns how many frames after the load the song starts playing in the game
    // (the driver is silent until then)
    public double PlaySong(int song)
    {
        if (song < 0 || song >= SongCount) return 0;
        double frames = PlaySongData(LufiaCompression.DecompressResource(_rom, song, out _));
        Song = song;
        return frames;
    }

    // loads and starts song data that isn't in the rom (unpacked, with its length word), for the note editor's preview
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
        int length = data[0] | data[1] << 8;
        Command(0x01);
        Write(2, 0xFF);
        Stream(new SnesReader(data, 2), length);
        Command(0x02);
        Song = -1;
        return UnpackFrames + (Clock - start) / ClocksPerFrame * UploadSlowdown;
    }

    // starts a song with no channels (song 01 with every channel marked unused): nothing is heard, but the driver runs
    // like it does while music plays. it only plays sound effects then (checked: after boot, or after 0A FF, effect
    // 10 gives silence, with this song it's heard), so we preview a sound on top of it
    public void PlaySilentSong()
    {
        var data = LufiaCompression.DecompressResource(_rom, 1, out _);
        for (int i = 0x12; i < 0x1A && i < data.Length; i++) data[i] = 0xFF;   // channel table (0x10-0x17 after the length word)
        PlaySongData(data);
    }

    // event 0A FF ($00:8F98 -> command 04)
    public void StopMusic()
    {
        Command(0x0B);
        if (Read(1) >= 0xFF) { Write(0, 1); Command(0x04); }
        Song = -1;
    }

    // $00:8E8F: sound effect n (command 09), for event command 54 and the thunder of a lightning flash
    public void PlaySoundEffect(int id)
    {
        Write(0, id);
        Command(0x09);
    }

    // the sound effect a lightning flash (event command 60) plays: the flash routine ($01:8098) takes record
    // (low nibble + 1) from the table at $01:80F1, and its byte 1 is the effect (0 = none) played on its first frame
    public static int FlashSoundEffect(RomBuffer rom, int param)
    {
        int table = Lufia1Constants.SnesAddressToFileOffset(0x0180F1) + (param & 0x0F) * 2;
        int record = Lufia1Constants.SnesAddressToFileOffset(0x01, rom.ReadUInt16Le(table));
        return rom.ReadByte(record + 1);
    }

    // $00:8F81: the sync point the song has reached (what event command 6C compares with)
    public int SyncPoint()
    {
        Command(0x06);
        return (Read(0) + 1) & 0xFF;
    }

    // runs the sound chip for count samples (2 per stereo pair) into the buffer
    public void Render(short[] buffer, int count)
    {
        SpcNative.lfspc_render(_spc, buffer, count & ~1);
        Clock += (count & ~1) / 2 * 32;
    }

    // the 64 KB of sound RAM, for tests
    public byte[] Ram()
    {
        var ram = new byte[0x10000];
        SpcNative.lfspc_ram(_spc, ram);
        return ram;
    }

    // reads bytes in order, from the rom (crossing LoROM bank ends like $00:9073) or from a buffer
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
            int value = _rom!.ReadByte(Lufia1Constants.SnesAddressToFileOffset(_bank, _addr));
            _addr++;
            if (_addr > 0xFFFF) { _addr = 0x8000; _bank++; }
            return value;
        }

        private int Get(int i) => i < _buf!.Length ? _buf[i] : 0;
    }
}
