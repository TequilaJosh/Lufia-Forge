using Velopack;
using Velopack.Sources;

namespace LufiaForge.Core;

/// <summary>
/// Checks GitHub Releases of the Lufia Forge repository for a newer version and installs it.
/// Only works in the installed app (Setup.exe); in a dev build it reports that updates are unavailable.
/// </summary>
public static class UpdateService
{
    public const string RepoUrl = "https://github.com/TequilaJosh/Lufia-Forge";

    private static UpdateManager CreateManager() =>
        new(new GithubSource(RepoUrl, accessToken: null, prerelease: false));

    public static string CurrentVersion =>
        CreateManager().CurrentVersion?.ToString()
        ?? typeof(UpdateService).Assembly.GetName().Version?.ToString(3)
        ?? "dev";

    /// <summary>
    /// Checks for an update and, if the user agrees, downloads it and restarts into the new version.
    /// When <paramref name="silentIfNone"/> is true (startup check), nothing is shown unless an update exists.
    /// </summary>
    public static async Task CheckAsync(bool silentIfNone, Func<bool>? hasUnsavedChanges = null)
    {
        try
        {
            var mgr = CreateManager();
            if (!mgr.IsInstalled)
            {
                if (!silentIfNone)
                    MessageBox.Show("Automatic updates only work in the installed version of Lufia Forge " +
                                    "(installed with LufiaForge-win-Setup.exe from the GitHub releases page).",
                                    "Check for Updates", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            var update = await mgr.CheckForUpdatesAsync();
            if (update == null)
            {
                if (!silentIfNone)
                    MessageBox.Show($"You have the latest version ({mgr.CurrentVersion}).",
                                    "Check for Updates", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            string newVersion = update.TargetFullRelease.Version.ToString();
            string prompt = $"Lufia Forge {newVersion} is available (you have {mgr.CurrentVersion}).\n\n" +
                            "Download and install it now? Lufia Forge will restart.";
            if (hasUnsavedChanges?.Invoke() == true)
                prompt += "\n\nWarning: you have unsaved ROM changes. Save first, or they will be lost.";

            if (MessageBox.Show(prompt, "Update Available", MessageBoxButton.YesNo,
                                MessageBoxImage.Information) != MessageBoxResult.Yes)
                return;

            await mgr.DownloadUpdatesAsync(update);
            mgr.ApplyUpdatesAndRestart(update);
        }
        catch (Exception ex)
        {
            // Offline or GitHub unreachable: never block the app over an update check.
            if (!silentIfNone)
                MessageBox.Show($"Could not check for updates:\n{ex.Message}", "Check for Updates",
                                MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }
}
