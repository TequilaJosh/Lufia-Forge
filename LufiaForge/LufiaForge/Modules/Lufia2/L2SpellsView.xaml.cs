using System.Windows.Controls;
using Kind = LufiaForge.Core.Lufia2.L2AiScripts.Kind;
using What = LufiaForge.Core.Lufia2.L2StatusSteps.What;

namespace LufiaForge.Modules.Lufia2;

public partial class L2SpellsView : UserControl
{
    public L2SpellsView()
    {
        InitializeComponent();
        DataContextChanged += (_, _) => { if (DataContext is L2SpellsPage page) Attach(page); };
    }

    // the new statuses card: what this record gives, cures or resists
    private void Attach(L2SpellsPage page)
    {
        Types.Attach(page, Kind.Spell);
        NewStatuses.Attach(page,
            new("Causes", "The chance the spell gives the status to its targets.", Kind.Spell, What.Cause),
            new("Cures", "The chance it cures the status.", Kind.Spell, What.Cure),
            new("Wards", "Its targets can't get the status for the rest of the battle.", Kind.Spell, What.Ward));
    }
}
