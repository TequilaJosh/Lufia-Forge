using LufiaForge.Core.Lufia2;

namespace LufiaForge.Core.Audio;

// Lufia II's own sound driver running on an emulated SNES sound chip. we redo the SNES side of the game (bank $80)
// routine by routine, talking to the driver through the four ports exactly like the game does:
//   $80:9383  boot: the block list at $90:C000 (file 0x84000), wait for $AB/$CD, answer $67/$89, then $80:9623
//             (command 0D with 0) and $80:93D6
//   $80:9703  shared samples: write address $5800 (11), slot 0 (13), samples 00-1F (14 each), end address (12)
//   $80:941A  load song n: stop (03), command 05, unpack file n, check "S2", the sample write address (header +18
//             set: $5800, otherwise after the shared samples, reloading them first if the last song wrote over
//             them), the song's samples into slots 20+ and its drum samples into their own slots, the song to $4400
//             (01), the end address (12). $80:93FE then starts it (02)
//   $80:9528  stop (03)        $80:953B  sound effect n (04)
// port helpers: $80:99FD puts a command on ports 2 and 3, $80:9A0A waits for $CD/$EF, clears both and waits for the
// echo, $80:98A5 sends a sample's 8-byte header, $80:9945 streams two bytes per handshake.
// the console's boot ROM isn't needed, the driver goes straight into sound RAM and starts at its entry point
public sealed class L2Sound : IDisposable
{
    // output samples per second (see LufiaSound.SampleRate)
    public const int SampleRate = LufiaSound.SampleRate;
    // songs are packed files 00-3E ($80:941A takes numbers below $64, files 3F and up are maps)
    public const int SongCount = 0x3F;
    // $80:953B ignores effect numbers from $8F
    public const int EffectCount = 0x8F;

    private const int DriverBlocks = 0x84000;   // $90:C000
    private const int SampleTable = 0x0C0000;   // $98:8000
    private const int DrumNotes = 0x001A34;     // $80:9A34: drum notes, 0 ends
    private const int DrumSamples = 0x001A59;   // $80:9A59: the shared sample of each
    public const int SongAddress = 0x4400, SharedSamples = 0x5800;

    private readonly RomBuffer _rom;
    private IntPtr _spc;
    private int _sharedEnd;      // $0584
    private int _songEnd;        // $0586
    private int _overwritten;    // $0588: the last song's header +18 (its samples replaced the shared ones)

    public int Song { get; private set; } = -1;
    // where free sound RAM starts after the shared samples ($0584) and after the song's ($0586)
    public int SharedSamplesEnd => _sharedEnd;
    public int SongSamplesEnd => _songEnd;

    private L2Sound(RomBuffer rom) { _rom = rom; }

    // boots the driver from the rom, or returns null when the sound DLL isn't there
    public static L2Sound? Create(RomBuffer rom)
    {
        if (!SpcNative.Available) return null;
        var sound = new L2Sound(rom);
        try { sound.Boot(); return sound; }
        catch { sound.Dispose(); return null; }
    }

    // like Create but lets the reason through when the driver doesn't answer (for tests)
    public static L2Sound CreateOrThrow(RomBuffer rom)
    {
        if (!SpcNative.Available) throw new InvalidOperationException("lufia_spc.dll isn't available.");
        var sound = new L2Sound(rom);
        try { sound.Boot(); return sound; }
        catch { sound.Dispose(); throw; }
    }

    public void Dispose()
    {
        if (_spc != IntPtr.Zero) { SpcNative.lfspc_delete(_spc); _spc = IntPtr.Zero; }
    }

    // ports

    private int Read(int port) { Run(3); return SpcNative.lfspc_read(_spc, port); }
    private void Write(int port, int value) { Run(3); SpcNative.lfspc_write(_spc, port, value & 0xFF); }
    // a 16-bit store to $2140 (ports 0 and 1)
    private void Write16(int port, int value) { Write(port, value); Write(port + 1, value >> 8); }
    private void Run(int clocks) { SpcNative.lfspc_wait(_spc, clocks); Clock += clocks; }

    // sound chip clocks (1.024 MHz) that have passed since the driver booted
    public long Clock { get; private set; }

    private void WaitUntil(Func<bool> done)
    {
        for (int i = 0; i < 2_000_000; i++)
        {
            if (done()) return;
            Run(2);
        }
        throw new InvalidOperationException($"The sound driver stopped answering (ports {Read(0):X2} {Read(1):X2} {Read(2):X2} {Read(3):X2}).");
    }

