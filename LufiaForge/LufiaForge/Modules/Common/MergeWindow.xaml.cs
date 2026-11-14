using System.ComponentModel;
using System.IO;
using System.Windows;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using LufiaForge.Core;
using LufiaForge.Core.Merge;
using Microsoft.Win32;
using Brush = System.Windows.Media.Brush;
using FontStyle = System.Windows.FontStyle;
using Brushes = System.Windows.Media.Brushes;
using Color = System.Windows.Media.Color;

namespace LufiaForge.Modules.Common;

/// <summary>
/// File, Merge Another Project Into This One: what the other project changed, by map (its events one by one, the lists
/// of its info, its layout) and by part of the game, each with its differential, ticked when it goes in. Things only the
/// other project changed start ticked; things both changed differently (conflicts) start unticked. Changes can be
/// counted from the version both projects were made from instead of the starting ROM (fewer conflicts). OK hands the
/// plan back (<see cref="Plan"/>); the caller makes the merge.
/// </summary>
public partial class MergeWindow : Window
{
    /// <summary>One line of the list.</summary>
    public sealed class Row : INotifyPropertyChanged
    {
        public MergeItem Item { get; }
        public Row(MergeItem item) { Item = item; }
        public string Group => Item.Group;
        public string What => Item.What;
        public bool Take
        {
            get => Item.Take;
            set { if (Item.Take == value) return; Item.Take = value; Changed(nameof(Take)); Changed(nameof(Status)); Changed(nameof(StatusBrush)); TakeChanged?.Invoke(); }
        }
        public string Status => Item.Conflict
            ? (Take ? "Conflict: the other project's version goes in" : "Conflict: this project's version stays")
            : (Take ? "Only the other project changed it: goes in" : "Only the other project changed it: left out");
        public Brush StatusBrush => (Brush)Application.Current.FindResource(Item.Conflict ? (Take ? "WarningBrush" : "ErrorBrush") : (Take ? "SuccessBrush" : "TextDisabledBrush"));
        public event PropertyChangedEventHandler? PropertyChanged;
        public static event Action? TakeChanged;
        public void Refresh() { Changed(nameof(Take)); Changed(nameof(Status)); Changed(nameof(StatusBrush)); }
        private void Changed(string p) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(p));
    }

    /// <summary>One line of a differential.</summary>
    public sealed record DetailLine(string Text, Brush Brush, FontStyle Style, Brush Back);

    private readonly Func<byte[]?, string?, Task<ProjectMerge>> _replan;
    private List<Row> _rows = new();
    public ProjectMerge Plan { get; private set; }

    public MergeWindow(ProjectMerge plan, Func<byte[]?, string?, Task<ProjectMerge>> replan)
    {
        InitializeComponent();
        _replan = replan;
        Plan = plan;
        Row.TakeChanged += Counts;
        Closed += (_, _) => Row.TakeChanged -= Counts;
        Load(plan);
    }

    private void Load(ProjectMerge plan)
    {
        Plan = plan;
        TitleText.Text = $"Merge \"{plan.TheirName}\" into \"{plan.OurName}\"";
        IntroText.Text = "Below is everything the other project changed that this one doesn't have, by map and by part of the game. " +
            "Ticked items go in. What only the other project changed starts ticked; what both projects changed differently (a conflict) " +
            "starts unticked, so this project's version stays. Pick an item to see the differential: this project's lines (-) against " +
            "the other project's (+).";
        BaseText.Text = plan.AncestorName != null
            ? $"Changes are counted from \"{plan.AncestorName}\", the version both projects were made from."
            : "Changes are counted from the ROM each project started from. If both projects were copied from one version, counting from that version leaves far fewer conflicts.";
        BaseButton.Visibility = plan.AncestorName != null ? Visibility.Visible : Visibility.Collapsed;
        WarningsText.Text = string.Join("\n", plan.Warnings.Select(w => "• " + w));
        WarningsText.Visibility = plan.Warnings.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        LogCheck.Content = $"Add the other project's change log lines to this one ({plan.TheirNewLog.Count})";
        LogCheck.IsEnabled = plan.TheirNewLog.Count > 0;
        NamesCheck.Content = $"Use the other project's map names where this project has none ({plan.TheirNewMapNames.Count})";
        NamesCheck.IsEnabled = plan.TheirNewMapNames.Count > 0;
        LogExpander.Header = $"The other project's change log since the projects split ({plan.TheirNewLog.Count} lines)";
        LogList.ItemsSource = plan.TheirNewLog;
        _rows = plan.Items.Select(i => new Row(i)).ToList();
        ApplyFilter();
        Counts();
        MergeButton.IsEnabled = true;
        ShowDetails(null);
    }

    private void ApplyFilter()
    {
        var view = new ListCollectionView(OnlyConflicts.IsChecked == true ? _rows.Where(r => r.Item.Conflict).ToList() : _rows);
        view.GroupDescriptions.Add(new PropertyGroupDescription(nameof(Row.Group)));
        List.ItemsSource = view;
    }

    private void Counts()
    {
        int only = _rows.Count(r => !r.Item.Conflict), both = _rows.Count - only;
        int taking = _rows.Count(r => r.Take);
        CountsText.Text = $"{only} change{(only == 1 ? "" : "s")} only the other project made, {both} conflict{(both == 1 ? "" : "s")}, " +
            $"{taking} of {_rows.Count} ticked to go in. Already the same in both: {Plan.SameCount} event{(Plan.SameCount == 1 ? "" : "s")}, lists or files" +
            (Plan.SameBytes > 0 ? $" and {(Plan.SameBytes < 1024 ? Plan.SameBytes + " bytes" : $"{Plan.SameBytes / 1024.0:0.#} KB")} of other data." : ".");
    }

    private void List_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e) => ShowDetails(List.SelectedItem as Row);

    private void ShowDetails(Row? row)
    {
        if (row == null) { DetailTitle.Text = "Pick an item to see what differs."; DetailWhy.Text = ""; DetailList.ItemsSource = null; return; }
        DetailTitle.Text = $"{row.Group}: {row.What}";
        DetailWhy.Text = row.Item.Why;
        List<DiffLine> lines;
        try { lines = row.Item.Details(); }
        catch (Exception ex) { lines = new() { new(DiffKind.Note, "The differential couldn't be made: " + ex.Message) }; }
        Brush B(string key) => (Brush)FindResource(key);
        var none = Brushes.Transparent;
        var red = new SolidColorBrush(Color.FromArgb(0x30, 0xE0, 0x50, 0x50));
        var green = new SolidColorBrush(Color.FromArgb(0x30, 0x50, 0xD0, 0x70));
        DetailList.ItemsSource = lines.Select(l => l.Kind switch
        {
            DiffKind.Ours => new DetailLine(l.Text, B("ErrorBrush"), FontStyles.Normal, red),
            DiffKind.Theirs => new DetailLine(l.Text, B("SuccessBrush"), FontStyles.Normal, green),
            DiffKind.Head => new DetailLine(l.Text, B("GoldBrush"), FontStyles.Normal, none),
            DiffKind.Note => new DetailLine(l.Text, B("TextDisabledBrush"), FontStyles.Italic, none),
            _ => new DetailLine(l.Text, B("TextSecondaryBrush"), FontStyles.Normal, none),
        }).ToList();
    }

    private void SetAll(Func<MergeItem, bool> take)
    {
        foreach (var r in _rows) r.Item.Take = take(r.Item);
        foreach (var r in _rows) r.Refresh();
        Counts();
    }

    private void TickAll_Click(object sender, RoutedEventArgs e) => SetAll(_ => true);
    private void UntickAll_Click(object sender, RoutedEventArgs e) => SetAll(_ => false);
    private void Suggested_Click(object sender, RoutedEventArgs e) => SetAll(i => !i.Conflict && !i.Why.Contains("isn't taken"));
    private void Filter_Click(object sender, RoutedEventArgs e) => ApplyFilter();

    private async void PickAncestor_Click(object sender, RoutedEventArgs e)
    {
        var d = new OpenFileDialog
        {
            Title = "Pick the version both projects were made from (a project, or a ROM)",
            Filter = $"Lufia Forge projects (*{LufiaProject.Extension})|*{LufiaProject.Extension}|SNES ROM files (*.sfc;*.smc)|*.sfc;*.smc|All files (*.*)|*.*",
        };
        if (d.ShowDialog(this) != true) return;
        byte[] bytes;
        string name;
        try
        {
            if (d.FileName.EndsWith(LufiaProject.Extension, StringComparison.OrdinalIgnoreCase))
            {
                var (p, rom) = LufiaProject.Open(d.FileName);
                bytes = rom; name = p.Name;
            }
            else
            {
                var report = RomLoader.Load(d.FileName);
                bytes = report.Buffer?.ToArray() ?? throw new InvalidDataException(report.Message);
                name = Path.GetFileName(d.FileName);
            }
        }
        catch (Exception ex) { MessageBox.Show(this, "That file couldn't be read: " + ex.Message, "Merge", MessageBoxButton.OK, MessageBoxImage.Error); return; }
        await Replan(bytes, name);
    }

    private async void UseBase_Click(object sender, RoutedEventArgs e) => await Replan(null, null);

    private async Task Replan(byte[]? ancestor, string? name)
    {
        MergeButton.IsEnabled = false;
        IsEnabled = false;
        Cursor = System.Windows.Input.Cursors.Wait;
        CountsText.Text = "Comparing the projects again...";
        try { Load(await _replan(ancestor, name)); }
        catch (Exception ex)
        {
            MessageBox.Show(this, "The projects couldn't be compared that way: " + ex.Message, "Merge", MessageBoxButton.OK, MessageBoxImage.Error);
            Load(Plan);
        }
        finally { IsEnabled = true; Cursor = null; }
    }

    private void Merge_Click(object sender, RoutedEventArgs e)
    {
        int conflictsTaken = _rows.Count(r => r.Item.Conflict && r.Take);
        if (conflictsTaken > 0 && _rows.Any(r => r.Item.Conflict && r.Take && r.Item.Why.Contains("same place in the free space")) &&
            MessageBox.Show(this, "Some ticked conflicts are new data both projects put at the same place in the free space. Taking the other " +
                "project's bytes there loses this project's change in that place (it has to be made again after the merge). Merge anyway?",
                "Merge", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
        Plan.TakeLog = LogCheck.IsChecked == true;
        Plan.TakeMapNames = NamesCheck.IsChecked == true;
        DialogResult = true;
    }
}
