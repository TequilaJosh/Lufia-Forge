using LufiaForge.Core.Lufia2;
using System.Windows.Controls;
using Kind = LufiaForge.Core.Lufia2.L2AiScripts.Kind;
using What = LufiaForge.Core.Lufia2.L2StatusSteps.What;

namespace LufiaForge.Modules.Lufia2;

public partial class L2ItemsView : UserControl
{
    public L2ItemsView()
    {
        InitializeComponent();
        DataContextChanged += (_, _) => { if (DataContext is L2ItemsPage p) Attach(p); };
    }

    /// <summary>The new statuses card: what this record gives, cures or resists.</summary>
    private void Attach(L2ItemsPage p)
    {
        Elements.Attach(p, LufiaForge.Core.Lufia2.L2Elements.Who.Item, i => p.PageRom is { } r && L2Battle.ReadItem(r, i).EquipType != 0);
        Types.Attach(p, Kind.Weapon, i =>
        {
            var rom = p.PageRom;
            if (rom == null) return null;
            var it = L2Battle.ReadItem(rom, i);
            return (it.EquipType & 1) != 0 ? Kind.Weapon : (it.Words & 0x0002) != 0 ? Kind.ItemBattle : null;
        });
        BounceFocus.Attach(p, monsters: false);
        NewStatuses.Attach(p,
            new("Weapon hit causes", "As a weapon: the chance its hit gives the status (its weapon script).", Kind.Weapon, What.Cause),
            new("Worn: resists", "As armor: the wearer's chance of getting it drops by this percent (100 = immune).", Kind.Armor, What.Resist),
            new("Used: causes", "Used in battle: the chance it gives the status to its targets.", Kind.ItemBattle, What.Cause),
            new("Used: cures", "Used in battle: the chance it cures the status.", Kind.ItemBattle, What.Cure),
            new("Menu: cures", "Used from the menu: the chance it cures the status (the ones that stay after a battle: Zombie, Decay, Burnt, Blind).", Kind.ItemMenu, What.Cure));
    }
}
