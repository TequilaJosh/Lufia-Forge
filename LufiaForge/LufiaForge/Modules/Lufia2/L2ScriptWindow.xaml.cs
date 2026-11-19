using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using LufiaForge.Core;
using LufiaForge.Core.Lufia2;
using Arg = LufiaForge.Core.Lufia2.L2AiScripts.Arg;
using Kind = LufiaForge.Core.Lufia2.L2AiScripts.Kind;
using Step = LufiaForge.Core.Lufia2.L2AiScripts.Step;
using Button = System.Windows.Controls.Button;
using CheckBox = System.Windows.Controls.CheckBox;
using ComboBox = System.Windows.Controls.ComboBox;
using Orientation = System.Windows.Controls.Orientation;
using TextBox = System.Windows.Controls.TextBox;

namespace LufiaForge.Modules.Lufia2;

// edits a Lufia II monster's or capsule monster's battle script (its turn, or when it's hit) step by step. each
// step's command comes from a list and its values go in fields (spells, monsters, items, stats by name, "go to" picks a
// step). Save writes it to Lufia Forge's script table (L2AiScripts), the game's own bytes stay where they are
public partial class L2ScriptWindow : Window
{
    private readonly RomBuffer _rom;
    private readonly Kind _kind;
    private readonly int _index;
    private readonly L2AiScripts.NameLists _names;
    private readonly List<Step> _steps;
    private readonly bool _edited;
    private bool _loading;

    // what was done (for Undo and the status bar), if the window closes with a change
    public string Change { get; private set; } = "";

    private sealed record OpChoice(L2AiScripts.OpDef Def)
    {
        public override string ToString() => $"{Def.Group}: {Def.Name}  ({Def.Op:X2})";
    }

    public L2ScriptWindow(RomBuffer rom, Kind kind, int index, string who)
    {
        InitializeComponent();
        _rom = rom; _kind = kind; _index = index;
        _names = L2AiScripts.NameLists.From(rom);
        string unreadable = "";
        _edited = L2AiScripts.Override(rom, kind, index) != null;
        try { _steps = L2AiScripts.Load(rom, kind, index).Steps; }
        catch (Exception ex)
        {
            // (some hacks point a few monsters' scripts at other data)
            _steps = new List<Step>();
            unreadable = $" Its current script can't be read ({ex.Message}); saving gives it this new one.";
        }
        bool attack = kind is Kind.MonsterAttack or Kind.CapsuleAi;
        Title = $"{who}: {L2AiScripts.KindName(kind)}";
        HeaderText.Text = Title;
        string start = "";
        if (_steps.Count == 0)
        {
            // no script, so start from what the game does then
            if (kind == Kind.Weapon)
            {
                _steps = new List<Step> { L2AiScripts.New(0x37), L2AiScripts.New(0x00) };
                start = " It had no weapon script (the game then just does a weapon attack), so this starts as that attack: add effects around it (an element, a status, critical hits...).";
            }
            else if (L2AiScripts.IsEffect(kind))
            {
                _steps = new List<Step> { L2AiScripts.New(0x00) };
                start = $" It had no script for this ({L2AiScripts.KindName(kind)}), so this starts as an empty one.";
            }
            else
            {
                _steps = attack ? new List<Step> { Make(0x32, 1), L2AiScripts.New(0x28), L2AiScripts.New(0x00) } : new List<Step> { L2AiScripts.New(0x00) };
                start = attack ? " It had no script (the game then just attacks), so this starts as that plain attack."
                               : " It had no script for being hit, so this starts as an empty one.";
            }
        }
        if (kind == Kind.IpAttack && L2AiScripts.IpAttacksWithEffect(rom, L2AiScripts.IpEffect(rom, index)) is { Count: > 1 } sharing)
            start += $" This effect is shared by {sharing.Count} IP attacks ({string.Join(", ", sharing.Select(i => L2Battle.ReadIp(rom, i).Name))}): a change applies to all of them.";
        InfoText.Text = (_edited ? "This script was edited in Lufia Forge (the game's own one is kept and comes back with 'Back to the game's script')."
                                 : "The game's own script. Saving keeps the game's bytes as they are and puts the edited script in the ROM's added space.") + start + unreadable;
        RevertButton.IsEnabled = _edited;
        foreach (var def in L2AiScripts.Ops.OrderBy(o => GroupOrder(o.Group))) OpBox.Items.Add(new OpChoice(def));   // (the list's own order, common ones first)
        RefreshList(0);
        // Ctrl+Z / Ctrl+Y take back changes to the steps (text boxes keep their own while typing)
        PreviewKeyDown += (_, e) =>
        {
            if ((System.Windows.Input.Keyboard.Modifiers & System.Windows.Input.ModifierKeys.Control) == 0 || System.Windows.Input.Keyboard.FocusedElement is System.Windows.Controls.Primitives.TextBoxBase) return;
            bool shift = (System.Windows.Input.Keyboard.Modifiers & System.Windows.Input.ModifierKeys.Shift) != 0;
            if (e.Key == System.Windows.Input.Key.Z && !shift) { UndoStep(); e.Handled = true; }
            else if (e.Key == System.Windows.Input.Key.Y || e.Key == System.Windows.Input.Key.Z && shift) { RedoStep(); e.Handled = true; }
        };
        if (!L2AiScripts.IsInstalled(rom) && !L2AiScripts.CanInstall(rom, out var why)) StatusText.Text = why;
    }

