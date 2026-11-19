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

// a flag (one bit of a byte) as a check box
public partial class L2Flag : ObservableObject
{
    public string Label { get; init; } = "";
    public int Bit { get; init; }
    [ObservableProperty] private bool _isChecked;
    public Action? Changed { get; set; }
    partial void OnIsCheckedChanged(bool value) => Changed?.Invoke();
}

// an optional word of an item (a stat bonus, the IP attack, ...), only the words the item has
public partial class L2Word : ObservableObject
{
    public string Label { get; init; } = "";
    public int Bit { get; init; }
    // the record has this word (else Lufia Forge can add it, see L2AiScripts.SetItemAddedWord)
    public bool InRecord { get; init; }
    [ObservableProperty] private int _value;
    [ObservableProperty] private bool _has;
    [ObservableProperty] private string _note = "";
    public Func<int, string>? Describe { get; init; }
    public Action? Changed { get; set; }
    public bool CanSwitch => !InRecord;
    // picked from a list (the IP attack by its name) instead of typed as a number
    public List<L2Option>? Options { get; init; }
    public bool IsChoice => Options != null;
    public bool IsNumber => Options == null;
    partial void OnValueChanged(int value) { Note = Describe?.Invoke(value) ?? ""; if (!Has && !InRecord) Has = true; else Changed?.Invoke(); }
    partial void OnHasChanged(bool value) => Changed?.Invoke();
}

public sealed record L2Option(int Value, string Label);

// base of the Lufia II pages: a list of records, the picked one's fields are shown, and an edit gets written to
// the ROM right away (Edit > Undo takes it back)
public abstract partial class L2Page : ObservableObject
{
    protected RomBuffer? Rom;
    protected MainViewModel? Main;
    protected bool Loading;

    // a number typed for a byte / word field, kept in its range (300 used to go in as 44, 70000 as 4464, -1 as 65535,
    // while the box still showed what was typed)
    protected static byte B(int value) => (byte)Math.Clamp(value, 0, 0xFF);
    protected static ushort W(int value) => (ushort)Math.Clamp(value, 0, 0xFFFF);

    // shows values again without writing (after a value was kept in its range)
    protected void ShowBack(Action set)
    {
        bool wasLoading = Loading;
        Loading = true;
        try { set(); } finally { Loading = wasLoading; }
    }
    // the ROM's RomBuffer.Version when the page last read it (or wrote it itself)
    private int _readVersion = -1;

    // the page has what the ROM holds now (after reading it, or after a write of its own)
    protected void MarkRead() { if (Rom != null) _readVersion = Rom.Version; }

    // for a card in the view that writes the ROM itself (the new statuses): the ROM, the record shown, and
    // a change to report (Edit, Undo)
    public RomBuffer? PageRom => Rom;
    public int PageIndex => SelectedIndex;
    public void ReportChange(string what)
    {
        if (Main == null) return;
        Main.NotifyRomModified("Lufia II: " + what);
        // the rest of the page shows the record again (its scripts' listings)
        int shownIndex = SelectedIndex;
        Loading = true;
        try { Load(shownIndex); } finally { Loading = false; }
        MarkRead();
        // the other cards show the record again too (a change in one can put in code another reads, like the status patch)
        Reloads++;
        OnPropertyChanged(nameof(Reloads));
    }

    // counts ReportChange calls, the cards in the view build again when it changes
    public int Reloads { get; private set; }

    // the ROM changed since the page read it (another page, Undo, a patch)
    protected bool ChangedSinceRead => Rom != null && Rom.Version != _readVersion;

    // the page is shown. if the ROM changed since it read it, the list and the record shown get read again,
    // keeping the pick. a page with edits not in the ROM yet keeps them
    public virtual void Shown()
    {
        if (!ChangedSinceRead) return;
        if (this is IPendingEdits { HasPendingEdits: true }) return;
        RefreshList(SelectedIndex < 0 ? 0 : SelectedIndex);
        MarkRead();
    }

    public ObservableCollection<string> Names { get; } = new();
    [ObservableProperty] private int _selectedIndex = -1;
    [ObservableProperty] private bool _isEditable;
    // why the page is read-only in this ROM (another hack moved a table it edits), or ""
    [ObservableProperty] private string _lockNote = "";

    // the page's name for TableGuard
    protected virtual string PageName => GetType().Name switch
    {
        "L2ItemsPage" => "Items", "L2SpellsPage" => "Spells", "L2IpPage" => "IP attacks", "L2MonstersPage" => "Monsters",
        "L2CharactersPage" => "Characters", "L2CapsulesPage" => "Capsule monsters", "L2ChestsPage" => "Treasure", "L2ExitsPage" => "Exits",
        "L2EventsPage" => "Events", "L2GraphicsPage" => "Graphics", "L2MusicPage" => "Music", _ => "",
    };
    // the picked record's description (items, spells, IP attacks), new lines as line breaks
    [ObservableProperty] private string _description = "";
    [ObservableProperty] private string _descriptionNote = "";

    // the description entry of record index (L2Descriptions), or -1
    protected virtual int DescriptionEntry(int index) => -1;
    public bool HasDescription => DescriptionEntry(0) >= 0;

    public void SetRom(RomBuffer rom, MainViewModel main)
    {
        Rom = rom; Main = main;
        OnListRead();
        Names.Clear();
        foreach (var name in ReadNames()) Names.Add(name);
        LockNote = TableGuard.LockReason(rom, PageName) ?? "";
        IsEditable = Names.Count > 0 && LockNote.Length == 0;
        SelectedIndex = -1;
        SelectedIndex = Names.Count > 0 ? 0 : -1;
        MarkRead();
    }

    // reads the list again (after a record was added) and picks select
    protected void RefreshList(int select)
    {
        if (Rom == null) return;
        Loading = true;
        try
        {
            OnListRead();
            Names.Clear();
            foreach (var name in ReadNames()) Names.Add(name);
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
            int entry = DescriptionEntry(value);
            if (entry >= 0) { Description = L2Descriptions.Read(Rom, entry).Replace("\n", Environment.NewLine); DescriptionNote = SpaceNote(); }
        }
        finally { Loading = false; }
        MarkRead();
    }

    private string SpaceNote()
    {
        var (used, room) = L2Descriptions.Space(Rom!);
        return $"Descriptions use {used} of {room} bytes; the game shows it with the X button (checked in a shop).";
    }

    private void WriteDescription()
    {
        if (Loading || Rom == null || Main == null || SelectedIndex < 0) return;
        int entry = DescriptionEntry(SelectedIndex);
        if (entry < 0) return;
        string? error = L2Descriptions.Write(Rom, entry, Description.Replace("\r\n", "\n"));
        if (error != null) { DescriptionNote = "Not written: " + error; return; }
        Main.NotifyRomModified($"Lufia II: description of {Names[SelectedIndex].Trim()}");
        MarkRead();
        DescriptionNote = SpaceNote();
    }

    protected abstract IEnumerable<string> ReadNames();
    // before the list is read for a ROM (or again after a record was added): the lists the fields pick from
    protected virtual void OnListRead() { }
    protected abstract void Load(int index);
    // writes the shown record, returns what changed (for Undo) or null
    protected abstract string? Write();

    // call from a field's change: writes the record and refreshes its line in the list
    protected void Edited()
    {
        if (Loading || Rom == null || Main == null || SelectedIndex < 0) return;
        string? what = Write();
        if (what == null) return;
        int index = SelectedIndex;
        Main.NotifyRomModified("Lufia II: " + what);
        MarkRead();
        var fresh = ReadNames().ElementAtOrDefault(index);
        if (fresh != null && fresh != Names[index]) { Loading = true; Names[index] = fresh; SelectedIndex = index; Loading = false; }
    }

