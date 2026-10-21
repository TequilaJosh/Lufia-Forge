using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LufiaForge.Core;
using LufiaForge.Core.Battle;
using LufiaForge.Core.Maps;
using Microsoft.Win32;
using System.Collections.ObjectModel;
using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace LufiaForge.Modules.GameData;

/// <summary>One battle background in the list.</summary>
public partial class BattleBackgroundItem : ObservableObject
{
    [ObservableProperty] private string _name = "";
    [ObservableProperty] private BattleBackgrounds.Converted _picture = null!;
    [ObservableProperty] private ImageSource? _thumbImage;
    [ObservableProperty] private string _sizeInfo = "";
    public string Notes { get; set; } = "";

    partial void OnPictureChanged(BattleBackgrounds.Converted value)
    {
        ThumbImage = BattleBackgroundEditorViewModel.ToImage(BattleBackgrounds.Render(value), BattleBackgrounds.Width, BattleBackgrounds.Height, 1);
        SizeInfo = $"{value.TileCount - 1} of {BattleBackgrounds.MaxTiles - 1} squares, {value.PaletteCount} of {BattleBackgrounds.Palettes} palettes";
    }
}

/// <summary>A row that picks a background: one map, or every map of a kind of area.</summary>
public partial class BattleBackgroundChoice : ObservableObject
{
    public string Label { get; init; } = "";
    public string Detail { get; init; } = "";
    public List<int> Maps { get; init; } = new();
    public bool IsArea { get; init; }
    internal BattleBackgroundEditorViewModel Owner = null!;

    /// <summary>0 = none (the darkened map), n = background n; -1 (an area only) = its maps differ.</summary>
    public int Choice
    {
        get => _blank ? -1 : Owner.ChoiceOf(Maps);
        set
        {
            // (a choice list being refilled sets -1: not a choice)
            if (value < 0 || value == Choice) return;
            Owner.SetChoice(Maps, value);
        }
    }

    /// <summary>Shows the choice again. (A list that took new names can keep its index with nothing selected; going
    /// through -1 makes it select again.)</summary>
    public void Refresh()
    {
        _blank = true;
        OnPropertyChanged(nameof(Choice));
        _blank = false;
        OnPropertyChanged(nameof(Choice));
    }

    private bool _blank;
}

/// <summary>
/// Battle backgrounds (see <see cref="BattleBackgrounds"/>): pictures shown behind the fights instead of the darkened
/// field map, one per kind of area (world map, caves, towers...) or per map.
/// </summary>
public partial class BattleBackgroundEditorViewModel : GameDataEditorBase
{
    public string OffsetText => "";

    public ObservableCollection<BattleBackgroundItem> Backgrounds { get; } = new();
    /// <summary>What the choice lists offer (a new list each time: changing one in place leaves the lists blank).</summary>
    [ObservableProperty] private List<string> _choiceNames = new();
    public ObservableCollection<BattleBackgroundChoice> Areas { get; } = new();
    public ObservableCollection<BattleBackgroundChoice> MapRows { get; } = new();

    [ObservableProperty] private BattleBackgroundItem? _selectedBackground;
    [ObservableProperty] private ImageSource? _preview;
    [ObservableProperty] private string _previewInfo = "";
    [ObservableProperty] private bool _showGrid;
    [ObservableProperty] private bool _dirty;

    private byte[] _maps = new byte[256];

    public override bool HasPendingEdits => Dirty && Ctx != null;

    /// <summary>The kinds of area, by tileset (what the game's maps of each are built from).</summary>
    private static readonly (string Name, int[] Tilesets)[] AreaKinds =
    {
        ("Towns", new[] { 0, 1, 2 }), ("Castles", new[] { 3 }), ("Palaces", new[] { 4 }), ("Shrines", new[] { 5 }),
        ("Caves", new[] { 6 }), ("Towers", new[] { 7 }), ("Island", new[] { 8 }), ("Ruins", new[] { 9 }), ("Fortress of Doom", new[] { 10 }),
    };

