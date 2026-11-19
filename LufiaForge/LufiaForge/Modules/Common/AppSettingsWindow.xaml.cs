using System.Windows;
using LufiaForge.Core;

namespace LufiaForge.Modules.Common;

// Edit > Settings: Lufia Forge's own settings (AppSettings). OK keeps them
public partial class AppSettingsWindow : Window
{
    public AppSettingsWindow()
    {
        InitializeComponent();
        Snes9xCheck.IsChecked = AppSettings.Snes9x151Mode;
    }

    // true if Snes9x 1.51 mode just got turned on (it was off before)
    public bool Snes9xTurnedOn { get; private set; }

    private void Ok_Click(object sender, RoutedEventArgs e)
    {
        bool snes9xMode = Snes9xCheck.IsChecked == true;
        Snes9xTurnedOn = snes9xMode && !AppSettings.Snes9x151Mode;
        if (snes9xMode != AppSettings.Snes9x151Mode) AppSettings.Snes9x151Mode = snes9xMode;
        DialogResult = true;
    }
}
