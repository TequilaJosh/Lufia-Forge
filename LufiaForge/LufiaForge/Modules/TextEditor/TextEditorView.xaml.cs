using System.Windows.Controls;

namespace LufiaForge.Modules.TextEditor;

public partial class TextEditorView : UserControl
{
    public TextEditorView() => InitializeComponent();

    private void List_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (sender is System.Windows.Controls.ListBox lb && lb.SelectedItem != null) lb.ScrollIntoView(lb.SelectedItem);
    }
}
