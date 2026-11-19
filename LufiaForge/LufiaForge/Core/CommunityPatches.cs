using System.IO;

namespace LufiaForge.Core;

// fan patches built into lufia forge (Patches\*.ips, embedded) so people don't have to hunt them down:
// - Lufia & the Fortress of Doom Restored v3.2 (FlamePurge and others) with Always Retry Mode (Justin3009)
// - the Lufia Recolor patch 1.0 with its Just the Shadows part
// their readmes ship in licenses\patches.
// restored rewrites a lot of the game (and grows it to 1.5 MB) so it only goes on a clean rom. the others are small
// and can come out again (Original_bytes.ips holds the clean rom's bytes they change).
// for lufia 2: Frue Lufia, Spekkio Lufia and Kureji Lufia v8.2 and The Wrath of Foomy (the Frue Lufia project, built on
// relnqshd's patches and lots of other people's work, readmes in licenses\patches), each one a whole-game patch for a
// clean rom
public static class CommunityPatches
{
    // crc32 of the clean usa rom (unheadered), the one restored is made for
    public const uint CleanCrc = 0x5E1AA1A6;
    // header checksum bytes. patches write them, we recompute them on save anyway
    private const int ChecksumStart = 0x7FDC, ChecksumEnd = 0x7FE0;

    private sealed record Rec(int Offset, byte[] Data);

    private static readonly Dictionary<string, List<Rec>> Cache = new();

    private static byte[] Load(string name)
    {
        using var stream = typeof(CommunityPatches).Assembly.GetManifestResourceStream("Patches." + name)
                      ?? throw new InvalidOperationException($"Built-in patch {name} is missing.");
        using var buffer = new MemoryStream();
        stream.CopyTo(buffer);
        return buffer.ToArray();
    }

    // the patch's records with RLE runs expanded, minus the header checksum
    private static List<Rec> Records(string name)
    {
        lock (Cache)
        {
            if (Cache.TryGetValue(name, out var records)) return records;
            byte[] patch = Load(name);
            records = new List<Rec>();
            int i = 5;
            while (i + 3 <= patch.Length && !(patch[i] == 'E' && patch[i + 1] == 'O' && patch[i + 2] == 'F'))
            {
                int offset = (patch[i] << 16) | (patch[i + 1] << 8) | patch[i + 2], size = (patch[i + 3] << 8) | patch[i + 4];
                i += 5;
                byte[] data;
                if (size == 0) { data = Enumerable.Repeat(patch[i + 2], (patch[i] << 8) | patch[i + 1]).ToArray(); i += 3; }
                else { data = patch[i..(i + size)]; i += size; }
                if (offset >= ChecksumStart && offset + data.Length <= ChecksumEnd) continue;
                records.Add(new Rec(offset, data));
            }
            Cache[name] = records;
            return records;
        }
    }

    private static bool Has(RomBuffer rom, List<Rec> records) =>
        records.All(r => r.Offset + r.Data.Length <= rom.Length && rom.ReadBytes(r.Offset, r.Data.Length).AsSpan().SequenceEqual(r.Data));

    // true when every byte the patch changes still has the clean rom's value (or, with alsoOk, that patch's value)
    private static bool Untouched(RomBuffer rom, List<Rec> records, List<Rec>? alsoOk = null)
    {
        var clean = Clean();
        Dictionary<int, byte>? allowed = alsoOk == null ? null : Bytes(alsoOk);
        foreach (var record in records)
            for (int k = 0; k < record.Data.Length; k++)
            {
                int offset = record.Offset + k;
                if (offset >= rom.Length) return false;
                byte current = rom.ReadByte(offset);
                if (clean.TryGetValue(offset, out byte cleanByte) && current == cleanByte) continue;
                if (allowed != null && allowed.TryGetValue(offset, out byte allowedByte) && current == allowedByte) continue;
                return false;
            }
        return true;
    }

