using LufiaForge.Core.Lufia2;

namespace LufiaForge.Modules.Lufia2;

// story characters (10-4E, L2Persistent) in the event editor: the "Send a story character" step's lists (every story
// character the game has, the character starts of the map it goes to), sending one away or onto a tile of this map,
// and turning a map character into a story character
public partial class L2ScriptEditor
{
    // asks the page to show a story character on the Story characters tab
    public event Action<int>? StoryCharRequested;

    // shows a story character on the Story characters tab (false = no page shows that tab here)
    public bool RequestStoryChar(int id)
    {
        if (StoryCharRequested == null) return false;
        StoryCharRequested(id);
        return true;
    }

    // every story character the game has, for the "Send a story character" step (one it doesn't have is
    // listed if the step names it)
    private List<L2Option> StoryChoices(int current)
    {
        var list = new List<L2Option>();
        var rom = _rom!;
        for (int id = L2Persistent.First; id <= L2Persistent.LastUsable; id++)
        {
            bool exists = L2Persistent.Exists(rom, id);
            if (exists || id == current)
                list.Add(new L2Option(id, $"{id:X2} {L2EventCommands.Cap(StoryLabel(id))}" + (exists ? "" : "  (not in the game: never shows up)")));
        }
        return list;
    }

    // a story character's name for lists: the user's name, the game's, else its sprite
    public string StoryLabel(int id)
    {
        if (L2Speakers.StoryName(id) is { } name) return name;
        if (_rom != null && L2Persistent.Exists(_rom, id)) return $"story character {id:X2} (sprite {L2Sprites.Name(L2Persistent.Record(_rom, id)[1])})";
        return $"story character {id:X2}";
    }

    // the character starts of map, for the "start" of a "Send a story character" step: where
    // each one is, and whose place it is
    private List<L2Option> StoryStartChoices(int map, int current)
    {
        var list = new List<L2Option>();
        var rom = _rom!;
        if (map < 0 || map >= L2Maps.Count(rom)) return list;
        HashSet<int> made;
        try { made = L2MapInfo.Stages(rom, map).Select(c => c.Id).ToHashSet(); } catch (Exception) { made = new(); }
        var story = L2Persistent.All(rom).Where(p => p.Map == map).ToList();
        foreach (var (_, charRecord) in L2MapInfo.Records(rom, map, L2MapInfo.Characters, 8))
        {
            var who = new List<string>();
            if (L2Persistent.IsAway(map, charRecord[0])) who.Add("away: outside the room, never seen");
            if (made.Contains(0x4F + charRecord[0])) who.Add($"also map character {charRecord[0]}'s place");
            var here = story.Where(p => p.Spot == charRecord[0]).Select(p => p.Id).Distinct().ToList();
            if (here.Count > 0) who.Add("story " + string.Join(", ", here.Select(i => i.ToString("X2"))));
            list.Add(new L2Option(charRecord[0], $"start {charRecord[0]} ({charRecord[1]}, {charRecord[2]})" + (who.Count > 0 ? "  " + string.Join("; ", who) : "")));
        }
        return list;
    }

    // "Send a story character" steps get their own lists
    private IEnumerable<L2Option>? StoryFieldChoices(L2Step step, L2ParamDef paramDef, int current)
    {
        if (step.IsText || step.Op != 0x35 || step.Bytes.Length < 4 || _rom == null) return null;
        if (paramDef.Kind == L2Kind.Who) return StoryChoices(current);
        if (paramDef.Kind == L2Kind.Number && paramDef.Label == "start") return StoryStartChoices(step.Bytes[2], current);
        return null;
    }

    // story character id goes away (where the game sends the ones it's done with) at the picked step,
    // gone from this map right away and seen nowhere after
    public void SendAway(int id)
    {
        if (_script == null || !CanEdit || id is < L2Persistent.First or > L2Persistent.LastUsable) return;
        Insert(-1, new L2Step { Bytes = new byte[] { 0x35, (byte)id, L2Persistent.AwayMap, L2Persistent.AwaySpot } });
        StageInfo = $"{L2EventCommands.Cap(Name(id))} is sent away at step {SelectedLine + 1}: gone from this map at once, and from then on on no map the party sees " +
                    "(the game does this with Tia when she joins). A later \"Send a story character\" step brings it back.";
    }

