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

// type-to-search for every dropdown in the app. typing while a ComboBox has focus opens a search box under it
// listing the entries that have the typed text anywhere ("kni" finds "01 Knife", the number works too).
// Up/Down and Enter or a click pick one, Esc cancels. we pick by setting the ComboBox's SelectedIndex, so the
// editors' bindings just see a normal selection. registered once for all ComboBoxes (Register)
public static class ComboSearch
{
    // lists shorter than this keep the ComboBox's own type-ahead
    private const int MinItems = 8;

    private sealed record Hit(int Index, string Text)
    {
        public override string ToString() => Text;
    }

    public static void Register() =>
        EventManager.RegisterClassHandler(typeof(ComboBox), UIElement.PreviewTextInputEvent, new TextCompositionEventHandler(OnTextInput));

    private static void OnTextInput(object sender, TextCompositionEventArgs e)
    {
        if (sender is not ComboBox combo || combo.IsEditable || !combo.IsEnabled || combo.Items.Count < MinItems) return;
        if (string.IsNullOrEmpty(e.Text) || char.IsControl(e.Text[0])) return;
        e.Handled = true;
        combo.IsDropDownOpen = false;
        Open(combo, e.Text);
    }

    private static string TextOf(ComboBox combo, object? item)
    {
        if (item == null) return "";
        if (item is string text) return text;
        if (item is ComboBoxItem comboItem) return comboItem.Content?.ToString() ?? "";
        if (!string.IsNullOrEmpty(combo.DisplayMemberPath))
        {
            var displayProperty = item.GetType().GetProperty(combo.DisplayMemberPath);
            if (displayProperty != null) return displayProperty.GetValue(item)?.ToString() ?? "";
        }
        return item.ToString() ?? "";
    }

    private static void Open(ComboBox combo, string startText)
    {
        var allEntries = combo.Items.Cast<object>().Select((o, i) => new Hit(i, TextOf(combo, o))).ToList();
        var black = Brushes.Black;
        var searchBox = new TextBox { Text = startText, Padding = new Thickness(4, 3, 4, 3), FontSize = 12, Foreground = black };
        var list = new ListBox
        {
            MaxHeight = 320, FontSize = 12, Foreground = black, BorderThickness = new Thickness(0),
            Margin = new Thickness(0, 4, 0, 0),
        };
        ScrollViewer.SetHorizontalScrollBarVisibility(list, ScrollBarVisibility.Disabled);
        var matchCount = new TextBlock { FontSize = 11, Foreground = Brushes.DimGray, Margin = new Thickness(0, 4, 0, 0) };
        var panel = new StackPanel { Margin = new Thickness(6) };
        panel.Children.Add(new TextBlock { Text = "Type to search - Enter picks, Esc cancels", FontSize = 11, Foreground = Brushes.DimGray, Margin = new Thickness(0, 0, 0, 4) });
        panel.Children.Add(searchBox);
        panel.Children.Add(list);
        panel.Children.Add(matchCount);
        var popup = new Popup
        {
            PlacementTarget = combo, Placement = PlacementMode.Bottom, StaysOpen = false, AllowsTransparency = false,
            MinWidth = Math.Max(combo.ActualWidth, 240),
            Child = new Border { Background = Brushes.White, BorderBrush = Brushes.Gray, BorderThickness = new Thickness(1), Child = panel },
        };

        void Refresh()
        {
            // names keep the game's @ for a space ("Long@Knife"), so both match a typed space
            string query = searchBox.Text.Trim().Replace('@', ' ');
            var hits = query.Length == 0 ? allEntries : allEntries.Where(h => h.Text.Replace('@', ' ').Contains(query, StringComparison.OrdinalIgnoreCase)).ToList();
            list.ItemsSource = hits;
            list.SelectedIndex = hits.Count > 0 ? 0 : -1;
            if (hits.Count > 0) list.ScrollIntoView(hits[0]);
            matchCount.Text = hits.Count == 1 ? "1 match" : $"{hits.Count} matches";
        }
        void Pick()
        {
            if (list.SelectedItem is Hit hit) combo.SelectedIndex = hit.Index;
            popup.IsOpen = false;
            combo.Focus();
        }
        void Move(int step)
        {
            int itemCount = list.Items.Count;
            if (itemCount == 0) return;
            list.SelectedIndex = Math.Clamp(list.SelectedIndex + step, 0, itemCount - 1);
            list.ScrollIntoView(list.SelectedItem);
        }

        searchBox.TextChanged += (_, _) => Refresh();
        searchBox.PreviewKeyDown += (_, key) =>
        {
            switch (key.Key)
            {
                case Key.Down: Move(1); key.Handled = true; break;
                case Key.Up: Move(-1); key.Handled = true; break;
                case Key.PageDown: Move(10); key.Handled = true; break;
                case Key.PageUp: Move(-10); key.Handled = true; break;
                case Key.Enter: Pick(); key.Handled = true; break;
                case Key.Escape: popup.IsOpen = false; combo.Focus(); key.Handled = true; break;
            }
        };
        list.PreviewMouseLeftButtonUp += (_, mouse) =>
        {
            if (mouse.OriginalSource is DependencyObject source && ItemsControl.ContainerFromElement(list, source) is ListBoxItem item)
            {
                list.SelectedItem = item.DataContext;
                Pick();
            }
        };
        popup.Opened += (_, _) =>
        {
            searchBox.Focus();
            Keyboard.Focus(searchBox);
            searchBox.CaretIndex = searchBox.Text.Length;
        };
        Refresh();
        popup.IsOpen = true;
    }
}
