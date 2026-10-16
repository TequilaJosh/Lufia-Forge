using CommunityToolkit.Mvvm.ComponentModel;
using LufiaForge.Core;
using LufiaForge.Core.Battle;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;

namespace LufiaForge.Modules.GameData;

/// <summary>Edits a monster's AI script as a list of steps (<see cref="AiScript"/>): every command, the game's and
/// Lufia Forge's own (turns, party HP, targeting, switches), with its values and the step a "go to" lands on.</summary>
public partial class AiScriptWindow : Window
{
    public sealed partial class StepVm : ObservableObject
    {
        public required AiScript.Step Step { get; init; }
        [ObservableProperty] private string _line = "";
        [ObservableProperty] private string _title = "";
    }

    public enum FieldKind { Number, List, Step }

    public sealed partial class FieldVm : ObservableObject
    {
        public required FieldKind Kind { get; init; }
        public required string Label { get; init; }
        public string Hint { get; init; } = "";
        public IList<string>? Choices { get; init; }
        public ObservableCollection<StepVm>? Steps { get; init; }
        public Action? Changed { get; init; }
        public Func<string, bool>? Parse { get; init; }
        public Action<int>? SetValue { get; init; }
        public Action<StepVm?>? SetTarget { get; init; }

        [ObservableProperty] private string _text = "";
        [ObservableProperty] private int _value;
        [ObservableProperty] private StepVm? _target;
        partial void OnTextChanged(string value) { if (Parse?.Invoke(value) == true) Changed?.Invoke(); }
        partial void OnValueChanged(int value) { SetValue?.Invoke(value); Changed?.Invoke(); }
        partial void OnTargetChanged(StepVm? value) { SetTarget?.Invoke(value); Changed?.Invoke(); }
    }

    private sealed record Choice(int Op, string Title);

    /// <summary>The commands, grouped the way people think of them.</summary>
    private static readonly Choice[] Commands =
    {
        new(0x05, "Do: attack"), new(0x07, "Do: cast a spell (if it has the MP)"), new(0x06, "Do: use an item"), new(0x08, "Do: special attack"),
        new(0x0B, "Do: guard"), new(0x11, "Do: run away"), new(0x03, "Do: call for help (another of its kind)"),
        new(0x17, "Target: the party member with the lowest HP"), new(0x18, "Target: the party member with the highest HP"),
        new(0x01, "Target: a party member (the game's way)"), new(0x02, "Target: a party member, from the back"),
        new(0x0C, "Target: itself"), new(0x0D, "Target: a random monster on its side"),
        new(0x04, "If: chance (random), go to"), new(0x0A, "If: its HP is above a share of max, go to"), new(0x10, "If: its MP is above a share of max, go to"),
        new(0x16, "If: a party member's HP is below a share of max, go to"), new(0x14, "If: this is turn N or later, go to"),
        new(0x15, "If: every N turns, go to"), new(0x19, "If: it is the only monster left, go to"),
        new(0x1A, "If: N or fewer party members are standing, go to"), new(0x1D, "If: a switch is on, go to"),
        new(0x1B, "Phase: switch on"), new(0x1C, "Phase: switch off"),
        new(0x09, "Go to"), new(0x00, "End of the script"),
        new(0x0F, "Other: show a battle message"), new(0x0E, "Other: next action makes no sound"), new(0x12, "Other: set its HP"), new(0x13, "Other: set its MP"),
    };

    private readonly ObservableCollection<StepVm> _steps = new();
    private readonly IList<string> _spells, _items;
    private readonly RomBuffer _rom;
    private readonly int _monster;
    private bool _filling;

    /// <summary>The script to write (when the window was closed with Use this script).</summary>
    public List<AiScript.Step>? Result { get; private set; }

    public AiScriptWindow(RomBuffer rom, int monster, string name, IList<string> spells, IList<string> items)
    {
        InitializeComponent();
        _rom = rom; _monster = monster; _spells = spells; _items = items;
        HeaderText.Text = $"AI script: {name}";
        CommandBox.ItemsSource = Commands;
        foreach (var s in AiScript.Read(rom, MonsterTable.RecordOffset(rom, monster))) _steps.Add(new StepVm { Step = s });
        StepList.ItemsSource = _steps;
        Refresh();
        StepList.SelectedIndex = _steps.Count > 0 ? 0 : -1;
        if (_steps.Count == 0) EditPanel.Visibility = Visibility.Collapsed;
    }

    // ── showing the steps ──

