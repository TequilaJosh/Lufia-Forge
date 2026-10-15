using LufiaForge.Core;
using System.IO;
using System.Windows;
using System.Windows.Controls;

namespace LufiaForge.Modules.Common;

/// <summary>Names a new project and picks where it's saved (a folder of its own in Documents\Lufia Forge Projects by default).</summary>
public partial class NewProjectWindow : Window
{
    public string ProjectPath { get; private set; } = "";
    public string ProjectName => NameBox.Text.Trim();

    public NewProjectWindow(string romPath)
    {
        InitializeComponent();
        RomText.Text = $"Starting from {Path.GetFileName(romPath)}.";
        string name = System.Text.RegularExpressions.Regex.Replace(Path.GetFileNameWithoutExtension(romPath), @"_\d{4}-\d{2}-\d{2}_\d{2}-\d{2}-\d{2}$", "");
        NameBox.Text = name;
        FolderBox.Text = LufiaProject.DefaultFolder;
        Loaded += (_, _) => { NameBox.Focus(); NameBox.SelectAll(); };
        Update();
    }

    private static string Clean(string name) =>
        string.Concat(name.Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c)).Trim().TrimEnd('.');

    private string Target()
    {
        string file = Clean(ProjectName);
        return file.Length == 0 ? "" : Path.Combine(FolderBox.Text.Trim(), file, file + LufiaProject.Extension);
    }

    private void Update()
    {
        if (PathText == null || OkButton == null) return;
        string t = Target();
        OkButton.IsEnabled = t.Length > 0 && FolderBox.Text.Trim().Length > 0;
        PathText.Text = t.Length == 0 ? "Give the project a name." : File.Exists(t) ? "A project with this name is already there: pick another name." : t;
        if (File.Exists(t)) OkButton.IsEnabled = false;
    }

    private void NameBox_TextChanged(object sender, TextChangedEventArgs e) => Update();

    private void Browse_Click(object sender, RoutedEventArgs e)
    {
        var d = new Microsoft.Win32.OpenFolderDialog { Title = "Where projects are saved", InitialDirectory = Directory.Exists(FolderBox.Text) ? FolderBox.Text : null };
        if (d.ShowDialog(this) == true) FolderBox.Text = d.FolderName;
    }

    private void Ok_Click(object sender, RoutedEventArgs e)
    {
        string t = Target();
        if (t.Length == 0 || File.Exists(t)) return;
        ProjectPath = t;
        DialogResult = true;
    }
}
