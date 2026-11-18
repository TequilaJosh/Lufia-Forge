using System.Collections.ObjectModel;
using System.Windows.Media;
using Color = System.Windows.Media.Color;
using Brush = System.Windows.Media.Brush;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LufiaForge.Core;
using LufiaForge.Core.Lufia2;
using LufiaForge.Core.Lufia2.Snes;
using LufiaForge.ViewModels;
using Microsoft.Win32;

namespace LufiaForge.Modules.Lufia2;

// battle animations. each one plays like in the game (we run the game's own animation code inside Lufia Forge).
// you can paint its sprite graphics or import them from PNG, change its colours and how long its steps last,
// make a copy to change on its own (gets a new animation number), and give it to a spell or an IP attack (L2SpellAnims)
public partial class L2AnimationsPage : ObservableObject
{
    private RomBuffer? _rom;
    private MainViewModel? _main;
    private readonly List<int> _ids = new();
    private L2SpellAnims.Script? _script;

    public ObservableCollection<string> Names { get; } = new();
    [ObservableProperty] private int _selectedIndex = -1;
    [ObservableProperty] private string _info = "";
    [ObservableProperty] private string _status = "";
    [ObservableProperty] private string _listing = "";
    [ObservableProperty] private bool _isNew;
    [ObservableProperty] private string _newName = "";

    // preview
    public List<string> Backgrounds { get; } = L2Gfx.BgNames.Select((n, i) => $"{i:X2}  {n}").ToList();
    [ObservableProperty] private int _background;
    [ObservableProperty] private List<string> _monsterNames = new();
    private List<(int Sprite, int Palette)> _monsters = new();
    [ObservableProperty] private int _monster;
    public List<string> Heroes { get; } = L2SpellAnims.Characters.ToList();
    [ObservableProperty] private int _hero;
    [ObservableProperty] private WriteableBitmap? _frame;
    [ObservableProperty] private string _playInfo = "";
    [ObservableProperty] private bool _loop = true;
    private List<uint[]> _frames = new();
    private int _frameIndex;
    private readonly DispatcherTimer _timer = new(DispatcherPriority.Render) { Interval = TimeSpan.FromSeconds(1 / 60.0) };
    private int _playToken;

    // graphics
    [ObservableProperty] private List<string> _graphicsChoices = new();
    private List<int> _graphicsIndexes = new();
    [ObservableProperty] private int _graphicsChoice = -1;
    [ObservableProperty] private List<string> _paletteChoices = new();
    private List<int> _paletteIndexes = new();
    [ObservableProperty] private int _paletteChoice = -1;
    [ObservableProperty] private BitmapSource? _sheetImage;
    [ObservableProperty] private string _sheetInfo = "";
    [ObservableProperty] private int _zoom = 3;
    public ObservableCollection<L2AnimSwatch> Swatches { get; } = new();
    [ObservableProperty] private int _colour = 1;
    [ObservableProperty] private string _colourText = "";
    private byte[]? _tiles;
    private int _sheetW, _sheetH;
    private byte[]? _sheetPx;
    private bool _painted;

    // timing
    public ObservableCollection<L2TimingItem> Timings { get; } = new();
    [ObservableProperty] private string _timingInfo = "";

    // giving it
    [ObservableProperty] private List<string> _spellNames = new();
    [ObservableProperty] private int _spellChoice = -1;
    [ObservableProperty] private List<string> _ipNames = new();
    [ObservableProperty] private int _ipChoice = -1;
    [ObservableProperty] private List<string> _itemNames = new();
    [ObservableProperty] private int _itemChoice = -1;
    private List<int> _itemIds = new();

    public L2AnimationsPage()
    {
        _timer.Tick += (_, _) => Tick();
    }

    public void SetRom(RomBuffer rom, MainViewModel main)
    {
        _rom = rom; _main = main;
        var monsters = ReadMonsters();
        int goblin = monsters.FindIndex(m => m.Name.Trim() == "Goblin");
        Monster = goblin >= 0 ? goblin + 1 : Math.Min(1, MonsterNames.Count - 1);
        RefreshList(-1);
    }

