using LufiaForge.Core.Battle;
using LufiaForge.Core.Maps;

namespace LufiaForge.Core;

/// <summary>
/// The game's palettes, as rows of 16 SNES colours (BGR555), for the Palettes tab. Each source knows where its
/// colours live and how to write them back (in place, or through <see cref="ResourceWriter"/> for compressed ones).
/// </summary>
public abstract class PaletteSource
{
    public string Name { get; init; } = "";
    public string Info { get; init; } = "";
    /// <summary>Label of each row (e.g. "Row 2").</summary>
    public string[] RowNames { get; init; } = Array.Empty<string>();
    public int Colors => RowNames.Length * 16;
    public abstract ushort[] Read(RomBuffer rom);
    /// <summary>Writes the colours; returns a note (e.g. where a compressed resource went), or null.</summary>
    public abstract string? Write(RomBuffer rom, ushort[] colors, bool allowExpand);
    public override string ToString() => Name;

    public static string[] Rows(int first, int count) => Enumerable.Range(first, count).Select(r => $"Row {r}").ToArray();
}

/// <summary>Uncompressed colours at a file offset.</summary>
public sealed class RawPalette : PaletteSource
{
    public int Offset { get; init; }
    public override ushort[] Read(RomBuffer rom) => Enumerable.Range(0, Colors).Select(i => rom.ReadUInt16Le(Offset + i * 2)).ToArray();
    public override string? Write(RomBuffer rom, ushort[] colors, bool allowExpand)
    {
        for (int i = 0; i < Colors; i++) rom.WriteUInt16Le(Offset + i * 2, (ushort)(colors[i] & 0x7FFF));
        rom.FixChecksum();
        return null;
    }
}

/// <summary>Colours inside a compressed resource (at byte <see cref="At"/> of the unpacked data).</summary>
public sealed class ResourcePalette : PaletteSource
{
    public int Resource { get; init; }
    public int At { get; init; }
    public override ushort[] Read(RomBuffer rom)
    {
        var d = LufiaCompression.DecompressResource(rom, Resource, out _);
        return Enumerable.Range(0, Colors).Select(i => (ushort)(d[At + i * 2] | d[At + i * 2 + 1] << 8)).ToArray();
    }
    public override string? Write(RomBuffer rom, ushort[] colors, bool allowExpand)
    {
        var d = LufiaCompression.DecompressResource(rom, Resource, out _);
        for (int i = 0; i < Colors; i++) { int c = colors[i] & 0x7FFF; d[At + i * 2] = (byte)c; d[At + i * 2 + 1] = (byte)(c >> 8); }
        var r = ResourceWriter.Save(rom, Resource, d, allowExpand);
        rom.FixChecksum();
        return r.Moved ? $"resource {Resource:X3} moved to 0x{r.FileOffset:X6} (it grew)" : null;
    }
}

public static class Palettes
{
    public const int BattlePalettes = 0x11200, SpritePaletteResource = 0xAF;

    /// <summary>Every palette source in the ROM: map palettes (with the maps using them), the shared map rows,
    /// character sprites, battle sprites and monster pictures.</summary>
    public static List<PaletteSource> All(RomBuffer rom)
    {
        var list = new List<PaletteSource>
        {
            new RawPalette { Name = "Map rows 0-1 (all maps)", Offset = LufiaMap.SharedPaletteRows, RowNames = PaletteSource.Rows(0, 2),
                Info = $"Rows 0 and 1 of every town, dungeon and world map (and the title screen), at 0x{LufiaMap.SharedPaletteRows:X6}." },
        };
        // map palettes: which maps use which index
        var users = new SortedDictionary<int, List<string>>();
        void Use(int idx, string who) { if (!users.TryGetValue(idx, out var l)) users[idx] = l = new(); l.Add(who); }
        foreach (var m in MapCatalog.Scan(rom))
        {
            try { Use(LufiaMap.Load(rom, m.MapId).PaletteIndex, $"map {m.MapId:X2}"); } catch { }
        }
        Use(TitleScreen.PaletteIndex, "title screen");
        foreach (var (idx, who) in users)
        {
            int at = LufiaMap.MapPaletteTable + (idx + 1) * 0xC0;
            if (at + 0xC0 > rom.Length) continue;
            string shown = who.Count <= 6 ? string.Join(", ", who) : string.Join(", ", who.Take(6)) + $" and {who.Count - 6} more";
            list.Add(new RawPalette { Name = $"Map palette {idx:X2}", Offset = at, RowNames = PaletteSource.Rows(2, 6),
                Info = $"Rows 2-7 of the maps using palette {idx:X2}: {shown}. At 0x{at:X6}." });
        }
        list.Add(new ResourcePalette { Name = "Character sprites", Resource = SpritePaletteResource, At = 0, RowNames = PaletteSource.Rows(0, 8),
            Info = "Field sprites (party, NPCs): 8 rows; a sprite's record picks its row. Compressed resource 0AF." });
        list.Add(new RawPalette { Name = "Battle sprites", Offset = BattlePalettes, RowNames = PaletteSource.Rows(0, 4),
            Info = $"Party in battle, the command ring and numbers: 4 rows at 0x{BattlePalettes:X6}." });
        for (int g = 0; g < MonsterGraphics.Count(rom); g++)
            list.Add(new ResourcePalette { Name = $"Monster picture {g:X2}", Resource = MonsterGraphics.Resource(rom, g), At = 0, RowNames = new[] { "Palette 0", "Palette 1" },
                Info = "A monster picture's two palettes (a monster's record picks 0 or 1: e.g. the recoloured twin). Compressed in the picture's resource." });
        return list;
    }
}
