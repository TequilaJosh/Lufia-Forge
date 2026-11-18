using LufiaForge.Core.Maps;

namespace LufiaForge.Core;

// lufia 2's text font in lufia 1, taken from the user's own lufia 2 rom (we don't store anything from lufia 2).
// both games use the same text font format: 8x16 glyphs, 1 bit per pixel, 16 bytes each (a byte per row,
// bit 7 = leftmost pixel), in ascii order, every cell 8 pixels wide.
// - lufia 1: file 0x54250 + 16 x code (the draw routine at $01:E001 reads $0A:C250 + code x 16). dialogue, choice
//   windows and the ring menus' lists all use it. the game fills the rest of the cell itself, so all 8x16 pixels are usable
// - lufia 2 (usa): file 0xD7770 + 16 x code. other versions get found by searching for its 'M' and 'N'
// both fonts put the baseline on row 13, so glyphs get copied row for row, except the intro's narration only shows
// rows 2-13. lufia 2's capitals start on row 1, so a glyph using row 1 loses one repeated row in its upper half (keeps
// its baseline, one pixel shorter). lufia 1 keeps its own glyphs where lufia 2 has something else (+ is a line in
// lufia 2, { } are quotes, 7F a symbol) or nothing. the original font is kept in the added rom space so it can be put back
public static class FontPatch
{
    public const int Lufia1Font = 0x54250, Lufia2Font = 0xD7770, FontBytes = 0x80 * 16;
    // the small 8x8 font (names, menu titles, numbers): snes 2bpp tiles with both planes equal, in ascii order.
    // lufia 1's is bold, lufia 2's (usa, at 0xF8500) is thin
    public const int Lufia1Small = 0x10000, Lufia2Small = 0xF8500;
    // small font characters that mean the same thing in both games
    private const string SmallChars = "0123456789ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz!'(),-./:;<=>?";
    private const int BackupId = 9;
    private static readonly byte[] Magic = "LFFT"u8.ToArray();
    // lufia 1 keeps these, lufia 2's mean something else
    private static readonly int[] Keep = { 0x2B, 0x7B, 0x7D, 0x7F };

    // lufia 2's 'M' and 'N' (usa rom), used to find the font in other versions
    private static int FindFont(byte[] lufia2Rom)
    {
        if (lufia2Rom.Length >= Lufia2Font + 0x80 * 16 && LooksLikeFont(lufia2Rom, Lufia2Font)) return Lufia2Font;
        // anywhere 16-byte glyphs for 0x20..0x7E start, with a blank space and letters that look right
        for (int offset = 0; offset + 0x80 * 16 <= lufia2Rom.Length; offset += 16)
            if (LooksLikeFont(lufia2Rom, offset)) return offset;
        return -1;
    }

    private static bool LooksLikeFont(byte[] romBytes, int table)
    {
        bool Blank(int c) => Enumerable.Range(0, 16).All(i => romBytes[table + c * 16 + i] == 0);
        int Ink(int c) => Enumerable.Range(0, 16).Sum(i => System.Numerics.BitOperations.PopCount(romBytes[table + c * 16 + i]));
        if (!Blank(0x20)) return false;
        for (int c = 'A'; c <= 'Z'; c++) if (Ink(c) < 12 || Ink(c) > 70) return false;
        for (int c = 'a'; c <= 'z'; c++) if (Ink(c) < 8 || Ink(c) > 60) return false;
        // 'I' is narrower than 'M' and 'W'
        return Ink('I') < Ink('M') && Ink('I') < Ink('W') && Ink('.') < Ink('A');
    }

