namespace LufiaForge.Core.Maps;

/// <summary>
/// When a song reaches its sync points. Command 6C waits until the music reaches point n; points mark bars of the
/// song but aren't evenly spaced (held bars), so songs measured in the game use their measured times. Others use
/// the average of 89.7 frames per point.
/// </summary>
public static class MusicSync
{
    public const double AverageFramesPerPoint = 89.7;

    /// <summary>Song → (point → frame after the song started), measured in BizHawk.</summary>
    private static readonly Dictionary<int, SortedDictionary<int, int>> Measured = new()
    {
        // song 1C: the opening (map 4F's music), measured 2026-10-08 from the pointer changes of the intro event
        [0x1C] = new()
        {
            [1] = 59, [4] = 284, [12] = 1001, [13] = 1091, [18] = 1540, [19] = 1629, [25] = 2167, [26] = 2257,
            [32] = 2795, [33] = 2885, [37] = 3243, [38] = 3333, [43] = 3781, [44] = 3871, [49] = 4319, [50] = 4409,
            [55] = 4947, [56] = 5037, [59] = 5306,
        },
    };

    /// <summary>Cutscenes whose music was already playing when they start: (map, event) → (song, frames already played).</summary>
    private static readonly Dictionary<(int Map, int Event), (int Song, int Lead)> KnownStarts = new()
    {
        [(0x4F, 8)] = (0x1C, 58),   // the intro: song 1C starts when map 4F loads (frame 224), 58 frames before the event runs (frame 282)
    };

    public static bool IsMeasured(int song) => Measured.ContainsKey(song);

    public static (int Song, int Lead) StartOf(int map, int ev) => KnownStarts.TryGetValue((map, ev), out var k) ? k : (-1, 0);

    /// <summary>Frame after the song started at which it reaches point <paramref name="point"/>.</summary>
    public static int FrameOfPoint(int song, int point)
    {
        if (!Measured.TryGetValue(song, out var table) || table.Count == 0) return (int)Math.Round(point * AverageFramesPerPoint);
        if (table.TryGetValue(point, out int exact)) return exact;
        // between measured points: linear; outside them: the average tempo from the nearest one
        int? lo = null, hi = null;
        foreach (var p in table.Keys) { if (p < point) lo = p; else if (p > point) { hi = p; break; } }
        if (lo is int a && hi is int b) return table[a] + (int)Math.Round((table[b] - table[a]) * (point - a) / (double)(b - a));
        if (lo is int last) return table[last] + (int)Math.Round((point - last) * AverageFramesPerPoint);
        int first = hi!.Value;
        return Math.Max(0, table[first] - (int)Math.Round((first - point) * AverageFramesPerPoint));
    }
}
