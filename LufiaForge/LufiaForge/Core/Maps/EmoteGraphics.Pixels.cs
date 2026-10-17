using LufiaForge.Modules.TileViewer;

namespace LufiaForge.Core.Maps;

public static partial class EmoteGraphics
{
    private static readonly Dictionary<int, uint[]> Cache = new();

    /// <summary>Emote <paramref name="i"/> as 16x16 BGRA pixels (see-through = 0), as the game shows it.</summary>
    public static uint[] Pixels(int i)
    {
        i = Math.Clamp(i, 0, Names.Length - 1);
        lock (Cache)
        {
            if (Cache.TryGetValue(i, out var hit)) return hit;
            var colours = Palette.Select((c, k) => k == 0 ? 0u
                : 0xFF000000u | (uint)((c & 31) * 255 / 31) << 16 | (uint)((c >> 5 & 31) * 255 / 31) << 8 | (uint)((c >> 10 & 31) * 255 / 31)).ToArray();
            var px = new uint[256];
            for (int t = 0; t < 4; t++)
            {
                var tile = SnesTileDecoder.DecodeTile(Tiles, i * BytesPerEmote + t * 32, BitDepth.Bpp4);
                for (int y = 0; y < 8; y++) for (int x = 0; x < 8; x++) px[(t / 2 * 8 + y) * 16 + t % 2 * 8 + x] = colours[tile[y * 8 + x]];
            }
            return Cache[i] = px;
        }
    }
}
