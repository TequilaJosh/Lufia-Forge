using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LufiaForge.Core;
using LufiaForge.Core.Lufia2;
using LufiaForge.ViewModels;

namespace LufiaForge.Modules.Lufia2;

/// <summary>A flag (one bit of a byte) as a check box.</summary>
public partial class L2Flag : ObservableObject
{
    public string Label { get; init; } = "";
    public int Bit { get; init; }
    [ObservableProperty] private bool _isChecked;
    public Action? Changed { get; set; }
    partial void OnIsCheckedChanged(bool value) => Changed?.Invoke();
}

/// <summary>An optional word of an item (a stat bonus, the IP attack, ...): only the words the item has.</summary>
public partial class L2Word : ObservableObject
{
    public string Label { get; init; } = "";
    public int Bit { get; init; }
    [ObservableProperty] private int _value;
    public Action? Changed { get; set; }
    partial void OnValueChanged(int value) => Changed?.Invoke();
}

public sealed record L2Option(int Value, string Label);

/// <summary>Base of the Lufia II pages: a list of records; the picked one's fields are shown, and an edit is written to
/// the ROM at once (Edit > Undo takes it back).</summary>
public abstract partial class L2Page : ObservableObject
{
    protected RomBuffer? Rom;
    protected MainViewModel? Main;
    protected bool Loading;

    public ObservableCollection<string> Names { get; } = new();
    [ObservableProperty] private int _selectedIndex = -1;
    [ObservableProperty] private bool _isEditable;
    /// <summary>The picked record's description (items, spells, IP attacks), new lines as line breaks.</summary>
    [ObservableProperty] private string _description = "";
    [ObservableProperty] private string _descriptionNote = "";

    /// <summary>The description entry of record <paramref name="index"/> (<see cref="L2Descriptions"/>), or -1.</summary>
    protected virtual int DescriptionEntry(int index) => -1;
    public bool HasDescription => DescriptionEntry(0) >= 0;

    public void SetRom(RomBuffer rom, MainViewModel main)
    {
        Rom = rom; Main = main;
        Names.Clear();
        foreach (var n in ReadNames()) Names.Add(n);
        IsEditable = Names.Count > 0;
        SelectedIndex = -1;
        SelectedIndex = Names.Count > 0 ? 0 : -1;
    }

    partial void OnSelectedIndexChanged(int value)
    {
        if (Rom == null || value < 0) return;
        Loading = true;
        try
        {
            Load(value);
            int e = DescriptionEntry(value);
            if (e >= 0) { Description = L2Descriptions.Read(Rom, e).Replace("\n", Environment.NewLine); DescriptionNote = SpaceNote(); }
        }
        finally { Loading = false; }
    }

    private string SpaceNote()
    {
        var (used, room) = L2Descriptions.Space(Rom!);
        return $"Descriptions use {used} of {room} bytes; the game shows it with the X button (checked in a shop).";
    }

    private void WriteDescription()
    {
        if (Loading || Rom == null || Main == null || SelectedIndex < 0) return;
        int e = DescriptionEntry(SelectedIndex);
        if (e < 0) return;
        string? error = L2Descriptions.Write(Rom, e, Description.Replace("\r\n", "\n"));
        if (error != null) { DescriptionNote = "Not written: " + error; return; }
        Main.NotifyRomModified($"Lufia II: description of {Names[SelectedIndex].Trim()}");
        DescriptionNote = SpaceNote();
    }

    protected abstract IEnumerable<string> ReadNames();
    protected abstract void Load(int index);
    /// <summary>Writes the shown record; returns what changed (for Undo), or null.</summary>
    protected abstract string? Write();

    /// <summary>Call from a field's change: writes the record and refreshes its line in the list.</summary>
    protected void Edited()
    {
        if (Loading || Rom == null || Main == null || SelectedIndex < 0) return;
        string? what = Write();
        if (what == null) return;
        int i = SelectedIndex;
        Main.NotifyRomModified("Lufia II: " + what);
        var fresh = ReadNames().ElementAtOrDefault(i);
        if (fresh != null && fresh != Names[i]) { Loading = true; Names[i] = fresh; SelectedIndex = i; Loading = false; }
    }

    protected override void OnPropertyChanged(PropertyChangedEventArgs e)
    {
        base.OnPropertyChanged(e);
        if (e.PropertyName is nameof(SelectedIndex) or nameof(IsEditable) or nameof(Names) or nameof(DescriptionNote) || e.PropertyName!.StartsWith("Picture")) return;
        if (e.PropertyName == nameof(Description)) { WriteDescription(); return; }
        if (IsField(e.PropertyName)) Edited();
    }

    protected virtual bool IsField(string property) => true;

    protected static BitmapSource? Picture((int Width, int Height, uint[] Pixels)? pic)
    {
        if (pic is not var (w, h, px)) return null;
        var bmp = BitmapSource.Create(w, h, 96, 96, PixelFormats.Bgra32, null, px.Select(c => (int)c).ToArray(), w * 4);
        bmp.Freeze();
        return bmp;
    }

