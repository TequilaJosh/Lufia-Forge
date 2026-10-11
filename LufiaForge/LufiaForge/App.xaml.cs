using System.Windows;

namespace LufiaForge;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        Controls.ComboSearch.Register();   // type-to-search in every dropdown
    }
}
