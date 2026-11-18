using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LufiaForge.Core.Lufia2;

namespace LufiaForge.Modules.Lufia2;

// what a pasted step names on the map it was copied from (a character, a point, a path or an area), and
// what it becomes on this map
public sealed class L2PasteRef
{
    public L2Kind Kind { get; init; }
    public int From { get; init; }
    public string Label { get; init; } = "";
    public List<L2Option> Choices { get; init; } = new();
    // the value picked (for paths and areas, CopyIt copies the source's record to this map)
    public int To { get; set; }
    public const int CopyIt = -2;
    // paths and areas are places of the map they come from, so by default the steps that use one are left out
    public const int SkipIt = -3;
}

// copying steps (from one event to another, on any map). Shift+click marks a range, Copy keeps it, Paste puts
// it below the picked step. on another map the characters, points, paths and areas the steps name get matched to this
// map's (paths and areas can be copied over)
public partial class L2ScriptEditor
{
    private sealed class StepClip
    {
        public int Map;
        public string From = "";
        public List<L2Step> Steps = new();
        // per step, per jump: the index of its target in Steps, or -1 (the step after the pasted ones)
        public List<int[]> Jumps = new();
    }

    private static StepClip? _clip;
    private int _markFrom = -1, _markTo = -1;

    [ObservableProperty] private string _clipInfo = "";

    // asks the panel to match what pasted steps name, returns false if the paste is cancelled
    public event Func<string, List<L2PasteRef>, bool>? PasteMatchRequested;

    public bool HasClip => _clip != null;

    // marks the steps from first to last (Shift+click) for Copy
    public void MarkRange(int first, int last)
    {
        _markFrom = Math.Min(first, last); _markTo = Math.Max(first, last);
        for (int i = 0; i < Lines.Count; i++) Lines[i].IsMarked = i >= _markFrom && i <= _markTo;
        Status = $"Steps {_markFrom + 1} to {_markTo + 1} marked: Copy (Ctrl+C) keeps them to paste in this or another event.";
    }

    public void ClearMark()
    {
        _markFrom = _markTo = -1;
        foreach (var line in Lines) line.IsMarked = false;
    }

    [RelayCommand]
    public void CopySteps()
    {
        if (_script == null || _event?.PuzzleOps != null) return;
        int from = _markFrom >= 0 ? _markFrom : SelectedLine, to = _markFrom >= 0 ? _markTo : SelectedLine;
        if (from < 0 || to >= _script.Steps.Count) { Status = "Pick a step (Shift+click another to take several) to copy."; return; }
        var steps = _script.Steps.GetRange(from, to - from + 1);
        var clip = new StepClip { Map = _map, From = Title };
        foreach (var step in steps)
        {
            var copy = new L2Step
            {
                Bytes = (byte[])step.Bytes.Clone(), IsText = step.IsText, Text = step.Text, End = step.End,
                // a new step, its text is written out (not kept as the old bytes)
                OriginalEnd = step.IsText ? (byte)0xFF : step.End, OriginalText = step.IsText ? "\u0001" : "",
            };
            clip.Steps.Add(copy);
            clip.Jumps.Add(step.Jumps.Select(j => j.Step == null ? -1 : steps.IndexOf(j.Step)).ToArray());
        }
        _clip = clip;
        OnPropertyChanged(nameof(HasClip));
        ClipInfo = $"{steps.Count} step{(steps.Count == 1 ? "" : "s")} copied from {Title}";
        Status = ClipInfo + ". Paste (Ctrl+V) puts them below the picked step, here or in another event.";
    }

    [RelayCommand]
    public void PasteSteps()
    {
        if (_clip == null || _script == null || _rom == null || _main == null || !CanEdit) return;
        var clip = _clip;
        var steps = clip.Steps.Select(s => new L2Step
        {
            Bytes = (byte[])s.Bytes.Clone(), IsText = s.IsText, Text = s.Text, End = s.End, OriginalEnd = s.OriginalEnd, OriginalText = s.OriginalText,
        }).ToList();
        string note = "";
        var dropped = new HashSet<int>();
        if (clip.Map != _map)
        {
            var refs = References(steps, clip.Map);
            if (refs.Count > 0)
            {
                if (PasteMatchRequested == null || !PasteMatchRequested.Invoke($"The steps come from {clip.From}. Pick what each thing they name is on this map:", refs))
                { Status = "Not pasted."; return; }
                try { note = Remap(steps, refs, clip.Map, out var leftOut) ; dropped = leftOut; }
                catch (Exception ex) { Status = "Not pasted: " + ex.Message; return; }
            }
        }
        // steps left out (they use a path or area of the other map): jumps to one go to the next kept step
        var keep = Enumerable.Range(0, steps.Count).Where(i => !dropped.Contains(i)).ToList();
        if (keep.Count == 0) { Status = "Not pasted: every step uses a path or area left out."; return; }
        int NewIndex(int old) { int index = keep.FindIndex(i => i >= old); return index; }
        var kept = keep.Select(i => steps[i]).ToList();
        int at = InsertIndex;
        Insert(at, kept.ToArray());
        // jumps inside the pasted steps point at the pasted copies
        for (int keptIndex = 0; keptIndex < kept.Count; keptIndex++)
        {
            var jumpTargets = clip.Jumps[keep[keptIndex]];
            for (int k = 0; k < kept[keptIndex].Jumps.Count && k < jumpTargets.Length; k++)
                if (jumpTargets[k] >= 0 && NewIndex(jumpTargets[k]) is int target && target >= 0) kept[keptIndex].Jumps[k].Step = kept[target];
        }
        if (dropped.Count > 0) note += $" {dropped.Count} step{(dropped.Count == 1 ? " that walks a path or uses an area of the other map was" : "s that walk paths or use areas of the other map were")} left out.";
        steps = kept;
        Changed(true);
        Status = $"{steps.Count} step{(steps.Count == 1 ? "" : "s")} pasted below step {at}." + note + Unfinished(steps);
    }

