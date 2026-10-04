using Velopack;

namespace LufiaForge;

public static class Program
{
    [STAThread]
    public static void Main(string[] args)
    {
        // Must run first: handles Velopack's install / update / uninstall hooks (and exits for them).
        VelopackApp.Build().Run();

        var app = new App();
        app.InitializeComponent();
        app.Run();
    }
}
