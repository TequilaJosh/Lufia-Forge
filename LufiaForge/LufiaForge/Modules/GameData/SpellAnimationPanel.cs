using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LufiaForge.Core;
using LufiaForge.Core.Battle;
using Microsoft.Win32;
using System.Collections.ObjectModel;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace LufiaForge.Modules.GameData;

/// <summary>The spell's battle animation (+9) and sound (+19): picked from a list, its sheet shown, exported, and
/// new animations made from imported sheets (see <see cref="SpellAnimations"/>).</summary>
public partial class SpellEditorViewModel
{
    public ObservableCollection<string> AnimationChoices { get; } = new();
    [ObservableProperty] private int _animation;
    [ObservableProperty] private int _sound;
    [ObservableProperty] private ImageSource? _animationSheet;
    [ObservableProperty] private string _animationInfo = "";
    // import settings
    [ObservableProperty] private int _frameWidth = 32;
    [ObservableProperty] private int _frameHeight = 32;
    [ObservableProperty] private int _frameCount = 8;
    [ObservableProperty] private int _stepFrames = 4;
    [ObservableProperty] private bool _fromBottom;
    [ObservableProperty] private string _flashColour = "";
    private bool _animLoading;

    private void FillAnimations(RomBuffer rom)
    {
        var users = new Dictionary<int, List<string>>();
        for (int sp = 1, n = SpellTablePatch.SpellCount(rom); sp < n; sp++)
        {
            int a = rom.ReadByte(SpellTablePatch.SpellOffset(rom, sp) + 9);
            if (!users.TryGetValue(a, out var l)) users[a] = l = new();
            l.Add(Ctx!.ReadName(SpellTablePatch.SpellOffset(rom, sp), GameDataOffsets.SpellNameLen).Replace('@', ' ').Trim());
        }
        AnimationChoices.Clear();
        for (int a = 0, n = SpellAnimations.Count(rom); a < n; a++)
        {
            string who = a == 0 ? "none" : users.TryGetValue(a, out var l) ? string.Join(", ", l.Take(3)) + (l.Count > 3 ? "..." : "")
                       : a >= SpellAnimations.OriginalCount ? "new" : a <= 3 ? "weapon hit" : "items / other";
            AnimationChoices.Add($"{a:X2}  {who}");
        }
    }

    private void LoadAnimation(RomBuffer rom, int o)
    {
        _animLoading = true;
        FillAnimations(rom);
        Animation = rom.ReadByte(o + 9);
        Sound = rom.ReadByte(o + 19);
        _animLoading = false;
        DrawAnimation();
    }

    partial void OnAnimationChanged(int value) { if (!_animLoading) DrawAnimation(); }

    private void DrawAnimation()
    {
        AnimationSheet = null;
        if (Ctx == null || Animation <= 0 || Animation >= SpellAnimations.Count(Ctx.Rom)) { AnimationInfo = Animation == 0 ? "No animation." : ""; if (_pending == null) StartPreview(); return; }
        try
        {
            var (idx, pal) = SpellAnimations.Sheet(Ctx.Rom, Animation);
            var px = idx.Select(i => pal[i]).ToArray();
            var bmp = BitmapSource.Create(SpellAnimations.SheetWidth, SpellAnimations.SheetHeight, 96, 96, PixelFormats.Bgra32, null, px, SpellAnimations.SheetWidth * 4);
            bmp.Freeze();
            AnimationSheet = bmp;
            var info = SpellAnimations.Describe(Ctx.Rom, Animation);
            if (_pending == null) StartPreview();
            AnimationInfo = $"Animation {Animation:X2}: sheet resource {info.Resource:X3}, {info.Tracks} track(s), {info.Width} x {info.Height} px" +
                            (info.FromBottom ? ", from the target's feet" : "") +
                            ". Spells can share an animation (and animations share sheets): import a new one instead of changing a shared sheet.";
        }
        catch (Exception ex) { AnimationInfo = "Can't show this animation: " + ex.Message; }
    }

    [RelayCommand]
    private void ExportAnimation()
    {
        if (Ctx == null || Animation <= 0) return;
        var dlg = new SaveFileDialog { Title = "Export animation sheet", Filter = "PNG picture (*.png)|*.png", FileName = $"animation {Animation:X2}.png" };
        if (dlg.ShowDialog() == true) ExportAnimationTo(dlg.FileName);
    }

    public void ExportAnimationTo(string path)
    {
        var (idx, pal) = SpellAnimations.Sheet(Ctx!.Rom, Animation);
        IndexedPng.Save(path, SpellAnimations.SheetWidth, SpellAnimations.SheetHeight, idx, pal, firstTransparent: true);
        Status = $"Saved {path}: 128 x 64, 8 x 4 cells of 16 x 16, 16 colours (colour 0 = see-through).";
    }

    // ── a new animation: choose a picture (shown and played), then make it ──