    protected override void OnPropertyChanged(PropertyChangedEventArgs e)
    {
        base.OnPropertyChanged(e);
        if (e.PropertyName is nameof(SelectedIndex) or nameof(IsEditable) or nameof(Names) or nameof(DescriptionNote) or nameof(Reloads) || e.PropertyName!.StartsWith("Picture")) return;
        if (e.PropertyName == nameof(Description)) { WriteDescription(); return; }
        if (IsField(e.PropertyName)) Edited();
    }

    protected virtual bool IsField(string property) => true;

    protected static BitmapSource? Picture((int Width, int Height, uint[] Pixels)? sprite)
    {
        if (sprite is not var (w, h, pixels)) return null;
        var bitmap = BitmapSource.Create(w, h, 96, 96, PixelFormats.Bgra32, null, pixels.Select(c => (int)c).ToArray(), w * 4);
        bitmap.Freeze();
        return bitmap;
    }

    // opens the battle script editor for (kind, index), after a change the page shows the record again
    protected void EditScript(L2AiScripts.Kind kind, int index, string who)
    {
        if (Rom == null || Main == null) return;
        var window = new L2ScriptWindow(Rom, kind, index, who) { Owner = System.Windows.Application.Current?.MainWindow };
        if (window.ShowDialog() != true) return;
        Main.NotifyRomModified("Lufia II: " + window.Change);
        int shownIndex = SelectedIndex;
        Loading = true;
        try { Load(shownIndex); } finally { Loading = false; }
        MarkRead();
        // the other cards show the record again too (a change in one can put in code another reads, like the status patch)
        Reloads++;
        OnPropertyChanged(nameof(Reloads));
    }

    // a record's two battle scripts as text (what runs now, Lufia Forge's edited one or the game's)
    protected static string ScriptsText(RomBuffer rom, L2AiScripts.Kind turn, L2AiScripts.Kind hit, int index)
    {
        var names = L2AiScripts.NameLists.From(rom);
        string Part(L2AiScripts.Kind kind, string title, string none)
        {
            try
            {
                var (steps, edited) = L2AiScripts.Load(rom, kind, index);
                return title + (edited ? "  (edited in Lufia Forge)" : "") + "\n" + (steps.Count == 0 ? none : L2AiScripts.Listing(steps, names));
            }
            catch (Exception ex) { return title + "\n" + ex.Message; }
        }
        return Part(turn, "ITS TURN", "No script: it just attacks.") + "\n\n" + Part(hit, "WHEN HIT", "No script.");
    }

    // a spell's or IP attack's effect as steps (what runs now, Lufia Forge's edited one or the game's)
    protected static string EffectListing(RomBuffer rom, L2AiScripts.Kind kind, int index)
    {
        try
        {
            var (steps, edited) = L2AiScripts.Load(rom, kind, index);
            return (edited ? "(edited in Lufia Forge)\n" : "") + (steps.Count == 0 ? "No effect script." : L2AiScripts.Listing(steps, L2AiScripts.NameLists.From(rom)));
        }
        catch (Exception ex) { return ex.Message; }
    }

    protected static List<L2Flag> Flags(string[] labels, int value, Action changed) =>
        labels.Select((l, i) => new L2Flag { Label = l, Bit = 1 << i, IsChecked = (value & 1 << i) != 0, Changed = changed }).ToList();

    protected static int FlagValue(IEnumerable<L2Flag> flags) => flags.Where(f => f.IsChecked).Sum(f => f.Bit);
}

// monsters

public partial class L2MonstersPage : L2Page
{
    private L2Battle.Monster? _monster;
    public List<L2Option> ItemOptions { get; private set; } = new();

    [ObservableProperty] private string _name = "";
    [ObservableProperty] private int _level, _hp, _mp, _atp, _dfp, _agl, _int, _gut, _mgr, _exp, _gold, _sprite, _palette;
    [ObservableProperty] private int _dropItem, _dropChance;
    [ObservableProperty] private bool _hasDrop;
    [ObservableProperty] private string _scriptText = "";
    [ObservableProperty] private string _aiText = "";
    [ObservableProperty] private BitmapSource? _pictureImage;
    // the battle sprites (1 up, the game's and Lufia Forge's new ones) and the palettes of the one picked
    [ObservableProperty] private List<L2Option> _spriteOptions = new();
    [ObservableProperty] private List<L2Option> _paletteOptions = new();
    [ObservableProperty] private string _spriteNote = "";
    // how the sprite moves in battle, and whether its battles still fit the battle sprite buffer
    [ObservableProperty] private string _movesNote = "";
    [ObservableProperty] private string _roomNote = "";
    [ObservableProperty] private bool _roomWarn;
    // how many monsters the ROM has (the US game's 224, or a hack's table up to 256)
    [ObservableProperty] private string _countNote = "";
    // what to know about the picked record in a hack (a placeholder slot, a record shared by several ids, a
    // name field that also holds script bytes), or ""
    [ObservableProperty] private string _recordNote = "";
    [ObservableProperty] private int _nameMax = L2Battle.MonsterNameLength;
    [ObservableProperty] private string _newMonsterNote = "";
    [ObservableProperty] private bool _canAddMonster;

    // after a monster was added, or one's name, sprite or palette changed (so the Encounters page lists and draws it)
    public event Action? MonstersChanged;

    // a new monster: a copy of the one shown (L2NewMonsters.Add), named "New xx"
    [RelayCommand]
    private void AddMonster()
    {
        if (Rom == null || Main == null || _monster == null) return;
        int from = _monster.Index;
        string fromName = _monster.Name;
        // a hack whose monster bank is full, the monsters move to a bank of their own first (in the same step)
        L2NewMonsters.RoomResult? room = null;
        if (L2NewMonsters.NeedsRoom(Rom))
        {
            // the room is worked out on a copy of the whole ROM and put in over it, and every page reads again after,
            // so the other pages' edits not in the ROM yet go in first
            Main.WritePendingEdits();
            var oldCursor = System.Windows.Input.Mouse.OverrideCursor;
            System.Windows.Input.Mouse.OverrideCursor = System.Windows.Input.Cursors.Wait;
            try { room = L2NewMonsters.PrepareRoom(Rom); }
            finally { System.Windows.Input.Mouse.OverrideCursor = oldCursor; }
            if (room.Bytes == null)
            {
                System.Windows.MessageBox.Show(room.Preview(), "New monster", System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Warning);
                return;
            }
            if (!ConfirmRoom(room, $"Then the copy of {fromName} goes there. Make room and add it? One step: Edit, Undo takes both back.")) return;
        }
        AddNew(room);
    }

    // adds a copy of the monster shown as one Undo step, after putting a prepared (checked) room in if there is one
    public void AddNew(L2NewMonsters.RoomResult? room)
    {
        if (Rom == null || Main == null || _monster == null) return;
        int from = _monster.Index;
        string fromName = _monster.Name;
        var before = room != null ? Rom.ToArray() : null;
        try
        {
            if (room != null) L2NewMonsters.ApplyRoom(Rom, room);
            int id = L2NewMonsters.Add(Rom, from);
            Main.NotifyRomModified(room != null
                ? $"Lufia II: monsters moved to bank ${room.ToBank:X2} and new monster {id:X2} (a copy of {fromName})"
                : $"Lufia II: new monster {id:X2} (a copy of {fromName})");
            if (room != null) Main.ReloadModules();   // the tidy may have moved spells too, so every page reads again
            RefreshList(id);
            MonstersChanged?.Invoke();
            if (room != null) Main.StatusMessage = $"Monsters moved to bank ${room.ToBank:X2} (bank ${room.FromBank:X2} was full), new monster {id:X2} added (Edit, Undo takes both back).";
        }
        catch (Exception ex)
        {
            if (before != null) Rom.ReplaceAll(before);   // nothing half done
            System.Windows.MessageBox.Show(ex.Message, "New monster", System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Warning);
        }
    }

