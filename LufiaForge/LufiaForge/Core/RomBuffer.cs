using System.IO;

namespace LufiaForge.Core;

/// <summary>
/// Holds the entire ROM in memory and provides safe read/write helpers.
/// All modules interact with the ROM through this class - never raw file I/O after load.
/// </summary>
public class RomBuffer
{
    private readonly byte[] _data;
    private bool _isDirty;

    public int Length => _data.Length;
    public bool IsDirty => _isDirty;
    public string FilePath { get; private set; }

    public RomBuffer(byte[] data, string filePath)
    {
        _data    = data;
        FilePath = filePath;
    }

    // -------------------------------------------------------------------------
    // Read helpers
    // -------------------------------------------------------------------------

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

    /// <summary>Read a slice of bytes. Returns a copy - not a reference into the buffer.</summary>
    public byte[] ReadBytes(int offset, int count)
    {
        ValidateOffset(offset, count);
        var result = new byte[count];
        Array.Copy(_data, offset, result, 0, count);
        return result;
    }

    /// <summary>
    /// Read bytes from offset until a terminator byte is found (terminator not included).
    /// Returns at most maxLength bytes to prevent runaway reads on bad offsets.
    /// </summary>
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

    /// <summary>Read a fixed-length ASCII string (for the SNES header title field).</summary>
    public string ReadAscii(int offset, int length)
    {
        ValidateOffset(offset, length);
        return System.Text.Encoding.ASCII.GetString(_data, offset, length);
    }

    // -------------------------------------------------------------------------
    // Write helpers
    // -------------------------------------------------------------------------

    public void WriteByte(int offset, byte value)
    {
        ValidateOffset(offset, 1);
        _data[offset] = value;
        _isDirty = true;
    }

    public void WriteUInt16Le(int offset, ushort value)
    {
        ValidateOffset(offset, 2);
        _data[offset]     = (byte)(value & 0xFF);
        _data[offset + 1] = (byte)(value >> 8);
        _isDirty = true;
    }

    public void WriteBytes(int offset, byte[] bytes)
    {
        ValidateOffset(offset, bytes.Length);
        Array.Copy(bytes, 0, _data, offset, bytes.Length);
        _isDirty = true;
    }

    // -------------------------------------------------------------------------
    // Snapshot / save
    // -------------------------------------------------------------------------

    /// <summary>Return a full copy of the current ROM data (for patching or saving).</summary>
    public byte[] ToArray()
    {
        var copy = new byte[_data.Length];
        Array.Copy(_data, copy, _data.Length);
        return copy;
    }

    /// <summary>Save the current buffer to its original file path.</summary>
    public void SaveToFile()
    {
        File.WriteAllBytes(FilePath, _data);
        _isDirty = false;
    }

    /// <summary>Save the current buffer to a new path (Save As) and update FilePath.</summary>
    public void SaveToFile(string path)
    {
        File.WriteAllBytes(path, _data);
        FilePath = path;
        _isDirty = false;
    }

    /// <summary>
    /// Overwrite the entire buffer with new data of equal length.
    /// Used by the Patch Manager to apply an IPS patch in-place.
    /// </summary>
    public void ReplaceAll(byte[] newData)
    {
        if (newData.Length != _data.Length)
            throw new InvalidOperationException(
                $"New data length {newData.Length} does not match ROM size {_data.Length}. " +
                "ROM expansion is not supported in this version.");
        Array.Copy(newData, _data, newData.Length);
        _isDirty = true;
    }

    /// <summary>Mark the buffer clean (e.g., after exporting a patch).</summary>
    public void ClearDirty() => _isDirty = false;

    // -------------------------------------------------------------------------
    // Utilities
    // -------------------------------------------------------------------------

    /// <summary>
    /// Search for a byte pattern in the ROM. Returns all matching file offsets.
    /// Useful for reverse-engineering string locations.
    /// </summary>
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

    private void ValidateOffset(int offset, int count)
    {
        if (offset < 0 || offset + count > _data.Length)
            throw new ArgumentOutOfRangeException(nameof(offset),
                $"Offset 0x{offset:X6} + {count} bytes exceeds ROM size 0x{_data.Length:X6}.");
    }
}
