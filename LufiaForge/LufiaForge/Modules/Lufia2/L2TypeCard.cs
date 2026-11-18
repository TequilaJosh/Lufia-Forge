using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using LufiaForge.Core.Lufia2;
using Kind = LufiaForge.Core.Lufia2.L2AiScripts.Kind;
using CheckBox = System.Windows.Controls.CheckBox;

namespace LufiaForge.Modules.Lufia2;

// monster types (L2MonsterTypes). on Monsters it's what the monster is (steps in its "when hit" script), on Items and
// Spells it's what the weapon or spell is strong against (its attack's element). a tick writes right away
public sealed class L2TypeCard : Border
{
    private L2Page? _page;
    private Kind _kind;
    private Func<int, Kind?>? _kindOf;
    private readonly StackPanel _body = new() { Width = 470 };
    private bool _loading;

    public L2TypeCard()
    {
        Child = _body;
        Loaded += (_, _) => { if (Style == null && TryFindResource("GdCard") is Style cardStyle) Style = cardStyle; };
    }

    // kind: MonsterDefense for what a monster is, Weapon or Spell for what it's strong against.
    // kindOf picks the script per record (for an item its weapon or its battle use, null hides the card)
    public void Attach(L2Page page, Kind kind, Func<int, Kind?>? kindOf = null)
    {
        if (_page != null) _page.PropertyChanged -= PageChanged;
        _page = page;
        _kind = kind;
        _kindOf = kindOf;
        page.PropertyChanged += PageChanged;
        Build();
    }

    private bool IsMonster => _kind == Kind.MonsterDefense;

    private void PageChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(L2Page.SelectedIndex) or nameof(L2Page.IsEditable) or nameof(L2Page.Reloads)) Dispatcher.BeginInvoke(Build);
    }

    private void Build()
    {
        _body.Children.Clear();
        var rom = _page?.PageRom;
        int index = _page?.PageIndex ?? -1;
        if (_kindOf != null && rom != null && index >= 0)
        {
            var kind = _kindOf(index);
            Visibility = kind == null ? Visibility.Collapsed : Visibility.Visible;
            if (kind == null) return;
            _kind = kind.Value;
        }
        _body.Children.Add(new TextBlock { Text = IsMonster ? "TYPE" : "STRONG AGAINST", Style = TryFindResource("GdSection") as Style });
        if (rom == null || index < 0 || !rom.IsLufia2) return;
        _body.Children.Add(new TextBlock
        {
            Style = TryFindResource("GdHint") as Style, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 6),
            Text = IsMonster
                ? "A type is a set of element percents (ELEMENTS card): Dragon is vs dragons +100%, Flying thrown +100% and earth -100%... A tick writes them, unticking sets them back to 0. Undead also makes healing hurt it (shared script 12 in its when-hit script). Beast, Aquatic, Plant and Humanoid are Lufia Forge's own."
                : (_kind == Kind.Weapon ? "As a weapon: " : _kind == Kind.ItemBattle ? "Used in battle: " : "") + "adds the type's element to the attack (a step after the one that sets its element), on top of any it has: a fire sword strong against dragons is both. Undead: light. The first tick also makes the game's dragon, insect, flying and hard shell checks count an attack with other elements (they wanted the element alone).",
        });
        int bits = IsMonster ? L2MonsterTypes.Of(rom, index) : L2MonsterTypes.StrongAgainst(rom, _kind, index);
        var typesPanel = new WrapPanel();
        _loading = true;
        for (int i = 0; i < L2MonsterTypes.Types.Length; i++)
        {
            var typeInfo = L2MonsterTypes.Types[i];
            if (!IsMonster && typeInfo.Element == 0) continue;
            int type = i;
            var checkBox = new CheckBox
            {
                Content = typeInfo.Name, IsChecked = (bits & 1 << i) != 0, Style = TryFindResource("GdCheck") as Style,
                Margin = new Thickness(0, 2, 14, 2), MinWidth = 96, ToolTip = typeInfo.Help,
            };
            checkBox.Click += (_, _) => Write(type, checkBox.IsChecked == true);
            typesPanel.Children.Add(checkBox);
        }
        _loading = false;
        _body.Children.Add(typesPanel);
    }

    private void Write(int type, bool on)
    {
        var rom = _page?.PageRom;
        int index = _page?.PageIndex ?? -1;
        if (_loading || rom == null || index < 0) return;
        try
        {
            bool changed = IsMonster ? L2MonsterTypes.Set(rom, index, type, on) : L2MonsterTypes.SetStrong(rom, _kind, index, type, on);
            string name = L2MonsterTypes.Types[type].Name;
            if (changed) _page!.ReportChange(IsMonster ? $"type {name} {(on ? "on" : "off")}" : $"strong against {name} {(on ? "on" : "off")}");
        }
        catch (Exception ex) { MessageBox.Show(ex.Message, "Type", MessageBoxButton.OK, MessageBoxImage.Warning); }
        Dispatcher.BeginInvoke(Build);
    }
}