    // shows what making room in a hack does (see L2NewMonsters.PrepareRoom) and asks
    internal static bool ConfirmRoom(L2NewMonsters.RoomResult room, string question) =>
        System.Windows.MessageBox.Show($"This ROM's monster bank (${room.FromBank:X2}, set up by the hack it started from) " +
            $"{(room.RoomBefore < 0x40 ? "is full" : $"has {room.RoomBefore} bytes left")}, so the monsters move to a bank of their own.\n\n" +
            room.Preview() + "\n\n" + question, "Lufia II: room for new monsters",
            System.Windows.MessageBoxButton.OKCancel, System.Windows.MessageBoxImage.Question) == System.Windows.MessageBoxResult.OK;

    private static string Line(L2Battle.Monster monster) =>
        $"{monster.Index:X2}  " + (monster.Unused ? "(unused slot)" : monster.Name + (monster.SameAs >= 0 ? $"  (= {monster.SameAs:X2})" : ""));

    protected override IEnumerable<string> ReadNames()
    {
        ItemOptions = L2Battle.Items(Rom!).Select(i => new L2Option(i.Index, $"{i.Index:X3} {i.Name}")).ToList();
        OnPropertyChanged(nameof(ItemOptions));
        var monsters = L2Battle.Monsters(Rom!);
        var sprites = Enumerable.Range(1, L2MonsterSprites.Count(Rom!)).Select(spriteId =>
        {
            var (tilesW, tilesH, _) = L2MonsterSprites.Size(Rom!, spriteId);
            var users = monsters.Where(m => m.Sprite == spriteId).Select(m => m.Name).Distinct().ToList();
            string names = users.Count == 0 ? (L2MonsterSprites.IsNew(spriteId) ? "new" : "no monster") : string.Join(", ", users.Take(2)) + (users.Count > 2 ? ", ..." : "");
            return new L2Option(spriteId, $"{spriteId:X2} ({tilesW * 8} x {tilesH * 8}) {names}");
        }).ToList();
        // (the same list stays, so the sprite list keeps its pick when a field of the monster is written)
        if (!sprites.SequenceEqual(SpriteOptions))
        {
            SpriteOptions = sprites;
            // the list shows the pick again (a new list leaves the box empty until the value is raised again)
            bool wasLoading = Loading; Loading = true;
            try { OnPropertyChanged(nameof(Sprite)); } finally { Loading = wasLoading; }
        }
        int inUse = monsters.Count(m => !m.Unused);
        CountNote =(monsters.Count == L2Battle.MonsterCount && inUse == monsters.Count ? "Lufia II's 224 monsters." :
                        $"This ROM's monster table has {monsters.Count} entries (the US game 224): {inUse} monsters" +
                        (inUse < monsters.Count ? $", {monsters.Count - inUse} unused slots the hack points at a placeholder." : ".")) +
                    " A change is written to the ROM at once (Edit, Undo takes it back).";
        return monsters.Select(Line);
    }

    protected override void OnListRead()
    {
        bool can = L2NewMonsters.CanAdd(Rom!, out var why), needsRoom = !can && L2NewMonsters.NeedsRoom(Rom!);
        string roomText = L2NewMonsters.IsHackMoved(Rom!)
            ? $"{L2NewMonsters.Room(Rom!)} bytes free in bank ${L2NewMonsters.Bank(Rom!):X2} (a copy takes its record, about 45 bytes, and its battle scripts)."
            : $"Room for about {Math.Max(0, L2NewMonsters.Room(Rom!) / 64)} more.";
        NewMonsterNote = can
            ? $"a copy of the monster shown (record, sprite, drop and battle scripts) as monster {L2NewMonsters.NextId(Rom!):X2}; then change what you like and put it in a monster group or story battle (Encounters page). {roomText}"
            : needsRoom
                ? $"a copy of the monster shown as monster {L2NewMonsters.NextId(Rom!):X2}. This ROM's monster bank (${L2NewMonsters.Bank(Rom!):X2}, set up by the hack) is full, so the first one moves the monsters to a bank of their own (several KB free; the hack's data is tidied first). You see what moves before anything changes."
                : why;
        CanAddMonster = can || needsRoom;
    }

    // the palettes of sprite (and the note under the picture)
    private void ShowSprite(int sprite)
    {
        int paletteCount = 0;
        try { if (sprite >= 1 && sprite <= L2MonsterSprites.Count(Rom!)) paletteCount = L2MonsterSprites.PaletteCount(Rom!, sprite); } catch { }
        PaletteOptions = Enumerable.Range(0, Math.Max(1, paletteCount)).Select(p => new L2Option(p, $"Palette {p}")).ToList();
        SpriteNote = paletteCount == 0 ? "No such sprite." :
            $"Sprite {sprite:X2}: {L2MonsterSprites.Where(Rom!, sprite)}, {paletteCount} palette{(paletteCount == 1 ? "" : "s")}. Paint it, give it new palettes or make new sprites in Graphics (Monster battle sprites).";
        MovesNote = paletteCount == 0 ? "" : $"In battle it {L2MonsterSprites.MovesText(L2MonsterSprites.Size(Rom!, sprite).Moves)} (set for the sprite in Graphics, Monster battle sprites).";
        ShowRoom(sprite);
    }

    // which of the monster's groups and story battles would need more than the 64 squares of the battle
    // sprite buffer with this sprite (L2BattleRoom)
    private void ShowRoom(int sprite)
    {
        RoomNote = ""; RoomWarn = false;
        if (_monster == null || Rom == null || sprite < 1 || sprite > L2MonsterSprites.Count(Rom)) return;
        try
        {
            var tooBig = L2BattleRoom.Overflows(Rom, _monster.Index, sprite);
            var monsters = L2Battle.Monsters(Rom);
            int most = L2BattleRoom.MostWith(Rom, _monster.Index, sprite);
            var (tilesW, tilesH, _) = L2MonsterSprites.Size(Rom, sprite);
            var (squaresW, squaresH) = L2MonsterSprites.Squares(tilesW, tilesH);
            string spriteText = $"The sprite takes {squaresW * squaresH} of the {L2BattleRoom.Squares} squares of 16 x 16 that the kinds of monster in one battle share.";
            if (tooBig.Count > 0)
            {
                RoomWarn = true;
                RoomNote = spriteText + " Too big for " + string.Join("; ", tooBig.Take(6).Select(o => o.What + " (" + L2BattleRoom.Describe(o.Need, monsters) + ")")) +
                           (tooBig.Count > 6 ? $" and {tooBig.Count - 6} more" : "") + ". Pick a smaller sprite, or change those battles on the Encounters page.";
            }
            else RoomNote = spriteText + (most == 0 ? " No monster group or story battle has this monster yet." : $" Its battles need at most {most} squares: they fit.");
        }
        catch (Exception ex) { RoomNote = ex.Message; }
    }

    // after a monster sprite was added, painted or given to a monster in Graphics, the sprite list and the
    // monster shown get read again
    public void RefreshSprites()
    {
        if (Rom == null) return;
        int shownIndex = SelectedIndex;
        Loading = true;
        try
        {
            var names = ReadNames().ToList();
            if (shownIndex >= 0 && shownIndex < Names.Count)
            {
                Load(shownIndex);
                if (shownIndex < names.Count && names[shownIndex] != Names[shownIndex]) Names[shownIndex] = names[shownIndex];
            }
        }
        finally { Loading = false; }
    }

