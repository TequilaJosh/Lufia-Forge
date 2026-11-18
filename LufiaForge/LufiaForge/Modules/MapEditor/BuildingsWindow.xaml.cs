using CommunityToolkit.Mvvm.ComponentModel;
using LufiaForge.Core;
using LufiaForge.Core.Maps;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace LufiaForge.Modules.MapEditor;

// ready-made structures from the game's maps (Prefabs), drawn the way they'll look on this map. pick one to stamp it
public partial class BuildingsWindow : Window
{
    public sealed partial class Item : ObservableObject
    {
        public required Prefabs.Placement Placement { get; init; }
        public required ImageSource Thumb { get; init; }
        public required string Label { get; init; }
        public required string Tip { get; init; }
        // set for the user's own presets (those can be deleted)
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
        foreach (var prefab in Prefabs.ForTileset(rom, map.Tileset, maps))
        {
            if (placer.Place(prefab) is not { } placement) continue;
            if (!seen.Add(prefab.W + "x" + prefab.H + ":" + string.Join(",", placement.Cells))) continue;   // the same thing from a tileset with the same graphics
            if (!tilesets.TryGetValue(placement.Threshold, out var tileset))
                tilesets[placement.Threshold] = tileset = new MapTileset(rom, map.Tileset, map.PaletteIndex, placement.Threshold);
            items.Add(new Item
            {
                Placement = placement,
                Thumb = Render(tileset, prefab.W, prefab.H, placement.Cells),
                Label = $"{prefab.W} x {prefab.H}" + (prefab.Count > 1 ? $"  ({prefab.Count}x in the game)" : ""),
                Tip = $"{prefab.W} x {prefab.H} blocks, from {prefab.Source}" + (prefab.Count > 1 ? $" ({prefab.Count} times in the game)" : ""),
            });
        }
        // the user's own presets (made with the Area tool's Save as preset), on maps with the same graphics
        var mine = new List<Item>();
        uint graphics = CustomPresets.GraphicsOf(rom, map.Tileset);
        foreach (var preset in CustomPresets.Load().Where(c => c.Graphics == graphics).OrderByDescending(c => c.Made))
        {
            if (placer.Place(preset) is not { } placement) continue;
            if (!tilesets.TryGetValue(placement.Threshold, out var tileset))
                tilesets[placement.Threshold] = tileset = new MapTileset(rom, map.Tileset, map.PaletteIndex, placement.Threshold);
            mine.Add(new Item
            {
                Placement = placement, CustomId = preset.Id,
                Thumb = Render(tileset, preset.W, preset.H, placement.Cells),
                Label = preset.Name,
                Tip = $"{preset.Name}: {preset.W} x {preset.H} blocks, made from {preset.From} on {preset.Made:yyyy-MM-dd} (right-click to delete)",
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

    private static ImageSource Render(MapTileset tileset, int w, int h, ushort[] cells)
    {
        int pw = w * 16, ph = h * 16;
        var px = new uint[pw * ph];
        for (int cell = 0; cell < cells.Length; cell++)
        {
            if (cells[cell] == Prefabs.Keep) continue;
            var block = tileset.MetatilePixels(cells[cell]);
            int bx = cell % w * 16, by = cell / w * 16;
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
        if (GroupList.SelectedItem is Group group)
        {
            group.Items.Remove(item);
            Gallery.ItemsSource = null; Gallery.ItemsSource = group.Items;
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
