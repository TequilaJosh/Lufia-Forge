using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LufiaForge.Core;
using LufiaForge.Core.Battle;
using Microsoft.Win32;
using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Color = System.Windows.Media.Color;
using Colors = System.Windows.Media.Colors;

namespace LufiaForge.Modules.GameData;

/// <summary>The monster's battle picture (see <see cref="MonsterGraphics"/>): shown, exported and imported.</summary>
public partial class MonsterEditorViewModel
{
    [ObservableProperty] private ImageSource? _picture;
    [ObservableProperty] private string _pictureInfo = "";

    partial void OnGraphicChanged(int value) => DrawPicture();
    partial void OnPaletteChanged(int value) => DrawPicture();

    private MonsterGraphics.Picture? LoadPicture()
    {
        if (Ctx == null || Graphic < 0 || Graphic >= MonsterGraphics.Count) return null;
        try { return MonsterGraphics.Load(Ctx.Rom, Graphic); } catch { return null; }
    }

    /// <summary>The other monsters drawn with the same graphic (and which palette they use).</summary>
    private List<(int Id, string Name, int Palette)> SharedWith()
    {
        var list = new List<(int, string, int)>();
        if (Ctx == null) return list;
        for (int i = 0; i < GameDataOffsets.MonsterCount; i++)
        {
            if (i == SelectedIndex) continue;
            int o = Ctx.MonsterOffset(i);
            if (Ctx.Rom.ReadByte(o + 14) != Graphic) continue;
            list.Add((i, Ctx.ReadName(o, GameDataOffsets.MonsterNameLen).Replace('@', ' ').Trim(), Ctx.Rom.ReadByte(o + 15) & 1));
        }
        return list;
    }

    private void DrawPicture()
    {
        var p = LoadPicture();
        if (p == null)
        {
            Picture = null;
            PictureInfo = Graphic >= MonsterGraphics.Count ? $"Graphic {Graphic:X2} is not a monster picture (00-{MonsterGraphics.Count - 1:X2})." : "";
            return;
        }
        var (px, w, h) = MonsterGraphics.Render(p, Palette);
        var bmp = BitmapSource.Create(w, h, 96, 96, PixelFormats.Bgra32, null, px, w * 4);
        bmp.Freeze();
        Picture = bmp;
        var shared = SharedWith();
        PictureInfo = $"Graphic {Graphic:X2} (resource {MonsterGraphics.Resource(Graphic):X3}): {w} x {h} pixels, palette {Palette & 1}." +
                      (shared.Count == 0 ? "" : " Also used by " + string.Join(", ", shared.Select(s => $"{s.Name} (palette {s.Palette})").Distinct()) +
                       " - a new picture changes them too.") +
                      (Graphic >= 0x60 && Graphic <= 0x64
                          ? " Sinistral picture: in battle the game animates colours 1-3 itself (Gades' aura, table at $0A:C0D2), whatever this palette says."
                          : "");
    }

    [RelayCommand]
    private void ExportPicture()
    {
        if (LoadPicture() is not { } p || SelectedIndex < 0) return;
        var dlg = new SaveFileDialog
        {
            Title = "Export monster picture", Filter = "PNG picture (*.png)|*.png",
            FileName = $"monster {Ctx!.ReadName(Ctx.MonsterOffset(SelectedIndex), GameDataOffsets.MonsterNameLen).Replace('@', ' ').Trim()}.png"
        };
        if (dlg.ShowDialog() != true) return;
        IndexedPng.Save(dlg.FileName, p.PixelWidth, p.PixelHeight, MonsterGraphics.Indices(p), MonsterGraphics.Colours(p, Palette), firstTransparent: true);
        Status = $"Saved {dlg.FileName}: an indexed picture (16 colours, colour 0 = see-through). Keep it indexed when editing; " +
                 "changing the colours of the palette changes the monster's colours.";
    }

