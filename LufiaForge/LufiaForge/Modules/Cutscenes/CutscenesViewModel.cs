using CommunityToolkit.Mvvm.ComponentModel;
using LufiaForge.Core;
using LufiaForge.Core.Maps;
using LufiaForge.Modules.Common;
using LufiaForge.Modules.MapEditor;
using LufiaForge.ViewModels;
using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace LufiaForge.Modules.Cutscenes;

/// <summary>A cutscene in the list.</summary>
public sealed class CutsceneEntry
{
    public int MapId { get; init; }
    public int Event { get; init; }
    public string MapLabel { get; init; } = "";
    /// <summary>A known name ("Opening intro") or empty.</summary>
    public string Title { get; init; } = "";
    public string Hint { get; init; } = "";
    public int StageCommands { get; init; }
    public int Texts { get; init; }
    public ImageSource? Picture { get; init; }
    public string Label => Title.Length > 0 ? Title : $"{MapLabel} – event {Event}";
    public string Details => (Title.Length > 0 ? $"{MapLabel} – event {Event} · " : "") +
                             $"{StageCommands} stage command{(StageCommands == 1 ? "" : "s")}, {Texts} text box{(Texts == 1 ? "" : "es")}";
}

/// <summary>
/// The Cutscenes tab: every event that moves characters, the camera or the screen, opened in the event editor's
/// cutscene layout (stage + timeline).
/// </summary>
public partial class CutscenesViewModel : ObservableObject, IEventHost
{
    private RomBuffer? _rom;
    private MainViewModel? _mainVm;
    private List<CutsceneEntry> _all = new();

    public RomBuffer? Rom => _rom;
    public ObservableCollection<CutsceneEntry> Cutscenes { get; } = new();
    public ObservableCollection<string> MapFilters { get; } = new();
    private readonly List<int> _mapFilterIds = new();

    [ObservableProperty] private CutsceneEntry? _selected;
    [ObservableProperty] private string _search = "";
    [ObservableProperty] private int _mapFilterIndex;
    [ObservableProperty] private bool _onlyBig;
    [ObservableProperty] private string _status = "Open a ROM to list its cutscenes.";
    [ObservableProperty] private bool _isBusy;

    /// <summary>Raised to open a cutscene in the editor.</summary>
    public event Action<EventScript, string>? OpenRequested;

    /// <summary>Names for cutscenes everyone knows.</summary>
    private static readonly Dictionary<(int Map, int Ev), string> KnownNames = new()
    {
        [(0x4F, 8)] = "Opening intro (before the title)",
        [(0x93, 2)] = "Prologue: the heroes enter the Fortress of Doom",
    };

    public void SetRom(RomBuffer rom, MainViewModel mainVm)
    {
        _rom = rom; _mainVm = mainVm;
        _ = BuildAsync();
    }

    private async Task BuildAsync()
    {
        if (_rom == null) return;
        var rom = _rom;
        IsBusy = true;
        Status = "Finding the cutscenes…";
        var maps = MapCatalog.ScanNamed(rom);
        var labels = maps.ToDictionary(m => m.MapId, m => m.Label);
        EventScript.MapLabel ??= id => labels.TryGetValue(id, out var l) ? l : $"map {id:X2}";
        var list = await Task.Run(() =>
        {
            var found = new List<CutsceneEntry>();
            foreach (var m in maps)
            {
                LufiaMap? map = null;
                try { map = LufiaMap.Load(rom, m.MapId); } catch { }
                // known cutscenes the game starts from code (the intro) aren't reached by a character or area
                var events = EventScript.PlausibleEvents(rom, m.MapId)
                    .Concat(KnownNames.Keys.Where(k => k.Map == m.MapId).Select(k => k.Ev)).Distinct().OrderBy(e => e);
                foreach (int ev in events)
                {
                    EventScript? s;
                    try { s = EventScript.Load(rom, m.MapId, ev); } catch { continue; }
                    if (s == null) continue;
                    int stage = CutsceneStage.StageCommandCount(s);
                    bool known = KnownNames.ContainsKey((m.MapId, ev));
                    // the intro starts with a jump into its code: count what it jumps to
                    if (stage == 0 && known && s.Ops.Count > 0 && s.Ops[0].Targets.Count > 0)
                    {
                        var code = EventScript.LoadAt(rom, m.MapId, ev, s.Ops[0].Targets[0].Absolute, s.JumpBase);
                        stage = CutsceneStage.StageCommandCount(code);
                    }
                    if (stage == 0) continue;
                    string hint = "";
                    try { hint = EventHints.Summarize(rom, s); } catch { }
                    int sprite = map?.Npcs.FirstOrDefault(n => n.Index == ev && !n.IsUnused)?.Sprite ?? -1;
                    found.Add(new CutsceneEntry
                    {
                        MapId = m.MapId, Event = ev, MapLabel = m.Label, Title = KnownNames.GetValueOrDefault((m.MapId, ev), ""),
                        Hint = hint, StageCommands = stage, Texts = s.Ops.Count(o => o.IsText), Picture = Picture(rom, sprite),
                    });
                }
            }
            return found;
        });
        _all = list.OrderByDescending(c => c.Title.Length > 0).ThenBy(c => c.MapId).ThenBy(c => c.Event).ToList();
        MapFilters.Clear(); _mapFilterIds.Clear();
        MapFilters.Add("All maps"); _mapFilterIds.Add(-1);
        foreach (var id in _all.Select(c => c.MapId).Distinct().OrderBy(i => i)) { MapFilters.Add(labels[id]); _mapFilterIds.Add(id); }
        MapFilterIndex = 0;
        IsBusy = false;
        ApplyFilter();
        Selected = Cutscenes.FirstOrDefault();
    }

