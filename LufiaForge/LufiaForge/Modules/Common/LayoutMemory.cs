using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using GridSplitter = System.Windows.Controls.GridSplitter;

namespace LufiaForge.Modules.Common;

// remembers how you sized a grid's sections with its splitters. set common:LayoutMemory.Key="tab.part" on the Grid.
// the sizes get saved when a splitter is let go (%AppData%\LufiaForge\layout.json) and put back next time the grid
// loads. double-click a splitter to put the grid back the way it was designed. views that size their own rows or
// columns (showing and hiding a part) use Get / Set instead
public static class LayoutMemory
{
    public static readonly DependencyProperty KeyProperty = DependencyProperty.RegisterAttached(
        "Key", typeof(string), typeof(LayoutMemory), new PropertyMetadata(null, OnKeyChanged));

    public static string? GetKey(DependencyObject o) => (string?)o.GetValue(KeyProperty);
    public static void SetKey(DependencyObject o, string? v) => o.SetValue(KeyProperty, v);

    private static readonly DependencyProperty DefaultsProperty = DependencyProperty.RegisterAttached(
        "LayoutDefaults", typeof(GridLength[]), typeof(LayoutMemory));

    private static string StorePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "LufiaForge", "layout.json");

    private static Dictionary<string, string>? _store;

    private static Dictionary<string, string> Store()
    {
        if (_store != null) return _store;
        try { _store = File.Exists(StorePath) ? JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(StorePath)) : null; }
        catch { }
        return _store ??= new();
    }

    // a remembered value (null = none)
    public static string? Get(string key) => Store().TryGetValue(key, out var saved) ? saved : null;

    // remembers a value (null forgets it)
    public static void Set(string key, string? value)
    {
        if (value == null) Store().Remove(key); else Store()[key] = value;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(StorePath)!);
            File.WriteAllText(StorePath, JsonSerializer.Serialize(Store(), new JsonSerializerOptions { WriteIndented = true }));
        }
        catch { /* fine, the sizes are just a convenience */ }
    }

    public static string Format(GridLength g) => g.GridUnitType switch
    {
        GridUnitType.Auto => "auto",
        GridUnitType.Star => g.Value.ToString("0.####", System.Globalization.CultureInfo.InvariantCulture) + "*",
        _ => g.Value.ToString("0.#", System.Globalization.CultureInfo.InvariantCulture),
    };

    public static GridLength? Parse(string? s)
    {
        if (string.IsNullOrWhiteSpace(s)) return null;
        if (s == "auto") return GridLength.Auto;
        bool star = s.EndsWith('*');
        if (!double.TryParse(star ? s[..^1] : s, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out double size) || size < 0) return null;
        return new GridLength(size, star ? GridUnitType.Star : GridUnitType.Pixel);
    }

    private static void OnKeyChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not Grid grid) return;
        grid.Loaded -= Grid_Loaded;
        grid.RemoveHandler(Thumb.DragCompletedEvent, new DragCompletedEventHandler(Grid_DragCompleted));
        grid.RemoveHandler(UIElement.PreviewMouseLeftButtonDownEvent, new MouseButtonEventHandler(Grid_PreviewMouseDown));
        if (e.NewValue is not string) return;
        grid.Loaded += Grid_Loaded;
        grid.AddHandler(Thumb.DragCompletedEvent, new DragCompletedEventHandler(Grid_DragCompleted));
        grid.AddHandler(UIElement.PreviewMouseLeftButtonDownEvent, new MouseButtonEventHandler(Grid_PreviewMouseDown), true);
    }

    private static List<GridLength> Sizes(Grid grid) =>
        grid.ColumnDefinitions.Select(c => c.Width).Concat(grid.RowDefinitions.Select(r => r.Height)).ToList();

    private static void Apply(Grid grid, IReadOnlyList<GridLength> sizes)
    {
        int columnCount = grid.ColumnDefinitions.Count;
        for (int i = 0; i < sizes.Count && i < columnCount + grid.RowDefinitions.Count; i++)
            if (i < columnCount) grid.ColumnDefinitions[i].Width = sizes[i]; else grid.RowDefinitions[i - columnCount].Height = sizes[i];
    }

    private static void Grid_Loaded(object sender, RoutedEventArgs e)
    {
        var grid = (Grid)sender;
        if (grid.GetValue(DefaultsProperty) == null) grid.SetValue(DefaultsProperty, Sizes(grid).ToArray());
        if (GetKey(grid) is not string key || Get(key) is not string saved) return;
        var parts = saved.Split(';').Select(Parse).ToList();
        // a layout saved from another version of the view (different rows or columns) gets dropped
        if (parts.Count != grid.ColumnDefinitions.Count + grid.RowDefinitions.Count || parts.Any(p => p == null)) return;
        Apply(grid, parts.Select(p => p!.Value).ToList());
    }

    private static void Grid_DragCompleted(object sender, DragCompletedEventArgs e)
    {
        if (!Moved(e) || e.OriginalSource is not GridSplitter splitter || sender is not Grid grid || splitter.Parent != grid || GetKey(grid) is not string key) return;
        // a section popped out into its own window shrank its row or column, so save the size it gets back when it returns
        var sizes = grid.ColumnDefinitions.Select(c => PopOutHost.SizeBeforePopOut(c, grid) ?? c.Width)
                     .Concat(grid.RowDefinitions.Select(r => PopOutHost.SizeBeforePopOut(r, grid) ?? r.Height));
        Set(key, string.Join(";", sizes.Select(Format)));
    }

    // a drag that really moved the splitter (a click doesn't, and neither does the first click of a double-click)
    public static bool Moved(DragCompletedEventArgs e) => !e.Canceled && (Math.Abs(e.HorizontalChange) >= 1 || Math.Abs(e.VerticalChange) >= 1);

    private static void Grid_PreviewMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ClickCount != 2 || sender is not Grid grid || GetKey(grid) is not string key) return;
        if (Splitter(e.OriginalSource as DependencyObject) is not { } splitter || splitter.Parent != grid) return;
        if (grid.GetValue(DefaultsProperty) is GridLength[] defaults) Apply(grid, defaults);
        Set(key, null);
        e.Handled = true;
    }

    private static GridSplitter? Splitter(DependencyObject? o)
    {
        for (; o != null; o = o is System.Windows.Media.Visual or System.Windows.Media.Media3D.Visual3D ? System.Windows.Media.VisualTreeHelper.GetParent(o) : LogicalTreeHelper.GetParent(o))
            if (o is GridSplitter splitter) return splitter;
        return null;
    }
}
