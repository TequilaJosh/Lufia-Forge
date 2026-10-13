using System.IO;

namespace LufiaForge.Core;

/// <summary>
/// Fan patches built into Lufia Forge (Patches\*.ips, embedded), so users don't have to hunt them down:
/// Lufia &amp; the Fortress of Doom Restored v3.2 (FlamePurge and others) with Always Retry Mode (Justin3009), and the
/// Lufia Recolor patch 1.0 with its Just the Shadows part. Their readmes ship in licenses\patches.
/// Restored rewrites much of the game (and grows it to 1.5 MB), so it only goes on a clean ROM; the others are small
/// and come out again (Original_bytes.ips holds the clean ROM's bytes they change).
/// </summary>
public static class CommunityPatches
{
    /// <summary>CRC32 of the clean USA ROM (unheadered), the one Restored is made for.</summary>
    public const uint CleanCrc = 0x5E1AA1A6;
    /// <summary>Header checksum bytes (patches write them; Lufia Forge recomputes them when saving).</summary>
    private const int ChecksumStart = 0x7FDC, ChecksumEnd = 0x7FE0;

    private sealed record Rec(int Offset, byte[] Data);

    private static readonly Dictionary<string, List<Rec>> Cache = new();

    private static byte[] Load(string name)
    {
        using var s = typeof(CommunityPatches).Assembly.GetManifestResourceStream("Patches." + name)
                      ?? throw new InvalidOperationException($"Built-in patch {name} is missing.");
        using var ms = new MemoryStream();
        s.CopyTo(ms);
        return ms.ToArray();
    }

    /// <summary>The patch's records (RLE runs expanded), without the header checksum.</summary>
    private static List<Rec> Records(string name)
    {
        lock (Cache)
        {
            if (Cache.TryGetValue(name, out var r)) return r;
            byte[] d = Load(name);
            r = new List<Rec>();
            int i = 5;
            while (i + 3 <= d.Length && !(d[i] == 'E' && d[i + 1] == 'O' && d[i + 2] == 'F'))
            {
                int o = (d[i] << 16) | (d[i + 1] << 8) | d[i + 2], n = (d[i + 3] << 8) | d[i + 4];
                i += 5;
                byte[] data;
                if (n == 0) { data = Enumerable.Repeat(d[i + 2], (d[i] << 8) | d[i + 1]).ToArray(); i += 3; }
                else { data = d[i..(i + n)]; i += n; }
                if (o >= ChecksumStart && o + data.Length <= ChecksumEnd) continue;
                r.Add(new Rec(o, data));
            }
            Cache[name] = r;
            return r;
        }
    }

    private static bool Has(RomBuffer rom, List<Rec> recs) =>
        recs.All(r => r.Offset + r.Data.Length <= rom.Length && rom.ReadBytes(r.Offset, r.Data.Length).AsSpan().SequenceEqual(r.Data));

    /// <summary>True when every byte the patch changes still holds the clean ROM's value (or, with
    /// <paramref name="alsoOk"/>, that patch's value).</summary>
    private static bool Untouched(RomBuffer rom, List<Rec> recs, List<Rec>? alsoOk = null)
    {
        var clean = Clean();
        Dictionary<int, byte>? ok = alsoOk == null ? null : Bytes(alsoOk);
        foreach (var r in recs)
            for (int k = 0; k < r.Data.Length; k++)
            {
                int o = r.Offset + k;
                if (o >= rom.Length) return false;
                byte b = rom.ReadByte(o);
                if (clean.TryGetValue(o, out byte c) && b == c) continue;
                if (ok != null && ok.TryGetValue(o, out byte v) && b == v) continue;
                return false;
            }
        return true;
    }

    private static Dictionary<int, byte> Bytes(List<Rec> recs)
    {
        var d = new Dictionary<int, byte>();
        foreach (var r in recs) for (int k = 0; k < r.Data.Length; k++) d[r.Offset + k] = r.Data[k];
        return d;
    }

    private static Dictionary<int, byte>? _clean;
    private static Dictionary<int, byte> Clean() => _clean ??= Bytes(Records("Original_bytes.ips"));

