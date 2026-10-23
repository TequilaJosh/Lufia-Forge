using CommunityToolkit.Mvvm.ComponentModel;
using LufiaForge.Core;
using LufiaForge.ViewModels;

namespace LufiaForge.Modules.Lufia2;

/// <summary>The Lufia II tab: shown instead of the Lufia 1 editors when a Lufia II ROM is loaded.</summary>
public partial class Lufia2ViewModel : ObservableObject
{
    private RomBuffer? _rom;
    private MainViewModel? _main;

    [ObservableProperty] private string _overview = "Load a Lufia II ROM.";

    public void SetRom(RomBuffer rom, MainViewModel main)
    {
        _rom = rom; _main = main;
        Overview = $"{GameDetect.Name(rom.Game)}\n{rom.Length / 1024} KB, title \"{GameDetect.Title(rom.ToArray())}\"";
    }
}