    partial void OnSpriteChanged(int value)
    {
        if (Rom == null) return;
        bool wasLoading = Loading;
        Loading = true;
        try
        {
            ShowSprite(value);
            if (Palette >= PaletteOptions.Count) Palette = 0;
        }
        finally { Loading = wasLoading; }
        if (Loading) return;
        PictureImage = Picture(L2Graphics.MonsterSprite(Rom, Sprite, Palette));
    }

    protected override void Load(int index)
    {
        if (SpriteOptions.Count != L2MonsterSprites.Count(Rom!)) ReadNames().ToList();   // sprites added in Graphics
        _monster = L2Battle.ReadMonster(Rom!, index);
        NameMax = Math.Max(1, L2Battle.NameRoom(Rom!, _monster));
        var notes = new List<string>();
        if (_monster.Unused) notes.Add("An unused slot: the hack points this id at a placeholder record that other ids share, so an edit here changes them all.");
        var sharing = L2Battle.Monsters(Rom!).Where(m => m.Index != index && m.Offset == _monster.Offset).Select(m => m.Index.ToString("X2")).ToList();
        if (!_monster.Unused && sharing.Count > 0) notes.Add($"Ids {string.Join(", ", sharing)} use the same record: an edit changes them too.");
        if (!_monster.Unused && NameMax < L2Battle.MonsterNameLength)
            notes.Add($"The name can have up to {NameMax} letter{(NameMax == 1 ? "" : "s")}: the rest of its field holds battle script bytes.");
        RecordNote = string.Join(" ", notes);
        Name = _monster.Name; Level = _monster.Level; Hp = _monster.Hp; Mp = _monster.Mp; Atp = _monster.Atp; Dfp = _monster.Dfp; Agl = _monster.Agl; Int = _monster.Int; Gut = _monster.Gut; Mgr = _monster.Mgr;
        Exp = _monster.Exp; Gold = _monster.Gold; Sprite = _monster.Sprite; ShowSprite(_monster.Sprite); Palette = _monster.Palette;
        HasDrop = _monster.DropAt > 0; DropItem = Math.Max(0, _monster.DropItem); DropChance = _monster.DropChance;
        ScriptText = HasDrop ? "" : "It drops nothing (a drop can't be added yet: the record would grow).";
        AiText = ScriptsText(Rom!, L2AiScripts.Kind.MonsterAttack, L2AiScripts.Kind.MonsterDefense, index);
        PictureImage = Picture(L2Graphics.MonsterSprite(Rom!, Sprite, Palette));
    }

    protected override bool IsField(string p) => p is not (nameof(ScriptText) or nameof(AiText) or nameof(HasDrop) or nameof(ItemOptions)
        or nameof(SpriteOptions) or nameof(PaletteOptions) or nameof(SpriteNote) or nameof(MovesNote) or nameof(RoomNote) or nameof(RoomWarn) or nameof(CountNote) or nameof(RecordNote) or nameof(NameMax)
        // (the new-monster note is set when the list is read again, after an Undo too, while the old record is still shown)
        or nameof(NewMonsterNote) or nameof(CanAddMonster));

    [RelayCommand] private void EditTurnScript() { if (_monster != null) EditScript(L2AiScripts.Kind.MonsterAttack, _monster.Index, _monster.Name); }
    [RelayCommand] private void EditHitScript() { if (_monster != null) EditScript(L2AiScripts.Kind.MonsterDefense, _monster.Index, _monster.Name); }

    protected override string? Write()
    {
        if (_monster == null) return null;
        string oldName = _monster.Name;   // (before the name typed goes in: a rename is a change the other pages show)
        _monster.Name = Name; _monster.Level = B(Level); _monster.Hp = W(Hp); _monster.Mp = W(Mp); _monster.Atp = W(Atp); _monster.Dfp = W(Dfp);
        _monster.Agl = B(Agl); _monster.Int = B(Int); _monster.Gut = B(Gut); _monster.Mgr = B(Mgr); _monster.Exp = W(Exp); _monster.Gold = W(Gold);
        ShowBack(() => { Level = _monster.Level; Hp = _monster.Hp; Mp = _monster.Mp; Atp = _monster.Atp; Dfp = _monster.Dfp; Agl = _monster.Agl; Int = _monster.Int; Gut = _monster.Gut; Mgr = _monster.Mgr; Exp = _monster.Exp; Gold = _monster.Gold; });
        byte oldSprite = _monster.Sprite, oldPalette = _monster.Palette;
        _monster.Sprite = (byte)Math.Clamp(Sprite, 1, L2MonsterSprites.Count(Rom!)); _monster.Palette = (byte)Math.Clamp(Palette, 0, 255);
        if (HasDrop) { _monster.DropItem = DropItem; _monster.DropChance = DropChance; }
        L2Battle.WriteMonster(Rom!, _monster);
        // the Encounters page shows the monster's picture and its battles' sprite room, so it reads again (after this write)
        if (oldSprite != _monster.Sprite || oldPalette != _monster.Palette || oldName != _monster.Name)
            System.Windows.Threading.Dispatcher.CurrentDispatcher.BeginInvoke(() => MonstersChanged?.Invoke());
        PictureImage = Picture(L2Graphics.MonsterSprite(Rom!, _monster.Sprite, _monster.Palette));
        return _monster.Name;
    }
}

// items

public partial class L2ItemsPage : L2Page
{
    private L2Battle.Item? _item;
    public List<L2Option> EquipTypeOptions { get; } =
        new[] { new L2Option(0, "(not equipment)") }.Concat(L2Battle.EquipTypes.Select((t, i) => new L2Option(1 << i, t))).ToList();
    public List<L2Option> IpOptions { get; private set; } = new();
    // an item's battle animation word is the animation + 1 ($81:AA3D takes 1 off, 0 plays animation 00 too)
    public List<L2Option> AnimationOptions { get; private set; } = new();

    [ObservableProperty] private string _name = "";
    [ObservableProperty] private int _price, _equipType, _icon;
    [ObservableProperty] private List<L2Flag> _useFlags = new();
    [ObservableProperty] private List<L2Flag> _equipBy = new();
    [ObservableProperty] private List<L2Word> _words = new();
    [ObservableProperty] private string _wordsText = "";
    [ObservableProperty] private string _effectText = "";
    // the item's chance to break when used (empty = the default, equipment never, other items always)
    [ObservableProperty] private string _breakText = "";
    [ObservableProperty] private string _breakNote = "";
    private string[] _ipNames = Array.Empty<string>();
    // the added words as the item had them when shown (bit -> has it, value)
    private Dictionary<int, (bool Has, int Value)> _addedAtLoad = new();

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

    private static List<L2Option> WithValue(List<L2Option> options, int value) => options.Any(o => o.Value == value) ? options : options.Append(new L2Option(value, $"Animation {value - 1:X2}")).ToList();

    [RelayCommand] private void EditMenuScript() => EditItem(L2AiScripts.Kind.ItemMenu);
    [RelayCommand] private void EditBattleScript() => EditItem(L2AiScripts.Kind.ItemBattle);
    [RelayCommand] private void EditWeaponScript() => EditItem(L2AiScripts.Kind.Weapon);
    [RelayCommand] private void EditArmorScript() => EditItem(L2AiScripts.Kind.Armor);
    private void EditItem(L2AiScripts.Kind kind) { if (_item != null) EditScript(kind, _item.Index, _item.Name.Trim()); }