    // the monsters the preview can show (one per sprite + palette)
    private List<L2Battle.Monster> ReadMonsters()
    {
        var monsters = L2Battle.Monsters(_rom!).Where(m => m.Sprite > 0).GroupBy(m => ((int)m.Sprite, (int)m.Palette)).Select(g => g.First()).ToList();
        _monsters = new List<(int, int)> { (-1, 0) }.Concat(monsters.Select(m => ((int)m.Sprite, (int)m.Palette))).ToList();
        MonsterNames = new List<string> { "(none)" }.Concat(monsters.Select(m => $"{m.Name.Trim()}")).ToList();
        return monsters;
    }

    // the ROM's version when we last read it (or wrote it ourselves)
    private int _readVersion = -1;

    // Shown(): if the ROM changed since we read it (spells, IP attacks, items or monsters changed on their pages, or Undo)
    // we read the lists and the animation again, keeping the picks.
    // _visible = the page is on screen. the preview only plays then (it emulates the game and draws at 60 Hz)
    private bool _visible;

    // another page got picked, so the preview stops
    public void Hidden() { _visible = false; _timer.Stop(); }

    public void Shown()
    {
        bool wasVisible = _visible;
        _visible = true;
        if (_rom == null || _rom.Version == _readVersion) { if (!wasVisible && !_timer.IsEnabled) Play(); return; }
        var (sprite, palette) = Monster >= 0 && Monster < _monsters.Count ? _monsters[Monster] : (-1, 0);
        int spell = SpellChoice, ipAttack = IpChoice, item = ItemChoice;
        ReadMonsters();
        int monsterIndex = _monsters.IndexOf((sprite, palette));
        Monster = monsterIndex >= 0 ? monsterIndex : Math.Min(1, MonsterNames.Count - 1);
        RefreshList(-1);
        SpellChoice = spell; IpChoice = ipAttack; ItemChoice = item;
    }

    private void RefreshList(int select)
    {
        if (_rom == null) return;
        _readVersion = _rom.Version;
        int keepId = select >= 0 ? select : SelectedIndex >= 0 && SelectedIndex < _ids.Count ? _ids[SelectedIndex] : 0x12;
        _ids.Clear(); Names.Clear();
        foreach (var (animId, name) in L2Magic.Animations(_rom)) { _ids.Add(animId); Names.Add($"{animId:X2}  {name}"); }
        SpellNames = L2Battle.Spells(_rom).Select(s => $"{s.Index:X2}  {s.Name.Trim()}  (now {L2Magic.SpellAnimation(_rom, s.Index):X2})").ToList();
        IpNames = L2Battle.IpAttacks(_rom).Select(a => $"{a.Index + 1:X2}  {a.Name}  (now {a.Animation:X2})").ToList();
        // weapons first (they swing with theirs), then the other items
        var items = L2Battle.Items(_rom).Where(it => it.Name.Trim().Length > 0).OrderBy(it => it.EquipType == 1 ? 0 : 1).ThenBy(it => it.Index).ToList();
        _itemIds = items.Select(it => it.Index).ToList();
        ItemNames = items.Select(it =>
        {
            int anim = ItemAnimation(it);
            return $"{it.Index:X3}  {it.Name.Trim()}{(it.EquipType == 1 ? " (weapon)" : "")}  (now {(anim < 0 ? "default" : anim.ToString("X2"))})";
        }).ToList();
        int listIndex = _ids.IndexOf(keepId);
        SelectedIndex = -1;
        SelectedIndex = listIndex >= 0 ? listIndex : 0;
    }

    private int Id => SelectedIndex >= 0 && SelectedIndex < _ids.Count ? _ids[SelectedIndex] : -1;

    partial void OnSelectedIndexChanged(int value) => Load();