    [RelayCommand]
    private void ImportPicture()
    {
        if (Ctx == null || SelectedIndex < 0 || LoadPicture() is not { } old) return;
        var dlg = new OpenFileDialog { Title = "Import monster picture", Filter = "Pictures (*.png;*.bmp;*.gif)|*.png;*.bmp;*.gif" };
        if (dlg.ShowDialog() != true) return;

        var ip = IndexedPng.TryLoad(dlg.FileName);
        BitmapSource? src = null;
        if (ip == null)
            try { src = new BitmapImage(new Uri(dlg.FileName)); }
            catch (Exception ex) { Status = $"Could not read the picture: {ex.Message}"; return; }
        int w = ip?.Width ?? src!.PixelWidth, h = ip?.Height ?? src!.PixelHeight;
        if (w % 16 != 0 || h % 16 != 0 || w > MonsterGraphics.MaxWidth * 16 || h > MonsterGraphics.MaxHeight * 16)
        {
            Status = $"The picture must be a multiple of 16 pixels wide and high, at most {MonsterGraphics.MaxWidth * 16} x {MonsterGraphics.MaxHeight * 16} " +
                     $"(this one is {w} x {h}; the current picture is {old.PixelWidth} x {old.PixelHeight}).";
            return;
        }

        int row = Palette & 1;
        var palettes = old.Data.Take(0x40).ToArray();
        var idx = new byte[w * h];
        string how;
        if (ip is { } png && png.Palette.Length <= 16)
        {
            // colour numbers as they are; the picture's palette becomes the monster's palette
            idx = png.Indices;
            for (int i = 1; i < 16; i++)
            {
                ushort v = MonsterGraphics.ToBgr555(i < png.Palette.Length ? png.Palette[i] : 0xFF000000u);
                palettes[row * 32 + i * 2] = (byte)v; palettes[row * 32 + i * 2 + 1] = (byte)(v >> 8);
            }
            how = "colour numbers kept, palette taken from the picture";
        }
        else
        {
            if (src == null) try { src = new BitmapImage(new Uri(dlg.FileName)); } catch (Exception ex) { Status = $"Could not read the picture: {ex.Message}"; return; }
            var bgra = new FormatConvertedBitmap(src, PixelFormats.Bgra32, null, 0);
            var px = new uint[w * h]; bgra.CopyPixels(px, w * 4, 0);
            var current = MonsterGraphics.Colours(old, row);
            var distinct = px.Where(c => c >> 24 >= 128).Select(c => c & 0xF8F8F8u).Distinct().ToList();
            bool allKnown = distinct.All(c => current.Skip(1).Any(k => (k & 0xF8F8F8u) == c));
            uint[] pal = current;
            if (!allKnown && distinct.Count <= 15 &&
                MessageBox.Show($"The picture has {distinct.Count} colours that are not all in the monster's palette.\n\n" +
                                "Yes = make the monster's palette from the picture's colours.\nNo = use the nearest colours of the current palette.",
                                "Monster colours", MessageBoxButton.YesNo) == MessageBoxResult.Yes)
            {
                pal = new uint[16];
                for (int i = 0; i < distinct.Count; i++) pal[i + 1] = 0xFF000000u | distinct[i];
                for (int i = 1; i < 16; i++)
                {
                    ushort v = MonsterGraphics.ToBgr555(pal[i]);
                    palettes[row * 32 + i * 2] = (byte)v; palettes[row * 32 + i * 2 + 1] = (byte)(v >> 8);
                }
                how = $"new palette from the picture's {distinct.Count} colours";
            }
            else how = allKnown ? "colours matched to the palette" : "nearest colours of the current palette";
            for (int i = 0; i < px.Length; i++)
            {
                if (px[i] >> 24 < 128) continue;
                long best = long.MaxValue;
                for (int c = 1; c < 16; c++)
                {
                    long dr = (long)(px[i] >> 16 & 255) - (pal[c] >> 16 & 255), dg = (long)(px[i] >> 8 & 255) - (pal[c] >> 8 & 255), db = (long)(px[i] & 255) - (pal[c] & 255);
                    long e = dr * dr * 3 + dg * dg * 4 + db * db * 2;
                    if (e < best) { best = e; idx[i] = (byte)c; }
                }
            }
        }
        if (idx.Any(i => i > 15)) { Status = "The picture uses more than 16 colours; save it as a 16-colour indexed picture."; return; }

        // the recoloured twin (the other palette row) follows the new colour numbers only if it was the same picture
        var twins = SharedWith().Where(s => s.Palette != row).ToList();
        if (twins.Count > 0 && !idx.SequenceEqual(MonsterGraphics.Indices(old)) &&
            MessageBox.Show($"{string.Join(", ", twins.Select(t => t.Name))} use{(twins.Count == 1 ? "s" : "")} the same picture with palette {1 - row}. " +
                            "They will show the new picture in their own colours, which may not fit it.\n\nImport anyway?",
                            "Shared picture", MessageBoxButton.YesNo) != MessageBoxResult.Yes)
            return;

        byte[] data;
        try { data = MonsterGraphics.Build(old, idx, w, h, palettes); }
        catch (ArgumentException ex) { Status = ex.Message; return; }

        int graphic = Graphic;
        ResourceWriter.Result? result = null;
        Commit($"Monster picture {graphic:X2}", () =>
        {
            int slot = ResourceWriter.SlotSize(Ctx.Rom, MonsterGraphics.Resource(graphic));
            bool big = LufiaCompression.Compress(data).Length > slot;
            if (big && Ctx.Rom.Length < Core.Maps.MapWriter.ExpandedSize &&
                MessageBox.Show("The new picture does not fit in the original space. Expand the ROM to 2 MB?", "Expand ROM?",
                    MessageBoxButton.YesNo) != MessageBoxResult.Yes)
                throw new InvalidOperationException("Not written: the ROM was not expanded.");
            result = ResourceWriter.Save(Ctx.Rom, MonsterGraphics.Resource(graphic), data, allowExpand: true);
        });
        DrawPicture();
        if (result != null)
            Status = $"Monster picture {graphic:X2} written ({w} x {h}, {how}){(result.Moved ? ", moved to the expanded ROM space" : "")} (unsaved)." +
                     (w != old.PixelWidth || h != old.PixelHeight ? " The size changed: check the battle in an emulator, big pictures can overlap other monsters." : "");
    }
}
