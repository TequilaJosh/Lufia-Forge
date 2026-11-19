using LufiaForge.Modules.TileViewer;

namespace LufiaForge.Core.Maps;

public static partial class EmoteGraphics
{
    private static readonly Dictionary<int, uint[]> Cache = new();

    // an emote as 16x16 BGRA pixels (see-through = 0), the way the game shows it
    public static uint[] Pixels(int emote)
    {
        emote = Math.Clamp(emote, 0, Names.Length - 1);
        lock (Cache)
        {
            if (Cache.TryGetValue(emote, out var cached)) return cached;
            var colours = Palette.Select((c, k) => k == 0 ? 0u
                : 0xFF000000u | (uint)((c & 31) * 255 / 31) << 16 | (uint)((c >> 5 & 31) * 255 / 31) << 8 | (uint)((c >> 10 & 31) * 255 / 31)).ToArray();
            var pixels = new uint[256];
            for (int quarter = 0; quarter < 4; quarter++)
            {
                var tile = SnesTileDecoder.DecodeTile(Tiles, emote * BytesPerEmote + quarter * 32, BitDepth.Bpp4);
                for (int y = 0; y < 8; y++) for (int x = 0; x < 8; x++) pixels[(quarter / 2 * 8 + y) * 16 + quarter % 2 * 8 + x] = colours[tile[y * 8 + x]];
            }
            return Cache[emote] = pixels;
        }
    }
}
