using System.ComponentModel;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using LufiaForge.Core.Lufia2;
using Who = LufiaForge.Core.Lufia2.L2Elements.Who;
using TextBox = System.Windows.Controls.TextBox;
using HorizontalAlignment = System.Windows.HorizontalAlignment;

namespace LufiaForge.Modules.Lufia2;

// the element percents of the record a page shows (L2Elements): a monster's, a hero's own, or an item's (what
// wearing it adds). one box per element, changes get written right away (the battle code and the table go in with
// the first one)
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
        Loaded += (_, _) => { if (Style == null && TryFindResource("GdCard") is Style cardStyle) Style = cardStyle; };
    }

    // shows = whether a record gets the card (for items only equipment does)
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
        if (e.PropertyName is nameof(L2Page.SelectedIndex) or nameof(L2Page.IsEditable) or nameof(L2Page.Reloads)) Dispatcher.BeginInvoke(Build);
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
        var percents = L2Elements.Row(rom, _who, index);
        var grid = new Grid();
        for (int col = 0; col < 4; col++) grid.ColumnDefinitions.Add(new ColumnDefinition { Width = col % 2 == 0 ? new GridLength(130) : new GridLength(90) });
        int rows = (L2Elements.Order.Length + 1) / 2;
        for (int r = 0; r < rows; r++) grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        _loading = true;
        for (int n = 0; n < L2Elements.Order.Length; n++)
        {
            int element = L2Elements.Order[n];
            int gridRow = n % rows, gridCol = n / rows * 2;
            var name = new TextBlock { Text = L2Elements.Name(element), Style = TryFindResource("GdFieldLabel") as Style, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis };
            Grid.SetRow(name, gridRow); Grid.SetColumn(name, gridCol);
            grid.Children.Add(name);
            var box = new TextBox
            {
                Text = percents[element] == 0 ? "" : (percents[element] > 0 ? "+" : "") + percents[element].ToString(CultureInfo.InvariantCulture),
                Style = TryFindResource("GdNumber") as Style, Width = 60, Margin = new Thickness(2, 1, 2, 1), HorizontalAlignment = HorizontalAlignment.Left,
                ToolTip = $"{L2Elements.Name(element)}: percent ({L2Elements.Min} to +{L2Elements.Max}; empty = 0)",
            };
            int thisElement = element;
            box.LostFocus += (_, _) =>
            {
                string text = box.Text.Trim().TrimEnd('%').Replace("+", "");
                if (text.Length == 0) text = "0";
                if (!int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out int percent) || percent < L2Elements.Min || percent > L2Elements.Max)
                {
                    Dispatcher.BeginInvoke(Build);
                    return;
                }
                // the value it has right now (without this, clicking into the box and out again with nothing typed installed the status patch)
                int now = _page?.PageRom is { } pageRom && L2Elements.IsIn(pageRom) && _page.PageIndex >= 0 ? L2Elements.Get(pageRom, _who, _page.PageIndex, thisElement) : percents[thisElement];
                if (percent == now) return;
                Write(thisElement, percent);
            };
            Grid.SetRow(box, gridRow); Grid.SetColumn(box, gridCol + 1);
            grid.Children.Add(box);
        }
        _loading = false;
        _body.Children.Add(grid);
    }

    private void Write(int element, int percent)
    {
        var rom = _page?.PageRom;
        int index = _page?.PageIndex ?? -1;
        if (_loading || rom == null || index < 0) return;
        try
        {
            if (L2Elements.Set(rom, _who, index, element, percent))
                _page!.ReportChange($"{L2Elements.Name(element)} {(percent > 0 ? "+" : "")}{percent}%");
        }
        catch (Exception e) { MessageBox.Show(e.Message, "Elements", MessageBoxButton.OK, MessageBoxImage.Warning); }
        Dispatcher.BeginInvoke(Build);
    }
}
