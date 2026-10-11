using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using ComboBox = System.Windows.Controls.ComboBox;
using ComboBoxItem = System.Windows.Controls.ComboBoxItem;
using TextBox = System.Windows.Controls.TextBox;
using ListBox = System.Windows.Controls.ListBox;
using ListBoxItem = System.Windows.Controls.ListBoxItem;
using Brushes = System.Windows.Media.Brushes;
using KeyEventArgs = System.Windows.Input.KeyEventArgs;

namespace LufiaForge.Controls;

/// <summary>
/// Type-to-search for every dropdown in the app: typing while a ComboBox has focus opens a search box under it that
/// lists the entries containing the typed text anywhere ("kni" finds "01 Knife"; the number works too). Up/Down and
/// Enter or a click pick one, Esc cancels. The choice is made by setting the ComboBox's SelectedIndex, so the
/// editors' bindings see an ordinary selection. Registered once for all ComboBoxes (<see cref="Register"/>).
/// </summary>
public static class ComboSearch
{
    /// <summary>Lists shorter than this keep the ComboBox's own type-ahead.</summary>
    private const int MinItems = 8;

    private sealed record Hit(int Index, string Text)
    {
        public override string ToString() => Text;
    }

    public static void Register() =>
        EventManager.RegisterClassHandler(typeof(ComboBox), UIElement.PreviewTextInputEvent, new TextCompositionEventHandler(OnTextInput));

    private static void OnTextInput(object sender, TextCompositionEventArgs e)
    {
        if (sender is not ComboBox cb || cb.IsEditable || !cb.IsEnabled || cb.Items.Count < MinItems) return;
        if (string.IsNullOrEmpty(e.Text) || char.IsControl(e.Text[0])) return;
        e.Handled = true;
        cb.IsDropDownOpen = false;
        Open(cb, e.Text);
    }

    private static string TextOf(ComboBox cb, object? item)
    {
        if (item == null) return "";
        if (item is string s) return s;
        if (item is ComboBoxItem ci) return ci.Content?.ToString() ?? "";
        if (!string.IsNullOrEmpty(cb.DisplayMemberPath))
        {
            var p = item.GetType().GetProperty(cb.DisplayMemberPath);
            if (p != null) return p.GetValue(item)?.ToString() ?? "";
        }
        return item.ToString() ?? "";
    }

    private static void Open(ComboBox cb, string start)
    {
        var all = cb.Items.Cast<object>().Select((o, i) => new Hit(i, TextOf(cb, o))).ToList();
        var black = Brushes.Black;
        var box = new TextBox { Text = start, Padding = new Thickness(4, 3, 4, 3), FontSize = 12, Foreground = black };
        var list = new ListBox
        {
            MaxHeight = 320, FontSize = 12, Foreground = black, BorderThickness = new Thickness(0),
            Margin = new Thickness(0, 4, 0, 0),
        };
        ScrollViewer.SetHorizontalScrollBarVisibility(list, ScrollBarVisibility.Disabled);
        var count = new TextBlock { FontSize = 11, Foreground = Brushes.DimGray, Margin = new Thickness(0, 4, 0, 0) };
        var panel = new StackPanel { Margin = new Thickness(6) };
        panel.Children.Add(new TextBlock { Text = "Type to search - Enter picks, Esc cancels", FontSize = 11, Foreground = Brushes.DimGray, Margin = new Thickness(0, 0, 0, 4) });
        panel.Children.Add(box);
        panel.Children.Add(list);
        panel.Children.Add(count);
        var popup = new Popup
        {
            PlacementTarget = cb, Placement = PlacementMode.Bottom, StaysOpen = false, AllowsTransparency = false,
            MinWidth = Math.Max(cb.ActualWidth, 240),
            Child = new Border { Background = Brushes.White, BorderBrush = Brushes.Gray, BorderThickness = new Thickness(1), Child = panel },
        };

        void Refresh()
        {
            // names keep the game's @ for a space ("Long@Knife"): both match a typed space
            string q = box.Text.Trim().Replace('@', ' ');
            var hits = q.Length == 0 ? all : all.Where(h => h.Text.Replace('@', ' ').Contains(q, StringComparison.OrdinalIgnoreCase)).ToList();
            list.ItemsSource = hits;
            list.SelectedIndex = hits.Count > 0 ? 0 : -1;
            if (hits.Count > 0) list.ScrollIntoView(hits[0]);
            count.Text = hits.Count == 1 ? "1 match" : $"{hits.Count} matches";
        }
        void Pick()
        {
            if (list.SelectedItem is Hit h) cb.SelectedIndex = h.Index;
            popup.IsOpen = false;
            cb.Focus();
        }
        void Move(int d)
        {
            int n = list.Items.Count;
            if (n == 0) return;
            list.SelectedIndex = Math.Clamp(list.SelectedIndex + d, 0, n - 1);
            list.ScrollIntoView(list.SelectedItem);
        }

        box.TextChanged += (_, _) => Refresh();
        box.PreviewKeyDown += (_, k) =>
        {
            switch (k.Key)
            {
                case Key.Down: Move(1); k.Handled = true; break;
                case Key.Up: Move(-1); k.Handled = true; break;
                case Key.PageDown: Move(10); k.Handled = true; break;
                case Key.PageUp: Move(-10); k.Handled = true; break;
                case Key.Enter: Pick(); k.Handled = true; break;
                case Key.Escape: popup.IsOpen = false; cb.Focus(); k.Handled = true; break;
            }
        };
        list.PreviewMouseLeftButtonUp += (_, m) =>
        {
            if (m.OriginalSource is DependencyObject d && ItemsControl.ContainerFromElement(list, d) is ListBoxItem item)
            {
                list.SelectedItem = item.DataContext;
                Pick();
            }
        };
        popup.Opened += (_, _) =>
        {
            box.Focus();
            Keyboard.Focus(box);
            box.CaretIndex = box.Text.Length;
        };
        Refresh();
        popup.IsOpen = true;
    }
}