    /// <summary>Which Lufia II background goes to which area when they are added (areas that have none only).</summary>
    private static readonly Dictionary<string, string> Lufia2For = new()
    {
        ["World map"] = "Grassland", ["Ancient Cave"] = "Dark Cave", ["Towns"] = "Grassland", ["Island"] = "Mountain Path",
        ["Caves"] = "Red Cave", ["Ruins"] = "Mossy Rock", ["Castles"] = "Palace Hall", ["Palaces"] = "Temple",
        ["Shrines"] = "Shrine", ["Towers"] = "Tower", ["Fortress of Doom"] = "Fortress",
    };

    protected override void OnRomLoaded() => LoadSelected();

    protected override void LoadRecord()
    {
        if (Ctx == null) return;
        var rom = Ctx.Rom;
        var (list, maps) = BattleBackgrounds.Read(rom);
        _maps = maps;
        Backgrounds.Clear();
        foreach (var b in list) AddItem(new BattleBackgroundItem { Name = b.Name, Picture = b.Picture });
        Areas.Clear();
        MapRows.Clear();
        var catalog = MapCatalog.ScanNamed(rom);
        var world = catalog.Where(m => m.MapId == LufiaMap.WorldMap).Select(m => m.MapId).ToList();
        if (world.Count > 0) Areas.Add(new BattleBackgroundChoice { Owner = this, IsArea = true, Label = "World map", Detail = "map 01", Maps = world });
        var placed = new HashSet<int>(world);
        // the Ancient Cave's two floor maps (not in the map list: they are built while playing)
        if (Core.Cave.AncientCave.Read(rom) is { } cave)
        {
            var floors = new List<int> { cave.Map1, cave.Map2 };
            Areas.Add(new BattleBackgroundChoice { Owner = this, IsArea = true, Label = "Ancient Cave", Detail = $"maps {cave.Map1:X2}, {cave.Map2:X2}", Maps = floors });
            placed.UnionWith(floors);
        }
        foreach (var (name, sets) in AreaKinds)
        {
            var ms = catalog.Where(m => m.MapId != LufiaMap.WorldMap && sets.Contains(m.Tileset)).ToList();
            if (ms.Count == 0) continue;
            placed.UnionWith(ms.Select(m => m.MapId));
            Areas.Add(new BattleBackgroundChoice { Owner = this, IsArea = true, Label = name, Detail = $"{ms.Count} maps", Maps = ms.Select(m => m.MapId).ToList() });
        }
        foreach (var g in catalog.Where(m => !placed.Contains(m.MapId)).GroupBy(m => m.Tileset).OrderBy(g => g.Key))
            Areas.Add(new BattleBackgroundChoice { Owner = this, IsArea = true, Label = $"Tileset {g.Key}", Detail = $"{g.Count()} maps", Maps = g.Select(m => m.MapId).ToList() });
        foreach (var m in catalog.OrderBy(m => m.MapId))
            MapRows.Add(new BattleBackgroundChoice { Owner = this, Label = m.Label, Maps = new List<int> { m.MapId } });
        foreach (var a in Areas.Where(a => a.Label == "Ancient Cave"))
            foreach (int m in a.Maps)
                MapRows.Add(new BattleBackgroundChoice { Owner = this, Label = $"{m:X2}  Ancient Cave floors", Maps = new List<int> { m } });
        RefreshChoices();
        SelectedBackground = Backgrounds.FirstOrDefault();
        UpdatePreview();
        Dirty = false;
        Status = Backgrounds.Count == 0
            ? "No battle backgrounds yet: fights show the darkened map. Add a picture, or the Lufia 2 backgrounds."
            : $"{Backgrounds.Count} battle backgrounds.";
    }

    // ── choices ──────────────────────────────────────────────────────────────

    internal int ChoiceOf(List<int> maps)
    {
        if (maps.Count == 0) return 0;
        int first = _maps[maps[0]];
        return maps.All(m => _maps[m] == first) ? first : -1;
    }

    internal void SetChoice(List<int> maps, int value)
    {
        foreach (var m in maps) _maps[m] = (byte)value;
        Dirty = true;
        foreach (var r in Areas) r.Refresh();
        foreach (var r in MapRows) r.Refresh();
    }

