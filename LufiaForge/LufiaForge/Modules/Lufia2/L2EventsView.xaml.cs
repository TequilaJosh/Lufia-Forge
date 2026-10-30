using System.Windows;
using System.Windows.Controls;
using LufiaForge.Core.Lufia2;
using Button = System.Windows.Controls.Button;
using ContextMenu = System.Windows.Controls.ContextMenu;
using MenuItem = System.Windows.Controls.MenuItem;

namespace LufiaForge.Modules.Lufia2;

public partial class L2EventsView : UserControl
{
    public L2EventsView() => InitializeComponent();

    private L2EventsPage? Page => DataContext as L2EventsPage;

    /// <summary>The "Add a step" menu: the commands by category; rarely used ones in their own sub-menu.</summary>
    private void Add_Click(object sender, RoutedEventArgs e)
    {
        if (Page == null || sender is not Button b) return;
        var menu = new ContextMenu { PlacementTarget = b, Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom };
        var offered = L2EventCommands.Definitions.Where(d => d.Template != null).ToList();
        foreach (string cat in L2EventCommands.Categories)
        {
            var defs = cat == L2EventCommands.Special ? offered.Where(d => d.Rare || d.Category == cat).ToList() : offered.Where(d => d.Category == cat && !d.Rare).ToList();
            if (defs.Count == 0) continue;
            var sub = new MenuItem { Header = cat };
            foreach (var d in defs.OrderBy(d => cat == L2EventCommands.Special ? d.Name : ""))
            {
                var item = new MenuItem { Header = d.Name, ToolTip = d.Help.Length > 0 ? d.Help : null };
                var def = d;
                item.Click += (_, _) => Page.AddCommand(def);
                sub.Items.Add(item);
            }
            menu.Items.Add(sub);
        }
        menu.IsOpen = true;
    }

    private void NewEvent_Click(object sender, RoutedEventArgs e)
    {
        if (Page?.CurrentRom == null || Page.CurrentMap < 0) return;
        if (!Page.AskBeforeLeaving()) return;   // the new event is opened after: unsaved steps would be dropped
        var w = new L2NewEventWindow(Page) { Owner = Window.GetWindow(this) };
        w.ShowDialog();
    }
}
