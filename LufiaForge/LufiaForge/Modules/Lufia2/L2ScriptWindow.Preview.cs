using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using LufiaForge.Core;
using LufiaForge.Core.Lufia2;
using LufiaForge.Core.Lufia2.Snes;
using Arg = LufiaForge.Core.Lufia2.L2AiScripts.Arg;
using Kind = LufiaForge.Core.Lufia2.L2AiScripts.Kind;
using Step = LufiaForge.Core.Lufia2.L2AiScripts.Step;

namespace LufiaForge.Modules.Lufia2;

/// <summary>A small preview of the animation the script plays (the picked step's, else the script's first, else the
/// spell's or IP attack's own), played by the game's own code (<see cref="L2AnimPreview"/>) and looped.</summary>
public partial class L2ScriptWindow
{
    private int _previewId = -2, _previewToken, _previewAt;
    private List<uint[]> _previewFrames = new();
    private WriteableBitmap? _previewBitmap;
    private DispatcherTimer? _previewTimer;
    private const int LoopPause = 40;

    /// <summary>The animation a step plays (its animation field), or -1.</summary>
    private static int AnimationOf(Step s)
    {
        var d = s.Def;
        if (d == null) return -1;
        for (int i = 0; i < d.Args.Length; i++)
            if (d.Args[i] == Arg.Animation)
            {
                int at = L2AiScripts.ArgAt(d, i);
                if (at < s.Args.Length && s.Args[at] is var v && v is not (0xFF or 0xE0)) return v;
            }
        return -1;
    }

    private int AnimationShown()
    {
        if (Current is { } s && AnimationOf(s) is var a && a >= 0) return a;
        foreach (var st in _steps) if (AnimationOf(st) is var b && b >= 0) return b;
        try
        {
            if (_kind == Kind.Spell) return L2Magic.SpellAnimation(_rom, _index);
            if (_kind == Kind.IpAttack) return L2Battle.ReadIp(_rom, _index).Animation;
        }
        catch (Exception) { }
        return -1;
    }

    /// <summary>Plays the animation of the picked step (or the script's) when it is another one than the one shown.</summary>
    private void UpdatePreview()
    {
        int id = AnimationShown();
        if (id == _previewId) return;
        _previewId = id;
        _previewTimer?.Stop();
        if (id < 0 || id >= 0xFF || !L2Magic.IsAnimation(_rom, id)) { PreviewBox.Visibility = Visibility.Collapsed; return; }
        PreviewBox.Visibility = Visibility.Visible;
        string name = id < _names.Animations.Length ? _names.Animations[id] : $"animation {id:X2}";
        PreviewText.Text = $"Animation {id:X2}: {name}. Playing the game's code...";
        int token = ++_previewToken;
        var copy = new RomBuffer(_rom.ToArray(), "preview");
        var (sprite, pal) = (-1, 0);
        if (_kind is Kind.MonsterAttack or Kind.MonsterDefense)
            try { var m = L2Battle.ReadMonster(_rom, _index); (sprite, pal) = (m.Sprite, m.Palette); } catch (Exception) { }
        Task.Run(() => L2AnimPreview.Play(copy, id, new L2AnimPreview.Options(0, sprite, pal, MaxFrames: 600))).ContinueWith(t => Dispatcher.BeginInvoke(() =>
        {
            if (token != _previewToken) return;
            if (t.Exception != null) { PreviewText.Text = $"Animation {id:X2}: {name}. It can't be played here: {t.Exception.InnerException?.Message}"; return; }
            var (frames, problem) = t.Result;
            _previewFrames = frames; _previewAt = 0;
            PreviewText.Text = $"Animation {id:X2}: {name}, {frames.Count / 60.0:0.0} s" + (problem != null ? $" ({problem})" : "") +
                               ". The heroes and the battle's windows aren't drawn; the Animations page edits it.";
            if (frames.Count == 0) return;
            _previewBitmap ??= new WriteableBitmap(SnesRenderer.Width, SnesRenderer.Height, 96, 96, PixelFormats.Bgra32, null);
            PreviewImage.Source = _previewBitmap;
            if (_previewTimer == null)
            {
                _previewTimer = new DispatcherTimer(DispatcherPriority.Render) { Interval = TimeSpan.FromSeconds(1 / 60.0) };
                _previewTimer.Tick += (_, _) => PreviewTick();
                Closed += (_, _) => _previewTimer.Stop();
            }
            _previewTimer.Start();
        }));
    }

    private void PreviewTick()
    {
        if (_previewBitmap == null || _previewFrames.Count == 0) { _previewTimer?.Stop(); return; }
        // the last frame stays a moment, then it plays again
        if (_previewAt >= _previewFrames.Count + LoopPause) _previewAt = 0;
        int f = Math.Min(_previewAt, _previewFrames.Count - 1);
        _previewBitmap.WritePixels(new Int32Rect(0, 0, SnesRenderer.Width, SnesRenderer.Height), _previewFrames[f], SnesRenderer.Width * 4, 0);
        _previewAt++;
    }
}
