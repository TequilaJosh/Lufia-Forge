using System.IO;
using System.IO.Compression;
using System.Security.Cryptography;

namespace LufiaForge.Core.Lufia2;

/// <summary>
/// Maps CF and F1 (shrines) are drawn with blocks the US game's block set 17 and tile set 11 don't have, so they show as
/// garbage; the European release (Lufia, 1996) changed those two sets (block set 17: 0x2CF blocks to 0x30D; tile set
/// 11: 802 tiles to 819) and renumbered the blocks of the other maps that use them (E7-EB), leaving CF and F1 as they
/// were. Found by comparing the two ROMs (2026-10-27; the European packed files are numbered one higher from 0x40).
/// <para>The fix puts the European sets in (data in Fixes/Lufia2_ShrineSets.bin: the block set, and the tile set as
/// tiles of the US tile set 11 by number plus the 17 it lacks) and gives every other map using block set 17 the block
/// of the new set that looks the same (pixels and priority); every block those maps use has one. Only done while both
/// sets are still the US game's (the fan hacks already carry the European block set).</para>
/// </summary>
public static class L2ShrineFix
{
    public const int BlockSet = 0x17, TileSet = 0x11;
    /// <summary>The maps made for the European sets (drawn as they are); other maps on block set 17 are renumbered.</summary>
    public static readonly int[] FixedMaps = { 0xCF, 0xF1 };

    private const string UsBlockSet = "43AFB9F2722CFD8DB6B3320764E9C73DC472BA02", UsTileSet = "53949EAF191784E075E86E84482145C0466C36D9",
                         EuBlockSet = "B9E5E8F4C5A340FD8265B3229CCDE240DC01944F", EuTileSet = "5F7B7AFB493C7BBBA95792F61F854FF991A89A38";

    private static int BlockSetFile => L2Maps.BlockSetFileBase + BlockSet;
    private static int TileSetFile => L2Maps.TileSetFileBase + TileSet;

    private static string Sha(byte[] b) => Convert.ToHexString(SHA1.HashData(b));

    /// <summary>Whether the ROM still has the US game's sets 17 and 11 with map CF on them (the fix applies).</summary>
    public static bool Needed(RomBuffer rom)
    {
        try
        {
            if (rom.Length < L2Rom.OriginalSize || GameDetect.Detect(rom.ToArray()) != GameKind.Lufia2) return false;
            if (rom.ReadByte(L2Maps.BlockSetList + FixedMaps[0]) != BlockSet || rom.ReadByte(L2Maps.TileSetList + FixedMaps[0]) != TileSet) return false;
            return Sha(L2Rom.ReadFile(rom, BlockSetFile)) == UsBlockSet && Sha(L2Rom.ReadFile(rom, TileSetFile)) == UsTileSet;
        }
        catch (Exception) { return false; }
    }

    /// <summary>Puts the European sets in and renumbers the other maps on them. Returns what was done; on a problem the
    /// ROM is left as it was and the reason thrown.</summary>
    public static string Apply(RomBuffer rom)
    {
        if (!Needed(rom)) throw new InvalidOperationException("This ROM doesn't have the US game's block set 17 and tile set 11.");
        var (blockSet, tileSet) = Build(rom);
        var before = rom.ToArray();
        try
        {
            var others = Enumerable.Range(0, L2Maps.Count).Where(m => rom.ReadByte(L2Maps.BlockSetList + m) == BlockSet && !FixedMaps.Contains(m)).ToList();
            var oldLooks = others.ToDictionary(m => m, m => Looks(rom, m));
            L2Rom.WriteFile(rom, BlockSetFile, blockSet);
            L2Rom.WriteFile(rom, TileSetFile, tileSet);
            foreach (int m in others)
            {
                var map = L2Maps.Read(rom, m);
                var newLooks = Looks(rom, m);
                var byLook = new Dictionary<string, int>();
                for (int b = newLooks.Length - 1; b >= 0; b--) byLook[newLooks[b]] = b;
                var old = oldLooks[m];
                foreach (var layer in map.Layers)
                    for (int i = 0; i < layer.Blocks.Length; i++)
                    {
                        int b = layer.Blocks[i];
                        if (b >= old.Length || !byLook.TryGetValue(old[b], out int nb))
                            throw new InvalidOperationException($"Map {m:X2} uses block {b:X3}, which the European block set has no copy of.");
                        layer.Blocks[i] = nb;
                    }
                L2Maps.Write(rom, map);
            }
            return $"block set 17 and tile set 11 replaced by the European release's; maps {string.Join(", ", others.Select(m => m.ToString("X2")))} renumbered to the same blocks";
        }
        catch (Exception)
        {
            rom.ReplaceAll(before);
            throw;
        }
    }

    /// <summary>Each block of the map's block set as it looks: its pixels and the priority of its four tiles.</summary>
    private static string[] Looks(RomBuffer rom, int map)
    {
        var m = L2Maps.Read(rom, map);
        var pixels = L2Maps.AllBlocks(rom, m);
        var set = L2Rom.ReadFile(rom, m.BlockSet);
        var looks = new string[pixels.Length];
        for (int b = 0; b < pixels.Length; b++)
        {
            int rec = 0x10 + b * 8, prio = 0;
            for (int q = 0; q < 4 && rec + q * 2 + 1 < set.Length; q++) if ((set[rec + q * 2 + 1] & 0x20) != 0) prio |= 1 << q;
            looks[b] = prio + ":" + Convert.ToBase64String(pixels[b].SelectMany(BitConverter.GetBytes).ToArray());
        }
        return looks;
    }

    /// <summary>The European block set and tile set, from the fix data and this ROM's tile set 11.</summary>
    private static (byte[] BlockSet, byte[] TileSet) Build(RomBuffer rom)
    {
        using var s = typeof(L2ShrineFix).Assembly.GetManifestResourceStream("Fixes.Lufia2_ShrineSets.bin")
                      ?? throw new InvalidOperationException("The shrine fix data is missing from Lufia Forge.");
        using var z = new DeflateStream(s, CompressionMode.Decompress);
        using var ms = new MemoryStream();
        z.CopyTo(ms);
        var r = new BinaryReader(new MemoryStream(ms.ToArray()));
        var blockSet = r.ReadBytes(r.ReadUInt16());
        var us = L2Rom.ReadFile(rom, TileSetFile);
        int n = r.ReadUInt16();
        var tiles = new byte[n * 32];
        for (int i = 0; i < n; i++)
        {
            int from = r.ReadUInt16();
            if (from == 0xFFFF) r.ReadBytes(32).CopyTo(tiles, i * 32);
            else Array.Copy(us, from * 32, tiles, i * 32, 32);
        }
        if (Sha(blockSet) != EuBlockSet || Sha(tiles) != EuTileSet) throw new InvalidOperationException("The shrine fix data didn't build the European sets.");
        return (blockSet, tiles);
    }
}
