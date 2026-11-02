using System.Windows;

namespace LufiaForge;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        Controls.ComboSearch.Register();   // type-to-search in every dropdown
        DispatcherUnhandledException += OnUiError;
        AppDomain.CurrentDomain.UnhandledException += OnFatalError;
    }

    private bool _showingError;

    /// <summary>An error nothing caught on the UI thread: said, and Lufia Forge carries on (the project stays open, so
    /// it can be saved).</summary>
    private void OnUiError(object sender, System.Windows.Threading.DispatcherUnhandledExceptionEventArgs e)
    {
        e.Handled = true;
        if (_showingError) return;   // the same error again while its message is up (a redraw that keeps failing)
        _showingError = true;
        try
        {
            MessageBox.Show($"Something went wrong: {e.Exception.Message}\n\nLufia Forge keeps running. If the page you were on " +
                            "acts oddly now, save the project and open it again.\n\n" + e.Exception.GetType().Name +
                            (e.Exception.StackTrace is { } st ? "\n" + string.Join("\n", st.Split('\n').Take(6)) : ""),
                            "Lufia Forge", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        catch { }
        finally { _showingError = false; }
    }

    /// <summary>An error on another thread: .NET closes the app after this, so a copy of the unsaved project is kept
    /// (<see cref="ViewModels.MainViewModel.SaveRecoveryCopy"/>) and where it went is said.</summary>
    private static void OnFatalError(object sender, UnhandledExceptionEventArgs e)
    {
        string? copy = ViewModels.MainViewModel.Instance?.SaveRecoveryCopy();
        try
        {
            MessageBox.Show($"Lufia Forge has to close after an error: {(e.ExceptionObject as Exception)?.Message ?? e.ExceptionObject?.ToString()}" +
                            (copy != null ? $"\n\nThe project's unsaved changes were kept in a copy next to it:\n{copy}\n\nOpen it with Open Project." : ""),
                            "Lufia Forge", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        catch { }
    }
}
