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
/// </summary>
public static class IpsHandler
{
    private static readonly byte[] IpsMagic = "PATCH"u8.ToArray();
    private static readonly byte[] IpsEof   = "EOF"u8.ToArray();

    // -------------------------------------------------------------------------
    // Apply
    // -------------------------------------------------------------------------

    /// <summary>
    /// Apply an IPS patch file to a copy of the ROM buffer.
    /// The original RomBuffer is NOT modified.
    /// Returns the patched bytes.
    /// </summary>
    public static byte[] ApplyPatch(RomBuffer rom, string patchPath)
    {
        byte[] patchData = File.ReadAllBytes(patchPath);
        byte[] romCopy   = rom.ToArray();

        ValidateMagic(patchData);

        int pos = 5; // skip "PATCH"
        while (pos + 3 <= patchData.Length)
        {
            // Check for EOF marker
            if (patchData[pos] == 'E' && patchData[pos + 1] == 'O' && patchData[pos + 2] == 'F')
                break;

            // Need 5 bytes minimum: 3 for offset + 2 for size
            if (pos + 5 > patchData.Length) break;

            int offset = (patchData[pos] << 16) | (patchData[pos + 1] << 8) | patchData[pos + 2];
            pos += 3;

            int size = (patchData[pos] << 8) | patchData[pos + 1];
            pos += 2;

            if (size == 0)
            {
                // RLE record: needs 2 bytes for run length + 1 byte for fill value
                if (pos + 3 > patchData.Length) break;
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
                if (pos + size > patchData.Length) break;
                EnsureCapacity(ref romCopy, offset + size);
                Array.Copy(patchData, pos, romCopy, offset, size);
                pos += size;
            }
        }

        return romCopy;
    }

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

        byte[] modified = modifiedRom.ToArray();

        using var stream = new FileStream(outputPatchPath, FileMode.Create, FileAccess.Write);
        using var writer = new BinaryWriter(stream);

        writer.Write(IpsMagic);

        // Walk both arrays and collect changed regions
        int i = 0;
        int len = Math.Min(original.Length, modified.Length);

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
            writer.Write((byte)(start >> 16));
            writer.Write((byte)(start >> 8));
            writer.Write((byte)(start));
            writer.Write((byte)(chunkLen >> 8));
            writer.Write((byte)(chunkLen));
            writer.Write(modified, start, chunkLen);

            i = start + chunkLen;
        }

        writer.Write(IpsEof);
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
