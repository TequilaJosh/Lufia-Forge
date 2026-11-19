using Button = System.Windows.Controls.Button;
using System.Windows.Threading;
using LufiaForge.Core;
using LufiaForge.Core.Audio;

namespace LufiaForge.Modules.Common;

// the "Listen" button of a song or sound in the event editors. plays it with the game's own sound driver (the same
// emulated sound chip the Music pages use). only one thing plays at a time. the button that started it says "Stop"
// while it plays, and clicking it stops it. a song plays until you stop it, a sound until it's done
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

    // plays a song (song = true) or a sound of either game from the button, or stops it if it's this button's that's
    // playing. returns a note for the status line ("" if all went fine)
    public static string Toggle(Button button, RomBuffer? rom, bool lufia2, bool song, int id)
    {
        var key = (lufia2, song, id);
        if (Equals(_key, key)) { Stop(); return ""; }   // (the card might have been rebuilt in the meantime)
        Stop();
        if (rom == null) return "No ROM loaded.";
        if (!SpcNative.Available) return "The sound DLL isn't available, so nothing can play.";
        try
        {
            Action<short[], int> render;
            if (lufia2)
            {
                var driver = L2Sound.Create(rom);
                if (driver == null) return "The game's sound driver didn't start.";
                _sound = driver;
                if (song) { if (!driver.PlaySong(id)) { Stop(); return $"Song {id:X2} isn't a song the game plays."; } }
                else
                {
                    if (id >= L2Sound.EffectCount) { Stop(); return $"The game has sounds 00-{L2Sound.EffectCount - 1:X2}; {id:X2} plays nothing."; }
                    driver.PlaySoundEffect(id);
                }
                render = (samples, pairs) => { lock (Gate) driver.Render(samples, pairs * 2); Heard(samples, pairs); };
            }
            else
            {
                var driver = LufiaSound.Create(rom);
                if (driver == null) return "The game's sound driver didn't start.";
                _sound = driver;
                if (song)
                {
                    if (id >= LufiaSound.SongCount) { Stop(); return id == 0xFF ? "FF stops the music: there's nothing to hear." : $"The game has songs 00-{LufiaSound.SongCount - 1:X2}; {id:X2} plays nothing."; }
                    driver.PlaySong(id);
                }
                else
                {
                    if ((id & 0x7F) >= SoundNames.Count(false)) { Stop(); return $"The game has sounds 00-{SoundNames.Count(false) - 1:X2}; {id:X2} plays nothing."; }
                    driver.PlaySilentSong(); driver.PlaySoundEffect(id);   // (the driver only plays sounds while a song runs)
                }
                render = (samples, pairs) => { lock (Gate) driver.Render(samples, pairs * 2); Heard(samples, pairs); };
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
            // a sound ends by itself once it's been quiet for a moment (a sound that keeps going stops after 12
            // seconds, one that plays nothing after 1.5)
            _timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(100) };
            _timer.Tick += (_, _) =>
            {
                double seconds = (DateTime.Now - _started).TotalSeconds;
                long quiet = Environment.TickCount64 - Interlocked.Read(ref _lastLoud);
                bool heard = Interlocked.Read(ref _lastLoud) != 0;
                if (seconds > 12 || heard && quiet > 400 || !heard && seconds > 1.5) Stop();
            };
            _timer.Start();
        }
        return "";
    }

    private static long _lastLoud;

    // runs on the render thread: notes when the sound was last loud enough to hear
    private static void Heard(short[] samples, int pairs)
    {
        for (int i = 0; i < pairs * 2; i++)
            if (samples[i] > 200 || samples[i] < -200) { Interlocked.Exchange(ref _lastLoud, Environment.TickCount64); return; }
    }

    // an editor left the screen, so whatever it plays stops, unless it's back a moment later (WPF unloads and loads a
    // panel again when it moves it, like the event editor while its page gets rebuilt)
    public static void StopWhenGone(System.Windows.FrameworkElement owner)
    {
        if (_audio == null) return;
        var delay = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(400) };
        delay.Tick += (_, _) => { delay.Stop(); if (!owner.IsLoaded) Stop(); };
        delay.Start();
    }

    // what played was loud enough to hear (for tests)
    public static bool WasHeard => Interlocked.Read(ref _lastLoud) != 0;

    // something is playing (for tests)
    public static bool IsPlaying => _audio != null;

    // stops whatever is playing (leaving an editor, playing something on a Music page)
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
