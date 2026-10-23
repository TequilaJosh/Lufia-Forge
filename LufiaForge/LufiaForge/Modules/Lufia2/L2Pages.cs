using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
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
        try { Load(value); } finally { Loading = false; }
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
        if (e.PropertyName is nameof(SelectedIndex) or nameof(IsEditable) or nameof(Names) || e.PropertyName!.StartsWith("Picture")) return;
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
        ScriptText = $"Attack script {(_m.AttackScript > 0 ? $"at {_m.AttackScript:X6}" : "none")}, defense script {(_m.DefenseScript > 0 ? $"at {_m.DefenseScript:X6}" : "none")}" +
                     (HasDrop ? "" : ". It drops nothing (a drop can't be added yet: the record would grow).");
        PictureImage = Picture(L2Graphics.MonsterSprite(Rom!, Sprite, Palette));
    }

    protected override bool IsField(string p) => p is not (nameof(ScriptText) or nameof(HasDrop) or nameof(ItemOptions));

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
    }

    protected override bool IsField(string p) => p is nameof(Name) or nameof(Price) or nameof(EquipType) or nameof(Icon);

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

    protected override IEnumerable<string> ReadNames() => L2Battle.Spells(Rom!).Select(s => $"{s.Index:X2}  {s.Name}");

    protected override void Load(int index)
    {
        _s = L2Battle.ReadSpell(Rom!, index);
        Name = _s.Name; Mp = _s.Mp; Price = _s.Price; InMenu = (_s.Flags & 0x40) != 0; InBattle = (_s.Flags & 0x80) != 0;
        LearnBy = Flags(L2Rom.Heroes, _s.LearnBy, Edited);
    }

    protected override bool IsField(string p) => p is nameof(Name) or nameof(Mp) or nameof(Price) or nameof(InMenu) or nameof(InBattle);

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

    protected override string? Write()
    {
        if (_a == null) return null;
        _a.Cost = (byte)Math.Clamp(Cost, 0, 255); _a.Animation = (byte)Animation;
        L2Battle.WriteIp(Rom!, _a);
        return _a.Name + " (IP)";
    }
}
