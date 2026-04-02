using CommunityToolkit.Mvvm.ComponentModel;
using System.Windows.Input;

namespace LufiaForge.Modules.Emulator;

/// <summary>One row in the controller-mapping grid — observable so the UI updates live.</summary>
public sealed partial class KeyBindingRow : ObservableObject
{
    public SnesButton Button { get; }
    public string     Label  { get; }

    [ObservableProperty] private Key    _boundKey;
    [ObservableProperty] private bool   _isCapturing;
    [ObservableProperty] private string _keyName = "";

    public KeyBindingRow(SnesButton button, Key key)
    {
        Button   = button;
        Label    = ControllerMapping.Labels[button];
        BoundKey = key;
        KeyName  = key.ToString();
    }

    partial void OnBoundKeyChanged(Key value) => KeyName = value.ToString();
}
