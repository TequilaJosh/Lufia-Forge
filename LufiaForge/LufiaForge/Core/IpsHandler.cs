using System.IO;

namespace LufiaForge.Core;

// ips (international patching system) patches, applying and exporting.
// spec: https://zerosoft.zophar.net/ips.php
//
// ips format:
//   header:  "PATCH" (5 bytes)
//   records: [3-byte offset][2-byte size][size bytes of data]
//            size 0 = RLE record [2-byte run length][1 byte fill value]
//   eof:     "EOF" (3 bytes)
//   optional truncate extension: 3 more bytes after EOF = final file size (lunar ips / flips). we write it whenever the
//   patched rom's size differs from the original (like a rom expanded to 2 MB), and honour it when applying
public static class IpsHandler
{
    private static readonly byte[] IpsMagic = "PATCH"u8.ToArray();
    private static readonly byte[] IpsEof   = "EOF"u8.ToArray();
    private const int EofOffset = 0x454F46;   // "EOF" read as a record's offset

    // apply

    // applies an ips patch file to a copy of the rom buffer and returns the patched bytes. the RomBuffer itself isn't touched
    public static byte[] ApplyPatch(RomBuffer rom, string patchPath) => ApplyPatch(rom.ToArray(), File.ReadAllBytes(patchPath));

    // applies ips patch bytes to a rom image and returns the patched copy (the image passed in stays the same).
    // a patch that's cut off (a record running past its end, or no "EOF" at the end) throws and changes nothing,
    // half a patch would leave the game broken
    public static byte[] ApplyPatch(byte[] rom, byte[] patchData)
    {
        byte[] romCopy   = (byte[])rom.Clone();

        ValidateMagic(patchData);

        int position = 5; // skip "PATCH"
        bool ended = false;
        while (position + 3 <= patchData.Length)
        {
            // the EOF marker (and the optional final size after it)
            if (patchData[position] == 'E' && patchData[position + 1] == 'O' && patchData[position + 2] == 'F')
            {
                if (position + 6 <= patchData.Length)
                {
                    int finalSize = (patchData[position + 3] << 16) | (patchData[position + 4] << 8) | patchData[position + 5];
                    if (finalSize > 0) Array.Resize(ref romCopy, finalSize);
                }
                ended = true;
                break;
            }

            // need at least 5 bytes, 3 for the offset + 2 for the size
            if (position + 5 > patchData.Length) throw CutOff(position);

            int offset = (patchData[position] << 16) | (patchData[position + 1] << 8) | patchData[position + 2];
            position += 3;

            int size = (patchData[position] << 8) | patchData[position + 1];
            position += 2;

            if (size == 0)
            {
                // RLE record: 2 bytes run length + 1 byte fill value
                if (position + 3 > patchData.Length) throw CutOff(position - 5);
                int runLength = (patchData[position] << 8) | patchData[position + 1];
                position += 2;
                byte fillByte = patchData[position++];
                EnsureCapacity(ref romCopy, offset + runLength);
                for (int i = 0; i < runLength; i++)
                    romCopy[offset + i] = fillByte;
            }
            else
            {
                // normal record: size bytes of patch data
                if (position + size > patchData.Length) throw CutOff(position - 5);
                EnsureCapacity(ref romCopy, offset + size);
                Array.Copy(patchData, position, romCopy, offset, size);
                position += size;
            }
        }
        if (!ended) throw CutOff(position);

        return romCopy;
    }

    private static InvalidDataException CutOff(int byteIndex) =>
        new($"The patch file is cut off (it ends inside its data at byte {byteIndex:N0}, or without the \"EOF\" that closes an IPS patch), " +
            "so it wasn't applied. Download it again.");

    // export

    // exports an ips patch with the differences between the original file and the current RomBuffer
    public static void ExportPatch(RomBuffer modifiedRom, string originalRomPath, string outputPatchPath)
    {
        byte[] original = File.ReadAllBytes(originalRomPath);
        // strip the smc header off the original if it has one
        if (original.Length % 1024 == Lufia1Constants.SmcHeaderSize)
            original = original[Lufia1Constants.SmcHeaderSize..];

        // the way a save writes it: checksum fixed and, on an 8 MB rom, the copy of the first bank renewed
        var fixedRom = new RomBuffer(modifiedRom.ToArray(), modifiedRom.FilePath);
        fixedRom.FixChecksum();
        byte[] modified = fixedRom.ToArray();

        using var stream = new FileStream(outputPatchPath, FileMode.Create, FileAccess.Write);
        using var writer = new BinaryWriter(stream);

        writer.Write(IpsMagic);

        // walk both arrays and collect the changed regions
        int i = 0;
        int commonLength = Math.Min(original.Length, modified.Length);
        // a record can't start at 0x454F46: its offset bytes read "EOF" and patchers stop there (an 8 MB rom gets that
        // far). so the record starts a byte earlier: those two bytes as data, then the rest from 0x454F47
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

        while (i < commonLength)
        {
            if (original[i] == modified[i]) { i++; continue; }

            // found a difference, collect the run of changed bytes (up to 65535)
            int start    = i;
            int maxChunk = Math.Min(commonLength - start, 0xFFFF);
            int chunkLen = 0;

            while (chunkLen < maxChunk && original[start + chunkLen] != modified[start + chunkLen])
                chunkLen++;

            Record(start, chunkLen);

            i = start + chunkLen;
        }

        // bytes past the end of the original (an expanded rom): runs of one value as RLE records, the rest as data.
        // everything gets written, the FF filler too, so any patcher builds exactly the same rom
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
