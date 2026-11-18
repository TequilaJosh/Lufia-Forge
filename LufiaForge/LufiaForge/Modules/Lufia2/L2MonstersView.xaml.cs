using System.Windows.Controls;
using Kind = LufiaForge.Core.Lufia2.L2AiScripts.Kind;
using What = LufiaForge.Core.Lufia2.L2StatusSteps.What;

namespace LufiaForge.Modules.Lufia2;

public partial class L2MonstersView : UserControl
{
    public L2MonstersView()
    {
        InitializeComponent();
        DataContextChanged += (_, _) => { if (DataContext is L2MonstersPage page) Attach(page); };
    }

    // the new statuses card: what this record gives, cures or resists
    private void Attach(L2MonstersPage page)
    {
        Elements.Attach(page, LufiaForge.Core.Lufia2.L2Elements.Who.Monster);
        Types.Attach(page, Kind.MonsterDefense);
        BounceFocus.Attach(page, monsters: true);
        NewStatuses.Attach(page,
            new("Resists", "Its chance of getting it drops by this percent (100 = immune): a step in its 'when hit' script.", Kind.MonsterDefense, What.Resist),
            new("From the start", "It has it from the moment it is in a battle (Zombie: an undead monster).", Kind.MonsterDefense, What.Resist, Start: true));
    }
}
