using System.Windows;
using System.Windows.Controls;
using Point = System.Windows.Point;
using Button = System.Windows.Controls.Button;

namespace LufiaForge.Modules.Common;

/// <summary>
/// A section that can be popped out into a window of its own: wrap it in
/// <c>&lt;common:PopOutHost Title="Stage"&gt;…&lt;/common:PopOutHost&gt;</c>. A small ⧉ button shows while the mouse is
/// over the section; the section then lives in its own window (move it to another screen, make it as big as you like)
/// while its place shows "Bring it back". Closing the window puts the section back. The section keeps working as
/// before: same data, same code, only somewhere else. Window places are remembered per section.
/// </summary>
public class PopOutHost : ContentControl
{
    static PopOutHost()
    {
        FocusableProperty.OverrideMetadata(typeof(PopOutHost), new FrameworkPropertyMetadata(false));
    }

    public static readonly DependencyProperty TitleProperty = DependencyProperty.Register(
        nameof(Title), typeof(string), typeof(PopOutHost), new PropertyMetadata("Section"));

    /// <summary>The window title and the name in "… is in its own window".</summary>
    public string Title { get => (string)GetValue(TitleProperty); set => SetValue(TitleProperty, value); }

    public static readonly DependencyProperty KeyProperty = DependencyProperty.Register(
        nameof(Key), typeof(string), typeof(PopOutHost), new PropertyMetadata(null));

    /// <summary>Where the window's place is remembered (defaults to the title).</summary>
    public string? Key { get => (string?)GetValue(KeyProperty); set => SetValue(KeyProperty, value); }

    public static readonly DependencyProperty ButtonMarginProperty = DependencyProperty.Register(
        nameof(ButtonMargin), typeof(Thickness), typeof(PopOutHost), new PropertyMetadata(new Thickness(0, 2, 20, 0)));

    /// <summary>Where the ⧉ button sits in the top right corner (moved in where it would cover a button).</summary>
    public Thickness ButtonMargin { get => (Thickness)GetValue(ButtonMarginProperty); set => SetValue(ButtonMarginProperty, value); }

    private static readonly DependencyPropertyKey IsPoppedOutKey = DependencyProperty.RegisterReadOnly(
        nameof(IsPoppedOut), typeof(bool), typeof(PopOutHost), new PropertyMetadata(false));
    public static readonly DependencyProperty IsPoppedOutProperty = IsPoppedOutKey.DependencyProperty;

    public bool IsPoppedOut => (bool)GetValue(IsPoppedOutProperty);

    public static readonly DependencyProperty PlaceholderTextProperty = DependencyProperty.Register(
        nameof(PlaceholderText), typeof(string), typeof(PopOutHost), new PropertyMetadata(""));
    public string PlaceholderText => (string)GetValue(PlaceholderTextProperty);

    private Window? _window;
    private object? _content;
    private (DefinitionBase Def, GridLength Size)? _shrunk;

    /// <summary>While the section is out, its row or column in the grid it sits in only takes what the "Bring it back"
    /// note needs, so the sections next to it get the room (put back when it returns).</summary>
    private void Shrink()
    {
        if (Parent is not Grid g) return;
        if (g.ColumnDefinitions.Count > 1 && Grid.GetColumnSpan(this) == 1 && Grid.GetColumn(this) < g.ColumnDefinitions.Count)
        {
            var c = g.ColumnDefinitions[Grid.GetColumn(this)];
            if (!c.Width.IsAuto) { _shrunk = (c, c.Width); c.Width = GridLength.Auto; }
        }
        else if (g.RowDefinitions.Count > 1 && Grid.GetRowSpan(this) == 1 && Grid.GetRow(this) < g.RowDefinitions.Count)
        {
            var r = g.RowDefinitions[Grid.GetRow(this)];
            if (!r.Height.IsAuto) { _shrunk = (r, r.Height); r.Height = GridLength.Auto; }
        }
    }

    private void Unshrink()
    {
        if (_shrunk is not { } s) return;
        // a view that sized the row or column itself meanwhile (showing or hiding a part) keeps its size, and so
        // does one whose section is hidden now (the view sizes it again when it shows it)
        _shrunk = null;
        if (Content is UIElement { Visibility: not Visibility.Visible }) return;
        if (s.Def is ColumnDefinition c && c.Width.IsAuto) c.Width = s.Size;
        else if (s.Def is RowDefinition r && r.Height.IsAuto) r.Height = s.Size;
    }

    /// <summary>The size a popped-out section's row or column had before it shrank (what LayoutMemory keeps).</summary>
    public static GridLength? SizeBeforePopOut(DefinitionBase d, Grid g) =>
        g.Children.OfType<PopOutHost>().FirstOrDefault(h => h._shrunk is { } s && ReferenceEquals(s.Def, d))?._shrunk?.Size;

    /// <summary>Whether a section inside <paramref name="root"/> is in its own window now.</summary>
    public static bool AnyPoppedOut(DependencyObject root)
    {
        foreach (var c in LogicalTreeHelper.GetChildren(root).OfType<DependencyObject>())
            if (c is PopOutHost { IsPoppedOut: true } || AnyPoppedOut(c)) return true;
        return false;
    }

    public override void OnApplyTemplate()
    {
        base.OnApplyTemplate();
        if (GetTemplateChild("PART_PopOut") is Button pop) { pop.Click -= PopOut_Click; pop.Click += PopOut_Click; }
        if (GetTemplateChild("PART_Return") is Button back) { back.Click -= Return_Click; back.Click += Return_Click; }
    }

    private void PopOut_Click(object sender, RoutedEventArgs e) => PopOut();
    private void Return_Click(object sender, RoutedEventArgs e) => _window?.Close();

