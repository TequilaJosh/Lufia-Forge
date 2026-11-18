using System.IO;

namespace LufiaForge.Core;

// the whole rom in memory, with checked reads and writes.
// every module goes through this to touch the rom, no raw file i/o after it's loaded
public class RomBuffer
{
    private byte[] _data;
    private bool _isDirty;

    public int Length => _data.Length;
    public bool IsDirty => _isDirty;

    // goes up with every change to the bytes. a page that read the rom at one version reads it again when it's shown
    // at another (another page, undo or a patch changed it in the meantime)
    public int Version { get; private set; }
    public string FilePath { get; private set; }

    public RomBuffer(byte[] data, string filePath)
    {
        _data    = data;
        FilePath = filePath;
        Game     = GameDetect.Detect(data);
    }

    // which game this rom is (from its header title, read when the buffer gets made)
    public GameKind Game { get; private set; }
    public bool IsLufia2 => Game == GameKind.Lufia2;

    // reading

    public byte ReadByte(int offset)
    {
        ValidateOffset(offset, 1);
        return _data[offset];
    }

    public ushort ReadUInt16Le(int offset)
    {
        ValidateOffset(offset, 2);
        return (ushort)(_data[offset] | (_data[offset + 1] << 8));
    }

    public uint ReadUInt24Le(int offset)
    {
        ValidateOffset(offset, 3);
        return (uint)(_data[offset] | (_data[offset + 1] << 8) | (_data[offset + 2] << 16));
    }

    public uint ReadUInt32Le(int offset)
    {
        ValidateOffset(offset, 4);
        return (uint)(_data[offset] | (_data[offset + 1] << 8) |
                      (_data[offset + 2] << 16) | (_data[offset + 3] << 24));
    }

    // reads a slice of bytes. returns a copy, not a reference into the buffer
    public byte[] ReadBytes(int offset, int count)
    {
        ValidateOffset(offset, count);
        var result = new byte[count];
        Array.Copy(_data, offset, result, 0, count);
        return result;
    }

    // reads bytes from offset up to a terminator byte (terminator not included).
    // at most maxLength bytes, so a bad offset can't run away
    public byte[] ReadUntil(int offset, byte terminator, int maxLength = 512)
    {
        var result = new List<byte>();
        int pos    = offset;
        while (pos < _data.Length && result.Count < maxLength)
        {
            byte b = _data[pos++];
            if (b == terminator) break;
            result.Add(b);
        }
        return result.ToArray();
    }

    // reads a fixed-length ascii string (for the snes header title)
    public string ReadAscii(int offset, int length)
    {
        ValidateOffset(offset, length);
        return System.Text.Encoding.ASCII.GetString(_data, offset, length);
    }

    // writing

    public void WriteByte(int offset, byte value)
    {
        ValidateOffset(offset, 1);
        Guard(offset, 1);
        _data[offset] = value;
        _isDirty = true; Version++;
    }

    public void WriteUInt16Le(int offset, ushort value)
    {
        ValidateOffset(offset, 2);
        Guard(offset, 2);
        _data[offset]     = (byte)(value & 0xFF);
        _data[offset + 1] = (byte)(value >> 8);
        _isDirty = true; Version++;
    }

    public void WriteBytes(int offset, byte[] bytes)
    {
        ValidateOffset(offset, bytes.Length);
        Guard(offset, bytes.Length);
        Array.Copy(bytes, 0, _data, offset, bytes.Length);
        _isDirty = true; Version++;
    }

    // snapshot / save

    // a full copy of the rom as it is now (for patching or saving)
    public byte[] ToArray()
    {
        var copy = new byte[_data.Length];
        Array.Copy(_data, copy, _data.Length);
        return copy;
    }

    // saves the buffer to a new copy next to the rom, named with the save date and time
    // (like "Lufia_2026-10-03_14-05-22.sfc"). the rom file that was opened never gets overwritten and FilePath keeps
    // pointing at it. returns the path of the copy
    public string SaveDatedCopy()
    {
        string folder = Path.GetDirectoryName(FilePath) ?? "";
        string extension = Path.GetExtension(FilePath);
        // if the opened rom is a dated copy itself, replace its stamp instead of stacking another one on
        string name = DatedSuffix.Replace(Path.GetFileNameWithoutExtension(FilePath), "");
        string path = Path.Combine(folder, $"{name}_{DateTime.Now:yyyy-MM-dd_HH-mm-ss}{extension}");

        WriteSafely(path);
        _isDirty = false;
        return path;
    }

    // writes the rom (checksum fixed, some edits write in place without it) to a temp file first and then puts it in
    // place, so a failed write never leaves a cut-off rom. the checksum gets fixed in the copy we write, not in the rom
    // being edited (that would count as an edit: project unsaved, an undo step)
    private void WriteSafely(string path)
    {
        var written = new RomBuffer((byte[])_data.Clone(), FilePath);
        written.FixChecksum();
        string tempFile = path + ".tmp";
        File.WriteAllBytes(tempFile, written._data);
        if (File.Exists(path)) File.Replace(tempFile, path, null);
        else File.Move(tempFile, path);
    }

    private static readonly System.Text.RegularExpressions.Regex DatedSuffix =
        new(@"_\d{4}-\d{2}-\d{2}_\d{2}-\d{2}-\d{2}$");

    // whether the rom's bytes are exactly these
    public bool SameAs(byte[] other) => _data.AsSpan().SequenceEqual(other);

    // writes the rom as it is now to a file (a playable copy) without changing FilePath
    public void WriteCopy(string path) => WriteSafely(path);

    // where copies of the rom go (a project's rom file next to the project): exports and the copy bizhawk plays
    public void SetFilePath(string path) => FilePath = path;