    /// <summary>Opens the battle script editor for (kind, index); after a change the page shows the record again.</summary>
    protected void EditScript(L2AiScripts.Kind kind, int index, string who)
    {
        if (Rom == null || Main == null) return;
        var w = new L2ScriptWindow(Rom, kind, index, who) { Owner = System.Windows.Application.Current?.MainWindow };
        if (w.ShowDialog() != true) return;
        Main.NotifyRomModified("Lufia II: " + w.Change);
        int i = SelectedIndex;
        Loading = true;
        try { Load(i); } finally { Loading = false; }
    }

    /// <summary>A record's two battle scripts as text (what runs now: Lufia Forge's edited one or the game's).</summary>
    protected static string ScriptsText(RomBuffer rom, L2AiScripts.Kind turn, L2AiScripts.Kind hit, int index)
    {
        var names = L2AiScripts.NameLists.From(rom);
        string Part(L2AiScripts.Kind k, string title, string none)
        {
            try
            {
                var (steps, edited) = L2AiScripts.Load(rom, k, index);
                return title + (edited ? "  (edited in Lufia Forge)" : "") + "\n" + (steps.Count == 0 ? none : L2AiScripts.Listing(steps, names));
            }
            catch (Exception e) { return title + "\n" + e.Message; }
        }
        return Part(turn, "ITS TURN", "No script: it just attacks.") + "\n\n" + Part(hit, "WHEN HIT", "No script.");
    }

    protected static List<L2Flag> Flags(string[] labels, int value, Action changed) =>
        labels.Select((l, i) => new L2Flag { Label = l, Bit = 1 << i, IsChecked = (value & 1 << i) != 0, Changed = changed }).ToList();

    protected static int FlagValue(IEnumerable<L2Flag> flags) => flags.Where(f => f.IsChecked).Sum(f => f.Bit);
}

// ------------------------------------------------------------------------------------------- monsters

public partial class L2MonstersPage : L2Page
{
    private L2Battle.Monster? _m;
    public List<L2Option> ItemOptions { get; private set; } = new();

    [ObservableProperty] private string _name = "";
    [ObservableProperty] private int _level, _hp, _mp, _atp, _dfp, _agl, _int, _gut, _mgr, _exp, _gold, _sprite, _palette;
    [ObservableProperty] private int _dropItem, _dropChance;
    [ObservableProperty] private bool _hasDrop;
    [ObservableProperty] private string _scriptText = "";
    [ObservableProperty] private string _aiText = "";
    [ObservableProperty] private BitmapSource? _pictureImage;

    protected override IEnumerable<string> ReadNames()
    {
        ItemOptions = L2Battle.Items(Rom!).Select(i => new L2Option(i.Index, $"{i.Index:X3} {i.Name}")).ToList();
        OnPropertyChanged(nameof(ItemOptions));
        return L2Battle.Monsters(Rom!).Select(m => $"{m.Index:X2}  {m.Name}");
    }

    protected override void Load(int index)
    {
        _m = L2Battle.ReadMonster(Rom!, index);
        Name = _m.Name; Level = _m.Level; Hp = _m.Hp; Mp = _m.Mp; Atp = _m.Atp; Dfp = _m.Dfp; Agl = _m.Agl; Int = _m.Int; Gut = _m.Gut; Mgr = _m.Mgr;
        Exp = _m.Exp; Gold = _m.Gold; Sprite = _m.Sprite; Palette = _m.Palette;
        HasDrop = _m.DropAt > 0; DropItem = Math.Max(0, _m.DropItem); DropChance = _m.DropChance;
        ScriptText = HasDrop ? "" : "It drops nothing (a drop can't be added yet: the record would grow).";
        AiText = ScriptsText(Rom!, L2AiScripts.Kind.MonsterAttack, L2AiScripts.Kind.MonsterDefense, index);
        PictureImage = Picture(L2Graphics.MonsterSprite(Rom!, Sprite, Palette));
    }

    protected override bool IsField(string p) => p is not (nameof(ScriptText) or nameof(AiText) or nameof(HasDrop) or nameof(ItemOptions));

    [RelayCommand] private void EditTurnScript() { if (_m != null) EditScript(L2AiScripts.Kind.MonsterAttack, _m.Index, _m.Name); }
    [RelayCommand] private void EditHitScript() { if (_m != null) EditScript(L2AiScripts.Kind.MonsterDefense, _m.Index, _m.Name); }

