// Resolve WPF vs WinForms type ambiguities when UseWindowsForms is enabled.
// These aliases ensure existing WPF code compiles without qualification changes.
global using Application    = System.Windows.Application;
global using UserControl    = System.Windows.Controls.UserControl;
global using MessageBox     = System.Windows.MessageBox;
global using MessageBoxButton = System.Windows.MessageBoxButton;
global using MessageBoxImage  = System.Windows.MessageBoxImage;
global using MessageBoxResult = System.Windows.MessageBoxResult;
global using OpenFileDialog   = Microsoft.Win32.OpenFileDialog;
global using SaveFileDialog   = Microsoft.Win32.SaveFileDialog;
