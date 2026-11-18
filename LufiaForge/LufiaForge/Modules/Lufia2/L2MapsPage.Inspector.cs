using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LufiaForge.Core;
using LufiaForge.Core.Lufia2;

namespace LufiaForge.Modules.Lufia2;

public partial class L2MapsPage
{
    // what is where

    private static int ListOf(SelKind kind) => kind switch
    {
        SelKind.Exit => L2MapEdit.Exits, SelKind.Door => L2MapEdit.Doors, SelKind.Trigger => L2MapEdit.Triggers,
        SelKind.Character => L2MapEdit.Characters, SelKind.Point => L2MapEdit.Points, _ => L2MapEdit.Treasure,
    };

    // the block area a record covers (x2 / y2 not included)
    private static (int X1, int Y1, int X2, int Y2) AreaOf(SelKind kind, byte[] b) => kind switch
    {
        SelKind.Exit or SelKind.Door or SelKind.Trigger => (b[1], b[2], Math.Max(b[1] + 1, (int)b[3]), Math.Max(b[2] + 1, (int)b[4])),
        _ => (b[1], b[2], b[1] + 1, b[2] + 1),
    };

    private bool Shown(SelKind kind) => kind switch
    {
        SelKind.Exit => ShowExits, SelKind.Door => ShowDoors, SelKind.Trigger => ShowTriggers, SelKind.Character => ShowCharacters,
        SelKind.Point => ShowPoints, _ => ShowTreasure,
    };

    // what's on block (x, y), small things first
    private List<(SelKind Kind, int Index)> HitsAt(int x, int y)
    {
        var hits = new List<(SelKind, int)>();
        if (_edit == null) return hits;
        foreach (var kind in new[] { SelKind.Character, SelKind.Treasure, SelKind.Point, SelKind.Exit, SelKind.Trigger, SelKind.Door })
        {
            if (!Shown(kind)) continue;
            var records = _edit.Records(ListOf(kind));
            for (int i = 0; i < records.Count; i++)
            {
                var (x1, y1, x2, y2) = AreaOf(kind, records[i].Bytes);
                if (x >= x1 && x < x2 && y >= y1 && y < y2 && (kind != SelKind.Character || AtStoryPoint(0x4F + records[i].Bytes[0]))) hits.Add((kind, i));
            }
        }
        return hits;
    }

    private string Describe(SelKind kind, int i)
    {
        if (_edit == null) return "";
        var records = _edit.Records(ListOf(kind));
        if (i < 0 || i >= records.Count) return "";
        var b = records[i].Bytes;
        return kind switch
        {
            SelKind.Exit => $"{(_edit.IsWorld ? "entrance" : "exit")} {b[0]} to {(b[8] == 0 ? "the world map" : $"{b[8]:X2} {NameOf(b[8])}")} ({b[6]},{b[7]})",
            SelKind.Door => $"door {b[0]}",
            SelKind.Trigger => $"step-on area {b[0]}" + (_triggerIds.Contains(b[0]) ? " (plays an event: double-click to open it)" : ""),
            SelKind.Character => (_edit.Sprites.TryGetValue(0x4F + b[0], out int sprite) && L2Speakers.SpriteName(sprite) is { } who ? who + ", " : "") +
                                 $"character {b[0]} (sprite {(_edit.Sprites.TryGetValue(0x4F + b[0], out int spriteId) ? L2Sprites.Name(spriteId) : "none")})"
                                 + (_talkIds.Contains(0x4F + b[0]) ? " (talking plays an event: double-click to open it)" : ""),
            SelKind.Point => $"preset point {b[0]}",
            _ => $"{(_edit.IsHidden(b) ? "hidden item" : "chest")} {b[0]} ({(_edit.ChestOf(b[0] & 0x1F) is { } chest ? ItemName(chest.Item) : "empty")})",
        };
    }

    // selection

    [ObservableProperty] private L2Selection? _selection;
    partial void OnSelectionChanged(L2Selection? value) { DrawOverlays(); UpdateInspector(); }