    protected override string? Write()
    {
        if (_m == null) return null;
        _m.Name = Name; _m.Level = (byte)Level; _m.Hp = (ushort)Hp; _m.Mp = (ushort)Mp; _m.Atp = (ushort)Atp; _m.Dfp = (ushort)Dfp;
        _m.Agl = (byte)Agl; _m.Int = (byte)Int; _m.Gut = (byte)Gut; _m.Mgr = (byte)Mgr; _m.Exp = (ushort)Exp; _m.Gold = (ushort)Gold;
        _m.Sprite = (byte)Math.Clamp(Sprite, 1, L2Graphics.MonsterSpriteCount); _m.Palette = (byte)Palette;
        if (HasDrop) { _m.DropItem = DropItem; _m.DropChance = DropChance; }
        L2Battle.WriteMonster(Rom!, _m);
        PictureImage = Picture(L2Graphics.MonsterSprite(Rom!, _m.Sprite, _m.Palette));
        return _m.Name;
    }
}

// ------------------------------------------------------------------------------------------- items

public partial class L2ItemsPage : L2Page
{
    private L2Battle.Item? _it;
    public List<L2Option> EquipTypeOptions { get; } =
        new[] { new L2Option(0, "(not equipment)") }.Concat(L2Battle.EquipTypes.Select((t, i) => new L2Option(1 << i, t))).ToList();
    public List<L2Option> IpOptions { get; private set; } = new();

    [ObservableProperty] private string _name = "";
    [ObservableProperty] private int _price, _equipType, _icon;
    [ObservableProperty] private List<L2Flag> _useFlags = new();
    [ObservableProperty] private List<L2Flag> _equipBy = new();
    [ObservableProperty] private List<L2Word> _words = new();
    [ObservableProperty] private string _wordsText = "";
    [ObservableProperty] private string _effectText = "";

    protected override IEnumerable<string> ReadNames()
    {
        IpOptions = L2Battle.IpAttacks(Rom!).Select(a => new L2Option(a.Index, $"{a.Index:X2} {a.Name}")).ToList();
        OnPropertyChanged(nameof(IpOptions));
        return L2Battle.Items(Rom!).Select(i => $"{i.Index:X3}  {i.Name}");
    }

    protected override void Load(int index)
    {
        _it = L2Battle.ReadItem(Rom!, index);
        Name = _it.Name; Price = _it.Price; EquipType = _it.EquipType; Icon = _it.Icon;
        UseFlags = Flags(L2Battle.ItemUse, _it.Use, Edited);
        EquipBy = Flags(L2Rom.Heroes, _it.EquipBy, Edited);
        Words = L2Battle.ItemWords.Where(w => _it.Values.ContainsKey(w.Bit) && !w.Name.EndsWith("script"))
            .Select(w => new L2Word { Label = w.Name, Bit = w.Bit, Value = _it.Values[w.Bit], Changed = Edited }).ToList();
        var scripts = L2Battle.ItemWords.Where(w => _it.Values.ContainsKey(w.Bit) && w.Name.EndsWith("script")).Select(w => w.Name.ToLowerInvariant()).ToList();
        WordsText = (Words.Count == 0 ? "No stat bonus or IP attack. " : "") +
                    (scripts.Count > 0 ? "Scripts: " + string.Join(", ", scripts) + ". " : "") +
                    "Only the values this item already has can be changed (adding one would make the record grow).";
        var spellNames = L2Battle.Spells(Rom!).Select(s => s.Name).ToArray();
        var parts = new List<string>();
        foreach (var (bit, label) in new[] { (1, "IN THE MENU"), (2, "IN BATTLE"), (4, "AS A WEAPON"), (8, "AS ARMOR") })
            if (_it.Values.TryGetValue(bit, out var off)) parts.Add(label + "\n" + L2BattleScript.Disassemble(Rom!, _it.Offset + off, _it.Offset, spellNames));
        EffectText = parts.Count > 0 ? string.Join("\n\n", parts) : "No effect script.";
    }

    protected override bool IsField(string p) => p is nameof(Name) or nameof(Price) or nameof(EquipType) or nameof(Icon);
    protected override int DescriptionEntry(int index) => index;

    protected override string? Write()
    {
        if (_it == null) return null;
        _it.Name = Name; _it.Price = (ushort)Price; _it.EquipType = (byte)EquipType; _it.Icon = (byte)Icon;
        _it.Use = (byte)FlagValue(UseFlags); _it.EquipBy = (byte)FlagValue(EquipBy);
        foreach (var w in Words) _it.Values[w.Bit] = (ushort)w.Value;
        L2Battle.WriteItem(Rom!, _it);
        return _it.Name;
    }
}

// ------------------------------------------------------------------------------------------- spells

public partial class L2SpellsPage : L2Page
{
    private L2Battle.Spell? _s;
    [ObservableProperty] private string _name = "";
    [ObservableProperty] private int _mp, _price;
    [ObservableProperty] private bool _inMenu, _inBattle;
    [ObservableProperty] private List<L2Flag> _learnBy = new();
    [ObservableProperty] private string _effectText = "";

    protected override IEnumerable<string> ReadNames() => L2Battle.Spells(Rom!).Select(s => $"{s.Index:X2}  {s.Name}");

