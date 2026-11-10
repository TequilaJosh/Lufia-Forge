using System.Collections.ObjectModel;
using System.Windows.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using LufiaForge.Core.Lufia2;

namespace LufiaForge.Modules.Lufia2;

/// <summary>A line the Characters section points at: of the open event, or of the map's start-up script.</summary>
public sealed record L2CastLink(string Label, int Line, bool StartUp, string Tip);

/// <summary>One character of the map in the Characters section: its sprite, where the start-up script makes it, where it
/// stands, and the lines of the open event that use it.</summary>
public sealed class L2CastRowVm
{
    public int Id { get; init; }
    public string Name { get; init; } = "";
    public BitmapSource? Picture { get; init; }
    public string Made { get; init; } = "";
    public string Uses { get; init; } = "";
    public bool InEvent { get; init; }
    /// <summary>The event can change its sprite (the editor can edit and it isn't the start-up script).</summary>
    public bool CanSprite { get; init; }
    /// <summary>A map character the start-up script makes once: its "When..." button sets the story flag it needs.</summary>
    public bool CanWhen { get; init; }
    public double Dim => InEvent ? 1.0 : 0.6;
    public List<L2CastLink> Links { get; init; } = new();
}

public partial class L2ScriptEditor
{
    /// <summary>The map's characters: the ones the event uses first, then the others.</summary>
    public ObservableCollection<L2CastRowVm> Cast { get; } = new();
    [ObservableProperty] private string _castHeader = "";

