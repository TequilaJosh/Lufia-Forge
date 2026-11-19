using System.Windows;
using System.Windows.Controls;
using Point = System.Windows.Point;
using Button = System.Windows.Controls.Button;

namespace LufiaForge.Modules.Common;

// a section that can pop out into its own window. wrap it in <common:PopOutHost Title="Stage">...</common:PopOutHost>.
// a small pop-out button shows while the mouse is over the section. click it and the section lives in its own window
// (move it to another screen, make it as big as you like) while its old spot shows "Bring it back". closing the
// window puts the section back. the section works just like before, same data and same code, only somewhere else.
// window positions are remembered per section
public class PopOutHost : ContentControl
{
    static PopOutHost()
    {
        FocusableProperty.OverrideMetadata(typeof(PopOutHost), new FrameworkPropertyMetadata(false));
    }

    public static readonly DependencyProperty TitleProperty = DependencyProperty.Register(
        nameof(Title), typeof(string), typeof(PopOutHost), new PropertyMetadata("Section"));

    // the window title, and the name in "... is in its own window"
    public string Title { get => (string)GetValue(TitleProperty); set => SetValue(TitleProperty, value); }

    public static readonly DependencyProperty KeyProperty = DependencyProperty.Register(
        nameof(Key), typeof(string), typeof(PopOutHost), new PropertyMetadata(null));

    // the key the window position is saved under (the title if not set)
    public string? Key { get => (string?)GetValue(KeyProperty); set => SetValue(KeyProperty, value); }

    public static readonly DependencyProperty ButtonMarginProperty = DependencyProperty.Register(
        nameof(ButtonMargin), typeof(Thickness), typeof(PopOutHost), new PropertyMetadata(new Thickness(0, 2, 20, 0)));

    // where the pop-out button sits in the top right corner (moved in where it would cover another button)
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

    // while the section is out, its row or column in the grid only takes what the "Bring it back" note needs, so the
    // sections next to it get the room (put back when it returns)
    private void Shrink()
    {
        if (Parent is not Grid grid) return;
        if (grid.ColumnDefinitions.Count > 1 && Grid.GetColumnSpan(this) == 1 && Grid.GetColumn(this) < grid.ColumnDefinitions.Count)
        {
            var column = grid.ColumnDefinitions[Grid.GetColumn(this)];
            if (!column.Width.IsAuto) { _shrunk = (column, column.Width); column.Width = GridLength.Auto; }
        }
        else if (grid.RowDefinitions.Count > 1 && Grid.GetRowSpan(this) == 1 && Grid.GetRow(this) < grid.RowDefinitions.Count)
        {
            var row = grid.RowDefinitions[Grid.GetRow(this)];
            if (!row.Height.IsAuto) { _shrunk = (row, row.Height); row.Height = GridLength.Auto; }
        }
    }

    private void Unshrink()
    {
        if (_shrunk is not { } shrunk) return;
        // if the view sized the row or column itself in the meantime (showing or hiding a part) it keeps that size,
        // same if the section is hidden now (the view sizes it again when it shows it)
        _shrunk = null;
        if (Content is UIElement { Visibility: not Visibility.Visible }) return;
        if (shrunk.Def is ColumnDefinition column && column.Width.IsAuto) column.Width = shrunk.Size;
        else if (shrunk.Def is RowDefinition row && row.Height.IsAuto) row.Height = shrunk.Size;
    }

    // the size a popped-out section's row or column had before it shrank (that's what LayoutMemory saves)
    public static GridLength? SizeBeforePopOut(DefinitionBase d, Grid g) =>
        g.Children.OfType<PopOutHost>().FirstOrDefault(host => host._shrunk is { } shrunk && ReferenceEquals(shrunk.Def, d))?._shrunk?.Size;

