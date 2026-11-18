using System.ComponentModel;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using LufiaForge.Core.Lufia2;
using Kind = LufiaForge.Core.Lufia2.L2AiScripts.Kind;
using Button = System.Windows.Controls.Button;
using CheckBox = System.Windows.Controls.CheckBox;
using Orientation = System.Windows.Controls.Orientation;
using TextBox = System.Windows.Controls.TextBox;

namespace LufiaForge.Modules.Lufia2;

// the BOUNCE AND FOCUS card on the Monsters and Items pages (part of the status patch, L2StatusPatch)
// - Bounce is the game's status bit 6. it sends physical hits back at the attacker, like Mirror does with magic. the game
//   never gives it out. with Lufia Forge a monster can start every battle with it, a worn item can give it to its wearer
//   at the start of every battle, an item used in battle can give or cure it (battle script 27 / 26 with status 6), and
//   it bounces plain weapon attacks too (the game's check only saw scripted ones)
// - Focus (Mewlies' idea for the unused Tag ring and Tag candy): a hero with it hits harder each turn in a row it attacks
//   the same foe with its weapon. a worn item gives it, or an item used in battle gives it for that battle (command 61)
// every change gets written right away
public sealed class L2BounceFocusCard : Border
{
    private L2Page? _page;
    private bool _monsters;
    private readonly StackPanel _body = new() { Width = 470 };

    public L2BounceFocusCard()
    {
        Child = _body;
        Loaded += (_, _) => { if (Style == null && TryFindResource("GdCard") is Style cardStyle) Style = cardStyle; };
    }

    // shows the card for page's records (monsters or items)
    public void Attach(L2Page page, bool monsters)
    {
        if (_page != null) _page.PropertyChanged -= PageChanged;
        _page = page;
        _monsters = monsters;
        page.PropertyChanged += PageChanged;
        Build();
    }

