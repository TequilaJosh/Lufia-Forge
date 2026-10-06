using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LufiaForge.Core;
using LufiaForge.Core.Maps;
using LufiaForge.Modules.Common;
using LufiaForge.ViewModels;
using Microsoft.Win32;
using System.Collections.ObjectModel;
using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace LufiaForge.Modules.TextEditor;

/// <summary>One dialogue box in the list.</summary>
public partial class DialogueRow : ObservableObject
{
    public DialogueLine Line { get; init; } = null!;
    public string MapLabel { get; init; } = "";
    public ImageSource? Picture { get; init; }
    public string Header => $"{MapLabel}" + (Line.Source.Length > 0 ? $"  ·  {Line.Source}" : $"  ·  event {Line.Event}");
    public string SpeakerText => Line.Speaker;
    [ObservableProperty] private string _preview = "";
    [ObservableProperty] private bool _isEdited;
}

/// <summary>
/// Text editor: every dialogue box of the game in one list (where it happens, who says it, with a picture
/// of the character), and an editor for the selected box. Saving goes through the event script writer.
/// </summary>
public partial class TextEditorViewModel : ObservableObject
{
    private RomBuffer? _rom;
    private MainViewModel? _mainVm;
    private List<DialogueRow> _all = new();
    private readonly Dictionary<int, ImageSource?> _pictures = new();
    private bool _loadingSelection;

    public ObservableCollection<DialogueRow> Rows { get; } = new();
    public ObservableCollection<string> MapFilters { get; } = new();
    private readonly List<int> _mapFilterIds = new();

    [ObservableProperty] private DialogueRow? _selectedRow;
    [ObservableProperty] private string _searchText = "";
    [ObservableProperty] private int _mapFilterIndex;
    [ObservableProperty] private bool _onlyCharacters;
    [ObservableProperty] private string _editText = "";
    [ObservableProperty] private string _locationText = "";
    [ObservableProperty] private string _sizeText = "";
    [ObservableProperty] private string _statusText = "Open a ROM to list the game's dialogue.";
    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private ImageSource? _selectedPicture;

    public bool HasSelection => SelectedRow != null;

    public void SetRom(RomBuffer rom, MainViewModel? mainVm = null)
    {
        _rom = rom;
        _mainVm = mainVm ?? _mainVm;
        TextDecoder.InvalidateDictionaryCache();
        _ = BuildAsync();
    }

    private async Task BuildAsync()
    {
        if (_rom == null) return;
        IsBusy = true;
        StatusText = "Collecting every dialogue box in the game…";
        var rom = _rom;
        var maps = MapCatalog.ScanNamed(rom);
        var lines = await Task.Run(() => DialogueIndex.Build(rom, maps.Select(m => m.MapId),
            (i, n) => Application.Current?.Dispatcher.BeginInvoke(() => StatusText = $"Collecting dialogue… map {i + 1} of {n}")));
        var labels = maps.ToDictionary(m => m.MapId, m => m.Label);
        _all = lines.Select(l => new DialogueRow
        {
            Line = l,
            MapLabel = labels.GetValueOrDefault(l.MapId, $"{l.MapId:X2}"),
            Picture = PictureFor(l.Sprite),
            Preview = Flatten(l.Text),
        }).ToList();

        MapFilters.Clear(); _mapFilterIds.Clear();
        MapFilters.Add("All maps"); _mapFilterIds.Add(-1);
        foreach (var m in maps.Where(m => lines.Any(l => l.MapId == m.MapId)))
        {
            MapFilters.Add(m.Label); _mapFilterIds.Add(m.MapId);
        }
        MapFilterIndex = 0;
        ApplyFilter();
        IsBusy = false;
        StatusText = $"{_all.Count} dialogue boxes on {MapFilters.Count - 1} maps. Pick one to edit it.";
    }

    private static string Flatten(string text)
    {
        var t = text.Replace("\n", " ").Trim();
        return t.Length > 140 ? t[..140] + "…" : t;
    }

    private ImageSource? PictureFor(int sprite)
    {
        if (_rom == null || sprite < 0) return null;
        if (_pictures.TryGetValue(sprite, out var img)) return img;
        if (NpcSprites.Render(_rom, sprite) is var (px, w, h))
        {
            var bmp = BitmapSource.Create(w, h, 96, 96, PixelFormats.Bgra32, null, px, w * 4);
            bmp.Freeze();
            img = bmp;
        }
        _pictures[sprite] = img;
        return img;
    }

    partial void OnSearchTextChanged(string value) => ApplyFilter();
    partial void OnMapFilterIndexChanged(int value) => ApplyFilter();
    partial void OnOnlyCharactersChanged(bool value) => ApplyFilter();

    private void ApplyFilter()
    {
        var keep = SelectedRow;
        Rows.Clear();
        int map = MapFilterIndex >= 0 && MapFilterIndex < _mapFilterIds.Count ? _mapFilterIds[MapFilterIndex] : -1;
        string q = SearchText.Trim();
        foreach (var r in _all)
        {
            if (map >= 0 && r.Line.MapId != map) continue;
            if (OnlyCharacters && !r.Line.Source.StartsWith("Character") && !r.Line.Speaker.StartsWith("Character")) continue;
            if (q.Length > 0 && !r.Line.Text.Contains(q, StringComparison.OrdinalIgnoreCase) &&
                !r.Header.Contains(q, StringComparison.OrdinalIgnoreCase)) continue;
            Rows.Add(r);
        }
        if (keep != null && Rows.Contains(keep)) SelectedRow = keep;
        if (!IsBusy) StatusText = $"{Rows.Count} of {_all.Count} dialogue boxes shown.";
    }

