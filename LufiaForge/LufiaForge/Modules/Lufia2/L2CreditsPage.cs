using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LufiaForge.Core;
using LufiaForge.Core.Lufia2;
using LufiaForge.ViewModels;

namespace LufiaForge.Modules.Lufia2;

/// <summary>The staff roll as text, one line per element; Apply writes it back.</summary>
public partial class L2CreditsPage : ObservableObject
{
    private RomBuffer? _rom;
    private MainViewModel? _main;
    [ObservableProperty] private string _text = "";
    [ObservableProperty] private string _status = "";

    public void SetRom(RomBuffer rom, MainViewModel main)
    {
        _rom = rom; _main = main;
        Text = L2Credits.Read(rom);
        Status = Room();
    }

    private string Room() => $"{L2Credits.Used(_rom!)} of {L2Credits.End - L2Credits.Start} bytes used. A longer line needs room from a shorter one.";

    [RelayCommand]
    private void Apply()
    {
        if (_rom == null || _main == null) return;
        string? error = L2Credits.Write(_rom, Text);
        if (error != null) { Status = "Not written: " + error; return; }
        _main.NotifyRomModified("Lufia II: credits");
        Text = L2Credits.Read(_rom);
        Status = "Written. " + Room();
    }

    [RelayCommand]
    private void Revert()
    {
        if (_rom == null) return;
        Text = L2Credits.Read(_rom);
        Status = Room();
    }
}