    private L2Selection? Find(SelKind kind, int index) => _edit != null && index >= 0 && index < _edit.Records(ListOf(kind)).Count ? new L2Selection(kind, index) : null;

    private void SelectAt(int cx, int cy, bool doubleClick)
    {
        var hits = HitsAt(cx, cy);
        if (hits.Count == 0) { Selection = null; return; }
        // clicking the same block again gets you what's under the picked thing
        int current = Selection == null ? -1 : hits.FindIndex(h => h.Kind == Selection.Kind && h.Index == Selection.Index);
        var pick = doubleClick && current >= 0 ? hits[current] : hits[(current + 1) % hits.Count];
        if (!(Selection?.Kind == pick.Kind && Selection.Index == pick.Index)) Selection = new L2Selection(pick.Kind, pick.Index);
        if (doubleClick)
        {
            if (pick.Kind == SelKind.Exit) GoToDestination();
            else if (pick.Kind == SelKind.Character) OpenTalkEvent();
            else if (pick.Kind == SelKind.Trigger) OpenTriggerEvent();
            return;
        }
        if (pick.Kind == SelKind.Door) return;   // doors are only shown, not moved
        var bytes = _edit!.Records(ListOf(pick.Kind))[pick.Index].Bytes;
        var (x1, y1, x2, y2) = AreaOf(pick.Kind, bytes);
        _dragging = true; _dragMoved = false;
        _dragStartX = cx; _dragStartY = cy;
        _dragOriginal = bytes;
        _dragResize = pick.Kind is SelKind.Exit or SelKind.Trigger && cx == x2 - 1 && cy == y2 - 1 && (x2 - x1 > 1 || y2 - y1 > 1);
    }

    private void DragSelection(int cx, int cy)
    {
        if (_edit == null || Selection == null || _dragOriginal == null) return;
        if (!_dragMoved)
        {
            Record(_dragResize ? "resizing " + Describe(Selection.Kind, Selection.Index) : "moving " + Describe(Selection.Kind, Selection.Index)); _dragMoved = true;
            _dragStart = _history.Count > 0 ? _history[^1].State : null; _dragUnder = new(_edit.Under);
        }
        int dx = cx - _dragStartX, dy = cy - _dragStartY;
        var moved = (byte[])_dragOriginal.Clone();
        int mapW = _edit.Width, mapH = _edit.Height;
        switch (Selection.Kind)
        {
            case SelKind.Exit:
            case SelKind.Trigger:
                if (_dragResize)
                {
                    moved[3] = (byte)Math.Clamp(_dragOriginal[3] + dx, moved[1] + 1, mapW);
                    moved[4] = (byte)Math.Clamp(_dragOriginal[4] + dy, moved[2] + 1, mapH);
                }
                else
                {
                    int w = _dragOriginal[3] - _dragOriginal[1], h = _dragOriginal[4] - _dragOriginal[2];
                    int nx = Math.Clamp(_dragOriginal[1] + dx, 0, mapW - w), ny = Math.Clamp(_dragOriginal[2] + dy, 0, mapH - h);
                    moved[1] = (byte)nx; moved[2] = (byte)ny; moved[3] = (byte)(nx + w); moved[4] = (byte)(ny + h);
                    // an exit that leads to its own map keeps pointing where it did
                }
                break;
            case SelKind.Character:
            {
                int nx = Math.Clamp(_dragOriginal[1] + dx, 0, mapW - 1), ny = Math.Clamp(_dragOriginal[2] + dy, 0, mapH - 1);
                int mx = nx - _dragOriginal[1], my = ny - _dragOriginal[2];
                moved[1] = (byte)nx; moved[2] = (byte)ny;
                // the walk area moves with it
                moved[3] = (byte)Math.Clamp(_dragOriginal[3] + mx, 0, 255); moved[4] = (byte)Math.Clamp(_dragOriginal[4] + my, 0, 255);
                moved[5] = (byte)Math.Clamp(_dragOriginal[5] + mx, 0, 255); moved[6] = (byte)Math.Clamp(_dragOriginal[6] + my, 0, 255);
                break;
            }
            default:
                moved[1] = (byte)Math.Clamp(_dragOriginal[1] + dx, 0, mapW - 1); moved[2] = (byte)Math.Clamp(_dragOriginal[2] + dy, 0, mapH - 1);
                break;
        }
        _edit.SetRecord(ListOf(Selection.Kind), Selection.Index, moved);
        if (Selection.Kind == SelKind.Treasure)
        {
            _edit.SyncHiddenLooks();
            // the chest's picture goes with it (redone from the start of the drag each time, so dragging back gives the floor back)
            if (_edit.PictureLayers(_dragOriginal) != 0 && _dragStart != null && _dragUnder != null)
            {
                _edit.RestoreLayers(_dragStart, _dragUnder);
                _edit.MoveChestPicture(_dragOriginal, _dragOriginal[1], _dragOriginal[2], moved[1], moved[2]);
                DrawMap();
            }
        }
        if (Selection.Kind == SelKind.Character) DrawMap();
        DrawOverlays(); UpdateInspector(); UpdateUndoState();
    }

