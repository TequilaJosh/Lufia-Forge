using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LufiaForge.Core;
using Microsoft.Win32;
using System.IO;
using System.Windows;

namespace LufiaForge.ViewModels;

public partial class MainViewModel : ObservableObject
{
    // -------------------------------------------------------------------------
    // Properties
    // -------------------------------------------------------------------------

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsRomLoaded))]
    [NotifyPropertyChangedFor(nameof(WindowTitle))]
    private RomBuffer? _romBuffer;

    [ObservableProperty] private string _statusMessage = "Ready. Open a Lufia 1 ROM to begin.";
    [ObservableProperty] private string _statusDetail  = "";
    [ObservableProperty] private bool   _isLoading     = false;
    [ObservableProperty] private int    _selectedTabIndex = 0;

    public bool IsRomLoaded => RomBuffer != null;

    public string WindowTitle => RomBuffer == null
        ? "Lufia Forge"
        : $"Lufia Forge - {Path.GetFileName(RomBuffer.FilePath)}{(RomBuffer.IsDirty ? " *" : "")}";

    // -------------------------------------------------------------------------
    // Commands
    // -------------------------------------------------------------------------

    [RelayCommand]
    private void OpenRom()
    {
        var dialog = new OpenFileDialog
        {
            Title  = "Open Lufia 1 ROM",
            Filter = "SNES ROM files (*.sfc;*.smc)|*.sfc;*.smc|All files (*.*)|*.*"
        };

        if (dialog.ShowDialog() != true) return;

        IsLoading = true;
        StatusMessage = "Loading ROM...";

        var report = RomLoader.Load(dialog.FileName);
        IsLoading = false;

        switch (report.Result)
        {
            case RomLoadResult.Success:
                RomBuffer     = report.Buffer;
                StatusMessage = $"Loaded: {Path.GetFileName(dialog.FileName)}";
                StatusDetail  = report.HadSmcHeader
                    ? "SMC header detected and stripped."
                    : $"Size: {report.RomSizeBytes / 1024}KB  |  Title: {report.DetectedTitle}";
                break;

            case RomLoadResult.ChecksumMismatch:
                RomBuffer     = report.Buffer; // Still load - might be a pre-patched ROM
                StatusMessage = $"Loaded with warnings: {Path.GetFileName(dialog.FileName)}";
                StatusDetail  = "Checksum mismatch - ROM may already be modified. Proceeding anyway.";
                MessageBox.Show(report.Message, "Checksum Warning",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
                break;

            case RomLoadResult.WrongGame:
                StatusMessage = "Load failed: Wrong game.";
                StatusDetail  = report.Message;
                MessageBox.Show(report.Message, "Wrong ROM",
                    MessageBoxButton.OK, MessageBoxImage.Error);
                break;

            default:
                StatusMessage = "Load failed.";
                StatusDetail  = report.Message;
                MessageBox.Show(report.Message, "Error Loading ROM",
                    MessageBoxButton.OK, MessageBoxImage.Error);
                break;
        }
    }

    [RelayCommand(CanExecute = nameof(IsRomLoaded))]
    private void SaveRom()
    {
        if (RomBuffer == null) return;
        try
        {
            RomBuffer.SaveToFile();
            StatusMessage = "ROM saved.";
            StatusDetail  = RomBuffer.FilePath;
            OnPropertyChanged(nameof(WindowTitle));
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Failed to save: {ex.Message}", "Save Error",
                MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    [RelayCommand(CanExecute = nameof(IsRomLoaded))]
    private void SaveRomAs()
    {
        if (RomBuffer == null) return;

        var dialog = new SaveFileDialog
        {
            Title      = "Save ROM As",
            Filter     = "SNES ROM (*.sfc)|*.sfc|All files (*.*)|*.*",
            FileName   = Path.GetFileNameWithoutExtension(RomBuffer.FilePath) + "_modified.sfc",
            InitialDirectory = Path.GetDirectoryName(RomBuffer.FilePath)
        };

        if (dialog.ShowDialog() != true) return;

        try
        {
            RomBuffer.SaveToFile(dialog.FileName);
            StatusMessage = "ROM saved as new file.";
            StatusDetail  = dialog.FileName;
            OnPropertyChanged(nameof(WindowTitle));
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Failed to save: {ex.Message}", "Save Error",
                MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    [RelayCommand]
    private void Exit() => Application.Current.Shutdown();

    // -------------------------------------------------------------------------
    // Called by child modules when they modify the ROM
    // -------------------------------------------------------------------------

    public void NotifyRomModified()
    {
        OnPropertyChanged(nameof(WindowTitle));
        StatusMessage = "ROM modified (unsaved changes).";
    }
}
