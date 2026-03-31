using System.IO;

namespace LufiaForge.Core;

public enum RomLoadResult
{
    Success,
    FileNotFound,
    FileTooSmall,
    ChecksumMismatch,
    WrongGame,
    UnknownError
}

public record RomLoadReport(
    RomLoadResult Result,
    RomBuffer?    Buffer,
    string        Message,
    bool          HadSmcHeader,
    string        DetectedTitle,
    int           RomSizeBytes
);

/// <summary>
/// Handles opening a ROM file, stripping optional SMC headers,
/// validating that this is Lufia 1 US, and returning a RomBuffer.
/// </summary>
public static class RomLoader
{
    public static RomLoadReport Load(string filePath)
    {
        try
        {
            if (!File.Exists(filePath))
                return Fail(RomLoadResult.FileNotFound, "File not found.", filePath);

            byte[] raw = File.ReadAllBytes(filePath);

            // Detect and strip 512-byte SMC copier header
            bool hadSmcHeader = false;
            if (raw.Length % 1024 == Lufia1Constants.SmcHeaderSize)
            {
                raw          = raw[Lufia1Constants.SmcHeaderSize..];
                hadSmcHeader = true;
            }

            if (raw.Length < Lufia1Constants.ExpectedRomSize)
                return Fail(RomLoadResult.FileTooSmall,
                    $"ROM is {raw.Length} bytes after header strip. Expected at least {Lufia1Constants.ExpectedRomSize}.",
                    filePath);

            // Read the SNES internal header title (21 chars at 0x7FC0)
            string title = ReadAscii(raw, Lufia1Constants.SnesHeaderOffset, 21).TrimEnd();

            if (!title.StartsWith("LUFIA", StringComparison.OrdinalIgnoreCase))
                return new RomLoadReport(
                    RomLoadResult.WrongGame,
                    null,
                    $"ROM title \"{title}\" does not match Lufia 1. Wrong game or wrong region?",
                    hadSmcHeader,
                    title,
                    raw.Length);

            // Validate SNES checksum
            ushort storedChecksum    = ReadUInt16Le(raw, Lufia1Constants.SnesChecksumOffset);
            ushort storedComplement  = ReadUInt16Le(raw, Lufia1Constants.SnesComplementOffset);
            ushort computedChecksum  = ComputeChecksum(raw);

            bool checksumOk = (storedChecksum + storedComplement == 0xFFFF) &&
                              (storedChecksum == computedChecksum);

            string message = checksumOk
                ? $"Loaded successfully. Title: \"{title}\". SMC header: {hadSmcHeader}."
                : $"Loaded with checksum warning (stored: 0x{storedChecksum:X4}, computed: 0x{computedChecksum:X4}). " +
                  $"ROM may be modified or a bad dump.";

            var buffer = new RomBuffer(raw, filePath);
            return new RomLoadReport(
                checksumOk ? RomLoadResult.Success : RomLoadResult.ChecksumMismatch,
                buffer,
                message,
                hadSmcHeader,
                title,
                raw.Length);
        }
        catch (Exception ex)
        {
            return new RomLoadReport(
                RomLoadResult.UnknownError,
                null,
                $"Unexpected error: {ex.Message}",
                false,
                string.Empty,
                0);
        }
    }

    // -------------------------------------------------------------------------
    // Private helpers
    // -------------------------------------------------------------------------

    private static RomLoadReport Fail(RomLoadResult result, string message, string path) =>
        new(result, null, message, false, string.Empty, 0);

    private static string ReadAscii(byte[] data, int offset, int length) =>
        System.Text.Encoding.ASCII.GetString(data, offset, length);

    private static ushort ReadUInt16Le(byte[] data, int offset) =>
        (ushort)(data[offset] | (data[offset + 1] << 8));

    /// <summary>
    /// SNES checksum: sum of all bytes in the ROM (wrapping at 16 bits).
    /// For LoROM 1MB, the checksum range covers the entire file.
    /// </summary>
    private static ushort ComputeChecksum(byte[] rom)
    {
        uint sum = 0;
        foreach (byte b in rom) sum += b;
        // Subtract the stored checksum and complement bytes themselves (they're included in the range
        // but the SNES spec stores 0xFF/0x00 there during calculation).
        // Simple approach: just sum everything - it's close enough for validation purposes.
        return (ushort)(sum & 0xFFFF);
    }
}