    private L2MapEdit.State? _dragStart;
    private Dictionary<(int X, int Y), (int Block, byte Flags)[]>? _dragUnder;

    // the inspector moving the picked treasure (and its chest picture), as one Undo step
    private void MoveTreasureTo(int x, int y)
    {
        if (_filingInspector || _edit == null || Selection is not { Kind: SelKind.Treasure } picked) return;
        var bytes = _edit.Records(L2MapEdit.Treasure)[picked.Index].Bytes;
        int nx = X(x), ny = Y(y);
        if (bytes[1] == nx && bytes[2] == ny) return;
        if (_edit.Records(L2MapEdit.Treasure).Any(r => r.Bytes[1] == nx && r.Bytes[2] == ny)) { Status = "There is treasure on that block already."; UpdateInspector(); return; }
        Record("moving " + Describe(picked.Kind, picked.Index));
        var moved = (byte[])bytes.Clone(); moved[1] = (byte)nx; moved[2] = (byte)ny;
        _edit.SetRecord(L2MapEdit.Treasure, picked.Index, moved);
        _edit.SyncHiddenLooks();
        _edit.MoveChestPicture(bytes, bytes[1], bytes[2], nx, ny);
        Refresh();
    }

    [RelayCommand]
    private void DeleteSelected()
    {
        if (_edit == null || Selection is not { } picked) return;
        if (picked.Kind == SelKind.Door) { Status = "Doors are part of the map's puzzles and keys; they are shown, not taken out."; return; }
        string what = Describe(picked.Kind, picked.Index);
        Record("taking out " + what);
        if (picked.Kind == SelKind.Treasure)
        {
            bool hidden = _edit.IsHidden(_edit.Records(L2MapEdit.Treasure)[picked.Index].Bytes);
            _edit.RemoveTreasure(picked.Index);
            Selection = null;
            Refresh();
            Status = $"Took out {what}." + (hidden ? "" : " Its block got the floor back.");
            return;
        }
        if (picked.Kind == SelKind.Character)
        {
            int id = 0x4F + _edit.Records(L2MapEdit.Characters)[picked.Index].Bytes[0];
            _edit.Sprites.Remove(id);
        }
        _edit.RemoveRecord(ListOf(picked.Kind), picked.Index);
        Selection = null;
        Refresh();
        Status = picked.Kind switch
        {
            SelKind.Character => $"Took out {what}: its creation and its talk event go when the map is written.",
            SelKind.Trigger => $"Took out {what}: its event goes when the map is written.",
            SelKind.Point => $"Took out {what}: events that put someone on it would now find nothing there.",
            _ => $"Took out {what}.",
        };
    }

    // the inspector

