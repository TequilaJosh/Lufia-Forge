using System.ComponentModel;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using LufiaForge.Core.Lufia2;
using Kind = LufiaForge.Core.Lufia2.L2AiScripts.Kind;
using What = LufiaForge.Core.Lufia2.L2StatusSteps.What;
using CheckBox = System.Windows.Controls.CheckBox;
using TextBox = System.Windows.Controls.TextBox;
using HorizontalAlignment = System.Windows.HorizontalAlignment;

namespace LufiaForge.Modules.Lufia2;

/// <summary>
/// The new statuses (<see cref="L2StatusPatch"/>) of the record a page shows: a row per status, a column per thing that
/// can give it (a weapon's hit causes it, armor resists it, a spell causes, cures or wards it, a monster resists it or has
/// it from the start). Each change writes the script step (or the monster's starting statuses) at once.
/// </summary>
public sealed class L2NewStatusCard : Border
{
    /// <summary>A column: its heading, what it writes (a script kind and a command, or the monster's starting statuses).</summary>
    public sealed record Column(string Header, string Tip, Kind Kind, What What, bool Start = false);

    private L2Page? _page;
    private Column[] _columns = Array.Empty<Column>();
    private readonly StackPanel _body = new() { Width = 470 };
    private bool _loading;

    public L2NewStatusCard()
    {
        Child = _body;
        Loaded += (_, _) => { if (Style == null && TryFindResource("GdCard") is Style st) Style = st; };
    }

    /// <summary>Shows the card for <paramref name="page"/>'s records with these columns (call once, from the view).</summary>
    public void Attach(L2Page page, params Column[] columns)
    {
        if (_page != null) _page.PropertyChanged -= PageChanged;
        _page = page;
        _columns = columns;
        page.PropertyChanged += PageChanged;
        Build();
    }

    private void PageChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(L2Page.SelectedIndex) or nameof(L2Page.IsEditable)) Dispatcher.BeginInvoke(Build);
    }

    private void Build()
    {
        _body.Children.Clear();
        _body.Children.Add(new TextBlock { Text = "NEW STATUSES", Style = TryFindResource("GdSection") as Style });
        var rom = _page?.PageRom;
        int index = _page?.PageIndex ?? -1;
        if (rom == null || index < 0 || !rom.IsLufia2) return;
        _body.Children.Add(new TextBlock
        {
            Style = TryFindResource("GdHint") as Style, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 6),
            Text = "Lufia Forge's 16 statuses (new battle code, added to the game with the first one set here). A number is a chance in percent (resist: 100 = immune); hover a status for what it does." +
                   (L2StatusPatch.IsApplied(rom) ? "" : " They aren't in this ROM yet."),
        });
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(110) });
        foreach (var _ in _columns) grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        for (int r = 0; r <= 16; r++) grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        for (int c = 0; c < _columns.Length; c++)
        {
            var h = new TextBlock { Text = _columns[c].Header, ToolTip = _columns[c].Tip, Style = TryFindResource("GdFieldLabel") as Style, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(2, 0, 2, 4) };
            Grid.SetColumn(h, c + 1);
            grid.Children.Add(h);
        }
        int start = _columns.Any(c => c.Start) ? L2StatusPatch.MonsterStart(rom, index) : 0;
        var values = _columns.Select(c => c.Start ? null : L2StatusSteps.GetAll(rom, c.Kind, index, c.What)).ToArray();
        _loading = true;
        for (int s = 0; s < 16; s++)
        {
            var name = new TextBlock { Text = L2StatusPatch.Names[s], ToolTip = L2StatusPatch.Help[s], Style = TryFindResource("GdFieldLabel") as Style, VerticalAlignment = VerticalAlignment.Center };
            Grid.SetRow(name, s + 1);
            grid.Children.Add(name);
            for (int c = 0; c < _columns.Length; c++)
            {
                var col = _columns[c];
                int st = s;
                FrameworkElement cell;
                if (col.Start || col.What == What.Ward)
                {
                    bool on = col.Start ? (start & 1 << s) != 0 : values[c]![s] > 0;
                    var cb = new CheckBox { IsChecked = on, Style = TryFindResource("GdCheck") as Style, Margin = new Thickness(4, 1, 4, 1), VerticalAlignment = VerticalAlignment.Center };
                    cb.Click += (_, _) => Write(col, st, cb.IsChecked == true ? 100 : 0);
                    cell = cb;
                }
                else
                {
                    var tb = new TextBox { Text = values[c]![s] == 0 ? "" : values[c]![s].ToString(CultureInfo.InvariantCulture), Style = TryFindResource("GdNumber") as Style, Width = 48, Margin = new Thickness(2, 1, 2, 1), HorizontalAlignment = HorizontalAlignment.Left, ToolTip = col.Tip };
                    tb.LostFocus += (_, _) =>
                    {
                        string t = tb.Text.Trim();
                        if (t.Length == 0) t = "0";
                        if (!int.TryParse(t, NumberStyles.Integer, CultureInfo.InvariantCulture, out int v) || v is < 0 or > 100) { tb.Text = ""; return; }
                        Write(col, st, v);
                    };
                    cell = tb;
                }
                Grid.SetRow(cell, s + 1);
                Grid.SetColumn(cell, c + 1);
                grid.Children.Add(cell);
            }
        }
        _loading = false;
        _body.Children.Add(grid);
    }

    private void Write(Column col, int status, int value)
    {
        var rom = _page?.PageRom;
        int index = _page?.PageIndex ?? -1;
        if (_loading || rom == null || index < 0) return;
        try
        {
            bool changed;
            if (col.Start)
            {
                int bits = L2StatusPatch.MonsterStart(rom, index);
                int now = value > 0 ? bits | 1 << status : bits & ~(1 << status);
                changed = now != bits || !L2StatusPatch.IsApplied(rom);
                if (changed) L2StatusPatch.SetMonsterStart(rom, index, now);
            }
            else changed = L2StatusSteps.Set(rom, col.Kind, index, col.What, status, value);
            if (changed) _page!.ReportChange($"{L2StatusPatch.Names[status]}: {col.Header.ToLowerInvariant()} {(value == 0 ? "none" : col.Start || col.What == What.Ward ? "on" : value + "%")}");
        }
        catch (Exception e) { MessageBox.Show(e.Message, "New statuses", MessageBoxButton.OK, MessageBoxImage.Warning); }
        Dispatcher.BeginInvoke(Build);
    }
}
