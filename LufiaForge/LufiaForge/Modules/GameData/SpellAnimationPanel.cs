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
        if (Ctx == null || Animation <= 0 || Animation >= SpellAnimations.Count(Ctx.Rom)) { AnimationInfo = Animation == 0 ? "No animation." : ""; if (_pendingSheets.Count == 0) StartPreview(); return; }
        try
        {
            var (idx, pal) = SpellAnimations.Sheet(Ctx.Rom, Animation);
            var px = idx.Select(i => pal[i]).ToArray();
            var bmp = BitmapSource.Create(SpellAnimations.SheetWidth, SpellAnimations.SheetHeight, 96, 96, PixelFormats.Bgra32, null, px, SpellAnimations.SheetWidth * 4);
            bmp.Freeze();
            AnimationSheet = bmp;
            var info = SpellAnimations.Describe(Ctx.Rom, Animation);
            if (_pendingSheets.Count == 0) StartPreview();
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

    // ── a new animation: up to three parts (start, middle repeated, end), each from a picture; shown and played,
    //    then made ──

    /// <summary>One part of a new animation: its picture and how it's cut into frames.</summary>
    private sealed class Part
    {
        public string? Path;
        public bool ManualGrid, ManualCount;
        public int GridW = 32, GridH = 32, Count;
        public List<SpellAnimationSheet.Frame> Frames = new();
        public string Info = "";
        public bool Error;
    }

    public static readonly string[] PartNames = { "Start", "Middle", "End" };
    public string[] PartChoices => PartNames;
    private readonly Part[] _parts = { new(), new(), new() };

    [ObservableProperty] private int _selectedPart = 1;          // a one-part animation is just a middle
    [ObservableProperty] private int _middleRepeats = 1;
    [ObservableProperty] private string _partsSummary = "";
    [ObservableProperty] private ImageSource? _pendingSheet;
    [ObservableProperty] private string _pendingInfo = "";
    [ObservableProperty] private bool _pendingIsError;
    [ObservableProperty] private bool _hasPending;
    [ObservableProperty] private ImageSource? _previewFrame;
    [ObservableProperty] private string _previewInfo = "";
    // the sheets the new animation needs (one if everything fits, else one per part) and each one's play order
    private List<(SpellAnimationSheet.Result Sheet, List<int> Order)> _pendingSheets = new();
    private List<BitmapSource> _previewFrames = new();
    private int _previewAt;
    private System.Windows.Threading.DispatcherTimer? _previewTimer;
    private bool _settingFrames;

    [RelayCommand]
    private void ImportAnimation()
    {
        if (Ctx == null || SelectedIndex < 0) return;
        var dlg = new OpenFileDialog { Title = $"Choose a picture for the {PartNames[SelectedPart].ToLower()} of the new animation", Filter = "Pictures (*.png;*.gif;*.bmp)|*.png;*.gif;*.bmp" };
        if (dlg.ShowDialog() == true) LoadPending(dlg.FileName);
    }

    /// <summary>Reads a picture for the selected part: finds its frames, shows the sheet made from all parts with a grid
    /// and plays the whole animation; nothing is written until <see cref="MakeAnimation"/>.</summary>
    public void LoadPending(string path)
    {
        var part = _parts[SelectedPart];
        part.Path = path; part.ManualGrid = false; part.ManualCount = false;
        CutPart(part);
        Combine();
    }

    /// <summary>For tests: a picture for a given part (0 start, 1 middle, 2 end).</summary>
    public void LoadPart(int part, string path) { SelectedPart = part; LoadPending(path); }

    private void CutPart(Part part)
    {
        part.Frames = new(); part.Error = false; part.Info = "";
        if (part.Path == null) return;
        string name = System.IO.Path.GetFileName(part.Path);
        try
        {
            var (argb, w, h) = LoadPicture(part.Path);
            int count = part.ManualCount ? part.Count : 0;
            var r = part.ManualGrid ? SpellAnimationSheet.Prepare(argb, w, h, part.GridW, part.GridH, count)
                                    : SpellAnimationSheet.Prepare(argb, w, h, 0, 0, count);
            part.Frames = r.FrameImages;
            if (!part.ManualGrid) { part.GridW = r.FrameImages.Max(f => f.W); part.GridH = r.FrameImages.Max(f => f.H); }
            part.Count = r.Frames;
            part.Info = $"{name}: " + string.Join(" ", r.Notes);
        }
        catch (Exception ex)
        {
            part.Info = $"{name} can't be used: {ex.Message}";
            part.Error = true;
        }
        if (SelectedPart == Array.IndexOf(_parts, part)) ShowPartFields(part);
    }

    private void ShowPartFields(Part part)
    {
        _settingFrames = true;
        FrameWidth = part.GridW; FrameHeight = part.GridH; FrameCount = part.Count;
        _settingFrames = false;
    }

    /// <summary>The parts' frames: on one sheet when they fit (start once, middle repeated, end once), else each part on
    /// a sheet of its own (the animation switches sheets between them); one frame size for all.</summary>
    private void Combine()
    {
        _pendingSheets = new(); PendingSheet = null; HasPending = false;
        var used = Enumerable.Range(0, 3).Where(i => _parts[i].Frames.Count > 0).ToList();
        PartsSummary = string.Join("   ", Enumerable.Range(0, 3).Select(i =>
            $"{PartNames[i]}: " + (_parts[i].Path == null ? "none" : _parts[i].Error ? "can't be used" : $"{_parts[i].Frames.Count} frames" + (i == 1 && MiddleRepeats > 1 ? $" x {MiddleRepeats}" : ""))));
        var sel = _parts[SelectedPart];
        string partText = sel.Path == null ? $"No picture for the {PartNames[SelectedPart].ToLower()} yet." : sel.Info;
        if (used.Count == 0)
        {
            PendingInfo = partText;
            PendingIsError = sel.Error;
            StartPreview();
            return;
        }
        int Reps(int i) => i == 1 ? Math.Clamp(MiddleRepeats, 1, 50) : 1;
        int minW = used.Max(i => _parts[i].Frames.Max(f => f.W)), minH = used.Max(i => _parts[i].Frames.Max(f => f.H));
        string how;
        try
        {
            // all on one sheet
            var frames = new List<SpellAnimationSheet.Frame>();
            var order = new List<int>();
            foreach (int i in used)
            {
                int first = frames.Count;
                frames.AddRange(_parts[i].Frames);
                for (int k = 0; k < Reps(i); k++) order.AddRange(Enumerable.Range(first, _parts[i].Frames.Count));
            }
            _pendingSheets.Add((SpellAnimationSheet.Pack(frames, null, minW, minH), order));
            how = "all on one sheet";
        }
        catch (InvalidOperationException) when (used.Count > 1)
        {
            // a sheet per part
            try
            {
                foreach (int i in used)
                {
                    var r = SpellAnimationSheet.Pack(_parts[i].Frames, null, minW, minH);
                    var order = new List<int>();
                    for (int k = 0; k < Reps(i); k++) order.AddRange(Enumerable.Range(0, _parts[i].Frames.Count));
                    _pendingSheets.Add((r, order));
                }
                how = $"too many for one sheet, so each part has its own ({used.Count} sheets; the animation switches between them)";
            }
            catch (Exception ex)
            {
                _pendingSheets = new();
                PendingInfo = (sel.Error ? partText + " " : "") + "The parts can't be used: " + ex.Message;
                PendingIsError = true;
                StartPreview();
                return;
            }
        }
        catch (Exception ex)
        {
            _pendingSheets = new();
            PendingInfo = (sel.Error ? partText + " " : "") + "It can't be used: " + ex.Message;
            PendingIsError = true;
            StartPreview();
            return;
        }
        var first0 = _pendingSheets[0].Sheet;
        int pictures = _pendingSheets.Sum(p => p.Order.Count);
        PendingSheet = Stack(_pendingSheets.Select(p => SheetWithGrid(p.Sheet.Indices, p.Sheet.Palette, p.Sheet.FrameWidth, p.Sheet.FrameHeight, p.Sheet.Frames)).ToList());
        PendingInfo = partText + $" Frames of {first0.FrameWidth} x {first0.FrameHeight}, {how}; {pictures} pictures played." +
                      " Click Make the animation to give it to this spell.";
        PendingIsError = sel.Error;
        HasPending = true;
        StartPreview();
    }

    /// <summary>Sheets one under the other, a gap between.</summary>
    private static BitmapSource Stack(List<BitmapSource> sheets)
    {
        if (sheets.Count == 1) return sheets[0];
        int w = sheets.Max(b => b.PixelWidth), h = sheets.Sum(b => b.PixelHeight) + 8 * (sheets.Count - 1);
        var px = new uint[w * h];
        int y0 = 0;
        foreach (var b in sheets)
        {
            var part = new uint[b.PixelWidth * b.PixelHeight];
            b.CopyPixels(part, b.PixelWidth * 4, 0);
            for (int y = 0; y < b.PixelHeight; y++) Array.Copy(part, y * b.PixelWidth, px, (y0 + y) * w, b.PixelWidth);
            y0 += b.PixelHeight + 8;
        }
        var bmp = BitmapSource.Create(w, h, 96, 96, PixelFormats.Bgra32, null, px, w * 4);
        bmp.Freeze();
        return bmp;
    }

    partial void OnSelectedPartChanged(int value)
    {
        ShowPartFields(_parts[value]);
        Combine();
    }

    partial void OnMiddleRepeatsChanged(int value) => Combine();

    // a new frame size: that grid for the part, all its frames; a frame count: only that many
    partial void OnFrameWidthChanged(int value) => FieldsChanged(grid: true);
    partial void OnFrameHeightChanged(int value) => FieldsChanged(grid: true);
    partial void OnFrameCountChanged(int value) => FieldsChanged(grid: false);

    private void FieldsChanged(bool grid)
    {
        var part = _parts[SelectedPart];
        if (_settingFrames || part.Path == null) return;
        if (grid) { part.ManualGrid = true; part.ManualCount = false; part.GridW = FrameWidth; part.GridH = FrameHeight; }
        else { part.ManualCount = true; part.Count = FrameCount; }
        CutPart(part);
        Combine();
    }

    partial void OnStepFramesChanged(int value) => StartPreview();

    /// <summary>Takes the selected part's picture out.</summary>
    [RelayCommand]
    private void ClearPending()
    {
        _parts[SelectedPart] = new Part();
        ShowPartFields(_parts[SelectedPart]);
        Combine();
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

    /// <summary>Plays the animation waiting to be made (all its parts in order), else this spell's animation when
    /// Lufia Forge made it (the game's own move and combine their pictures in battle: shown as their sheet only).</summary>
    private void StartPreview()
    {
        _previewTimer?.Stop();
        _previewFrames = new();
        PreviewFrame = null;
        if (Ctx == null) return;
        // pictures to play: (sheet colour numbers, palette, x, y)
        var pics = new List<(byte[] Idx, uint[] Pal, int X, int Y)>();
        int fw, fh, step;
        if (_pendingSheets.Count > 0)
        {
            var f0 = _pendingSheets[0].Sheet;
            fw = f0.FrameWidth; fh = f0.FrameHeight; step = StepFrames;
            int perRow = 8 / (fw / 16);
            foreach (var (sheet, order) in _pendingSheets)
                foreach (int f in order) pics.Add((sheet.Indices, sheet.Palette, f % perRow * fw, f / perRow * fh));
            PreviewInfo = "The new animation (not made yet).";
        }
        else if (Animation > 0 && SpellAnimations.MadeFrames(Ctx.Rom, Animation) is { } made)
        {
            fw = made.Width; fh = made.Height; step = made.Step;
            var sheets = new Dictionary<int, (byte[], uint[])>();
            foreach (var (res, x, y) in made.Frames)
            {
                if (!sheets.TryGetValue(res, out var sh)) sheets[res] = sh = SpellAnimations.SheetOfResource(Ctx.Rom, res);
                pics.Add((sh.Item1, sh.Item2, x, y));
            }
            PreviewInfo = $"Animation {Animation:X2}: {pics.Count} pictures of {fw} x {fh}" + (sheets.Count > 1 ? $" from {sheets.Count} sheets." : ".");
        }
        else
        {
            PreviewInfo = Animation > 0 ? "The game's own animations move and combine their pictures in battle, so only their sheet is shown above." : "";
            return;
        }
        const int S = 3;
        var made2 = new Dictionary<(byte[], int, int), BitmapSource>();
        foreach (var (idx, pal, cx, cy) in pics)
        {
            if (!made2.TryGetValue((idx, cx, cy), out var bmp))
            {
                var px = new uint[fw * S * fh * S];
                for (int y = 0; y < fh * S; y++) for (int x = 0; x < fw * S; x++)
                {
                    int sx = cx + x / S, sy = cy + y / S;
                    byte i = sx < SpellAnimations.SheetWidth && sy < SpellAnimations.SheetHeight ? idx[sy * SpellAnimations.SheetWidth + sx] : (byte)0;
                    px[y * fw * S + x] = i == 0 ? 0u : pal[i];
                }
                bmp = BitmapSource.Create(fw * S, fh * S, 96, 96, PixelFormats.Bgra32, null, px, fw * S * 4);
                bmp.Freeze();
                made2[(idx, cx, cy)] = bmp;
            }
            _previewFrames.Add(bmp);
        }
        if (_previewFrames.Count == 0) return;
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

    /// <summary>Makes the new animation from the parts' pictures and gives it to the selected spell.</summary>
    [RelayCommand]
    private void MakeAnimation()
    {
        if (Ctx == null || SelectedIndex < 0 || _pendingSheets.Count == 0) return;
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
        var sheets = _pendingSheets.ToList();
        var p = sheets[0].Sheet;
        int pictures = sheets.Sum(x => x.Order.Count);
        var parts = sheets.Select(x => (x.Sheet.Indices, x.Sheet.Palette, (IReadOnlyList<int>)x.Order)).ToList();
        try
        {
            Commit($"Spell {sp:X2} animation", () =>
            {
                made = SpellAnimations.ImportParts(Ctx.Rom, parts, p.FrameWidth, p.FrameHeight, StepFrames, FromBottom, flash);
                Ctx.Rom.WriteByte(SpellTablePatch.SpellOffset(Ctx.Rom, sp) + 9, (byte)made);
            });
        }
        catch (Exception ex) { PendingInfo = "Can't make it: " + ex.Message; PendingIsError = true; return; }
        if (made < 0) return;
        for (int i = 0; i < 3; i++) _parts[i] = new Part();
        _pendingSheets = new(); PendingSheet = null; HasPending = false;
        LoadSelected();
        PartsSummary = "";
        PendingInfo = $"Animation {made:X2} made ({pictures} pictures of {p.FrameWidth} x {p.FrameHeight}" + (sheets.Count > 1 ? $", {sheets.Count} sheets" : "") + ") and given to this spell. It plays above. Unsaved.";
        PendingIsError = false;
        Status = PendingInfo;
    }

    /// <summary>For tests: choose a picture (the middle) and make the animation in one go.</summary>
    public void ImportAnimationFrom(string path)
    {
        LoadPart(1, path);
        MakeAnimation();
    }
}
