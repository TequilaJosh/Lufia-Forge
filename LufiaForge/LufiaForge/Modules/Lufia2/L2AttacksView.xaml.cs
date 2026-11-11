using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using LufiaForge.Core;
using LufiaForge.Core.Lufia2;
using LufiaForge.Core.Lufia2.Snes;
using Button = System.Windows.Controls.Button;
using ContextMenu = System.Windows.Controls.ContextMenu;
using MenuItem = System.Windows.Controls.MenuItem;

namespace LufiaForge.Modules.Lufia2;

public partial class L2AttacksView : UserControl
{
    private L2AttacksPage? _vm;
    private int _shownAnim = -2, _shownMonster = -2, _token, _at;
    private List<uint[]> _frames = new();
    private WriteableBitmap? _bitmap;
    private readonly DispatcherTimer _timer = new(DispatcherPriority.Render) { Interval = TimeSpan.FromSeconds(1 / 60.0) };
    private const int LoopPause = 40;

    public L2AttacksView()
    {
        InitializeComponent();
        _timer.Tick += (_, _) => Tick();
        DataContextChanged += (_, _) =>
        {
            if (_vm != null) _vm.PropertyChanged -= VmChanged;
            _vm = DataContext as L2AttacksPage;
            if (_vm != null) _vm.PropertyChanged += VmChanged;
        };
        IsVisibleChanged += (_, e) => { if (e.NewValue is true) UpdatePreview(); else _timer.Stop(); };
        Unloaded += (_, _) => { _timer.Stop(); Common.SoundPreview.StopWhenGone(this); };
    }

    private void VmChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(L2AttacksPage.PreviewAnimation) or nameof(L2AttacksPage.PreviewMonster)) Dispatcher.BeginInvoke(UpdatePreview);
    }

    private void AddEffect_Click(object sender, RoutedEventArgs e)
    {
        if (_vm == null) return;
        var menu = new ContextMenu { PlacementTarget = AddEffectButton };
        foreach (var k in _vm.EffectKinds)
        {
            var item = new MenuItem { Header = k.Label };
            var kind = k;
            item.Click += (_, _) => _vm.AddEffectCommand.Execute(kind);
            menu.Items.Add(item);
        }
        menu.IsOpen = true;
    }

    private void Listen_Click(object sender, RoutedEventArgs e)
    {
        if (_vm?.CurrentRom is not { } rom || sender is not Button b) return;
        if (_vm.Sound < 0) { _vm.Status = "It has no sound of its own."; return; }
        string note = Common.SoundPreview.Toggle(b, rom, lufia2: true, song: false, _vm.Sound);
        if (note.Length > 0) _vm.Status = note;
    }

    // ── the animation preview (the game's own code, as in the battle script window) ──

    private void UpdatePreview()
    {
        if (_vm?.CurrentRom is not { } rom || !IsVisible) return;
        int id = _vm.PreviewAnimation, monster = _vm.PreviewMonster;
        if (id == _shownAnim && monster == _shownMonster) { if (_frames.Count > 0) _timer.Start(); return; }
        _shownAnim = id; _shownMonster = monster;
        _timer.Stop();
        if (id < 0 || id >= 0xFF || !L2Magic.IsAnimation(rom, id)) { PreviewBox.Visibility = Visibility.Collapsed; PreviewText.Text = ""; return; }
        PreviewBox.Visibility = Visibility.Visible;
        PreviewText.Text = $"Animation {id:X2}: playing the game's code...";
        int token = ++_token;
        var copy = new RomBuffer(rom.ToArray(), "preview");
        var (sprite, pal) = (-1, 0);
        try { var m = L2Battle.ReadMonster(rom, monster); (sprite, pal) = (m.Sprite, m.Palette); } catch (Exception) { }
        Task.Run(() => L2AnimPreview.Play(copy, id, new L2AnimPreview.Options(0, sprite, pal, MaxFrames: 600))).ContinueWith(t => Dispatcher.BeginInvoke(() =>
        {
            if (token != _token) return;
            if (t.Exception != null) { PreviewText.Text = $"Animation {id:X2} can't be played here: {t.Exception.InnerException?.Message}"; return; }
            var (frames, problem) = t.Result;
            _frames = frames; _at = 0;
            PreviewText.Text = $"Animation {id:X2}, {frames.Count / 60.0:0.0} s" + (problem != null ? $" ({problem})" : "") + ". The heroes and windows aren't drawn.";
            if (frames.Count == 0) return;
            _bitmap ??= new WriteableBitmap(SnesRenderer.Width, SnesRenderer.Height, 96, 96, PixelFormats.Bgra32, null);
            PreviewImage.Source = _bitmap;
            _timer.Start();
        }));
    }

    private void Tick()
    {
        if (_bitmap == null || _frames.Count == 0) { _timer.Stop(); return; }
        if (_at >= _frames.Count + LoopPause) _at = 0;
        int f = Math.Min(_at, _frames.Count - 1);
        _bitmap.WritePixels(new Int32Rect(0, 0, SnesRenderer.Width, SnesRenderer.Height), _frames[f], SnesRenderer.Width * 4, 0);
        _at++;
    }
}