    private void RefreshChoices()
    {
        ChoiceNames = new List<string> { "None (the darkened map)" }.Concat(Backgrounds.Select(b => b.Name)).ToList();
        RefreshRows();
        // (the lists drop their selection a little later, once they have taken the new names: show it again then)
        System.Windows.Application.Current?.Dispatcher.BeginInvoke(RefreshRows, System.Windows.Threading.DispatcherPriority.Background);
    }

    private void RefreshRows()
    {
        foreach (var r in Areas) r.Refresh();
        foreach (var r in MapRows) r.Refresh();
    }

    // ── preview ──────────────────────────────────────────────────────────────

    partial void OnSelectedBackgroundChanged(BattleBackgroundItem? value) => UpdatePreview();
    partial void OnShowGridChanged(bool value) => UpdatePreview();

    private void UpdatePreview()
    {
        if (SelectedBackground is not { } b) { Preview = null; PreviewInfo = ""; return; }
        var px = BattleBackgrounds.Render(b.Picture);
        if (ShowGrid)
        {
            // where the fight puts things: the monsters' row and the party's windows (as on the world map)
            void Box(int x0, int y0, int x1, int y1, uint c)
            {
                for (int x = x0; x <= x1; x++) { px[y0 * 256 + x] = c; px[y1 * 256 + x] = c; }
                for (int y = y0; y <= y1; y++) { px[y * 256 + x0] = c; px[y * 256 + x1] = c; }
            }
            Box(48, 40, 207, 112, 0xFFF0C040);     // monsters
            Box(16, 136, 119, 168, 0xFFFFFFFF);    // party windows
            Box(136, 136, 239, 168, 0xFFFFFFFF);
            Box(16, 176, 119, 208, 0xFFFFFFFF);
            Box(136, 176, 239, 208, 0xFFFFFFFF);
        }
        Preview = ToImage(px, 256, 224, 2);
        int users = Enumerable.Range(0, 256).Count(m => _maps[m] == Backgrounds.IndexOf(b) + 1);
        PreviewInfo = $"{b.SizeInfo}. Used by {users} map{(users == 1 ? "" : "s")}." + (b.Notes.Length > 0 ? " " + b.Notes : "");
    }

    internal static ImageSource ToImage(uint[] px, int w, int h, int scale)
    {
        var o = new uint[w * scale * h * scale];
        for (int y = 0; y < h * scale; y++) for (int x = 0; x < w * scale; x++) o[y * w * scale + x] = px[(y / scale) * w + x / scale];
        var bmp = BitmapSource.Create(w * scale, h * scale, 96, 96, PixelFormats.Bgra32, null, o, w * scale * 4);
        bmp.Freeze();
        return bmp;
    }

    // ── pictures ─────────────────────────────────────────────────────────────

    private static (uint[] Px, int W, int H) LoadPicture(Stream s)
    {
        var frame = BitmapDecoder.Create(s, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad).Frames[0];
        var bmp = new FormatConvertedBitmap(frame, PixelFormats.Bgra32, null, 0);
        var px = new uint[bmp.PixelWidth * bmp.PixelHeight];
        bmp.CopyPixels(px, bmp.PixelWidth * 4, 0);
        return (px, bmp.PixelWidth, bmp.PixelHeight);
    }

    private static string? PickPicture()
    {
        var dlg = new OpenFileDialog { Title = "Choose a picture for a battle background", Filter = "Pictures|*.png;*.bmp;*.gif;*.jpg;*.jpeg|All files|*.*" };
        return dlg.ShowDialog() == true ? dlg.FileName : null;
    }

    private BattleBackgroundItem? MakeItem(string name, Stream s)
    {
        try
        {
            var (px, w, h) = LoadPicture(s);
            var (pic, notes) = BattleBackgrounds.Fit(px, w, h);
            return new BattleBackgroundItem { Name = Clip(name), Picture = pic, Notes = notes.Count == 0 ? "" : "Made to fit: " + string.Join("; ", notes) + "." };
        }
        catch (Exception ex)
        {
            Status = $"That picture can't be used: {ex.Message}";
            return null;
        }
    }

    private static string Clip(string name)
    {
        var s = new string(name.Where(c => c >= ' ' && c < (char)0x7F).ToArray()).Trim();
        return s.Length > BattleBackgrounds.NameLength ? s[..BattleBackgrounds.NameLength] : s.Length == 0 ? "Background" : s;
    }

