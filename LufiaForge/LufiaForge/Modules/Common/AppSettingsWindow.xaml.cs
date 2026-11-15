using System.Windows;
using LufiaForge.Core;

namespace LufiaForge.Modules.Common;

/// <summary>Edit, Settings: Lufia Forge's own settings (<see cref="AppSettings"/>). OK keeps them.</summary>
public partial class AppSettingsWindow : Window
{
    public AppSettingsWindow()
    {
        InitializeComponent();
        Snes9xCheck.IsChecked = AppSettings.Snes9x151Mode;
    }

    /// <summary>Whether Snes9x 1.51 mode was turned on now (it was off).</summary>
    public bool Snes9xTurnedOn { get; private set; }

    private void Ok_Click(object sender, RoutedEventArgs e)
    {
        bool on = Snes9xCheck.IsChecked == true;
        Snes9xTurnedOn = on && !AppSettings.Snes9x151Mode;
        if (on != AppSettings.Snes9x151Mode) AppSettings.Snes9x151Mode = on;
        DialogResult = true;
    }
}
