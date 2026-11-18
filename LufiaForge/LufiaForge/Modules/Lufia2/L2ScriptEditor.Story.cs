using LufiaForge.Core.Lufia2;

namespace LufiaForge.Modules.Lufia2;

/// <summary>
/// Story characters (10-4E, <see cref="L2Persistent"/>) in the event editor: the "Send a story character" step's lists
/// (every story character the game has, the character starts of the map it goes to), sending one away or onto a tile
/// of this map, and turning a map character into a story character.
/// </summary>
public partial class L2ScriptEditor
{
    /// <summary>Asks the page to show a story character on the Story characters tab.</summary>
    public event Action<int>? StoryCharRequested;

    /// <summary>Shows a story character on the Story characters tab (false: no page shows that tab here).</summary>
    public bool RequestStoryChar(int id)
    {
        if (StoryCharRequested == null) return false;
        StoryCharRequested(id);
        return true;
    }

    /// <summary>Every story character the game has, for the "Send a story character" step (one it doesn't have is
    /// listed when the step names it).</summary>
    private List<L2Option> StoryChoices(int v)
    {
        var list = new List<L2Option>();
        var rom = _rom!;
        for (int id = L2Persistent.First; id <= L2Persistent.LastUsable; id++)
        {
            bool has = L2Persistent.Exists(rom, id);
            if (has || id == v)
                list.Add(new L2Option(id, $"{id:X2} {L2EventCommands.Cap(StoryLabel(id))}" + (has ? "" : "  (not in the game: never shows up)")));
        }
        return list;
    }

    /// <summary>A story character's name for lists: the user's name, the game's, else its sprite.</summary>
    public string StoryLabel(int id)
    {
        if (L2Speakers.StoryName(id) is { } n) return n;
        if (_rom != null && L2Persistent.Exists(_rom, id)) return $"story character {id:X2} (sprite {L2Sprites.Name(L2Persistent.Record(_rom, id)[1])})";
        return $"story character {id:X2}";
    }

    /// <summary>The character starts of <paramref name="map"/>, for the "start" of a "Send a story character" step: where
    /// each is, and whose place it is.</summary>
    private List<L2Option> StoryStartChoices(int map, int v)
    {
        var list = new List<L2Option>();
        var rom = _rom!;
        if (map < 0 || map >= L2Maps.Count(rom)) return list;
        HashSet<int> made;
        try { made = L2MapInfo.Stages(rom, map).Select(c => c.Id).ToHashSet(); } catch (Exception) { made = new(); }
        var story = L2Persistent.All(rom).Where(p => p.Map == map).ToList();
        foreach (var (_, b) in L2MapInfo.Records(rom, map, L2MapInfo.Characters, 8))
        {
            var who = new List<string>();
            if (L2Persistent.IsAway(map, b[0])) who.Add("away: outside the room, never seen");
            if (made.Contains(0x4F + b[0])) who.Add($"also map character {b[0]}'s place");
            var here = story.Where(p => p.Spot == b[0]).Select(p => p.Id).Distinct().ToList();
            if (here.Count > 0) who.Add("story " + string.Join(", ", here.Select(i => i.ToString("X2"))));
            list.Add(new L2Option(b[0], $"start {b[0]} ({b[1]}, {b[2]})" + (who.Count > 0 ? "  " + string.Join("; ", who) : "")));
        }
        return list;
    }

    /// <summary>"Send a story character" steps get their own lists.</summary>
    private IEnumerable<L2Option>? StoryFieldChoices(L2Step s, L2ParamDef p, int v)
    {
        if (s.IsText || s.Op != 0x35 || s.Bytes.Length < 4 || _rom == null) return null;
        if (p.Kind == L2Kind.Who) return StoryChoices(v);
        if (p.Kind == L2Kind.Number && p.Label == "start") return StoryStartChoices(s.Bytes[2], v);
        return null;
    }

    /// <summary>Story character <paramref name="id"/> goes away (where the game sends the ones it is done with) at the
    /// picked step: gone from this map at once and seen nowhere after.</summary>
    public void SendAway(int id)
    {
        if (_script == null || !CanEdit || id is < L2Persistent.First or > L2Persistent.LastUsable) return;
        Insert(-1, new L2Step { Bytes = new byte[] { 0x35, (byte)id, L2Persistent.AwayMap, L2Persistent.AwaySpot } });
        StageInfo = $"{L2EventCommands.Cap(Name(id))} is sent away at step {SelectedLine + 1}: gone from this map at once, and from then on on no map the party sees " +
                    "(the game does this with Tia when she joins). A later \"Send a story character\" step brings it back.";
    }