    private void Load()
    {
        _timer.Stop(); _frames.Clear(); Frame = null; PlayInfo = "";
        if (_rom == null || Id < 0) return;
        _script = L2SpellAnims.Parse(_rom, Id);
        IsNew = L2SpellAnims.IsNew(_rom, Id);
        NewName = IsNew ? L2SpellAnims.Name(_rom, Id) : "";
        var graphics = L2SpellAnims.Graphics(_script);
        var palettes = L2SpellAnims.Palettes(_script).Where(p => p.Step.Op != 0x2C).ToList();
        Info = $"Animation {Id:X2}" + (IsNew ? $" (new, kept by Lufia Forge in bank ${_script.Bank:X2})" : "") + $": {_script.Steps.Count} steps at ${_script.Bank:X2}:{_script.Start:X4}. " +
               (_script.Problem != null ? _script.Problem + " " : "") +
               (IsNew ? "Its graphics, colours and steps are its own copies." : "The game's animations share graphics, colours and steps: change a copy (Make a copy) unless you want every animation using them changed.");
        Listing = L2SpellAnims.Listing(_script);
        _graphicsIndexes = graphics.Where(g => g.What == "sprites").Select(g => g.Index).Concat(graphics.Where(g => g.What != "sprites").Select(g => g.Index)).Distinct().ToList();
        GraphicsChoices = _graphicsIndexes.Select(gfxIndex =>
        {
            var g = graphics.First(x => x.Index == gfxIndex);
            string file = gfxIndex < L2SpellAnims.FirstNewGfx ? $"file {L2SpellAnims.GameFile(_rom, gfxIndex):X3}" : "Lufia Forge's";
            return $"{(g.What == "sprites" ? "Sprites" : "Other graphics")} {gfxIndex:X2} ({file}, {g.What})";
        }).ToList();
        _paletteIndexes = palettes.OrderBy(p => p.Step.Op == 0xDD ? 1 : 0).Select(p => p.Index).Distinct().ToList();
        PaletteChoices = _paletteIndexes.Select(palIndex => $"Colours {palIndex:X} ({palettes.First(p => p.Index == palIndex).What})").ToList();
        GraphicsChoice = _graphicsIndexes.Count > 0 ? 0 : -1;
        PaletteChoice = _paletteIndexes.Count > 0 ? 0 : -1;
        LoadSheet();
        Timings.Clear();
        foreach (var step in L2SpellAnims.Timings(_script)) Timings.Add(new L2TimingItem(step, $"Step length at {_script.Bank:X2}:{step.Address:X4}" + (step.Op == 0x02 ? "" : " (1-20)"), L2SpellAnims.TimingFrames(step)));
        TimingInfo = Timings.Count == 0
            ? "Its steps keep the length they start with (one frame), so it has no lengths to change here."
            : "How many frames each wait lasts in the scripts of this animation (one line per place that sets it). Longer steps play it slower. Lengths marked 1-20 take 1, 2, 3, 4, 5, 6, 8, 10 or 20.";
        if (_visible) Play();   // not while another page is shown (opening a project and every Undo reload this page too)
    }

    // preview

    [RelayCommand]
    private void Play()
    {
        if (_rom == null || Id < 0) return;
        _timer.Stop();
        int token = ++_playToken, id = Id, background = Background, hero = Math.Max(0, Hero);
        var (sprite, palette) = Monster >= 0 && Monster < _monsters.Count ? _monsters[Monster] : (-1, 0);
        var romCopy = new RomBuffer(_rom.ToArray(), "preview");
        PlayInfo = "Playing the game's code...";
        Task.Run(() => L2AnimPreview.Play(romCopy, id, new L2AnimPreview.Options(background, sprite, palette, Hero: hero))).ContinueWith(task =>
        {
            if (token != _playToken) return;
            if (task.Exception != null) { PlayInfo = "Couldn't play it: " + task.Exception.InnerException?.Message; return; }
            var (frames, problem) = task.Result;
            _frames = frames; _frameIndex = 0;
            PlayInfo = $"{frames.Count} frames ({frames.Count / 60.0:0.0} s)" + (problem != null ? ". " + problem : "") + ". No sound; the heroes and the battle's windows aren't drawn.";
            if (frames.Count > 0) { Frame = new WriteableBitmap(SnesRenderer.Width, SnesRenderer.Height, 96, 96, PixelFormats.Bgra32, null); _timer.Start(); }
        }, TaskScheduler.FromCurrentSynchronizationContext());
    }

