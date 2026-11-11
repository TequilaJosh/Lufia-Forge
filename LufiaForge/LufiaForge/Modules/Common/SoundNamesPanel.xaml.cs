using System.Windows;
using System.Windows.Controls;
using CommunityToolkit.Mvvm.ComponentModel;
using LufiaForge.Core.Audio;
using Button = System.Windows.Controls.Button;

namespace LufiaForge.Modules.Common;

/// <summary>One sound in the Settings list: its name (typed in place) and where the given name comes from.</summary>
public partial class SoundNameRow : ObservableObject
{
    private readonly bool _lufia2;
    public int Id { get; }
    public string IdText => Id.ToString("X2");
    public string Why => SoundNames.Why(_lufia2, Id) + (IsRenamed ? $" Given name: {SoundNames.Default(_lufia2, Id)}." : "");
    public bool IsRenamed => SoundNames.IsRenamed(_lufia2, Id);

    public SoundNameRow(bool lufia2, int id) { _lufia2 = lufia2; Id = id; }

    public string Name
    {
        get => SoundNames.Name(_lufia2, Id);
        set { SoundNames.Rename(_lufia2, Id, value); Refresh(); }
    }

    public void Refresh()
    {
        OnPropertyChanged(nameof(Name)); OnPropertyChanged(nameof(IsRenamed)); OnPropertyChanged(nameof(Why));
    }
}

/// <summary>Settings: the game's sounds with their names, each renamable and with Listen. <see cref="Lufia2"/> picks the game.</summary>
public partial class SoundNamesPanel : UserControl
{
    private readonly List<SoundNameRow> _all = new();

    public static readonly DependencyProperty Lufia2Property = DependencyProperty.Register(nameof(Lufia2), typeof(bool), typeof(SoundNamesPanel),
        new PropertyMetadata(false, (d, _) => ((SoundNamesPanel)d).Fill()));

    public bool Lufia2 { get => (bool)GetValue(Lufia2Property); set => SetValue(Lufia2Property, value); }

    public SoundNamesPanel()
    {
        InitializeComponent();
        Fill();
        Unloaded += (_, _) => SoundPreview.StopWhenGone(this);
    }

    private void Fill()
    {
        _all.Clear();
        for (int i = 0; i < SoundNames.Count(Lufia2); i++) _all.Add(new SoundNameRow(Lufia2, i));
        Filter();
    }

    private void Filter()
    {
        string f = FindBox?.Text.Trim() ?? "";
        Rows.ItemsSource = f.Length == 0 ? _all
            : _all.Where(r => r.IdText.Equals(f, StringComparison.OrdinalIgnoreCase) || r.Name.Contains(f, StringComparison.OrdinalIgnoreCase)).ToList();
    }

    private void Find_TextChanged(object sender, TextChangedEventArgs e) => Filter();

    private static Core.RomBuffer? Rom => (Application.Current?.MainWindow?.DataContext as ViewModels.MainViewModel)?.RomBuffer;

    private void Listen_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { DataContext: SoundNameRow r } b) return;
        Note.Text = SoundPreview.Toggle(b, Rom, Lufia2, song: false, r.Id);
    }

    private void Reset_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: SoundNameRow r }) r.Name = "";
    }
}