    partial void OnSelectedRowChanged(DialogueRow? value)
    {
        OnPropertyChanged(nameof(HasSelection));
        _loadingSelection = true;
        EditText = value?.Line.Text ?? "";
        _loadingSelection = false;
        SelectedPicture = value?.Picture;
        if (value == null) { LocationText = ""; SizeText = ""; return; }
        var l = value.Line;
        LocationText = $"{value.MapLabel} · event {l.Event}" + (l.Source.Length > 0 ? $" ({l.Source})" : "") +
                       $" · line {l.Line + 1}" + (l.IsCodeView ? $" of the code at 0x{l.RegionStart:X6}" : "") +
                       $"\n{l.Speaker} · ROM 0x{l.Offset:X6}";
        UpdateSize();
    }

    partial void OnEditTextChanged(string value)
    {
        if (_loadingSelection) return;
        UpdateSize();
    }

    private EventScript? LoadScript(DialogueLine l) =>
        _rom == null ? null
        : l.IsCodeView ? EventScript.LoadAt(_rom, l.MapId, l.Event, l.RegionStart, l.JumpBase)
        : EventScript.Load(_rom, l.MapId, l.Event);

    private void UpdateSize()
    {
        if (SelectedRow == null || _rom == null) { SizeText = ""; return; }
        var s = LoadScript(SelectedRow.Line);
        if (s == null || SelectedRow.Line.Line >= s.Ops.Count) { SizeText = ""; return; }
        var op = s.Ops[SelectedRow.Line.Line];
        int was = op.RawLength - op.Bytes.Length;
        int now = s.EncodeText(EditText, op.Terminator).Length;
        SizeText = EditText.Replace("\r", "") == op.OriginalText ? $"{was} bytes"
                 : now == was ? $"{now} bytes (same size: saved in place)"
                 : $"{now} bytes (was {was}: the event will be moved{(SelectedRow.Line.IsCodeView ? " — not possible for this line, keep the same size" : "")})";
    }

    [RelayCommand]
    private void Apply()
    {
        if (_rom == null || SelectedRow == null) return;
        var row = SelectedRow;
        var l = row.Line;
        try
        {
            var s = LoadScript(l);
            if (s == null || l.Line >= s.Ops.Count || !s.Ops[l.Line].IsText) { StatusText = "This line can't be found any more."; return; }
            s.Ops[l.Line].Text = EditText.Replace("\r", "");
            if (!s.IsModified) { StatusText = "Nothing changed."; return; }
            bool fits = s.FitsInPlace();
            bool expand = _rom.Length >= MapWriter.ExpandedSize;
            if (!fits && !expand)
            {
                if (MessageBox.Show("The new text is a different length, so its event has to be moved. To store it, Lufia Forge needs " +
                        "to expand the ROM from 1 MB to 2 MB. Expanded ROMs work in BizHawk, Snes9x and bsnes; savestates made before " +
                        "the expansion undo anything in the new space, so continue from an in-game save. Continue?",
                        "Expand ROM?", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
                { StatusText = "Not applied."; return; }
                expand = true;
            }
            string result = s.Save(expand);
            l.Text = EditText.Replace("\r", "");
            row.Preview = Flatten(l.Text);
            row.IsEdited = true;
            string what = $"Dialogue: {row.MapLabel}, event {l.Event}, line {l.Line + 1}";
            _mainVm?.NotifyRomModified(what);
            StatusText = "✔ Written to the ROM. Use Save ROM to write a dated copy.";
            UpdateSize();
            InfoDialog.Show("Written to ROM", "✔ Dialogue written to the ROM",
                result + "\n\nThe change is in the ROM loaded in Lufia Forge, but not on disk yet. Use File > Save ROM " +
                "(or the button below) to write a new dated copy; the ROM you opened is never overwritten.",
                path: _rom.FilePath, pathLabel: "ROM being edited (the copy is saved next to it)",
                primaryText: _mainVm != null ? "💾 Save ROM now" : null, primary: () => _mainVm?.SaveNow());
            if (!s.IsCodeView && !fits) _ = BuildAsync();   // the event moved: line offsets changed
        }
        catch (Exception ex)
        {
            StatusText = "Not applied: " + ex.Message;
        }
    }

    [RelayCommand]
    private void Revert()
    {
        if (SelectedRow == null) return;
        _loadingSelection = true;
        EditText = SelectedRow.Line.Text;
        _loadingSelection = false;
        UpdateSize();
    }

    [RelayCommand]
    private void ExportAll()
    {
        if (_all.Count == 0) return;
        var dlg = new SaveFileDialog { Title = "Export all dialogue", Filter = "Text file (*.txt)|*.txt", FileName = "Lufia dialogue.txt" };
        if (dlg.ShowDialog() != true) return;
        using var w = new StreamWriter(dlg.FileName);
        foreach (var r in _all)
        {
            w.WriteLine($"== {r.Header} · {r.Line.Speaker} · ROM 0x{r.Line.Offset:X6}");
            w.WriteLine(r.Line.Text);
            w.WriteLine();
        }
        StatusText = $"Exported {_all.Count} dialogue boxes to {dlg.FileName}.";
    }
}
