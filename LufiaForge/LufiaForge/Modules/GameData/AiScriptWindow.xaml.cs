using CommunityToolkit.Mvvm.ComponentModel;
using LufiaForge.Core;
using LufiaForge.Core.Battle;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;

namespace LufiaForge.Modules.GameData;

// edits a monster's AI script as a list of steps (AiScript). every command is here, the game's and
// Lufia Forge's own (turns, party HP, targeting, switches), with its values and the step a "go to" lands on
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

    // the commands, grouped the way people think of them
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
        new(0x1E, "If: a party member check (level, HP, MP, status...), go to"), new(0x1F, "Target: the party member with the highest / lowest value"),
        new(0x1B, "Phase: switch on"), new(0x1C, "Phase: switch off"),
        new(0x20, "Numbers: set a number"), new(0x21, "Numbers: add to a number"), new(0x24, "Numbers: set a number from the party"),
        new(0x22, "If: a number compares, go to"), new(0x23, "Do: bonus damage this turn (a share of the target's HP)"),
        new(0x25, "Do: several actions this turn"),
        new(0x09, "Go to"), new(0x00, "End of the script"),
        new(0x0F, "Other: show a battle message"), new(0x0E, "Other: next action makes no sound"), new(0x12, "Other: set its HP"), new(0x13, "Other: set its MP"),
    };

    private readonly ObservableCollection<StepVm> _steps = new();
    private readonly IList<string> _spells, _items;
    private readonly RomBuffer _rom;
    private readonly int _monster;
    private bool _filling;

    // the script to write, set when the window was closed with Use this script
    public List<AiScript.Step>? Result { get; private set; }

    public AiScriptWindow(RomBuffer rom, int monster, string name, IList<string> spells, IList<string> items)
    {
        InitializeComponent();
        _rom = rom; _monster = monster; _spells = spells; _items = items;
        HeaderText.Text = $"AI script: {name}";
        CommandBox.ItemsSource = Commands;
        foreach (var step in AiScript.Read(rom, MonsterTable.RecordOffset(rom, monster))) _steps.Add(new StepVm { Step = step });
        StepList.ItemsSource = _steps;
        Refresh();
        StepList.SelectedIndex = _steps.Count > 0 ? 0 : -1;
        if (_steps.Count == 0) EditPanel.Visibility = Visibility.Collapsed;
    }

    // showing the steps

    private void Refresh()
    {
        var offsets = new Dictionary<AiScript.Step, int>();
        int offset = 0;
        foreach (var vm in _steps) { offsets[vm.Step] = offset; offset += vm.Step.Length; }
        var indexByOffset = _steps.Select((vm, i) => (offsets[vm.Step], i)).GroupBy(t => t.Item1).ToDictionary(g => g.Key, g => g.First().i);
        string StepName(int stepOffset) => indexByOffset.TryGetValue(stepOffset, out int i) ? $"step {i + 1}" : "(no step)";
        string Spell(int id) => id < _spells.Count ? _spells[id][3..].Trim() : $"spell {id:X2}";
        string Item(int id) => id < _items.Count ? _items[id][3..].Trim() : $"item {id:X2}";
        bool severalActions = false;   // after a command 25 (several actions a turn) an action doesn't end the turn
        for (int i = 0; i < _steps.Count; i++)
        {
            var step = _steps[i].Step;
            if (step.Op == 0x25) severalActions = true;
            var args = (byte[])step.Args.Clone();
            int jumpArg = MonsterAi.JumpArg(step.Op);
            int? jump = null;
            if (jumpArg >= 0 && args.Length >= jumpArg + 2)
            {
                int targetOffset = step.Target != null && offsets.TryGetValue(step.Target, out int found) ? found : -1;
                jump = targetOffset;
                args[jumpArg] = (byte)targetOffset; args[jumpArg + 1] = (byte)(targetOffset >> 8);
            }
            var command = new MonsterAi.Command(offsets[step], step.Op, args, MonsterAi.Name(step.Op), jump, MonsterAi.EndsTurn(step.Op) && !severalActions);
            string text = step.Op == 0 ? "End of the script." : MonsterAi.Plain(command, StepName, Spell, Item);
            if (jumpArg >= 0 && step.Target == null) text += "  (pick the step to go to)";
            _steps[i].Line = $"{i + 1,2}.  {text}";
            _steps[i].Title = $"Step {i + 1}: {(text.Length > 60 ? text[..60] + "..." : text)}";
        }
        int scriptLength = AiScript.Length(_steps.Select(vm => vm.Step).ToList());
        int record = MonsterTable.RecordOffset(_rom, _monster);
        var (oldStart, oldLen) = MonsterAi.ScriptRange(_rom, record);
        int free = AiScript.FreeSpace(_rom, _monster);
        bool far = MonsterAi.HomeOf(_rom, record).Far, canFar = AiFarScripts.CanInstall(_rom);
        SizeText.Text = $"{_steps.Count} steps, {scriptLength} bytes. " + (
            scriptLength > AiFarScripts.MaxLength ? $"Too long: a monster's script can be at most {AiFarScripts.MaxLength} bytes. Make it shorter." :
            far ? "It goes in Lufia Forge's script space (where this monster's script already is)." :
            scriptLength <= oldLen ? "It fits where the old script is." :
            scriptLength <= free ? $"It goes in the free space for monster scripts in bank 0 ({free} bytes free)." :
            canFar ? "Bank 0 is full, so it goes in Lufia Forge's script space (in the ROM's added space)." :
            $"Too long: there are {Math.Max(free, oldLen)} bytes for it (another patch changed the battle's AI code, so the added space can't be used). Make it shorter.") +
            (AiScript.UsesNewCommands(_steps.Select(vm => vm.Step).ToList()) ? " Uses Lufia Forge's new commands (added to the ROM with it)." : "") +
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
        if (_filling || StepList.SelectedItem is not StepVm vm || CommandBox.SelectedItem is not Choice choice || choice.Op == vm.Step.Op) return;
        vm.Step.Op = choice.Op;
        vm.Step.Args = Defaults(choice.Op);
        vm.Step.Target = MonsterAi.JumpArg(choice.Op) >= 0 ? NextStep(vm) : null;
        Refresh();
        ShowStep();
    }

    private AiScript.Step? NextStep(StepVm vm)
    {
        int i = _steps.IndexOf(vm);
        return i + 1 < _steps.Count ? _steps[i + 1].Step : vm.Step;
    }

    // ok starting values for a command that was just picked
    private static byte[] Defaults(int op)
    {
        var args = new byte[Math.Max(0, MonsterAi.ArgCount(op))];
        switch (op)
        {
            case 0x04: case 0x0A: case 0x10: case 0x16: args[0] = 128; break;   // 50%
            case 0x14: args[0] = 3; break;
            case 0x15: args[0] = 2; break;
            case 0x1A: args[0] = 1; break;
            case 0x1E: args[0] = 0x10; args[1] = 5; args[2] = 2; args[3] = 30; break;   // any standing member below 30% HP
            case 0x1F: args[0] = 1; args[1] = 1; break;                            // the lowest HP
            case 0x21: args[1] = 1; break;
            case 0x23: args[1] = 25; break;
            case 0x24: args[1] = 0x13; break;
            case 0x25: args[0] = 2; break;
            case 0x08: args[1] = 10; break;
            case 0x07: args[0] = 1; break;
            case 0x06: args[0] = 1; break;
        }
        return args;
    }

    private static string Help(int op) => op switch
    {
        0x07 => "Without enough MP for the spell, the monster carries on with the next step.",
        0x01 or 0x02 => "The game's own way of choosing a party member; the three values tune it (keep them as they are if unsure).",
        0x16 => "Only party members who are in the party and still standing count. Pair it with \"Target: the party member with the lowest HP\" to finish off the weakest.",
        0x14 or 0x15 => "Turns are counted for this monster from the start of the battle: its first turn is turn 1.",
        0x1B or 0x1C or 0x1D => "Each monster has 8 switches, all off when the battle starts. Use them for phases: when something happens, switch one on, and check it at the top of the script.",
        0x17 or 0x18 or 0x1F => "Choose the target first, then the action (attack, spell...). Only party members who are still standing count.",
        0x1E => "\"Any\" is true when one standing member passes, \"every\" when all of them do. HP % is 0-100. Status bits: KO is 32 (use \"has any of the bits\" with 32). From Ok Impala's AI extensions.",
        0x20 or 0x21 or 0x22 or 0x24 => "Each monster has 4 numbers (0-255), all 0 when the battle starts: count hits, turns since something happened, how often it healed... From Ok Impala's AI extensions.",
        0x25 => "Put it first, then the actions one after another (each with its own target or bonus damage if you like): after each action the monster acts again at once, and the script carries on from the step after that action. 1-8. From Ok Impala's AI extensions (their 1B).",
        0x23 => "Set it before the attack or spell: every hit this turn that does damage gets the extra, up to 9999. It is cleared at the start of the monster's next turn. 0% = none. From Ok Impala's AI extensions.",
        0x0A or 0x10 => "50% = half of its max. While it's above, the script goes to the chosen step; once it drops below, it carries on.",
        0x04 => "The chance is out of 100%: 25% means a quarter of the turns go to the chosen step.",
        0x09 => "Jumps to another step (for example back to the top).",
        _ => "",
    };

    // the values of a step

    private List<FieldVm> MakeFields(StepVm vm)
    {
        var step = vm.Step;
        var list = new List<FieldVm>();
        void Changed() => Refresh();
        FieldVm Byte(int i, string label, string hint = "", int max = 255)
        {
            var field = new FieldVm
            {
                Kind = FieldKind.Number, Label = label, Hint = hint, Changed = Changed,
                Parse = text => { if (!int.TryParse(text, out int number) || number < 0 || number > max) return false; step.Args[i] = (byte)number; return true; },
            };
            field.Text = step.Args[i].ToString();
            return field;
        }
        FieldVm Hex(int i, string label, string hint = "")
        {
            var field = new FieldVm
            {
                Kind = FieldKind.Number, Label = label, Hint = hint, Changed = Changed,
                Parse = text => { if (!int.TryParse(text, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out int number) || number < 0 || number > 255) return false; step.Args[i] = (byte)number; return true; },
            };
            field.Text = step.Args[i].ToString("X2");
            return field;
        }
        FieldVm Percent(int i, string label)
        {
            var field = new FieldVm
            {
                Kind = FieldKind.Number, Label = label, Hint = "%", Changed = Changed,
                Parse = text => { if (!int.TryParse(text.TrimEnd('%'), out int number) || number < 0 || number > 100) return false; step.Args[i] = (byte)Math.Clamp((int)Math.Round(number * 256 / 100.0), 0, 255); return true; },
            };
            field.Text = ((int)Math.Round(step.Args[i] * 100 / 256.0)).ToString();
            return field;
        }
        FieldVm Word(int i, string label)
        {
            var field = new FieldVm
            {
                Kind = FieldKind.Number, Label = label, Changed = Changed,
                Parse = text => { if (!int.TryParse(text, out int number) || number < 0 || number > 65535) return false; step.Args[i] = (byte)number; step.Args[i + 1] = (byte)(number >> 8); return true; },
            };
            field.Text = (step.Args[i] | step.Args[i + 1] << 8).ToString();
            return field;
        }
        // a value the list doesn't have (a hack's own) shows up as one more choice. if we clamped it instead, just
        // picking it would write the clamped value into the step and the script would save as changed
        FieldVm Pick(int i, string label, IList<string> choices)
        {
            int raw = step.Args[i];
            var options = choices.ToList();
            bool other = raw >= options.Count;
            if (other) options.Add($"{raw:X2} (other)");
            var field = new FieldVm { Kind = FieldKind.List, Label = label, Choices = options, Changed = Changed,
                                  SetValue = index => { if (index >= 0) step.Args[i] = (byte)(other && index == options.Count - 1 ? raw : index); } };
            field.Value = other ? options.Count - 1 : raw;
            return field;
        }
        FieldVm Step(string label)
        {
            var field = new FieldVm { Kind = FieldKind.Step, Label = label, Steps = _steps, Changed = Changed, SetTarget = target => step.Target = target?.Step };
            field.Target = _steps.FirstOrDefault(x => x.Step == step.Target);
            return field;
        }
        var switches = Enumerable.Range(0, 8).Select(n => $"Switch {n}").ToList();
        var numbers = Enumerable.Range(0, 4).Select(n => $"Number {n}").ToList();
        FieldVm PickValue(int i, string label, (int Value, string Name)[] choices)
        {
            int raw = step.Args[i];
            (int Value, string Name)[] all = Array.FindIndex(choices, c => c.Value == raw) >= 0 ? choices : choices.Append((Value: raw, Name: $"{raw:X2} (other)")).ToArray();
            var field = new FieldVm { Kind = FieldKind.List, Label = label, Choices = all.Select(c => c.Name).ToList(), Changed = Changed,
                                  SetValue = index => { if (index >= 0) step.Args[i] = (byte)all[index].Value; } };
            field.Value = Array.FindIndex(all, c => c.Value == raw);
            return field;
        }
        FieldVm Signed(int i, string label)
        {
            var field = new FieldVm
            {
                Kind = FieldKind.Number, Label = label, Hint = "-128 to 127", Changed = Changed,
                Parse = text => { if (!int.TryParse(text, out int number) || number < -128 || number > 127) return false; step.Args[i] = (byte)(sbyte)number; return true; },
            };
            field.Text = ((sbyte)step.Args[i]).ToString();
            return field;
        }
        var stats = MonsterAi.StatChoices.Select(x => char.ToUpper(x[0]) + x[1..]).ToList();
        var comparisons = MonsterAi.CmpChoices.ToList();
        switch (step.Op)
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
            case 0x1E: list.Add(PickValue(0, "Who", MonsterAi.WhoChoices)); list.Add(Pick(1, "Value", stats)); list.Add(Pick(2, "Is", comparisons));
                       list.Add(Word(3, "Than")); list.Add(Step("Go to")); break;
            case 0x1F: list.Add(Pick(0, "Value", stats)); list.Add(Pick(1, "Which", new List<string> { "Highest", "Lowest" })); break;
            case 0x20: list.Add(Pick(0, "Number", numbers)); list.Add(Byte(1, "Set to")); break;
            case 0x21: list.Add(Pick(0, "Number", numbers)); list.Add(Signed(1, "Add")); break;
            case 0x22: list.Add(Pick(0, "Number", numbers)); list.Add(Pick(1, "Is", comparisons)); list.Add(Byte(2, "Than")); list.Add(Step("Go to")); break;
            case 0x23: list.Add(Pick(0, "Of the target's", new List<string> { "Max HP", "Current HP" })); list.Add(Byte(1, "Extra damage", "% (0 = none)")); break;
            case 0x24: list.Add(Pick(0, "Number", numbers)); list.Add(PickValue(1, "From", MonsterAi.WhoValueChoices)); list.Add(Pick(2, "Value", stats)); break;
            case 0x25: list.Add(Byte(0, "Actions this turn", "1-8", 8)); break;
        }
        return list;
    }

    // adding, removing, moving

    private void Add_Click(object sender, RoutedEventArgs e)
    {
        int insertAt = StepList.SelectedIndex < 0 ? _steps.Count : StepList.SelectedIndex + 1;
        // with nothing picked it goes in before a final "end"
        if (StepList.SelectedIndex < 0 && _steps.Count > 0 && _steps[^1].Step.Op == 0x00) insertAt = _steps.Count - 1;
        var vm = new StepVm { Step = new AiScript.Step { Op = 0x05, Args = new byte[1] } };
        _steps.Insert(insertAt, vm);
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
        foreach (var other in _steps) if (other.Step.Target == vm.Step) other.Step.Target = next;
        Refresh();
        StepList.SelectedIndex = Math.Min(i, _steps.Count - 1);
    }

    private void Move(int direction)
    {
        if (StepList.SelectedItem is not StepVm vm) return;
        int i = _steps.IndexOf(vm), j = i + direction;
        if (j < 0 || j >= _steps.Count) return;
        _steps.Move(i, j);
        Refresh();
        StepList.SelectedItem = vm;
    }

    private void Up_Click(object sender, RoutedEventArgs e) => Move(-1);
    private void Down_Click(object sender, RoutedEventArgs e) => Move(1);

    private void Ok_Click(object sender, RoutedEventArgs e)
    {
        var steps = _steps.Select(vm => vm.Step).ToList();
        var missing = steps.FindIndex(step => MonsterAi.JumpArg(step.Op) >= 0 && (step.Target == null || !steps.Contains(step.Target)));
        if (missing >= 0) { MessageBox.Show($"Step {missing + 1} goes to a step that isn't there: pick one.", "AI script"); StepList.SelectedIndex = missing; return; }
        Result = steps;
        DialogResult = true;
    }
}

// picks the template for one of a command's values (number, list or step)
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