    [ObservableProperty] private string _selectionTitle = "Nothing picked";
    [ObservableProperty] private string _selectionDetails = "Pick something on the map with the Select tool (S).";
    [ObservableProperty] private bool _isExitSelected, _isTriggerSelected, _isCharacterSelected, _isPointSelected, _isTreasureSelected, _isDoorSelected;
    private bool _filingInspector;

    // exit
    [ObservableProperty] private int _exitX, _exitY, _exitW, _exitH, _destX, _destY;
    [ObservableProperty] private int _destMapIndex = -1;
    [ObservableProperty] private string _exitStyle = "";
    public ObservableCollection<string> DestinationChoices { get; } = new();
    // area
    [ObservableProperty] private int _areaPosX, _areaPosY, _areaSizeW, _areaSizeH;
    // character
    [ObservableProperty] private int _charX, _charY, _walkX1, _walkY1, _walkX2, _walkY2;
    [ObservableProperty] private int _charSprite = -1, _charMove = -1;
    [ObservableProperty] private System.Windows.Media.Imaging.BitmapSource? _charSpriteImage;
    public ObservableCollection<string> SpriteChoices { get; } = new();
    public ObservableCollection<string> MoveChoices { get; } = new();
    private readonly List<int> _moveValues = new();
    [ObservableProperty] private string _talkPreview = "";
    // point
    [ObservableProperty] private int _pointX, _pointY;
    // treasure
    [ObservableProperty] private int _treasureX, _treasureY;
    [ObservableProperty] private int _treasureItem = -1;
    [ObservableProperty] private bool _treasureIsGold;
    [ObservableProperty] private int _treasureGold = 100;
    [ObservableProperty] private string _treasureNote = "";
    [ObservableProperty] private string _treasureKind = "";
    public ObservableCollection<string> ItemChoices { get; } = new();

    private void FillChoices()
    {
        if (_rom == null) return;
        if (DestinationChoices.Count == 0)
            for (int mapId = 0; mapId < L2Maps.Count(_rom!); mapId++) DestinationChoices.Add($"{mapId:X2}  {NameOf(mapId)}");
        if (SpriteChoices.Count != L2Sprites.Count(_rom))
        {
            SpriteChoices.Clear();
            for (int sprite = 0; sprite < L2Sprites.Count(_rom); sprite++) SpriteChoices.Add(sprite < 7 ? $"{sprite:X2}  {L2Rom.Heroes[sprite]}" : L2Sprites.Name(sprite));
        }
        if (MoveChoices.Count == 0)
            foreach (var (mode, name) in L2EventCommands.MoveModes) { MoveChoices.Add($"{mode:X2}  {name}"); _moveValues.Add(mode); }
        if (ItemChoices.Count == 0)
            foreach (var item in L2Battle.Items(_rom)) ItemChoices.Add($"{item.Index:X3}  {item.Name.Trim()}");
    }