    /// <summary>The Characters section, read again from the map and the event's steps as they are now.</summary>
    private void FillCast()
    {
        Cast.Clear();
        CastHeader = "";
        var rom = _rom;
        if (rom == null || _map < 0 || _event == null || _event.PuzzleOps != null || _script == null) return;
        bool here = _event.IsInit;

        // the start-up script's creates (68 / 7B), the character starts and the story characters that stand here
        List<L2MapInfo.Create> creates;
        try { creates = L2MapInfo.Stages(rom, _map); } catch (Exception) { creates = new(); }
        var starts = new Dictionary<int, byte[]>();
        foreach (var (_, b) in L2MapInfo.Records(rom, _map, L2MapInfo.Characters, 8)) starts.TryAdd(0x4F + b[0], b);

        // what the event's steps do with each character: (line, what)
        var uses = new Dictionary<int, List<(int Line, string What)>>();
        void Use(int id, int line, string what)
        {
            if (id < 0 || id == 0xFF) return;
            if (!uses.TryGetValue(id, out var l)) uses[id] = l = new();
            if (!l.Any(u => u.Line == line)) l.Add((line, what));
        }
        // a talk: the one talked to starts it, and "the person talked to" and questions speak as it
        int talkedTo = _event.Entry is { Head: L2EventBlocks.TalkList } te ? te.Id : -1;
        if (talkedTo >= 0 && _script.Steps.Count > 0) Use(talkedTo, 0, "talked to");
        for (int i = 0; i < _script.Steps.Count; i++)
        {
            var s = _script.Steps[i];
            if (s.IsText) { Use(s.Op is 0x08 or 0x10 && talkedTo >= 0 ? talkedTo : L2Cutscene.SpeakerActor(s.Bytes), i, "speaks"); continue; }
            var def = L2EventCommands.Find(s.Op);
            if (def == null) continue;
            if (s.Op == 0x34 && s.Bytes.Length >= 3) { Use(s.Bytes[1], i, $"looks like sprite {L2Sprites.Name(L2Sprites.ToEditor(rom, _map, s.Bytes[2]))}"); continue; }
            if (s.Op is 0x68 or 0x7B && s.Bytes.Length >= 3) { Use(s.Bytes[1], i, "made"); continue; }
            foreach (var p in def.FieldsFor(s.Bytes))
                if (p.Kind is L2Kind.Who or L2Kind.WhoOrAll)
                    try { Use(p.Get(s.Bytes), i, ""); } catch (Exception) { }
        }

        var ids = new HashSet<int>(creates.Select(c => c.Id));
        ids.UnionWith(starts.Keys.Where(id => creates.Any(c => c.Id == id) || _persistent.Any(p => p.Id == id) || uses.ContainsKey(id)));
        ids.UnionWith(_persistent.Select(p => p.Id));
        ids.UnionWith(uses.Keys);

        var rows = new List<L2CastRowVm>();
        foreach (int id in ids)
        {
            var links = new List<L2CastLink>();
            var made = new List<string>();
            int sprite = _who.TryGetValue(id, out var w) ? w.Sprite : -1;
            if (id < 7)
                made.Add(id == 0 ? "The party's leader: always there." : "A hero: there while in the party.");
            foreach (var c in creates.Where(c => c.Id == id))
            {
                if (sprite < 0) sprite = c.Sprite;
                made.Add($"Made on start-up line {c.Step + 1} with sprite {L2Sprites.Name(c.Sprite)}" + (c.When.Length > 0 ? $" (only while {c.When})" : "") + ".");
                if (c.Step >= 0)
                    links.Add(new L2CastLink($"Start-up line {c.Step + 1}", c.Step, true,
                        here ? "Go to the line that makes it" : "Open the map's start-up script at the line that makes it (◀ Back comes here again)"));
            }
            var places = _persistent.Where(p => p.Id == id).ToList();
            if (places.Count > 0)
            {
                if (sprite < 0) sprite = places[0].Sprite;
                string Place(L2Persistent.Place p) => $"start {p.Spot}" + (starts.TryGetValue(0x4F + p.Spot, out var at) ? $" ({at[1]}, {at[2]})" : "") +
                    (p.From < 0 ? " from a new game on" : $" once {(p.From < _maps.Length ? _maps[p.From] : $"map {p.From:X2}")} sends it");
                made.Add($"A story character with sprite {L2Sprites.Name(places[0].Sprite)}: the game puts it on its map itself (a new game, or \"Send a story character to another map\"), " +
                         "so the start-up script doesn't make it. Here: " + string.Join("; ", places.Select(Place)) + ".");
            }
            if (id == talkedTo) made.Add("The one talked to: this event plays when the party talks to it.");
            if (id >= 0x50 && !creates.Any(c => c.Id == id))
                made.Add("No \"Add a map character\" for it in the start-up script, so the game doesn't make it here (steps that use it do nothing).");
            if (starts.TryGetValue(id, out var st))
                made.Add($"Stands at ({st[1]}, {st[2]})" + (st[5] - st[3] > 1 || st[6] - st[4] > 1 ? $", walks in ({st[3]}, {st[4]})-({st[5] - 1}, {st[6] - 1})." : ", standing still."));
            if (_cast.Contains(id)) made.Add("Made for cutscenes: hidden until a step makes it appear.");

            string usesText = "";
            if (uses.TryGetValue(id, out var u))
            {
                var parts = u.Select(x => x.What.Length > 0 ? $"line {x.Line + 1} ({x.What})" : $"line {x.Line + 1}").ToList();
                usesText = "In this " + (here ? "script" : "event") + ": " + (parts.Count > 12 ? string.Join(", ", parts.Take(12)) + $" and {parts.Count - 12} more" : string.Join(", ", parts)) + ".";
                foreach (var x in u.Where(x => x.What.StartsWith("looks like")).Concat(u.Where(x => !x.What.StartsWith("looks like")).Take(1)).OrderBy(x => x.Line))
                    links.Add(new L2CastLink(x.What.StartsWith("looks like") ? $"Line {x.Line + 1}: sprite" : $"Line {x.Line + 1}", x.Line, false,
                        x.What.StartsWith("looks like") ? "Go to the line that changes its sprite" : "Go to the first line that uses it"));
            }
            rows.Add(new L2CastRowVm
            {
                Id = id,
                Name = $"{id:X2}  {(id < 7 ? LongName(id) : Name(id))}",
                Picture = sprite >= 0 ? SpritePicture(sprite) : null,
                Made = string.Join(" ", made),
                Uses = usesText,
                InEvent = u != null,
                CanSprite = CanEdit && !here && id != 0xFF,
                CanWhen = CanEdit && id >= 0x50 && creates.Count(c => c.Id == id) == 1,
                Links = links,
            });
        }
        int inEvent = rows.Count(r => r.InEvent);
        foreach (var r in rows.OrderBy(r => r.InEvent ? 0 : 1).ThenBy(r => r.InEvent ? uses[r.Id].Min(x => x.Line) : r.Id)) Cast.Add(r);
        int onMap = rows.Count(r => r.Id >= 7);
        CastHeader = $"Characters: {onMap} on this map, {inEvent} in this {(here ? "script" : "event")}";
    }

