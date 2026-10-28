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
    /// <summary>The record has this word (else Lufia Forge can add it, see <see cref="L2AiScripts.SetItemAddedWord"/>).</summary>
    public bool InRecord { get; init; }
    [ObservableProperty] private int _value;
    [ObservableProperty] private bool _has;
    [ObservableProperty] private string _note = "";
    public Func<int, string>? Describe { get; init; }
    public Action? Changed { get; set; }
    public bool CanSwitch => !InRecord;
    /// <summary>Picked from a list (the IP attack by its name) instead of typed as a number.</summary>
    public List<L2Option>? Options { get; init; }
    public bool IsChoice => Options != null;
    public bool IsNumber => Options == null;
    partial void OnValueChanged(int value) { Note = Describe?.Invoke(value) ?? ""; if (!Has && !InRecord) Has = true; else Changed?.Invoke(); }
    partial void OnHasChanged(bool value) => Changed?.Invoke();
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
    /// <summary>Why the page is read-only in this ROM (another hack moved a table it edits), or "".</summary>
    [ObservableProperty] private string _lockNote = "";

    /// <summary>The page's name for <see cref="TableGuard"/>.</summary>
    protected virtual string PageName => GetType().Name switch
    {
        "L2ItemsPage" => "Items", "L2SpellsPage" => "Spells", "L2IpPage" => "IP attacks", "L2MonstersPage" => "Monsters",
        "L2CharactersPage" => "Characters", "L2CapsulesPage" => "Capsule monsters", "L2ChestsPage" => "Treasure", "L2ExitsPage" => "Exits",
        "L2EventsPage" => "Events", "L2GraphicsPage" => "Graphics", "L2MusicPage" => "Music", _ => "",
    };
    /// <summary>The picked record's description (items, spells, IP attacks), new lines as line breaks.</summary>
    [ObservableProperty] private string _description = "";
    [ObservableProperty] private string _descriptionNote = "";

    /// <summary>The description entry of record <paramref name="index"/> (<see cref="L2Descriptions"/>), or -1.</summary>
    protected virtual int DescriptionEntry(int index) => -1;
    public bool HasDescription => DescriptionEntry(0) >= 0;

    public void SetRom(RomBuffer rom, MainViewModel main)
    {
        Rom = rom; Main = main;
        OnListRead();
        Names.Clear();
        foreach (var n in ReadNames()) Names.Add(n);
        LockNote = TableGuard.LockReason(rom, PageName) ?? "";
        IsEditable = Names.Count > 0 && LockNote.Length == 0;
        SelectedIndex = -1;
        SelectedIndex = Names.Count > 0 ? 0 : -1;
    }

    /// <summary>Reads the list again (after a record was added) and picks <paramref name="select"/>.</summary>
    protected void RefreshList(int select)
    {
        if (Rom == null) return;
        Loading = true;
        try
        {
            OnListRead();
            Names.Clear();
            foreach (var n in ReadNames()) Names.Add(n);
        }
        finally { Loading = false; }
        SelectedIndex = -1;
        SelectedIndex = Math.Min(select, Names.Count - 1);
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
    /// <summary>Before the list is read for a ROM (or again after a record was added): lists the fields pick from.</summary>
    protected virtual void OnListRead() { }
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

    /// <summary>A spell's or IP attack's effect as steps (what runs now: Lufia Forge's edited one or the game's).</summary>
    protected static string EffectListing(RomBuffer rom, L2AiScripts.Kind kind, int index)
    {
        try
        {
            var (steps, edited) = L2AiScripts.Load(rom, kind, index);
            return (edited ? "(edited in Lufia Forge)\n" : "") + (steps.Count == 0 ? "No effect script." : L2AiScripts.Listing(steps, L2AiScripts.NameLists.From(rom)));
        }
        catch (Exception e) { return e.Message; }
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
    /// <summary>The battle sprites (1 up, the game's and Lufia Forge's new ones) and the palettes of the one picked.</summary>
    [ObservableProperty] private List<L2Option> _spriteOptions = new();
    [ObservableProperty] private List<L2Option> _paletteOptions = new();
    [ObservableProperty] private string _spriteNote = "";
    /// <summary>How many monsters the ROM has (the US game's 224, or a hack's table up to 256).</summary>
    [ObservableProperty] private string _countNote = "";
    /// <summary>What to know about the picked record in a hack (a placeholder slot, a record shared by several ids, a
    /// name field that also holds script bytes), or "".</summary>
    [ObservableProperty] private string _recordNote = "";
    [ObservableProperty] private int _nameMax = L2Battle.MonsterNameLength;

    private static string Line(L2Battle.Monster m) =>
        $"{m.Index:X2}  " + (m.Unused ? "(unused slot)" : m.Name + (m.SameAs >= 0 ? $"  (= {m.SameAs:X2})" : ""));

    protected override IEnumerable<string> ReadNames()
    {
        ItemOptions = L2Battle.Items(Rom!).Select(i => new L2Option(i.Index, $"{i.Index:X3} {i.Name}")).ToList();
        OnPropertyChanged(nameof(ItemOptions));
        var ms = L2Battle.Monsters(Rom!);
        SpriteOptions = Enumerable.Range(1, L2MonsterSprites.Count(Rom!)).Select(n =>
        {
            var (tx, ty, _) = L2MonsterSprites.Size(Rom!, n);
            var who = ms.Where(m => m.Sprite == n).Select(m => m.Name).Distinct().ToList();
            string names = who.Count == 0 ? (L2MonsterSprites.IsNew(n) ? "new" : "no monster") : string.Join(", ", who.Take(2)) + (who.Count > 2 ? ", ..." : "");
            return new L2Option(n, $"{n:X2} ({tx * 8} x {ty * 8}) {names}");
        }).ToList();
        int real = ms.Count(m => !m.Unused);
        CountNote = (ms.Count == L2Battle.MonsterCount && real == ms.Count ? "Lufia II's 224 monsters." :
                        $"This ROM's monster table has {ms.Count} entries (the US game 224): {real} monsters" +
                        (real < ms.Count ? $", {ms.Count - real} unused slots the hack points at a placeholder." : ".")) +
                    " A change is written to the ROM at once (Edit, Undo takes it back).";
        return ms.Select(Line);
    }

    /// <summary>The palettes of sprite <paramref name="sprite"/> (and the note under the picture).</summary>
    private void ShowSprite(int sprite)
    {
        int pals = 0;
        try { if (sprite >= 1 && sprite <= L2MonsterSprites.Count(Rom!)) pals = L2MonsterSprites.PaletteCount(Rom!, sprite); } catch { }
        PaletteOptions = Enumerable.Range(0, Math.Max(1, pals)).Select(p => new L2Option(p, $"Palette {p}")).ToList();
        SpriteNote = pals == 0 ? "No such sprite." :
            $"Sprite {sprite:X2}: {L2MonsterSprites.Where(Rom!, sprite)}, {pals} palette{(pals == 1 ? "" : "s")}. Paint it, give it new palettes or make new sprites in Graphics (Monster battle sprites).";
    }

    partial void OnSpriteChanged(int value)
    {
        if (Rom == null) return;
        bool was = Loading;
        Loading = true;
        try
        {
            ShowSprite(value);
            if (Palette >= PaletteOptions.Count) Palette = 0;
        }
        finally { Loading = was; }
        if (Loading) return;
        PictureImage = Picture(L2Graphics.MonsterSprite(Rom, Sprite, Palette));
    }

    protected override void Load(int index)
    {
        if (SpriteOptions.Count != L2MonsterSprites.Count(Rom!)) ReadNames().ToList();   // sprites added in Graphics
        _m = L2Battle.ReadMonster(Rom!, index);
        NameMax = Math.Max(1, L2Battle.NameRoom(Rom!, _m));
        var notes = new List<string>();
        if (_m.Unused) notes.Add("An unused slot: the hack points this id at a placeholder record that other ids share, so an edit here changes them all.");
        var sharing = L2Battle.Monsters(Rom!).Where(m => m.Index != index && m.Offset == _m.Offset).Select(m => m.Index.ToString("X2")).ToList();
        if (!_m.Unused && sharing.Count > 0) notes.Add($"Ids {string.Join(", ", sharing)} use the same record: an edit changes them too.");
        if (!_m.Unused && NameMax < L2Battle.MonsterNameLength)
            notes.Add($"The name can have up to {NameMax} letter{(NameMax == 1 ? "" : "s")}: the rest of its field holds battle script bytes.");
        RecordNote = string.Join(" ", notes);
        Name = _m.Name; Level = _m.Level; Hp = _m.Hp; Mp = _m.Mp; Atp = _m.Atp; Dfp = _m.Dfp; Agl = _m.Agl; Int = _m.Int; Gut = _m.Gut; Mgr = _m.Mgr;
        Exp = _m.Exp; Gold = _m.Gold; Sprite = _m.Sprite; ShowSprite(_m.Sprite); Palette = _m.Palette;
        HasDrop = _m.DropAt > 0; DropItem = Math.Max(0, _m.DropItem); DropChance = _m.DropChance;
        ScriptText = HasDrop ? "" : "It drops nothing (a drop can't be added yet: the record would grow).";
        AiText = ScriptsText(Rom!, L2AiScripts.Kind.MonsterAttack, L2AiScripts.Kind.MonsterDefense, index);
        PictureImage = Picture(L2Graphics.MonsterSprite(Rom!, Sprite, Palette));
    }

    protected override bool IsField(string p) => p is not (nameof(ScriptText) or nameof(AiText) or nameof(HasDrop) or nameof(ItemOptions)
        or nameof(SpriteOptions) or nameof(PaletteOptions) or nameof(SpriteNote) or nameof(CountNote) or nameof(RecordNote) or nameof(NameMax));

    [RelayCommand] private void EditTurnScript() { if (_m != null) EditScript(L2AiScripts.Kind.MonsterAttack, _m.Index, _m.Name); }
    [RelayCommand] private void EditHitScript() { if (_m != null) EditScript(L2AiScripts.Kind.MonsterDefense, _m.Index, _m.Name); }

    protected override string? Write()
    {
        if (_m == null) return null;
        _m.Name = Name; _m.Level = (byte)Level; _m.Hp = (ushort)Hp; _m.Mp = (ushort)Mp; _m.Atp = (ushort)Atp; _m.Dfp = (ushort)Dfp;
        _m.Agl = (byte)Agl; _m.Int = (byte)Int; _m.Gut = (byte)Gut; _m.Mgr = (byte)Mgr; _m.Exp = (ushort)Exp; _m.Gold = (ushort)Gold;
        _m.Sprite = (byte)Math.Clamp(Sprite, 1, L2MonsterSprites.Count(Rom!)); _m.Palette = (byte)Math.Clamp(Palette, 0, 255);
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
    /// <summary>An item's battle animation word: the animation + 1 ($81:AA3D takes 1 off; 0 plays animation 00 too).</summary>
    public List<L2Option> AnimationOptions { get; private set; } = new();

    [ObservableProperty] private string _name = "";
    [ObservableProperty] private int _price, _equipType, _icon;
    [ObservableProperty] private List<L2Flag> _useFlags = new();
    [ObservableProperty] private List<L2Flag> _equipBy = new();
    [ObservableProperty] private List<L2Word> _words = new();
    [ObservableProperty] private string _wordsText = "";
    [ObservableProperty] private string _effectText = "";
    private string[] _ipNames = Array.Empty<string>();

    protected override void OnListRead()
    {
        // items name an IP attack counted from 1 ($81:F45E takes 1 off), 0 = none
        IpOptions = new[] { new L2Option(0, "(none)") }.Concat(L2Battle.IpAttacks(Rom!).Select(a => new L2Option(a.Index + 1, a.Name))).ToList();
        _ipNames = L2Battle.IpAttacks(Rom!).Select(a => a.Name).ToArray();
        OnPropertyChanged(nameof(IpOptions));
        AnimationOptions = new[] { new L2Option(0, "(default)") }.Concat(L2Magic.Animations(Rom!).Select(a => new L2Option(a.Value + 1, a.Name))).ToList();
        OnPropertyChanged(nameof(AnimationOptions));
    }

    protected override IEnumerable<string> ReadNames()
    {
        return L2Battle.Items(Rom!).Select(i => $"{i.Index:X3}  {i.Name}");
    }

    private static List<L2Option> WithValue(List<L2Option> l, int v) => l.Any(o => o.Value == v) ? l : l.Append(new L2Option(v, $"Animation {v - 1:X2}")).ToList();

    [RelayCommand] private void EditMenuScript() => EditItem(L2AiScripts.Kind.ItemMenu);
    [RelayCommand] private void EditBattleScript() => EditItem(L2AiScripts.Kind.ItemBattle);
    [RelayCommand] private void EditWeaponScript() => EditItem(L2AiScripts.Kind.Weapon);
    [RelayCommand] private void EditArmorScript() => EditItem(L2AiScripts.Kind.Armor);
    private void EditItem(L2AiScripts.Kind k) { if (_it != null) EditScript(k, _it.Index, _it.Name.Trim()); }

    protected override void Load(int index)
    {
        _it = L2Battle.ReadItem(Rom!, index);
        Name = _it.Name; Price = _it.Price; EquipType = _it.EquipType; Icon = _it.Icon;
        UseFlags = Flags(L2Battle.ItemUse, _it.Use, Edited);
        EquipBy = Flags(L2Rom.Heroes, _it.EquipBy, Edited);
        // every bonus word: the record's own, or one Lufia Forge adds (the item loader hook puts it in)
        var added = L2AiScripts.ItemAddedWords(Rom!, index);
        string Ip(int v) => v >= 1 && v <= _ipNames.Length ? "" : v == 0 ? "" : "not an IP attack";
        Words = L2Battle.ItemWords.Where(w => !w.Name.EndsWith("script") && w.Bit != 0x4000)
            .Select(w =>
            {
                bool own = _it.Values.ContainsKey(w.Bit);
                int v = own ? _it.Values[w.Bit] : added.GetValueOrDefault(w.Bit);
                var word = new L2Word { Label = w.Name, Bit = w.Bit, InRecord = own, Describe = w.Bit == 0x8000 ? Ip : null, Changed = Edited,
                                        Options = w.Bit == 0x8000 ? IpOptions : w.Bit == 0x2000 ? WithValue(AnimationOptions, v) : null };
                word.Value = v; word.Has = own || added.ContainsKey(w.Bit); word.Note = w.Bit == 0x8000 ? Ip(v) : "";
                return word;
            }).ToList();
        WordsText = "Tick a bonus to give it to the item (ATP for weapons, DFP for armor...). The IP attack is the one its wearer can use (new ones are made on the IP attacks page).";
        var names = L2AiScripts.NameLists.From(Rom!);
        var parts = new List<string>();
        foreach (var k in new[] { L2AiScripts.Kind.ItemMenu, L2AiScripts.Kind.ItemBattle, L2AiScripts.Kind.Weapon, L2AiScripts.Kind.Armor })
        {
            var (steps, edited) = L2AiScripts.Load(Rom!, k, index);
            if (steps.Count > 0) parts.Add(L2AiScripts.KindName(k).ToUpperInvariant() + (edited ? "  (edited in Lufia Forge)" : "") + "\n" + L2AiScripts.Listing(steps, names));
        }
        EffectText = parts.Count > 0 ? string.Join("\n\n", parts) : "No effect yet: the buttons below give it one.";
    }

    protected override bool IsField(string p) => p is nameof(Name) or nameof(Price) or nameof(EquipType) or nameof(Icon);
    protected override int DescriptionEntry(int index) => index;

    protected override string? Write()
    {
        if (_it == null) return null;
        _it.Name = Name; _it.Price = (ushort)Price; _it.EquipType = (byte)EquipType; _it.Icon = (byte)Icon;
        _it.Use = (byte)FlagValue(UseFlags); _it.EquipBy = (byte)FlagValue(EquipBy);
        foreach (var w in Words)
        {
            if (w.InRecord) _it.Values[w.Bit] = (ushort)w.Value;
            else L2AiScripts.SetItemAddedWord(Rom!, _it.Index, w.Bit, w.Has ? w.Value : null);
        }
        L2Battle.WriteItem(Rom!, _it);
        return _it.Name;
    }
}

// ------------------------------------------------------------------------------------------- spells

/// <summary>Options for a target / pointer combo box: the known ones in words, and the record's own value when the game
/// uses one that has no name.</summary>
internal static class L2Choices
{
    public static List<L2Option> With(IEnumerable<(int Value, string Name)> known, int current)
    {
        var l = known.Select(k => new L2Option(k.Value, k.Name)).ToList();
        if (l.All(o => o.Value != current)) l.Add(new L2Option(current, $"Other ({current:X2})"));
        return l;
    }
}

public partial class L2SpellsPage : L2Page
{
    private L2Battle.Spell? _s;
    [ObservableProperty] private string _name = "";
    [ObservableProperty] private int _mp, _price, _target, _animation;
    [ObservableProperty] private bool _inMenu, _inBattle;
    [ObservableProperty] private List<L2Flag> _learnBy = new();
    [ObservableProperty] private string _effectText = "";
    [ObservableProperty] private string _countText = "";
    [ObservableProperty] private string _animationNote = "";
    [ObservableProperty] private List<L2Option> _targetOptions = new();
    [ObservableProperty] private List<L2Option> _animationOptions = new();

    protected override void OnListRead() => AnimationOptions = L2Magic.Animations(Rom!).Select(a => new L2Option(a.Value, a.Name)).ToList();

    protected override IEnumerable<string> ReadNames()
    {
        var all = L2Battle.Spells(Rom!);
        CountText = $"Lufia II's spells ({all.Count}; the game has 40, it can have {L2Magic.MaxSpells}). A change is written to the ROM at once (Edit, Undo takes it back).";
        return all.Select(s => $"{s.Index:X2}  {s.Name}" + (s.Index >= L2Battle.SpellCountOriginal ? "  (new)" : ""));
    }

    protected override void Load(int index)
    {
        _s = L2Battle.ReadSpell(Rom!, index);
        Name = _s.Name; Mp = _s.Mp; Price = _s.Price; InMenu = (_s.Flags & 0x40) != 0; InBattle = (_s.Flags & 0x80) != 0;
        TargetOptions = L2Choices.With(L2Magic.Targets, _s.Target);
        Target = _s.Target;
        int anim = L2Magic.SpellAnimation(Rom!, index);
        if (anim >= 0 && AnimationOptions.All(o => o.Value != anim)) AnimationOptions = AnimationOptions.Append(new L2Option(anim, $"Animation {anim:X2}")).ToList();
        Animation = anim;
        LearnBy = Flags(L2Rom.Heroes, _s.LearnBy, Edited);
        EffectText = EffectListing(Rom!, L2AiScripts.Kind.Spell, index);
        bool scriptAnim = L2AiScripts.Load(Rom!, L2AiScripts.Kind.Spell, index).Steps.Any(st => st.Op == 0x5A);
        AnimationNote = (scriptAnim ? "Its effect starts an animation of its own (a \"Battle animation\" step), which plays instead of this one. " : "")
            + "A monster casting it shows the same animation, aimed at the heroes.";
    }

    [RelayCommand] private void EditEffect() { if (_s != null) EditScript(L2AiScripts.Kind.Spell, _s.Index, _s.Name.Trim()); }

    /// <summary>A new spell: a copy of the picked one (record, effect as it runs now, animation, description), named
    /// "New nn". Heroes get it from a shop (Shops page), a start list (Characters page) or an event.</summary>
    [RelayCommand]
    private void AddSpell()
    {
        if (Rom == null || Main == null || _s == null) return;
        try
        {
            int id = L2Magic.AddSpell(Rom, _s.Index);
            Main.NotifyRomModified($"Lufia II: new spell {id:X2} (a copy of {_s.Name.Trim()})");
            RefreshList(id);
        }
        catch (Exception e) { System.Windows.MessageBox.Show(e.Message, "New spell", System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Warning); }
    }

    protected override bool IsField(string p) => p is nameof(Name) or nameof(Mp) or nameof(Price) or nameof(InMenu) or nameof(InBattle) or nameof(Target) or nameof(Animation);
    protected override int DescriptionEntry(int index) => L2Descriptions.SpellEntry(index);

    protected override string? Write()
    {
        if (_s == null) return null;
        _s.Name = Name; _s.Mp = (ushort)Mp; _s.Price = (ushort)Price; _s.Target = (byte)Target;
        _s.Flags = (byte)(_s.Flags & 0x3F | (InMenu ? 0x40 : 0) | (InBattle ? 0x80 : 0));
        _s.LearnBy = (byte)FlagValue(LearnBy);
        L2Battle.WriteSpell(Rom!, _s);
        if (Animation >= 0 && Animation != L2Magic.SpellAnimation(Rom!, _s.Index))
        {
            try { L2Magic.SetSpellAnimation(Rom!, _s.Index, Animation); }
            catch (Exception e) { System.Windows.MessageBox.Show(e.Message, "Animation", System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Warning); }
            _s = L2Battle.ReadSpell(Rom!, _s.Index);   // the first change moves the spells (L2Magic.Install)
        }
        return _s.Name;
    }
}

// ------------------------------------------------------------------------------------------- IP attacks

public partial class L2IpPage : L2Page
{
    private L2Battle.IpAttack? _a;
    [ObservableProperty] private string _name = "";
    [ObservableProperty] private int _cost, _animation, _target, _cursor;
    [ObservableProperty] private string _effectText = "";
    [ObservableProperty] private string _effectNote = "";
    [ObservableProperty] private bool _isShared;
    [ObservableProperty] private string _itemsText = "";
    [ObservableProperty] private string _countText = "";
    [ObservableProperty] private List<L2Option> _targetOptions = new();
    [ObservableProperty] private List<L2Option> _cursorOptions = new();
    [ObservableProperty] private List<L2Option> _animationOptions = new();
    public int NameMax => L2Magic.IpNameLength;

    protected override void OnListRead() => AnimationOptions = L2Magic.Animations(Rom!).Select(a => new L2Option(a.Value, a.Name)).ToList();

    protected override IEnumerable<string> ReadNames()
    {
        var all = L2Battle.IpAttacks(Rom!);
        CountText = $"Lufia II's IP attacks ({all.Count}; the game has 168, it can have {L2Magic.MaxIp}). A hero uses the IP attack of a piece of equipment (the Items page gives one to any item).";
        return all.Select(a => $"{a.Index:X2}  {a.Name}" + (a.Index >= L2Battle.IpCountOriginal ? "  (new)" : ""));
    }

    protected override void Load(int index)
    {
        _a = L2Battle.ReadIp(Rom!, index);
        Name = _a.Name; Cost = _a.Cost;
        if (AnimationOptions.All(o => o.Value != _a.Animation)) AnimationOptions = AnimationOptions.Append(new L2Option(_a.Animation, $"Animation {_a.Animation:X2}")).ToList();
        Animation = _a.Animation;
        TargetOptions = L2Choices.With(L2Magic.Targets, _a.Targeting); Target = _a.Targeting;
        CursorOptions = L2Choices.With(L2Magic.Cursors, _a.Cursor); Cursor = _a.Cursor;
        EffectText = EffectListing(Rom!, L2AiScripts.Kind.IpAttack, index);
        var sharing = L2AiScripts.IpAttacksWithEffect(Rom!, _a.Effect).Where(i => i != index).ToList();
        IsShared = sharing.Count > 0;
        EffectNote = sharing.Count > 0
            ? $"This effect is shared with {string.Join(", ", sharing.Take(6).Select(i => L2Battle.ReadIp(Rom!, i).Name))}{(sharing.Count > 6 ? $" and {sharing.Count - 6} more" : "")}: a change applies to them too. \"Use its own copy\" lets this one change alone."
            : "Its effect is its own.";
        var items = L2Battle.Items(Rom!).Where(it => (it.Values.TryGetValue(0x8000, out var v) ? v : L2AiScripts.ItemAddedWords(Rom!, it.Index).GetValueOrDefault(0x8000)) == index + 1)
            .Select(it => it.Name.Trim()).ToList();
        ItemsText = items.Count == 0 ? "No item gives it yet: pick an item on the Items page and choose this attack under \"IP attack\"."
            : "Given by: " + string.Join(", ", items.Take(10)) + (items.Count > 10 ? $" and {items.Count - 10} more" : "") + ".";
    }

    [RelayCommand] private void EditEffect() { if (_a != null) EditScript(L2AiScripts.Kind.IpAttack, _a.Index, _a.Name); }

    [RelayCommand]
    private void OwnEffect()
    {
        if (Rom == null || Main == null || _a == null) return;
        try
        {
            L2Magic.GiveIpOwnEffect(Rom, _a.Index);
            Main.NotifyRomModified($"Lufia II: {_a.Name} has its own effect");
            int i = SelectedIndex;
            Loading = true;
            try { Load(i); } finally { Loading = false; }
        }
        catch (Exception e) { System.Windows.MessageBox.Show(e.Message, "Own effect", System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Warning); }
    }

    /// <summary>A new IP attack: a copy of the picked one (its numbers, description and effect as it runs now, as an effect
    /// of its own), named "New attack".</summary>
    [RelayCommand]
    private void AddIp()
    {
        if (Rom == null || Main == null || _a == null) return;
        try
        {
            int id = L2Magic.AddIp(Rom, _a.Index);
            Main.NotifyRomModified($"Lufia II: new IP attack {id:X2} (a copy of {_a.Name})");
            RefreshList(id);
        }
        catch (Exception e) { System.Windows.MessageBox.Show(e.Message, "New IP attack", System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Warning); }
    }

    protected override bool IsField(string p) => p is nameof(Name) or nameof(Cost) or nameof(Animation) or nameof(Target) or nameof(Cursor);
    protected override int DescriptionEntry(int index) => Rom == null ? L2Descriptions.IpBase + 1 + index : L2Descriptions.IpEntry(Rom, index);

    protected override string? Write()
    {
        if (_a == null) return null;
        if (Name.Trim() != _a.Name)
        {
            try { L2Magic.SetIpName(Rom!, _a.Index, Name); }
            catch (Exception e) { System.Windows.MessageBox.Show(e.Message, "Name", System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Warning); }
            _a = L2Battle.ReadIp(Rom!, _a.Index);
        }
        _a.Cost = (byte)Math.Clamp(Cost, 0, 255); _a.Animation = (byte)Animation; _a.Targeting = (byte)Target; _a.Cursor = (byte)Cursor;
        L2Battle.WriteIp(Rom!, _a);
        return _a.Name + " (IP)";
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

// (the Encounters page is in L2EncountersPage.cs)


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
            Names.Add($"{s.Id:X2}  {what}{where}" + (s.Id > L2Shops.LastOriginal ? "  (added)" : ""));
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
        if (TableGuard.LockReason(_rom, "Shops") is { } locked) { System.Windows.MessageBox.Show(locked, "Shops"); return; }
        for (int i = 0; i < sec.Entries.Count; i++) sec.Entries[i] = ui.Slots[i].Value;
        L2Shops.WriteSection(_rom, sec);
        _main.NotifyRomModified($"Lufia II: shop {shop.Id:X2}");
    }
}
