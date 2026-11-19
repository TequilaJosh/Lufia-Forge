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

// one battle background in the list
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

// a row that picks a background for one map, or for every map of a kind of area
public partial class BattleBackgroundChoice : ObservableObject
{
    public string Label { get; init; } = "";
    public string Detail { get; init; } = "";
    public List<int> Maps { get; init; } = new();
    public bool IsArea { get; init; }
    internal BattleBackgroundEditorViewModel Owner = null!;

    // 0 = none (the darkened map), n = background n, -1 (areas only) = its maps don't all match
    public int Choice
    {
        get => _blank ? -1 : Owner.ChoiceOf(Maps);
        set
        {
            // a choice list being refilled sets -1, that's not a real pick
            if (value < 0 || value == Choice) return;
            Owner.SetChoice(Maps, value);
        }
    }

    // shows the choice again. a list that took new names can keep its index with nothing selected,
    // going through -1 makes it select again
    public void Refresh()
    {
        _blank = true;
        OnPropertyChanged(nameof(Choice));
        _blank = false;
        OnPropertyChanged(nameof(Choice));
    }

    private bool _blank;
}

// battle backgrounds (see BattleBackgrounds): pictures behind the fights instead of the darkened field map,
// one per kind of area (world map, caves, towers...) or per map
public partial class BattleBackgroundEditorViewModel : GameDataEditorBase
{
    public string OffsetText => "";

    public ObservableCollection<BattleBackgroundItem> Backgrounds { get; } = new();
    // what the choice lists offer. it's a new list each time, changing one in place leaves the lists blank
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

    // the kinds of area by tileset (what the game's maps of each kind are built from)
    private static readonly (string Name, int[] Tilesets)[] AreaKinds =
    {
        ("Towns", new[] { 0, 1, 2 }), ("Castles", new[] { 3 }), ("Palaces", new[] { 4 }), ("Shrines", new[] { 5 }),
        ("Caves", new[] { 6 }), ("Towers", new[] { 7 }), ("Island", new[] { 8 }), ("Ruins", new[] { 9 }), ("Fortress of Doom", new[] { 10 }),
    };