    // effects the pasted steps start and don't end (a colour, a ring, a quake). the steps that ended them
    // were left out of the copy, and the effect would stay for the rest of the event
    private static string Unfinished(List<L2Step> steps)
    {
        bool colour = false, shake = false;
        var rings = new HashSet<int>();
        foreach (var step in steps.Where(s => !s.IsText && s.Bytes.Length > 0))
        {
            int op = step.Op;
            if (op is 0x93 or 0x94 or 0x95 or 0x96 or 0x9B) colour = true;
            else if (op is 0x97 or 0xC5) colour = false;
            else if (op == 0x5A) shake = true;
            else if (op is 0x56 or 0x8A) shake = false;
            else if (op is 0x8D or 0xA0 or 0xC2 or 0xC3 or 0xC8 or 0xC9 && step.Bytes.Length > 1) rings.Add(step.Bytes[1]);
            else if (op is 0x9A or 0x9F or 0xA2 && step.Bytes.Length > 1) rings.Remove(step.Bytes[1]);
        }
        var left = new List<string>();
        if (colour) left.Add("a colour (add \"End the colour\" after them)");
        if (shake) left.Add("a quake (add \"Stop the shaking\")");
        if (rings.Count > 0) left.Add($"ring{(rings.Count == 1 ? "" : "s")} {string.Join(", ", rings.Select(r => r.ToString("X2")))} (add \"Remove a ring\")");
        return left.Count == 0 ? "" : " Careful: they start " + string.Join(" and ", left) + " and don't end it; the step that did was probably left out of the copy.";
    }

    // the characters (not the heroes), points, paths and areas the steps name, with this map's choices
    private List<L2PasteRef> References(List<L2Step> steps, int fromMap)
    {
        var refs = new List<L2PasteRef>();
        var srcPoints = L2MapInfo.Records(_rom!, fromMap, L2MapInfo.Points, 3).ToDictionary(r => (int)r.Bytes[0], r => r.Bytes);
        var srcAreas = L2MapInfo.Records(_rom!, fromMap, L2MapInfo.Areas, 5).GroupBy(r => (int)r.Bytes[0]).ToDictionary(g => g.Key, g => g.First().Bytes);
        var there = new L2Speakers(_rom!, fromMap);
        foreach (var step in steps)
        {
            var def = L2EventCommands.Find(step.Op);
            if (def == null) continue;
            foreach (var paramDef in def.FieldsFor(step.Bytes))
            {
                if (paramDef.Kind is not (L2Kind.Who or L2Kind.WhoOrAll or L2Kind.Point or L2Kind.Path or L2Kind.Area)) continue;
                int value;
                try { value = paramDef.Get(step.Bytes); } catch (Exception) { continue; }
                var kind = paramDef.Kind == L2Kind.WhoOrAll ? L2Kind.Who : paramDef.Kind;
                if (kind == L2Kind.Who && (value < 0x07 || value == 0xFF)) continue;   // the heroes and "everyone" are the same everywhere
                if (refs.Any(r => r.Kind == kind && r.From == value)) continue;
                List<L2Option> choices;
                string label;
                int to;
                switch (kind)
                {
                    case L2Kind.Who:
                        choices = WhoChoices(false).Where(o => o.Value >= 0x07).ToList();
                        label = $"Character {value:X2} ({there.Label(value)})";
                        to = choices.Any(o => o.Value == value) ? value : choices.FirstOrDefault()?.Value ?? value;
                        if (!choices.Any(o => o.Value == value)) choices.Insert(0, new L2Option(value, $"{value:X2} keep it ({there.Label(value)}; not on this map)"));
                        break;
                    case L2Kind.Point:
                        choices = ChoicesOf(L2Kind.Point).ToList();
                        label = $"Point {value}" + (srcPoints.TryGetValue(value, out var pointRecord) ? $" ({pointRecord[1]},{pointRecord[2]} there)" : "");
                        to = choices.Any(o => o.Value == value) ? value : choices.FirstOrDefault()?.Value ?? value;
                        break;
                    case L2Kind.Path:
                        choices = ChoicesOf(L2Kind.Path).Prepend(new L2Option(L2PasteRef.CopyIt, "copy it (it walks the other map's places)"))
                            .Prepend(new L2Option(L2PasteRef.SkipIt, "leave out the steps that walk it")).ToList();
                        label = $"Path {value}";
                        to = L2PasteRef.SkipIt;
                        break;
                    default:
                        choices = ChoicesOf(L2Kind.Area).Prepend(new L2Option(L2PasteRef.CopyIt, "copy it (the same place on this map)"))
                            .Prepend(new L2Option(L2PasteRef.SkipIt, "leave out the steps that use it")).ToList();
                        label = $"Area {value}" + (srcAreas.TryGetValue(value, out var areaRecord) ? $" ({areaRecord[1]},{areaRecord[2]})-({areaRecord[3] - 1},{areaRecord[4] - 1}) there" : "");
                        to = L2PasteRef.SkipIt;
                        break;
                }
                refs.Add(new L2PasteRef { Kind = kind, From = value, Label = label, Choices = choices, To = to });
            }
        }
        return refs;
    }

