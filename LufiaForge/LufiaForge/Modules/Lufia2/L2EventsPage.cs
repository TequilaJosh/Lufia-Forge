using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LufiaForge.Core.Lufia2;

namespace LufiaForge.Modules.Lufia2;

/// <summary>A map's event and puzzle scripts, command by command: pick one to change its bytes in place (same
/// length), and the story flags with every place that uses them.</summary>
public partial class L2EventsPage : L2Page
{
    private readonly List<L2Script.Op?> _ops = new();
    private Dictionary<int, List<string>>? _flagUses;

    public ObservableCollection<string> Lines { get; } = new();
    [ObservableProperty] private int _selectedLine = -1;
    [ObservableProperty] private string _summary = "";
    [ObservableProperty] private string _bytesText = "";
    [ObservableProperty] private string _editInfo = "Pick a command to change its bytes.";
    [ObservableProperty] private bool _canEdit;
    public ObservableCollection<string> Flags { get; } = new();
    [ObservableProperty] private int _selectedFlag = -1;
    [ObservableProperty] private string _flagUsesText = "";

    protected override IEnumerable<string> ReadNames()
    {
        _flagUses = null;
        Flags.Clear();
        return Enumerable.Range(0, L2Maps.Count).Select(m =>
        {
            string n = L2Maps.Name(Rom!, m);
            return $"{m:X2}  {(n.Length > 0 && !n.Contains('~') ? n : "Map " + m.ToString("X2"))}";
        });
    }

    protected override void Load(int index)
    {
        var scripts = L2Script.MapScripts(Rom!, index).Concat(L2Puzzles.MapScripts(Rom!, index)).ToList();
        _ops.Clear(); Lines.Clear();
        int commands = 0, texts = 0;
        foreach (var (title, ops) in scripts)
        {
            _ops.Add(null); Lines.Add($"== {title}");
            foreach (var o in ops)
            {
                _ops.Add(o);
                if (o.IsText) { texts++; Lines.Add($"{o.At:X6}    \"{o.Text.Replace("\n", " / ")}\""); }
                else { commands++; Lines.Add($"{o.At:X6}  {o.Text}"); }
            }
        }
        Summary = $"{scripts.Count} scripts, {commands} commands, {texts} pieces of text. Addresses are file offsets. Story flags are bits at $7E:077E, " +
                  "variables bytes at $7E:079E; puzzle scripts (switches, doors, spots) follow the event scripts and have their own flags ($7F:D100).";
        if (Flags.Count == 0) LoadFlags();
        SelectedLine = -1;
    }

    partial void OnSelectedLineChanged(int value)
    {
        var op = value >= 0 && value < _ops.Count ? _ops[value] : null;
        CanEdit = op != null && !op.IsText;
        if (op == null) { BytesText = ""; EditInfo = "Pick a command to change its bytes."; return; }
        if (op.IsText) { BytesText = ""; EditInfo = "Text: change it on the Text page."; return; }
        BytesText = string.Join(" ", Rom!.ReadBytes(op.At, op.Length).Select(b => b.ToString("X2")));
        EditInfo = $"{op.Length} bytes at {op.At:X6}. Change the bytes (hex) and Apply; the command must keep its length, so the script around it stays put.";
    }

    [RelayCommand]
    private void ApplyBytes()
    {
        if (Rom == null || Main == null || SelectedLine < 0 || _ops[SelectedLine] is not { IsText: false } op) return;
        byte[] bytes;
        try { bytes = BytesText.Split(' ', StringSplitOptions.RemoveEmptyEntries).Select(s => Convert.ToByte(s, 16)).ToArray(); }
        catch (Exception) { EditInfo = "Not written: write the bytes as hex pairs separated by spaces."; return; }
        if (bytes.Length != op.Length) { EditInfo = $"Not written: it must stay {op.Length} bytes (it has {bytes.Length})."; return; }
        var old = Rom.ReadBytes(op.At, op.Length);
        Rom.WriteBytes(op.At, bytes);
        bool puzzle = Lines.Take(SelectedLine).LastOrDefault(l => l.StartsWith("== "))?.StartsWith("== Puzzle") == true;
        int len = puzzle ? L2Puzzles.Length(Rom, op.At) : L2Script.Length(Rom, op.At);
        if (len != op.Length) { Rom.WriteBytes(op.At, old); EditInfo = $"Not written: as a command that would be {len} bytes, not {op.Length}."; return; }
        Main.NotifyRomModified($"Lufia II: event command at {op.At:X6}");
        int keep = SelectedLine;
        _flagUses = null; Flags.Clear();
        Load(SelectedIndex);
        SelectedLine = keep;
        EditInfo = "Written. " + EditInfo;
    }

    // ------------------------------------------------------------------ story flags

    private void LoadFlags()
    {
        _flagUses = new Dictionary<int, List<string>>();
        for (int m = 0; m < L2Maps.Count; m++)
        {
            string map = $"{m:X2} {L2Maps.Name(Rom!, m)}";
            foreach (var (title, ops) in L2Script.MapScripts(Rom!, m))
                foreach (var o in ops.Where(o => !o.IsText))
                {
                    int c = Rom!.ReadByte(o.At);
                    void Use(int flag, string how)
                    {
                        if (!_flagUses.TryGetValue(flag, out var l)) _flagUses[flag] = l = new();
                        l.Add($"{how,-8} {map}, {title} ({o.At:X6})");
                    }
                    switch (c)
                    {
                        case 0x1A: Use(Rom.ReadByte(o.At + 1), "set"); break;
                        case 0x1B: Use(Rom.ReadByte(o.At + 1), "clear"); break;
                        case 0x15 or 0x6A: Use(Rom.ReadByte(o.At + 1), "test"); break;
                        case 0x14:
                            for (int q = o.At + 1, g = 0; g < 64 && q < o.At + o.Length; g++)
                            {
                                int k = Rom.ReadByte(q++);
                                if (k == 0xFF) break;
                                if ((k & 0xC0) == 0xC0) { q += (k & 0xF8) switch { 0xC0 => 4, 0xC8 or 0xD0 or 0xD8 => 3, _ => 2 }; continue; }
                                if ((k & 0x30) == 0) { Use(Rom.ReadByte(q), "test"); q += 1; }
                                else q += 2;
                            }
                            break;
                    }
                }
        }
        foreach (var f in Enumerable.Range(0, 256))
        {
            var uses = _flagUses.GetValueOrDefault(f);
            int sets = uses?.Count(u => u.StartsWith("set")) ?? 0, tests = uses?.Count(u => u.StartsWith("test")) ?? 0;
            Flags.Add($"{f:X2}  {(uses == null ? "unused" : $"set {sets}x, tested {tests}x")}");
        }
    }

    partial void OnSelectedFlagChanged(int value)
    {
        if (_flagUses == null || value < 0) { FlagUsesText = ""; return; }
        FlagUsesText = _flagUses.TryGetValue(value, out var l) ? string.Join("\n", l) : "No event script uses this flag.";
    }

    protected override bool IsField(string p) => false;
    protected override string? Write() => null;
}