    protected override void Load(int index)
    {
        _s = L2Battle.ReadSpell(Rom!, index);
        Name = _s.Name; Mp = _s.Mp; Price = _s.Price; InMenu = (_s.Flags & 0x40) != 0; InBattle = (_s.Flags & 0x80) != 0;
        LearnBy = Flags(L2Rom.Heroes, _s.LearnBy, Edited);
        int script = Rom!.ReadUInt16Le(_s.Offset + 0x11);
        EffectText = L2BattleScript.Disassemble(Rom!, _s.Offset + script, _s.Offset, L2Battle.Spells(Rom!).Select(x => x.Name).ToArray());
    }

    protected override bool IsField(string p) => p is nameof(Name) or nameof(Mp) or nameof(Price) or nameof(InMenu) or nameof(InBattle);
    protected override int DescriptionEntry(int index) => L2Descriptions.SpellBase + index;

    protected override string? Write()
    {
        if (_s == null) return null;
        _s.Name = Name; _s.Mp = (ushort)Mp; _s.Price = (ushort)Price;
        _s.Flags = (byte)(_s.Flags & 0x3F | (InMenu ? 0x40 : 0) | (InBattle ? 0x80 : 0));
        _s.LearnBy = (byte)FlagValue(LearnBy);
        L2Battle.WriteSpell(Rom!, _s);
        return _s.Name;
    }
}

// ------------------------------------------------------------------------------------------- IP attacks

public partial class L2IpPage : L2Page
{
    private L2Battle.IpAttack? _a;
    [ObservableProperty] private string _name = "";
    [ObservableProperty] private int _cost, _animation;

    protected override IEnumerable<string> ReadNames() => L2Battle.IpAttacks(Rom!).Select(a => $"{a.Index:X2}  {a.Name}");

    protected override void Load(int index)
    {
        _a = L2Battle.ReadIp(Rom!, index);
        Name = _a.Name; Cost = _a.Cost; Animation = _a.Animation;
    }

    protected override bool IsField(string p) => p is nameof(Cost) or nameof(Animation);
    protected override int DescriptionEntry(int index) => L2Descriptions.IpBase + index;

    protected override string? Write()
    {
        if (_a == null) return null;
        _a.Cost = (byte)Math.Clamp(Cost, 0, 255); _a.Animation = (byte)Animation;
        L2Battle.WriteIp(Rom!, _a);
        return _a.Name + " (IP)";
    }
}

// ------------------------------------------------------------------------------------------- maps

public partial class L2MapsPage : ObservableObject
{
    private RomBuffer? _rom;
    private MainViewModel? _main;
    private L2Maps.Map? _work;
    private int _blockCount;
    public const int PickerColumns = 16;
    [ObservableProperty] private BitmapSource? _pickerImage;
    [ObservableProperty] private int _selectedBlock = 1;
    [ObservableProperty] private bool _editUpper;
    [ObservableProperty] private bool _isDirty;
    [ObservableProperty] private string _editText = "";
    public ObservableCollection<string> Names { get; } = new();
    [ObservableProperty] private int _selectedIndex = -1;
    [ObservableProperty] private bool _showGround = true, _showUpper = true, _showCollision, _showInfo = true;
    public ObservableCollection<string> InfoLines { get; } = new();
    private List<L2MapInfo.Entry> _entries = new();
    [ObservableProperty] private BitmapSource? _mapImage;
    [ObservableProperty] private string _info = "";

    public void SetRom(RomBuffer rom, MainViewModel main)
    {
        _rom = rom; _main = main;
        Names.Clear();
        for (int id = 0; id < L2Maps.Count; id++)
        {
            string n = L2Maps.Name(rom, id);
            Names.Add($"{id:X2}  {(n.Length > 0 && !n.Contains('~') ? n : id < 2 ? (id == 0 ? "World map" : "Sea floor") : "Map " + id.ToString("X2"))}");
        }
        SelectedIndex = -1;
        SelectedIndex = 3;
    }

    partial void OnSelectedIndexChanged(int value)
    {
        _work = null; IsDirty = false;
        InfoLines.Clear();
        _entries = _rom != null && value >= 0 ? L2MapInfo.Read(_rom, value) : new();
        foreach (var e in _entries) InfoLines.Add($"{e.Kind} {e.Id:X2} at ({e.X1},{e.Y1}){(e.X2 != e.X1 || e.Y2 != e.Y1 ? $"-({e.X2},{e.Y2})" : "")} {e.Detail}".TrimEnd());
        if (_rom != null && value >= 2)
        {
            try { _work = L2Maps.Read(_rom, value); _blockCount = L2Maps.BlockCount(_rom, value); DrawPicker(); } catch { _work = null; }
        }
        else PickerImage = null;
        Draw();
    }

    partial void OnSelectedBlockChanged(int value) =>
        EditText = $"Block {value:X3} picked. Left-click or drag on the map paints it on the {(EditUpper ? "upper" : "ground")} layer; right-click picks the block under the mouse.";
    partial void OnEditUpperChanged(bool value) => OnSelectedBlockChanged(SelectedBlock);

