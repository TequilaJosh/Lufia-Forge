using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using TextBox = System.Windows.Controls.TextBox;
using ListBox = System.Windows.Controls.ListBox;

namespace LufiaForge.Modules.Common;

// the "Find..." box of a record list (the GdFindList style puts one above the list). typing selects the first entry
// with the text in it, Enter goes to the next one, Esc clears it. entries only get selected, never hidden, so lists
// bound by index keep their numbering
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
        box.KeyDown += (_, keyArgs) =>
        {
            if (keyArgs.Key == Key.Enter) { Find(box, (Keyboard.Modifiers & ModifierKeys.Shift) != 0 ? -1 : 1); keyArgs.Handled = true; }
            else if (keyArgs.Key == Key.Escape) { box.Clear(); keyArgs.Handled = true; }
            else if (keyArgs.Key == Key.Down && box.TemplatedParent is ListBox listBox) { listBox.Focus(); (listBox.ItemContainerGenerator.ContainerFromIndex(Math.Max(0, listBox.SelectedIndex)) as UIElement)?.Focus(); keyArgs.Handled = true; }
        };
    }

    // selects the next match (step 1), the previous one (-1) or the first one starting at the current entry (0)
    private static void Find(TextBox box, int step)
    {
        if (box.TemplatedParent is not ListBox list) return;
        string term = box.Text.Trim();
        if (term.Length == 0 || list.Items.Count == 0) return;
        int count = list.Items.Count, start = Math.Max(0, list.SelectedIndex);
        if (step == 0)
        {
            // typing: keep the selected entry if it still matches, otherwise the first match from the top
            if (list.SelectedIndex >= 0 && TextOf(list, list.SelectedItem).Contains(term, StringComparison.OrdinalIgnoreCase)) return;
            start = 0;
        }
        for (int tried = 0; tried < count; tried++)
        {
            int i = step >= 0 ? (start + step + tried) % count : ((start - 1 - tried) % count + count) % count;
            if (!TextOf(list, list.Items[i]).Contains(term, StringComparison.OrdinalIgnoreCase)) continue;
            if (list.SelectedIndex != i) list.SelectedIndex = i;
            list.ScrollIntoView(list.Items[i]);
            return;
        }
    }

    private static string TextOf(ListBox list, object? item)
    {
        if (item == null) return "";
        if (item is string text) return text;
        var type = item.GetType();
        if (!string.IsNullOrEmpty(list.DisplayMemberPath))
            return type.GetProperty(list.DisplayMemberPath)?.GetValue(item)?.ToString() ?? "";
        if (type.GetMethod("ToString", Type.EmptyTypes)?.DeclaringType != typeof(object)) return item.ToString() ?? "";
        // no text of its own, so use every readable string / number property
        return string.Join(" ", type.GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(p => p.GetIndexParameters().Length == 0 && (p.PropertyType == typeof(string) || p.PropertyType == typeof(int)))
            .Select(p => { try { return p.GetValue(item)?.ToString(); } catch { return null; } }));
    }
}