    [RelayCommand] private void Stop() => _timer.Stop();

    private void Tick()
    {
        if (Frame == null || _frames.Count == 0) { _timer.Stop(); return; }
        if (_frameIndex >= _frames.Count)
        {
            if (!Loop) { _timer.Stop(); return; }
            _frameIndex = 0;
        }
        Frame.WritePixels(new System.Windows.Int32Rect(0, 0, SnesRenderer.Width, SnesRenderer.Height), _frames[_frameIndex], SnesRenderer.Width * 4, 0);
        _frameIndex++;
    }

    partial void OnBackgroundChanged(int value) { if (_rom != null) Play(); }
    partial void OnMonsterChanged(int value) { if (_rom != null && Id >= 0) Play(); }
    partial void OnHeroChanged(int value) { if (_rom != null && Id >= 0) Play(); }

    // graphics and colours

    partial void OnGraphicsChoiceChanged(int value) => LoadSheet();
    partial void OnPaletteChoiceChanged(int value) => LoadSheet();

    private int GfxIndex => GraphicsChoice >= 0 && GraphicsChoice < _graphicsIndexes.Count ? _graphicsIndexes[GraphicsChoice] : -1;
    private int PalIndex => PaletteChoice >= 0 && PaletteChoice < _paletteIndexes.Count ? _paletteIndexes[PaletteChoice] : -1;

    private int[] Colours() => _rom != null && PalIndex >= 0 ? L2SpellAnims.ReadPalette(_rom, PalIndex) : Enumerable.Range(0, 16).Select(i => i * 2 | i * 2 << 5 | i * 2 << 10).ToArray();

    private void LoadSheet()
    {
        SheetImage = null; _tiles = null; _sheetPx = null;
        if (_rom == null || GfxIndex < 0) { SheetInfo = "No graphics."; Swatch(); return; }
        _tiles = L2SpellAnims.ReadGraphics(_rom, GfxIndex);
        if (_tiles == null) { SheetInfo = "No graphics."; Swatch(); return; }
        (_sheetW, _sheetH, _sheetPx) = L2SpellAnims.Sheet(_tiles);
        var users = L2SpellAnims.GraphicsUsers(_rom, GfxIndex, Id);
        SheetInfo = $"{_tiles.Length / 32} tiles of 8 x 8, 16 colours (colour 0 is see-through). The sprite graphics go to the sprites' first 256 tiles; " +
                    "the other graphics are background maps and pictures some animations draw with. " +
                    (users.Count > 0 ? $"Also used by animations {string.Join(", ", users.Select(u => u.ToString("X2")))}: painting here changes them too." : "Only this animation uses them.");
        Swatch();
        DrawSheet();
    }

    private void Swatch()
    {
        Swatches.Clear();
        var colours = Colours();
        for (int i = 0; i < 16; i++) Swatches.Add(new L2AnimSwatch(i, new SolidColorBrush(ToColor(colours[i])), i == Colour));
        ColourText = $"{colours[Math.Clamp(Colour, 0, 15)]:X4}";
    }

    private static Color ToColor(int bgr555) => Color.FromRgb((byte)((bgr555 & 31) * 255 / 31), (byte)((bgr555 >> 5 & 31) * 255 / 31), (byte)((bgr555 >> 10 & 31) * 255 / 31));

