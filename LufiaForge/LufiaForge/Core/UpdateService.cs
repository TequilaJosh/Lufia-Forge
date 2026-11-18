using Velopack;
using Velopack.Sources;

namespace LufiaForge.Core;

// checks the github releases of the release-only repo for a newer version and installs it.
// beta builds (a version with a suffix, like 0.4.0-beta.2) take pre-releases too, stable builds only stable ones.
// only works in the installed app (Setup.exe), a dev build just says updates aren't available
public static class UpdateService
{
    public const string RepoUrl = "https://github.com/TequilaJosh/Lufia-Forge-Releases";

    // true for beta builds, their version has a pre-release suffix
    public static bool IsBeta =>
        (System.Reflection.CustomAttributeExtensions.GetCustomAttribute<System.Reflection.AssemblyInformationalVersionAttribute>(
            typeof(UpdateService).Assembly)?.InformationalVersion ?? "").Split('+')[0].Contains('-');

    private static UpdateManager CreateManager() =>
        new(new GithubSource(RepoUrl, accessToken: null, prerelease: IsBeta));

    public static string CurrentVersion =>
        CreateManager().CurrentVersion?.ToString()
        ?? typeof(UpdateService).Assembly.GetName().Version?.ToString(3)
        ?? "dev";

    // checks for an update and, if the user says yes, downloads it and restarts into the new version.
    // with silentIfNone (the startup check) nothing shows up unless there's an update.
    // the restart closes the app without asking about saving, so saveBeforeRestart saves the project first (after the
    // download, so edits made in the meantime are kept too). if it can't save, there's no restart
    public static async Task CheckAsync(bool silentIfNone, Func<bool>? hasUnsavedChanges = null, Func<Task<bool>>? saveBeforeRestart = null)
    {
        try
        {
            var manager = CreateManager();
            if (!manager.IsInstalled)
            {
                if (!silentIfNone)
                    MessageBox.Show("Automatic updates only work in the installed version of Lufia Forge " +
                                    "(installed with LufiaForge-win-Setup.exe from github.com/TequilaJosh/Lufia-Forge-Releases).",
                                    "Check for Updates", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            var update = await manager.CheckForUpdatesAsync();
            if (update == null)
            {
                if (!silentIfNone)
                    MessageBox.Show($"You have the latest version ({manager.CurrentVersion}).",
                                    "Check for Updates", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            string newVersion = update.TargetFullRelease.Version.ToString();
            string prompt = $"Lufia Forge {newVersion} is available (you have {manager.CurrentVersion}).\n\n" +
                            "Download and install it now? Lufia Forge will restart.";
            if (hasUnsavedChanges?.Invoke() == true)
                prompt += "\n\nThe project has unsaved changes: it is saved before Lufia Forge restarts.";

            if (MessageBox.Show(prompt, "Update Available", MessageBoxButton.YesNo,
                                MessageBoxImage.Information) != MessageBoxResult.Yes)
                return;

            await manager.DownloadUpdatesAsync(update);
            if (saveBeforeRestart != null && !await saveBeforeRestart())
            {
                MessageBox.Show($"Lufia Forge {newVersion} is downloaded, but the project couldn't be saved, so Lufia Forge didn't restart " +
                                "(the changes would have been lost).\n\nSave the project (or use Save Project As), then use Check for Updates " +
                                "again to install it.", "Update Available", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
            manager.ApplyUpdatesAndRestart(update);
        }
        catch (Exception ex)
        {
            // offline or github can't be reached, never block the app over an update check
            if (!silentIfNone)
                MessageBox.Show($"Could not check for updates:\n{ex.Message}", "Check for Updates",
                                MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }
}