    private void UpdateInspector()
    {
        _filingInspector = true;
        try
        {
            var picked = Selection;
            IsExitSelected = picked?.Kind == SelKind.Exit; IsTriggerSelected = picked?.Kind == SelKind.Trigger; IsCharacterSelected = picked?.Kind == SelKind.Character;
            IsPointSelected = picked?.Kind == SelKind.Point; IsTreasureSelected = picked?.Kind == SelKind.Treasure; IsDoorSelected = picked?.Kind == SelKind.Door;
            if (_edit == null || picked == null || _rom == null) { SelectionTitle = "Nothing picked"; SelectionDetails = "Pick something on the map with the Select tool (S)."; return; }
            FillChoices();
            var records = _edit.Records(ListOf(picked.Kind));
            if (picked.Index >= records.Count) { Selection = null; return; }
            var b = records[picked.Index].Bytes;
            switch (picked.Kind)
            {
                case SelKind.Exit:
                    SelectionTitle = _edit.IsWorld ? $"Entrance {b[0]}" : $"Exit {b[0]}";
                    SelectionDetails = (_edit.IsWorld ? "Walking onto it from the world map takes the party into a map. " : "")
                        + (b[8] == 0 ? $"Leads to the world map at block ({b[6]},{b[7]})." : b[8] == _edit.MapId && _edit.IsWorld ? "It doesn't lead anywhere yet: click Pick on map... and choose the map and the place the party arrives at."
                        : $"Leads to map {b[8]:X2} {NameOf(b[8])} at block ({b[6]},{b[7]}).");
                    ExitX = b[1]; ExitY = b[2]; ExitW = Math.Max(1, b[3] - b[1]); ExitH = Math.Max(1, b[4] - b[2]);
                    DestMapIndex = b[8] < DestinationChoices.Count ? b[8] : -1; DestX = b[6]; DestY = b[7];
                    ExitStyle = b[5].ToString("X2");
                    break;
                case SelKind.Trigger:
                    SelectionTitle = $"Step-on area {b[0]}";
                    bool hasEvent = L2Events.Block(_rom, _edit.MapId) is int eventBlock && eventBlock >= 0 && L2EventBlocks.ReadLists(_rom, eventBlock).Any(e => e.Head == L2EventBlocks.TriggerList && e.Id == b[0]);
                    SelectionDetails = $"Walking onto it plays its event{(hasEvent ? "" : " (made when the map is written)")}. It plays every time the party steps onto it: a story flag makes it play once.";
                    AreaPosX = b[1]; AreaPosY = b[2]; AreaSizeW = Math.Max(1, b[3] - b[1]); AreaSizeH = Math.Max(1, b[4] - b[2]);
                    break;
                case SelKind.Character:
                {
                    int id = 0x4F + b[0];
                    SelectionTitle = $"Character {b[0]} (actor {id:X2})";
                    SelectionDetails = "Drag it to move it (its walk area moves too)." + StoryNote(id);
                    CharX = b[1]; CharY = b[2]; WalkX1 = b[3]; WalkY1 = b[4]; WalkX2 = Math.Max(b[3], b[5] - 1); WalkY2 = Math.Max(b[4], b[6] - 1);
                    CharSprite = _edit.Sprites.GetValueOrDefault(id, -1);
                    CharMove = _moveValues.IndexOf(b[7]);
                    CharSpriteImage = SpriteImage(CharSprite);
                    TalkPreview = TalkLine(id);
                    break;
                }
                case SelKind.Point:
                    SelectionTitle = $"Preset point {b[0]}";
                    SelectionDetails = "Scenes put the party (Go to another map) or characters (Put a character on a point) here.";
                    PointX = b[1]; PointY = b[2];
                    break;
                case SelKind.Treasure:
                {
                    bool hidden = _edit.IsHidden(b);
                    SelectionTitle = hidden ? $"Hidden item {b[0]}" : $"Chest {b[0]}";
                    var chest = _edit.ChestOf(b[0] & 0x1F);
                    TreasureX = b[1]; TreasureY = b[2];
                    TreasureIsGold = chest != null && L2Gold.IsGold(chest.Item);
                    TreasureGold = TreasureIsGold ? L2Gold.AmountOf(chest!.Item) : 100;
                    TreasureItem = chest == null || TreasureIsGold ? -1 : chest.Item;
                    TreasureKind = hidden ? "Hidden: nothing shows; pressing A facing this block finds the item." : "A chest: pressing A facing it opens it and gives the item.";
                    SelectionDetails = chest == null ? "No treasure record uses this spot, so it gives nothing."
                        : $"Found once: opened flag {chest.Flag:X3} remembers it (each treasure has its own){(chest.Special ? "; left out of the game's count of opened chests" : "")}.";
                    TreasureNote = (hidden ? "Drag it to move it." : "Drag it to move it: the chest's picture moves with it, and the block it stood on gets the floor it had (or the floor around it).")
                        + (L2Gold.CantInstall(_rom) is { } noGold ? " " + noGold : "");
                    break;
                }
                case SelKind.Door:
                    SelectionTitle = $"Door {b[0]}";
                    SelectionDetails = $"Covers ({b[1]},{b[2]})-({b[3] - 1},{b[4] - 1}); opens onto ({b[5]},{b[6]})-({b[7] - 1},{b[8] - 1}); key / lock byte {b[13]:X2}. Doors belong to the map's puzzles and keys and are shown here, not edited.";
                    break;
            }
        }
        finally { _filingInspector = false; }
    }