    [ObservableProperty] private ImageSource? _pendingSheet;
    [ObservableProperty] private string _pendingInfo = "";
    [ObservableProperty] private bool _pendingIsError;
    [ObservableProperty] private bool _hasPending;
    [ObservableProperty] private ImageSource? _previewFrame;
    [ObservableProperty] private string _previewInfo = "";
    private string? _pendingPath;
    private SpellAnimationSheet.Result? _pending;
    private List<BitmapSource> _previewFrames = new();
    private int _previewAt;
    private System.Windows.Threading.DispatcherTimer? _previewTimer;
    private bool _settingFrames;

    [RelayCommand]
    private void ImportAnimation()
    {
        if (Ctx == null || SelectedIndex < 0) return;
        var dlg = new OpenFileDialog { Title = "Choose a picture for the new animation", Filter = "Pictures (*.png;*.gif;*.bmp)|*.png;*.gif;*.bmp" };
        if (dlg.ShowDialog() == true) LoadPending(dlg.FileName);
    }

    /// <summary>Reads a picture for a new animation: finds its frames, shows the game's sheet made from it with a grid
    /// and plays it; nothing is written until <see cref="MakeAnimation"/>.</summary>
    public void LoadPending(string path)
    {
        _pendingPath = path;
        PreparePending();
    }

    private void PreparePending()
    {
        _pending = null;
        PendingSheet = null;
        HasPending = false;
        if (_pendingPath == null) return;
        try
        {
            var (argb, w, h) = LoadPicture(_pendingPath);
            var r = SpellAnimationSheet.Prepare(argb, w, h, FrameWidth, FrameHeight, FrameCount);
            _pending = r;
            _settingFrames = true;
            FrameWidth = r.FrameWidth; FrameHeight = r.FrameHeight; FrameCount = r.Frames;
            _settingFrames = false;
            PendingSheet = SheetWithGrid(r.Indices, r.Palette, r.FrameWidth, r.FrameHeight, r.Frames);
            PendingInfo = $"{System.IO.Path.GetFileName(_pendingPath)}: " + string.Join(" ", r.Notes) +
                          $" Outlined in gold above, playing below. Click Make the animation to give it to this spell.";
            PendingIsError = false;
            HasPending = true;
        }
        catch (Exception ex)
        {
            PendingInfo = $"{System.IO.Path.GetFileName(_pendingPath)} can't be used: {ex.Message}";
            PendingIsError = true;
        }
        StartPreview();
    }

    partial void OnFrameWidthChanged(int value) { if (!_settingFrames && _pendingPath != null) PreparePending(); }
    partial void OnFrameHeightChanged(int value) { if (!_settingFrames && _pendingPath != null) PreparePending(); }
    partial void OnFrameCountChanged(int value) { if (!_settingFrames && _pendingPath != null) PreparePending(); }
    partial void OnStepFramesChanged(int value) => StartPreview();

    [RelayCommand]
    private void ClearPending()
    {
        _pendingPath = null; _pending = null; PendingSheet = null; HasPending = false; PendingInfo = ""; PendingIsError = false;
        StartPreview();
    }

    /// <summary>The picture as 0xAARRGGBB pixels (an indexed PNG's colour 0 is see-through).</summary>
    private static (uint[] Argb, int W, int H) LoadPicture(string path)
    {
        if (IndexedPng.TryLoad(path) is { } png)
            return (png.Indices.Select(i => i == 0 ? 0u : png.Palette[i] | 0xFF000000u).ToArray(), png.Width, png.Height);
        var src = new FormatConvertedBitmap(new BitmapImage(new Uri(path)), PixelFormats.Bgra32, null, 0);
        var px = new uint[src.PixelWidth * src.PixelHeight];
        src.CopyPixels(px, src.PixelWidth * 4, 0);
        return (px, src.PixelWidth, src.PixelHeight);
    }

    /// <summary>The sheet at 2x with its frames outlined (the ones used in gold, the rest dim).</summary>
    private static BitmapSource SheetWithGrid(byte[] idx, uint[] pal, int fw, int fh, int frames)
    {
        const int S = 2, W = SpellAnimations.SheetWidth * S, H = SpellAnimations.SheetHeight * S;
        var px = new uint[W * H];
        for (int y = 0; y < H; y++) for (int x = 0; x < W; x++)
        {
            byte i = idx[y / S * SpellAnimations.SheetWidth + x / S];
            px[y * W + x] = i == 0 ? 0xFF1C1430u : pal[i];
        }
        int perRow = 8 / (fw / 16), rows = 4 / (fh / 16);
        for (int f = 0; f < perRow * rows; f++)
        {
            uint c = f < frames ? 0xFFF0C040u : 0xFF4A3E60u;
            int x0 = f % perRow * fw * S, y0 = f / perRow * fh * S, x1 = x0 + fw * S - 1, y1 = y0 + fh * S - 1;
            for (int x = x0; x <= x1; x++) { px[y0 * W + x] = c; px[y1 * W + x] = c; }
            for (int y = y0; y <= y1; y++) { px[y * W + x0] = c; px[y * W + x1] = c; }
        }
        var bmp = BitmapSource.Create(W, H, 96, 96, PixelFormats.Bgra32, null, px, W * 4);
        bmp.Freeze();
        return bmp;
    }