    private static Dictionary<int, byte> Bytes(List<Rec> records)
    {
        var bytesByOffset = new Dictionary<int, byte>();
        foreach (var record in records) for (int k = 0; k < record.Data.Length; k++) bytesByOffset[record.Offset + k] = record.Data[k];
        return bytesByOffset;
    }

    private static Dictionary<int, byte>? _clean;
    private static Dictionary<int, byte> Clean() => _clean ??= Bytes(Records("Original_bytes.ips"));

    private static void Write(RomBuffer rom, List<Rec> records)
    {
        foreach (var record in records) rom.WriteBytes(record.Offset, record.Data);
        rom.FixChecksum();
    }

    // puts the clean rom's bytes back everywhere the patch wrote
    private static void Unwrite(RomBuffer rom, List<Rec> records)
    {
        var clean = Clean();
        foreach (var record in records)
            for (int k = 0; k < record.Data.Length; k++) rom.WriteByte(record.Offset + k, clean[record.Offset + k]);
        rom.FixChecksum();
    }

    // lufia restored

    private const string RestoredFile = "Lufia1Restored_v32.ips";

    public static bool IsCleanRom(RomBuffer rom) => rom.Length == 0x100000 && Crc32(rom.ToArray()) == CleanCrc;

    // restored is in: its item descriptions are there, or most of its records are (later edits change some)
    public static bool RestoredApplied(RomBuffer rom)
    {
        if (rom.Length < 0x180000) return false;
        if (ItemDescriptions.Find(rom) != null) return true;
        var records = Records(RestoredFile);
        int matching = records.Count(r => r.Offset + r.Data.Length <= rom.Length && rom.ReadBytes(r.Offset, r.Data.Length).AsSpan().SequenceEqual(r.Data));
        return matching * 10 >= records.Count * 9;
    }

    // applies restored to the whole rom (grows it to 1.5 MB). clean rom only
    public static void ApplyRestored(RomBuffer rom)
    {
        if (!IsCleanRom(rom)) throw new InvalidOperationException("Restored goes on a clean, unedited ROM.");
        rom.ReplaceAll(IpsHandler.ApplyPatch(rom.ToArray(), Load(RestoredFile)));
        rom.FixChecksum();
    }

    // always retry mode

    private const string RetryFile = "AlwaysRetryMode.ips";
    public static bool RetryApplied(RomBuffer rom) => Has(rom, Records(RetryFile));
    public static bool RetryOriginal(RomBuffer rom) => Untouched(rom, Records(RetryFile));
    public static void ApplyRetry(RomBuffer rom) { if (!RetryApplied(rom)) { Check(RetryOriginal(rom), "Always Retry Mode"); Write(rom, Records(RetryFile)); } }

    // the small patches only go where the bytes they change are still the clean rom's. written blind, one that went
    // over another hack (the dutch lufia, which has its code somewhere else) wrote into that hack's own bytes.
    // the patch manager doesn't offer them then, this stops any other caller from doing it
    private static void Check(bool original, string name)
    {
        if (!original) throw new InvalidOperationException($"The bytes {name} changes aren't the clean game's here (another patch or a hack changed them), so it can't be added safely.");
    }
    public static void RemoveRetry(RomBuffer rom) => Unwrite(rom, Records(RetryFile));

    // lufia recolor

    private const string RecolorFile = "LufiaRecolor_1.0.ips", ShadowsFile = "JustTheShadows.ips";

    public static bool RecolorApplied(RomBuffer rom) => Has(rom, Records(RecolorFile));
    // the colours it changes are still the game's own (just the shadows can be in, recolor includes it)
    public static bool RecolorOriginal(RomBuffer rom) => Untouched(rom, Records(RecolorFile), Records(ShadowsFile));
    public static void ApplyRecolor(RomBuffer rom) { if (!RecolorApplied(rom)) { Check(RecolorOriginal(rom), "Lufia Recolor"); Write(rom, Records(RecolorFile)); } }
    public static void RemoveRecolor(RomBuffer rom) => Unwrite(rom, Records(RecolorFile));