    private string TalkLine(int id)
    {
        if (_rom == null || _edit == null) return "";
        int block = L2Events.Block(_rom, _edit.MapId);
        if (block < 0) return "";
        var talk = L2EventBlocks.ReadLists(_rom, block).FirstOrDefault(x => x.Head == L2EventBlocks.TalkList && x.Id == id);
        if (talk == null) return "No talk event yet.";
        try
        {
            var textStep = L2EventScript.Read(_rom, block + talk.Offset, block).Steps.FirstOrDefault(x => x.IsText);
            string line = textStep?.Text.Replace("\n", " ") ?? "(a talk event with no text)";
            return "“" + (line.Length > 90 ? line[..90] + "..." : line) + "”";
        }
        catch { return ""; }
    }

    private System.Windows.Media.Imaging.BitmapSource? SpriteImage(int sprite)
    {
        if (_rom == null || sprite < 0 || L2Graphics.MapSprite(_rom, sprite) is not var (w, h, pixels)) return null;
        return System.Windows.Media.Imaging.BitmapSource.Create(w, h, 96, 96, System.Windows.Media.PixelFormats.Bgra32, null, pixels.Select(c => (int)c).ToArray(), w * 4);
    }

    // changes the picked record (one undo step per field change)
    private void EditRecord(string what, Action<byte[]> change)
    {
        if (_filingInspector || _edit == null || Selection is not { } picked) return;
        var current = _edit.Records(ListOf(picked.Kind))[picked.Index].Bytes;
        var changed = (byte[])current.Clone();
        change(changed);
        if (changed.AsSpan().SequenceEqual(current)) return;
        Record(what);
        _edit.SetRecord(ListOf(picked.Kind), picked.Index, changed);
        if (picked.Kind == SelKind.Treasure) _edit.SyncHiddenLooks();
        if (picked.Kind == SelKind.Character) DrawMap();
        DrawOverlays(); UpdateInspector(); UpdateUndoState();
    }

    private byte X(int value) => (byte)Math.Clamp(value, 0, Math.Max(0, (_edit?.Width ?? 1) - 1));
    private byte Y(int value) => (byte)Math.Clamp(value, 0, Math.Max(0, (_edit?.Height ?? 1) - 1));

    partial void OnExitXChanged(int value) => EditRecord("moving the exit", b => { int w = b[3] - b[1]; b[1] = X(value); b[3] = (byte)(b[1] + w); });
    partial void OnExitYChanged(int value) => EditRecord("moving the exit", b => { int h = b[4] - b[2]; b[2] = Y(value); b[4] = (byte)(b[2] + h); });
    partial void OnExitWChanged(int value) => EditRecord("resizing the exit", b => b[3] = (byte)Math.Clamp(b[1] + Math.Max(1, value), 1, _edit!.Width));
    partial void OnExitHChanged(int value) => EditRecord("resizing the exit", b => b[4] = (byte)Math.Clamp(b[2] + Math.Max(1, value), 1, _edit!.Height));
    partial void OnDestMapIndexChanged(int value) { if (value >= 0) EditRecord("changing where the exit leads", b => { b[8] = (byte)value; if (value == 0 && b[5] != 0xF0) b[5] = 0xF0; }); }
    partial void OnDestXChanged(int value) => EditRecord("changing where the exit leads", b => b[6] = (byte)Math.Clamp(value, 0, 255));
    partial void OnDestYChanged(int value) => EditRecord("changing where the exit leads", b => b[7] = (byte)Math.Clamp(value, 0, 255));
    partial void OnExitStyleChanged(string value)
    {
        if (int.TryParse(value.Trim(), System.Globalization.NumberStyles.HexNumber, null, out int style) && style is >= 0 and <= 255)
            EditRecord("changing how the party arrives", b => b[5] = (byte)style);
    }

