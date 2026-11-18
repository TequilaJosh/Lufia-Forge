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

// opens a rom file, strips an smc header if there is one, checks it's lufia 1 or lufia 2 (us) and returns a RomBuffer
// (its Game says which one)
public static class RomLoader
{
    public static RomLoadReport Load(string filePath)
    {
        try
        {
            if (!File.Exists(filePath))
                return Fail(RomLoadResult.FileNotFound, "File not found.", filePath);

            byte[] raw = File.ReadAllBytes(filePath);

            // find and strip the 512-byte smc copier header
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

            // the snes internal header title (21 chars at 0x7FC0)
            string title = ReadAscii(raw, Lufia1Constants.SnesHeaderOffset, 21).TrimEnd();

            var game = GameDetect.Detect(raw);
            if (game == GameKind.Unknown)
                return new RomLoadReport(
                    RomLoadResult.WrongGame,
                    null,
                    GameDetect.WhyNot(raw),
                    hadSmcHeader,
                    title,
                    raw.Length);

            // check the snes checksum
            ushort storedChecksum    = ReadUInt16Le(raw, Lufia1Constants.SnesChecksumOffset);
            ushort storedComplement  = ReadUInt16Le(raw, Lufia1Constants.SnesComplementOffset);
            ushort computedChecksum  = ComputeChecksum(raw);

            bool checksumOk = (storedChecksum + storedComplement == 0xFFFF) &&
                              (storedChecksum == computedChecksum);

            string message = checksumOk
                ? $"Loaded successfully: {GameDetect.Name(game)}. Title: \"{title}\". SMC header: {hadSmcHeader}."
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

    private static RomLoadReport Fail(RomLoadResult result, string message, string path) =>
        new(result, null, message, false, string.Empty, 0);

    private static string ReadAscii(byte[] data, int offset, int length) =>
        System.Text.Encoding.ASCII.GetString(data, offset, length);

    private static ushort ReadUInt16Le(byte[] data, int offset) =>
        (ushort)(data[offset] | (data[offset + 1] << 8));

    // snes checksum: the sum of all bytes in the rom (wrapping at 16 bits).
    // for a 1 MB lorom the checksum covers the whole file
    private static ushort ComputeChecksum(byte[] rom)
    {
        // the stored checksum and complement add up to the same as FFFF/0000, so they can stay in the sum
        return RomBuffer.SnesChecksum(rom);
    }
}
