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

// one dialogue box in the list
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

// one entry of the character picker. that's everyone who speaks, recognised by their sprite (a character keeps the
// same sprite on every map) or as a party member
public sealed class CharacterOption
{
    public string Key { get; init; } = "";
    public string Label { get; init; } = "";
    public string Detail { get; init; } = "";
    public ImageSource? Picture { get; init; }
}

// the text editor: every dialogue box in the game in one list (where it happens, who says it, with a picture of the
// character) and an editor for the picked box. saving goes through the event script writer
public partial class TextEditorViewModel : ObservableObject, LufiaForge.ViewModels.IPendingEdits
{
    private RomBuffer? _rom;
    private MainViewModel? _mainVm;
    private List<DialogueRow> _all = new();
    private readonly Dictionary<int, ImageSource?> _pictures = new();
    private bool _loadingSelection;

    public ObservableCollection<DialogueRow> Rows { get; } = new();
    public ObservableCollection<string> MapFilters { get; } = new();
    // everyone who speaks. pick one to list all their lines on every map
    public ObservableCollection<CharacterOption> CharacterFilters { get; } = new();
    [ObservableProperty] private CharacterOption? _characterFilter;
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

    private int _buildToken;

    private async Task BuildAsync()
    {
        if (_rom == null) return;
        IsBusy = true;
        StatusText = "Collecting every dialogue box in the game…";
        var rom = _rom;
        _builtVersion = rom.Version;
        int build = ++_buildToken;
        List<MapInfo> maps;
        List<DialogueLine> lines;
        try
        {
            maps = MapCatalog.ScanNamed(rom);
            lines = await Task.Run(() => DialogueIndex.Build(rom, maps.Select(m => m.MapId),
                (mapIndex, mapCount) => Application.Current?.Dispatcher.BeginInvoke(() => StatusText = $"Collecting dialogue… map {mapIndex + 1} of {mapCount}")));
        }
        catch (Exception ex)
        {
            // (some odd event in a hack) the tab says so and tries again next time, instead of sitting on "Collecting"
            if (build == _buildToken) { IsBusy = false; _builtVersion = -1; StatusText = "The dialogue couldn't be collected: " + ex.Message; }
            return;
        }
        if (build != _buildToken || rom != _rom) return;   // a newer build (or another ROM) took over
        // where the user is (kept when the list gets rebuilt after a line moved). read it now, rows can get picked in
        // the meantime
        var keepLine = SelectedRow?.Line;
        int keepMap = MapFilterIndex >= 0 && MapFilterIndex < _mapFilterIds.Count ? _mapFilterIds[MapFilterIndex] : -1;
        string? keepCharacter = CharacterFilter?.Key;
        string keepText = EditText;
        _replacingList = true;   // (the rows go away and come back, nothing gets written for that)
        try { FillList(lines, maps, keepMap, keepCharacter); }
        finally { _replacingList = false; }
        IsBusy = false;
        StatusText = $"{_all.Count} dialogue boxes on {MapFilters.Count - 1} maps. Pick one to edit it.";
        // pick the same box again, found by its text. lines added or removed before it somewhere else change its
        // number, and a box whose text got changed somewhere else isn't picked by number (typing would go into
        // another box)
        if (keepLine != null && Rows.FirstOrDefault(r => r.Line.MapId == keepLine.MapId && r.Line.Event == keepLine.Event &&
                                                         r.Line.IsCodeView == keepLine.IsCodeView && r.Line.Text == keepLine.Text) is { } again)
        {
            SelectedRow = again;
            if (EditText != keepText) EditText = keepText;   // anything typed since the line was written stays
        }
        else if (keepLine != null && keepText.Replace("\r", "") != keepLine.Text)
        {
            StatusText = "The box you were editing was changed somewhere else (another tab, Undo or a patch), so your text wasn't written.";
            // there's no box to keep it in, so show it so it can be copied (Ctrl+C copies a message box) and put back in
            if (Application.Current != null)
                MessageBox.Show(StatusText + " Your text (Ctrl+C copies this message):\n\n" + keepText.Replace("\r", ""), "Dialogue not written",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void FillList(List<DialogueLine> lines, List<MapInfo> maps, int keepMap, string? keepCharacter)
    {
        var labels = maps.ToDictionary(m => m.MapId, m => m.Label);
        _all = lines.Select(line => new DialogueRow
        {
            Line = line,
            MapLabel = labels.GetValueOrDefault(line.MapId, $"{line.MapId:X2}"),
            Picture = PictureFor(line.Sprite),
            Preview = Flatten(line.Text),
        }).ToList();

        MapFilters.Clear(); _mapFilterIds.Clear();
        MapFilters.Add("All maps"); _mapFilterIds.Add(-1);
        foreach (var map in maps.Where(m => lines.Any(l => l.MapId == m.MapId)))
        {
            MapFilters.Add(map.Label); _mapFilterIds.Add(map.MapId);
        }
        MapFilterIndex = Math.Max(0, _mapFilterIds.IndexOf(keepMap));
        BuildCharacterFilters();
        if (keepCharacter != null && CharacterFilters.FirstOrDefault(c => c.Key == keepCharacter) is { } character) CharacterFilter = character;
        ApplyFilter();
    }

    // RomBuffer.Version when the list was read
    private int _builtVersion = -1;

    // the tab is shown again. if another tab, Undo or a patch changed the ROM since the list was read, read it again
    // (not while a box has text that isn't written yet, writing finds that box again, see Locate)
    public void RefreshIfStale()
    {
        if (_rom == null || IsBusy || _rom.Version == _builtVersion || HasPendingEdits) return;
        _ = BuildAsync();
    }

    private static string Flatten(string text)
    {
        var flat = text.Replace("\n", " ").Trim();
        return flat.Length > 140 ? flat[..140] + "…" : flat;
    }

    private ImageSource? PictureFor(int sprite)
    {
        if (_rom == null || sprite < 0) return null;
        if (_pictures.TryGetValue(sprite, out var picture)) return picture;
        if (NpcSprites.Render(_rom, sprite) is var (pixels, width, height))
        {
            var bitmap = BitmapSource.Create(width, height, 96, 96, PixelFormats.Bgra32, null, pixels, width * 4);
            bitmap.Freeze();
            picture = bitmap;
        }
        _pictures[sprite] = picture;
        return picture;
    }

    partial void OnSearchTextChanged(string value) => ApplyFilter();
    partial void OnMapFilterIndexChanged(int value) => ApplyFilter();
    partial void OnOnlyCharactersChanged(bool value) => ApplyFilter();
    partial void OnCharacterFilterChanged(CharacterOption? value) => ApplyFilter();

    // who a line belongs to: the sprite of the character speaking, or the party member
    private static string CharacterKey(DialogueLine line)
    {
        if (line.Speaker.StartsWith("Party actor ") && int.TryParse(line.Speaker.AsSpan(12, 1), out int actor)) return $"party:{actor}";
        return line.Sprite >= 0 ? $"sprite:{line.Sprite}" : "";
    }

    private static string PartyName(int actor) => actor switch
    {
        0 or 1 => "Party leader",
        _ => $"Party member {actor}",
    };

    private void BuildCharacterFilters()
    {
        CharacterFilters.Clear();
        CharacterFilters.Add(new CharacterOption { Key = "", Label = "Everyone", Detail = $"{_all.Count} lines" });
        var groups = _all.GroupBy(r => CharacterKey(r.Line)).Where(g => g.Key.Length > 0)
                         .OrderBy(g => g.Key.StartsWith("party") ? 0 : 1).ThenByDescending(g => g.Count());
        foreach (var group in groups)
        {
            int maps = group.Select(r => r.Line.MapId).Distinct().Count();
            int events = group.Select(r => (r.Line.MapId, r.Line.Event)).Distinct().Count();
            bool party = group.Key.StartsWith("party");
            int number = int.Parse(group.Key[(group.Key.IndexOf(':') + 1)..]);
            CharacterFilters.Add(new CharacterOption
            {
                Key = group.Key,
                Label = party ? PartyName(number) : $"Sprite {number:X2}",
                Detail = $"{group.Count()} lines · {events} events · {maps} maps",
                Picture = party ? null : group.First().Picture,
            });
        }
        CharacterFilter = CharacterFilters[0];
    }

    private void ApplyFilter()
    {
        var keep = SelectedRow;
        Rows.Clear();
        int map = MapFilterIndex >= 0 && MapFilterIndex < _mapFilterIds.Count ? _mapFilterIds[MapFilterIndex] : -1;
        string term = SearchText.Trim();
        foreach (var row in _all)
        {
            if (map >= 0 && row.Line.MapId != map) continue;
            if (OnlyCharacters && !row.Line.Source.StartsWith("Character") && !row.Line.Speaker.StartsWith("Character")) continue;
            if (CharacterFilter is { Key.Length: > 0 } character && CharacterKey(row.Line) != character.Key) continue;
            if (term.Length > 0 && !row.Line.Text.Contains(term, StringComparison.OrdinalIgnoreCase) &&
                !row.Header.Contains(term, StringComparison.OrdinalIgnoreCase)) continue;
            Rows.Add(row);
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
        if (value == null) { LocationText = ""; SizeText = ""; KeepTyping(); return; }
        var line = value.Line;
        LocationText = $"{value.MapLabel} · event {line.Event}" + (line.Source.Length > 0 ? $" ({line.Source})" : "") +
                       $" · line {line.Line + 1}" + (line.IsCodeView ? $" of the code at 0x{line.RegionStart:X6}" : "") +
                       $"\n{line.Speaker} · ROM 0x{line.Offset:X6}";
        UpdateSize();
        KeepTyping();   // (the box we left couldn't be written, so go back to it)
    }

    partial void OnEditTextChanged(string value)
    {
        if (_loadingSelection) return;
        UpdateSize();
    }

    private EventScript? LoadScript(DialogueLine line) =>
        _rom == null ? null
        : line.IsCodeView ? EventScript.LoadAt(_rom, line.MapId, line.Event, line.RegionStart, line.JumpBase)
        : EventScript.Load(_rom, line.MapId, line.Event);

    // where a row's box is in its event right now. its line number while that line still has the box's text,
    // otherwise the one line of the event with that text (another tab added or removed lines before it). -1 if we
    // can't tell (the box got changed somewhere else, or the event that jumps to the code it's in moved). the list is
    // read when the tab opens, so a row's number alone could point at a different box by now
    public static int Locate(RomBuffer rom, EventScript? script, DialogueLine line)
    {
        if (script == null) return -1;
        if (line.IsCodeView && LufiaMap.EventScriptOffset(rom, line.MapId, line.Event) != line.JumpBase) return -1;
        if (line.Line < script.Ops.Count && script.Ops[line.Line].IsText && script.Ops[line.Line].OriginalText == line.Text) return line.Line;
        var same = Enumerable.Range(0, script.Ops.Count).Where(i => script.Ops[i].IsText && script.Ops[i].OriginalText == line.Text).ToList();
        return same.Count == 1 ? same[0] : -1;
    }

    private void UpdateSize()
    {
        if (SelectedRow == null || _rom == null) { SizeText = ""; return; }
        var script = LoadScript(SelectedRow.Line);
        int lineIndex = Locate(_rom, script, SelectedRow.Line);
        if (lineIndex < 0) { SizeText = "This box was changed somewhere else: it can't be written from here."; return; }
        var op = script!.Ops[lineIndex];
        int oldLength = op.RawLength - op.Bytes.Length;
        if (Core.DutchLetters.Unwritable(_rom, EditText) is string problem) { SizeText = "Can't be saved: " + problem + "."; return; }
        int newLength = script.EncodeText(EditText, op.Terminator).Length;
        SizeText = EditText.Replace("\r", "") == op.OriginalText ? $"{oldLength} bytes"
                 : newLength == oldLength ? $"{newLength} bytes (same size: saved in place)"
                 : $"{newLength} bytes (was {oldLength}: the event will be moved{(SelectedRow.Line.IsCodeView ? ": not possible for this line, keep the same size" : "")})";
    }

    // opens the whole event the picked line is in, in the event editor
    [RelayCommand]
    private void OpenEvent()
    {
        if (_rom == null || SelectedRow == null) return;
        var line = SelectedRow.Line;
        EventScript? script = null;
        try { script = EventScript.Load(_rom, line.MapId, line.Event); } catch { }
        if (script == null) { StatusText = "This line's event couldn't be opened."; return; }
        var editor = new LufiaForge.Modules.MapEditor.EventEditorWindow(_rom, script, $"{SelectedRow.MapLabel} · event {line.Event}", new Host(this))
        { Owner = Application.Current?.MainWindow };
        editor.Show();
    }

    // what the event editor needs when it's opened from here
    private sealed class Host : LufiaForge.Modules.MapEditor.IEventHost
    {
        private readonly TextEditorViewModel _vm;
        public Host(TextEditorViewModel vm) { _vm = vm; }
        public bool ConfirmExpand(string what) =>
            MessageBox.Show($"{what}\n\nTo store it, Lufia Forge needs to expand the ROM from 1 MB to 2 MB and use the new space. Continue?",
                "Expand ROM?", MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes;
        public void EventSaved(string what) { _vm._mainVm?.NotifyRomModified(what); _ = _vm.BuildAsync(); }
        public void ConfirmWritten(string heading, string details) =>
            InfoDialog.Show("Saved in the project", "✔ " + heading, details + "\n\nIt's in your project now: saving the project keeps it (Ctrl+S, or on its own every 5 minutes), and File > Export ROM writes a playable ROM.",
                path: null, pathLabel: null);
    }

    // the line being edited isn't what the ROM has
    public bool HasPendingEdits => _rom != null && SelectedRow != null && EditText.Replace("\r", "") != SelectedRow.Line.Text;

    public string? WritePendingEdits()
    {
        if (!HasPendingEdits) return null;
        return Write(SelectedRow!, quiet: true) ? null : StatusText;
    }

    // a box whose text couldn't be written when another one got picked. it gets picked again with that text
    private (DialogueRow Row, string Text)? _keepTyping;
    private bool _restoringRow, _replacingList;

    // picking another line writes the one being edited first. if that fails the text stays and the box gets picked
    // again with it (see OnSelectedRowChanged). not while the list is being re-read (that picks the box again itself)
    partial void OnSelectedRowChanging(DialogueRow? oldValue, DialogueRow? newValue)
    {
        if (_restoringRow || _replacingList) return;
        if (oldValue != null && _rom != null && EditText.Replace("\r", "") != oldValue.Line.Text && !Write(oldValue, quiet: true))
            _keepTyping = (oldValue, EditText);
    }

    private void KeepTyping()
    {
        if (_keepTyping is not { } kept) return;
        _keepTyping = null;
        string why = StatusText;
        void Back()
        {
            if (!Rows.Contains(kept.Row)) Rows.Insert(0, kept.Row);   // (filtered out in the meantime, so list it while it's being edited)
            _restoringRow = true;
            try { SelectedRow = kept.Row; }
            finally { _restoringRow = false; }
            _loadingSelection = true;
            EditText = kept.Text;
            _loadingSelection = false;
            UpdateSize();
            StatusText = why + " Your text is kept: change it so it can be written, or Revert.";
        }
        if (Application.Current?.Dispatcher is { } dispatcher) dispatcher.BeginInvoke(Back); else Back();
    }

    [RelayCommand]
    private void Apply()
    {
        if (_rom == null || SelectedRow == null) return;
        Write(SelectedRow, quiet: false);
    }

    // writes the text into the row's box. false (with the reason in the status) if it couldn't
    private bool Write(DialogueRow row, bool quiet)
    {
        if (_rom == null) return false;
        var line = row.Line;
        try
        {
            bool current = _rom.Version == _builtVersion;
            var script = LoadScript(line);
            int lineIndex = Locate(_rom, script, line);
            if (lineIndex < 0)
            {
                StatusText = "Not applied: this box was changed somewhere else since the list was read (another tab, Undo or a patch)." +
                             (quiet ? " Apply reads the list again." : "");
                // re-read the list. the box gets picked again by its text if it's still there, otherwise the text is shown to copy
                if (!quiet && !IsBusy) _ = BuildAsync();
                return false;
            }
            script!.Ops[lineIndex].Text = EditText.Replace("\r", "");
            if (!script.IsModified) { StatusText = "Nothing changed."; return true; }
            bool fits = script.FitsInPlace();
            bool expand = _rom.Length >= MapWriter.ExpandedSize || quiet;
            if (!fits && !expand)
            {
                if (MessageBox.Show("The new text is a different length, so its event has to be moved. To store it, Lufia Forge needs " +
                        "to expand the ROM from 1 MB to 2 MB. Expanded ROMs work in BizHawk, Snes9x and bsnes; savestates made before " +
                        "the expansion undo anything in the new space, so continue from an in-game save. Continue?",
                        "Expand ROM?", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
                { StatusText = "Not applied."; return false; }
                expand = true;
            }
            string result = script.Save(expand);
            // nothing but this write since the list was read, and the box is where the list has it, so the list is still right
            if (current && fits && lineIndex == line.Line) _builtVersion = _rom.Version;
            line.Text = EditText.Replace("\r", "");
            row.Preview = Flatten(line.Text);
            row.IsEdited = true;
            string what = $"Dialogue: {row.MapLabel}, event {line.Event}, line {line.Line + 1}";
            _mainVm?.NotifyRomModified(what);
            StatusText = "✔ Saved in the project.";
            UpdateSize();
            if (!quiet) InfoDialog.Show("Saved in the project", "✔ Dialogue saved in the project",
                result + "\n\nIt's in your project now: saving the project keeps it (Ctrl+S, or on its own every 5 minutes), and File > Export ROM writes a playable ROM.",
                path: null, pathLabel: null,
                primaryText: _mainVm != null ? "💾 Save project now" : null, primary: () => _mainVm?.SaveNow());
            if ((!script.IsCodeView && !fits) || lineIndex != line.Line) _ = BuildAsync();   // the event moved (line offsets changed), or the box's number did
            return true;
        }
        catch (Exception ex)
        {
            StatusText = "Not applied: " + ex.Message;
            return false;
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
        var dialog = new SaveFileDialog { Title = "Export all dialogue", Filter = "Text file (*.txt)|*.txt", FileName = "Lufia dialogue.txt" };
        if (dialog.ShowDialog() != true) return;
        using var writer = new StreamWriter(dialog.FileName);
        foreach (var row in _all)
        {
            writer.WriteLine($"== {row.Header} · {row.Line.Speaker} · ROM 0x{row.Line.Offset:X6}");
            writer.WriteLine(row.Line.Text);
            writer.WriteLine();
        }
        StatusText = $"Exported {_all.Count} dialogue boxes to {dialog.FileName}.";
    }
}
