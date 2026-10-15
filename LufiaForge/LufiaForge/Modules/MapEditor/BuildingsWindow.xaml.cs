using CommunityToolkit.Mvvm.ComponentModel;
using LufiaForge.Core;
using LufiaForge.Core.Maps;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace LufiaForge.Modules.MapEditor;

/// <summary>Ready-made structures from the game's maps (<see cref="Prefabs"/>), shown as they will look on this map; pick one to stamp it.</summary>
public partial class BuildingsWindow : Window
{
    public sealed partial class Item : ObservableObject
    {
        public required Prefabs.Placement Placement { get; init; }
        public required ImageSource Thumb { get; init; }
        public required string Label { get; init; }
        public required string Tip { get; init; }
        /// <summary>A preset of the user's (can be deleted).</summary>
        public string? CustomId { get; init; }
        public Visibility DeleteVisibility => CustomId != null ? Visibility.Visible : Visibility.Collapsed;
        public double MaxW => Math.Min(176, Placement.Prefab.W * 16 * 1.5);
        public double MaxH => Math.Min(170, Placement.Prefab.H * 16 * 1.5);
        [ObservableProperty] private bool _isSelected;
    }

    private sealed record Group(string Label, List<Item> Items);

    private Item? _selected;

    public Prefabs.Placement? Result { get; private set; }

    public BuildingsWindow(RomBuffer rom, LufiaMap map, IList<MapInfo> maps)
    {
        InitializeComponent();
        var placer = new Prefabs.Placer(rom, map, maps);
        var tilesets = new Dictionary<int, MapTileset>();
        var items = new List<Item>();
        var seen = new HashSet<string>();
        foreach (var p in Prefabs.ForTileset(rom, map.Tileset, maps))
        {
            if (placer.Place(p) is not { } pl) continue;
            if (!seen.Add(p.W + "x" + p.H + ":" + string.Join(",", pl.Cells))) continue;   // the same thing from a tileset with the same graphics
            if (!tilesets.TryGetValue(pl.Threshold, out var ts))
                tilesets[pl.Threshold] = ts = new MapTileset(rom, map.Tileset, map.PaletteIndex, pl.Threshold);
            items.Add(new Item
            {
                Placement = pl,
                Thumb = Render(ts, p.W, p.H, pl.Cells),
                Label = $"{p.W} x {p.H}" + (p.Count > 1 ? $"  ({p.Count}x in the game)" : ""),
                Tip = $"{p.W} x {p.H} blocks, from {p.Source}" + (p.Count > 1 ? $" ({p.Count} times in the game)" : ""),
            });
        }
        // the user's own presets (made with the Area tool's Save as preset), on maps with the same graphics
        var mine = new List<Item>();
        uint graphics = CustomPresets.GraphicsOf(rom, map.Tileset);
        foreach (var c in CustomPresets.Load().Where(c => c.Graphics == graphics).OrderByDescending(c => c.Made))
        {
            if (placer.Place(c) is not { } pl) continue;
            if (!tilesets.TryGetValue(pl.Threshold, out var ts))
                tilesets[pl.Threshold] = ts = new MapTileset(rom, map.Tileset, map.PaletteIndex, pl.Threshold);
            mine.Add(new Item
            {
                Placement = pl, CustomId = c.Id,
                Thumb = Render(ts, c.W, c.H, pl.Cells),
                Label = c.Name,
                Tip = $"{c.Name}: {c.W} x {c.H} blocks, made from {c.From} on {c.Made:yyyy-MM-dd} (right-click to delete)",
            });
        }
        var groups = items.GroupBy(i => i.Placement.Prefab.Category)
                          .OrderBy(g => g.Key == "Buildings" ? 0 : g.Key == "Small objects" ? 1 : 2)
                          .Select(g => new Group($"{g.Key} ({g.Count()})", g.ToList())).ToList();
        if (mine.Count > 0) groups.Insert(0, new Group($"My presets ({mine.Count})", mine));
        HeaderText.Text = $"Buildings (tileset {map.Tileset}: {items.Count} ready to place" + (mine.Count > 0 ? $", {mine.Count} of your own)" : ")");
        GroupList.ItemsSource = groups;
        GroupList.SelectedIndex = groups.Count > 0 ? 0 : -1;
        if (groups.Count == 0) ChoiceText.Text = "No structures were found for this tileset.";
    }

    private static ImageSource Render(MapTileset ts, int w, int h, ushort[] cells)
    {
        int pw = w * 16, ph = h * 16;
        var px = new uint[pw * ph];
        for (int c = 0; c < cells.Length; c++)
        {
            if (cells[c] == Prefabs.Keep) continue;
            var block = ts.MetatilePixels(cells[c]);
            int bx = c % w * 16, by = c / w * 16;
            for (int y = 0; y < 16; y++)
                for (int x = 0; x < 16; x++)
                    px[(by + y) * pw + bx + x] = block[y * 16 + x] | 0xFF000000;
        }
        var bmp = BitmapSource.Create(pw, ph, 96, 96, PixelFormats.Bgra32, null, px, pw * 4);
        bmp.Freeze();
        return bmp;
    }

    private void GroupList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        Gallery.ItemsSource = (GroupList.SelectedItem as Group)?.Items;
        Scroll.ScrollToTop();
    }

    private void Card_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not Item item) return;
        if (_selected != null) _selected.IsSelected = false;
        _selected = item;
        item.IsSelected = true;
        OkButton.IsEnabled = true;
        ChoiceText.Text = item.Tip + ". Place it, then click on your map to stamp it.";
        if (e.ClickCount == 2) Ok_Click(sender, e);
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) { DialogResult = false; Close(); }

    private void Delete_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not Item { CustomId: { } id } item) return;
        if (MessageBox.Show($"Delete the preset \"{item.Label}\"?", "My presets", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
        CustomPresets.Remove(id);
        if (GroupList.SelectedItem is Group g)
        {
            g.Items.Remove(item);
            Gallery.ItemsSource = null; Gallery.ItemsSource = g.Items;
        }
        if (_selected == item) { _selected = null; OkButton.IsEnabled = false; ChoiceText.Text = "Preset deleted."; }
    }

    private void Ok_Click(object sender, RoutedEventArgs e)
    {
        if (_selected == null) return;
        Result = _selected.Placement;
        DialogResult = true;
        Close();
    }
}
