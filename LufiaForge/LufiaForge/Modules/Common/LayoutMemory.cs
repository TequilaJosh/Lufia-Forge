using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using GridSplitter = System.Windows.Controls.GridSplitter;

namespace LufiaForge.Modules.Common;

/// <summary>
/// Remembers how a grid's sections were sized with its splitters: set <c>common:LayoutMemory.Key="tab.part"</c> on
/// the Grid. Sizes are kept when a splitter is let go (%AppData%\LufiaForge\layout.json) and put back the next time
/// the grid loads; a double-click on a splitter puts the grid back as it was designed. Views that size their rows or
/// columns themselves (showing and hiding a part) use <see cref="Get"/> / <see cref="Set"/> instead.
/// </summary>
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

    /// <summary>A remembered value (null = none).</summary>
    public static string? Get(string key) => Store().TryGetValue(key, out var v) ? v : null;

    /// <summary>Remembers a value (null forgets it).</summary>
    public static void Set(string key, string? value)
    {
        if (value == null) Store().Remove(key); else Store()[key] = value;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(StorePath)!);
            File.WriteAllText(StorePath, JsonSerializer.Serialize(Store(), new JsonSerializerOptions { WriteIndented = true }));
        }
        catch { /* not fatal: sizes are a convenience */ }
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
        if (!double.TryParse(star ? s[..^1] : s, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out double v) || v < 0) return null;
        return new GridLength(v, star ? GridUnitType.Star : GridUnitType.Pixel);
    }

    private static void OnKeyChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not Grid g) return;
        g.Loaded -= Grid_Loaded;
        g.RemoveHandler(Thumb.DragCompletedEvent, new DragCompletedEventHandler(Grid_DragCompleted));
        g.RemoveHandler(UIElement.PreviewMouseLeftButtonDownEvent, new MouseButtonEventHandler(Grid_PreviewMouseDown));
        if (e.NewValue is not string) return;
        g.Loaded += Grid_Loaded;
        g.AddHandler(Thumb.DragCompletedEvent, new DragCompletedEventHandler(Grid_DragCompleted));
        g.AddHandler(UIElement.PreviewMouseLeftButtonDownEvent, new MouseButtonEventHandler(Grid_PreviewMouseDown), true);
    }

    private static List<GridLength> Sizes(Grid g) =>
        g.ColumnDefinitions.Select(c => c.Width).Concat(g.RowDefinitions.Select(r => r.Height)).ToList();

    private static void Apply(Grid g, IReadOnlyList<GridLength> sizes)
    {
        int nc = g.ColumnDefinitions.Count;
        for (int i = 0; i < sizes.Count && i < nc + g.RowDefinitions.Count; i++)
            if (i < nc) g.ColumnDefinitions[i].Width = sizes[i]; else g.RowDefinitions[i - nc].Height = sizes[i];
    }

    private static void Grid_Loaded(object sender, RoutedEventArgs e)
    {
        var g = (Grid)sender;
        if (g.GetValue(DefaultsProperty) == null) g.SetValue(DefaultsProperty, Sizes(g).ToArray());
        if (GetKey(g) is not string key || Get(key) is not string saved) return;
        var parts = saved.Split(';').Select(Parse).ToList();
        // a layout from another version of the view (other rows or columns) is dropped
        if (parts.Count != g.ColumnDefinitions.Count + g.RowDefinitions.Count || parts.Any(p => p == null)) return;
        Apply(g, parts.Select(p => p!.Value).ToList());
    }

    private static void Grid_DragCompleted(object sender, DragCompletedEventArgs e)
    {
        if (e.OriginalSource is not GridSplitter s || sender is not Grid g || s.Parent != g || GetKey(g) is not string key) return;
        Set(key, string.Join(";", Sizes(g).Select(Format)));
    }

    private static void Grid_PreviewMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ClickCount != 2 || sender is not Grid g || GetKey(g) is not string key) return;
        if (Splitter(e.OriginalSource as DependencyObject) is not { } s || s.Parent != g) return;
        if (g.GetValue(DefaultsProperty) is GridLength[] defaults) Apply(g, defaults);
        Set(key, null);
        e.Handled = true;
    }

    private static GridSplitter? Splitter(DependencyObject? o)
    {
        for (; o != null; o = o is System.Windows.Media.Visual or System.Windows.Media.Media3D.Visual3D ? System.Windows.Media.VisualTreeHelper.GetParent(o) : LogicalTreeHelper.GetParent(o))
            if (o is GridSplitter s) return s;
        return null;
    }
}
