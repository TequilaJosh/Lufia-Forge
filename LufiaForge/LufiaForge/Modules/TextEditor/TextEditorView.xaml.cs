using LufiaForge.Core;
using System.Text;
using System.Windows.Controls;

namespace LufiaForge.Modules.TextEditor;

public partial class TextEditorView : UserControl
{
    private TextEditorViewModel? _vm;

    public TextEditorView()
    {
        InitializeComponent();
        DataContextChanged += OnDataContextChanged;
    }

    private void OnDataContextChanged(object sender, System.Windows.DependencyPropertyChangedEventArgs e)
    {
        if (_vm != null)
            _vm.PropertyChanged -= Vm_PropertyChanged;

        _vm = DataContext as TextEditorViewModel;

        if (_vm != null)
            _vm.PropertyChanged += Vm_PropertyChanged;
    }

    private void Vm_PropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(TextEditorViewModel.SelectedEntry))
            RefreshHexView();
    }

    private void RefreshHexView()
    {
        if (_vm?.SelectedEntry == null || HexView == null) return;

        // Get the ROM from the parent window's DataContext - we go up the visual tree
        var mainWindow = System.Windows.Application.Current.MainWindow;
        var mainVm     = mainWindow?.DataContext as ViewModels.MainViewModel;
        var rom        = mainVm?.RomBuffer;

        if (rom == null) { HexView.Text = "(No ROM)"; return; }

        var entry = _vm.SelectedEntry;
        var sb    = new StringBuilder();

        // Re-decode with token detail
        var result = TextDecoder.Decode(rom, entry.RomOffset, expandDictionary: true);

        sb.AppendLine($"Offset: {entry.OffsetHex}  |  {result.Tokens.Count} tokens  |  {result.BytesConsumed} bytes");
        sb.AppendLine();

        // Show each token with its raw hex
        foreach (var token in result.Tokens)
        {
            byte[] raw = rom.ReadBytes(token.RomOffset, token.ByteLength);
            string hex = BitConverter.ToString(raw).Replace("-", " ");

            string kindLabel = token.Kind switch
            {
                TextTokenKind.Ascii         => "ASCII",
                TextTokenKind.DictionaryRef => "DICT ",
                TextTokenKind.PageBreak     => "PAGE ",
                TextTokenKind.Newline       => "NL   ",
                TextTokenKind.CharName      => "NAME ",
                TextTokenKind.Control       => "CTRL ",
                TextTokenKind.EndString     => "END  ",
                _                           => "?    "
            };

            string display = token.Display.Replace("\n", "\\n").Replace("\r", "");
            sb.AppendLine($"{token.RomOffset:X6}  [{kindLabel}]  {hex,-12}  {display}");
        }

        HexView.Text = sb.ToString();
    }
}
