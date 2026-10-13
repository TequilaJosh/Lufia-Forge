using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using TextBox = System.Windows.Controls.TextBox;
using ListBox = System.Windows.Controls.ListBox;

namespace LufiaForge.Modules.Common;

/// <summary>
/// The "Find…" box of a record list (the GdFindList style puts one above the list): typing selects the first entry
/// containing the text, Enter the next one, Esc clears it. Entries are only selected, never hidden, so lists bound by
/// index keep their numbering.
/// </summary>
public static class ListFind
{
    public static readonly DependencyProperty IsFindBoxProperty =
        DependencyProperty.RegisterAttached("IsFindBox", typeof(bool), typeof(ListFind), new PropertyMetadata(false, OnChanged));

    public static bool GetIsFindBox(DependencyObject o) => (bool)o.GetValue(IsFindBoxProperty);
    public static void SetIsFindBox(DependencyObject o, bool v) => o.SetValue(IsFindBoxProperty, v);

    private static void OnChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not TextBox box || e.NewValue is not true) return;
        box.TextChanged += (_, _) => Find(box, 0);
        box.KeyDown += (_, k) =>
        {
            if (k.Key == Key.Enter) { Find(box, (Keyboard.Modifiers & ModifierKeys.Shift) != 0 ? -1 : 1); k.Handled = true; }
            else if (k.Key == Key.Escape) { box.Clear(); k.Handled = true; }
            else if (k.Key == Key.Down && box.TemplatedParent is ListBox lb) { lb.Focus(); (lb.ItemContainerGenerator.ContainerFromIndex(Math.Max(0, lb.SelectedIndex)) as UIElement)?.Focus(); k.Handled = true; }
        };
    }

    /// <summary>Selects the next entry (step 1), the previous (-1) or the first from the current one (0) that matches.</summary>
    private static void Find(TextBox box, int step)
    {
        if (box.TemplatedParent is not ListBox list) return;
        string q = box.Text.Trim();
        if (q.Length == 0 || list.Items.Count == 0) return;
        int n = list.Items.Count, start = Math.Max(0, list.SelectedIndex);
        if (step == 0)
        {
            // typing: keep the selected entry when it still matches, else the first match from the top
            if (list.SelectedIndex >= 0 && TextOf(list, list.SelectedItem).Contains(q, StringComparison.OrdinalIgnoreCase)) return;
            start = 0;
        }
        for (int k = 0; k < n; k++)
        {
            int i = step >= 0 ? (start + step + k) % n : ((start - 1 - k) % n + n) % n;
            if (!TextOf(list, list.Items[i]).Contains(q, StringComparison.OrdinalIgnoreCase)) continue;
            if (list.SelectedIndex != i) list.SelectedIndex = i;
            list.ScrollIntoView(list.Items[i]);
            return;
        }
    }

    private static string TextOf(ListBox list, object? item)
    {
        if (item == null) return "";
        if (item is string s) return s;
        var type = item.GetType();
        if (!string.IsNullOrEmpty(list.DisplayMemberPath))
            return type.GetProperty(list.DisplayMemberPath)?.GetValue(item)?.ToString() ?? "";
        if (type.GetMethod("ToString", Type.EmptyTypes)?.DeclaringType != typeof(object)) return item.ToString() ?? "";
        // no text of its own: every readable string / number property
        return string.Join(" ", type.GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(p => p.GetIndexParameters().Length == 0 && (p.PropertyType == typeof(string) || p.PropertyType == typeof(int)))
            .Select(p => { try { return p.GetValue(item)?.ToString(); } catch { return null; } }));
    }
}
