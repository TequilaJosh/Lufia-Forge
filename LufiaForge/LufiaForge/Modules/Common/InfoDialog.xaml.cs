using System.Diagnostics;
using System.IO;
using System.Windows;

namespace LufiaForge.Modules.Common;

/// <summary>Themed confirmation box: what was written, where, and an optional follow-up action.</summary>
public partial class InfoDialog : Window
{
    private string? _path;
    private Action? _primary;

    private InfoDialog() => InitializeComponent();

    /// <param name="path">A file to show with "Open folder" / "Copy path" buttons.</param>
    /// <param name="primaryText">Caption of an extra action button (e.g. "Save ROM now").</param>
    public static void Show(string title, string heading, string body,
                            string? path = null, string? pathLabel = null,
                            string? primaryText = null, Action? primary = null)
    {
        var d = new InfoDialog { Title = title };
        d.HeadingText.Text = heading;
        d.BodyText.Text = body;
        if (path != null)
        {
            d._path = path;
            d.PathPanel.Visibility = Visibility.Visible;
            d.PathLabel.Text = pathLabel ?? "File";
            d.PathBox.Text = path;
        }
        if (primaryText != null && primary != null)
        {
            d._primary = primary;
            d.PrimaryButton.Content = primaryText;
            d.PrimaryButton.Visibility = Visibility.Visible;
        }
        var owner = Application.Current?.Windows.OfType<Window>().FirstOrDefault(w => w.IsActive)
                    ?? Application.Current?.MainWindow;
        if (owner != null && owner != d && owner.IsLoaded) d.Owner = owner;
        d.ShowDialog();
    }

    private void OpenFolder_Click(object sender, RoutedEventArgs e)
    {
        if (_path == null) return;
        try
        {
            if (File.Exists(_path)) Process.Start("explorer.exe", $"/select,\"{_path}\"");
            else if (Path.GetDirectoryName(_path) is { } dir && Directory.Exists(dir)) Process.Start("explorer.exe", $"\"{dir}\"");
        }
        catch { /* explorer not available */ }
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