    protected override void Load(int index)
    {
        _item = L2Battle.ReadItem(Rom!, index);
        Name = _item.Name; Price = _item.Price; EquipType = _item.EquipType; Icon = _item.Icon;
        UseFlags = Flags(L2Battle.ItemUse, _item.Use, Edited);
        EquipBy = Flags(L2Rom.Heroes, _item.EquipBy, Edited);
        // every bonus word, the record's own or one Lufia Forge adds (the item loader hook puts it in)
        var added = L2AiScripts.ItemAddedWords(Rom!, index);
        string Ip(int attack) => attack >= 1 && attack <= _ipNames.Length ? "" : attack == 0 ? "" : "not an IP attack";
        Words = L2Battle.ItemWords.Where(w => !w.Name.EndsWith("script") && w.Bit != 0x4000)
            .Select(itemWord =>
            {
                bool own = _item.Values.ContainsKey(itemWord.Bit);
                int value = own ? _item.Values[itemWord.Bit] : added.GetValueOrDefault(itemWord.Bit);
                var word = new L2Word { Label = itemWord.Name, Bit = itemWord.Bit, InRecord = own, Describe = itemWord.Bit == 0x8000 ? Ip : null, Changed = Edited,
                                        Options = itemWord.Bit == 0x8000 ? IpOptions : itemWord.Bit == 0x2000 ? WithValue(AnimationOptions, value) : null };
                word.Value = value; word.Has = own || added.ContainsKey(itemWord.Bit); word.Note = itemWord.Bit == 0x8000 ? Ip(value) : "";
                return word;
            }).ToList();
        _addedAtLoad = Words.Where(w => !w.InRecord).ToDictionary(w => w.Bit, w => (w.Has, w.Value));
        WordsText = "Tick a bonus to give it to the item (ATP for weapons, DFP for armor...). The IP attack is the one its wearer can use (new ones are made on the IP attacks page).";
        var names = L2AiScripts.NameLists.From(Rom!);
        var parts = new List<string>();
        foreach (var kind in new[] { L2AiScripts.Kind.ItemMenu, L2AiScripts.Kind.ItemBattle, L2AiScripts.Kind.Weapon, L2AiScripts.Kind.Armor })
        {
            var (steps, edited) = L2AiScripts.Load(Rom!, kind, index);
            if (steps.Count > 0) parts.Add(L2AiScripts.KindName(kind).ToUpperInvariant() + (edited ? "  (edited in Lufia Forge)" : "") + "\n" + L2AiScripts.Listing(steps, names));
        }
        EffectText = parts.Count > 0 ? string.Join("\n\n", parts) : "No effect yet: the buttons below give it one.";
        int own = L2ItemBreak.Own(Rom!, index);
        BreakText = own == L2ItemBreak.Default ? "" : own.ToString(System.Globalization.CultureInfo.InvariantCulture);
        BreakNote = BreakDescription(index);
    }

    private string BreakDescription(int index)
    {
        int chance = L2ItemBreak.Chance(Rom!, index);
        string now = chance >= 100 ? "used up every time" : chance <= 0 ? "never used up" : $"a {chance}% chance to break each time (in battle: \"... broke!\")";
        if (!L2ItemBreak.IsIn(Rom!))
            return $"Now: {now} (the game uses up every item a hero uses, equipment too). Empty = the default: equipment never breaks, other items always. Typing a number puts the code for it in.";
        return $"Now: {now}." + (L2ItemBreak.Own(Rom!, index) == L2ItemBreak.Default ? " Empty = the default: equipment never breaks, other items always." : " Empty goes back to the default.");
    }

    protected override bool IsField(string p) => p is nameof(Name) or nameof(Price) or nameof(EquipType) or nameof(Icon) or nameof(BreakText);
    protected override int DescriptionEntry(int index) => index;

    protected override string? Write()
    {
        if (_item == null) return null;
        _item.Name = Name; _item.Price = W(Price); _item.EquipType = B(EquipType); _item.Icon = B(Icon);
        ShowBack(() => { Price = _item.Price; Icon = _item.Icon; });
        _item.Use = (byte)FlagValue(UseFlags); _item.EquipBy = (byte)FlagValue(EquipBy);
        foreach (var word in Words)
        {
            if (word.InRecord) { _item.Values[word.Bit] = W(word.Value); if (word.Value != _item.Values[word.Bit]) { var wordCopy = word; ShowBack(() => wordCopy.Value = _item.Values[wordCopy.Bit]); } }
            // an added word only when it was changed here (the Animations page gives items an animation word too)
            else if (_addedAtLoad.GetValueOrDefault(word.Bit) != (word.Has, word.Value))
            {
                L2AiScripts.SetItemAddedWord(Rom!, _item.Index, word.Bit, word.Has ? word.Value : null);
                _addedAtLoad[word.Bit] = (word.Has, word.Value);
            }
        }
        L2Battle.WriteItem(Rom!, _item);
        WriteBreak();
        return _item.Name;
    }

    // the break chance. equipment that can be used gets the code too (else the game uses it up)
    private void WriteBreak()
    {
        string typed = BreakText.Trim().TrimEnd('%').Trim();
        int want = L2ItemBreak.Default;
        if (typed.Length > 0 && (!int.TryParse(typed, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out want) || want is < 0 or > 100))
            want = L2ItemBreak.Own(Rom!, _item!.Index);
        bool usableGear = _item!.EquipType != 0 && (_item.Use & 0xC0) != 0;
        if (L2ItemBreak.CantInstall(Rom!) == null && (want != L2ItemBreak.Own(Rom!, _item.Index) || usableGear && !L2ItemBreak.IsIn(Rom!)))
            L2ItemBreak.Set(Rom!, _item.Index, want);
        int own = L2ItemBreak.Own(Rom!, _item.Index);
        string shown = own == L2ItemBreak.Default ? "" : own.ToString(System.Globalization.CultureInfo.InvariantCulture);
        if (BreakText != shown) { bool wasLoading = Loading; Loading = true; BreakText = shown; Loading = wasLoading; }
        BreakNote = BreakDescription(_item.Index);
    }
}

// spells

// options for a target / pointer combo box: the known ones in words, plus the record's own value if the game
// uses one that has no name
internal static class L2Choices
{
    public static List<L2Option> With(IEnumerable<(int Value, string Name)> known, int current)
    {
        var options = known.Select(k => new L2Option(k.Value, k.Name)).ToList();
        if (options.All(o => o.Value != current)) options.Add(new L2Option(current, $"Other ({current:X2})"));
        return options;
    }
}

