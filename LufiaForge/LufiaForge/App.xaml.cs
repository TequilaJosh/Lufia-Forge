using System.Windows;

namespace LufiaForge;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        Core.AppSettings.Init();
        Controls.ComboSearch.Register();   // type-to-search in every dropdown
        DispatcherUnhandledException += OnUiError;
        AppDomain.CurrentDomain.UnhandledException += OnFatalError;
    }

    private bool _showingError;

    // an error nothing caught on the ui thread. we show it and carry on, the project stays open so it can still be saved
    private void OnUiError(object sender, System.Windows.Threading.DispatcherUnhandledExceptionEventArgs e)
    {
        e.Handled = true;
        if (_showingError) return;   // the same error again while its message is up (a redraw that keeps failing)
        _showingError = true;
        try
        {
            MessageBox.Show($"Something went wrong: {e.Exception.Message}\n\nLufia Forge keeps running. If the page you were on " +
                            "acts oddly now, save the project and open it again.\n\n" + e.Exception.GetType().Name +
                            (e.Exception.StackTrace is { } stackTrace ? "\n" + string.Join("\n", stackTrace.Split('\n').Take(6)) : ""),
                            "Lufia Forge", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        catch { }
        finally { _showingError = false; }
    }

    // an error on another thread. .net closes the app after this, so we keep a copy of the unsaved project
    // (MainViewModel.SaveRecoveryCopy) and say where it went
    private static void OnFatalError(object sender, UnhandledExceptionEventArgs e)
    {
        string? recoveryCopy = ViewModels.MainViewModel.Instance?.SaveRecoveryCopy();
        try
        {
            MessageBox.Show($"Lufia Forge has to close after an error: {(e.ExceptionObject as Exception)?.Message ?? e.ExceptionObject?.ToString()}" +
                            (recoveryCopy != null ? $"\n\nThe project's unsaved changes were kept in a copy next to it:\n{recoveryCopy}\n\nOpen it with Open Project." : ""),
                            "Lufia Forge", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        catch { }
    }
}