    /// <summary>Gives a character another sprite for this event ("Change a character's sprite", 34): put before the
    /// first step that uses it, or right after it when that step makes it appear (a command on a hidden character
    /// reaches another one in the game), else below the picked step.</summary>
    public void ChangeSpriteInEvent(int id, int sprite)
    {
        if (_rom == null || _main == null || _script == null || !CanEdit || _event?.IsInit == true) return;
        int spriteByte;
        try { spriteByte = L2Sprites.ToGame(_rom, _map, sprite, L2MapInfo.NewSpriteBytesUsed(_rom, _map, true)); }
        catch (Exception ex) { StageInfo = "Not changed: " + ex.Message; return; }
        if (sprite >= L2Sprites.GameCount) _main.NotifyRomModified($"Lufia II: map {_map:X2} shows a new sprite");
        int at = -1;
        for (int i = 0; i < _script.Steps.Count && at < 0; i++)
        {
            var s = _script.Steps[i];
            if (s.IsText) continue;
            var def = L2EventCommands.Find(s.Op);
            if (def == null) continue;
            bool uses = false;
            foreach (var p in def.FieldsFor(s.Bytes))
                if (p.Kind is L2Kind.Who or L2Kind.WhoOrAll)
                    try { if (p.Get(s.Bytes) == id) uses = true; } catch (Exception) { }
            if (!uses) continue;
            // a step that shows it (2D, or an appear action: A3 who 60 / 62 / 86) goes first
            bool shows = s.Op == 0x2D || s.Op == 0xA3 && s.Bytes.Length >= 3 && s.Bytes[2] is 0x60 or 0x62 or 0x86;
            at = shows ? i + 1 : i;
            if (shows && at < _script.Steps.Count && !_script.Steps[at].IsText && _script.Steps[at].Op == 0x2F && _script.Steps[at].Bytes.Length > 1 && _script.Steps[at].Bytes[1] == id) at++;
        }
        Insert(at, new L2Step { Bytes = new byte[] { 0x34, (byte)id, (byte)spriteByte } });
        StageInfo = $"{L2EventCommands.Cap(Name(id))} looks like {L2Sprites.Name(sprite)} from step {SelectedLine + 1} on (only in this event" +
                    (id is >= 0x10 and < 0x50 ? "; other events still show the story character's own sprite" : "") + ").";
    }

    /// <summary>Character <paramref name="id"/> is made always (<paramref name="flag"/> -1) or only while a story flag is on
    /// or off (<see cref="L2EventBlocks.SetCreateCondition"/>); the open event is read again.</summary>
    public void SetWhen(int id, int flag, bool on)
    {
        if (_rom == null || _main == null || _map < 0) return;
        if (HasPendingEdits && !SaveNow(quiet: true)) return;
        try
        {
            L2EventBlocks.SetCreateCondition(_rom, _map, id, flag, on);
            _main.NotifyRomModified($"Lufia II: map {_map:X2} character {id - 0x4F} " + (flag < 0 ? "always made" : $"made while story flag {flag:X2} is {(on ? "on" : "off")}"));
            int keep = SelectedLine;
            ReadMap();
            var items = ItemsOf(_rom, _map);
            if (_event != null) _event = _event.IsInit ? items[0] : items.FirstOrDefault(x2 => x2.Entry?.Head == _event.Entry?.Head && x2.Entry?.Id == _event.Entry?.Id) ?? _event;
            ReadScript(keep);
            MarkRead();
            StageInfo = flag < 0
                ? $"{L2EventCommands.Cap(Name(id))} is on the map always."
                : $"{L2EventCommands.Cap(Name(id))} is on the map only while story flag {flag:X2} is {(on ? "on" : "off")} (checked as the map loads: another character can stand on its place under the other flags).";
            Saved?.Invoke("character condition");
        }
        catch (Exception ex) { StageInfo = "Not changed: " + ex.Message; }
    }

