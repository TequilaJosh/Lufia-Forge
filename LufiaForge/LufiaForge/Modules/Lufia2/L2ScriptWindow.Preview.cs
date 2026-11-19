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

// a small looping preview of the animation the script plays (the picked step's, else the script's first, else the
// spell's or IP attack's own), played by the game's own code (L2AnimPreview)
public partial class L2ScriptWindow
{
    private int _previewId = -2, _previewToken, _previewAt;
    private List<uint[]> _previewFrames = new();
    private WriteableBitmap? _previewBitmap;
    private DispatcherTimer? _previewTimer;
    private const int LoopPause = 40;

    // the animation a step plays (its animation field), or -1
    private static int AnimationOf(Step step)
    {
        var def = step.Def;
        if (def == null) return -1;
        for (int i = 0; i < def.Args.Length; i++)
            if (def.Args[i] == Arg.Animation)
            {
                int argIndex = L2AiScripts.ArgAt(def, i);
                if (argIndex < step.Args.Length && step.Args[argIndex] is var value && value is not (0xFF or 0xE0)) return value;
            }
        return -1;
    }

    private int AnimationShown()
    {
        if (Current is { } current && AnimationOf(current) is var animation && animation >= 0) return animation;
        foreach (var step in _steps) if (AnimationOf(step) is var stepAnimation && stepAnimation >= 0) return stepAnimation;
        try
        {
            if (_kind == Kind.Spell) return L2Magic.SpellAnimation(_rom, _index);
            if (_kind == Kind.IpAttack) return L2Battle.ReadIp(_rom, _index).Animation;
        }
        catch (Exception) { }
        return -1;
    }

    // plays the animation of the picked step (or the script's) if it's a different one than the one shown
    private void UpdatePreview()
    {
        int id = AnimationShown();
        if (id == _previewId || _previewClosed) return;
        // closing stops it for good, so a preview still being made never starts its timer (that used to keep the
        // closed window and its frames alive, drawing at 60 Hz)
        if (!_previewHooked)
        {
            _previewHooked = true;
            Closed += (_, _) => { _previewClosed = true; _previewToken++; _previewTimer?.Stop(); _previewFrames = new(); };
        }
        _previewId = id;
        _previewTimer?.Stop();
        if (id < 0 || id >= 0xFF || !L2Magic.IsAnimation(_rom, id)) { PreviewBox.Visibility = Visibility.Collapsed; return; }
        PreviewBox.Visibility = Visibility.Visible;
        string name = id < _names.Animations.Length ? _names.Animations[id] : $"animation {id:X2}";
        PreviewText.Text = $"Animation {id:X2}: {name}. Playing the game's code...";
        int token = ++_previewToken;
        var copy = new RomBuffer(_rom.ToArray(), "preview");
        var (sprite, palette) = (-1, 0);
        if (_kind is Kind.MonsterAttack or Kind.MonsterDefense)
            try { var monster = L2Battle.ReadMonster(_rom, _index); (sprite, palette) = (monster.Sprite, monster.Palette); } catch (Exception) { }
        Task.Run(() => L2AnimPreview.Play(copy, id, new L2AnimPreview.Options(0, sprite, palette, MaxFrames: 600))).ContinueWith(task => Dispatcher.BeginInvoke(() =>
        {
            if (token != _previewToken || _previewClosed) return;
            if (task.Exception != null) { PreviewText.Text = $"Animation {id:X2}: {name}. It can't be played here: {task.Exception.InnerException?.Message}"; return; }
            var (frames, problem) = task.Result;
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
            }
            _previewTimer.Start();
        }));
    }

    private bool _previewHooked, _previewClosed;

    private void PreviewTick()
    {
        if (_previewBitmap == null || _previewFrames.Count == 0) { _previewTimer?.Stop(); return; }
        // the last frame stays a moment, then it plays again
        if (_previewAt >= _previewFrames.Count + LoopPause) _previewAt = 0;
        int frame = Math.Min(_previewAt, _previewFrames.Count - 1);
        _previewBitmap.WritePixels(new Int32Rect(0, 0, SnesRenderer.Width, SnesRenderer.Height), _previewFrames[frame], SnesRenderer.Width * 4, 0);
        _previewAt++;
    }
}
