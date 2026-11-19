using Velopack;

namespace LufiaForge;

public static class Program
{
    [STAThread]
    public static void Main(string[] args)
    {
        // has to run first, it handles velopack's install / update / uninstall hooks (and exits for them)
        VelopackApp.Build().Run();

        var app = new App();
        app.InitializeComponent();
        app.Run();
    }
}