    private void DrawSheet()
    {
        if (_sheetPx == null) return;
        var colours = Colours();
        var px = new uint[_sheetPx.Length];
        for (int i = 0; i < px.Length; i++)
        {
            int colourIndex = _sheetPx[i];
            px[i] = colourIndex == 0 ? 0xFF202020u : 0xFF000000u | (uint)(ToColor(colours[colourIndex]).R << 16 | ToColor(colours[colourIndex]).G << 8 | ToColor(colours[colourIndex]).B);
        }
        var bmp = BitmapSource.Create(_sheetW, _sheetH, 96, 96, PixelFormats.Bgra32, null, px, _sheetW * 4);
        bmp.Freeze();
        SheetImage = bmp;
    }

    public void PickColour(int i) { Colour = i; Swatch(); }

    public void PaintPress(int x, int y, bool right)
    {
        if (_sheetPx == null || x < 0 || y < 0 || x >= _sheetW || y >= _sheetH) return;
        if (right) { Colour = _sheetPx[y * _sheetW + x]; Swatch(); return; }
        PaintDrag(x, y);
    }

    public void PaintDrag(int x, int y)
    {
        if (_sheetPx == null || x < 0 || y < 0 || x >= _sheetW || y >= _sheetH) return;
        if (_sheetPx[y * _sheetW + x] == Colour) return;
        _sheetPx[y * _sheetW + x] = (byte)Colour;
        _painted = true;
        DrawSheet();
    }

    public void PaintRelease()
    {
        if (!_painted || _rom == null || _main == null || _tiles == null || _sheetPx == null) return;
        _painted = false;
        try
        {
            L2SpellAnims.WriteGraphics(_rom, GfxIndex, L2SpellAnims.SheetTiles(_sheetW, _sheetH, _sheetPx, _tiles.Length));
            _main.NotifyRomModified($"Lufia II: animation {Id:X2} graphics painted");
            _readVersion = _rom.Version;
            _tiles = L2SpellAnims.ReadGraphics(_rom, GfxIndex);
            Status = "Painted. Play shows it.";
        }
        catch (Exception e) { Status = "Not written: " + e.Message; }
    }

    [RelayCommand]
    private void SetColour()
    {
        if (_rom == null || _main == null || PalIndex < 0) return;
        string text = ColourText.Trim().TrimStart('#');
        int colour;
        if (text.Length == 6 && int.TryParse(text, System.Globalization.NumberStyles.HexNumber, null, out int rgb))
            colour = L2Gfx.Bgr555(0xFF000000u | (uint)rgb);
        else if (!int.TryParse(text, System.Globalization.NumberStyles.HexNumber, null, out colour)) { Status = "A colour is 4 hex digits (BGR555, as the game keeps it) or 6 (#RRGGBB)."; return; }
        L2SpellAnims.SetColour(_rom, PalIndex, Colour, colour);
        _main.NotifyRomModified($"Lufia II: animation colours {PalIndex:X}, colour {Colour}");
        _readVersion = _rom.Version;
        var users = L2SpellAnims.PaletteUsers(_rom, PalIndex, Id);
        Status = "Colour set." + (users.Count > 0 ? $" Animations {string.Join(", ", users.Select(u => u.ToString("X2")))} use these colours too." : "");
        Swatch(); DrawSheet();
    }

    [RelayCommand]
    private void ExportSheet()
    {
        if (_sheetPx == null) return;
        var dlg = new SaveFileDialog { Title = "Export animation graphics", Filter = "PNG picture (*.png)|*.png", FileName = $"animation {Id:X2} graphics {GfxIndex:X2}.png" };
        if (dlg.ShowDialog() != true) return;
        ExportSheetTo(dlg.FileName);
    }

    public void ExportSheetTo(string path)
    {
        var colours = Colours();
        IndexedPng.Save(path, _sheetW, _sheetH, _sheetPx!, colours.Select(v =>0xFF000000u | (uint)(ToColor(v).R << 16 | ToColor(v).G << 8 | ToColor(v).B)).ToArray(), firstTransparent: true);
        Status = $"Saved {path}: {_sheetW} x {_sheetH}, 16 colours, colour 0 see-through.";
    }

