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

// the new statuses (L2StatusPatch) of the record a page shows. a row per status, a column per thing that can give it
// (a weapon's hit causes it, armor resists it, a spell causes, cures or wards it, a monster resists it or has it from
// the start). each change writes the script step (or the monster's starting statuses) right away
public sealed class L2NewStatusCard : Border
{
    // a column: its heading and what it writes (a script kind and a command, or the monster's starting statuses)
    public sealed record Column(string Header, string Tip, Kind Kind, What What, bool Start = false);

    private L2Page? _page;
    private Column[] _columns = Array.Empty<Column>();
    private readonly StackPanel _body = new() { Width = 470 };
    private bool _loading;

    public L2NewStatusCard()
    {
        Child = _body;
        Loaded += (_, _) => { if (Style == null && TryFindResource("GdCard") is Style cardStyle) Style = cardStyle; };
    }

    // shows the card for page's records with these columns (call once, from the view)
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
        if (e.PropertyName is nameof(L2Page.SelectedIndex) or nameof(L2Page.IsEditable) or nameof(L2Page.Reloads)) Dispatcher.BeginInvoke(Build);
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
                   (_columns.Any(c => HasEvery(c)) ? " The first row sets all 16 at once (resist it 100: immune to every one)." : "") +
                   (L2StatusPatch.IsIn(rom) ? "" : " They aren't in this ROM yet."),
        });
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(110) });
        foreach (var _ in _columns) grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        for (int r = 0; r <= 17; r++) grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        for (int c = 0; c < _columns.Length; c++)
        {
            var heading = new TextBlock { Text = _columns[c].Header, ToolTip = _columns[c].Tip, Style = TryFindResource("GdFieldLabel") as Style, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(2, 0, 2, 4) };
            Grid.SetColumn(heading, c + 1);
            grid.Children.Add(heading);
        }
        int start = _columns.Any(c => c.Start) ? L2StatusPatch.MonsterStart(rom, index) : 0;
        // a row per status, and first (if a column has it) "every new status" (status 10: 5E, 5F, 60)
        var values = _columns.Select(c => c.Start ? null : L2StatusSteps.GetAll(rom, c.Kind, index, c.What)
            .Append(HasEvery(c) ? L2StatusSteps.Get(rom, c.Kind, index, c.What, L2StatusPatch.Every) : 0).ToArray()).ToArray();
        bool every = _columns.Any(HasEvery);
        _loading = true;
        foreach (int status in (every ? new[] { L2StatusPatch.Every } : Array.Empty<int>()).Concat(Enumerable.Range(0, 16)))
        {
            int row = status == L2StatusPatch.Every ? 1 : every ? status + 2 : status + 1;
            var name = new TextBlock
            {
                Text = status == L2StatusPatch.Every ? "Every status" : L2StatusPatch.Names[status],
                ToolTip = status == L2StatusPatch.Every ? "All 16 at once (a single step: 'every new status')." : L2StatusPatch.Help[status],
                Style = TryFindResource("GdFieldLabel") as Style, VerticalAlignment = VerticalAlignment.Center,
            };
            Grid.SetRow(name, row);
            grid.Children.Add(name);
            for (int c = 0; c < _columns.Length; c++)
            {
                var column = _columns[c];
                int capturedStatus = status;
                if (status == L2StatusPatch.Every && !HasEvery(column)) continue;
                FrameworkElement cell;
                if (column.Start || column.What == What.Ward)
                {
                    bool on = column.Start ? (start & 1 << status) != 0 : values[c]![status] > 0;
                    var checkBox = new CheckBox { IsChecked = on, Style = TryFindResource("GdCheck") as Style, Margin = new Thickness(4, 1, 4, 1), VerticalAlignment = VerticalAlignment.Center };
                    checkBox.Click += (_, _) => Write(column, capturedStatus, checkBox.IsChecked == true ? 100 : 0);
                    cell = checkBox;
                }
                else
                {
                    var numberBox = new TextBox { Text = values[c]![status] == 0 ? "" : values[c]![status].ToString(CultureInfo.InvariantCulture), Style = TryFindResource("GdNumber") as Style, Width = 48, Margin = new Thickness(2, 1, 2, 1), HorizontalAlignment = HorizontalAlignment.Left, ToolTip = column.Tip };
                    int shown = values[c]![status];
                    numberBox.LostFocus += (_, _) =>
                    {
                        string typed = numberBox.Text.Trim();
                        if (typed.Length == 0) typed = "0";
                        // a bad number puts back the value the ROM has (blanking it used to show "none" while the ROM kept
                        // the old value, then the next visit to the box wrote 0 and took the step out)
                        if (!int.TryParse(typed, NumberStyles.Integer, CultureInfo.InvariantCulture, out int percent) || percent is < 0 or > 100)
                        { numberBox.Text = shown == 0 ? "" : shown.ToString(CultureInfo.InvariantCulture); return; }
                        if (percent == shown) return;   // nothing typed
                        Write(column, capturedStatus, percent);
                    };
                    cell = numberBox;
                }
                Grid.SetRow(cell, row);
                Grid.SetColumn(cell, c + 1);
                grid.Children.Add(cell);
            }
        }
        _loading = false;
        _body.Children.Add(grid);
    }

    // true if a column takes "every new status" (cure, resist, ward, not causes or starting statuses)
    private static bool HasEvery(Column column) => !column.Start && column.What != What.Cause;

    private void Write(Column column, int status, int value)
    {
        var rom = _page?.PageRom;
        int index = _page?.PageIndex ?? -1;
        if (_loading || rom == null || index < 0) return;
        try
        {
            bool changed;
            if (column.Start)
            {
                int bits = L2StatusPatch.MonsterStart(rom, index);
                int now = value > 0 ? bits | 1 << status : bits & ~(1 << status);
                changed = now != bits || !L2StatusPatch.IsApplied(rom);
                if (changed) L2StatusPatch.SetMonsterStart(rom, index, now);
            }
            else changed = L2StatusSteps.Set(rom, column.Kind, index, column.What, status, value);
            if (changed) _page!.ReportChange($"{L2StatusPatch.NameOf(status)}: {column.Header.ToLowerInvariant()} {(value == 0 ? "none" : column.Start || column.What == What.Ward ? "on" : value + "%")}");
        }
        catch (Exception ex) { MessageBox.Show(ex.Message, "New statuses", MessageBoxButton.OK, MessageBoxImage.Warning); }
        Dispatcher.BeginInvoke(Build);
    }
}