    // which Lufia II background each area gets when they're added (only areas that have none)
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
        foreach (var background in list) AddItem(new BattleBackgroundItem { Name = background.Name, Picture = background.Picture });
        Areas.Clear();
        MapRows.Clear();
        var catalog = MapCatalog.ScanNamed(rom);
        var world = catalog.Where(m => m.MapId == LufiaMap.WorldMap).Select(m => m.MapId).ToList();
        if (world.Count > 0) Areas.Add(new BattleBackgroundChoice { Owner = this, IsArea = true, Label = "World map", Detail = "map 01", Maps = world });
        var placed = new HashSet<int>(world);
        // the Ancient Cave's two floor maps. they're not in the map list, they get built while you play
        if (Core.Cave.AncientCave.Read(rom) is { } cave)
        {
            var floors = new List<int> { cave.Map1, cave.Map2 };
            Areas.Add(new BattleBackgroundChoice { Owner = this, IsArea = true, Label = "Ancient Cave", Detail = $"maps {cave.Map1:X2}, {cave.Map2:X2}", Maps = floors });
            placed.UnionWith(floors);
        }
        foreach (var (name, tilesets) in AreaKinds)
        {
            var areaMaps = catalog.Where(m => m.MapId != LufiaMap.WorldMap && tilesets.Contains(m.Tileset)).ToList();
            if (areaMaps.Count == 0) continue;
            placed.UnionWith(areaMaps.Select(m => m.MapId));
            Areas.Add(new BattleBackgroundChoice { Owner = this, IsArea = true, Label = name, Detail = $"{areaMaps.Count} maps", Maps = areaMaps.Select(m => m.MapId).ToList() });
        }
        foreach (var group in catalog.Where(m => !placed.Contains(m.MapId)).GroupBy(m => m.Tileset).OrderBy(g => g.Key))
            Areas.Add(new BattleBackgroundChoice { Owner = this, IsArea = true, Label = $"Tileset {group.Key}", Detail = $"{group.Count()} maps", Maps = group.Select(m => m.MapId).ToList() });
        foreach (var map in catalog.OrderBy(m => m.MapId))
            MapRows.Add(new BattleBackgroundChoice { Owner = this, Label = map.Label, Maps = new List<int> { map.MapId } });
        foreach (var area in Areas.Where(a => a.Label == "Ancient Cave"))
            foreach (int mapId in area.Maps)
                MapRows.Add(new BattleBackgroundChoice { Owner = this, Label = $"{mapId:X2}  Ancient Cave floors", Maps = new List<int> { mapId } });
        RefreshChoices();
        SelectedBackground = Backgrounds.FirstOrDefault();
        UpdatePreview();
        Dirty = false;
        Status = Backgrounds.Count == 0
            ? "No battle backgrounds yet: fights show the darkened map. Add a picture, or the Lufia 2 backgrounds."
            : $"{Backgrounds.Count} battle backgrounds.";
    }

    // choices

    internal int ChoiceOf(List<int> maps)
    {
        if (maps.Count == 0) return 0;
        int first = _maps[maps[0]];
        return maps.All(m => _maps[m] == first) ? first : -1;
    }

    internal void SetChoice(List<int> maps, int value)
    {
        foreach (var mapId in maps) _maps[mapId] = (byte)value;
        Dirty = true;
        foreach (var row in Areas) row.Refresh();
        foreach (var row in MapRows) row.Refresh();
    }

    private void RefreshChoices()
    {
        ChoiceNames = new List<string> { "None (the darkened map)" }.Concat(Backgrounds.Select(b => b.Name)).ToList();
        RefreshRows();
        // the lists drop their selection a little later, once they've taken the new names, so show it again then
        System.Windows.Application.Current?.Dispatcher.BeginInvoke(RefreshRows, System.Windows.Threading.DispatcherPriority.Background);
    }

    private void RefreshRows()
    {
        foreach (var row in Areas) row.Refresh();
        foreach (var row in MapRows) row.Refresh();
    }

    // preview

    partial void OnSelectedBackgroundChanged(BattleBackgroundItem? value) => UpdatePreview();
    partial void OnShowGridChanged(bool value) => UpdatePreview();

    private void UpdatePreview()
    {
        if (SelectedBackground is not { } background) { Preview = null; PreviewInfo = ""; return; }
        var pixels = BattleBackgrounds.Render(background.Picture);
        if (ShowGrid)
        {
            // where the fight puts things: the monsters' row and the party's windows (same as on the world map)
            void Box(int x0, int y0, int x1, int y1, uint color)
            {
                for (int x = x0; x <= x1; x++) { pixels[y0 * 256 + x] = color; pixels[y1 * 256 + x] = color; }
                for (int y = y0; y <= y1; y++) { pixels[y * 256 + x0] = color; pixels[y * 256 + x1] = color; }
            }
            Box(48, 40, 207, 112, 0xFFF0C040);     // monsters
            Box(16, 136, 119, 168, 0xFFFFFFFF);    // party windows
            Box(136, 136, 239, 168, 0xFFFFFFFF);
            Box(16, 176, 119, 208, 0xFFFFFFFF);
            Box(136, 176, 239, 208, 0xFFFFFFFF);
        }
        Preview = ToImage(pixels, 256, 224, 2);
        int users = Enumerable.Range(0, 256).Count(m => _maps[m] == Backgrounds.IndexOf(background) + 1);
        PreviewInfo = $"{background.SizeInfo}. Used by {users} map{(users == 1 ? "" : "s")}." + (background.Notes.Length > 0 ? " " + background.Notes : "");
    }

    internal static ImageSource ToImage(uint[] px, int w, int h, int scale)
    {
        var scaled = new uint[w * scale * h * scale];
        for (int y = 0; y < h * scale; y++) for (int x = 0; x < w * scale; x++) scaled[y * w * scale + x] = px[(y / scale) * w + x / scale];
        var bmp = BitmapSource.Create(w * scale, h * scale, 96, 96, PixelFormats.Bgra32, null, scaled, w * scale * 4);
        bmp.Freeze();
        return bmp;
    }

    // pictures

    private static (uint[] Px, int W, int H) LoadPicture(Stream stream)
    {
        var frame = BitmapDecoder.Create(stream, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad).Frames[0];
        var bmp = new FormatConvertedBitmap(frame, PixelFormats.Bgra32, null, 0);
        var pixels = new uint[bmp.PixelWidth * bmp.PixelHeight];
        bmp.CopyPixels(pixels, bmp.PixelWidth * 4, 0);
        return (pixels, bmp.PixelWidth, bmp.PixelHeight);
    }

    private static string? PickPicture()
    {
        var dlg = new OpenFileDialog { Title = "Choose a picture for a battle background", Filter = "Pictures|*.png;*.bmp;*.gif;*.jpg;*.jpeg|All files|*.*" };
        return dlg.ShowDialog() == true ? dlg.FileName : null;
    }

    private BattleBackgroundItem? MakeItem(string name, Stream stream)
    {
        try
        {
            var (pixels, width, height) = LoadPicture(stream);
            var (picture, notes) = BattleBackgrounds.Fit(pixels, width, height);
            return new BattleBackgroundItem { Name = Clip(name), Picture = picture, Notes = notes.Count == 0 ? "" : "Made to fit: " + string.Join("; ", notes) + "." };
        }
        catch (Exception ex)
        {
            Status = $"That picture can't be used: {ex.Message}";
            return null;
        }
    }

    private static string Clip(string name)
    {
        var clipped = new string(name.Where(c => c >= ' ' && c < (char)0x7F).ToArray()).Trim();
        return clipped.Length > BattleBackgrounds.NameLength ? clipped[..BattleBackgrounds.NameLength] : clipped.Length == 0 ? "Background" : clipped;
    }

    [RelayCommand]
    private void AddPicture()
    {
        if (Ctx == null) return;
        if (Backgrounds.Count >= BattleBackgrounds.MaxBackgrounds) { Status = $"There can be at most {BattleBackgrounds.MaxBackgrounds} backgrounds."; return; }
        var path = PickPicture();
        if (path != null) AddPictureFrom(path);
    }

    // adds a background made from a picture file (Add picture, minus the file dialog)
    public void AddPictureFrom(string path)
    {
        if (Ctx == null) return;
        using var stream = File.OpenRead(path);
        var item = MakeItem(Path.GetFileNameWithoutExtension(path), stream);
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
        if (Ctx == null || SelectedBackground is not { } background) return;
        var path = PickPicture();
        if (path == null) return;
        using var stream = File.OpenRead(path);
        var item = MakeItem(background.Name, stream);
        if (item == null) return;
        background.Picture = item.Picture;
        background.Notes = item.Notes;
        Dirty = true;
        UpdatePreview();
        Status = $"New picture for \"{background.Name}\"." + (item.Notes.Length > 0 ? " " + item.Notes : "");
    }

    [RelayCommand]
    private void RemoveBackground()
    {
        if (Ctx == null || SelectedBackground is not { } background) return;
        int number = Backgrounds.IndexOf(background) + 1;
        for (int m = 0; m < 256; m++)
        {
            if (_maps[m] == number) _maps[m] = 0;
            else if (_maps[m] > number) _maps[m]--;
        }
        Backgrounds.Remove(background);
        RefreshChoices();
        SelectedBackground = Backgrounds.FirstOrDefault();
        Dirty = true;
        Status = $"Removed \"{background.Name}\"; its maps show the darkened map again.";
    }

    [RelayCommand]
    private void ExportPicture()
    {
        if (SelectedBackground is not { } background) return;
        var dlg = new SaveFileDialog { Title = "Save the background as a picture", Filter = "PNG picture|*.png", FileName = background.Name + ".png" };
        if (dlg.ShowDialog() != true) return;
        var pixels = BattleBackgrounds.Render(background.Picture);
        var bmp = BitmapSource.Create(256, 224, 96, 96, PixelFormats.Bgra32, null, pixels, 256 * 4);
        using var file = File.Create(dlg.FileName);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bmp));
        encoder.Save(file);
        Status = $"Saved {Path.GetFileName(dlg.FileName)} (256 x 224, the colours the game shows). Edit it and use Replace picture to bring it back.";
    }

    // adds Lufia II's 24 battle backgrounds and gives them to the areas that don't have one yet.
    // uses the pictures built into Lufia Forge (Backgrounds\L2 *.png) if it has them, else reads them from the
    // user's own Lufia II ROM
    [RelayCommand]
    private void AddLufia2()
    {
        if (Ctx == null) return;
        var assembly = typeof(BattleBackgroundEditorViewModel).Assembly;
        var builtIn = Lufia2Backgrounds.Names.Select(n => assembly.GetManifestResourceStream($"Backgrounds.{Clip("L2 " + n)}.png")).ToList();
        if (builtIn.All(stream => stream != null))
        {
            var pictures = new List<uint[]>();
            foreach (var stream in builtIn) using (stream) pictures.Add(LoadPicture(stream!).Px);
            AddLufia2Pictures(pictures);
            return;
        }
        foreach (var stream in builtIn) stream?.Dispose();
        var dlg = new OpenFileDialog { Title = "Choose your Lufia II: Rise of the Sinistrals (USA) ROM", Filter = "SNES ROMs|*.sfc;*.smc|All files|*.*" };
        if (dlg.ShowDialog() != true) return;
        AddLufia2From(dlg.FileName);
    }

    // add the Lufia II backgrounds without the file dialog
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

    // puts the 24 Lufia II backgrounds (256 x 224 ARGB, in Lufia2Backgrounds.Names order) in the list,
    // and gives them to the areas that have none
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
                var (picture, notes) = BattleBackgrounds.Fit(pictures[k], BattleBackgrounds.Width, BattleBackgrounds.Height);
                AddItem(new BattleBackgroundItem { Name = name, Picture = picture, Notes = notes.Count == 0 ? "From Lufia II." : "From Lufia II, made to fit: " + string.Join("; ", notes) + "." });
                added.Add(name);
            }
            catch (Exception ex) { Status = $"Lufia II background {k} can't be read: {ex.Message}"; }
        }
        RefreshChoices();
        var given = new List<string>();
        foreach (var area in Areas)
        {
            if (area.Choice != 0 || !Lufia2For.TryGetValue(area.Label, out var pick)) continue;
            int number = Backgrounds.ToList().FindIndex(b => b.Name == "L2 " + pick) + 1;
            if (number <= 0) continue;
            area.Choice = number;
            given.Add($"{area.Label}: {pick}");
        }
        SelectedBackground = Backgrounds.FirstOrDefault(b => added.Contains(b.Name)) ?? SelectedBackground;
        Dirty = true;
        Status = (added.Count > 0 ? $"Added {added.Count} Lufia II backgrounds." : "The Lufia II backgrounds are already in.")
               + (given.Count > 0 ? $" Given to the areas that had none ({string.Join(", ", given)})." : "");
    }

    partial void OnDirtyChanged(bool value) { if (value) Edited = true; }

    // adds a background to the list and watches its name, since the choice lists show it
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

    // writing

    [RelayCommand]
    private void Apply()
    {
        if (Ctx == null) return;
        var rom = Ctx.Rom;
        bool expand = Backgrounds.Count > 0 && rom.Length < MapWriter.ExpandedSize;
        if (expand && !AutoWriting &&
            MessageBox.Show("Battle backgrounds need the expanded ROM (2 MB). Expand it?", "Expand ROM?", MessageBoxButton.YesNo) != MessageBoxResult.Yes)
        { Refuse("Not applied: the ROM wasn't expanded."); return; }
        var list = Backgrounds.Select(b => new BattleBackgrounds.Background(Clip(b.Name), b.Picture)).ToList();
        var maps = _maps.ToArray();
        if (!Commit("Battle backgrounds", () =>
            {
                if (expand) rom.Expand(MapWriter.ExpandedSize);
                BattleBackgrounds.Apply(rom, list, maps);
            }))
            return;   // still pending, with the error
        Dirty = false;
    }
}