    private static int GroupOrder(string group) => group switch
    {
        "Actions" => 0, "Choices and jumps" => 1, "Show and sound" => 2, "Effects of a special attack" => 3, "Numbers" => 4, _ => 5,
    };

    private static Step Make(byte op, params int[] values)
    {
        var step = L2AiScripts.New(op);
        for (int i = 0; i < values.Length; i++) L2AiScripts.SetArg(step, i, values[i]);
        return step;
    }

    private Step? Current => StepList.SelectedIndex >= 0 && StepList.SelectedIndex < _steps.Count ? _steps[StepList.SelectedIndex] : null;

    private string Line(int i) => $"{i + 1,3}  {L2AiScripts.Describe(_steps[i], _steps, _names)}";

    private void RefreshList(int select)
    {
        _loading = true;
        StepList.Items.Clear();
        for (int i = 0; i < _steps.Count; i++) StepList.Items.Add(Line(i));
        _loading = false;
        StepList.SelectedIndex = Math.Clamp(select, -1, _steps.Count - 1);
        if (StepList.SelectedIndex < 0) ShowStep();
        StepList.ScrollIntoView(StepList.SelectedItem);
        Record();
    }

    // undo / redo inside the window (every change to the steps, until Save)

    private readonly List<(List<Step> Steps, int Selected)> _undo = new(), _redo = new();
    private (List<Step> Steps, int Selected)? _last;
    private bool _restoring;

    // a copy of the steps, with their "go to"s pointing at the copies
    private static List<Step> CloneSteps(List<Step> steps)
    {
        var copy = steps.Select(s => s.Clone()).ToList();
        for (int i = 0; i < steps.Count; i++)
            for (int t = 0; t < steps[i].Targets.Length; t++)
            {
                var target = steps[i].Targets[t];
                int targetPos = target == null ? -1 : steps.IndexOf(target);
                copy[i].Targets[t] = targetPos >= 0 ? copy[targetPos] : null;
            }
        return copy;
    }

    // after a change, the state before it becomes an undo step
    private void Record()
    {
        if (_restoring) return;
        var now = (CloneSteps(_steps), StepList.SelectedIndex);
        if (_last is { } before && Same(before.Steps, now.Item1)) { _last = now; return; }
        if (_last is { } prev) { _undo.Add(prev); _redo.Clear(); }
        _last = now;
        UpdateUndoButtons();
    }

    private static bool Same(List<Step> a, List<Step> b) =>
        a.Count == b.Count && a.Zip(b).All(p => p.First.Op == p.Second.Op && p.First.Args.SequenceEqual(p.Second.Args)
            && p.First.Targets.Select(t => t == null ? -1 : a.IndexOf(t)).SequenceEqual(p.Second.Targets.Select(t => t == null ? -1 : b.IndexOf(t))));

    private void Restore((List<Step> Steps, int Selected) state)
    {
        _restoring = true;
        try
        {
            _steps.Clear();
            _steps.AddRange(CloneSteps(state.Steps));
            RefreshList(state.Selected);
            ShowStep();
        }
        finally { _restoring = false; }
        _last = (CloneSteps(_steps), StepList.SelectedIndex);
        UpdateUndoButtons();
    }

    private void UndoStep()
    {
        if (_undo.Count == 0 || _last is not { } now) return;
        var state = _undo[^1]; _undo.RemoveAt(_undo.Count - 1);
        _redo.Add(now);
        Restore(state);
        StatusText.Text = "Undone (Ctrl+Y puts it back).";
    }

    private void RedoStep()
    {
        if (_redo.Count == 0 || _last is not { } now) return;
        var state = _redo[^1]; _redo.RemoveAt(_redo.Count - 1);
        _undo.Add(now);
        Restore(state);
        StatusText.Text = "Redone.";
    }

    private void UpdateUndoButtons()
    {
        if (UndoButton == null) return;
        UndoButton.IsEnabled = _undo.Count > 0;
        RedoButton.IsEnabled = _redo.Count > 0;
    }

    private void Undo_Click(object sender, RoutedEventArgs e) => UndoStep();
    private void Redo_Click(object sender, RoutedEventArgs e) => RedoStep();

    // after a value changed, redo the lines (other steps' "go to" texts too) and the bytes
    private void Changed()
    {
        _loading = true;
        int keep = StepList.SelectedIndex;
        for (int i = 0; i < _steps.Count; i++) if ((string)StepList.Items[i] != Line(i)) StepList.Items[i] = Line(i);
        StepList.SelectedIndex = keep;
        _loading = false;
        ShowBytes();
        Record();
    }

    private void ShowBytes()
    {
        try
        {
            var bytes = L2AiScripts.Build(_steps, 1);
            BytesText.Text = $"{bytes.Length} bytes: {BitConverter.ToString(bytes).Replace('-', ' ')}";
        }
        catch (Exception ex) { BytesText.Text = ex.Message; }
        UpdatePreview();
    }

    private void StepList_SelectionChanged(object sender, SelectionChangedEventArgs e) { if (!_loading) ShowStep(); }