    [RelayCommand]
    private void AddPicture()
    {
        if (Ctx == null) return;
        if (Backgrounds.Count >= BattleBackgrounds.MaxBackgrounds) { Status = $"There can be at most {BattleBackgrounds.MaxBackgrounds} backgrounds."; return; }
        var path = PickPicture();
        if (path != null) AddPictureFrom(path);
    }

    /// <summary>Adds a background made from a picture file (Add picture without the file dialog).</summary>
    public void AddPictureFrom(string path)
    {
        if (Ctx == null) return;
        using var s = File.OpenRead(path);
        var item = MakeItem(Path.GetFileNameWithoutExtension(path), s);
        if (item == null) return;
        AddItem(item);
        RefreshChoices();
        SelectedBackground = item;
        Dirty = true;
        Status = $"Added \"{item.Name}\". Pick it for areas or maps on the right." + (item.Notes.Length > 0 ? " " + item.Notes : "");
    }

    [RelayCommand]
    private void ReplacePicture()
    {
        if (Ctx == null || SelectedBackground is not { } b) return;
        var path = PickPicture();
        if (path == null) return;
        using var s = File.OpenRead(path);
        var item = MakeItem(b.Name, s);
        if (item == null) return;
        b.Picture = item.Picture;
        b.Notes = item.Notes;
        Dirty = true;
        UpdatePreview();
        Status = $"New picture for \"{b.Name}\"." + (item.Notes.Length > 0 ? " " + item.Notes : "");
    }

    [RelayCommand]
    private void RemoveBackground()
    {
        if (Ctx == null || SelectedBackground is not { } b) return;
        int n = Backgrounds.IndexOf(b) + 1;
        for (int m = 0; m < 256; m++)
        {
            if (_maps[m] == n) _maps[m] = 0;
            else if (_maps[m] > n) _maps[m]--;
        }
        Backgrounds.Remove(b);
        RefreshChoices();
        SelectedBackground = Backgrounds.FirstOrDefault();
        Dirty = true;
        Status = $"Removed \"{b.Name}\"; its maps show the darkened map again.";
    }

    [RelayCommand]
    private void ExportPicture()
    {
        if (SelectedBackground is not { } b) return;
        var dlg = new SaveFileDialog { Title = "Save the background as a picture", Filter = "PNG picture|*.png", FileName = b.Name + ".png" };
        if (dlg.ShowDialog() != true) return;
        var px = BattleBackgrounds.Render(b.Picture);
        var bmp = BitmapSource.Create(256, 224, 96, 96, PixelFormats.Bgra32, null, px, 256 * 4);
        using var f = File.Create(dlg.FileName);
        var enc = new PngBitmapEncoder();
        enc.Frames.Add(BitmapFrame.Create(bmp));
        enc.Save(f);
        Status = $"Saved {Path.GetFileName(dlg.FileName)} (256 x 224, the colours the game shows). Edit it and use Replace picture to bring it back.";
    }

    /// <summary>Adds the 24 battle backgrounds of Lufia II and gives them to the areas that have no background yet:
    /// the pictures built into Lufia Forge (Backgrounds\L2 *.png) when it has them, else read from the user's own
    /// Lufia II ROM.</summary>
    [RelayCommand]
    private void AddLufia2()
    {
        if (Ctx == null) return;
        var asm = typeof(BattleBackgroundEditorViewModel).Assembly;
        var builtIn = Lufia2Backgrounds.Names.Select(n => asm.GetManifestResourceStream($"Backgrounds.{Clip("L2 " + n)}.png")).ToList();
        if (builtIn.All(st => st != null))
        {
            var pictures = new List<uint[]>();
            foreach (var st in builtIn) using (st) pictures.Add(LoadPicture(st!).Px);
            AddLufia2Pictures(pictures);
            return;
        }
        foreach (var st in builtIn) st?.Dispose();
        var dlg = new OpenFileDialog { Title = "Choose your Lufia II: Rise of the Sinistrals (USA) ROM", Filter = "SNES ROMs|*.sfc;*.smc|All files|*.*" };
        if (dlg.ShowDialog() != true) return;
        AddLufia2From(dlg.FileName);
    }