    /// <summary>A story character (<paramref name="id"/>; -1: a new one with <paramref name="sprite"/>) stands on
    /// (<paramref name="x"/>, <paramref name="y"/>) of this map from the picked step on: a character start there (one
    /// made when none is free) and a "Send a story character" step.</summary>
    public void SendStoryHere(int id, int sprite, int x, int y)
    {
        if (_rom == null || _main == null || _script == null || !CanEdit || _map < 0) return;
        // reading the map again below reads the open event again: unsaved steps are saved first
        if (HasPendingEdits && !SaveNow(quiet: true)) return;
        var rom = _rom;
        int spot;
        bool made = id < 0;
        try
        {
            (id, spot) = L2Rom.AllOrNothing(rom, () =>
            {
                int i = id < 0 ? L2Persistent.New(rom, sprite) : id;
                return (i, L2Persistent.StartAt(rom, _map, x, y));
            });
        }
        catch (Exception ex) { StageInfo = "Not added: " + ex.Message; return; }
        _main.NotifyRomModified(made ? $"Lufia II: new story character {id:X2}" : $"Lufia II: map {_map:X2} character start for story character {id:X2}");
        int keep = SelectedLine;
        ReadMap();
        var items = ItemsOf(rom, _map);
        if (_event != null) _event = _event.IsInit ? items[0] : items.FirstOrDefault(x2 => x2.Entry?.Head == _event.Entry?.Head && x2.Entry?.Id == _event.Entry?.Id) ?? _event;
        ReadScript(keep);
        MarkRead();
        // (the page reads its lists again on Saved, which loads the event again when it has unsaved steps: before the step)
        Saved?.Invoke("story character");
        Insert(-1, new L2Step { Bytes = new byte[] { 0x35, (byte)id, (byte)_map, (byte)spot } });
        StageInfo = $"{(made ? "New story character" : "Story character")} {id:X2} ({StoryLabel(id)}) stands on ({x}, {y}) from step {SelectedLine + 1} on, " +
                    "from the next time this map loads (the game doesn't put a story character on the map being shown; to have it here in this scene, " +
                    "use a map character for the scene: \"Add a character…\"). Name it on the Story characters tab.";
    }

    /// <summary>The place a "Send a story character" step sends to, picked on that map (the window of the "Go to
    /// another map" steps): a character start there (made when none is free on that tile).</summary>
    public void PickStoryPlace(L2FieldVm f, int map, int x, int y)
    {
        if (_rom == null || _main == null || f.Step.IsText || f.Step.Op != 0x35) return;
        try
        {
            var before = L2MapInfo.Records(_rom, map, L2MapInfo.Characters, 8).Count();
            int spot = L2Persistent.StartAt(_rom, map, x, y);
            bool made = L2MapInfo.Records(_rom, map, L2MapInfo.Characters, 8).Count() > before;
            if (made) _main.NotifyRomModified($"Lufia II: map {map:X2} character start {spot}");
            _placeField = f;
            _choices.Clear();
            Finish(b => { b[2] = (byte)map; b[3] = (byte)spot; }, $"map {map:X2} start {spot} ({x}, {y})" + (made ? ", a new start" : ""));
        }
        catch (Exception ex) { StageInfo = "Not written: " + ex.Message; }
    }

    /// <summary>Map character <paramref name="charId"/> becomes a story character (<see cref="L2Persistent.MakeStory"/>).</summary>
    public void MakeStoryCharacter(int charId)
    {
        if (_rom == null || _main == null || _map < 0 || !CanEdit) return;
        if (HasPendingEdits && !SaveNow(quiet: true)) return;
        int id;
        try { id = L2Persistent.MakeStory(_rom, _map, charId); }
        catch (Exception ex) { StageInfo = "Not changed: " + ex.Message; return; }
        _main.NotifyRomModified($"Lufia II: map {_map:X2} character {charId - 0x4F} is story character {id:X2}");
        int keep = SelectedLine;
        ReadMap();
        var items = ItemsOf(_rom, _map);
        if (_event != null)
        {
            var e = _event.Entry;
            int want = e?.Head == L2EventBlocks.TalkList && e.Id == charId ? id : e?.Id ?? -1;
            _event = _event.IsInit ? items[0] : items.FirstOrDefault(x2 => x2.Entry?.Head == e?.Head && x2.Entry?.Id == want) ?? _event;
        }
        ReadScript(keep);
        MarkRead();
        StageInfo = $"Map character {charId - 0x4F} is now story character {id:X2}: a new game puts it here, on its start, and the game remembers where it is. " +
                    "\"Send away\" or a \"Send a story character\" step moves it; give it a name on the Story characters tab.";
        Saved?.Invoke("story character");
    }
}