    private void ShowStep()
    {
        _loading = true;
        try
        {
            ArgPanel.Children.Clear();
            var step = Current;
            StepHeader.Text = step == null ? "STEP" : $"STEP {StepList.SelectedIndex + 1}";
            OpBox.IsEnabled = step != null;
            if (step == null) { OpBox.SelectedIndex = -1; OpHelp.Text = ""; ShowBytes(); return; }
            OpBox.SelectedItem = OpBox.Items.Cast<OpChoice>().FirstOrDefault(c => c.Def.Op == step.Op);
            var def = step.Def!;
            OpHelp.Text = def.Help;
            int jump = 0;
            for (int i = 0; i < def.Args.Length; i++)
                ArgPanel.Children.Add(Field(step, def.Args[i], i, def.Args[i] == Arg.Jump ? jump++ : -1));
            ShowBytes();
        }
        finally { _loading = false; }
    }

    private void OpBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading || Current is not { } step || OpBox.SelectedItem is not OpChoice choice || choice.Def.Op == step.Op) return;
        // the step keeps its place (and the steps that go to it), its values start at zero
        var fresh = L2AiScripts.New(choice.Def.Op);
        step.Op = fresh.Op; step.Args = fresh.Args; step.Targets = fresh.Targets; step.OutsideTargets = fresh.OutsideTargets;
        if (choice.Def.Op == 0x32) L2AiScripts.SetArg(step, 0, 1);   // the usual target: one random foe
        // a new "go to" goes to the next step (none after the last one, Save asks for one. a step going to
        // itself would loop forever in the battle)
        for (int j = 0; j < step.Targets.Length; j++) step.Targets[j] = _steps.ElementAtOrDefault(StepList.SelectedIndex + 1);
        Changed();
        ShowStep();
    }

    // fields

    private static string Label(Arg arg, int argIndex, L2AiScripts.OpDef def) => L2AiScripts.ArgInfo(def.Op, argIndex)?.Label ?? arg switch
    {
        Arg.Message => "message",
        Arg.NewStatus => "status",
        Arg.Jump => "go to",
        Arg.Chance => "chance (of 256)",
        Arg.Percent => "percent",
        Arg.Register => def.Args.Count(x => x == Arg.Register) > 1 ? $"number {argIndex + 1}" : "number",
        Arg.Value => "value",
        Arg.Spell => "spell",
        Arg.Monster => "monster",
        Arg.Item => "item",
        Arg.CapsuleAttack => "capsule attack",
        Arg.Element => "element",
        Arg.Stat => "stat",
        Arg.Status => "status",
        Arg.Target => "target",
        Arg.Restore => "what",
        Arg.Library => "shared script",
        Arg.AttackName => "name shown",
        Arg.Change => "change (%)",
        Arg.Animation => "animation",
        Arg.Sound => "sound",
        _ => def.Args.Length > 1 ? $"value {argIndex + 1}" : "value",
    };

    private FrameworkElement Field(Step step, Arg arg, int argIndex, int jump)
    {
        var def = step.Def!;
        var row = new DockPanel { Margin = new Thickness(0, 4, 0, 4) };
        row.Children.Add(new TextBlock { Text = Label(arg, argIndex, def), Width = 120, VerticalAlignment = System.Windows.VerticalAlignment.Center, Style = (Style)FindResource("GdFieldLabel") });
        FrameworkElement editor = arg switch
        {
            Arg.Jump => StepPicker(step, jump),
            Arg.Spell => List(step, argIndex, _names.Spells),
            Arg.Monster => List(step, argIndex, _names.Monsters),
            Arg.CapsuleAttack => List(step, argIndex, _names.CapsuleAttacks),
            Arg.Item => List(step, argIndex, _names.Items, 0x1FF),
            Arg.Stat => List(step, argIndex, L2AiScripts.Stats),
            Arg.Status => List(step, argIndex, L2AiScripts.Statuses),
            Arg.NewStatus => List(step, argIndex, step.Op == 0x5D ? L2StatusPatch.Names : L2StatusPatch.Names.Append(L2StatusPatch.EveryName).ToArray()),   // 10: all 16 (cure, resist, ward)
            Arg.Target => List(step, argIndex, L2AiScripts.Targets, plain: true),
            Arg.AttackName => NameField(step, argIndex),
            Arg.Message => List(step, argIndex, Enumerable.Range(0, 0x30).Select(L2AiScripts.MessageText).ToArray()),
            Arg.Change => Signed(step, argIndex),
            Arg.Restore => List(step, argIndex, L2AiScripts.Restores),
            Arg.Animation when _names.Animations.Length == 0x100 => List(step, argIndex, _names.Animations),
            Arg.Element => Elements(step, argIndex),
            Arg.Value => ValueField(step, argIndex),
            Arg.Register => RegisterField(step, argIndex),
            Arg.Library => LibraryField(step, argIndex),
            _ => Number(step, argIndex, hex: arg is Arg.Animation or Arg.Sound),
        };
        row.Children.Add(editor);
        // what the number means, under its field
        if (L2AiScripts.ArgInfo(def.Op, argIndex) is not { Help.Length: > 0 } info) return row;
        var both = new StackPanel();
        both.Children.Add(row);
        both.Children.Add(new TextBlock { Text = info.Help, Style = (Style)FindResource("GdHint"), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(120, 0, 0, 6) });
        return both;
    }

    // a shared script: picked from the list (each with what it does), plus a button that copies its steps in here
    // so they can be changed for this monster alone
    private FrameworkElement LibraryField(Step step, int argIndex)
    {
        var panel = new StackPanel();
        var box = Combo();
        for (int libraryId = 0; libraryId < L2AiScripts.LibraryCount; libraryId++) box.Items.Add($"{libraryId:X2}  {_names.Library[libraryId]}");
        int current = L2AiScripts.ArgValue(step, argIndex);
        if (current >= L2AiScripts.LibraryCount) box.Items.Add($"{current:X2}  (not in the game's list)");
        box.SelectedIndex = Math.Min(current, box.Items.Count - 1);
        box.SelectionChanged += (_, _) =>
        {
            if (_loading || box.SelectedIndex < 0 || box.SelectedIndex >= L2AiScripts.LibraryCount) return;
            L2AiScripts.SetArg(step, argIndex, box.SelectedIndex);
            Changed();
        };
        panel.Children.Add(box);
        var copy = new Button
        {
            Content = "Copy its steps in here, to change them",
            Style = (Style)FindResource("LufiaButton"), Padding = new Thickness(10, 4, 10, 4), Margin = new Thickness(0, 8, 0, 0),
            HorizontalAlignment = System.Windows.HorizontalAlignment.Left,
            ToolTip = "Every monster that calls this shared script shares it. Copied in, its steps belong to this script alone and can be edited, removed or added to.",
        };
        copy.Click += (_, _) =>
        {
            int insertAt = StepList.SelectedIndex;
            try
            {
                int stepCount = L2AiScripts.InlineLibrary(_rom, _steps, insertAt);
                RefreshList(insertAt);
                StatusText.Text = $"Shared script {current:X2} copied in as steps {insertAt + 1}-{insertAt + stepCount}: change them as you like, then Save.";
            }
            catch (Exception ex) { StatusText.Text = ex.Message; }
        };
        panel.Children.Add(copy);
        return panel;
    }

    // the attack name shown. picked from the game's names (and the added ones), or typed: a name nothing else has
    // becomes a new name (L2AttackNames, written at Save), and an added name only this window made gets changed in place
    private FrameworkElement NameField(Step step, int argIndex)
    {
        var panel = new StackPanel();
        string[] Names() => _names.AttackNames;   // (the typed ones at the end)
        var box = Combo();
        void Fill()
        {
            box.Items.Clear();
            foreach (var (name, number) in Names().Select((n, k) => (n, k))) box.Items.Add($"{number:X2}  {name}");
            int current = L2AiScripts.ArgValue(step, argIndex);
            if (current >= box.Items.Count) box.Items.Add($"{current:X2}  (not in the list)");
            box.SelectedIndex = Math.Min(current, box.Items.Count - 1);
        }
        Fill();
        box.SelectionChanged += (_, _) =>
        {
            if (_loading || box.SelectedIndex < 0 || box.SelectedIndex >= Names().Length) return;
            L2AiScripts.SetArg(step, argIndex, box.SelectedIndex);
            Changed();
        };
        panel.Children.Add(box);
        var row = new DockPanel { Margin = new Thickness(0, 6, 0, 0) };
        var useButton = new Button { Content = "Use this name", Style = (Style)FindResource("LufiaButton"), Padding = new Thickness(8, 2, 8, 2), Margin = new Thickness(6, 0, 0, 0) };
        DockPanel.SetDock(useButton, Dock.Right);
        var nameBox = new TextBox { Style = (Style)FindResource("GdText"), MaxLength = L2Attacks.NameMax, ToolTip = $"Your own name for it (up to {L2Attacks.NameMax} letters)" };
        int now = L2AiScripts.ArgValue(step, argIndex);
        nameBox.Text = now > 0 && now < Names().Length ? Names()[now] : "";
        row.Children.Add(useButton); row.Children.Add(nameBox);
        panel.Children.Add(new TextBlock { Text = "or type your own:", Style = (Style)FindResource("GdHint"), Margin = new Thickness(0, 6, 0, 0) });
        panel.Children.Add(row);
        void Use()
        {
            string typed = nameBox.Text.Trim();
            if (L2AttackNames.Check(_rom, typed) is { } bad) { StatusText.Text = "Name not used: " + bad; return; }
            var all = Names();
            int found = Array.FindIndex(all, n => n == typed);
            int current = L2AiScripts.ArgValue(step, argIndex);
            if (found > 0) { L2AiScripts.SetArg(step, argIndex, found); StatusText.Text = $"\"{typed}\" is name {found:X2}: used."; }
            else if (current >= _romNames && current - _romNames < _typed.Count && _steps.Count(x => x.Op == 0x1E && x.Args[0] == current) == 1)
            {
                _typed[current - _romNames].Text = typed;   // a name this window made, used by this step only, so just change it
                _names.AttackNames[current] = typed;
                StatusText.Text = $"Name {current:X2} is \"{typed}\" now (written when you Save).";
            }
            else
            {
                if (L2AttackNames.CantAdd(_rom) is { } no || _names.AttackNames.Length > L2AttackNames.Last) { StatusText.Text = "Name not added: " + (L2AttackNames.CantAdd(_rom) ?? "no room left."); return; }
                int newIndex = _names.AttackNames.Length;
                _typed.Add(new TypedName { Text = typed, Animation = L2Attacks.NameAnimation(_rom, current) });
                _names.AttackNames = _names.AttackNames.Append(typed).ToArray();
                L2AiScripts.SetArg(step, argIndex, newIndex);
                StatusText.Text = $"\"{typed}\" is a new name ({newIndex:X2}): it's added when you Save, and other monsters can use it too.";
            }
            _loading = true; Fill(); _loading = false;
            Changed();
        }
        useButton.Click += (_, _) => Use();
        nameBox.KeyDown += (_, e) => { if (e.Key == System.Windows.Input.Key.Enter) { Use(); e.Handled = true; } };
        return panel;
    }

    // names typed in this window that aren't in the ROM yet (numbers after the ROM's last name, in order)
    private sealed class TypedName { public string Text = ""; public int Animation = 0xE0; }
    private readonly List<TypedName> _typed = new();
    private int _romNames => _names.AttackNames.Length - _typed.Count;

    private ComboBox Combo() => new() { Style = (Style)FindResource("GdCombo"), HorizontalAlignment = System.Windows.HorizontalAlignment.Stretch, Width = double.NaN, MaxDropDownHeight = 480 };

    private FrameworkElement StepPicker(Step step, int jump)
    {
        var box = Combo();
        for (int k = 0; k < _steps.Count; k++) box.Items.Add(Line(k).TrimStart());
        var target = step.Targets[jump];
        box.SelectedIndex = target == null ? -1 : _steps.IndexOf(target);
        box.SelectionChanged += (_, _) =>
        {
            if (_loading || box.SelectedIndex < 0) return;
            step.Targets[jump] = _steps[box.SelectedIndex];
            step.OutsideTargets[jump] = -1;
            Changed();
        };
        return box;
    }

    private FrameworkElement List(Step step, int argIndex, string[] names, int mask = 0xFFFF, bool plain = false)
    {
        var box = Combo();
        foreach (var (name, number) in names.Select((n, k) => (n, k))) box.Items.Add(plain ? name : $"{number:X2}  {name}");
        int value = L2AiScripts.ArgValue(step, argIndex);
        if ((value & mask) >= names.Length) box.Items.Add($"{value & mask:X2}  (not in the list)");
        box.SelectedIndex = Math.Min(value & mask, box.Items.Count - 1);
        box.SelectionChanged += (_, _) =>
        {
            if (_loading || box.SelectedIndex < 0 || box.SelectedIndex >= names.Length) return;
            L2AiScripts.SetArg(step, argIndex, (L2AiScripts.ArgValue(step, argIndex) & ~mask) | box.SelectedIndex);
            Changed();
        };
        return box;
    }

    private FrameworkElement Elements(Step step, int argIndex)
    {
        var wrap = new WrapPanel();
        int bits = L2AiScripts.ArgValue(step, argIndex);
        var hint = new TextBlock { Style = (Style)FindResource("GdHint"), Width = 300, Margin = new Thickness(0, 2, 0, 0) };
        void Hint() => hint.Text = $"= {L2AiScripts.ElementText(L2AiScripts.ArgValue(step, argIndex))} ({L2AiScripts.ArgValue(step, argIndex):X4})";
        foreach (var (bit, name) in L2AiScripts.Elements)
        {
            var checkBox = new CheckBox { Content = name, IsChecked = (bits & bit) != 0, Width = 100, Margin = new Thickness(0, 1, 0, 1), Foreground = (System.Windows.Media.Brush)FindResource("TextPrimaryBrush") };
            checkBox.Click += (_, _) =>
            {
                int now = L2AiScripts.ArgValue(step, argIndex);
                L2AiScripts.SetArg(step, argIndex, checkBox.IsChecked == true ? now | bit : now & ~bit);
                Hint(); Changed();
            };
            wrap.Children.Add(checkBox);
        }
        Hint();
        wrap.Children.Add(hint);
        return wrap;
    }

    // the registers offered by name (the known ones, then every other one as "shared value XX")
    private static readonly int[] RegisterChoices = L2AiScripts.KnownRegisters
        .Concat(Enumerable.Range(0, 0x100).Except(L2AiScripts.KnownRegisters)).ToArray();

    private ComboBox RegisterBox(int value)
    {
        var box = Combo();
        foreach (int register in RegisterChoices) box.Items.Add($"{L2AiScripts.RegisterName(register)}  ({register:X2})");
        box.SelectedIndex = Array.IndexOf(RegisterChoices, value & 0xFF);
        return box;
    }

    // a register, picked by what it holds
    private FrameworkElement RegisterField(Step step, int argIndex)
    {
        var box = RegisterBox(L2AiScripts.ArgValue(step, argIndex));
        box.SelectionChanged += (_, _) =>
        {
            if (_loading || box.SelectedIndex < 0) return;
            L2AiScripts.SetArg(step, argIndex, RegisterChoices[box.SelectedIndex]);
            Changed();
        };
        return box;
    }

    // a value: a number, or what a register holds
    private FrameworkElement ValueField(Step step, int argIndex)
    {
        var panel = new StackPanel();
        var mode = new ComboBox { Style = (Style)FindResource("GdCombo"), Width = 160, HorizontalAlignment = System.Windows.HorizontalAlignment.Left };
        mode.Items.Add("a number"); mode.Items.Add("what a number holds");
        var box = new TextBox { Style = (Style)FindResource("GdNumber"), Width = 90, Margin = new Thickness(0, 4, 0, 0), HorizontalAlignment = System.Windows.HorizontalAlignment.Left };
        int value = L2AiScripts.ArgValue(step, argIndex);
        bool isRegister = (value >> 8) == 0x80;
        var regBox = RegisterBox(isRegister ? value : 0x2A);
        regBox.Margin = new Thickness(0, 4, 0, 0);
        mode.SelectedIndex = isRegister ? 1 : 0;
        box.Text = isRegister ? "0" : ((short)value).ToString(CultureInfo.InvariantCulture);
        void Show() { box.Visibility = mode.SelectedIndex == 0 ? Visibility.Visible : Visibility.Collapsed; regBox.Visibility = mode.SelectedIndex == 1 ? Visibility.Visible : Visibility.Collapsed; }
        void Write()
        {
            Show();
            if (_loading) return;
            if (mode.SelectedIndex == 1)
            {
                if (regBox.SelectedIndex < 0) return;
                L2AiScripts.SetArg(step, argIndex, 0x8000 | RegisterChoices[regBox.SelectedIndex]);
            }
            else if (int.TryParse(box.Text, NumberStyles.Integer, CultureInfo.InvariantCulture, out int number) && number is >= -32768 and <= 0x7FFF && (number & 0xFF00) != 0x8000)
                L2AiScripts.SetArg(step, argIndex, number & 0xFFFF);
            else { StatusText.Text = "A number is -32768 to 32767."; return; }
            StatusText.Text = "";
            Changed();
        }
        mode.SelectionChanged += (_, _) => Write();
        box.TextChanged += (_, _) => Write();
        regBox.SelectionChanged += (_, _) => Write();
        Show();
        panel.Children.Add(mode); panel.Children.Add(box); panel.Children.Add(regBox);
        return panel;
    }

    private FrameworkElement Number(Step step, int argIndex, bool hex)
    {
        var def = step.Def!;
        int max = L2AiScripts.Size(def.Args[argIndex]) == 2 ? 0xFFFF : 0xFF;
        var panel = new StackPanel { Orientation = Orientation.Horizontal };
        var box = new TextBox { Style = (Style)FindResource("GdNumber"), Width = 70 };
        int value = L2AiScripts.ArgValue(step, argIndex);
        box.Text = hex ? value.ToString(max > 0xFF ? "X4" : "X2") : value.ToString(CultureInfo.InvariantCulture);
        var hint = new TextBlock { Style = (Style)FindResource("GdHint"), VerticalAlignment = System.Windows.VerticalAlignment.Center, Margin = new Thickness(6, 0, 0, 0) };
        void Hint() => hint.Text = (hex ? "hex" : $"0-{max}") + (def.Args[argIndex] is Arg.Chance or Arg.Percent ? "  " + L2AiScripts.ArgText(def.Args[argIndex], L2AiScripts.ArgValue(step, argIndex), _names) : "");
        box.TextChanged += (_, _) =>
        {
            if (_loading) return;
            bool okay = hex ? int.TryParse(box.Text, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out int number)
                            : int.TryParse(box.Text, NumberStyles.Integer, CultureInfo.InvariantCulture, out number);
            if (!okay || number < 0 || number > max) { StatusText.Text = hex ? $"A value is 0-{max:X} (hex)." : $"A value is 0-{max}."; return; }
            StatusText.Text = "";
            L2AiScripts.SetArg(step, argIndex, number);
            Hint(); Changed();
        };
        Hint();
        panel.Children.Add(box); panel.Children.Add(hint);
        return panel;
    }

    // a signed percentage (-128 to 127), for raising or lowering a stat
    private FrameworkElement Signed(Step step, int argIndex)
    {
        var panel = new StackPanel { Orientation = Orientation.Horizontal };
        var box = new TextBox { Style = (Style)FindResource("GdNumber"), Width = 70, Text = ((sbyte)L2AiScripts.ArgValue(step, argIndex)).ToString(CultureInfo.InvariantCulture) };
        box.TextChanged += (_, _) =>
        {
            if (_loading) return;
            if (!int.TryParse(box.Text, NumberStyles.Integer, CultureInfo.InvariantCulture, out int change) || change is < -128 or > 127) { StatusText.Text = "A change is -128 to 127 (minus lowers the stat)."; return; }
            StatusText.Text = "";
            L2AiScripts.SetArg(step, argIndex, change & 0xFF);
            Changed();
        };
        panel.Children.Add(box);
        panel.Children.Add(new TextBlock { Text = "% (minus lowers it)", Style = (Style)FindResource("GdHint"), VerticalAlignment = System.Windows.VerticalAlignment.Center, Margin = new Thickness(6, 0, 0, 0) });
        return panel;
    }

    // ready-made actions

    private static Step Op(byte op, params int[] values) => Make(op, values);

    // the usual shapes of a monster's turn, the way the game's own scripts write them. each one is a list of steps,
    // a "go to" is given as the index of the step it goes to in the same list
    private static readonly (string Name, string Help, Func<List<Step>> Build)[] Ready =
    {
        ("Attack a random hero", "Pick a random hero, attack.", () => new() { Op(0x32, 1), Op(0x28), Op(0x00) }),
        ("Cast a spell at a random hero", "Pick the spell after adding it (it needs the MP).", () => new() { Op(0x32, 1), Op(0x2C, 4), Op(0x00) }),
        ("Cast a spell on itself", "For healing or raising its own stats.", () => new() { Op(0x32, 6), Op(0x2C, 0x19), Op(0x00) }),
        ("Cast a spell on all heroes", "", () => new() { Op(0x32, 1), Op(0x32, 5), Op(0x2C, 4), Op(0x00) }),
        ("A special attack with its own name", "Physical damage, a name at the top and an animation, like a dragon's breath. Set the damage, element, name and animation.",
            () => new() { Op(0x32, 1), Op(0x21, 0, 100, 0), Op(0x1E, 1), Op(0x5A, 0xE0), Op(0x00) }),
        ("A special attack on all heroes", "", () => new() { Op(0x32, 1), Op(0x32, 5), Op(0x21, 0, 60, 0), Op(0x1E, 1), Op(0x5A, 0xE0), Op(0x00) }),
        ("A draining attack (gets the HP it takes)", "Physical damage; the attacker gets back the HP the target loses (the action's drain flag, after the damage).",
            () => new() { Op(0x32, 1), Op(0x21, 0, 60, 0), Op(0x1E, 1), Op(0x17, 0x26, 0x0002), Op(0x5A, 0x24), Op(0x00) }),
        ("An attack that ignores DFP", "Every hit a critical one of normal damage (critical hits 100%, x1): a critical hit goes through the target's DFP.",
            () => new() { Op(0x32, 1), Op(0x20, 100, 16), Op(0x21, 0, 60, 0), Op(0x1E, 1), Op(0x5A, 0xE0), Op(0x00) }),
        ("An attack that always does the same", "Physical damage with no random part (the damage spread set to 0 after it; the game's is up to 12% more).",
            () => new() { Op(0x32, 1), Op(0x21, 0, 60, 0), Op(0x0C, 0x2B, 0), Op(0x1E, 1), Op(0x5A, 0xE0), Op(0x00) }),
        ("A status attack (poison, sleep...)", "Causes a status with a chance, shows a name and an animation, as the game's poison bites and songs do.",
            () => new() { Op(0x32, 1), Op(0x27, 0, 50), Op(0x1E, 1), Op(0x0C, 0x23, 5), Op(0x5A, 0x24), Op(0x00) }),
        ("Raise or lower a stat", "Changes the target's stat for the battle by a percentage.",
            () => new() { Op(0x32, 6), Op(0x25, 5, 25), Op(0x1E, 1), Op(0x0C, 0x23, 5), Op(0x5A, 0x28), Op(0x00) }),
        ("Call for help (attack when there's no room)", "Calls another monster; when it can't, it attacks instead.",
            () => Linked(new() { Op(0x2D, 0), Op(0x04), Op(0x00), Op(0x32, 1), Op(0x28), Op(0x00) }, (1, 3))),
        ("Half the time attack, half the time a spell", "With a chance (128 of 256) it goes to the second action. Change the chance or the actions.",
            () => Linked(new() { Op(0x05, 128), Op(0x32, 1), Op(0x28), Op(0x00), Op(0x32, 1), Op(0x2C, 4), Op(0x00) }, (0, 4))),
        ("Taunt (show a name, do nothing)", "A laugh, a roar, a warning: the name shows and nothing else happens.",
            () => new() { Op(0x1E, 0x6C), Op(0x41), Op(0x00) }),   // "Ha, ha, ha, ha, ha!"
        ("Defend", "", () => new() { Op(0x29), Op(0x00) }),
        ("Run away", "", () => new() { Op(0x2A), Op(0x00) }),
    };

    private static List<Step> Linked(List<Step> steps, params (int From, int To)[] jumps)
    {
        foreach (var (from, to) in jumps) steps[from].Targets[0] = steps[to];
        return steps;
    }

    private void Ready_Click(object sender, RoutedEventArgs e)
    {
        var menu = new ContextMenu { PlacementTarget = (UIElement)sender, Placement = System.Windows.Controls.Primitives.PlacementMode.Top };
        foreach (var (name, help, build) in Ready)
        {
            var item = new MenuItem { Header = name, ToolTip = help.Length > 0 ? help : null };
            item.Click += (_, _) =>
            {
                var newSteps = build();
                int insertAt = Math.Clamp(StepList.SelectedIndex + 1, 0, _steps.Count);
                _steps.InsertRange(insertAt, newSteps);
                RefreshList(insertAt);
                StatusText.Text = $"Added: {name} (steps {insertAt + 1}-{insertAt + newSteps.Count}). Pick a step to change what it uses.";
            };
            menu.Items.Add(item);
        }
        // the Sinistrals' waves, as their own battle scripts do them (without the counter that times them)
        var waves = new MenuItem { Header = "Sinistral attacks" };
        foreach (var (who, wave) in new[] { ("Gades", "Destructo-wave"), ("Amon", "Chaos wave"), ("Erim", "Devastation wave"), ("Daos", "Terror wave") })
        {
            var steps = SinistralWave(who);
            if (steps == null) continue;
            var item = new MenuItem { Header = $"{wave} ({who})", ToolTip = $"{who}'s wave as the game does it: every hero, " + string.Join(", ", steps.Select(s => L2AiScripts.Describe(s, steps, _names)).Where(t => !t.StartsWith("End"))).ToLowerInvariant() };
            item.Click += (_, _) =>
            {
                var newSteps = SinistralWave(who)!;
                int insertAt = Math.Clamp(StepList.SelectedIndex + 1, 0, _steps.Count);
                _steps.InsertRange(insertAt, newSteps);
                RefreshList(insertAt);
                StatusText.Text = $"Added: {wave} (steps {insertAt + 1}-{insertAt + newSteps.Count}), {who}'s own steps. Change the damage, the statuses or the name if you like; " +
                                  "in front of it a chance (\"With a 50% chance, go to\") makes it one of several actions.";
            };
            waves.Items.Add(item);
        }
        if (waves.Items.Count > 0) { menu.Items.Add(new Separator()); menu.Items.Add(waves); }
        menu.IsOpen = true;
    }

    // a Sinistral's wave from its battle script in the ROM: the steps from its start to its first End, minus the
    // chance and counter steps that time it in the Sinistral's own script (05, 07, 0C, 0E). null if the monster or
    // its wave isn't found (a ROM where they were changed)
    private List<Step>? SinistralWave(string who)
    {
        int monster = Array.FindIndex(_names.Monsters, n => n.Trim() == who);
        if (monster < 0) return null;
        List<Step> steps;
        try { steps = L2AiScripts.LoadOriginal(_rom, L2AiScripts.Kind.MonsterAttack, monster); } catch (Exception) { return null; }
        int end = steps.FindIndex(s => s.Op == 0x00);
        if (end < 0 || !steps.Take(end).Any(s => s.Op == 0x1E)) return null;
        return steps.Take(end + 1).Where(s => s.Op is not (0x05 or 0x07 or 0x0C or 0x0E)).Select(s => s.Clone()).ToList();
    }

    // steps

    private void Insert(int at, Step step)
    {
        _steps.Insert(Math.Clamp(at, 0, _steps.Count), step);
        RefreshList(_steps.IndexOf(step));
    }

    private void Add_Click(object sender, RoutedEventArgs e) => Insert(StepList.SelectedIndex + 1, L2AiScripts.New(0x00));
    private void AddAbove_Click(object sender, RoutedEventArgs e) => Insert(Math.Max(0, StepList.SelectedIndex), L2AiScripts.New(0x00));

    private void Copy_Click(object sender, RoutedEventArgs e)
    {
        if (Current is { } current) Insert(StepList.SelectedIndex + 1, current.Clone());
    }

    private void Move(int by)
    {
        int i = StepList.SelectedIndex, j = i + by;
        if (i < 0 || j < 0 || j >= _steps.Count) return;
        (_steps[i], _steps[j]) = (_steps[j], _steps[i]);
        RefreshList(j);
    }

    private void Up_Click(object sender, RoutedEventArgs e) => Move(-1);
    private void Down_Click(object sender, RoutedEventArgs e) => Move(1);

    private void Delete_Click(object sender, RoutedEventArgs e)
    {
        int i = StepList.SelectedIndex;
        if (i < 0) return;
        var gone = _steps[i];
        _steps.RemoveAt(i);
        // steps that went to it go to the step that took its place
        var next = _steps.ElementAtOrDefault(i) ?? _steps.LastOrDefault();
        foreach (var step in _steps)
            for (int j = 0; j < step.Targets.Length; j++)
                if (ReferenceEquals(step.Targets[j], gone)) step.Targets[j] = next;
        RefreshList(Math.Min(i, _steps.Count - 1));
    }

    // save

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            if (_steps.Count == 0) throw new InvalidOperationException("The script has no steps: it needs at least an End.");
            int bad = _steps.FindIndex(s => s.Targets.Where((t, j) => t == null && s.OutsideTargets[j] < 0).Any());
            if (bad >= 0) throw new InvalidOperationException($"Step {bad + 1}'s \"go to\" has no step picked.");
            int self = _steps.FindIndex(s => s.Op == 0x03 && s.Targets.Length > 0 && ReferenceEquals(s.Targets[0], s));
            if (self >= 0) throw new InvalidOperationException($"Step {self + 1} goes to itself: the battle would never end.");
            if (!_steps.Any(s => s.Def?.EndsPath == true)) throw new InvalidOperationException("The script never ends: add an End (or Quit) step.");
            // names typed here go in first (they get the numbers the steps already use), together with the script or
            // not at all. a failed save used to leave the names in the ROM (outside Undo) and saving again added them twice
            int savedAt = L2Rom.AllOrNothing(_rom, () =>
            {
                foreach (var typedName in _typed) L2AttackNames.Add(_rom, typedName.Text, typedName.Animation);
                return L2AiScripts.Save(_rom, _kind, _index, _steps);
            });
            Change = $"{Title}: {(_edited ? "edited" : "new script")} ({L2AiScripts.Build(_steps, 1).Length} bytes at 0x{savedAt:X6})";
            DialogResult = true;
        }
        catch (Exception ex) { StatusText.Text = "Not saved: " + ex.Message; }
    }

    private void Revert_Click(object sender, RoutedEventArgs e)
    {
        if (L2AiScripts.Revert(_rom, _kind, _index)) { Change = $"{Title}: back to the game's script"; DialogResult = true; }
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => DialogResult = false;
}