    private void PageChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(L2Page.SelectedIndex) or nameof(L2Page.IsEditable) or nameof(L2Page.Reloads)) Dispatcher.BeginInvoke(Build);
    }

    private TextBlock Hint(string text) => new()
    {
        Text = text, Style = TryFindResource("GdHint") as Style, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(22, 0, 0, 6),
    };

    private void Check(string label, bool on, string hint, Action<bool> write, bool enabled = true)
    {
        var checkBox = new CheckBox { Content = label, IsChecked = on, IsEnabled = enabled, Style = TryFindResource("GdCheck") as Style, Margin = new Thickness(0, 4, 0, 0) };
        checkBox.Click += (_, _) => Do(() => write(checkBox.IsChecked == true), $"{label}: {(checkBox.IsChecked == true ? "on" : "off")}");
        _body.Children.Add(checkBox);
        _body.Children.Add(Hint(hint));
    }

    private void Build()
    {
        _body.Children.Clear();
        _body.Children.Add(new TextBlock { Text = "BOUNCE AND FOCUS", Style = TryFindResource("GdSection") as Style });
        var rom = _page?.PageRom;
        int index = _page?.PageIndex ?? -1;
        if (rom == null || index < 0 || !rom.IsLufia2) return;
        bool patchApplied = L2StatusPatch.IsApplied(rom);
        _body.Children.Add(new TextBlock
        {
            Style = TryFindResource("GdHint") as Style, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 6),
            Text = "Bounce sends physical attacks (plain attacks too) back at the attacker, as Mirror does magic: \"<name> bounces it " +
                   "back!\". It lasts until cured or the battle ends. Focus: a hero hits harder each turn in a row it " +
                   "attacks the same foe with its weapon (\"Maxim's focus: 25% more\")." + (patchApplied ? "" : " Both are new battle code, added to the game with the first one set here."),
        });
        if (_monsters)
        {
            Check("Starts every battle with Bounce", (L2StatusPatch.MonsterGameStart(rom, index) & L2StatusPatch.Bounce) != 0,
                "Physical hits on it come back at whoever hit it. A monster's attack can also cause or cure Bounce (Monster attacks page).",
                on => L2StatusPatch.SetMonsterGameStart(rom, index, on ? L2StatusPatch.MonsterGameStart(rom, index) | L2StatusPatch.Bounce : L2StatusPatch.MonsterGameStart(rom, index) & ~L2StatusPatch.Bounce));
            return;
        }
        var item = L2Battle.ReadItem(rom, index);
        bool equipment = (item.Use & 0x02) != 0, battle = (item.Use & 0x80) != 0;
        Check("Its wearer starts every battle with Bounce", (L2StatusPatch.ItemGameStart(rom, index) & L2StatusPatch.Bounce) != 0,
            equipment ? "Wearing it is enough." : "Only for equipment (this item can't be worn).",
            on => L2StatusPatch.SetItemGameStart(rom, index, on ? L2StatusPatch.ItemGameStart(rom, index) | L2StatusPatch.Bounce : L2StatusPatch.ItemGameStart(rom, index) & ~L2StatusPatch.Bounce),
            equipment);
        Check("Used in battle: gives Bounce", L2StatusSteps.HasStep(rom, Kind.ItemBattle, index, 0x27, 6),
            battle ? "Its targets get Bounce (100%)." : "Tick \"Usable in battle\" above first.",
            on => L2StatusSteps.SetStep(rom, Kind.ItemBattle, index, on, 0x27, 6, 100), battle);
        Check("Used in battle: cures Bounce", L2StatusSteps.HasStep(rom, Kind.ItemBattle, index, 0x26, 6),
            battle ? "Its targets lose Bounce (100%)." : "Tick \"Usable in battle\" above first.",
            on => L2StatusSteps.SetStep(rom, Kind.ItemBattle, index, on, 0x26, 6, 100), battle);
        Check("Focus when worn (as the Tag ring)", L2StatusPatch.ItemFocus(rom, index),
            equipment ? "Its wearer has focus in every battle." : "Only for equipment (this item can't be worn).",
            on => L2StatusPatch.SetItemFocus(rom, index, on), equipment);
        Check("Used in battle: focus for the battle (as the Tag candy)", L2StatusSteps.HasStep(rom, Kind.ItemBattle, index, 0x61),
            battle ? "The heroes it is used on have focus until the battle ends." : "Tick \"Usable in battle\" above first.",
            on => L2StatusSteps.SetStep(rom, Kind.ItemBattle, index, on, 0x61), battle);

        // the focus percents (one set for the whole game)
        var percents = L2StatusPatch.FocusPercents(rom);
        _body.Children.Add(new TextBlock { Text = "Focus bonus, attack in a row on one foe (every focus item):", Style = TryFindResource("GdFieldLabel") as Style, Margin = new Thickness(0, 6, 0, 2) });
        var row = new StackPanel { Orientation = Orientation.Horizontal };
        string[] headers = { "2nd", "3rd", "4th", "5th", "6th+" };
        var boxes = new List<TextBox>();
        for (int i = 0; i < 5; i++)
        {
            row.Children.Add(new TextBlock { Text = headers[i], Style = TryFindResource("GdFieldLabel") as Style, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(i == 0 ? 0 : 8, 0, 4, 0) });
            var box = new TextBox { Text = percents[i].ToString(CultureInfo.InvariantCulture), Width = 40, Style = TryFindResource("GdNumber") as Style, ToolTip = "Percent more damage (0-255)." };
            boxes.Add(box);
            row.Children.Add(box);
            row.Children.Add(new TextBlock { Text = "%", Style = TryFindResource("GdFieldLabel") as Style, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(2, 0, 0, 0) });
        }
        foreach (var box in boxes)
            box.LostFocus += (_, _) =>
            {
                var values = boxes.Select(b => int.TryParse(b.Text.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out int v) && v is >= 0 and <= 255 ? v : -1).ToArray();
                var now = L2StatusPatch.FocusPercents(rom);
                if (values.Any(v => v < 0)) { Dispatcher.BeginInvoke(Build); return; }
                // same as what's shown = nothing to write (and we don't put the status patch in just because a box got visited)
                if (values.Select(v => (byte)v).SequenceEqual(now)) return;
                Do(() => L2StatusPatch.SetFocusPercents(rom, values), "focus bonus " + string.Join(" / ", values.Select(v => "+" + v + "%")));
            };
        _body.Children.Add(row);

        if (!L2StatusPatch.TagItemsDone(rom) && L2StatusPatch.TagItems(rom) is var (rings, candies) && rings.Count + candies.Count > 0)
        {
            var tagButton = new Button
            {
                Content = "Make the Tag ring and Tag candy focus items", Style = TryFindResource("LufiaButton") as Style,
                Margin = new Thickness(0, 10, 0, 0), HorizontalAlignment = System.Windows.HorizontalAlignment.Left,
                ToolTip = "The game never uses them. Mewlies' idea: the Tag ring gives its wearer focus, the Tag candy gives a hero focus for the rest of the battle.",
            };
            tagButton.Click += (_, _) => Do(() => { L2StatusPatch.MakeTagItems(rom); }, "Tag ring and Tag candy made focus items");
            _body.Children.Add(tagButton);
        }
    }

    private void Do(Action write, string? what = null)
    {
        if (_page?.PageRom is not { } rom) return;
        try
        {
            var before = rom.ToArray();
            write();
            if (!rom.SameAs(before)) _page.ReportChange(what ?? "Bounce and focus");
        }
        catch (Exception e) { MessageBox.Show(e.Message, "Bounce and focus", MessageBoxButton.OK, MessageBoxImage.Warning); }
        Dispatcher.BeginInvoke(Build);
    }
}