    [RelayCommand]
    private void ImportSheet()
    {
        if (_sheetPx == null) return;
        var dlg = new OpenFileDialog { Title = "Import animation graphics", Filter = "Pictures (*.png;*.gif;*.bmp)|*.png;*.gif;*.bmp" };
        if (dlg.ShowDialog() == true) ImportSheetFrom(dlg.FileName);
    }

    // loads a picture as the graphics. a palette PNG keeps its colour numbers, any other picture gets matched to the
    // colours shown (see-through pixels become colour 0). has to be the sheet's size (128 wide)
    public void ImportSheetFrom(string path)
    {
        if (_rom == null || _main == null || _sheetPx == null || _tiles == null) return;
        try
        {
            byte[] indices;
            int width, height;
            if (IndexedPng.TryLoad(path) is { } png && png.Palette.Length <= 16) { (width, height, indices) = (png.Width, png.Height, png.Indices); }
            else
            {
                var src = new FormatConvertedBitmap(new BitmapImage(new Uri(path)), PixelFormats.Bgra32, null, 0);
                width = src.PixelWidth; height = src.PixelHeight;
                var argb = new uint[width * height];
                src.CopyPixels(argb, width * 4, 0);
                var colours = Colours().Select(ToColor).ToArray();
                indices = new byte[width * height];
                for (int i = 0; i < argb.Length; i++)
                {
                    uint pixel = argb[i];
                    if (pixel >> 24 < 128) continue;
                    int r = (int)(pixel >> 16 & 255), g = (int)(pixel >> 8 & 255), b = (int)(pixel & 255), best = 1, bestDistance = int.MaxValue;
                    for (int k = 1; k < 16; k++) { int distance = (colours[k].R - r) * (colours[k].R - r) + (colours[k].G - g) * (colours[k].G - g) + (colours[k].B - b) * (colours[k].B - b); if (distance < bestDistance) { bestDistance = distance; best = k; } }
                    indices[i] = (byte)best;
                }
            }
            if (width != _sheetW || height != _sheetH) { Status = $"The picture is {width} x {height}; these graphics are {_sheetW} x {_sheetH} (export them to get the layout)."; return; }
            _sheetPx = indices.Select(v => (byte)(v & 15)).ToArray();
            L2SpellAnims.WriteGraphics(_rom, GfxIndex, L2SpellAnims.SheetTiles(_sheetW, _sheetH, _sheetPx, _tiles.Length));
            _main.NotifyRomModified($"Lufia II: animation {Id:X2} graphics imported");
            _readVersion = _rom.Version;
            LoadSheet();
            Status = $"Imported {System.IO.Path.GetFileName(path)}. Play shows it.";
        }
        catch (Exception e) { Status = "Not imported: " + e.Message; }
    }

    // timing

    [RelayCommand]
    private void WriteTiming()
    {
        if (_rom == null || _main == null || _script == null) return;
        foreach (var timing in Timings) L2SpellAnims.SetTiming(_rom, timing.Step, timing.Frames);
        _main.NotifyRomModified($"Lufia II: animation {Id:X2} step lengths");
        var shared = L2SpellAnims.SharedWith(_rom, _script, Timings.Select(t => t.Step.File));
        Status = "Step lengths written." + (shared.Count > 0 ? $" Animations {string.Join(", ", shared.Select(u => u.ToString("X2")))} run these steps too." : "");
        Load();
    }

    [RelayCommand] private void Slower() { foreach (var t in Timings) t.Frames = Math.Min(255, t.Frames * 2); }
    [RelayCommand] private void Faster() { foreach (var t in Timings) t.Frames = Math.Max(1, t.Frames / 2); }

    // new animations

    [RelayCommand]
    private void MakeCopy()
    {
        if (_rom == null || _main == null || Id < 0) return;
        if (!L2SpellAnims.CanInstall(_rom, out var why)) { Status = why; return; }
        try
        {
            int newId = L2SpellAnims.Copy(_rom, Id, string.IsNullOrWhiteSpace(NewName) || IsNew ? $"Copy of {Id:X2}" : NewName);
            _main.NotifyRomModified($"Lufia II: new animation {newId:X2} (a copy of {Id:X2})");
            RefreshList(newId);
            Status = $"Animation {newId:X2} made: a copy of the one before with its own graphics, colours and steps. Give it to a spell, an IP attack or a weapon below.";
        }
        catch (Exception e) { Status = "Not made: " + e.Message; }
    }

