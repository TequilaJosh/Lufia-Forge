using System.Diagnostics;
using System.IO;
using System.Windows;

namespace LufiaForge.Modules.Common;

// our themed confirmation box: what got written, where, and maybe a follow-up action
public partial class InfoDialog : Window
{
    private string? _path;
    private Action? _primary;

    private InfoDialog() => InitializeComponent();

    // path = a file to show with "Open folder" / "Copy path" buttons
    // primaryText = the caption of an extra action button (like "Save project now")
    public static void Show(string title, string heading, string body,
                            string? path = null, string? pathLabel = null,
                            string? primaryText = null, Action? primary = null)
    {
        var dialog = new InfoDialog { Title = title };
        dialog.HeadingText.Text = heading;
        dialog.BodyText.Text = body;
        if (path != null)
        {
            dialog._path = path;
            dialog.PathPanel.Visibility = Visibility.Visible;
            dialog.PathLabel.Text = pathLabel ?? "File";
            dialog.PathBox.Text = path;
        }
        if (primaryText != null && primary != null)
        {
            dialog._primary = primary;
            dialog.PrimaryButton.Content = primaryText;
            dialog.PrimaryButton.Visibility = Visibility.Visible;
        }
        var owner = Application.Current?.Windows.OfType<Window>().FirstOrDefault(w => w.IsActive)
                    ?? Application.Current?.MainWindow;
        if (owner != null && owner != dialog && owner.IsLoaded) dialog.Owner = owner;
        dialog.ShowDialog();
    }

    private void OpenFolder_Click(object sender, RoutedEventArgs e)
    {
        if (_path == null) return;
        try
        {
            if (File.Exists(_path)) Process.Start("explorer.exe", $"/select,\"{_path}\"");
            else if (Path.GetDirectoryName(_path) is { } folder && Directory.Exists(folder)) Process.Start("explorer.exe", $"\"{folder}\"");
        }
        catch { /* no explorer */ }
    }

    private void CopyPath_Click(object sender, RoutedEventArgs e)
    {
        if (_path != null) System.Windows.Clipboard.SetText(_path);
    }

    private void Primary_Click(object sender, RoutedEventArgs e)
    {
        Close();
        _primary?.Invoke();
    }

    private void Ok_Click(object sender, RoutedEventArgs e) => Close();
}