    /// <summary>Add Lufia II backgrounds without the file dialog.</summary>
    public void AddLufia2From(string path)
    {
        if (Ctx == null) return;
        byte[]? rom;
        try { rom = Lufia2Backgrounds.Rom(File.ReadAllBytes(path)); }
        catch (Exception ex) { Status = $"That file can't be read: {ex.Message}"; return; }
        if (rom == null) { Status = "That isn't a Lufia II: Rise of the Sinistrals (USA) ROM."; return; }
        var pictures = new List<uint[]>();
        try { for (int k = 0; k < Lufia2Backgrounds.Count; k++) pictures.Add(Lufia2Backgrounds.Picture(rom, k)); }
        catch (Exception ex) { Status = $"The Lufia II backgrounds can't be read: {ex.Message}"; return; }
        AddLufia2Pictures(pictures);
    }

    /// <summary>The 24 Lufia II backgrounds (256 x 224 ARGB, in <see cref="Lufia2Backgrounds.Names"/> order) into the
    /// list, and to the areas that have none.</summary>
    private void AddLufia2Pictures(List<uint[]> pictures)
    {
        var added = new List<string>();
        for (int k = 0; k < pictures.Count; k++)
        {
            string name = Clip("L2 " + Lufia2Backgrounds.Names[k]);
            if (Backgrounds.Any(b => b.Name == name)) continue;
            if (Backgrounds.Count >= BattleBackgrounds.MaxBackgrounds) { Status = $"There can be at most {BattleBackgrounds.MaxBackgrounds} backgrounds."; break; }
            try
            {
                var (pic, notes) = BattleBackgrounds.Fit(pictures[k], BattleBackgrounds.Width, BattleBackgrounds.Height);
                AddItem(new BattleBackgroundItem { Name = name, Picture = pic, Notes = notes.Count == 0 ? "From Lufia II." : "From Lufia II, made to fit: " + string.Join("; ", notes) + "." });
                added.Add(name);
            }
            catch (Exception ex) { Status = $"Lufia II background {k} can't be read: {ex.Message}"; }
        }
        RefreshChoices();
        var given = new List<string>();
        foreach (var area in Areas)
        {
            if (area.Choice != 0 || !Lufia2For.TryGetValue(area.Label, out var pick)) continue;
            int n = Backgrounds.ToList().FindIndex(b => b.Name == "L2 " + pick) + 1;
            if (n <= 0) continue;
            area.Choice = n;
            given.Add($"{area.Label}: {pick}");
        }
        SelectedBackground = Backgrounds.FirstOrDefault(b => added.Contains(b.Name)) ?? SelectedBackground;
        Dirty = true;
        Status = (added.Count > 0 ? $"Added {added.Count} Lufia II backgrounds." : "The Lufia II backgrounds are already in.")
               + (given.Count > 0 ? $" Given to the areas that had none ({string.Join(", ", given)})." : "");
    }

    partial void OnDirtyChanged(bool value) { if (value) Edited = true; }

    /// <summary>Adds a background to the list, watching its name (the choice lists show it).</summary>
    private void AddItem(BattleBackgroundItem item)
    {
        Backgrounds.Add(item);
        item.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName != nameof(BattleBackgroundItem.Name)) return;
            RefreshChoices();
            Dirty = true;
        };
    }

    // ── writing ─────────────────────────────────────────────────────────────

    [RelayCommand]
    private void Apply()
    {
        if (Ctx == null) return;
        var rom = Ctx.Rom;
        bool expand = Backgrounds.Count > 0 && rom.Length < MapWriter.ExpandedSize;
        if (expand && !AutoWriting &&
            MessageBox.Show("Battle backgrounds need the expanded ROM (2 MB). Expand it?", "Expand ROM?", MessageBoxButton.YesNo) != MessageBoxResult.Yes)
        { Status = "Not applied."; return; }
        var list = Backgrounds.Select(b => new BattleBackgrounds.Background(Clip(b.Name), b.Picture)).ToList();
        var maps = _maps.ToArray();
        Commit("Battle backgrounds", () =>
        {
            if (expand) rom.Expand(MapWriter.ExpandedSize);
            BattleBackgrounds.Apply(rom, list, maps);
        });
        Dirty = false;
    }
}