    private void DrawPicker()
    {
        if (_rom == null || _work == null) return;
        int rows = (_blockCount + PickerColumns - 1) / PickerColumns, w = PickerColumns * 16, h = Math.Max(16, rows * 16);
        var px = new uint[w * h];
        for (int b = 0; b < _blockCount; b++)
        {
            var bp = L2Maps.BlockPixels(_rom, _work.Id, b);
            int x0 = b % PickerColumns * 16, y0 = b / PickerColumns * 16;
            for (int y = 0; y < 16; y++) for (int x = 0; x < 16; x++) px[(y0 + y) * w + x0 + x] = bp[y * 16 + x] == 0 ? 0xFF202020u : bp[y * 16 + x];
        }
        PickerImage = BitmapSource.Create(w, h, 96, 96, PixelFormats.Bgra32, null, px.Select(c => (int)c).ToArray(), w * 4);
        OnSelectedBlockChanged(SelectedBlock);
    }

    /// <summary>A click on the block picker (pixel position in it).</summary>
    public void PickFromPicker(double x, double y)
    {
        int b = (int)y / 16 * PickerColumns + (int)x / 16;
        if (b >= 0 && b < _blockCount) SelectedBlock = b;
    }

    /// <summary>Paints the picked block at a map pixel position (or picks the block there).</summary>
    public void PaintAt(double x, double y, bool pick)
    {
        int layer = EditUpper ? 1 : 0;
        if (_work == null || _work.Layers.Length <= layer) return;
        var L = _work.Layers[layer];
        int cx = (int)x / 16, cy = (int)y / 16;
        if (cx < 0 || cy < 0 || cx >= L.Width || cy >= L.Height) return;
        int i = cy * L.Width + cx;
        if (pick) { SelectedBlock = L.Blocks[i]; return; }
        if (L.Blocks[i] == SelectedBlock) return;
        L.Blocks[i] = SelectedBlock;
        IsDirty = true;
        Draw();
    }

    [CommunityToolkit.Mvvm.Input.RelayCommand]
    private void ApplyMap()
    {
        if (_rom == null || _main == null || _work == null || !IsDirty) return;
        try
        {
            int before = _rom.Length;
            L2Maps.Write(_rom, _work);
            _main.NotifyRomModified($"Lufia II: map {_work.Id:X2}");
            IsDirty = false;
            EditText = "Written to the ROM" + (_rom.Length > before ? " (it no longer fit where it was: the ROM grew to 3 MB and the map moved there)." : ".");
        }
        catch (Exception ex) { EditText = "Not written: " + ex.Message; }
    }

    [CommunityToolkit.Mvvm.Input.RelayCommand]
    private void RevertMap() => OnSelectedIndexChanged(SelectedIndex);

    private static void Dot(uint[] px, int w, int h, int x, int y, uint c) { if (x >= 0 && y >= 0 && x < w && y < h) px[y * w + x] = c; }
    partial void OnShowGroundChanged(bool value) => Draw();
    partial void OnShowUpperChanged(bool value) => Draw();
    partial void OnShowCollisionChanged(bool value) => Draw();
    partial void OnShowInfoChanged(bool value) => Draw();

    /// <summary>Goes to the map an exit leads to (a double-click on the exit in the list).</summary>
    public void FollowInfo(int index)
    {
        if (index < 0 || index >= _entries.Count || _entries[index].DestMap < 0) return;
        SelectedIndex = _entries[index].DestMap;
    }