    private static ImageSource? Picture(RomBuffer rom, int sprite)
    {
        if (sprite < 0 || NpcSprites.Render(rom, sprite) is not var (px, w, h)) return null;
        var bmp = BitmapSource.Create(w, h, 96, 96, PixelFormats.Bgra32, null, px, w * 4);
        bmp.Freeze();
        return bmp;
    }

    partial void OnSearchChanged(string value) => ApplyFilter();
    partial void OnMapFilterIndexChanged(int value) => ApplyFilter();
    partial void OnOnlyBigChanged(bool value) => ApplyFilter();

    private void ApplyFilter()
    {
        var keep = Selected;
        Cutscenes.Clear();
        int map = MapFilterIndex >= 0 && MapFilterIndex < _mapFilterIds.Count ? _mapFilterIds[MapFilterIndex] : -1;
        string q = Search.Trim();
        foreach (var c in _all)
        {
            if (map >= 0 && c.MapId != map) continue;
            if (OnlyBig && c.StageCommands < 6) continue;
            if (q.Length > 0 && !c.Label.Contains(q, StringComparison.OrdinalIgnoreCase) && !c.Hint.Contains(q, StringComparison.OrdinalIgnoreCase)) continue;
            Cutscenes.Add(c);
        }
        if (keep != null && Cutscenes.Contains(keep)) { _suppressOpen = true; Selected = keep; _suppressOpen = false; }
        if (!IsBusy) Status = $"{Cutscenes.Count} of {_all.Count} cutscenes shown.";
    }

    private bool _suppressOpen;

    partial void OnSelectedChanged(CutsceneEntry? value)
    {
        if (value == null || _rom == null || _suppressOpen) return;
        // whole event: the code its jumps lead to is part of it (the intro is one jump to its real code)
        var s = EventScript.LoadWhole(_rom, value.MapId, value.Event);
        if (s == null) return;
        OpenRequested?.Invoke(s, value.Label);
    }

    // ── IEventHost ──
    public bool ConfirmExpand(string what) =>
        MessageBox.Show(
            $"{what}\n\nTo store it, Lufia Forge needs to expand the ROM from 1 MB to 2 MB and use the new space. " +
            "Expanded ROMs work in BizHawk, Snes9x and bsnes. Note: savestates made before the expansion will undo " +
            "anything stored in the new space, so continue from an in-game save instead. Continue?",
            "Expand ROM?", MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes;

    public void EventSaved(string what) => _mainVm?.NotifyRomModified(what);

    public void ConfirmWritten(string heading, string details) =>
        InfoDialog.Show("Written to ROM", "✔ " + heading,
            details + "\n\nThe change is in the ROM loaded in Lufia Forge, but not on disk yet. " +
            "Use File > Save ROM (or the button below) to write a new dated copy; the ROM you opened is never overwritten.",
            path: _rom?.FilePath, pathLabel: "ROM being edited (the copy is saved next to it)",
            primaryText: _mainVm != null ? "💾 Save ROM now" : null, primary: () => _mainVm?.SaveNow());
}
