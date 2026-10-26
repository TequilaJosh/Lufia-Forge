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

/// <summary>
/// Edits a Lufia II monster's or capsule monster's battle script (its turn, or when it is hit) step by step: the
/// command of each step from a list, its values in fields (spells, monsters, items, stats by name; "go to" picks a
/// step). Save writes it to Lufia Forge's script table (<see cref="L2AiScripts"/>); the game's own bytes stay.
/// </summary>
public partial class L2ScriptWindow : Window
{
    private readonly RomBuffer _rom;
    private readonly Kind _kind;
    private readonly int _index;
    private readonly L2AiScripts.NameLists _names;
    private readonly List<Step> _steps;
    private readonly bool _edited;
    private bool _loading;

    /// <summary>What was done (for Undo and the status bar), when the window closes with a change.</summary>
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
        catch (Exception e)
        {
            // (some hacks point a few monsters' scripts at other data)
            _steps = new List<Step>();
            unreadable = $" Its current script can't be read ({e.Message}); saving gives it this new one.";
        }
        bool attack = kind is Kind.MonsterAttack or Kind.CapsuleAi;
        Title = $"{who}: {L2AiScripts.KindName(kind)}";
        HeaderText.Text = Title;
        string start = "";
        if (_steps.Count == 0)
        {
            // no script: start from what the game does then
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
        foreach (var d in L2AiScripts.Ops.OrderBy(o => GroupOrder(o.Group)).ThenBy(o => o.Name)) OpBox.Items.Add(new OpChoice(d));
        RefreshList(0);
        if (!L2AiScripts.IsInstalled(rom) && !L2AiScripts.CanInstall(rom, out var why)) StatusText.Text = why;
    }

    private static int GroupOrder(string g) => g switch { "Actions" => 0, "Flow" => 1, "Effects" => 2, "Registers" => 3, "Show and sound" => 4, _ => 5 };

    private static Step Make(byte op, params int[] values)
    {
        var s = L2AiScripts.New(op);
        for (int i = 0; i < values.Length; i++) L2AiScripts.SetArg(s, i, values[i]);
        return s;
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
    }

    /// <summary>After a value changed: the lines (other steps' "go to" texts too) and the bytes.</summary>
    private void Changed()
    {
        _loading = true;
        int keep = StepList.SelectedIndex;
        for (int i = 0; i < _steps.Count; i++) if ((string)StepList.Items[i] != Line(i)) StepList.Items[i] = Line(i);
        StepList.SelectedIndex = keep;
        _loading = false;
        ShowBytes();
    }

    private void ShowBytes()
    {
        try
        {
            var b = L2AiScripts.Build(_steps, 1);
            BytesText.Text = $"{b.Length} bytes: {BitConverter.ToString(b).Replace('-', ' ')}";
        }
        catch (Exception e) { BytesText.Text = e.Message; }
    }

    private void StepList_SelectionChanged(object sender, SelectionChangedEventArgs e) { if (!_loading) ShowStep(); }

    private void ShowStep()
    {
        _loading = true;
        try
        {
            ArgPanel.Children.Clear();
            var s = Current;
            StepHeader.Text = s == null ? "STEP" : $"STEP {StepList.SelectedIndex + 1}";
            OpBox.IsEnabled = s != null;
            if (s == null) { OpBox.SelectedIndex = -1; OpHelp.Text = ""; ShowBytes(); return; }
            OpBox.SelectedItem = OpBox.Items.Cast<OpChoice>().FirstOrDefault(c => c.Def.Op == s.Op);
            var d = s.Def!;
            OpHelp.Text = d.Help;
            int jump = 0;
            for (int i = 0; i < d.Args.Length; i++)
                ArgPanel.Children.Add(Field(s, d.Args[i], i, d.Args[i] == Arg.Jump ? jump++ : -1));
            ShowBytes();
        }
        finally { _loading = false; }
    }

