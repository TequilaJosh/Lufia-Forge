using LufiaForge.Core;
using LufiaForge.Core.Lufia2;

namespace LufiaForge.Modules.Lufia2;

// "go to another map" steps (16 map scene point): the scene list, plain words for what happens, and the fixes when
// the screen would stay black after the map change (no such scene there, or one that never fades in, see
// L2Cutscene.CheckArrival)
public partial class L2ScriptEditor
{
    // hero fix codes for these cards (the others are in L2LineVm.HeroFix)
    public const int FixMakeScene = -3, FixAddFadeIn = -4;

    private static bool IsGoTo(L2Step step) => !step.IsText && step.Op == 0x16 && step.Bytes.Length >= 4;

    private string MapLabel(int map) => _rom == null ? $"map {map:X2}" : $"{map:X2} {L2Maps.Name(_rom, map)}";

    // the scenes of map for a go-to step's scene field, numbered in hex like the rest of the editor, each with what it
    // says first. the test starts (F0 up, made by Test in the game) go last. when the step names a scene the map
    // hasn't got, it gets an entry that says so
    private IEnumerable<L2Option> SceneChoices(int map, int current)
    {
        var list = new List<L2Option>();
        int block = _rom == null ? -1 : L2Events.Block(_rom, map);
        if (block >= 0)
        {
            List<L2EventBlocks.Entry> scenes;
            try { scenes = L2EventBlocks.ReadLists(_rom!, block).Where(e => e.Head == L2EventBlocks.SceneList).ToList(); }
            catch (Exception) { scenes = new(); }
            foreach (var scene in scenes.OrderBy(e => e.Id >= 0xF0).ThenBy(e => e.Id))
            {
                string first = Short(FirstText(_rom!, block + scene.Offset, block));
                string what = scene.Id >= 0xF0 ? "a test start (made by Test in the game)"
                    : L2Cutscene.CheckArrival(_rom!, map, scene.Id) == L2Cutscene.ArrivalTrouble.NoFadeIn ? "never fades in, the screen would stay black"
                    : first.Length > 0 ? $"\"{first}\"" : "no text";
                list.Add(new L2Option(scene.Id, $"scene {scene.Id:X2}: {what}"));
            }
        }
        if (current >= 0 && list.All(o => o.Value != current))
            list.Add(new L2Option(current, $"scene {current:X2}: not on this map! (the screen would stay black)"));
        return list;
    }

    // what a go-to step does, in plain words, for its card
    private string ArrivalExplain(L2Step step)
    {
        if (!IsGoTo(step) || _rom == null) return "";
        int map = step.Bytes[1], scene = step.Bytes[2], point = step.Bytes[3];
        return $"What happens: the screen fades to black, map {MapLabel(map)} loads with the party on its point {point} (Maxim hidden), " +
               $"then that map's scene {scene:X2} plays. That scene has to show Maxim and fade the screen in (its first steps), " +
               "or the screen stays black. Anything after this step doesn't run.";
    }

    // the warning and fix button for a go-to step whose arrival would stay black
    private void ArrivalWarning(L2LineVm vm, L2Step step)
    {
        if (!IsGoTo(step) || _rom == null) return;
        int map = step.Bytes[1], scene = step.Bytes[2];
        string warning = L2Cutscene.CheckArrival(_rom, map, scene) switch
        {
            L2Cutscene.ArrivalTrouble.NoScene =>
                $"Map {MapLabel(map)} has no scene {scene:X2}, so after the map change nothing fades the screen in: it stays black (checked in the game). " +
                "Pick one of that map's scenes above, or make one with the button below.",
            L2Cutscene.ArrivalTrouble.NoFadeIn =>
                $"Scene {scene:X2} of map {MapLabel(map)} never fades the screen in, so after the map change the screen stays black (checked in the game). " +
                "The button below puts Show Maxim, Fade in and Wait for the fade at its top.",
            L2Cutscene.ArrivalTrouble.NoEvents => $"Map {MapLabel(map)} has no events, so nothing can play there: pick another map.",
            _ => "",
        };
        if (warning.Length == 0) return;
        vm.Warning = (vm.Warning.Length > 0 ? vm.Warning + " " : "") + warning;
        switch (L2Cutscene.CheckArrival(_rom, map, scene))
        {
            case L2Cutscene.ArrivalTrouble.NoScene:
                vm.HeroFix = FixMakeScene;
                vm.HeroFixText = $"Make a new scene on map {map:X2} for this (it shows Maxim and fades in, then you write the rest)";
                break;
            case L2Cutscene.ArrivalTrouble.NoFadeIn:
                vm.HeroFix = FixAddFadeIn;
                vm.HeroFixText = $"Add a fade-in to scene {scene:X2} of map {map:X2}";
                break;
        }
    }

    // after the map of a go-to step changes: the scene and point stay if that map has them, else the first ones it has
    // (a scene that fades in, not a test start). the bytes come back changed or null
    private byte[]? FitArrival(L2Step step)
    {
        if (!IsGoTo(step) || _rom == null) return null;
        var bytes = (byte[])step.Bytes.Clone();
        FitArrivalBytes(bytes);
        return bytes.AsSpan().SequenceEqual(step.Bytes) ? null : bytes;
    }