    // $80:9A0A: wait for $CD on port 2 and $EF on port 3, clear both, wait until both read 0
    private void Ack()
    {
        WaitUntil(() => Read(2) == 0xCD);
        WaitUntil(() => Read(3) == 0xEF);
        Write(2, 0); Write(3, 0);
        WaitUntil(() => Read(2) == 0);
        WaitUntil(() => Read(3) == 0);
    }

    // $80:99FD + $80:9A0A
    private void Command(int cmd)
    {
        Write(2, cmd); Write(3, cmd);
        Ack();
    }

    // boot

    private void Boot()
    {
        // what $80:9C0F sends the boot ROM: [length][address][data]..., length 0 = start at address
        var ram = new byte[0x10000];
        int pos = DriverBlocks, entry = 0x0300;
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

        // $80:9397
        WaitUntil(() => Read(0) == 0xAB);
        WaitUntil(() => Read(1) == 0xCD);
        Write(2, 0x67); Write(3, 0x89);
        Ack();
        // $80:93B6: 1 only when the controller reads $3000 (Select + Start held) at power-on, 0 otherwise
        SetMode(0);
        // $80:93D6
        _overwritten = 0;
        LoadSharedSamples();
        _overwritten = 0;
    }

    // $80:9623: command 0D with the value on port 0 (the driver keeps it in $99, the boot sends 0)
    public void SetMode(int value)
    {
        Write(0, value);
        Command(0x0D);
    }

    private const int SnapshotSize = 0x10200;

    private static byte[] Snapshot(byte[] ram, int pc)
    {
        var file = new byte[SnapshotSize];
        "SNES-SPC700 Sound File Data v0.30"u8.CopyTo(file);
        file[0x21] = 26; file[0x22] = 26; file[0x23] = 27; file[0x24] = 30;
        file[0x25] = (byte)pc; file[0x26] = (byte)(pc >> 8);
        file[0x2A] = 0x02;
        file[0x2B] = 0xEF;
        ram.CopyTo(file, 0x100);
        file[0x100 + 0xF1] = 0x00;
        file[0x10100 + 0x6C] = 0xE0;
        return file;
    }

    // $80:9703
    private void LoadSharedSamples()
    {
        Write16(0, SharedSamples); Command(0x11);
        Write(0, 0x00); Command(0x13);
        for (int id = 0; id < 0x20; id++) UploadSample(id);
        Command(0x12);
        _sharedEnd = Read(0) | Read(1) << 8;
    }

    // $80:9886: command 14, then the sample's header ($80:98A5) and its BRR stream ($80:9945)
    private void UploadSample(int id)
    {
        Command(0x14);
        // $80:98A5: entry = 23-bit offset from $98:8000 in 32 KB banks
        int entry = SampleTable + (id & 0xFF) * 3;
        int word = _rom.ReadUInt16Le(entry), high = _rom.ReadByte(entry + 2);
        int y = (word & 0x7FFF) | 0x8000;
        int rol = (high << 1 | word >> 15);
        int bank = (rol + 0x98 + (rol >> 8)) & 0xFF;   // ROL then ADC #$98 with the carry out of the ROL
        var source = new SnesReader(_rom, bank, y);
        // the length word: read through a copy of Y, the bank stays where it ends (like $98D7-$98E7 do)
        int length = source.Next(); length |= source.Next() << 8;
        source.Rewind(y);
        for (int x = 8; x >= 1; x--)
        {
            Write(0, source.Next());
            Write(2, x);
            int want = x | 0x80;
            WaitUntil(() => Read(2) == want);
        }
        Write(2, 0xFF);
        Ack();
        Stream(source, length);
    }

    // $80:9945: $FF on port 2 and wait for its echo, then two bytes per handshake (counter on port 2, the driver
    // answers counter | $80), then $FF on port 3 and $80:9A0A
    private void Stream(SnesReader source, int x)
    {
        Write(2, 0xFF);
        WaitUntil(() => Read(2) == 0xFF);
        int counter = 0;
        int first = source.Next(), second = source.Next();
        while (true)
        {
            Write(0, first); Write(1, second);
            Write(2, counter & 0x7F);
            first = source.Next(); second = source.Next();
            int want = (counter | 0x80) & 0xFF;
            WaitUntil(() => Read(2) == want);
            counter = (counter + 2) & 0xFF;
            bool more = x >= 3;
            x = (x - 2) & 0xFFFF;
            if (!more) break;
        }
        Write(3, 0xFF);
        Ack();
    }

    // music