    private void OpBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading || Current is not { } s || OpBox.SelectedItem is not OpChoice c || c.Def.Op == s.Op) return;
        // the step keeps its place (and the steps that go to it); its values start at zero
        var fresh = L2AiScripts.New(c.Def.Op);
        s.Op = fresh.Op; s.Args = fresh.Args; s.Targets = fresh.Targets; s.OutsideTargets = fresh.OutsideTargets;
        if (c.Def.Op == 0x32) L2AiScripts.SetArg(s, 0, 1);   // the usual target: one random foe
        // a new "go to" goes to the next step (none after the last one: Save asks for one; a step going to
        // itself would loop forever in the battle)
        for (int j = 0; j < s.Targets.Length; j++) s.Targets[j] = _steps.ElementAtOrDefault(StepList.SelectedIndex + 1);
        Changed();
        ShowStep();
    }

    // ── fields ──────────────────────────────────────────────────────────────

    private static string Label(Arg a, int i, L2AiScripts.OpDef d) => a switch
    {
        Arg.Jump => "go to",
        Arg.Chance => "chance (of 256)",
        Arg.Percent => "percent",
        Arg.Register => d.Args.Count(x => x == Arg.Register) > 1 ? $"number {i + 1}" : "number",
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
        Arg.AttackName => "attack name",
        Arg.Animation => "animation",
        Arg.Sound => "sound",
        _ => d.Args.Length > 1 ? $"value {i + 1}" : "value",
    };

    private FrameworkElement Field(Step s, Arg a, int i, int jump)
    {
        var d = s.Def!;
        var row = new DockPanel { Margin = new Thickness(0, 4, 0, 4) };
        row.Children.Add(new TextBlock { Text = Label(a, i, d), Width = 120, VerticalAlignment = System.Windows.VerticalAlignment.Center, Style = (Style)FindResource("GdFieldLabel") });
        FrameworkElement editor = a switch
        {
            Arg.Jump => StepPicker(s, jump),
            Arg.Spell => List(s, i, _names.Spells),
            Arg.Monster => List(s, i, _names.Monsters),
            Arg.CapsuleAttack => List(s, i, _names.CapsuleAttacks),
            Arg.Item => List(s, i, _names.Items, 0x1FF),
            Arg.Stat => List(s, i, L2AiScripts.Stats),
            Arg.Status => List(s, i, L2AiScripts.Statuses),
            Arg.Target => List(s, i, L2AiScripts.Targets, plain: true),
            Arg.Restore => List(s, i, L2AiScripts.Restores),
            Arg.Element => Elements(s, i),
            Arg.Value => ValueField(s, i),
            Arg.Register => RegisterField(s, i),
            Arg.Library => LibraryField(s, i),
            _ => Number(s, i, hex: a is Arg.AttackName or Arg.Animation or Arg.Sound),
        };
        row.Children.Add(editor);
        return row;
    }

    /// <summary>A shared script: picked from the list (each with what it does), and a button that copies its steps in
    /// here so they can be changed for this monster alone.</summary>
    private FrameworkElement LibraryField(Step s, int i)
    {
        var panel = new StackPanel();
        var box = Combo();
        for (int n = 0; n < L2AiScripts.LibraryCount; n++) box.Items.Add($"{n:X2}  {_names.Library[n]}");
        int v = L2AiScripts.ArgValue(s, i);
        if (v >= L2AiScripts.LibraryCount) box.Items.Add($"{v:X2}  (not in the game's list)");
        box.SelectedIndex = Math.Min(v, box.Items.Count - 1);
        box.SelectionChanged += (_, _) =>
        {
            if (_loading || box.SelectedIndex < 0 || box.SelectedIndex >= L2AiScripts.LibraryCount) return;
            L2AiScripts.SetArg(s, i, box.SelectedIndex);
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
            int at = StepList.SelectedIndex;
            try
            {
                int n = L2AiScripts.InlineLibrary(_rom, _steps, at);
                RefreshList(at);
                StatusText.Text = $"Shared script {v:X2} copied in as steps {at + 1}-{at + n}: change them as you like, then Save.";
            }
            catch (Exception ex) { StatusText.Text = ex.Message; }
        };
        panel.Children.Add(copy);
        return panel;
    }

    private ComboBox Combo() => new() { Style = (Style)FindResource("GdCombo"), HorizontalAlignment = System.Windows.HorizontalAlignment.Stretch, Width = double.NaN, MaxDropDownHeight = 480 };

    private FrameworkElement StepPicker(Step s, int jump)
    {
        var box = Combo();
        for (int k = 0; k < _steps.Count; k++) box.Items.Add(Line(k).TrimStart());
        var t = s.Targets[jump];
        box.SelectedIndex = t == null ? -1 : _steps.IndexOf(t);
        box.SelectionChanged += (_, _) =>
        {
            if (_loading || box.SelectedIndex < 0) return;
            s.Targets[jump] = _steps[box.SelectedIndex];
            s.OutsideTargets[jump] = -1;
            Changed();
        };
        return box;
    }

    private FrameworkElement List(Step s, int i, string[] names, int mask = 0xFFFF, bool plain = false)
    {
        var box = Combo();
        foreach (var (n, k) in names.Select((n, k) => (n, k))) box.Items.Add(plain ? n : $"{k:X2}  {n}");
        int v = L2AiScripts.ArgValue(s, i);
        if ((v & mask) >= names.Length) box.Items.Add($"{v & mask:X2}  (not in the list)");
        box.SelectedIndex = Math.Min(v & mask, box.Items.Count - 1);
        box.SelectionChanged += (_, _) =>
        {
            if (_loading || box.SelectedIndex < 0 || box.SelectedIndex >= names.Length) return;
            L2AiScripts.SetArg(s, i, (L2AiScripts.ArgValue(s, i) & ~mask) | box.SelectedIndex);
            Changed();
        };
        return box;
    }

    private FrameworkElement Elements(Step s, int i)
    {
        var wrap = new WrapPanel();
        int v = L2AiScripts.ArgValue(s, i);
        var hint = new TextBlock { Style = (Style)FindResource("GdHint"), Width = 300, Margin = new Thickness(0, 2, 0, 0) };
        void Hint() => hint.Text = $"= {L2AiScripts.ElementText(L2AiScripts.ArgValue(s, i))} ({L2AiScripts.ArgValue(s, i):X4})";
        foreach (var (bit, name) in L2AiScripts.Elements)
        {
            var cb = new CheckBox { Content = name, IsChecked = (v & bit) != 0, Width = 100, Margin = new Thickness(0, 1, 0, 1), Foreground = (System.Windows.Media.Brush)FindResource("TextPrimaryBrush") };
            cb.Click += (_, _) =>
            {
                int now = L2AiScripts.ArgValue(s, i);
                L2AiScripts.SetArg(s, i, cb.IsChecked == true ? now | bit : now & ~bit);
                Hint(); Changed();
            };
            wrap.Children.Add(cb);
        }
        Hint();
        wrap.Children.Add(hint);
        return wrap;
    }

    /// <summary>The registers offered by name (the known ones, then every other one as "shared value XX").</summary>
    private static readonly int[] RegisterChoices = L2AiScripts.KnownRegisters
        .Concat(Enumerable.Range(0, 0x100).Except(L2AiScripts.KnownRegisters)).ToArray();

    private ComboBox RegisterBox(int value)
    {
        var box = Combo();
        foreach (int r in RegisterChoices) box.Items.Add($"{L2AiScripts.RegisterName(r)}  ({r:X2})");
        box.SelectedIndex = Array.IndexOf(RegisterChoices, value & 0xFF);
        return box;
    }

    /// <summary>A register: picked by what it holds.</summary>
    private FrameworkElement RegisterField(Step s, int i)
    {
        var box = RegisterBox(L2AiScripts.ArgValue(s, i));
        box.SelectionChanged += (_, _) =>
        {
            if (_loading || box.SelectedIndex < 0) return;
            L2AiScripts.SetArg(s, i, RegisterChoices[box.SelectedIndex]);
            Changed();
        };
        return box;
    }

    /// <summary>A value: a number, or what a register holds.</summary>
    private FrameworkElement ValueField(Step s, int i)
    {
        var panel = new StackPanel();
        var mode = new ComboBox { Style = (Style)FindResource("GdCombo"), Width = 160, HorizontalAlignment = System.Windows.HorizontalAlignment.Left };
        mode.Items.Add("a number"); mode.Items.Add("what a number holds");
        var box = new TextBox { Style = (Style)FindResource("GdNumber"), Width = 90, Margin = new Thickness(0, 4, 0, 0), HorizontalAlignment = System.Windows.HorizontalAlignment.Left };
        int v = L2AiScripts.ArgValue(s, i);
        bool reg = (v >> 8) == 0x80;
        var regBox = RegisterBox(reg ? v : 0x2A);
        regBox.Margin = new Thickness(0, 4, 0, 0);
        mode.SelectedIndex = reg ? 1 : 0;
        box.Text = reg ? "0" : ((short)v).ToString(CultureInfo.InvariantCulture);
        void Show() { box.Visibility = mode.SelectedIndex == 0 ? Visibility.Visible : Visibility.Collapsed; regBox.Visibility = mode.SelectedIndex == 1 ? Visibility.Visible : Visibility.Collapsed; }
        void Write()
        {
            Show();
            if (_loading) return;
            if (mode.SelectedIndex == 1)
            {
                if (regBox.SelectedIndex < 0) return;
                L2AiScripts.SetArg(s, i, 0x8000 | RegisterChoices[regBox.SelectedIndex]);
            }
            else if (int.TryParse(box.Text, NumberStyles.Integer, CultureInfo.InvariantCulture, out int nv) && nv is >= -32768 and <= 0x7FFF && (nv & 0xFF00) != 0x8000)
                L2AiScripts.SetArg(s, i, nv & 0xFFFF);
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

    private FrameworkElement Number(Step s, int i, bool hex)
    {
        var d = s.Def!;
        int max = L2AiScripts.Size(d.Args[i]) == 2 ? 0xFFFF : 0xFF;
        var panel = new StackPanel { Orientation = Orientation.Horizontal };
        var box = new TextBox { Style = (Style)FindResource("GdNumber"), Width = 70 };
        int v = L2AiScripts.ArgValue(s, i);
        box.Text = hex ? v.ToString(max > 0xFF ? "X4" : "X2") : v.ToString(CultureInfo.InvariantCulture);
        var hint = new TextBlock { Style = (Style)FindResource("GdHint"), VerticalAlignment = System.Windows.VerticalAlignment.Center, Margin = new Thickness(6, 0, 0, 0) };
        void Hint() => hint.Text = (hex ? "hex" : $"0-{max}") + (d.Args[i] is Arg.Chance or Arg.Percent ? "  " + L2AiScripts.ArgText(d.Args[i], L2AiScripts.ArgValue(s, i), _names) : "");
        box.TextChanged += (_, _) =>
        {
            if (_loading) return;
            bool okay = hex ? int.TryParse(box.Text, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out int nv)
                            : int.TryParse(box.Text, NumberStyles.Integer, CultureInfo.InvariantCulture, out nv);
            if (!okay || nv < 0 || nv > max) { StatusText.Text = hex ? $"A value is 0-{max:X} (hex)." : $"A value is 0-{max}."; return; }
            StatusText.Text = "";
            L2AiScripts.SetArg(s, i, nv);
            Hint(); Changed();
        };
        Hint();
        panel.Children.Add(box); panel.Children.Add(hint);
        return panel;
    }

    // ── steps ───────────────────────────────────────────────────────────────

    private void Insert(int at, Step s)
    {
        _steps.Insert(Math.Clamp(at, 0, _steps.Count), s);
        RefreshList(_steps.IndexOf(s));
    }

    private void Add_Click(object sender, RoutedEventArgs e) => Insert(StepList.SelectedIndex + 1, L2AiScripts.New(0x00));
    private void AddAbove_Click(object sender, RoutedEventArgs e) => Insert(Math.Max(0, StepList.SelectedIndex), L2AiScripts.New(0x00));

    private void Copy_Click(object sender, RoutedEventArgs e)
    {
        if (Current is { } s) Insert(StepList.SelectedIndex + 1, s.Clone());
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
        foreach (var s in _steps)
            for (int j = 0; j < s.Targets.Length; j++)
                if (ReferenceEquals(s.Targets[j], gone)) s.Targets[j] = next;
        RefreshList(Math.Min(i, _steps.Count - 1));
    }

    // ── save ────────────────────────────────────────────────────────────────

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
            int at = L2AiScripts.Save(_rom, _kind, _index, _steps);
            Change = $"{Title}: {(_edited ? "edited" : "new script")} ({L2AiScripts.Build(_steps, 1).Length} bytes at 0x{at:X6})";
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