    public static bool ShadowsApplied(RomBuffer rom) => Has(rom, Records(ShadowsFile)) && !RecolorApplied(rom);
    public static bool ShadowsOriginal(RomBuffer rom) => Untouched(rom, Records(ShadowsFile));
    public static void ApplyShadows(RomBuffer rom) { if (!ShadowsApplied(rom) && !RecolorApplied(rom)) { Check(ShadowsOriginal(rom), "Just the Shadows"); Write(rom, Records(ShadowsFile)); } }
    public static void RemoveShadows(RomBuffer rom) => Unwrite(rom, Records(ShadowsFile));

    // lufia 2: frue, spekkio and kureji lufia v8.2, the wrath of foomy

    // crc32 of the clean lufia 2 (usa) rom, unheadered, the one the v8.2 patches are made for
    public const uint Lufia2CleanCrc = 0x20F2AC29;

    // the lufia 2 hacks: id and patch file (headerless ips, each one grows the rom to 4 MB)
    public static readonly (string Id, string File)[] Lufia2Hacks =
    {
        ("frue", "Lufia2_FrueLufia_v8-2.ips"), ("spekkio", "Lufia2_SpekkioLufia_v8-2.ips"),
        ("kureji", "Lufia2_KurejiLufia_v8-2.ips"), ("foomy", "Lufia2_WrathOfFoomy_v8-2.ips"),
    };

    private static uint _lastCrc;
    private static byte[]? _lastCrcData;

    public static bool IsCleanLufia2(RomBuffer rom)
    {
        if (rom.Length != 0x280000) return false;
        var data = rom.ToArray();
        if (_lastCrcData == null || !data.AsSpan().SequenceEqual(_lastCrcData)) { _lastCrc = Crc32(data); _lastCrcData = data; }
        return _lastCrc == Lufia2CleanCrc;
    }

    // how much of the patch's records the rom has (0 to 1)
    private static double Match(RomBuffer rom, string file)
    {
        var records = Records(file);
        int matching = records.Count(r => r.Offset + r.Data.Length <= rom.Length && rom.ReadBytes(r.Offset, r.Data.Length).AsSpan().SequenceEqual(r.Data));
        return records.Count == 0 ? 0 : matching / (double)records.Count;
    }

    // which hack is in the rom, or null. the best match, if at least 90% of its records are there
    // (the hacks share most of their bytes so only the best one counts)
    public static string? Lufia2HackIn(RomBuffer rom)
    {
        if (rom.Length < 0x400000) return null;
        var best = Lufia2Hacks.Select(h => (h.Id, Score: Match(rom, h.File))).OrderByDescending(h => h.Score).First();
        return best.Score >= 0.9 ? best.Id : null;
    }

    // applies hack id to the whole rom (grows it to 4 MB). clean rom only
    public static void ApplyLufia2Hack(RomBuffer rom, string id)
    {
        if (!IsCleanLufia2(rom)) throw new InvalidOperationException("These patches go on a clean, unedited Lufia II (USA) ROM.");
        string file = Lufia2Hacks.First(h => h.Id == id).File;
        rom.ReplaceAll(IpsHandler.ApplyPatch(rom.ToArray(), Load(file)));
        rom.FixChecksum();
    }

    private static readonly uint[] CrcTable = Enumerable.Range(0, 256).Select(n =>
    {
        uint crc = (uint)n;
        for (int k = 0; k < 8; k++) crc = (crc & 1) != 0 ? 0xEDB88320 ^ (crc >> 1) : crc >> 1;
        return crc;
    }).ToArray();

    public static uint Crc32(byte[] data)
    {
        uint crc = 0xFFFFFFFF;
        foreach (byte b in data) crc = CrcTable[(crc ^ b) & 0xFF] ^ (crc >> 8);
        return crc ^ 0xFFFFFFFF;
    }
}
