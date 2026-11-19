using LufiaForge.Core.Battle;
using LufiaForge.Core.Maps;

namespace LufiaForge.Core;

// the game's palettes as rows of 16 snes colours (BGR555), for the palettes tab. each source knows where its colours
// live and how to write them back (in place, or through ResourceWriter for compressed ones)
public abstract class PaletteSource
{
    public string Name { get; init; } = "";
    public string Info { get; init; } = "";
    // label of each row, like "Row 2"
    public string[] RowNames { get; init; } = Array.Empty<string>();
    public int Colors => RowNames.Length * 16;
    public abstract ushort[] Read(RomBuffer rom);
    // writes the colours. returns a note (like where a compressed resource went), or null
    public abstract string? Write(RomBuffer rom, ushort[] colors, bool allowExpand);
    public override string ToString() => Name;

    public static string[] Rows(int first, int count) => Enumerable.Range(first, count).Select(r => $"Row {r}").ToArray();
}

// uncompressed colours at a file offset
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

// colours inside a compressed resource (at byte At of the unpacked data)
public sealed class ResourcePalette : PaletteSource
{
    public int Resource { get; init; }
    public int At { get; init; }
    public override ushort[] Read(RomBuffer rom)
    {
        var unpacked = LufiaCompression.DecompressResource(rom, Resource, out _);
        return Enumerable.Range(0, Colors).Select(i => (ushort)(unpacked[At + i * 2] | unpacked[At + i * 2 + 1] << 8)).ToArray();
    }
    public override string? Write(RomBuffer rom, ushort[] colors, bool allowExpand)
    {
        var unpacked = LufiaCompression.DecompressResource(rom, Resource, out _);
        for (int i = 0; i < Colors; i++) { int color = colors[i] & 0x7FFF; unpacked[At + i * 2] = (byte)color; unpacked[At + i * 2 + 1] = (byte)(color >> 8); }
        var result = ResourceWriter.Save(rom, Resource, unpacked, allowExpand);
        rom.FixChecksum();
        return result.Moved ? $"resource {Resource:X3} moved to 0x{result.FileOffset:X6} (it grew)" : null;
    }
}

public static class Palettes
{
    public const int BattlePalettes = 0x11200, SpritePaletteResource = 0xAF;

    // every palette source in the rom: map palettes (with the maps that use them), the shared map rows, character
    // sprites, battle sprites and monster pictures
    public static List<PaletteSource> All(RomBuffer rom)
    {
        var list = new List<PaletteSource>
        {
            new RawPalette { Name = "Map rows 0-1 (all maps)", Offset = LufiaMap.SharedPaletteRows, RowNames = PaletteSource.Rows(0, 2),
                Info = $"Rows 0 and 1 of every town, dungeon and world map (and the title screen), at 0x{LufiaMap.SharedPaletteRows:X6}." },
        };
        // map palettes, which maps use which index
        var users = new SortedDictionary<int, List<string>>();
        void Use(int paletteIndex, string user) { if (!users.TryGetValue(paletteIndex, out var names)) users[paletteIndex] = names = new(); names.Add(user); }
        foreach (var map in MapCatalog.Scan(rom))
        {
            try { Use(LufiaMap.Load(rom, map.MapId).PaletteIndex, $"map {map.MapId:X2}"); } catch { }
        }
        Use(TitleScreen.PaletteIndex, "title screen");
        foreach (var (paletteIndex, usedBy) in users)
        {
            // the game's pointer table $01:E160 has 20 palettes. a map with a bigger number reads garbage, and
            // "its" colours would be the setup script table at 0x18000
            if (paletteIndex >= LufiaMap.MapPaletteCount) continue;
            int paletteOffset = LufiaMap.MapPaletteTable + (paletteIndex + 1) * 0xC0;
            if (paletteOffset + 0xC0 > rom.Length) continue;
            string shown = usedBy.Count <= 6 ? string.Join(", ", usedBy) : string.Join(", ", usedBy.Take(6)) + $" and {usedBy.Count - 6} more";
            list.Add(new RawPalette { Name = $"Map palette {paletteIndex:X2}", Offset = paletteOffset, RowNames = PaletteSource.Rows(2, 6),
                Info = $"Rows 2-7 of the maps using palette {paletteIndex:X2}: {shown}. At 0x{paletteOffset:X6}." });
        }
        list.Add(new ResourcePalette { Name = "Character sprites", Resource = SpritePaletteResource, At = 0, RowNames = PaletteSource.Rows(0, 8),
            Info = "Field sprites (party, NPCs): 8 rows; a sprite's record picks its row. Compressed resource 0AF." });
        list.Add(new RawPalette { Name = "Battle sprites", Offset = BattlePalettes, RowNames = PaletteSource.Rows(0, 4),
            Info = $"Party in battle, the command ring and numbers: 4 rows at 0x{BattlePalettes:X6}." });
        for (int picture = 0; picture < MonsterGraphics.Count(rom); picture++)
            list.Add(new ResourcePalette { Name = $"Monster picture {picture:X2}", Resource = MonsterGraphics.Resource(rom, picture), At = 0, RowNames = new[] { "Palette 0", "Palette 1" },
                Info = "A monster picture's two palettes (a monster's record picks 0 or 1: e.g. the recoloured twin). Compressed in the picture's resource." });
        return list;
    }
}