    /// <summary>Hero <paramref name="hero"/> isn't in the party in this event: a new map character with the hero's sprite
    /// takes its place in every step of the event (made with the map, hidden; a "Show a character" goes before its first
    /// step unless that step makes it appear).</summary>
    public void HeroToCharacter(int hero)
    {
        if (_rom == null || _script == null || hero is < 1 or > 6 || !CanEdit) return;
        // where it first stands: the first "put" or "appears" with a place
        (int X, int Y) at = (0, 0);
        var points = L2MapInfo.Records(_rom, _map, L2MapInfo.Points, 3).ToDictionary(r => (int)r.Bytes[0], r => (r.Bytes[1], r.Bytes[2]));
        foreach (var st in _script.Steps.Where(s => !s.IsText && s.Bytes.Length >= 3 && s.Bytes[1] == hero))
        {
            if (st.Op is 0x30 or 0x98 && points.TryGetValue(st.Bytes[2], out var p)) { at = (p.Item1, p.Item2); break; }
            if (st.Op == 0x2F && st.Bytes.Length >= 4) { at = (st.Bytes[2], st.Bytes[3]); break; }
        }
        int id = AddCastMember(hero, at.X, at.Y);
        if (id < 0 || _script == null) return;
        Remember();
        int first = -1;
        for (int i = 0; i < _script.Steps.Count; i++)
        {
            var s = _script.Steps[i];
            if (s.IsText || L2EventCommands.Find(s.Op) is not { } def || s.Op is 0x2B or 0x2C) continue;
            var b = (byte[])s.Bytes.Clone();
            bool changed = false;
            foreach (var p in def.FieldsFor(b))
                if (p.Kind is L2Kind.Who or L2Kind.WhoOrAll)
                    try { if (p.Get(b) == hero) { p.Set(b, id); changed = true; } } catch (Exception) { }
            if (!changed) continue;
            s.Bytes = b;
            if (first < 0) first = i;
        }
        if (first >= 0)
        {
            var f = _script.Steps[first];
            // shown before it (the game's character commands skip a hidden character and move the leader instead; it is
            // made where it is first put, so it shows up there)
            if (f.Op is not (0x98 or 0x2D))
                _script.Steps.Insert(first, new L2Step { Bytes = new byte[] { 0x2D, (byte)id } });
        }
        Changed(true);
        StageInfo = $"In this event {L2EventCommands.HeroNames[hero]} is now map character {id - 0x4F} with {L2EventCommands.HeroNames[hero]}'s sprite " +
                    $"(made with the map, hidden until the event shows it{(at != (0, 0) ? $", standing at ({at.X}, {at.Y})" : "")}).";
    }

    /// <summary>The story flags with their names, for the "When..." window.</summary>
    public List<L2Option> FlagChoices => ChoicesOf(L2Kind.Flag);

    /// <summary>A new character for this map's cutscenes (<see cref="AddCastMember"/>) that appears on (x, y) at the
    /// picked step of this event.</summary>
    public void AddCharacterHere(int sprite, int x, int y)
    {
        bool init = _event?.IsInit == true;
        int id = AddCastMember(sprite, x, y);
        if (id < 0 || init) return;
        string made = StageInfo;
        AddPlace(id, x, y);
        StageInfo = made.Replace(" Click a tile and pick \"appears here\" to show it.", "") + $" It appears on ({x}, {y}) at step {SelectedLine + 1}.";
    }

    /// <summary>Goes to a line of the Characters section: in this event, or the start-up script opened at it.</summary>
    public void GoTo(L2CastLink link)
    {
        if (link.StartUp && _event?.IsInit != true)
        {
            OpenRequested?.Invoke(_map, -1, -1);
            if (_event?.IsInit != true) return;
        }
        SelectedLine = link.Line;
        HighlightLine(link.Line);
    }
}
