using Button = System.Windows.Controls.Button;
using System.Windows.Threading;
using LufiaForge.Core;
using LufiaForge.Core.Audio;

namespace LufiaForge.Modules.Common;

/// <summary>
/// The "Listen" button of a song or sound in the event editors: plays it with the game's own sound driver (the same
/// emulated sound chip as the Music pages). One thing plays at a time; the button that started it shows "Stop" while
/// it plays, and a click on it stops it. A song plays until stopped, a sound until it has ended.
/// </summary>
public static class SoundPreview
{
    public const string ListenText = "▶ Listen", StopText = "■ Stop";

    private static readonly object Gate = new();
    private static IDisposable? _sound;
    private static AudioOut? _audio;
    private static Button? _button;
    private static object? _key;
    private static DispatcherTimer? _timer;
    private static DateTime _started;

    /// <summary>Plays a song (true) or a sound of either game from the button, or stops it when this button's is playing.
    /// Returns a note for the status line ("" when fine).</summary>
    public static string Toggle(Button button, RomBuffer? rom, bool lufia2, bool song, int id)
    {
        var key = (lufia2, song, id);
        if (Equals(_key, key)) { Stop(); return ""; }   // (the card may have been made again meanwhile)
        Stop();
        if (rom == null) return "No ROM loaded.";
        if (!SpcNative.Available) return "The sound DLL isn't available, so nothing can play.";
        try
        {
            Action<short[], int> render;
            if (lufia2)
            {
                var s = L2Sound.Create(rom);
                if (s == null) return "The game's sound driver didn't start.";
                _sound = s;
                if (song) { if (!s.PlaySong(id)) { Stop(); return $"Song {id:X2} isn't a song the game plays."; } }
                else
                {
                    if (id >= L2Sound.EffectCount) { Stop(); return $"The game has sounds 00-{L2Sound.EffectCount - 1:X2}; {id:X2} plays nothing."; }
                    s.PlaySoundEffect(id);
                }
                render = (buf, pairs) => { lock (Gate) s.Render(buf, pairs * 2); Heard(buf, pairs); };
            }
            else
            {
                var s = LufiaSound.Create(rom);
                if (s == null) return "The game's sound driver didn't start.";
                _sound = s;
                if (song)
                {
                    if (id >= LufiaSound.SongCount) { Stop(); return id == 0xFF ? "FF stops the music: there's nothing to hear." : $"The game has songs 00-{LufiaSound.SongCount - 1:X2}; {id:X2} plays nothing."; }
                    s.PlaySong(id);
                }
                else
                {
                    if ((id & 0x7F) >= SoundNames.Count(false)) { Stop(); return $"The game has sounds 00-{SoundNames.Count(false) - 1:X2}; {id:X2} plays nothing."; }
                    s.PlaySilentSong(); s.PlaySoundEffect(id);   // (the driver plays sounds only while a song runs)
                }
                render = (buf, pairs) => { lock (Gate) s.Render(buf, pairs * 2); Heard(buf, pairs); };
            }
            _audio = new AudioOut(lufia2 ? L2Sound.SampleRate : LufiaSound.SampleRate, render);
            if (!_audio.Start()) { Stop(); return "No sound device."; }
        }
        catch (Exception ex) { Stop(); return "Couldn't play: " + ex.Message; }

        _button = button; _key = key;
        button.Content = StopText;
        _started = DateTime.Now;
        if (!song)
        {
            // a sound ends by itself: when what is played has been quiet for a moment (a sound that keeps going stops
            // after 12 seconds; one that plays nothing after 1.5)
            _timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(100) };
            _timer.Tick += (_, _) =>
            {
                double secs = (DateTime.Now - _started).TotalSeconds;
                long quiet = Environment.TickCount64 - Interlocked.Read(ref _lastLoud);
                bool heard = Interlocked.Read(ref _lastLoud) != 0;
                if (secs > 12 || heard && quiet > 400 || !heard && secs > 1.5) Stop();
            };
            _timer.Start();
        }
        return "";
    }

    private static long _lastLoud;

    /// <summary>The render thread: notes when the sound was last loud enough to hear.</summary>
    private static void Heard(short[] buf, int pairs)
    {
        for (int i = 0; i < pairs * 2; i++)
            if (buf[i] > 200 || buf[i] < -200) { Interlocked.Exchange(ref _lastLoud, Environment.TickCount64); return; }
    }

    /// <summary>
    /// An editor left the screen: what it plays stops, unless it is back a moment later (WPF unloads and loads a panel
    /// again when it moves it, such as the event editor while its page is made again).
    /// </summary>
    public static void StopWhenGone(System.Windows.FrameworkElement owner)
    {
        if (_audio == null) return;
        var t = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(400) };
        t.Tick += (_, _) => { t.Stop(); if (!owner.IsLoaded) Stop(); };
        t.Start();
    }

    /// <summary>What plays has been loud enough to hear (tests).</summary>
    public static bool WasHeard => Interlocked.Read(ref _lastLoud) != 0;

    /// <summary>Something is playing (tests).</summary>
    public static bool IsPlaying => _audio != null;

    /// <summary>Stops whatever plays (leaving an editor, playing on a Music page).</summary>
    public static void Stop()
    {
        _timer?.Stop(); _timer = null;
        _audio?.Dispose(); _audio = null;
        Interlocked.Exchange(ref _lastLoud, 0);   // (after the render thread has stopped)
        lock (Gate) { _sound?.Dispose(); _sound = null; }
        if (_button != null) _button.Content = ListenText;
        _button = null; _key = null;
    }
}