    /// <summary>Redraws the picked map (also after an edit elsewhere).</summary>
    public void Draw()
    {
        if (_rom != null && SelectedIndex == 0)
        {
            var (ww, wh, wpx) = L2WorldMap.Render(_rom, 2);
            MapImage = BitmapSource.Create(ww, wh, 96, 96, PixelFormats.Bgra32, null, wpx.Select(c => (int)c).ToArray(), ww * 4);
            Info = "The world map: 128 x 128 blocks of 32 x 32 pixels (shown at half size).";
            return;
        }
        if (_rom == null || SelectedIndex < 2) { MapImage = null; Info = SelectedIndex == 1 ? "The sea floor is drawn differently (coming later)." : ""; return; }
        try
        {
            var map = _work ?? L2Maps.Read(_rom, SelectedIndex);
            var (w, h, px) = L2Maps.Render(_rom, map, ShowGround, ShowUpper);
            if (ShowCollision && map.Layers.Length > 1)
            {
                var L = map.Layers[1];
                for (int cy = 0; cy < L.Height; cy++)
                    for (int cx = 0; cx < L.Width; cx++)
                    {
                        byte f = L.Flags[cy * L.Width + cx];
                        uint tint = (f & 0x80) != 0 ? 0xFFFF3030u : (f & 0x20) != 0 ? 0xFF30C0FFu : (f & 0x0C) != 0 ? 0xFFFFC030u : 0;
                        if (tint == 0) continue;
                        for (int y = 0; y < 16; y++)
                            for (int x = 0; x < 16; x++)
                            {
                                bool edge = (f & 0x80) != 0 || (f & 0x20) != 0 || (f & 0x04) != 0 && y < 2 || (f & 0x08) != 0 && x < 2;
                                if (!edge || (x + y) % 2 == 1 && (f & 0xA0) != 0) continue;
                                int i = (cy * 16 + y) * w + cx * 16 + x;
                                if (i < px.Length) px[i] = tint;
                            }
                    }
            }
            if (ShowInfo)
            {
                var sprites = L2MapInfo.CharacterSprites(_rom, SelectedIndex);
                foreach (var e in _entries.Where(e => e.Kind == "Character start"))
                {
                    if (!sprites.TryGetValue(e.Id + 0x4F, out int spr) || L2Graphics.MapSprite(_rom, spr) is not var (sw, sh, spx)) continue;
                    int x0 = e.X1 * 16 + 8 - sw / 2, y0 = e.Y1 * 16 + 16 - sh;
                    for (int y = 0; y < sh; y++)
                        for (int x = 0; x < sw; x++)
                            if (spx[y * sw + x] != 0) Dot(px, w, h, x0 + x, y0 + y, spx[y * sw + x]);
                }
            }
            if (ShowInfo)
                foreach (var e in _entries)
                {
                    uint col = e.Kind switch { "Exit" => 0xFF40FF40u, "Door" => 0xFFFFE040u, "Treasure" => 0xFFFF40FFu, "Character start" => 0xFF40C0FFu, "Cutscene trigger" => 0xFFFF8040u, _ => 0xFFFFFFFFu };
                    int x1 = e.X1 * 16, y1 = e.Y1 * 16, x2 = (e.X2 + 1) * 16 - 1, y2 = (e.Y2 + 1) * 16 - 1;
                    for (int x = x1; x <= x2; x++) { Dot(px, w, h, x, y1, col); Dot(px, w, h, x, y2, col); }
                    for (int y = y1; y <= y2; y++) { Dot(px, w, h, x1, y, col); Dot(px, w, h, x2, y, col); }
                }
            MapImage = BitmapSource.Create(w, h, 96, 96, PixelFormats.Bgra32, null, px.Select(c => (int)(c == 0 ? 0xFF000000u : c)).ToArray(), w * 4);
            Info = $"{map.Layers[0].Width} x {map.Layers[0].Height} blocks; block set file {map.BlockSet:X3}, tile set file {map.TileSet:X3}, map file {L2Maps.MapFileBase + SelectedIndex:X3}." +
                   (ShowCollision ? "  Red: obstacle, blue: doorway, orange: wall on the top / left edge." : "");
        }
        catch (Exception ex) { MapImage = null; Info = "Couldn't draw it: " + ex.Message; }
    }
}

// ------------------------------------------------------------------------------------------- text

public partial class L2TextPage : ObservableObject
{
    private RomBuffer? _rom;
    private MainViewModel? _main;
    private List<L2Events.Line> _lines = new();
    private readonly List<int> _maps = new();

    public ObservableCollection<string> MapNames { get; } = new();
    public ObservableCollection<string> LineNames { get; } = new();
    [ObservableProperty] private int _selectedMap = -1;
    [ObservableProperty] private int _selectedLine = -1;
    [ObservableProperty] private string _text = "";
    [ObservableProperty] private string _speaker = "";
    [ObservableProperty] private string _room = "";
    [ObservableProperty] private string _status = "";

    public void SetRom(RomBuffer rom, MainViewModel main)
    {
        _rom = rom; _main = main;
        MapNames.Clear(); _maps.Clear();
        int total = 0;
        for (int m = 0; m < L2Maps.Count; m++)
        {
            int c = L2Events.Lines(rom, m).Count;
            if (c == 0) continue;
            string n = L2Maps.Name(rom, m);
            _maps.Add(m);
            MapNames.Add($"{m:X2}  {(n.Length > 0 && !n.Contains('~') ? n : "Map " + m.ToString("X2"))}  ({c})");
            total += c;
        }
        Status = $"{total} lines on {_maps.Count} maps.";
        SelectedMap = -1;
        SelectedMap = _maps.Count > 0 ? 0 : -1;
    }

    partial void OnSelectedMapChanged(int value)
    {
        LineNames.Clear();
        if (_rom == null || value < 0) { _lines = new(); return; }
        _lines = L2Events.Lines(_rom, _maps[value]);
        foreach (var l in _lines) LineNames.Add($"{l.Speaker}: {l.Text.Replace("\n", " ")}");
        SelectedLine = -1;
        SelectedLine = _lines.Count > 0 ? 0 : -1;
    }

    partial void OnSelectedLineChanged(int value)
    {
        if (value < 0 || value >= _lines.Count) { Text = ""; Speaker = ""; Room = ""; return; }
        var l = _lines[value];
        Speaker = $"{l.Speaker}, at {l.Offset:X6}";
        Text = l.Text;
        Room = $"{l.Length} bytes in the ROM.";
    }

