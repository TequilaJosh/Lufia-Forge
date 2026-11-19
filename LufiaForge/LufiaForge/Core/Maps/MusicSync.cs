namespace LufiaForge.Core.Maps;

// when a song reaches its sync points. command 6C waits until the music reaches point n. points mark bars of the song
// but they aren't evenly spaced (held bars), so songs we measured in the game use the measured times. others use what
// the game's sound driver reports when it plays them on the emulated sound chip (UseRom), or the average of 89.7
// frames per point when there's no rom or sound DLL
public static class MusicSync
{
    public const double AverageFramesPerPoint = 89.7;

    // song -> (point -> frame after the song started), measured in BizHawk
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

    // cutscenes whose music was already playing when they start: (map, event) -> (song, frames already played)
    private static readonly Dictionary<(int Map, int Event), (int Song, int Lead)> KnownStarts = new()
    {
        [(0x4F, 8)] = (0x1C, 58),   // the intro: song 1C starts when map 4F loads (frame 224), 58 frames before the event runs (frame 282)
    };

    public static bool IsMeasured(int song) => Measured.ContainsKey(song);

    public static (int Song, int Lead) StartOf(int map, int ev) => KnownStarts.TryGetValue((map, ev), out var known) ? known : (-1, 0);

    private static RomBuffer? _rom;
    private static readonly Dictionary<int, Dictionary<int, int>?> Emulated = new();

    // lets unmeasured songs get timed by playing them on the emulated sound chip
    public static void UseRom(RomBuffer? rom)
    {
        if (ReferenceEquals(rom, _rom)) return;
        lock (Emulated) { _rom = rom; Emulated.Clear(); }
    }

    // point -> frame after the load for a song, from the game's sound driver: frames until the song starts (the
    // upload) plus the frame on which command 06 first reports the point, plus the frame the script takes to see it.
    // polled once per frame for 6 minutes. checked against song 1C's measured points, all within 1 frame
    private static Dictionary<int, int>? EmulatedPoints(int song)
    {
        lock (Emulated)
        {
            if (Emulated.TryGetValue(song, out var cached)) return cached;
            Dictionary<int, int>? points = null;
            if (_rom != null)
                try
                {
                    using var sound = Audio.LufiaSound.Create(_rom);
                    if (sound != null)
                    {
                        points = new();
                        double latency = sound.PlaySong(song);
                        long start = sound.Clock;
                        var buffer = new short[2 * 1200];
                        int last = -1;
                        for (int frame = 0; frame < 60 * 360; frame++)
                        {
                            long target = start + (long)((frame + 1) * Audio.LufiaSound.ClocksPerFrame);
                            int samples = (int)Math.Max(0, (target - sound.Clock) / 32);
                            if (samples > 0) sound.Render(buffer, samples * 2);
                            int point = sound.SyncPoint();
                            // +1: the script sees the point at its next poll, a frame after the driver reaches it
                            if (point != last && !points.ContainsKey(point)) points[point] = (int)Math.Round(latency + frame + 2);
                            last = point;
                        }
                    }
                }
                catch { points = null; }
            Emulated[song] = points;
            return points;
        }
    }

    // the frame after the song started at which it reaches point
    public static int FrameOfPoint(int song, int point)
    {
        if (!Measured.TryGetValue(song, out var table) || table.Count == 0)
        {
            // the driver's own answer when we have the rom. points it never reports wait forever in the game,
            // here they fall back to the average tempo
            if (EmulatedPoints(song) is { } emulated && emulated.TryGetValue(point, out int frame)) return frame;
            return (int)Math.Round(point * AverageFramesPerPoint);
        }
        if (table.TryGetValue(point, out int exact)) return exact;
        // between measured points it's linear, outside them it's the average tempo from the nearest one
        int? lo = null, hi = null;
        foreach (var known in table.Keys) { if (known < point) lo = known; else if (known > point) { hi = known; break; } }
        if (lo is int a && hi is int b) return table[a] + (int)Math.Round((table[b] - table[a]) * (point - a) / (double)(b - a));
        if (lo is int last) return table[last] + (int)Math.Round((point - last) * AverageFramesPerPoint);
        int first = hi!.Value;
        return Math.Max(0, table[first] - (int)Math.Round((first - point) * AverageFramesPerPoint));
    }
}
