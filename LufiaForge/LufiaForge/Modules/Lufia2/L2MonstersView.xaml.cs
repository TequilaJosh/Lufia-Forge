using System.Windows.Controls;
using Kind = LufiaForge.Core.Lufia2.L2AiScripts.Kind;
using What = LufiaForge.Core.Lufia2.L2StatusSteps.What;

namespace LufiaForge.Modules.Lufia2;

public partial class L2MonstersView : UserControl
{
    public L2MonstersView()
    {
        InitializeComponent();
        DataContextChanged += (_, _) => { if (DataContext is L2MonstersPage p) Attach(p); };
    }

    /// <summary>The new statuses card: what this record gives, cures or resists.</summary>
    private void Attach(L2MonstersPage p)
    {
        Types.Attach(p, Kind.MonsterDefense);
        NewStatuses.Attach(p,
            new("Resists", "Its chance of getting it drops by this percent (100 = immune): a step in its 'when hit' script.", Kind.MonsterDefense, What.Resist),
            new("From the start", "It has it from the moment it is in a battle (Zombie: an undead monster).", Kind.MonsterDefense, What.Resist, Start: true));
    }
}