    partial void OnTextChanged(string value)
    {
        if (_rom == null || SelectedLine < 0 || SelectedLine >= _lines.Count) return;
        int n = L2Text.Encode(_rom, value).Length, room = _lines[SelectedLine].Length;
        Room = n <= room ? $"{n} of {room} bytes." : $"{n} of {room} bytes: {n - room} too many (shorten it; words from the game's dictionary take 1 or 2 bytes).";
    }

    [CommunityToolkit.Mvvm.Input.RelayCommand]
    private void Apply()
    {
        if (_rom == null || _main == null || SelectedLine < 0) return;
        var l = _lines[SelectedLine];
        if (!Text.EndsWith("[WAIT]") && !Text.EndsWith("[END]")) { Status = "The text has to end with [WAIT] or [END] like the original."; return; }
        if (!L2Events.WriteLine(_rom, l, Text))
        {
            Status = L2Events.HasBackReference(_rom, l)
                ? "It doesn't fit: shorten it first. (This line reuses words from an earlier line in a short form; written out they take more room.)"
                : "It doesn't fit: shorten it first.";
            return;
        }
        _main.NotifyRomModified($"Lufia II: text on map {l.Map:X2}");
        int keep = SelectedLine;
        OnSelectedMapChanged(SelectedMap);
        SelectedLine = keep;
        Status = "Written. (Edit > Undo takes it back.)";
    }
}


// ------------------------------------------------------------------------------------------- encounters

/// <summary>A choice in a list of choices (a monster in a group slot, a group in a map's list).</summary>
public partial class L2Slot : ObservableObject
{
    public string Label { get; init; } = "";
    public List<L2Option> Options { get; init; } = new();
    [ObservableProperty] private int _value;
    public Action? Changed { get; set; }
    partial void OnValueChanged(int value) => Changed?.Invoke();
}

public partial class L2EncountersPage : ObservableObject
{
    private RomBuffer? _rom;
    private MainViewModel? _main;
    private bool _loading;
    private List<L2Option> _monsters = new(), _groupOptions = new();

    public ObservableCollection<string> GroupNames { get; } = new();
    public ObservableCollection<string> MapNames { get; } = new();
    [ObservableProperty] private int _selectedGroup = -1;
    [ObservableProperty] private int _selectedMap = -1;
    [ObservableProperty] private List<L2Slot> _groupSlots = new();
    [ObservableProperty] private List<L2Slot> _mapSlots = new();
    [ObservableProperty] private string _mapText = "";

    public void SetRom(RomBuffer rom, MainViewModel main)
    {
        _rom = rom; _main = main;
        _monsters = new[] { new L2Option(0xFF, "-") }.Concat(L2Battle.Monsters(rom).Select(m => new L2Option(m.Index, $"{m.Index:X2} {m.Name}"))).ToList();
        RefreshGroups();
        MapNames.Clear();
        for (int m = 0; m < L2Maps.Count; m++)
        {
            string n = L2Maps.Name(rom, m);
            MapNames.Add($"{m:X2}  {(n.Length > 0 && !n.Contains('~') ? n : "Map " + m.ToString("X2"))}  ({L2Encounters.MapList(rom, m).Length})");
        }
        SelectedGroup = -1; SelectedGroup = 0;
        SelectedMap = -1; SelectedMap = 0x0E;
    }

    private string GroupLabel(int g) =>
        $"{g:X2}  " + string.Join(", ", L2Encounters.Group(_rom!, g).Where(m => m != 0xFF).Select(m => _monsters.FirstOrDefault(o => o.Value == m)?.Label[3..] ?? m.ToString("X2")));

    private void RefreshGroups()
    {
        int keep = SelectedGroup;
        GroupNames.Clear();
        for (int g = 0; g < L2Encounters.GroupCount; g++) GroupNames.Add(GroupLabel(g));
        _groupOptions = Enumerable.Range(0, L2Encounters.GroupCount).Select(g => new L2Option(g, GroupLabel(g))).ToList();
        if (keep >= 0) { _loading = true; SelectedGroup = keep; _loading = false; }
    }

    partial void OnSelectedGroupChanged(int value)
    {
        if (_rom == null || value < 0 || _loading) return;
        var ms = L2Encounters.Group(_rom, value);
        GroupSlots = ms.Select((m, i) => new L2Slot { Label = $"Monster {i + 1}", Options = _monsters, Value = m, Changed = GroupEdited }).ToList();
    }

    private void GroupEdited()
    {
        if (_rom == null || _main == null || SelectedGroup < 0) return;
        L2Encounters.WriteGroup(_rom, SelectedGroup, GroupSlots.Select(s => s.Value).ToArray());
        _main.NotifyRomModified($"Lufia II: monster group {SelectedGroup:X2}");
        _loading = true; GroupNames[SelectedGroup] = GroupLabel(SelectedGroup); SelectedGroup = SelectedGroup; _loading = false;
        _groupOptions = Enumerable.Range(0, L2Encounters.GroupCount).Select(g => new L2Option(g, GroupLabel(g))).ToList();
        OnSelectedMapChanged(SelectedMap);   // (its group names)
    }