    // true if some section inside root is in its own window right now
    public static bool AnyPoppedOut(DependencyObject root)
    {
        foreach (var child in LogicalTreeHelper.GetChildren(root).OfType<DependencyObject>())
            if (child is PopOutHost { IsPoppedOut: true } || AnyPoppedOut(child)) return true;
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

    // moves the section into its own window (or brings that window to the front)
    public void PopOut()
    {
        if (_window != null) { _window.Activate(); return; }
        if (Content is not UIElement body) return;
        var owner = Window.GetWindow(this);
        // at least a good size to work in (you usually pop a section out to get more room), at most the screen
        double width = Math.Min(Math.Max(ActualWidth, 900), SystemParameters.WorkArea.Width - 60);
        double height = Math.Min(Math.Max(ActualHeight, 650), SystemParameters.WorkArea.Height - 60);
        Point topLeft = IsVisible ? PointToScreen(new Point(0, 0)) : new Point(100, 100);
        // screen pixels to WPF units
        if (PresentationSource.FromVisual(this)?.CompositionTarget is { } target) topLeft = target.TransformFromDevice.Transform(topLeft);

        _content = Content;
        // the section sees the same data as here (also a section whose own DataContext binds to a part of it) and
        // finds the names its bindings use (ElementName) in the view it came from
        // (a UserControl, so bindings that look for "the view I'm in" still find one with the same data)
        var frame = new UserControl { Padding = new Thickness(6) };
        frame.SetBinding(DataContextProperty, new System.Windows.Data.Binding(nameof(DataContext)) { Source = this });
        for (DependencyObject? node = this; node != null; node = LogicalTreeHelper.GetParent(node) ?? System.Windows.Media.VisualTreeHelper.GetParent(node))
            if (NameScope.GetNameScope(node) is { } names) { NameScope.SetNameScope(frame, names); break; }
        var resources = new ResourceDictionary();
        // the styles and templates the section had where it was (its view's resources) come along
        for (DependencyObject? node = this; node != null; node = LogicalTreeHelper.GetParent(node) ?? System.Windows.Media.VisualTreeHelper.GetParent(node))
            if (node is FrameworkElement element && (element.Resources.Count > 0 || element.Resources.MergedDictionaries.Count > 0) && node is not Window)
                resources.MergedDictionaries.Insert(0, element.Resources);
        Content = null;
        frame.Content = body;

        var win = new Window
        {
            Title = $"{Title} - Lufia Forge",
            Width = width, Height = height,
            Left = Math.Max(SystemParameters.WorkArea.Left, Math.Min(topLeft.X + 30, SystemParameters.WorkArea.Right - width)),
            Top = Math.Max(SystemParameters.WorkArea.Top, Math.Min(topLeft.Y + 30, SystemParameters.WorkArea.Bottom - height)),
            WindowStartupLocation = WindowStartupLocation.Manual,
            Background = TryFindResource("MidPurpleBrush") as System.Windows.Media.Brush,
            Resources = resources,
            Icon = owner?.Icon,
            ShowInTaskbar = true,
            Content = frame,
        };
        if (LayoutMemory.Get(PlaceKey) is string place && place.Split(',') is { Length: 4 } parts &&
            parts.Select(s => double.TryParse(s, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var v) ? v : double.NaN).ToArray() is var bounds &&
            bounds.All(v => !double.IsNaN(v)) && bounds[2] >= 200 && bounds[3] >= 150 && OnScreen(bounds[0], bounds[1], bounds[2]))
        {
            win.Left = bounds[0]; win.Top = bounds[1]; win.Width = bounds[2]; win.Height = bounds[3];
        }
        if (owner != null && owner.IsVisible) win.Owner = owner;
        // keys pressed in the window go on to the view the section came from (its shortcuts, then the app's, Ctrl+S,
        // Ctrl+Z...), like the section was still there
        win.PreviewKeyDown += (_, e) => e.Handled |= Forward(e, System.Windows.Input.Keyboard.PreviewKeyDownEvent);
        win.KeyDown += (_, e) => e.Handled |= Forward(e, System.Windows.Input.Keyboard.KeyDownEvent);
        // you type in a box here and then click the main window (Save, another tab). the box keeps its focus in this
        // window, so it never lost it and its text never got handed over (the boxes hand it over when they lose
        // focus). so leaving the window takes the focus off the box (its text goes in), and coming back puts it back
        IInputElement? lastFocus = null;
        win.Deactivated += (_, _) =>
        {
            lastFocus = System.Windows.Input.FocusManager.GetFocusedElement(win);
            if (lastFocus is System.Windows.Controls.Primitives.TextBoxBase) System.Windows.Input.FocusManager.SetFocusedElement(win, null);
            else lastFocus = null;
        };
        win.Activated += (_, _) =>
        {
            if (lastFocus is UIElement { IsVisible: true } focused && System.Windows.Input.FocusManager.GetFocusedElement(win) == null) focused.Focus();
            lastFocus = null;
        };
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
        // the section's home view, or the main window if its tab isn't on screen (so the app's keys still work)
        UIElement? target = IsLoaded ? this : Application.Current?.MainWindow;
        if (target == null || PresentationSource.FromVisual(target) is not { } source) return false;
        var copy = new System.Windows.Input.KeyEventArgs(e.KeyboardDevice, source, e.Timestamp, e.Key) { RoutedEvent = kind };
        target.RaiseEvent(copy);
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
        if (win.Content is UserControl frame) frame.Content = null;
        win.Content = null;
        Content = _content;
        _content = null;
        _window = null;
        SetValue(IsPoppedOutKey, false);
        Unshrink();
        Window.GetWindow(this)?.Activate();
    }
}
