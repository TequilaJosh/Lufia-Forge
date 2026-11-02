using System.IO;

namespace LufiaForge.Core;

/// <summary>
/// IPS (International Patching System) patch apply and export.
/// Spec: https://zerosoft.zophar.net/ips.php
///
/// IPS format:
///   Header:  "PATCH" (5 bytes)
///   Records: [3-byte offset][2-byte size][size bytes of data]
///            If size == 0: RLE record [2-byte run length][1 byte fill value]
///   EOF:     "EOF" (3 bytes)
///   Optional truncate extension: 3 more bytes after EOF = final file size (Lunar IPS / Flips). Written whenever the
///   patched ROM's size differs from the original (e.g. a ROM expanded to 2 MB), and honoured when applying.
/// </summary>
public static class IpsHandler
{
    private static readonly byte[] IpsMagic = "PATCH"u8.ToArray();
    private static readonly byte[] IpsEof   = "EOF"u8.ToArray();
    private const int EofOffset = 0x454F46;   // "EOF" read as a record's offset

    // -------------------------------------------------------------------------
    // Apply
    // -------------------------------------------------------------------------

    /// <summary>
    /// Apply an IPS patch file to a copy of the ROM buffer.
    /// The original RomBuffer is NOT modified.
    /// Returns the patched bytes.
    /// </summary>
    public static byte[] ApplyPatch(RomBuffer rom, string patchPath) => ApplyPatch(rom.ToArray(), File.ReadAllBytes(patchPath));

    /// <summary>Apply IPS patch bytes to a ROM image; returns the patched copy (the image passed in is not changed).
    /// A patch that is cut off (a record running past its end, or no "EOF" at the end) throws and changes nothing: half
    /// a patch would leave the game broken.</summary>
    public static byte[] ApplyPatch(byte[] rom, byte[] patchData)
    {
        byte[] romCopy   = (byte[])rom.Clone();

        ValidateMagic(patchData);

        int pos = 5; // skip "PATCH"
        bool ended = false;
        while (pos + 3 <= patchData.Length)
        {
            // Check for EOF marker (and the optional final-size extension after it)
            if (patchData[pos] == 'E' && patchData[pos + 1] == 'O' && patchData[pos + 2] == 'F')
            {
                if (pos + 6 <= patchData.Length)
                {
                    int finalSize = (patchData[pos + 3] << 16) | (patchData[pos + 4] << 8) | patchData[pos + 5];
                    if (finalSize > 0) Array.Resize(ref romCopy, finalSize);
                }
                ended = true;
                break;
            }

            // Need 5 bytes minimum: 3 for offset + 2 for size
            if (pos + 5 > patchData.Length) throw CutOff(pos);

            int offset = (patchData[pos] << 16) | (patchData[pos + 1] << 8) | patchData[pos + 2];
            pos += 3;

            int size = (patchData[pos] << 8) | patchData[pos + 1];
            pos += 2;

            if (size == 0)
            {
                // RLE record: needs 2 bytes for run length + 1 byte for fill value
                if (pos + 3 > patchData.Length) throw CutOff(pos - 5);
                int runLength = (patchData[pos] << 8) | patchData[pos + 1];
                pos += 2;
                byte fillByte = patchData[pos++];
                EnsureCapacity(ref romCopy, offset + runLength);
                for (int i = 0; i < runLength; i++)
                    romCopy[offset + i] = fillByte;
            }
            else
            {
                // Normal record: needs 'size' bytes of patch data
                if (pos + size > patchData.Length) throw CutOff(pos - 5);
                EnsureCapacity(ref romCopy, offset + size);
                Array.Copy(patchData, pos, romCopy, offset, size);
                pos += size;
            }
        }
        if (!ended) throw CutOff(pos);

        return romCopy;
    }

    private static InvalidDataException CutOff(int at) =>
        new($"The patch file is cut off (it ends inside its data at byte {at:N0}, or without the \"EOF\" that closes an IPS patch), " +
            "so it wasn't applied. Download it again.");

    // -------------------------------------------------------------------------
    // Export
    // -------------------------------------------------------------------------