public partial class L2SpellsPage : L2Page
{
    private L2Battle.Spell? _spell;
    private int _animAtLoad = -1;
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
        _spell = L2Battle.ReadSpell(Rom!, index);
        Name = _spell.Name; Mp = _spell.Mp; Price = _spell.Price; InMenu = (_spell.Flags & 0x40) != 0; InBattle = (_spell.Flags & 0x80) != 0;
        TargetOptions = L2Choices.With(L2Magic.Targets, _spell.Target);
        Target = _spell.Target;
        int anim = L2Magic.SpellAnimation(Rom!, index);
        if (anim >= 0 && AnimationOptions.All(o => o.Value != anim)) AnimationOptions = AnimationOptions.Append(new L2Option(anim, $"Animation {anim:X2}")).ToList();
        Animation = _animAtLoad = anim;
        LearnBy = Flags(L2Rom.Heroes, _spell.LearnBy, Edited);
        EffectText = EffectListing(Rom!, L2AiScripts.Kind.Spell, index);
        bool scriptAnim = L2AiScripts.Load(Rom!, L2AiScripts.Kind.Spell, index).Steps.Any(st => st.Op == 0x5A);
        AnimationNote = (scriptAnim ? "Its effect starts an animation of its own (a \"Battle animation\" step), which plays instead of this one. " : "")
            + "A monster casting it shows the same animation, aimed at the heroes.";
    }

    [RelayCommand] private void EditEffect() { if (_spell != null) EditScript(L2AiScripts.Kind.Spell, _spell.Index, _spell.Name.Trim()); }

    // a new spell: a copy of the picked one (record, effect as it runs now, animation, description), named
    // "New nn". heroes get it from a shop (Shops page), a start list (Characters page) or an event
    [RelayCommand]
    private void AddSpell()
    {
        if (Rom == null || Main == null || _spell == null) return;
        try
        {
            int id = L2Magic.AddSpell(Rom, _spell.Index);
            Main.NotifyRomModified($"Lufia II: new spell {id:X2} (a copy of {_spell.Name.Trim()})");
            RefreshList(id);
        }
        catch (Exception ex) { System.Windows.MessageBox.Show(ex.Message, "New spell", System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Warning); }
    }

    protected override bool IsField(string p) => p is nameof(Name) or nameof(Mp) or nameof(Price) or nameof(InMenu) or nameof(InBattle) or nameof(Target) or nameof(Animation);
    protected override int DescriptionEntry(int index) => L2Descriptions.SpellEntry(index);

    protected override string? Write()
    {
        if (_spell == null) return null;
        // the record as the ROM has it now (moving the spells, from any page, gives it a new place)
        _spell = L2Battle.ReadSpell(Rom!, _spell.Index);
        _spell.Name = Name; _spell.Mp = W(Mp); _spell.Price = W(Price); _spell.Target = B(Target);
        ShowBack(() => { Mp = _spell.Mp; Price = _spell.Price; });
        _spell.Flags = (byte)(_spell.Flags & 0x3F | (InMenu ? 0x40 : 0) | (InBattle ? 0x80 : 0));
        _spell.LearnBy = (byte)FlagValue(LearnBy);
        L2Battle.WriteSpell(Rom!, _spell);
        // the animation only when it was changed here (the Animations page gives spells animations too)
        if (Animation >= 0 && Animation != _animAtLoad && Animation != L2Magic.SpellAnimation(Rom!, _spell.Index))
        {
            _animAtLoad = Animation;
            try { L2Magic.SetSpellAnimation(Rom!, _spell.Index, Animation); }
            catch (Exception ex) { System.Windows.MessageBox.Show(ex.Message, "Animation", System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Warning); }
            _spell = L2Battle.ReadSpell(Rom!, _spell.Index);   // the first change moves the spells (L2Magic.Install)
        }
        return _spell.Name;
    }
}

// IP attacks

public partial class L2IpPage : L2Page
{
    private L2Battle.IpAttack? _attack;
    private int _animAtLoad = -1;
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
        _attack = L2Battle.ReadIp(Rom!, index);
        Name = _attack.Name; Cost = _attack.Cost;
        if (AnimationOptions.All(o => o.Value != _attack.Animation)) AnimationOptions = AnimationOptions.Append(new L2Option(_attack.Animation, $"Animation {_attack.Animation:X2}")).ToList();
        Animation = _animAtLoad = _attack.Animation;
        TargetOptions = L2Choices.With(L2Magic.Targets, _attack.Targeting); Target = _attack.Targeting;
        CursorOptions = L2Choices.With(L2Magic.Cursors, _attack.Cursor); Cursor = _attack.Cursor;
        EffectText = EffectListing(Rom!, L2AiScripts.Kind.IpAttack, index);
        var sharing = L2AiScripts.IpAttacksWithEffect(Rom!, _attack.Effect).Where(i => i != index).ToList();
        IsShared = sharing.Count > 0;
        EffectNote = sharing.Count > 0
            ? $"This effect is shared with {string.Join(", ", sharing.Take(6).Select(i => L2Battle.ReadIp(Rom!, i).Name))}{(sharing.Count > 6 ? $" and {sharing.Count - 6} more" : "")}: a change applies to them too. \"Use its own copy\" lets this one change alone."
            : "Its effect is its own.";
        var items = L2Battle.Items(Rom!).Where(it => (it.Values.TryGetValue(0x8000, out var attackWord) ? attackWord : L2AiScripts.ItemAddedWords(Rom!, it.Index).GetValueOrDefault(0x8000)) == index + 1)
            .Select(it => it.Name.Trim()).ToList();
        ItemsText = items.Count == 0 ? "No item gives it yet: pick an item on the Items page and choose this attack under \"IP attack\"."
            : "Given by: " + string.Join(", ", items.Take(10)) + (items.Count > 10 ? $" and {items.Count - 10} more" : "") + ".";
    }

    [RelayCommand] private void EditEffect() { if (_attack != null) EditScript(L2AiScripts.Kind.IpAttack, _attack.Index, _attack.Name); }

    [RelayCommand]
    private void OwnEffect()
    {
        if (Rom == null || Main == null || _attack == null) return;
        try
        {
            L2Magic.GiveIpOwnEffect(Rom, _attack.Index);
            Main.NotifyRomModified($"Lufia II: {_attack.Name} has its own effect");
            int shownIndex = SelectedIndex;
            Loading = true;
            try { Load(shownIndex); } finally { Loading = false; }
        }
        catch (Exception ex) { System.Windows.MessageBox.Show(ex.Message, "Own effect", System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Warning); }
    }

    // a new IP attack: a copy of the picked one (its numbers, description and effect as it runs now, as an effect
    // of its own), named "New attack"
    [RelayCommand]
    private void AddIp()
    {
        if (Rom == null || Main == null || _attack == null) return;
        try
        {
            int id = L2Magic.AddIp(Rom, _attack.Index);
            Main.NotifyRomModified($"Lufia II: new IP attack {id:X2} (a copy of {_attack.Name})");
            RefreshList(id);
        }
        catch (Exception ex) { System.Windows.MessageBox.Show(ex.Message, "New IP attack", System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Warning); }
    }

    protected override bool IsField(string p) => p is nameof(Name) or nameof(Cost) or nameof(Animation) or nameof(Target) or nameof(Cursor);
    protected override int DescriptionEntry(int index) => Rom == null ? L2Descriptions.IpBase + 1 + index : L2Descriptions.IpEntry(Rom, index);

    protected override string? Write()
    {
        if (_attack == null) return null;
        // the record as the ROM has it now (moving the IP attacks, from any page, gives it a new place)
        _attack = L2Battle.ReadIp(Rom!, _attack.Index);
        if (Name.Trim() != _attack.Name)
        {
            try { L2Magic.SetIpName(Rom!, _attack.Index, Name); }
            catch (Exception ex) { System.Windows.MessageBox.Show(ex.Message, "Name", System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Warning); }
            _attack = L2Battle.ReadIp(Rom!, _attack.Index);
        }
        // the animation only when it was changed here (the Animations page gives IP attacks animations too)
        if (Animation != _animAtLoad) { _attack.Animation = (byte)Animation; _animAtLoad = Animation; }
        _attack.Cost = (byte)Math.Clamp(Cost, 0, 255); _attack.Targeting = (byte)Target; _attack.Cursor = (byte)Cursor;
        L2Battle.WriteIp(Rom!, _attack);
        return _attack.Name + " (IP)";
    }
}