    // checks a lufia 2 rom file (.sfc/.smc, or a .zip with one in it) and returns its bytes without a copier header,
    // or throws
    public static byte[] ReadLufia2(string path)
    {
        byte[] romBytes;
        if (path.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
        {
            using var zip = System.IO.Compression.ZipFile.OpenRead(path);
            var entry = zip.Entries.FirstOrDefault(e => e.Name.EndsWith(".sfc", StringComparison.OrdinalIgnoreCase) || e.Name.EndsWith(".smc", StringComparison.OrdinalIgnoreCase))
                        ?? throw new InvalidOperationException("That zip has no .sfc or .smc file in it.");
            if (entry.Length > 8 << 20) throw new InvalidOperationException("The ROM in that zip is too big to be Lufia II.");
            using var stream = entry.Open();
            using var buffer = new System.IO.MemoryStream();
            stream.CopyTo(buffer);
            romBytes = buffer.ToArray();
        }
        else romBytes = System.IO.File.ReadAllBytes(path);
        if (romBytes.Length % 0x8000 == 512) romBytes = romBytes[512..];
        if (romBytes.Length < 0x100000) throw new InvalidOperationException("That file is too small to be a Lufia II ROM.");
        string title = new string(romBytes.Skip(0x7FC0).Take(21).Select(b => b is >= 0x20 and < 0x7F ? (char)b : ' ').ToArray());
        if (FindFont(romBytes) < 0) throw new InvalidOperationException($"No Lufia II font found in that ROM (its title reads \"{title.Trim()}\").");
        return romBytes;
    }

    public static bool IsApplied(RomBuffer rom) => BackupOffset(rom) > 0;

    private static int BackupOffset(RomBuffer rom)
    {
        if (rom.Length < MapWriter.ExpandedSize) return 0;
        var backup = ExpansionSpace.Load(rom).Entries.FirstOrDefault(x => x.Kind == ExpansionSpace.Kind.Patch && x.Id == BackupId);
        return backup != null && rom.ReadBytes(backup.Start, 4).SequenceEqual(Magic) ? backup.Start : 0;
    }

    // puts lufia 2's text font in (and with small, the letters, digits and punctuation of its small font too).
    // returns how many glyphs changed
    public static int Apply(RomBuffer rom, byte[] lufia2, bool small = true)
    {
        int fontOffset = FindFont(lufia2);
        if (fontOffset < 0) throw new InvalidOperationException("No Lufia II font found in that ROM.");
        if (rom.Length < MapWriter.ExpandedSize) rom.Expand(MapWriter.ExpandedSize);
        if (!IsApplied(rom))
        {
            var freeSpace = ExpansionSpace.Load(rom);
            int backupOffset = freeSpace.Allocate(4 + 2 * FontBytes, ExpansionSpace.Kind.Patch, BackupId);
            rom.WriteBytes(backupOffset, Magic);
            rom.WriteBytes(backupOffset + 4, rom.ReadBytes(Lufia1Font, FontBytes));
            rom.WriteBytes(backupOffset + 4 + FontBytes, rom.ReadBytes(Lufia1Small, FontBytes));
            // if the dutch letters are in, their places keep the signs (from the letters' own backup), not the letters,
            // so putting the font back after the letters came out shows the signs again (it used to show e-acute for #)
            foreach (var (code, text, smallTile) in DutchLetters.SavedSigns(rom))
            {
                rom.WriteBytes(backupOffset + 4 + code * 16, text);
                rom.WriteBytes(backupOffset + 4 + FontBytes + code * 16, smallTile);
            }
            freeSpace.Save();
        }
        int changed = 0;
        for (int c = 0x20; c < 0x7F; c++)
        {
            if (Keep.Contains(c)) continue;
            var glyph = FitTop(lufia2.AsSpan(fontOffset + c * 16, 16).ToArray());
            if (glyph.All(b => b == 0) && rom.ReadBytes(Lufia1Font + c * 16, 16).Any(b => b != 0)) continue;
            if (!rom.ReadBytes(Lufia1Font + c * 16, 16).SequenceEqual(glyph)) changed++;
            rom.WriteBytes(Lufia1Font + c * 16, glyph);
        }
        // the small font, only if lufia 2's is where we expect it (a blank space, an 'A' with both planes equal)
        bool smallFound = lufia2.Length >= Lufia2Small + FontBytes &&
                          lufia2.AsSpan(Lufia2Small + 0x20 * 16, 16).ToArray().All(b => b == 0) &&
                          Enumerable.Range(0, 8).All(r => lufia2[Lufia2Small + 'A' * 16 + 2 * r] == lufia2[Lufia2Small + 'A' * 16 + 2 * r + 1]) &&
                          lufia2.AsSpan(Lufia2Small + 'A' * 16, 16).ToArray().Any(b => b != 0);
        if (small && smallFound)
            foreach (char ch in SmallChars)
            {
                var glyph = lufia2.AsSpan(Lufia2Small + ch * 16, 16).ToArray();
                if (glyph.All(b => b == 0)) continue;
                if (!rom.ReadBytes(Lufia1Small + ch * 16, 16).SequenceEqual(glyph)) changed++;
                rom.WriteBytes(Lufia1Small + ch * 16, glyph);
            }
        // the dutch letters sit on signs this font just replaced, so make them again from its letters
        if (DutchLetters.IsApplied(rom)) DutchLetters.Apply(rom);
        rom.FixChecksum();
        return changed;
    }

    // moves a glyph's row 1 down by dropping a repeated row (or the most similar pair) between rows 2 and 8
    public static byte[] FitTop(byte[] g)
    {
        if (g[1] == 0) return g;
        int best = -1, bestDiff = int.MaxValue;
        for (int r = 8; r >= 2; r--)
        {
            int diff = System.Numerics.BitOperations.PopCount((uint)(g[r] ^ g[r - 1]));
            if (diff < bestDiff) { bestDiff = diff; best = r; }
        }
        var fitted = (byte[])g.Clone();
        for (int r = best; r >= 2; r--) fitted[r] = g[r - 1];   // rows 1..best-1 move down one, row best (a repeat) goes
        fitted[1] = 0;
        return fitted;
    }

    // puts the font back the way it was before the first Apply
    public static void Restore(RomBuffer rom)
    {
        int backupOffset = BackupOffset(rom);
        if (backupOffset == 0) return;
        rom.WriteBytes(Lufia1Font, rom.ReadBytes(backupOffset + 4, FontBytes));
        rom.WriteBytes(Lufia1Small, rom.ReadBytes(backupOffset + 4 + FontBytes, FontBytes));
        var freeSpace = ExpansionSpace.Load(rom);
        freeSpace.Free(backupOffset);
        freeSpace.Save();
        if (DutchLetters.IsApplied(rom)) DutchLetters.Apply(rom);   // the font we put back had the signs there
        rom.FixChecksum();
    }
}