    // saves the buffer to a new path (save as) and updates FilePath
    public void SaveToFile(string path)
    {
        WriteSafely(path);
        FilePath = path;
        _isDirty = false;
    }

    // overwrites the whole buffer with new data.
    // the patch manager uses it to apply an ips patch in place
    public void ReplaceAll(byte[] newData)
    {
        // a patch can expand (or shrink) the rom, so take its size
        if (newData.Length != _data.Length) _data = new byte[newData.Length];
        Array.Copy(newData, _data, newData.Length);
        _isDirty = true; Version++;
        Maps.ExpansionSpace.Forget(this);
    }

    // the rom's size before we first expanded it in this session (0 = not expanded)
    public int SizeBeforeExpansion { get; private set; }

    // grows the rom to newSize bytes, fills the new space with fill and updates the snes header's rom size byte.
    // existing data stays the same
    public void Expand(int newSize, byte fill = 0xFF)
    {
        if (newSize <= _data.Length) return;
        // a rom another hack already made bigger than 1 MB: everything it put there has to stay where it is
        if (SizeBeforeExpansion == 0) SizeBeforeExpansion = _data.Length;
        Maps.ExpansionSpace.Forget(this);
        var grown = new byte[newSize];
        Array.Copy(_data, grown, _data.Length);
        Array.Fill(grown, fill, _data.Length, newSize - _data.Length);
        _data = grown;
        // header rom size byte: 2^n KB (0x0A = 1 MB, 0x0B = 2 MB)
        int sizeKb = newSize / 1024, sizeExponent = 0;
        while ((1 << sizeExponent) < sizeKb) sizeExponent++;
        _data[Lufia1Constants.SnesRomSizeOffset] = (byte)sizeExponent;
        _isDirty = true; Version++;
    }

    // recomputes the snes header checksum and complement for the current data
    public void FixChecksum()
    {
        Put16(Lufia1Constants.SnesComplementOffset, 0xFFFF);
        Put16(Lufia1Constants.SnesChecksumOffset, 0x0000);
        bool exLoRom = IsExLoRomCopy();
        if (exLoRom) CopyFirstBank();
        ushort checksum = SnesChecksum(_data);
        Put16(Lufia1Constants.SnesChecksumOffset, checksum);
        Put16(Lufia1Constants.SnesComplementOffset, (ushort)(checksum ^ 0xFFFF));
        if (exLoRom) CopyFirstBank();
        _isDirty = true; Version++;
    }

    // an 8 MB "ExLoROM" (a lufia 2 we made 8 MB): banks 00-7D read the upper 4 MB, so the console starts in, and takes
    // its interrupts through, a copy of the first 32 KB kept at 0x400000 (header included). that copy is how we tell a
    // rom like that apart. it gets renewed with the checksum so it follows changes to the first bank
    public bool IsExLoRomCopy() =>
        _data.Length == ExLoRomSize && _data.AsSpan(ExLoRomSize / 2 + 0x7FC0, 0x16).SequenceEqual(_data.AsSpan(0x7FC0, 0x16));

    public const int ExLoRomSize = 0x800000;

    private void CopyFirstBank() => Array.Copy(_data, 0, _data, ExLoRomSize / 2, 0x8000);

    // the snes header checksum: the sum of all bytes, where a size that isn't a power of two (like 1.5 MB) counts its
    // last part repeated up to the next power of two, the way the console mirrors it (1 MB + 2 x 512 KB)
    public static ushort SnesChecksum(byte[] rom)
    {
        int powerOfTwo = 1;
        while (powerOfTwo * 2 <= rom.Length) powerOfTwo *= 2;
        uint low = 0, high = 0;
        for (int i = 0; i < powerOfTwo; i++) low += rom[i];
        for (int i = powerOfTwo; i < rom.Length; i++) high += rom[i];
        int rest = rom.Length - powerOfTwo;
        uint repeats = rest > 0 && powerOfTwo % rest == 0 ? (uint)(powerOfTwo / rest) : 1;
        return (ushort)(low + high * repeats);
    }

    private void Put16(int offset, ushort value)
    {
        _data[offset] = (byte)value;
        _data[offset + 1] = (byte)(value >> 8);
    }

    // marks the buffer clean (after exporting a patch, say)
    public void ClearDirty() => _isDirty = false;

    // utilities

    // searches the rom for a byte pattern and returns every file offset that matches.
    // handy for finding where strings are
    public List<int> FindPattern(byte[] pattern)
    {
        var results = new List<int>();
        int limit   = _data.Length - pattern.Length;
        for (int i = 0; i <= limit; i++)
        {
            bool match = true;
            for (int j = 0; j < pattern.Length; j++)
            {
                if (_data[i + j] != pattern[j]) { match = false; break; }
            }
            if (match) results.Add(i);
        }
        return results;
    }

    // the rom as it was when the project started (or when it was opened), so we know what another hack put in it,
    // for RomIdentity.ForeignRanges. null when we don't know
    public byte[]? Original { get; set; }

    // places nothing can be written to: tables another hack moved away (their old bytes might be that hack's data
    // now). set when a rom is opened (TableGuard.Protect)
    public List<(int Start, int End, string Why)> Protected { get; } = new();

    private void Guard(int offset, int count)
    {
        if (Protected.Count == 0) return;
        foreach (var (start, end, why) in Protected)
            if (offset < end && offset + count > start) throw new InvalidOperationException(why);
    }

    private void ValidateOffset(int offset, int count)
    {
        if (offset < 0 || offset + count > _data.Length)
            throw new ArgumentOutOfRangeException(nameof(offset),
                $"Offset 0x{offset:X6} + {count} bytes exceeds ROM size 0x{_data.Length:X6}.");
    }
}