// text

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
        for (int mapNumber = 0; mapNumber < L2Maps.Count(rom); mapNumber++)
        {
            int lineCount = L2Events.Lines(rom, mapNumber).Count;
            if (lineCount == 0) continue;
            string name = L2Maps.Name(rom, mapNumber);
            _maps.Add(mapNumber);
            MapNames.Add($"{mapNumber:X2}  {(name.Length > 0 && !name.Contains('~') ? name : "Map " + mapNumber.ToString("X2"))}  ({lineCount})");
            total += lineCount;
        }
        Status = $"{total} lines on {_maps.Count} maps.";
        SelectedMap = -1;
        SelectedMap = _maps.Count > 0 ? 0 : -1;
    }

    // the ROM's version when the lines were read (their places move when another page writes a map's events)
    private int _readVersion = -1;

    // the page is shown. if the ROM changed since the lines were read, the maps and lines get read again,
    // keeping the map and line picked (and the text being typed, if it was changed but not applied)
    public void Shown()
    {
        if (_rom == null || _main == null || _rom.Version == _readVersion) return;
        int map = SelectedMap >= 0 && SelectedMap < _maps.Count ? _maps[SelectedMap] : -1;
        var line = SelectedLine >= 0 && SelectedLine < _lines.Count ? _lines[SelectedLine] : null;
        string typed = Text;
        bool unapplied = line != null && typed.Replace("\r", "") != line.Text;
        string status = Status;
        SetRom(_rom, _main);
        int mapIndex = _maps.IndexOf(map);
        if (mapIndex < 0) return;
        if (mapIndex != SelectedMap) SelectedMap = mapIndex;
        if (line == null) return;
        int again = _lines.FindIndex(x => x.Text == line.Text && x.Speaker == line.Speaker);
        if (again < 0) return;
        SelectedLine = again;
        if (unapplied) Text = typed;
        Status = status;
    }

    partial void OnSelectedMapChanged(int value)
    {
        LineNames.Clear();
        if (_rom == null || value < 0) { _lines = new(); return; }
        _lines = L2Events.Lines(_rom, _maps[value]);
        _readVersion = _rom.Version;
        foreach (var line in _lines) LineNames.Add($"{line.Speaker}: {line.Text.Replace("\n", " ")}");
        SelectedLine = -1;
        SelectedLine = _lines.Count > 0 ? 0 : -1;
    }

    partial void OnSelectedLineChanged(int value)
    {
        if (value < 0 || value >= _lines.Count) { Text = ""; Speaker = ""; Room = ""; return; }
        var line = _lines[value];
        Speaker = $"{line.Speaker}, at {line.Offset:X6}";
        Text = line.Text;
        OnTextChanged(Text);
    }

    [ObservableProperty] private string _widthWarning = "";
    public bool HasWidthWarning => WidthWarning.Length > 0;
    partial void OnWidthWarningChanged(string value) => OnPropertyChanged(nameof(HasWidthWarning));

    partial void OnTextChanged(string value)
    {
        if (_rom == null || SelectedLine < 0 || SelectedLine >= _lines.Count) { WidthWarning = ""; return; }
        int size, room = _lines[SelectedLine].Length;
        // (while typing a tag can be half written or unknown, so the counter says so instead of the byte count)
        try { size = L2Text.Encode(_rom, value).Length; }
        catch (Exception ex) when (ex is InvalidOperationException or FormatException)
        {
            Room = ex.Message;
            WidthWarning = "";
            return;
        }
        Room = size <= room ? $"{size} of {room} bytes: written in place."
            : $"{size} bytes, {size - room} more than its place holds: Apply writes the map's events again in added space, so the line can be as long as you like.";
        WidthWarning = L2Events.WidthWarning(value.Replace("[WAIT]", "").Replace("[END]", "").Replace("[CHOICE]", ""));
    }

    [CommunityToolkit.Mvvm.Input.RelayCommand]
    private void Apply()
    {
        if (_rom == null || _main == null || SelectedLine < 0) return;
        var line = _lines[SelectedLine];
        if (!Text.EndsWith("[WAIT]") && !Text.EndsWith("[END]") && !Text.EndsWith("[CHOICE]")) { Status = "The text has to end with [WAIT], [END] or [CHOICE] like the original."; return; }
        // the line must still be where it was read (another page may have moved the map's events meanwhile)
        if (!L2Events.Lines(_rom, line.Map).Any(x => x.Offset == line.Offset && x.Length == line.Length && x.Text == line.Text))
        {
            _readVersion = -1;
            Shown();   // (the text typed stays when its line is found again)
            Status = "Not written: the map's events changed since the list was read, so it was read again. Check the line picked and press Apply again.";
            return;
        }
        string done;
        try { done = L2Events.WriteText(_rom, line, Text); }
        catch (Exception ex) { Status = "Not written: " + ex.Message; return; }
        L2FlagUses.Invalidate();
        _main.NotifyRomModified($"Lufia II: text on map {line.Map:X2}");
        // a line written elsewhere sorts to a new place in the list, so pick it again by its text
        int keep = SelectedLine;
        string written = Text.Replace("\r", "");
        OnSelectedMapChanged(SelectedMap);
        int again = _lines.FindIndex(x => x.Text == written && x.Speaker == line.Speaker);
        if (again < 0) again = _lines.FindIndex(x => x.Text == written);
        SelectedLine = again >= 0 ? again : keep;
        Status = (done.StartsWith("Written") ? done : "Written. " + done) + " (Edit > Undo takes it back.)";
    }
}


// encounters

// a choice in a list of choices (a monster in a group slot, a group in a map's list)
public partial class L2Slot : ObservableObject
{
    public string Label { get; init; } = "";
    public List<L2Option> Options { get; init; } = new();
    [ObservableProperty] private int _value;
    public Action? Changed { get; set; }
    partial void OnValueChanged(int value) => Changed?.Invoke();
}

// (the Encounters page is in L2EncountersPage.cs)
// a story battle's slot: the monster and its place on the screen
public partial class L2StorySlot : ObservableObject
{
    public string Label { get; init; } = "";
    public List<L2Option> Options { get; init; } = new();
    [ObservableProperty] private int _monster;
    [ObservableProperty] private int _x;
    [ObservableProperty] private int _y;
    public Action? Changed { get; set; }
    partial void OnMonsterChanged(int value) => Changed?.Invoke();
    partial void OnXChanged(int value) => Changed?.Invoke();
    partial void OnYChanged(int value) => Changed?.Invoke();
}



// shops

// one list of a shop (items, weapons, ...), its slots as choices
public sealed class L2ShopSection
{
    public string Title { get; init; } = "";
    public int Bit { get; init; }
    public List<L2Slot> Slots { get; init; } = new();
}

public partial class L2ShopsPage : ObservableObject
{
    private RomBuffer? _rom;
    private MainViewModel? _main;
    private bool _loading;
    private List<L2Shops.Shop> _shops = new();
    private List<L2Option> _items = new(), _spells = new();

    public ObservableCollection<string> Names { get; } = new();
    [ObservableProperty] private int _selectedIndex = -1;
    [ObservableProperty] private List<L2ShopSection> _sections = new();
    [ObservableProperty] private string _info = "";
    [ObservableProperty] private string _newShopNote = "";
    [ObservableProperty] private bool _canAdd;
    // the lists the shop has (its menu): items, weapons, armor, spells, and whether it buys
    [ObservableProperty] private bool _hasItems, _hasWeapons, _hasArmor, _hasSpells, _buys;
    [ObservableProperty] private bool _canChangeLists;

    public void SetRom(RomBuffer rom, MainViewModel main)
    {
        _rom = rom; _main = main;
        ReadChoices();
        ReadList(0);
    }

    // the items and spells a slot can hold, with their names and prices as they are now
    private void ReadChoices()
    {
        _items = L2Battle.Items(_rom!).Select(i => new L2Option(i.Index, $"{i.Index:X3} {i.Name}  ({i.Price} G)")).ToList();
        _spells = L2Battle.Spells(_rom!).Select(s => new L2Option(s.Index, $"{s.Index:X2} {s.Name}  ({s.Price} G)")).ToList();
    }

    // the ROM's version when the shops were read
    private int _readVersion = -1;