    partial void OnAreaPosXChanged(int value) => EditRecord("moving the area", b => { int w = b[3] - b[1]; b[1] = X(value); b[3] = (byte)(b[1] + w); });
    partial void OnAreaPosYChanged(int value) => EditRecord("moving the area", b => { int h = b[4] - b[2]; b[2] = Y(value); b[4] = (byte)(b[2] + h); });
    partial void OnAreaSizeWChanged(int value) => EditRecord("resizing the area", b => b[3] = (byte)Math.Clamp(b[1] + Math.Max(1, value), 1, _edit!.Width));
    partial void OnAreaSizeHChanged(int value) => EditRecord("resizing the area", b => b[4] = (byte)Math.Clamp(b[2] + Math.Max(1, value), 1, _edit!.Height));

    partial void OnCharXChanged(int value) => EditRecord("moving the character", b => { int delta = X(value) - b[1]; b[1] = X(value); b[3] = (byte)Math.Clamp(b[3] + delta, 0, 255); b[5] = (byte)Math.Clamp(b[5] + delta, 0, 255); });
    partial void OnCharYChanged(int value) => EditRecord("moving the character", b => { int delta = Y(value) - b[2]; b[2] = Y(value); b[4] = (byte)Math.Clamp(b[4] + delta, 0, 255); b[6] = (byte)Math.Clamp(b[6] + delta, 0, 255); });
    partial void OnWalkX1Changed(int value) => EditRecord("changing where the character walks", b => b[3] = X(value));
    partial void OnWalkY1Changed(int value) => EditRecord("changing where the character walks", b => b[4] = Y(value));
    partial void OnWalkX2Changed(int value) => EditRecord("changing where the character walks", b => b[5] = (byte)(X(value) + 1));
    partial void OnWalkY2Changed(int value) => EditRecord("changing where the character walks", b => b[6] = (byte)(Y(value) + 1));
    partial void OnCharMoveChanged(int value) { if (value >= 0 && value < _moveValues.Count) EditRecord("changing how the character moves", b => b[7] = (byte)_moveValues[value]); }
    partial void OnCharSpriteChanged(int value)
    {
        if (_filingInspector || _edit == null || Selection is not { Kind: SelKind.Character } picked || value < 0) return;
        int id = 0x4F + _edit.Records(L2MapEdit.Characters)[picked.Index].Bytes[0];
        if (_edit.Sprites.GetValueOrDefault(id, -1) == value) return;
        Record("changing the character's sprite");
        _edit.Sprites[id] = value;
        CharSpriteImage = SpriteImage(value);
        DrawMap(); UpdateUndoState();
    }

    partial void OnPointXChanged(int value) => EditRecord("moving the point", b => b[1] = X(value));
    partial void OnPointYChanged(int value) => EditRecord("moving the point", b => b[2] = Y(value));
    partial void OnTreasureXChanged(int value) => MoveTreasureTo(value, TreasureY);
    partial void OnTreasureYChanged(int value) => MoveTreasureTo(TreasureX, value);

    partial void OnTreasureItemChanged(int value) { if (value >= 0) SetChestItem(value); }
    partial void OnTreasureIsGoldChanged(bool value) => SetChestItem(value ? L2Gold.Item(Math.Max(1, TreasureGold)) : 0);
    partial void OnTreasureGoldChanged(int value) { if (TreasureIsGold) SetChestItem(L2Gold.Item(Math.Max(1, value))); }

    // sets what the picked treasure holds (an item, or gold as L2Gold.EditorBase + amount)
    private void SetChestItem(int item)
    {
        if (_filingInspector || _rom == null || _edit == null || Selection is not { Kind: SelKind.Treasure } picked) return;
        if (L2Gold.IsGold(item) && L2Gold.CantInstall(_rom) is { } why) { Status = why; UpdateInspector(); return; }
        int spot = _edit.Records(L2MapEdit.Treasure)[picked.Index].Bytes[0] & 0x1F;
        var chest = _edit.ChestOf(spot);
        if (chest == null || chest.Item == item) return;
        Record("changing what the treasure holds");
        _edit.SetTreasureItem(spot, item);
        UpdateInspector(); DrawOverlays(); UpdateUndoState();
        Status = $"It now holds {ItemName(item)}.";
    }