    private static void Write(RomBuffer rom, List<Rec> recs)
    {
        foreach (var r in recs) rom.WriteBytes(r.Offset, r.Data);
        rom.FixChecksum();
    }

    /// <summary>Puts the clean ROM's bytes back wherever the patch wrote.</summary>
    private static void Unwrite(RomBuffer rom, List<Rec> recs)
    {
        var clean = Clean();
        foreach (var r in recs)
            for (int k = 0; k < r.Data.Length; k++) rom.WriteByte(r.Offset + k, clean[r.Offset + k]);
        rom.FixChecksum();
    }

    // ---- Lufia Restored ------------------------------------------------------------------------

    private const string RestoredFile = "Lufia1Restored_v32.ips";

    public static bool IsCleanRom(RomBuffer rom) => rom.Length == 0x100000 && Crc32(rom.ToArray()) == CleanCrc;

    /// <summary>Restored is in (its item descriptions, or most of its records: later edits change some).</summary>
    public static bool RestoredApplied(RomBuffer rom)
    {
        if (rom.Length < 0x180000) return false;
        if (ItemDescriptions.Find(rom) != null) return true;
        var recs = Records(RestoredFile);
        int same = recs.Count(r => r.Offset + r.Data.Length <= rom.Length && rom.ReadBytes(r.Offset, r.Data.Length).AsSpan().SequenceEqual(r.Data));
        return same * 10 >= recs.Count * 9;
    }

    /// <summary>Applies Restored to the whole ROM (it grows to 1.5 MB). Only on a clean ROM.</summary>
    public static void ApplyRestored(RomBuffer rom)
    {
        if (!IsCleanRom(rom)) throw new InvalidOperationException("Restored goes on a clean, unedited ROM.");
        rom.ReplaceAll(IpsHandler.ApplyPatch(rom.ToArray(), Load(RestoredFile)));
        rom.FixChecksum();
    }

    // ---- Always Retry Mode -----------------------------------------------------------------------

    private const string RetryFile = "AlwaysRetryMode.ips";
    public static bool RetryApplied(RomBuffer rom) => Has(rom, Records(RetryFile));
    public static bool RetryOriginal(RomBuffer rom) => Untouched(rom, Records(RetryFile));
    public static void ApplyRetry(RomBuffer rom) => Write(rom, Records(RetryFile));
    public static void RemoveRetry(RomBuffer rom) => Unwrite(rom, Records(RetryFile));

    // ---- Lufia Recolor ---------------------------------------------------------------------------

    private const string RecolorFile = "LufiaRecolor_1.0.ips", ShadowsFile = "JustTheShadows.ips";

    public static bool RecolorApplied(RomBuffer rom) => Has(rom, Records(RecolorFile));
    /// <summary>The colours it changes are the game's own (Just the Shadows may be in: Recolor includes it).</summary>
    public static bool RecolorOriginal(RomBuffer rom) => Untouched(rom, Records(RecolorFile), Records(ShadowsFile));
    public static void ApplyRecolor(RomBuffer rom) => Write(rom, Records(RecolorFile));
    public static void RemoveRecolor(RomBuffer rom) => Unwrite(rom, Records(RecolorFile));

    public static bool ShadowsApplied(RomBuffer rom) => Has(rom, Records(ShadowsFile)) && !RecolorApplied(rom);
    public static bool ShadowsOriginal(RomBuffer rom) => Untouched(rom, Records(ShadowsFile));
    public static void ApplyShadows(RomBuffer rom) => Write(rom, Records(ShadowsFile));
    public static void RemoveShadows(RomBuffer rom) => Unwrite(rom, Records(ShadowsFile));

    // ---------------------------------------------------------------------------------------------

    private static readonly uint[] CrcTable = Enumerable.Range(0, 256).Select(n =>
    {
        uint c = (uint)n;
        for (int k = 0; k < 8; k++) c = (c & 1) != 0 ? 0xEDB88320 ^ (c >> 1) : c >> 1;
        return c;
    }).ToArray();

    public static uint Crc32(byte[] data)
    {
        uint c = 0xFFFFFFFF;
        foreach (byte b in data) c = CrcTable[(c ^ b) & 0xFF] ^ (c >> 8);
        return c ^ 0xFFFFFFFF;
    }
}
