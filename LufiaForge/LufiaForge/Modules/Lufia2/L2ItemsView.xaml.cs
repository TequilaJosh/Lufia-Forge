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
        DataContextChanged += (_, _) => { if (DataContext is L2ItemsPage page) Attach(page); };
    }

    // hooks up the cards. the new statuses card shows what this record gives, cures or resists
    private void Attach(L2ItemsPage page)
    {
        Elements.Attach(page, LufiaForge.Core.Lufia2.L2Elements.Who.Item, i => page.PageRom is { } rom && L2Battle.ReadItem(rom, i).EquipType != 0);
        Types.Attach(page, Kind.Weapon, i =>
        {
            var rom = page.PageRom;
            if (rom == null) return null;
            var item = L2Battle.ReadItem(rom, i);
            return (item.EquipType & 1) != 0 ? Kind.Weapon : (item.Words & 0x0002) != 0 ? Kind.ItemBattle : null;
        });
        BounceFocus.Attach(page, monsters: false);
        NewStatuses.Attach(page,
            new("Weapon hit causes", "As a weapon: the chance its hit gives the status (its weapon script).", Kind.Weapon, What.Cause),
            new("Worn: resists", "As armor: the wearer's chance of getting it drops by this percent (100 = immune).", Kind.Armor, What.Resist),
            new("Used: causes", "Used in battle: the chance it gives the status to its targets.", Kind.ItemBattle, What.Cause),
            new("Used: cures", "Used in battle: the chance it cures the status.", Kind.ItemBattle, What.Cure),
            new("Menu: cures", "Used from the menu: the chance it cures the status (the ones that stay after a battle: Zombie, Decay, Burnt, Blind).", Kind.ItemMenu, What.Cure));
    }
}
