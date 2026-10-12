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
        if (Ctx == null || Animation <= 0 || Animation >= SpellAnimations.Count(Ctx.Rom)) { AnimationInfo = Animation == 0 ? "No animation." : ""; return; }
        try
        {
            var (idx, pal) = SpellAnimations.Sheet(Ctx.Rom, Animation);
            var px = idx.Select(i => pal[i]).ToArray();
            var bmp = BitmapSource.Create(SpellAnimations.SheetWidth, SpellAnimations.SheetHeight, 96, 96, PixelFormats.Bgra32, null, px, SpellAnimations.SheetWidth * 4);
            bmp.Freeze();
            AnimationSheet = bmp;
            var info = SpellAnimations.Describe(Ctx.Rom, Animation);
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

    [RelayCommand]
    private void ImportAnimation()
    {
        if (Ctx == null || SelectedIndex < 0) return;
        var dlg = new OpenFileDialog { Title = "Import animation sheet", Filter = "Pictures (*.png;*.gif;*.bmp)|*.png;*.gif;*.bmp" };
        if (dlg.ShowDialog() == true) ImportAnimationFrom(dlg.FileName);
    }

    /// <summary>Makes a new animation from a sheet and gives it to the selected spell.</summary>
    public void ImportAnimationFrom(string path)
    {
        if (Ctx == null || SelectedIndex < 0) return;
        uint? flash = null;
        if (!string.IsNullOrWhiteSpace(FlashColour))
        {
            var t = FlashColour.Trim().TrimStart('#');
            if (t.Length != 6 || !uint.TryParse(t, System.Globalization.NumberStyles.HexNumber, null, out uint rgb))
            {
                Status = "Flash colour: a web colour like #FFE080, or empty for none.";
                return;
            }
            flash = 0xFF000000u | rgb;
        }
        byte[] idx; uint[] pal;
        if (IndexedPng.TryLoad(path) is { } png)
        {
            if (png.Width != SpellAnimations.SheetWidth || png.Height != SpellAnimations.SheetHeight)
            {
                Status = $"The sheet must be {SpellAnimations.SheetWidth} x {SpellAnimations.SheetHeight} pixels (this one is {png.Width} x {png.Height}).";
                return;
            }
            if (png.Indices.Any(i => i > 15)) { Status = "The sheet uses more than 16 colours; save it as a 16-colour indexed picture."; return; }
            idx = png.Indices;
            pal = png.Palette.Concat(new uint[16]).Take(16).ToArray();
        }
        else
        {
            // a full-colour picture: up to 15 colours plus transparency make the palette
            var src = new FormatConvertedBitmap(new BitmapImage(new Uri(path)), PixelFormats.Bgra32, null, 0);
            if (src.PixelWidth != SpellAnimations.SheetWidth || src.PixelHeight != SpellAnimations.SheetHeight)
            {
                Status = $"The sheet must be {SpellAnimations.SheetWidth} x {SpellAnimations.SheetHeight} pixels.";
                return;
            }
            var px = new uint[SpellAnimations.SheetWidth * SpellAnimations.SheetHeight];
            src.CopyPixels(px, SpellAnimations.SheetWidth * 4, 0);
            var colours = px.Where(c => c >> 24 >= 128).Select(c => c & 0xF8F8F8u).Distinct().ToList();
            if (colours.Count > 15) { Status = $"The sheet has {colours.Count} colours; an animation can use 15 (plus see-through)."; return; }
            pal = new uint[16];
            for (int i = 0; i < colours.Count; i++) pal[i + 1] = 0xFF000000u | colours[i];
            idx = px.Select(c => c >> 24 < 128 ? (byte)0 : (byte)(colours.IndexOf(c & 0xF8F8F8u) + 1)).ToArray();
        }
        int sp = SelectedIndex, made = -1;
        var opts = new SpellAnimations.ImportOptions(FrameWidth, FrameHeight, FrameCount, StepFrames, FromBottom, flash);
        Commit($"Spell {sp:X2} animation", () =>
        {
            made = SpellAnimations.Import(Ctx.Rom, idx, pal, opts);
            Ctx.Rom.WriteByte(SpellTablePatch.SpellOffset(Ctx.Rom, sp) + 9, (byte)made);
        });
        if (made < 0) return;
        LoadSelected();
        Status = $"Animation {made:X2} made from the sheet and given to this spell ({FrameCount} frames of {FrameWidth} x {FrameHeight}). Unsaved.";
    }
}