    // going places

    [RelayCommand]
    private void GoToDestination()
    {
        if (_edit == null || Selection is not { Kind: SelKind.Exit } picked) return;
        var bytes = _edit.Records(L2MapEdit.Exits)[picked.Index].Bytes;
        int destMap = bytes[8], x = bytes[6], y = bytes[7];
        SelectedIndex = destMap;
        if (_edit != null)
        {
            AreaRect = null;
            JumpRequested?.Invoke(x * 16, y * 16);
            Status = $"Here: the exit leads to ({x},{y}).";
        }
    }

    // asks the view to scroll so map pixel (x, y) is in sight
    public event Action<double, double>? JumpRequested;

    // opens the picker window for where the exit leads
    [RelayCommand]
    private void PickDestination()
    {
        if (_rom == null || _edit == null || Selection is not { Kind: SelKind.Exit } picked) return;
        var bytes = _edit.Records(L2MapEdit.Exits)[picked.Index].Bytes;
        // an exit that still leads back to its own map: offer the world map from a town, a town from the world map
        int start = bytes[8] != _edit.MapId || !_edit.IsWorld ? bytes[8] : 3;
        if (start == 1) start = 0;
        var window = new L2PickPlaceWindow(_rom, start, bytes[6], bytes[7], NameOf) { Owner = System.Windows.Application.Current.MainWindow };
        if (window.ShowDialog() != true) return;
        EditRecord("changing where the exit leads", b => { b[8] = (byte)window.Map; b[6] = (byte)window.X; b[7] = (byte)window.Y; if (window.Map == 0 && b[5] != 0xF0) b[5] = 0xF0; });
        Status = $"{(_edit.IsWorld ? "Entrance" : "Exit")} {bytes[0]} now leads to {(window.Map == 0 ? "the world map" : $"{window.Map:X2} {NameOf(window.Map)}")} at ({window.X},{window.Y}).";
    }

    // writes the map and opens the picked character's talk event in Events (makes a short one if it has none)
    [RelayCommand]
    private void OpenTalkEvent()
    {
        if (_rom == null || _main == null || _edit == null || Selection is not { Kind: SelKind.Character } picked) return;
        int id = 0x4F + _edit.Records(L2MapEdit.Characters)[picked.Index].Bytes[0];
        if (WritePendingEdits() is { } why) { Status = "The map couldn't be written first: " + why; return; }
        int block = L2Events.Block(_rom, _edit.MapId);
        if (block < 0) { Status = "This map has no event block."; return; }
        if (!L2EventBlocks.ReadLists(_rom, block).Any(e => e.Head == L2EventBlocks.TalkList && e.Id == id))
        {
            try
            {
                L2EventBlocks.Add(_rom, _edit.MapId, L2EventBlocks.TalkList, id, L2EventBlocks.NewScript(block, new byte[] { 0x08 }, "...", -1));
                _main.NotifyRomModified($"Lufia II: talk event for character {id - 0x4F} on map {_edit.MapId:X2}");
            }
            catch (Exception ex) { Status = "No talk event could be made: " + ex.Message; return; }
        }
        OpenEventRequested?.Invoke(_edit.MapId, L2EventBlocks.TalkList, id);
    }

    [RelayCommand]
    private void OpenTriggerEvent()
    {
        if (_rom == null || _edit == null || Selection is not { Kind: SelKind.Trigger } picked) return;
        int id = _edit.Records(L2MapEdit.Triggers)[picked.Index].Bytes[0];
        if (WritePendingEdits() is { } why) { Status = "The map couldn't be written first: " + why; return; }
        OpenEventRequested?.Invoke(_edit.MapId, L2EventBlocks.TriggerList, id);
    }
}