    private void Refresh()
    {
        var offsets = new Dictionary<AiScript.Step, int>();
        int p = 0;
        foreach (var s in _steps) { offsets[s.Step] = p; p += s.Step.Length; }
        var byOffset = _steps.Select((s, i) => (offsets[s.Step], i)).GroupBy(t => t.Item1).ToDictionary(g => g.Key, g => g.First().i);
        string StepName(int off) => byOffset.TryGetValue(off, out int i) ? $"step {i + 1}" : "(no step)";
        string Spell(int id) => id < _spells.Count ? _spells[id][3..].Trim() : $"spell {id:X2}";
        string Item(int id) => id < _items.Count ? _items[id][3..].Trim() : $"item {id:X2}";
        for (int i = 0; i < _steps.Count; i++)
        {
            var s = _steps[i].Step;
            var args = (byte[])s.Args.Clone();
            int ja = MonsterAi.JumpArg(s.Op);
            int? jump = null;
            if (ja >= 0 && args.Length >= ja + 2)
            {
                int to = s.Target != null && offsets.TryGetValue(s.Target, out int t) ? t : -1;
                jump = to;
                args[ja] = (byte)to; args[ja + 1] = (byte)(to >> 8);
            }
            var cmd = new MonsterAi.Command(offsets[s], s.Op, args, MonsterAi.Name(s.Op), jump, MonsterAi.EndsTurn(s.Op));
            string text = s.Op == 0 ? "End of the script." : MonsterAi.Plain(cmd, StepName, Spell, Item);
            if (ja >= 0 && s.Target == null) text += "  (pick the step to go to)";
            _steps[i].Line = $"{i + 1,2}.  {text}";
            _steps[i].Title = $"Step {i + 1}: {(text.Length > 60 ? text[..60] + "..." : text)}";
        }
        int len = AiScript.Length(_steps.Select(s => s.Step).ToList());
        var (oldStart, oldLen) = MonsterAi.ScriptRange(_rom, MonsterTable.RecordOffset(_rom, _monster));
        int free = AiScript.FreeSpace(_rom, _monster);
        SizeText.Text = $"{_steps.Count} steps, {len} bytes. " + (len <= oldLen ? "It fits where the old script is." :
            len <= free ? $"It goes in the free space for monster scripts ({free} bytes free)." : $"Too long: there are {Math.Max(free, oldLen)} bytes for it. Make it shorter.") +
            (AiScript.UsesNewCommands(_steps.Select(s => s.Step).ToList()) ? " Uses Lufia Forge's new commands (added to the ROM with it)." : "") +
            (_steps.Count > 0 && _steps[^1].Step.Op is not (0x00 or 0x09) && !(MonsterAi.EndsTurn(_steps[^1].Step.Op) && _steps[^1].Step.Op != 0x07)
                ? " The last step can carry on past the end: add \"End of the script\" (or an attack) after it." : "");
    }

    private void StepList_SelectionChanged(object sender, SelectionChangedEventArgs e) => ShowStep();

    private void ShowStep()
    {
        if (StepList.SelectedItem is not StepVm vm) { EditPanel.Visibility = Visibility.Collapsed; return; }
        EditPanel.Visibility = Visibility.Visible;
        _filling = true;
        StepTitle.Text = $"STEP {StepList.SelectedIndex + 1}";
        CommandBox.SelectedItem = Commands.FirstOrDefault(c => c.Op == vm.Step.Op);
        if (CommandBox.SelectedItem == null) CommandBox.Text = MonsterAi.Name(vm.Step.Op);
        Fields.ItemsSource = MakeFields(vm);
        StepHelp.Text = Help(vm.Step.Op);
        _filling = false;
    }