    // the page is shown. if the ROM changed since (new spells, renamed items, Undo), the shops and the
    // choices get read again, keeping the shop picked
    public void Shown()
    {
        if (_rom == null || _rom.Version == _readVersion) return;
        ReadChoices();
        ReadList(Math.Max(0, SelectedIndex));
    }

    private void ReadList(int select)
    {
        var rom = _rom!;
        _readVersion = rom.Version;
        _shops = L2Shops.All(rom);
        Names.Clear();
        foreach (var shop in _shops)
        {
            var maps = L2Shops.UsedOn(rom, shop.Id);
            string where = maps.Count > 0 ? "  " + L2Maps.Name(rom, maps[0]) : "";
            string what = string.Join(", ", (shop.IsComposite ? new[] { "second-hand" } : Array.Empty<string>()).Concat(shop.Sections.Select(x => x.Name)));
            Names.Add($"{shop.Id:X2}  {what}{where}" + (shop.Id > L2Shops.LastOriginal || L2Shops.IsAdded(rom, shop) ? "  (added)" : ""));
        }
        CanAdd = L2Shops.CanInstall(rom, out var why) && L2Shops.NextId(rom) > 0;
        NewShopNote = CanAdd
            ? $"a copy of the shop shown as shop {L2Shops.NextId(rom):X2}; then change its lists. An event opens it with \"Open a shop\" (18 {L2Shops.NextId(rom):X2}) on the Events page, for example in a character's talk event."
            : why.Length > 0 ? why : "Every id of this ROM's shop table is used.";
        CanChangeLists = L2Shops.CanInstall(rom, out _);
        SelectedIndex = -1;
        SelectedIndex = _shops.Count == 0 ? -1 : Math.Clamp(select, 0, _shops.Count - 1);
    }

    partial void OnSelectedIndexChanged(int value)
    {
        if (_rom == null || value < 0 || value >= _shops.Count) { Sections = new(); return; }
        var shop = _shops[value];
        Sections = shop.Sections.Select(sec => new L2ShopSection
        {
            Title = sec.Name.ToUpperInvariant(), Bit = sec.Bit,
            Slots = sec.Entries.Select((entry, i) => new L2Slot
            {
                Label = $"{i + 1}.", Options = sec.IsSpells ? _spells : _items, Value = entry,
                Changed = () => SlotEdited(shop, sec),
            }).ToList(),
        }).ToList();
        _loading = true;
        HasItems = (shop.Mask & 0x04) != 0; HasWeapons = (shop.Mask & 0x08) != 0; HasArmor = (shop.Mask & 0x10) != 0;
        HasSpells = (shop.Mask & 0x20) != 0; Buys = shop.Sells;
        _loading = false;
        var maps = L2Shops.UsedOn(_rom, shop.Id);
        Info = (shop.IsComposite ? "Its first list is made by the game: every item with a count in a table in RAM ($7F:F080), probably items sold earlier. It has nothing to edit. " : "") +
               (maps.Count > 0 ? $"Opened on map{(maps.Count > 1 ? "s" : "")} {string.Join(", ", maps.Select(m => $"{m:X2} {L2Maps.Name(_rom, m)}"))}. " : $"No event opens it yet: add \"Open a shop\" (18 {shop.Id:X2}) to an event on the Events page. ") +
               (shop.Sells ? "It also buys (Sell). " : "") +
               $"Each slot can be any item (or spell); + and - change how many a list has (up to {L2Shops.MaxEntries}).";
    }

    private void SlotEdited(L2Shops.Shop shop, L2Shops.Section sec)
    {
        if (_rom == null || _main == null) return;
        var shownSection = Sections.ElementAtOrDefault(shop.Sections.IndexOf(sec));
        if (shownSection == null) return;
        if (TableGuard.LockReason(_rom, "Shops") is { } locked) { System.Windows.MessageBox.Show(locked, "Shops"); return; }
        if (shownSection.Slots.Any(s => !sec.IsSpells && s.Value == 0)) { System.Windows.MessageBox.Show("Item 000 ends a list, so a slot can't hold it: take the slot away with - instead.", "Shops"); return; }
        for (int i = 0; i < sec.Entries.Count; i++) sec.Entries[i] = shownSection.Slots[i].Value;
        L2Shops.WriteSection(_rom, sec);
        _main.NotifyRomModified($"Lufia II: shop {shop.Id:X2}");
        _readVersion = _rom.Version;
    }

    // writes the shown shop anew with change applied to its lists (any length, see L2Shops.Rewrite)
    private void Rewrite(Func<Dictionary<int, List<int>>, int, int> change, string what)
    {
        if (_rom == null || _main == null || SelectedIndex < 0 || SelectedIndex >= _shops.Count) return;
        var shop = _shops[SelectedIndex];
        var lists = Sections.ToDictionary(s => s.Bit, s => s.Slots.Select(x => x.Value).ToList());
        try
        {
            int mask = change(lists, shop.Mask);
            L2Shops.Rewrite(_rom, shop.Id, shop.Look, mask, lists.ToDictionary(k => k.Key, k => (IReadOnlyList<int>)k.Value));
            _main.NotifyRomModified($"Lufia II: shop {shop.Id:X2} ({what})");
        }
        catch (Exception ex) { System.Windows.MessageBox.Show(ex.Message, "Shops", System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Warning); }
        ReadList(SelectedIndex);
    }

    private static int FirstOf(int bit) => bit == 5 ? 0 : 1;

    [RelayCommand]
    private void AddSlot(int bit) => Rewrite((lists, mask) =>
    {
        if (lists[bit].Count >= L2Shops.MaxEntries) throw new InvalidOperationException($"A list can have up to {L2Shops.MaxEntries} entries here.");
        lists[bit].Add(lists[bit].Count > 0 ? lists[bit][^1] : FirstOf(bit)); return mask;
    }, "a slot added");

    [RelayCommand]
    private void RemoveSlot(int bit) => Rewrite((lists, mask) =>
    {
        if (lists[bit].Count <= 1) throw new InvalidOperationException("A list keeps at least one entry: untick the list instead.");
        lists[bit].RemoveAt(lists[bit].Count - 1); return mask;
    }, "a slot taken away");

    partial void OnHasItemsChanged(bool value) => ListToggled(2, value);
    partial void OnHasWeaponsChanged(bool value) => ListToggled(3, value);
    partial void OnHasArmorChanged(bool value) => ListToggled(4, value);
    partial void OnHasSpellsChanged(bool value) => ListToggled(5, value);
    partial void OnBuysChanged(bool value) => ListToggled(7, value);

    private void ListToggled(int bit, bool on)
    {
        if (_loading || _rom == null) return;
        Rewrite((lists, mask) =>
        {
            if (bit < 7)
            {
                if (on && !lists.ContainsKey(bit)) lists[bit] = new List<int> { FirstOf(bit) };
                if (!on) lists.Remove(bit);
            }
            return on ? mask | 1 << bit : mask & ~(1 << bit);
        }, on ? "a list added" : "a list taken away");
    }

    // a new shop: a copy of the one shown with an id of its own (L2Shops.Add)
    [RelayCommand]
    private void AddShop()
    {
        if (_rom == null || _main == null || SelectedIndex < 0 || SelectedIndex >= _shops.Count) return;
        try
        {
            int id = L2Shops.Add(_rom, _shops[SelectedIndex].Id);
            _main.NotifyRomModified($"Lufia II: new shop {id:X2} (a copy of shop {_shops[SelectedIndex].Id:X2})");
            _shops = L2Shops.All(_rom);
            ReadList(_shops.FindIndex(s => s.Id == id));
        }
        catch (Exception ex) { System.Windows.MessageBox.Show(ex.Message, "New shop", System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Warning); }
    }
}