    /// <summary>Plays the picture waiting to be made into an animation, else this spell's animation when Lufia Forge
    /// made it (the game's own move and combine their pictures in battle: shown as their sheet only).</summary>
    private void StartPreview()
    {
        _previewTimer?.Stop();
        _previewFrames = new();
        PreviewFrame = null;
        if (Ctx == null) return;
        byte[] idx; uint[] pal; int fw, fh, step; List<(int X, int Y)> cells;
        if (_pending != null)
        {
            idx = _pending.Indices; pal = _pending.Palette; fw = _pending.FrameWidth; fh = _pending.FrameHeight; step = StepFrames;
            int perRow = 8 / (fw / 16);
            cells = Enumerable.Range(0, _pending.Frames).Select(f => (f % perRow * fw, f / perRow * fh)).ToList();
            PreviewInfo = "The new animation (not made yet).";
        }
        else if (Animation > 0 && SpellAnimations.MadeFrames(Ctx.Rom, Animation) is { } made)
        {
            (idx, pal) = SpellAnimations.Sheet(Ctx.Rom, Animation);
            fw = made.Width; fh = made.Height; step = made.Step; cells = made.Frames;
            PreviewInfo = $"Animation {Animation:X2}: {cells.Count} frames of {fw} x {fh}.";
        }
        else
        {
            PreviewInfo = Animation > 0 ? "The game's own animations move and combine their pictures in battle, so only their sheet is shown above." : "";
            return;
        }
        const int S = 3;
        foreach (var (cx, cy) in cells)
        {
            var px = new uint[fw * S * fh * S];
            for (int y = 0; y < fh * S; y++) for (int x = 0; x < fw * S; x++)
            {
                int sx = cx + x / S, sy = cy + y / S;
                byte i = sx < SpellAnimations.SheetWidth && sy < SpellAnimations.SheetHeight ? idx[sy * SpellAnimations.SheetWidth + sx] : (byte)0;
                px[y * fw * S + x] = i == 0 ? 0u : pal[i];
            }
            var bmp = BitmapSource.Create(fw * S, fh * S, 96, 96, PixelFormats.Bgra32, null, px, fw * S * 4);
            bmp.Freeze();
            _previewFrames.Add(bmp);
        }
        _previewAt = 0;
        PreviewFrame = _previewFrames[0];
        _previewTimer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(Math.Clamp(step, 1, 15) * 1000.0 / 60) };
        int pause = 0;
        _previewTimer.Tick += (_, _) =>
        {
            if (_previewFrames.Count == 0) return;
            if (pause > 0) { pause--; if (pause == 0) { _previewAt = 0; PreviewFrame = _previewFrames[0]; } return; }
            _previewAt++;
            if (_previewAt >= _previewFrames.Count) { pause = 4; PreviewFrame = null; return; }   // a short gap, then again
            PreviewFrame = _previewFrames[_previewAt];
        };
        _previewTimer.Start();
    }

    /// <summary>Makes the new animation from the chosen picture and gives it to the selected spell.</summary>
    [RelayCommand]
    private void MakeAnimation()
    {
        if (Ctx == null || SelectedIndex < 0 || _pending == null) return;
        uint? flash = null;
        if (!string.IsNullOrWhiteSpace(FlashColour))
        {
            var t = FlashColour.Trim().TrimStart('#');
            if (t.Length != 6 || !uint.TryParse(t, System.Globalization.NumberStyles.HexNumber, null, out uint rgb))
            {
                PendingInfo = "Flash colour: a web colour like #FFE080, or empty for none.";
                PendingIsError = true;
                return;
            }
            flash = 0xFF000000u | rgb;
        }
        int sp = SelectedIndex, made = -1;
        var p = _pending;
        var opts = new SpellAnimations.ImportOptions(p.FrameWidth, p.FrameHeight, p.Frames, StepFrames, FromBottom, flash);
        Commit($"Spell {sp:X2} animation", () =>
        {
            made = SpellAnimations.Import(Ctx.Rom, p.Indices, p.Palette, opts);
            Ctx.Rom.WriteByte(SpellTablePatch.SpellOffset(Ctx.Rom, sp) + 9, (byte)made);
        });
        if (made < 0) return;
        _pendingPath = null; _pending = null; PendingSheet = null; HasPending = false;
        LoadSelected();
        PendingInfo = $"Animation {made:X2} made ({p.Frames} frames of {p.FrameWidth} x {p.FrameHeight}) and given to this spell. It plays below. Unsaved.";
        PendingIsError = false;
        Status = PendingInfo;
    }

    /// <summary>For tests: choose a picture and make the animation in one go.</summary>
    public void ImportAnimationFrom(string path)
    {
        LoadPending(path);
        MakeAnimation();
    }
}