    // the same on a go-to step's bytes, in place
    private void FitArrivalBytes(byte[] bytes)
    {
        if (_rom == null || bytes.Length < 4 || bytes[0] != 0x16) return;
        int map = bytes[1];
        int block = L2Events.Block(_rom, map);
        if (block >= 0 && L2Cutscene.CheckArrival(_rom, map, bytes[2]) == L2Cutscene.ArrivalTrouble.NoScene)
        {
            List<int> scenes;
            try { scenes = L2EventBlocks.ReadLists(_rom, block).Where(e => e.Head == L2EventBlocks.SceneList && e.Id < 0xF0).Select(e => e.Id).ToList(); }
            catch (Exception) { scenes = new(); }
            int good = scenes.FirstOrDefault(id => L2Cutscene.CheckArrival(_rom, map, id) == L2Cutscene.ArrivalTrouble.None, -1);
            if (good >= 0) bytes[2] = (byte)good;
        }
        var points = L2MapInfo.Records(_rom, map, L2MapInfo.Points, 3).Select(r => (int)r.Bytes[0]).ToList();
        if (points.Count > 0 && !points.Contains(bytes[3])) bytes[3] = (byte)points[0];
    }

    // the point on (x, y) of map, or a new one made there. returns its number and whether it was made
    private static (int Id, bool Made) PointOn(RomBuffer rom, int map, int x, int y)
    {
        var edit = new L2MapEdit(rom, map);
        var here = edit.Records(L2MapEdit.Points).FirstOrDefault(r => r.Bytes[1] == x && r.Bytes[2] == y);
        if (here.Bytes != null) return (here.Bytes[0], false);
        int id = edit.NextPointId();
        if (id > 0xFF) throw new InvalidOperationException($"Map {map:X2} has no free point number.");
        edit.AddRecord(L2MapEdit.Points, new[] { (byte)id, (byte)x, (byte)y });
        edit.Apply(rom);
        return (id, true);
    }

    // the "Go to another map" button of the stage: the party goes to (x, y) of map, where a new scene plays (it shows
    // Maxim and fades in, ready to write). the step goes after the picked one
    public void AddGoTo(int map, int x, int y)
    {
        if (_rom == null || _main == null || _script == null || !CanEdit || _map < 0) return;
        if (map < 2) { StageInfo = "The party can only arrive in a town or dungeon this way, not on the world map."; return; }
        // reading the map again below reads the open event again, so unsaved steps get saved first
        if (HasPendingEdits && !SaveNow(quiet: true)) return;
        var rom = _rom;
        int point, scene;
        bool madePoint;
        try
        {
            (point, madePoint, scene) = L2Rom.AllOrNothing(rom, () =>
            {
                var (id, made) = PointOn(rom, map, x, y);
                return (id, made, L2Cutscene.MakeArrivalScene(rom, map));
            });
        }
        catch (Exception ex) { StageInfo = "Not added: " + ex.Message; return; }
        _main.NotifyRomModified($"Lufia II: new scene {scene:X2} of map {map:X2}" + (madePoint ? $" and its point {point}" : ""));
        ReopenAfterChange(SelectedLine);
        // (the page reads its lists again on Saved, which loads the event again if it has unsaved steps, so this goes before the step)
        Saved?.Invoke("new scene");
        Insert(-1, new L2Step { Bytes = new byte[] { 0x16, (byte)map, (byte)scene, (byte)point } });
        StageInfo = $"Step {SelectedLine + 1}: the party goes to map {MapLabel(map)}, point {point} ({x}, {y}){(madePoint ? ", a new point" : "")}, " +
                    $"where the new scene {scene:X2} plays. It shows Maxim, fades in and says \"...\": press \"Open scene\" on the step to write the rest there. " +
                    "Steps after this one don't run (the event ends with the map change).";
    }

    // the fix button of a go-to card: makes the missing scene (and points the step at it) or adds a fade-in to it
    public void FixArrival(int index, int fix)
    {
        if (_rom == null || _main == null || _script == null || !CanEdit || index < 0 || index >= _script.Steps.Count || !IsGoTo(_script.Steps[index])) return;
        if (fix is not (FixMakeScene or FixAddFadeIn)) return;
        if (HasPendingEdits && !SaveNow(quiet: true)) return;
        var step = _script.Steps[index];
        int map = step.Bytes[1], scene = step.Bytes[2];
        try
        {
            if (fix == FixAddFadeIn)
            {
                L2Cutscene.AddFadeIn(_rom, map, scene);
                _main.NotifyRomModified($"Lufia II: fade-in added to scene {scene:X2} of map {map:X2}");
                ReopenAfterChange(index);
                StageInfo = $"Scene {scene:X2} of map {MapLabel(map)} now starts with Show Maxim, Fade in and Wait for the fade, so the party shows up after the map change.";
                Saved?.Invoke("fade-in");
                return;
            }
            int made = L2Cutscene.MakeArrivalScene(_rom, map);
            _main.NotifyRomModified($"Lufia II: new scene {made:X2} of map {map:X2} for a map change");
            ReopenAfterChange(index);
            // point the step at the new scene and write it
            var again = _script!.Steps[index];
            Remember();
            var bytes = (byte[])again.Bytes.Clone();
            bytes[2] = (byte)made;
            again.Bytes = bytes;
            Changed(true);
            if (!SaveNow(quiet: true)) return;
            StageInfo = $"Made scene {made:X2} on map {MapLabel(map)} and pointed this step at it. It shows Maxim, fades in and says \"...\": " +
                        "press \"Open scene\" on this step to write what happens there.";
        }
        catch (Exception ex) { StageInfo = "Not changed: " + ex.Message; }
    }

    // reads the map and this event again after the ROM changed outside it (a scene made or changed, maybe on this map)
    private void ReopenAfterChange(int keep)
    {
        if (_rom == null) return;
        ReadMap();
        var items = ItemsOf(_rom, _map);
        if (_event != null)
        {
            var entry = _event.Entry;
            _event = _event.IsInit ? items[0] : items.FirstOrDefault(item => item.Entry?.Head == entry?.Head && item.Entry?.Id == entry?.Id) ?? _event;
        }
        ReadScript(keep);
        MarkRead();
    }
}