    // $80:93FE: load song n ($80:941A) and start it (command 02). false when the game would ignore it
    public bool PlaySong(int song)
    {
        if (song < 0 || song >= SongCount) { Stop(); Command(0x05); return false; }
        if (!PlaySongData(L2Rom.ReadFile(_rom, song))) return false;
        Song = song;
        return true;
    }

    // $80:941A + command 02 on song data that isn't necessarily the rom's (unpacked file: 2-byte length, then the
    // image for $4400). false when the "S2" signature is missing (then the game doesn't start anything)
    public bool PlaySongData(byte[] data)
    {
        if (!LoadSong(data)) return false;
        Command(0x02);
        return true;
    }

    private bool LoadSong(byte[] file)
    {
        Song = -1;
        Command(0x03);   // $80:9528
        Command(0x05);   // $80:95D2
        // the unpacked file sits at $7E:2020; the sample list ($7E:2000-$201F) right before it
        var wram = new byte[0x20 + Math.Max(file.Length, 0x52) + 4];
        file.CopyTo(wram, 0x20);
        int B(int address) => wram[address - 0x2000];
        if (B(0x2022) != 0x53 || B(0x2023) != 0x32) return false;   // "S2"

        int big = B(0x203A);
        if (big != 0)
        {
            _overwritten = big;
            Write16(0, SharedSamples); Command(0x11);
        }
        else
        {
            bool reload = _overwritten != 0;   // TRB $0588
            _overwritten = 0;
            if (reload) LoadSharedSamples();
            Write16(0, _sharedEnd); Command(0x11);
        }

        // $80:947F: sample ids below $80 go to slots 20, 21, ...
        int slot = 0x20, x = 0;
        for (; x < 0x20; x++)
        {
            int id = B(0x2052 + x);
            if ((id & 0x80) != 0) break;
            Write(0, slot); Command(0x13);
            UploadSample(id);
            slot++;
        }
        if (x < 0x20)
        {
            // $80:94AA: the rest are drum notes, and each one's shared sample goes in again, into the slot of its number
            var sent = new int[0x20];
            Array.Fill(sent, 0xFF);
            int count = 0;
            for (; x < 0x20; x++)
            {
                int value = B(0x2052 + x);
                if (value == 0xFF) break;
                int note = value & 0x7F;
                for (int y = 0; ; y++)
                {
                    int drumNote = _rom.ReadByte(DrumNotes + y);
                    if (drumNote == 0) break;
                    if (drumNote != note) continue;
                    int id = _rom.ReadByte(DrumSamples + y);
                    if (Array.IndexOf(sent, id) >= 0) break;
                    sent[count & 0x1F] = id; count++;
                    Write(0, id); Command(0x13);
                    UploadSample(id);
                    break;
                }
            }
        }

        // $80:9911: the song to $4400, length from the file's first word
        Write16(0, SongAddress);
        int length = file[0] | file[1] << 8;
        Command(0x01);
        Stream(new SnesReader(file, 2), length);
        Command(0x12);
        _songEnd = Read(0) | Read(1) << 8;
        return true;
    }

    // $80:9528: stop the music (command 03)
    public void Stop()
    {
        Command(0x03);
        Song = -1;
    }

    // $80:953B: sound effect n (command 04), numbers from $8F get ignored like the game does
    public void PlaySoundEffect(int id)
    {
        if (id < 0 || id >= EffectCount) return;
        Write(0, id);
        Command(0x04);
    }

    // $80:96F2: the song's sync counter (command 08), what event 4D waits for
    public int SyncPoint()
    {
        Command(0x08);
        return Read(0);
    }

    // $80:95F0: the driver's status byte (command 0F), bit 1 = a sound effect is playing
    public int Status()
    {
        Command(0x0F);
        return Read(0);
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

    // reads bytes in order, from the rom (crossing LoROM bank ends like $80:97DA) or from a buffer
    private sealed class SnesReader
    {
        private readonly RomBuffer? _rom; private readonly byte[]? _buf;
        private int _bank, _addr, _pos;
        public SnesReader(RomBuffer rom, int bank, int addr) { _rom = rom; _bank = bank; _addr = addr; }
        public SnesReader(byte[] buf, int pos) { _buf = buf; _pos = pos; }

        public void Rewind(int addr) => _addr = addr;

        public int Next()
        {
            if (_buf != null) { int i = _pos++; return i < _buf.Length ? _buf[i] : 0; }
            int offset = L2Rom.SnesToFile(_bank << 16 | _addr);
            int value = offset < _rom!.Length ? _rom.ReadByte(offset) : 0;
            _addr++;
            if (_addr > 0xFFFF) { _addr = 0x8000; _bank = (_bank + 1) & 0xFF; }
            return value;
        }
    }
}