    // a story character (id, or -1 for a new one with sprite) stands on (x, y) of this map from the picked step on.
    // that's a character start there (one gets made if none is free) and a "Send a story character" step
    public void SendStoryHere(int id, int sprite, int x, int y)
    {
        if (_rom == null || _main == null || _script == null || !CanEdit || _map < 0) return;
        // reading the map again below reads the open event again, so unsaved steps get saved first
        if (HasPendingEdits && !SaveNow(quiet: true)) return;
        var rom = _rom;
        int spot;
        bool made = id < 0;
        try
        {
            (id, spot) = L2Rom.AllOrNothing(rom, () =>
            {
                int storyId = id < 0 ? L2Persistent.New(rom, sprite) : id;
                return (storyId, L2Persistent.StartAt(rom, _map, x, y));
            });
        }
        catch (Exception ex) { StageInfo = "Not added: " + ex.Message; return; }
        _main.NotifyRomModified(made ? $"Lufia II: new story character {id:X2}" : $"Lufia II: map {_map:X2} character start for story character {id:X2}");
        int keep = SelectedLine;
        ReadMap();
        var items = ItemsOf(rom, _map);
        if (_event != null) _event = _event.IsInit ? items[0] : items.FirstOrDefault(item => item.Entry?.Head == _event.Entry?.Head && item.Entry?.Id == _event.Entry?.Id) ?? _event;
        ReadScript(keep);
        MarkRead();
        // (the page reads its lists again on Saved, which loads the event again if it has unsaved steps, so this goes before the step)
        Saved?.Invoke("story character");
        Insert(-1, new L2Step { Bytes = new byte[] { 0x35, (byte)id, (byte)_map, (byte)spot } });
        StageInfo = $"{(made ? "New story character" : "Story character")} {id:X2} ({StoryLabel(id)}) stands on ({x}, {y}) from step {SelectedLine + 1} on, " +
                    "from the next time this map loads (the game doesn't put a story character on the map being shown; to have it here in this scene, " +
                    "use a map character for the scene: \"Add a character…\"). Name it on the Story characters tab.";
    }

    // the place a "Send a story character" step sends to, picked on that map (the window of the "Go to
    // another map" steps): a character start there (made if none is free on that tile)
    public void PickStoryPlace(L2FieldVm field, int map, int x, int y)
    {
        if (_rom == null || _main == null || field.Step.IsText || field.Step.Op != 0x35) return;
        try
        {
            var before = L2MapInfo.Records(_rom, map, L2MapInfo.Characters, 8).Count();
            int spot = L2Persistent.StartAt(_rom, map, x, y);
            bool made = L2MapInfo.Records(_rom, map, L2MapInfo.Characters, 8).Count() > before;
            if (made) _main.NotifyRomModified($"Lufia II: map {map:X2} character start {spot}");
            _placeField = field;
            _choices.Clear();
            Finish(b => { b[2] = (byte)map; b[3] = (byte)spot; }, $"map {map:X2} start {spot} ({x}, {y})" + (made ? ", a new start" : ""));
        }
        catch (Exception ex) { StageInfo = "Not written: " + ex.Message; }
    }

    // map character charId becomes a story character (L2Persistent.MakeStory)
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
            var entry = _event.Entry;
            int want = entry?.Head == L2EventBlocks.TalkList && entry.Id == charId ? id : entry?.Id ?? -1;
            _event = _event.IsInit ? items[0] : items.FirstOrDefault(item => item.Entry?.Head == entry?.Head && item.Entry?.Id == want) ?? _event;
        }
        ReadScript(keep);
        MarkRead();
        StageInfo = $"Map character {charId - 0x4F} is now story character {id:X2}: a new game puts it here, on its start, and the game remembers where it is. " +
                    "\"Send away\" or a \"Send a story character\" step moves it; give it a name on the Story characters tab.";
        Saved?.Invoke("story character");
    }
}
