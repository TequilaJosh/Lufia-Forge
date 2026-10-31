using LufiaForge.Core.Maps;

namespace LufiaForge.Core;

/// <summary>
/// Lufia II's text font in Lufia 1, taken from the user's own Lufia II ROM (nothing of Lufia II is stored in Lufia
/// Forge). Both games use the same text font format: 8x16 glyphs, one bit per pixel, 16 bytes each (a byte per row,
/// bit 7 = leftmost pixel), in ASCII order, every cell 8 pixels wide.
/// <list type="bullet">
/// <item>Lufia 1: file 0x54250 + 16 x code (the draw routine at $01:E001 reads $0A:C250 + code x 16); dialogue,
/// choice windows and the ring menus' lists all use it. The game fills the rest of the cell itself, so all 8x16
/// pixels are usable.</item>
/// <item>Lufia II (USA): file 0xD7770 + 16 x code; other versions are found by searching for its 'M' and 'N'.</item>
/// </list>
/// Both fonts put the baseline on row 13, so glyphs are copied row for row, except that the intro's narration only
/// shows rows 2-13: Lufia II's capitals start on row 1, so a glyph using row 1 loses one repeated row in its upper
/// half (it keeps its baseline and is one pixel shorter). Lufia 1 keeps its own glyphs where
/// Lufia II has something else (+ is a line in Lufia II, { } are quotes, 7F a symbol) or nothing. The original font
/// is kept in the added ROM space so it can be put back.
/// </summary>
public static class FontPatch
{
    public const int Lufia1Font = 0x54250, Lufia2Font = 0xD7770, FontBytes = 0x80 * 16;
    /// <summary>The small 8x8 font (names, menu titles, numbers): SNES 2bpp tiles with both planes equal, in ASCII
    /// order. Lufia 1's is bold; Lufia II's (USA, at 0xF8500) is thin.</summary>
    public const int Lufia1Small = 0x10000, Lufia2Small = 0xF8500;
    /// <summary>Small-font characters that are the same thing in both games.</summary>
    private const string SmallChars = "0123456789ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz!'(),-./:;<=>?";
    private const int BackupId = 9;
    private static readonly byte[] Magic = "LFFT"u8.ToArray();
    /// <summary>Lufia 1 keeps these: Lufia II's differ in meaning.</summary>
    private static readonly int[] Keep = { 0x2B, 0x7B, 0x7D, 0x7F };

    /// <summary>Lufia II's 'M' and 'N' (USA ROM), used to find the font in other versions.</summary>
    private static int FindFont(byte[] l2)
    {
        if (l2.Length >= Lufia2Font + 0x80 * 16 && LooksLikeFont(l2, Lufia2Font)) return Lufia2Font;
        // anywhere 16-byte glyphs for 0x20..0x7E start, with a blank space and plausible letters
        for (int p = 0; p + 0x80 * 16 <= l2.Length; p += 16)
            if (LooksLikeFont(l2, p)) return p;
        return -1;
    }

    private static bool LooksLikeFont(byte[] d, int table)
    {
        bool Blank(int c) => Enumerable.Range(0, 16).All(i => d[table + c * 16 + i] == 0);
        int Ink(int c) => Enumerable.Range(0, 16).Sum(i => System.Numerics.BitOperations.PopCount(d[table + c * 16 + i]));
        if (!Blank(0x20)) return false;
        for (int c = 'A'; c <= 'Z'; c++) if (Ink(c) < 12 || Ink(c) > 70) return false;
        for (int c = 'a'; c <= 'z'; c++) if (Ink(c) < 8 || Ink(c) > 60) return false;
        // 'I' is narrower than 'M' and 'W'
        return Ink('I') < Ink('M') && Ink('I') < Ink('W') && Ink('.') < Ink('A');
    }

