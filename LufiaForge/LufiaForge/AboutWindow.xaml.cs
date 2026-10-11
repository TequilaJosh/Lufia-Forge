using System.Windows;

namespace LufiaForge;

/// <summary>Version, thanks to the people who helped, and the work Lufia Forge builds on.</summary>
public partial class AboutWindow : Window
{
    public sealed record Credit(string Name, string For);

    /// <summary>People who helped make Lufia Forge what it is (testing, ideas, bug reports).</summary>
    public static readonly Credit[] Thanks =
    {
        new("Ok Impala!", "Testing from the first betas, and a long list of ideas that became features: graphics import/export, map copy and paste, the music editor, title screen and credits editors, fewer random battles, party members retargeting, 4 MB ROMs, and the rename command for flashbacks with the old heroes."),
        new("Jester", "The idea of giving Lufia 1 the Lufia II font."),
        new("Pedro José", "Feedback and encouragement in the Discord."),
        new("Mewlies", "Feedback and Lufia lore in the Discord."),
        new("Everyone in the Discord", "Every bug report, screenshot and \"what if...\" that made it in."),
    };

    /// <summary>Earlier work Lufia Forge builds on or works with.</summary>
    public static readonly Credit[] BuiltOn =
    {
        new("FlamePurge", "Lufia & the Fortress of Doom Restored: Lufia Forge reads and edits ROMs with Restored (and its Always Retry option) as their base."),
        new("The Lufia Recolor patch", "Character and window colours closer to Lufia II; supported as a base ROM."),
        new("L1ME (Lufia 1 Multi Editor)", "Its character, item, magic, monster and shop offsets were the starting point of the Game Data editors."),
        new("Vegetaman, Digisalt and flobo", "Text encoding research and the AllOriginal table."),
    };

    public AboutWindow()
    {
        InitializeComponent();
        string version;
        try { version = Core.UpdateService.CurrentVersion; }
        catch (Exception) { version = typeof(AboutWindow).Assembly.GetName().Version?.ToString(3) ?? "?"; }   // not installed (a dev build)
        VersionText.Text = $"Version {version}";
        ThanksList.ItemsSource = Thanks;
        CreditsList.ItemsSource = BuiltOn;
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();
}