    // puts this map's characters, points, paths and areas into the steps (copying the paths and areas that are
    // to be copied). returns a note for the status line
    private string Remap(List<L2Step> steps, List<L2PasteRef> refs, int fromMap, out HashSet<int> drop)
    {
        drop = new HashSet<int>();
        var copies = new List<string>();
        var newIds = new Dictionary<(L2Kind, int), int>();
        if (refs.Any(r => r.To == L2PasteRef.CopyIt))
        {
            var edit = new L2MapEdit(_rom!, _map);
            foreach (var copyRef in refs.Where(r => r.To == L2PasteRef.CopyIt))
            {
                if (copyRef.Kind == L2Kind.Path)
                {
                    var source = L2Paths.Read(_rom!, fromMap).FirstOrDefault(p => p.Id == copyRef.From) ?? throw new InvalidOperationException($"path {copyRef.From} isn't on the map the steps come from.");
                    int id = edit.NextPathId();
                    edit.SetPath(id, source.X, source.Y, source.Flags, source.Program);
                    newIds[(copyRef.Kind, copyRef.From)] = id;
                    copies.Add($"path {copyRef.From} as path {id}");
                }
                else
                {
                    var source = L2MapInfo.Records(_rom!, fromMap, L2MapInfo.Areas, 5).FirstOrDefault(a => a.Bytes[0] == copyRef.From).Bytes
                              ?? throw new InvalidOperationException($"area {copyRef.From} isn't on the map the steps come from.");
                    int id = NextAreaId(edit);
                    edit.AddRecord(L2MapInfo.Areas, new[] { (byte)id, source[1], source[2], source[3], source[4] });
                    newIds[(copyRef.Kind, copyRef.From)] = id;
                    copies.Add($"area {copyRef.From} as area {id}");
                }
            }
            edit.Apply(_rom!);
            _main!.NotifyRomModified($"Lufia II: map {_map:X2} paths and areas from map {fromMap:X2}");
            ReadPaths();
            _areas = L2MapInfo.Records(_rom!, _map, L2MapInfo.Areas, 5).Select(x => ((int)x.Bytes[0], $"area {x.Bytes[0]} ({x.Bytes[1]},{x.Bytes[2]})-({x.Bytes[3] - 1},{x.Bytes[4] - 1})")).ToList();
            _choices.Clear();
        }
        for (int stepIndex = 0; stepIndex < steps.Count; stepIndex++)
        {
            var step = steps[stepIndex];
            var def = L2EventCommands.Find(step.Op);
            if (def == null) continue;
            var bytes = (byte[])step.Bytes.Clone();
            foreach (var paramDef in def.FieldsFor(bytes))
            {
                if (paramDef.Kind is not (L2Kind.Who or L2Kind.WhoOrAll or L2Kind.Point or L2Kind.Path or L2Kind.Area)) continue;
                int value;
                try { value = paramDef.Get(bytes); } catch (Exception) { continue; }
                var kind = paramDef.Kind == L2Kind.WhoOrAll ? L2Kind.Who : paramDef.Kind;
                var match = refs.FirstOrDefault(x => x.Kind == kind && x.From == value);
                if (match == null) continue;
                if (match.To == L2PasteRef.SkipIt) { drop.Add(stepIndex); continue; }
                int to = match.To == L2PasteRef.CopyIt ? newIds[(kind, value)] : match.To;
                paramDef.Set(bytes, to);
            }
            step.Bytes = bytes;
        }
        return copies.Count > 0 ? " Copied to this map: " + string.Join(", ", copies) + "." : "";
    }

    private static int NextAreaId(L2MapEdit edit) =>
        edit.Records(L2MapInfo.Areas).Select(r => (int)r.Bytes[0]).DefaultIfEmpty(0).Max() + 1;
}