    /// <summary>Checks a Lufia II ROM file (.sfc/.smc, or a .zip holding one) and returns its bytes without a copier
    /// header, or throws.</summary>
    public static byte[] ReadLufia2(string path)
    {
        byte[] d;
        if (path.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
        {
            using var zip = System.IO.Compression.ZipFile.OpenRead(path);
            var entry = zip.Entries.FirstOrDefault(e => e.Name.EndsWith(".sfc", StringComparison.OrdinalIgnoreCase) || e.Name.EndsWith(".smc", StringComparison.OrdinalIgnoreCase))
                        ?? throw new InvalidOperationException("That zip has no .sfc or .smc file in it.");
            if (entry.Length > 8 << 20) throw new InvalidOperationException("The ROM in that zip is too big to be Lufia II.");
            using var s = entry.Open();
            using var ms = new System.IO.MemoryStream();
            s.CopyTo(ms);
            d = ms.ToArray();
        }
        else d = System.IO.File.ReadAllBytes(path);
        if (d.Length % 0x8000 == 512) d = d[512..];
        if (d.Length < 0x100000) throw new InvalidOperationException("That file is too small to be a Lufia II ROM.");
        string title = new string(d.Skip(0x7FC0).Take(21).Select(b => b is >= 0x20 and < 0x7F ? (char)b : ' ').ToArray());
        if (FindFont(d) < 0) throw new InvalidOperationException($"No Lufia II font found in that ROM (its title reads \"{title.Trim()}\").");
        return d;
    }

    public static bool IsApplied(RomBuffer rom) => BackupOffset(rom) > 0;

    private static int BackupOffset(RomBuffer rom)
    {
        if (rom.Length < MapWriter.ExpandedSize) return 0;
        var e = ExpansionSpace.Load(rom).Entries.FirstOrDefault(x => x.Kind == ExpansionSpace.Kind.Patch && x.Id == BackupId);
        return e != null && rom.ReadBytes(e.Start, 4).SequenceEqual(Magic) ? e.Start : 0;
    }

    /// <summary>Puts Lufia II's text font in (and, with <paramref name="small"/>, the letters, digits and punctuation
    /// of its small font). Returns how many glyphs changed.</summary>
    public static int Apply(RomBuffer rom, byte[] lufia2, bool small = true)
    {
        int src = FindFont(lufia2);
        if (src < 0) throw new InvalidOperationException("No Lufia II font found in that ROM.");
        if (rom.Length < MapWriter.ExpandedSize) rom.Expand(MapWriter.ExpandedSize);
        if (!IsApplied(rom))
        {
            var space = ExpansionSpace.Load(rom);
            int at = space.Allocate(4 + 2 * FontBytes, ExpansionSpace.Kind.Patch, BackupId);
            rom.WriteBytes(at, Magic);
            rom.WriteBytes(at + 4, rom.ReadBytes(Lufia1Font, FontBytes));
            rom.WriteBytes(at + 4 + FontBytes, rom.ReadBytes(Lufia1Small, FontBytes));
            space.Save();
        }
        int changed = 0;
        for (int c = 0x20; c < 0x7F; c++)
        {
            if (Keep.Contains(c)) continue;
            var g = FitTop(lufia2.AsSpan(src + c * 16, 16).ToArray());
            if (g.All(b => b == 0) && rom.ReadBytes(Lufia1Font + c * 16, 16).Any(b => b != 0)) continue;
            if (!rom.ReadBytes(Lufia1Font + c * 16, 16).SequenceEqual(g)) changed++;
            rom.WriteBytes(Lufia1Font + c * 16, g);
        }
        // the small font: only where Lufia II's is where we expect it (a blank space, an 'A' with both planes equal)
        bool smallFound = lufia2.Length >= Lufia2Small + FontBytes &&
                          lufia2.AsSpan(Lufia2Small + 0x20 * 16, 16).ToArray().All(b => b == 0) &&
                          Enumerable.Range(0, 8).All(r => lufia2[Lufia2Small + 'A' * 16 + 2 * r] == lufia2[Lufia2Small + 'A' * 16 + 2 * r + 1]) &&
                          lufia2.AsSpan(Lufia2Small + 'A' * 16, 16).ToArray().Any(b => b != 0);
        if (small && smallFound)
            foreach (char ch in SmallChars)
            {
                var g = lufia2.AsSpan(Lufia2Small + ch * 16, 16).ToArray();
                if (g.All(b => b == 0)) continue;
                if (!rom.ReadBytes(Lufia1Small + ch * 16, 16).SequenceEqual(g)) changed++;
                rom.WriteBytes(Lufia1Small + ch * 16, g);
            }
        // the Dutch letters sit on signs this font just replaced: made again from its letters
        if (DutchLetters.IsApplied(rom)) DutchLetters.Apply(rom);
        rom.FixChecksum();
        return changed;
    }

    /// <summary>Moves a glyph's row 1 down by dropping a repeated row (or the most similar pair) between rows 2 and 8.</summary>
    public static byte[] FitTop(byte[] g)
    {
        if (g[1] == 0) return g;
        int best = -1, bestDiff = int.MaxValue;
        for (int r = 8; r >= 2; r--)
        {
            int d = System.Numerics.BitOperations.PopCount((uint)(g[r] ^ g[r - 1]));
            if (d < bestDiff) { bestDiff = d; best = r; }
        }
        var o = (byte[])g.Clone();
        for (int r = best; r >= 2; r--) o[r] = g[r - 1];   // rows 1..best-1 move down one, row best (a repeat) goes
        o[1] = 0;
        return o;
    }

    /// <summary>Puts the font back as it was before the first <see cref="Apply"/>.</summary>
    public static void Restore(RomBuffer rom)
    {
        int b = BackupOffset(rom);
        if (b == 0) return;
        rom.WriteBytes(Lufia1Font, rom.ReadBytes(b + 4, FontBytes));
        rom.WriteBytes(Lufia1Small, rom.ReadBytes(b + 4 + FontBytes, FontBytes));
        var space = ExpansionSpace.Load(rom);
        space.Free(b);
        space.Save();
        if (DutchLetters.IsApplied(rom)) DutchLetters.Apply(rom);   // (the font put back had the signs there)
        rom.FixChecksum();
    }
}
