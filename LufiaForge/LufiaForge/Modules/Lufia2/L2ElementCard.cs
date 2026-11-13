using System.ComponentModel;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using LufiaForge.Core.Lufia2;
using Who = LufiaForge.Core.Lufia2.L2Elements.Who;
using TextBox = System.Windows.Controls.TextBox;
using HorizontalAlignment = System.Windows.HorizontalAlignment;

namespace LufiaForge.Modules.Lufia2;

/// <summary>
/// The element percents of the record a page shows (<see cref="L2Elements"/>): a monster's, a hero's own, or an item's
/// (what wearing it adds). A box per element; a change is written at once (the battle code and the table go in with
/// the first one).
/// </summary>
public sealed class L2ElementCard : Border
{
    private L2Page? _page;
    private Who _who;
    private Func<int, bool>? _shows;
    private readonly StackPanel _body = new() { Width = 470 };
    private bool _loading;

    public L2ElementCard()
    {
        Child = _body;
        Loaded += (_, _) => { if (Style == null && TryFindResource("GdCard") is Style st) Style = st; };
    }

    /// <summary><paramref name="shows"/>: whether a record has the card (an item: only equipment).</summary>
    public void Attach(L2Page page, Who who, Func<int, bool>? shows = null)
    {
        if (_page != null) _page.PropertyChanged -= PageChanged;
        _page = page;
        _who = who;
        _shows = shows;
        page.PropertyChanged += PageChanged;
        Build();
    }

    private void PageChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(L2Page.SelectedIndex) or nameof(L2Page.IsEditable)) Dispatcher.BeginInvoke(Build);
    }

    private string Hint() => _who switch
    {
        Who.Monster => "How much each element hurts it, in percent: +22 takes 22% more, -42 takes 42% less, -100 nothing, -200 heals it as much as it would hurt. Several elements in one attack multiply (fire +50 and light +50 make x2.25). It changes the attack's power before DFP, as the game's own weaknesses do.",
        Who.Hero => "This hero's own element percents (+ takes more, - less, -100 nothing, below -100 heals). The items the hero wears add theirs (Items page). The STATUS screen in the game shows the total with Y.",
        _ => "What wearing it adds to the hero's element percents (+ takes more, - less, -100 nothing, below -100 heals). Any equipment slot counts, the weapon too.",
    };

    private void Build()
    {
        _body.Children.Clear();
        var rom = _page?.PageRom;
        int index = _page?.PageIndex ?? -1;
        if (_shows != null && rom != null && index >= 0)
        {
            bool show = _shows(index);
            Visibility = show ? Visibility.Visible : Visibility.Collapsed;
            if (!show) return;
        }
        _body.Children.Add(new TextBlock { Text = "ELEMENTS", Style = TryFindResource("GdSection") as Style });
        if (rom == null || index < 0 || !rom.IsLufia2) return;
        _body.Children.Add(new TextBlock
        {
            Style = TryFindResource("GdHint") as Style, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 6),
            Text = Hint() + (L2Elements.IsIn(rom) ? "" : " The element table isn't in this ROM yet: these are what the game's scripts do now. The first change puts the table in and moves them into it."),
        });
        var row = L2Elements.Row(rom, _who, index);
        var grid = new Grid();
        for (int c = 0; c < 4; c++) grid.ColumnDefinitions.Add(new ColumnDefinition { Width = c % 2 == 0 ? new GridLength(130) : new GridLength(90) });
        int rows = (L2Elements.Order.Length + 1) / 2;
        for (int r = 0; r < rows; r++) grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        _loading = true;
        for (int n = 0; n < L2Elements.Order.Length; n++)
        {
            int k = L2Elements.Order[n];
            int r = n % rows, c = n / rows * 2;
            var name = new TextBlock { Text = L2Elements.Name(k), Style = TryFindResource("GdFieldLabel") as Style, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis };
            Grid.SetRow(name, r); Grid.SetColumn(name, c);
            grid.Children.Add(name);
            var tb = new TextBox
            {
                Text = row[k] == 0 ? "" : (row[k] > 0 ? "+" : "") + row[k].ToString(CultureInfo.InvariantCulture),
                Style = TryFindResource("GdNumber") as Style, Width = 60, Margin = new Thickness(2, 1, 2, 1), HorizontalAlignment = HorizontalAlignment.Left,
                ToolTip = $"{L2Elements.Name(k)}: percent ({L2Elements.Min} to +{L2Elements.Max}; empty = 0)",
            };
            int kk = k;
            tb.LostFocus += (_, _) =>
            {
                string t = tb.Text.Trim().TrimEnd('%').Replace("+", "");
                if (t.Length == 0) t = "0";
                if (!int.TryParse(t, NumberStyles.Integer, CultureInfo.InvariantCulture, out int v) || v < L2Elements.Min || v > L2Elements.Max)
                {
                    Dispatcher.BeginInvoke(Build);
                    return;
                }
                Write(kk, v);
            };
            Grid.SetRow(tb, r); Grid.SetColumn(tb, c + 1);
            grid.Children.Add(tb);
        }
        _loading = false;
        _body.Children.Add(grid);
    }

    private void Write(int k, int percent)
    {
        var rom = _page?.PageRom;
        int index = _page?.PageIndex ?? -1;
        if (_loading || rom == null || index < 0) return;
        try
        {
            if (L2Elements.Set(rom, _who, index, k, percent))
                _page!.ReportChange($"{L2Elements.Name(k)} {(percent > 0 ? "+" : "")}{percent}%");
        }
        catch (Exception e) { MessageBox.Show(e.Message, "Elements", MessageBoxButton.OK, MessageBoxImage.Warning); }
        Dispatcher.BeginInvoke(Build);
    }
}
