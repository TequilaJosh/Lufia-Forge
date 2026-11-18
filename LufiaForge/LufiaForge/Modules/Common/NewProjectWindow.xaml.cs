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

    /// <summary>A new project from the one open (File, New Project From This One): named "&lt;name&gt; 2" at first, in a
    /// folder of its own next to the project's folder.</summary>
    public NewProjectWindow(LufiaProject from) : this(from.Path)
    {
        Title = TitleText.Text = "New project from this one";
        RomText.Text = $"\"{from.Name}\" is saved, then copied into a new project, and you go on in the copy. \"{from.Name}\" stays as it is now: open it from File > Open Project or the list of recent projects to go back to it.";
        NoteText.Text = "The copy keeps everything: the changes, the map names, the bookmarks and flag names. Both start from the same ROM, and the ROM is never changed.";
        OkButton.Content = "✔  Make the copy";
        string folder = Path.GetDirectoryName(Path.GetDirectoryName(from.Path) ?? "") ?? "";
        if (folder.Length > 0) FolderBox.Text = folder;
        NameBox.Text = NextName(from.Name, n => File.Exists(Target(n)));
        Update();
    }

    /// <summary>The name after this one: a number at its end counts up ("p2" -> "p3", "Hack v9" -> "Hack v10"), otherwise
    /// " 2" goes after it; names already taken are skipped.</summary>
    public static string NextName(string name, Func<string, bool> taken)
    {
        var m = System.Text.RegularExpressions.Regex.Match(name, @"^(.*?)(\d+)$");
        string head = m.Success ? m.Groups[1].Value : name + " ";
        long n = m.Success && long.TryParse(m.Groups[2].Value, out long k) ? k + 1 : 2;
        string next = head + n;
        for (int i = 0; i < 1000 && taken(next); i++) next = head + ++n;
        return next;
    }

    private static string Clean(string name) => LufiaProject.FileNameFor(name);

    private string Target(string projectName)
    {
        string file = Clean(projectName);
        return file.Length == 0 ? "" : Path.Combine(FolderBox.Text.Trim(), file, file + LufiaProject.Extension);
    }

    private string Target() => Target(ProjectName);

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