    partial void OnSelectedMapChanged(int value)
    {
        if (_rom == null || value < 0) return;
        var (at, len) = L2Encounters.MapList(_rom, value);
        MapSlots = Enumerable.Range(0, len).Select(i => new L2Slot { Label = $"Monster {i + 1}", Options = _groupOptions, Value = _rom.ReadByte(at + i), Changed = MapEdited }).ToList();
        var shared = L2Encounters.SharingWith(_rom, value);
        MapText = len == 0 ? "This map has no monsters." :
            $"The group each of the map's {len} monsters fights (in the order the map places them)." +
            (shared.Count > 0 ? $" Shared with map{(shared.Count > 1 ? "s" : "")} {string.Join(", ", shared.Select(m => m.ToString("X2")))}: a change here changes {(shared.Count > 1 ? "them" : "it")} too." : "");
    }

    private void MapEdited()
    {
        if (_rom == null || _main == null || SelectedMap < 0) return;
        var (at, _) = L2Encounters.MapList(_rom, SelectedMap);
        for (int i = 0; i < MapSlots.Count; i++) _rom.WriteByte(at + i, (byte)MapSlots[i].Value);
        _main.NotifyRomModified($"Lufia II: monsters of map {SelectedMap:X2}");
    }
}


// ------------------------------------------------------------------------------------------- shops

/// <summary>One list of a shop (items, weapons, ...), its slots as choices.</summary>
public sealed class L2ShopSection
{
    public string Title { get; init; } = "";
    public List<L2Slot> Slots { get; init; } = new();
}

public partial class L2ShopsPage : ObservableObject
{
    private RomBuffer? _rom;
    private MainViewModel? _main;
    private List<L2Shops.Shop> _shops = new();
    private List<L2Option> _items = new(), _spells = new();

    public ObservableCollection<string> Names { get; } = new();
    [ObservableProperty] private int _selectedIndex = -1;
    [ObservableProperty] private List<L2ShopSection> _sections = new();
    [ObservableProperty] private string _info = "";

    public void SetRom(RomBuffer rom, MainViewModel main)
    {
        _rom = rom; _main = main;
        _items = L2Battle.Items(rom).Select(i => new L2Option(i.Index, $"{i.Index:X3} {i.Name}  ({i.Price} G)")).ToList();
        _spells = L2Battle.Spells(rom).Select(s => new L2Option(s.Index, $"{s.Index:X2} {s.Name}  ({s.Price} G)")).ToList();
        _shops = L2Shops.All(rom);
        Names.Clear();
        foreach (var s in _shops)
        {
            var maps = L2Shops.UsedOn(rom, s.Id);
            string where = maps.Count > 0 ? "  " + L2Maps.Name(rom, maps[0]) : "";
            string what = string.Join(", ", (s.IsComposite ? new[] { "second-hand" } : Array.Empty<string>()).Concat(s.Sections.Select(x => x.Name)));
            Names.Add($"{s.Id:X2}  {what}{where}");
        }
        SelectedIndex = -1;
        SelectedIndex = _shops.Count > 0 ? 0 : -1;
    }

    partial void OnSelectedIndexChanged(int value)
    {
        if (_rom == null || value < 0 || value >= _shops.Count) { Sections = new(); return; }
        var shop = _shops[value];
        Sections = shop.Sections.Select(sec => new L2ShopSection
        {
            Title = sec.Name.ToUpperInvariant(),
            Slots = sec.Entries.Select((e, i) => new L2Slot
            {
                Label = $"{i + 1}.", Options = sec.IsSpells ? _spells : _items, Value = e,
                Changed = () => SlotEdited(shop, sec),
            }).ToList(),
        }).ToList();
        var maps = L2Shops.UsedOn(_rom, shop.Id);
        Info = (shop.IsComposite ? "Its first list is made by the game: every item with a count in a table in RAM ($7F:F080), probably items sold earlier. It has nothing to edit. " : "") +
               (maps.Count > 0 ? $"Opened on map{(maps.Count > 1 ? "s" : "")} {string.Join(", ", maps.Select(m => $"{m:X2} {L2Maps.Name(_rom, m)}"))}. " : "") +
               (shop.Sells ? "It also buys (Sell). " : "") +
               "Each slot can be any item (or spell); the lists keep their length.";
    }

    private void SlotEdited(L2Shops.Shop shop, L2Shops.Section sec)
    {
        if (_rom == null || _main == null) return;
        var ui = Sections.ElementAtOrDefault(shop.Sections.IndexOf(sec));
        if (ui == null) return;
        for (int i = 0; i < sec.Entries.Count; i++) sec.Entries[i] = ui.Slots[i].Value;
        L2Shops.WriteSection(_rom, sec);
        _main.NotifyRomModified($"Lufia II: shop {shop.Id:X2}");
    }
}