    private string PlaceKey => "popout." + (Key ?? Title);

    /// <summary>Moves the section into its own window (or brings that window forward).</summary>
    public void PopOut()
    {
        if (_window != null) { _window.Activate(); return; }
        if (Content is not UIElement body) return;
        var owner = Window.GetWindow(this);
        // at least a good working size (a section is usually popped out to get more room), at most the screen
        double w = Math.Min(Math.Max(ActualWidth, 900), SystemParameters.WorkArea.Width - 60);
        double h = Math.Min(Math.Max(ActualHeight, 650), SystemParameters.WorkArea.Height - 60);
        Point at = IsVisible ? PointToScreen(new Point(0, 0)) : new Point(100, 100);
        // screen pixels to WPF units
        if (PresentationSource.FromVisual(this)?.CompositionTarget is { } ct) at = ct.TransformFromDevice.Transform(at);

        _content = Content;
        // the section sees the same data as here (also a section whose own DataContext binds to a part of it) and finds
        // the names its bindings use (ElementName) in the view it came from
        // (a UserControl, so bindings that look for "the view I am in" still find one with the same data)
        var frame = new UserControl { Padding = new Thickness(6) };
        frame.SetBinding(DataContextProperty, new System.Windows.Data.Binding(nameof(DataContext)) { Source = this });
        for (DependencyObject? p = this; p != null; p = LogicalTreeHelper.GetParent(p) ?? System.Windows.Media.VisualTreeHelper.GetParent(p))
            if (NameScope.GetNameScope(p) is { } names) { NameScope.SetNameScope(frame, names); break; }
        var resources = new ResourceDictionary();
        // the styles and templates the section found where it was (its view's resources) go with it
        for (DependencyObject? p = this; p != null; p = LogicalTreeHelper.GetParent(p) ?? System.Windows.Media.VisualTreeHelper.GetParent(p))
            if (p is FrameworkElement f && (f.Resources.Count > 0 || f.Resources.MergedDictionaries.Count > 0) && p is not Window)
                resources.MergedDictionaries.Insert(0, f.Resources);
        Content = null;
        frame.Content = body;

        var win = new Window
        {
            Title = $"{Title} - Lufia Forge",
            Width = w, Height = h,
            Left = Math.Max(SystemParameters.WorkArea.Left, Math.Min(at.X + 30, SystemParameters.WorkArea.Right - w)),
            Top = Math.Max(SystemParameters.WorkArea.Top, Math.Min(at.Y + 30, SystemParameters.WorkArea.Bottom - h)),
            WindowStartupLocation = WindowStartupLocation.Manual,
            Background = TryFindResource("MidPurpleBrush") as System.Windows.Media.Brush,
            Resources = resources,
            Icon = owner?.Icon,
            ShowInTaskbar = true,
            Content = frame,
        };
        if (LayoutMemory.Get(PlaceKey) is string place && place.Split(',') is { Length: 4 } p4 &&
            p4.Select(s => double.TryParse(s, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var v) ? v : double.NaN).ToArray() is var r &&
            r.All(v => !double.IsNaN(v)) && r[2] >= 200 && r[3] >= 150 && OnScreen(r[0], r[1], r[2]))
        {
            win.Left = r[0]; win.Top = r[1]; win.Width = r[2]; win.Height = r[3];
        }
        if (owner != null && owner.IsVisible) win.Owner = owner;
        // keys pressed in the window go on to the view the section came from (its shortcuts, then the app's: Ctrl+S,
        // Ctrl+Z...), as if the section were still there
        win.PreviewKeyDown += (_, e) => e.Handled |= Forward(e, System.Windows.Input.Keyboard.PreviewKeyDownEvent);
        win.KeyDown += (_, e) => e.Handled |= Forward(e, System.Windows.Input.Keyboard.KeyDownEvent);
        win.Closing += (_, _) => Return(win);
        _window = win;
        SetValue(IsPoppedOutKey, true);
        Shrink();
        SetValue(PlaceholderTextProperty, $"{Title} is in its own window.");
        win.Show();
    }

    private bool Forward(System.Windows.Input.KeyEventArgs e, RoutedEvent kind)
    {
        if (e.Handled) return false;
        // the section's home view, or the main window when its tab isn't on screen (the app's keys still work)
        UIElement? to = IsLoaded ? this : Application.Current?.MainWindow;
        if (to == null || PresentationSource.FromVisual(to) is not { } source) return false;
        var copy = new System.Windows.Input.KeyEventArgs(e.KeyboardDevice, source, e.Timestamp, e.Key) { RoutedEvent = kind };
        to.RaiseEvent(copy);
        return copy.Handled;
    }

    private static bool OnScreen(double left, double top, double width) =>
        left + width > SystemParameters.VirtualScreenLeft + 40 && left < SystemParameters.VirtualScreenLeft + SystemParameters.VirtualScreenWidth - 40 &&
        top >= SystemParameters.VirtualScreenTop - 10 && top < SystemParameters.VirtualScreenTop + SystemParameters.VirtualScreenHeight - 40;

    private void Return(Window win)
    {
        if (_window != win) return;
        if (win.WindowState == WindowState.Normal)
            LayoutMemory.Set(PlaceKey, string.Join(",", new[] { win.Left, win.Top, win.Width, win.Height }
                .Select(v => v.ToString("0", System.Globalization.CultureInfo.InvariantCulture))));
        if (win.Content is UserControl b) b.Content = null;
        win.Content = null;
        Content = _content;
        _content = null;
        _window = null;
        SetValue(IsPoppedOutKey, false);
        Unshrink();
        Window.GetWindow(this)?.Activate();
    }
}