    /// <summary>
    /// Export an IPS patch representing the diff between the original file and the current RomBuffer.
    /// </summary>
    public static void ExportPatch(RomBuffer modifiedRom, string originalRomPath, string outputPatchPath)
    {
        byte[] original = File.ReadAllBytes(originalRomPath);
        // Strip SMC header from original if present
        if (original.Length % 1024 == Lufia1Constants.SmcHeaderSize)
            original = original[Lufia1Constants.SmcHeaderSize..];

        // as a save writes it: the checksum fixed and, on an 8 MB ROM, the copy of the first bank renewed
        var fixedRom = new RomBuffer(modifiedRom.ToArray(), modifiedRom.FilePath);
        fixedRom.FixChecksum();
        byte[] modified = fixedRom.ToArray();

        using var stream = new FileStream(outputPatchPath, FileMode.Create, FileAccess.Write);
        using var writer = new BinaryWriter(stream);

        writer.Write(IpsMagic);

        // Walk both arrays and collect changed regions
        int i = 0;
        int len = Math.Min(original.Length, modified.Length);
        // A record can't start at 0x454F46: its offset bytes read "EOF", and patchers stop there (an 8 MB ROM reaches
        // it). The record starts a byte earlier instead: those two bytes as data, then the rest from 0x454F47.
        bool SkipEof(ref int start, ref int count)
        {
            if (start != EofOffset) return false;
            writer.Write(new byte[] { 0x45, 0x4F, 0x45, 0x00, 0x02 });   // offset 0x454F45, 2 bytes
            writer.Write(modified, EofOffset - 1, 2);
            start++; count--;
            return count == 0;
        }
        void Record(int start, int count)
        {
            if (SkipEof(ref start, ref count)) return;
            writer.Write((byte)(start >> 16)); writer.Write((byte)(start >> 8)); writer.Write((byte)start);
            writer.Write((byte)(count >> 8)); writer.Write((byte)count);
            writer.Write(modified, start, count);
        }
        void Rle(int start, int count, byte value)
        {
            if (SkipEof(ref start, ref count)) return;
            writer.Write((byte)(start >> 16)); writer.Write((byte)(start >> 8)); writer.Write((byte)start);
            writer.Write((byte)0); writer.Write((byte)0);
            writer.Write((byte)(count >> 8)); writer.Write((byte)count);
            writer.Write(value);
        }

        while (i < len)
        {
            if (original[i] == modified[i]) { i++; continue; }

            // Found a difference - collect the run of changed bytes (up to 65535)
            int start    = i;
            int maxChunk = Math.Min(len - start, 0xFFFF);
            int chunkLen = 0;

            while (chunkLen < maxChunk && original[start + chunkLen] != modified[start + chunkLen])
                chunkLen++;

            // Write IPS record: [3-byte offset][2-byte size][data]
            Record(start, chunkLen);

            i = start + chunkLen;
        }

        // Bytes past the end of the original (an expanded ROM): runs of one value as RLE records, the rest as data.
        // Everything is written, including the FF filler, so any patcher builds exactly the same ROM.
        while (i < modified.Length)
        {
            int run = 1;
            while (i + run < modified.Length && run < 0xFFFF && modified[i + run] == modified[i]) run++;
            if (run >= 8) { Rle(i, run, modified[i]); i += run; continue; }
            int start = i, count = 0;
            while (i < modified.Length && count < 0xFFFF)
            {
                int same = 1;
                while (i + same < modified.Length && same < 8 && modified[i + same] == modified[i]) same++;
                if (same >= 8) break;
                i++; count++;
            }
            if (count > 0) Record(start, count);
        }

        writer.Write(IpsEof);
        if (modified.Length != original.Length)
        {
            // truncate/size extension: the patched file is exactly this long
            writer.Write((byte)(modified.Length >> 16)); writer.Write((byte)(modified.Length >> 8)); writer.Write((byte)modified.Length);
        }
    }

    // -------------------------------------------------------------------------
    // Helpers
    // -------------------------------------------------------------------------

    private static void ValidateMagic(byte[] data)
    {
        if (data.Length < 5 || data[0] != 'P' || data[1] != 'A' ||
            data[2] != 'T' || data[3] != 'C' || data[4] != 'H')
            throw new InvalidDataException("Not a valid IPS patch file (missing PATCH header).");
    }

    private static void EnsureCapacity(ref byte[] array, int requiredLength)
    {
        if (requiredLength > array.Length)
            Array.Resize(ref array, requiredLength);
    }
}
