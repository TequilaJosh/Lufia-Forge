using LufiaForge.Core.Maps;

namespace LufiaForge.Core.Audio;

/// <summary>
/// The music of a cutscene as a stream of samples on the cutscene's frame clock: songs start where the script
/// starts them (command 0A, or the song a known cutscene begins with, see <see cref="MusicSync"/>), silent while
/// the game uploads them, and stop at 0A FF; sound effects (54, and the thunder of 60) play over them.
/// </summary>
public sealed class CutsceneMusic : IDisposable
{
    /// <summary>At a frame of the cutscene: a song starting (or -1: the music stopping), or a sound effect.</summary>
    public readonly record struct Cue(int Frame, int Song, int Effect = -1)
    {
        public bool IsEffect => Effect >= 0;
    }

    public const double SamplesPerFrame = LufiaSound.SampleRate / LufiaSound.FramesPerSecond;

    private readonly RomBuffer _rom;
    private readonly List<Cue> _cues;
    private LufiaSound? _sound;
    private int _next;              // next cue to apply
    private double _origin;         // frame of sample 0
    private long _sample;           // sample pairs produced since the origin
    private double _silentUntil = double.MinValue;
    private bool _playing;
    private readonly short[] _scratch = new short[2 * 4096];

    public CutsceneMusic(RomBuffer rom, IEnumerable<Cue> cues)
    {
        _rom = rom;
        _cues = cues.OrderBy(c => c.Frame).ToList();
    }

    public bool HasMusic => _cues.Count > 0;

    /// <summary>The emulated driver (tests).</summary>
    public LufiaSound? Sound => _sound;

    /// <summary>Sound cues of a script: the song it starts with (if known), every 0A, sound effects (54) and the
    /// thunder of lightning flashes (60).</summary>
    public static List<Cue> Cues(RomBuffer rom, EventScript script, IReadOnlyList<TimelineItem> timeline)
    {
        var cues = new List<Cue>();
        var (song, lead) = MusicSync.StartOf(script.MapId, script.Event);
        if (song >= 0) cues.Add(new Cue(-lead, song));
        foreach (var it in timeline)
        {
            if (it.Line >= script.Ops.Count || script.Ops[it.Line] is not { IsText: false } op) continue;
            if (op.Bytes.Length < 2) continue;
            switch (op.Bytes[0])
            {
                case 0x0A: cues.Add(new Cue(it.Start, op.Bytes[1] == 0xFF ? -1 : op.Bytes[1])); break;
                case 0x54: cues.Add(new Cue(it.Start, 0, op.Bytes[1])); break;
                case 0x60 when LufiaSound.FlashSoundEffect(rom, op.Bytes[1]) is var fx and > 0: cues.Add(new Cue(it.Start, 0, fx)); break;
            }
        }
        return cues;
    }

    /// <summary>Gets ready to produce the music from <paramref name="frame"/> on.</summary>
    public void Seek(double frame)
    {
        _sound?.Dispose();
        _sound = LufiaSound.Create(_rom);
        _next = 0; _sample = 0; _playing = false; _silentUntil = double.MinValue;
        // start emulating at the first cue before the frame (earlier cues are replaced by later songs anyway)
        int from = _cues.FindLastIndex(c => c.Frame <= frame && !c.IsEffect && c.Song >= 0);
        _origin = from >= 0 ? _cues[from].Frame : frame;
        _next = from >= 0 ? from : _cues.FindIndex(c => c.Frame >= frame);
        if (_next < 0) _next = _cues.Count;
        long skip = (long)Math.Round((frame - _origin) * SamplesPerFrame);
        while (skip > 0)
        {
            int n = (int)Math.Min(skip, _scratch.Length / 2);
            Render(_scratch, n);
            skip -= n;
        }
    }

    private double FrameAt(long sample) => _origin + sample / SamplesPerFrame;
    private long SampleAt(double frame) => (long)Math.Ceiling((frame - _origin) * SamplesPerFrame);

    /// <summary>Fills <paramref name="pairs"/> stereo sample pairs (16-bit, 32 kHz).</summary>
    public void Render(short[] buffer, int pairs)
    {
        int done = 0;
        while (done < pairs)
        {
            double now = FrameAt(_sample);
            if (_next < _cues.Count && _cues[_next].Frame <= now + 1e-9)
            {
                Apply(_cues[_next++]);
                continue;
            }
            long n = pairs - done;
            if (_next < _cues.Count) n = Math.Min(n, Math.Max(1, SampleAt(_cues[_next].Frame) - _sample));
            // the driver keeps running (sound effects) except while the game is still uploading a song
            bool silent = _sound == null || now < _silentUntil;
            if (now < _silentUntil) n = Math.Min(n, Math.Max(1, SampleAt(_silentUntil) - _sample));
            if (silent) Array.Clear(buffer, done * 2, (int)n * 2);
            else
            {
                var chunk = done == 0 && n == pairs ? buffer : new short[n * 2];
                _sound!.Render(chunk, (int)n * 2);
                if (!ReferenceEquals(chunk, buffer)) Array.Copy(chunk, 0, buffer, done * 2, n * 2);
            }
            done += (int)n; _sample += n;
        }
    }

    private void Apply(Cue c)
    {
        if (_sound == null) return;
        if (c.IsEffect) { _sound.PlaySoundEffect(c.Effect); return; }
        if (c.Song < 0) { if (_playing) _sound.StopMusic(); _playing = false; return; }
        double latency = _sound.PlaySong(c.Song);
        _playing = true;
        _silentUntil = c.Frame + latency;
    }

    public void Dispose() { _sound?.Dispose(); _sound = null; }
}