    private void CommandBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_filling || StepList.SelectedItem is not StepVm vm || CommandBox.SelectedItem is not Choice c || c.Op == vm.Step.Op) return;
        vm.Step.Op = c.Op;
        vm.Step.Args = Defaults(c.Op);
        vm.Step.Target = MonsterAi.JumpArg(c.Op) >= 0 ? NextStep(vm) : null;
        Refresh();
        ShowStep();
    }

    private AiScript.Step? NextStep(StepVm vm)
    {
        int i = _steps.IndexOf(vm);
        return i + 1 < _steps.Count ? _steps[i + 1].Step : vm.Step;
    }

    /// <summary>Sensible values for a command just picked.</summary>
    private static byte[] Defaults(int op)
    {
        var a = new byte[Math.Max(0, MonsterAi.ArgCount(op))];
        switch (op)
        {
            case 0x04: case 0x0A: case 0x10: case 0x16: a[0] = 128; break;   // 50%
            case 0x14: a[0] = 3; break;
            case 0x15: a[0] = 2; break;
            case 0x1A: a[0] = 1; break;
            case 0x08: a[1] = 10; break;
            case 0x07: a[0] = 1; break;
            case 0x06: a[0] = 1; break;
        }
        return a;
    }

    private static string Help(int op) => op switch
    {
        0x07 => "Without enough MP for the spell, the monster carries on with the next step.",
        0x01 or 0x02 => "The game's own way of choosing a party member; the three values tune it (keep them as they are if unsure).",
        0x16 => "Only party members who are in the party and still standing count. Pair it with \"Target: the party member with the lowest HP\" to finish off the weakest.",
        0x14 or 0x15 => "Turns are counted for this monster from the start of the battle: its first turn is turn 1.",
        0x1B or 0x1C or 0x1D => "Each monster has 8 switches, all off when the battle starts. Use them for phases: when something happens, switch one on, and check it at the top of the script.",
        0x17 or 0x18 => "Choose the target first, then the action (attack, spell...).",
        0x0A or 0x10 => "50% = half of its max. While it's above, the script goes to the chosen step; once it drops below, it carries on.",
        0x04 => "The chance is out of 100%: 25% means a quarter of the turns go to the chosen step.",
        0x09 => "Jumps to another step (for example back to the top).",
        _ => "",
    };

    // ── the values of a step ──

    private List<FieldVm> MakeFields(StepVm vm)
    {
        var s = vm.Step;
        var list = new List<FieldVm>();
        void Changed() => Refresh();
        FieldVm Byte(int i, string label, string hint = "", int max = 255)
        {
            var f = new FieldVm
            {
                Kind = FieldKind.Number, Label = label, Hint = hint, Changed = Changed,
                Parse = t => { if (!int.TryParse(t, out int v) || v < 0 || v > max) return false; s.Args[i] = (byte)v; return true; },
            };
            f.Text = s.Args[i].ToString();
            return f;
        }
        FieldVm Hex(int i, string label, string hint = "")
        {
            var f = new FieldVm
            {
                Kind = FieldKind.Number, Label = label, Hint = hint, Changed = Changed,
                Parse = t => { if (!int.TryParse(t, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out int v) || v < 0 || v > 255) return false; s.Args[i] = (byte)v; return true; },
            };
            f.Text = s.Args[i].ToString("X2");
            return f;
        }
        FieldVm Percent(int i, string label)
        {
            var f = new FieldVm
            {
                Kind = FieldKind.Number, Label = label, Hint = "%", Changed = Changed,
                Parse = t => { if (!int.TryParse(t.TrimEnd('%'), out int v) || v < 0 || v > 100) return false; s.Args[i] = (byte)Math.Clamp((int)Math.Round(v * 256 / 100.0), 0, 255); return true; },
            };
            f.Text = ((int)Math.Round(s.Args[i] * 100 / 256.0)).ToString();
            return f;
        }
        FieldVm Word(int i, string label)
        {
            var f = new FieldVm
            {
                Kind = FieldKind.Number, Label = label, Changed = Changed,
                Parse = t => { if (!int.TryParse(t, out int v) || v < 0 || v > 65535) return false; s.Args[i] = (byte)v; s.Args[i + 1] = (byte)(v >> 8); return true; },
            };
            f.Text = (s.Args[i] | s.Args[i + 1] << 8).ToString();
            return f;
        }
        FieldVm Pick(int i, string label, IList<string> choices)
        {
            var f = new FieldVm { Kind = FieldKind.List, Label = label, Choices = choices, Changed = Changed, SetValue = v => { if (v >= 0) s.Args[i] = (byte)v; } };
            f.Value = Math.Min(s.Args[i], choices.Count - 1);
            return f;
        }
        FieldVm Step(string label)
        {
            var f = new FieldVm { Kind = FieldKind.Step, Label = label, Steps = _steps, Changed = Changed, SetTarget = t => s.Target = t?.Step };
            f.Target = _steps.FirstOrDefault(x => x.Step == s.Target);
            return f;
        }
        var switches = Enumerable.Range(0, 8).Select(n => $"Switch {n}").ToList();
        switch (s.Op)
        {
            case 0x01: case 0x02: list.Add(Hex(0, "Value 1 (hex)")); list.Add(Hex(1, "Value 2 (hex)")); list.Add(Hex(2, "Value 3 (hex)")); break;
            case 0x03: case 0x05: case 0x0F: list.Add(Hex(0, "Battle message (hex)", "00 = none")); break;
            case 0x04: list.Add(Percent(0, "Chance")); list.Add(Step("Then go to")); break;
            case 0x06: list.Add(Pick(0, "Item", _items)); break;
            case 0x07: list.Add(Pick(0, "Spell", _spells)); break;
            case 0x08: list.Add(Hex(0, "Special attack (hex)")); list.Add(Byte(1, "Power")); list.Add(Hex(2, "Battle message (hex)", "00 = none")); break;
            case 0x09: list.Add(Step("Go to")); break;
            case 0x0A: list.Add(Percent(0, "While its HP is above")); list.Add(Step("Go to")); break;
            case 0x10: list.Add(Percent(0, "While its MP is above")); list.Add(Step("Go to")); break;
            case 0x12: list.Add(Word(0, "HP")); break;
            case 0x13: list.Add(Word(0, "MP")); break;
            case 0x14: list.Add(Byte(0, "From turn", "its 1st turn = 1")); list.Add(Step("Go to")); break;
            case 0x15: list.Add(Byte(0, "Every how many turns")); list.Add(Step("Go to")); break;
            case 0x16: list.Add(Percent(0, "HP below")); list.Add(Step("Go to")); break;
            case 0x19: list.Add(Step("Go to")); break;
            case 0x1A: list.Add(Byte(0, "Standing (at most)", "1-4", 4)); list.Add(Step("Go to")); break;
            case 0x1B: case 0x1C: list.Add(Pick(0, "Switch", switches)); break;
            case 0x1D: list.Add(Pick(0, "Switch", switches)); list.Add(Step("Go to")); break;
        }
        return list;
    }

    // ── adding, removing, moving ──

    private void Add_Click(object sender, RoutedEventArgs e)
    {
        int at = StepList.SelectedIndex < 0 ? _steps.Count : StepList.SelectedIndex + 1;
        // before a final "end" by default
        if (StepList.SelectedIndex < 0 && _steps.Count > 0 && _steps[^1].Step.Op == 0x00) at = _steps.Count - 1;
        var vm = new StepVm { Step = new AiScript.Step { Op = 0x05, Args = new byte[1] } };
        _steps.Insert(at, vm);
        Refresh();
        StepList.SelectedItem = vm;
    }

    private void Delete_Click(object sender, RoutedEventArgs e)
    {
        if (StepList.SelectedItem is not StepVm vm) return;
        int i = _steps.IndexOf(vm);
        _steps.RemoveAt(i);
        // "go to" steps that landed on it land on the step that took its place
        var next = i < _steps.Count ? _steps[i].Step : _steps.Count > 0 ? _steps[^1].Step : null;
        foreach (var s in _steps) if (s.Step.Target == vm.Step) s.Step.Target = next;
        Refresh();
        StepList.SelectedIndex = Math.Min(i, _steps.Count - 1);
    }

    private void Move(int d)
    {
        if (StepList.SelectedItem is not StepVm vm) return;
        int i = _steps.IndexOf(vm), j = i + d;
        if (j < 0 || j >= _steps.Count) return;
        _steps.Move(i, j);
        Refresh();
        StepList.SelectedItem = vm;
    }

    private void Up_Click(object sender, RoutedEventArgs e) => Move(-1);
    private void Down_Click(object sender, RoutedEventArgs e) => Move(1);

    private void Ok_Click(object sender, RoutedEventArgs e)
    {
        var steps = _steps.Select(s => s.Step).ToList();
        var missing = steps.FindIndex(s => MonsterAi.JumpArg(s.Op) >= 0 && (s.Target == null || !steps.Contains(s.Target)));
        if (missing >= 0) { MessageBox.Show($"Step {missing + 1} goes to a step that isn't there: pick one.", "AI script"); StepList.SelectedIndex = missing; return; }
        Result = steps;
        DialogResult = true;
    }
}

/// <summary>Picks the template for a value of a command (number, list, step).</summary>
public sealed class FieldTemplateSelector : DataTemplateSelector
{
    public DataTemplate? Number { get; set; }
    public DataTemplate? List { get; set; }
    public DataTemplate? Step { get; set; }

    public override DataTemplate? SelectTemplate(object item, DependencyObject container) =>
        (item as AiScriptWindow.FieldVm)?.Kind switch
        {
            AiScriptWindow.FieldKind.List => List,
            AiScriptWindow.FieldKind.Step => Step,
            _ => Number,
        };
}