    [RelayCommand]
    private void Rename()
    {
        if (_rom == null || _main == null || !IsNew) return;
        L2SpellAnims.SetName(_rom, Id, NewName);
        _main.NotifyRomModified($"Lufia II: animation {Id:X2} named");
        RefreshList(Id);
    }

    [RelayCommand]
    private void GiveToSpell()
    {
        if (_rom == null || _main == null || SpellChoice < 0 || Id < 0) return;
        var spells = L2Battle.Spells(_rom);
        if (SpellChoice >= spells.Count) return;
        try
        {
            L2Magic.SetSpellAnimation(_rom, spells[SpellChoice].Index, Id);
            _main.NotifyRomModified($"Lufia II: spell {spells[SpellChoice].Name.Trim()} shows animation {Id:X2}");
            Status = $"{spells[SpellChoice].Name.Trim()} shows animation {Id:X2} now.";
            int keep = SpellChoice; RefreshList(Id); SpellChoice = keep;
        }
        catch (Exception e) { Status = e.Message; }
    }

    [RelayCommand]
    private void GiveToIp()
    {
        if (_rom == null || _main == null || IpChoice < 0 || Id < 0) return;
        var ips = L2Battle.IpAttacks(_rom);
        if (IpChoice >= ips.Count) return;
        var attack = ips[IpChoice];
        attack.Animation = (byte)Id;
        L2Battle.WriteIp(_rom, attack);
        _main.NotifyRomModified($"Lufia II: IP attack {attack.Name} shows animation {Id:X2}");
        Status = $"{attack.Name} shows animation {Id:X2} now.";
        int keep = IpChoice; RefreshList(Id); IpChoice = keep;
    }

    // the animation an item gives: its word 0x2000 = animation + 1 (in its record or added by Lufia Forge), -1 = default
    private int ItemAnimation(L2Battle.Item item)
    {
        int word = item.Values.TryGetValue(0x2000, out var value) ? value : L2AiScripts.ItemAddedWords(_rom!, item.Index).GetValueOrDefault(0x2000);
        return word > 0 ? word - 1 : -1;
    }

    // gives the item this animation: the record's word 0x2000 if it has one, otherwise a word Lufia Forge adds.
    // a weapon swings with it ($81:A8B7), an item used in battle shows it ($81:AA3D)
    [RelayCommand]
    private void GiveToItem()
    {
        if (_rom == null || _main == null || ItemChoice < 0 || ItemChoice >= _itemIds.Count || Id < 0) return;
        var it = L2Battle.ReadItem(_rom, _itemIds[ItemChoice]);
        try
        {
            if (it.Values.ContainsKey(0x2000)) { it.Values[0x2000] = (ushort)(Id + 1); L2Battle.WriteItem(_rom, it); }
            else L2AiScripts.SetItemAddedWord(_rom, it.Index, 0x2000, Id + 1);
            _main.NotifyRomModified($"Lufia II: {it.Name.Trim()} shows animation {Id:X2}");
            Status = $"{it.Name.Trim()} shows animation {Id:X2} now" + (it.EquipType == 1 ? " when it hits." : " when it's used in battle.");
            int keep = ItemChoice; RefreshList(Id); ItemChoice = keep;
        }
        catch (Exception e) { Status = e.Message; }
    }
}

public sealed record L2AnimSwatch(int Index, Brush Brush, bool Selected);

public sealed partial class L2TimingItem : ObservableObject
{
    public L2SpellAnims.Step Step { get; }
    public string Label { get; }
    [ObservableProperty] private int _frames;
    public L2TimingItem(L2SpellAnims.Step step, string label, int frames) { Step = step; Label = label; _frames = frames; }
}
